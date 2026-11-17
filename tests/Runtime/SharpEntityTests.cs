using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>
/// The custom entity base classes on their own, with a host that only records: state setters publish once attached and
/// never before, snapshots and descriptions have their exact JSON shapes, commands reach the overridden method or the
/// composed delegate, keys, duplicates and sensor values are checked, and a component's timers, tracks, logs, state
/// reads and service calls reach the host and Home Assistant.
/// </summary>
static class SharpEntityTests
{
    sealed class RecordingHost : IComponentHost
    {
        public readonly List<SharpEntity> Published = [];
        public readonly List<string> Calls = [];
        public CancellationToken Stopping => CancellationToken.None;
        CancellationToken IComponentHost.Calls => CancellationToken.None;
        // Set to make each publish read the snapshot, as the real host does.
        public bool Snapshots;
        public void Publish(SharpEntity entity) { if (Snapshots) entity.Snapshot("plug:" + entity.Key, true); else Published.Add(entity); }
        public IDisposable Every(TimeSpan interval, Func<Task> action, bool immediately) { Calls.Add($"every {interval.TotalSeconds} {immediately}"); return new MemoryStream(); }
        public IDisposable Track(string entityId, Func<EntityChange, Task> action) { Calls.Add("track " + entityId); return new MemoryStream(); }
        public void Log(string message, PyLogLevel level) => Calls.Add($"log {level} {message}");
    }

    // Reaches the protected services a component's own code uses.
    sealed class Poller : SharpDevice
    {
        public void Schedule() { Every(TimeSpan.FromSeconds(10), () => Task.CompletedTask); Track("binary_sensor.door", _ => Task.CompletedTask); }
        public void Write(string message) => Log(message, PyLogLevel.Warn);
        public string Read(string entityId) => State(entityId).State;
        public Task<JsonElement?> Call(string service, JsonNode? target, JsonObject? data) => CallService(service, target, data);
    }

    sealed class Relay : SharpSwitch
    {
        public int TurnedOn;
        public Relay() : base("relay") { }
        protected override Task TurnOn() { TurnedOn++; return Task.CompletedTask; }
    }

    sealed class Plug : SharpDevice
    {
        public Plug() { Add(new SharpSwitch("relay")); }
        public void AddAgain() => Add(new SharpSensor("relay"));
    }

    static T Throws<T>(Func<object?> action, string what) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        catch (Exception error) { throw new Exception($"{what}: threw {error.GetType().Name} instead of {typeof(T).Name}: {error.Message}"); }
        throw new Exception($"{what}: did not throw {typeof(T).Name}");
    }

    static async Task<T> ThrowsAsync<T>(Func<Task> action, string what) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        catch (Exception error) { throw new Exception($"{what}: threw {error.GetType().Name} instead of {typeof(T).Name}: {error.Message}"); }
        throw new Exception($"{what}: did not throw {typeof(T).Name}");
    }

    [RuntimeSuite("--sharp-entities", Order = 12)]
    public static async Task Run()
    {
        Publishing();
        Detached();
        NotRunning();
        Described();
        await Commands();
        Keys();
        Duplicates();
        SensorValues();
        Domains();
        await ConcurrentAttributes();
        await Services();
    }

    static void Publishing()
    {
        var host = new RecordingHost();
        var relay = new SharpSwitch("relay") { Host = host };
        relay.IsOn = true;
        Check(host.Published.Count == 1 && host.Published[0] == relay, $"setting IsOn publishes once, published {host.Published.Count}");
        var snapshot = relay.Snapshot("plug:relay", running: true).ToJsonString();
        Check(snapshot == """{"uniqueId":"plug:relay","available":true,"values":{"is_on":true},"attributes":{}}""", "switch snapshot: " + snapshot);
        Console.WriteLine("PASS sharp entities: a state setter on an attached switch publishes once, with the exact snapshot");
    }

    static void Detached()
    {
        var relay = new SharpSwitch("relay");
        relay.IsOn = true; relay.Available = false; relay.Attributes["x"] = 1; relay.Publish();
        var sensor = new SharpSensor("power") { Value = 3 };
        sensor.Publish();
        Console.WriteLine("PASS sharp entities: no publish and no exception before a host is attached");
    }

    static void NotRunning()
    {
        var relay = new SharpSwitch("relay");
        Check(relay.Available, "available by default");
        var snapshot = relay.Snapshot("plug:relay", running: false).ToJsonString();
        Check(snapshot.Contains("\"available\":false"), "snapshot of a definition that is not running: " + snapshot);
        Console.WriteLine("PASS sharp entities: a snapshot while not running is unavailable even when Available is true");
    }

    static void Described()
    {
        var power = new SharpSensor("power", "Power") { Unit = "W", DeviceClass = "power", StateClass = "measurement" };
        var description = power.Describe("plug:power").ToJsonString();
        const string expected = """{"uniqueId":"plug:power","key":"power","domain":"sensor","name":"Power","icon":null,"deviceClass":"power","entityCategory":null,"enabledByDefault":true,"commands":[],"options":{"unit":"W","stateClass":"measurement","options":null}}""";
        Check(description == expected, "sensor description: " + description);
        var diagnostic = new SharpBinarySensor("fault") { Category = EntityCategory.Diagnostic, EnabledByDefault = false }.Describe("plug:fault").ToJsonString();
        Check(diagnostic.Contains("\"entityCategory\":\"diagnostic\"") && diagnostic.Contains("\"enabledByDefault\":false") && diagnostic.Contains("\"options\":{}"), "binary sensor description: " + diagnostic);
        var commands = new SharpSwitch("relay").Describe("plug:relay").ToJsonString();
        Check(commands.Contains("\"commands\":[\"turn_on\",\"turn_off\"]"), "switch commands: " + commands);
        Console.WriteLine("PASS sharp entities: Describe gives the exact description, with category, commands and options");
    }

    static async Task Commands()
    {
        var relay = new Relay();
        await relay.Command("turn_on", default);
        Check(relay.TurnedOn == 1, "turn_on calls the overridden TurnOn");
        var turnedOff = 0;
        var composed = new SharpSwitch("relay") { OnTurnOn = () => Task.CompletedTask, OnTurnOff = () => { turnedOff++; return Task.CompletedTask; } };
        await composed.Command("turn_off", default);
        var turnedOn = 0;
        composed = new SharpSwitch("relay") { OnTurnOn = () => { turnedOn++; return Task.CompletedTask; } };
        await composed.Command("turn_on", default);
        Check(turnedOn == 1 && turnedOff == 1, "turn_on and turn_off call the composed delegates");
        await ThrowsAsync<NotSupportedException>(() => composed.Command("toggle", default), "toggle");
        await ThrowsAsync<NotSupportedException>(() => new SharpSwitch("relay").Command("turn_on", default), "a switch with neither method nor delegate");
        await ThrowsAsync<NotSupportedException>(() => new SharpSensor("power").Command("turn_on", default), "a sensor has no commands");
        Console.WriteLine("PASS sharp entities: commands reach the overridden method or the delegate; others are not supported");
    }

    static void Keys()
    {
        Check(new SharpSensor().Key == "main", "default key");
        Check(new SharpSwitch("a_1", "A").Name == "A", "name from the constructor");
        Check(new SharpSensor(new string('a', 40)).Key.Length == 40, "a 40-character key");
        foreach (var bad in new[] { "Relay", "1x", "", "_x", "relay\n", "relay-1", new string('a', 41) })
            Throws<ArgumentException>(() => new SharpSensor(bad), "key " + JsonSerializer.Serialize(bad));
        Console.WriteLine("PASS sharp entities: the default key is main and a key must be lower case, starting with a letter");
    }

    static void Duplicates()
    {
        var plug = new Plug();
        var error = Throws<ArgumentException>(() => { plug.AddAgain(); return null; }, "duplicate key");
        Check(error.Message.Contains("relay"), "the error names the key: " + error.Message);
        Check(plug.Entities.Count == 1, "the duplicate is not added");
        Console.WriteLine("PASS sharp entities: a device rejects a second entity with the same key, naming it");
    }

    static void SensorValues()
    {
        var host = new RecordingHost();
        var sensor = new SharpSensor("power") { Host = host };
        string Published(object? value)
        {
            sensor.Value = value;
            return host.Published[^1].Snapshot("plug:power", true)["values"]!.ToJsonString();
        }
        Check(Published(double.NaN) == """{"value":null}""", "NaN");
        Check(Published(double.PositiveInfinity) == """{"value":null}""", "infinity");
        Check(Published(float.NegativeInfinity) == """{"value":null}""", "float infinity");
        var number = Published(41.2);
        Check(number == """{"value":41.2}""", "a number stays a number: " + number);
        Check(Published(7) == """{"value":7}""", "an int stays a number");
        var date = Published(new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc));
        Check(date == """{"value":"2026-11-17T00:00:00.0000000Z"}""", "a date is ISO 8601: " + date);
        Published(new DateTimeOffset(2026, 11, 17, 1, 0, 0, TimeSpan.FromHours(1)));
        var offset = sensor.Snapshot("plug:power", true)["values"]!["value"]!.GetValue<string>();
        Check(offset == "2026-11-17T01:00:00.0000000+01:00", "an offset date is ISO 8601: " + offset);
        Check(Published(true) == """{"value":true}""", "a bool stays a bool");
        Check(Published("idle") == """{"value":"idle"}""", "text stays text");
        Check(Published(null) == """{"value":null}""", "null");
        Check(Published(new Version(1, 2)) == """{"value":"1.2"}""", "any other object is its text");
        Check(host.Published.Count == 11, $"each set publishes once, published {host.Published.Count}");
        Console.WriteLine("PASS sharp entities: sensor values keep numbers, write dates as ISO 8601 and drop NaN and infinities");
    }

    class Derived : SharpSwitch { }
    class Twice : Derived { }

    static void Domains()
    {
        Check(SharpDomains.Of(typeof(Derived)) == "switch" && SharpDomains.Of(typeof(Twice)) == "switch", "a switch subclass");
        Check(SharpDomains.Of(typeof(SharpSensor)) == "sensor" && SharpDomains.Of(typeof(SharpBinarySensor)) == "binary_sensor", "typed entities");
        Check(SharpDomains.Of(typeof(SharpDevice)) == null && SharpDomains.Of(typeof(Plug)) == null && SharpDomains.Of(typeof(string)) == null, "not an entity");
        Console.WriteLine("PASS sharp entities: SharpDomains finds the domain of the nearest typed base");
    }

    static async Task Services()
    {
        var detached = new Poller();
        Throws<InvalidOperationException>(() => { detached.Schedule(); return null; }, "a timer before the component is attached");
        var host = new RecordingHost();
        var poller = new Poller { Host = host };
        poller.Schedule(); poller.Write("hello");
        Check(host.Calls.SequenceEqual(["every 10 True", "track binary_sensor.door", "log Warn hello"]), "timers, tracks and logs go to the host: " + string.Join(", ", host.Calls));
        Check(poller.Read("sensor.outdoor") == "fresh", "State reads the entity from Home Assistant");

        var calls = new List<string>(); var previous = PyInterop.CallService;
        PyInterop.CallService = (domain, service, json, done, _) => { calls.Add($"{domain}.{service} {json}"); done?.Invoke(); };
        try
        {
            Check(await poller.Call("light.turn_on", "light.kitchen", new() { ["brightness"] = 40 }) == null, "no response unless asked");
            await poller.Call("light.turn_off", new JsonObject { ["area_id"] = "kitchen" }, null);
            Check(calls.SequenceEqual(["light.turn_on {\"brightness\":40,\"entity_id\":\"light.kitchen\"}", "light.turn_off {\"area_id\":\"kitchen\"}"]), "service calls: " + string.Join(", ", calls));
            await ThrowsAsync<InvalidOperationException>(() => poller.Call("light.turn_on", new JsonObject { ["brightness"] = 1 }, null), "a target key that is not one");
            await ThrowsAsync<InvalidOperationException>(() => poller.Call("turn_on", null, null), "a service without a domain");
        }
        finally { PyInterop.CallService = previous; }

        var relay = new SharpSwitch("relay");
        relay.Attributes["count"] = 2; relay.Attributes["tags"] = new List<string> { "a", "b" }; relay.Attributes["when"] = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);
        relay.Attributes["more"] = new Dictionary<string, object?> { ["ratio"] = double.NaN, ["node"] = new JsonObject { ["x"] = 1 } };
        var attributes = relay.Snapshot("plug:relay", true)["attributes"]!.ToJsonString();
        Check(attributes == """{"count":2,"more":{"ratio":null,"node":{"x":1}},"tags":["a","b"],"when":"2026-11-17T00:00:00.0000000Z"}""", "attributes: " + attributes);
        Console.WriteLine("PASS sharp entities: timers, tracks, logs, state reads, service calls and attributes of a component");
    }

    // A definition's timers, entity changes and commands run on different threads: one may set an attribute while
    // another publishes, and reading the attributes for the snapshot must not fail.
    static async Task ConcurrentAttributes()
    {
        var relay = new SharpSwitch("relay") { Host = new RecordingHost { Snapshots = true } };
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var writer = Task.Run(() =>
        {
            for (var i = 0; !stop.IsCancellationRequested; i++)
            {
                relay.Attributes["key" + i % 64] = i;
                if (i % 3 == 0) relay.Attributes.Remove("key" + (i + 17) % 64);
            }
        });
        var publishes = 0;
        var publisher = Task.Run(() => { while (!stop.IsCancellationRequested) { relay.Publish(); publishes++; } });
        await Task.WhenAll(writer, publisher);
        Check(publishes > 0, "the publisher ran");
        Console.WriteLine("PASS sharp entities: publishing while another thread changes the attributes does not fail");
    }
}
