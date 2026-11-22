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

    // Why each definition that cannot start last could not, by id, as a save last warned of it (see WarnCannotStart).
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _cannotStart = new(StringComparer.Ordinal);

    /// <summary>
    /// The document as the API answers it: its stored fields, and how it runs now: <c>automationId</c>, <c>active</c>,
    /// <c>currentRevisionActive</c>, <c>activationError</c> (the load error, else why its <c>Start()</c> failed),
    /// <c>status</c> (<c>running</c>, <c>setup</c>, <c>failed</c> when its <c>Start()</c> threw or, on and not loaded,
    /// when it could not be prepared: its setup failed, it does not compile, its libraries are broken; <c>off</c> when
    /// turned off, or on and not loaded for no recorded reason) and its <c>liveEntities</c> (none when not loaded). A
    /// secret setting is answered without its value (<see cref="WithoutSecretValues"/>): every answer of a definition is
    /// built here.
    /// </summary>
    JsonObject EntityJson(EntityDefinitionDocument document)
    {
        var path = EntityRuntimePath(document.Id);
        var running = _userScriptManager.GetUserScripts().FirstOrDefault(s => s.CompiledScript.FilePath == path && s.Active);
        var host = RunningHost(document.Id);
        var status = host?.Status ?? (document.Enabled && _loadErrors.ContainsKey(path) ? "failed" : "off");
        var json = JsonSerializer.SerializeToNode(document, VisualStore.Json)!.AsObject();
        WithoutSecretValues(json, document);
        json["automationId"] = _history.Identity(path);
        json["active"] = running != null;
        json["currentRevisionActive"] = running != null && running.CompiledScript.VisualRevision == document.Revision && running.CompiledScript.SourceCode == document.Source && !_loadErrors.ContainsKey(path);
        json["activationError"] = _loadErrors.TryGetValue(path, out var error) ? error : status == "failed" ? host!.Error : running != null ? null : "Not active";
        json["status"] = status;
        json["liveEntities"] = JsonSerializer.SerializeToNode(host?.Entities ?? [], VisualStore.Json);
        return json;
    }

    /// <summary>
    /// How many devices the definition makes in Home Assistant, as the list answers it: those its Device nodes and C#
    /// devices declare (<see cref="EntityDefinitionDocument.Devices"/>).
    /// </summary>
    internal static int DeviceCount(EntityDefinitionDocument document) => document.Devices?.Count ?? 0;

    /// <summary>
    /// The definition's source compiled, with what its host and its runs need: its generated class, which its host runs
    /// alongside the entities the document describes, with the run metadata an automation's graph has (its id, revision,
    /// graph, embedded catalog and one entry per root).
    /// </summary>
    async Task<CompiledUserScript> CompileDefinition(EntityDefinitionDocument document, GeneratedServiceCalls? services)
    {
        // The graph as previewed and stored, with its definition composed into it, as its source embeds it.
        var graph = DefinitionGraph.Compose(document.Body);
        // A document read from its file carries no embedded catalog: it is worked out again from the libraries as saved.
        var catalog = document.EmbeddedCatalog ?? VisualGenerator.EmbeddedCatalog(graph, SavedLibraries(services).Preview.Snapshot).ToJsonString();
        var snapshot = VisualCatalogSnapshot.BuiltIn.WithRoot(JsonNode.Parse(catalog)!.AsObject());
        var roots = DefinitionGraph.Roots(graph, snapshot);
        // An Entity node has one entry per flow it starts, named for its entity and its pin; every other root one of its own.
        var rootNodes = (graph["nodes"] as JsonArray ?? []).OfType<JsonObject>().Where(n => VisualNodeCatalog.IsRoot(n, snapshot)).ToArray();
        bool Hub(JsonObject node) => VisualNodeCatalog.IsHub(VisualNodeCatalog.Spec(node, snapshot));
        var triggers = rootNodes.Where(n => !Hub(n))
            .ToDictionary(VisualNodeCatalog.EffectiveTriggerMethod, n => new VisualTrigger(VisualNodeCatalog.Text(n, "id"),
                VisualNodeCatalog.Text(n, "label") is { Length: > 0 } label ? label : null, TrackHistory(n, snapshot)), StringComparer.Ordinal);
        foreach (var flow in rootNodes.Where(Hub).SelectMany(n => DefinitionGraph.HubFlows(n, graph, snapshot)))
            triggers[flow.Method] = new VisualTrigger(flow.NodeId, flow.Label, flow.TrackHistory);
        var script = await _compiler.CompileSingleFile(EntityRuntimePath(document.Id), document.Source, services);
        script.DesignatedRootType = "VisualAutomation";
        script.VisualRevision = document.Revision;
        script.CatalogGeneration = document.CatalogGeneration;
        script.VisualId = document.Id;
        script.VisualTriggers = triggers;
        script.VisualGraph = graph.ToJsonString();
        script.VisualCatalog = catalog;
        // Its devices are its Device nodes and its C# devices: it is no device itself. Its C# nodes take their settings'
        // values here, from the document, which only the host reads (they may be secret).
        var components = DefinitionGraph.Components(graph, snapshot, document.Settings);
        script.Definition = new(document.Id, document.Name, document.Settings)
        {
            Entities = DefinitionGraph.Entities(graph, snapshot), Devices = GraphDevices(graph, snapshot), Roots = roots,
            Components = components, Create = components.Count > 0 ? NodeClasses(script.Assembly) : null,
            NodeSetups = NodeSetupsOf(script.Assembly),
        };
        return script;
    }

    /// <summary>
    /// What makes the class of a compiled graph's C# node by its node id: the assembly's <c>SharpNodes.Create</c>, called
    /// as it is (what it throws unwrapped); null for every node when the assembly has none.
    /// </summary>
    internal static Func<string, object?> NodeClasses(System.Reflection.Assembly assembly)
    {
        var create = assembly.GetType("SharpNodes")?.GetMethod("Create", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, [typeof(string)]);
        return nodeId => create?.Invoke(null, System.Reflection.BindingFlags.DoNotWrapExceptions, null, [nodeId], null);
    }

    /// <summary>
    /// The <c>[NodeSetup]</c> of each node library class a compiled graph uses: its assembly's <c>SharpNodeSetups.All</c>,
    /// in order; none when the assembly has no <c>SharpNodeSetups</c>, as a graph without such nodes does.
    /// </summary>
    /// <exception cref="InvalidOperationException">Its <c>SharpNodeSetups</c> has no <c>All</c>, or one that is not an array of setups.</exception>
    internal static IReadOnlyList<NodeClassSetup> NodeSetupsOf(System.Reflection.Assembly assembly)
    {
        if (assembly.GetType("SharpNodeSetups") is not { } type) return [];
        var field = type.GetField("All", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("The compiled definition's SharpNodeSetups has no public static field All");
        var value = field.GetValue(null);
        if (value is ValueTuple<string, Func<NodeSetupContext, Task>>[] all)
            return [.. all.Select(entry => new NodeClassSetup(entry.Item1, entry.Item2))];
        throw new InvalidOperationException(value == null
            ? "The compiled definition's SharpNodeSetups.All is null"
            : $"The compiled definition's SharpNodeSetups.All is a {field.FieldType}, not an array of (string Class, Func<NodeSetupContext, Task> Setup)");
    }

    /// <summary>
    /// The devices a graph's Device nodes declare, as its host hands them to Home Assistant, worked out from the graph again
    /// as its entities are (<see cref="DefinitionGraph.Devices"/>); one whose config is not a device's is left out, which a
    /// saved graph, checked as it was saved, does not have. A C# device's entry (<see cref="VisualNodeCatalog.IsCSharpDevice"/>)
    /// is left out too: it reaches the host as one of the graph's components (<see cref="DefinitionGraph.Components"/>),
    /// whose class gives the rest of its device once set up.
    /// </summary>
    internal static List<DefinitionDevice> GraphDevices(JsonObject graph, VisualCatalogSnapshot snapshot)
    {
        List<DefinitionDevice> devices = [];
        foreach (var entry in DefinitionGraph.Devices(graph, snapshot.Root))
        {
            if (VisualNodeCatalog.IsCSharpDevice(entry)) continue;
            try { if (entry.Deserialize<DefinitionDevice>(VisualStore.Json) is { Key: not null } device) devices.Add(device); }
            catch (JsonException) { }
        }
        return devices;
    }

    /// <summary>
    /// Whether a root's runs are recorded: its <c>trackHistory</c>, else its kind's default as the catalog gives it (on for
    /// When started). A trigger's is off unless set: in a definition, a trigger mostly keeps a state up to date, as an
    /// Entity node's state flow does (an automation's trigger is on unless set; the catalog's default is that one, and
    /// stays so). An Entity node's flows are recorded as its <c>history</c> says (<see cref="DefinitionGraph.HubFlows"/>).
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
    /// <returns><c>{id, saved, document, active, activationError}</c>, and <c>diagnostics</c> with warnings.</returns>
    internal async Task<object> ActivateDefinition(EntityDefinitionDocument document, GeneratedServiceCalls? services = null, IReadOnlyList<VisualDiagnostic>? warnings = null)
    {
        var (json, active, activationError) = await Activate(document, services);
        return warnings is { Count: > 0 } ? new { id = document.Id, saved = true, document = json, active, activationError, diagnostics = warnings }
            : new { id = document.Id, saved = true, document = json, active, activationError };
    }

    // ActivateDefinition's work: the document as answered, whether its revision runs now, and why not.
    async Task<(JsonObject Json, bool Active, string? ActivationError)> Activate(EntityDefinitionDocument document, GeneratedServiceCalls? services)
    {
        string? activationError = null;
        if (!document.Enabled) await UnloadDefinition(document.Id);
        else
        {
            activationError = await LoadDefinition(document, services);
            WarnCannotStart(document, activationError);
            // The answer says how the start went, unless it takes long; a publish that failed in it is the host's to log.
            if (RunningHost(document.Id) is { } host)
                try { await host.Started.WaitAsync(StartReportWait); } catch (Exception) { }
            // Loaded, so active, but its Start() threw: the answer says why.
            if (activationError == null && RunningHost(document.Id) is { Status: "failed" } failed) activationError = failed.Error;
        }
        var json = EntityJson(document);
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

    /// <summary>
    /// After a save (or a library save, or turning it on) loaded the definition: when it could not, one warning names it and
    /// says why, in the words its <c>activationError</c> answers (a setup's own message is user code, which the person
    /// wrote; a setting's value is never in it), and whether the revision before it keeps running. Once per reason: the
    /// same reason again says nothing, until it loads. Never throws.
    /// </summary>
    /// <param name="error">The load error; null when it loaded.</param>
    void WarnCannotStart(EntityDefinitionDocument document, string? error)
    {
        try
        {
            if (error == null)
            {
                _cannotStart.TryRemove(document.Id, out _);
                return;
            }
            if (_cannotStart.TryGetValue(document.Id, out var last) && last == error) return;
            _cannotStart[document.Id] = error;
            var name = $"\"{document.Name}\" ({document.Id})";
            Logger.Warn(RunningHost(document.Id) == null ? $"Entity definition {name} cannot start: {error}"
                : $"Entity definition {name} cannot start its saved revision, so the one before keeps running: {error}");
        }
        catch { }
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
    /// compiled against a service not there yet) or in the node libraries. Code that does not compile reads as each compiler
    /// error's message, code and node library (<see cref="Located"/>), never the generated file's path. Problems and
    /// compiler errors are listed as an automation's startup line lists them (<see cref="Listed"/>):
    /// the first few, each cut short, and how many more, as its <c>activationError</c> and its one warning say them. A
    /// failure that is neither a problem nor a compiler error (its setup failed) reads as the host words a failure
    /// (<see cref="DefinitionHost.Reason"/>), cut short as one problem is (<see cref="Capped"/>): what was thrown, with the
    /// failure underneath when it does not say it already, not the innermost failure alone.
    /// </summary>
    void RecordDefinitionLoadError(EntityDefinitionDocument document, Exception error)
    {
        var path = EntityRuntimePath(document.Id);
        if (error is not DefinitionValidationException { Preview.Diagnostics: var diagnostics })
        {
            RecordLoadError(path, error);
            if (error is CompilationErrorException compilation) _loadErrors[path] = Listed([.. compilation.Errors.Select(e => Located(document, e))]);
            else _loadErrors[path] = Capped(Reason(error));
            return;
        }
        var errors = diagnostics.Where(d => d.Severity >= 3).ToArray();
        if (errors.Length > 0) _loadProblems[path] = errors.Length; else _loadProblems.TryRemove(path, out _);
        if (errors.Any(d => d.StartLine != null || d.Code == "definition.library")) _curableLoadErrors[path] = 0;
        else _curableLoadErrors.TryRemove(path, out _);
        _loadErrors[path] = Listed(GraphProblems(diagnostics));
    }

    /// <summary>Why a definition's setup failed, as its load error and its warning say it (<see cref="DefinitionHost.Reason"/>).</summary>
    static string Reason(Exception error)
    {
        try { return DefinitionHost.Reason(error) ?? error.GetType().Name; }
        catch { return error.GetType().Name; }
    }

    /// <summary>A definition's problems as its load error and startup log line give them: its errors only, each after the id of its node.</summary>
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
    /// stored definition's, which the save puts in (<see cref="DefinitionSetting.Keep"/>). One sent back as an answer masks
    /// it, <c>hasValue</c> and no value, is kept alike; with a value, the value wins (<see cref="DefinitionSetting.HasValue"/>).
    /// An <c>entities</c> key given as null is an empty list, which the preview refuses whatever it holds. A
    /// <c>devices</c> key, as a read answer has it, is no member of a body and is ignored: a definition's devices are its
    /// Device nodes, and the store lists them again (<see cref="DefinitionGraph.Devices"/>). A <c>device</c> that is not
    /// null is refused by the preview (<c>definition.deviceNotAllowed</c>); null is none.
    /// </summary>
    static DefinitionBody DefinitionBody(JsonObject request)
    {
        var definition = ApiRequest.Object(request, "definition");
        var body = definition.Deserialize<DefinitionBody>(VisualStore.Json) ?? throw new ArgumentException("definition is required");
        if (body.Entities == null && definition.ContainsKey("entities")) body = body with { Entities = [] };
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

    /// <summary>
    /// A graph's entity a save gives another domain under the same key is another entity: the last state the one before
    /// showed is forgotten, so that it does not come back as this one's. (An entity a save removes is forgotten as its
    /// definition next starts.) The entities compared are those the two documents list: Entity nodes and C# entities alike
    /// (a C# entity's domain is its class's), each known by <c>&lt;definition id&gt;:&lt;key&gt;</c>, as both are in Home
    /// Assistant. The entities a C# device adds are listed by neither document (their unique ids have three parts,
    /// <c>&lt;definition id&gt;:&lt;device key&gt;:&lt;entity key&gt;</c>), so none of them is forgotten here, and never by
    /// a two-part id: a device's key is no entity key.
    /// </summary>
    void ForgetRetyped(EntityDefinitionDocument? before, EntityDefinitionDocument after)
    {
        if (_userScriptManager.States is not { } states || before?.Entities is not { } old) return;
        static IEnumerable<(string Key, string Domain)> Described(JsonArray entities) =>
            entities.OfType<JsonObject>().Select(e => (VisualNodeCatalog.Text(e, "key"), VisualNodeCatalog.Text(e, "domain")));
        var now = Described(after.Entities ?? []).GroupBy(e => e.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Domain, StringComparer.Ordinal);
        states.Forget(Described(old).Where(e => now.TryGetValue(e.Key, out var domain) && domain != e.Domain).Select(e => $"{after.Id}:{e.Key}"));
    }

    static object PreviewJson(DefinitionPreview preview) => new { valid = preview.Valid, diagnostics = preview.Diagnostics, source = preview.Source, sourceMap = preview.SourceMap };

    /// <summary>
    /// The request's definition with each Custom C# step's pins, and each C# entity's and C# device's signature (its
    /// settings), written from its code first when it asks so
    /// (<c>deriveSignatures</c>), as an automation's request does (<see cref="VisualGenerator.DeriveSignatures(JsonObject, VisualCatalogSnapshot, out List{VisualDiagnostic})"/>);
    /// a request that does not ask, or has no graph, as it came.
    /// </summary>
    /// <param name="removed">What deriving removed (values and wires of pins the code no longer has), one warning each; empty when nothing was derived.</param>
    DefinitionBody DerivedBody(JsonObject request, GeneratedServiceCalls? services, VisualCatalogSnapshot authoring, out List<VisualDiagnostic> removed)
    {
        removed = [];
        var body = DefinitionBody(request);
        if (ApiRequest.OptionalBool(request, "deriveSignatures") != true || body.Logic is not { Kind: "graph", Graph: { } graph }) return body;
        return body with { Logic = body.Logic with { Graph = new VisualGenerator(_diagnosticsProvider, services).DeriveSignatures(graph, authoring, out removed) } };
    }

    /// <summary>
    /// Runs a definition's Trigger step now, as Run now runs an automation's (<see cref="DispatchVisual"/>): through
    /// the same manual path, so the run is recorded with its trace whatever the step's history setting. Only a Trigger
    /// that permits manual runs, of a definition that is on and runs its saved revision.
    /// </summary>
    /// <returns><c>{version, runId}</c> once the run's first record is written.</returns>
    /// <exception cref="ArgumentException">The step is not the definition's, or not a Trigger.</exception>
    /// <exception cref="InvalidOperationException">The definition is off, does not run its saved revision, or the Trigger permits no manual runs.</exception>
    object DispatchDefinition(EntityDefinitionDocument document, string triggerId, VisualCatalogSnapshot snapshot)
    {
        if (!document.Enabled) throw new InvalidOperationException("The entity definition is off");
        var json = EntityJson(document);
        if (!json["currentRevisionActive"]!.GetValue<bool>() || json["status"]!.GetValue<string>() != "running")
            throw new InvalidOperationException("The saved revision of this entity definition is not running: "
                + (_loadErrors.TryGetValue(EntityRuntimePath(document.Id), out var error) ? error : RunningHost(document.Id) is { Status: "failed", Error: { } failure } ? failure : "it has not started"));
        var node = (document.Logic.Graph!["nodes"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(n => VisualNodeCatalog.Text(n, "id") == triggerId) ?? throw new ArgumentException("Trigger not found");
        if (VisualNodeCatalog.Text(node, "kind") != "trigger")
            throw new ArgumentException($"Only a Trigger step can be run by hand; {VisualGraphValidator.Words.StepName(node, VisualNodeCatalog.Spec(node, snapshot))} is not one");
        if (!(node["config"]?["manual"] is JsonValue manual && manual.TryGetValue<bool>(out var allowed) ? allowed : true)) throw new InvalidOperationException("Trigger does not permit manual execution");
        // A trigger run by hand carries what an automation's does (the entity's current state for an entity trigger).
        var payload = TriggerPayload.Manual(VisualNodeCatalog.TriggerType(node, VisualCatalogSnapshot.BuiltIn), node["config"] as JsonObject, PyInterop.Entity);
        return new { version = 1, runId = _userScriptManager.DispatchManual(json["automationId"]!.GetValue<string>(), "VisualAutomation", VisualNodeCatalog.EffectiveTriggerMethod(node), payload) };
    }

    /// <summary>
    /// The entity definitions' JSON API, answered in the envelope <see cref="VisualRequest"/> uses: <c>list</c>,
    /// <c>read</c>, <c>preview</c>, <c>save</c>, <c>set_enabled</c>, <c>delete</c> and <c>run</c> (a Trigger
    /// step, by hand). Saving or turning on a definition runs it; turning it off or deleting it unloads it. Preview and save
    /// take <c>deriveSignatures</c>, as an automation's do: each Custom C# step's pins and each C# node's signature are first written from its code,
    /// what that removed is answered as warnings, and a preview answers the graph with those pins as <c>graph</c>.
    /// </summary>
    /// <param name="requestJson">The request: <c>operation</c> and that operation's fields.</param>
    /// <returns><c>{version, ok: true, result}</c> (for a list, also <c>unreadable: [{id, reason, deletable}]</c>), or <c>{version, ok: false, error: {code, detail}}</c>.</returns>
    public async Task<string> EntityRequest(string requestJson)
    {
        await _visualGate.WaitAsync();
        try
        {
            var request = ApiRequest.Parse(requestJson);
            var operation = ApiRequest.String(request, "operation");
            // Before any field an operation reads: an unknown one is answered as such, not as a field it lacks.
            if (operation is not ("list" or "read" or "preview" or "save" or "set_enabled" or "delete" or "run")) throw new ArgumentException("Unknown entity operation");
            object result;
            // A list's definition files that cannot be read, answered beside its result.
            List<UnreadableDefinition>? unreadable = null;
            // One read of the typed service calls for the whole request, as VisualRequest does.
            var services = _diagnosticsProvider.ServiceCalls();
            var libraries = SavedLibraries(services);
            var snapshot = libraries.Preview.Snapshot;
            // A graph is previewed and saved against the catalog an automation's is: the libraries and Home Assistant's services.
            var authoring = AuthoringSnapshot(snapshot);
            var store = EntityDocumentsWith(services);
            if (operation == "preview")
            {
                var body = DerivedBody(request, services, authoring, out var removed);
                var preview = DefinitionGeneratorWith(services).Preview(body, authoring);
                // A graph's entity ids that Home Assistant does not have are advice, as an automation's are.
                preview = preview with { Diagnostics = [.. removed, .. preview.Diagnostics, .. preview.Graph == null ? [] : EntityWarnings(preview.Graph, authoring)] };
                // Deriving answers the graph with the pins it wrote, and nothing else of the definition (its settings may be secret).
                result = ApiRequest.OptionalBool(request, "deriveSignatures") == true
                    ? new { valid = preview.Valid, diagnostics = preview.Diagnostics, source = preview.Source, sourceMap = preview.SourceMap,
                        graph = body.Logic is { Kind: "graph", Graph: { } derived } ? DefinitionGraph.Decompose(preview.Graph ?? derived) : null }
                    : PreviewJson(preview);
            }
            // Listing omits the generated source and its map; "read" returns them. In place of its devices, it counts
            // them (DeviceCount). The entities page's list (view "home") adds how each definition
            // has been running, as the automations page's does. Beside the result, unreadable names each definition file
            // that cannot be read, with why, so that it can be deleted (at revision 0).
            else if (operation == "list")
            {
                var view = ApiRequest.OptionalString(request, "view");
                if (view is not (null or "home")) throw new ArgumentException("Unknown list view; the entity definitions list has the view home");
                var today = view == "home" ? StartOfToday(ApiRequest.OptionalString(request, "todayUtc")) : (DateTime?)null;
                unreadable = [];
                var listed = store.List(unreadable: unreadable).Select(document =>
                {
                    var json = EntityJson(document);
                    json.Remove("source"); json.Remove("sourceMap");
                    json["devices"] = DeviceCount(document);
                    return json;
                }).ToArray();
                if (today != null) WithRecentRuns(listed, today.Value);
                result = listed;
            }
            else
            {
                var id = ApiRequest.String(request, "id");
                if (operation == "read") result = EntityJson(store.Read(id) ?? throw new FileNotFoundException("Entity definition not found"));
                else if (operation == "run") result = DispatchDefinition(store.Read(id) ?? throw new FileNotFoundException("Entity definition not found"), ApiRequest.String(request, "triggerId"), snapshot);
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
                    if (!enabled || valid && !EntityJson(document)["currentRevisionActive"]!.GetValue<bool>())
                        (_, active, activationError) = await Activate(document, services);
                    else
                    {
                        // Not activated: either it is not valid, and its load error says why, or its revision already runs,
                        // when the answer says whether its Start() failed, as a read does.
                        var json = EntityJson(document);
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
                        EntityDefinitionDocument? before = null;
                        try { before = store.Read(id); } catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
                        var document = store.Save(id, DerivedBody(request, services, authoring, out var removed), revision, authoring, libraries.Generation, ApiRequest.OptionalBool(request, "enabled"), out var preview);
                        ForgetRetyped(before, document);
                        // A valid preview's diagnostics are its warnings: advice, which never stops the save or its activation;
                        // so are what deriving removed and a graph's entity ids that Home Assistant does not have (EntityWarnings never throws).
                        result = await ActivateDefinition(document, services, [.. removed, .. preview.Diagnostics, .. preview.Graph == null ? [] : EntityWarnings(preview.Graph, authoring)]);
                    }
                    else if (operation == "delete")
                    {
                        store.Delete(id, revision);
                        _cannotStart.TryRemove(id, out _);
                        // As an automation's delete: its run history's identity goes too, so a definition saved later under
                        // this id starts a history of its own.
                        await _userScriptManager.DeleteScriptAsync(EntityRuntimePath(id));
                        ClearLoadError(EntityRuntimePath(id));
                        // Its entities' last states go with it, so a definition saved later under this id starts afresh.
                        if (_userScriptManager.States is { } states) states.Forget(states.Ids().Where(unique => unique.StartsWith(id + ":", StringComparison.Ordinal)));
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
            return unreadable == null ? JsonSerializer.Serialize(new { version = 1, ok = true, result }, VisualStore.Json)
                : JsonSerializer.Serialize(new { version = 1, ok = true, result, unreadable }, VisualStore.Json);
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

    /// <summary>
    /// The picture of a camera of a running definition, as JPEG, which Home Assistant pulls while someone watches it: its
    /// current frame, made first when the camera makes its frames, with the requests within one interval sharing one
    /// (<see cref="DefinitionHost.CameraImage"/>). Never waits for the API.
    /// </summary>
    /// <param name="uniqueId">The camera's unique id, <c>&lt;definition id&gt;:&lt;key&gt;</c>.</param>
    /// <exception cref="InvalidOperationException">The entity is not running or not a camera, it has shown no frame, or it cannot make one and has no good one from the last 30 seconds.</exception>
    /// <exception cref="TimeoutException">Making a frame took longer than 8 seconds and there is no good one from the last 30 seconds.</exception>
    public async Task<byte[]> CameraImage(string uniqueId)
    {
        if (!_userScriptManager.Entities.TryGet(uniqueId, out var host, out var entity)) throw new InvalidOperationException("The entity is not running");
        if (entity is not SharpCamera camera) throw new InvalidOperationException("The entity is not a camera");
        return await host.CameraImage(camera);
    }
}
