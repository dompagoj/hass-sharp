using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

public partial class HassSharpManager
{
    string AuthoringRoot => Path.Combine(HassPath.HassConfiguration, ".storage", "hass_sharp");
    NodeLibraryStore LibraryDocuments => new(Path.Combine(AuthoringRoot, "libraries"));
    AuthoringTransaction AuthoringFiles => new(AuthoringRoot);
    CancellationTokenSource _languageLifetime = new();

    string LibraryWorkspaceGeneration(IEnumerable<NodeLibraryDocument> documents)
    {
        // The typed service calls are not part of it: they follow Home Assistant's services, and an open editor must not
        // see its libraries as changed elsewhere whenever a service is registered.
        var content = NodeLibraryStore.Generation(documents);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    (NodeLibraryDocument[] Documents, string Generation, NodeLibraryPreview Preview) SavedLibraries()
    {
        AuthoringFiles.Recover();
        var documents = LibraryDocuments.List();
        return (documents, LibraryWorkspaceGeneration(documents), new NodeLibraryCompiler(_diagnosticsProvider).Preview(documents.Select(d => d.AsSource())));
    }
    // Only authoring validation of one graph sees the service schemas. Init and the library-save
    // rebuild use SavedLibraries' snapshot without them, so loading never depends on which
    // integrations had registered services (C4, D3); the catalog root never contains them (D2).
    VisualCatalogSnapshot AuthoringSnapshot(VisualCatalogSnapshot saved) => saved.WithServices(ServiceSchemas);
    static string Success(object result) => JsonSerializer.Serialize(new { version = 1, ok = true, result }, VisualStore.Json);
    static bool IsServicesRequest(string requestJson)
    {
        // Anything malformed (null, invalid JSON, duplicate keys, ...) falls through to the gated
        // path, which reports it in the usual invalid_request envelope.
        try { return JsonNode.Parse(requestJson) is JsonObject request && request["operation"] is JsonValue op && op.TryGetValue<string>(out var name) && name == "services"; }
        catch { return false; }
    }
    /// <summary>
    /// The ports a Call service node gets for one service, computed by the validator's own DataInputs and
    /// DataOutputs: every schema field is added to config.fields (not set, in Home Assistant's order), and
    /// returnResponse is on when the service can return a response. Unknown services keep only the target.
    /// </summary>
    internal static object ServicePorts(string service, VisualCatalogSnapshot snapshot)
    {
        var schema = snapshot.Services?.Items[service] as JsonObject;
        static int Order(JsonNode? field) => field?["order"] is JsonValue order && order.TryGetValue<int>(out var value) ? value : 0;
        var fields = new JsonObject();
        foreach (var (key, _) in (schema?["fields"] as JsonObject ?? []).OrderBy(f => Order(f.Value)).ThenBy(f => f.Key, StringComparer.Ordinal).ToArray())
            fields[key] = new JsonObject { ["kind"] = "unset" };
        var response = schema == null ? null : VisualNodeCatalog.Text(schema, "response", "none");
        var node = new JsonObject { ["id"] = "service", ["kind"] = "service",
            ["config"] = new JsonObject { ["service"] = service, ["fields"] = fields, ["returnResponse"] = response is "optional" or "only" } };
        return new { service, known = schema != null, response, hasTarget = schema?["target"] is JsonObject,
            inputs = VisualNodeCatalog.DataInputs(node, null, snapshot), outputs = VisualNodeCatalog.DataOutputs(node, snapshot) };
    }
    static string RequiredString(JsonObject request, string key) => request[key]?.GetValue<string>() ?? throw new ArgumentException($"{key} is required");

    public async Task<string> NodeLibraryRequest(string requestJson)
    {
        // The schemas are immutable and swapped whole: the editor never waits behind Init's compiles for them.
        if (IsServicesRequest(requestJson)) return Success(ServiceSchemas.Section);
        await _visualGate.WaitAsync();
        try
        {
            var request = JsonNode.Parse(requestJson)!.AsObject();
            var operation = RequiredString(request, "operation");
            var saved = SavedLibraries();
            if (operation == "catalog") return Success(new { catalog = saved.Preview.Snapshot.Catalog, generation = saved.Generation, servicesGeneration = ServiceSchemas.Generation, saved = true, valid = saved.Preview.Valid, diagnostics = saved.Preview.Diagnostics });
            if (operation == "list") return Success(new { documents = saved.Documents, generation = saved.Generation });
            if (operation == "service_ports") return Success(ServicePorts(RequiredString(request, "service"), AuthoringSnapshot(saved.Preview.Snapshot)));
            // A Custom C# node's code: its pins as the compiler derives them, and the language services, beside the saved libraries.
            var customRequest = request["custom"] is JsonValue flag && flag.TryGetValue<bool>(out var custom) && custom;
            if (operation == "custom_preview" || customRequest && operation is "completion" or "hover" or "signature" or "format" or "definition")
            {
                var nodeId = request["nodeId"] is JsonValue node ? node.GetValue<string>() : "custom";
                if (nodeId.Length == 0) throw new ArgumentException("nodeId must not be empty");
                var code = new CustomNodeSource(nodeId, RequiredString(request, "source"));
                var libraries = saved.Documents.ToDictionary(d => d.Id, d => d.AsSource(), StringComparer.Ordinal);
                if (request["sources"] is JsonArray edited)
                foreach (var item in edited)
                {
                    var change = item!.AsObject(); var sourceId = RequiredString(change, "id"); NodeLibraryStore.ValidateId(sourceId);
                    libraries[sourceId] = new(sourceId, RequiredString(change, "source"), libraries.GetValueOrDefault(sourceId)?.Revision ?? 0);
                }
                var ordered = libraries.Values.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
                if (operation == "custom_preview")
                {
                    var compiled = new NodeLibraryCompiler(_diagnosticsProvider).Preview(ordered, [code], _languageLifetime.Token);
                    var result = compiled.Snapshot.Custom[nodeId];
                    return Success(new { valid = compiled.Valid && result.Valid, diagnostics = result.Diagnostics, signature = result.Signature });
                }
                var workspace = RequiredString(request, "workspaceVersion");
                if (request["generation"] is {} current && current.GetValue<string>() != saved.Generation) throw new VisualConflictException();
                var lifetime = _languageLifetime.Token;
                var request_ = new NodeLibraryLanguageServices(_diagnosticsProvider).RequestAsync(operation, ordered, code, request["position"]?.GetValue<int>() ?? 0, lifetime);
                _visualGate.Release();
                object answer;
                try { answer = await request_; }
                finally { await _visualGate.WaitAsync(); }
                return Success(new { id = nodeId, workspaceVersion = workspace, generation = saved.Generation, saved = false, data = answer });
            }
            var id = RequiredString(request, "id"); NodeLibraryStore.ValidateId(id);
            var old = saved.Documents.SingleOrDefault(d => d.Id == id);
            if (operation == "read") return Success(old ?? throw new FileNotFoundException("Library document not found"));
            if (operation is "preview" or "completion" or "hover" or "signature" or "format" or "definition")
            {
                var source = RequiredString(request, "source");
                var workspaceVersion = RequiredString(request, "workspaceVersion");
                if (request["generation"] is {} expected && expected.GetValue<string>() != saved.Generation) throw new VisualConflictException();
                var sources = saved.Documents.ToDictionary(d => d.Id, d => d.AsSource(), StringComparer.Ordinal);
                if (request["sources"] is JsonArray unsaved)
                foreach (var item in unsaved)
                {
                    var change = item!.AsObject(); var sourceId = RequiredString(change, "id"); NodeLibraryStore.ValidateId(sourceId);
                    sources[sourceId] = new(sourceId, RequiredString(change, "source"), sources.GetValueOrDefault(sourceId)?.Revision ?? 0);
                }
                sources[id] = new(id, source, old?.Revision ?? 0);
                var snapshot = sources.Values.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
                var sourceGeneration = LibraryWorkspaceGeneration(snapshot.Select(s => new NodeLibraryDocument(1,s.Id,s.Id,s.Revision,s.Source)));
                object data;
                if (operation == "preview")
                {
                    var preview = new NodeLibraryCompiler(_diagnosticsProvider).Preview(snapshot, _languageLifetime.Token);
                    data = new { preview.Valid, preview.Diagnostics, catalog = preview.Snapshot.Catalog };
                }
                else
                {
                    var token = _languageLifetime.Token;
                    // The immutable source snapshot remains coherent after releasing the authoring gate.
                    var services = new NodeLibraryLanguageServices(_diagnosticsProvider);
                    var languageTask = services.RequestAsync(operation, snapshot, id, request["position"]?.GetValue<int>() ?? 0, token);
                    _visualGate.Release();
                    try { data = await languageTask; }
                    finally { await _visualGate.WaitAsync(); }
                }
                return Success(new { id, workspaceVersion, generation = saved.Generation, sourceGeneration, saved = false, data });
            }
            if (operation is not ("save" or "delete")) throw new ArgumentException("Unknown library operation");
            var revision = request["expectedRevision"]?.GetValue<long>() ?? throw new ArgumentException("expectedRevision is required");
            if ((old?.Revision ?? 0) != revision) throw new VisualConflictException();
            if (operation == "delete" && old == null) throw new FileNotFoundException("Library document not found");
            NodeLibraryDocument? document = null;
            if (operation == "save")
            {
                var name = RequiredString(request, "name"); NodeLibraryStore.ValidateName(name);
                document = new(1, id, name, checked(revision + 1), RequiredString(request, "source"));
            }
            // Source ordering is part of the generated artifact identity and must match reload.
            var candidates = saved.Documents.Where(d => d.Id != id).Concat(document == null ? [] : new[] { document })
                .OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
            var candidate = new NodeLibraryCompiler(_diagnosticsProvider).Preview(candidates.Select(d => d.AsSource()));
            var generation = LibraryWorkspaceGeneration(candidates);
            // Every artifact embeds every source, including advanced C# and indirect helper dependencies.
            var graphs = VisualDocuments.List(strict: true);
            var previews = graphs.Select(g => (Document: g, Preview: new VisualGenerator(_diagnosticsProvider).Preview(g.Graph, candidate.Snapshot))).ToArray();
            if (!candidate.Valid || previews.Any(p => !p.Preview.Valid))
                return Error("validation", new { diagnostics = candidate.Diagnostics, automations = previews.Where(p => !p.Preview.Valid).Select(p => new { id = p.Document.Id, diagnostics = p.Preview.Diagnostics }) });
            var rebuilt = previews.Select(p => new VisualDocument(1,p.Document.Id,checked(p.Document.Revision+1),p.Document.Graph,p.Preview.Source,p.Preview.SourceMap) { CatalogGeneration = generation, EmbeddedCatalog = p.Preview.EmbeddedCatalog, Enabled = p.Document.Enabled, SavedUtc = p.Document.SavedUtc }).ToArray();
            var changes = rebuilt.ToDictionary(d => VisualDocuments.FilePath(d.Id), d => (string?)JsonSerializer.Serialize(d, VisualStore.Json));
            changes[LibraryDocuments.DocumentPath(id)] = document == null ? null : JsonSerializer.Serialize(document, VisualStore.Json);
            AuthoringFiles.Commit(changes);
            var automations = new List<object>();
            foreach (var graph in rebuilt) automations.Add(await ActivateVisualDocument(graph));
            return Success(new { saved = true, document, deleted = operation == "delete", generation, catalog = candidate.Snapshot.Catalog, automations });
        }
        catch (VisualConflictException ex) { return Error("conflict", ex.Message); }
        catch (FileNotFoundException ex) { return Error("not_found", ex.Message); }
        catch (OperationCanceledException) { return Error("cancelled", "Library language request cancelled"); }
        catch (IOException ex) { return Error("storage", ex.Message); }
        catch (UnauthorizedAccessException ex) { return Error("storage", ex.Message); }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or NullReferenceException or OverflowException)
        { return Error("invalid_request", ex.Message); }
        finally { _visualGate.Release(); }
    }
}
