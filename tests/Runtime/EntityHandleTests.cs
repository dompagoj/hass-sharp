using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;
using HassSharp.Entities;
using static VisualExecutionTests;

/// <summary>
/// The base entity handle: it reads the live state, converts to and from its id in the runtime, serializes as its id,
/// and does not clash with a node library's own types of the same names.
/// </summary>
static class EntityHandleTests
{
    static T Throws<T>(Action action, string what) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        catch (Exception error) { throw new Exception($"{what}: threw {error.GetType().Name} instead of {typeof(T).Name}: {error.Message}"); }
        throw new Exception($"{what}: did not throw {typeof(T).Name}");
    }

    [RuntimeSuite("--entity-handles", Order = 37)]
    public static async Task Run()
    {
        var entity = PyInterop.Entity; var trigger = UserScriptManager.CurrentTrigger.Value;
        try
        {
            Basics();
            Missing();
            Conversions();
            TriggerState();
            OwnNames();
            await Copied();
            await Pins();
            await ErrorPin();
        }
        finally { PyInterop.Entity = entity; UserScriptManager.CurrentTrigger.Value = trigger; }
    }

    static void Basics()
    {
        PyInterop.Entity = id => new() { EntityId = id, State = "on", Attributes = new() { ["brightness"] = 128 } };
        var light = new Entity("light.kitchen");
        Check(light.EntityId == "light.kitchen" && (string)light == "light.kitchen" && light.ToString() == "light.kitchen", "id");
        Check(light.RawState == "on" && light.NumericState == null, "state");
        Check(light.Attribute("brightness")?.GetInt32() == 128 && light.Attribute("missing") == null, "attribute");
        Check(light.Snapshot().State == "on", "snapshot");
        Check(light.Equals(new Entity("light.kitchen")) && !light.Equals(new Entity("light.hall")) && light.GetHashCode() == new Entity("light.kitchen").GetHashCode(), "equality");
        PyInterop.Entity = id => new() { EntityId = id, State = "21.5" };
        Check(new Entity("sensor.t").NumericState == 21.5, "numeric");
        PyInterop.Entity = id => new() { EntityId = id, State = "NaN" };
        Check(new Entity("sensor.t").NumericState == null, "a state that is not finite is no number");
        foreach (var bad in new[] { "not an id", "light", "Light.kitchen", "light.kitchen.x", "" })
            Throws<ArgumentException>(() => _ = new Entity(bad), "bad id " + bad);
        Console.WriteLine("PASS entity handles: id, state, attributes, snapshot, equality and validation");
    }

    // A handle for an entity Home Assistant does not have fails when it is read, naming the entity.
    static void Missing()
    {
        PyInterop.Entity = _ => null!;
        var error = Throws<InvalidOperationException>(() => _ = new Entity("light.gone").RawState, "missing");
        Check(error.Message.Contains("light.gone"), "names the entity");
        Console.WriteLine("PASS entity handles: a missing entity fails when read, and names itself");
    }

    static void Conversions()
    {
        Check(VisualRuntime.Coerce("light.kitchen", typeof(Entity), "x", false) is Entity { EntityId: "light.kitchen" }, "id to handle");
        Check(VisualRuntime.Coerce(new Entity("light.kitchen"), typeof(string), "x", false) is "light.kitchen", "handle to id");
        Check(VisualRuntime.Coerce(null, typeof(Entity), "x", false) == null, "null stays null");
        var made = VisualRuntime.Coerce("light.kitchen", typeof(Entity), "x", false, entities: id => new Derived(id));
        Check(made is Derived, "the factory makes the handle");
        var wrong = Throws<InvalidOperationException>(() => VisualRuntime.Coerce("light.kitchen", typeof(Derived), "x", false, () => "Step · Pin"), "wrong class");
        Check(wrong.Message == "Step · Pin needs Derived, and light.kitchen is not one", "wrong class message: " + wrong.Message);
        ServiceTarget? target = new Entity("light.kitchen"); ServiceTarget? same = "light.kitchen";
        Check(target!.ToJson()!.ToJsonString() == same!.ToJson()!.ToJsonString(), "target");
        ServiceTarget? several = new[] { new Entity("light.a"), new Entity("light.b") };
        Check(several!.ToJson()!.ToJsonString() == """{"entity_id":["light.a","light.b"]}""", "targets");
        Check(JsonSerializer.Serialize(new Entity("light.kitchen")) == "\"light.kitchen\"", "json");
        Check(JsonSerializer.Deserialize<Entity>("\"light.kitchen\"")!.EntityId == "light.kitchen", "json back");
        Check(JsonSerializer.Deserialize<Derived>("\"light.kitchen\"") is Derived { EntityId: "light.kitchen" }, "json back, a subclass");
        Check(JsonSerializer.Serialize(new[] { new Derived("light.a") }) == "[\"light.a\"]", "json, a subclass");
        Throws<JsonException>(() => JsonSerializer.Deserialize<Entity>("\"nope\""), "json, a bad id");
        Check(Encoding.UTF8.GetString(TraceJson.Value(new Entity("light.kitchen"), 1000).Json) == "\"light.kitchen\"", "trace");
        Console.WriteLine("PASS entity handles: conversions with ids, service targets, JSON and trace");
    }

    // The converter attribute is not inherited: a handle class carries it itself to be read from JSON as its own type.
    [System.Text.Json.Serialization.JsonConverter(typeof(EntityJsonConverter))]
    class Derived(string id) : Entity(id);

    // A handle for the entity that started the run reads the state the run was given, as ReadEntity does.
    static void TriggerState()
    {
        PyInterop.Entity = id => new() { EntityId = id, State = "from-home-assistant" };
        UserScriptManager.CurrentTrigger.Value = new(new() { EntityId = "sensor.door", State = "from-trigger" });
        Check(new Entity("sensor.door").RawState == "from-trigger" && new Entity("sensor.other").RawState == "from-home-assistant", "trigger state");
        UserScriptManager.CurrentTrigger.Value = null;
        Check(new Entity("sensor.door").RawState == "from-home-assistant", "no trigger");
        Console.WriteLine("PASS entity handles: the triggering entity reads the trigger's state");
    }

    // A library may declare types named like the handle classes: its own win in its code.
    static void OwnNames()
    {
        var library = new NodeLibraryCompiler(Diagnostics).Preview([new("own", """
            public record Entity(string Name);
            public record LightEntity(string Name);
            public static class Own
            {
                [Node("Make entity")] public static Entity MakeEntity() => new("a");
                [Node("Make light")] public static LightEntity MakeLight() => new("b");
            }
            """, 1)]);
        Check(library.Valid, "a library with its own Entity and LightEntity: " + string.Join("; ", library.Diagnostics.Select(d => d.Message)));
        Console.WriteLine("PASS entity handles: a library's own Entity and LightEntity compile");
        // A handle converts to a non-null string without a nullability warning, in a declaration and as an argument.
        var converted = new NodeLibraryCompiler(Diagnostics).Preview([new("converted", """
            public static class Converted
            {
                /// <summary>Id.</summary>
                [Node("Id")] public static string Id(Entity entity) { string id = entity; return id; }
                /// <summary>State.</summary>
                [Node("State")] public static EntityStateSnapshot State(NodeContext context, Entity entity) => context.State(entity);
            }
            """, 1)]);
        Check(converted.Valid && converted.Diagnostics.Length == 0, "a handle converts to string without diagnostics: " + string.Join("; ", converted.Diagnostics.Select(d => d.Message)));
        Console.WriteLine("PASS entity handles: a handle converts to a non-null string without warnings");
    }

    // A value wired into a For each body is copied as an immutable value: the handle is the same one there and reads.
    static async Task Copied()
    {
        PyInterop.Entity = id => new() { EntityId = id, State = "on" };
        var handle = new Entity("light.kitchen");
        Check(ReferenceEquals(new VisualSnapshot().Copy(handle), handle), "copy keeps the handle");
        var log = PyInterop.Log; var messages = new System.Collections.Concurrent.ConcurrentQueue<string>(); PyInterop.Log = (_, m) => messages.Enqueue(m);
        try
        {
            var graph = G(Code("make", "return new Entity(\"light.kitchen\");", returns: "Entity"), N("f", "foreach", new { items = Lit("array", new[] { 1 }), itemType = "number" }),
                Code("read", "Logger.Info(\"read \" + e.RawState);", "Entity e"));
            graph["edges"] = new System.Text.Json.Nodes.JsonArray(E("t", "make"), E("make", "f"), E("f", "read", "body"), D("make", "read", "e", "result"));
            await Execute(graph);
            Check(messages.Contains("read on"), "the handle is read in the For each body");
        }
        finally { PyInterop.Log = log; }
        Console.WriteLine("PASS entity handles: a handle wired into a For each body is read there");
    }

    const string Motion = """
        public static class Motion
        {
            [Node("Is on")] public static bool IsOn(BinarySensorEntity sensor) => sensor.State == BinarySensorState.On;
            [Node("Id")] public static string Id(Entity entity) => entity.EntityId;
            [Node("Pick")] public static LightEntity Pick(LightEntity light, LightEntity? other = null) => other ?? light;
            [Node("Both")] public static (LightEntity Light, BinarySensorEntity Sensor) Both(LightEntity? maybe) => (new("light.a"), new("binary_sensor.b"));
            [Node("Each")] public static async Task Each([Port("Each", FlowControl = true)] Func<LightEntity, Task> each) => await each(new("light.a"));
        }
        """;
    const string TurnOn = """{"light":{"turn_on":{"target":{"entity":{"domain":"light"}},"fields":{}}}}""";

    /// <summary>Records the last document a run's trace writes.</summary>
    sealed class Sink : IRunTraceSink
    {
        public JsonObject? Final;
        void IRunTraceSink.Write(string json, int eventCount, bool truncated, bool final, bool immediate) { if (final) Final = JsonNode.Parse(json)!.AsObject(); }
    }

    /// <summary>Compiles the graph against the provider's classes, runs its trigger with the payload, and returns the run's trace.</summary>
    internal static async Task<JsonObject> Traced(DiagnosticsProvider provider, JsonObject graph, VisualCatalogSnapshot snapshot, JsonElement payload)
    {
        var preview = new VisualGenerator(provider).Preview(graph, snapshot);
        Check(preview.Valid, "the graph does not compile: " + JsonSerializer.Serialize(preview.Diagnostics));
        var compiled = await new CodeCompiler(provider).CompileSingleFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cs"), preview.Source);
        try
        {
            var instance = (Automation)Activator.CreateInstance(compiled.Assembly.GetType("VisualAutomation")!)!;
            using var cts = new CancellationTokenSource(); instance._cancellationToken.Value = cts.Token;
            var sink = new Sink(); var prior = InvocationContext.Current; Task run;
            InvocationContext.Current = new("run-" + Guid.NewGuid().ToString("N"), InvocationCause.Entity, null, payload) { TraceSink = sink };
            try { run = (Task)instance.GetType().GetMethod("Run_74")!.Invoke(instance, null)!; }
            finally { InvocationContext.Current = prior; }
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            return sink.Final ?? throw new Exception("the run wrote no final trace");
        }
        finally { compiled.Unload(); }
    }
    internal static JsonObject Event(JsonObject trace, string node) => trace["events"]!.AsArray().Select(e => e!.AsObject()).Single(e => e["node"]!.GetValue<string>() == node);

    // A handle type on a node method is a pin of the entity id type, of its domain for a domain's class; the wire and
    // the trace carry the id, the method gets the handle.
    static async Task Pins()
    {
        var provider = new DiagnosticsProvider();
        var installed = provider.UseEntitySchema([.. EntitySchemaCatalog.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ha-entity-components.json"))).Items.Select(p => KeyValuePair.Create(p.Key, (JsonObject)p.Value!))]);
        Check(installed.Accepted, "the fixture's classes were not installed");
        var services = ServiceSchemaCatalog.Parse(TurnOn, _ => { });
        provider.UseServiceSchema(services);
        var library = new NodeLibraryCompiler(provider).Preview([new("motion", Motion, 1)]);
        Check(library.Valid, "the library taking handles: " + string.Join("; ", library.Diagnostics.Select(d => d.Message)));
        var snapshot = library.Snapshot.WithServices(services);
        var catalog = snapshot.Catalog;
        string Kind(string name) => snapshot.Methods.Values.Single(m => m.Method.Name == name).Kind;
        JsonNode node(string name) => catalog["nodes"]![Kind(name)]!;
        static IEnumerable<string> Types(JsonNode node, string input) => node["inputs"]![input]!["types"]!.AsArray().Select(t => t!.GetValue<string>());

        Check(Types(node("IsOn"), "sensor").SequenceEqual(["entity_id:binary_sensor"]), "domain pin");
        Check(Types(node("Id"), "entity").SequenceEqual(["entity_id"]), "any entity");
        Check(node("Pick")["outputs"]!["result"]!["type"]!.GetValue<string>() == "entity_id:light", "result");
        Check(node("Pick")["inputs"]!["other"]!["required"]!.GetValue<bool>() == false && node("Pick")["inputs"]!["other"]!["default"] == null, "optional");
        Check(Types(node("Pick"), "other").SequenceEqual(["entity_id:light"]), "a ? does not change the pin's type");
        Check(!catalog["types"]!.AsObject().ContainsKey("global::HassSharp.Entities.LightEntity"), "no second type");
        Check(!catalog["types"]!.AsObject().Any(t => t.Key.StartsWith("global::HassSharp.Entities.", StringComparison.Ordinal) && t.Key.EndsWith("Entity", StringComparison.Ordinal)), "no handle class is a type: " + string.Join(", ", catalog["types"]!.AsObject().Select(t => t.Key).Where(k => k.Contains("Entities"))));
        Check(node("Both")["outputs"]!["light"]!["type"]!.GetValue<string>() == "entity_id:light" && node("Both")["outputs"]!["sensor"]!["type"]!.GetValue<string>() == "entity_id:binary_sensor", "tuple elements");
        Check(node("Both")["inputs"]!["maybe"]!["required"]!.GetValue<bool>() == false && node("Both")["inputs"]!["maybe"]!["default"] == null, "nullable without a default");
        Check(node("Each")["outputs"]!["each"]!["type"]!.GetValue<string>() == "entity_id:light", "flow value");
        var pick = snapshot.Methods.Values.Single(m => m.Method.Name == "Pick");
        Check(pick.Parameters[0].TypeId == "entity_id:light" && pick.Parameters[0].TypeExpression == "global::HassSharp.Entities.LightEntity", "the C# identity stays the expression");

        PyInterop.Entity = id => new() { EntityId = id, State = "on" };
        HasEntityState State(string id) => new() { EntityId = id, State = "on", LastChanged = 1_700_000_000, LastReported = 1_700_000_000 };
        JsonObject Step(string name, string id) => VisualGraphContract.CreateNode(Kind(name), id, snapshot);
        string Errors(JsonObject graph) => string.Join("; ", VisualGraphValidator.Validate(graph, snapshot).Select(d => d.Message));
        // An entity trigger's entity into the pin: its domain's class reaches the method, its id the trace.
        JsonObject Triggered(string entity)
        {
            var graph = G(Step("IsOn", "on"));
            graph["nodes"]![0]!["config"] = new JsonObject { ["type"] = "entity", ["entity"] = entity };
            graph["edges"]!.AsArray().Add(D("t", "on", "sensor", "entity_id"));
            return graph;
        }
        var hall = Triggered("binary_sensor.hall");
        Check(Errors(hall) == "", "the binary sensor trigger into the pin: " + Errors(hall));
        var traced = Event(await Traced(provider, hall, snapshot, TriggerPayload.Entity(State("binary_sensor.hall"), null)), "on");
        Check(traced["inputs"]!["sensor"]!["v"]!.GetValue<string>() == "binary_sensor.hall" && traced["outputs"]!["result"]!["v"]!.GetValue<bool>(), "the trace of the pin: " + traced.ToJsonString());
        var light = Triggered("light.hall");
        var wrong = VisualGraphValidator.Validate(light, snapshot);
        var expected = VisualGraphValidator.Words.Incompatible(catalog, "Sensor", "entity_id:light", ["entity_id:binary_sensor"]);
        Check(wrong.Any(d => d.NodeId == "on" && d.InputId == "sensor" && d.Message == expected), $"a light into the binary sensor pin, expected {expected}: " + Errors(light));

        // A literal entity of the pin's domain is typed in; one of another domain is refused.
        var literal = G(Step("IsOn", "on"));
        literal["nodes"]![1]!["config"]!["arguments"]!["sensor"] = Lit("entity_id:binary_sensor", "binary_sensor.hall");
        Check(Errors(literal) == "", "a literal binary sensor: " + Errors(literal));
        traced = Event(await Traced(provider, literal, snapshot, TriggerPayload.Manual()), "on");
        Check(traced["inputs"]!["sensor"]!["v"]!.GetValue<string>() == "binary_sensor.hall" && traced["outputs"]!["result"]!["v"]!.GetValue<bool>(), "the trace of a literal: " + traced.ToJsonString());
        literal["nodes"]![1]!["config"]!["arguments"]!["sensor"] = Lit("entity_id:binary_sensor", "light.hall");
        Check(VisualGraphValidator.Validate(literal, snapshot).Any(d => d.NodeId == "on" && d.InputId == "sensor"), "a literal light into the binary sensor pin: " + Errors(literal));

        // A returned handle goes out as its id: into a Call service target that takes lights.
        var made = new List<string>(); var call = PyInterop.CallService;
        PyInterop.CallService = (domain, name, json, done, _) => { lock (made) made.Add(domain + "." + name + " " + json); done?.Invoke(); };
        try
        {
            var picked = G(Step("Pick", "pick"), N("s", "service", new JsonObject { ["service"] = "light.turn_on", ["fields"] = new JsonObject(), ["target"] = new JsonObject { ["kind"] = "unset" }, ["returnResponse"] = false }));
            picked["nodes"]![1]!["config"]!["arguments"]!["light"] = Lit("entity_id:light", "light.kitchen");
            picked["edges"]!.AsArray().Add(D("pick", "s", "target", "result"));
            Check(Errors(picked) == "", "a picked light into the light target: " + Errors(picked));
            await Traced(provider, picked, snapshot, TriggerPayload.Manual());
            Check(made.Count == 1 && made[0].StartsWith("light.turn_on ", StringComparison.Ordinal) && JsonNode.DeepEquals(JsonNode.Parse(made[0]["light.turn_on ".Length..]), JsonNode.Parse("""{"entity_id":"light.kitchen"}""")), "the call: " + string.Join(" | ", made));
        }
        finally { PyInterop.CallService = call; }

        // A handle that may be empty goes out of no pin, and none goes inside a list, another type or a tuple.
        void Refused(string code, string message)
        {
            var preview = new NodeLibraryCompiler(provider).Preview([new("bad", "public static class Bad { " + code + " }", 1)]);
            Check(!preview.Valid && preview.Diagnostics.Any(d => d.Message == message) && preview.Snapshot.Methods.Count == 0, code + ": " + string.Join("; ", preview.Diagnostics.Select(d => d.Message)));
        }
        Refused("[Node(\"Find\")] public static LightEntity? Find() => null;", NodeLibraryCompiler.EntityResultMessage);
        Refused("[Node(\"Find\")] public static Task<LightEntity?> Find() => Task.FromResult<LightEntity?>(null);", NodeLibraryCompiler.EntityResultMessage);
        Refused("[Node(\"Find\")] public static (LightEntity? Light, int Count) Find() => default;", NodeLibraryCompiler.EntityResultMessage);
        Refused("[Node(\"Each\")] public static async Task Each([Port(\"Each\", FlowControl = true)] Func<LightEntity?, Task> each) => await each(null);", NodeLibraryCompiler.EntityResultMessage);
        Refused("[Node(\"Count\")] public static int Count(List<LightEntity> lights) => lights.Count;", NodeLibraryCompiler.EntityNestedMessage);
        Refused("[Node(\"Count\")] public static int Count(Entity[] lights) => lights.Length;", NodeLibraryCompiler.EntityNestedMessage);
        Refused("[Node(\"Count\")] public static int Count((LightEntity Light, int Count) pair) => pair.Count;", NodeLibraryCompiler.EntityNestedMessage);
        Refused("[Node(\"All\")] public static List<LightEntity> All() => [];", NodeLibraryCompiler.EntityNestedMessage);
        Refused("[Node(\"All\")] public static (LightEntity[] Lights, int Count) All() => default;", NodeLibraryCompiler.EntityNestedMessage);

        // A Custom C# node's pins are derived the same way, and deriving again leaves them as they are.
        var custom = G(N("c", "custom", new JsonObject { ["source"] = "public static class CustomNode\n{\n    [Node(\"Motion\")]\n    public static bool Run(BinarySensorEntity motionSensor) => motionSensor.State == BinarySensorState.On;\n}", ["arguments"] = new JsonObject() }));
        var derived = new VisualGenerator(provider).DeriveSignatures(custom, snapshot);
        var signature = derived["nodes"]![1]!["config"]!["signature"]!.AsObject();
        Check(Types(signature, "motionSensor").SequenceEqual(["entity_id:binary_sensor"]), "the custom node's pin: " + signature.ToJsonString());
        Check(signature["types"]!.AsObject().Count == 0, "the custom node lists a handle class as a type: " + signature["types"]!.ToJsonString());
        var again = new VisualGenerator(provider).DeriveSignatures(derived, snapshot)["nodes"]![1]!["config"]!["signature"]!.AsObject();
        Check(VisualGenerator.SamePins(signature, again), "deriving again changed the pins: " + again.ToJsonString());
        Console.WriteLine("PASS entity handles: a handle type is a pin of its domain's entity id; the wire and the trace carry the id");
    }

    // Review Focus 1: an entity Home Assistant does not have when a handle is read fails the reading step, naming the
    // entity; the step takes its error pin and the run goes on. Through a node method's pin, and through Select property.
    static async Task ErrorPin()
    {
        var provider = NodeLibraryFixture.EntityProvider;
        var library = new NodeLibraryCompiler(provider).Preview([new("motion", Motion, 1)]);
        Check(library.Valid, "the library taking handles: " + string.Join("; ", library.Diagnostics.Select(d => d.Message)));
        var snapshot = library.Snapshot;
        string Kind(string name) => snapshot.Methods.Values.Single(m => m.Method.Name == name).Kind;
        var log = PyInterop.Log; var messages = new System.Collections.Concurrent.ConcurrentQueue<string>(); PyInterop.Log = (_, m) => messages.Enqueue(m);
        PyInterop.Entity = _ => null!;
        try
        {
            // The step reading the handle; on its error pin a Log of the error, then a step after both paths.
            async Task Recovers(JsonObject graph, string step, string what)
            {
                foreach (var node in new[] { N("le", "last_error"), N("err", "log", new { value = new { kind = "unset" } }), N("after", "log", new { value = Lit("string", "after") }) })
                    graph["nodes"]!.AsArray().Add(node);
                foreach (var edge in new[] { E("t", step), E(step, "after"), E(step, "err", "error"), E("err", "after"), D("le", "err", "value", "message") }) graph["edges"]!.AsArray().Add(edge);
                Check(VisualGraphValidator.Validate(graph, snapshot) is var found && !found.Any(d => d.Severity == 3), what + ": the graph does not validate: " + string.Join("; ", found.Select(d => d.Message)));
                messages.Clear();
                var trace = await Traced(provider, graph, snapshot, TriggerPayload.Manual());
                var failed = Event(trace, step);
                Check(failed["status"]!.GetValue<string>() == "error" && failed["port"]!.GetValue<string>() == "error" && failed["error"]!["message"]!.GetValue<string>().Contains("binary_sensor.hall", StringComparison.Ordinal),
                    what + ": the step did not take its error pin naming the entity: " + failed.ToJsonString());
                Check(messages.Count(m => m.Contains("Entity 'binary_sensor.hall' is unavailable.", StringComparison.Ordinal)) == 1 && messages.Last() == "after" && Event(trace, "after")["status"]!.GetValue<string>() == "ok",
                    what + ": the error path or the step after it did not run: " + string.Join(" | ", messages));
            }
            var pin = G(VisualGraphContract.CreateNode(Kind("IsOn"), "on", snapshot));
            pin["edges"] = new JsonArray();
            pin["nodes"]![1]!["config"]!["arguments"]!["sensor"] = Lit("entity_id:binary_sensor", "binary_sensor.hall");
            await Recovers(pin, "on", "a node method's pin");
            // Select property reads State from the id on its wire, as a value node of the Log that has the error pin.
            var selected = G(N("c", "constant", new { type = "entity_id:binary_sensor", value = Lit("entity_id:binary_sensor", "binary_sensor.hall") }), N("sel", "select_property", new { property = "State" }), N("use", "log", new { value = new { kind = "unset" } }));
            selected["edges"] = new JsonArray(D("c", "sel", "value"), D("sel", "use", "value"));
            await Recovers(selected, "use", "Select property");
        }
        finally { PyInterop.Log = log; }
        Console.WriteLine("PASS entity handles: a missing entity fails the step reading its handle, which takes its error pin, and the run goes on");
    }
}
