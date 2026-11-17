using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>
/// Entity definitions whose logic is a graph of nodes, hosted over a whole manager through the entity API, with recording
/// fakes for the entity bridge and a dictionary for Home Assistant's states: a derived binary sensor follows its sources,
/// becomes unavailable while one cannot be read and warns of that once; the last of several quick changes wins; a switch
/// runs its command flows, and a command fails when its flow fails, has none or does not finish; the When started flow runs
/// before the entities are registered and its failure fails the save; a device groups its entities; settings, revisions,
/// turning off and on, delete, library saves and a restart reach the graph; a trigger flow sets a state; and the run history
/// records the command and setup flows but not the States.
/// </summary>
static class EntityGraphTests
{
    [RuntimeSuite("--entity-graph", Order = 16)]
    public static async Task Run()
    {
        var failures = new List<string>();
        // Every test runs, each over its own manager, and each failure is reported under its name.
        foreach (var (name, test) in new (string, Func<Task>)[] {
            (nameof(DerivedMotionSensorFollowsItsSources), DerivedMotionSensorFollowsItsSources),
            (nameof(AnUnavailableSourceMakesTheEntityUnavailableUntilItReturns), AnUnavailableSourceMakesTheEntityUnavailableUntilItReturns),
            (nameof(TheLastOfSeveralQuickChangesWins), TheLastOfSeveralQuickChangesWins),
            (nameof(ASwitchRunsItsCommandFlows), ASwitchRunsItsCommandFlows),
            (nameof(AFailingCommandFlowFailsTheCommand), AFailingCommandFlowFailsTheCommand),
            (nameof(ACommandWithoutAFlowFails), ACommandWithoutAFlowFails),
            (nameof(ACommandThatDoesNotFinishTimesOut), ACommandThatDoesNotFinishTimesOut),
            (nameof(SetupRunsBeforeTheSyncAndItsFailureFailsTheSave), SetupRunsBeforeTheSyncAndItsFailureFailsTheSave),
            (nameof(ADeviceDefinitionGroupsItsEntities), ADeviceDefinitionGroupsItsEntities),
            (nameof(SettingsReachTheGraph), SettingsReachTheGraph),
            (nameof(ReplacingARevisionKeepsCustomisedEntities), ReplacingARevisionKeepsCustomisedEntities),
            (nameof(DisableEnableAndDelete), DisableEnableAndDelete),
            (nameof(ALibrarySaveRebuildsAGraphDefinitionThatUsesALibraryNode), ALibrarySaveRebuildsAGraphDefinitionThatUsesALibraryNode),
            (nameof(ALibrarySaveThatBreaksAGraphDefinitionIsRefused), ALibrarySaveThatBreaksAGraphDefinitionIsRefused),
            (nameof(StartupLoadsGraphDefinitions), StartupLoadsGraphDefinitions),
            (nameof(ReactiveRunsAreNotRecordedAndCommandRunsAre), ReactiveRunsAreNotRecordedAndCommandRunsAre),
            (nameof(DeleteForgetsTheRunHistory), DeleteForgetsTheRunHistory),
            (nameof(AFailingStateWarnsOnceUntilItRecovers), AFailingStateWarnsOnceUntilItRecovers),
            (nameof(ATriggerFlowInADefinitionCanSetState), ATriggerFlowInADefinitionCanSetState),
            (nameof(AnUnknownSourceIsWarnedOfInPreviewAndSave), AnUnknownSourceIsWarnedOfInPreviewAndSave),
            (nameof(AnInvalidGraphDefinitionReadsLikeAnAutomationAndIsNotRetried), AnInvalidGraphDefinitionReadsLikeAnAutomationAndIsNotRetried) })
        {
            try
            {
                await test();
                Console.WriteLine($"PASS entity graph: {name}");
            }
            catch (Exception e) { failures.Add($"{name}: {e.Message}"); }
        }
        if (failures.Count > 0) throw new Exception($"Entity graph: {failures.Count} failing:\n" + string.Join("\n", failures));
    }

    // ---- Graphs ----

    // Wires with ids of their own words, so that the same graph built twice is the same graph (and source).
    static JsonObject Data(string source, string target, string targetPort, string sourcePort = "value")
    { var wire = D(source, target, targetPort, sourcePort); wire["id"] = $"{source}.{sourcePort}:{target}.{targetPort}"; return wire; }
    static JsonObject Next(string source, string target) { var wire = E(source, target); wire["id"] = $"{source}:next:{target}"; return wire; }
    static JsonObject Graph(string name, JsonObject[] nodes, params JsonObject[] edges) =>
        new() { ["version"] = 5, ["name"] = name, ["nodes"] = new JsonArray(nodes), ["edges"] = new JsonArray(edges) };
    static JsonObject Entity(string key, string domain, object? more = null)
    {
        var entity = new JsonObject { ["key"] = key, ["domain"] = domain };
        if (more != null) foreach (var (name, value) in JsonSerializer.SerializeToNode(more)!.AsObject()) entity[name] = value?.DeepClone();
        return entity;
    }
    static JsonObject Definition(string name, JsonObject graph, JsonNode[] entities, JsonNode[]? settings = null, object? device = null) => new()
    {
        ["name"] = name, ["device"] = device == null ? null : JsonSerializer.SerializeToNode(device),
        ["settings"] = new JsonArray((settings ?? []).Select(s => s.DeepClone()).ToArray()),
        ["entities"] = new JsonArray(entities.Select(e => e.DeepClone()).ToArray()),
        ["logic"] = new JsonObject { ["kind"] = "graph", ["graph"] = graph.DeepClone() },
    };
    static readonly object Unset = new { kind = "unset" };
    static JsonObject State(string id, string entity, JsonObject? values = null)
    {
        var config = values ?? new JsonObject(); config["entity"] = entity;
        return N(id, "entity_state", config);
    }
    static JsonObject SetState(string id, string entity, JsonObject values)
    {
        values["entity"] = entity;
        return N(id, "set_entity_state", values);
    }
    static JsonObject On(bool on) => new() { ["is_on"] = Lit("boolean", on) };
    static JsonObject Command(string id, string entity, string command) => N(id, "entity_command", new { entity, command });
    static JsonObject Read(string id, string entity) => N(id, "read_entity", new JsonObject { ["entity"] = Lit("entity_id", entity) });

    /// <summary>The acceptance definition: a binary sensor on while motion A or motion B is (the shared fixture's graph).</summary>
    static JsonObject Motion() => Definition("Any motion", EntityStoreTests.MotionGraph(), [.. EntityStoreTests.MotionEntities().Select(e => e!)]);
    /// <summary>A sensor whose value is <c>sensor.outside</c>'s state, as a number.</summary>
    static JsonObject Copy() => Definition("Outside", Graph("Outside",
        [N("cur", "entity_current", new JsonObject { ["entity"] = Lit("entity_id", "sensor.outside") }), N("f", "entity_field", new { entity = Unset, field = "state", type = "number" }), State("s", "outside")],
        Data("cur", "f", "entity"), Data("f", "s", "value")), [Entity("outside", "sensor", new { name = "Outside" })]);
    /// <summary>A switch whose turn on and turn off flows set its state; either flow left out when asked.</summary>
    static JsonObject Relay(bool turnOn = true, bool turnOff = true, JsonObject[]? before = null)
    {
        var nodes = new List<JsonObject>(); var edges = new List<JsonObject>();
        if (turnOn)
        {
            nodes.Add(Command("on", "relay", "turn_on")); nodes.Add(SetState("set_on", "relay", On(true)));
            var previous = "on";
            foreach (var step in before ?? []) { nodes.Add(step); edges.Add(Next(previous, step["id"]!.GetValue<string>())); previous = step["id"]!.GetValue<string>(); }
            edges.Add(Next(previous, "set_on"));
        }
        if (turnOff) { nodes.Add(Command("off", "relay", "turn_off")); nodes.Add(SetState("set_off", "relay", On(false))); edges.Add(Next("off", "set_off")); }
        return Definition("Relay", Graph("Relay", [.. nodes], [.. edges]), [Entity("relay", "switch", new { name = "Relay" })]);
    }
    /// <summary>A Custom C# node whose code is <paramref name="source"/>, its pins written from the code as the editor writes them.</summary>
    static JsonObject Custom(string id, string source) => N(id, "custom", new JsonObject { ["source"] = source, ["arguments"] = new JsonObject() });
    /// <summary>A value node that hands its input on once the test releases <c>Gate</c>; cancelled with its run.</summary>
    const string SlowValue = "public static class CustomNode\n{\n    public static readonly System.Threading.SemaphoreSlim Gate = new(0);\n    [Node(\"Slow\", Pure = true)]\n    public static async Task<bool> Run(bool value, CancellationToken token)\n    {\n        await Gate.WaitAsync(token);\n        return value;\n    }\n}";
    /// <summary>A step that waits until the test releases <c>Gate</c>; cancelled with its run.</summary>
    const string HoldStep = "public static class CustomNode\n{\n    public static readonly System.Threading.SemaphoreSlim Gate = new(0);\n    [Node(\"Hold\")]\n    public static async Task Run(NodeContext context)\n    {\n        await Gate.WaitAsync(context.CancellationToken);\n    }\n}";

    // ---- The rig ----

    /// <summary>A manager over an <see cref="EntityTestHome"/>, with Home Assistant's states in <see cref="States"/>; disposing unloads it and puts back what it and the home replaced.</summary>
    sealed class Rig : IAsyncDisposable
    {
        readonly Func<string, HasEntityState?> entity = PyInterop.Entity;
        public readonly ConcurrentDictionary<string, string> States = new(StringComparer.Ordinal);
        public EntityTestHome Home { get; }
        public HassSharpManager Api { get; private set; } = null!;
        Rig(string name)
        {
            Home = new("entity-graph-" + name);
            PyInterop.Entity = id => States.TryGetValue(id, out var state) ? new() { EntityId = id, State = state, Attributes = [] } : null;
        }
        public static async Task<Rig> Start(string name, params (string Entity, string State)[] states)
        {
            var rig = new Rig(name);
            foreach (var (id, state) in states) rig.States[id] = state;
            rig.Api = new(rig.Home.Root);
            await rig.Api.Init();
            return rig;
        }
        /// <summary>Home Assistant reloads the entry: a second manager over the same directory.</summary>
        public async Task Restart()
        {
            await Api.UnloadAsync();
            Api = new(Home.Root);
            await Api.Init();
        }
        public async Task<JsonNode> Request(object request) => JsonNode.Parse(await Api.EntityRequest(JsonSerializer.Serialize(request)))!;
        public async Task<JsonObject> Ok(object request)
        {
            var answer = await Request(request);
            Check(answer["ok"]!.GetValue<bool>(), "the request failed: " + answer.ToJsonString());
            return answer["result"]!.AsObject();
        }
        public Task<JsonObject> Save(string id, JsonObject definition, long revision = 0) => Ok(new { operation = "save", id, expectedRevision = revision, definition });
        public Task<JsonObject> Read(string id) => Ok(new { operation = "read", id });
        public async Task<JsonObject> Library(string id, long revision, string source)
        {
            var answer = JsonNode.Parse(await Api.NodeLibraryRequest(JsonSerializer.Serialize(new { operation = "save", id, name = id, expectedRevision = revision, source })))!;
            return answer.AsObject();
        }
        /// <summary>The kind of the library node labelled <paramref name="label"/>.</summary>
        public async Task<string> LibraryNode(string label)
        {
            var catalog = JsonNode.Parse(await Api.NodeLibraryRequest("""{"operation":"catalog"}"""))!["result"]!["catalog"]!["nodes"]!.AsObject();
            return catalog.Single(n => n.Key.StartsWith("library:", StringComparison.Ordinal) && n.Value?["label"]?.GetValue<string>() == label).Key;
        }
        /// <summary>An entity changes in Home Assistant; completes once every run it started has ended.</summary>
        public Task Change(string entityId, string state)
        {
            HasEntityState? old = States.TryGetValue(entityId, out var was) ? new() { EntityId = entityId, State = was, Attributes = [] } : null;
            States[entityId] = state;
            return Api.OnTrackedEntityChange(entityId, new HasEntityState { EntityId = entityId, State = state, Attributes = [] }, old);
        }
        public UserScriptManager Scripts => (UserScriptManager)typeof(HassSharpManager).GetField("_userScriptManager", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(Api)!;
        /// <summary>Sends what the pump holds now: after it, every state posted before is pushed (or left out unchanged).</summary>
        public Task Flush() => Scripts.Pump.FlushAsync();
        public int Batches => Home.Pushes().Length;
        /// <summary>Every snapshot pushed for the unique id, from the given batch on.</summary>
        public JsonObject[] Pushed(string uniqueId, int from = 0) => Home.Snapshots(from).Where(s => s["uniqueId"]!.GetValue<string>() == uniqueId).ToArray();
        /// <summary>Waits for a snapshot of the unique id, from the given batch on, that the condition holds for; returns it.</summary>
        public async Task<JsonObject> Until(string uniqueId, Func<JsonObject, bool> condition, string what, int from = 0)
        {
            await EntityTestHome.Until(() => Pushed(uniqueId, from).Any(condition), $"{what} (pushed for {uniqueId}: {string.Join(" ", Pushed(uniqueId, from).Select(s => s.ToJsonString()))})");
            return Pushed(uniqueId, from).First(condition);
        }
        UserScript Running(string definitionId) =>
            Scripts.GetUserScripts().Single(s => s.CompiledScript.FilePath.EndsWith("entity_" + definitionId + ".cs", StringComparison.Ordinal) && s.Active);
        /// <summary>The static <paramref name="field"/> of the definition's compiled code, such as a Custom C# node's gate.</summary>
        public T Static<T>(string definitionId, string field) =>
            (T)Running(definitionId).CompiledScript.Assembly.GetTypes().Select(t => t.GetField(field)).Single(f => f != null)!.GetValue(null)!;
        /// <summary>Once the definition's host has started and published what it publishes as it starts, and the pump has sent it.</summary>
        public async Task Started(string definitionId)
        {
            await Running(definitionId).Host!.Started.WaitAsync(TimeSpan.FromSeconds(5));
            await Flush();
        }
        public JsonObject[] History(string automationId) =>
            JsonNode.Parse(Api.GetRunHistory(automationId, 0, 100, null))!["runs"]!.AsArray().Select(r => r!.AsObject()).ToArray();
        public async ValueTask DisposeAsync()
        {
            try { if (Api != null) await Api.UnloadAsync(); }
            finally
            {
                PyInterop.Entity = entity;
                await Home.DisposeAsync();
            }
        }
    }

    static bool Available(JsonObject snapshot) => snapshot["available"]!.GetValue<bool>();
    static bool Is(JsonObject snapshot, string field, object? value) => JsonNode.DeepEquals(snapshot["values"]?[field], value == null ? null : JsonSerializer.SerializeToNode(value));
    static string Status(JsonObject document) => document["status"]!.GetValue<string>();
    static string Lines(IEnumerable<string> lines) => string.Join(" | ", lines);

    static async Task<T> Throws<T>(Func<Task> action, string what) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        catch (Exception error) { throw new Exception($"{what}: threw {error.GetType().Name} instead of {typeof(T).Name}: {error.Message}"); }
        throw new Exception($"{what}: did not throw {typeof(T).Name}");
    }

    // ---- Tests ----

    /// <summary>
    /// The acceptance case: saved, the definition syncs one binary sensor without a device, and its first push is worked out
    /// from the sources' states as Home Assistant starts it; each change of a source pushes the new state, and a change that
    /// leaves the state as it was pushes nothing.
    /// </summary>
    static async Task DerivedMotionSensorFollowsItsSources()
    {
        await using var rig = await Rig.Start("motion", ("binary_sensor.motion_a", "off"), ("binary_sensor.motion_b", "off"));
        var saved = await rig.Save("motion", Motion());
        Check(saved["active"]!.GetValue<bool>() && saved["activationError"] == null && Status(saved["document"]!.AsObject()) == "running"
            && saved["document"]!["currentRevisionActive"]!.GetValue<bool>(), "the definition runs once saved: " + saved.ToJsonString());
        var sync = JsonNode.Parse(rig.Home.Syncs("motion").Single())!.AsObject();
        Check(sync.ContainsKey("device") && sync["device"] == null && sync["name"]!.GetValue<string>() == "Any motion", "the sync has no device: " + sync.ToJsonString());
        Check(sync["entities"]!.ToJsonString() == """[{"uniqueId":"motion:any_motion","key":"any_motion","domain":"binary_sensor","name":"Any motion","icon":null,"deviceClass":null,"entityCategory":null,"enabledByDefault":true,"commands":[],"options":{}}]""",
            "the sync describes the one binary sensor: " + sync["entities"]!.ToJsonString());
        Check(saved["document"]!["liveEntities"]!.ToJsonString() == """[{"uniqueId":"motion:any_motion","key":"any_motion","domain":"binary_sensor","name":"Any motion","entityId":"binary_sensor.motion_any_motion"}]""",
            "the live entities: " + saved["document"]!["liveEntities"]!.ToJsonString());

        await rig.Until("motion:any_motion", _ => true, "the first push");
        var first = rig.Pushed("motion:any_motion")[0];
        Check(Available(first) && Is(first, "is_on", false), "the first push is worked out from the startup states: " + first.ToJsonString());

        var batches = rig.Batches;
        await rig.Change("binary_sensor.motion_a", "on");
        await rig.Flush();
        Check(rig.Pushed("motion:any_motion", batches).LastOrDefault() is { } on && Available(on) && Is(on, "is_on", true), "motion A on pushes on: " + Lines(rig.Home.Pushes().Skip(batches)));
        batches = rig.Batches;
        await rig.Change("binary_sensor.motion_b", "on");
        await rig.Flush();
        Check(rig.Pushed("motion:any_motion", batches).Length == 0, "a change that leaves the state on pushes nothing: " + Lines(rig.Home.Pushes().Skip(batches)));
        await rig.Change("binary_sensor.motion_a", "off");
        await rig.Change("binary_sensor.motion_b", "off");
        await rig.Flush();
        Check(rig.Pushed("motion:any_motion", batches).LastOrDefault() is { } off && Available(off) && Is(off, "is_on", false), "both off pushes off: " + Lines(rig.Home.Pushes().Skip(batches)));
    }

    /// <summary>A State whose source cannot be read leaves its entity unavailable, keeping its last value, until the source can be read again.</summary>
    static async Task AnUnavailableSourceMakesTheEntityUnavailableUntilItReturns()
    {
        await using var rig = await Rig.Start("unavailable", ("sensor.outside", "12"));
        await rig.Save("copy", Copy());
        await rig.Until("copy:outside", s => Available(s) && Is(s, "value", 12.0), "the first push copies the source");
        var batches = rig.Batches;
        await rig.Change("sensor.outside", "unavailable");
        await rig.Flush();
        Check(rig.Pushed("copy:outside", batches).LastOrDefault() is { } gone && !Available(gone), "a source that cannot be read makes the entity unavailable: " + Lines(rig.Home.Pushes().Skip(batches)));
        batches = rig.Batches;
        await rig.Change("sensor.outside", "14");
        await rig.Flush();
        Check(rig.Pushed("copy:outside", batches).LastOrDefault() is { } back && Available(back) && Is(back, "value", 14.0), "the source back makes it available with its value: " + Lines(rig.Home.Pushes().Skip(batches)));
    }

    /// <summary>
    /// Three changes in a row while the State is still working out the first: each newer one restarts it, and the last push
    /// is the third's. The State's value node waits until the test releases it.
    /// </summary>
    static async Task TheLastOfSeveralQuickChangesWins()
    {
        await using var rig = await Rig.Start("quick", ("binary_sensor.motion_a", "off"));
        var graph = Derived(Graph("Quick",
            [N("a", "entity_is", new JsonObject { ["entity"] = Lit("entity_id", "binary_sensor.motion_a"), ["state"] = Lit("string", "on") }), Custom("slow", SlowValue), State("s", "quick")],
            Data("a", "slow", "value"), Data("slow", "s", "is_on", "result")));
        await rig.Save("quick", Definition("Quick", graph, [Entity("quick", "binary_sensor")]));
        var gate = rig.Static<SemaphoreSlim>("quick", "Gate");
        await rig.Started("quick");
        Check(rig.Pushed("quick:quick").Length == 0, "the entity was pushed before its State was worked out: " + Lines(rig.Home.Pushes()));
        gate.Release();
        await rig.Until("quick:quick", s => Available(s) && Is(s, "is_on", false), "the startup state once released");

        var batches = rig.Batches;
        var changes = new[] { rig.Change("binary_sensor.motion_a", "on"), rig.Change("binary_sensor.motion_a", "off"), rig.Change("binary_sensor.motion_a", "on") };
        // Whichever runs are still waiting may go on: the earlier ones were cancelled, or end before the third starts.
        gate.Release(changes.Length);
        await Task.WhenAll(changes).WaitAsync(TimeSpan.FromSeconds(10));
        await rig.Flush();
        Check(rig.Pushed("quick:quick", batches).LastOrDefault() is { } last && Available(last) && Is(last, "is_on", true), "the last push is the third change's: " + Lines(rig.Home.Pushes().Skip(batches)));
    }

    /// <summary>A switch's turn on and turn off flows run on its commands: each command completes once its flow has, and the state it set is pushed.</summary>
    static async Task ASwitchRunsItsCommandFlows()
    {
        await using var rig = await Rig.Start("switch");
        var saved = await rig.Save("relay", Relay());
        Check(!saved.ContainsKey("diagnostics"), "a switch with both flows has no warnings: " + saved.ToJsonString());
        var sync = JsonNode.Parse(rig.Home.Syncs("relay").Single())!;
        Check(sync["entities"]![0]!["commands"]!.ToJsonString() == """["turn_on","turn_off"]""", "the switch takes its commands: " + sync.ToJsonString());
        await rig.Until("relay:relay", s => Available(s) && Is(s, "is_on", null), "a switch without a State is pushed unknown as it starts");
        var batches = rig.Batches;
        await rig.Api.EntityCommand("relay:relay", "turn_on", null);
        await rig.Flush();
        Check(rig.Pushed("relay:relay", batches).LastOrDefault() is { } on && Is(on, "is_on", true), "turn on pushes on: " + Lines(rig.Home.Pushes().Skip(batches)));
        batches = rig.Batches;
        await rig.Api.EntityCommand("relay:relay", "turn_off", """{"transition":2}""");
        await rig.Flush();
        Check(rig.Pushed("relay:relay", batches).LastOrDefault() is { } off && Is(off, "is_on", false), "turn off pushes off: " + Lines(rig.Home.Pushes().Skip(batches)));
        var unknown = await Throws<NotSupportedException>(() => rig.Api.EntityCommand("relay:relay", "toggle", null), "a command the switch has not");
        Check(unknown.Message.Contains("toggle", StringComparison.Ordinal), "the unknown command is named: " + unknown.Message);
    }

    /// <summary>A command whose flow fails fails with the flow's error, the text Home Assistant shows; nothing is pushed.</summary>
    static async Task AFailingCommandFlowFailsTheCommand()
    {
        await using var rig = await Rig.Start("failing-command");
        await rig.Save("relay", Relay(before: [Read("read", "sensor.gone")]));
        await rig.Until("relay:relay", _ => true, "the switch starts");
        var batches = rig.Batches;
        var failed = await Throws<InvalidOperationException>(() => rig.Api.EntityCommand("relay:relay", "turn_on", null), "a failing flow");
        Check(failed.Message == "Entity 'sensor.gone' is unavailable.", "the flow's error is the command's: " + failed.Message);
        await rig.Flush();
        Check(rig.Pushed("relay:relay", batches).Length == 0, "a failing flow pushed: " + Lines(rig.Home.Pushes().Skip(batches)));
    }

    /// <summary>A command no flow handles fails, naming the entity and the command; the save warned of it.</summary>
    static async Task ACommandWithoutAFlowFails()
    {
        await using var rig = await Rig.Start("no-flow");
        var saved = await rig.Save("relay", Relay(turnOn: false));
        Check(saved["diagnostics"]?.AsArray().Any(d => d!["code"]!.GetValue<string>() == "command.unhandled" && d["severity"]!.GetValue<int>() == 2) == true && saved["active"]!.GetValue<bool>(),
            "the save warns of the command no flow handles, and runs: " + saved.ToJsonString());
        await rig.Until("relay:relay", _ => true, "the switch starts");
        var failed = await Throws<InvalidOperationException>(() => rig.Api.EntityCommand("relay:relay", "turn_on", null), "a command without a flow");
        Check(failed.Message == "Relay has no flow for Turn on", "the message names the entity and the command: " + failed.Message);
        await rig.Api.EntityCommand("relay:relay", "turn_off", null);
    }

    /// <summary>A command whose flow does not finish in time fails, naming the entity and the command; the flow is left to finish.</summary>
    static async Task ACommandThatDoesNotFinishTimesOut()
    {
        await using var rig = await Rig.Start("timeout");
        var definition = Relay(before: [Custom("hold", HoldStep)]);
        definition["logic"]!["graph"] = Derived(definition["logic"]!["graph"]!.AsObject());
        await rig.Save("relay", definition);
        await rig.Until("relay:relay", _ => true, "the switch starts");
        DefinitionHost.CommandTimeout = TimeSpan.FromMilliseconds(200);
        var batches = rig.Batches;
        var late = await Throws<TimeoutException>(() => rig.Api.EntityCommand("relay:relay", "turn_on", null), "a flow that does not finish");
        Check(late.Message.Contains("Relay", StringComparison.Ordinal) && late.Message.Contains("Turn on", StringComparison.Ordinal), "the timeout names the entity and the command: " + late.Message);
        rig.Static<SemaphoreSlim>("relay", "Gate").Release();
        await rig.Until("relay:relay", s => Is(s, "is_on", true), "the flow left to finish sets the state", batches);
    }

    /// <summary>
    /// The When started flow runs before the entities are registered, and the state it sets is the first one pushed. A
    /// revision whose flow fails is saved, not run: the save answers the flow's error, nothing is registered, and the
    /// revision before it keeps running; a new definition whose flow fails registers nothing.
    /// </summary>
    static async Task SetupRunsBeforeTheSyncAndItsFailureFailsTheSave()
    {
        await using var rig = await Rig.Start("setup");
        var order = new ConcurrentQueue<string>();
        var sync = PyInterop.BeginSyncEntities; var log = PyInterop.Log;
        PyInterop.BeginSyncEntities = (json, ok, fail) => { order.Enqueue("sync"); sync(json, ok, fail); };
        PyInterop.Log = (level, message) => { order.Enqueue(message); log(level, message); };
        try
        {
            JsonObject Level(params JsonObject[] steps)
            {
                var nodes = new List<JsonObject> { N("start", "entity_setup") }; var edges = new List<JsonObject>(); var previous = "start";
                foreach (var step in steps) { nodes.Add(step); edges.Add(Next(previous, step["id"]!.GetValue<string>())); previous = step["id"]!.GetValue<string>(); }
                return Definition("Level", Graph("Level", [.. nodes], [.. edges]), [Entity("level", "sensor")]);
            }
            var good = Level(N("say", "log", new { value = Lit("string", "setting up") }), SetState("first", "level", new JsonObject { ["value"] = Lit("number", 5) }));
            var saved = await rig.Save("level", good);
            Check(saved["active"]!.GetValue<bool>() && Status(saved["document"]!.AsObject()) == "running", "the definition runs: " + saved.ToJsonString());
            var seen = order.ToArray();
            Check(Array.IndexOf(seen, "setting up") is >= 0 and var said && Array.IndexOf(seen, "sync") > said, "the flow runs before the sync: " + Lines(seen));
            await rig.Until("level:level", _ => true, "the first push");
            Check(rig.Pushed("level:level")[0] is var first && Available(first) && Is(first, "value", 5.0), "the first push has the state the flow set: " + rig.Pushed("level:level")[0].ToJsonString());

            var batches = rig.Batches;
            var failing = await rig.Save("level", Level(Read("read", "sensor.gone")), 1);
            Check(failing["saved"]!.GetValue<bool>() && !failing["active"]!.GetValue<bool>() && failing["activationError"]?.GetValue<string>() == "Entity 'sensor.gone' is unavailable."
                && !failing["document"]!["currentRevisionActive"]!.GetValue<bool>() && Status(failing["document"]!.AsObject()) == "running" && failing["document"]!["liveEntities"]!.AsArray().Count == 1,
                "a revision whose flow fails answers its error and leaves the one before running: " + failing.ToJsonString());
            Check(rig.Home.Syncs("level").Length == 1, "a failing flow registered the entities");
            await rig.Flush();
            Check(!rig.Pushed("level:level", batches).Any(s => !Available(s)), "the running revision was shown unavailable: " + Lines(rig.Home.Pushes().Skip(batches)));

            var fresh = await rig.Save("fresh", Level(Read("read", "sensor.gone")));
            Check(!fresh["active"]!.GetValue<bool>() && fresh["activationError"]?.GetValue<string>() == "Entity 'sensor.gone' is unavailable." && Status(fresh["document"]!.AsObject()) == "off",
                "a new definition whose flow fails does not run: " + fresh.ToJsonString());
            Check(rig.Home.Syncs("fresh").Length == 0, "a new definition whose flow fails registered its entities");
        }
        finally { PyInterop.BeginSyncEntities = sync; PyInterop.Log = log; }
    }

    /// <summary>A definition with a device registers its three entities under it, each described as the document says.</summary>
    static async Task ADeviceDefinitionGroupsItsEntities()
    {
        await using var rig = await Rig.Start("device");
        var graph = Graph("Plug", [State("p", "power", new JsonObject { ["value"] = Lit("number", 42) }), State("o", "online", On(true)),
            Command("on", "relay", "turn_on"), SetState("set_on", "relay", On(true)), Command("off", "relay", "turn_off"), SetState("set_off", "relay", On(false))],
            Next("on", "set_on"), Next("off", "set_off"));
        var definition = Definition("Plug", graph,
            [Entity("power", "sensor", new { name = "Power", deviceClass = "power", options = new { unit = "W", stateClass = "measurement" } }),
             Entity("online", "binary_sensor", new { deviceClass = "connectivity", entityCategory = "diagnostic" }),
             Entity("relay", "switch", new { name = "Relay", icon = "mdi:power-socket", enabledByDefault = false })],
            device: new { manufacturer = "Acme", model = "P1", suggestedArea = "Kitchen" });
        var saved = await rig.Save("plug", definition);
        Check(saved["active"]!.GetValue<bool>(), "the device definition runs: " + saved.ToJsonString());
        var sync = JsonNode.Parse(rig.Home.Syncs("plug").Single())!;
        Check(sync["device"]!.ToJsonString() == """{"name":"Plug","manufacturer":"Acme","model":"P1","swVersion":null,"hwVersion":null,"configurationUrl":null,"suggestedArea":"Kitchen","via":null}""",
            "the device is the document's, named after the definition: " + sync["device"]!.ToJsonString());
        Check(sync["entities"]!.ToJsonString() == """[{"uniqueId":"plug:power","key":"power","domain":"sensor","name":"Power","icon":null,"deviceClass":"power","entityCategory":null,"enabledByDefault":true,"commands":[],"options":{"unit":"W","stateClass":"measurement","options":null}},"""
            + """{"uniqueId":"plug:online","key":"online","domain":"binary_sensor","name":null,"icon":null,"deviceClass":"connectivity","entityCategory":"diagnostic","enabledByDefault":true,"commands":[],"options":{}},"""
            + """{"uniqueId":"plug:relay","key":"relay","domain":"switch","name":"Relay","icon":"mdi:power-socket","deviceClass":null,"entityCategory":null,"enabledByDefault":false,"commands":["turn_on","turn_off"],"options":{}}]""",
            "the three entities as the document describes them: " + sync["entities"]!.ToJsonString());
        await rig.Until("plug:power", s => Is(s, "value", 42.0), "the power State");
        await rig.Until("plug:online", s => Is(s, "is_on", true), "the online State");
        await rig.Until("plug:relay", s => Available(s), "the relay");
    }

    /// <summary>A setting's value reaches the graph, also when a save changes only the value, which leaves the generated source as it was.</summary>
    static async Task SettingsReachTheGraph()
    {
        await using var rig = await Rig.Start("settings");
        var graph = Graph("Level", [N("k", "setting", new { key = "offset" }), State("s", "level")], Data("k", "s", "value"));
        JsonObject Level(double offset) => Definition("Level", graph, [Entity("level", "sensor")], [new JsonObject { ["key"] = "offset", ["type"] = "number", ["label"] = "Offset", ["value"] = offset }]);
        var first = await rig.Save("level", Level(4));
        await rig.Until("level:level", s => Is(s, "value", 4.0), "the State has the setting's value");
        var batches = rig.Batches;
        var second = await rig.Save("level", Level(7), 1);
        Check(second["document"]!["source"]!.GetValue<string>() == first["document"]!["source"]!.GetValue<string>(), "a value-only change left the source as it was");
        Check(second["active"]!.GetValue<bool>() && second["document"]!["currentRevisionActive"]!.GetValue<bool>() && rig.Home.Syncs("level").Length == 2, "the new value is activated: " + second.ToJsonString());
        await rig.Until("level:level", s => Is(s, "value", 7.0), "the State has the new value", batches);
    }

    /// <summary>
    /// A revision that renames an entity's key registers the new set once it prepared: the renamed entity's old unique id
    /// is left out of the sync (Home Assistant removes it then), and the others keep theirs.
    /// </summary>
    static async Task ReplacingARevisionKeepsCustomisedEntities()
    {
        await using var rig = await Rig.Start("replace");
        JsonObject Pair(string second) => Definition("Pair", Graph("Pair", [State("s1", "a", new JsonObject { ["value"] = Lit("number", 1) }), State("s2", second, new JsonObject { ["value"] = Lit("number", 2) })]),
            [Entity("a", "sensor", new { name = "A" }), Entity(second, "sensor")]);
        await rig.Save("pair", Pair("b"));
        await rig.Until("pair:b", _ => true, "b is pushed");
        var batches = rig.Batches;
        var renamed = await rig.Save("pair", Pair("c"), 1);
        Check(renamed["active"]!.GetValue<bool>(), "the renaming revision runs: " + renamed.ToJsonString());
        var syncs = rig.Home.Syncs("pair");
        static string[] Ids(string sync) => JsonNode.Parse(sync)!["entities"]!.AsArray().Select(e => e!["uniqueId"]!.GetValue<string>()).ToArray();
        Check(syncs.Length == 2 && Ids(syncs[0]).SequenceEqual(["pair:a", "pair:b"]) && Ids(syncs[1]).SequenceEqual(["pair:a", "pair:c"]), "the second sync: " + Lines(syncs));
        Check(JsonNode.Parse(syncs[1])!["entities"]![0]!.ToJsonString() == JsonNode.Parse(syncs[0])!["entities"]![0]!.ToJsonString(), "the untouched entity is described as before");
        await rig.Until("pair:c", s => Is(s, "value", 2.0), "c is pushed", batches);
        await rig.Until("pair:a", s => Is(s, "value", 1.0), "a is pushed again", batches);
        Check(rig.Pushed("pair:b", batches).Length == 0, "the renamed entity's old unique id was pushed");
    }

    /// <summary>Off shows the entities unavailable and refuses commands; on registers and runs it again; delete unloads it and removes its entities.</summary>
    static async Task DisableEnableAndDelete()
    {
        await using var rig = await Rig.Start("toggle", ("binary_sensor.motion_a", "on"), ("binary_sensor.motion_b", "off"));
        await rig.Save("motion", Motion());
        await rig.Until("motion:any_motion", s => Is(s, "is_on", true), "the motion State");
        var batches = rig.Batches;
        var off = await rig.Ok(new { operation = "set_enabled", id = "motion", enabled = false });
        Check(off.ToJsonString() == """{"id":"motion","enabled":false,"active":false,"activationError":null}""", "turned off: " + off.ToJsonString());
        await rig.Until("motion:any_motion", s => !Available(s), "the entity unavailable once off", batches);
        Check(Status(await rig.Read("motion")) == "off", "it reads off");
        await rig.Change("binary_sensor.motion_a", "off");
        await rig.Flush();
        Check(rig.Pushed("motion:any_motion", batches).All(s => !Available(s)), "a definition that is off worked out its state: " + Lines(rig.Home.Pushes().Skip(batches)));

        batches = rig.Batches;
        var on = await rig.Ok(new { operation = "set_enabled", id = "motion", enabled = true });
        Check(on.ToJsonString() == """{"id":"motion","enabled":true,"active":true,"activationError":null}""" && rig.Home.Syncs("motion").Length == 2, "turned on: " + on.ToJsonString());
        await rig.Until("motion:any_motion", s => Available(s) && Is(s, "is_on", false), "the State worked out again", batches);

        await rig.Save("relay", Relay());
        await rig.Ok(new { operation = "set_enabled", id = "relay", enabled = false });
        Check((await Throws<InvalidOperationException>(() => rig.Api.EntityCommand("relay:relay", "turn_on", null), "a command while off")).Message == "The entity is not running", "a command while off");
        await rig.Ok(new { operation = "set_enabled", id = "relay", enabled = true });
        await rig.Api.EntityCommand("relay:relay", "turn_on", null);

        Check((await rig.Ok(new { operation = "delete", id = "relay", expectedRevision = 1 }))["deleted"]!.GetValue<bool>() && rig.Home.Removals().Contains("relay"), "delete removes the entities");
        Check((await rig.Request(new { operation = "read", id = "relay" }))["error"]!["code"]!.GetValue<string>() == "not_found", "the deleted definition is gone");
        await Throws<InvalidOperationException>(() => rig.Api.EntityCommand("relay:relay", "turn_on", null), "a command after delete");
    }

    /// <summary>A library save that changes a node a graph definition uses rebuilds the definition and runs it with the new code.</summary>
    static async Task ALibrarySaveRebuildsAGraphDefinitionThatUsesALibraryNode()
    {
        await using var rig = await Rig.Start("library");
        Check((await rig.Library("maths", 0, EntityStoreTests.Maths))["ok"]!.GetValue<bool>(), "the library saves");
        var half = await rig.LibraryNode("Half");
        var graph = Graph("Level", [N("k", "setting", new { key = "offset" }), N("h", half, new { arguments = new { } }), State("s", "level")], Data("k", "h", "value"), Data("h", "s", "value", "result"));
        await rig.Save("level", Definition("Level", graph, [Entity("level", "sensor")], [new JsonObject { ["key"] = "offset", ["type"] = "number", ["value"] = 4 }]));
        await rig.Until("level:level", s => Is(s, "value", 2.0), "half the setting");
        var batches = rig.Batches;
        var rebuilt = await rig.Library("maths", 1, EntityStoreTests.Maths.Replace("value / 2", "value / 4"));
        var answer = rebuilt["result"]?["definitions"]?.AsArray().SingleOrDefault(d => d!["id"]!.GetValue<string>() == "level");
        Check(answer?["active"]?.GetValue<bool>() == true && answer["document"]!["revision"]!.GetValue<long>() == 2, "the rebuilt definition runs: " + rebuilt.ToJsonString());
        await rig.Until("level:level", s => Is(s, "value", 1.0), "the new code works out the state", batches);
    }

    /// <summary>A library save that takes away a node a running graph definition uses is refused, naming the definition, which keeps running.</summary>
    static async Task ALibrarySaveThatBreaksAGraphDefinitionIsRefused()
    {
        await using var rig = await Rig.Start("library-break");
        await rig.Library("maths", 0, EntityStoreTests.Maths);
        var half = await rig.LibraryNode("Half");
        var graph = Graph("Level", [N("k", "setting", new { key = "offset" }), N("h", half, new { arguments = new { } }), State("s", "level")], Data("k", "h", "value"), Data("h", "s", "value", "result"));
        await rig.Save("level", Definition("Level", graph, [Entity("level", "sensor")], [new JsonObject { ["key"] = "offset", ["type"] = "number", ["value"] = 4 }]));
        await rig.Until("level:level", s => Is(s, "value", 2.0), "half the setting");
        var refused = await rig.Library("maths", 1, "public static class Maths { [Node(\"Double\", Pure = true)] public static double Twice(double value) => value * 2; }");
        Check(!refused["ok"]!.GetValue<bool>() && refused["error"]!["code"]!.GetValue<string>() == "validation"
            && refused["error"]!["detail"]!["definitions"]!.AsArray().Any(d => d!["id"]!.GetValue<string>() == "level"), "the save is refused for the definition: " + refused.ToJsonString());
        var level = await rig.Read("level");
        Check(Status(level) == "running" && level["revision"]!.GetValue<long>() == 1 && level["currentRevisionActive"]!.GetValue<bool>(), "the definition runs as it was: " + level.ToJsonString());
        var batches = rig.Batches;
        await rig.Ok(new { operation = "set_enabled", id = "level", enabled = false });
        await rig.Ok(new { operation = "set_enabled", id = "level", enabled = true });
        await rig.Until("level:level", s => Available(s) && Is(s, "value", 2.0), "the definition still runs on the library as saved", batches);
    }

    /// <summary>A restart loads the saved graph definitions, registers their entities again and works out their states.</summary>
    static async Task StartupLoadsGraphDefinitions()
    {
        await using var rig = await Rig.Start("startup", ("binary_sensor.motion_a", "off"), ("binary_sensor.motion_b", "off"));
        await rig.Save("motion", Motion());
        await rig.Save("relay", Relay());
        await rig.Until("motion:any_motion", s => Is(s, "is_on", false), "the State before the restart");
        rig.States["binary_sensor.motion_b"] = "on";
        var batches = rig.Batches;
        await rig.Restart();
        Check(rig.Home.Syncs("motion").Length == 2 && rig.Home.Syncs("relay").Length == 2, "the restart registers the entities again");
        var motion = await rig.Read("motion");
        Check(Status(motion) == "running" && motion["revision"]!.GetValue<long>() == 1 && motion["currentRevisionActive"]!.GetValue<bool>(), "it runs after the restart: " + motion.ToJsonString());
        await rig.Until("motion:any_motion", s => Available(s) && Is(s, "is_on", true), "the State worked out after the restart", batches);
        await rig.Api.EntityCommand("relay:relay", "turn_on", null);
        await rig.Until("relay:relay", s => Is(s, "is_on", true), "the command after the restart", batches);
    }

    /// <summary>The run history records the When started and When commanded flows, with the definition's id and a trace, and never a State.</summary>
    static async Task ReactiveRunsAreNotRecordedAndCommandRunsAre()
    {
        await using var rig = await Rig.Start("history", ("binary_sensor.motion_a", "off"), ("binary_sensor.motion_b", "off"));
        var motion = EntityStoreTests.MotionGraph();
        var relay = Relay(turnOff: false)["logic"]!["graph"]!.AsObject();
        foreach (var node in relay["nodes"]!.AsArray()) motion["nodes"]!.AsArray().Add(node!.DeepClone());
        foreach (var edge in relay["edges"]!.AsArray()) motion["edges"]!.AsArray().Add(edge!.DeepClone());
        motion["nodes"]!.AsArray().Add(N("start", "entity_setup"));
        motion["nodes"]!.AsArray().Add(SetState("first", "relay", On(false)));
        motion["edges"]!.AsArray().Add(Next("start", "first"));
        var saved = await rig.Save("both", Definition("Both", motion, [.. EntityStoreTests.MotionEntities().Select(e => e!), Entity("relay", "switch")]));
        var automationId = saved["document"]!["automationId"]!.GetValue<string>();
        await rig.Until("both:any_motion", _ => true, "the State");
        await rig.Change("binary_sensor.motion_a", "on");
        await rig.Api.EntityCommand("both:relay", "turn_on", null);
        var runs = rig.History(automationId);
        Check(runs.Select(r => r["cause"]!.GetValue<string>()).Order(StringComparer.Ordinal).SequenceEqual(["Command", "Setup"]), "the runs recorded: " + Lines(runs.Select(r => r.ToJsonString())));
        var command = runs.Single(r => r["cause"]!.GetValue<string>() == "Command");
        Check(command["status"]!.GetValue<string>() == "Succeeded" && command["visualId"]!.GetValue<string>() == "both" && command["triggerNodeId"]!.GetValue<string>() == "on"
            && command["visualRevision"]!.GetValue<long>() == 1 && command["hasTrace"]!.GetValue<bool>(), "the command's run: " + command.ToJsonString());
        Check(command["trigger"]!.ToJsonString().Contains("command", StringComparison.Ordinal), "the command run's trigger: " + command["trigger"]!.ToJsonString());
        var trace = JsonNode.Parse(rig.Api.GetRunTrace(command["runId"]!.GetValue<string>()))!;
        Check(trace["snapshot"]!["graph"]!["definition"]!["entities"]!.AsArray().Count == 2 && trace["trace"] != null, "the command's trace has the graph with its definition: " + trace.ToJsonString()[..Math.Min(400, trace.ToJsonString().Length)]);
    }

    /// <summary>Deleting a definition, built from nodes or a class, forgets its run history's identity: one saved again under its id starts anew.</summary>
    static async Task DeleteForgetsTheRunHistory()
    {
        await using var rig = await Rig.Start("delete-history");
        var first = (await rig.Save("relay", Relay()))["document"]!["automationId"]!.GetValue<string>();
        await rig.Api.EntityCommand("relay:relay", "turn_on", null);
        Check(rig.History(first).Length == 1, "the command was recorded");
        await rig.Ok(new { operation = "delete", id = "relay", expectedRevision = 1 });
        var again = (await rig.Save("relay", Relay()))["document"]!["automationId"]!.GetValue<string>();
        Check(again != first && rig.History(again).Length == 0, $"saved again, it has a history of its own: {first} / {again}");

        Check((await rig.Library("shelly", 0, EntityApiTests.Library))["ok"]!.GetValue<bool>(), "the class library saves");
        var plug = (await rig.Ok(new { operation = "save", id = "plug", expectedRevision = 0, definition = EntityApiTests.Definition("Plug", "10.0.9.1") }))["document"]!["automationId"]!.GetValue<string>();
        await rig.Ok(new { operation = "delete", id = "plug", expectedRevision = 1 });
        var plugAgain = (await rig.Ok(new { operation = "save", id = "plug", expectedRevision = 0, definition = EntityApiTests.Definition("Plug", "10.0.9.1") }))["document"]!["automationId"]!.GetValue<string>();
        Check(plugAgain != plug, "a class definition saved again has a history of its own");
    }

    /// <summary>
    /// A State that keeps failing warns once, naming the definition, the entity and why; again only when why changes; and
    /// says once that it recovered. Never an error, never a stack trace.
    /// </summary>
    static async Task AFailingStateWarnsOnceUntilItRecovers()
    {
        await using var rig = await Rig.Start("warn", ("sensor.outside", "unavailable"));
        string[] Warnings() => rig.Home.Logs(PyLogLevel.Warn).Where(l => l.Contains("outside", StringComparison.OrdinalIgnoreCase)).ToArray();
        await rig.Save("copy", Copy());
        await EntityTestHome.Until(() => Warnings().Length == 1, "one warning for the failing State: " + Lines(rig.Home.Logs()));
        var warning = Warnings()[0];
        Check(warning.StartsWith("Outside: ", StringComparison.Ordinal) && warning.Contains("Outside (outside)", StringComparison.Ordinal) && warning.Contains("unavailable", StringComparison.Ordinal),
            "the warning names the definition, the entity and why: " + warning);
        await rig.Change("sensor.outside", "unavailable");
        await rig.Change("sensor.outside", "unavailable");
        Check(Warnings().Length == 1, "the same failure again warned again: " + Lines(Warnings()));
        await rig.Change("sensor.outside", "unknown");
        Check(Warnings().Length == 2 && Warnings()[1].Contains("unknown", StringComparison.Ordinal), "another reason warns again: " + Lines(Warnings()));
        await rig.Change("sensor.outside", "unknown");
        Check(Warnings().Length == 2, "the second reason again warned again: " + Lines(Warnings()));
        await rig.Change("sensor.outside", "21");
        var recovered = rig.Home.Logs(PyLogLevel.Info).Where(l => l.StartsWith("Outside: ", StringComparison.Ordinal)).ToArray();
        Check(recovered.Length == 1 && recovered[0].Contains("Outside (outside)", StringComparison.Ordinal), "one line says it recovered: " + Lines(recovered));
        await rig.Change("sensor.outside", "22");
        Check(rig.Home.Logs(PyLogLevel.Info).Count(l => l.StartsWith("Outside: ", StringComparison.Ordinal)) == 1, "a State that keeps working said so again");
        await rig.Change("sensor.outside", "unavailable");
        Check(Warnings().Length == 3, "a failure after the recovery warns again: " + Lines(Warnings()));
        Check(rig.Home.Logs(PyLogLevel.Error).Length == 0, "an error was logged: " + Lines(rig.Home.Logs(PyLogLevel.Error)));
        Check(!rig.Home.Logs().Any(l => l.Contains("   at ", StringComparison.Ordinal)), "a stack trace was logged: " + Lines(rig.Home.Logs()));
    }

    /// <summary>A trigger flow in a definition's graph runs as an automation's does, and its Set state step sets the entity's state.</summary>
    static async Task ATriggerFlowInADefinitionCanSetState()
    {
        await using var rig = await Rig.Start("trigger", ("sensor.button", "idle"));
        var graph = Graph("Button", [N("t", "trigger", new { type = "entity", entity = "sensor.button" }), SetState("set", "last", new JsonObject { ["value"] = Lit("string", "pressed") })], Next("t", "set"));
        await rig.Save("button", Definition("Button", graph, [Entity("last", "sensor")]));
        await rig.Until("button:last", s => Is(s, "value", null), "the sensor starts unknown");
        var batches = rig.Batches;
        await rig.Change("sensor.button", "pressed");
        await rig.Flush();
        Check(rig.Pushed("button:last", batches).LastOrDefault() is { } set && Is(set, "value", "pressed"), "the trigger flow set the state: " + Lines(rig.Home.Pushes().Skip(batches)));
    }

    /// <summary>A source Home Assistant does not have is warned of, on its node, in the preview and in the save's answer; the definition still runs.</summary>
    static async Task AnUnknownSourceIsWarnedOfInPreviewAndSave()
    {
        await using var rig = await Rig.Start("unknown", ("binary_sensor.motion_a", "off"));
        static bool Warned(JsonNode? diagnostics) => diagnostics?.AsArray().Any(d => d!["code"]!.GetValue<string>() == "entity.unknown" && d["nodeId"]!.GetValue<string>() == "b"
            && d["severity"]!.GetValue<int>() == 2 && d["message"]!.GetValue<string>().Contains("binary_sensor.motion_b", StringComparison.Ordinal)) == true;
        var preview = await rig.Ok(new { operation = "preview", definition = Motion() });
        Check(preview["valid"]!.GetValue<bool>() && Warned(preview["diagnostics"]), "the preview warns of motion B: " + preview["diagnostics"]!.ToJsonString());
        var saved = await rig.Save("motion", Motion());
        Check(saved["active"]!.GetValue<bool>() && Warned(saved["diagnostics"]), "the save warns of motion B and runs: " + saved.ToJsonString());
    }

    /// <summary>
    /// A graph definition that no longer validates at a restart (its library node was removed on disk) answers its problems
    /// as an automation's are, each after its node's id, and is not tried again when Home Assistant pushes its services.
    /// </summary>
    static async Task AnInvalidGraphDefinitionReadsLikeAnAutomationAndIsNotRetried()
    {
        await using var rig = await Rig.Start("invalid");
        await rig.Library("maths", 0, EntityStoreTests.Maths);
        var half = await rig.LibraryNode("Half");
        var graph = Graph("Level", [N("k", "setting", new { key = "offset" }), N("h", half, new { arguments = new { } }), State("s", "level")], Data("k", "h", "value"), Data("h", "s", "value", "result"));
        await rig.Save("level", Definition("Level", graph, [Entity("level", "sensor")], [new JsonObject { ["key"] = "offset", ["type"] = "number", ["value"] = 4 }]));
        var path = Path.Combine(rig.Home.Root, ".storage", "hass_sharp", "libraries", "maths.json");
        var library = JsonNode.Parse(File.ReadAllText(path))!;
        library["source"] = "public static class Maths { [Node(\"Double\", Pure = true)] public static double Twice(double value) => value * 2; }";
        await rig.Api.UnloadAsync();
        File.WriteAllText(path, library.ToJsonString());
        await rig.Restart();
        var level = await rig.Read("level");
        var error = level["activationError"]!.GetValue<string>();
        Check(Status(level) == "off" && error.Split('\n').All(line => line.StartsWith("h: ", StringComparison.Ordinal) || line.StartsWith("s: ", StringComparison.Ordinal) || line.StartsWith("k: ", StringComparison.Ordinal)),
            "the problems are each after their node's id: " + level.ToJsonString());
        var curable = (System.Collections.Concurrent.ConcurrentDictionary<string, byte>)typeof(HassSharpManager).GetField("_curableLoadErrors", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(rig.Api)!;
        Check(!curable.Keys.Any(k => k.EndsWith("entity_level.cs", StringComparison.Ordinal)), "a graph that does not validate is retried on every push: " + string.Join(", ", curable.Keys));
    }
}
