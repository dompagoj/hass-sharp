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
        public readonly Dictionary<string, object?> Settings = new(StringComparer.Ordinal);
        public readonly HashSet<string> Secrets = new(StringComparer.Ordinal);
        public bool FailUnavailable;
        public void SetState(string entityKey, IReadOnlyDictionary<string, object?> values, IReadOnlySet<string> hidden) =>
            Calls.Add($"state {entityKey} " + string.Join(" ", values.Select(p => p.Key + "=" + Typed(p.Value))));
        public void SetAvailable(string entityKey, bool available)
        {
            Calls.Add($"available {entityKey} {(available ? "yes" : "no")}");
            if (!available && FailUnavailable) throw new InvalidOperationException("The definition is stopping");
        }
        public object? Setting(string key) => Settings.TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);
        public bool IsSecret(string key) => Secrets.Contains(key);
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

    /// <summary>An entity definition's graph: the nodes and wires, and a definition of these entities and settings.</summary>
    static JsonObject Entity(JsonObject[] nodes, JsonObject[] edges, (string Key, string Domain)[] entities, params (string Key, string Type)[] settings) => new()
    {
        ["version"] = 5, ["name"] = "Entity", ["nodes"] = new JsonArray(nodes), ["edges"] = new JsonArray(edges),
        ["definition"] = new JsonObject
        {
            ["entities"] = new JsonArray(entities.Select(e => (JsonNode)new JsonObject { ["key"] = e.Key, ["domain"] = e.Domain }).ToArray()),
            ["settings"] = new JsonArray(settings.Select(s => (JsonNode)new JsonObject { ["key"] = s.Key, ["type"] = s.Type }).ToArray()),
        },
    };
    static readonly object Unset = new { kind = "unset" };
    static JsonObject Is(string id, string entity, string state = "on") => N(id, "entity_is", new JsonObject { ["entity"] = Lit("entity_id", entity), ["state"] = Lit("string", state) });
    static JsonObject Current(string id, string entity) => N(id, "entity_current", new JsonObject { ["entity"] = Lit("entity_id", entity) });
    static JsonObject State(string id, string entity, JsonObject? values = null)
    {
        var config = values ?? new JsonObject(); config["entity"] = entity;
        return N(id, "entity_state", config);
    }
    static JsonObject SetState(string id, string entity, JsonObject? values = null)
    {
        var config = values ?? new JsonObject(); config["entity"] = entity;
        return N(id, "set_entity_state", config);
    }
    static JsonObject Setting(string id, string key) => N(id, "setting", new { key });
    /// <summary>The acceptance graph: a binary sensor on while either of two motion sensors is.</summary>
    static JsonObject AnyMotion() => Entity(
        [Is("a", "binary_sensor.motion_a"), Is("b", "binary_sensor.motion_b"), N("or", "or", new { left = Unset, right = Unset }), State("s", "any_motion")],
        [D("a", "or", "left"), D("b", "or", "right"), D("or", "s", "is_on")],
        [("any_motion", "binary_sensor")]);

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
        public async Task Run(string root, IDefinitionRuntime? definition, CancellationToken token = default, IRunTraceSink? sink = null)
        {
            var method = VisualNodeCatalog.EffectiveTriggerMethod(new JsonObject { ["id"] = root });
            var prior = InvocationContext.Current;
            Task task;
            try
            {
                InvocationContext.Current = new InvocationContext("run-" + Guid.NewGuid().ToString("N"), InvocationCause.Entity, null, TriggerPayload.Manual()) { Definition = definition, TraceSink = sink };
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
        var entity = PyInterop.Entity; var log = PyInterop.Log; var trigger = UserScriptManager.CurrentTrigger.Value;
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
                (nameof(EntityIsWorksInAnAutomation), EntityIsWorksInAnAutomation) })
            {
                States.Clear(); UserScriptManager.CurrentTrigger.Value = null;
                try { await test(); }
                catch (Exception e) { failures.Add($"{name}: {e.Message}"); }
            }
        }
        finally { PyInterop.Entity = entity; PyInterop.Log = log; UserScriptManager.CurrentTrigger.Value = trigger; States.Clear(); }
        if (failures.Count > 0) throw new Exception($"Entity graph runtime: {failures.Count} failing:\n" + string.Join("\n", failures));
        Console.WriteLine("PASS entity graph runtime: State sets state and availability, Set state, Set available and Setting reach the definition, Has state and Current state read Home Assistant, entity nodes need a definition");
    }

    /// <summary>A State hands its inputs to the definition, then marks the entity available.</summary>
    static async Task StateNodeSetsStateFromItsInputs()
    {
        using var compiled = await Compiled.Of(Entity([State("s", "door", new JsonObject { ["is_on"] = Lit("boolean", true) })], [], [("door", "binary_sensor")]), "a State of a chosen value");
        var definition = new FakeDefinition();
        await compiled.Run("s", definition);
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
            await compiled.Run("s", definition);
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
            [Current("cur", "sensor.outside"), N("f", "entity_field", new { entity = Unset, field = "state", type = "number" }), State("s", "copy")],
            [D("cur", "f", "entity"), D("f", "s", "value")],
            [("copy", "sensor")]);
        using var compiled = await Compiled.Of(graph, "a sensor copying another");
        var definition = new FakeDefinition();
        var error = await Fails(() => compiled.Run("s", definition), "a State reading a missing entity");
        Check(error.Message.Contains("Entity 'sensor.outside' is unavailable.", StringComparison.Ordinal), "the run fails with the unavailable message: " + error.Message);
        Same(definition.Calls, ["available copy no"], "a State that cannot be worked out marks its entity unavailable, and sets no state");
        States["sensor.outside"] = "21.5";
        await compiled.Run("s", definition);
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
        using var compiled = await Compiled.Of(Entity([wait, State("s", "door")], [D("wait", "s", "is_on", "result")], [("door", "binary_sensor")]), "a State waiting on its input");
        var definition = new FakeDefinition();
        using var cancel = new CancellationTokenSource(); cancel.CancelAfter(100);
        var error = await Fails(() => compiled.Run("s", definition, cancel.Token), "a cancelled State");
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
        using var compiled = await Compiled.Of(Entity([cancel, State("s", "door")], [D("cancel", "s", "is_on", "result")], [("door", "binary_sensor")]), "a State cancelled by its input");
        using var source = new CancellationTokenSource();
        var field = compiled.Assembly.GetTypes().Select(t => t.GetField("Source")).Single(f => f != null)!;
        field.SetValue(null, source);
        var definition = new FakeDefinition();
        var error = await Fails(() => compiled.Run("s", definition, source.Token), "a State cancelled before publishing");
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
            [Current("cur", "sensor.outside"), N("f", "entity_field", new { entity = Unset, field = "state", type = "number" }), State("s", "copy")],
            [D("cur", "f", "entity"), D("f", "s", "value")],
            [("copy", "sensor")]);
        using var compiled = await Compiled.Of(graph, "a sensor copying a missing one");
        var definition = new FakeDefinition { FailUnavailable = true };
        var error = await Fails(() => compiled.Run("s", definition), "a State whose unavailable marking fails");
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
            [Setting("k1", "password"), State("s1", "label"), Setting("k2", "name"), State("s2", "plain")],
            [D("k1", "s1", "value"), D("k2", "s2", "value")],
            [("label", "sensor"), ("plain", "sensor")],
            ("password", "string"), ("name", "string"));
        using var compiled = await Compiled.Of(graph, "States of a secret and a plain setting");
        var definition = new FakeDefinition { Settings = { ["password"] = "hunter2", ["name"] = "kitchen" }, Secrets = { "password" } };
        var secret = new TraceSink(); var plain = new TraceSink();
        await compiled.Run("s1", definition, sink: secret);
        await compiled.Run("s2", definition, sink: plain);
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
            [N("on", "entity_command", new { entity = "relay", command = "turn_on" }), Setting("tok", "token"), Setting("flag", "armed"), Setting("door", "door"),
             N("list", "array_create", new JsonObject { ["items"] = new JsonArray(new JsonObject { ["kind"] = "unset" }) }),
             N("loop", "foreach", new JsonObject { ["items"] = new JsonObject { ["kind"] = "unset" }, ["itemType"] = "string" }), N("say", "log", new { value = Unset }),
             N("i", "if", new { value = Unset }), N("is", "entity_is", new JsonObject { ["entity"] = new JsonObject { ["kind"] = "unset" }, ["state"] = Lit("string", "on") }),
             N("as_id", "convert", new { type = "entity_id", value = Unset }), N("yes", "log", new { value = Unset })],
            [E("on", "loop"), E("loop", "say", "body"), E("loop", "i"), E("i", "yes", "true"),
             D("tok", "list", "item:0"), D("list", "loop", "items"), D("loop", "say", "value", "item"), D("flag", "i", "value"), D("door", "as_id", "value"), D("as_id", "is", "entity"), D("is", "yes", "value")],
            [("relay", "switch")],
            ("token", "string"), ("armed", "boolean"), ("door", "string"));
        using var compiled = await Compiled.Of(graph, "a loop over a secret and a condition of one");
        States["binary_sensor.secret_door"] = "on";
        var definition = new FakeDefinition { Settings = { ["token"] = Secret, ["armed"] = true, ["door"] = "binary_sensor.secret_door" }, Secrets = { "token", "armed", "door" } };
        var sink = new TraceSink();
        await compiled.Run("on", definition, sink: sink);
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
            await compiled.Run("s", definition);
            Same(definition.Calls, [$"state any_motion is_on=bool:{(b == "on" ? "true" : "false")}", "available any_motion yes"], $"motion A missing, motion B {b}");
        }
        // Unavailable is a state like any other: asked for, it is equal.
        using var asked = await Compiled.Of(Entity([Is("u", "binary_sensor.motion_b", "unavailable"), State("s", "gone")], [D("u", "s", "is_on")], [("gone", "binary_sensor")]), "a State of an unavailable entity");
        var unavailable = new FakeDefinition();
        await asked.Run("s", unavailable);
        Same(unavailable.Calls, ["state gone is_on=bool:true", "available gone yes"], "Has state unavailable is true for an unavailable entity");
        States.Remove("binary_sensor.motion_b");
        var missing = new FakeDefinition();
        await asked.Run("s", missing);
        Same(missing.Calls, ["state gone is_on=bool:false", "available gone yes"], "Has state unavailable is false for a missing entity");
    }

    /// <summary>Has state reads the state the change that started the run carries for that entity, not an older one Home Assistant still reports.</summary>
    static async Task EntityIsReadsTheTriggeringEntitysFreshState()
    {
        using var compiled = await Compiled.Of(AnyMotion(), "the derived binary sensor");
        States["binary_sensor.motion_a"] = "off"; States["binary_sensor.motion_b"] = "off";
        UserScriptManager.CurrentTrigger.Value = new(new() { EntityId = "binary_sensor.motion_a", State = "on" });
        var definition = new FakeDefinition();
        await compiled.Run("s", definition);
        Same(definition.Calls, ["state any_motion is_on=bool:true", "available any_motion yes"], "the triggering entity's own state is read");
    }

    /// <summary>When commanded and When started run their steps; Set available and Set state call the definition.</summary>
    static async Task SetStateAndSetAvailableStepsCallTheDefinition()
    {
        static JsonObject Available(string id, bool available) => N(id, "set_entity_available", new JsonObject { ["entity"] = "relay", ["available"] = Lit("boolean", available) });
        var graph = Entity(
            [N("on", "entity_command", new { entity = "relay", command = "turn_on" }), SetState("on_set", "relay", new JsonObject { ["is_on"] = Lit("boolean", true) }),
             N("setup", "entity_setup"), Available("up", true), SetState("first", "relay", new JsonObject { ["is_on"] = Lit("boolean", false) }), Available("down", false)],
            [E("on", "on_set"), E("setup", "up"), E("up", "first"), E("first", "down")],
            [("relay", "switch")]);
        using var compiled = await Compiled.Of(graph, "a switch with a command and a setup");
        var definition = new FakeDefinition();
        await compiled.Run("setup", definition);
        Same(definition.Calls, ["available relay yes", "state relay is_on=bool:false", "available relay no"], "When started runs its steps in order");
        definition.Calls.Clear();
        await compiled.Run("on", definition);
        Same(definition.Calls, ["state relay is_on=bool:true"], "When commanded sets the state and nothing else");
    }

    /// <summary>A state set by a step stays set when a later step fails; the run fails, and the entity is not marked unavailable.</summary>
    static async Task StateSetBeforeALaterFailingStepStaysSet()
    {
        var graph = Entity(
            [N("on", "entity_command", new { entity = "relay", command = "turn_on" }), SetState("set", "relay", new JsonObject { ["is_on"] = Lit("boolean", true) }),
             N("read", "read_entity", new JsonObject { ["entity"] = Lit("entity_id", "sensor.gone") })],
            [E("on", "set"), E("set", "read")],
            [("relay", "switch")]);
        using var compiled = await Compiled.Of(graph, "a command that fails after setting the state");
        var definition = new FakeDefinition();
        var error = await Fails(() => compiled.Run("on", definition), "a command whose later step fails");
        Check(error.Message.Contains("Entity 'sensor.gone' is unavailable.", StringComparison.Ordinal), "the run fails with the later step's error: " + error.Message);
        Same(definition.Calls, ["state relay is_on=bool:true"], "the state set before the failure stays set, and nothing marks the entity unavailable");
    }

    /// <summary>A Setting is the definition's value, as text, true or false, or a number.</summary>
    static async Task SettingGivesItsValueAsItsType()
    {
        var graph = Entity(
            [Setting("k1", "name"), State("s1", "label"), Setting("k2", "flag"), State("s2", "flag_copy"), Setting("k3", "level"), State("s3", "level_copy")],
            [D("k1", "s1", "value"), D("k2", "s2", "is_on"), D("k3", "s3", "value")],
            [("label", "sensor"), ("flag_copy", "binary_sensor"), ("level_copy", "sensor")],
            ("name", "string"), ("flag", "boolean"), ("level", "number"));
        using var compiled = await Compiled.Of(graph, "States of settings");
        var definition = new FakeDefinition { Settings = { ["name"] = "kitchen", ["flag"] = true, ["level"] = 4.5 } };
        foreach (var root in new[] { "s1", "s2", "s3" }) await compiled.Run(root, definition);
        Same(definition.Calls, ["state label value=string:kitchen", "available label yes", "state flag_copy is_on=bool:true", "available flag_copy yes", "state level_copy value=double:4.5", "available level_copy yes"],
            "each Setting gives its value as its type");
    }

    /// <summary>A sensor's value takes a number, handed over as a number, or text, handed over as text.</summary>
    static async Task SensorStateAcceptsNumberAndText()
    {
        var graph = Entity(
            [State("n", "count", new JsonObject { ["value"] = Lit("number", 3) }), State("t", "mode", new JsonObject { ["value"] = Lit("string", "21.5") }),
             Current("cur", "sensor.outside"), N("f", "entity_field", new { entity = Unset, field = "state", type = "string" }), State("w", "copy")],
            [D("cur", "f", "entity"), D("f", "w", "value")],
            [("count", "sensor"), ("mode", "sensor"), ("copy", "sensor")]);
        using var compiled = await Compiled.Of(graph, "sensors of numbers and text");
        States["sensor.outside"] = "12";
        var definition = new FakeDefinition();
        foreach (var root in new[] { "n", "t", "w" }) await compiled.Run(root, definition);
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
    /// Every entity node needs the definition of the run in progress; without one it fails, saying so. A State, When
    /// commanded and When started fail as the run starts. A Setting, Set state and Set available are each reached on
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
            [State("s", "door", new JsonObject { ["is_on"] = Lit("boolean", true) }),
             N("on", "entity_command", new { entity = "relay", command = "turn_on" }), SetState("on_set", "relay", new JsonObject { ["is_on"] = Lit("boolean", true) }),
             N("setup", "entity_setup"), N("setup_up", "set_entity_available", new JsonObject { ["entity"] = "relay", ["available"] = Lit("boolean", true) }),
             Manual("t1"), Setting("k", "name"), N("show", "log", new { value = Unset }),
             Manual("t2"), Said("before_set", "before set"), SetState("set", "relay", new JsonObject { ["is_on"] = Lit("boolean", true) }),
             Manual("t3"), Said("before_up", "before available"), N("up", "set_entity_available", new JsonObject { ["entity"] = "relay", ["available"] = Lit("boolean", true) })],
            [E("on", "on_set"), E("setup", "setup_up"), E("t1", "show"), D("k", "show", "value"), E("t2", "before_set"), E("before_set", "set"), E("t3", "before_up"), E("before_up", "up")],
            [("door", "binary_sensor"), ("relay", "switch")],
            ("name", "string"));
        using var compiled = await Compiled.Of(graph, "entity nodes of every kind");
        foreach (var root in new[] { "s", "on", "setup" })
        {
            var error = await Fails(() => compiled.Run(root, null), $"root {root} without a definition");
            Check(error is InvalidOperationException && error.Message == Message, $"root {root} fails as it starts, saying it needs a definition: {error.GetType().Name}: {error.Message}");
        }
        var setting = await Fails(() => compiled.Run("t1", null), "a Setting without a definition");
        Check(setting is InvalidOperationException && setting.Message == "Setting (used by Log): " + Message, "the Setting fails itself, named with the step reading it: " + setting.Message);
        foreach (var (root, said, step) in new[] { ("t2", "before set", "Set state"), ("t3", "before available", "Set available") })
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
}
