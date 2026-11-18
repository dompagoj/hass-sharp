using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

public partial class HassSharpManager
{
    /// <summary>The saved entity definitions, one file each in <c>.storage/hass_sharp/entities</c>.</summary>
    EntityDefinitionStore EntityDocuments => EntityDocumentsWith(null);
    /// <summary>The definitions, previewing a graph with the typed service calls a request read once (the current ones when null).</summary>
    EntityDefinitionStore EntityDocumentsWith(GeneratedServiceCalls? services) => new(Path.Combine(AuthoringRoot, "entities"), DefinitionGeneratorWith(services));
    DefinitionGenerator DefinitionGeneratorWith(GeneratedServiceCalls? services) => new(_diagnosticsProvider, services);

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
    /// <c>off</c> when not loaded) and its <c>liveEntities</c> (none when not loaded). A secret setting is answered without
    /// its value (<see cref="WithoutSecretValues"/>): every answer of a definition is built here.
    /// </summary>
    /// <param name="libraries">The libraries the definition is answered against, which say which of a class's settings are secret.</param>
    JsonObject EntityJson(EntityDefinitionDocument document, VisualCatalogSnapshot libraries)
    {
        var path = EntityRuntimePath(document.Id);
        var running = _userScriptManager.GetUserScripts().FirstOrDefault(s => s.CompiledScript.FilePath == path && s.Active);
        var host = RunningHost(document.Id);
        var status = host?.Status ?? "off";
        var json = JsonSerializer.SerializeToNode(document, VisualStore.Json)!.AsObject();
        WithoutSecretValues(json, document, libraries);
        json["automationId"] = _history.Identity(path);
        json["active"] = running != null;
        json["currentRevisionActive"] = running != null && running.CompiledScript.VisualRevision == document.Revision && running.CompiledScript.SourceCode == document.Source && !_loadErrors.ContainsKey(path);
        json["activationError"] = _loadErrors.TryGetValue(path, out var error) ? error : status == "failed" ? host!.Error : running != null ? null : "Not active";
        json["status"] = status;
        json["liveEntities"] = JsonSerializer.SerializeToNode(host?.Entities ?? [], VisualStore.Json);
        return json;
    }

    /// <summary>
    /// The definition's source compiled, with what its host and its runs need. A class: its <c>SharpDefinition</c> factory
    /// and setting values. A graph: its generated class, which its host runs alongside the entities the document describes,
    /// with the run metadata an automation's graph has (its id, revision, graph, embedded catalog and one entry per root).
    /// </summary>
    async Task<CompiledUserScript> CompileDefinition(EntityDefinitionDocument document, GeneratedServiceCalls? services)
    {
        if (document.Logic.Kind != "graph")
        {
            var compiled = await _compiler.CompileSingleFile(EntityRuntimePath(document.Id), document.Source, services);
            compiled.DesignatedRootType = "SharpDefinition";
            compiled.VisualRevision = document.Revision;
            compiled.CatalogGeneration = document.CatalogGeneration;
            compiled.Definition = new(document.Id, document.Name, document.Device, document.Settings);
            return compiled;
        }
        // The graph as previewed and stored, with its definition composed into it, as its source embeds it.
        var graph = DefinitionGraph.Compose(document.Body);
        // A document read from its file carries no embedded catalog: it is worked out again from the libraries as saved.
        var catalog = document.EmbeddedCatalog ?? VisualGenerator.EmbeddedCatalog(graph, SavedLibraries(services).Preview.Snapshot).ToJsonString();
        var snapshot = VisualCatalogSnapshot.BuiltIn.WithRoot(JsonNode.Parse(catalog)!.AsObject());
        var roots = DefinitionGraph.Roots(graph, snapshot);
        var triggers = (graph["nodes"] as JsonArray ?? []).OfType<JsonObject>().Where(n => VisualNodeCatalog.IsRoot(n, snapshot))
            .ToDictionary(VisualNodeCatalog.EffectiveTriggerMethod, n => new VisualTrigger(VisualNodeCatalog.Text(n, "id"),
                VisualNodeCatalog.Text(n, "label") is { Length: > 0 } label ? label : null, TrackHistory(n, snapshot)), StringComparer.Ordinal);
        var script = await _compiler.CompileSingleFile(EntityRuntimePath(document.Id), document.Source, services);
        script.DesignatedRootType = "VisualAutomation";
        script.VisualRevision = document.Revision;
        script.CatalogGeneration = document.CatalogGeneration;
        script.VisualId = document.Id;
        script.VisualTriggers = triggers;
        script.VisualGraph = graph.ToJsonString();
        script.VisualCatalog = catalog;
        script.Definition = new(document.Id, document.Name, document.Device, document.Settings)
            { Entities = (JsonArray?)document.Entities?.DeepClone() ?? [], Roots = roots };
        return script;
    }

    /// <summary>
    /// Whether a root's runs are recorded: its <c>trackHistory</c>, else its kind's default as the catalog gives it (off for a
    /// State, on for When commanded and When started). A trigger's is off unless set: in a definition, a trigger mostly
    /// keeps a state up to date, as a State does (an automation's trigger is on unless set; the catalog's default is that
    /// one, and stays so).
    /// </summary>
    static bool TrackHistory(JsonObject node, VisualCatalogSnapshot snapshot) =>
        node["config"]?["trackHistory"] is JsonValue given && given.TryGetValue<bool>(out var track) ? track
        : VisualNodeCatalog.Text(node, "kind") == "trigger" ? false
        : VisualNodeCatalog.Spec(node, snapshot)?["config"]?["trackHistory"]?["default"] is JsonValue fallback && fallback.TryGetValue<bool>(out var byKind) ? byKind
        : true;

    /// <summary>
    /// Runs the saved definition: compiles it and loads it, or replaces the revision running. A failure is recorded as
    /// its load error and answered as <c>activationError</c>; when no revision is left running then, the entities it
    /// last ran are shown unavailable. A definition that loaded but whose <c>Start()</c> threw answers that error as
    /// <c>activationError</c>, and stays active. A definition that is off is never started: whatever of it runs is unloaded.
    /// </summary>
    /// <param name="services">The typed service calls the request read once, so the compile references them.</param>
    /// <param name="warnings">The save's warnings, such as a command no flow handles; answered as <c>diagnostics</c> when there is any.</param>
    /// <param name="libraries">The libraries the document is answered against (<see cref="EntityJson"/>); the saved ones when null.</param>
    /// <returns><c>{id, saved, document, active, activationError}</c>, and <c>diagnostics</c> with warnings.</returns>
    internal async Task<object> ActivateDefinition(EntityDefinitionDocument document, GeneratedServiceCalls? services = null, IReadOnlyList<VisualDiagnostic>? warnings = null,
        VisualCatalogSnapshot? libraries = null)
    {
        var (json, active, activationError) = await Activate(document, services, libraries ?? SavedLibraries(services).Preview.Snapshot);
        return warnings is { Count: > 0 } ? new { id = document.Id, saved = true, document = json, active, activationError, diagnostics = warnings }
            : new { id = document.Id, saved = true, document = json, active, activationError };
    }

    // ActivateDefinition's work: the document as answered against the libraries, whether its revision runs now, and why not.
    async Task<(JsonObject Json, bool Active, string? ActivationError)> Activate(EntityDefinitionDocument document, GeneratedServiceCalls? services, VisualCatalogSnapshot libraries)
    {
        string? activationError = null;
        if (!document.Enabled) await UnloadDefinition(document.Id);
        else
        {
            activationError = await LoadDefinition(document, services);
            // The answer says how the start went, unless it takes long; a publish that failed in it is the host's to log.
            if (RunningHost(document.Id) is { } host)
                try { await host.Started.WaitAsync(StartReportWait); } catch (Exception) { }
            // Loaded, so active, but its Start() threw: the answer says why.
            if (activationError == null && RunningHost(document.Id) is { Status: "failed" } failed) activationError = failed.Error;
        }
        var json = EntityJson(document, libraries);
        return (json, json["currentRevisionActive"]!.GetValue<bool>(), activationError);
    }

    /// <summary>
    /// Compiles the definition and loads it, or replaces the revision running, without waiting for its start. A failure
    /// is recorded as its load error; when no revision is left running then, the entities it last ran are shown unavailable.
    /// </summary>
    /// <returns>The load error; null when it loaded.</returns>
    async Task<string?> LoadDefinition(EntityDefinitionDocument document, GeneratedServiceCalls? services = null)
    {
        var path = EntityRuntimePath(document.Id);
        var known = RunningHost(document.Id)?.Entities ?? [];
        try { await LoadOrReplace(await CompileDefinition(document, services)); }
        catch (Exception ex)
        {
            RecordDefinitionLoadError(document, ex);
            if (RunningHost(document.Id) == null) PostUnavailable(known);
            return _loadErrors[path];
        }
        ClearLoadError(path);
        return null;
    }

    /// <summary>The definition with the source generated from it against these libraries.</summary>
    /// <exception cref="DefinitionValidationException">It is not valid against them.</exception>
    EntityDefinitionDocument Regenerated(EntityDefinitionDocument document, VisualCatalogSnapshot snapshot, string generation)
    {
        var preview = DefinitionGeneratorWith(null).Preview(document.Body, snapshot);
        if (!preview.Valid) throw new DefinitionValidationException(preview);
        return document with { Source = preview.Source, SourceMap = preview.SourceMap, CatalogGeneration = generation, EmbeddedCatalog = preview.EmbeddedCatalog?.ToJsonString() };
    }

    /// <summary>
    /// Records why the definition does not load, as <see cref="RecordLoadError"/> does. A graph that does not validate reads
    /// as an automation's graph does: its errors only, each after the id of its node, and it is tried again on a push of
    /// Home Assistant's services or entity classes only when that can cure it: a problem in a line of code (a Custom C# node
    /// compiled against a service not there yet) or in the node libraries.
    /// </summary>
    void RecordDefinitionLoadError(EntityDefinitionDocument document, Exception error)
    {
        var path = EntityRuntimePath(document.Id);
        if (document.Logic.Kind != "graph" || error is not DefinitionValidationException { Preview.Diagnostics: var diagnostics })
        {
            RecordLoadError(path, error);
            return;
        }
        var errors = diagnostics.Where(d => d.Severity >= 3).ToArray();
        if (errors.Length > 0) _loadProblems[path] = errors.Length; else _loadProblems.TryRemove(path, out _);
        if (errors.Any(d => d.StartLine != null || d.Code == "definition.library")) _curableLoadErrors[path] = 0;
        else _curableLoadErrors.TryRemove(path, out _);
        _loadErrors[path] = string.Join("\n", GraphProblems(diagnostics));
    }

    /// <summary>A graph definition's problems as its load error and startup log line give them: its errors only, each after the id of its node.</summary>
    static string[] GraphProblems(IEnumerable<VisualDiagnostic> diagnostics) =>
        [.. diagnostics.Where(d => d.Severity >= 3).Select(d => d.NodeId is { } node ? $"{node}: {d.Message}" : d.Message)];

    /// <summary>
    /// Stores the definition's regenerated source, so the revision that runs it is recognised as current and a later
    /// activation compiles it, not the code its file embedded before (see <see cref="EntityDefinitionStore.RefreshDerived"/>).
    /// A failure is logged, never thrown.
    /// </summary>
    void StoreRegenerated(EntityDefinitionDocument current)
    {
        try { EntityDocuments.RefreshDerived(current.Id, current.Revision, current.Source, current.SourceMap, current.CatalogGeneration); }
        catch (Exception ex) { try { Logger.Error($"Cannot refresh stored source of entity definition {current.Id}: {ex}"); } catch { } }
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

    /// <summary>
    /// The request's <c>definition</c>. A setting given <c>keep</c> carries no value (null counts as none): the value is the
    /// stored definition's, which the save puts in (<see cref="DefinitionSetting.Keep"/>).
    /// </summary>
    static DefinitionBody DefinitionBody(JsonObject request)
    {
        var body = ApiRequest.Object(request, "definition").Deserialize<DefinitionBody>(VisualStore.Json) ?? throw new ArgumentException("definition is required");
        // The value is not quoted: it may be a secret.
        if (body.Settings?.FirstOrDefault(s => s is { Keep: true, Value: { } value } && value.GetValueKind() != JsonValueKind.Null) is { } both)
            throw new ArgumentException($"Setting {both.Key} has both a value and keep; give the value, or keep to leave the saved one as it is");
        return body;
    }

    /// <summary>
    /// Adds to each listed definition how it has been running, in the members and shapes the automations page's list
    /// uses (<see cref="HomeList"/>), from one history query: <c>recentRuns</c> (at most 10, newest last), <c>runsToday</c>
    /// (since <paramref name="today"/>), <c>failedLastDay</c> and <c>lastRunUtc</c> (null when it never ran).
    /// </summary>
    void WithRecentRuns(JsonObject[] listed, DateTime today)
    {
        static string Identity(JsonObject json) => json["automationId"]!.GetValue<string>();
        var runs = _history.Recent(listed.Select(Identity).ToArray(), today, DateTime.UtcNow.AddDays(-1));
        foreach (var json in listed)
        {
            var recent = runs.GetValueOrDefault(Identity(json));
            json["recentRuns"] = JsonSerializer.SerializeToNode(recent?.Runs ?? [], VisualStore.Json);
            json["runsToday"] = recent?.RunsToday ?? 0;
            json["failedLastDay"] = recent?.FailedLastDay ?? 0;
            json["lastRunUtc"] = JsonSerializer.SerializeToNode(recent?.LastRunUtc, VisualStore.Json);
        }
    }

    static object PreviewJson(DefinitionPreview preview) => new { valid = preview.Valid, diagnostics = preview.Diagnostics, source = preview.Source, sourceMap = preview.SourceMap };

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
            // Before any field an operation reads: an unknown one is answered as such, not as a field it lacks.
            if (operation is not ("types" or "list" or "read" or "preview" or "save" or "set_enabled" or "delete")) throw new ArgumentException("Unknown entity operation");
            object result;
            // One read of the typed service calls for the whole request, as VisualRequest does.
            var services = _diagnosticsProvider.ServiceCalls();
            var libraries = SavedLibraries(services);
            var snapshot = libraries.Preview.Snapshot;
            // A graph is previewed and saved against the catalog an automation's is: the libraries and Home Assistant's services.
            var authoring = AuthoringSnapshot(snapshot);
            var store = EntityDocumentsWith(services);
            if (operation == "types")
                result = new { types = snapshot.EntityTypes.Values.OrderBy(t => t.Id, StringComparer.Ordinal).ToArray(), generation = libraries.Generation };
            else if (operation == "preview")
            {
                var preview = DefinitionGeneratorWith(services).Preview(DefinitionBody(request), authoring);
                // A graph's entity ids that Home Assistant does not have are advice, as an automation's are.
                result = PreviewJson(preview.Graph == null ? preview : preview with { Diagnostics = [.. preview.Diagnostics, .. EntityWarnings(preview.Graph, authoring)] });
            }
            // Listing omits the generated source and its map; "read" returns them. The entities page's list (view "home")
            // adds how each definition has been running, as the automations page's does.
            else if (operation == "list")
            {
                var view = ApiRequest.OptionalString(request, "view");
                if (view is not (null or "home")) throw new ArgumentException("Unknown list view; the entity definitions list has the view home");
                var today = view == "home" ? StartOfToday(ApiRequest.OptionalString(request, "todayUtc")) : (DateTime?)null;
                var listed = store.List().Select(document =>
                {
                    var json = EntityJson(document, snapshot);
                    json.Remove("source"); json.Remove("sourceMap");
                    return json;
                }).ToArray();
                if (today != null) WithRecentRuns(listed, today.Value);
                result = listed;
            }
            else
            {
                var id = ApiRequest.String(request, "id");
                if (operation == "read") result = EntityJson(store.Read(id) ?? throw new FileNotFoundException("Entity definition not found"), snapshot);
                else if (operation == "set_enabled")
                {
                    var enabled = ApiRequest.Bool(request, "enabled");
                    var document = store.SetEnabled(id, enabled);
                    // Turned on, it runs the libraries as saved now, never the code its file embeds from before; one not
                    // valid against them records why and does not load. One whose saved revision already runs is left as it is.
                    var valid = true;
                    if (enabled)
                        try { document = Regenerated(document, snapshot, libraries.Generation); StoreRegenerated(document); }
                        catch (DefinitionValidationException ex) { RecordDefinitionLoadError(document, ex); valid = false; }
                    bool active;
                    string? activationError;
                    if (!enabled || valid && !EntityJson(document, snapshot)["currentRevisionActive"]!.GetValue<bool>())
                        (_, active, activationError) = await Activate(document, services, snapshot);
                    else
                    {
                        // Not activated: either it is not valid, and its load error says why, or its revision already runs,
                        // when the answer says whether its Start() failed, as a read does.
                        var json = EntityJson(document, snapshot);
                        active = json["currentRevisionActive"]!.GetValue<bool>();
                        activationError = valid ? (json["status"]!.GetValue<string>() == "failed" ? json["activationError"]?.GetValue<string>() : null)
                            : json["activationError"]?.GetValue<string>();
                    }
                    result = new { id, enabled = document.Enabled, active, activationError };
                }
                else
                {
                    var revision = ApiRequest.Long(request, "expectedRevision", "the revision this replaces, 0 for a new definition");
                    if (operation == "save")
                    {
                        var document = store.Save(id, DefinitionBody(request), revision, authoring, libraries.Generation, ApiRequest.OptionalBool(request, "enabled"), out var preview);
                        // A valid preview's diagnostics are its warnings: advice, which never stops the save or its activation;
                        // so are a graph's entity ids that Home Assistant does not have (EntityWarnings never throws).
                        result = await ActivateDefinition(document, services, preview.Graph == null ? preview.Diagnostics : [.. preview.Diagnostics, .. EntityWarnings(preview.Graph, authoring)], snapshot);
                    }
                    else if (operation == "delete")
                    {
                        store.Delete(id, revision);
                        // As an automation's delete: its run history's identity goes too, so a definition saved later under
                        // this id starts a history of its own.
                        await _userScriptManager.DeleteScriptAsync(EntityRuntimePath(id));
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
