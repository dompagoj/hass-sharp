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
            (nameof(ASwitchNodeRunsItsCommandFlows), ASwitchNodeRunsItsCommandFlows),
            (nameof(ACommandPinWithoutStepsFails), ACommandPinWithoutStepsFails),
            (nameof(ALightNodeTurnedOnWithoutABrightnessIsAtFull), ALightNodeTurnedOnWithoutABrightnessIsAtFull),
            (nameof(ACommandThatDoesNotFinishTimesOut), ACommandThatDoesNotFinishTimesOut),
            (nameof(ACommandWhoseFlowIsCancelledSaysWhy), ACommandWhoseFlowIsCancelledSaysWhy),
            (nameof(SetupRunsBeforeTheSyncAndItsFailureFailsTheSave), SetupRunsBeforeTheSyncAndItsFailureFailsTheSave),
            (nameof(ASetupThatDoesNotFinishFailsTheSave), ASetupThatDoesNotFinishFailsTheSave),
            (nameof(ADeviceNodeGroupsItsEntities), ADeviceNodeGroupsItsEntities),
            (nameof(TwoDevicesAndAStandaloneEntityReachHomeAssistant), TwoDevicesAndAStandaloneEntityReachHomeAssistant),
            (nameof(TheDevicesAreListedAfterTheDeviceTheyArePartOf), TheDevicesAreListedAfterTheDeviceTheyArePartOf),
            (nameof(AClassRequestsDeviceIsStoredWithoutWhatOnlyADeviceNodeHasAndAGraphsIsRefused), AClassRequestsDeviceIsStoredWithoutWhatOnlyADeviceNodeHasAndAGraphsIsRefused),
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
            (nameof(AnEntityNodeCreatesItsEntity), AnEntityNodeCreatesItsEntity),
            (nameof(APreviewWithoutAnyEntityAsksForOne), APreviewWithoutAnyEntityAsksForOne),
            (nameof(AGraphWithOnlyACSharpNodeIsADefinition), AGraphWithOnlyACSharpNodeIsADefinition),
            (nameof(AnUnknownSourceIsWarnedOfInPreviewAndSave), AnUnknownSourceIsWarnedOfInPreviewAndSave),
            (nameof(AnInvalidGraphDefinitionReadsLikeAnAutomationAndIsNotRetried), AnInvalidGraphDefinitionReadsLikeAnAutomationAndIsNotRetried),
            (nameof(AnAutomationSentWithADefinitionStaysAnAutomation), AnAutomationSentWithADefinitionStaysAnAutomation),
            (nameof(ALibrarySaveAnswersNoSecretSettingValues), ALibrarySaveAnswersNoSecretSettingValues),
            (nameof(PreviewAnswersASourceMap), PreviewAnswersASourceMap),
            (nameof(SecretSettingsAreMaskedInEveryAnswer), SecretSettingsAreMaskedInEveryAnswer),
            (nameof(ASaveKeepsASecretItWasNotGiven), ASaveKeepsASecretItWasNotGiven),
            (nameof(AnAnswerSavedBackKeepsItsSecrets), AnAnswerSavedBackKeepsItsSecrets),
            (nameof(AnOlderClassDocumentStillLoadsAndRuns), AnOlderClassDocumentStillLoadsAndRuns),
            (nameof(ThePreviewAcceptsAKeptSecret), ThePreviewAcceptsAKeptSecret),
            (nameof(NoProblemQuotesASecret), NoProblemQuotesASecret),
            (nameof(TheHomeListSummarisesRuns), TheHomeListSummarisesRuns),
            (nameof(ASwitchKeepsItsStateThroughANewRevision), ASwitchKeepsItsStateThroughANewRevision),
            (nameof(ALibraryRebuildKeepsASwitchsState), ALibraryRebuildKeepsASwitchsState),
            (nameof(AWhenStartedFlowOverridesTheStateCarriedOver), AWhenStartedFlowOverridesTheStateCarriedOver),
            (nameof(AnEntityOfAnotherDomainUnderTheSameKeyStartsUnknown), AnEntityOfAnotherDomainUnderTheSameKeyStartsUnknown),
            (nameof(AValueTheNewRevisionRefusesIsNotCarriedOver), AValueTheNewRevisionRefusesIsNotCarriedOver),
            (nameof(AnUnavailableEntityStaysUnavailableThroughANewRevision), AnUnavailableEntityStaysUnavailableThroughANewRevision),
            (nameof(AValueSetWhileTheNewRevisionPreparesReachesIt), AValueSetWhileTheNewRevisionPreparesReachesIt),
            (nameof(AValueTheNewRevisionSetsItselfWinsOverOneSetMeanwhile), AValueTheNewRevisionSetsItselfWinsOverOneSetMeanwhile),
            (nameof(AnAvailabilityAStateWorkedOutIsNotCarriedOver), AnAvailabilityAStateWorkedOutIsNotCarriedOver),
            (nameof(ALightThatCanNoLongerBeDimmedLosesItsBrightness), ALightThatCanNoLongerBeDimmedLosesItsBrightness),
            (nameof(ASelectWhoseOptionWasRemovedStartsUnknown), ASelectWhoseOptionWasRemovedStartsUnknown),
            (nameof(AGraphSwitchComesBackOn), AGraphSwitchComesBackOn),
            (nameof(ANumberSurvivesARestart), ANumberSurvivesARestart),
            (nameof(ALightComesBackWithItsBrightness), ALightComesBackWithItsBrightness),
            (nameof(WhenStartedOverridesARestoredState), WhenStartedOverridesARestoredState),
            (nameof(ARefusedStoredValueIsDropped), ARefusedStoredValueIsDropped),
            (nameof(AStateStepsEntityIsNotRestored), AStateStepsEntityIsNotRestored),
            (nameof(DeleteForgetsTheLastStates), DeleteForgetsTheLastStates),
            (nameof(AnEntityLeavingTheDefinitionIsForgotten), AnEntityLeavingTheDefinitionIsForgotten),
            (nameof(AnEntityGivenAnotherDomainDoesNotComeBackAsTheOther), AnEntityGivenAnotherDomainDoesNotComeBackAsTheOther),
            (nameof(AnUnavailableEntityIsNotRemembered), AnUnavailableEntityIsNotRemembered),
            (nameof(AnEntityOnADeviceKeepsItsPushedState), AnEntityOnADeviceKeepsItsPushedState),
            (nameof(AnEntityOnADeviceWorksOutItsState), AnEntityOnADeviceWorksOutItsState),
            (nameof(ADeviceNodeIsNeverRun), ADeviceNodeIsNeverRun),
            (nameof(AnEntityOnADeviceIsHandedOverToANewRevision), AnEntityOnADeviceIsHandedOverToANewRevision),
            (nameof(ACommandFlowThatEndsAtAStopStepSucceeds), ACommandFlowThatEndsAtAStopStepSucceeds),
            (nameof(FailingSetupAndTriggerFlowsAreLoggedByName), FailingSetupAndTriggerFlowsAreLoggedByName),
            (nameof(AnAutomationsFailureIsLoggedAsBefore), AnAutomationsFailureIsLoggedAsBefore),
            (nameof(AFailingPollLogsOncePerReason), AFailingPollLogsOncePerReason),
            (nameof(ASlowStateWarnsOnce), ASlowStateWarnsOnce),
            (nameof(ADefinitionThatCannotStartIsFailed), ADefinitionThatCannotStartIsFailed),
            (nameof(ADefinitionThatCannotStartIsOffWhenTurnedOff), ADefinitionThatCannotStartIsOffWhenTurnedOff),
            (nameof(ASecretStaysHiddenInTheStepsThatReadIt), ASecretStaysHiddenInTheStepsThatReadIt),
            (nameof(TextBuiltFromASecretIsHiddenWhereItIsMadeAndUsed), TextBuiltFromASecretIsHiddenWhereItIsMadeAndUsed),
            (nameof(OnlyWhatASecretReachesIsHidden), OnlyWhatASecretReachesIsHidden),
            (nameof(AStepWithASecretInputShowsItsOutputs), AStepWithASecretInputShowsItsOutputs),
            (nameof(ASecretThatFailsIsNotQuoted), ASecretThatFailsIsNotQuoted),
            (nameof(SetStateKeepsWhatItIsNotGiven), SetStateKeepsWhatItIsNotGiven),
            (nameof(ACommandHandsItsDataToTheFlow), ACommandHandsItsDataToTheFlow),
            (nameof(ALightTurnedOnWithoutABrightnessHasItsLastOneOrFull), ALightTurnedOnWithoutABrightnessHasItsLastOneOrFull),
            (nameof(ALightThatCannotBeDimmedGetsNoBrightness), ALightThatCannotBeDimmedGetsNoBrightness),
            (nameof(AButtonRunsItsFlow), AButtonRunsItsFlow),
            (nameof(ACommandsDataIsCheckedAsAClassChecksIt), ACommandsDataIsCheckedAsAClassChecksIt),
            (nameof(APasswordTextsRefusedValueIsNotQuoted), APasswordTextsRefusedValueIsNotQuoted),
            (nameof(ADefinitionTakesPicturesUnderItsOwnIdAndAnAutomationCannot), ADefinitionTakesPicturesUnderItsOwnIdAndAnAutomationCannot),
            (nameof(APictureGoesIntoNoLibraryPinAndNoLibraryValueIsAPicture), APictureGoesIntoNoLibraryPinAndNoLibraryValueIsAPicture),
            (nameof(AGraphCameraMakesItsFrameOnlyWhenOneIsNeeded), AGraphCameraMakesItsFrameOnlyWhenOneIsNeeded),
            (nameof(AFrameFlowWhoseSourceFailsGivesTheLastGoodFrame), AFrameFlowWhoseSourceFailsGivesTheLastGoodFrame),
            (nameof(AFrameFlowWithoutShowFrameKeepsTheFrame), AFrameFlowWithoutShowFrameKeepsTheFrame),
            (nameof(APushedFrameIsAnsweredWithoutRunningAnything), APushedFrameIsAnsweredWithoutRunningAnything),
            (nameof(AFrameFlowPastItsLimitIsCancelledAndTheCameraRecovers), AFrameFlowPastItsLimitIsCancelledAndTheCameraRecovers),
            (nameof(ARevisionSavedWhileAFrameFlowRunsServesFrames), ARevisionSavedWhileAFrameFlowRunsServesFrames),
            (nameof(APushedFrameStaysAcrossASave), APushedFrameStaysAcrossASave),
            (nameof(AFrameFlowCancelledAsHomeAssistantStopsIsNoFailure), AFrameFlowCancelledAsHomeAssistantStopsIsNoFailure),
            (nameof(ACSharpEntityRunsBesideAHub), ACSharpEntityRunsBesideAHub),
            (nameof(ACSharpEntityTakesItsCommands), ACSharpEntityTakesItsCommands),
            (nameof(TwoPlugsOfOneClassAreTwoDevices), TwoPlugsOfOneClassAreTwoDevices),
            (nameof(AGraphWithCSharpNodesHasNoMainDevice), AGraphWithCSharpNodesHasNoMainDevice),
            (nameof(ADeviceCanBePartOfACSharpDeviceDeclaredLater), ADeviceCanBePartOfACSharpDeviceDeclaredLater),
            (nameof(ACSharpEntityOnADeviceTakesItsName), ACSharpEntityOnADeviceTakesItsName),
            (nameof(ACSharpEntityAloneIsCalledItsTitle), ACSharpEntityAloneIsCalledItsTitle),
            (nameof(SettingsAreAppliedBeforeSetup), SettingsAreAppliedBeforeSetup),
            (nameof(AClassThatCannotStartFailsTheDefinition), AClassThatCannotStartFailsTheDefinition),
            (nameof(ACSharpEntityIsRestoredAndAHubCarriedOver), ACSharpEntityIsRestoredAndAHubCarriedOver),
            (nameof(TwoEntitiesWithOneUniqueIdAreRefused), TwoEntitiesWithOneUniqueIdAreRefused),
            (nameof(TheAnswersListWhatTheCSharpNodesMake), TheAnswersListWhatTheCSharpNodesMake),
            (nameof(ACSharpEntityGivenAnotherDomainIsForgotten), ACSharpEntityGivenAnotherDomainIsForgotten) })
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
    /// <summary>
    /// An Entity node of the key and type (<see cref="Hub"/>): named by its key unless <paramref name="more"/> names it,
    /// with the rest of <paramref name="more"/> (such as its options or <c>history</c>) in place of its own settings and
    /// <paramref name="values"/> as its state values.
    /// </summary>
    static JsonObject Entity(string id, string key, string domain, object? more = null, JsonObject? values = null) =>
        Hub(id, key, domain, more == null ? null : JsonSerializer.SerializeToNode(more)!.AsObject(), values);
    /// <summary>The Entity node without a name, as an entity on a Device node may be.</summary>
    static JsonObject Unnamed(JsonObject hub) { hub["config"]!.AsObject().Remove("name"); return hub; }
    /// <summary>The node of the definition's graph with the id.</summary>
    static JsonObject NodeOf(JsonObject definition, string id) =>
        definition["logic"]!["graph"]!["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == id)!.AsObject();
    /// <summary>The definition as a client sends it: its entities are the Entity nodes of its graph, and its devices its Device nodes.</summary>
    static JsonObject Definition(string name, JsonObject graph, JsonNode[]? settings = null) => new()
    {
        ["name"] = name,
        ["settings"] = new JsonArray((settings ?? []).Select(s => s.DeepClone()).ToArray()),
        ["logic"] = new JsonObject { ["kind"] = "graph", ["graph"] = graph.DeepClone() },
    };
    static readonly object Unset = new { kind = "unset" };
    static JsonObject SetState(string id, string entity, JsonObject values)
    {
        values["entity"] = entity;
        return N(id, "set_entity_state", values);
    }
    static JsonObject On(bool on) => new() { ["is_on"] = Lit("boolean", on) };
    /// <summary>A step order wire from an Entity node's execution pin (<see cref="Flow"/>), with an id of its own words.</summary>
    static JsonObject Pin(string hub, string port, string target) { var wire = Flow(hub, port, target); wire["id"] = $"{hub}:{port}:{target}"; return wire; }
    static string Id(JsonObject node) => node["id"]!.GetValue<string>();
    static JsonObject Read(string id, string entity) => N(id, "read_entity", new JsonObject { ["entity"] = Lit("entity_id", entity) });

    /// <summary>The acceptance definition: a binary sensor on while motion A or motion B is (the shared fixture's graph).</summary>
    static JsonObject Motion() => Definition("Any motion", EntityStoreTests.MotionGraph());
    /// <summary>Waits, no longer than 5 s, for a Custom C# node's <c>Entered</c> to be released once: its run has reached it.</summary>
    static async Task Reached(SemaphoreSlim entered, string what) => Check(await entered.WaitAsync(TimeSpan.FromSeconds(5)), "timed out waiting: " + what);
    /// <summary>A sensor whose value is <c>sensor.outside</c>'s state, as a number.</summary>
    static JsonObject Copy() => Definition("Outside", Graph("Outside",
        [Entity("s", "outside", "sensor", new { name = "Outside" }), N("cur", "entity_current", new JsonObject { ["entity"] = Lit("entity_id", "sensor.outside") }), N("f", "entity_field", new { entity = Unset, field = "state", type = "number" })],
        Data("cur", "f", "entity"), Data("f", "s", "value")));
    /// <summary>
    /// A switch whose Turn on and Turn off pins lead to Set state steps that set its state, Turn on through the steps
    /// <paramref name="before"/> first; either pin left without steps when asked.
    /// </summary>
    static JsonObject Relay(bool turnOn = true, bool turnOff = true, JsonObject[]? before = null)
    {
        var nodes = new List<JsonObject> { Entity("relay", "relay", "switch", new { name = "Relay" }) }; var edges = new List<JsonObject>();
        if (turnOn)
        {
            List<JsonObject> chain = [.. before ?? [], SetState("set_on", "relay", On(true))];
            nodes.AddRange(chain);
            edges.Add(Pin("relay", "command:turn_on", Id(chain[0])));
            for (var i = 1; i < chain.Count; i++) edges.Add(Next(Id(chain[i - 1]), Id(chain[i])));
        }
        if (turnOff) { nodes.Add(SetState("set_off", "relay", On(false))); edges.Add(Pin("relay", "command:turn_off", "set_off")); }
        return Definition("Relay", Graph("Relay", [.. nodes], [.. edges]));
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
        Check(sync["devices"] is JsonArray { Count: 0 } && !sync.ContainsKey("device") && sync["name"]!.GetValue<string>() == "Any motion", "the sync has no device: " + sync.ToJsonString());
        Check(sync["entities"]!.ToJsonString() == """[{"uniqueId":"motion:any_motion","key":"any_motion","domain":"binary_sensor","name":"Any motion","icon":null,"deviceClass":null,"entityCategory":null,"enabledByDefault":true,"commands":[],"options":{},"device":null}]""",
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
            [Entity("s", "quick", "sensor"), N("cur", "entity_current", new JsonObject { ["entity"] = Lit("entity_id", "sensor.level") }), N("f", "entity_field", new { entity = Unset, field = "state", type = "number" }), Custom("slow", SlowValue)],
            Data("cur", "f", "entity"), Data("f", "slow", "value"), Data("slow", "s", "value", "result")));
        await rig.Save("quick", Definition("Quick", graph));
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
        // The warning is on the switch's Entity node, at the pin no step is connected to. It names the entity by its name.
        static bool Unhandled(JsonNode? diagnostics) => diagnostics?.AsArray().Any(d => d!["code"]!.GetValue<string>() == "command.unhandled" && d["severity"]!.GetValue<int>() == 2
            && d["nodeId"]?.GetValue<string>() == "relay" && d["portId"]?.GetValue<string>() == "command:turn_on" && d["message"]!.GetValue<string>() == "Relay does nothing on Turn on: connect a step to Turn on") == true;
        var preview = await rig.Ok(new { operation = "preview", definition = Relay(turnOn: false) });
        Check(preview["valid"]!.GetValue<bool>() && Unhandled(preview["diagnostics"]), "the preview warns of the command no flow handles: " + preview["diagnostics"]?.ToJsonString());
        // An entity on a Device node needs no name: one without is named by its key.
        var unnamed = OnADevice(Relay(turnOn: false), "relay");
        Unnamed(NodeOf(unnamed, "relay"));
        var byKey = (await rig.Ok(new { operation = "preview", definition = unnamed }))["diagnostics"]!.AsArray().Select(d => d!["message"]!.GetValue<string>()).ToArray();
        Check(byKey.Contains("relay does nothing on Turn on: connect a step to Turn on"), "an entity without a name is named by its key: " + string.Join(" | ", byKey));
        var saved = await rig.Save("relay", Relay(turnOn: false));
        Check(Unhandled(saved["diagnostics"]) && saved["active"]!.GetValue<bool>(), "the save warns of the command no flow handles, and runs: " + saved.ToJsonString());
        await rig.Until("relay:relay", _ => true, "the switch starts");
        var failed = await Throws<InvalidOperationException>(() => rig.Api.EntityCommand("relay:relay", "turn_on", null), "a command without a flow");
        Check(failed.Message == "Relay has no flow for Turn on", "the message names the entity and the command: " + failed.Message);
        await rig.Api.EntityCommand("relay:relay", "turn_off", null);
    }

    /// <summary>
    /// As in <see cref="ASwitchRunsItsCommandFlows"/>, the steps on a switch node's command pins run on its commands and
    /// the state they set is pushed; each run is recorded under the node, the entity's name and the command's.
    /// </summary>
    static async Task ASwitchNodeRunsItsCommandFlows()
    {
        await using var rig = await Rig.Start("switch-node");
        var automationId = AutomationId(await rig.Save("relay", Relay()));
        var sync = JsonNode.Parse(rig.Home.Syncs("relay").Single())!;
        Check(sync["entities"]![0]!["commands"]!.ToJsonString() == """["turn_on","turn_off"]""", "the switch takes its commands: " + sync.ToJsonString());
        await rig.Until("relay:relay", s => Available(s) && Is(s, "is_on", null), "a switch whose state is not worked out is pushed unknown as it starts");
        var batches = rig.Batches;
        await rig.Api.EntityCommand("relay:relay", "turn_on", null);
        await rig.Flush();
        Check(rig.Pushed("relay:relay", batches).LastOrDefault() is { } on && Is(on, "is_on", true), "turn on pushes on: " + Lines(rig.Home.Pushes().Skip(batches)));
        batches = rig.Batches;
        await rig.Api.EntityCommand("relay:relay", "turn_off", null);
        await rig.Flush();
        Check(rig.Pushed("relay:relay", batches).LastOrDefault() is { } off && Is(off, "is_on", false), "turn off pushes off: " + Lines(rig.Home.Pushes().Skip(batches)));
        var runs = CommandRuns(rig, automationId);
        Check(runs.Length == 2 && runs.All(r => r["status"]!.GetValue<string>() == "Succeeded" && r["triggerNodeId"]!.GetValue<string>() == "relay"), "both command runs are recorded, of the node: " + Lines(runs.Select(r => r.ToJsonString())));
        Check(runs.Select(r => r["actionLabel"]!.GetValue<string>()).SequenceEqual(["Relay: Turn off", "Relay: Turn on"]), "each run is named by the entity and its command: " + Lines(runs.Select(r => r.ToJsonString())));
        // The trace tells the flows apart: the node, and the pin its flow starts at.
        foreach (var (run, port) in runs.Zip(["command:turn_off", "command:turn_on"]))
        {
            var trace = JsonNode.Parse(rig.Api.GetRunTrace(run["runId"]!.GetValue<string>()))!["trace"]!;
            Check(trace["rootNodeId"]!.GetValue<string>() == "relay" && trace["rootPort"]?.GetValue<string>() == port, $"the trace of {port} names the node and its pin: " + trace.ToJsonString()[..Math.Min(300, trace.ToJsonString().Length)]);
        }
    }

    /// <summary>A command whose pin has no step fails, naming the entity and the command, and the definition runs (without the previews of <see cref="ACommandWithoutAFlowFails"/>).</summary>
    static async Task ACommandPinWithoutStepsFails()
    {
        await using var rig = await Rig.Start("no-pin-flow");
        var saved = await rig.Save("relay", Relay(turnOn: false));
        Check(saved["active"]!.GetValue<bool>(), "the definition runs: " + saved.ToJsonString());
        await rig.Until("relay:relay", _ => true, "the switch starts");
        var failed = await Throws<InvalidOperationException>(() => rig.Api.EntityCommand("relay:relay", "turn_on", null), "a command without a flow");
        Check(failed.Message == "Relay has no flow for Turn on", "the message names the entity and the command: " + failed.Message);
        await rig.Api.EntityCommand("relay:relay", "turn_off", null);
    }

    /// <summary>
    /// A light node turned on without a brightness reads the one the host fills in on its command pin: having none yet,
    /// the catalog's default, full.
    /// </summary>
    static async Task ALightNodeTurnedOnWithoutABrightnessIsAtFull()
    {
        await using var rig = await Rig.Start("light-node");
        var graph = Graph("Lamp", [Entity("lamp", "lamp", "light", new { name = "Lamp", options = new { dimmable = true } }), SetState("bright", "lamp", On(true))],
            Pin("lamp", "command:turn_on", "bright"), Data("lamp", "bright", "brightness", "command:turn_on:brightness"));
        await rig.Save("lamp", Definition("Lamp", graph));
        await rig.Until("lamp:lamp", _ => true, "the light starts");
        await rig.Api.EntityCommand("lamp:lamp", "turn_on", null);
        await rig.Until("lamp:lamp", s => Is(s, "is_on", true) && Is(s, "brightness", 255), "turned on at full, having no brightness yet");
        await rig.Api.EntityCommand("lamp:lamp", "turn_on", """{"brightness":80}""");
        await rig.Until("lamp:lamp", s => Is(s, "brightness", 80), "turned on at the brightness asked for");
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
                var nodes = new List<JsonObject> { Entity("level", "level", "sensor"), N("start", "entity_setup") }; var edges = new List<JsonObject>(); var previous = "start";
                foreach (var step in steps) { nodes.Add(step); edges.Add(Next(previous, step["id"]!.GetValue<string>())); previous = step["id"]!.GetValue<string>(); }
                return Definition("Level", Graph("Level", [.. nodes], [.. edges]));
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
            Check(!fresh["active"]!.GetValue<bool>() && fresh["activationError"]?.GetValue<string>() == "Entity 'sensor.gone' is unavailable." && Status(fresh["document"]!.AsObject()) == "failed",
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
        var graph = Derived(Graph("Level", [Entity("level", "level", "sensor"), N("start", "entity_setup"), Custom("hold", HoldStep)], Next("start", "hold")));
        var saved = await rig.Save("level", Definition("Level", graph));
        Check(!saved["active"]!.GetValue<bool>() && saved["activationError"]?.GetValue<string>() == "Level does not start: its When started flow did not finish within 0.3 s" && rig.Home.Syncs("level").Length == 0,
            "a setup that does not finish fails the save: " + saved.ToJsonString());
    }

    /// <summary>
    /// A definition whose Device node <c>plug</c> has its three entities wired to it registers them under it, each
    /// described as the document says, one of them without a name of its own: the device is the Device node's.
    /// </summary>
    static async Task ADeviceNodeGroupsItsEntities()
    {
        await using var rig = await Rig.Start("device");
        var graph = Graph("Plug",
            [Entity("p", "power", "sensor", new { name = "Power", deviceClass = "power", options = new { unit = "W", stateClass = "measurement" } }, new JsonObject { ["value"] = Lit("number", 42) }),
             Unnamed(Entity("o", "online", "binary_sensor", new { deviceClass = "connectivity", entityCategory = "diagnostic" }, On(true))),
             Entity("relay", "relay", "switch", new { name = "Relay", icon = "mdi:power-socket", enabledByDefault = false }),
             SetState("set_on", "relay", On(true)), SetState("set_off", "relay", On(false)),
             DeviceNode("plug", "Plug", new { manufacturer = "Acme", model = "P1", suggestedArea = "Kitchen" })],
            Pin("relay", "command:turn_on", "set_on"), Pin("relay", "command:turn_off", "set_off"));
        foreach (var hub in new[] { "p", "o", "relay" }) Wire(graph, "plug", hub);
        var definition = Definition("Plug", graph);
        var saved = await rig.Save("plug", definition);
        Check(saved["active"]!.GetValue<bool>() && !saved.ContainsKey("diagnostics"), "the definition with a device saves without a problem and runs: " + saved.ToJsonString());
        var sync = JsonNode.Parse(rig.Home.Syncs("plug").Single())!;
        Check(sync["devices"]!.ToJsonString() == """[{"key":"plug","name":"Plug","manufacturer":"Acme","model":"P1","swVersion":null,"hwVersion":null,"configurationUrl":null,"suggestedArea":"Kitchen","via":null}]"""
            && !sync.AsObject().ContainsKey("device"), "the device is the Device node's: " + sync.ToJsonString());
        Check(sync["entities"]!.ToJsonString() == """[{"uniqueId":"plug:power","key":"power","domain":"sensor","name":"Power","icon":null,"deviceClass":"power","entityCategory":null,"enabledByDefault":true,"commands":[],"options":{"unit":"W","stateClass":"measurement","options":null},"device":"plug"},"""
            + """{"uniqueId":"plug:online","key":"online","domain":"binary_sensor","name":null,"icon":null,"deviceClass":"connectivity","entityCategory":"diagnostic","enabledByDefault":true,"commands":[],"options":{},"device":"plug"},"""
            + """{"uniqueId":"plug:relay","key":"relay","domain":"switch","name":"Relay","icon":"mdi:power-socket","deviceClass":null,"entityCategory":null,"enabledByDefault":false,"commands":["turn_on","turn_off"],"options":{},"device":"plug"}]""",
            "the three entities as the document describes them: " + sync["entities"]!.ToJsonString());
        await rig.Until("plug:power", s => Is(s, "value", 42.0), "the power State");
        await rig.Until("plug:online", s => Is(s, "is_on", true), "the online State");
        await rig.Until("plug:relay", s => Available(s), "the relay");
    }

    /// <summary>A Device node of the key (also its id), named, with the rest of its config (such as its model or Part of).</summary>
    static JsonObject DeviceNode(string key, string name, object? more = null)
    {
        var config = new JsonObject { ["key"] = key, ["name"] = name };
        foreach (var (member, value) in JsonSerializer.SerializeToNode(more ?? new { })!.AsObject()) config[member] = value?.DeepClone();
        return N(key, "device", config);
    }
    /// <summary>Wires the Device node's Entities pin to the Entity node's Device pin, the pin's marker beside it as the editor saves it.</summary>
    static void Wire(JsonObject graph, string device, string hub)
    {
        graph["edges"]!.AsArray().Add(Data(device, hub, "device", "entities"));
        var config = graph["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == hub)!["config"]!.AsObject();
        if (config["arguments"] is not JsonObject arguments) config["arguments"] = arguments = [];
        arguments["device"] = JsonSerializer.SerializeToNode(Unset);
    }

    /// <summary>
    /// Device nodes plug (Hall plug, part of hub, declared first) and hub, a switch on plug, a binary sensor on hub and a
    /// standalone one: Home Assistant is handed hub, then plug, part of hub, and each entity's device, or none.
    /// </summary>
    static async Task TwoDevicesAndAStandaloneEntityReachHomeAssistant()
    {
        await using var rig = await Rig.Start("two-devices");
        var graph = Graph("Plug",
            [DeviceNode("plug", "Hall plug", new { model = "P1", via = new { device = "hub" } }), DeviceNode("hub", "Hub"),
             Entity("relay", "relay", "switch", new { name = "Relay" }),
             Unnamed(Entity("online", "online", "binary_sensor", new { deviceClass = "connectivity" }, On(true))),
             Entity("bare", "bare", "binary_sensor", new { name = "Bare" }, On(false)),
             SetState("set_on", "relay", On(true)), SetState("set_off", "relay", On(false))],
            Pin("relay", "command:turn_on", "set_on"), Pin("relay", "command:turn_off", "set_off"));
        Wire(graph, "plug", "relay");
        Wire(graph, "hub", "online");
        var saved = await rig.Save("plug", Definition("Plug", graph));
        Check(saved["active"]!.GetValue<bool>() && !saved.ContainsKey("diagnostics"), "the definition saves without a problem and runs: " + saved.ToJsonString());
        var sync = JsonNode.Parse(rig.Home.Syncs("plug").Single())!.AsObject();
        Check(sync["devices"]!.ToJsonString() == """[{"key":"hub","name":"Hub","manufacturer":null,"model":null,"swVersion":null,"hwVersion":null,"configurationUrl":null,"suggestedArea":null,"via":null},"""
            + """{"key":"plug","name":"Hall plug","manufacturer":null,"model":"P1","swVersion":null,"hwVersion":null,"configurationUrl":null,"suggestedArea":null,"via":{"device":"hub"}}]""",
            "hub, then plug, part of hub: " + sync["devices"]!.ToJsonString());
        Check(!sync.ContainsKey("device"), "the definition is no device of its own: " + sync.ToJsonString());
        var on = sync["entities"]!.AsArray().ToDictionary(e => e!["key"]!.GetValue<string>(), e => e!.AsObject().TryGetPropertyValue("device", out var key) ? key?.ToJsonString() : "absent");
        Check(on.Count == 3 && on["relay"] == "\"plug\"" && on["online"] == "\"hub\"" && on["bare"] == null, "each entity's device: " + sync["entities"]!.ToJsonString());
        await rig.Until("plug:online", s => Is(s, "is_on", true), "the online State");
        await rig.Until("plug:bare", s => Is(s, "is_on", false), "the bare State");
        await rig.Api.EntityCommand("plug:relay", "turn_on", null);
        await rig.Until("plug:relay", s => Is(s, "is_on", true), "the relay on a device takes its command");
    }

    /// <summary>
    /// The devices a definition hands Home Assistant come each after the one it is part of; the others keep node order. A
    /// key two nodes have is the first of them, and a circle or an unknown parent, which the validator refuses, neither
    /// loops nor throws: what can be ordered is, the rest follows in node order, and a parent that is no other device of
    /// the list is none.
    /// </summary>
    static Task TheDevicesAreListedAfterTheDeviceTheyArePartOf()
    {
        static DefinitionDevice Device(string key, string? partOf = null, string? name = null) =>
            new(null, null, null, null, null, null, partOf == null ? null : new DefinitionVia(null, null, partOf)) { Key = key, Name = name ?? key };
        static string Keys(IEnumerable<DefinitionDevice> devices) => string.Join(",", devices.Select(d => d.Name));
        var chain = DefinitionHost.InOrder([Device("c", "b"), Device("b", "a"), Device("a"), Device("d")]);
        Check(Keys(chain) == "a,b,c,d", "a chain declared backwards: " + Keys(chain));
        var roots = DefinitionHost.InOrder([Device("z"), Device("y"), Device("x", "y")]);
        Check(Keys(roots) == "z,y,x", "roots keep node order: " + Keys(roots));
        var circle = DefinitionHost.InOrder([Device("x", "y"), Device("self", "self"), Device("y", "x"), Device("u", "nowhere"), Device("z")]);
        Check(Keys(circle) == "u,z,x,self,y", "a circle and an unknown parent: " + Keys(circle));
        var twice = DefinitionHost.InOrder([Device("plug", "hub"), Device("hub", name: "Hub"), Device("hub", "plug", "Desk hub")]);
        Check(Keys(twice) == "Hub,plug", "a key two nodes have is the first of them: " + Keys(twice));
        var keys = new HashSet<string>(["a", "x"], StringComparer.Ordinal);
        string Via(DefinitionDevice device) => DefinitionHost.DeviceJson(device, keys)["via"]?.ToJsonString() ?? "null";
        Check(Via(Device("x", "a")) == """{"device":"a"}""" && Via(Device("x", "nowhere")) == "null" && Via(Device("x", "x")) == "null"
            && Via(new(null, null, null, null, null, null, new DefinitionVia(null, "zigbee")) { Key = "x" }) == """{"deviceId":"zigbee"}"""
            && Via(new(null, null, null, null, null, null, new DefinitionVia("hub", null)) { Key = "x" }) == """{"definition":"hub"}""",
            "Part of in the sync: another device of the list, a Home Assistant device or another definition; else none");
        return Task.CompletedTask;
    }

    /// <summary>
    /// A class request's device carries none of what only a Device node has, its key, name and Part of another of its
    /// devices: they are left out where the request is read, and nothing is refused. A graph request's device is refused,
    /// and with device null it saves and Home Assistant gets no device for it.
    /// </summary>
    static async Task AClassRequestsDeviceIsStoredWithoutWhatOnlyADeviceNodeHasAndAGraphsIsRefused()
    {
        await using var rig = await Rig.Start("request-device");
        var graph = Relay();
        graph["device"] = JsonSerializer.SerializeToNode(new { key = "x", name = "y", model = "P1", via = new { device = "z" } });
        var refused = await rig.Request(new { operation = "save", id = "relay", expectedRevision = 0, definition = graph });
        Check(refused["error"]?["code"]?.GetValue<string>() == "validation" && refused["error"]!["detail"]!["diagnostics"]!.AsArray().Single() is { } problem
            && problem["code"]!.GetValue<string>() == "definition.deviceNotAllowed" && problem["inputId"]!.GetValue<string>() == "device"
            && problem["message"]!.GetValue<string>() == EntityStoreTests.DeviceNotAllowed, "the graph definition with a device is refused: " + refused.ToJsonString());
        Check(!File.Exists(StoredFile(rig, "relay")) && rig.Home.Syncs("relay").Length == 0, "nothing is stored or synced");
        graph["device"] = null;
        var saved = await rig.Save("relay", graph);
        Check(saved["active"]!.GetValue<bool>() && !saved.ContainsKey("diagnostics"), "with device null it saves and runs: " + saved.ToJsonString());
        Check(JsonNode.Parse(File.ReadAllText(StoredFile(rig, "relay")))!["device"] == null && (await rig.Read("relay"))["device"] == null, "its device is stored and read as null");
        var sync = JsonNode.Parse(rig.Home.Syncs("relay").Single())!;
        Check(sync["devices"] is JsonArray { Count: 0 } && sync["entities"]!.AsArray().All(e => e!["device"] == null), "Home Assistant gets no device for it: " + sync.ToJsonString());

        Check((await rig.Library("secret", 0, SecretLibrary))["ok"]!.GetValue<bool>(), "the library saves");
        var plug = JsonSerializer.SerializeToNode(SecretPlugDefinition())!.AsObject();
        plug["device"] = JsonSerializer.SerializeToNode(new { key = "x", name = "y", via = new { device = "z", deviceId = "abc" } });
        saved = await rig.Save("plug", plug);
        Check(saved["active"]!.GetValue<bool>(), "the class definition saves and runs: " + saved.ToJsonString());
        var expected = JsonNode.Parse("""{"manufacturer":null,"model":null,"swVersion":null,"hwVersion":null,"configurationUrl":null,"suggestedArea":null,"via":{"definition":null,"deviceId":"abc"}}""");
        var stored = JsonNode.Parse(File.ReadAllText(StoredFile(rig, "plug")))!["device"];
        Check(JsonNode.DeepEquals(stored, expected) && JsonNode.DeepEquals((await rig.Read("plug"))["device"], expected), "the class's device as stored: " + stored?.ToJsonString());
        sync = JsonNode.Parse(rig.Home.Syncs("plug").Single())!;
        Check(sync["devices"]![0]!["name"]!.GetValue<string>() == "Plug" && sync["devices"]![0]!["via"]!.ToJsonString() == """{"deviceId":"abc"}""",
            "the class's device is named after the definition, part of the Home Assistant device: " + sync.ToJsonString());
    }

    /// <summary>A setting's value reaches the graph, also when a save changes only the value, which leaves the generated source as it was.</summary>
    static async Task SettingsReachTheGraph()
    {
        await using var rig = await Rig.Start("settings");
        var graph = Graph("Level", [Entity("s", "level", "sensor"), N("k", "setting", new { key = "offset" })], Data("k", "s", "value"));
        JsonObject Level(double offset) => Definition("Level", graph, [new JsonObject { ["key"] = "offset", ["type"] = "number", ["label"] = "Offset", ["value"] = offset }]);
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
        JsonObject Pair(string second) => Definition("Pair", Graph("Pair",
            [Entity("s1", "a", "sensor", new { name = "A" }, new JsonObject { ["value"] = Lit("number", 1) }), Entity("s2", second, "sensor", values: new JsonObject { ["value"] = Lit("number", 2) })]));
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
        var graph = Graph("Level", [Entity("s", "level", "sensor"), N("k", "setting", new { key = "offset" }), N("h", half, new { arguments = new { } })], Data("k", "h", "value"), Data("h", "s", "value", "result"));
        await rig.Save("level", Definition("Level", graph, [new JsonObject { ["key"] = "offset", ["type"] = "number", ["value"] = 4 }]));
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
        var graph = Graph("Level", [Entity("s", "level", "sensor"), N("k", "setting", new { key = "offset" }), N("h", half, new { arguments = new { } })], Data("k", "h", "value"), Data("h", "s", "value", "result"));
        await rig.Save("level", Definition("Level", graph, [new JsonObject { ["key"] = "offset", ["type"] = "number", ["value"] = 4 }]));
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
        // Init does not wait for a start, which runs on a thread of its own.
        await rig.Started("motion");
        var motion = await rig.Read("motion");
        Check(Status(motion) == "running" && motion["revision"]!.GetValue<long>() == 1 && motion["currentRevisionActive"]!.GetValue<bool>(), "it runs after the restart: " + motion.ToJsonString());
        await rig.Until("motion:any_motion", s => Available(s) && Is(s, "is_on", true), "the State worked out after the restart", batches);
        await rig.Api.EntityCommand("relay:relay", "turn_on", null);
        await rig.Until("relay:relay", s => Is(s, "is_on", true), "the command after the restart", batches);
    }

    /// <summary>
    /// The run history records the When started flow and a command's flow, with the definition's id and a trace, and
    /// never a state flow; a trigger's flow only when its node turns it on.
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
        var saved = await rig.Save("both", Definition("Both", motion));
        var automationId = saved["document"]!["automationId"]!.GetValue<string>();
        await rig.Until("both:any_motion", _ => true, "the State");
        await rig.Change("binary_sensor.motion_a", "on");
        await rig.Api.EntityCommand("both:relay", "turn_on", null);
        var runs = rig.History(automationId);
        Check(rig.Home.Logs().Contains("quiet") && rig.Home.Logs().Contains("tracked"), "both trigger flows ran: " + Lines(rig.Home.Logs()));
        Check(runs.Select(r => r["cause"]!.GetValue<string>()).Order(StringComparer.Ordinal).SequenceEqual(["Command", "Entity", "Setup"]), "the runs recorded: " + Lines(runs.Select(r => r.ToJsonString())));
        Check(runs.Single(r => r["cause"]!.GetValue<string>() == "Entity")["triggerNodeId"]!.GetValue<string>() == "tracked", "the trigger flow recorded is the one that turns its history on: " + Lines(runs.Select(r => r.ToJsonString())));
        var command = runs.Single(r => r["cause"]!.GetValue<string>() == "Command");
        Check(command["status"]!.GetValue<string>() == "Succeeded" && command["visualId"]!.GetValue<string>() == "both" && command["triggerNodeId"]!.GetValue<string>() == "relay"
            && command["visualRevision"]!.GetValue<long>() == 1 && command["hasTrace"]!.GetValue<bool>(), "the command's run: " + command.ToJsonString());
        Check(command["trigger"]!.ToJsonString().Contains("command", StringComparison.Ordinal), "the command run's trigger: " + command["trigger"]!.ToJsonString());
        var trace = JsonNode.Parse(rig.Api.GetRunTrace(command["runId"]!.GetValue<string>()))!;
        Check(trace["snapshot"]!["graph"]!["definition"] is JsonObject && trace["snapshot"]!["graph"]!["nodes"]!.AsArray().Count(n => n!["kind"]!.GetValue<string>() == "entity") == 2 && trace["trace"] != null,
            "the command's trace has the graph with its definition and its two Entity nodes: " + trace.ToJsonString()[..Math.Min(400, trace.ToJsonString().Length)]);
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
        var graph = Derived(Graph("Outside", [Entity("s", "outside", "sensor", new { name = "Outside" }), N("cur", "entity_current", new JsonObject { ["entity"] = Lit("entity_id", "sensor.outside") }),
            N("f", "entity_field", new { entity = Unset, field = "state", type = "number" }), Custom("broken", BrokenValue)], Data("cur", "f", "entity"), Data("f", "broken", "value"), Data("broken", "s", "value", "result")));
        await rig.Save("bug", Definition("Outside", graph));
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
        var graph = Graph("Mode", [Entity("s", "mode", "sensor", new { name = "Mode", deviceClass = "enum", options = new { options = new[] { "low", "high" } } }),
            N("cur", "entity_current", new JsonObject { ["entity"] = Lit("entity_id", "sensor.mode") }), N("f", "entity_field", new { entity = Unset, field = "state", type = "string" })],
            Data("cur", "f", "entity"), Data("f", "s", "value"));
        await rig.Save("mode", Definition("Mode", graph));
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
        var graph = Graph("Temp", [Entity("s", "temp", "sensor", new { name = "Temp", deviceClass = "temperature", options = new { unit = "°C" } }),
            N("cur", "entity_current", new JsonObject { ["entity"] = Lit("entity_id", "sensor.raw") }), N("f", "entity_field", new { entity = Unset, field = "state", type = "string" })],
            Data("cur", "f", "entity"), Data("f", "s", "value"));
        await rig.Save("temp", Definition("Temp", graph));
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
        var graph = Graph("Button", [Entity("last", "last", "sensor"), N("t", "trigger", new { type = "entity", entity = "sensor.button" }), SetState("set", "last", new JsonObject { ["value"] = Lit("string", "pressed") })], Next("t", "set"));
        await rig.Save("button", Definition("Button", graph));
        await rig.Until("button:last", s => Is(s, "value", null), "the sensor starts unknown");
        var batches = rig.Batches;
        await rig.Change("sensor.button", "pressed");
        await rig.Flush();
        Check(rig.Pushed("button:last", batches).LastOrDefault() is { } set && Is(set, "value", "pressed"), "the trigger flow set the state: " + Lines(rig.Home.Pushes().Skip(batches)));
    }

    /// <summary>
    /// A definition that declares an entity with an Entity node creates it: the preview asks for no entity, the save
    /// stores and answers the node's entity, syncs it, and a Set state naming it sets it. The answer saved back with its
    /// entities is refused; saved back without them, it answers the same entities.
    /// </summary>
    static async Task AnEntityNodeCreatesItsEntity()
    {
        await using var rig = await Rig.Start("entity-node", ("sensor.button", "idle"));
        var graph = Graph("Door", [Hub("door", "door", "binary_sensor"), N("t", "trigger", new { type = "entity", entity = "sensor.button" }), SetState("set", "door", On(true))], Next("t", "set"));
        var definition = Definition("Door", graph);
        var preview = await rig.Ok(new { operation = "preview", definition });
        Check(preview["valid"]!.GetValue<bool>() && !preview["diagnostics"]!.AsArray().Any(d => d!["code"]?.GetValue<string>() == "definition.entitiesRequired"),
            "the preview is valid and asks for no entity: " + preview["diagnostics"]!.ToJsonString());
        await rig.Save("door", definition);
        var read = await rig.Read("door");
        var entities = read["entities"]!.AsArray();
        Check(entities.Count == 1 && entities[0]!["key"]!.GetValue<string>() == "door" && entities[0]!["domain"]!.GetValue<string>() == "binary_sensor" && entities[0]!["name"]!.GetValue<string>() == "door",
            "the read answer's entities are the node's: " + entities.ToJsonString());
        var sync = JsonNode.Parse(rig.Home.Syncs("door").Last())!;
        Check(sync["entities"]!.AsArray().Select(e => e!["uniqueId"]!.GetValue<string>()).SequenceEqual(["door:door"]), "the home syncs the node's entity: " + sync.ToJsonString());
        await rig.Until("door:door", _ => true, "the binary sensor starts");
        var batches = rig.Batches;
        await rig.Change("sensor.button", "pressed");
        await rig.Flush();
        Check(rig.Pushed("door:door", batches).LastOrDefault() is { } set && Is(set, "is_on", true), "the trigger flow set the node's entity: " + Lines(rig.Home.Pushes().Skip(batches)));
        // The read answer's entities are for reading: saved back with them, it is refused in words; without them, as the
        // editor sends it, it answers the same entities. Its device is null, which a graph's request may send.
        Check(read.ContainsKey("device") && read["device"] == null, "a graph definition's read answer has device null: " + read.ToJsonString());
        var back = new JsonObject
        {
            ["name"] = read["name"]!.DeepClone(), ["device"] = read["device"]?.DeepClone(), ["settings"] = read["settings"]!.DeepClone(),
            ["entities"] = read["entities"]!.DeepClone(), ["logic"] = read["logic"]!.DeepClone(),
        };
        var refused = await rig.Request(new { operation = "save", id = "door", expectedRevision = read["revision"]!.GetValue<long>(), definition = back });
        Check(refused["error"]?["code"]?.GetValue<string>() == "validation" && refused["error"]!["detail"]!["diagnostics"]!.AsArray().Single() is { } problem
            && problem["code"]!.GetValue<string>() == "definition.entitiesNotAllowed" && problem["inputId"]!.GetValue<string>() == "entities"
            && problem["message"]!.GetValue<string>() == EntityStoreTests.ListedEntities, "the read answer saved back with its entities is refused: " + refused.ToJsonString());
        back.Remove("entities");
        // Its devices are for reading too, but sent back they are no problem: they are left as the graph's Device nodes give them.
        Check(read["devices"] is JsonArray { Count: 0 } && entities[0]!.AsObject().TryGetPropertyValue("device", out var on) && on == null,
            "the read answer lists no device, and its entity is on none: " + read.ToJsonString());
        back["devices"] = new JsonArray(new JsonObject { ["key"] = "evil", ["name"] = "Not on the canvas" });
        var again = await rig.Save("door", back, read["revision"]!.GetValue<long>());
        Check(JsonNode.DeepEquals(again["document"]!["entities"], read["entities"]), "the read answer saved back without them answers the same entities: " + again["document"]!["entities"]!.ToJsonString());
        Check(again["document"]!["devices"] is JsonArray { Count: 0 } && !again.ToJsonString().Contains("evil", StringComparison.Ordinal),
            "the devices it sent back are ignored: " + again["document"]!.ToJsonString());
    }

    /// <summary>A definition that has no Entity node is asked for one, and for nothing else.</summary>
    static async Task APreviewWithoutAnyEntityAsksForOne()
    {
        await using var rig = await Rig.Start("no-entity");
        var definition = Definition("Nothing", Graph("Nothing", []));
        var preview = await rig.Ok(new { operation = "preview", definition });
        var errors = preview["diagnostics"]!.AsArray().Where(d => d!["severity"]!.GetValue<int>() == 3).ToArray();
        Check(!preview["valid"]!.GetValue<bool>() && errors.Length == 1 && errors[0]!["code"]!.GetValue<string>() == "definition.entitiesRequired",
            "the one error asks for an entity: " + preview["diagnostics"]!.ToJsonString());
    }

    /// <summary>
    /// A graph whose only node is a C# entity, or a C# device, declares what it makes: its preview does not ask for an
    /// entity, and has no problem at all. It is saved, with its node's factory in its source, and runs.
    /// </summary>
    static async Task AGraphWithOnlyACSharpNodeIsADefinition()
    {
        await using var rig = await Rig.Start("csharp-only");
        foreach (var node in new[] { EntityNodeTests.CSharpEntity("thermo", "thermometer"), EntityNodeTests.CSharpDevice("plug", "plug", "Plug") })
        {
            var id = Id(node);
            var definition = Definition("Hall", Graph("Hall", [node]));
            var preview = await rig.Ok(new { operation = "preview", definition });
            Check(preview["valid"]!.GetValue<bool>() && preview["diagnostics"]!.AsArray().Count == 0,
                $"a graph of only {id} is asked for no entity, and has no problem: " + preview["diagnostics"]!.ToJsonString());
            var saved = await rig.Save("hall_" + id, definition);
            Check(saved["saved"]!.GetValue<bool>() && saved["active"]!.GetValue<bool>() && saved["activationError"] == null
                && saved["document"]!["source"]!.GetValue<string>().Contains($"\"{id}\" => new global::NodeLib.Custom_{id}.", StringComparison.Ordinal),
                $"a graph of only {id} is saved with its factory, and runs: " + saved.ToJsonString());
        }
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
    /// as an automation's are, each after its node's id, warns once of them, and is not tried again when Home Assistant
    /// pushes its services.
    /// </summary>
    static async Task AnInvalidGraphDefinitionReadsLikeAnAutomationAndIsNotRetried()
    {
        await using var rig = await Rig.Start("invalid");
        await rig.Library("maths", 0, EntityStoreTests.Maths);
        var half = await rig.LibraryNode("Half");
        var graph = Graph("Level", [Entity("s", "level", "sensor"), N("k", "setting", new { key = "offset" }), N("h", half, new { arguments = new { } })], Data("k", "h", "value"), Data("h", "s", "value", "result"));
        await rig.Save("level", Definition("Level", graph, [new JsonObject { ["key"] = "offset", ["type"] = "number", ["value"] = 4 }]));
        var path = Path.Combine(rig.Home.Root, ".storage", "hass_sharp", "libraries", "maths.json");
        var library = JsonNode.Parse(File.ReadAllText(path))!;
        library["source"] = "public static class Maths { [Node(\"Double\", Pure = true)] public static double Twice(double value) => value * 2; }";
        await rig.Api.UnloadAsync();
        File.WriteAllText(path, library.ToJsonString());
        await rig.Restart();
        var level = await rig.Read("level");
        var error = level["activationError"]!.GetValue<string>();
        Check(Status(level) == "failed" && error.Split('\n').All(line => line.StartsWith("h: ", StringComparison.Ordinal) || line.StartsWith("s: ", StringComparison.Ordinal) || line.StartsWith("k: ", StringComparison.Ordinal)),
            "the problems are each after their node's id: " + level.ToJsonString());
        // The one warning of a definition that cannot start, with the problems as the load error has them; nothing at error.
        Check(rig.Home.Logs(PyLogLevel.Warn).Count(l => l == $"Entity definition \"Level\" (level) cannot start: {error}") == 1 && rig.Home.Logs(PyLogLevel.Error).Length == 0,
            "the startup warning has the problems as the load error does: " + Lines(rig.Home.Logs(PyLogLevel.Warn)) + " / " + Lines(rig.Home.Logs(PyLogLevel.Error)));
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
        var stray = new JsonObject { ["entities"] = new JsonArray(new JsonObject { ["key"] = "door", ["domain"] = "binary_sensor" }), ["settings"] = new JsonArray() };
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
        var state = Graph("State", [N("t", "trigger", new { type = "manual" }), Entity("s", "door", "binary_sensor", values: On(true))]);
        var refused = await Visual(new { operation = "save", id = "state", expectedRevision = 0, graph = With(state) });
        Check(!refused["ok"]!.GetValue<bool>() && refused["error"]!["code"]!.GetValue<string>() == "validation" && Codes(refused).Contains("node.document"),
            "an Entity node in an automation is refused: " + refused.ToJsonString());
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
        var graph = Graph("Level", [Entity("s", "level", "sensor"), N("k", "setting", new { key = "offset" })], Data("k", "s", "value"));
        await rig.Save("level", Definition("Level", graph, [
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

    /// <summary>
    /// A version 1 class document, as an earlier build saved it, still loads: after a restart it is listed and read as it
    /// is, starts, and Home Assistant gets its entities.
    /// </summary>
    static async Task AnOlderClassDocumentStillLoadsAndRuns()
    {
        await using var rig = await Rig.Start("older-class");
        Check((await rig.Library("secret", 0, SecretLibrary))["ok"]!.GetValue<bool>(), "the library saves");
        await rig.Save("plug", JsonSerializer.SerializeToNode(SecretPlugDefinition(new { key = "Host", value = "10.0.0.7" }))!.AsObject());
        var path = StoredFile(rig, "plug");
        var file = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        file["version"] = 1;
        File.WriteAllText(path, file.ToJsonString());
        var syncs = rig.Home.Syncs("plug").Length;
        await rig.Restart();
        var read = await rig.Read("plug");
        Check(read["version"]!.GetValue<int>() == 1 && read["logic"]!["kind"]!.GetValue<string>() == "class" && read["revision"]!.GetValue<long>() == 1, "it reads as it was saved: " + read.ToJsonString());
        var listed = (await rig.Request(new { operation = "list" }))["result"]!.AsArray();
        Check(listed.Any(d => d!["id"]!.GetValue<string>() == "plug"), "it is listed: " + listed.ToJsonString());
        await EntityTestHome.Until(() => rig.Home.Syncs("plug").Length > syncs, "the restart syncs it");
        var sync = JsonNode.Parse(rig.Home.Syncs("plug").Last())!;
        Check(sync["entities"]!.AsArray().Select(e => e!["uniqueId"]!.GetValue<string>()).SequenceEqual(["plug:relay"]), "Home Assistant gets its entity: " + sync.ToJsonString());
        await rig.Until("plug:relay", s => Available(s), "its relay runs");
    }

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
        Definition("Level", Graph("Level", [Entity("s", "level", "sensor"), N("k", "setting", new { key = "offset" })], Data("k", "s", "value")), settings);
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

        // Its Entity node without a name, and on no device: not valid.
        var unnamed = Motion(); unnamed["logic"]!["graph"]!["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "s")!["config"]!.AsObject().Remove("name");
        var invalid = await rig.Ok(new { operation = "preview", definition = unnamed });
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

        // A stored setting the class does not declare (renamed while the libraries had errors, or a file edited) may be
        // secret for all that is known: it is masked, the class's other settings as before.
        var stray = JsonNode.Parse(File.ReadAllText(StoredFile(rig, "plug")))!.AsObject();
        stray["settings"]!.AsArray().Add(new JsonObject { ["key"] = "Pin", ["value"] = "stray-hunter2" });
        File.WriteAllText(StoredFile(rig, "plug"), stray.ToJsonString());
        var strayRead = await rig.Read("plug");
        var strayListed = (await rig.Request(new { operation = "list" }))["result"]!.AsArray().Single(d => d!["id"]!.GetValue<string>() == "plug")!;
        string[] straySettings = [.. plugSettings, """{"key":"Pin","value":null,"secret":true,"hasValue":true}"""];
        Check(Shaped(strayRead, straySettings) && Shaped(strayListed, straySettings) && !(strayRead.ToJsonString() + strayListed.ToJsonString()).Contains("hunter2", StringComparison.Ordinal),
            "a setting the class does not declare is masked: " + strayRead["settings"]!.ToJsonString() + " / " + strayListed["settings"]!.ToJsonString());

        // Two stored settings of a graph with one key (a file edited by hand), one of them secret: the key is secret, so
        // both are masked.
        var twin = JsonNode.Parse(File.ReadAllText(StoredFile(rig, "level")))!.AsObject();
        twin["id"] = "twin"; twin["enabled"] = false;
        twin["settings"]!.AsArray().Add(new JsonObject { ["key"] = "token", ["type"] = "string", ["value"] = "twin-hunter2" });
        File.WriteAllText(StoredFile(rig, "twin"), twin.ToJsonString());
        var twinRead = await rig.Read("twin");
        var twinListed = (await rig.Request(new { operation = "list" }))["result"]!.AsArray().Single(d => d!["id"]!.GetValue<string>() == "twin")!;
        string[] twinSettings = [.. levelSettings, """{"key":"token","value":null,"type":"string","secret":true,"hasValue":true}"""];
        Check(Shaped(twinRead, twinSettings) && Shaped(twinListed, twinSettings) && !(twinRead.ToJsonString() + twinListed.ToJsonString()).Contains("hunter2", StringComparison.Ordinal),
            "a key that one of its settings marks secret is masked in each: " + twinRead["settings"]!.ToJsonString() + " / " + twinListed["settings"]!.ToJsonString());
    }

    /// <summary>
    /// A definition read and saved back as answered keeps its secrets: a masked setting sent back with <c>hasValue</c> and
    /// no value keeps the stored value, as <c>keep</c> does. A value given with <c>hasValue</c> replaces it; <c>hasValue</c>
    /// false with no value clears it. A kept secret stays secret, whatever the entry says. Under the fallback that masks
    /// every setting (the libraries do not compile), such a save is refused and writes nothing; once they compile again,
    /// the answer read before saves back with every value kept.
    /// </summary>
    static async Task AnAnswerSavedBackKeepsItsSecrets()
    {
        await using var rig = await Rig.Start("saved-back");
        Check((await rig.Library("secret", 0, SecretLibrary))["ok"]!.GetValue<bool>(), "the library saves");
        await rig.Ok(new { operation = "save", id = "plug", expectedRevision = 0,
            definition = SecretPlugDefinition(new { key = "Host", value = "10.0.0.7" }, new { key = "Password", value = "class-hunter2" }) });
        await rig.Save("level", Level(Offset(4), Token("graph-hunter2")));
        // The definition members of an answer that a save takes, as the answer has them (its entities are for reading only):
        // a class's device, and a graph's device of null.
        static JsonObject Back(JsonObject answer) => new()
        {
            ["name"] = answer["name"]!.DeepClone(), ["device"] = answer["device"]?.DeepClone(), ["settings"] = answer["settings"]!.DeepClone(),
            ["logic"] = answer["logic"]!.DeepClone(),
        };
        async Task<JsonNode> SaveBack(JsonObject answer, Action<JsonObject>? change = null)
        {
            var definition = Back(answer);
            change?.Invoke(definition);
            return await rig.Request(new { operation = "save", id = answer["id"]!.GetValue<string>(), expectedRevision = answer["revision"]!.GetValue<long>(), definition });
        }
        string Settings(string id) => JsonNode.Parse(File.ReadAllText(StoredFile(rig, id)))!["settings"]!.ToJsonString();
        const string plugStored = """[{"key":"Host","value":"10.0.0.7"},{"key":"Password","value":"class-hunter2"}]""";

        var plugBack = await SaveBack(await rig.Read("plug"));
        var levelBack = await SaveBack(await rig.Read("level"));
        Check(plugBack["ok"]!.GetValue<bool>() && levelBack["ok"]!.GetValue<bool>(), "the answers save back: " + plugBack.ToJsonString() + " / " + levelBack.ToJsonString());
        Check(Settings("plug") == plugStored, "the class definition saved back keeps its values: " + Settings("plug"));
        Check(Settings("level") == """[{"key":"offset","value":4,"type":"number"},{"key":"token","value":"graph-hunter2","label":"Token","type":"string","secret":true}]""",
            "the graph definition saved back keeps its secret, and stores no hasValue: " + Settings("level"));

        // A kept secret stays secret, also when the entry sent no longer says so: by keep or by hasValue.
        var plain = new JsonObject { ["key"] = "token", ["type"] = "string", ["label"] = "Token", ["keep"] = true };
        await rig.Save("level", Level(Offset(4), plain), 2);
        Check(StoredSetting(rig, "level", "token")["secret"]?.GetValue<bool>() == true, "a secret kept by keep stays secret: " + Settings("level"));
        var answered = new JsonObject { ["key"] = "token", ["type"] = "string", ["label"] = "Token", ["value"] = null, ["hasValue"] = true };
        var stillSecret = await rig.Save("level", Level(Offset(4), answered), 3);
        Check(StoredSetting(rig, "level", "token").ToJsonString() == """{"key":"token","value":"graph-hunter2","label":"Token","type":"string","secret":true}"""
            && !stillSecret.ToJsonString().Contains("hunter2", StringComparison.Ordinal), "a kept secret stays secret: " + Settings("level") + " / " + stillSecret.ToJsonString());

        // A value given wins over hasValue; hasValue false with no value clears it.
        await SaveBack(await rig.Read("level"), d => d["settings"]![1]!["value"] = "new-hunter2");
        Check(StoredSetting(rig, "level", "token")["value"]!.GetValue<string>() == "new-hunter2", "a value given with hasValue replaces the stored one: " + Settings("level"));
        var cleared = await SaveBack(await rig.Read("level"), d => d["settings"]![1]!["hasValue"] = false);
        Check(cleared["ok"]!.GetValue<bool>() && StoredSetting(rig, "level", "token")["value"] == null && !Answered(cleared["result"]!["document"]!, "token")["hasValue"]!.GetValue<bool>(),
            "hasValue false with no value clears it: " + Settings("level"));

        // The libraries stop compiling (their file changed on disk): every setting of the class definition is masked, Host too.
        var libraryFile = Path.Combine(rig.Home.Root, ".storage", "hass_sharp", "libraries", "secret.json");
        var compiling = File.ReadAllText(libraryFile);
        var broken = JsonNode.Parse(compiling)!.AsObject();
        broken["source"] = SecretLibrary + "\npublic class Broken { int x = ; }";
        File.WriteAllText(libraryFile, broken.ToJsonString());
        var masked = await rig.Read("plug");
        Check(masked["settings"]!.ToJsonString() == """[{"key":"Host","value":null,"secret":true,"hasValue":true},{"key":"Password","value":null,"secret":true,"hasValue":true}]""",
            "with the libraries not compiling every setting is masked: " + masked["settings"]!.ToJsonString());
        var home = (await rig.Request(new { operation = "list", view = "home" }))["result"]!.ToJsonString();
        Check(!home.Contains("hunter2", StringComparison.Ordinal) && !home.Contains("10.0.0.7", StringComparison.Ordinal), "the home list has a masked value: " + home);
        // Saved back then, it is refused (the libraries have errors) and nothing is written.
        var refused = await SaveBack(masked);
        Check(refused["error"]?["code"]?.GetValue<string>() == "validation"
            && refused["error"]!["detail"]!["diagnostics"]!.AsArray().Any(d => d!["code"]!.GetValue<string>() == "definition.library")
            && Settings("plug") == plugStored && (await rig.Read("plug"))["revision"]!.GetValue<long>() == 2, "a save while the libraries do not compile: " + refused.ToJsonString());
        // Once they compile again, the answer read while they did not saves back with every value kept, Host's too.
        File.WriteAllText(libraryFile, compiling);
        var restored = await SaveBack(masked);
        Check(restored["ok"]!.GetValue<bool>() && Settings("plug") == plugStored && Answered(restored["result"]!["document"]!, "Host")["value"]!.GetValue<string>() == "10.0.0.7",
            "the masked answer saves back with its values kept: " + Settings("plug") + " / " + restored.ToJsonString());
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
        var relayDefinition = Relay(before: [Read("read", "sensor.gone")]);
        relayDefinition["settings"] = new JsonArray(Token("relay-hunter2"));
        await rig.Save("relay", relayDefinition);
        await rig.Save("motion", Motion());
        await rig.Until("relay:relay", _ => true, "the switch starts");
        await rig.Api.EntityCommand("relay:relay", "turn_off", null);
        await Throws<InvalidOperationException>(() => rig.Api.EntityCommand("relay:relay", "turn_on", null), "a failing flow");
        var today = DateTime.UtcNow.Date.ToString("O");
        var home = (await rig.Request(new { operation = "list", view = "home", todayUtc = today }))["result"]!.AsArray();
        Check(home.Select(d => d!["id"]!.GetValue<string>()).SequenceEqual(["motion", "relay"]), "the home list: " + home.ToJsonString());
        Check(!home.ToJsonString().Contains("hunter2", StringComparison.Ordinal)
            && home.Single(d => d!["id"]!.GetValue<string>() == "relay")!["settings"]![0]!["hasValue"]!.GetValue<bool>(), "the home list masks a secret: " + home.ToJsonString());
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

    /// <summary>The definition with a field of its first Entity node changed, which makes a new revision of it.</summary>
    static JsonObject Touched(JsonObject definition, string icon = "mdi:power")
    {
        var touched = definition.DeepClone().AsObject();
        touched["logic"]!["graph"]!["nodes"]!.AsArray().First(n => n!["kind"]!.GetValue<string>() == "entity")!["config"]!["icon"] = icon;
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
        graph["nodes"]!.AsArray().Add(Entity("s", "level", "sensor"));
        graph["edges"]!.AsArray().Add(Data("k", "h", "value"));
        graph["edges"]!.AsArray().Add(Data("h", "s", "value", "result"));
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
        var saved = await rig.Save("relay", Definition("Relay", Graph("Relay", [Entity("relay", "relay", "binary_sensor", new { name = "Relay" })])), 1);
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
        JsonObject Button(object? options = null) => Definition("Button", Graph("Button",
            [Entity("last", "last", "sensor", options == null ? new { name = "Last" } : new { name = "Last", options }),
             N("t", "trigger", new { type = "entity", entity = "sensor.button" }), SetState("set", "last", new JsonObject { ["value"] = Lit("string", "pressed") })], Next("t", "set")));
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
        graph["nodes"]!.AsArray().Add(N("gone", "set_entity_available", new JsonObject { ["entity"] = "relay", ["available"] = Lit("boolean", false) }));
        graph["edges"]!.AsArray().Add(Pin("relay", "command:turn_off", "gone"));
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

    /// <summary>
    /// While a new revision is prepared (here: Home Assistant has not yet answered its sync), the running one still takes
    /// commands: the state such a command sets is the new revision's first, not the one it was handed as it began.
    /// </summary>
    static async Task AValueSetWhileTheNewRevisionPreparesReachesIt()
    {
        await using var rig = await Rig.Start("carry-meanwhile");
        await rig.Save("relay", Relay());
        await rig.Api.EntityCommand("relay:relay", "turn_off", null);
        await rig.Until("relay:relay", s => Is(s, "is_on", false), "the switch is off");
        var first = await Meanwhile(rig, Touched(Relay()), async () =>
        {
            await rig.Api.EntityCommand("relay:relay", "turn_on", null);
            await rig.Until("relay:relay", s => Is(s, "is_on", true), "the running revision turns it on while the new one is prepared");
        });
        Check(first.Length > 0 && first.All(s => Available(s) && Is(s, "is_on", true)), "the new revision starts on: " + Lines(first.Select(s => s.ToJsonString())));
    }

    /// <summary>A value the new revision's When started flow set is its own: one the running revision sets meanwhile does not replace it.</summary>
    static async Task AValueTheNewRevisionSetsItselfWinsOverOneSetMeanwhile()
    {
        await using var rig = await Rig.Start("carry-own");
        var definition = Relay();
        var graph = definition["logic"]!["graph"]!.AsObject();
        graph["nodes"]!.AsArray().Add(N("start", "entity_setup"));
        graph["nodes"]!.AsArray().Add(SetState("first", "relay", On(false)));
        graph["edges"]!.AsArray().Add(Next("start", "first"));
        await rig.Save("relay", definition);
        await rig.Until("relay:relay", s => Is(s, "is_on", false), "the When started flow turns it off");
        var first = await Meanwhile(rig, Touched(definition), async () =>
        {
            await rig.Api.EntityCommand("relay:relay", "turn_on", null);
            await rig.Until("relay:relay", s => Is(s, "is_on", true), "the running revision turns it on while the new one is prepared");
        });
        Check(first.Length > 0 && first.All(s => Available(s) && Is(s, "is_on", false)), "the new revision starts with the state its flow set: " + Lines(first.Select(s => s.ToJsonString())));
    }

    /// <summary>
    /// Saves <paramref name="next"/> as relay's next revision, holding Home Assistant's answer to its sync while
    /// <paramref name="during"/> runs against the revision still running; then every push for the switch once the new
    /// revision has started.
    /// </summary>
    static async Task<JsonObject[]> Meanwhile(Rig rig, JsonObject next, Func<Task> during)
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = PyInterop.BeginSyncEntities;
        PyInterop.BeginSyncEntities = (json, ok, fail) => { held.TrySetResult(); _ = release.Task.ContinueWith(_ => sync(json, ok, fail), TaskScheduler.Default); };
        try
        {
            var saving = rig.Save("relay", next, 1);
            await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await during();
            await rig.Flush();
            var batches = rig.Batches;
            release.SetResult();
            var saved = await saving;
            Check(saved["active"]!.GetValue<bool>(), "the new revision runs: " + saved.ToJsonString());
            await rig.Started("relay");
            return rig.Pushed("relay:relay", batches);
        }
        finally
        {
            release.TrySetResult();
            PyInterop.BeginSyncEntities = sync;
        }
    }

    /// <summary>
    /// A sensor a State worked out, and left unavailable as its source failed, keeps its value through a revision that no
    /// longer works it out, but not that availability: no State of the new revision says it is unavailable.
    /// </summary>
    static async Task AnAvailabilityAStateWorkedOutIsNotCarriedOver()
    {
        await using var rig = await Rig.Start("carry-worked", ("sensor.outside", "20"));
        await rig.Save("outside", Copy());
        await rig.Until("outside:outside", s => Available(s) && Is(s, "value", 20.0), "the State works out the value");
        await rig.Change("sensor.outside", "warm");
        await rig.Until("outside:outside", s => !Available(s), "a source that is not a number leaves it unavailable");
        var batches = rig.Batches;
        await rig.Save("outside", Definition("Outside", Graph("Outside", [Entity("outside", "outside", "sensor", new { name = "Outside" })])), 1);
        await rig.Started("outside");
        var after = rig.Pushed("outside:outside", batches);
        Check(after.Length > 0 && after.All(s => Available(s) && Is(s, "value", 20.0)), "the new revision keeps the value, available: " + Lines(after.Select(s => s.ToJsonString())));
    }

    /// <summary>
    /// A dimmable light on at 120, saved as one that cannot be dimmed: after the swap it is still on, without a
    /// brightness, which the debug log says, and a plain turn on gives its flow no brightness.
    /// </summary>
    static async Task ALightThatCanNoLongerBeDimmedLosesItsBrightness()
    {
        await using var rig = await Rig.Start("carry-dimmable");
        await rig.Save("lamp", Lamp());
        await rig.Api.EntityCommand("lamp:lamp", "turn_on", """{"brightness":120}""");
        await rig.Until("lamp:lamp", s => Is(s, "is_on", true) && Is(s, "brightness", 120), "on at 120");
        var batches = rig.Batches;
        var plain = Definition("Lamp", Graph("Lamp", [Entity("lamp", "lamp", "light", new { name = "Lamp" }), SetState("bright", "lamp", On(true)), SetState("dark", "lamp", On(false))],
            Pin("lamp", "command:turn_on", "bright"), Pin("lamp", "command:turn_off", "dark")));
        var automationId = AutomationId(await rig.Save("lamp", plain, 1));
        await rig.Started("lamp");
        var after = rig.Pushed("lamp:lamp", batches);
        Check(after.Length > 0 && after.All(s => Available(s) && Is(s, "is_on", true) && Is(s, "brightness", null)), "on, without its brightness: " + Lines(after.Select(s => s.ToJsonString())));
        Check(rig.Home.Logs(PyLogLevel.Debug).Any(l => l == "Lamp: The brightness 120 of Lamp (lamp) is given, and a light that cannot be dimmed has none; it is left out of the state carried over from the running revision"),
            "the value left out is logged: " + Lines(rig.Home.Logs(PyLogLevel.Debug)));
        await rig.Api.EntityCommand("lamp:lamp", "turn_on", null);
        var trace = JsonNode.Parse(rig.Api.GetRunTrace(CommandRuns(rig, automationId)[0]["runId"]!.GetValue<string>()))!;
        Check(NoBrightness(Step(trace, "lamp")), "the flow got a brightness: " + Step(trace, "lamp").ToJsonString());
    }

    /// <summary>A select whose option the new revision no longer lists starts unknown after the swap, and runs.</summary>
    static async Task ASelectWhoseOptionWasRemovedStartsUnknown()
    {
        await using var rig = await Rig.Start("carry-option");
        await rig.Save("fan", Fan("low", "high"));
        await rig.Api.EntityCommand("fan:speed", "select_option", """{"option":"high"}""");
        await rig.Until("fan:speed", s => Is(s, "option", "high"), "high");
        var batches = rig.Batches;
        var saved = await rig.Save("fan", Fan("low", "medium"), 1);
        Check(saved["active"]!.GetValue<bool>() && Status(saved["document"]!.AsObject()) == "running", "the new revision runs: " + saved.ToJsonString());
        await rig.Started("fan");
        var after = rig.Pushed("fan:speed", batches);
        Check(after.Length > 0 && after.All(s => Available(s) && Is(s, "option", null)), "unknown after the swap: " + Lines(after.Select(s => s.ToJsonString())));
    }

    /// <summary>A fan's speed: a select whose option is the one chosen.</summary>
    static JsonObject Fan(params string[] options) => Definition("Fan", Graph("Fan", [Entity("speed", "speed", "select", new { name = "Speed", options = new { options } }), SetState("show", "speed", new JsonObject())],
        Pin("speed", "command:select_option", "show"), Data("speed", "show", "option", "command:select_option:option")));

    /// <summary>A number its set_value flow sets to the value given, from 0 to 100.</summary>
    static JsonObject Dial() => Definition("Dial", Graph("Dial", [Entity("dial", "dial", "number", new { name = "Dial", options = new { min = 0, max = 100 } }), SetState("show", "dial", new JsonObject())],
        Pin("dial", "command:set_value", "show"), Data("dial", "show", "value", "command:set_value:value")));

    // ---- An entity comes back with the state it last showed ----

    /// <summary>A graph's switch a command turned on comes back on after a restart: its first push is on, before any command.</summary>
    static async Task AGraphSwitchComesBackOn()
    {
        await using var rig = await Rig.Start("restore-switch");
        await rig.Save("relay", Relay());
        await rig.Api.EntityCommand("relay:relay", "turn_on", null);
        await rig.Until("relay:relay", s => Is(s, "is_on", true), "the switch is on");
        await rig.Flush();
        var batches = rig.Batches;
        await rig.Restart();
        await rig.Started("relay");
        var after = rig.Pushed("relay:relay", batches);
        Check(after.Length > 0 && after.All(s => Available(s) && Is(s, "is_on", true)), "the switch comes back on: " + Lines(after.Select(s => s.ToJsonString())));
        await rig.Api.EntityCommand("relay:relay", "turn_off", null);
        await rig.Until("relay:relay", s => Is(s, "is_on", false), "it takes commands", batches);
    }

    /// <summary>A number set by a command comes back with that value after a restart: its first push, before any command.</summary>
    static async Task ANumberSurvivesARestart()
    {
        await using var rig = await Rig.Start("restore-number");
        await rig.Save("dial", Dial());
        await rig.Api.EntityCommand("dial:dial", "set_value", """{"value":42}""");
        await rig.Until("dial:dial", s => Is(s, "value", 42), "the number is 42");
        await rig.Flush();
        var batches = rig.Batches;
        await rig.Restart();
        await rig.Started("dial");
        var after = rig.Pushed("dial:dial", batches);
        Check(after.Length > 0 && Available(after[0]) && Is(after[0], "value", 42), "the number comes back at 42: " + Lines(after.Select(s => s.ToJsonString())));
    }

    /// <summary>A light set to 120 comes back with it after a restart, and a plain turn on then reads 120, not full.</summary>
    static async Task ALightComesBackWithItsBrightness()
    {
        await using var rig = await Rig.Start("restore-light");
        await rig.Save("lamp", Lamp());
        await rig.Api.EntityCommand("lamp:lamp", "turn_on", """{"brightness":120}""");
        await rig.Api.EntityCommand("lamp:lamp", "turn_off", null);
        await rig.Until("lamp:lamp", s => Is(s, "is_on", false) && Is(s, "brightness", 120), "off, at 120");
        await rig.Flush();
        var batches = rig.Batches;
        await rig.Restart();
        await rig.Started("lamp");
        var after = rig.Pushed("lamp:lamp", batches);
        Check(after.Length > 0 && after.All(s => Available(s) && Is(s, "is_on", false) && Is(s, "brightness", 120)), "the light comes back off at 120: " + Lines(after.Select(s => s.ToJsonString())));
        var automationId = (await rig.Read("lamp"))["automationId"]!.GetValue<string>();
        await rig.Api.EntityCommand("lamp:lamp", "turn_on", null);
        await rig.Until("lamp:lamp", s => Is(s, "is_on", true) && Is(s, "brightness", 120), "on at its brightness", batches);
        Check(Clear(ReadBrightness(rig, automationId), 120), "the flow read the restored brightness: " + ReadBrightness(rig, automationId)?.ToJsonString());
    }

    /// <summary>The state the When started flow sets is the entity's first after a restart, not the one it comes back with.</summary>
    static async Task WhenStartedOverridesARestoredState()
    {
        await using var rig = await Rig.Start("restore-setup");
        var definition = Relay();
        var graph = definition["logic"]!["graph"]!.AsObject();
        graph["nodes"]!.AsArray().Add(N("start", "entity_setup"));
        graph["nodes"]!.AsArray().Add(SetState("first", "relay", On(false)));
        graph["edges"]!.AsArray().Add(Next("start", "first"));
        await rig.Save("relay", definition);
        await rig.Api.EntityCommand("relay:relay", "turn_on", null);
        await rig.Until("relay:relay", s => Is(s, "is_on", true), "the switch is on");
        await rig.Flush();
        var batches = rig.Batches;
        await rig.Restart();
        await rig.Started("relay");
        var after = rig.Pushed("relay:relay", batches);
        Check(after.Length > 0 && after.All(s => Available(s) && Is(s, "is_on", false)), "the When started flow's state comes first: " + Lines(after.Select(s => s.ToJsonString())));
    }

    /// <summary>
    /// A select whose stored option the definition no longer lists (changed while it was off) starts unknown, which the
    /// debug log says, and the definition runs.
    /// </summary>
    static async Task ARefusedStoredValueIsDropped()
    {
        await using var rig = await Rig.Start("restore-refused");
        await rig.Save("fan", Fan("low", "high"));
        await rig.Api.EntityCommand("fan:speed", "select_option", """{"option":"high"}""");
        await rig.Until("fan:speed", s => Is(s, "option", "high"), "high");
        await rig.Ok(new { operation = "set_enabled", id = "fan", enabled = false });
        await rig.Save("fan", Fan("low", "medium"), 1);
        await rig.Flush();
        var batches = rig.Batches;
        var on = await rig.Ok(new { operation = "set_enabled", id = "fan", enabled = true });
        Check(on["active"]!.GetValue<bool>() && on["activationError"] == null, "the definition runs: " + on.ToJsonString());
        await rig.Started("fan");
        var after = rig.Pushed("fan:speed", batches);
        Check(after.Length > 0 && after.All(s => Available(s) && Is(s, "option", null)), "the select starts unknown: " + Lines(after.Select(s => s.ToJsonString())));
        Check(rig.Home.Logs(PyLogLevel.Debug).Any(l => l == "Fan: The option \"high\" of Speed (speed) is not one of low or medium; it is left out of its last state"),
            "the value left out is logged: " + Lines(rig.Home.Logs(PyLogLevel.Debug)));
        await rig.Api.EntityCommand("fan:speed", "select_option", """{"option":"medium"}""");
        await rig.Until("fan:speed", s => Is(s, "option", "medium"), "it takes commands", batches);
    }

    /// <summary>
    /// An entity a State works out is neither remembered nor brought back: its State works it out again as it starts (a
    /// value stored for it is forgotten).
    /// </summary>
    static async Task AStateStepsEntityIsNotRestored()
    {
        await using var rig = await Rig.Start("restore-state", ("sensor.outside", "20"));
        await rig.Save("outside", Copy());
        await rig.Until("outside:outside", s => Is(s, "value", 20.0), "the State works out the value");
        var states = rig.Scripts.States!;
        Check(!states.Ids().Contains("outside:outside"), "a State's entity was remembered");
        states.Remember("outside:outside", new JsonObject { ["value"] = 99.0 });
        await states.FlushAsync();
        rig.States["sensor.outside"] = "21";
        var batches = rig.Batches;
        await rig.Restart();
        await rig.Started("outside");
        await rig.Until("outside:outside", s => Is(s, "value", 21.0), "the State works it out again", batches);
        var after = rig.Pushed("outside:outside", batches);
        Check(after.All(s => Available(s) && Is(s, "value", 21.0)), "it starts with what its State works out: " + Lines(after.Select(s => s.ToJsonString())));
        Check(!rig.Scripts.States!.Ids().Contains("outside:outside"), "the value stored for it is kept");
    }

    /// <summary>Deleting a definition forgets its entities' last states: one saved later under its id starts unknown.</summary>
    static async Task DeleteForgetsTheLastStates()
    {
        await using var rig = await Rig.Start("restore-delete");
        await rig.Save("relay", Relay());
        await rig.Api.EntityCommand("relay:relay", "turn_on", null);
        await rig.Until("relay:relay", s => Is(s, "is_on", true), "the switch is on");
        Check(rig.Scripts.States!.Ids().Contains("relay:relay"), "the switch is remembered");
        await rig.Ok(new { operation = "delete", id = "relay", expectedRevision = 1 });
        Check(!rig.Scripts.States!.Ids().Any(id => id.StartsWith("relay:", StringComparison.Ordinal)), "the deleted definition's states are kept: " + string.Join(", ", rig.Scripts.States!.Ids()));
        var batches = rig.Batches;
        await rig.Save("relay", Relay());
        await rig.Started("relay");
        var after = rig.Pushed("relay:relay", batches);
        Check(after.Length > 0 && after.All(s => Is(s, "is_on", null)), "a definition saved again under the id starts unknown: " + Lines(after.Select(s => s.ToJsonString())));
    }

    /// <summary>An entity a new revision no longer has is forgotten: added back later, it starts unknown.</summary>
    static async Task AnEntityLeavingTheDefinitionIsForgotten()
    {
        await using var rig = await Rig.Start("restore-left");
        JsonObject Pair(bool spare)
        {
            var definition = Relay();
            if (spare)
            {
                var graph = definition["logic"]!["graph"]!.AsObject();
                graph["nodes"]!.AsArray().Add(Entity("spare", "spare", "switch", new { name = "Spare" }));
                graph["nodes"]!.AsArray().Add(SetState("spare_set", "spare", On(true)));
                graph["edges"]!.AsArray().Add(Pin("spare", "command:turn_on", "spare_set"));
            }
            return definition;
        }
        await rig.Save("relay", Pair(spare: true));
        await rig.Api.EntityCommand("relay:spare", "turn_on", null);
        await rig.Until("relay:spare", s => Is(s, "is_on", true), "the spare is on");
        await rig.Save("relay", Pair(spare: false), 1);
        await rig.Started("relay");
        Check(!rig.Scripts.States!.Ids().Contains("relay:spare") && rig.Scripts.States!.Ids().Contains("relay:relay"),
            "the entity that left is forgotten, the other kept: " + string.Join(", ", rig.Scripts.States!.Ids()));
        var batches = rig.Batches;
        await rig.Save("relay", Pair(spare: true), 2);
        await rig.Started("relay");
        var after = rig.Pushed("relay:spare", batches);
        Check(after.Length > 0 && after.All(s => Is(s, "is_on", null)), "the entity added back starts unknown: " + Lines(after.Select(s => s.ToJsonString())));
    }

    /// <summary>
    /// What an entity shows while it is unavailable is not remembered: its last remembered values stay, and it comes back
    /// with them after a restart.
    /// </summary>
    static async Task AnUnavailableEntityIsNotRemembered()
    {
        await using var rig = await Rig.Start("restore-unavailable");
        var definition = Relay(turnOff: false);
        var graph = definition["logic"]!["graph"]!.AsObject();
        graph["nodes"]!.AsArray().Add(N("gone", "set_entity_available", new JsonObject { ["entity"] = "relay", ["available"] = Lit("boolean", false) }));
        graph["nodes"]!.AsArray().Add(SetState("set_off", "relay", On(false)));
        graph["edges"]!.AsArray().Add(Pin("relay", "command:turn_off", "gone"));
        graph["edges"]!.AsArray().Add(Next("gone", "set_off"));
        await rig.Save("relay", definition);
        await rig.Api.EntityCommand("relay:relay", "turn_on", null);
        await rig.Until("relay:relay", s => Available(s) && Is(s, "is_on", true), "on");
        await rig.Api.EntityCommand("relay:relay", "turn_off", null);
        await rig.Until("relay:relay", s => !Available(s) && Is(s, "is_on", false), "unavailable, then off");
        Check(rig.Scripts.States!.TryGet("relay:relay", out var values) && values.ToJsonString() == """{"is_on":true}""", "what it showed unavailable was remembered: " + values.ToJsonString());
        await rig.Flush();
        var batches = rig.Batches;
        await rig.Restart();
        await rig.Started("relay");
        var after = rig.Pushed("relay:relay", batches);
        Check(after.Length > 0 && after.All(s => Available(s) && Is(s, "is_on", true)), "it comes back with its last remembered values, available: " + Lines(after.Select(s => s.ToJsonString())));
    }

    /// <summary>
    /// A switch saved, while its definition is off, as a binary sensor under the same key is another entity: turned on
    /// again, it starts unknown, not with the switch's last state.
    /// </summary>
    static async Task AnEntityGivenAnotherDomainDoesNotComeBackAsTheOther()
    {
        await using var rig = await Rig.Start("restore-domain");
        await rig.Save("relay", Relay());
        await rig.Api.EntityCommand("relay:relay", "turn_on", null);
        await rig.Until("relay:relay", s => Is(s, "is_on", true), "the switch is on");
        await rig.Ok(new { operation = "set_enabled", id = "relay", enabled = false });
        await rig.Save("relay", Definition("Relay", Graph("Relay", [Entity("relay", "relay", "binary_sensor", new { name = "Relay" })])), 1);
        Check(!rig.Scripts.States!.Ids().Contains("relay:relay"), "the switch's state is kept for the binary sensor");
        await rig.Flush();
        var batches = rig.Batches;
        await rig.Ok(new { operation = "set_enabled", id = "relay", enabled = true });
        await rig.Started("relay");
        var after = rig.Pushed("relay:relay", batches);
        Check(after.Length > 0 && after.All(s => Available(s) && Is(s, "is_on", null)), "the binary sensor starts unknown: " + Lines(after.Select(s => s.ToJsonString())));
    }

    // ---- An entity on a Device node runs as one without the wire ----

    /// <summary>
    /// The definition with a Device node <c>plug</c> (Hall plug) and, unless <paramref name="wired"/> is false, a wire from
    /// its Entities pin to the Device pin of the Entity node <paramref name="hub"/>, whose config then carries the pin's
    /// marker as the editor saves it (<c>arguments.device</c> unset, beside the wire).
    /// </summary>
    static JsonObject OnADevice(JsonObject definition, string hub, bool wired = true)
    {
        var graph = definition["logic"]!["graph"]!.AsObject();
        graph["nodes"]!.AsArray().Insert(0, N("plug", "device", new { key = "plug", name = "Hall plug" }));
        if (!wired) return definition;
        graph["edges"]!.AsArray().Add(Data("plug", hub, "device", "entities"));
        var config = graph["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == hub)!["config"]!.AsObject();
        if (config["arguments"] is not JsonObject arguments) config["arguments"] = arguments = [];
        arguments["device"] = JsonSerializer.SerializeToNode(Unset);
        return definition;
    }
    /// <summary>The flows of the definition's graph, as the host runs them (<see cref="DefinitionGraph.Roots"/>).</summary>
    static DefinitionRoots Roots(JsonObject definition) => DefinitionGraph.Roots(definition["logic"]!["graph"]!.AsObject(), VisualCatalogSnapshot.BuiltIn);

    /// <summary>
    /// A switch on a Device node whose state its Turn on flow pushes saves without a problem, and its Device wire gives it
    /// no state flow: it comes back on after a restart, as one without the wire does.
    /// </summary>
    static async Task AnEntityOnADeviceKeepsItsPushedState()
    {
        await using var rig = await Rig.Start("device-restore");
        var definition = OnADevice(Relay(), "relay");
        var roots = Roots(definition);
        Check(roots.States.Count == 0 && !roots.StateEntities.Values.Contains("relay"), "the Device wire gives the switch no state flow: " + string.Join(", ", roots.States));
        var saved = await rig.Save("relay", definition);
        Check(saved["active"]!.GetValue<bool>() && !saved.ContainsKey("diagnostics"), "the switch on a device saves without a problem and runs: " + saved.ToJsonString());
        var stored = NodeOf((await rig.Read("relay")), "relay")["config"]!["arguments"]!["device"];
        Check(JsonNode.DeepEquals(stored, JsonSerializer.SerializeToNode(Unset)), "the Device pin's marker is kept: " + stored?.ToJsonString());
        await rig.Api.EntityCommand("relay:relay", "turn_on", null);
        await rig.Until("relay:relay", s => Is(s, "is_on", true), "the switch is on");
        await rig.Flush();
        var batches = rig.Batches;
        await rig.Restart();
        await rig.Started("relay");
        var after = rig.Pushed("relay:relay", batches);
        Check(after.Length > 0 && after.All(s => Available(s) && Is(s, "is_on", true)), "the switch on a device comes back on: " + Lines(after.Select(s => s.ToJsonString())));
        await rig.Api.EntityCommand("relay:relay", "turn_off", null);
        await rig.Until("relay:relay", s => Is(s, "is_on", false), "it takes commands", batches);
    }

    /// <summary>
    /// A binary sensor on a Device node whose On is wired from a value works out its state from that value, and again
    /// when the entity the value reads changes; its Device wire is not read.
    /// </summary>
    static async Task AnEntityOnADeviceWorksOutItsState()
    {
        await using var rig = await Rig.Start("device-state", ("binary_sensor.motion", "on"));
        var graph = Graph("Motion", [Entity("s", "motion", "binary_sensor", new { name = "Motion" }),
            N("is", "entity_is", new JsonObject { ["entity"] = Lit("entity_id", "binary_sensor.motion"), ["state"] = Lit("string", "on") })], Data("is", "s", "is_on"));
        var definition = OnADevice(Definition("Motion", graph), "s");
        var states = Roots(definition).StateEntities.Values;
        Check(states.SequenceEqual(["motion"]), "the wired On gives it its state flow, and the Device wire adds none: " + string.Join(", ", states));
        var saved = await rig.Save("motion", definition);
        Check(saved["active"]!.GetValue<bool>() && !saved.ContainsKey("diagnostics"), "the sensor on a device saves without a problem and runs: " + saved.ToJsonString());
        await rig.Until("motion:motion", s => Available(s) && Is(s, "is_on", true), "the state flow sets On");
        var batches = rig.Batches;
        await rig.Change("binary_sensor.motion", "off");
        await rig.Until("motion:motion", s => Available(s) && Is(s, "is_on", false), "the state flow follows its source", batches);
        Check(rig.Home.Logs(PyLogLevel.Warn).Length == 0 && rig.Home.Logs(PyLogLevel.Error).Length == 0, "the state flow failed: " + Lines(rig.Home.Logs()));
    }

    /// <summary>
    /// A Device node with nothing wired to it saves and starts, and the definition's other flows run; the generated source
    /// has a case for the Custom C# step its flow runs and none for the Device node, which is never run.
    /// </summary>
    static async Task ADeviceNodeIsNeverRun()
    {
        await using var rig = await Rig.Start("device-never-run");
        const string note = "public static class CustomNode\n{\n    [Node(\"Note\")]\n    public static void Run(NodeContext context) { }\n}";
        var definition = OnADevice(Relay(before: [Custom("note", note)]), "relay", wired: false);
        definition["logic"]!["graph"] = Derived(definition["logic"]!["graph"]!.AsObject());
        var saved = await rig.Save("relay", definition);
        Check(saved["active"]!.GetValue<bool>(), "the definition with a Device node runs: " + saved.ToJsonString());
        var source = saved["document"]!["source"]!.GetValue<string>();
        Check(source.Contains("case \"note\":", StringComparison.Ordinal), "the Custom C# step has its case");
        Check(!source.Contains("case \"plug\":", StringComparison.Ordinal), "the generated source has a case for the Device node");
        await rig.Until("relay:relay", s => Available(s), "the switch starts");
        await rig.Api.EntityCommand("relay:relay", "turn_on", null);
        await rig.Until("relay:relay", s => Is(s, "is_on", true), "its Turn on flow runs");
        var automationId = saved["document"]!["automationId"]!.GetValue<string>();
        await EntityTestHome.Until(() => rig.History(automationId).Any(r => r["cause"]!.GetValue<string>() == "Command" && r["status"]!.GetValue<string>() == "Succeeded"),
            "the command's run succeeded: " + Lines(rig.History(automationId).Select(r => r.ToJsonString())));
        Check(rig.Home.Logs(PyLogLevel.Warn).Length == 0 && rig.Home.Logs(PyLogLevel.Error).Length == 0, "the definition logged a failure: " + Lines(rig.Home.Logs()));
    }

    /// <summary>
    /// A switch on a Device node a command turned on stays on through a save of a new revision, as one without the wire
    /// does: its pushed state is handed over, every push after the save is on, and its commands reach the new revision.
    /// </summary>
    static async Task AnEntityOnADeviceIsHandedOverToANewRevision()
    {
        await using var rig = await Rig.Start("device-carry");
        var definition = OnADevice(Relay(), "relay");
        await rig.Save("relay", definition);
        await rig.Until("relay:relay", s => Available(s) && Is(s, "is_on", null), "the switch starts unknown");
        await rig.Api.EntityCommand("relay:relay", "turn_on", null);
        await rig.Until("relay:relay", s => Is(s, "is_on", true), "the switch is on");
        var batches = rig.Batches;
        var saved = await rig.Save("relay", Touched(definition), 1);
        Check(saved["active"]!.GetValue<bool>() && rig.Home.Syncs("relay").Length == 2, "the new revision runs: " + saved.ToJsonString());
        await rig.Started("relay");
        var after = rig.Pushed("relay:relay", batches);
        Check(after.Length > 0 && after.All(s => Available(s) && Is(s, "is_on", true)), "every push after the save is on: " + Lines(after.Select(s => s.ToJsonString())));
        await rig.Api.EntityCommand("relay:relay", "turn_off", null);
        await rig.Until("relay:relay", s => Is(s, "is_on", false), "the new revision takes the command", batches);
    }

    // ---- How a definition's flows end ----

    /// <summary>A command whose flow ends at a Stop step has succeeded: the state the flow set before it is pushed.</summary>
    static async Task ACommandFlowThatEndsAtAStopStepSucceeds()
    {
        await using var rig = await Rig.Start("stop-command");
        var graph = Graph("Relay", [Entity("relay", "relay", "switch", new { name = "Relay" }), SetState("set_on", "relay", On(true)), N("halt", "stop")], Pin("relay", "command:turn_on", "set_on"), Next("set_on", "halt"));
        var saved = await rig.Save("relay", Definition("Relay", graph));
        await rig.Until("relay:relay", _ => true, "the switch starts");
        await rig.Api.EntityCommand("relay:relay", "turn_on", null);
        await rig.Until("relay:relay", s => Is(s, "is_on", true), "the state set before the Stop step");
        var runs = rig.History(saved["document"]!["automationId"]!.GetValue<string>());
        Check(runs.Any(r => r["cause"]!.GetValue<string>() == "Command" && r["status"]!.GetValue<string>() == "Stopped"), "the run is recorded as stopped: " + Lines(runs.Select(r => r.ToJsonString())));
        Check(rig.Home.Logs(PyLogLevel.Warn).Length == 0 && rig.Home.Logs(PyLogLevel.Error).Length == 0, "a flow that ended at a Stop step was logged: " + Lines(rig.Home.Logs()));
    }

    /// <summary>
    /// A When started flow that fails logs only the definition's cannot-start warning, which, as its error does, names the
    /// step that failed and its cause, and the whole failure at Debug; a trigger's flow in a definition that fails logs one
    /// error naming the definition and the flow, with no stack, and the whole failure at Debug; the runner's line for an
    /// automation is not written.
    /// </summary>
    static async Task FailingSetupAndTriggerFlowsAreLoggedByName()
    {
        await using var rig = await Rig.Start("flow-failures", ("sensor.button", "idle"));
        var setup = Graph("Level", [Entity("level", "level", "sensor"), N("start", "entity_setup"), Read("read", "sensor.gone")], Next("start", "read"));
        var failing = await rig.Save("level", Definition("Level", setup));
        Check(failing["activationError"]?.GetValue<string>() == "Entity 'sensor.gone' is unavailable.", "the save answers the flow's error: " + failing.ToJsonString());
        // The cannot-start warning says it, once: the failing When started flow is not logged at error too.
        Check(rig.Home.Logs(PyLogLevel.Error).Length == 0, "a failing When started flow is logged at error: " + Lines(rig.Home.Logs(PyLogLevel.Error)));
        Check(rig.Home.Logs(PyLogLevel.Warn).SequenceEqual(["Entity definition \"Level\" (level) cannot start: Entity 'sensor.gone' is unavailable."]),
            "one warning names the definition and why: " + Lines(rig.Home.Logs(PyLogLevel.Warn)));
        Check(rig.Home.Logs(PyLogLevel.Debug).Any(l => l.StartsWith("Level: When started failed: ", StringComparison.Ordinal) && l.Contains("   at ", StringComparison.Ordinal)), "the whole failure at Debug: " + Lines(rig.Home.Logs(PyLogLevel.Debug)));

        // A step that fails over a cause of its own: the reason names the step, then the cause, in the answer and the warning alike.
        var converting = Graph("Count", [Entity("count", "count", "sensor"), N("start", "entity_setup"), N("conv", "convert", new { type = "number", value = Lit("string", "warm") }), SetState("set", "count", new JsonObject())],
            Next("start", "set"), Data("conv", "set", "value"));
        var warnings = rig.Home.Logs(PyLogLevel.Warn).Length;
        var named = await rig.Save("count", Definition("Count", converting));
        var reason = named["activationError"]?.GetValue<string>() ?? "";
        Check(reason == "Convert (used by Set state): Cannot convert conv.value to System.Double. (The input string 'warm' was not in a correct format.)",
            "the save answers the step that failed and why: " + named.ToJsonString());
        Check(rig.Home.Logs(PyLogLevel.Warn).Skip(warnings).SequenceEqual([$"Entity definition \"Count\" (count) cannot start: {reason}"]) && rig.Home.Logs(PyLogLevel.Error).Length == 0,
            "one warning names the step: " + Lines(rig.Home.Logs(PyLogLevel.Warn).Skip(warnings)));

        var trigger = N("t", "trigger", new { type = "entity", entity = "sensor.button" });
        trigger["label"] = "Button pressed";
        var unlabelled = N("u", "trigger", new { type = "entity", entity = "sensor.button" });
        var triggers = Graph("Button", [Entity("last", "last", "sensor"), trigger, Read("read", "sensor.gone"), unlabelled, Read("again", "sensor.gone")], Next("t", "read"), Next("u", "again"));
        await rig.Save("button", Definition("Button", triggers));
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

    /// <summary>
    /// A trigger flow that keeps failing (a poll of a device that does not answer) logs one error per new reason, naming
    /// the definition and the trigger, and each failure whole at Debug; once it works again, one line says so.
    /// </summary>
    static async Task AFailingPollLogsOncePerReason()
    {
        await using var rig = await Rig.Start("poll", ("sensor.tick", "0"), ("sensor.reading", "unavailable"));
        var trigger = N("t", "trigger", new { type = "entity", entity = "sensor.tick" });
        trigger["label"] = "Poll";
        var graph = Graph("Level", [Entity("level", "level", "sensor"), trigger, N("cur", "entity_current", new JsonObject { ["entity"] = Lit("entity_id", "sensor.reading") }),
            N("f", "entity_field", new { entity = Unset, field = "state", type = "number" }), SetState("set", "level", new JsonObject())],
            Next("t", "set"), Data("cur", "f", "entity"), Data("f", "set", "value"));
        await rig.Save("level", Definition("Level", graph));
        string[] Errors() => rig.Home.Logs(PyLogLevel.Error).Where(l => l.StartsWith("Level: ", StringComparison.Ordinal)).ToArray();
        var tick = 0;
        Task Poll() => rig.Change("sensor.tick", (++tick).ToString(System.Globalization.CultureInfo.InvariantCulture));
        for (var i = 0; i < 3; i++) await Poll();
        Check(Errors().Length == 1 && Errors()[0].StartsWith("Level: Poll failed: ", StringComparison.Ordinal) && Errors()[0].Contains("'unavailable'", StringComparison.Ordinal)
            && !Errors()[0].Contains("   at ", StringComparison.Ordinal), "three failures, one error naming the definition and the trigger: " + Lines(rig.Home.Logs()));
        Check(rig.Home.Logs(PyLogLevel.Debug).Count(l => l.StartsWith("Level: Poll failed: ", StringComparison.Ordinal) && l.Contains("   at ", StringComparison.Ordinal)) == 3,
            "each failure whole at Debug: " + Lines(rig.Home.Logs(PyLogLevel.Debug)));
        rig.States["sensor.reading"] = "unknown";
        await Poll();
        await Poll();
        Check(Errors().Length == 2 && Errors()[1].StartsWith("Level: Poll failed: ", StringComparison.Ordinal) && Errors()[1].Contains("'unknown'", StringComparison.Ordinal),
            "a second reason, a second error: " + Lines(Errors()));
        rig.States["sensor.reading"] = "21";
        var batches = rig.Batches;
        await Poll();
        await Poll();
        await rig.Flush();
        Check(rig.Pushed("level:level", batches).LastOrDefault() is { } set && Is(set, "value", 21.0), "the poll set the state: " + Lines(rig.Home.Pushes().Skip(batches)));
        Check(rig.Home.Logs(PyLogLevel.Info).Where(l => l.StartsWith("Level: ", StringComparison.Ordinal)).SequenceEqual(["Level: Poll works again"]),
            "one line says it works again: " + Lines(rig.Home.Logs(PyLogLevel.Info)));
        rig.States["sensor.reading"] = "unavailable";
        await Poll();
        Check(Errors().Length == 3 && rig.Home.Logs(PyLogLevel.Warn).Length == 0, "a failure after it worked again is an error again, and nothing warns: " + Lines(rig.Home.Logs()));
    }

    /// <summary>
    /// A State whose run has not finished after <see cref="DefinitionHost.SlowStateWarning"/> warns once, naming the
    /// definition and the entity; when it then finishes, Debug says so; a State that finishes in time says nothing.
    /// </summary>
    static async Task ASlowStateWarnsOnce()
    {
        await using var rig = await Rig.Start("slow", ("sensor.level", "0"));
        DefinitionHost.SlowStateWarning = TimeSpan.FromMilliseconds(100);
        var graph = Derived(Graph("Slow",
            [Entity("s", "quick", "sensor", new { name = "Quick" }), N("cur", "entity_current", new JsonObject { ["entity"] = Lit("entity_id", "sensor.level") }), N("f", "entity_field", new { entity = Unset, field = "state", type = "number" }), Custom("slow", SlowValue)],
            Data("cur", "f", "entity"), Data("f", "slow", "value"), Data("slow", "s", "value", "result")));
        await rig.Save("slow", Definition("Slow", graph));
        var (gate, entered) = (rig.Static<SemaphoreSlim>("slow", "Gate"), rig.Static<SemaphoreSlim>("slow", "Entered"));
        await Reached(entered, "the startup run reaches the value node");
        string[] Warnings() => rig.Home.Logs(PyLogLevel.Warn).Where(l => l.StartsWith("Slow: ", StringComparison.Ordinal)).ToArray();
        string[] Late() => rig.Home.Logs(PyLogLevel.Debug).Where(l => l.StartsWith("Slow: the State of Quick (quick) finished after ", StringComparison.Ordinal)).ToArray();
        await EntityTestHome.Until(() => Warnings().Length == 1, "the warning: " + Lines(rig.Home.Logs()));
        await Task.Delay(300);
        Check(Warnings().SequenceEqual(["Slow: the State of Quick (quick) has not finished after 0.1 s"]) && Late().Length == 0, "one warning names the entity: " + Lines(rig.Home.Logs()));
        gate.Release();
        await rig.Until("slow:quick", s => Available(s) && Is(s, "value", 0.0), "the late state");
        await EntityTestHome.Until(() => Late().Length == 1, "the late finish at Debug: " + Lines(rig.Home.Logs(PyLogLevel.Debug)));
        Check(Warnings().Length == 1, "the late finish warned: " + Lines(Warnings()));
        // In time: nothing.
        gate.Release();
        var batches = rig.Batches;
        await rig.Change("sensor.level", "1");
        await rig.Until("slow:quick", s => Available(s) && Is(s, "value", 1.0), "the quick state", batches);
        await Task.Delay(300);
        Check(Warnings().Length == 1 && Late().Length == 1, "a State that finished in time logged: " + Lines(rig.Home.Logs()));
    }

    /// <summary>
    /// A definition that is on and cannot start answers <c>failed</c> with why, read or saved: a graph whose When started
    /// flow fails, a class whose <c>Setup()</c> throws. A save that meets it warns once, naming the definition and why;
    /// a save for the same reason again does not; a library save that meets it for another reason does.
    /// </summary>
    static async Task ADefinitionThatCannotStartIsFailed()
    {
        await using var rig = await Rig.Start("cannot-start");
        string[] Warnings() => rig.Home.Logs(PyLogLevel.Warn).Where(l => l.StartsWith("Entity definition ", StringComparison.Ordinal)).ToArray();
        var setup = Definition("Level", Graph("Level", [Entity("level", "level", "sensor"), N("start", "entity_setup"), Read("read", "sensor.gone")], Next("start", "read")));
        var saved = await rig.Save("level", setup);
        Check(!saved["active"]!.GetValue<bool>() && saved["activationError"]?.GetValue<string>() == "Entity 'sensor.gone' is unavailable."
            && Status(saved["document"]!.AsObject()) == "failed" && saved["document"]!["activationError"]?.GetValue<string>() == "Entity 'sensor.gone' is unavailable.",
            "a graph whose When started flow fails is failed: " + saved.ToJsonString());
        var read = await rig.Read("level");
        Check(Status(read) == "failed" && read["activationError"]?.GetValue<string>() == "Entity 'sensor.gone' is unavailable." && read["liveEntities"]!.AsArray().Count == 0,
            "it reads failed: " + read.ToJsonString());
        Check(Warnings().SequenceEqual(["Entity definition \"Level\" (level) cannot start: Entity 'sensor.gone' is unavailable."]), "one warning names it and why: " + Lines(rig.Home.Logs()));
        await rig.Save("level", setup, 1);
        Check(Warnings().Length == 1, "a save for the same reason warned again: " + Lines(Warnings()));

        Check((await rig.Library("shelly", 0, EntityApiTests.Library))["ok"]!.GetValue<bool>(), "the class library saves");
        var broken = await rig.Ok(new { operation = "save", id = "broken", expectedRevision = 0, definition = EntityApiTests.Definition("Broken plug", null, "library:shelly:NodeLib.BrokenPlug") });
        Check(!broken["active"]!.GetValue<bool>() && Status(broken["document"]!.AsObject()) == "failed"
            && broken["document"]!["activationError"]?.GetValue<string>() == "no plug answers at this host", "a class whose Setup() throws is failed: " + broken.ToJsonString());
        Check(Status(await rig.Read("broken")) == "failed", "it reads failed");
        Check(Warnings().Length == 2 && Warnings()[1] == "Entity definition \"Broken plug\" (broken) cannot start: no plug answers at this host", "the class's warning: " + Lines(Warnings()));

        // A library save rebuilds both: one fails for the same reason, which says nothing; the other for a new one, which warns.
        var library = await rig.Library("shelly", 1, EntityApiTests.Library.Replace("no plug answers at this host", "the plug refused the connection"));
        var rebuilt = library["result"]!["definitions"]!.AsArray();
        Check(rebuilt.Count == 2 && rebuilt.All(d => d!["document"]!["status"]!.GetValue<string>() == "failed" && d["enabled"]!.GetValue<bool>()), "the library save answers both failed: " + library.ToJsonString());
        Check(Warnings().Length == 3 && Warnings()[2] == "Entity definition \"Broken plug\" (broken) cannot start: the plug refused the connection",
            "a library save that meets a new reason warns: " + Lines(Warnings()));
        Check(!rig.Home.Logs(PyLogLevel.Warn).Concat(rig.Home.Logs(PyLogLevel.Error)).Any(l => l.Contains("   at ", StringComparison.Ordinal)), "a stack trace was logged above Debug: " + Lines(rig.Home.Logs()));
    }

    /// <summary>A definition that cannot start answers <c>off</c> once turned off, and <c>failed</c> again once turned on, which warns no more.</summary>
    static async Task ADefinitionThatCannotStartIsOffWhenTurnedOff()
    {
        await using var rig = await Rig.Start("cannot-start-off");
        await rig.Save("level", Definition("Level", Graph("Level", [Entity("level", "level", "sensor"), N("start", "entity_setup"), Read("read", "sensor.gone")], Next("start", "read"))));
        await rig.Ok(new { operation = "set_enabled", id = "level", enabled = false });
        var off = await rig.Read("level");
        Check(Status(off) == "off" && !off["enabled"]!.GetValue<bool>() && off["activationError"]?.GetValue<string>() == "Not active", "turned off, it is off: " + off.ToJsonString());
        var on = await rig.Ok(new { operation = "set_enabled", id = "level", enabled = true });
        Check(!on["active"]!.GetValue<bool>() && on["activationError"]?.GetValue<string>() == "Entity 'sensor.gone' is unavailable." && Status(await rig.Read("level")) == "failed",
            "turned on again, it is failed: " + on.ToJsonString());
        Check(rig.Home.Logs(PyLogLevel.Warn).Count(l => l.StartsWith("Entity definition ", StringComparison.Ordinal)) == 1, "turning it on warned again: " + Lines(rig.Home.Logs(PyLogLevel.Warn)));
    }

    // ---- A secret setting's value stays out of the steps that read it ----

    /// <summary>An obviously fake secret; a check never prints it, only whether and where it was found.</summary>
    const string FakeSecret = "fake-secret-hunter2";
    /// <summary>Text helpers as a node library declares them: value nodes, and one step with a flow of its own.</summary>
    const string TextLibrary = """
        public static class Texts
        {
            [Node("Text length", Pure = true)] public static double Length(string text) => text.Length;
            [Node("Bearer", Pure = true)] public static string Bearer(string token) => "Bearer " + token;
            [Node("Join", Pure = true)] public static string Join(string first, string second) => first + "/" + second;
            [Node("Measure")] public static double Measure(string text) => text.Length;
        }
        """;
    static JsonObject Plain(string key, string value) => new() { ["key"] = key, ["type"] = "string", ["value"] = value };
    static JsonObject UnsetValue() => new() { ["kind"] = "unset" };

    /// <summary>A rig with the text library saved, and the kind of each of its nodes by label.</summary>
    static async Task<(Rig Rig, Dictionary<string, string> Kinds)> TextRig(string name)
    {
        var rig = await Rig.Start(name);
        Check((await rig.Library("texts", 0, TextLibrary))["ok"]!.GetValue<bool>(), "the text library saves");
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var label in new[] { "Text length", "Bearer", "Join", "Measure" }) kinds[label] = await rig.LibraryNode(label);
        return (rig, kinds);
    }

    /// <summary>Saves the definition, runs the entity's command and answers the recorded run's trace as the panel and MCP clients get it.</summary>
    static async Task<JsonNode> Commanded(Rig rig, string id, JsonObject definition, string entity, string command)
    {
        var automationId = (await rig.Save(id, definition))["document"]!["automationId"]!.GetValue<string>();
        await rig.Until(id + ":" + entity, _ => true, "the definition starts");
        await rig.Api.EntityCommand(id + ":" + entity, command, null);
        return JsonNode.Parse(rig.Api.GetRunTrace(rig.History(automationId).Single(r => r["cause"]!.GetValue<string>() == "Command")["runId"]!.GetValue<string>()))!;
    }
    // A value node is worked out for each step that reads it: its first event (the whole answer is checked for the secret).
    static JsonObject Step(JsonNode answer, string node) => answer["trace"]!["events"]!.AsArray().Select(e => e!.AsObject()).First(e => e["node"]!.GetValue<string>() == node);
    /// <summary>Whether the Entity node's step in a command run gave its flow no brightness.</summary>
    static bool NoBrightness(JsonObject step) => step["outputs"] is not JsonObject outputs || !outputs.Any(o => o.Key.EndsWith("brightness", StringComparison.Ordinal));
    /// <summary>A recorded port written as <c>***</c>, marked redacted, from the wire given (none for an output).</summary>
    static bool Hidden(JsonNode? port, string? node = null, string? from = null) =>
        port?["v"] is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == "***" && port["redacted"]?.GetValue<bool>() == true
        && (node == null ? port["src"] == null : port["src"]?["node"]?.GetValue<string>() == node && port["src"]?["port"]?.GetValue<string>() == from);
    /// <summary>A recorded port in clear, with the value given.</summary>
    static bool Clear(JsonNode? port, JsonNode want) => port != null && port["redacted"] == null && JsonNode.DeepEquals(port["v"], want);
    /// <summary>What a check prints of a recorded port: its keys, and a text value only by its length, so a failure never prints a secret.</summary>
    static string Shape(JsonNode? port) => port is not JsonObject o ? "none"
        : "{" + string.Join(", ", o.Select(p => p.Key + ": " + (p.Value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? $"text({v.GetValue<string>().Length})" : p.Value?.ToJsonString().Replace(FakeSecret, "<secret>", StringComparison.Ordinal)))) + "}";
    static void Unquoted(string text, string what) => Check(!text.Contains("hunter2", StringComparison.Ordinal), $"{what} quotes the secret ({text.Length} characters)");

    /// <summary>
    /// The case the live check found: a secret token's length compared, and the comparison setting a switch. Its value is
    /// hidden in the setting's output, in the length step's input and output and in the comparison's input; the
    /// comparison's true or false, and the Set state step's input of it, are shown, as is the number it is compared with.
    /// </summary>
    static async Task ASecretStaysHiddenInTheStepsThatReadIt()
    {
        var (rig, kinds) = await TextRig("secret-flow");
        await using var _ = rig;
        var graph = Graph("Relay", [Entity("relay", "relay", "switch", new { name = "Relay" }), N("tok1", "setting", new { key = "token" }), N("len1", kinds["Text length"], new { arguments = new { } }),
            N("cmp", "compare", new { left = UnsetValue(), right = Lit("number", 5), @operator = "gt" }), SetState("set_on", "relay", new JsonObject())],
            Pin("relay", "command:turn_on", "set_on"), Data("tok1", "len1", "text"), Data("len1", "cmp", "left", "result"), Data("cmp", "set_on", "is_on"));
        var answer = await Commanded(rig, "relay", Definition("Relay", graph, [Token(FakeSecret)]), "relay", "turn_on");
        await rig.Until("relay:relay", s => Is(s, "is_on", true), "the comparison of the real value sets the switch");
        Unquoted(answer.ToJsonString(), "the run's trace answer");
        Check(Hidden(Step(answer, "tok1")["outputs"]?["value"]), "the setting's own output: " + Shape(Step(answer, "tok1")["outputs"]?["value"]));
        Check(Hidden(Step(answer, "len1")["inputs"]?["text"], "tok1", "value"), "the length step's input: " + Shape(Step(answer, "len1")["inputs"]?["text"]));
        Check(Hidden(Step(answer, "len1")["outputs"]?["result"]), "the length worked out from it: " + Shape(Step(answer, "len1")["outputs"]?["result"]));
        Check(Hidden(Step(answer, "cmp")["inputs"]?["left"], "len1", "result"), "the comparison's input of the length: " + Shape(Step(answer, "cmp")["inputs"]?["left"]));
        Check(Clear(Step(answer, "cmp")["inputs"]?["right"], 5), "the number it is compared with: " + Shape(Step(answer, "cmp")["inputs"]?["right"]));
        Check(Clear(Step(answer, "cmp")["outputs"]?["value"], true), "the comparison's true or false: " + Shape(Step(answer, "cmp")["outputs"]?["value"]));
        Check(Clear(Step(answer, "set_on")["inputs"]?["is_on"], true) && Step(answer, "set_on")["inputs"]!["is_on"]!["src"]?["node"]?.GetValue<string>() == "cmp",
            "the Set state step's input of it: " + Shape(Step(answer, "set_on")["inputs"]?["is_on"]));
    }

    /// <summary>
    /// Text built from a secret is hidden where it is made and where it is used: in a service call's field (and the
    /// service's data in the log), a Set state step's text and a Log step's line. The service and the entity get the real text.
    /// </summary>
    static async Task TextBuiltFromASecretIsHiddenWhereItIsMadeAndUsed()
    {
        var (rig, kinds) = await TextRig("secret-text");
        await using var _ = rig;
        var calls = new ConcurrentQueue<string>();
        var call = PyInterop.CallService;
        PyInterop.CallService = (domain, name, json, done, _) => { calls.Enqueue(domain + "." + name + " " + json); done?.Invoke(); };
        try
        {
            var service = N("svc", "service", new JsonObject { ["service"] = "notify.send_message", ["fields"] = new JsonObject { ["message"] = UnsetValue() }, ["returnResponse"] = false });
            var graph = Graph("Relay", [Entity("relay", "relay", "switch"), Entity("label", "label", "sensor"), N("tok1", "setting", new { key = "token" }), N("bear", kinds["Bearer"], new { arguments = new { } }),
                service, SetState("set_label", "label", new JsonObject()), N("say", "log", new { value = UnsetValue() })],
                Pin("relay", "command:turn_on", "svc"), Next("svc", "set_label"), Next("set_label", "say"),
                Data("tok1", "bear", "token"), Data("bear", "svc", "field:message", "result"), Data("bear", "set_label", "value", "result"), Data("bear", "say", "value", "result"));
            var answer = await Commanded(rig, "relay", Definition("Relay", graph, [Token(FakeSecret)]), "relay", "turn_on");
            Check(calls.Count == 1 && calls.Single().Contains("Bearer " + FakeSecret, StringComparison.Ordinal), $"the service gets the real text ({calls.Count} calls)");
            await rig.Until("relay:label", s => s["values"]?["value"]?.GetValue<string>() == "Bearer " + FakeSecret, "the entity gets the real text");
            Unquoted(answer.ToJsonString(), "the run's trace answer");
            Unquoted(string.Join("\n", rig.Home.Logs()), "the log");
            Check(Hidden(Step(answer, "bear")["inputs"]?["token"], "tok1", "value") && Hidden(Step(answer, "bear")["outputs"]?["result"]),
                "where the text is made: " + Shape(Step(answer, "bear")["inputs"]?["token"]) + " / " + Shape(Step(answer, "bear")["outputs"]?["result"]));
            Check(Hidden(Step(answer, "svc")["inputs"]?["field:message"], "bear", "result"), "the service call's field: " + Shape(Step(answer, "svc")["inputs"]?["field:message"]));
            Check(Hidden(Step(answer, "set_label")["inputs"]?["value"], "bear", "result"), "the Set state step's text: " + Shape(Step(answer, "set_label")["inputs"]?["value"]));
            Check(Hidden(Step(answer, "say")["inputs"]?["value"], "bear", "result"), "the Log step's input: " + Shape(Step(answer, "say")["inputs"]?["value"]));
            Check(rig.Home.Logs().Any(l => l.Contains("notify.send_message", StringComparison.Ordinal) && l.Contains("\"message\":\"***\"", StringComparison.Ordinal)),
                "the service's data in the debug log has the field hidden");
        }
        finally { PyInterop.CallService = call; }
    }

    /// <summary>
    /// A setting that is not secret flows in clear everywhere, beside a secret one; a value step with one secret and one
    /// clear input hides the secret input and its output, and shows the clear input.
    /// </summary>
    static async Task OnlyWhatASecretReachesIsHidden()
    {
        var (rig, kinds) = await TextRig("secret-mixed");
        await using var _ = rig;
        var graph = Graph("Relay", [Entity("relay", "relay", "switch"), Entity("label", "label", "sensor"), Entity("count", "count", "sensor"),
            N("tok1", "setting", new { key = "token" }), N("nm", "setting", new { key = "name" }),
            N("nlen", kinds["Text length"], new { arguments = new { } }), N("join", kinds["Join"], new { arguments = new { } }),
            SetState("set_label", "label", new JsonObject()), SetState("set_count", "count", new JsonObject())],
            Pin("relay", "command:turn_on", "set_label"), Next("set_label", "set_count"),
            Data("nm", "nlen", "text"), Data("tok1", "join", "first"), Data("nm", "join", "second"), Data("join", "set_label", "value", "result"), Data("nlen", "set_count", "value", "result"));
        var answer = await Commanded(rig, "relay", Definition("Relay", graph, [Token(FakeSecret), Plain("name", "kitchen")]), "relay", "turn_on");
        Unquoted(answer.ToJsonString(), "the run's trace answer");
        Check(Clear(Step(answer, "nm")["outputs"]?["value"], "kitchen"), "the plain setting: " + Shape(Step(answer, "nm")["outputs"]?["value"]));
        Check(Clear(Step(answer, "nlen")["inputs"]?["text"], "kitchen") && Clear(Step(answer, "nlen")["outputs"]?["result"], 7), "the plain setting's length: " + Shape(Step(answer, "nlen")["outputs"]?["result"]));
        Check(Clear(Step(answer, "set_count")["inputs"]?["value"], 7), "the plain length's use: " + Shape(Step(answer, "set_count")["inputs"]?["value"]));
        Check(Hidden(Step(answer, "join")["inputs"]?["first"], "tok1", "value") && Clear(Step(answer, "join")["inputs"]?["second"], "kitchen"),
            "the join's inputs: " + Shape(Step(answer, "join")["inputs"]?["first"]) + " / " + Shape(Step(answer, "join")["inputs"]?["second"]));
        Check(Hidden(Step(answer, "join")["outputs"]?["result"]) && Hidden(Step(answer, "set_label")["inputs"]?["value"], "join", "result"),
            "the join's output and its use: " + Shape(Step(answer, "join")["outputs"]?["result"]) + " / " + Shape(Step(answer, "set_label")["inputs"]?["value"]));
    }

    /// <summary>A step with a flow of its own that reads a secret hides that input; what it gives is shown, and so is its use.</summary>
    static async Task AStepWithASecretInputShowsItsOutputs()
    {
        var (rig, kinds) = await TextRig("secret-step");
        await using var _ = rig;
        var graph = Graph("Relay", [Entity("relay", "relay", "switch"), Entity("count", "count", "sensor"), N("tok1", "setting", new { key = "token" }), N("meas", kinds["Measure"], new { arguments = new { } }),
            SetState("set_count", "count", new JsonObject())],
            Pin("relay", "command:turn_on", "meas"), Next("meas", "set_count"), Data("tok1", "meas", "text"), Data("meas", "set_count", "value", "result"));
        var answer = await Commanded(rig, "relay", Definition("Relay", graph, [Token(FakeSecret)]), "relay", "turn_on");
        Unquoted(answer.ToJsonString(), "the run's trace answer");
        Check(Hidden(Step(answer, "meas")["inputs"]?["text"], "tok1", "value"), "the step's secret input: " + Shape(Step(answer, "meas")["inputs"]?["text"]));
        Check(Clear(Step(answer, "meas")["outputs"]?["result"], FakeSecret.Length), "the step's output: " + Shape(Step(answer, "meas")["outputs"]?["result"]));
        Check(Clear(Step(answer, "set_count")["inputs"]?["value"], FakeSecret.Length), "its use: " + Shape(Step(answer, "set_count")["inputs"]?["value"]));
    }

    /// <summary>
    /// A secret that a step cannot use is not quoted: not in the trace, the run's error, the command's error or the log.
    /// Text that is not a number converted to one; text an entity with options refuses; a number of iterations too many.
    /// </summary>
    static async Task ASecretThatFailsIsNotQuoted()
    {
        var (rig, kinds) = await TextRig("secret-failure");
        await using var _ = rig;
        var graph = Graph("Relay", [Entity("relay", "relay", "switch"), Entity("relay2", "relay2", "switch"), Entity("count", "count", "sensor"),
            Entity("mode", "mode", "sensor", new { options = new { options = new[] { "a", "b" } } }),
            N("tok1", "setting", new { key = "token" }), N("conv", "convert", new { type = "number", value = UnsetValue() }), SetState("set_count", "count", new JsonObject()),
            SetState("set_mode", "mode", new JsonObject()),
            N("len1", kinds["Text length"], new { arguments = new { } }), N("loop", "repeat", new { count = UnsetValue(), maxIterations = 5 }),
             N("tick", "log", new { value = Lit("string", "tick") })],
            Pin("relay", "command:turn_on", "set_count"), Pin("relay", "command:turn_off", "set_mode"), Pin("relay2", "command:turn_on", "loop"), E("loop", "tick", "body"),
            Data("tok1", "conv", "value"), Data("conv", "set_count", "value"), Data("tok1", "set_mode", "value"), Data("tok1", "len1", "text"), Data("len1", "loop", "count", "result"));
        var definition = Definition("Relay", graph, [Token(FakeSecret)]);
        var automationId = (await rig.Save("relay", definition))["document"]!["automationId"]!.GetValue<string>();
        await rig.Until("relay:relay", _ => true, "the definition starts");
        var errors = new List<string>();
        foreach (var (entity, command) in new[] { ("relay", "turn_on"), ("relay", "turn_off"), ("relay2", "turn_on") })
        {
            var before = rig.History(automationId).Select(r => r["runId"]!.GetValue<string>()).ToHashSet();
            var refused = await Throws<Exception>(() => rig.Api.EntityCommand("relay:" + entity, command, null), $"{entity} {command}");
            var run = rig.History(automationId).Single(r => !before.Contains(r["runId"]!.GetValue<string>()));
            var answer = JsonNode.Parse(rig.Api.GetRunTrace(run["runId"]!.GetValue<string>()))!;
            Check(run["status"]!.GetValue<string>() == "Failed", $"{entity} {command} failed: {run["status"]}");
            Unquoted(answer.ToJsonString(), $"the trace answer of {entity} {command}");
            errors.Add(run["error"]!.GetValue<string>());
            errors.Add(refused.Message);
        }
        Unquoted(string.Join("\n", errors), "the runs' and the commands' errors");
        string Shown(string error) => error.Replace(FakeSecret, "<secret>", StringComparison.Ordinal);
        Check(errors.All(e => e.Contains("secret setting", StringComparison.Ordinal)), "each error says the value is hidden: " + string.Join(" | ", errors.Select(Shown)));
        // The error names the step and its input that could not use the value, as every guard's message does.
        Check(errors[0] == "Convert · Value cannot use the value it was given, which is hidden because it comes from a secret setting" && errors[1] == errors[0],
            "the conversion's error names its step and input: " + Shown(errors[0]) + " | " + Shown(errors[1]));
        Unquoted(string.Join("\n", rig.Home.Logs()), "the log");
    }

    // ---- Partial states and command data ----

    /// <summary>The automation id a saved definition's runs are recorded under.</summary>
    static string AutomationId(JsonObject saved) => saved["document"]!["automationId"]!.GetValue<string>();
    /// <summary>The runs a command started, most recent first.</summary>
    static JsonObject[] CommandRuns(Rig rig, string automationId) => rig.History(automationId).Where(r => r["cause"]!.GetValue<string>() == "Command").ToArray();
    /// <summary>A light that can be dimmed, turned on at the brightness its command gives and turned off; both flows set its state.</summary>
    static JsonObject Lamp() => Definition("Lamp", Graph("Lamp",
        [Entity("lamp", "lamp", "light", new { name = "Lamp", options = new { dimmable = true } }), SetState("bright", "lamp", On(true)), SetState("dark", "lamp", On(false))],
        Pin("lamp", "command:turn_on", "bright"), Pin("lamp", "command:turn_off", "dark"), Data("lamp", "bright", "brightness", "command:turn_on:brightness")));

    /// <summary>A Set state step sets what it is given and the entity keeps the rest: a light turned off keeps its brightness.</summary>
    static async Task SetStateKeepsWhatItIsNotGiven()
    {
        await using var rig = await Rig.Start("partial-state");
        await rig.Save("lamp", Lamp());
        await rig.Until("lamp:lamp", _ => true, "the light starts");
        await rig.Api.EntityCommand("lamp:lamp", "turn_on", """{"brightness":100}""");
        await rig.Until("lamp:lamp", s => Is(s, "is_on", true) && Is(s, "brightness", 100), "turned on at the brightness asked for");
        var batches = rig.Batches;
        await rig.Api.EntityCommand("lamp:lamp", "turn_off", null);
        await rig.Until("lamp:lamp", s => Is(s, "is_on", false) && Is(s, "brightness", 100), "turned off, keeping its brightness", batches);
    }

    /// <summary>A command's pin gives its data to the steps after it: a number's value, a select's option, a text and a cover's position.</summary>
    static async Task ACommandHandsItsDataToTheFlow()
    {
        await using var rig = await Rig.Start("command-data");
        var graph = Graph("Panel",
            [Entity("target", "target", "number", new { options = new { min = 5, max = 30, step = 0.5 } }), Entity("mode", "mode", "select", new { options = new { options = new[] { "low", "high" } } }),
             Entity("message", "message", "text"), Entity("blind", "blind", "cover", new { options = new { positionable = true } }),
             SetState("show_target", "target", new JsonObject()), SetState("show_mode", "mode", new JsonObject()), SetState("show_message", "message", new JsonObject()), SetState("show_blind", "blind", new JsonObject())],
            Pin("target", "command:set_value", "show_target"), Pin("mode", "command:select_option", "show_mode"), Pin("message", "command:set_value", "show_message"), Pin("blind", "command:set_position", "show_blind"),
            Data("target", "show_target", "value", "command:set_value:value"), Data("mode", "show_mode", "option", "command:select_option:option"),
            Data("message", "show_message", "value", "command:set_value:value"), Data("blind", "show_blind", "position", "command:set_position:position"));
        await rig.Save("panel", Definition("Panel", graph));
        await rig.Until("panel:target", _ => true, "the panel starts");
        foreach (var (key, command, data) in new[] { ("target", "set_value", """{"value":21.5}"""), ("mode", "select_option", """{"option":"high"}"""), ("message", "set_value", """{"value":"Hello"}"""), ("blind", "set_position", """{"position":40}""") })
            await rig.Api.EntityCommand("panel:" + key, command, data);
        await rig.Until("panel:target", s => Is(s, "value", 21.5), "the number shows the value it was set to");
        await rig.Until("panel:mode", s => Is(s, "option", "high"), "the select shows the option chosen");
        await rig.Until("panel:message", s => Is(s, "value", "Hello"), "the text shows the text it was given");
        await rig.Until("panel:blind", s => Is(s, "position", 40) && Is(s, "is_closed", null), "the cover shows the position it was moved to, and nothing else it was not given");
    }

    /// <summary>The light's brightness as the latest command's flow read it: the input of its Set state step.</summary>
    static JsonNode? ReadBrightness(Rig rig, string automationId) =>
        Step(JsonNode.Parse(rig.Api.GetRunTrace(CommandRuns(rig, automationId)[0]["runId"]!.GetValue<string>()))!, "bright")["inputs"]?["brightness"];

    /// <summary>
    /// A light turned on without a brightness (no data, empty data or a null brightness) is turned on at the one it has,
    /// or at full when it has none yet: the flow reads that as the command's brightness, and pushes it.
    /// </summary>
    static async Task ALightTurnedOnWithoutABrightnessHasItsLastOneOrFull()
    {
        await using var rig = await Rig.Start("absent-brightness");
        var automationId = AutomationId(await rig.Save("lamp", Lamp()));
        await rig.Until("lamp:lamp", _ => true, "the light starts");
        await rig.Api.EntityCommand("lamp:lamp", "turn_on", null);
        await rig.Until("lamp:lamp", s => Is(s, "is_on", true) && Is(s, "brightness", 255), "turned on at full, having no brightness yet");
        Check(Clear(ReadBrightness(rig, automationId), 255), "the flow read full as the command's brightness: " + ReadBrightness(rig, automationId)?.ToJsonString());
        await rig.Api.EntityCommand("lamp:lamp", "turn_on", """{"brightness":80}""");
        await rig.Until("lamp:lamp", s => Is(s, "brightness", 80), "turned on at the brightness asked for");
        Check(CommandRuns(rig, automationId).Length == 2 && Clear(ReadBrightness(rig, automationId), 80), "the latest run is listed first and read 80");
        foreach (var data in new string?[] { null, "{}", """{"brightness":null}""" })
        {
            var batches = rig.Batches;
            await rig.Api.EntityCommand("lamp:lamp", "turn_off", null);
            await rig.Until("lamp:lamp", s => Is(s, "is_on", false) && Is(s, "brightness", 80), "turned off, keeping its brightness", batches);
            batches = rig.Batches;
            await rig.Api.EntityCommand("lamp:lamp", "turn_on", data);
            await rig.Until("lamp:lamp", s => Is(s, "is_on", true) && Is(s, "brightness", 80), $"turned on with {data ?? "no data"} at the brightness it has", batches);
            Check(Clear(ReadBrightness(rig, automationId), 80), $"with {data ?? "no data"} the flow read the light's brightness as the command's: " + ReadBrightness(rig, automationId)?.ToJsonString());
        }
    }

    /// <summary>
    /// A light that cannot be dimmed is turned on without a brightness: none is filled in, as the light takes none, and its
    /// flow runs. A flow that reads the brightness all the same fails in words, without ids, and nothing is pushed.
    /// </summary>
    static async Task ALightThatCannotBeDimmedGetsNoBrightness()
    {
        await using var rig = await Rig.Start("no-brightness");
        var graph = Graph("Lamps",
            [Entity("plain", "plain", "light", new { name = "Plain" }), Entity("wired", "wired", "light", new { name = "Wired" }),
             SetState("lit", "plain", On(true)), SetState("dark", "plain", On(false)), SetState("bright", "wired", On(true)), SetState("dark2", "wired", On(false))],
            Pin("plain", "command:turn_on", "lit"), Pin("plain", "command:turn_off", "dark"), Pin("wired", "command:turn_on", "bright"), Pin("wired", "command:turn_off", "dark2"),
            Data("wired", "bright", "brightness", "command:turn_on:brightness"));
        var automationId = AutomationId(await rig.Save("lamps", Definition("Lamps", graph)));
        await rig.Until("lamps:plain", _ => true, "the lights start");
        await rig.Api.EntityCommand("lamps:plain", "turn_on", null);
        await rig.Until("lamps:plain", s => Is(s, "is_on", true) && Is(s, "brightness", null), "the light that cannot be dimmed is turned on, with no brightness");
        var trace = JsonNode.Parse(rig.Api.GetRunTrace(CommandRuns(rig, automationId)[0]["runId"]!.GetValue<string>()))!;
        Check(NoBrightness(Step(trace, "plain")), "no brightness was filled in for it: " + Step(trace, "plain").ToJsonString());
        await rig.Flush();
        var batches = rig.Batches;
        var refused = await Throws<InvalidOperationException>(() => rig.Api.EntityCommand("lamps:wired", "turn_on", "{}"), "a flow reading a brightness its light cannot have");
        Check(refused.Message == "Set state: Turn on came without a brightness, and Wired has none", "the step reading the brightness fails in words: " + refused.Message);
        await rig.Flush();
        Check(rig.Pushed("lamps:wired", batches).Length == 0, "a failed command pushed: " + Lines(rig.Home.Pushes().Skip(batches)));
    }

    /// <summary>A button takes its one command, press, and its flow runs; it has no state of its own.</summary>
    static async Task AButtonRunsItsFlow()
    {
        await using var rig = await Rig.Start("button");
        var graph = Graph("Bell", [Entity("bell", "bell", "button", new { name = "Bell" }), Entity("last", "last", "sensor"), SetState("rang", "last", new JsonObject { ["value"] = Lit("string", "pressed") })],
            Pin("bell", "command:press", "rang"));
        var saved = await rig.Save("bell", Definition("Bell", graph));
        Check(!saved.ContainsKey("diagnostics"), "a button whose press is handled has no warnings: " + saved.ToJsonString());
        var sync = JsonNode.Parse(rig.Home.Syncs("bell").Single())!;
        Check(sync["entities"]![0]!["commands"]!.ToJsonString() == """["press"]""", "the button takes its command: " + sync.ToJsonString());
        await rig.Until("bell:bell", s => Available(s) && s["values"]!.ToJsonString() == "{}", "the button is pushed available, with no state");
        await rig.Api.EntityCommand("bell:bell", "press", "{}");
        await rig.Until("bell:last", s => Is(s, "value", "pressed"), "the press runs its flow");
    }

    /// <summary>
    /// A graph's command is refused as a class's is when the entity does not take its data, before any flow runs and with
    /// the class's message: a number beyond its maximum, a brightness for a light that cannot be dimmed, an option not in
    /// the list. Nothing is pushed and no run is recorded.
    /// </summary>
    static async Task ACommandsDataIsCheckedAsAClassChecksIt()
    {
        await using var rig = await Rig.Start("command-check");
        var graph = Graph("Panel",
            [Entity("target", "target", "number", new { name = "Target", options = new { min = 5, max = 30 } }), Entity("lamp", "lamp", "light", new { name = "Lamp" }),
             Entity("mode", "mode", "select", new { name = "Mode", options = new { options = new[] { "low", "high" } } }),
             SetState("show_target", "target", new JsonObject()), SetState("lit", "lamp", On(true)), SetState("dark", "lamp", On(false)), SetState("show_mode", "mode", new JsonObject())],
            Pin("target", "command:set_value", "show_target"), Pin("lamp", "command:turn_on", "lit"), Pin("lamp", "command:turn_off", "dark"), Pin("mode", "command:select_option", "show_mode"),
            Data("target", "show_target", "value", "command:set_value:value"), Data("mode", "show_mode", "option", "command:select_option:option"));
        var automationId = AutomationId(await rig.Save("panel", Definition("Panel", graph)));
        await rig.Until("panel:target", _ => true, "the panel starts");
        await rig.Flush();
        var batches = rig.Batches;
        foreach (var (key, command, data, message) in new[] {
            ("target", "set_value", """{"value":45}""", "Target (target) takes a number from 5 to 30, not 45"),
            ("lamp", "turn_on", """{"brightness":128}""", "Lamp (lamp) cannot be dimmed, so it takes no brightness, not 128"),
            ("mode", "select_option", """{"option":"medium"}""", "Mode (mode) takes one of low or high, not \"medium\"") })
        {
            var refused = await Throws<ArgumentException>(() => rig.Api.EntityCommand("panel:" + key, command, data), $"{key} {command} {data}");
            Check(refused.Message == message, $"{key} {command} {data} is refused with the class's message: {refused.Message}");
        }
        await rig.Flush();
        Check(rig.Pushed("panel:target", batches).Length + rig.Pushed("panel:lamp", batches).Length + rig.Pushed("panel:mode", batches).Length == 0, "a refused command changed a state: " + Lines(rig.Home.Pushes().Skip(batches)));
        Check(CommandRuns(rig, automationId).Length == 0, "a refused command ran its flow: " + Lines(rig.History(automationId).Select(r => r.ToJsonString())));
        await rig.Api.EntityCommand("panel:lamp", "turn_on", "{}");
        await rig.Until("panel:lamp", s => Is(s, "is_on", true), "a light that cannot be dimmed is turned on without a brightness", batches);
    }

    /// <summary>A password text's value that a Set state step gives and the text refuses is not quoted in the refusal, as its class words it.</summary>
    static async Task APasswordTextsRefusedValueIsNotQuoted()
    {
        await using var rig = await Rig.Start("password-text");
        var graph = Graph("Code", [Entity("code", "code", "text", new { name = "Code", options = new { pattern = "^[0-9]+$", mode = "password" } }), SetState("show", "code", new JsonObject { ["value"] = Lit("string", "abcd") })],
            Pin("code", "command:set_value", "show"));
        await rig.Save("code", Definition("Code", graph));
        await rig.Until("code:code", _ => true, "the text starts");
        var refused = await Throws<InvalidOperationException>(() => rig.Api.EntityCommand("code:code", "set_value", """{"value":"1234"}"""), "a password text given a text it refuses");
        Check(refused.Message == "The hidden value given for Code (code) is not a text that matches ^[0-9]+$", "the refusal does not quote the password: " + refused.Message);
        Check(!string.Join("\n", rig.Home.Logs()).Contains("abcd", StringComparison.Ordinal), "the log quotes the password");
    }

    /// <summary>
    /// A definition's Take a picture asks Home Assistant under the definition's own id (so Home Assistant can refuse a
    /// picture of the definition's own camera), and its drawing steps draw on what it gave; an automation cannot take
    /// one: the three steps are refused in its save.
    /// </summary>
    static async Task ADefinitionTakesPicturesUnderItsOwnIdAndAnAutomationCannot()
    {
        await using var rig = await Rig.Start("picture");
        var picture = SharpImage.Blank(16, 16, SharpColor.Blue).ToJpeg();
        rig.Home.AnswerPictures = (_, camera, ok, fail) => { if (camera == "camera.porch") ok(picture); else fail("Camera is off"); };
        JsonObject[] Steps() => [N("snap", "camera_snapshot", new JsonObject { ["entity"] = Lit("entity_id:camera", "camera.porch") }),
            N("rect", "image_draw_rect", new JsonObject { ["image"] = JsonSerializer.SerializeToNode(Unset), ["x"] = Lit("number", 1), ["y"] = Lit("number", 1), ["width"] = Lit("number", 8), ["height"] = Lit("number", 8) }),
            N("label", "image_draw_text", new JsonObject { ["image"] = JsonSerializer.SerializeToNode(Unset), ["text"] = Lit("string", "Porch"), ["x"] = Lit("number", 1), ["y"] = Lit("number", 1) }),
            N("say", "log", new { value = Unset })];
        JsonObject[] Wires() => [Next("snap", "say"), Data("snap", "rect", "image", "image"), Data("rect", "label", "image", "image"), Data("label", "say", "value", "image")];
        var graph = Graph("Porch", [Entity("seen", "seen", "sensor"), N("setup", "entity_setup"), .. Steps(), SetState("set", "seen", new JsonObject { ["value"] = Lit("string", "taken") })], [Next("setup", "snap"), .. Wires(), Next("say", "set")]);
        await rig.Save("porch", Definition("Porch", graph));
        await rig.Until("porch:seen", s => Is(s, "value", "taken"), "the When started flow takes the picture");
        Check(rig.Home.Pictures().SequenceEqual([("porch", "camera.porch")]), "the picture is asked for under the definition's id: " + string.Join(", ", rig.Home.Pictures()));
        Check(rig.Home.Logs().Contains("Image · 1 KB · 2 drawings"), "the drawings are on the picture: " + Lines(rig.Home.Logs()));

        var automation = Graph("Pictures", [N("t", "trigger", new { type = "manual" }), .. Steps()], [Next("t", "snap"), .. Wires()]);
        var refused = JsonNode.Parse(await rig.Api.VisualRequest(JsonSerializer.Serialize(new { operation = "save", id = "pictures", expectedRevision = 0, graph = automation })))!;
        var problems = refused["error"]?["detail"]?["diagnostics"]?.AsArray().Where(d => d!["code"]?.GetValue<string>() == "node.document").Select(d => d!["nodeId"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray() ?? [];
        Check(!refused["ok"]!.GetValue<bool>() && problems.SequenceEqual(["label", "rect", "snap"]), "an automation cannot take a picture or draw on one: " + refused.ToJsonString());
        Check(rig.Home.Pictures().Length == 1, "the refused automation took no picture");
    }

    /// <summary>
    /// A picture goes into no node library pin, not even one of its own class (<c>SharpImage</c>), what that implements or
    /// <c>object</c>, and a library's <c>SharpImage</c> is no picture for a drawing step: the type is the catalog's own,
    /// decided by its entry alone, as the editor decides it. A library's SharpImage still goes into its own SharpImage pins.
    /// </summary>
    static Task APictureGoesIntoNoLibraryPinAndNoLibraryValueIsAPicture()
    {
        const string source = """
            public static class Pictures
            {
                [Node("Keep picture")] public static void KeepPicture(SharpImage picture) {}
                [Node("Keep snapshot")] public static void KeepSnapshot(IVisualSnapshot value) {}
                [Node("Keep anything")] public static void KeepAnything(object value) {}
                [Node("Make picture")] public static SharpImage MakePicture() => SharpImage.Blank(1, 1);
            }
            """;
        var preview = new NodeLibraryCompiler(new DiagnosticsProvider()).Preview([new("pictures", source, 1)]);
        Check(preview.Valid, "the library compiles: " + string.Join("; ", preview.Diagnostics.Select(d => d.Message)));
        var snapshot = preview.Snapshot;
        JsonObject Library(string method, string id) => VisualGraphContract.CreateNode(snapshot.Methods.Values.Single(m => m.Method.Name == method).Kind, id, snapshot);
        JsonObject Picture(JsonObject step, params JsonObject[] wires)
        {
            var graph = Graph("Pictures", [N("setup", "entity_setup"), N("snap", "camera_snapshot", new JsonObject { ["entity"] = Lit("entity_id:camera", "camera.porch") }), step],
                [Next("setup", "snap"), Next("snap", step["id"]!.GetValue<string>()), .. wires]);
            graph["definition"] = new JsonObject { ["settings"] = new JsonArray() };
            return graph;
        }
        string[] Problems(JsonObject graph) => [.. VisualGraphValidator.Validate(graph, snapshot).Select(d => $"{d.NodeId}.{d.InputId}: {d.Message}")];
        foreach (var (method, port) in new[] { ("KeepPicture", "picture"), ("KeepSnapshot", "value"), ("KeepAnything", "value") })
        {
            var problems = Problems(Picture(Library(method, "keep"), Data("snap", "keep", port, "image")));
            Check(problems.Length == 1 && problems[0].StartsWith($"keep.{port}: ", StringComparison.Ordinal) && problems[0].EndsWith(", not Image", StringComparison.Ordinal),
                $"a picture into {method}'s pin is refused by its type: " + string.Join(" | ", problems));
        }
        var rect = N("rect", "image_draw_rect", new JsonObject { ["image"] = JsonSerializer.SerializeToNode(Unset), ["x"] = Lit("number", 1), ["y"] = Lit("number", 1), ["width"] = Lit("number", 8), ["height"] = Lit("number", 8) });
        var made = Picture(Library("MakePicture", "make"), Data("make", "rect", "image", "result"));
        made["nodes"]!.AsArray().Add(rect);
        made["nodes"]!.AsArray().Add(N("say", "log", new { value = Unset }));
        made["edges"]!.AsArray().Add(Next("make", "say"));
        made["edges"]!.AsArray().Add(Data("rect", "say", "value", "image"));
        var drawn = Problems(made);
        Check(drawn.Length == 1 && drawn[0].StartsWith("rect.image: Image accepts Image, not ", StringComparison.Ordinal), "a library's SharpImage is no picture for Draw rectangle: " + string.Join(" | ", drawn));
        var own = Picture(Library("MakePicture", "make"), Data("make", "keep", "picture", "result"));
        own["nodes"]!.AsArray().Add(Library("KeepPicture", "keep"));
        own["edges"]!.AsArray().Add(Next("make", "keep"));
        Check(Problems(own).Length == 0, "a library's SharpImage goes into its own SharpImage pin: " + string.Join(" | ", Problems(own)));
        return Task.CompletedTask;
    }

    // ---- A camera built from nodes ----

    /// <summary>The picture <c>camera.front</c> gives: 40 by 30, white, or black for a second one.</summary>
    static byte[] Front(bool black = false) => SharpImage.Blank(40, 30, black ? SharpColor.Black : SharpColor.White).ToJpeg();
    static JsonObject Snap(string id) => N(id, "camera_snapshot", new JsonObject { ["entity"] = Lit("entity_id:camera", "camera.front") });
    static JsonObject Rect(string id) => N(id, "image_draw_rect", new JsonObject
        { ["image"] = JsonSerializer.SerializeToNode(Unset), ["x"] = Lit("number", 2), ["y"] = Lit("number", 2), ["width"] = Lit("number", 20), ["height"] = Lit("number", 10) });
    static JsonObject ShowFrame(string id, string entity = "annotated") => N(id, "show_frame", new JsonObject { ["entity"] = entity, ["image"] = JsonSerializer.SerializeToNode(Unset) });
    /// <summary>The camera <c>annotated</c>'s Entity node; its frame flow's history as the catalog has it unless <paramref name="trackHistory"/> is given.</summary>
    static JsonObject Camera(bool? trackHistory = null)
    {
        var camera = Entity("annotated", "annotated", "camera", new { name = "Annotated" });
        if (trackHistory is { } track) camera["config"]!["history"]!["frame"] = track;
        return camera;
    }
    /// <summary>
    /// The phase's acceptance graph: Annotated's When a frame is needed → Take a picture (camera.front) → Draw rectangle →
    /// Draw text → Show frame ▸ Annotated; its history as the catalog has it unless <paramref name="trackHistory"/> is given.
    /// </summary>
    static JsonObject Annotated(bool? trackHistory = null) => Definition("Annotated", Graph("Annotated",
        [Camera(trackHistory), Snap("snap"), Rect("rect"),
         N("label", "image_draw_text", new JsonObject { ["image"] = JsonSerializer.SerializeToNode(Unset), ["text"] = Lit("string", "Front door"), ["x"] = Lit("number", 1), ["y"] = Lit("number", 1) }),
         ShowFrame("show")],
        Pin("annotated", "frame", "snap"), Next("snap", "show"), Data("snap", "rect", "image", "image"), Data("rect", "label", "image", "image"), Data("label", "show", "image", "image")));
    static Task<byte[]> Frame(Rig rig, string uniqueId = "annotated:annotated") => rig.Api.CameraImage(uniqueId);
    static bool Same(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);
    static (DefinitionHost Host, SharpCamera Camera) Annotating(Rig rig, string uniqueId = "annotated:annotated") =>
        rig.Scripts.Entities.TryGet(uniqueId, out var host, out var entity) ? (host, (SharpCamera)entity) : throw new Exception(uniqueId + " is not running");
    static string[] Problems(Rig rig) => [.. rig.Home.Logs(PyLogLevel.Error), .. rig.Home.Logs(PyLogLevel.Warn)];

    /// <summary>
    /// The acceptance graph: nothing runs while nobody watches; five requests at once run the flow once and get one JPEG
    /// drawn on the picture taken; its runs are not recorded unless its history is on, and then its trace shows the
    /// picture as a short text, never its bytes.
    /// </summary>
    static async Task AGraphCameraMakesItsFrameOnlyWhenOneIsNeeded()
    {
        await using var rig = await Rig.Start("frame", ("camera.front", "idle"));
        var front = Front();
        rig.Home.AnswerPictures = (_, camera, ok, fail) => { if (camera == "camera.front") ok(front); else fail("Camera is off"); };
        var saved = await rig.Save("annotated", Annotated());
        Check(saved["active"]!.GetValue<bool>() && saved["diagnostics"] == null, "the camera runs, with nothing to warn of: " + saved.ToJsonString());
        var automationId = saved["document"]!["automationId"]!.GetValue<string>();
        await rig.Started("annotated");
        await Task.Delay(100);
        Check(rig.Home.Pictures().Length == 0, "nothing runs while nobody watches: " + string.Join(", ", rig.Home.Pictures()));

        var frames = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Frame(rig)));
        Check(frames.All(frame => frame is [0xFF, 0xD8, ..] && Same(frame, frames[0])) && !Same(frames[0], front), "five requests at once get one JPEG, drawn on the picture taken");
        TurboJpeg.Header(frames[0], out var width, out var height);
        Check(width == 40 && height == 30, $"the frame has the picture's size: {width}x{height}");
        Check(rig.Home.Pictures().SequenceEqual([("annotated", "camera.front")]), "the flow ran once for the five requests: " + string.Join(", ", rig.Home.Pictures()));
        Check(rig.History(automationId).Length == 0, "a frame flow is not recorded by default: " + Lines(rig.History(automationId).Select(r => r.ToJsonString())));
        Check(Problems(rig).Length == 0, "nothing is logged: " + Lines(Problems(rig)));

        // With its history on, the frame made is a run, whose trace shows the picture as a short text.
        await rig.Save("annotated", Annotated(trackHistory: true), saved["document"]!["revision"]!.GetValue<long>());
        await rig.Started("annotated");
        var traced = await Frame(rig);
        var runs = rig.History(automationId);
        Check(runs.Length == 1 && runs[0]["cause"]!.GetValue<string>() == "Frame" && runs[0]["status"]!.GetValue<string>() == "Succeeded"
            && runs[0]["triggerNodeId"]!.GetValue<string>() == "annotated" && runs[0]["hasTrace"]!.GetValue<bool>(), "the frame made is recorded: " + Lines(runs.Select(r => r.ToJsonString())));
        Check(runs[0]["trigger"]!.ToJsonString().Contains("annotated", StringComparison.Ordinal), "the run's trigger names the camera: " + runs[0]["trigger"]!.ToJsonString());
        var text = rig.Api.GetRunTrace(runs[0]["runId"]!.GetValue<string>());
        var shown = JsonNode.Parse(text)!["trace"]!["events"]!.AsArray().Single(e => e!["node"]!.GetValue<string>() == "show")!["inputs"]!["image"]!;
        Check(shown["v"]?.GetValue<string>() == $"Image · {(front.Length + 1023) / 1024} KB · 2 drawings", "the trace shows the picture as a short text: " + shown.ToJsonString());
        Check(!text.Contains(Convert.ToBase64String(front)[..40], StringComparison.Ordinal) && !text.Contains(Convert.ToBase64String(traced)[..40], StringComparison.Ordinal)
            && !text.Contains("[255,216", StringComparison.Ordinal), "the trace has no picture bytes");
    }

    /// <summary>
    /// A frame flow whose picture cannot be taken leaves the last good frame as the answer, logged once for the reason, as
    /// a class's Frame() is (the flow's failure is not logged a second time), and once when it works again.
    /// </summary>
    static async Task AFrameFlowWhoseSourceFailsGivesTheLastGoodFrame()
    {
        await using var rig = await Rig.Start("frame-failing", ("camera.front", "idle"));
        CameraFrames.IntervalSecond = TimeSpan.FromMilliseconds(20);
        var (front, black) = (Front(), Front(black: true));
        var answer = 0;
        rig.Home.AnswerPictures = (_, _, ok, fail) => { switch (Volatile.Read(ref answer)) { case 0: ok(front); break; case 1: fail("Camera camera.front is off"); break; default: ok(black); break; } };
        await rig.Save("annotated", Annotated());
        await rig.Started("annotated");
        var good = await Frame(rig);
        Volatile.Write(ref answer, 1);
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(30);
            Check(Same(await Frame(rig), good), $"failure {i + 1} answers the last good frame");
        }
        Check(rig.Home.Pictures().Length == 6, $"each request an interval apart ran the flow: {rig.Home.Pictures().Length} pictures");
        var problems = Problems(rig);
        Check(problems.Length == 1 && problems[0].StartsWith("Annotated: Annotated could not make a frame: ", StringComparison.Ordinal) && problems[0].Contains("Camera camera.front is off", StringComparison.Ordinal),
            "five failures for one reason log one line, the camera's: " + Lines(problems));
        Volatile.Write(ref answer, 2);
        await Task.Delay(30);
        var again = await Frame(rig);
        Check(!Same(again, good) && again is [0xFF, 0xD8, ..], "the camera makes frames again");
        Check(rig.Home.Logs(PyLogLevel.Info).Count(line => line == "Annotated: Annotated makes frames again") == 1 && Problems(rig).Length == 1,
            "the recovery is logged once: " + Lines(rig.Home.Logs()));
    }

    /// <summary>A frame flow that shows no frame leaves the frame as it was: the one When started showed.</summary>
    static async Task AFrameFlowWithoutShowFrameKeepsTheFrame()
    {
        await using var rig = await Rig.Start("frame-kept", ("camera.front", "idle"));
        var front = Front();
        rig.Home.AnswerPictures = (_, _, ok, _) => ok(front);
        var graph = Graph("Kept", [Camera(), N("setup", "entity_setup"), Snap("snap"), ShowFrame("show"), N("say", "log", new { value = Lit("string", "asked") })],
            Next("setup", "snap"), Next("snap", "show"), Data("snap", "show", "image", "image"), Pin("annotated", "frame", "say"));
        var saved = await rig.Save("annotated", Definition("Annotated", graph));
        Check(saved["diagnostics"] == null, "a Show frame in another flow is enough: " + saved.ToJsonString());
        await rig.Started("annotated");
        Check(Same(await Frame(rig), front), "the frame When started showed is the answer");
        Check(rig.Home.Logs().Count(line => line == "asked") == 1 && rig.Home.Pictures().Length == 1, "the frame flow ran, and took no picture: " + Lines(rig.Home.Logs()));
    }

    /// <summary>
    /// With nothing on the camera's frame pin, a request is answered with the frame shown last, by any flow, and runs nothing:
    /// When started shows the first, a trigger's flow a later one.
    /// </summary>
    static async Task APushedFrameIsAnsweredWithoutRunningAnything()
    {
        await using var rig = await Rig.Start("frame-pushed", ("binary_sensor.motion", "off"), ("camera.front", "idle"));
        var front = Front();
        rig.Home.AnswerPictures = (_, _, ok, _) => ok(front);
        var graph = Graph("Pushed",
            [Camera(), N("setup", "entity_setup"), Snap("snap"), ShowFrame("show"), N("seen", "trigger", new { type = "entity", entity = "binary_sensor.motion" }), Snap("again"), Rect("mark"), ShowFrame("marked")],
            Next("setup", "snap"), Next("snap", "show"), Data("snap", "show", "image", "image"),
            Next("seen", "again"), Next("again", "marked"), Data("again", "mark", "image", "image"), Data("mark", "marked", "image", "image"));
        var saved = await rig.Save("annotated", Definition("Annotated", graph));
        Check(saved["active"]!.GetValue<bool>() && saved["diagnostics"] == null, "the camera runs: " + saved.ToJsonString());
        await rig.Started("annotated");
        for (var i = 0; i < 3; i++) Check(Same(await Frame(rig), front), $"request {i + 1} gets the frame When started showed");
        Check(rig.Home.Pictures().Length == 1, $"the requests ran nothing: {rig.Home.Pictures().Length} pictures");
        await rig.Change("binary_sensor.motion", "on");
        var marked = await Frame(rig);
        Check(marked is [0xFF, 0xD8, ..] && !Same(marked, front) && rig.Home.Pictures().Length == 2, "the trigger's flow shows a frame of its own, which the next request gets");
    }

    /// <summary>
    /// A frame flow still running at the time limit is cancelled (its run records why): the request gets the last good
    /// frame, the next one starts a flow of its own, and once the picture comes again the camera makes frames again.
    /// The time limit is 0.2 s for the flows whose picture never comes, and the usual one for the others: on a slow
    /// machine a flow that makes its frame can take longer than 0.2 s, and only one that hangs must pass the limit.
    /// </summary>
    static async Task AFrameFlowPastItsLimitIsCancelledAndTheCameraRecovers()
    {
        await using var rig = await Rig.Start("frame-late", ("camera.front", "idle"));
        CameraFrames.IntervalSecond = TimeSpan.FromMilliseconds(20);
        var limit = CameraFrames.MakeLimit;
        var (front, black) = (Front(), Front(black: true));
        var answer = 0;
        // 1: Home Assistant never answers.
        rig.Home.AnswerPictures = (_, _, ok, _) => { switch (Volatile.Read(ref answer)) { case 0: ok(front); break; case 1: break; default: ok(black); break; } };
        var saved = await rig.Save("annotated", Annotated(trackHistory: true));
        var automationId = saved["document"]!["automationId"]!.GetValue<string>();
        await rig.Started("annotated");
        var good = await Frame(rig);
        // Each attempt takes the limit as it begins.
        CameraFrames.MakeLimit = TimeSpan.FromMilliseconds(200);
        Volatile.Write(ref answer, 1);
        JsonObject[] Cancelled() => rig.History(automationId).Where(r => r["status"]!.GetValue<string>() == "Cancelled").ToArray();
        for (var i = 1; i <= 2; i++)
        {
            await Task.Delay(30);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Check(Same(await Frame(rig), good) && clock.Elapsed >= TimeSpan.FromMilliseconds(190) && clock.Elapsed < TimeSpan.FromSeconds(2),
                $"request {i} past the limit answers the last good frame ({clock.ElapsedMilliseconds} ms)");
            await EntityTestHome.Until(() => Cancelled().Length == i, $"flow {i} past the limit is cancelled");
            // Its end reaches the camera through a pool continuation.
            await EntityTestHome.Until(() => !Annotating(rig).Camera.Frames.Busy, $"flow {i} has ended for the camera");
        }
        Check(rig.Home.Pictures().Length == 3, $"the second request started a flow of its own: {rig.Home.Pictures().Length} pictures");
        Check(Cancelled().All(r => r["error"]?.GetValue<string>() == "Took longer than 0.2 s"), "the runs say why they were cancelled: " + Lines(Cancelled().Select(r => r.ToJsonString())));
        Check(Problems(rig).SequenceEqual(["Annotated: Annotated could not make a frame: it took longer than 0.2 s"]), "the time limit is logged once: " + Lines(Problems(rig)));
        CameraFrames.MakeLimit = limit;
        Volatile.Write(ref answer, 2);
        await Task.Delay(30);
        var again = await Frame(rig);
        Check(!Same(again, good) && again is [0xFF, 0xD8, ..], "the camera makes frames again by itself");
        Check(rig.Home.Logs(PyLogLevel.Info).Count(line => line == "Annotated: Annotated makes frames again") == 1, "and says so once: " + Lines(rig.Home.Logs()));
    }

    /// <summary>A revision saved while a frame flow waits for its picture: the old flow is cancelled, its request ends, the new revision serves frames.</summary>
    static async Task ARevisionSavedWhileAFrameFlowRunsServesFrames()
    {
        await using var rig = await Rig.Start("frame-saved", ("camera.front", "idle"));
        var front = Front();
        rig.Home.AnswerPictures = (_, _, _, _) => { };
        var saved = await rig.Save("annotated", Annotated());
        await rig.Started("annotated");
        var waiting = Frame(rig);
        await EntityTestHome.Until(() => rig.Home.Pictures().Length == 1, "the frame flow asks for its picture");
        rig.Home.AnswerPictures = (_, _, ok, _) => ok(front);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var replaced = await rig.Save("annotated", Annotated(), saved["document"]!["revision"]!.GetValue<long>());
        // Well within the 10 s that stopping would wait for the flow, with room for a compile on a slow machine.
        Check(replaced["active"]!.GetValue<bool>() && clock.Elapsed < TimeSpan.FromSeconds(5), $"the new revision runs, saved in {clock.ElapsedMilliseconds} ms: " + replaced.ToJsonString());
        var ended = await Throws<InvalidOperationException>(() => waiting, "the request to the replaced revision");
        Check(ended.Message == "The entity is not running", "the request to the replaced revision ends: " + ended.Message);
        await rig.Started("annotated");
        var frame = await Frame(rig);
        Check(frame is [0xFF, 0xD8, ..] && !Same(frame, front) && rig.Home.Pictures().Length == 2, "the new revision serves frames");
        Check(Problems(rig).Length == 0, "the cancelled flow is no failure: " + Lines(Problems(rig)));
    }

    /// <summary>
    /// A camera whose frames a trigger's flow shows (Take a picture → Show frame) keeps its picture across a save: the
    /// request right after it gets the frame the replaced revision showed, before the trigger fires again.
    /// </summary>
    static async Task APushedFrameStaysAcrossASave()
    {
        await using var rig = await Rig.Start("frame-pushed-saved", ("binary_sensor.motion", "off"), ("camera.front", "idle"));
        var front = Front();
        rig.Home.AnswerPictures = (_, _, ok, _) => ok(front);
        var graph = Graph("Pushed", [Camera(), N("seen", "trigger", new { type = "entity", entity = "binary_sensor.motion" }), Snap("snap"), Rect("mark"), ShowFrame("show")],
            Next("seen", "snap"), Next("snap", "show"), Data("snap", "mark", "image", "image"), Data("mark", "show", "image", "image"));
        var saved = await rig.Save("annotated", Definition("Annotated", graph));
        await rig.Started("annotated");
        await rig.Change("binary_sensor.motion", "on");
        await EntityTestHome.Until(() => rig.Home.Pictures().Length == 1, "the trigger's flow takes its picture");
        var shown = await Frame(rig);
        var replaced = await rig.Save("annotated", Definition("Annotated", graph), saved["document"]!["revision"]!.GetValue<long>());
        Check(replaced["active"]!.GetValue<bool>(), "the new revision runs: " + replaced.ToJsonString());
        await rig.Started("annotated");
        Check(Same(await Frame(rig), shown) && rig.Home.Pictures().Length == 1, "the request right after the save gets the frame the replaced revision showed, and nothing runs");
        Check(Problems(rig).Length == 0, "nothing fails: " + Lines(Problems(rig)));
    }

    /// <summary>
    /// Home Assistant stopping cancels the script's lifetime before the host's own stop: a frame flow cancelled that way is
    /// no failure of the camera (no Error line), the request gets the last good frame, and the run is still recorded
    /// Cancelled with the reason. The order is made certain here by unlinking the host's stop from the lifetime.
    /// </summary>
    static async Task AFrameFlowCancelledAsHomeAssistantStopsIsNoFailure()
    {
        await using var rig = await Rig.Start("frame-stopping", ("camera.front", "idle"));
        CameraFrames.IntervalSecond = TimeSpan.FromMilliseconds(20);
        var front = Front();
        rig.Home.AnswerPictures = (_, _, ok, _) => ok(front);
        var saved = await rig.Save("annotated", Annotated(trackHistory: true));
        var automationId = saved["document"]!["automationId"]!.GetValue<string>();
        await rig.Started("annotated");
        var good = await Frame(rig);
        rig.Home.AnswerPictures = (_, _, _, _) => { };
        var (host, _) = Annotating(rig);
        await Task.Delay(50);
        // Unlinked before the request begins, so that neither the request nor the making of its frame sees a stop: only
        // the lifetime ends.
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(DefinitionHost).GetField("stopping", flags)!.SetValue(host, new CancellationTokenSource());
        var waiting = Frame(rig);
        await EntityTestHome.Until(() => rig.Home.Pictures().Length == 2, "the frame flow asks for its picture");
        var script = (UserScript)typeof(DefinitionHost).GetFields(flags).Single(field => field.FieldType == typeof(UserScript)).GetValue(host)!;
        script.CancelLifetime(CancelReasons.Stopping);
        Check(Same(await waiting, good), "the request gets the last good frame");
        await EntityTestHome.Until(() => rig.History(automationId).Any(r => r["status"]!.GetValue<string>() == "Cancelled"), "the run is recorded");
        var run = rig.History(automationId).Single(r => r["status"]!.GetValue<string>() == "Cancelled");
        Check(run["error"]?.GetValue<string>() == CancelReasons.Stopping, "the run says why it was cancelled: " + run.ToJsonString());
        Check(!Problems(rig).Any(line => line.Contains("could not make a frame", StringComparison.Ordinal)), "a flow cancelled as Home Assistant stops is no failure: " + Lines(Problems(rig)));
    }

    // ---- C# entity and C# device nodes ----

    // Where the classes below note what they do, so that two revisions (two assemblies) note on one list, in order.
    const string CallsSlot = "hass-sharp-entity-graph-calls";
    const string NoteMethod = "static void Note(string what) => ((System.Collections.Concurrent.ConcurrentQueue<string>?)System.AppDomain.CurrentDomain.GetData(\"" + CallsSlot + "\"))?.Enqueue(what);";
    /// <summary>A new list for the classes' notes, which they write from now on.</summary>
    static ConcurrentQueue<string> Calls()
    {
        var calls = new ConcurrentQueue<string>();
        AppDomain.CurrentDomain.SetData(CallsSlot, calls);
        return calls;
    }
    /// <summary>Notes each sync of the rig on <paramref name="calls"/> as <c>sync</c>, before it is answered.</summary>
    static void NoteSyncs(ConcurrentQueue<string> calls)
    {
        var sync = PyInterop.BeginSyncEntities;
        PyInterop.BeginSyncEntities = (json, ok, fail) => { calls.Enqueue("sync"); sync(json, ok, fail); };
    }
    /// <summary>The signature the compiler derives from the code of a node of the kind, as the editor stores it.</summary>
    static JsonObject Signature(string kind, string source)
    {
        var descriptor = new NodeLibraryCompiler(Diagnostics).Preview([], [new CustomNodeSource("c", source, kind)]).Snapshot.Custom["c"];
        Check(descriptor.Valid, $"the {kind} code compiles: " + string.Join(" | ", descriptor.Diagnostics.Select(d => d.Message)));
        return (JsonObject)descriptor.Signature!.DeepClone();
    }
    /// <summary>A C# entity or C# device node of the code, with the signature its code gives and <paramref name="arguments"/> (typed values) under arguments.</summary>
    static JsonObject CSharp(string kind, string id, string key, string source, JsonObject? arguments = null, string? name = null)
    {
        var more = new JsonObject { ["source"] = source, ["signature"] = Signature(kind, source), ["arguments"] = arguments ?? new JsonObject() };
        return kind == "csharp_entity" ? EntityNodeTests.CSharpEntity(id, key, more) : EntityNodeTests.CSharpDevice(id, key, name ?? key, more);
    }
    static JsonObject Nodes(JsonObject definition) => definition["logic"]!["graph"]!.AsObject();
    static JsonObject LastSync(Rig rig, string id) => JsonNode.Parse(rig.Home.Syncs(id).Last())!.AsObject();
    static JsonObject Described(JsonObject sync, string uniqueId) => sync["entities"]!.AsArray().Single(e => e!["uniqueId"]!.GetValue<string>() == uniqueId)!.AsObject();
    static string UniqueIds(JsonObject sync) => string.Join(",", sync["entities"]!.AsArray().Select(e => e!["uniqueId"]!.GetValue<string>()));

    /// <summary>A sensor that shows 21.5 °C once started.</summary>
    const string TempSource = """
        public class Temp : SharpSensor
        {
            public Temp() { Unit = "°C"; }
            protected override Task Start() { Value = 21.5; return Task.CompletedTask; }
        }
        """;
    /// <summary>A switch whose commands set its state, noting its Setup, Start and Stop after <paramref name="tag"/>.</summary>
    static string LampSource(string tag) => $$"""
        public class Lamp : SharpSwitch
        {
            {{NoteMethod}}
            protected override Task Setup() { Note("{{tag}}.Setup"); return Task.CompletedTask; }
            protected override Task Start() { Note("{{tag}}.Start"); return Task.CompletedTask; }
            protected override Task Stop() { Note("{{tag}}.Stop"); return Task.CompletedTask; }
            protected override Task TurnOn() { IsOn = true; return Task.CompletedTask; }
            protected override Task TurnOff() { IsOn = false; return Task.CompletedTask; }
        }
        """;
    /// <summary>A plug at an address with one relay, which notes its address as it is set up and as its relay is turned on; its device page shows its maker and model.</summary>
    static readonly string PlugSource = $$"""
        public class Plug : SharpDevice
        {
            {{NoteMethod}}
            [Setting("Address")]
            public string Address { get; set; } = "";
            public Plug() { Add(new SharpSwitch("relay", "Relay") { OnTurnOn = () => { Note("on " + Address); return Task.CompletedTask; } }); }
            protected override Task Setup()
            {
                Note("setup " + Address);
                Manufacturer = "Acme";
                Model = "Plug at " + Address;
                return Task.CompletedTask;
            }
        }
        """;

    /// <summary>
    /// A hub switch with its flows and a C# sensor in one definition: both are synced, each under its own unique id; the
    /// hub's Turn on runs its flow, and the sensor shows what its class set as it started. The sensor is named after its
    /// class's title, on no device.
    /// </summary>
    static async Task ACSharpEntityRunsBesideAHub()
    {
        await using var rig = await Rig.Start("csharp-beside");
        var definition = Relay();
        Nodes(definition)["nodes"]!.AsArray().Add(CSharp("csharp_entity", "temp", "temp", TempSource));
        var saved = await rig.Save("hall", definition);
        Check(saved["active"]!.GetValue<bool>() && saved["activationError"] == null, "the definition runs: " + saved.ToJsonString());
        var sync = LastSync(rig, "hall");
        Check(UniqueIds(sync) == "hall:relay,hall:temp", "both are synced: " + sync.ToJsonString());
        var temp = Described(sync, "hall:temp");
        Check(temp["domain"]!.GetValue<string>() == "sensor" && temp["key"]!.GetValue<string>() == "temp" && temp["device"] == null && temp["options"]!["unit"]!.GetValue<string>() == "°C",
            "the C# sensor is described by its class, on no device: " + temp.ToJsonString());
        Check(saved["document"]!["liveEntities"]!.AsArray().Select(e => e!["uniqueId"]!.GetValue<string>()).SequenceEqual(["hall:relay", "hall:temp"]), "both are live: " + saved["document"]!["liveEntities"]!.ToJsonString());
        await rig.Until("hall:temp", s => Available(s) && Is(s, "value", 21.5), "the sensor shows what its class set as it started");
        await rig.Api.EntityCommand("hall:relay", "turn_on", null);
        await rig.Until("hall:relay", s => Is(s, "is_on", true), "the hub's Turn on runs its flow");
    }

    /// <summary>A command for a C# switch reaches its class, as a class definition's does: no flow is looked for.</summary>
    static async Task ACSharpEntityTakesItsCommands()
    {
        await using var rig = await Rig.Start("csharp-commands");
        Calls();
        await rig.Save("hall", Definition("Hall", Graph("Hall", [CSharp("csharp_entity", "lamp", "lamp", LampSource("lamp"))])));
        await rig.Started("hall");
        await rig.Api.EntityCommand("hall:lamp", "turn_on", null);
        await rig.Until("hall:lamp", s => Available(s) && Is(s, "is_on", true), "turn on reaches the class");
        await rig.Api.EntityCommand("hall:lamp", "turn_off", null);
        await rig.Until("hall:lamp", s => Is(s, "is_on", false), "turn off reaches the class");
        var refused = await Throws<NotSupportedException>(() => rig.Api.EntityCommand("hall:lamp", "toggle", null), "a command the class does not take");
        Check(!refused.Message.Contains("has no flow", StringComparison.Ordinal), "the class refuses it, not the graph: " + refused.Message);
    }

    /// <summary>
    /// Two C# device nodes of one class at two addresses are two devices: each its own entry, with what its class's
    /// device page shows, and its own relay under a unique id of its device; each instance had its own address when it
    /// was set up, and a command reaches the relay of its own. Without one of them, the next revision syncs the other alone.
    /// </summary>
    static async Task TwoPlugsOfOneClassAreTwoDevices()
    {
        await using var rig = await Rig.Start("csharp-plugs");
        var calls = Calls();
        JsonObject Plug(string key, string address) => CSharp("csharp_device", key, key, PlugSource, new JsonObject { ["Address"] = Lit("string", address) }, name: key == "plug_a" ? "Desk plug" : "Shelf plug");
        var saved = await rig.Save("hall", Definition("Hall", Graph("Hall", [Plug("plug_a", "10.0.0.1"), Plug("plug_b", "10.0.0.2")])));
        Check(saved["active"]!.GetValue<bool>() && saved["activationError"] == null, "the definition runs: " + saved.ToJsonString());
        var sync = LastSync(rig, "hall");
        Check(sync["devices"]!.ToJsonString() == """[{"key":"plug_a","name":"Desk plug","manufacturer":"Acme","model":"Plug at 10.0.0.1","swVersion":null,"hwVersion":null,"configurationUrl":null,"suggestedArea":null,"via":null},"""
            + """{"key":"plug_b","name":"Shelf plug","manufacturer":"Acme","model":"Plug at 10.0.0.2","swVersion":null,"hwVersion":null,"configurationUrl":null,"suggestedArea":null,"via":null}]""",
            "two devices, each with what its class shows: " + sync["devices"]!.ToJsonString());
        Check(UniqueIds(sync) == "hall:plug_a:relay,hall:plug_b:relay"
            && Described(sync, "hall:plug_a:relay")["device"]!.GetValue<string>() == "plug_a" && Described(sync, "hall:plug_b:relay")["device"]!.GetValue<string>() == "plug_b"
            && Described(sync, "hall:plug_a:relay")["key"]!.GetValue<string>() == "relay",
            "each relay under its device's unique id, on its device: " + sync["entities"]!.ToJsonString());
        Check(calls.Where(c => c.StartsWith("setup", StringComparison.Ordinal)).SequenceEqual(["setup 10.0.0.1", "setup 10.0.0.2"]), "each had its own address before its Setup(): " + string.Join(", ", calls));
        await rig.Api.EntityCommand("hall:plug_b:relay", "turn_on", null);
        Check(calls.Where(c => c.StartsWith("on", StringComparison.Ordinal)).SequenceEqual(["on 10.0.0.2"]), "the command reached plug_b's relay alone: " + string.Join(", ", calls));
        await rig.Save("hall", Definition("Hall", Graph("Hall", [Plug("plug_a", "10.0.0.1")])), 1);
        var after = LastSync(rig, "hall");
        Check(after["devices"]!.AsArray().Select(d => d!["key"]!.GetValue<string>()).SequenceEqual(["plug_a"]) && UniqueIds(after) == "hall:plug_a:relay",
            "without plug_b its device and relay are no longer synced: " + after.ToJsonString());
    }

    /// <summary>
    /// An Entity node wired to no Device node, beside a C# device, stands alone: the graph has no device of its own (no
    /// <c>main</c>), and Home Assistant gets the C# device alone.
    /// </summary>
    static async Task AGraphWithCSharpNodesHasNoMainDevice()
    {
        await using var rig = await Rig.Start("csharp-no-main");
        var definition = Relay();
        Nodes(definition)["nodes"]!.AsArray().Add(CSharp("csharp_device", "plug", "plug", PlugSource, name: "Plug"));
        await rig.Save("hall", definition);
        var sync = LastSync(rig, "hall");
        Check(sync["devices"]!.AsArray().Select(d => d!["key"]!.GetValue<string>()).SequenceEqual(["plug"]) && !sync.ContainsKey("device"), "the C# device is the only device: " + sync.ToJsonString());
        Check(Described(sync, "hall:relay")["device"] == null && Described(sync, "hall:plug:relay")["device"]!.GetValue<string>() == "plug",
            "the hub stands alone, the plug's relay is on the plug: " + sync["entities"]!.ToJsonString());
    }

    /// <summary>
    /// A Device node that is part of a C# device declared after it: Home Assistant gets the C# device first, then the
    /// Device node, part of it.
    /// </summary>
    static async Task ADeviceCanBePartOfACSharpDeviceDeclaredLater()
    {
        await using var rig = await Rig.Start("csharp-part-of");
        var graph = Graph("Hall", [DeviceNode("strip", "Strip", new { via = new { device = "plug" } }), Entity("socket", "socket", "binary_sensor", new { name = "Socket" }, On(true)),
            CSharp("csharp_device", "plug", "plug", PlugSource, name: "Plug")]);
        Wire(graph, "strip", "socket");
        var saved = await rig.Save("hall", Definition("Hall", graph));
        Check(saved["active"]!.GetValue<bool>(), "the definition runs: " + saved.ToJsonString());
        var devices = LastSync(rig, "hall")["devices"]!.AsArray();
        Check(devices.Select(d => d!["key"]!.GetValue<string>()).SequenceEqual(["plug", "strip"]) && devices[1]!["via"]!.ToJsonString() == """{"device":"plug"}""",
            "the C# device comes first, and the strip is part of it: " + devices.ToJsonString());
    }

    /// <summary>A sensor whose code names it none, and one that names itself.</summary>
    const string ProbeSource = "public class Probe : SharpSensor { }";
    const string NamedSource = "public class Named : SharpSensor { public Named() { Name = \"Kitchen probe\"; } }";

    /// <summary>A C# entity wired to a Device node takes no name of its own (Home Assistant shows the device's) unless its code names it.</summary>
    static async Task ACSharpEntityOnADeviceTakesItsName()
    {
        await using var rig = await Rig.Start("csharp-on-device");
        var graph = Graph("Hall", [DeviceNode("hall", "Hall"), CSharp("csharp_entity", "probe", "probe", ProbeSource), CSharp("csharp_entity", "named", "named", NamedSource)]);
        Wire(graph, "hall", "probe");
        Wire(graph, "hall", "named");
        await rig.Save("hall", Definition("Hall", graph));
        var sync = LastSync(rig, "hall");
        Check(Described(sync, "hall:probe") is { } probe && probe["name"] == null && probe["device"]!.GetValue<string>() == "hall", "unnamed on the device: " + sync["entities"]!.ToJsonString());
        Check(Described(sync, "hall:named") is { } named && named["name"]!.GetValue<string>() == "Kitchen probe" && named["device"]!.GetValue<string>() == "hall", "its code's name wins: " + sync["entities"]!.ToJsonString());
    }

    /// <summary>
    /// A C# entity that stands alone is called its class's title when its code names it none, so Home Assistant never
    /// falls back to the definition's name for it; a name its code sets wins.
    /// </summary>
    static async Task ACSharpEntityAloneIsCalledItsTitle()
    {
        await using var rig = await Rig.Start("csharp-alone");
        var title = Signature("csharp_entity", ProbeSource)["title"]!.GetValue<string>();
        Check(title.Length > 0 && title != "Hall", "the class has a title of its own: " + title);
        await rig.Save("hall", Definition("Hall", Graph("Hall", [CSharp("csharp_entity", "probe", "probe", ProbeSource), CSharp("csharp_entity", "named", "named", NamedSource)])));
        var sync = LastSync(rig, "hall");
        Check(Described(sync, "hall:probe") is { } probe && probe["name"]?.GetValue<string>() == title && probe["device"] == null, "alone, it is called its title: " + sync["entities"]!.ToJsonString());
        Check(Described(sync, "hall:named")["name"]!.GetValue<string>() == "Kitchen probe", "its code's name wins: " + sync["entities"]!.ToJsonString());
    }

    /// <summary>
    /// The value of a wired secret Setting and a typed whole number are on the instance when its <c>Setup()</c> runs; the
    /// secret is in no log line, no sync, no push and no answer.
    /// </summary>
    static async Task SettingsAreAppliedBeforeSetup()
    {
        await using var rig = await Rig.Start("csharp-settings");
        var calls = Calls();
        const string secret = "s3cret-t0ken";
        var source = $$"""
            public class Router : SharpDevice
            {
                {{NoteMethod}}
                [Setting("Token", Secret = true)]
                public string Token { get; set; } = "";
                [Setting("Port")]
                public int Port { get; set; }
                public Router() { Add(new SharpSensor("uptime", "Uptime")); }
                protected override Task Setup() { Note($"setup {Token} {Port}"); return Task.CompletedTask; }
            }
            """;
        // A When started step, so that the definition has a recorded run and a trace to look into.
        var graph = Graph("Hall", [N("token", "setting", new { key = "token" }), CSharp("csharp_device", "router", "router", source, new JsonObject { ["Port"] = Lit("number", 8080) }, name: "Router"),
            N("start", "entity_setup")], Data("token", "router", "Token"));
        var saved = await rig.Save("hall", Definition("Hall", graph, [new JsonObject { ["key"] = "token", ["label"] = "Token", ["type"] = "string", ["secret"] = true, ["value"] = secret }]));
        Check(saved["active"]!.GetValue<bool>() && saved["activationError"] == null, "the definition runs: " + saved.ToJsonString());
        Check(calls.Contains($"setup {secret} 8080"), "both are set before Setup(): " + calls.Count);
        await rig.Started("hall");
        var read = await rig.Read("hall");
        Check(!saved.ToJsonString().Contains(secret, StringComparison.Ordinal) && !read.ToJsonString().Contains(secret, StringComparison.Ordinal), "no answer holds the secret");
        Check(!rig.Home.Syncs("hall").Any(s => s.Contains(secret, StringComparison.Ordinal)) && !rig.Home.Pushes().Any(p => p.Contains(secret, StringComparison.Ordinal))
            && !rig.Home.Logs().Any(l => l.Contains(secret, StringComparison.Ordinal)), "no sync, push or log line holds the secret");
        var runs = rig.History(saved["document"]!["automationId"]!.GetValue<string>());
        Check(runs.Length > 0, "the definition has a recorded run");
        Check(runs.All(run => !run.ToJsonString().Contains(secret, StringComparison.Ordinal) && !rig.Api.GetRunTrace(run["runId"]!.GetValue<string>()).Contains(secret, StringComparison.Ordinal)),
            "no run or trace holds the secret");
    }

    /// <summary>
    /// A C# node whose constructor throws or never returns (an instance's, or its class's static one), whose
    /// <c>[Setting]</c> setter throws, whose <c>Setup()</c> throws, or whose <c>Setup()</c> never returns fails the
    /// definition's start with a sentence naming the node, within the set-up time: nothing of the definition is
    /// registered or shown available, not even its Entity node's entity; another definition keeps running. The value a
    /// setter refused is not in the sentence.
    /// </summary>
    static async Task AClassThatCannotStartFailsTheDefinition()
    {
        await using var rig = await Rig.Start("csharp-failing");
        await rig.Save("relay", Relay());
        await rig.Started("relay");
        DefinitionHost.SetupTimeout = TimeSpan.FromMilliseconds(300);
        // What a constructor that never returns waits on: let go once the test has its answer (or after a while, so that a
        // host that waits for it without a bound answers, and fails the check, rather than holding up the suite).
        using var stuck = new ManualResetEventSlim();
        AppDomain.CurrentDomain.SetData(StuckSlot, stuck);
        _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => { try { stuck.Set(); } catch (ObjectDisposedException) { } }, TaskScheduler.Default);
        const string wait = "((System.Threading.ManualResetEventSlim)System.AppDomain.CurrentDomain.GetData(\"" + StuckSlot + "\")!).Wait();";
        foreach (var (id, source, title, arguments, error) in new (string, string, string, JsonObject?, string)[] {
            ("fuse", "public class Fuse : SharpSensor { public Fuse() { throw new InvalidOperationException(\"no wiring\"); } }", "Fuse", null, "Bomb: {0} could not be created: no wiring"),
            ("stuck", "public class Stuck : SharpSensor { public Stuck() { " + wait + " } }", "Stuck", null, "Bomb: {0} could not be created: its constructor did not finish within 0.3 s"),
            ("frozen", "public class Frozen : SharpSensor { static Frozen() { " + wait + " } }", "Frozen", null, "Bomb: {0} could not be created: its constructor did not finish within 0.3 s"),
            ("picky", "public class Picky : SharpSensor { [Setting(\"Level\")] public int Level { get => 0; set => throw new InvalidOperationException(\"the level is out of reach\"); } }", "Picky",
                new JsonObject { ["Level"] = Lit("number", 4711) }, "Bomb: {0}: the level is out of reach"),
            ("faulty", "public class Faulty : SharpSensor { protected override Task Setup() => throw new InvalidOperationException(\"no answer\"); }", "Faulty", null, "Bomb: {0}: no answer"),
            // A cancellation of its own (as an HttpClient time-out is) while the host is not stopping is a failure like any other.
            ("cancelled", "public class Cancelled : SharpSensor { protected override Task Setup() => Task.FromCanceled(new CancellationToken(true)); }", "Cancelled", null, "Bomb: {0}: A task was canceled."),
            ("hang", "public class Hang : SharpSensor { protected override Task Setup() => Task.Delay(Timeout.Infinite, Stopping); }", "Hang", null, "Bomb: {0}: Setup() did not finish within 0.3 s") })
        {
            var node = CSharp("csharp_entity", id, id, source, arguments);
            var called = node["config"]!["signature"]!["title"]!.GetValue<string>();
            var graph = Graph("Bomb", [Entity("lamp", "lamp", "binary_sensor", new { name = "Lamp" }, On(true)), node]);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var saved = await rig.Save("bomb_" + id, Definition("Bomb", graph));
            var expected = string.Format(System.Globalization.CultureInfo.InvariantCulture, error, called);
            Check(!saved["active"]!.GetValue<bool>() && saved["activationError"]?.GetValue<string>() == expected && Status(saved["document"]!.AsObject()) == "failed",
                $"{title}: the start fails naming the node ({expected}): " + saved.ToJsonString());
            Check(clock.Elapsed < TimeSpan.FromSeconds(2), $"{title}: the start fails within the set-up time: {clock.Elapsed}");
            Check(rig.Home.Syncs("bomb_" + id).Length == 0 && !rig.Home.Snapshots().Any(s => s["uniqueId"]!.GetValue<string>().StartsWith("bomb_" + id + ":", StringComparison.Ordinal) && Available(s)),
                $"{title}: nothing of it is registered or shown available");
            Check(!expected.Contains("4711", StringComparison.Ordinal) && !rig.Home.Logs().Any(l => l.Contains("4711", StringComparison.Ordinal)), $"{title}: no value a setter refused is quoted");
        }
        stuck.Set();
        await rig.Api.EntityCommand("relay:relay", "turn_on", null);
        await rig.Until("relay:relay", s => Available(s) && Is(s, "is_on", true), "the other definition keeps running");
    }
    const string StuckSlot = "hass-sharp-entity-graph-stuck";

    /// <summary>
    /// A hub and a C# switch, both turned on, then a new revision: the new class is set up and synced, then the old one
    /// stopped, then the new one started (as a class definition's hot replace); the hub's state is carried over and the
    /// C# switch's restored from what it last showed. After a restart both come back on.
    /// </summary>
    static async Task ACSharpEntityIsRestoredAndAHubCarriedOver()
    {
        await using var rig = await Rig.Start("csharp-restore");
        var calls = Calls();
        NoteSyncs(calls);
        JsonObject Revision(string tag)
        {
            var definition = Relay();
            Nodes(definition)["nodes"]!.AsArray().Add(CSharp("csharp_entity", "lamp", "lamp", LampSource(tag)));
            return definition;
        }
        await rig.Save("hall", Revision("old"));
        await rig.Started("hall");
        await rig.Api.EntityCommand("hall:relay", "turn_on", null);
        await rig.Api.EntityCommand("hall:lamp", "turn_on", null);
        await rig.Until("hall:relay", s => Is(s, "is_on", true), "the hub is on");
        await rig.Until("hall:lamp", s => Is(s, "is_on", true), "the C# switch is on");
        await rig.Flush();
        calls.Clear();
        var batches = rig.Batches;
        await rig.Save("hall", Revision("new"), 1);
        await rig.Started("hall");
        Check(calls.SequenceEqual(["new.Setup", "sync", "old.Stop", "new.Start"]), "hot replace order: " + string.Join(", ", calls));
        await rig.Until("hall:relay", s => Available(s) && Is(s, "is_on", true), "the hub's state is carried over", batches);
        await rig.Until("hall:lamp", s => Available(s) && Is(s, "is_on", true), "the C# switch's state is restored", batches);
        Check(!rig.Pushed("hall:lamp", batches).Any(s => !Is(s, "is_on", true)) && !rig.Pushed("hall:relay", batches).Any(s => !Available(s)), "nothing else is pushed for them: " + Lines(rig.Home.Pushes().Skip(batches)));
        batches = rig.Batches;
        await rig.Restart();
        await rig.Started("hall");
        await rig.Until("hall:relay", s => Available(s) && Is(s, "is_on", true), "the hub comes back on after a restart", batches);
        await rig.Until("hall:lamp", s => Available(s) && Is(s, "is_on", true), "the C# switch comes back on after a restart", batches);
        await rig.Api.EntityCommand("hall:lamp", "turn_off", null);
        await rig.Until("hall:lamp", s => Is(s, "is_on", false), "it takes commands after the restart", batches);
    }

    /// <summary>
    /// A C# device that adds two entities of one key fails with the sentence its class gives; a hub and a C# entity of one
    /// key, loaded past the validator, have one unique id and are refused with the host's key sentence. Two C# devices
    /// that each add an entity of one key are not (<see cref="TwoPlugsOfOneClassAreTwoDevices"/>).
    /// </summary>
    static async Task TwoEntitiesWithOneUniqueIdAreRefused()
    {
        await using var rig = await Rig.Start("csharp-unique");
        const string twice = "public class Twice : SharpDevice { public Twice() { Add(new SharpSwitch(\"relay\")); Add(new SharpSwitch(\"relay\")); } }";
        var saved = await rig.Save("twice", Definition("Twice", Graph("Twice", [CSharp("csharp_device", "pair", "pair", twice, name: "Pair")])));
        Check(saved["activationError"]?.GetValue<string>() == "Twice: Pair could not be created: Twice already has an entity with key 'relay'. (Parameter 'entity')",
            "the class's own sentence: " + saved.ToJsonString());
        // A hub lamp and a C# entity temp, saved; then the same compiled graph loaded as another definition whose C# entity's key is lamp.
        var graph = Graph("Dup", [Entity("lamp", "lamp", "binary_sensor", new { name = "Lamp" }, On(true)), CSharp("csharp_entity", "temp", "temp", TempSource)]);
        var document = (await rig.Save("dup", Definition("Dup", graph)))["document"]!;
        var running = rig.Scripts.GetUserScripts().Single(s => s.CompiledScript.Definition?.Id == "dup").CompiledScript;
        var compiled = await new CodeCompiler(Diagnostics).CompileSingleFile(Path.Combine(rig.Home.Root, "entity_dup_twin.cs"), document["source"]!.GetValue<string>());
        compiled.DesignatedRootType = running.DesignatedRootType;
        compiled.VisualId = "dup_twin";
        compiled.VisualTriggers = running.VisualTriggers;
        compiled.VisualGraph = running.VisualGraph;
        compiled.VisualCatalog = running.VisualCatalog;
        var given = running.Definition!;
        compiled.Definition = new("dup_twin", "Dup twin", null, given.Settings)
            { Entities = given.Entities, Devices = given.Devices, Roots = given.Roots, Components = [.. given.Components.Select(c => c with { Key = "lamp" })] };
        Exception? failure = null;
        await rig.Scripts.LoadUserScripts([compiled], (_, error) => failure = error);
        Check(failure?.Message == "Dup twin: two entities have the key 'lamp'", "the host's key sentence: " + failure?.Message);
        Check(rig.Home.Syncs("dup_twin").Length == 0, "nothing of it is registered");
    }

    /// <summary>A strip whose one relay keeps the state its commands set.</summary>
    const string StripSource = """
        public class Strip : SharpDevice
        {
            public Strip()
            {
                SharpSwitch relay = null!;
                relay = Add(new SharpSwitch("relay", "Relay")
                {
                    OnTurnOn = () => { relay.IsOn = true; return Task.CompletedTask; },
                    OnTurnOff = () => { relay.IsOn = false; return Task.CompletedTask; },
                });
            }
        }
        """;
    static IEnumerable<string> Keys(JsonNode? list) => list!.AsArray().Select(e => e!["key"]!.GetValue<string>());

    /// <summary>
    /// The read and list answers of a running definition with a Device node, two Entity nodes, two C# entities and a C#
    /// device: <c>entities</c> lists the Entity nodes', then the C# entities' (<c>{key, domain, name: null, device,
    /// csharp: true}</c>), and <c>devices</c> (a count in the list) the Device node and the C# device. What the C#
    /// device's code adds is answered as Home Assistant has it, under a unique id of three parts: the C# device <c>plug</c>'s
    /// relay is <c>hall:plug:relay</c>, never the Entity node <c>plug</c>'s <c>hall:plug</c>. Each entity runs once.
    /// </summary>
    static async Task TheAnswersListWhatTheCSharpNodesMake()
    {
        await using var rig = await Rig.Start("csharp-answers");
        var graph = Graph("Hall", [DeviceNode("hall", "Hall"), Entity("lamp", "lamp", "binary_sensor", new { name = "Lamp" }, On(true)),
            Entity("plug", "plug", "binary_sensor", new { name = "Plug sensor" }, On(false)), CSharp("csharp_entity", "temp", "temp", TempSource),
            CSharp("csharp_entity", "probe", "probe", ProbeSource), CSharp("csharp_device", "plug_node", "plug", StripSource, name: "Plug")]);
        Wire(graph, "hall", "lamp");
        Wire(graph, "hall", "temp");
        var saved = await rig.Save("hall", Definition("Hall", graph));
        Check(saved["active"]!.GetValue<bool>() && saved["activationError"] == null, "the definition runs: " + saved.ToJsonString());
        await rig.Started("hall");
        var sync = LastSync(rig, "hall");
        Check(UniqueIds(sync) == "hall:lamp,hall:plug,hall:temp,hall:probe,hall:plug:relay", "each entity is synced once: " + sync["entities"]!.ToJsonString());
        var read = await rig.Read("hall");
        var listed = (await rig.Request(new { operation = "list" }))["result"]!.AsArray().Single(d => d!["id"]!.GetValue<string>() == "hall")!.AsObject();
        var csharp = new JsonArray(
            new JsonObject { ["key"] = "temp", ["domain"] = "sensor", ["name"] = null, ["device"] = "hall", ["csharp"] = true },
            new JsonObject { ["key"] = "probe", ["domain"] = "sensor", ["name"] = null, ["device"] = null, ["csharp"] = true });
        foreach (var (what, answer) in new[] { ("read", read), ("list", listed) })
        {
            var entities = answer["entities"]!.AsArray();
            Check(Keys(entities).SequenceEqual(["lamp", "plug", "temp", "probe"]), $"its {what} answer lists the Entity nodes, then the C# entities: " + entities.ToJsonString());
            Check(entities[0]!["device"]!.GetValue<string>() == "hall" && entities[1]!["device"] == null && !entities[0]!.AsObject().ContainsKey("csharp"), $"its {what} answer's hubs: " + entities.ToJsonString());
            Check(JsonNode.DeepEquals(new JsonArray([entities[2]!.DeepClone(), entities[3]!.DeepClone()]), csharp), $"its {what} answer's C# entities: " + entities.ToJsonString());
            Check(Keys(answer["liveEntities"]).SequenceEqual(["lamp", "plug", "temp", "probe", "relay"])
                && answer["liveEntities"]!.AsArray().Select(e => e!["uniqueId"]!.GetValue<string>()).SequenceEqual(["hall:lamp", "hall:plug", "hall:temp", "hall:probe", "hall:plug:relay"]),
                $"its {what} answer's live entities, the C# device's relay under its device: " + answer["liveEntities"]!.ToJsonString());
        }
        Check(read["devices"]!.AsArray().Select(d => $"{d!["key"]}:{d["csharp"]?.GetValue<bool>() == true}").SequenceEqual(["hall:False", "plug:True"]), "the read answer's devices: " + read["devices"]!.ToJsonString());
        Check(listed["devices"]!.GetValue<int>() == 2, "the list counts the Device node and the C# device: " + listed["devices"]!.ToJsonString());
        var text = read["entities"]!.ToJsonString() + read["devices"]!.ToJsonString();
        Check(!text.Contains("relay", StringComparison.Ordinal) && !text.Contains("source", StringComparison.Ordinal) && !text.Contains("signature", StringComparison.Ordinal),
            "the C# device's entities and the nodes' code are listed in neither: " + text);
    }

    /// <summary>
    /// Saved, while off, with another domain under the same key, a C# entity is another entity: its last state is
    /// forgotten, as an Entity node's is, and so is that of the Entity node <c>plug</c> given another domain; one of the
    /// same domain keeps its own. The relay the C# device <c>plug</c> adds, <c>hall:plug:relay</c>, is no entity a
    /// document lists and keeps its state: nothing forgets it by the Entity node <c>plug</c>'s <c>hall:plug</c>.
    /// </summary>
    static async Task ACSharpEntityGivenAnotherDomainIsForgotten()
    {
        await using var rig = await Rig.Start("csharp-retyped");
        Calls();
        JsonObject Revision(bool retyped)
        {
            var plug = retyped ? Entity("plug", "plug", "binary_sensor", new { name = "Plug" }, On(true)) : Entity("plug", "plug", "switch", new { name = "Plug" });
            var nodes = new List<JsonObject> { plug, CSharp("csharp_entity", "lamp", "lamp", retyped ? TempSource : LampSource("lamp")), CSharp("csharp_entity", "fan", "fan", LampSource("fan")),
                CSharp("csharp_device", "plug_node", "plug", StripSource, name: "Plug strip") };
            var edges = new List<JsonObject>();
            if (!retyped) { nodes.Add(SetState("set_on", "plug", On(true))); edges.Add(Pin("plug", "command:turn_on", "set_on")); }
            return Definition("Hall", Graph("Hall", [.. nodes], [.. edges]));
        }
        await rig.Save("hall", Revision(retyped: false));
        await rig.Started("hall");
        foreach (var id in new[] { "hall:plug", "hall:lamp", "hall:fan", "hall:plug:relay" })
        {
            await rig.Api.EntityCommand(id, "turn_on", null);
            await rig.Until(id, s => Is(s, "is_on", true), id + " is on");
        }
        var states = rig.Scripts.States!;
        Check(new[] { "hall:plug", "hall:lamp", "hall:fan", "hall:plug:relay" }.All(states.Ids().Contains), "each is remembered: " + string.Join(", ", states.Ids()));
        await rig.Ok(new { operation = "set_enabled", id = "hall", enabled = false });
        var saved = await rig.Save("hall", Revision(retyped: true), 1);
        var ids = rig.Scripts.States!.Ids().ToArray();
        Check(!ids.Contains("hall:lamp") && !ids.Contains("hall:plug"), "the C# entity and the Entity node given another domain are forgotten: " + string.Join(", ", ids));
        Check(ids.Contains("hall:fan") && ids.Contains("hall:plug:relay"), "the C# switch of the same domain and the C# device's relay keep theirs: " + string.Join(", ", ids));
        Check(saved["document"]!["entities"]!.AsArray().Single(e => e!["key"]!.GetValue<string>() == "lamp")!["domain"]!.GetValue<string>() == "sensor", "the C# entity is a sensor now: " + saved.ToJsonString());
    }
}
