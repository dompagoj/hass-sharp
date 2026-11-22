using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>
/// The custom entity base classes on their own, with a host that only records: state setters publish once attached and
/// never before, snapshots and descriptions have their exact JSON shapes, commands reach the overridden method or the
/// composed delegate, keys, duplicates and sensor values are checked, and a component's timers, tracks, logs, state
/// reads and service calls reach the host and Home Assistant. The button, number, select, text, light and cover: their
/// descriptions and snapshots, their commands with the data Home Assistant sends, each value they refuse by message
/// with their state unchanged, and their rows of <see cref="SharpDomains.Graph"/>.
/// </summary>
static class SharpEntityTests
{
    sealed class RecordingHost : IComponentHost
    {
        public readonly List<SharpEntity> Published = [];
        public readonly List<string> Calls = [];
        public string DefinitionId => "plug";
        public CancellationToken Stopping => CancellationToken.None;
        CancellationToken IComponentHost.Calls => CancellationToken.None;
        // Set to make each publish read the snapshot, as the real host does.
        public bool Snapshots;
        public void Publish(SharpEntity entity) { if (Snapshots) entity.Snapshot("plug:" + entity.Key, true); else Published.Add(entity); }
        public IDisposable Every(TimeSpan interval, Func<Task> action, bool immediately) { Calls.Add($"every {interval.TotalSeconds} {immediately}"); return new MemoryStream(); }
        public IDisposable Track(string entityId, Func<EntityChange, Task> action) { Calls.Add("track " + entityId); return new MemoryStream(); }
        public void Log(string message, PyLogLevel level) => Calls.Add($"log {level} {message}");
        // Made on first use, as the real host's; never disposed, as no definition stops here.
        HttpClient? http;
        public HttpClient Http => http ??= SharpHttp.Create();
        public SharpFiles Files => throw new NotSupportedException("this host downloads nothing");
    }

    // Reaches the protected services a component's own code uses.
    sealed class Poller : SharpDevice
    {
        public void Schedule() { Every(TimeSpan.FromSeconds(10), () => Task.CompletedTask); Track("binary_sensor.door", _ => Task.CompletedTask); }
        public IDisposable Follow(HassSharp.Entities.Entity? entity) => Track(entity!, _ => Task.CompletedTask);
        public void Write(string message) => Log(message, PyLogLevel.Warn);
        public string Read(string entityId) => State(entityId).State;
        public Task<JsonElement?> Call(string service, JsonNode? target, JsonObject? data) => CallService(service, target, data);
        public Task<JsonElement?> Typed(string service, JsonNode? target, JsonObject? data, bool response = false) => Services.CallService(service, target, data, response);
    }

    // Calls a service through Services in its constructor, before any host is attached.
    sealed class Eager : SharpSensor
    {
        public readonly Task<JsonElement?> Pending;
        public Eager() : base("eager") { Pending = Services.CallService("light.turn_on", "light.porch"); }
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
        ControlsDescribed();
        await Buttons();
        await Numbers();
        await Selects();
        await Texts();
        await Lights();
        await Covers();
        GraphRows();
        await Limits();
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
        // A day alone, as a date sensor takes it, whatever the culture (its own text is the culture's short date).
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("en-US");
        try
        {
            var day = Published(new DateOnly(2026, 11, 17));
            Check(day == """{"value":"2026-11-17"}""", "a day is ISO 8601: " + day);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
        Check(Published(true) == """{"value":true}""", "a bool stays a bool");
        Check(Published("idle") == """{"value":"idle"}""", "text stays text");
        Check(Published(null) == """{"value":null}""", "null");
        Check(Published(new Version(1, 2)) == """{"value":"1.2"}""", "any other object is its text");
        Check(host.Published.Count == 12, $"each set publishes once, published {host.Published.Count}");
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
        // A handle is tracked by its id; no handle is refused, naming the parameter.
        poller.Follow(new HassSharp.Entities.Entity("light.kitchen"));
        Check(host.Calls.Last() == "track light.kitchen", "a handle's track goes to the host by its id: " + string.Join(", ", host.Calls));
        var none = Throws<ArgumentNullException>(() => poller.Follow(null), "a track of no handle");
        Check(none.ParamName == "entity" && host.Calls.Count == 4, $"no handle is refused before the host is asked: {none.ParamName} " + string.Join(", ", host.Calls));
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

            // Services, which the typed methods call through, is the component's own CallService: the same payload, the
            // same refusals, a response when asked for, and before a host is attached (as in a constructor) it calls too.
            calls.Clear();
            Check(await poller.Typed("light.turn_on", "light.kitchen", new() { ["brightness"] = 40 }) == null, "no response through Services unless asked");
            await poller.Typed("light.turn_off", new JsonObject { ["area_id"] = "kitchen" }, null);
            Check(calls.SequenceEqual(["light.turn_on {\"brightness\":40,\"entity_id\":\"light.kitchen\"}", "light.turn_off {\"area_id\":\"kitchen\"}"]), "service calls through Services: " + string.Join(", ", calls));
            var bad = await ThrowsAsync<InvalidOperationException>(() => poller.Typed("light.turn_on", new JsonObject { ["brightness"] = 1 }, null), "a target key that is not one, through Services");
            var direct = await ThrowsAsync<InvalidOperationException>(() => poller.Call("light.turn_on", new JsonObject { ["brightness"] = 1 }, null), "a target key that is not one");
            Check(bad.Message == direct.Message, $"Services refuses as CallService does: {bad.Message} / {direct.Message}");
            await ThrowsAsync<InvalidOperationException>(() => poller.Typed("turn_on", null, null), "a service without a domain, through Services");
            var respond = PyInterop.CallServiceWithResponse;
            PyInterop.CallServiceWithResponse = (domain, service, json, done, _) => { calls.Add($"{domain}.{service} {json} response"); done("""{"todo.shopping":{"items":[]}}"""); };
            try
            {
                var answer = await poller.Typed("todo.get_items", "todo.shopping", null, response: true);
                Check(answer?.GetProperty("todo.shopping").GetProperty("items").GetArrayLength() == 0 && calls.Last() == "todo.get_items {\"entity_id\":\"todo.shopping\"} response", "a response through Services: " + string.Join(", ", calls));
            }
            finally { PyInterop.CallServiceWithResponse = respond; }
            var eager = new Eager();
            Check(await eager.Pending.WaitAsync(TimeSpan.FromSeconds(5)) == null && calls.Last() == "light.turn_on {\"entity_id\":\"light.porch\"}", "a call through Services in a constructor: " + string.Join(", ", calls));
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

    // ---- Button, number, select, text, light and cover ----

    static JsonElement Data(string json) => JsonDocument.Parse(json).RootElement.Clone();
    static string Options(SharpEntity entity) => entity.Describe("plug:" + entity.Key)["options"]!.ToJsonString();
    static string Commanded(SharpEntity entity) => entity.Describe("plug:" + entity.Key)["commands"]!.ToJsonString();
    static string Values(SharpEntity entity) => entity.Snapshot("plug:" + entity.Key, true)["values"]!.ToJsonString();

    // A command refused with exactly this message, before the method or delegate ran.
    static async Task Refuses(SharpEntity entity, string command, string data, string message)
    {
        var error = await ThrowsAsync<ArgumentException>(() => entity.Command(command, Data(data)), $"{entity.Key} {command} {data}");
        Check(error.Message == message, $"{entity.Key} {command} {data}: the message is \"{error.Message}\", not \"{message}\"");
    }

    // A state the class sets refused with exactly this message, nothing published and the state as it was.
    static void SetRefused(SharpEntity entity, RecordingHost host, Action set, string message)
    {
        var (before, published) = (Values(entity), host.Published.Count);
        var error = Throws<ArgumentException>(() => { set(); return null; }, $"{entity.Key}: a state it refuses");
        Check(error.Message == message, $"{entity.Key}: the message is \"{error.Message}\", not \"{message}\"");
        Check(Values(entity) == before && host.Published.Count == published, $"{entity.Key}: a refused state changes and publishes nothing: {Values(entity)}");
    }

    static void ControlsDescribed()
    {
        var expected = new (SharpEntity Entity, string Domain, string Commands, string Options, string Values)[]
        {
            (new SharpButton("bell"), "button", """["press"]""", "{}", "{}"),
            (new SharpNumber("target"), "number", """["set_value"]""", """{"min":0,"max":100,"step":1,"mode":"auto","unit":null}""", """{"value":null}"""),
            (new SharpNumber("target") { Min = 5, Max = 30, Step = 0.5, Mode = SharpNumberMode.Box, Unit = "C", Value = 21.5 }, "number", """["set_value"]""",
                """{"min":5,"max":30,"step":0.5,"mode":"box","unit":"C"}""", """{"value":21.5}"""),
            (new SharpNumber("target") { Mode = SharpNumberMode.Slider }, "number", """["set_value"]""", """{"min":0,"max":100,"step":1,"mode":"slider","unit":null}""", """{"value":null}"""),
            (new SharpSelect("mode"), "select", """["select_option"]""", """{"options":[]}""", """{"option":null}"""),
            (new SharpSelect("mode") { Options = ["low", "high"], Option = "high" }, "select", """["select_option"]""", """{"options":["low","high"]}""", """{"option":"high"}"""),
            (new SharpText("code"), "text", """["set_value"]""", """{"min":0,"max":100,"pattern":null,"mode":"text"}""", """{"value":null}"""),
            (new SharpText("code") { Min = 2, Max = 4, Pattern = "^[0-9]*$", Mode = SharpTextMode.Password, Value = "123" }, "text", """["set_value"]""",
                """{"min":2,"max":4,"pattern":"^[0-9]*$","mode":"password"}""", """{"value":"123"}"""),
            (new SharpLight("lamp"), "light", """["turn_on","turn_off"]""", """{"dimmable":false}""", """{"is_on":null,"brightness":null}"""),
            (new SharpLight("lamp") { Dimmable = true, IsOn = true, Brightness = 128 }, "light", """["turn_on","turn_off"]""", """{"dimmable":true}""", """{"is_on":true,"brightness":128}"""),
            (new SharpCover("blind"), "cover", """["open","close","stop","set_position"]""", """{"positionable":false}""", """{"is_closed":null,"position":null}"""),
            (new SharpCover("blind") { Positionable = true, IsClosed = false, Position = 40 }, "cover", """["open","close","stop","set_position"]""", """{"positionable":true}""", """{"is_closed":false,"position":40}"""),
        };
        foreach (var (entity, domain, commands, options, values) in expected)
        {
            Check(entity.Describe("plug:" + entity.Key)["domain"]!.GetValue<string>() == domain && SharpDomains.Of(entity.GetType()) == domain, $"{entity.GetType().Name} is a {domain}");
            Check(Commanded(entity) == commands, $"{domain} commands: {Commanded(entity)}");
            Check(Options(entity) == options, $"{domain} options: {Options(entity)}");
            Check(Values(entity) == values, $"{domain} values: {Values(entity)}");
        }
        var host = new RecordingHost();
        var lamp = new SharpLight("lamp") { Dimmable = true, Host = host };
        lamp.IsOn = true; lamp.Brightness = 10; lamp.Brightness = null;
        Check(host.Published.Count == 3 && Values(lamp) == """{"is_on":true,"brightness":null}""", $"each state setter publishes: {host.Published.Count} {Values(lamp)}");
        var blind = new SharpCover("blind") { Positionable = true, Host = host };
        blind.IsClosed = true; blind.Position = 0;
        var number = new SharpNumber("target") { Host = host }; number.Value = 3;
        var select = new SharpSelect("mode") { Options = ["low"], Host = host }; select.Option = "low";
        var text = new SharpText("code") { Host = host }; text.Value = "x";
        Check(host.Published.Count == 8, $"each state setter of a cover, number, select and text publishes once: {host.Published.Count}");
        Console.WriteLine("PASS sharp entities: a button, number, select, text, light and cover describe their domain, commands and options exactly, and their state setters publish");
    }

    sealed class Bell : SharpButton
    {
        public int Pressed;
        public Bell() : base("bell") { }
        protected override Task Press() { Pressed++; return Task.CompletedTask; }
    }

    static async Task Buttons()
    {
        var bell = new Bell();
        await bell.Command("press", default);
        await bell.Command("press", Data("""{"ignored":1}"""));
        Check(bell.Pressed == 2, "press calls the overridden Press, whatever the data");
        var pressed = 0;
        await new SharpButton("bell") { OnPress = () => { pressed++; return Task.CompletedTask; } }.Command("press", default);
        Check(pressed == 1, "press calls the delegate");
        await ThrowsAsync<NotSupportedException>(() => new SharpButton("bell").Command("press", default), "a button with neither method nor delegate");
        await ThrowsAsync<NotSupportedException>(() => bell.Command("turn_on", default), "a button has no other command");
        Console.WriteLine("PASS sharp entities: a button's press reaches the overridden method or the delegate");
    }

    sealed class Thermostat : SharpNumber
    {
        public readonly List<double> Set = [];
        public Thermostat() : base("target", "Target temperature") { Min = 5; Max = 30; Step = 0.5; }
        protected override Task SetValue(double value) { Set.Add(value); Value = value; return Task.CompletedTask; }
    }

    static async Task Numbers()
    {
        var host = new RecordingHost();
        var target = new Thermostat { Host = host };
        await target.Command("set_value", Data("""{"value":21.5}"""));
        await target.Command("set_value", Data("""{"value":5}"""));
        await target.Command("set_value", Data("""{"value":30.0,"other":"x"}"""));
        Check(target.Set.SequenceEqual([21.5, 5, 30]) && target.Value == 30, "set_value calls the overridden SetValue with the value: " + string.Join(", ", target.Set));
        await Refuses(target, "set_value", """{"value":45}""", "Target temperature (target) takes a number from 5 to 30, not 45");
        await Refuses(target, "set_value", """{"value":4.9}""", "Target temperature (target) takes a number from 5 to 30, not 4.9");
        await Refuses(target, "set_value", """{"value":"20"}""", "Target temperature (target) takes a number from 5 to 30, not \"20\"");
        await Refuses(target, "set_value", """{}""", "Target temperature (target) needs a value: it takes a number from 5 to 30");
        await Refuses(target, "set_value", """{"value":null}""", "Target temperature (target) needs a value: it takes a number from 5 to 30");
        await Refuses(target, "set_value", "[]", "Target temperature (target) needs a value: it takes a number from 5 to 30");
        Check(target.Set.Count == 3 && target.Value == 30, "a refused value never reaches SetValue, and the state is unchanged");
        SetRefused(target, host, () => target.Value = 31, "Target temperature (target) takes a number from 5 to 30, not 31");
        SetRefused(target, host, () => target.Value = double.NaN, "Target temperature (target) takes a number from 5 to 30, not NaN");
        target.Value = null;
        Check(target.Value == null, "a number may be unknown");
        var set = new List<double>();
        var plain = new SharpNumber("level") { OnSetValue = value => { set.Add(value); return Task.CompletedTask; } };
        await plain.Command("set_value", Data("""{"value":100}"""));
        await Refuses(plain, "set_value", """{"value":-1}""", "level takes a number from 0 to 100, not -1");
        Check(set.SequenceEqual([100.0]), "set_value calls the delegate with the value");
        await ThrowsAsync<NotSupportedException>(() => new SharpNumber("level").Command("set_value", Data("""{"value":1}""")), "a number with neither method nor delegate");
        await ThrowsAsync<NotSupportedException>(() => plain.Command("turn_on", default), "a number has no other command");
        Console.WriteLine("PASS sharp entities: a number's set_value reaches the method or delegate with a value in its range; others are refused by message and change nothing");
    }

    sealed class Fan : SharpSelect
    {
        public readonly List<string> Chosen = [];
        public Fan() : base("mode", "Mode") { Options = ["low", "high"]; }
        protected override Task SelectOption(string option) { Chosen.Add(option); Option = option; return Task.CompletedTask; }
    }

    static async Task Selects()
    {
        var host = new RecordingHost();
        var fan = new Fan { Host = host };
        await fan.Command("select_option", Data("""{"option":"high"}"""));
        Check(fan.Chosen.SequenceEqual(["high"]) && fan.Option == "high", "select_option calls the overridden SelectOption with the option");
        await Refuses(fan, "select_option", """{"option":"medium"}""", "Mode (mode) takes one of low or high, not \"medium\"");
        await Refuses(fan, "select_option", """{"option":"High"}""", "Mode (mode) takes one of low or high, not \"High\"");
        await Refuses(fan, "select_option", """{"option":1}""", "Mode (mode) takes one of low or high, not 1");
        await Refuses(fan, "select_option", """{}""", "Mode (mode) needs an option: it takes one of low or high");
        Check(fan.Chosen.Count == 1 && fan.Option == "high", "a refused option never reaches SelectOption, and the state is unchanged");
        SetRefused(fan, host, () => fan.Option = "medium", "Mode (mode) takes one of low or high, not \"medium\"");
        var chosen = new List<string>();
        var plain = new SharpSelect("mode") { Options = ["a"], OnSelectOption = option => { chosen.Add(option); return Task.CompletedTask; } };
        await plain.Command("select_option", Data("""{"option":"a"}"""));
        Check(chosen.SequenceEqual(["a"]), "select_option calls the delegate with the option");
        await Refuses(new SharpSelect("empty"), "select_option", """{"option":"a"}""", "empty takes one of its options, and it has none, not \"a\"");
        await ThrowsAsync<NotSupportedException>(() => new SharpSelect("mode") { Options = ["a"] }.Command("select_option", Data("""{"option":"a"}""")), "a select with neither method nor delegate");
        Check(new SharpSelect("mode") { Options = null! }.Options.Count == 0, "options set to null are none");
        Console.WriteLine("PASS sharp entities: a select's select_option reaches the method or delegate with one of its options; others are refused by message and change nothing");
    }

    sealed class Keypad : SharpText
    {
        public readonly List<string> Set = [];
        public Keypad(SharpTextMode mode = SharpTextMode.Text) : base("code", "Code") { Min = 2; Max = 4; Pattern = "[0-9]+"; Mode = mode; }
        protected override Task SetValue(string value) { Set.Add(value); Value = value; return Task.CompletedTask; }
    }

    static async Task Texts()
    {
        var host = new RecordingHost();
        var code = new Keypad { Host = host };
        await code.Command("set_value", Data("""{"value":"123"}"""));
        // Matched from the start, as Home Assistant matches it.
        await code.Command("set_value", Data("""{"value":"12ab"}"""));
        Check(code.Set.SequenceEqual(["123", "12ab"]) && code.Value == "12ab", "set_value calls the overridden SetValue with the text: " + string.Join(", ", code.Set));
        await Refuses(code, "set_value", """{"value":"12345"}""", "Code (code) takes a text of 2 to 4 characters, not one of 5");
        await Refuses(code, "set_value", """{"value":"1"}""", "Code (code) takes a text of 2 to 4 characters, not one of 1");
        await Refuses(code, "set_value", """{"value":"ab12"}""", "Code (code) takes a text that matches [0-9]+, not \"ab12\"");
        await Refuses(code, "set_value", """{"value":12}""", "Code (code) takes a text of 2 to 4 characters, not 12");
        await Refuses(code, "set_value", """{}""", "Code (code) needs a value: it takes a text of 2 to 4 characters");
        Check(code.Set.Count == 2 && code.Value == "12ab", "a refused text never reaches SetValue, and the state is unchanged");
        SetRefused(code, host, () => code.Value = "x1", "Code (code) takes a text that matches [0-9]+, not \"x1\"");
        SetRefused(code, host, () => code.Value = "", "Code (code) takes a text of 2 to 4 characters, not one of 0");
        // Characters as Home Assistant counts them: three emoji are three characters, not six UTF-16 units.
        var emoji = new SharpText("emoji") { Max = 3, Value = "\U0001F44D\U0001F44D\U0001F44D" };
        Check(emoji.Value!.Length == 6, "three emoji fit a text of at most 3 characters");
        var password = new Keypad(SharpTextMode.Password);
        await Refuses(password, "set_value", """{"value":"abcd"}""", "Code (code) takes a text that matches [0-9]+, not the hidden text given");
        await Refuses(password, "set_value", """{"value":"12345"}""", "Code (code) takes a text of 2 to 4 characters, not one of 5");
        var set = new List<string>();
        var plain = new SharpText("note") { OnSetValue = value => { set.Add(value); return Task.CompletedTask; } };
        await plain.Command("set_value", Data("""{"value":""}"""));
        Check(set.SequenceEqual([""]), "set_value calls the delegate with the text");
        Throws<ArgumentException>(() => new SharpText("bad") { Pattern = "([" }, "a pattern that is not a regular expression");
        await ThrowsAsync<NotSupportedException>(() => new SharpText("note").Command("set_value", Data("""{"value":"a"}""")), "a text with neither method nor delegate");
        Console.WriteLine("PASS sharp entities: a text's set_value reaches the method or delegate with a text that fits; others are refused by message, a password's text unquoted, and change nothing");
    }

    sealed class Lamp : SharpLight
    {
        public readonly List<int?> On = [];
        public int Off;
        public Lamp(bool dimmable) : base("lamp", "Lamp") { Dimmable = dimmable; }
        protected override Task TurnOn(int? brightness) { On.Add(brightness); IsOn = true; if (brightness != null) Brightness = brightness; return Task.CompletedTask; }
        protected override Task TurnOff() { Off++; IsOn = false; return Task.CompletedTask; }
    }

    static async Task Lights()
    {
        var host = new RecordingHost();
        var lamp = new Lamp(dimmable: true) { Host = host };
        await lamp.Command("turn_on", Data("""{"brightness":128}"""));
        await lamp.Command("turn_on", Data("""{"brightness":255.0,"transition":2}"""));
        await lamp.Command("turn_on", Data("""{"brightness":0}"""));
        await lamp.Command("turn_on", Data("""{}"""));
        await lamp.Command("turn_on", default);
        await lamp.Command("turn_off", default);
        Check(lamp.On.SequenceEqual([128, 255, 0, null, null]) && lamp.Off == 1 && lamp.Brightness == 0 && lamp.IsOn == false,
            "turn_on calls the overridden TurnOn with the brightness or null, turn_off TurnOff: " + string.Join(", ", lamp.On));
        await Refuses(lamp, "turn_on", """{"brightness":256}""", "Lamp (lamp) takes a brightness from 0 to 255, in whole numbers, not 256");
        await Refuses(lamp, "turn_on", """{"brightness":-1}""", "Lamp (lamp) takes a brightness from 0 to 255, in whole numbers, not -1");
        await Refuses(lamp, "turn_on", """{"brightness":127.5}""", "Lamp (lamp) takes a brightness from 0 to 255, in whole numbers, not 127.5");
        await Refuses(lamp, "turn_on", """{"brightness":"high"}""", "Lamp (lamp) takes a brightness from 0 to 255, in whole numbers, not \"high\"");
        Check(lamp.On.Count == 5 && lamp.Brightness == 0 && lamp.IsOn == false, "a refused brightness never reaches TurnOn, and the state is unchanged");
        SetRefused(lamp, host, () => lamp.Brightness = 300, "Lamp (lamp) takes a brightness from 0 to 255, in whole numbers, not 300");
        var plain = new Lamp(dimmable: false) { Host = host };
        await plain.Command("turn_on", Data("""{"brightness":null}"""));
        Check(plain.On.SequenceEqual([(int?)null]), "a light that cannot be dimmed is turned on without a brightness");
        await Refuses(plain, "turn_on", """{"brightness":10}""", "Lamp (lamp) cannot be dimmed, so it takes no brightness, not 10");
        SetRefused(plain, host, () => plain.Brightness = 10, "Lamp (lamp) cannot be dimmed, so it takes no brightness, not 10");
        int? given = -1; var off = 0;
        var composed = new SharpLight("lamp") { Dimmable = true, OnTurnOn = brightness => { given = brightness; return Task.CompletedTask; }, OnTurnOff = () => { off++; return Task.CompletedTask; } };
        await composed.Command("turn_on", Data("""{"brightness":40}"""));
        await composed.Command("turn_off", default);
        Check(given == 40 && off == 1, "turn_on and turn_off call the delegates");
        await ThrowsAsync<NotSupportedException>(() => new SharpLight("lamp").Command("turn_on", default), "a light with neither method nor delegate");
        await ThrowsAsync<NotSupportedException>(() => new SharpLight("lamp").Command("turn_off", default), "a light with neither method nor delegate");
        Console.WriteLine("PASS sharp entities: a light's turn_on reaches the method or delegate with a brightness from 0 to 255 or none; a brightness out of range or for a light that cannot dim is refused by message and changes nothing");
    }

    sealed class Blind : SharpCover
    {
        public readonly List<string> Done = [];
        public Blind(bool positionable) : base("blind", "Blind") { Positionable = positionable; }
        protected override Task Open() { Done.Add("open"); return Task.CompletedTask; }
        protected override Task Close() { Done.Add("close"); return Task.CompletedTask; }
        protected override Task StopMoving() { Done.Add("stop"); return Task.CompletedTask; }
        protected override Task SetPosition(int position) { Done.Add("at " + position); Position = position; return Task.CompletedTask; }
    }

    static async Task Covers()
    {
        var host = new RecordingHost();
        var blind = new Blind(positionable: true) { Host = host };
        foreach (var (command, data) in new[] { ("open", "{}"), ("close", "{}"), ("stop", "{}"), ("set_position", """{"position":40}"""), ("set_position", """{"position":100.0}"""), ("set_position", """{"position":0}""") })
            await blind.Command(command, Data(data));
        Check(blind.Done.SequenceEqual(["open", "close", "stop", "at 40", "at 100", "at 0"]) && blind.Position == 0, "each command calls its overridden method: " + string.Join(", ", blind.Done));
        await Refuses(blind, "set_position", """{"position":101}""", "Blind (blind) takes a position from 0 (closed) to 100 (open), in whole numbers, not 101");
        await Refuses(blind, "set_position", """{"position":40.5}""", "Blind (blind) takes a position from 0 (closed) to 100 (open), in whole numbers, not 40.5");
        await Refuses(blind, "set_position", """{}""", "Blind (blind) needs a position: it takes a position from 0 (closed) to 100 (open), in whole numbers");
        Check(blind.Done.Count == 6 && blind.Position == 0, "a refused position never reaches SetPosition, and the state is unchanged");
        SetRefused(blind, host, () => blind.Position = -1, "Blind (blind) takes a position from 0 (closed) to 100 (open), in whole numbers, not -1");
        var fixedBlind = new Blind(positionable: false) { Host = host };
        await fixedBlind.Command("open", default);
        await Refuses(fixedBlind, "set_position", """{"position":40}""", "Blind (blind) has no position, so it takes none, not 40");
        await Refuses(fixedBlind, "set_position", """{}""", "Blind (blind) has no position, so it cannot be moved to one");
        SetRefused(fixedBlind, host, () => fixedBlind.Position = 40, "Blind (blind) has no position, so it takes none, not 40");
        Check(fixedBlind.Done.SequenceEqual(["open"]), "a cover without a position is never asked to move to one");
        var done = new List<string>();
        Task Did(string what) { done.Add(what); return Task.CompletedTask; }
        var composed = new SharpCover("blind") { Positionable = true, OnOpen = () => Did("open"), OnClose = () => Did("close"), OnStopMoving = () => Did("stop"), OnSetPosition = at => Did("at " + at) };
        foreach (var (command, data) in new[] { ("open", "{}"), ("close", "{}"), ("stop", "{}"), ("set_position", """{"position":70}""") })
            await composed.Command(command, Data(data));
        Check(done.SequenceEqual(["open", "close", "stop", "at 70"]), "each command calls its delegate: " + string.Join(", ", done));
        foreach (var command in new[] { "open", "close", "stop" })
            await ThrowsAsync<NotSupportedException>(() => new SharpCover("blind").Command(command, default), $"a cover with neither method nor delegate for {command}");
        await ThrowsAsync<NotSupportedException>(() => new SharpCover("blind") { Positionable = true }.Command("set_position", Data("""{"position":1}""")), "a cover with neither method nor delegate for set_position");
        Console.WriteLine("PASS sharp entities: a cover's open, close, stop and set_position reach the method or delegate; a position out of range or for a cover without one is refused by message and changes nothing");
    }

    // The rows a graph's entities are created from: each reads its options, and each state checks and keeps the values a
    // class's setters take, as plain values (a brightness and a position as a double).
    static void GraphRows()
    {
        SharpEntity Create(string domain, string options, string more = "") => SharpDomains.Create(domain, JsonNode.Parse($$"""{"key":"e","name":"E"{{more}},"options":{{options}}}""")!.AsObject());
        string? Refused(SharpEntity entity, string key, object? value) => SharpDomains.Graph[entity.Domain].State[key].Refuses(entity, value);
        void Keep(SharpEntity entity, string key, object? value) => SharpDomains.Graph[entity.Domain].State[key].Keep(entity, value);
        object? Read(SharpEntity entity, string key) => SharpDomains.Graph[entity.Domain].State[key].Read(entity);

        Check(Create("button", "{}") is SharpButton button && SharpDomains.Graph["button"].State.Count == 0 && Values(button) == "{}", "a button has no state");
        var number = Create("number", """{"min":5,"max":30,"step":0.5,"mode":"slider","unit":"C"}""");
        Check(Options(number) == """{"min":5,"max":30,"step":0.5,"mode":"slider","unit":"C"}""", "a graph's number reads its options: " + Options(number));
        Check(Options(Create("number", "{}")) == """{"min":0,"max":100,"step":1,"mode":"auto","unit":null}""", "a graph's number without options has the class's defaults");
        Check(Refused(number, "value", 31.0) == "is not a number from 5 to 30" && Refused(number, "value", "20") == "is not a number" && Refused(number, "value", null) == null && Refused(number, "value", 5.0) == null, "a graph's number takes what the class does");
        Keep(number, "value", 21.5);
        Check(Read(number, "value") is 21.5, "a graph's number keeps its value");
        var select = Create("select", """{"options":["low","high"]}""");
        Check(Options(select) == """{"options":["low","high"]}""" && Refused(select, "option", "medium") == "is not one of low or high" && Refused(select, "option", 1.0) == "is not text" && Refused(select, "option", "low") == null,
            "a graph's select reads its options and takes one of them: " + Options(select));
        Keep(select, "option", "low");
        Check(Read(select, "option") is "low", "a graph's select keeps its option");
        var text = Create("text", """{"min":2,"max":4.9,"pattern":"^[a-z]*$","mode":"password"}""");
        Check(Options(text) == """{"min":2,"max":4,"pattern":"^[a-z]*$","mode":"password"}""", "a graph's text reads its options, its lengths as whole numbers: " + Options(text));
        Check(Refused(text, "value", "abcde") == "is not a text of 2 to 4 characters" && Refused(text, "value", "ab1") == "is not a text that matches ^[a-z]*$" && Refused(text, "value", 1.0) == "is not text" && Refused(text, "value", "abc") == null,
            "a graph's text takes what the class does");
        var light = Create("light", """{"dimmable":true}""");
        Check(Options(light) == """{"dimmable":true}""" && Refused(light, "brightness", 128.0) == null && Refused(light, "brightness", 255.5) == "is not a whole number from 0 to 255"
            && Refused(light, "brightness", 256.0) == "is not a whole number from 0 to 255" && Refused(light, "brightness", "x") == "is not a number" && Refused(light, "is_on", "on") == "is neither true nor false", "a graph's light takes what the class does");
        Keep(light, "is_on", true); Keep(light, "brightness", 128.0);
        Check(((SharpLight)light).Brightness == 128 && Read(light, "brightness") is 128.0 && Read(light, "is_on") is true, "a graph's light keeps a brightness as a whole number and reads it as a double");
        Keep(light, "brightness", null);
        Check(Read(light, "brightness") == null, "a graph's light may have no brightness");
        var plain = Create("light", "{}");
        Check(Options(plain) == """{"dimmable":false}""" && Refused(plain, "brightness", 1.0) == "is given, and a light that cannot be dimmed has none" && Refused(plain, "brightness", null) == null, "a light that cannot be dimmed takes no brightness");
        var cover = Create("cover", """{"positionable":true}""");
        Check(Options(cover) == """{"positionable":true}""" && Refused(cover, "position", 40.0) == null && Refused(cover, "position", 101.0) == "is not a whole number from 0 to 100" && Refused(cover, "is_closed", 1.0) == "is neither true nor false", "a graph's cover takes what the class does");
        Keep(cover, "is_closed", false); Keep(cover, "position", 40.0);
        Check(Read(cover, "position") is 40.0 && Read(cover, "is_closed") is false, "a graph's cover keeps its state");
        Check(Refused(Create("cover", "{}"), "position", 40.0) == "is given, and a cover without a position has none", "a cover without a position takes none");
        Check(Create("cover", """{"positionable":"yes"}""", ""","icon":"mdi:blinds" """) is SharpCover { Positionable: false, Icon: "mdi:blinds", Name: "E" }, "an option of another type is read as absent, and the description fields as for any entity");
        // A sensor's device class, unless it is a date, a timestamp, an uptime or an enum, makes Home Assistant read its state as a number.
        var power = Create("sensor", "{}", ""","deviceClass":"power" """);
        Check(Refused(power, "value", "high") == "is text, and a sensor with the device class power needs a number" && Refused(power, "value", "12.5") == null && Refused(power, "value", 3.0) == null,
            "a sensor of a numeric device class takes a number or numeric text");
        foreach (var deviceClass in new[] { "date", "timestamp", "uptime", "enum" })
            Check(Refused(Create("sensor", "{}", $$""","deviceClass":"{{deviceClass}}" """), "value", "2026-11-18") == null, $"a sensor of the device class {deviceClass} takes text");
        Check(Refused(Create("sensor", """{"options":["a","b"]}""", ""","deviceClass":"power" """), "value", "a") == null, "a sensor with options takes one of them whatever its device class");
        Console.WriteLine("PASS sharp entities: each new domain's graph row reads its options and takes, keeps and reads the values its class does; a numeric device class needs a number");
    }

    // The options Home Assistant would not take, refused as the class is made; a pattern that takes too long to match,
    // refused as an ArgumentException; a password text's value never quoted. Every failure is reported.
    static async Task Limits()
    {
        var failures = new List<string>();
        void Refused(Func<object?> make, string message, string what)
        {
            try { make(); failures.Add($"{what}: not refused"); }
            catch (ArgumentException error) { if (error.Message != message) failures.Add($"{what}: the message is \"{error.Message}\", not \"{message}\""); }
            catch (Exception error) { failures.Add($"{what}: threw {error.GetType().Name}: {error.Message}"); }
        }
        async Task CommandRefused(SharpEntity entity, string data, string message, string what)
        {
            try { await entity.Command("set_value", Data(data)); failures.Add($"{what}: not refused"); }
            catch (ArgumentException error) { if (error.Message != message) failures.Add($"{what}: the message is \"{error.Message}\", not \"{message}\""); }
            catch (Exception error) { failures.Add($"{what}: threw {error.GetType().Name}: {error.Message}"); }
        }
        // A1: a step above 0.
        foreach (var step in new[] { 0, -0.5, double.NaN, double.PositiveInfinity })
            Refused(() => new SharpNumber("target", "Target") { Step = step }, $"Target (target) takes a step above 0, not {CommandData.Shown(step)}", $"a number's step {step}");
        if (new SharpNumber("target") { Step = 0.001 }.Step != 0.001) failures.Add("a small step is taken");
        // A2, A3: a text of 0 to 255 characters.
        Refused(() => new SharpText("code", "Code") { Max = 256 }, "Code (code) takes a longest length of 255 or less, as Home Assistant keeps at most 255 characters, not 256", "a text's max above 255");
        Refused(() => new SharpText("code", "Code") { Min = -1 }, "Code (code) takes a shortest length of 0 or more, not -1", "a text's min below 0");
        if (new SharpText("code") { Min = 0, Max = 255 } is not { Min: 0, Max: 255 }) failures.Add("a text of 0 to 255 characters is taken");
        // B4: a pattern that takes too long is a refusal naming the entity, its value never quoted.
        var slow = new SharpText("code", "Code") { Pattern = "^(a+)+$", Mode = SharpTextMode.Password };
        var text = new string('a', 40) + "!";
        const string tooLong = "Code (code) cannot take the text: matching it to its pattern ^(a+)+$ took longer than 1 s";
        Refused(() => slow.Value = text, tooLong, "a state whose pattern takes too long");
        await CommandRefused(slow, $$"""{"value":"{{text}}"}""", tooLong, "a command whose pattern takes too long");
        if (slow.Value != null) failures.Add("a text refused for a slow pattern is unchanged");
        // Matched from the start, as Python's re.match: alternatives and a verbose pattern's comment keep their meaning.
        var either = new SharpText("code") { Pattern = "a|b" };
        Refused(() => either.Value = "xb", "code takes a text that matches a|b, not \"xb\"", "an alternative matched later in the text");
        either.Value = "bx";
        var verbose = new SharpText("code") { Pattern = "(?x)[0-9]+ # digits" };
        verbose.Value = "12ab";
        Refused(() => verbose.Value = "ab12", "code takes a text that matches (?x)[0-9]+ # digits, not \"ab12\"", "a verbose pattern matched later in the text");
        // B5: a password text given something other than a text does not quote it.
        await CommandRefused(new Keypad(SharpTextMode.Password), """{"value":12}""", "Code (code) takes a text of 2 to 4 characters, not the hidden value given", "a password text given a number");
        // In a graph, a pattern that takes too long is a refusal too.
        var graph = SharpDomains.Create("text", JsonNode.Parse("""{"key":"code","options":{"pattern":"^(a+)+$"}}""")!.AsObject());
        try
        {
            var reason = SharpDomains.Graph["text"].State["value"].Refuses(graph, text);
            if (reason != "took longer than 1 s to match the pattern ^(a+)+$") failures.Add("a graph's text with a slow pattern: " + reason);
        }
        catch (Exception error) { failures.Add($"a graph's text with a slow pattern threw {error.GetType().Name}"); }
        if (failures.Count > 0) throw new Exception($"Limits: {failures.Count} failing:\n" + string.Join("\n", failures));
        Console.WriteLine("PASS sharp entities: a number's step above 0 and a text of 0 to 255 characters; a pattern that takes too long is a refusal, matched from the start, and a password text's value is never quoted");
    }
}
