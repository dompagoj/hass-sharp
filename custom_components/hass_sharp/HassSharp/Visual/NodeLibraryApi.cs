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
        var content = NodeLibraryStore.Generation(documents) + "\0" + _diagnosticsProvider.HassEntitiesSyntaxTreeNullable()?.ToString();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    (NodeLibraryDocument[] Documents, string Generation, NodeLibraryPreview Preview) SavedLibraries()
    {
        AuthoringFiles.Recover();
        var documents = LibraryDocuments.List();
        return (documents, LibraryWorkspaceGeneration(documents), new NodeLibraryCompiler(_diagnosticsProvider).Preview(documents.Select(d => d.AsSource())));
    }
    static string Success(object result) => JsonSerializer.Serialize(new { version = 1, ok = true, result }, VisualStore.Json);
    static string RequiredString(JsonObject request, string key) => request[key]?.GetValue<string>() ?? throw new ArgumentException($"{key} is required");

    public async Task<string> NodeLibraryRequest(string requestJson)
    {
        await _visualGate.WaitAsync();
        try
        {
            var request = JsonNode.Parse(requestJson)!.AsObject();
            var operation = RequiredString(request, "operation");
            var saved = SavedLibraries();
            if (operation == "catalog") return Success(new { catalog = saved.Preview.Snapshot.Catalog, generation = saved.Generation, saved = true, valid = saved.Preview.Valid, diagnostics = saved.Preview.Diagnostics });
            if (operation == "list") return Success(new { documents = saved.Documents, generation = saved.Generation });
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
            var rebuilt = previews.Select(p => new VisualDocument(1,p.Document.Id,checked(p.Document.Revision+1),p.Document.Graph,p.Preview.Source,p.Preview.SourceMap) { CatalogGeneration = generation }).ToArray();
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
