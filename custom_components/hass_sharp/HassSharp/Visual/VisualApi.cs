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

    async Task<object> ActivateVisualDocument(VisualDocument document)
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
        return new { id = document.Id, saved = true, document, active = document.CurrentRevisionActive, activationError };
    }

    async Task<CompiledUserScript> CompileVisualDocument(VisualDocument document)
    {
        var compiled = await _compiler.CompileSingleFile(VisualRuntimePath(document.Id), document.Source);
        compiled.DesignatedRootType = "VisualAutomation";
        compiled.VisualRevision = document.Revision;
        compiled.CatalogGeneration = document.CatalogGeneration;
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
            var request = JsonNode.Parse(requestJson)!.AsObject();
            var operation = request["operation"]!.GetValue<string>();
            object result;
            var store = VisualDocuments;
            var libraries = SavedLibraries();
            var authoring = AuthoringSnapshot(libraries.Preview.Snapshot);
            if (operation == "preview") result = new VisualGenerator(_diagnosticsProvider).Preview(request["graph"]!.AsObject(), authoring);
            // The automations page: no graphs, an outline of each instead, and how each has been running (one history query).
            else if (operation == "list" && request["view"]?.GetValue<string>() == "home") result = HomeList(store.List().Select(WithRuntimeIdentity).ToArray(), request["todayUtc"]);
            // Listing omits the generated source and its map; "read" returns them.
            else if (operation == "list") result = store.List().Select(WithRuntimeIdentity).Select(document =>
            {
                var listed = JsonSerializer.SerializeToNode(document, VisualStore.Json)!.AsObject();
                listed.Remove("source"); listed.Remove("sourceMap"); listed.Remove("savedUtc");
                return listed;
            }).ToArray();
            else
            {
                var id = request["id"]!.GetValue<string>();
                if (operation == "run") result = DispatchVisual(store.Read(id) ?? throw new FileNotFoundException("Visual document not found"), request["triggerId"]!.GetValue<string>());
                else if (operation == "read") result = WithRuntimeIdentity(store.Read(id) ?? throw new FileNotFoundException("Visual document not found"));
                else if (operation == "set_enabled")
                {
                    var enabled = request["enabled"]?.GetValue<bool>() ?? throw new ArgumentException("enabled is required");
                    var document = store.SetEnabled(id, enabled);
                    // The loaded script keeps running its manual runs; only its own triggers and startup run follow the switch.
                    _userScriptManager.SetEnabled(VisualRuntimePath(id), document.Enabled);
                    result = new { id, enabled = document.Enabled };
                }
                else
                {
                    var revision = request["expectedRevision"]!.GetValue<long>();
                    if (operation == "save")
                    {
                        var document = store.Save(id, request["graph"]!.AsObject(), revision, authoring, libraries.Generation);
                        result = await ActivateVisualDocument(document);
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
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or NullReferenceException or OverflowException)
        { return Error("invalid_request", ex.Message); }
        finally { _visualGate.Release(); }
    }
    /// <summary>The automations page's list: each document without its graph, with <see cref="Outline"/>, its load problems and recent runs.</summary>
    object[] HomeList(VisualDocument[] documents, JsonNode? todayUtc)
    {
        // The start of the viewer's day, for "runs today"; UTC midnight when not given.
        var today = DateTime.UtcNow.Date;
        if (todayUtc != null && !DateTime.TryParse(todayUtc.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture,
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
