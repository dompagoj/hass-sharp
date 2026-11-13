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
        var compiled = await _compiler.CompileSingleFile(VisualRuntimePath(document.Id), document.Source, true);
        compiled.DesignatedRootType = "VisualAutomation";
        compiled.VisualRevision = document.Revision;
        compiled.CatalogGeneration = document.CatalogGeneration;
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
            // Listing omits the generated source and its map; "read" returns them.
            else if (operation == "list") result = store.List().Select(WithRuntimeIdentity).Select(document =>
            {
                var listed = JsonSerializer.SerializeToNode(document, VisualStore.Json)!.AsObject();
                listed.Remove("source"); listed.Remove("sourceMap");
                return listed;
            }).ToArray();
            else
            {
                var id = request["id"]!.GetValue<string>();
                if (operation == "run") result = DispatchVisual(store.Read(id) ?? throw new FileNotFoundException("Visual document not found"), request["triggerId"]!.GetValue<string>());
                else if (operation == "read") result = WithRuntimeIdentity(store.Read(id) ?? throw new FileNotFoundException("Visual document not found"));
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
                        await _userScriptManager.DeleteScriptAsync(VisualRuntimePath(id), requireExisting: false);
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
    static string Error(string code, object detail) => JsonSerializer.Serialize(new { version = 1, ok = false, error = new { code, detail } }, VisualStore.Json);
}
