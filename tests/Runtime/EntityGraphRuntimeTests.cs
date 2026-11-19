using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>
/// What the visual runtime does with an entity definition's graph: a State works out its inputs and hands them to the
/// definition, marking the entity available, or unavailable when working them out fails; Set state and Set available
/// steps call the definition; a Setting is the definition's value, of its own type. The run reaches its definition
/// through the run in progress (<see cref="InvocationContext.Definition"/>), which these tests fill with a fake; an
/// entity node run without one fails. Has state and Current state read Home Assistant as Read entity does, the state the
/// change that started the run carries first, and work in an automation, which has no definition.
/// </summary>
static class EntityGraphRuntimeTests
{
    /// <summary>
    /// The definition a run reaches: each call, in order, as text; settings by key, those in <see cref="Secrets"/> secret.
    /// With <see cref="FailUnavailable"/>, marking an entity unavailable fails, as a host stopping might.
    /// </summary>
    sealed class FakeDefinition : IDefinitionRuntime
    {
        public readonly List<string> Calls = [];
        /// <summary>Each state value handed over as hidden, as <c>&lt;entity&gt;.&lt;key&gt;</c>.</summary>
        public readonly List<string> Hidden = [];
        public readonly Dictionary<string, object?> Settings = new(StringComparer.Ordinal);
        public readonly HashSet<string> Secrets = new(StringComparer.Ordinal);
        public bool FailUnavailable;
        public void SetState(string entityKey, IReadOnlyDictionary<string, object?> values, IReadOnlySet<string> hidden)
        {
            Calls.Add($"state {entityKey} " + string.Join(" ", values.Select(p => p.Key + "=" + Typed(p.Value))));
            Hidden.AddRange(hidden.Order(StringComparer.Ordinal).Select(key => entityKey + "." + key));
        }
        public void SetAvailable(string entityKey, bool available)
        {
            Calls.Add($"available {entityKey} {(available ? "yes" : "no")}");
            if (!available && FailUnavailable) throw new InvalidOperationException("The definition is stopping");
        }
        public object? Setting(string key) => Settings.TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);
        public bool IsSecret(string key) => Secrets.Contains(key);
        /// <summary>The downloads a test gives the definition; without, a run here makes none (see EntityToolsTests).</summary>
        public SharpFiles? Downloads;
        public SharpFiles Files => Downloads ?? throw new InvalidOperationException("This definition downloads nothing");
        /// <summary>The definition's id, which a picture is taken under.</summary>
        public string DefinitionId { get; init; } = "fake";
        /// <summary>Each frame shown, as the picture was handed over.</summary>
        public readonly List<SharpImage> Frames = [];
        public void ShowFrame(string entityKey, SharpImage image)
        {
            Calls.Add($"frame {entityKey} {image.Drawings} drawing(s)");
            Frames.Add(image);
        }
    }
    /// <summary>The trace documents a run writes; the last one is its final trace.</summary>
    sealed class TraceSink : IRunTraceSink
    {
        readonly List<string> writes = [];
        public void Write(string json, int eventCount, bool truncated, bool final, bool immediate = false) { lock (writes) writes.Add(json); }
        public JsonObject Final { get { lock (writes) return JsonNode.Parse(writes[^1])!.AsObject(); } }
        /// <summary>The one event of the node in the final trace.</summary>
        public JsonObject Event(string node) => Final["events"]!.AsArray().Select(e => e!.AsObject()).Single(e => e["node"]!.GetValue<string>() == node);
    }
    /// <summary>A value with its .NET type, so a number given as text is told apart: <c>double:21.5</c>, <c>string:21.5</c>, <c>null</c>.</summary>
    static string Typed(object? value) => value switch
    {
        null => "null",
        bool b => "bool:" + (b ? "true" : "false"),
        double d => "double:" + d.ToString(CultureInfo.InvariantCulture),
        string s => "string:" + s,
        _ => value.GetType().Name + ":" + Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    /// <summary>An entity definition's graph: the nodes, whose Entity nodes declare its entities, the wires, and a definition of these settings.</summary>
    static JsonObject Entity(JsonObject[] nodes, JsonObject[] edges, params (string Key, string Type)[] settings) => new()
    {
        ["version"] = 5, ["name"] = "Entity", ["nodes"] = new JsonArray(nodes), ["edges"] = new JsonArray(edges),
        ["definition"] = new JsonObject
        {
            ["settings"] = new JsonArray(settings.Select(s => (JsonNode)new JsonObject { ["key"] = s.Key, ["type"] = s.Type }).ToArray()),
        },
    };
    static readonly object Unset = new { kind = "unset" };
    static JsonObject Is(string id, string entity, string state = "on") => N(id, "entity_is", new JsonObject { ["entity"] = Lit("entity_id", entity), ["state"] = Lit("string", state) });
    static JsonObject Current(string id, string entity) => N(id, "entity_current", new JsonObject { ["entity"] = Lit("entity_id", entity) });
    /// <summary>The Entity node <paramref name="id"/> of the key and type whose state is worked out from <paramref name="values"/> and the values wired to it.</summary>
    static JsonObject State(string id, string key, string domain, JsonObject? values = null) => Hub(id, key, domain, values);
    static JsonObject SetState(string id, string entity, JsonObject? values = null)
    {
        var config = values ?? new JsonObject(); config["entity"] = entity;
        return N(id, "set_entity_state", config);
    }
    static JsonObject Setting(string id, string key) => N(id, "setting", new { key });
    /// <summary>The acceptance graph: a binary sensor on while either of two motion sensors is.</summary>
    static JsonObject AnyMotion() => Entity(
        [Is("a", "binary_sensor.motion_a"), Is("b", "binary_sensor.motion_b"), N("or", "or", new { left = Unset, right = Unset }), State("s", "any_motion", "binary_sensor")],
        [D("a", "or", "left"), D("b", "or", "right"), D("or", "s", "is_on")]);

    /// <summary>A graph's class, compiled once, whose root methods each test runs as often as it needs.</summary>
    sealed class Compiled : IDisposable
    {
        readonly CompiledUserScript script;
        readonly Automation instance;
        Compiled(CompiledUserScript script) { this.script = script; instance = (Automation)Activator.CreateInstance(script.Assembly.GetType("VisualAutomation")!)!; }
        /// <summary>The compiled assembly, for a test to reach a Custom C# node's own statics.</summary>
        public System.Reflection.Assembly Assembly => script.Assembly;
        public static async Task<Compiled> Of(JsonObject graph, string what)
        {
            graph = Derived(graph);
            var preview = new VisualGenerator(Diagnostics).Preview(graph);
            Check(preview.Valid, $"{what} previews as valid: {JsonSerializer.Serialize(preview.Diagnostics)}");
            return new(await new CodeCompiler(Diagnostics).CompileSingleFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cs"), preview.Source));
        }
        /// <summary>
        /// Runs the root's method as the runner would: under a run of its own that reaches <paramref name="definition"/>
        /// (none when null), with the cancellation <paramref name="token"/>, traced into <paramref name="sink"/> when given.
        /// </summary>
        public Task Run(string root, IDefinitionRuntime? definition, CancellationToken token = default, IRunTraceSink? sink = null) =>
            Invoke(VisualNodeCatalog.EffectiveTriggerMethod(new JsonObject { ["id"] = root }), definition, token, sink, TriggerPayload.Manual());
        /// <summary>
        /// Runs the flow of the Entity node <paramref name="root"/> on <paramref name="port"/> (its method,
        /// <see cref="VisualNodeCatalog.FlowMethod"/>) as <see cref="Run(string, IDefinitionRuntime?, CancellationToken, IRunTraceSink?)"/>
        /// runs a root, with <paramref name="payload"/> as its trigger (a command's, with its data), else a manual one.
        /// </summary>
        public Task Run(string root, string port, IDefinitionRuntime? definition, JsonElement? payload = null, IRunTraceSink? sink = null, CancellationToken token = default) =>
            Invoke(VisualNodeCatalog.FlowMethod(new JsonObject { ["id"] = root }, port), definition, token, sink, payload ?? TriggerPayload.Manual());
        async Task Invoke(string method, IDefinitionRuntime? definition, CancellationToken token, IRunTraceSink? sink, JsonElement payload)
        {
            var prior = InvocationContext.Current;
            Task task;
            try
            {
                InvocationContext.Current = new InvocationContext("run-" + Guid.NewGuid().ToString("N"), InvocationCause.Entity, null, payload) { Definition = definition, TraceSink = sink };
                instance._cancellationToken.Value = token;
                task = (Task)instance.GetType().GetMethod(method)!.Invoke(instance, null)!;
            }
            finally { InvocationContext.Current = prior; instance._cancellationToken.Value = default; }
            await task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        public void Dispose() => script.Unload();
    }

    /// <summary>Home Assistant's states, by entity id: an id it does not have is missing.</summary>
    static readonly Dictionary<string, string> States = new(StringComparer.Ordinal);
    static void Same(IReadOnlyList<string> got, IReadOnlyList<string> want, string what) =>
        Check(got.SequenceEqual(want), $"{what}:\n  expected [{string.Join(" | ", want)}]\n  got      [{string.Join(" | ", got)}]");
    static async Task<Exception> Fails(Func<Task> run, string what)
    {
        try { await run(); }
        catch (Exception error) { return error; }
        throw new Exception($"{what}: the run did not fail");
    }

    [RuntimeSuite("--entity-graph-runtime", Order = 11)]
    public static async Task Run()
    {
        var entity = PyInterop.Entity; var log = PyInterop.Log; var trigger = UserScriptManager.CurrentTrigger.Value; var cameras = PyInterop.BeginCameraSnapshot;
        PyInterop.Entity = id => States.TryGetValue(id, out var state) ? new() { EntityId = id, State = state } : null;
        var failures = new List<string>();
        try
        {
            // Every test runs, and each failure is reported under its name.
            foreach (var (name, test) in new (string, Func<Task>)[] {
                (nameof(StateNodeSetsStateFromItsInputs), StateNodeSetsStateFromItsInputs),
                (nameof(OrOfTwoSourcesFollowsEither), OrOfTwoSourcesFollowsEither),
                (nameof(StateNodeMarksTheEntityUnavailableWhenASourceIsMissing), StateNodeMarksTheEntityUnavailableWhenASourceIsMissing),
                (nameof(CancelledStateDoesNotMarkTheEntityUnavailable), CancelledStateDoesNotMarkTheEntityUnavailable),
                (nameof(StateCancelledBeforePublishingMakesNoCall), StateCancelledBeforePublishingMakesNoCall),
                (nameof(AFailingUnavailableKeepsTheStatesOwnFailure), AFailingUnavailableKeepsTheStatesOwnFailure),
                (nameof(SecretSettingIsRedactedInTheTrace), SecretSettingIsRedactedInTheTrace),
                (nameof(ASecretIsFollowedThroughALoopAndIntoACondition), ASecretIsFollowedThroughALoopAndIntoACondition),
                (nameof(EntityIsIsFalseForAMissingEntity), EntityIsIsFalseForAMissingEntity),
                (nameof(EntityIsReadsTheTriggeringEntitysFreshState), EntityIsReadsTheTriggeringEntitysFreshState),
                (nameof(SetStateAndSetAvailableStepsCallTheDefinition), SetStateAndSetAvailableStepsCallTheDefinition),
                (nameof(StateSetBeforeALaterFailingStepStaysSet), StateSetBeforeALaterFailingStepStaysSet),
                (nameof(SettingGivesItsValueAsItsType), SettingGivesItsValueAsItsType),
                (nameof(SensorStateAcceptsNumberAndText), SensorStateAcceptsNumberAndText),
                (nameof(EmptyStateValueIsPassedThrough), () => { EmptyStateValueIsPassedThrough(); return Task.CompletedTask; }),
                (nameof(EntityNodesFailWithoutADefinition), EntityNodesFailWithoutADefinition),
                (nameof(EntityIsWorksInAnAutomation), EntityIsWorksInAnAutomation),
                (nameof(SetStateHandsOverOnlyWhatItIsGiven), SetStateHandsOverOnlyWhatItIsGiven),
                (nameof(APasswordTextsValueOfAnotherTypeIsNotQuoted), APasswordTextsValueOfAnotherTypeIsNotQuoted),
                (nameof(HttpRequestGetsText), HttpRequestGetsText),
                (nameof(HttpRequestPostsJsonAndReadsJson), HttpRequestPostsJsonAndReadsJson),
                (nameof(HttpRequestFailsOnAnErrorStatus), HttpRequestFailsOnAnErrorStatus),
                (nameof(HttpRequestTimesOutAndIsCancelledWithItsRun), HttpRequestTimesOutAndIsCancelledWithItsRun),
                (nameof(HttpRequestRefusesWhatItCannotUse), HttpRequestRefusesWhatItCannotUse),
                (nameof(HttpRequestHidesASecretsInputs), HttpRequestHidesASecretsInputs),
                (nameof(HttpRequestKeepsItsResponseOutOfRunHistory), HttpRequestKeepsItsResponseOutOfRunHistory),
                (nameof(CompareOfASecretTextAndANumberDoesNotQuoteIt), CompareOfASecretTextAndANumberDoesNotQuoteIt),
                (nameof(HttpRequestNamesAHostThatCannotBeConnectedTo), HttpRequestNamesAHostThatCannotBeConnectedTo),
                (nameof(HttpRequestHeadGivesNoBody), HttpRequestHeadGivesNoBody),
                (nameof(DownloadFileHidesASecretsAddressAndChecksum), DownloadFileHidesASecretsAddressAndChecksum),
                (nameof(HttpRequestFollowsRedirectsWithoutHandingHeadersToAnotherHost), HttpRequestFollowsRedirectsWithoutHandingHeadersToAnotherHost),
                (nameof(HttpRequestRefusesAHeaderBeforeSending), HttpRequestRefusesAHeaderBeforeSending),
                (nameof(HttpRequestReadsAnEmptyOrCompressedAnswer), HttpRequestReadsAnEmptyOrCompressedAnswer),
                (nameof(NothingKeepsACookie), NothingKeepsACookie),
                (nameof(DrawingStepsDrawOnACopyOfThePicture), DrawingStepsDrawOnACopyOfThePicture),
                (nameof(TakeAPictureFailsToItsErrorWireAndIsCancelledWithItsRun), TakeAPictureFailsToItsErrorWireAndIsCancelledWithItsRun),
                (nameof(ADrawingStepRoundsAPositionThatIsNotWhole), ADrawingStepRoundsAPositionThatIsNotWhole),
                (nameof(APictureInAnObjectIsItsShortText), APictureInAnObjectIsItsShortText),
                (nameof(APictureIsTracedAndLoggedAsAShortText), APictureIsTracedAndLoggedAsAShortText),
                (nameof(ShowFrameHandsThePictureToTheDefinition), ShowFrameHandsThePictureToTheDefinition),
                (nameof(AnEntityNodeWorksOutItsState), AnEntityNodeWorksOutItsState),
                (nameof(ASetValueOnTheNodeIsItsState), ASetValueOnTheNodeIsItsState),
                (nameof(AFailingValueMakesTheEntityUnavailable), AFailingValueMakesTheEntityUnavailable),
                (nameof(ACommandRunsTheStepsOnItsPin), ACommandRunsTheStepsOnItsPin),
                (nameof(ACommandsValueIsReadOnItsFlow), ACommandsValueIsReadOnItsFlow),
                (nameof(AFrameRunsTheStepsOnItsPin), AFrameRunsTheStepsOnItsPin),
                (nameof(TheRootsOfANodeAreItsWiredFlows), () => { TheRootsOfANodeAreItsWiredFlows(); return Task.CompletedTask; }),
                (nameof(AGraphOfANodeWithoutFlowsCompiles), AGraphOfANodeWithoutFlowsCompiles),
                (nameof(HistoryFollowsTheNodesSwitches), () => { HistoryFollowsTheNodesSwitches(); return Task.CompletedTask; }) })
            {
                States.Clear(); UserScriptManager.CurrentTrigger.Value = null;
                try { await test(); }
                catch (Exception e) { failures.Add($"{name}: {e.Message}"); }
            }
        }
        finally { PyInterop.Entity = entity; PyInterop.Log = log; PyInterop.BeginCameraSnapshot = cameras; UserScriptManager.CurrentTrigger.Value = trigger; States.Clear(); }
        if (failures.Count > 0) throw new Exception($"Entity graph runtime: {failures.Count} failing:\n" + string.Join("\n", failures));
        Console.WriteLine("PASS entity graph runtime: an Entity node's state sets state and availability, Set state, Set available and Setting reach the definition, Has state and Current state read Home Assistant, entity nodes need a definition");
        Console.WriteLine("PASS entity graph runtime: HTTP request gets text, posts and reads JSON, fails on an error status, a timeout, a large or misshapen answer by messages naming only the host, is cancelled with its run, hides a secret's inputs and a hidden response; Compare does not quote a secret");
        Console.WriteLine("PASS entity graph runtime: Take a picture asks Home Assistant under its definition's id, fails to its error wire and is cancelled with its run; Draw rectangle and Draw text draw on a copy and round a position that is not whole, a picture is traced and logged as a short text, never its bytes, and goes into an object as that text; a camera's frame flow runs the steps after its pin and Show frame hands its picture to the definition");
        Console.WriteLine("PASS entity graph runtime: an Entity node works out its state from its wired and set values, or marks its entity unavailable, runs the steps on each wired command and its frame pin with the command's values, and its wired flows are its roots");
        Console.WriteLine("PASS entity graph runtime: HTTP request names a host it cannot connect to, gives no body for a HEAD, follows redirects itself without handing its headers to another host, refuses a header before sending, and reads an empty or compressed answer; Download file hides a secret's address and checksum");
    }

    /// <summary>A State hands its inputs to the definition, then marks the entity available.</summary>
    static async Task StateNodeSetsStateFromItsInputs()
    {
        using var compiled = await Compiled.Of(Entity([State("s", "door", "binary_sensor", new JsonObject { ["is_on"] = Lit("boolean", true) })], []), "a State of a chosen value");
        var definition = new FakeDefinition();
        await compiled.Run("s", "state", definition);
        Same(definition.Calls, ["state door is_on=bool:true", "available door yes"], "the State sets the state, then the availability");
    }

    /// <summary>The acceptance graph follows either source, over the four on/off combinations.</summary>
    static async Task OrOfTwoSourcesFollowsEither()
    {
        using var compiled = await Compiled.Of(AnyMotion(), "the derived binary sensor");
        foreach (var a in new[] { false, true }) foreach (var b in new[] { false, true })
        {
            States["binary_sensor.motion_a"] = a ? "on" : "off"; States["binary_sensor.motion_b"] = b ? "on" : "off";
            var definition = new FakeDefinition();
            await compiled.Run("s", "state", definition);
            Same(definition.Calls, [$"state any_motion is_on=bool:{(a || b ? "true" : "false")}", "available any_motion yes"], $"motion A {States["binary_sensor.motion_a"]}, motion B {States["binary_sensor.motion_b"]}");
        }
    }

    /// <summary>
    /// A State whose Current state source does not exist marks the entity unavailable, and the run fails with the
    /// unavailable message; once the source appears, a new run sets the state and marks it available again.
    /// </summary>
    static async Task StateNodeMarksTheEntityUnavailableWhenASourceIsMissing()
    {
        var graph = Entity(
            [Current("cur", "sensor.outside"), N("f", "entity_field", new { entity = Unset, field = "state", type = "number" }), State("s", "copy", "sensor")],
            [D("cur", "f", "entity"), D("f", "s", "value")]);
        using var compiled = await Compiled.Of(graph, "a sensor copying another");
        var definition = new FakeDefinition();
        var error = await Fails(() => compiled.Run("s", "state", definition), "a State reading a missing entity");
        Check(error.Message.Contains("Entity 'sensor.outside' is unavailable.", StringComparison.Ordinal), "the run fails with the unavailable message: " + error.Message);
        Same(definition.Calls, ["available copy no"], "a State that cannot be worked out marks its entity unavailable, and sets no state");
        States["sensor.outside"] = "21.5";
        await compiled.Run("s", "state", definition);
        Same(definition.Calls, ["available copy no", "state copy value=double:21.5", "available copy yes"], "once the source appears, the State sets the state and marks the entity available");
    }

    /// <summary>A State cancelled while working out its inputs (a newer change restarts it) is not a failure: the entity stays as it was.</summary>
    static async Task CancelledStateDoesNotMarkTheEntityUnavailable()
    {
        var wait = N("wait", "custom", new JsonObject
        {
            ["source"] = "public static class CustomNode\n{\n    [Node(\"Wait\", Pure = true)]\n    public static async Task<bool> Run(CancellationToken token)\n    {\n        await Task.Delay(-1, token);\n        return true;\n    }\n}",
            ["arguments"] = new JsonObject(),
        });
        using var compiled = await Compiled.Of(Entity([wait, State("s", "door", "binary_sensor")], [D("wait", "s", "is_on", "result")]), "a State waiting on its input");
        var definition = new FakeDefinition();
        using var cancel = new CancellationTokenSource(); cancel.CancelAfter(100);
        var error = await Fails(() => compiled.Run("s", "state", definition, token: cancel.Token), "a cancelled State");
        Check(error is OperationCanceledException, "the cancelled run ends cancelled, not failed: " + error.GetType().Name + ": " + error.Message);
        Same(definition.Calls, [], "a cancelled State neither sets the state nor marks the entity unavailable");
    }

    /// <summary>
    /// A State whose run is cancelled after its inputs were worked out, but before it hands them over, makes no call: a
    /// run already superseded or stopped does not publish. Its one value node cancels the run and returns normally.
    /// </summary>
    static async Task StateCancelledBeforePublishingMakesNoCall()
    {
        var cancel = N("cancel", "custom", new JsonObject
        {
            ["source"] = "public static class CustomNode\n{\n    public static CancellationTokenSource? Source;\n    [Node(\"Cancel\", Pure = true)]\n    public static bool Run()\n    {\n        Source!.Cancel();\n        return true;\n    }\n}",
            ["arguments"] = new JsonObject(),
        });
        using var compiled = await Compiled.Of(Entity([cancel, State("s", "door", "binary_sensor")], [D("cancel", "s", "is_on", "result")]), "a State cancelled by its input");
        using var source = new CancellationTokenSource();
        var field = compiled.Assembly.GetTypes().Select(t => t.GetField("Source")).Single(f => f != null)!;
        field.SetValue(null, source);
        var definition = new FakeDefinition();
        var error = await Fails(() => compiled.Run("s", "state", definition, token: source.Token), "a State cancelled before publishing");
        Check(error is OperationCanceledException, "the run ends cancelled: " + error.GetType().Name + ": " + error.Message);
        Same(definition.Calls, [], "a State cancelled before publishing neither sets the state nor changes the availability");
    }

    /// <summary>
    /// When marking the entity unavailable fails too (a definition stopping), the run still fails with the State's own
    /// error; the second failure is logged, not lost.
    /// </summary>
    static async Task AFailingUnavailableKeepsTheStatesOwnFailure()
    {
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>(); PyInterop.Log = (_, m) => messages.Enqueue(m);
        var graph = Entity(
            [Current("cur", "sensor.outside"), N("f", "entity_field", new { entity = Unset, field = "state", type = "number" }), State("s", "copy", "sensor")],
            [D("cur", "f", "entity"), D("f", "s", "value")]);
        using var compiled = await Compiled.Of(graph, "a sensor copying a missing one");
        var definition = new FakeDefinition { FailUnavailable = true };
        var error = await Fails(() => compiled.Run("s", "state", definition), "a State whose unavailable marking fails");
        Check(error.Message.Contains("Entity 'sensor.outside' is unavailable.", StringComparison.Ordinal) && error.GetBaseException().Message == "Entity 'sensor.outside' is unavailable.",
            "the run fails with the State's own error, as the run record shows it: " + error.Message + " / " + error.GetBaseException().Message);
        Same(definition.Calls, ["available copy no"], "the State tried to mark its entity unavailable");
        Check(messages.Any(m => m.Contains("copy", StringComparison.Ordinal) && m.Contains("unavailable", StringComparison.Ordinal) && m.Contains("The definition is stopping", StringComparison.Ordinal)),
            "the failure to mark it unavailable is logged: " + string.Join(" | ", messages));
    }

    /// <summary>
    /// A secret setting's value is written into the trace as <c>***</c>, and the State still gets the real value; a
    /// setting that is not secret is traced as it is.
    /// </summary>
    static async Task SecretSettingIsRedactedInTheTrace()
    {
        var graph = Entity(
            [Setting("k1", "password"), State("s1", "label", "sensor"), Setting("k2", "name"), State("s2", "plain", "sensor")],
            [D("k1", "s1", "value"), D("k2", "s2", "value")],
            ("password", "string"), ("name", "string"));
        using var compiled = await Compiled.Of(graph, "States of a secret and a plain setting");
        var definition = new FakeDefinition { Settings = { ["password"] = "hunter2", ["name"] = "kitchen" }, Secrets = { "password" } };
        var secret = new TraceSink(); var plain = new TraceSink();
        await compiled.Run("s1", "state", definition, sink: secret);
        await compiled.Run("s2", "state", definition, sink: plain);
        Same(definition.Calls, ["state label value=string:hunter2", "available label yes", "state plain value=string:kitchen", "available plain yes"], "each State gets its setting's real value");
        var redacted = secret.Event("k1")["outputs"]?["value"];
        Check(JsonNode.DeepEquals(redacted, JsonNode.Parse("""{"type":"string","v":"***","redacted":true}""")), "the secret setting's value is redacted in the trace: " + redacted?.ToJsonString());
        var shown = plain.Event("k2")["outputs"]?["value"];
        Check(JsonNode.DeepEquals(shown, JsonNode.Parse("""{"type":"string","v":"kitchen"}""")), "a setting that is not secret is traced as it is: " + shown?.ToJsonString());
    }

    /// <summary>
    /// A secret is followed through what hands it on: a list made of it, a For each over that list and the item it gives
    /// the body, whose Log writes <c>***</c>. A secret true or false read by an If is hidden (it is the setting's own
    /// value); Has state of an entity whose id is secret hides the id and shows its true or false.
    /// </summary>
    static async Task ASecretIsFollowedThroughALoopAndIntoACondition()
    {
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>(); PyInterop.Log = (_, m) => messages.Enqueue(m);
        const string Secret = "fake-secret-hunter2";
        var graph = Entity(
            [Hub("relay", "relay", "switch"), Setting("tok", "token"), Setting("flag", "armed"), Setting("door", "door"),
             N("list", "array_create", new JsonObject { ["items"] = new JsonArray(new JsonObject { ["kind"] = "unset" }) }),
             N("loop", "foreach", new JsonObject { ["items"] = new JsonObject { ["kind"] = "unset" }, ["itemType"] = "string" }), N("say", "log", new { value = Unset }),
             N("i", "if", new { value = Unset }), N("is", "entity_is", new JsonObject { ["entity"] = new JsonObject { ["kind"] = "unset" }, ["state"] = Lit("string", "on") }),
             N("as_id", "convert", new { type = "entity_id", value = Unset }), N("yes", "log", new { value = Unset })],
            [Flow("relay", "command:turn_on", "loop"), E("loop", "say", "body"), E("loop", "i"), E("i", "yes", "true"),
             D("tok", "list", "item:0"), D("list", "loop", "items"), D("loop", "say", "value", "item"), D("flag", "i", "value"), D("door", "as_id", "value"), D("as_id", "is", "entity"), D("is", "yes", "value")],
            ("token", "string"), ("armed", "boolean"), ("door", "string"));
        using var compiled = await Compiled.Of(graph, "a loop over a secret and a condition of one");
        States["binary_sensor.secret_door"] = "on";
        var definition = new FakeDefinition { Settings = { ["token"] = Secret, ["armed"] = true, ["door"] = "binary_sensor.secret_door" }, Secrets = { "token", "armed", "door" } };
        var sink = new TraceSink();
        await compiled.Run("relay", "command:turn_on", definition, sink: sink);
        var text = sink.Final.ToJsonString();
        Check(!text.Contains("hunter2", StringComparison.Ordinal) && !text.Contains("secret_door", StringComparison.Ordinal), $"the trace quotes a secret ({text.Length} characters)");
        Check(messages.SequenceEqual(["***", "True"]), "the Log of the item writes ***, the Log of a true or false writes it: " + string.Join(" | ", messages.Select(m => m.Length > 8 ? $"text({m.Length})" : m)));
        static bool Hidden(JsonNode? port, string? node = null) => port?["v"]?.ToJsonString() == "\"***\"" && port["redacted"]?.GetValue<bool>() == true && (node == null || port["src"]?["node"]?.GetValue<string>() == node);
        static string Keys(JsonNode? port) => port is JsonObject o ? string.Join(",", o.Select(p => p.Key)) : "none";
        Check(Hidden(sink.Event("list")["inputs"]?["item:0"], "tok") && Hidden(sink.Event("list")["outputs"]?["value"]), "the list made of the secret: " + Keys(sink.Event("list")["outputs"]?["value"]));
        Check(Hidden(sink.Event("loop")["inputs"]?["items"], "list"), "the loop's list: " + Keys(sink.Event("loop")["inputs"]?["items"]));
        Check(Hidden(sink.Event("say")["inputs"]?["value"], "loop"), "the item in the body: " + Keys(sink.Event("say")["inputs"]?["value"]));
        Check(Hidden(sink.Event("i")["inputs"]?["value"], "flag"), "the secret true or false the If reads: " + Keys(sink.Event("i")["inputs"]?["value"]));
        Check(Hidden(sink.Event("is")["inputs"]?["entity"], "as_id") && sink.Event("is")["outputs"]?["value"]?["v"]?.GetValue<bool>() == true && sink.Event("is")["outputs"]!["value"]!["redacted"] == null,
            "Has state of a secret entity id: " + Keys(sink.Event("is")["inputs"]?["entity"]) + " / " + Keys(sink.Event("is")["outputs"]?["value"]));
    }

    /// <summary>Has state is false for an entity Home Assistant does not have, and the State is worked out from the other source.</summary>
    static async Task EntityIsIsFalseForAMissingEntity()
    {
        using var compiled = await Compiled.Of(AnyMotion(), "the derived binary sensor");
        foreach (var b in new[] { "off", "on", "unavailable" })
        {
            States["binary_sensor.motion_b"] = b;
            var definition = new FakeDefinition();
            await compiled.Run("s", "state", definition);
            Same(definition.Calls, [$"state any_motion is_on=bool:{(b == "on" ? "true" : "false")}", "available any_motion yes"], $"motion A missing, motion B {b}");
        }
        // Unavailable is a state like any other: asked for, it is equal.
        using var asked = await Compiled.Of(Entity([Is("u", "binary_sensor.motion_b", "unavailable"), State("s", "gone", "binary_sensor")], [D("u", "s", "is_on")]), "a State of an unavailable entity");
        var unavailable = new FakeDefinition();
        await asked.Run("s", "state", unavailable);
        Same(unavailable.Calls, ["state gone is_on=bool:true", "available gone yes"], "Has state unavailable is true for an unavailable entity");
        States.Remove("binary_sensor.motion_b");
        var missing = new FakeDefinition();
        await asked.Run("s", "state", missing);
        Same(missing.Calls, ["state gone is_on=bool:false", "available gone yes"], "Has state unavailable is false for a missing entity");
    }

    /// <summary>Has state reads the state the change that started the run carries for that entity, not an older one Home Assistant still reports.</summary>
    static async Task EntityIsReadsTheTriggeringEntitysFreshState()
    {
        using var compiled = await Compiled.Of(AnyMotion(), "the derived binary sensor");
        States["binary_sensor.motion_a"] = "off"; States["binary_sensor.motion_b"] = "off";
        UserScriptManager.CurrentTrigger.Value = new(new() { EntityId = "binary_sensor.motion_a", State = "on" });
        var definition = new FakeDefinition();
        await compiled.Run("s", "state", definition);
        Same(definition.Calls, ["state any_motion is_on=bool:true", "available any_motion yes"], "the triggering entity's own state is read");
    }

    /// <summary>When commanded and When started run their steps; Set available and Set state call the definition.</summary>
    static async Task SetStateAndSetAvailableStepsCallTheDefinition()
    {
        static JsonObject Available(string id, bool available) => N(id, "set_entity_available", new JsonObject { ["entity"] = "relay", ["available"] = Lit("boolean", available) });
        var graph = Entity(
            [Hub("relay", "relay", "switch"), SetState("on_set", "relay", new JsonObject { ["is_on"] = Lit("boolean", true) }),
             N("setup", "entity_setup"), Available("up", true), SetState("first", "relay", new JsonObject { ["is_on"] = Lit("boolean", false) }), Available("down", false)],
            [Flow("relay", "command:turn_on", "on_set"), E("setup", "up"), E("up", "first"), E("first", "down")]);
        using var compiled = await Compiled.Of(graph, "a switch with a command and a setup");
        var definition = new FakeDefinition();
        await compiled.Run("setup", definition);
        Same(definition.Calls, ["available relay yes", "state relay is_on=bool:false", "available relay no"], "When started runs its steps in order");
        definition.Calls.Clear();
        await compiled.Run("relay", "command:turn_on", definition);
        Same(definition.Calls, ["state relay is_on=bool:true"], "Turn on sets the state and nothing else");
    }

    /// <summary>A state set by a step stays set when a later step fails; the run fails, and the entity is not marked unavailable.</summary>
    static async Task StateSetBeforeALaterFailingStepStaysSet()
    {
        var graph = Entity(
            [Hub("relay", "relay", "switch"), SetState("set", "relay", new JsonObject { ["is_on"] = Lit("boolean", true) }),
             N("read", "read_entity", new JsonObject { ["entity"] = Lit("entity_id", "sensor.gone") })],
            [Flow("relay", "command:turn_on", "set"), E("set", "read")]);
        using var compiled = await Compiled.Of(graph, "a command that fails after setting the state");
        var definition = new FakeDefinition();
        var error = await Fails(() => compiled.Run("relay", "command:turn_on", definition), "a command whose later step fails");
        Check(error.Message.Contains("Entity 'sensor.gone' is unavailable.", StringComparison.Ordinal), "the run fails with the later step's error: " + error.Message);
        Same(definition.Calls, ["state relay is_on=bool:true"], "the state set before the failure stays set, and nothing marks the entity unavailable");
    }

    /// <summary>A Setting is the definition's value, as text, true or false, or a number.</summary>
    static async Task SettingGivesItsValueAsItsType()
    {
        var graph = Entity(
            [Setting("k1", "name"), State("s1", "label", "sensor"), Setting("k2", "flag"), State("s2", "flag_copy", "binary_sensor"), Setting("k3", "level"), State("s3", "level_copy", "sensor")],
            [D("k1", "s1", "value"), D("k2", "s2", "is_on"), D("k3", "s3", "value")],
            ("name", "string"), ("flag", "boolean"), ("level", "number"));
        using var compiled = await Compiled.Of(graph, "States of settings");
        var definition = new FakeDefinition { Settings = { ["name"] = "kitchen", ["flag"] = true, ["level"] = 4.5 } };
        foreach (var root in new[] { "s1", "s2", "s3" }) await compiled.Run(root, "state", definition);
        Same(definition.Calls, ["state label value=string:kitchen", "available label yes", "state flag_copy is_on=bool:true", "available flag_copy yes", "state level_copy value=double:4.5", "available level_copy yes"],
            "each Setting gives its value as its type");
    }

    /// <summary>A sensor's value takes a number, handed over as a number, or text, handed over as text.</summary>
    static async Task SensorStateAcceptsNumberAndText()
    {
        var graph = Entity(
            [State("n", "count", "sensor", new JsonObject { ["value"] = Lit("number", 3) }), State("t", "mode", "sensor", new JsonObject { ["value"] = Lit("string", "21.5") }),
             Current("cur", "sensor.outside"), N("f", "entity_field", new { entity = Unset, field = "state", type = "string" }), State("w", "copy", "sensor")],
            [D("cur", "f", "entity"), D("f", "w", "value")]);
        using var compiled = await Compiled.Of(graph, "sensors of numbers and text");
        States["sensor.outside"] = "12";
        var definition = new FakeDefinition();
        foreach (var root in new[] { "n", "t", "w" }) await compiled.Run(root, "state", definition);
        Same(definition.Calls, ["state count value=double:3", "available count yes", "state mode value=string:21.5", "available mode yes", "state copy value=string:12", "available copy yes"],
            "a number stays a number and text stays text");
    }

    /// <summary>An empty value is handed over as such (the entity's state becomes unknown), never refused; a JSON value as the plain value it holds.</summary>
    static void EmptyStateValueIsPassedThrough()
    {
        string[] both = ["number", "string"];
        Check(VisualRuntime.EntityStateValue(null, both, "x") == null, "an empty value is handed over empty");
        Check(VisualRuntime.EntityStateValue(JsonSerializer.SerializeToElement<object?>(null), both, "x") == null, "a JSON null is handed over empty");
        Check(VisualRuntime.EntityStateValue(JsonSerializer.SerializeToElement(7), both, "x") is 7.0, "a JSON number is handed over as a number");
        Check(VisualRuntime.EntityStateValue(JsonSerializer.SerializeToElement("7"), both, "x") is "7", "JSON text is handed over as text");
        Check(VisualRuntime.EntityStateValue(5, both, "x") is 5.0, "a whole number is handed over as a number");
        Check(VisualRuntime.EntityStateValue(JsonSerializer.SerializeToElement(true), ["boolean"], "x") is true, "JSON true is handed over as true");
        try { VisualRuntime.EntityStateValue("on", ["boolean"], "State · On"); throw new Exception("text taken for true or false"); }
        catch (InvalidOperationException error) { Check(error.Message == "State · On needs true or false, not on", "a value of another type is refused, naming the pin: " + error.Message); }
        try { VisualRuntime.EntityStateValue(true, both, "State · Value"); throw new Exception("true taken for a number or text"); }
        catch (InvalidOperationException error) { Check(error.Message == "State · Value needs a number or text, not true", "true or false is worded as such when refused: " + error.Message); }
        try { VisualRuntime.EntityStateValue(DayOfWeek.Monday, both, "State · Value"); throw new Exception("an enum member taken for a number"); }
        catch (InvalidOperationException error) { Check(error.Message == "State · Value needs a number or text, not Monday", "an enum member is not a number: " + error.Message); }
    }

    /// <summary>
    /// Every entity node needs the definition of the run in progress; without one it fails, saying so. An Entity node's
    /// state, command and frame flows and When started fail as the run starts. A Setting, Set state and Set available are each reached on
    /// their own: under a trigger of an entity definition's graph, after a step that runs first, as a run the runner has
    /// not given a definition would reach them.
    /// </summary>
    static async Task EntityNodesFailWithoutADefinition()
    {
        const string Message = "This node only works in an entity definition";
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>(); PyInterop.Log = (_, m) => messages.Enqueue(m);
        static JsonObject Manual(string id) => N(id, "trigger", new { type = "manual" });
        static JsonObject Said(string id, string text) => N(id, "log", new { value = Lit("string", text) });
        var graph = Entity(
            [State("s", "door", "binary_sensor", new JsonObject { ["is_on"] = Lit("boolean", true) }),
             Hub("relay", "relay", "switch"), SetState("on_set", "relay", new JsonObject { ["is_on"] = Lit("boolean", true) }),
             N("setup", "entity_setup"), N("setup_up", "set_entity_available", new JsonObject { ["entity"] = "relay", ["available"] = Lit("boolean", true) }),
             Manual("t1"), Setting("k", "name"), N("show", "log", new { value = Unset }),
             Manual("t2"), Said("before_set", "before set"), SetState("set", "relay", new JsonObject { ["is_on"] = Lit("boolean", true) }),
             Manual("t3"), Said("before_up", "before available"), N("up", "set_entity_available", new JsonObject { ["entity"] = "relay", ["available"] = Lit("boolean", true) }),
             Manual("t4"), Said("before_picture", "before picture"), Snapshot("picture", "camera.porch"),
             Hub("lens", "lens", "camera"), Said("framed", "framed")],
            [Flow("relay", "command:turn_on", "on_set"), E("setup", "setup_up"), E("t1", "show"), D("k", "show", "value"), E("t2", "before_set"), E("before_set", "set"), E("t3", "before_up"), E("before_up", "up"),
             E("t4", "before_picture"), E("before_picture", "picture"), Flow("lens", "frame", "framed")],
            ("name", "string"));
        using var compiled = await Compiled.Of(graph, "entity nodes of every kind");
        foreach (var (root, port) in new[] { ("s", "state"), ("relay", "command:turn_on"), ("setup", null), ("lens", "frame") })
        {
            var error = await Fails(() => port == null ? compiled.Run(root, null) : compiled.Run(root, port, null), $"root {root} {port} without a definition");
            Check(error is InvalidOperationException && error.Message == Message, $"root {root} {port} fails as it starts, saying it needs a definition: {error.GetType().Name}: {error.Message}");
        }
        var setting = await Fails(() => compiled.Run("t1", null), "a Setting without a definition");
        Check(setting is InvalidOperationException && setting.Message == "Setting (used by Log): " + Message, "the Setting fails itself, named with the step reading it: " + setting.Message);
        foreach (var (root, said, step) in new[] { ("t2", "before set", "Set state"), ("t3", "before available", "Set available"), ("t4", "before picture", "Take a picture") })
        {
            while (messages.TryDequeue(out _)) { }
            var error = await Fails(() => compiled.Run(root, null), $"{step} without a definition");
            Check(messages.Contains(said), $"the step before {step} ran: " + string.Join(" | ", messages));
            Check(error is InvalidOperationException && error.Message == Message, $"{step} fails itself, saying it needs a definition: {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>Has state and Current state work in an automation, which has no definition, reading Home Assistant.</summary>
    static async Task EntityIsWorksInAnAutomation()
    {
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>(); PyInterop.Log = (_, m) => messages.Enqueue(m);
        var graph = G(Is("is", "light.porch"), N("i", "if", new { value = Unset }), N("yes", "log", new { value = Lit("string", "yes") }), N("no", "log", new { value = Lit("string", "no") }),
            Current("cur", "light.porch"), N("f", "entity_field", new { entity = Unset, field = "state", type = "string" }), N("show", "log", new { value = Unset }));
        graph["edges"] = new JsonArray(E("t", "i"), E("i", "yes", "true"), E("i", "no", "false"), E("yes", "show"), E("no", "show"),
            D("is", "i", "value"), D("cur", "f", "entity"), D("f", "show", "value"));
        foreach (var state in new[] { "on", "off" })
        {
            while (messages.TryDequeue(out _)) { }
            States["light.porch"] = state;
            var prior = InvocationContext.Current;
            try { InvocationContext.Current = new("run", InvocationCause.Manual, null, TriggerPayload.Manual()); await Execute(graph); }
            finally { InvocationContext.Current = prior; }
            Same(messages.ToArray(), [state == "on" ? "yes" : "no", state], $"an automation reads the porch light {state}");
        }
    }

    /// <summary>
    /// A Set state step hands over only the values it is given (a light's brightness, not whether it is on), and a State
    /// only those it is given beside its required ones. A value from a secret setting is still traced hidden, from its
    /// wire, and handed over as hidden; a value it is not given is not traced.
    /// </summary>
    static async Task SetStateHandsOverOnlyWhatItIsGiven()
    {
        var graph = Entity(
            [Hub("lamp", "lamp", "light"), Setting("lvl", "level"), SetState("dim", "lamp"), State("s", "blind", "cover", new JsonObject { ["position"] = Lit("number", 40) })],
            [Flow("lamp", "command:turn_on", "dim"), D("lvl", "dim", "brightness")],
            ("level", "number"));
        using var compiled = await Compiled.Of(graph, "a Set state of a secret brightness");
        var definition = new FakeDefinition { Settings = { ["level"] = 128.0 }, Secrets = { "level" } };
        var sink = new TraceSink();
        await compiled.Run("lamp", "command:turn_on", definition, sink: sink);
        Same(definition.Calls, ["state lamp brightness=double:128"], "only the brightness is handed over");
        Same(definition.Hidden, ["lamp.brightness"], "the brightness from a secret is handed over as hidden");
        var traced = sink.Event("dim")["inputs"]?["brightness"];
        Check(traced?["v"]?.ToJsonString() == "\"***\"" && traced["redacted"]?.GetValue<bool>() == true && traced["src"]?["node"]?.GetValue<string>() == "lvl",
            "the secret brightness is traced hidden, from its wire: " + (traced is JsonObject o ? string.Join(",", o.Select(p => p.Key)) : "none"));
        Check(sink.Event("dim")["inputs"]?["is_on"] == null, "a value the step is not given is traced: " + sink.Event("dim")["inputs"]?.ToJsonString());
        definition.Calls.Clear();
        await compiled.Run("s", "state", definition);
        Same(definition.Calls, ["state blind position=double:40", "available blind yes"], "a State hands over the optional values it is given");
    }

    /// <summary>
    /// A password text's value of another type is refused without quoting it, as the class words its own refusals; a plain
    /// text's refusal quotes it. The validator lets only text into a text's value (a wire from a number, an object, a JSON
    /// value or an enum is refused), so no saved graph reaches this: the two parts are checked as the runtime joins them.
    /// </summary>
    static Task APasswordTextsValueOfAnotherTypeIsNotQuoted()
    {
        var catalog = new JsonObject { ["definition"] = new JsonObject { ["entities"] = new JsonArray(
            new JsonObject { ["key"] = "pin", ["domain"] = "text", ["options"] = new JsonObject { ["mode"] = "password" } },
            new JsonObject { ["key"] = "note", ["domain"] = "text", ["options"] = new JsonObject { ["mode"] = "text" } },
            new JsonObject { ["key"] = "plain", ["domain"] = "text" },
            new JsonObject { ["key"] = "power", ["domain"] = "sensor", ["options"] = new JsonObject { ["mode"] = "password" } }) } };
        Check(VisualRuntime.PasswordText(catalog, "pin") && !VisualRuntime.PasswordText(catalog, "note") && !VisualRuntime.PasswordText(catalog, "plain")
            && !VisualRuntime.PasswordText(catalog, "power") && !VisualRuntime.PasswordText(catalog, "missing"), "only a text shown as a password is one");
        foreach (var (quote, want) in new[] { (false, "State · Value needs text, not the hidden value given"), (true, "State · Value needs text, not 1234") })
        {
            try { VisualRuntime.EntityStateValue(1234.0, ["string"], "State · Value", quote); throw new Exception("a number taken for text"); }
            catch (InvalidOperationException error) { Check(error.Message == want, $"the refusal {(quote ? "quotes" : "does not quote")} the value: " + error.Message); }
        }
        return Task.CompletedTask;
    }

    // ---- HTTP request ----

    /// <summary>An HTTP request step: its URL typed in, the rest of its configuration as given.</summary>
    static JsonObject Request(string id, string url, object? config = null)
    {
        var node = N(id, "http_request", config ?? new { });
        node["config"]!["url"] = Lit("string", url);
        node["config"]!["responseType"] ??= "string";
        return node;
    }
    static JsonObject Field(string id, string pointer, string type) => N(id, "get_field", new { pointer, type, value = Unset });
    /// <summary>The two sensors a request feeds, as Entity nodes of their keys.</summary>
    static JsonObject[] Readings() => [Hub("reading", "reading", "sensor"), Hub("count", "count", "sensor")];
    /// <summary>When started, then the steps in order: the graph of a sensor fed by a request.</summary>
    static JsonObject Started(JsonObject[] nodes, JsonObject[] edges, params (string Key, string Type)[] settings)
    {
        var steps = nodes.Where(n => !VisualNodeCatalog.IsPure(VisualNodeCatalog.Root["nodes"]?[n["kind"]!.GetValue<string>()])).Select(n => n["id"]!.GetValue<string>()).ToArray();
        var flow = new[] { "setup" }.Concat(steps).ToArray();
        return Entity([N("setup", "entity_setup"), .. nodes, .. Readings()], [.. flow.Zip(flow.Skip(1), (a, b) => E(a, b)).Where(e => !edges.Any(x => x["port"] != null && x["source"]!.GetValue<string>() == e["source"]!.GetValue<string>())), .. edges], settings);
    }
    static string Traced(JsonNode? port) => port?["v"]?.ToJsonString() ?? "none";

    /// <summary>A GET of text: the body is the text, the status a number, the headers an object with lower-case names.</summary>
    static async Task HttpRequestGetsText()
    {
        await using var server = LoopbackServer.Start();
        server.Respond("/rpc/Switch.GetStatus", new() { Body = System.Text.Encoding.UTF8.GetBytes("on"), Headers = new Dictionary<string, string> { ["X-Device-Name"] = "Plug", ["Content-Type"] = "text/plain; charset=utf-8" } });
        using var compiled = await Compiled.Of(Started([Request("get", server.Url("/rpc/Switch.GetStatus?id=0")), SetState("set", "reading")], [D("get", "set", "value", "body")]), "a GET of text");
        var definition = new FakeDefinition(); var sink = new TraceSink();
        await compiled.Run("setup", definition, sink: sink);
        Same(definition.Calls, ["state reading value=string:on"], "the body is the text");
        var request = server.Requests().Single();
        Check(request.Method == "GET" && request.Target == "/rpc/Switch.GetStatus?id=0" && request.Headers["User-Agent"] == "HassSharp" && request.Body.Length == 0, "the request: " + request.Method + " " + request.Target);
        var outputs = sink.Event("get")["outputs"]!;
        Check(Traced(outputs["status"]) == "200" && outputs["status"]!["redacted"] == null, "the status is a number: " + outputs["status"]?.ToJsonString());
        Check(outputs["headers"]?["v"]?["x-device-name"]?.GetValue<string>() == "Plug" && outputs["headers"]!["v"]!["content-type"]?.GetValue<string>() == "text/plain; charset=utf-8"
            && outputs["headers"]!["v"]!.AsObject().All(p => p.Key == p.Key.ToLowerInvariant()), "the headers by lower-case name: " + outputs["headers"]?.ToJsonString());
    }

    /// <summary>
    /// A POST of a JSON object sends it with Content-Type application/json and the headers given; a header may set another
    /// type, and text is sent as it is. A JSON object or list answer is read for Get field.
    /// </summary>
    static async Task HttpRequestPostsJsonAndReadsJson()
    {
        await using var server = LoopbackServer.Start();
        server.Respond("/rpc/Switch.Set", LoopbackServer.Reply.Text("""{"was_on":false,"name":"Plug"}"""));
        server.Respond("/meters", LoopbackServer.Reply.Text("[1.5, 2.5, 3.5]"));
        server.Respond("/note", LoopbackServer.Reply.Text("noted"));
        var post = Request("post", server.Url("/rpc/Switch.Set"), new JsonObject
        {
            ["method"] = "POST", ["responseType"] = "object",
            ["body"] = Lit("object", new { id = 0, on = true }), ["headers"] = Lit("object", new Dictionary<string, object> { ["X-Key"] = "k1", ["X-Count"] = 2 }),
        });
        var list = Request("list", server.Url("/meters"), new { responseType = "array" });
        var text = Request("text", server.Url("/note"), new JsonObject { ["method"] = "PUT", ["body"] = Lit("string", "<b>on</b>"), ["headers"] = Lit("object", new Dictionary<string, string> { ["Content-Type"] = "text/html" }) });
        var patch = Request("patch", server.Url("/note"), new JsonObject { ["method"] = "PATCH", ["body"] = Lit("array", new[] { 1, 2 }), ["headers"] = Lit("object", new Dictionary<string, string> { ["content-type"] = "application/merge-patch+json" }) });
        using var compiled = await Compiled.Of(Started([post, Field("name", "/name", "string"), SetState("set_name", "reading"), list, Field("second", "/1", "number"), SetState("set_count", "count"), text, patch],
            [D("post", "name", "value", "body"), D("name", "set_name", "value"), D("list", "second", "value", "body"), D("second", "set_count", "value")]), "a POST of JSON");
        var definition = new FakeDefinition();
        await compiled.Run("setup", definition);
        Same(definition.Calls, ["state reading value=string:Plug", "state count value=double:2.5"], "a JSON object and a JSON list are read");
        var sent = server.Requests();
        var set = sent.Single(r => r.Path == "/rpc/Switch.Set");
        Check(set.Method == "POST" && set.Headers["Content-Type"] == "application/json" && System.Text.Encoding.UTF8.GetString(set.Body) == """{"id":0,"on":true}"""
            && set.Headers["X-Key"] == "k1" && set.Headers["X-Count"] == "2", "the POST: " + set.Headers.GetValueOrDefault("Content-Type") + " " + System.Text.Encoding.UTF8.GetString(set.Body));
        var put = sent.Single(r => r.Method == "PUT");
        Check(put.Headers["Content-Type"] == "text/html" && System.Text.Encoding.UTF8.GetString(put.Body) == "<b>on</b>", "text with a header's type: " + put.Headers.GetValueOrDefault("Content-Type"));
        var merge = sent.Single(r => r.Method == "PATCH");
        Check(merge.Headers["Content-Type"] == "application/merge-patch+json" && System.Text.Encoding.UTF8.GetString(merge.Body) == "[1,2]", "a list with a header's type: " + merge.Headers.GetValueOrDefault("Content-Type"));
    }

    /// <summary>
    /// A status outside 200 to 299 fails the step by a message naming the host and the status; with an On error wire
    /// the run takes it; with Fail on an error status off it goes on with the status.
    /// </summary>
    static async Task HttpRequestFailsOnAnErrorStatus()
    {
        await using var server = LoopbackServer.Start();
        var host = "127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture);
        server.Respond("/missing", new() { Status = 404, Body = System.Text.Encoding.UTF8.GetBytes("no such page") });
        var url = server.Url("/missing?token=hunter2");
        using var plain = await Compiled.Of(Started([Request("get", url), SetState("set", "reading")], [D("get", "set", "value", "body")]), "a GET of a missing page");
        var definition = new FakeDefinition(); var sink = new TraceSink();
        var error = await Fails(() => plain.Run("setup", definition, sink: sink), "a 404");
        Check(error.Message == $"HTTP 404 from {host}", "the message: " + error.Message);
        Same(definition.Calls, [], "nothing after the failed step runs");
        Check(sink.Event("get")["status"]?.GetValue<string>() == "failed" && !sink.Final.ToJsonString().Contains("hunter2", StringComparison.Ordinal), "the step failed in the trace: " + sink.Event("get").ToJsonString());

        using var recovered = await Compiled.Of(Started([Request("get", url), SetState("set", "reading"), N("why", "last_error"), SetState("said", "reading")],
            [E("get", "set"), E("get", "said", "error"), D("get", "set", "value", "body"), D("why", "said", "value", "message")]), "a GET of a missing page with an On error wire");
        definition = new FakeDefinition();
        await recovered.Run("setup", definition);
        Same(definition.Calls, [$"state reading value=string:HTTP 404 from {host}"], "the On error wire is taken");

        using var lenient = await Compiled.Of(Started([Request("get", url, new { failOnStatus = false }), SetState("set", "reading"), SetState("code", "count")],
            [D("get", "set", "value", "body"), D("get", "code", "value", "status")]), "a GET of a missing page that does not fail on its status");
        definition = new FakeDefinition();
        await lenient.Run("setup", definition);
        Same(definition.Calls, ["state reading value=string:no such page", "state count value=double:404"], "with Fail on an error status off, the step goes on with the status");
    }

    /// <summary>
    /// A server that does not answer within the timeout fails the step, naming the host and the timeout; a run cancelled
    /// mid-request ends cancelled, and no connection is left open after either.
    /// </summary>
    static async Task HttpRequestTimesOutAndIsCancelledWithItsRun()
    {
        await using var silent = SilentServer.Start();
        var host = "127.0.0.1:" + silent.Port.ToString(CultureInfo.InvariantCulture);
        using var compiled = await Compiled.Of(Started([Request("get", silent.Url("/status"), new { timeout = 1 }), SetState("set", "reading")], [D("get", "set", "value", "body")]), "a GET of a server that does not answer");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var error = await Fails(() => compiled.Run("setup", new FakeDefinition()), "a request that times out");
        Check(error is TimeoutException && error.Message == $"{host} did not answer within 1 second", "the message: " + error.GetType().Name + ": " + error.Message);
        Check(clock.Elapsed >= TimeSpan.FromMilliseconds(950) && clock.Elapsed < TimeSpan.FromSeconds(5), $"timed out after {clock.Elapsed.TotalMilliseconds} ms");
        await EntityTestHome.Until(() => silent.Open == 0, "the connection of the timed out request closes");

        using var cancel = new CancellationTokenSource();
        var definition = new FakeDefinition();
        var run = compiled.Run("setup", definition, cancel.Token);
        await EntityTestHome.Until(() => silent.Targets().Length == 2 && silent.Open == 1, "the second request arrives");
        clock.Restart();
        cancel.Cancel();
        var cancelled = await Fails(() => run, "a run cancelled mid-request");
        Check(cancelled is OperationCanceledException && clock.Elapsed < TimeSpan.FromSeconds(1), $"the run ends cancelled at once: {cancelled.GetType().Name} after {clock.Elapsed.TotalMilliseconds} ms");
        await EntityTestHome.Until(() => silent.Open == 0, "the connection of the cancelled request closes");
        Same(definition.Calls, [], "a cancelled request sets nothing");
    }

    /// <summary>
    /// An answer over 4 MiB, an answer that is not the JSON shape asked for, a body on a GET or HEAD and an address that is
    /// not http or https fail the step by messages that quote neither the address nor the answer.
    /// </summary>
    static async Task HttpRequestRefusesWhatItCannotUse()
    {
        await using var server = LoopbackServer.Start();
        var host = "127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture);
        var big = new byte[4 * 1024 * 1024 + 1]; Array.Fill(big, (byte)'a');
        server.Respond("/big", new() { Body = big });
        server.Respond("/streamed", new() { Body = big, Chunked = true, Pieces = 8 });
        server.Respond("/exactly", new() { Body = big[..^1] });
        server.Respond("/page", LoopbackServer.Reply.Text("<html>hunter2</html>"));
        server.Respond("/list", LoopbackServer.Reply.Text("""["hunter2"]"""));
        server.Respond("/object", LoopbackServer.Reply.Text("""{"key":"hunter2"}"""));
        server.Respond("/twice", LoopbackServer.Reply.Text("""{"hunter2":1,"hunter2":2}"""));
        async Task<string> Message(JsonObject request, string what)
        {
            request["id"] = "get";
            using var compiled = await Compiled.Of(Started([request], []), what);
            var error = await Fails(() => compiled.Run("setup", new FakeDefinition()), what);
            Check(!error.Message.Contains("hunter2", StringComparison.Ordinal) && !error.Message.Contains("/page", StringComparison.Ordinal) && error.InnerException == null, $"{what} quotes what it should not: {error.Message}");
            return error.Message;
        }
        Check(await Message(Request("get", server.Url("/big")), "an answer over 4 MiB") == $"The answer from {host} is larger than 4 MiB", "an answer over 4 MiB");
        Check(await Message(Request("get", server.Url("/streamed")), "an answer streamed past 4 MiB") == $"The answer from {host} is larger than 4 MiB", "an answer streamed past 4 MiB");
        foreach (var (path, type, want) in new[] { ("/page", "object", "a JSON object"), ("/list", "object", "a JSON object"), ("/object", "array", "a JSON list"), ("/page", "array", "a JSON list"), ("/twice", "object", "a JSON object") })
            Check(await Message(Request("get", server.Url(path + "?token=hunter2"), new { responseType = type }), $"{path} read as {type}") == $"The answer from {host} is not {want}", $"{path} read as {type}");
        // A body on a GET or HEAD, which the validators refuse (http.body), is refused by a graph run as it is too.
        foreach (var method in new[] { "GET", "HEAD" })
        {
            var request = Request("get", server.Url("/page"), new JsonObject { ["method"] = method, ["body"] = Lit("string", "hunter2") });
            var graph = new JsonObject { ["version"] = 5, ["name"] = "Body", ["nodes"] = new JsonArray(N("t", "trigger", new { type = "manual" }), request), ["edges"] = new JsonArray(E("t", "get")) };
            var types = new Dictionary<string, Type> { ["boolean"] = typeof(bool), ["number"] = typeof(double), ["string"] = typeof(string), ["object"] = typeof(JsonObject), ["array"] = typeof(JsonArray) };
            var runtime = new VisualRuntime(graph.ToJsonString(), VisualNodeCatalog.Root.ToJsonString(), types, TriggerPayload.Manual(), default, (_, _, _) => Task.CompletedTask);
            var error = await Fails(() => runtime.RunAsync("t"), "a body on a " + method);
            Check(error.Message == $"HTTP request cannot send a body with {method}", $"a body on a {method}: {error.Message}");
        }
        foreach (var url in new[] { "ftp://127.0.0.1/hunter2", "/page?hunter2", "http://exa mple.com/hunter2" })
        {
            var graph = Started([N("url", "constant", new { type = "string", value = Lit("string", url) }), N("get", "http_request", new { responseType = "string", url = Unset })], [D("url", "get", "url")]);
            using var compiled = await Compiled.Of(graph, "a connected address " + url);
            var error = await Fails(() => compiled.Run("setup", new FakeDefinition()), "an address that is not one: " + url);
            Check(error.Message == "HTTP request · URL is not an http:// or https:// address", $"{url}: {error.Message}");
        }
        // Exactly 4 MiB is read whole.
        using var exactly = await Compiled.Of(Started([Request("get", server.Url("/exactly")), SetState("set", "count")], [D("get", "set", "value", "body")]), "an answer of exactly 4 MiB");
        var definition = new FakeDefinition();
        await exactly.Run("setup", definition);
        Check(definition.Calls.Single() == "state count value=string:" + new string('a', 4 * 1024 * 1024), "an answer of exactly 4 MiB is read whole: " + definition.Calls.Single().Length);
    }

    /// <summary>
    /// A secret setting wired into the URL and into a header: the request is made with them, the trace hides both inputs
    /// with their wires kept, and a failure names only the host; a header the request cannot send fails without quoting it.
    /// </summary>
    static async Task HttpRequestHidesASecretsInputs()
    {
        await using var server = LoopbackServer.Start();
        var host = "127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture);
        server.Respond("/rpc/Shelly.GetStatus", new() { Status = 401 });
        var graph = Started(
            [Setting("address", "address"), Setting("token", "token"), N("headers", "object_create", new JsonObject { ["fields"] = new JsonObject { ["Authorization"] = new JsonObject { ["kind"] = "unset" } } }),
             N("get", "http_request", new { responseType = "string", url = Unset, headers = Unset }), SetState("set", "reading")],
            [D("address", "get", "url"), D("token", "headers", "field:Authorization"), D("headers", "get", "headers"), D("get", "set", "value", "status")],
            ("address", "string"), ("token", "string"));
        using var compiled = await Compiled.Of(graph, "a request to a secret address with a secret header");
        var definition = new FakeDefinition { Settings = { ["address"] = server.Url("/rpc/Shelly.GetStatus?auth_key=hunter2"), ["token"] = "Bearer hunter2-token" }, Secrets = { "address", "token" } };
        var sink = new TraceSink();
        var error = await Fails(() => compiled.Run("setup", definition, sink: sink), "a secret request that fails");
        Check(error.Message == $"HTTP 401 from {host}", "the failure names only the host: " + error.Message);
        var request = server.Requests().Single();
        Check(request.Target == "/rpc/Shelly.GetStatus?auth_key=hunter2" && request.Headers["Authorization"] == "Bearer hunter2-token", "the request is made with the secrets: " + request.Target);
        var text = sink.Final.ToJsonString();
        Check(!text.Contains("hunter2", StringComparison.Ordinal), $"the trace quotes a secret ({text.Length} characters)");
        var inputs = sink.Event("get")["inputs"]!;
        foreach (var (port, from) in new[] { ("url", "address"), ("headers", "headers") })
            Check(inputs[port]?["v"]?.GetValue<string>() == "***" && inputs[port]!["redacted"]?.GetValue<bool>() == true && inputs[port]!["src"]?["node"]?.GetValue<string>() == from,
                $"the secret {port} is traced hidden from its wire: " + string.Join(",", (inputs[port] as JsonObject ?? []).Select(p => p.Key)));
        Check(sink.Event("get")["error"]?["message"]?.GetValue<string>() == $"HTTP 401 from {host}", "the trace's failure names only the host");

        // A secret header value that cannot be sent, and a secret address that is not one.
        foreach (var (address, token) in new[] { (server.Url("/rpc/Shelly.GetStatus"), "hunter2\r\nX-Injected: 1"), ("ftp://hunter2.example/", "hunter2") })
        {
            definition = new FakeDefinition { Settings = { ["address"] = address, ["token"] = token }, Secrets = { "address", "token" } };
            var refused = await Fails(() => compiled.Run("setup", definition), "a secret that cannot be sent: " + address);
            Check(!refused.Message.Contains("hunter2", StringComparison.Ordinal) && refused.InnerException == null, "the refusal quotes the secret: " + refused.Message);
            Check(refused.Message == (token.Contains('\n') ? "HTTP request · Headers cannot use the value it was given, which is hidden because it comes from a secret setting" : "HTTP request · URL is not an http:// or https:// address"), "the refusal: " + refused.Message);
        }
        // The same header, not secret, is named without its value.
        definition = new FakeDefinition { Settings = { ["address"] = server.Url("/rpc/Shelly.GetStatus"), ["token"] = "hunter2\r\nX-Injected: 1" }, Secrets = { "address" } };
        var plain = await Fails(() => compiled.Run("setup", definition), "a header that cannot be sent");
        Check(plain.Message == "HTTP request · Headers: Authorization has a value that cannot be sent in a header; use plain ASCII text on one line", "a header that cannot be sent is named, without its value: " + plain.Message);
        Check(server.Requests().Length == 1, "a request that cannot be made is not sent: " + server.Requests().Length);
    }

    /// <summary>
    /// With Keep the response out of run history, the body and headers are traced hidden, and so is what a Get field
    /// works out from the body, which is handed to the definition as hidden and logged as ***; the status is shown.
    /// </summary>
    static async Task HttpRequestKeepsItsResponseOutOfRunHistory()
    {
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>(); PyInterop.Log = (_, m) => messages.Enqueue(m);
        await using var server = LoopbackServer.Start();
        server.Respond("/secret", new() { Body = System.Text.Encoding.UTF8.GetBytes("""{"name":"hunter2","count":3}"""), Headers = new Dictionary<string, string> { ["X-Session"] = "hunter2-session" } });
        var graph = Started([Request("get", server.Url("/secret"), new { responseType = "object", hideResponse = true }), Field("name", "/name", "string"), SetState("set", "reading"), N("say", "log", new { value = Unset }),
            Field("broken", "/name", "number"), N("bad", "log", new { value = Unset })],
            [D("get", "name", "value", "body"), D("name", "set", "value"), D("name", "say", "value"), D("get", "broken", "value", "body"), D("broken", "bad", "value")]);
        using var compiled = await Compiled.Of(graph, "a request whose response is kept out of run history");
        var definition = new FakeDefinition(); var sink = new TraceSink();
        var error = await Fails(() => compiled.Run("setup", definition, sink: sink), "a hidden value of another type");
        Check(!error.Message.Contains("hunter2", StringComparison.Ordinal), "a failure on the hidden value quotes it: " + error.Message);
        Check(error.Message == "Get field (used by Log): Get field · Source cannot use the value it was given, which is hidden because it comes from a response kept out of run history", "the failure says why it is hidden: " + error.Message);
        Same(definition.Calls, ["state reading value=string:hunter2"], "the definition gets the real value");
        Same(definition.Hidden, ["reading.value"], "the value is handed over as hidden");
        Check(messages.Contains("***") && !messages.Any(m => m.Contains("hunter2", StringComparison.Ordinal)), "the Log writes ***: " + messages.Count);
        Check(!sink.Final.ToJsonString().Contains("hunter2", StringComparison.Ordinal), "the trace quotes the hidden response");
        var outputs = sink.Event("get")["outputs"]!;
        Check(Traced(outputs["status"]) == "200" && outputs["status"]!["redacted"] == null, "the status is shown: " + outputs["status"]?.ToJsonString());
        Check(outputs["body"]?["redacted"]?.GetValue<bool>() == true && outputs["headers"]?["redacted"]?.GetValue<bool>() == true, "the body and headers are hidden: " + outputs.ToJsonString());
        // Get field is worked out for each step that reads it.
        var fields = sink.Final["events"]!.AsArray().Where(e => e!["node"]!.GetValue<string>() == "name").ToArray();
        Check(fields.Length == 2 && fields.All(e => e!["inputs"]?["value"]?["redacted"]?.GetValue<bool>() == true && e["outputs"]?["value"]?["redacted"]?.GetValue<bool>() == true), "Get field of the hidden body is hidden: " + string.Join(" ", fields.Select(e => e!.ToJsonString())));
        // The same response, not hidden, is shown.
        var shown = (JsonObject)graph.DeepClone();
        shown["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "get")!["config"]!["hideResponse"] = false;
        using var plain = await Compiled.Of(shown, "the same request, its response shown");
        sink = new TraceSink();
        await Fails(() => plain.Run("setup", new FakeDefinition(), sink: sink), "a value of another type");
        Check(sink.Event("get")["outputs"]?["body"]?["v"]?["name"]?.GetValue<string>() == "hunter2", "a response not hidden is shown: " + sink.Event("get")["outputs"]?.ToJsonString());
    }

    /// <summary>
    /// Compare's ordering of two values of different types (which the validators refuse, so only a graph run as it is
    /// reaches it) fails without quoting a secret text: the run is made here as the generated code makes it.
    /// </summary>
    static async Task CompareOfASecretTextAndANumberDoesNotQuoteIt()
    {
        var graph = Entity(
            [N("t", "trigger", new { type = "manual" }), Setting("pin", "pin"), N("cmp", "compare", new { @operator = "gt", left = Unset, right = Lit("number", 5) }), N("show", "log", new { value = Unset })],
            [E("t", "show"), D("pin", "cmp", "left"), D("cmp", "show", "value")],
            ("pin", "string"));
        var types = new Dictionary<string, Type> { ["boolean"] = typeof(bool), ["number"] = typeof(double), ["string"] = typeof(string), ["any"] = typeof(object) };
        async Task<Exception> Run(params string[] secrets)
        {
            var prior = InvocationContext.Current;
            try
            {
                var definition = new FakeDefinition { Settings = { ["pin"] = "hunter2" } };
                definition.Secrets.UnionWith(secrets);
                InvocationContext.Current = new InvocationContext("run-compare", InvocationCause.Entity, null, TriggerPayload.Manual()) { Definition = definition };
                var runtime = new VisualRuntime(graph.ToJsonString(), VisualNodeCatalog.Root.ToJsonString(), types, TriggerPayload.Manual(), default, (_, _, _) => Task.CompletedTask, comparisonTypes: new() { ["cmp"] = "any" });
                return await Fails(() => runtime.RunAsync("t"), "an ordering of text and a number");
            }
            finally { InvocationContext.Current = prior; }
        }
        var secret = await Run("pin");
        Check(secret.Message == "Compare (used by Log): Compare · First value cannot use the value it was given, which is hidden because it comes from a secret setting" && secret.InnerException?.InnerException == null,
            "the ordering of a secret says it is hidden: " + secret.Message);
        var plain = await Run();
        Check(plain.Message.Contains("hunter2", StringComparison.Ordinal), "the ordering of a value that is not secret still says what failed: " + plain.Message);
    }

    /// <summary>
    /// A host that takes no connection (its listener's backlog is full, so the connection is never answered) fails the
    /// step when the pools' connect limit passes, before the step's own timeout: by the plain message that names the
    /// host, with no failure under it.
    /// </summary>
    static async Task HttpRequestNamesAHostThatCannotBeConnectedTo()
    {
        using var listener = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        listener.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        listener.Listen(1);
        var port = ((System.Net.IPEndPoint)listener.LocalEndPoint!).Port;
        var fillers = new List<System.Net.Sockets.Socket>();
        try
        {
            // Connections it never accepts fill its backlog; the first one that is not answered shows it is full.
            while (fillers.Count < 64)
            {
                var filler = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                fillers.Add(filler);
                if (!filler.ConnectAsync(System.Net.IPAddress.Loopback, port).Wait(300)) break;
            }
            SharpHttp.ConnectTimeout = TimeSpan.FromSeconds(1);
            using var compiled = await Compiled.Of(Started([Request("get", $"http://127.0.0.1:{port}/status", new { timeout = 5 })], []), "a request to a host that takes no connection");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var error = await Fails(() => compiled.Run("setup", new FakeDefinition()), "a request to a host that takes no connection");
            Check(error is HttpRequestException && error.Message == $"Could not reach 127.0.0.1:{port}" && error.InnerException == null, $"the failure: {error.GetType().Name}: {error.Message}");
            Check(clock.Elapsed < TimeSpan.FromSeconds(4), $"the connect limit, not the step's timeout, ended it: {clock.Elapsed.TotalMilliseconds} ms");
        }
        finally
        {
            SharpHttp.ConnectTimeout = TimeSpan.FromSeconds(10);
            foreach (var filler in fillers) filler.Dispose();
        }
    }

    /// <summary>
    /// A HEAD of something larger than 4 MiB gives its status and headers as received, and no body, whatever Response
    /// type says: a step reading the body fails by name, in plain words.
    /// </summary>
    static async Task HttpRequestHeadGivesNoBody()
    {
        await using var server = LoopbackServer.Start();
        server.Respond("/firmware.bin", new() { ContentLength = 10 * 1024 * 1024, Headers = new Dictionary<string, string> { ["X-Version"] = "1.2" } });
        var head = Request("head", server.Url("/firmware.bin"), new { method = "HEAD", responseType = "object" });
        using var compiled = await Compiled.Of(Started([head, SetState("set", "count")], [D("head", "set", "value", "status")]), "a HEAD of a large file");
        var definition = new FakeDefinition(); var sink = new TraceSink();
        await compiled.Run("setup", definition, sink: sink);
        Same(definition.Calls, ["state count value=double:200"], "a HEAD gives the status");
        var outputs = sink.Event("head")["outputs"]!;
        Check(outputs["headers"]?["v"]?["content-length"]?.GetValue<string>() == "10485760" && outputs["headers"]!["v"]!["x-version"]?.GetValue<string>() == "1.2", "the headers as received: " + outputs.ToJsonString());
        Check(outputs["body"] == null, "a HEAD gives no body: " + outputs.ToJsonString());
        Check(server.Requests().Single().Method == "HEAD", "the request is a HEAD");

        using var reading = await Compiled.Of(Started([Request("head", server.Url("/firmware.bin"), new { method = "HEAD" }), SetState("set", "reading")], [D("head", "set", "value", "body")]), "a step reading the body of a HEAD");
        var error = await Fails(() => reading.Run("setup", new FakeDefinition()), "a step reading the body of a HEAD");
        Check(error.Message.StartsWith("Set state", StringComparison.Ordinal) && error.Message.EndsWith(" reads the body of HTTP request, which uses HEAD and gets none; choose GET to read it", StringComparison.Ordinal), "the failure: " + error.Message);
    }

    sealed class Receiving : IDisposable { public void Dispose() { } }

    /// <summary>
    /// Download file fed a secret address gives the file no name from it (it is called <c>file</c>), and its path is
    /// hidden when the address or the checksum is secret, as the checksum is part of it; a refused address fails by a
    /// plain sentence.
    /// </summary>
    static async Task DownloadFileHidesASecretsAddressAndChecksum()
    {
        await using var server = LoopbackServer.Start();
        var model = System.Text.Encoding.UTF8.GetBytes("weights");
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(model));
        server.Respond("/hunter2-token/model.bin", new() { Body = model });
        server.Respond("/models/model.bin", new() { Body = model });
        var root = Path.Combine(Path.GetTempPath(), "hass-sharp-files-" + Guid.NewGuid().ToString("N"));
        try
        {
            var graph = Started([Setting("address", "address"), Setting("checksum", "checksum"), N("download", "download_file", new { url = Unset, sha256 = Unset }), SetState("set", "reading")],
                [D("address", "download", "url"), D("checksum", "download", "sha256"), D("download", "set", "value", "path")], ("address", "string"), ("checksum", "string"));
            using var compiled = await Compiled.Of(graph, "a download of a secret address");
            foreach (var (address, secret, name) in new[] { (server.Url("/hunter2-token/model.bin?key=hunter2"), "address", "file"), (server.Url("/models/model.bin"), "checksum", "model.bin") })
            {
                var definition = new FakeDefinition { Settings = { ["address"] = address, ["checksum"] = sha }, Secrets = { secret }, Downloads = new SharpFiles(root, _ => { }, () => new Receiving()) };
                var sink = new TraceSink();
                await compiled.Run("setup", definition, sink: sink);
                var path = Path.Combine(root, sha, name);
                Same(definition.Calls, ["state reading value=string:" + path], $"the path, with a secret {secret}");
                Same(definition.Hidden, ["reading.value"], $"the path is handed over as hidden, with a secret {secret}");
                var text = sink.Final.ToJsonString();
                Check(!text.Contains("hunter2", StringComparison.Ordinal) && (secret != "checksum" || !text.Contains(sha, StringComparison.Ordinal)), $"the trace quotes the secret {secret}");
                Check(sink.Event("download")["outputs"]?["path"]?["redacted"]?.GetValue<bool>() == true, $"the path is traced hidden, with a secret {secret}: " + sink.Event("download")["outputs"]?.ToJsonString());
            }
            var refused = Started([N("url", "constant", new { type = "string", value = Lit("string", "ftp://example.com/model.bin") }), N("download", "download_file", new { url = Unset, sha256 = Lit("string", sha) })], [D("url", "download", "url")]);
            using var ftp = await Compiled.Of(refused, "a download of an ftp address");
            var error = await Fails(() => ftp.Run("setup", new FakeDefinition { Downloads = new SharpFiles(root, _ => { }, () => new Receiving()) }), "a download of an ftp address");
            Check(error.Message == "The address of a download must be an http or https URL", "a plain sentence: " + error.Message);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// HTTP request follows redirects itself: to the same host with its headers; to another host only when it has no
    /// headers (else it fails, naming the hosts only, and the other host never sees them); a 303, or a 302 after a POST,
    /// as a GET without the body; a 307 with the method and the body; at most 5; and not one without an address to go to
    /// or to an address that is not http or https.
    /// </summary>
    static async Task HttpRequestFollowsRedirectsWithoutHandingHeadersToAnotherHost()
    {
        await using var server = LoopbackServer.Start();
        var host = "127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture);
        var other = "localhost:" + server.Port.ToString(CultureInfo.InvariantCulture);
        server.Respond("/b", LoopbackServer.Reply.Text("""{"at":"b"}"""));
        void Redirect(string path, int status, string? location) => server.Respond(path, new() { Status = status, Headers = location == null ? null : new Dictionary<string, string> { ["Location"] = location } });
        JsonObject Headers() => Lit("object", new Dictionary<string, string> { ["X-Api-Key"] = "k1" });
        async Task<Exception?> Run(JsonObject request)
        {
            request["id"] = "get";
            using var compiled = await Compiled.Of(Started([request, Field("at", "/at", "string"), SetState("set", "reading")], [D("get", "at", "value", "body"), D("at", "set", "value")]), "a redirect");
            var definition = new FakeDefinition();
            try { await compiled.Run("setup", definition); }
            catch (Exception error) { return error; }
            Same(definition.Calls, ["state reading value=string:b"], "the final answer's body");
            return null;
        }
        LoopbackServer.Request Last(string path) => server.Requests().Last(r => r.Path == path);

        Redirect("/same", 302, "/b");
        Check(await Run(Request("get", server.Url("/same"), new JsonObject { ["responseType"] = "object", ["headers"] = Headers() })) == null, "a redirect to the same host is followed");
        Check(Last("/b").Headers.GetValueOrDefault("X-Api-Key") == "k1", "a redirect to the same host keeps the headers");

        server.Clear();
        Redirect("/cross", 302, server.OtherHostUrl("/b"));
        var crossed = await Run(Request("get", server.Url("/cross"), new JsonObject { ["responseType"] = "object", ["headers"] = Headers() }));
        Check(crossed?.Message == $"{host} redirected to {other}, and the headers were not sent there" && crossed.InnerException == null, "a redirect to another host with headers: " + crossed?.Message);
        Check(!server.Requests().Any(r => r.Path == "/b"), "the other host never sees the headers");
        Check(await Run(Request("get", server.Url("/cross"), new { responseType = "object" })) == null, "a redirect to another host without headers is followed");
        Check(Last("/b").Headers["Host"] == other, "the other host is asked: " + Last("/b").Headers["Host"]);

        foreach (var (status, method) in new[] { (303, "POST"), (302, "POST"), (301, "POST"), (303, "PUT") })
        {
            server.Clear();
            Redirect("/see", status, "/b");
            var posted = Request("get", server.Url("/see"), new JsonObject { ["method"] = method, ["responseType"] = "object", ["body"] = Lit("object", new { on = true }), ["headers"] = Lit("object", new Dictionary<string, string> { ["Content-Type"] = "application/merge-patch+json" }) });
            Check(await Run(posted) == null, $"a {status} after a {method} is followed");
            var then = Last("/b");
            Check(then.Method == "GET" && then.Body.Length == 0 && !then.Headers.ContainsKey("Content-Type"), $"a {status} after a {method} arrives as a GET without the body: {then.Method} {then.Body.Length} bytes");
            Check(Last("/see").Headers["Content-Type"] == "application/merge-patch+json", $"the {method} itself had its body's type");
        }
        foreach (var status in new[] { 307, 308 })
        {
            server.Clear();
            Redirect("/keep", status, "/b");
            Check(await Run(Request("get", server.Url("/keep"), new JsonObject { ["method"] = "POST", ["responseType"] = "object", ["body"] = Lit("object", new { on = true }) })) == null, $"a {status} is followed");
            var kept = Last("/b");
            Check(kept.Method == "POST" && System.Text.Encoding.UTF8.GetString(kept.Body) == """{"on":true}""" && kept.Headers["Content-Type"] == "application/json", $"a {status} keeps the method and the body: {kept.Method}");
        }

        for (var i = 0; i < 6; i++) Redirect($"/r{i}", 302, i == 5 ? "/b" : $"/r{i + 1}");
        Check(await Run(Request("get", server.Url("/r1"), new { responseType = "object" })) == null, "five redirects are followed");
        var many = await Run(Request("get", server.Url("/r0"), new { responseType = "object" }));
        Check(many?.Message == $"{host} redirected too many times", "six redirects fail: " + many?.Message);

        Redirect("/nowhere", 302, null);
        Redirect("/ftp", 301, "ftp://example.com/b");
        var nowhere = await Run(Request("get", server.Url("/nowhere"), new { responseType = "object" }));
        Check(nowhere?.Message == $"{host} redirected without an address to go to", "a redirect without an address: " + nowhere?.Message);
        var ftp = await Run(Request("get", server.Url("/ftp"), new { responseType = "object" }));
        Check(ftp?.Message == $"{host} redirected to an address that is not http or https", "a redirect to ftp: " + ftp?.Message);

        // Where a redirect goes is part of the response: with the response kept out of run history, a failure after a
        // redirect to another host names the host asked and says "another host" for the one it went to.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var closed = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
        listener.Stop();
        server.Respond("/missing", new() { Status = 404 });
        Redirect("/gone", 302, server.OtherHostUrl("/missing"));
        Redirect("/away", 302, $"http://localhost:{closed}/b");
        foreach (var hidden in new[] { false, true })
        {
            JsonObject Config(bool headers = false)
            {
                var config = new JsonObject { ["responseType"] = "object", ["hideResponse"] = hidden };
                if (headers) config["headers"] = Headers();
                return config;
            }
            var (crossedTo, missing, unreachable) = (await Run(Request("get", server.Url("/cross"), Config(headers: true))),
                await Run(Request("get", server.Url("/gone"), Config())), await Run(Request("get", server.Url("/away"), Config())));
            string[] want = hidden
                ? [$"{host} redirected to another host, and the headers were not sent there", $"{host} redirected to another host, which answered HTTP 404", $"{host} redirected to another host, which could not be reached"]
                : [$"{host} redirected to {other}, and the headers were not sent there", $"HTTP 404 from {other}", $"Could not reach localhost:{closed}"];
            string?[] got = [crossedTo?.Message, missing?.Message, unreachable?.Message];
            Check(got.SequenceEqual(want), $"the failures after a redirect, {(hidden ? "with" : "without")} the response kept out of run history: " + string.Join(" | ", got));
        }
    }

    /// <summary>
    /// No client of the process keeps a cookie: one a server sets is not sent with a later request to it, from the same
    /// definition's <c>Http</c>, another definition's, a download, or an HTTP request step; a definition that needs a
    /// session sends the Cookie header itself.
    /// </summary>
    static async Task NothingKeepsACookie()
    {
        await using var server = LoopbackServer.Start();
        server.Respond("/login", new() { Body = "ok"u8.ToArray(), Headers = new Dictionary<string, string> { ["Set-Cookie"] = "session=s3cr3t; Path=/" } });
        server.Respond("/status", LoopbackServer.Reply.Text("""{"at":"b"}"""));
        string? Sent() => server.Requests().Last(r => r.Path == "/status").Headers.GetValueOrDefault("Cookie");
        var sent = new List<string>();
        using (var mine = SharpHttp.Create())
        {
            await mine.GetStringAsync(server.Url("/login"));
            await mine.GetStringAsync(server.Url("/status"));
            sent.Add("the same client: " + (Sent() ?? "none"));
            using var other = SharpHttp.Create();
            await other.GetStringAsync(server.Url("/status"));
            sent.Add("another definition's: " + (Sent() ?? "none"));
        }
        var root = Path.Combine(Path.GetTempPath(), "cookies-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = new SharpFiles(root, _ => { }, () => new Receiving());
            await files.Download(server.Url("/login"), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("ok"u8.ToArray())));
            var body = System.Text.Encoding.UTF8.GetBytes("""{"at":"b"}""");
            await files.Download(server.Url("/status"), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body)));
            sent.Add("a download: " + (Sent() ?? "none"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        using var compiled = await Compiled.Of(Started([Request("login", server.Url("/login")), Request("get", server.Url("/status"), new { responseType = "object" }), Field("at", "/at", "string"), SetState("set", "reading")],
            [D("get", "at", "value", "body"), D("at", "set", "value")]), "two requests");
        await compiled.Run("setup", new FakeDefinition());
        sent.Add("an HTTP request step: " + (Sent() ?? "none"));
        Same(sent, ["the same client: none", "another definition's: none", "a download: none", "an HTTP request step: none"], "the cookie the server set");
    }

    /// <summary>
    /// A header that cannot be sent is refused before anything is sent: a name that cannot be a header's, a value with a
    /// line break or a character that is not ASCII (which .NET only refuses when sending), and a body's header without a
    /// body; each by its own plain message, which never quotes the value.
    /// </summary>
    static async Task HttpRequestRefusesAHeaderBeforeSending()
    {
        await using var server = LoopbackServer.Start();
        server.Respond("/status", LoopbackServer.Reply.Text("ok"));
        foreach (var (name, value, message) in new[]
        {
            ("Bad Name", "x", "HTTP request · Headers: Bad Name is not a header name"),
            ("", "x", "HTTP request · Headers: Unnamed is not a header name"),
            ("X:Name", "x", "HTTP request · Headers: X:Name is not a header name"),
            ("X-Name", "café", "HTTP request · Headers: X-Name has a value that cannot be sent in a header; use plain ASCII text on one line"),
            ("X-Name", "a\r\nb", "HTTP request · Headers: X-Name has a value that cannot be sent in a header; use plain ASCII text on one line"),
            ("Content-Type", "text/plain", "HTTP request · Headers: Content-Type can only be sent with a body"),
        })
        {
            var request = Request("get", server.Url("/status"), new JsonObject { ["headers"] = Lit("object", new Dictionary<string, string> { [name] = value }) });
            using var compiled = await Compiled.Of(Started([request], []), "a header " + name);
            var error = await Fails(() => compiled.Run("setup", new FakeDefinition()), "a header " + name);
            Check(error.Message == message && error.InnerException == null, $"the header {name}: {error.Message}");
        }
        Check(server.Requests().Length == 0, "a header that cannot be sent sends nothing: " + server.Requests().Length);
    }

    /// <summary>
    /// An empty answer read as JSON fails by saying it is empty; a compressed answer is read decompressed, and its
    /// decompressed size counts against the limit.
    /// </summary>
    static async Task HttpRequestReadsAnEmptyOrCompressedAnswer()
    {
        await using var server = LoopbackServer.Start();
        var host = "127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture);
        static byte[] Gzip(byte[] bytes)
        {
            using var packed = new MemoryStream();
            using (var gzip = new System.IO.Compression.GZipStream(packed, System.IO.Compression.CompressionLevel.SmallestSize)) gzip.Write(bytes);
            return packed.ToArray();
        }
        var gzipped = new Dictionary<string, string> { ["Content-Encoding"] = "gzip", ["Content-Type"] = "application/json" };
        server.Respond("/none", new() { Status = 204 });
        server.Respond("/empty", new() { Body = [] });
        server.Respond("/packed", new() { Body = Gzip(System.Text.Encoding.UTF8.GetBytes("""{"at":"packed"}""")), Headers = gzipped });
        var big = new byte[5 * 1024 * 1024]; Array.Fill(big, (byte)' ');
        server.Respond("/bomb", new() { Body = Gzip(big), Headers = gzipped });
        foreach (var (path, type) in new[] { ("/none", "object"), ("/empty", "array") })
        {
            using var compiled = await Compiled.Of(Started([Request("get", server.Url(path), new { responseType = type })], []), "an empty answer");
            var error = await Fails(() => compiled.Run("setup", new FakeDefinition()), "an empty answer at " + path);
            Check(error.Message == $"The answer from {host} is empty", $"an empty answer at {path}: {error.Message}");
        }
        using var packed = await Compiled.Of(Started([Request("get", server.Url("/packed"), new { responseType = "object" }), Field("at", "/at", "string"), SetState("set", "reading")], [D("get", "at", "value", "body"), D("at", "set", "value")]), "a compressed answer");
        var definition = new FakeDefinition();
        await packed.Run("setup", definition);
        Same(definition.Calls, ["state reading value=string:packed"], "a compressed answer is read decompressed");
        Check(server.Requests().Last().Headers.GetValueOrDefault("Accept-Encoding")?.Contains("gzip", StringComparison.Ordinal) == true, "the request takes a compressed answer");
        using var bomb = await Compiled.Of(Started([Request("get", server.Url("/bomb"))], []), "a compressed answer larger than the limit");
        var tooLarge = await Fails(() => bomb.Run("setup", new FakeDefinition()), "a compressed answer larger than the limit");
        Check(tooLarge.Message == $"The answer from {host} is larger than 4 MiB" && Gzip(big).Length < 64 * 1024, "the decompressed size counts: " + tooLarge.Message);
    }

    // ---- Pictures ----

    /// <summary>A small JPEG, as a camera gives one.</summary>
    static byte[] Picture() => SharpImage.Blank(48, 32, SharpColor.Blue).ToJpeg();
    /// <summary>Home Assistant's cameras: each picture asked for is recorded as "definition camera" and answered by <paramref name="answer"/>.</summary>
    static List<string> Cameras(Action<Action<byte[]>, Action<string>> answer)
    {
        var asked = new List<string>();
        PyInterop.BeginCameraSnapshot = (definition, camera, ok, fail) => { lock (asked) asked.Add(definition + " " + camera); answer(ok, fail); };
        return asked;
    }
    static JsonObject Snapshot(string id, string camera) => N(id, "camera_snapshot", new JsonObject { ["entity"] = Lit("entity_id:camera", camera) });
    static JsonObject Rect(string id, object? x = null) => N(id, "image_draw_rect", new JsonObject
    {
        ["image"] = JsonSerializer.SerializeToNode(Unset), ["x"] = Lit("number", x ?? 2), ["y"] = Lit("number", 2), ["width"] = Lit("number", 20), ["height"] = Lit("number", 10),
    });
    static JsonObject Label(string id, JsonNode? text = null) => N(id, "image_draw_text", new JsonObject
    {
        ["image"] = JsonSerializer.SerializeToNode(Unset), ["text"] = text ?? Lit("string", "Porch"), ["x"] = Lit("number", 1), ["y"] = Lit("number", 1),
    });
    /// <summary>The size a trace gives a picture of <paramref name="bytes"/>: its kilobytes, rounded up.</summary>
    static string Kilobytes(byte[] bytes) => ((bytes.Length + 1023) / 1024).ToString(CultureInfo.InvariantCulture) + " KB";
    /// <summary>Whether the image has read its picture's header or encoded itself: neither is needed to draw.</summary>
    static bool Untouched(SharpImage image)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        return (int)typeof(SharpImage).GetField("width", flags)!.GetValue(image)! == 0 && typeof(SharpImage).GetField("encoded", flags)!.GetValue(image) == null;
    }

    /// <summary>
    /// Take a picture asks Home Assistant for the camera's picture under its definition's id; Draw rectangle and Draw text
    /// each draw on a copy of the picture wired to them, so the picture itself and every other step's copy stay as they
    /// were: two steps drawing on one picture give two images of one drawing each. Drawing reads and encodes nothing; the
    /// picture with its drawings is a JPEG of its own.
    /// </summary>
    static async Task DrawingStepsDrawOnACopyOfThePicture()
    {
        var picture = Picture();
        var asked = Cameras((ok, _) => ok(picture));
        var graph = new JsonObject
        {
            ["version"] = 5, ["name"] = "Porch",
            ["nodes"] = new JsonArray(N("t", "trigger", new { type = "manual" }), Snapshot("snap", "camera.porch"), Rect("rect"), Label("label"), Rect("other"), N("keep", "keep")),
            ["edges"] = new JsonArray(E("t", "snap"), E("snap", "keep"), D("snap", "rect", "image", "image"), D("rect", "label", "image", "image"), D("snap", "other", "image", "image"),
                D("snap", "keep", "source", "image"), D("rect", "keep", "rect", "image"), D("label", "keep", "label", "image"), D("other", "keep", "other", "image")),
        };
        // The step "keep" stands for one that takes a picture (Task 6's Show frame): it keeps what is wired to it.
        var kept = new Dictionary<string, SharpImage>(StringComparer.Ordinal);
        var types = new Dictionary<string, Type> { ["boolean"] = typeof(bool), ["number"] = typeof(double), ["string"] = typeof(string), ["image"] = typeof(SharpImage) };
        var runtime = new VisualRuntime(graph.ToJsonString(), VisualNodeCatalog.Root.ToJsonString(), types, TriggerPayload.Manual(), default, (id, scope, run) =>
        {
            if (id == "keep") foreach (var port in new[] { "source", "rect", "label", "other" }) kept[port] = (SharpImage)run.Input(scope, id, port)!;
            return Task.CompletedTask;
        });
        var prior = InvocationContext.Current;
        try
        {
            InvocationContext.Current = new InvocationContext("run-" + Guid.NewGuid().ToString("N"), InvocationCause.Entity, null, TriggerPayload.Manual()) { Definition = new FakeDefinition { DefinitionId = "porch" } };
            await runtime.RunAsync("t").WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { InvocationContext.Current = prior; }
        Same(asked, ["porch camera.porch"], "the picture is asked for under the definition's id");
        Check(kept.Count == 4, "the step kept every image: " + string.Join(", ", kept.Keys));
        Check(kept["source"].Drawings == 0 && kept["rect"].Drawings == 1 && kept["label"].Drawings == 2 && kept["other"].Drawings == 1,
            $"drawings: source {kept["source"].Drawings}, rectangle {kept["rect"].Drawings}, text on it {kept["label"].Drawings}, the other rectangle {kept["other"].Drawings}");
        Check(kept.Values.Distinct(ReferenceEqualityComparer.Instance).Count() == 4, "each step's image is one of its own");
        Check(kept.Values.All(Untouched), "drawing neither reads the picture nor encodes it");
        Check(ReferenceEquals(kept["source"].ToJpeg(), picture), "the picture taken is passed on as it came");
        var drawn = kept["label"].ToJpeg();
        Check(drawn is [0xFF, 0xD8, ..] && !drawn.AsSpan().SequenceEqual(picture) && !drawn.AsSpan().SequenceEqual(kept["rect"].ToJpeg()), "the picture with its drawings is a JPEG of its own");
    }

    /// <summary>
    /// A camera Home Assistant gives no picture of fails Take a picture with Home Assistant's message, which its On error
    /// wire takes; a camera that does not answer is waited for until the run is cancelled, which ends it at once.
    /// </summary>
    static async Task TakeAPictureFailsToItsErrorWireAndIsCancelledWithItsRun()
    {
        var asked = Cameras((_, fail) => fail("Camera is off"));
        using var off = await Compiled.Of(Started([Snapshot("snap", "camera.porch"), N("why", "last_error"), SetState("said", "reading")],
            [E("snap", "said", "error"), D("why", "said", "value", "message")]), "a picture of a camera that is off");
        var definition = new FakeDefinition { DefinitionId = "porch" };
        await off.Run("setup", definition);
        Same(definition.Calls, ["state reading value=string:Camera is off"], "the On error wire is taken with Home Assistant's message");
        Same(asked, ["porch camera.porch"], "the picture is asked for under the definition's id");

        asked = Cameras((_, _) => { });
        using var silent = await Compiled.Of(Started([Snapshot("snap", "camera.porch"), SetState("set", "reading", new JsonObject { ["value"] = Lit("string", "taken") })], []), "a picture of a camera that does not answer");
        using var cancel = new CancellationTokenSource();
        definition = new FakeDefinition();
        var run = silent.Run("setup", definition, cancel.Token);
        await EntityTestHome.Until(() => { lock (asked) return asked.Count == 1; }, "the picture is asked for");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        cancel.Cancel();
        var cancelled = await Fails(() => run, "a run cancelled while it waits for a picture");
        Check(cancelled is OperationCanceledException && clock.Elapsed < TimeSpan.FromSeconds(1), $"the run ends cancelled at once: {cancelled.GetType().Name} after {clock.Elapsed.TotalMilliseconds} ms");
        Same(definition.Calls, [], "a cancelled picture sets nothing");
    }

    /// <summary>
    /// A picture that goes into an input of any type (here Create object) is written as its short text, as a trace shows
    /// it: nothing reads its size, nothing is decoded, and none of its bytes go along.
    /// </summary>
    static async Task APictureInAnObjectIsItsShortText()
    {
        var picture = Picture();
        Cameras((ok, _) => ok(picture));
        var log = PyInterop.Log;
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>(); PyInterop.Log = (_, m) => messages.Enqueue(m);
        try
        {
            using var compiled = await Compiled.Of(Started([Snapshot("snap", "camera.porch"), Rect("rect"), N("obj", "object_create", new JsonObject { ["fields"] = new JsonObject { ["picture"] = new JsonObject { ["kind"] = "unset" } } }), N("say", "log", new { value = Unset })],
                [D("snap", "rect", "image", "image"), D("rect", "obj", "field:picture", "image"), D("obj", "say", "value")]), "a picture in an object");
            await compiled.Run("setup", new FakeDefinition());
            var said = messages.Single(m => m.Contains("picture", StringComparison.Ordinal));
            Check(JsonNode.Parse(said)?["picture"] is JsonValue text && text.TryGetValue<string>(out var shown) && shown == $"Image · {(picture.Length + 1023) / 1024} KB · 1 drawing", "the picture in the object is its short text: " + said);
            Check(!said.Contains("Width", StringComparison.OrdinalIgnoreCase) && !said.Contains(Convert.ToBase64String(picture)[..20], StringComparison.Ordinal), "and nothing else of it: " + said);
        }
        finally { PyInterop.Log = log; }
    }

    // The drawings a picture carries, as their records write themselves (Rectangle { X = 11, ... }).
    static string[] Drawn(SharpImage image) =>
        [.. ((System.Collections.IEnumerable)typeof(SharpImage).GetField("drawings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(image)!).Cast<object>().Select(d => d.ToString()!)];

    /// <summary>
    /// A position or size that is not whole is rounded to the nearest pixel (a half away from zero), typed or wired; a
    /// value that is not a number, or is infinite, fails the drawing step, naming it and the input, and the step that uses it.
    /// </summary>
    static async Task ADrawingStepRoundsAPositionThatIsNotWhole()
    {
        var picture = Picture();
        Cameras((ok, _) => ok(picture));
        JsonObject Show(string id) => N(id, "show_frame", new JsonObject { ["entity"] = "lens", ["image"] = JsonSerializer.SerializeToNode(Unset) });
        foreach (var (typed, x) in new[] { (10.5, 11), (-0.5, -1), (10.4, 10), (2.0, 2) })
        {
            using var compiled = await Compiled.Of(Entity([N("setup", "entity_setup"), Hub("lens", "lens", "camera"), Snapshot("snap", "camera.porch"), Rect("rect", typed), Show("show")],
                [E("setup", "snap"), E("snap", "show"), D("snap", "rect", "image", "image"), D("rect", "show", "image", "image")]), $"a rectangle at {typed}");
            var definition = new FakeDefinition();
            await compiled.Run("setup", definition);
            var drawn = Drawn(definition.Frames.Single()).Single();
            Check(drawn.StartsWith($"Rectangle {{ X = {x}, Y = 2, Width = 20, Height = 10,", StringComparison.Ordinal), $"a rectangle typed at {typed} is drawn at {x}: " + drawn);
        }

        // Wired: a Custom C# node gives the value, which the test sets.
        var value = N("value", "custom", new JsonObject
        {
            ["source"] = "public static class CustomNode\n{\n    public static double Value;\n    [Node(\"Value\", Pure = true)]\n    public static double Run() => Value;\n}",
            ["arguments"] = new JsonObject(),
        });
        var wired = Rect("rect");
        wired["config"]!["width"] = JsonSerializer.SerializeToNode(Unset);
        using var through = await Compiled.Of(Entity([N("setup", "entity_setup"), Hub("lens", "lens", "camera"), Snapshot("snap", "camera.porch"), value, wired, Show("show")],
            [E("setup", "snap"), E("snap", "show"), D("snap", "rect", "image", "image"), D("value", "rect", "width", "result"), D("rect", "show", "image", "image")]), "a rectangle of a wired width");
        var field = through.Assembly.GetTypes().Select(t => t.GetField("Value")).Single(f => f != null)!;
        foreach (var (given, width) in new[] { (10.5, 11), (-0.5, -1), (10.4, 10) })
        {
            field.SetValue(null, given);
            var definition = new FakeDefinition();
            await through.Run("setup", definition);
            var drawn = Drawn(definition.Frames.Single()).Single();
            Check(drawn.Contains($", Width = {width}, ", StringComparison.Ordinal), $"a width wired as {given} is drawn as {width}: " + drawn);
        }
        foreach (var (given, shown) in new[] { (double.NaN, "NaN"), (double.PositiveInfinity, "Infinity"), (double.NegativeInfinity, "-Infinity"), (1_000_000_001.0, "1000000001") })
        {
            field.SetValue(null, given);
            var definition = new FakeDefinition();
            var error = await Fails(() => through.Run("setup", definition), $"a width wired as {shown}");
            Check(error.Message == $"Draw rectangle (used by Show frame): Width must be a number of pixels from -1000000000 to 1000000000, not {shown}", $"a width wired as {shown}: " + error.Message);
            Same(definition.Calls, [], $"a width of {shown} shows nothing");
        }
    }

    /// <summary>
    /// A run's trace shows a picture as a short text, its size and how many drawings it carries, never its bytes, and
    /// stays within its limits; a Log step writes the same text. A picture is never secret: one with a secret drawn on it
    /// is shown, while the secret text going into the step is hidden.
    /// </summary>
    static async Task APictureIsTracedAndLoggedAsAShortText()
    {
        var picture = Picture();
        Cameras((ok, _) => ok(picture));
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>(); PyInterop.Log = (_, m) => messages.Enqueue(m);
        using var compiled = await Compiled.Of(Started([Snapshot("snap", "camera.porch"), Setting("caption", "caption"), Rect("rect"), Label("label", JsonSerializer.SerializeToNode(Unset)), N("say", "log", new { value = Unset })],
            [D("snap", "rect", "image", "image"), D("rect", "label", "image", "image"), D("caption", "label", "text"), D("label", "say", "value", "image")], ("caption", "string")), "a picture traced");
        var definition = new FakeDefinition { Settings = { ["caption"] = "hunter2" }, Secrets = { "caption" } };
        var sink = new TraceSink();
        await compiled.Run("setup", definition, sink: sink);
        var size = Kilobytes(picture);
        var taken = sink.Event("snap")["outputs"]!["image"]!;
        Check(taken["type"]?.GetValue<string>() == "image" && taken["v"]?.GetValue<string>() == $"Image · {size} · 0 drawings" && taken["trunc"] == null, "the picture taken: " + taken.ToJsonString());
        Check(sink.Event("rect")["outputs"]!["image"]!["v"]?.GetValue<string>() == $"Image · {size} · 1 drawing", "one drawing: " + sink.Event("rect")["outputs"]!.ToJsonString());
        var labelled = sink.Event("label");
        Check(labelled["outputs"]!["image"]!["v"]?.GetValue<string>() == $"Image · {size} · 2 drawings" && labelled["outputs"]!["image"]!["redacted"] == null, "a picture with a secret drawn on it is shown: " + labelled["outputs"]!.ToJsonString());
        Check(labelled["inputs"]!["text"]!["redacted"]?.GetValue<bool>() == true && labelled["inputs"]!["image"]!["v"]?.GetValue<string>() == $"Image · {size} · 1 drawing", "the secret going in is hidden: " + labelled["inputs"]!.ToJsonString());
        Check(messages.Contains($"Image · {size} · 2 drawings"), "the Log step writes the short text: " + string.Join(" | ", messages));
        var text = sink.Final.ToJsonString();
        var base64 = Convert.ToBase64String(picture);
        Check(!text.Contains("hunter2", StringComparison.Ordinal) && !text.Contains(base64[..40], StringComparison.Ordinal) && !text.Contains("[255,216", StringComparison.Ordinal) && !text.Contains("255, 216", StringComparison.Ordinal),
            "the trace has no bytes and no secret: " + text);
        Check(sink.Final["truncated"]?.GetValue<bool>() == false && !text.Contains("\"trunc\"", StringComparison.Ordinal) && System.Text.Encoding.UTF8.GetByteCount(text) < VisualTrace.MaxEventBytes,
            $"the trace stays within its limits: {System.Text.Encoding.UTF8.GetByteCount(text)} bytes");
    }

    /// <summary>
    /// A camera's frame flow runs the steps after its pin, as a When started flow does, and Show frame hands the picture wired
    /// to it to its definition for the camera it names; a run already cancelled shows nothing.
    /// </summary>
    static async Task ShowFrameHandsThePictureToTheDefinition()
    {
        var picture = Picture();
        var asked = Cameras((ok, _) => ok(picture));
        using var compiled = await Compiled.Of(Entity(
            [Hub("lens", "lens", "camera"), Snapshot("snap", "camera.porch"), Rect("rect"), N("show", "show_frame", new JsonObject { ["entity"] = "lens", ["image"] = JsonSerializer.SerializeToNode(Unset) })],
            [Flow("lens", "frame", "snap"), E("snap", "show"), D("snap", "rect", "image", "image"), D("rect", "show", "image", "image")]), "a frame flow");
        var definition = new FakeDefinition { DefinitionId = "lens" };
        await compiled.Run("lens", "frame", definition);
        Same(definition.Calls, ["frame lens 1 drawing(s)"], "Show frame hands the picture with its rectangle to the definition");
        Same(asked, ["lens camera.porch"], "the picture is taken under the definition's id");
        var shown = definition.Frames.Single().ToJpeg();
        Check(shown is [0xFF, 0xD8, ..] && !shown.AsSpan().SequenceEqual(picture), "the frame shown is the picture drawn on");

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        definition = new FakeDefinition();
        await Fails(() => compiled.Run("lens", "frame", definition, token: cancel.Token), "a frame flow cancelled before it starts");
        Same(definition.Calls, [], "a cancelled run shows no frame");
    }

    // ---- Entity nodes ----

    /// <summary>An Entity node (<see cref="VisualExecutionTests.Hub"/>) with <paramref name="values"/> set on it as its state values, when given.</summary>
    static JsonObject Hub(string id, string key, string domain, JsonObject? values = null, JsonObject? more = null) => VisualExecutionTests.Hub(id, key, domain, more, values);
    /// <summary>An entity definition's graph of these nodes and wires, without settings.</summary>
    static JsonObject Nodes(JsonObject[] nodes, params JsonObject[] edges) => Entity(nodes, edges);
    static JsonObject Constant(string id, bool value) => N(id, "constant", new { type = "boolean", value = Lit("boolean", value) });
    static JsonObject Said(string id) => N(id, "log", new { value = Lit("string", id) });

    /// <summary>An Entity node's state flow hands the value wired to it to the definition, then marks the entity available.</summary>
    static async Task AnEntityNodeWorksOutItsState()
    {
        using var compiled = await Compiled.Of(Nodes([Hub("door", "door", "binary_sensor"), Constant("c", true)], D("c", "door", "is_on")), "an Entity node with a wired value");
        var definition = new FakeDefinition();
        await compiled.Run("door", "state", definition);
        Same(definition.Calls, ["state door is_on=bool:true", "available door yes"], "the node sets the state, then the availability");
    }

    /// <summary>A value set on the node is its state; a value it is not given, or one left unset, is not handed over.</summary>
    static async Task ASetValueOnTheNodeIsItsState()
    {
        var values = new JsonObject { ["is_on"] = Lit("boolean", true), ["brightness"] = JsonSerializer.SerializeToNode(Unset) };
        using var compiled = await Compiled.Of(Nodes([Hub("lamp", "lamp", "light", values)]), "an Entity node with a set value");
        var definition = new FakeDefinition();
        await compiled.Run("lamp", "state", definition);
        Same(definition.Calls, ["state lamp is_on=bool:true", "available lamp yes"], "only the value set is handed over");
        Check(!definition.Calls.Any(c => c.Contains("brightness", StringComparison.Ordinal)), "the brightness, left unset, is not handed over: " + string.Join(" | ", definition.Calls));
    }

    /// <summary>
    /// A value that cannot be worked out (a value node that throws, wired over the unset value the editor leaves) marks the
    /// entity unavailable and sets no state; the run fails with the value's own error.
    /// </summary>
    static async Task AFailingValueMakesTheEntityUnavailable()
    {
        var broken = N("bad", "custom", new JsonObject
        {
            ["source"] = "public static class CustomNode\n{\n    [Node(\"Broken\", Pure = true)]\n    public static bool Run() => throw new InvalidOperationException(\"no reading\");\n}",
            ["arguments"] = new JsonObject(),
        });
        var values = new JsonObject { ["is_on"] = JsonSerializer.SerializeToNode(Unset) };
        using var compiled = await Compiled.Of(Nodes([Hub("door", "door", "binary_sensor", values), broken], D("bad", "door", "is_on", "result")), "an Entity node with a failing value");
        var definition = new FakeDefinition();
        var error = await Fails(() => compiled.Run("door", "state", definition), "an Entity node whose value fails");
        Check(error.Message.Contains("no reading", StringComparison.Ordinal), "the run fails with the value's error: " + error.Message);
        Same(definition.Calls, ["available door no"], "the entity is marked unavailable, and no state is set");
    }

    /// <summary>Each command runs the steps connected to its pin, and only those.</summary>
    static async Task ACommandRunsTheStepsOnItsPin()
    {
        var graph = Nodes(
            [Hub("relay", "relay", "switch"), SetState("set_on", "relay", new JsonObject { ["is_on"] = Lit("boolean", true) }), SetState("set_off", "relay", new JsonObject { ["is_on"] = Lit("boolean", false) })],
            Flow("relay", "command:turn_on", "set_on"), Flow("relay", "command:turn_off", "set_off"));
        using var compiled = await Compiled.Of(graph, "a switch node with both commands");
        var definition = new FakeDefinition();
        await compiled.Run("relay", "command:turn_on", definition);
        Same(definition.Calls, ["state relay is_on=bool:true"], "Turn on runs its steps");
        definition.Calls.Clear();
        await compiled.Run("relay", "command:turn_off", definition);
        Same(definition.Calls, ["state relay is_on=bool:false"], "Turn off runs its steps");
    }

    /// <summary>
    /// A command's value is read on its flow from the command's data; a command that came without it (one the host could
    /// not fill: it fills the catalog's default, see <c>EntityGraphTests.ALightNodeTurnedOnWithoutABrightnessIsAtFull</c>)
    /// fails the step that reads it, in words.
    /// </summary>
    static async Task ACommandsValueIsReadOnItsFlow()
    {
        var graph = Nodes([Hub("lamp", "lamp", "light", more: new JsonObject { ["name"] = "Lamp", ["options"] = new JsonObject { ["dimmable"] = true } }), SetState("set", "lamp")],
            Flow("lamp", "command:turn_on", "set"), D("lamp", "set", "brightness", "command:turn_on:brightness"));
        using var compiled = await Compiled.Of(graph, "a light node reading its brightness");
        var definition = new FakeDefinition();
        var sink = new TraceSink();
        await compiled.Run("lamp", "command:turn_on", definition, TriggerPayload.Command("lamp", "turn_on", JsonSerializer.SerializeToElement(new { brightness = 40 })), sink);
        Same(definition.Calls, ["state lamp brightness=double:40"], "the command's brightness is the state's");
        Check(sink.Event("lamp")["outputs"]?["command:turn_on:brightness"]?["v"]?.GetValue<double>() == 40, "the node is traced with the command's value: " + sink.Event("lamp").ToJsonString());
        definition.Calls.Clear();
        var absent = await Fails(() => compiled.Run("lamp", "command:turn_on", definition, TriggerPayload.Command("lamp", "turn_on", null)), "a command without its value");
        Check(absent.Message == "Set state: Turn on came without a brightness, and Lamp has none", "the step reading the value fails in words: " + absent.Message);
        Same(definition.Calls, [], "nothing is set");
    }

    /// <summary>A camera node's frame pin runs the steps connected to it, which show a frame.</summary>
    static async Task AFrameRunsTheStepsOnItsPin()
    {
        var picture = Picture();
        var asked = Cameras((ok, _) => ok(picture));
        using var compiled = await Compiled.Of(Nodes(
            [Hub("cam", "lens", "camera"), Snapshot("snap", "camera.porch"), Rect("rect"), N("show", "show_frame", new JsonObject { ["entity"] = "lens", ["image"] = JsonSerializer.SerializeToNode(Unset) })],
            Flow("cam", "frame", "snap"), E("snap", "show"), D("snap", "rect", "image", "image"), D("rect", "show", "image", "image")), "a camera node's frame flow");
        var definition = new FakeDefinition { DefinitionId = "lens" };
        await compiled.Run("cam", "frame", definition);
        Same(definition.Calls, ["frame lens 1 drawing(s)"], "Show frame hands the picture with its rectangle to the definition");
        Same(asked, ["lens camera.porch"], "the picture is taken under the definition's id");
    }

    /// <summary>
    /// A node's roots are its wired flows, before the old kinds': its state when a value is supplied, each command whose
    /// pin is wired, its frame when wired; each named for the run history. A node with nothing supplied or wired has none.
    /// </summary>
    static void TheRootsOfANodeAreItsWiredFlows()
    {
        var door = Hub("door", "door", "binary_sensor", new JsonObject { ["is_on"] = Lit("boolean", true) }, new JsonObject { ["name"] = "" });
        var relay = Hub("relay", "relay", "switch", more: new JsonObject { ["name"] = "Relay" });
        var cam = Hub("cam", "lens", "camera");
        var graph = Nodes([door, relay, cam, N("old", "entity_command", new { entity = "other", command = "turn_on" }), Said("x")],
            Flow("relay", "command:turn_on", "x"), Flow("relay", "command:turn_off", "x"), Flow("cam", "frame", "x"));
        var roots = DefinitionGraph.Roots(graph, VisualCatalogSnapshot.BuiltIn);
        Same(roots.States, [VisualNodeCatalog.FlowMethod(door, "state")], "the door's state is a State");
        Check(roots.StateEntities[VisualNodeCatalog.FlowMethod(door, "state")] == "door", "of the door");
        Same([.. roots.Commands.Select(p => p.Key + "=" + p.Value)],
            [$"relay:turn_on={VisualNodeCatalog.FlowMethod(relay, "command:turn_on")}", $"relay:turn_off={VisualNodeCatalog.FlowMethod(relay, "command:turn_off")}", $"other:turn_on={VisualNodeCatalog.EffectiveTriggerMethod(N("old", "entity_command"))}"],
            "each wired command is a command flow, then the old kind's");
        Check(roots.Frames["lens"] == VisualNodeCatalog.FlowMethod(cam, "frame"), "the camera's frame flow");
        var flows = new[] { door, relay, cam }.SelectMany(n => DefinitionGraph.HubFlows(n, graph, VisualCatalogSnapshot.BuiltIn)).ToArray();
        Same([.. flows.Select(f => $"{f.NodeId} {f.Port} {f.Kind} {f.Key} {f.Command ?? "-"} {f.Label} {f.Method}")],
            [$"door state state door - door: State {VisualNodeCatalog.FlowMethod(door, "state")}",
             $"relay command:turn_on command relay turn_on Relay: Turn on {VisualNodeCatalog.FlowMethod(relay, "command:turn_on")}",
             $"relay command:turn_off command relay turn_off Relay: Turn off {VisualNodeCatalog.FlowMethod(relay, "command:turn_off")}",
             $"cam frame frame lens - lens: When a frame is needed {VisualNodeCatalog.FlowMethod(cam, "frame")}"],
            "the flows, in order, named by the entity's name, else its key");
        var unnamed = Hub("n", "", "binary_sensor", new JsonObject { ["is_on"] = Lit("boolean", false) }, new JsonObject { ["name"] = "" });
        Check(DefinitionGraph.HubFlows(unnamed, Nodes([unnamed]), VisualCatalogSnapshot.BuiltIn).Single().Label == "Entity: State", "an entity with neither name nor key is named Entity");

        var half = Hub("relay", "relay", "switch");
        var halfRoots = DefinitionGraph.Roots(Nodes([half, Said("x")], Flow("relay", "command:turn_on", "x")), VisualCatalogSnapshot.BuiltIn);
        Check(halfRoots.Commands.ContainsKey("relay:turn_on") && !halfRoots.Commands.ContainsKey("relay:turn_off") && halfRoots.States.Count == 0,
            "a command whose pin is not wired has no flow: " + string.Join(",", halfRoots.Commands.Keys));
        static JsonObject Light() => Hub("lamp", "lamp", "light", new JsonObject { ["is_on"] = JsonSerializer.SerializeToNode(Unset) });
        var light = Light();
        Check(DefinitionGraph.HubFlows(light, Nodes([light]), VisualCatalogSnapshot.BuiltIn).Count == 0, "a node with nothing supplied and nothing wired has no flow");
        var wiredLight = Light();
        var wired = Nodes([wiredLight, Constant("c", true)], D("c", "lamp", "is_on"));
        Check(DefinitionGraph.HubFlows(wiredLight, wired, VisualCatalogSnapshot.BuiltIn).Select(f => f.Port).SequenceEqual(["state"]), "a wired value is supplied over the unset one");
    }

    /// <summary>A graph whose only Entity node has no flow builds a class with no action and no flow method.</summary>
    static async Task AGraphOfANodeWithoutFlowsCompiles()
    {
        using var compiled = await Compiled.Of(Nodes([Hub("b", "press", "button")]), "a button node with nothing connected");
        var type = compiled.Assembly.GetType("VisualAutomation")!;
        Check(!type.GetMethods().Any(m => m.Name.StartsWith("Run_", StringComparison.Ordinal)), "the class has no flow method: " + string.Join(",", type.GetMethods().Select(m => m.Name)));
        Check(((Automation)Activator.CreateInstance(type)!).Configure().Actions.Count == 0, "and no action");
    }

    /// <summary>A node's run history switches decide which of its flows are recorded: by default its commands only.</summary>
    static void HistoryFollowsTheNodesSwitches()
    {
        static HubFlow[] Flows(JsonObject? history)
        {
            var more = new JsonObject { ["options"] = new JsonObject { ["dimmable"] = true } };
            if (history != null) more["history"] = history;
            var light = Hub("lamp", "lamp", "light", new JsonObject { ["is_on"] = Lit("boolean", true) }, more);
            var cam = Hub("cam", "lens", "camera", more: history == null ? null : new JsonObject { ["history"] = history.DeepClone() });
            var graph = Nodes([light, cam, Said("x")], Flow("lamp", "command:turn_on", "x"), Flow("cam", "frame", "x"));
            return [.. DefinitionGraph.HubFlows(light, graph, VisualCatalogSnapshot.BuiltIn), .. DefinitionGraph.HubFlows(cam, graph, VisualCatalogSnapshot.BuiltIn)];
        }
        Same([.. Flows(null).Select(f => f.Kind + "=" + f.TrackHistory)], ["state=False", "command=True", "frame=False"], "by default only commands are recorded");
        Same([.. Flows(new JsonObject { ["state"] = true, ["commands"] = false, ["frame"] = true }).Select(f => f.Kind + "=" + f.TrackHistory)], ["state=True", "command=False", "frame=True"],
            "the switches flip them");
    }
}
