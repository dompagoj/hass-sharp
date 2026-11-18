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
            (nameof(ACommandWhoseFlowIsCancelledSaysWhy), ACommandWhoseFlowIsCancelledSaysWhy),
            (nameof(SetupRunsBeforeTheSyncAndItsFailureFailsTheSave), SetupRunsBeforeTheSyncAndItsFailureFailsTheSave),
            (nameof(ASetupThatDoesNotFinishFailsTheSave), ASetupThatDoesNotFinishFailsTheSave),
            (nameof(ADeviceDefinitionGroupsItsEntities), ADeviceDefinitionGroupsItsEntities),
            (nameof(SettingsReachTheGraph), SettingsReachTheGraph),
            (nameof(ARenamedKeyLeavesTheSyncAndTheOthersKeepTheirUniqueIds), ARenamedKeyLeavesTheSyncAndTheOthersKeepTheirUniqueIds),
            (nameof(DisableEnableAndDelete), DisableEnableAndDelete),
            (nameof(ALibrarySaveRebuildsAGraphDefinitionThatUsesALibraryNode), ALibrarySaveRebuildsAGraphDefinitionThatUsesALibraryNode),
            (nameof(ALibrarySaveThatBreaksAGraphDefinitionIsRefused), ALibrarySaveThatBreaksAGraphDefinitionIsRefused),
            (nameof(StartupLoadsGraphDefinitions), StartupLoadsGraphDefinitions),
            (nameof(ReactiveRunsAreNotRecordedAndCommandRunsAre), ReactiveRunsAreNotRecordedAndCommandRunsAre),
            (nameof(DeleteForgetsTheRunHistory), DeleteForgetsTheRunHistory),
            (nameof(AFailingStateWarnsOnceUntilItRecovers), AFailingStateWarnsOnceUntilItRecovers),
            (nameof(AFailingNodeInAStateIsNamed), AFailingNodeInAStateIsNamed),
            (nameof(AValueTheEntityRefusesMakesItUnavailable), AValueTheEntityRefusesMakesItUnavailable),
            (nameof(SettingsAreReadWithoutThrowing), SettingsAreReadWithoutThrowing),
            (nameof(ASensorWithAUnitTakesNumericText), ASensorWithAUnitTakesNumericText),
            (nameof(SensorValuesAreCheckedAsHomeAssistantTakesThem), SensorValuesAreCheckedAsHomeAssistantTakesThem),
            (nameof(ATriggerFlowInADefinitionCanSetState), ATriggerFlowInADefinitionCanSetState),
            (nameof(AnUnknownSourceIsWarnedOfInPreviewAndSave), AnUnknownSourceIsWarnedOfInPreviewAndSave),
            (nameof(AnInvalidGraphDefinitionReadsLikeAnAutomationAndIsNotRetried), AnInvalidGraphDefinitionReadsLikeAnAutomationAndIsNotRetried),
            (nameof(AnAutomationSentWithADefinitionStaysAnAutomation), AnAutomationSentWithADefinitionStaysAnAutomation),
            (nameof(ALibrarySaveAnswersNoSecretSettingValues), ALibrarySaveAnswersNoSecretSettingValues),
            (nameof(PreviewAnswersASourceMap), PreviewAnswersASourceMap),
            (nameof(SecretSettingsAreMaskedInEveryAnswer), SecretSettingsAreMaskedInEveryAnswer),
            (nameof(ASaveKeepsASecretItWasNotGiven), ASaveKeepsASecretItWasNotGiven),
            (nameof(ThePreviewAcceptsAKeptSecret), ThePreviewAcceptsAKeptSecret),
            (nameof(NoProblemQuotesASecret), NoProblemQuotesASecret),
            (nameof(TheHomeListSummarisesRuns), TheHomeListSummarisesRuns),
            (nameof(ASwitchKeepsItsStateThroughANewRevision), ASwitchKeepsItsStateThroughANewRevision),
            (nameof(ALibraryRebuildKeepsASwitchsState), ALibraryRebuildKeepsASwitchsState),
            (nameof(AWhenStartedFlowOverridesTheStateCarriedOver), AWhenStartedFlowOverridesTheStateCarriedOver),
            (nameof(AnEntityOfAnotherDomainUnderTheSameKeyStartsUnknown), AnEntityOfAnotherDomainUnderTheSameKeyStartsUnknown),
            (nameof(AValueTheNewRevisionRefusesIsNotCarriedOver), AValueTheNewRevisionRefusesIsNotCarriedOver),
            (nameof(AnUnavailableEntityStaysUnavailableThroughANewRevision), AnUnavailableEntityStaysUnavailableThroughANewRevision),
            (nameof(ACommandFlowThatEndsAtAStopStepSucceeds), ACommandFlowThatEndsAtAStopStepSucceeds),
            (nameof(FailingSetupAndTriggerFlowsAreLoggedByName), FailingSetupAndTriggerFlowsAreLoggedByName),
            (nameof(AnAutomationsFailureIsLoggedAsBefore), AnAutomationsFailureIsLoggedAsBefore) })
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
    /// <summary>Waits, no longer than 5 s, for a Custom C# node's <c>Entered</c> to be released once: its run has reached it.</summary>
    static async Task Reached(SemaphoreSlim entered, string what) => Check(await entered.WaitAsync(TimeSpan.FromSeconds(5)), "timed out waiting: " + what);
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
    const string SlowValue = "public static class CustomNode\n{\n    public static readonly System.Threading.SemaphoreSlim Gate = new(0), Entered = new(0);\n    [Node(\"Slow\", Pure = true)]\n    public static async Task<double> Run(double value, CancellationToken token)\n    {\n        Entered.Release();\n        await Gate.WaitAsync(token);\n        return value;\n    }\n}";
    /// <summary>A step that waits until the test releases <c>Gate</c>; cancelled with its run.</summary>
    const string HoldStep = "public static class CustomNode\n{\n    public static readonly System.Threading.SemaphoreSlim Gate = new(0), Entered = new(0);\n    [Node(\"Hold\")]\n    public static async Task Run(NodeContext context)\n    {\n        Entered.Release();\n        await Gate.WaitAsync(context.CancellationToken);\n    }\n}";
    /// <summary>A value node that fails as a bug in it would: it reads a member of null.</summary>
    const string BrokenValue = "public static class CustomNode\n{\n    [Node(\"Broken\", Pure = true)]\n    public static double Run(double value)\n    {\n        string? text = value > 1000 ? \"big\" : null;\n        return value + text!.Length;\n    }\n}";

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
    /// Three changes in a row, each while the State still works out the one before: each newer one cancels the run before
    /// it, so no value of a cancelled run is ever pushed, and the last push is the third's. The State's value node passes
    /// its source's value through (1, 2, 3, each run its own trigger's), and waits, one run at a time, until the test
    /// releases it; a run that is not cancelled keeps waiting, so the next never reaches the node and the test times out.
    /// </summary>
    static async Task TheLastOfSeveralQuickChangesWins()
    {
        await using var rig = await Rig.Start("quick", ("sensor.level", "0"));
        var graph = Derived(Graph("Quick",
            [N("cur", "entity_current", new JsonObject { ["entity"] = Lit("entity_id", "sensor.level") }), N("f", "entity_field", new { entity = Unset, field = "state", type = "number" }), Custom("slow", SlowValue), State("s", "quick")],
            Data("cur", "f", "entity"), Data("f", "slow", "value"), Data("slow", "s", "value", "result")));
        await rig.Save("quick", Definition("Quick", graph, [Entity("quick", "sensor")]));
        var (gate, entered) = (rig.Static<SemaphoreSlim>("quick", "Gate"), rig.Static<SemaphoreSlim>("quick", "Entered"));
        await rig.Started("quick");
        Check(rig.Pushed("quick:quick").Length == 0, "the entity was pushed before its State was worked out: " + Lines(rig.Home.Pushes()));
        await Reached(entered, "the startup run reaches the value node");
        gate.Release();
        await rig.Until("quick:quick", s => Available(s) && Is(s, "value", 0.0), "the startup state once released");

        var batches = rig.Batches;
        var changes = new List<Task>();
        foreach (var value in new[] { "1", "2", "3" })
        {
            changes.Add(rig.Change("sensor.level", value));
            // The run before this one was cancelled where it waited, and this one reached the node in its place.
            await Reached(entered, $"the run of change {value} reaches the value node");
        }
        gate.Release();
        await Task.WhenAll(changes).WaitAsync(TimeSpan.FromSeconds(10));
        await rig.Flush();
        var pushed = rig.Pushed("quick:quick", batches);
        Check(!pushed.Any(s => Is(s, "value", 1.0) || Is(s, "value", 2.0)), "a cancelled run's value was pushed: " + Lines(rig.Home.Pushes().Skip(batches)));
        Check(pushed.LastOrDefault() is { } last && Available(last) && Is(last, "value", 3.0), "the last push is the third change's: " + Lines(rig.Home.Pushes().Skip(batches)));
        Check(gate.CurrentCount == 0 && entered.CurrentCount == 0, "a run reached the value node or got past it unseen");
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
        // One warning names the definition, the entity and the command, with no stack; the whole failure is at Debug.
        var warnings = rig.Home.Logs(PyLogLevel.Warn).Where(l => l.StartsWith("Relay: ", StringComparison.Ordinal)).ToArray();
        Check(warnings.Length == 1 && warnings[0].StartsWith("Relay: Relay (relay): Turn on failed: ", StringComparison.Ordinal) && warnings[0].Contains("Entity 'sensor.gone' is unavailable.", StringComparison.Ordinal)
            && !warnings[0].Contains("   at ", StringComparison.Ordinal), "the warning names the definition, the entity and the command: " + Lines(warnings));
        Check(rig.Home.Logs(PyLogLevel.Error).Length == 0 && !rig.Home.Logs().Any(l => l.StartsWith("Invocation ", StringComparison.Ordinal)), "the runner logged the failure as an automation's: " + Lines(rig.Home.Logs()));
        Check(rig.Home.Logs(PyLogLevel.Debug).Any(l => l.StartsWith("Relay: Relay (relay): Turn on failed: ", StringComparison.Ordinal) && l.Contains("   at ", StringComparison.Ordinal)), "the whole failure at Debug: " + Lines(rig.Home.Logs(PyLogLevel.Debug)));
    }

    /// <summary>A command no flow handles fails, naming the entity and the command; the save warned of it.</summary>
    static async Task ACommandWithoutAFlowFails()
    {
        await using var rig = await Rig.Start("no-flow");
        // The warning is the definition's, on no node: no node handles the command.
        static bool Unhandled(JsonNode? diagnostics) => diagnostics?.AsArray().Any(d => d!["code"]!.GetValue<string>() == "command.unhandled" && d["severity"]!.GetValue<int>() == 2 && d["nodeId"] == null) == true;
        var preview = await rig.Ok(new { operation = "preview", definition = Relay(turnOn: false) });
        Check(preview["valid"]!.GetValue<bool>() && Unhandled(preview["diagnostics"]), "the preview warns of the command no flow handles: " + preview["diagnostics"]?.ToJsonString());
        var saved = await rig.Save("relay", Relay(turnOn: false));
        Check(Unhandled(saved["diagnostics"]) && saved["active"]!.GetValue<bool>(), "the save warns of the command no flow handles, and runs: " + saved.ToJsonString());
        await rig.Until("relay:relay", _ => true, "the switch starts");
        var failed = await Throws<InvalidOperationException>(() => rig.Api.EntityCommand("relay:relay", "turn_on", null), "a command without a flow");
        Check(failed.Message == "Relay has no flow for Turn on", "the message names the entity and the command: " + failed.Message);
        await rig.Api.EntityCommand("relay:relay", "turn_off", null);
    }

    /// <summary>
    /// A command whose flow does not finish in time fails, naming the entity and the command; the flow is left to finish.
    /// A second command, queued behind that flow, waits for it, so it times out too without its own flow having started;
    /// each flow runs to its end once released, one after the other.
    /// </summary>
    static async Task ACommandThatDoesNotFinishTimesOut()
    {
        await using var rig = await Rig.Start("timeout");
        var definition = Relay(before: [Custom("hold", HoldStep)]);
        definition["logic"]!["graph"] = Derived(definition["logic"]!["graph"]!.AsObject());
        await rig.Save("relay", definition);
        await rig.Until("relay:relay", _ => true, "the switch starts");
        var (gate, entered) = (rig.Static<SemaphoreSlim>("relay", "Gate"), rig.Static<SemaphoreSlim>("relay", "Entered"));
        DefinitionHost.CommandTimeout = TimeSpan.FromMilliseconds(200);
        var batches = rig.Batches;
        var late = await Throws<TimeoutException>(() => rig.Api.EntityCommand("relay:relay", "turn_on", null), "a flow that does not finish");
        Check(late.Message == "Relay: Turn on did not finish within 0.2 s; it goes on in the background", "the timeout names the entity and the command: " + late.Message);
        await Reached(entered, "the first flow is held");
        var queued = await Throws<TimeoutException>(() => rig.Api.EntityCommand("relay:relay", "turn_on", null), "a command queued behind it");
        Check(queued.Message == late.Message, "the queued command's timeout: " + queued.Message);
        Check(entered.CurrentCount == 0, "the queued command's flow started before the one ahead of it ended");
        await rig.Flush();
        Check(rig.Pushed("relay:relay", batches).Length == 0, "a held flow pushed: " + Lines(rig.Home.Pushes().Skip(batches)));
        gate.Release();
        await rig.Until("relay:relay", s => Is(s, "is_on", true), "the first flow, left to finish, sets the state", batches);
        await Reached(entered, "the queued flow starts once the first ended");
        gate.Release();
        DefinitionHost.CommandTimeout = TimeSpan.FromSeconds(10);
        // Both flows ended: a third command runs at once.
        var third = rig.Api.EntityCommand("relay:relay", "turn_on", null);
        await Reached(entered, "a third command's flow starts");
        gate.Release();
        await third;
    }

    /// <summary>
    /// A command whose flow is cancelled, here because a save replaces the definition while the flow waits, fails saying
    /// so in words for a definition, not the runner's words for an automation.
    /// </summary>
    static async Task ACommandWhoseFlowIsCancelledSaysWhy()
    {
        await using var rig = await Rig.Start("cancelled");
        var definition = Relay(before: [Custom("hold", HoldStep)]);
        definition["logic"]!["graph"] = Derived(definition["logic"]!["graph"]!.AsObject());
        await rig.Save("relay", definition);
        await rig.Until("relay:relay", _ => true, "the switch starts");
        var entered = rig.Static<SemaphoreSlim>("relay", "Entered");
        var command = rig.Api.EntityCommand("relay:relay", "turn_on", null);
        await Reached(entered, "the flow is held");
        await rig.Save("relay", definition, 1);
        var cancelled = await Throws<InvalidOperationException>(() => command.WaitAsync(TimeSpan.FromSeconds(5)), "a flow cancelled by a new revision");
        Check(cancelled.Message == "Relay: Turn on did not finish: its flow was cancelled because the definition was reloaded", "the cancelled command's message: " + cancelled.Message);
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

            var stopped = await rig.Save("stopped", Level(SetState("first", "level", new JsonObject { ["value"] = Lit("number", 6) }), N("halt", "stop")));
            Check(stopped["active"]!.GetValue<bool>() && stopped["activationError"] == null && rig.Home.Syncs("stopped").Length == 1,
                "a flow that ends at a Stop step has started the definition: " + stopped.ToJsonString());
            await rig.Until("stopped:level", s => Available(s) && Is(s, "value", 6.0), "the state the flow set before its Stop step");

            var fresh = await rig.Save("fresh", Level(Read("read", "sensor.gone")));
            Check(!fresh["active"]!.GetValue<bool>() && fresh["activationError"]?.GetValue<string>() == "Entity 'sensor.gone' is unavailable." && Status(fresh["document"]!.AsObject()) == "off",
                "a new definition whose flow fails does not run: " + fresh.ToJsonString());
            Check(rig.Home.Syncs("fresh").Length == 0, "a new definition whose flow fails registered its entities");
        }
        finally { PyInterop.BeginSyncEntities = sync; PyInterop.Log = log; }
    }

    /// <summary>
    /// A When started flow that does not finish within the setup time fails the save, saying so; nothing is registered.
    /// </summary>
    static async Task ASetupThatDoesNotFinishFailsTheSave()
    {
        await using var rig = await Rig.Start("setup-timeout");
        DefinitionHost.SetupTimeout = TimeSpan.FromMilliseconds(300);
        var graph = Derived(Graph("Level", [N("start", "entity_setup"), Custom("hold", HoldStep)], Next("start", "hold")));
        var saved = await rig.Save("level", Definition("Level", graph, [Entity("level", "sensor")]));
        Check(!saved["active"]!.GetValue<bool>() && saved["activationError"]?.GetValue<string>() == "Level does not start: its When started flow did not finish within 0.3 s" && rig.Home.Syncs("level").Length == 0,
            "a setup that does not finish fails the save: " + saved.ToJsonString());
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
    /// <remarks>
    /// Only the unique ids the fake bridge is sent are seen here. That an entity keeping its unique id keeps what the
    /// user customised in Home Assistant's registry (its entity id, name, area) is the Python bridge's, and its tests
    /// check it (<c>tests/python/test_entity_platforms.py</c>, <c>test_entity_bridge.py</c>).
    /// </remarks>
    static async Task ARenamedKeyLeavesTheSyncAndTheOthersKeepTheirUniqueIds()
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
        // A class definition beside it, which the same library save rebuilds and runs again.
        Check((await rig.Library("shelly", 0, EntityApiTests.Library))["ok"]!.GetValue<bool>(), "the class library saves");
        await rig.Ok(new { operation = "save", id = "plug", expectedRevision = 0, definition = EntityApiTests.Definition("Plug", "10.0.9.1") });
        var half = await rig.LibraryNode("Half");
        var graph = Graph("Level", [N("k", "setting", new { key = "offset" }), N("h", half, new { arguments = new { } }), State("s", "level")], Data("k", "h", "value"), Data("h", "s", "value", "result"));
        await rig.Save("level", Definition("Level", graph, [Entity("level", "sensor")], [new JsonObject { ["key"] = "offset", ["type"] = "number", ["value"] = 4 }]));
        await rig.Until("level:level", s => Is(s, "value", 2.0), "half the setting");
        var batches = rig.Batches;
        var rebuilt = await rig.Library("maths", 1, EntityStoreTests.Maths.Replace("value / 2", "value / 4"));
        var answer = rebuilt["result"]?["definitions"]?.AsArray().SingleOrDefault(d => d!["id"]!.GetValue<string>() == "level");
        Check(answer?["active"]?.GetValue<bool>() == true && answer["document"]!["revision"]!.GetValue<long>() == 2, "the rebuilt definition runs: " + rebuilt.ToJsonString());
        var plug = rebuilt["result"]!["definitions"]!.AsArray().SingleOrDefault(d => d!["id"]!.GetValue<string>() == "plug");
        Check(plug?["active"]?.GetValue<bool>() == true && plug["document"]!["revision"]!.GetValue<long>() == 2 && Status(plug["document"]!.AsObject()) == "running", "the class definition is rebuilt and runs: " + plug?.ToJsonString());
        await rig.Until("level:level", s => Is(s, "value", 1.0), "the new code works out the state", batches);
        await rig.Api.EntityCommand("plug:relay", "turn_on", null);
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

    /// <summary>
    /// The run history records the When started and When commanded flows, with the definition's id and a trace, and never
    /// a State; a trigger's flow only when its node turns it on.
    /// </summary>
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
        // Two trigger flows on motion A: one as it comes, one that turns its history on.
        motion["nodes"]!.AsArray().Add(N("quiet", "trigger", new { type = "entity", entity = "binary_sensor.motion_a" }));
        motion["nodes"]!.AsArray().Add(N("say_quiet", "log", new { value = Lit("string", "quiet") }));
        motion["edges"]!.AsArray().Add(Next("quiet", "say_quiet"));
        motion["nodes"]!.AsArray().Add(N("tracked", "trigger", new { type = "entity", entity = "binary_sensor.motion_a", trackHistory = true }));
        motion["nodes"]!.AsArray().Add(N("say_tracked", "log", new { value = Lit("string", "tracked") }));
        motion["edges"]!.AsArray().Add(Next("tracked", "say_tracked"));
        var saved = await rig.Save("both", Definition("Both", motion, [.. EntityStoreTests.MotionEntities().Select(e => e!), Entity("relay", "switch")]));
        var automationId = saved["document"]!["automationId"]!.GetValue<string>();
        await rig.Until("both:any_motion", _ => true, "the State");
        await rig.Change("binary_sensor.motion_a", "on");
        await rig.Api.EntityCommand("both:relay", "turn_on", null);
        var runs = rig.History(automationId);
        Check(rig.Home.Logs().Contains("quiet") && rig.Home.Logs().Contains("tracked"), "both trigger flows ran: " + Lines(rig.Home.Logs()));
        Check(runs.Select(r => r["cause"]!.GetValue<string>()).Order(StringComparer.Ordinal).SequenceEqual(["Command", "Entity", "Setup"]), "the runs recorded: " + Lines(runs.Select(r => r.ToJsonString())));
        Check(runs.Single(r => r["cause"]!.GetValue<string>() == "Entity")["triggerNodeId"]!.GetValue<string>() == "tracked", "the trigger flow recorded is the one that turns its history on: " + Lines(runs.Select(r => r.ToJsonString())));
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
        Check(warning.StartsWith("Outside: Outside (outside) is unavailable: ", StringComparison.Ordinal) && warning.Contains("(used by ", StringComparison.Ordinal) && warning.Contains("'unavailable'", StringComparison.Ordinal),
            "the warning names the definition, the entity, the failing node and why: " + warning);
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
        Check(!rig.Home.Logs(PyLogLevel.Warn).Concat(rig.Home.Logs(PyLogLevel.Info)).Any(l => l.Contains("   at ", StringComparison.Ordinal)), "a stack trace was logged above Debug: " + Lines(rig.Home.Logs()));
        // Each failure warned of is logged whole at Debug, once, after its warning; the same failure again logs nothing.
        var details = rig.Home.Logs().Where(l => l.StartsWith("Outside: ", StringComparison.Ordinal) && !l.Contains("has a state again", StringComparison.Ordinal)).ToArray();
        Check(details.Length == 6 && Enumerable.Range(0, 3).All(i => details[2 * i].StartsWith("Outside: Outside (outside) is unavailable: ", StringComparison.Ordinal)
            && details[2 * i + 1].StartsWith("Outside: the State of Outside (outside) failed: ", StringComparison.Ordinal) && details[2 * i + 1].Contains("   at ", StringComparison.Ordinal))
            && rig.Home.Logs(PyLogLevel.Debug).Count(l => l.StartsWith("Outside: the State of", StringComparison.Ordinal)) == 3,
            "each warning, then its failure at Debug, once per reason: " + Lines(details));
    }

    /// <summary>
    /// A bug in a value node of a State (a Custom C# node reading a member of null) is warned of naming that node and the
    /// step it was used by, and logged whole at Debug, so that it can be found in a State of several nodes.
    /// </summary>
    static async Task AFailingNodeInAStateIsNamed()
    {
        await using var rig = await Rig.Start("bug", ("sensor.outside", "12"));
        var graph = Derived(Graph("Outside", [N("cur", "entity_current", new JsonObject { ["entity"] = Lit("entity_id", "sensor.outside") }), N("f", "entity_field", new { entity = Unset, field = "state", type = "number" }),
            Custom("broken", BrokenValue), State("s", "outside")], Data("cur", "f", "entity"), Data("f", "broken", "value"), Data("broken", "s", "value", "result")));
        await rig.Save("bug", Definition("Outside", graph, [Entity("outside", "sensor", new { name = "Outside" })]));
        await rig.Until("bug:outside", s => !Available(s), "the entity is unavailable");
        string[] Warnings() => rig.Home.Logs(PyLogLevel.Warn).Where(l => l.StartsWith("Outside: ", StringComparison.Ordinal)).ToArray();
        await EntityTestHome.Until(() => Warnings().Length == 1, "the warning: " + Lines(rig.Home.Logs()));
        Check(Warnings()[0].StartsWith("Outside: Outside (outside) is unavailable: Broken (used by ", StringComparison.Ordinal) && Warnings()[0].Contains("Object reference not set", StringComparison.Ordinal),
            "the warning names the failing node and its step: " + Warnings()[0]);
        Check(rig.Home.Logs(PyLogLevel.Debug).Any(l => l.Contains("NullReferenceException", StringComparison.Ordinal) && l.Contains("   at ", StringComparison.Ordinal)), "the failure at Debug: " + Lines(rig.Home.Logs(PyLogLevel.Debug)));
    }

    /// <summary>
    /// A value the entity does not take (a sensor's reading that is not one of its options) fails its State's run, as a
    /// failure to work out its inputs does: the entity is unavailable, one warning says why, and a value it takes makes
    /// it available again with that value, said once.
    /// </summary>
    static async Task AValueTheEntityRefusesMakesItUnavailable()
    {
        await using var rig = await Rig.Start("refused", ("sensor.mode", "low"));
        var graph = Graph("Mode", [N("cur", "entity_current", new JsonObject { ["entity"] = Lit("entity_id", "sensor.mode") }), N("f", "entity_field", new { entity = Unset, field = "state", type = "string" }), State("s", "mode")],
            Data("cur", "f", "entity"), Data("f", "s", "value"));
        await rig.Save("mode", Definition("Mode", graph, [Entity("mode", "sensor", new { name = "Mode", deviceClass = "enum", options = new { options = new[] { "low", "high" } } })]));
        await rig.Until("mode:mode", s => Available(s) && Is(s, "value", "low"), "the first push");
        string[] Warnings() => rig.Home.Logs(PyLogLevel.Warn).Where(l => l.StartsWith("Mode: ", StringComparison.Ordinal)).ToArray();
        var batches = rig.Batches;
        await rig.Change("sensor.mode", "bad");
        await rig.Flush();
        Check(rig.Pushed("mode:mode", batches).LastOrDefault() is { } refused && !Available(refused) && Is(refused, "value", "low"), "a refused value makes the entity unavailable: " + Lines(rig.Home.Pushes().Skip(batches)));
        Check(Warnings().SequenceEqual(["Mode: Mode (mode) is unavailable: The value \"bad\" of Mode (mode) is not one of its options: low, high"]), "one warning names the refusal: " + Lines(Warnings()));
        await rig.Change("sensor.mode", "bad");
        Check(Warnings().Length == 1, "the same refusal warned again: " + Lines(Warnings()));
        batches = rig.Batches;
        await rig.Change("sensor.mode", "high");
        await rig.Flush();
        Check(rig.Pushed("mode:mode", batches).LastOrDefault() is { } back && Available(back) && Is(back, "value", "high"), "a value it takes makes it available: " + Lines(rig.Home.Pushes().Skip(batches)));
        Check(rig.Home.Logs(PyLogLevel.Info).Count(l => l == "Mode: Mode (mode) has a state again") == 1, "one line says it recovered: " + Lines(rig.Home.Logs(PyLogLevel.Info)));
    }

    /// <summary>
    /// A sensor with a unit takes text that reads as a number, published as given, as Home Assistant takes it; other text
    /// it refuses, which leaves it unavailable with one warning, until a number comes again.
    /// </summary>
    static async Task ASensorWithAUnitTakesNumericText()
    {
        await using var rig = await Rig.Start("numeric", ("sensor.raw", "21.5"));
        var graph = Graph("Temp", [N("cur", "entity_current", new JsonObject { ["entity"] = Lit("entity_id", "sensor.raw") }), N("f", "entity_field", new { entity = Unset, field = "state", type = "string" }), State("s", "temp")],
            Data("cur", "f", "entity"), Data("f", "s", "value"));
        await rig.Save("temp", Definition("Temp", graph, [Entity("temp", "sensor", new { name = "Temp", deviceClass = "temperature", options = new { unit = "°C" } })]));
        await rig.Until("temp:temp", s => Available(s) && Is(s, "value", "21.5"), "numeric text is published as given");
        string[] Warnings() => rig.Home.Logs(PyLogLevel.Warn).Where(l => l.StartsWith("Temp: ", StringComparison.Ordinal)).ToArray();
        var batches = rig.Batches;
        await rig.Change("sensor.raw", "warm");
        await rig.Flush();
        Check(rig.Pushed("temp:temp", batches).LastOrDefault() is { } refused && !Available(refused), "other text makes it unavailable: " + Lines(rig.Home.Pushes().Skip(batches)));
        Check(Warnings().SequenceEqual(["Temp: Temp (temp) is unavailable: The value \"warm\" of Temp (temp) is text, and a sensor with a unit needs a number"]), "one warning: " + Lines(Warnings()));
        batches = rig.Batches;
        await rig.Change("sensor.raw", "-1e1");
        await rig.Flush();
        Check(rig.Pushed("temp:temp", batches).LastOrDefault() is { } back && Available(back) && Is(back, "value", "-1e1"), "numeric text again: " + Lines(rig.Home.Pushes().Skip(batches)));
    }

    /// <summary>
    /// What a sensor's value takes, by the sensor: with options, one of them; with a unit or a state class, a number or
    /// text that reads as a finite number; otherwise any number or text. A number that is not finite is taken by a sensor
    /// without options and shows as unknown; one with options refuses it, as it is none of them.
    /// </summary>
    static Task SensorValuesAreCheckedAsHomeAssistantTakesThem()
    {
        var value = SharpDomains.Graph["sensor"].State["value"];
        var plain = new SharpSensor("plain"); var unit = new SharpSensor("unit") { Unit = "W" }; var total = new SharpSensor("total") { StateClass = "total" };
        var modes = new SharpSensor("modes") { Options = ["low", "high"] };
        string? Refused(SharpSensor sensor, object? given) => value.Refuses(sensor, given);
        Check(Refused(plain, "warm") == null && Refused(plain, 3.0) == null && Refused(plain, double.NaN) == null && Refused(plain, null) == null, "a plain sensor takes any number or text");
        Check(Refused(unit, "21.5") == null && Refused(unit, " 7 ") == null && Refused(unit, "1e3") == null && Refused(unit, 4.0) == null && Refused(unit, double.PositiveInfinity) == null && Refused(unit, null) == null,
            "a sensor with a unit takes numbers and numeric text");
        Check(Refused(unit, "warm") == "is text, and a sensor with a unit needs a number" && Refused(unit, "Infinity") != null && Refused(unit, "21,5") != null
            && Refused(total, "warm") == "is text, and a sensor with a state class needs a number", "a sensor with a unit or a state class refuses other text");
        Check(Refused(modes, "low") == null && Refused(modes, null) == null && Refused(modes, "warm") == "is not one of its options: low, high"
            && Refused(modes, 1.0) == "is not one of its options: low, high" && Refused(modes, double.NaN) == "is not one of its options: low, high", "a sensor with options takes only them");
        Check(Refused(plain, true) == "is neither a number nor text", "a sensor refuses true or false");
        value.Keep(unit, double.NaN);
        Check(unit.Snapshot("x:unit", running: true)["values"]!["value"] == null, "a number that is not finite shows as unknown: " + unit.Snapshot("x:unit", running: true).ToJsonString());
        return Task.CompletedTask;
    }

    /// <summary>
    /// A setting's value as a run reads it, whatever backs it: a number or text put in by code as well as one parsed; a
    /// value that is neither text, true, false nor a finite number reads as none, never throwing.
    /// </summary>
    static Task SettingsAreReadWithoutThrowing()
    {
        var settings = DefinitionHost.GraphSettings([
            new("whole", JsonValue.Create(4)), new("money", JsonValue.Create(2.5m)), new("text", JsonValue.Create("x")), new("flag", JsonValue.Create(true)),
            new("parsed", JsonNode.Parse("7")), new("list", JsonNode.Parse("[1]")), new("nan", JsonValue.Create(double.NaN)), new("none", null),
            new("secret", JsonValue.Create("s")) { Secret = true }]);
        Check(settings["whole"].Value is 4.0 && settings["money"].Value is 2.5 && settings["text"].Value is "x" && settings["flag"].Value is true && settings["parsed"].Value is 7.0
            && settings["list"].Value == null && settings["nan"].Value == null && settings["none"].Value == null && settings["secret"].Secret && !settings["text"].Secret,
            "the settings as read: " + string.Join(", ", settings.Select(s => $"{s.Key}={s.Value.Value ?? "null"}")));
        return Task.CompletedTask;
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
        Check(rig.Home.Logs(PyLogLevel.Error).Any(l => l.StartsWith("Cannot load entity definition \"Level\" (level): ", StringComparison.Ordinal) && l.Contains(": " + error.Split('\n')[0], StringComparison.Ordinal)
            && l.Contains("; " + error.Split('\n')[1], StringComparison.Ordinal)), "the startup log line has the problems as the load error does: " + Lines(rig.Home.Logs(PyLogLevel.Error)));
        var curable = (System.Collections.Concurrent.ConcurrentDictionary<string, byte>)typeof(HassSharpManager).GetField("_curableLoadErrors", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(rig.Api)!;
        Check(!curable.Keys.Any(k => k.EndsWith("entity_level.cs", StringComparison.Ordinal)), "a graph that does not validate is retried on every push: " + string.Join(", ", curable.Keys));
    }

    /// <summary>
    /// An automation's graph that a client sends with an object <c>definition</c>, over the automation API, is the same
    /// automation as without it: preview and save give the same diagnostics and generated source, the stored graph has no
    /// <c>definition</c>, it still needs a trigger, and a node only an entity definition takes is still refused.
    /// </summary>
    static async Task AnAutomationSentWithADefinitionStaysAnAutomation()
    {
        await using var rig = await Rig.Start("automation-definition");
        async Task<JsonNode> Visual(object request) => JsonNode.Parse(await rig.Api.VisualRequest(JsonSerializer.Serialize(request)))!;
        var stray = new JsonObject { ["entities"] = new JsonArray(Entity("door", "binary_sensor")), ["settings"] = new JsonArray() };
        JsonObject With(JsonObject graph) { var sent = graph.DeepClone().AsObject(); sent["definition"] = stray.DeepClone(); return sent; }
        var plain = Graph("Plain", [N("t", "trigger", new { type = "manual" }), N("say", "log", new { value = Lit("string", "hi") })], Next("t", "say"));

        var alone = (await Visual(new { operation = "preview", graph = plain }))["result"]!;
        var sent = (await Visual(new { operation = "preview", graph = With(plain) }))["result"]!;
        Check(alone["valid"]!.GetValue<bool>() && sent["valid"]!.GetValue<bool>(), "both preview as valid: " + sent.ToJsonString());
        Check(sent["diagnostics"]!.ToJsonString() == alone["diagnostics"]!.ToJsonString(), "the same diagnostics: " + sent["diagnostics"]!.ToJsonString() + " / " + alone["diagnostics"]!.ToJsonString());
        Check(sent["source"]!.GetValue<string>() == alone["source"]!.GetValue<string>(), "the same generated source");

        var saved = await Visual(new { operation = "save", id = "plain", expectedRevision = 0, graph = With(plain) });
        var savedAlone = await Visual(new { operation = "save", id = "alone", expectedRevision = 0, graph = plain });
        Check(saved["ok"]!.GetValue<bool>() && saved["result"]!["active"]!.GetValue<bool>(), "the automation saves and runs: " + saved.ToJsonString());
        var (document, plainDocument) = (saved["result"]!["document"]!, savedAlone["result"]!["document"]!);
        Check(!document["graph"]!.AsObject().ContainsKey("definition") && document["graph"]!.ToJsonString() == plainDocument["graph"]!.ToJsonString(),
            "the stored automation is the graph without the definition: " + document["graph"]!.ToJsonString());
        Check(document["source"]!.GetValue<string>() == plainDocument["source"]!.GetValue<string>() && saved["result"]!["diagnostics"]?.ToJsonString() == savedAlone["result"]!["diagnostics"]?.ToJsonString(),
            "the save generates the source of the graph without the definition, with the same diagnostics");

        static string[] Codes(JsonNode answer) => answer["error"]?["detail"]?["diagnostics"]?.AsArray().Select(d => d!["code"]?.GetValue<string>() ?? "").ToArray() ?? [];
        var state = Graph("State", [N("t", "trigger", new { type = "manual" }), State("s", "door", On(true))]);
        var refused = await Visual(new { operation = "save", id = "state", expectedRevision = 0, graph = With(state) });
        Check(!refused["ok"]!.GetValue<bool>() && refused["error"]!["code"]!.GetValue<string>() == "validation" && Codes(refused).Contains("node.document"),
            "a State in an automation is refused: " + refused.ToJsonString());
        var preview = (await Visual(new { operation = "preview", graph = With(state) }))["result"]!;
        Check(!preview["valid"]!.GetValue<bool>() && preview["diagnostics"]!.AsArray().Any(d => d!["code"]!.GetValue<string>() == "node.document"), "the preview refuses it too: " + preview["diagnostics"]!.ToJsonString());
        var untriggered = await Visual(new { operation = "save", id = "untriggered", expectedRevision = 0, graph = With(Graph("Untriggered", [N("say", "log", new { value = Lit("string", "hi") })])) });
        Check(!untriggered["ok"]!.GetValue<bool>() && Codes(untriggered).Contains("graph.noTrigger"), "an automation still needs a trigger: " + untriggered.ToJsonString());
    }

    /// <summary>
    /// A library save answers each definition it rebuilt without the value of a secret setting: a class's setting its
    /// class marks secret, a graph's setting the document marks secret. Their other settings' values are there.
    /// </summary>
    static async Task ALibrarySaveAnswersNoSecretSettingValues()
    {
        await using var rig = await Rig.Start("library-secrets");
        const string library = """
            public class SecretPlug : SharpDevice
            {
                [Setting] public string Host { get; set; } = "";
                [Setting(Secret = true)] public string Password { get; set; } = "";
                public SecretPlug() { Add(new SharpSwitch("relay") { OnTurnOn = () => Task.CompletedTask }); }
            }
            """;
        Check((await rig.Library("secret", 0, library))["ok"]!.GetValue<bool>(), "the library saves");
        await rig.Ok(new { operation = "save", id = "plug", expectedRevision = 0, definition = new
        {
            name = "Plug", settings = new object[] { new { key = "Host", value = "10.0.0.7" }, new { key = "Password", value = "class-hunter2" } },
            logic = new { kind = "class", type = "library:secret:NodeLib.SecretPlug" },
        } });
        var graph = Graph("Level", [N("k", "setting", new { key = "offset" }), State("s", "level")], Data("k", "s", "value"));
        await rig.Save("level", Definition("Level", graph, [Entity("level", "sensor")], [
            new JsonObject { ["key"] = "offset", ["type"] = "number", ["value"] = 4 },
            new JsonObject { ["key"] = "token", ["type"] = "string", ["label"] = "Token", ["value"] = "graph-hunter2", ["secret"] = true }]));
        var rebuilt = await rig.Library("secret", 1, library.Replace("\"\";\n    [Setting(Secret", "\"\"; // rebuilt\n    [Setting(Secret"));
        Check(rebuilt["ok"]!.GetValue<bool>(), "the library saves again: " + rebuilt.ToJsonString());
        var definitions = rebuilt["result"]!["definitions"]!.AsArray();
        var text = definitions.ToJsonString();
        Check(definitions.Count == 2 && !text.Contains("hunter2", StringComparison.Ordinal), "the answer has no secret value: " + text);
        JsonArray Settings(string id) => definitions.Single(d => d!["id"]!.GetValue<string>() == id)!["document"]!["settings"]!.AsArray();
        Check(Settings("plug").Select(s => s!.ToJsonString()).SequenceEqual(["""{"key":"Host","value":"10.0.0.7"}""", """{"key":"Password","value":null,"secret":true,"hasValue":true}"""]),
            "the class's secret setting has no value, the other keeps its own: " + Settings("plug").ToJsonString());
        Check(Settings("level").Select(s => s!.ToJsonString()).SequenceEqual(["""{"key":"offset","value":4,"type":"number"}""", """{"key":"token","value":null,"label":"Token","type":"string","secret":true,"hasValue":true}"""]),
            "the graph's secret setting has no value and is still marked secret, the other keeps its own: " + Settings("level").ToJsonString());
        // The stored documents keep the values.
        Check(File.ReadAllText(StoredFile(rig, "plug")).Contains("class-hunter2", StringComparison.Ordinal), "the class definition still has its value");
        Check(File.ReadAllText(StoredFile(rig, "level")).Contains("graph-hunter2", StringComparison.Ordinal), "the graph definition still has its value");
    }

    // ---- What the panel needs: source maps, masked secrets, kept secrets, a run summary ----

    /// <summary>A definition's stored file.</summary>
    static string StoredFile(Rig rig, string id) => Path.Combine(rig.Home.Root, ".storage", "hass_sharp", "entities", id + ".json");

    /// <summary>A class with a secret setting and one that is not, as a node library declares it.</summary>
    const string SecretLibrary = """
        public class SecretPlug : SharpDevice
        {
            [Setting] public string Host { get; set; } = "";
            [Setting(Secret = true)] public string Password { get; set; } = "";
            public SecretPlug() { Add(new SharpSwitch("relay") { OnTurnOn = () => Task.CompletedTask }); }
        }
        """;
    const string SecretPlug = "library:secret:NodeLib.SecretPlug";
    static object SecretPlugDefinition(params object[] settings) => new { name = "Plug", settings, logic = new { kind = "class", type = SecretPlug } };

    /// <summary>A sensor whose value is the setting <c>offset</c>, with the settings given.</summary>
    static JsonObject Level(params JsonObject[] settings) =>
        Definition("Level", Graph("Level", [N("k", "setting", new { key = "offset" }), State("s", "level")], Data("k", "s", "value")), [Entity("level", "sensor")], settings);
    static JsonObject Offset(double value) => new() { ["key"] = "offset", ["type"] = "number", ["value"] = value };
    static JsonObject Token(JsonNode? value, string type = "string") => new() { ["key"] = "token", ["type"] = type, ["label"] = "Token", ["value"] = value, ["secret"] = true };
    static JsonObject KeptToken(string type = "string") => new() { ["key"] = "token", ["type"] = type, ["label"] = "Token", ["secret"] = true, ["keep"] = true };
    static JsonNode StoredSetting(Rig rig, string id, string key) =>
        JsonNode.Parse(File.ReadAllText(StoredFile(rig, id)))!["settings"]!.AsArray().Single(s => s!["key"]!.GetValue<string>() == key)!;
    static JsonNode Answered(JsonNode document, string key) => document["settings"]!.AsArray().Single(s => s!["key"]!.GetValue<string>() == key)!;

    /// <summary>
    /// A preview answers the source map a read of the same definition, once saved, gives: a graph's ranges of its nodes and
    /// libraries, a class's of its libraries; an invalid one answers none.
    /// </summary>
    static async Task PreviewAnswersASourceMap()
    {
        await using var rig = await Rig.Start("preview-map", ("binary_sensor.motion_a", "off"), ("binary_sensor.motion_b", "off"));
        var preview = await rig.Ok(new { operation = "preview", definition = Motion() });
        Check(preview["valid"]!.GetValue<bool>() && preview.Select(p => p.Key).Order(StringComparer.Ordinal).SequenceEqual(["diagnostics", "source", "sourceMap", "valid"]),
            "the preview's members: " + preview.ToJsonString());
        Check(preview["sourceMap"]!.AsArray().Any(m => m!["nodeId"]!.GetValue<string>() == "s"), "the graph's preview maps its nodes: " + preview["sourceMap"]!.ToJsonString());
        await rig.Save("motion", Motion());
        var read = await rig.Read("motion");
        Check(JsonNode.DeepEquals(preview["sourceMap"], read["sourceMap"]), "the graph's preview maps its source as read does: " + preview["sourceMap"]!.ToJsonString() + " / " + read["sourceMap"]!.ToJsonString());

        Check((await rig.Library("secret", 0, SecretLibrary))["ok"]!.GetValue<bool>(), "the library saves");
        var plug = SecretPlugDefinition(new { key = "Host", value = "10.0.0.7" });
        var classPreview = await rig.Ok(new { operation = "preview", definition = plug });
        await rig.Ok(new { operation = "save", id = "plug", expectedRevision = 0, definition = plug });
        var classRead = await rig.Read("plug");
        Check(classPreview["valid"]!.GetValue<bool>() && classPreview["sourceMap"]!.AsArray().Count == 1 && JsonNode.DeepEquals(classPreview["sourceMap"], classRead["sourceMap"]),
            "the class's preview maps its source as read does: " + classPreview["sourceMap"]!.ToJsonString() + " / " + classRead["sourceMap"]!.ToJsonString());

        var noEntities = Motion(); noEntities["entities"] = new JsonArray();
        var invalid = await rig.Ok(new { operation = "preview", definition = noEntities });
        Check(!invalid["valid"]!.GetValue<bool>() && invalid["sourceMap"] is JsonArray { Count: 0 } && invalid["source"]!.GetValue<string>() == "", "an invalid preview maps nothing: " + invalid.ToJsonString());
    }

    /// <summary>
    /// Save, read and list answer a secret setting without its value, marked secret and saying whether it has one: a
    /// class's that its class marks secret, a graph's that the document marks; every setting of a class definition whose
    /// class the libraries no longer declare. The other settings keep their values, and the stored files keep the secrets.
    /// </summary>
    static async Task SecretSettingsAreMaskedInEveryAnswer()
    {
        await using var rig = await Rig.Start("masked-secrets");
        Check((await rig.Library("secret", 0, SecretLibrary))["ok"]!.GetValue<bool>(), "the library saves");
        var plugSaved = await rig.Ok(new { operation = "save", id = "plug", expectedRevision = 0,
            definition = SecretPlugDefinition(new { key = "Host", value = "10.0.0.7" }, new { key = "Password", value = "class-hunter2" }) });
        var levelSaved = await rig.Save("level", Level(Offset(4), Token("graph-hunter2"), new JsonObject { ["key"] = "pin", ["type"] = "string", ["secret"] = true, ["value"] = null }));
        var plugSettings = new[] { """{"key":"Host","value":"10.0.0.7"}""", """{"key":"Password","value":null,"secret":true,"hasValue":true}""" };
        var levelSettings = new[] { """{"key":"offset","value":4,"type":"number"}""", """{"key":"token","value":null,"label":"Token","type":"string","secret":true,"hasValue":true}""",
            """{"key":"pin","value":null,"type":"string","secret":true,"hasValue":false}""" };
        static bool Shaped(JsonNode document, string[] settings) => document["settings"]!.AsArray().Select(s => s!.ToJsonString()).SequenceEqual(settings);
        Check(Shaped(plugSaved["document"]!, plugSettings), "the class's save answer: " + plugSaved["document"]!["settings"]!.ToJsonString());
        Check(Shaped(levelSaved["document"]!, levelSettings), "the graph's save answer: " + levelSaved["document"]!["settings"]!.ToJsonString());
        var plugRead = await rig.Read("plug");
        var levelRead = await rig.Read("level");
        Check(Shaped(plugRead, plugSettings) && Shaped(levelRead, levelSettings), "the reads: " + plugRead["settings"]!.ToJsonString() + " / " + levelRead["settings"]!.ToJsonString());
        var listed = (await rig.Request(new { operation = "list" }))["result"]!.AsArray();
        Check(Shaped(listed.Single(d => d!["id"]!.GetValue<string>() == "plug")!, plugSettings) && Shaped(listed.Single(d => d!["id"]!.GetValue<string>() == "level")!, levelSettings),
            "the list: " + listed.ToJsonString());
        var answers = plugSaved.ToJsonString() + levelSaved.ToJsonString() + plugRead.ToJsonString() + levelRead.ToJsonString() + listed.ToJsonString();
        Check(!answers.Contains("hunter2", StringComparison.Ordinal), "an answer has a secret: " + answers);
        Check(File.ReadAllText(StoredFile(rig, "plug")).Contains("class-hunter2", StringComparison.Ordinal) && File.ReadAllText(StoredFile(rig, "level")).Contains("graph-hunter2", StringComparison.Ordinal),
            "the stored files keep the secrets");
        Check(!File.ReadAllText(StoredFile(rig, "plug")).Contains("hasValue", StringComparison.Ordinal) && !File.ReadAllText(StoredFile(rig, "level")).Contains("hasValue", StringComparison.Ordinal),
            "a stored file says hasValue");
        Check(!rig.Home.Logs().Any(l => l.Contains("hunter2", StringComparison.Ordinal)) && !rig.Home.Syncs("plug").Concat(rig.Home.Syncs("level")).Any(s => s.Contains("hunter2", StringComparison.Ordinal)),
            "a log or a sync has a secret");

        // A class definition whose class the libraries no longer declare: which of its settings are secret is not known, so each is masked.
        var gone = JsonNode.Parse(File.ReadAllText(StoredFile(rig, "plug")))!.AsObject();
        gone["id"] = "gone"; gone["logic"]!["type"] = "library:secret:NodeLib.GonePlug"; gone["enabled"] = false;
        File.WriteAllText(StoredFile(rig, "gone"), gone.ToJsonString());
        var goneRead = await rig.Read("gone");
        var goneListed = (await rig.Request(new { operation = "list" }))["result"]!.AsArray().Single(d => d!["id"]!.GetValue<string>() == "gone")!;
        var goneSettings = new[] { """{"key":"Host","value":null,"secret":true,"hasValue":true}""", """{"key":"Password","value":null,"secret":true,"hasValue":true}""" };
        Check(Shaped(goneRead, goneSettings) && Shaped(goneListed, goneSettings), "a class not declared masks every setting: " + goneRead["settings"]!.ToJsonString() + " / " + goneListed["settings"]!.ToJsonString());
    }

    /// <summary>
    /// A save may keep a setting's stored value without being given it: <c>keep</c> stores the value saved before, a new
    /// value replaces it, a null value without <c>keep</c> removes it, and <c>keep</c> with nothing stored (a new key, a new
    /// definition) stores none. A kept value that no longer fits a changed type is refused without being quoted; <c>keep</c>
    /// with a value is a bad request. Neither <c>keep</c> nor <c>hasValue</c> is stored.
    /// </summary>
    static async Task ASaveKeepsASecretItWasNotGiven()
    {
        await using var rig = await Rig.Start("kept-secrets");
        await rig.Save("level", Level(Offset(4), Token("graph-hunter2")));
        var kept = await rig.Save("level", Level(Offset(5), KeptToken()), 1);
        Check(StoredSetting(rig, "level", "token").ToJsonString() == """{"key":"token","value":"graph-hunter2","label":"Token","type":"string","secret":true}"""
            && StoredSetting(rig, "level", "offset")["value"]!.GetValue<double>() == 5, "keep stores the value saved before: " + File.ReadAllText(StoredFile(rig, "level")));
        Check(Answered(kept["document"]!, "token")["hasValue"]!.GetValue<bool>() && !kept.ToJsonString().Contains("hunter2", StringComparison.Ordinal), "the kept secret is answered masked: " + kept.ToJsonString());
        // A client may send back what it was answered, hasValue and all: hasValue is not stored either.
        var answeredBack = KeptToken(); answeredBack["hasValue"] = true; answeredBack["value"] = null;
        await rig.Save("level", Level(Offset(5), answeredBack), 2);
        var file = File.ReadAllText(StoredFile(rig, "level"));
        Check(StoredSetting(rig, "level", "token")["value"]!.GetValue<string>() == "graph-hunter2" && !file.Contains("keep", StringComparison.Ordinal) && !file.Contains("hasValue", StringComparison.Ordinal),
            "neither keep nor hasValue is stored: " + file);

        await rig.Save("level", Level(Offset(5), Token("new-secret")), 3);
        Check(StoredSetting(rig, "level", "token")["value"]!.GetValue<string>() == "new-secret", "a new value replaces the stored one: " + File.ReadAllText(StoredFile(rig, "level")));
        var removed = await rig.Save("level", Level(Offset(5), Token(null)), 4);
        Check(StoredSetting(rig, "level", "token")["value"] == null && !Answered(removed["document"]!, "token")["hasValue"]!.GetValue<bool>(), "a null value without keep removes it: " + removed.ToJsonString());
        var fresh = new JsonObject { ["key"] = "fresh", ["type"] = "string", ["secret"] = true, ["keep"] = true };
        var keptNothing = await rig.Save("level", Level(Offset(5), Token("again"), fresh), 5);
        Check(StoredSetting(rig, "level", "fresh").ToJsonString() == """{"key":"fresh","value":null,"type":"string","secret":true}"""
            && !Answered(keptNothing["document"]!, "fresh")["hasValue"]!.GetValue<bool>(), "keep for a key with nothing stored stores no value: " + File.ReadAllText(StoredFile(rig, "level")));
        var created = await rig.Save("other", Level(Offset(1), KeptToken()));
        Check(StoredSetting(rig, "other", "token")["value"] == null && !Answered(created["document"]!, "token")["hasValue"]!.GetValue<bool>(), "keep in a new definition stores no value");

        // A kept value that does not fit the setting's new type is that setting's type problem; the value is not quoted.
        var retyped = await rig.Request(new { operation = "save", id = "level", expectedRevision = 6, definition = Level(Offset(5), KeptToken("number"), fresh) });
        var problem = retyped["error"]?["detail"]?["diagnostics"]?.AsArray().SingleOrDefault(d => d!["inputId"]?.GetValue<string>() == "token");
        Check(retyped["error"]?["code"]?.GetValue<string>() == "validation" && problem?["code"]?.GetValue<string>() == "definition.settingType"
            && !retyped.ToJsonString().Contains("again", StringComparison.Ordinal), "a kept value of the wrong type: " + retyped.ToJsonString());
        // keep with a value is a bad request, naming the setting; nothing is written.
        var both = Token("graph-hunter2"); both["keep"] = true;
        var bad = await rig.Request(new { operation = "save", id = "level", expectedRevision = 6, definition = Level(Offset(5), both) });
        Check(bad["error"]?["code"]?.GetValue<string>() == "invalid_request" && bad["error"]!["detail"]!.GetValue<string>().Contains("token", StringComparison.Ordinal)
            && !bad.ToJsonString().Contains("hunter2", StringComparison.Ordinal), "keep with a value: " + bad.ToJsonString());
        Check((await rig.Read("level"))["revision"]!.GetValue<long>() == 6 && StoredSetting(rig, "level", "token")["value"]!.GetValue<string>() == "again", "a refused save wrote");

        // A class definition keeps its secret the same way; a setting that is not secret is kept as well.
        Check((await rig.Library("secret", 0, SecretLibrary))["ok"]!.GetValue<bool>(), "the library saves");
        await rig.Ok(new { operation = "save", id = "plug", expectedRevision = 0, definition = SecretPlugDefinition(new { key = "Host", value = "10.0.0.7" }, new { key = "Password", value = "class-hunter2" }) });
        var plug = await rig.Ok(new { operation = "save", id = "plug", expectedRevision = 1, definition = SecretPlugDefinition(new { key = "Host", keep = true }, new { key = "Password", keep = true }) });
        Check(File.ReadAllText(StoredFile(rig, "plug")).Contains("""{"key":"Host","value":"10.0.0.7"},{"key":"Password","value":"class-hunter2"}""", StringComparison.Ordinal)
            && Answered(plug["document"]!, "Password")["hasValue"]!.GetValue<bool>() && Answered(plug["document"]!, "Host")["value"]!.GetValue<string>() == "10.0.0.7",
            "a class definition keeps its values: " + File.ReadAllText(StoredFile(rig, "plug")));
    }

    /// <summary>
    /// A preview does not read the stored definition, so a kept setting's value is not checked; everything else is. It
    /// generates the source a definition with that value would: no value reaches the source.
    /// </summary>
    static async Task ThePreviewAcceptsAKeptSecret()
    {
        await using var rig = await Rig.Start("preview-kept");
        var kept = await rig.Ok(new { operation = "preview", definition = Level(Offset(4), KeptToken("number")) });
        var given = await rig.Ok(new { operation = "preview", definition = Level(Offset(4), Token(7, "number")) });
        Check(kept["valid"]!.GetValue<bool>() && kept["source"]!.GetValue<string>() == given["source"]!.GetValue<string>(), "a kept secret previews as one with a value: " + kept.ToJsonString());
        var badType = KeptToken("colour");
        var refused = await rig.Ok(new { operation = "preview", definition = Level(Offset(4), badType) });
        Check(!refused["valid"]!.GetValue<bool>() && refused["diagnostics"]!.AsArray().Any(d => d!["code"]!.GetValue<string>() == "definition.settingType" && d["inputId"]!.GetValue<string>() == "token"),
            "the rest of a kept setting is checked: " + refused.ToJsonString());
        var both = Token("graph-hunter2"); both["keep"] = true;
        var bad = await rig.Request(new { operation = "preview", definition = Level(Offset(4), both) });
        Check(bad["error"]?["code"]?.GetValue<string>() == "invalid_request" && !bad.ToJsonString().Contains("hunter2", StringComparison.Ordinal), "a preview of keep with a value: " + bad.ToJsonString());

        Check((await rig.Library("secret", 0, SecretLibrary))["ok"]!.GetValue<bool>(), "the library saves");
        var classKept = await rig.Ok(new { operation = "preview", definition = SecretPlugDefinition(new { key = "Password", keep = true }) });
        Check(classKept["valid"]!.GetValue<bool>(), "a class's kept secret previews: " + classKept.ToJsonString());
        var classUnknown = await rig.Ok(new { operation = "preview", definition = SecretPlugDefinition(new { key = "Pasword", keep = true }) });
        Check(!classUnknown["valid"]!.GetValue<bool>() && classUnknown["diagnostics"]!.AsArray().Any(d => d!["code"]!.GetValue<string>() == "definition.unknownSetting"),
            "a kept setting the class has not is still unknown: " + classUnknown.ToJsonString());
    }

    /// <summary>A secret whose value does not fit its type is reported by its name and type, never with its value: in a preview, a refused save and the log.</summary>
    static async Task NoProblemQuotesASecret()
    {
        await using var rig = await Rig.Start("unquoted-secrets");
        var graphPreview = await rig.Ok(new { operation = "preview", definition = Level(Offset(4), Token("graph-hunter2", "number")) });
        var graphSave = await rig.Request(new { operation = "save", id = "level", expectedRevision = 0, definition = Level(Offset(4), Token("graph-hunter2", "number")) });
        Check(!graphPreview["valid"]!.GetValue<bool>() && graphPreview["diagnostics"]!.AsArray().Any(d => d!["code"]!.GetValue<string>() == "definition.settingType" && d["message"]!.GetValue<string>() == "Token takes a number")
            && graphSave["error"]?["code"]?.GetValue<string>() == "validation", "a graph secret of the wrong type: " + graphPreview.ToJsonString() + " / " + graphSave.ToJsonString());
        Check((await rig.Library("secret", 0, SecretLibrary))["ok"]!.GetValue<bool>(), "the library saves");
        var plug = SecretPlugDefinition(new { key = "Password", value = new[] { "class-hunter2" } });
        var classPreview = await rig.Ok(new { operation = "preview", definition = plug });
        var classSave = await rig.Request(new { operation = "save", id = "plug", expectedRevision = 0, definition = plug });
        Check(!classPreview["valid"]!.GetValue<bool>() && classPreview["diagnostics"]!.AsArray().Any(d => d!["code"]!.GetValue<string>() == "definition.settingType")
            && classSave["error"]?["code"]?.GetValue<string>() == "validation", "a class secret of the wrong type: " + classPreview.ToJsonString() + " / " + classSave.ToJsonString());
        var texts = graphPreview.ToJsonString() + graphSave.ToJsonString() + classPreview.ToJsonString() + classSave.ToJsonString() + string.Join("\n", rig.Home.Logs());
        Check(!texts.Contains("hunter2", StringComparison.Ordinal), "a problem quotes a secret: " + texts);
    }

    /// <summary>
    /// The home list answers each definition as the list does, with how it has been running, as the automations page's
    /// list does: its newest runs, newest last, how many ran today, how many failed in the last day and when it last ran.
    /// </summary>
    static async Task TheHomeListSummarisesRuns()
    {
        await using var rig = await Rig.Start("home-list", ("binary_sensor.motion_a", "off"), ("binary_sensor.motion_b", "off"));
        await rig.Save("relay", Relay(before: [Read("read", "sensor.gone")]));
        await rig.Save("motion", Motion());
        await rig.Until("relay:relay", _ => true, "the switch starts");
        await rig.Api.EntityCommand("relay:relay", "turn_off", null);
        await Throws<InvalidOperationException>(() => rig.Api.EntityCommand("relay:relay", "turn_on", null), "a failing flow");
        var today = DateTime.UtcNow.Date.ToString("O");
        var home = (await rig.Request(new { operation = "list", view = "home", todayUtc = today }))["result"]!.AsArray();
        Check(home.Select(d => d!["id"]!.GetValue<string>()).SequenceEqual(["motion", "relay"]), "the home list: " + home.ToJsonString());
        var relay = home.Single(d => d!["id"]!.GetValue<string>() == "relay")!.AsObject();
        var runs = relay["recentRuns"]!.AsArray();
        Check(runs.Count == 2 && runs[0]!["status"]!.GetValue<string>() == "Succeeded" && runs[1]!["status"]!.GetValue<string>() == "Failed"
            && runs[1]!.ToJsonString().StartsWith("{\"runId\":", StringComparison.Ordinal) && runs[1]!.AsObject().Select(p => p.Key).SequenceEqual(["runId", "status", "startUtc", "durationMs"]),
            "the relay's runs, newest last: " + runs.ToJsonString());
        Check(relay["runsToday"]!.GetValue<int>() == 2 && relay["failedLastDay"]!.GetValue<int>() == 1 && relay["lastRunUtc"] != null, "the relay's counts: " + relay.ToJsonString());
        Check(!relay.ContainsKey("source") && !relay.ContainsKey("sourceMap") && !relay.ContainsKey("outline") && !relay.ContainsKey("problemCount")
            && relay["status"]!.GetValue<string>() == "running" && relay.ContainsKey("liveEntities") && relay.ContainsKey("automationId"), "the relay's entry is the list's: " + relay.ToJsonString());
        var motion = home.Single(d => d!["id"]!.GetValue<string>() == "motion")!.AsObject();
        Check(motion["recentRuns"] is JsonArray { Count: 0 } && motion["runsToday"]!.GetValue<int>() == 0 && motion["failedLastDay"]!.GetValue<int>() == 0
            && motion.ContainsKey("lastRunUtc") && motion["lastRunUtc"] == null, "a definition without runs: " + motion.ToJsonString());

        var plain = (await rig.Request(new { operation = "list" }))["result"]!.AsArray();
        Check(plain.All(d => !d!.AsObject().ContainsKey("recentRuns") && !d.AsObject().ContainsKey("runsToday")), "the plain list has no run summary: " + plain.ToJsonString());
        Check((await rig.Request(new { operation = "list", view = "home" }))["ok"]!.GetValue<bool>(), "todayUtc may be left out");
        var unknown = await rig.Request(new { operation = "list", view = "garden" });
        Check(unknown["error"]?["code"]?.GetValue<string>() == "invalid_request", "an unknown view: " + unknown.ToJsonString());
        var badDay = await rig.Request(new { operation = "list", view = "home", todayUtc = "yesterday" });
        Check(badDay["error"]?["code"]?.GetValue<string>() == "invalid_request", "a todayUtc that is not a time: " + badDay.ToJsonString());
    }

    // ---- A new revision carries the states its flows set over ----

    /// <summary>The definition with a field of its first entity changed, which makes a new revision of it.</summary>
    static JsonObject Touched(JsonObject definition, string icon = "mdi:power")
    {
        var touched = definition.DeepClone().AsObject();
        touched["entities"]![0]!["icon"] = icon;
        return touched;
    }

    /// <summary>
    /// A switch a command turned on stays on through a save of a new revision: every push after the save is on, never
    /// unknown; and its commands reach the new revision.
    /// </summary>
    static async Task ASwitchKeepsItsStateThroughANewRevision()
    {
        await using var rig = await Rig.Start("carry");
        await rig.Save("relay", Relay());
        await rig.Until("relay:relay", s => Available(s) && Is(s, "is_on", null), "the switch starts unknown");
        await rig.Api.EntityCommand("relay:relay", "turn_on", null);
        await rig.Until("relay:relay", s => Is(s, "is_on", true), "the switch is on");
        var batches = rig.Batches;
        var saved = await rig.Save("relay", Touched(Relay()), 1);
        Check(saved["active"]!.GetValue<bool>() && rig.Home.Syncs("relay").Length == 2, "the new revision runs: " + saved.ToJsonString());
        await rig.Started("relay");
        var after = rig.Pushed("relay:relay", batches);
        Check(after.Length > 0 && after.All(s => Available(s) && Is(s, "is_on", true)), "every push after the save is on: " + Lines(after.Select(s => s.ToJsonString())));
        await rig.Api.EntityCommand("relay:relay", "turn_off", null);
        await rig.Until("relay:relay", s => Is(s, "is_on", false), "the new revision takes the command", batches);
    }

    /// <summary>A library save that rebuilds a definition keeps the state its switch's command flow set.</summary>
    static async Task ALibraryRebuildKeepsASwitchsState()
    {
        await using var rig = await Rig.Start("carry-library");
        await rig.Library("maths", 0, EntityStoreTests.Maths);
        var half = await rig.LibraryNode("Half");
        var definition = Relay();
        var graph = definition["logic"]!["graph"]!.AsObject();
        graph["nodes"]!.AsArray().Add(N("k", "setting", new { key = "offset" }));
        graph["nodes"]!.AsArray().Add(N("h", half, new { arguments = new { } }));
        graph["nodes"]!.AsArray().Add(State("s", "level"));
        graph["edges"]!.AsArray().Add(Data("k", "h", "value"));
        graph["edges"]!.AsArray().Add(Data("h", "s", "value", "result"));
        definition["entities"]!.AsArray().Add(Entity("level", "sensor"));
        definition["settings"] = new JsonArray(new JsonObject { ["key"] = "offset", ["type"] = "number", ["value"] = 4 });
        await rig.Save("relay", definition);
        await rig.Until("relay:level", s => Is(s, "value", 2.0), "half the setting");
        await rig.Api.EntityCommand("relay:relay", "turn_on", null);
        await rig.Until("relay:relay", s => Is(s, "is_on", true), "the switch is on");
        var batches = rig.Batches;
        var rebuilt = await rig.Library("maths", 1, EntityStoreTests.Maths.Replace("value / 2", "value / 4"));
        Check(rebuilt["result"]?["definitions"]?.AsArray().SingleOrDefault()?["active"]?.GetValue<bool>() == true, "the rebuilt definition runs: " + rebuilt.ToJsonString());
        await rig.Until("relay:level", s => Is(s, "value", 1.0), "the new code works out the level", batches);
        await rig.Started("relay");
        var after = rig.Pushed("relay:relay", batches);
        Check(after.Length > 0 && after.All(s => Available(s) && Is(s, "is_on", true)), "every push after the rebuild is on: " + Lines(after.Select(s => s.ToJsonString())));
    }

    /// <summary>The state a new revision's When started flow sets is its first, not the state carried over.</summary>
    static async Task AWhenStartedFlowOverridesTheStateCarriedOver()
    {
        await using var rig = await Rig.Start("carry-setup");
        var definition = Relay();
        var graph = definition["logic"]!["graph"]!.AsObject();
        graph["nodes"]!.AsArray().Add(N("start", "entity_setup"));
        graph["nodes"]!.AsArray().Add(SetState("first", "relay", On(false)));
        graph["edges"]!.AsArray().Add(Next("start", "first"));
        await rig.Save("relay", definition);
        await rig.Until("relay:relay", s => Is(s, "is_on", false), "the When started flow turns it off");
        await rig.Api.EntityCommand("relay:relay", "turn_on", null);
        await rig.Until("relay:relay", s => Is(s, "is_on", true), "the switch is on");
        var batches = rig.Batches;
        await rig.Save("relay", Touched(definition), 1);
        await rig.Started("relay");
        var after = rig.Pushed("relay:relay", batches);
        Check(after.Length > 0 && after.All(s => Available(s) && Is(s, "is_on", false)), "the new revision starts with the state its flow set: " + Lines(after.Select(s => s.ToJsonString())));
    }

    /// <summary>An entity whose key the new revision gives to another domain starts unknown, though the two take the same state.</summary>
    static async Task AnEntityOfAnotherDomainUnderTheSameKeyStartsUnknown()
    {
        await using var rig = await Rig.Start("carry-domain");
        await rig.Save("relay", Relay());
        await rig.Api.EntityCommand("relay:relay", "turn_on", null);
        await rig.Until("relay:relay", s => Is(s, "is_on", true), "the switch is on");
        var batches = rig.Batches;
        var saved = await rig.Save("relay", Definition("Relay", Graph("Relay", []), [Entity("relay", "binary_sensor", new { name = "Relay" })]), 1);
        Check(saved["active"]!.GetValue<bool>(), "the new revision runs: " + saved.ToJsonString());
        await rig.Started("relay");
        var after = rig.Pushed("relay:relay", batches);
        Check(after.Length > 0 && after.All(s => Available(s) && Is(s, "is_on", null)), "the binary sensor starts unknown: " + Lines(after.Select(s => s.ToJsonString())));
    }

    /// <summary>
    /// A sensor's value a trigger flow set is carried over by a new revision that takes it, and not by one that refuses
    /// it (its options changed): that one starts unknown.
    /// </summary>
    static async Task AValueTheNewRevisionRefusesIsNotCarriedOver()
    {
        await using var rig = await Rig.Start("carry-refused", ("sensor.button", "idle"));
        var graph = Graph("Button", [N("t", "trigger", new { type = "entity", entity = "sensor.button" }), SetState("set", "last", new JsonObject { ["value"] = Lit("string", "pressed") })], Next("t", "set"));
        JsonObject Button(object? options = null) => Definition("Button", graph, [Entity("last", "sensor", options == null ? new { name = "Last" } : new { name = "Last", options })]);
        await rig.Save("button", Button());
        await rig.Change("sensor.button", "pressed");
        await rig.Until("button:last", s => Is(s, "value", "pressed"), "the trigger flow set the value");
        var batches = rig.Batches;
        await rig.Save("button", Touched(Button()), 1);
        await rig.Started("button");
        var kept = rig.Pushed("button:last", batches);
        Check(kept.Length > 0 && kept.All(s => Available(s) && Is(s, "value", "pressed")), "a revision that takes the value keeps it: " + Lines(kept.Select(s => s.ToJsonString())));
        batches = rig.Batches;
        await rig.Save("button", Button(new { options = new[] { "low", "high" } }), 2);
        await rig.Started("button");
        var refused = rig.Pushed("button:last", batches);
        Check(refused.Length > 0 && refused.All(s => Available(s) && Is(s, "value", null)), "a revision whose sensor refuses the value starts unknown: " + Lines(refused.Select(s => s.ToJsonString())));
    }

    /// <summary>An entity a flow made unavailable stays unavailable through a new revision, with its state.</summary>
    static async Task AnUnavailableEntityStaysUnavailableThroughANewRevision()
    {
        await using var rig = await Rig.Start("carry-unavailable");
        var definition = Relay(turnOff: false);
        var graph = definition["logic"]!["graph"]!.AsObject();
        graph["nodes"]!.AsArray().Add(Command("off", "relay", "turn_off"));
        graph["nodes"]!.AsArray().Add(N("gone", "set_entity_available", new JsonObject { ["entity"] = "relay", ["available"] = Lit("boolean", false) }));
        graph["edges"]!.AsArray().Add(Next("off", "gone"));
        await rig.Save("relay", definition);
        await rig.Api.EntityCommand("relay:relay", "turn_on", null);
        await rig.Api.EntityCommand("relay:relay", "turn_off", null);
        await rig.Until("relay:relay", s => !Available(s) && Is(s, "is_on", true), "the switch is on and unavailable");
        var batches = rig.Batches;
        await rig.Save("relay", Touched(definition), 1);
        await rig.Started("relay");
        var after = rig.Pushed("relay:relay", batches);
        Check(after.Length > 0 && after.All(s => !Available(s) && Is(s, "is_on", true)), "the new revision keeps it unavailable and on: " + Lines(after.Select(s => s.ToJsonString())));
    }

    // ---- How a definition's flows end ----

    /// <summary>A command whose flow ends at a Stop step has succeeded: the state the flow set before it is pushed.</summary>
    static async Task ACommandFlowThatEndsAtAStopStepSucceeds()
    {
        await using var rig = await Rig.Start("stop-command");
        var graph = Graph("Relay", [Command("on", "relay", "turn_on"), SetState("set_on", "relay", On(true)), N("halt", "stop")], Next("on", "set_on"), Next("set_on", "halt"));
        var saved = await rig.Save("relay", Definition("Relay", graph, [Entity("relay", "switch", new { name = "Relay" })]));
        await rig.Until("relay:relay", _ => true, "the switch starts");
        await rig.Api.EntityCommand("relay:relay", "turn_on", null);
        await rig.Until("relay:relay", s => Is(s, "is_on", true), "the state set before the Stop step");
        var runs = rig.History(saved["document"]!["automationId"]!.GetValue<string>());
        Check(runs.Any(r => r["cause"]!.GetValue<string>() == "Command" && r["status"]!.GetValue<string>() == "Stopped"), "the run is recorded as stopped: " + Lines(runs.Select(r => r.ToJsonString())));
        Check(rig.Home.Logs(PyLogLevel.Warn).Length == 0 && rig.Home.Logs(PyLogLevel.Error).Length == 0, "a flow that ended at a Stop step was logged: " + Lines(rig.Home.Logs()));
    }

    /// <summary>
    /// A When started flow that fails, and a trigger's flow in a definition that fails, each log one error naming the
    /// definition and the flow, with no stack, and the whole failure at Debug; the runner's line for an automation is
    /// not written.
    /// </summary>
    static async Task FailingSetupAndTriggerFlowsAreLoggedByName()
    {
        await using var rig = await Rig.Start("flow-failures", ("sensor.button", "idle"));
        var setup = Graph("Level", [N("start", "entity_setup"), Read("read", "sensor.gone")], Next("start", "read"));
        var failing = await rig.Save("level", Definition("Level", setup, [Entity("level", "sensor")]));
        Check(failing["activationError"]?.GetValue<string>() == "Entity 'sensor.gone' is unavailable.", "the save answers the flow's error: " + failing.ToJsonString());
        var setupErrors = rig.Home.Logs(PyLogLevel.Error).Where(l => l.StartsWith("Level: ", StringComparison.Ordinal)).ToArray();
        Check(setupErrors.Length == 1 && setupErrors[0].StartsWith("Level: When started failed: ", StringComparison.Ordinal) && setupErrors[0].Contains("Entity 'sensor.gone' is unavailable.", StringComparison.Ordinal)
            && !setupErrors[0].Contains("   at ", StringComparison.Ordinal), "one error names the definition and its When started flow: " + Lines(rig.Home.Logs(PyLogLevel.Error)));
        Check(rig.Home.Logs(PyLogLevel.Debug).Any(l => l.StartsWith("Level: When started failed: ", StringComparison.Ordinal) && l.Contains("   at ", StringComparison.Ordinal)), "the whole failure at Debug: " + Lines(rig.Home.Logs(PyLogLevel.Debug)));

        var trigger = N("t", "trigger", new { type = "entity", entity = "sensor.button" });
        trigger["label"] = "Button pressed";
        var unlabelled = N("u", "trigger", new { type = "entity", entity = "sensor.button" });
        var triggers = Graph("Button", [trigger, Read("read", "sensor.gone"), unlabelled, Read("again", "sensor.gone")], Next("t", "read"), Next("u", "again"));
        await rig.Save("button", Definition("Button", triggers, [Entity("last", "sensor")]));
        await rig.Change("sensor.button", "pressed");
        var triggerErrors = rig.Home.Logs(PyLogLevel.Error).Where(l => l.StartsWith("Button: ", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        Check(triggerErrors.Length == 2 && triggerErrors[0].StartsWith("Button: Button pressed failed: ", StringComparison.Ordinal) && triggerErrors[1].StartsWith("Button: Trigger (u) failed: ", StringComparison.Ordinal)
            && triggerErrors.All(l => l.Contains("Entity 'sensor.gone' is unavailable.", StringComparison.Ordinal) && !l.Contains("   at ", StringComparison.Ordinal)),
            "one error for each trigger flow names the definition and the trigger: " + Lines(rig.Home.Logs(PyLogLevel.Error)));
        Check(!rig.Home.Logs().Any(l => l.StartsWith("Invocation ", StringComparison.Ordinal)), "the runner logged a definition's failure as an automation's: " + Lines(rig.Home.Logs()));
    }

    /// <summary>An automation's failing run is logged by the runner as before: one error with the run id, the method and the whole failure.</summary>
    static async Task AnAutomationsFailureIsLoggedAsBefore()
    {
        await using var rig = await Rig.Start("automation-failure", ("sensor.button", "idle"));
        var graph = Graph("Failing", [N("t", "trigger", new { type = "entity", entity = "sensor.button" }), Read("read", "sensor.gone")], Next("t", "read"));
        var saved = JsonNode.Parse(await rig.Api.VisualRequest(JsonSerializer.Serialize(new { operation = "save", id = "failing", expectedRevision = 0, graph })))!;
        Check(saved["ok"]!.GetValue<bool>(), "the automation saves: " + saved.ToJsonString());
        await rig.Change("sensor.button", "pressed");
        var errors = rig.Home.Logs(PyLogLevel.Error);
        Check(errors.Length == 1 && System.Text.RegularExpressions.Regex.IsMatch(errors[0], @"^Invocation [0-9a-f]{32} \(VisualAutomation::Run_[0-9]+\) failed: System\.InvalidOperationException: Entity 'sensor\.gone' is unavailable\.")
            && errors[0].Contains("   at ", StringComparison.Ordinal), "the automation's failure line: " + Lines(errors));
    }
}
