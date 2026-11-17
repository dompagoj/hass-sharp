using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

public partial class HassSharpManager
{
    /// <summary>The saved entity definitions, one file each in <c>.storage/hass_sharp/entities</c>.</summary>
    EntityDefinitionStore EntityDocuments => new(Path.Combine(AuthoringRoot, "entities"), new DefinitionGenerator());

    /// <summary>
    /// The path a definition's compiled script is known by in the script manager, its load errors and run history. It
    /// is never written; its folder and prefix keep it apart from an automation's of the same id.
    /// </summary>
    string EntityRuntimePath(string id) => Path.Combine(AuthoringRoot, "entities", "entity_" + id + ".cs");

    /// <summary>How long a save waits for a definition's <c>Start()</c>, so its answer says whether it runs; then it answers <c>setup</c>.</summary>
    static readonly TimeSpan StartReportWait = TimeSpan.FromSeconds(5);

    /// <summary>How long a delete waits for Home Assistant to remove the definition's entities; settable for tests.</summary>
    internal static TimeSpan RemovalTimeout { get; set; } = TimeSpan.FromSeconds(30);

    // The started host of the definition; null when none is, or when it is stopping.
    DefinitionHost? RunningHost(string id) => _userScriptManager.Entities.Host(id) is { Status: not "stopped" } host ? host : null;

    /// <summary>
    /// The document as the API answers it: its stored fields, and how it runs now: <c>automationId</c>, <c>active</c>,
    /// <c>currentRevisionActive</c>, <c>activationError</c> (the load error, else why its <c>Start()</c> failed),
    /// <c>status</c> (<c>running</c>, <c>setup</c>, <c>failed</c>, or
    /// <c>off</c> when not loaded) and its <c>liveEntities</c> (none when not loaded).
    /// </summary>
    JsonObject EntityJson(EntityDefinitionDocument document)
    {
        var path = EntityRuntimePath(document.Id);
        var running = _userScriptManager.GetUserScripts().FirstOrDefault(s => s.CompiledScript.FilePath == path && s.Active);
        var host = RunningHost(document.Id);
        var status = host?.Status ?? "off";
        var json = JsonSerializer.SerializeToNode(document, VisualStore.Json)!.AsObject();
        json["automationId"] = _history.Identity(path);
        json["active"] = running != null;
        json["currentRevisionActive"] = running != null && running.CompiledScript.VisualRevision == document.Revision && running.CompiledScript.SourceCode == document.Source && !_loadErrors.ContainsKey(path);
        json["activationError"] = _loadErrors.TryGetValue(path, out var error) ? error : status == "failed" ? host!.Error : running != null ? null : "Not active";
        json["status"] = status;
        json["liveEntities"] = JsonSerializer.SerializeToNode(host?.Entities ?? [], VisualStore.Json);
        return json;
    }

    async Task<CompiledUserScript> CompileDefinition(EntityDefinitionDocument document, GeneratedServiceCalls? services)
    {
        var compiled = await _compiler.CompileSingleFile(EntityRuntimePath(document.Id), document.Source, services);
        compiled.DesignatedRootType = "SharpDefinition";
        compiled.VisualRevision = document.Revision;
        compiled.CatalogGeneration = document.CatalogGeneration;
        compiled.Definition = new(document.Id, document.Name, document.Device, document.Settings);
        return compiled;
    }

    /// <summary>
    /// Runs the saved definition: compiles it and loads it, or replaces the revision running. A failure is recorded as
    /// its load error and answered as <c>activationError</c>; when no revision is left running then, the entities it
    /// last ran are shown unavailable. A definition that loaded but whose <c>Start()</c> threw answers that error as
    /// <c>activationError</c>, and stays active. A definition that is off is never started: whatever of it runs is unloaded.
    /// </summary>
    /// <param name="services">The typed service calls the request read once, so the compile references them.</param>
    /// <returns><c>{id, saved, document, active, activationError}</c>.</returns>
    internal async Task<object> ActivateDefinition(EntityDefinitionDocument document, GeneratedServiceCalls? services = null)
    {
        var path = EntityRuntimePath(document.Id);
        string? activationError = null;
        if (!document.Enabled) await UnloadDefinition(document.Id);
        else
        {
            var known = RunningHost(document.Id)?.Entities ?? [];
            try { await LoadOrReplace(await CompileDefinition(document, services)); }
            catch (Exception ex) { RecordLoadError(path, ex); activationError = _loadErrors[path]; }
            if (activationError == null) ClearLoadError(path);
            else if (RunningHost(document.Id) == null) PostUnavailable(known);
            // The answer says how the start went, unless it takes long; a publish that failed in it is the host's to log.
            if (RunningHost(document.Id) is { } host)
                try { await host.Started.WaitAsync(StartReportWait); } catch (Exception) { }
            // Loaded, so active, but its Start() threw: the answer says why.
            if (activationError == null && RunningHost(document.Id) is { Status: "failed" } failed) activationError = failed.Error;
        }
        var json = EntityJson(document);
        return new { id = document.Id, saved = true, document = json, active = json["currentRevisionActive"]!.GetValue<bool>(), activationError };
    }

    /// <summary>Unloads the definition, forgets its load error and shows the entities it ran unavailable.</summary>
    async Task UnloadDefinition(string id)
    {
        var known = RunningHost(id)?.Entities ?? [];
        await _userScriptManager.UnloadScriptAsync(EntityRuntimePath(id));
        ClearLoadError(EntityRuntimePath(id));
        PostUnavailable(known);
    }

    // Through the pump, after the unload: a state the stopped instance posted is replaced, never sent after this one.
    void PostUnavailable(IEnumerable<LiveEntity> entities)
    {
        foreach (var entity in entities)
            _userScriptManager.Pump.Post(entity.UniqueId, new JsonObject
                { ["uniqueId"] = entity.UniqueId, ["available"] = false, ["values"] = new JsonObject(), ["attributes"] = new JsonObject() });
    }

    static DefinitionBody DefinitionBody(JsonObject request) =>
        ApiRequest.Object(request, "definition").Deserialize<DefinitionBody>(VisualStore.Json) ?? throw new ArgumentException("definition is required");

    static object PreviewJson(DefinitionPreview preview) => new { valid = preview.Valid, diagnostics = preview.Diagnostics, source = preview.Source };

    /// <summary>
    /// The entity definitions' JSON API, answered in the envelope <see cref="VisualRequest"/> uses: <c>types</c>,
    /// <c>list</c>, <c>read</c>, <c>preview</c>, <c>save</c>, <c>set_enabled</c> and <c>delete</c>. Saving or turning on a
    /// definition runs it; turning it off or deleting it unloads it.
    /// </summary>
    /// <param name="requestJson">The request: <c>operation</c> and that operation's fields.</param>
    /// <returns><c>{version, ok: true, result}</c>, or <c>{version, ok: false, error: {code, detail}}</c>.</returns>
    public async Task<string> EntityRequest(string requestJson)
    {
        await _visualGate.WaitAsync();
        try
        {
            var request = ApiRequest.Parse(requestJson);
            var operation = ApiRequest.String(request, "operation");
            object result;
            // One read of the typed service calls for the whole request, as VisualRequest does.
            var services = _diagnosticsProvider.ServiceCalls();
            var libraries = SavedLibraries(services);
            var snapshot = libraries.Preview.Snapshot;
            var store = EntityDocuments;
            if (operation == "types")
                result = new { types = snapshot.EntityTypes.Values.OrderBy(t => t.Id, StringComparer.Ordinal).ToArray(), generation = libraries.Generation };
            else if (operation == "preview") result = PreviewJson(new DefinitionGenerator().Preview(DefinitionBody(request), snapshot));
            // Listing omits the generated source and its map; "read" returns them.
            else if (operation == "list") result = store.List().Select(document =>
            {
                var listed = EntityJson(document);
                listed.Remove("source"); listed.Remove("sourceMap");
                return listed;
            }).ToArray();
            else
            {
                var id = ApiRequest.String(request, "id");
                if (operation == "read") result = EntityJson(store.Read(id) ?? throw new FileNotFoundException("Entity definition not found"));
                else if (operation == "set_enabled")
                {
                    var enabled = ApiRequest.Bool(request, "enabled");
                    var document = store.SetEnabled(id, enabled);
                    // Turning on a definition whose saved revision already runs leaves it running as it is.
                    if (!enabled || !EntityJson(document)["currentRevisionActive"]!.GetValue<bool>()) await ActivateDefinition(document, services);
                    result = new { id, enabled = document.Enabled };
                }
                else
                {
                    var revision = ApiRequest.Long(request, "expectedRevision", "the revision this replaces, 0 for a new definition");
                    if (operation == "save")
                    {
                        var document = store.Save(id, DefinitionBody(request), revision, snapshot, libraries.Generation, ApiRequest.OptionalBool(request, "enabled"));
                        result = await ActivateDefinition(document, services);
                    }
                    else if (operation == "delete")
                    {
                        store.Delete(id, revision);
                        await _userScriptManager.UnloadScriptAsync(EntityRuntimePath(id));
                        ClearLoadError(EntityRuntimePath(id));
                        // Whether it ran or not: Home Assistant may still have its entities from an earlier run. Its last
                        // states go out first, so none arrives after the entities are gone.
                        await _userScriptManager.Pump.FlushAsync();
                        // Bounded: every request waits behind this one, and Python may never answer.
                        try { await PyInterop.RemoveEntitiesAsync(id).WaitAsync(RemovalTimeout); }
                        catch (Exception ex)
                        {
                            var reason = ex is TimeoutException ? $"Home Assistant did not answer within {RemovalTimeout.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s" : ex.Message;
                            try { Logger.Error($"Cannot remove the entities of deleted entity definition {id} from Home Assistant: {reason}"); } catch { }
                        }
                        result = new { deleted = true };
                    }
                    else throw new ArgumentException("Unknown entity operation");
                }
            }
            return JsonSerializer.Serialize(new { version = 1, ok = true, result }, VisualStore.Json);
        }
        catch (IOException ex) when (ex is not FileNotFoundException) { return Error("storage", ex.Message); }
        catch (UnauthorizedAccessException ex) { return Error("storage", ex.Message); }
        catch (DefinitionValidationException ex) { return Error("validation", PreviewJson(ex.Preview)); }
        catch (VisualConflictException ex) { return Error("conflict", ex.Message); }
        catch (FileNotFoundException ex) { return Error("not_found", ex.Message); }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or OverflowException)
        { return Error("invalid_request", ex.Message); }
        // Every field is read through ApiRequest, so a null reference here is HassSharp's own defect, not the request's.
        catch (NullReferenceException ex) { return InternalError("Entity", ex); }
        finally { _visualGate.Release(); }
    }

    /// <summary>
    /// Runs a command Home Assistant sent to an entity of a running definition, such as <c>turn_on</c>, with the user's
    /// handler. Never waits for the API: a handler may take long or use the API itself.
    /// </summary>
    /// <param name="uniqueId">The entity's unique id, <c>&lt;definition id&gt;:&lt;key&gt;</c>.</param>
    /// <param name="command">The command, such as <c>turn_on</c>.</param>
    /// <param name="dataJson">The service data as a JSON object; null or empty for none.</param>
    /// <exception cref="InvalidOperationException">The entity is not running.</exception>
    /// <exception cref="NotSupportedException">The entity has no such command.</exception>
    public async Task EntityCommand(string uniqueId, string command, string? dataJson)
    {
        if (!_userScriptManager.Entities.TryGet(uniqueId, out var host, out var entity)) throw new InvalidOperationException("The entity is not running");
        using var data = JsonDocument.Parse(string.IsNullOrEmpty(dataJson) ? "{}" : dataJson);
        // A copy, so a handler may keep it after this returns.
        await host.Command(entity, command, data.RootElement.Clone());
    }
}
