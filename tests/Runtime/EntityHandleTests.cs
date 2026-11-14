using System.Text;
using System.Text.Json;
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
}
