using System.Text.Json;
using System.Text.Json.Nodes;
namespace HassSharp;

public partial class HassSharpManager
{
    readonly SemaphoreSlim _visualGate = new(1, 1);
    VisualStore VisualDocuments => new(Path.Combine(HassPath.HassConfiguration, ".storage", "hass_sharp", "visual"), new(_diagnosticsProvider));
    string VisualRuntimePath(string id) => Path.Combine(HassPath.HassConfiguration, ".storage", "hass_sharp", "visual", "visual_" + id + ".cs");

    VisualDocument WithRuntimeIdentity(VisualDocument document)
    {
        var path = VisualRuntimePath(document.Id);
        var running = _userScriptManager.GetUserScripts().FirstOrDefault(s => s.CompiledScript.FilePath == path && s.Active);
        var active = running != null;
        return document with { AutomationId = _history.Identity(path), Active = active,
            ActiveRevision = running?.CompiledScript.VisualRevision, ActiveSourceHash = running?.Revision,
            CurrentRevisionActive = running?.CompiledScript.VisualRevision == document.Revision && running?.CompiledScript.SourceCode == document.Source && !_loadErrors.ContainsKey(path),
            ActivationError = _loadErrors.TryGetValue(path, out var error) ? error : active ? null : "Not active" };
    }

    /// <param name="warnings">What the save removed while deriving Custom C# pins; returned as <c>diagnostics</c> when there is any.</param>
    async Task<object> ActivateVisualDocument(VisualDocument document, IReadOnlyList<VisualDiagnostic>? warnings = null)
    {
        string? activationError = null;
        try
        {
            var compiled = await CompileVisualDocument(document);
            if (!_userScriptManager.GetUserScripts().Any(s => s.CompiledScript.FilePath == VisualRuntimePath(document.Id)))
            {
                UserScript loaded;
                try { loaded = _userScriptManager.LoadUserScript(compiled, staged: true); }
                catch { compiled.Unload(); throw; }
                try { await loaded.ConfigureAndStart(); }
                catch { try { await loaded.ClearFromTrackingAsync(); } finally { loaded.Unload(); } throw; }
            }
            else await _userScriptManager.UpdateUserScript(compiled);
        }
        catch (Exception ex) { RecordLoadError(VisualRuntimePath(document.Id), ex); activationError = _loadErrors[VisualRuntimePath(document.Id)]; }
        if (activationError == null) _loadErrors.TryRemove(VisualRuntimePath(document.Id), out _);
        document = WithRuntimeIdentity(document);
        return warnings is { Count: > 0 } ? (object)new { id = document.Id, saved = true, document, active = document.CurrentRevisionActive, activationError, diagnostics = warnings }
            : new { id = document.Id, saved = true, document, active = document.CurrentRevisionActive, activationError };
    }

    async Task<CompiledUserScript> CompileVisualDocument(VisualDocument document)
    {
        var compiled = await _compiler.CompileSingleFile(VisualRuntimePath(document.Id), document.Source);
        compiled.DesignatedRootType = "VisualAutomation";
        compiled.VisualRevision = document.Revision;
        compiled.CatalogGeneration = document.CatalogGeneration;
        // The switch is read here, at compile time. That is safe only because every compile/activate path (Init, save, the
        // library rebuild) and set_enabled hold _visualGate, so set_enabled cannot land between this read and the load.
        compiled.Enabled = document.Enabled;
        // What a run records: its trigger node, and the graph/catalog snapshot a trace is viewed against.
        compiled.VisualId = document.Id;
        compiled.VisualTriggers = document.Graph["nodes"]!.AsArray().OfType<JsonObject>().Where(n => VisualNodeCatalog.Text(n, "kind") == "trigger")
            .ToDictionary(VisualNodeCatalog.EffectiveTriggerMethod, n => new VisualTrigger(VisualNodeCatalog.Text(n, "id"), VisualNodeCatalog.Text(n, "label") is { Length: > 0 } label ? label : null), StringComparer.Ordinal);
        compiled.VisualGraph = document.Graph.ToJsonString();
        compiled.VisualCatalog = document.EmbeddedCatalog;
        return compiled;
    }

    object DispatchVisual(VisualDocument document, string triggerId)
    {
        document = WithRuntimeIdentity(document);
        if (!document.CurrentRevisionActive) throw new InvalidOperationException("The saved visual revision is not active");
        var trigger = document.Graph["nodes"]!.AsArray().SingleOrDefault(n => n?["kind"]?.GetValue<string>() == "trigger" && n?["id"]?.GetValue<string>() == triggerId)?.AsObject()
            ?? throw new ArgumentException("Trigger not found");
        if (!(trigger["config"]?["manual"]?.GetValue<bool>() ?? true)) throw new InvalidOperationException("Trigger does not permit manual execution");
        // An entity or state trigger run manually carries the entity's current state, read as the Read entity node does;
        // a state that cannot be read is logged and the run starts without trigger values.
        var payload = TriggerPayload.Manual(VisualNodeCatalog.TriggerType(trigger, VisualCatalogSnapshot.BuiltIn), trigger["config"] as JsonObject, PyInterop.Entity);
        return new { version = 1, runId = _userScriptManager.DispatchManual(document.AutomationId!, "VisualAutomation", VisualNodeCatalog.EffectiveTriggerMethod(trigger), payload) };
    }

    // Single JSON bridge keeps Python.NET calls and frontend versioning explicit.
    public async Task<string> VisualRequest(string requestJson)
    {
        await _visualGate.WaitAsync();
        try
        {
            var request = ApiRequest.Parse(requestJson);
            var operation = ApiRequest.String(request, "operation");
            object result;
            var store = VisualDocuments;
            var libraries = SavedLibraries();
            var authoring = AuthoringSnapshot(libraries.Preview.Snapshot);
            // deriveSignatures: each Custom C# node's pins are first written from its code, so a caller can send only the code.
            var derive = ApiRequest.OptionalBool(request, "deriveSignatures") ?? false;
            // What deriving removed (values and wires of pins the code no longer has) is reported as warnings.
            List<VisualDiagnostic> removed = [];
            JsonObject Graph() => derive ? new VisualGenerator(_diagnosticsProvider).DeriveSignatures(ApiRequest.Object(request, "graph"), authoring, out removed) : ApiRequest.Object(request, "graph");
            if (operation == "preview")
            {
                var graph = Graph(); var preview = new VisualGenerator(_diagnosticsProvider).Preview(graph, authoring);
                result = preview with { Graph = derive ? preview.Graph ?? graph : preview.Graph, Diagnostics = [.. removed, .. preview.Diagnostics, .. UnknownEntities(preview.Graph ?? graph, authoring, PyInterop.Entity)] };
            }
            // The automations page: no graphs, an outline of each instead, and how each has been running (one history query).
            else if (operation == "list" && ApiRequest.OptionalString(request, "view") == "home") result = HomeList(store.List().Select(WithRuntimeIdentity).ToArray(), ApiRequest.OptionalString(request, "todayUtc"));
            // Listing omits the generated source and its map; "read" returns them.
            else if (operation == "list") result = store.List().Select(WithRuntimeIdentity).Select(document =>
            {
                var listed = JsonSerializer.SerializeToNode(document, VisualStore.Json)!.AsObject();
                listed.Remove("source"); listed.Remove("sourceMap"); listed.Remove("savedUtc");
                return listed;
            }).ToArray();
            else
            {
                var id = ApiRequest.String(request, "id");
                if (operation == "run") result = DispatchVisual(store.Read(id) ?? throw new FileNotFoundException("Visual document not found"), ApiRequest.String(request, "triggerId"));
                else if (operation == "read") result = WithRuntimeIdentity(store.Read(id) ?? throw new FileNotFoundException("Visual document not found"));
                else if (operation == "set_enabled")
                {
                    var enabled = ApiRequest.Bool(request, "enabled");
                    var document = store.SetEnabled(id, enabled);
                    // The loaded script keeps running its manual runs; only its own triggers and startup run follow the switch.
                    // Stored first, then applied; a failure while cancelling runs or detaching triggers is logged, not returned.
                    await _userScriptManager.SetEnabledAsync(VisualRuntimePath(id), document.Enabled);
                    result = new { id, enabled = document.Enabled };
                }
                else
                {
                    var revision = ApiRequest.Long(request, "expectedRevision", "the revision this replaces, 0 for a new automation");
                    if (operation == "save")
                    {
                        // A new automation may be saved turned off (a duplicate is): stored before activation, so it never runs on.
                        var enabled = ApiRequest.OptionalBool(request, "enabled");
                        var document = store.Save(id, Graph(), revision, authoring, libraries.Generation, enabled);
                        result = await ActivateVisualDocument(document, [.. removed, .. UnknownEntities(document.Graph, authoring, PyInterop.Entity)]);
                    }
                    else if (operation == "delete")
                    {
                        store.Delete(id, revision);
                        await _userScriptManager.DeleteScriptAsync(VisualRuntimePath(id));
                        _loadErrors.TryRemove(VisualRuntimePath(id), out _);
                        result = new { deleted = true };
                    }
                    else throw new ArgumentException("Unknown visual operation");
                }
            }
            return JsonSerializer.Serialize(new { version = 1, ok = true, result }, VisualStore.Json);
        }
        catch (IOException ex) when (ex is not FileNotFoundException) { return Error("storage", ex.Message); }
        catch (UnauthorizedAccessException ex) { return Error("storage", ex.Message); }
        catch (VisualValidationException ex) { return Error("validation", ex.Preview); }
        catch (VisualConflictException ex) { return Error("conflict", ex.Message); }
        catch (FileNotFoundException ex) { return Error("not_found", ex.Message); }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or OverflowException)
        { return Error("invalid_request", ex.Message); }
        catch (NullReferenceException) { return Error("invalid_request", ApiRequest.Unreadable); }
        finally { _visualGate.Release(); }
    }
    /// <summary>
    /// A warning (code <c>entity.unknown</c>) on the node and input of each entity id written into the graph that Home
    /// Assistant has no state for: an entity trigger's entity, a Home Assistant trigger's entity_id, and literal values
    /// of entity inputs (an entity, a list of them, a service target's entities). Such a graph still saves and
    /// activates: an entity can be added later. None
    /// at all when the lookup is unavailable (not set, or failing); preview and save only, never Init, when Home
    /// Assistant may not have loaded its entities yet.
    /// </summary>
    internal static List<VisualDiagnostic> UnknownEntities(JsonObject graph, VisualCatalogSnapshot snapshot, Func<string, HasEntityState?>? lookup)
    {
        var warnings = new List<VisualDiagnostic>();
        if (lookup == null) return warnings;
        var root = VisualNodeCatalog.WithGraphTypes(snapshot.Root, graph);
        var known = new Dictionary<string, bool>(StringComparer.Ordinal);
        var unavailable = false;
        void Check(string node, string input, string entity, bool trigger)
        {
            // A template, or HA's own "all" and "none", names no single entity.
            if (unavailable || entity.Length == 0 || entity is "all" or "none" || entity.Contains("{{", StringComparison.Ordinal)) return;
            if (!known.TryGetValue(entity, out var exists))
            {
                try { known[entity] = exists = lookup(entity) != null; }
                catch { unavailable = true; return; }
            }
            if (!exists)
                warnings.Add(new(node, $"Home Assistant has no entity {entity}. Check the spelling: " + (trigger ? "a trigger on it never fires." : "this step finds no such entity when it runs."), 2, InputId: input, Code: "entity.unknown"));
        }
        static IEnumerable<string> Ids(JsonNode? value) => value switch
        {
            JsonValue single when single.TryGetValue<string>(out var id) => [id],
            JsonArray list => list.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var id) ? id : null).OfType<string>(),
            _ => [],
        };
        // A native trigger's entity ids as Home Assistant reads them: a text is a comma-separated list, each id is
        // trimmed and lower-cased (entity_ids_or_uuids); a template is left alone (it names no entity until it runs).
        static IEnumerable<string> NativeIds(JsonNode? value) => Ids(value)
            .SelectMany(text => text.Contains("{{", StringComparison.Ordinal) || text.Contains("{%", StringComparison.Ordinal) ? [] : text.Split(','))
            .Select(entity => entity.Trim().ToLowerInvariant()).Where(entity => entity.Length > 0);
        foreach (var node in (graph["nodes"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var id = VisualNodeCatalog.Text(node, "id");
            if (VisualNodeCatalog.Text(node, "kind") == "trigger" && VisualNodeCatalog.TriggerType(node, snapshot) is var triggerType)
            {
                if (triggerType == "entity") Check(id, "entity", VisualNodeCatalog.Text(node["config"], "entity"), true);
                // A Home Assistant trigger's entity_id, and its target's (a purpose-specific trigger), read as Home Assistant
                // reads them; an entity registry id (it has no dot) names no entity id to look up.
                else if (triggerType == "ha" && node["config"]?["trigger"] is JsonObject native)
                    foreach (var entity in NativeIds(native["entity_id"]).Concat(NativeIds(native["target"]?["entity_id"])).Where(e => e.Contains('.')).Distinct()) Check(id, "trigger", entity, true);
            }
            JsonObject inputs;
            try { inputs = VisualNodeCatalog.DataInputs(node, null, snapshot); } catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException) { continue; }
            foreach (var (port, _) in inputs)
            {
                if (VisualNodeCatalog.InputValue(node, port, snapshot) is not JsonObject operand || VisualNodeCatalog.Text(operand, "kind") != "literal") continue;
                var type = VisualNodeCatalog.Text(operand, "type");
                var value = operand["value"];
                var entities = VisualTypes.Refines(root, type, "entity_id") ? Ids(value)
                    : value is JsonObject target && VisualTypes.Refines(root, type, "target") ? Ids(target["entity_id"])
                    : VisualTypes.Resolve(root, type)?["elementType"] is JsonValue element && element.TryGetValue<string>(out var item) && VisualTypes.Refines(root, item, "entity_id") ? Ids(value)
                    : [];
                foreach (var entity in entities) Check(id, port, entity, false);
            }
        }
        return unavailable ? [] : warnings;
    }

    /// <summary>The automations page's list: each document without its graph, with <see cref="Outline"/>, its load problems and recent runs.</summary>
    object[] HomeList(VisualDocument[] documents, string? todayUtc)
    {
        // The start of the viewer's day, for "runs today"; UTC midnight when not given.
        var today = DateTime.UtcNow.Date;
        if (todayUtc != null && !DateTime.TryParse(todayUtc, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out today))
            throw new ArgumentException("todayUtc must be an ISO 8601 time");
        var runs = _history.Recent(documents.Select(d => d.AutomationId!).ToArray(), today, DateTime.UtcNow.AddDays(-1));
        return documents.Select(document =>
        {
            var path = VisualRuntimePath(document.Id);
            var recent = runs.GetValueOrDefault(document.AutomationId!);
            return (object)new
            {
                id = document.Id, revision = document.Revision, automationId = document.AutomationId, enabled = document.Enabled,
                active = document.Active, currentRevisionActive = document.CurrentRevisionActive, activationError = document.ActivationError,
                problemCount = _loadErrors.ContainsKey(path) && _loadProblems.TryGetValue(path, out var problems) ? problems : (int?)null,
                savedUtc = document.SavedUtc, catalogGeneration = document.CatalogGeneration,
                outline = Outline(document.Graph),
                recentRuns = recent?.Runs ?? [], runsToday = recent?.RunsToday ?? 0, failedLastDay = recent?.FailedLastDay ?? 0, lastRunUtc = recent?.LastRunUtc,
            };
        }).ToArray();
    }

    /// <summary>
    /// What the automations page draws and summarises, a fraction of the graph's size: the name; each node's kind,
    /// position and label, with the id and config of trigger nodes only; execution edges as pairs of node indexes.
    /// </summary>
    internal static JsonObject Outline(JsonObject graph)
    {
        var nodes = new JsonArray();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in (graph["nodes"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var kind = VisualNodeCatalog.Text(node, "kind");
            var position = node["position"] as JsonObject;
            static long Coordinate(JsonNode? value) => value is JsonValue number && number.TryGetValue<double>(out var x) && double.IsFinite(x) ? (long)Math.Round(x) : 0;
            var outlined = new JsonObject { ["kind"] = kind, ["x"] = Coordinate(position?["x"]), ["y"] = Coordinate(position?["y"]) };
            if (VisualNodeCatalog.Text(node, "label") is { Length: > 0 } label) outlined["label"] = label;
            if (kind == "trigger") { outlined["id"] = VisualNodeCatalog.Text(node, "id"); outlined["config"] = node["config"]?.DeepClone(); }
            index.TryAdd(VisualNodeCatalog.Text(node, "id"), nodes.Count);
            nodes.Add(outlined);
        }
        var edges = new JsonArray();
        foreach (var edge in (graph["edges"] as JsonArray ?? []).OfType<JsonObject>())
            if (VisualNodeCatalog.Text(edge, "kind") != "data" && index.TryGetValue(VisualNodeCatalog.Text(edge, "source"), out var from) && index.TryGetValue(VisualNodeCatalog.Text(edge, "target"), out var to))
                edges.Add(new JsonArray(from, to));
        return new JsonObject { ["name"] = graph["name"]?.DeepClone(), ["nodes"] = nodes, ["edges"] = edges };
    }

    static string Error(string code, object detail) => JsonSerializer.Serialize(new { version = 1, ok = false, error = new { code, detail } }, VisualStore.Json);
}
