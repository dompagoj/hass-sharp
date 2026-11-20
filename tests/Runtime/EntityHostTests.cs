using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>
/// The definition host inside the script manager, running a definition built from nodes whose one C# device or C# entity
/// node's class is of this assembly, made through <see cref="CompiledDefinition.Create"/>, with recording fakes for the
/// entity bridge: settings, setup, sync and start
/// run in order; the sync JSON is exact; start publishes every entity (unavailable when a start fails); a failed setup
/// or bad keys sync nothing; a hot replace stops the old instance before starting the new one with nothing unavailable
/// in between; commands, timers and tracks reach the instance and their failures are logged; and unloading stops the
/// host without touching run history or Home Assistant.
/// </summary>
static class EntityHostTests
{
    static readonly List<string> Calls = [], Synced = [], Pushed = [], Logged = [];
    static readonly List<(PyLogLevel Level, string Message)> Levelled = [];

    internal static void Record(string call) { lock (Calls) Calls.Add(call); }
    static string[] Recorded() { lock (Calls) return [.. Calls]; }
    static string[] Syncs() { lock (Synced) return [.. Synced]; }
    static string[] Pushes() { lock (Pushed) return [.. Pushed]; }
    static string[] Logs() { lock (Logged) return [.. Logged]; }
    static string[] Logs(PyLogLevel level) { lock (Logged) return Levelled.Where(line => line.Level == level).Select(line => line.Message).ToArray(); }

    static void Reset()
    {
        lock (Calls) Calls.Clear();
        lock (Synced) Synced.Clear();
        lock (Pushed) Pushed.Clear();
        lock (Logged) { Logged.Clear(); Levelled.Clear(); }
        HostPlug.SetupHook = HostPlug.StartHook = HostPlug.StopHook = null;
        HostTicker.Ticks = 0;
        HostPoller.Ticks = 0;
        HostPoller.Failure = null;
        HostWaiter.Gate = Task.CompletedTask;
    }

    // Python's answer to a sync: an entity id for each unique id, as "<domain>.<definition>_<key>".
    static string Answer(string json)
    {
        var definition = JsonNode.Parse(json)!;
        var entities = new JsonObject();
        foreach (var entity in definition["entities"]!.AsArray())
            entities[entity!["uniqueId"]!.GetValue<string>()] = new JsonObject
                { ["entityId"] = $"{entity["domain"]}.{definition["definitionId"]}_{entity["key"]}" };
        return new JsonObject { ["entities"] = entities }.ToJsonString();
    }

    // Every snapshot pushed so far, in order.
    static JsonNode[] Snapshots() => Pushes().SelectMany(batch => JsonNode.Parse(batch)!.AsArray()).Select(node => node!).ToArray();

    static bool PushedAvailable(string uniqueId, bool available) =>
        Snapshots().Any(s => s["uniqueId"]!.GetValue<string>() == uniqueId && s["available"]!.GetValue<bool>() == available);

    static async Task Until(Func<bool> condition, string what)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Check(clock.Elapsed < TimeSpan.FromSeconds(5), "timed out waiting: " + what);
            await Task.Delay(5);
        }
    }

    /// <summary>
    /// A definition built from nodes whose one node is <paramref name="node"/>, a C# device or C# entity, beside the Device
    /// nodes <paramref name="devices"/>: no Entity node and no flow, so nothing of the script is compiled. Its class is the
    /// test's own, which <paramref name="create"/> makes where the compiled graph's <c>SharpNodes.Create</c> would; for any
    /// other node id it makes none.
    /// </summary>
    internal static CompiledUserScript Compiled(string id, string name, DefinitionComponent node, Func<object> create, params DefinitionDevice[] devices) => new()
    {
        FilePath = $"entity_{id}.cs", FileName = $"entity_{id}", SourceCode = "", Assembly = typeof(EntityHostTests).Assembly,
        DesignatedRootType = "VisualAutomation",
        Definition = new(id, name, [])
        {
            Entities = [], Devices = devices, Components = [node],
            Create = nodeId => nodeId == node.NodeId ? create() : null,
        },
    };

    /// <summary>A C# device node of key <paramref name="key"/>, named <paramref name="name"/>, with its settings' values.</summary>
    internal static DefinitionComponent Device(string key, string? name, params DefinitionSetting[] settings) =>
        new("csharp_device_1", "device", key, name, null, null, name ?? key, settings);

    /// <summary>A C# entity node of key <paramref name="key"/> whose class's title is <paramref name="title"/>: standing alone, or wired to the Device node <paramref name="device"/>.</summary>
    internal static DefinitionComponent Entity(string key, string title, string? device = null) =>
        new("csharp_entity_1", "entity", key, null, null, device, title, []);

    // The C# device node most tests run their class in: key plug, named Plug.
    static DefinitionComponent Plug(params DefinitionSetting[] settings) => Device("plug", "Plug", settings);

    static UserScriptManager Manager(RunHistoryStore? history = null) =>
        new() { History = history, Pump = new EntityStatePump(TimeSpan.FromMilliseconds(5)) };

    // Loads the definition and returns its host; the failure is thrown.
    static async Task<DefinitionHost> Load(UserScriptManager manager, CompiledUserScript compiled)
    {
        Exception? failure = null;
        await manager.LoadUserScripts([compiled], (_, error) => failure = error);
        if (failure != null) throw failure;
        var script = manager.GetUserScripts().Single(s => s.CompiledScript.FilePath == compiled.FilePath);
        Check(script.Classes.Length == 0, "a definition script has no automation classes");
        return script.Host ?? throw new Exception("the definition script has no host");
    }

    static async Task<Exception> LoadFails(UserScriptManager manager, CompiledUserScript compiled)
    {
        Exception? failure = null;
        await manager.LoadUserScripts([compiled], (_, error) => failure = error);
        Check(manager.GetUserScripts().Count == 0, "a failed definition stays unloaded");
        return failure ?? throw new Exception("the definition loaded");
    }

    static async Task<T> Throws<T>(Func<Task> action, string what) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new Exception($"{what}: expected {typeof(T).Name}");
    }

    [RuntimeSuite("--entity-host", Order = 13)]
    public static async Task Run()
    {
        var (sync, push, log, drain) = (PyInterop.BeginSyncEntities, PyInterop.PushEntityStates, PyInterop.Log, UserScript.RunDrainTimeout);
        PyInterop.BeginSyncEntities = (json, ok, _) => { Record("sync"); lock (Synced) Synced.Add(json); ok(Answer(json)); };
        PyInterop.PushEntityStates = batch => { lock (Pushed) Pushed.Add(batch); };
        PyInterop.Log = (level, message) => { lock (Logged) { Logged.Add(message); Levelled.Add(((PyLogLevel)level, message)); } };
        try
        {
            await Ordered();
            await RootEntity();
            await Settings();
            await StartFails();
            await SetupFails();
            await BadKeys();
            await HotReplace();
            await Commands();
            await Controls();
            await TimersAndTracks();
            await AFailingEveryLogsOncePerReason();
            await Stopping();
            await Unloading();
            await TrackOffLock();
            await ThrowingLog();
            await SetupTimesOut();
            await SetupThatBlocksItsThreadTimesOut();
            await SyncTimesOut();
            await StopWaitsForCallbacks();
            await SlowStart();
            await PublishFailures();
            await ServiceFromStop();
            await LateAdd();
            await UserLog();
            await MissingJsonCacheHandler();
            await RestoredState();
            await WhatSetupSetsWins();
        }
        finally
        {
            PyInterop.BeginSyncEntities = sync;
            PyInterop.PushEntityStates = push;
            PyInterop.Log = log;
            UserScript.RunDrainTimeout = drain;
            Reset();
        }
    }

    static async Task Ordered()
    {
        Reset();
        var manager = Manager();
        var host = await Load(manager, Compiled("plug1", "Plug one", Plug(new DefinitionSetting("Address", "10.0.0.5")), () => new HostPlug()));
        await Until(() => Recorded().Length == 8, "the start of the device and its entities");
        string[] expected = ["settings", "device.Setup", "relay.Setup", "power.Setup", "sync", "device.Start", "relay.Start", "power.Start"];
        Check(Recorded().SequenceEqual(expected), "order: " + string.Join(", ", Recorded()));
        var json = Syncs().Single();
        const string expectedJson = """{"definitionId":"plug1","name":"Plug one","devices":[{"key":"plug","name":"Plug","manufacturer":"Shelly","model":"Plug S","swVersion":null,"hwVersion":null,"configurationUrl":null,"suggestedArea":null,"via":null}],"entities":[{"uniqueId":"plug1:plug:relay","key":"relay","domain":"switch","name":"Relay","icon":null,"deviceClass":null,"entityCategory":null,"enabledByDefault":true,"commands":["turn_on","turn_off"],"options":{},"device":"plug"},{"uniqueId":"plug1:plug:power","key":"power","domain":"sensor","name":"Power","icon":null,"deviceClass":"power","entityCategory":null,"enabledByDefault":true,"commands":[],"options":{"unit":"W","stateClass":"measurement","options":null},"device":"plug"}]}""";
        Check(json == expectedJson, "sync JSON: " + json);
        Check(host.DefinitionId == "plug1", "definition id");
        var live = string.Join(";", host.Entities.Select(e => $"{e.UniqueId},{e.Key},{e.Domain},{e.Name},{e.EntityId}"));
        Check(live == "plug1:plug:relay,relay,switch,Relay,switch.plug1_relay;plug1:plug:power,power,sensor,Power,sensor.plug1_power", "live entities: " + live);
        await Until(() => PushedAvailable("plug1:plug:relay", true) && PushedAvailable("plug1:plug:power", true), "every entity pushed available");
        Check(host.Status == "running" && host.Error == null, $"status {host.Status}, error {host.Error}");
        Check(!PushedAvailable("plug1:plug:relay", false) && !PushedAvailable("plug1:plug:power", false), "nothing pushed unavailable");
        Console.WriteLine("PASS entity host: settings, device setup, entity setups, sync, device start, entity starts in order; exact sync JSON; every entity pushed available");
        await manager.UnloadUserScriptsAsync();
    }

    static async Task RootEntity()
    {
        Reset();
        var manager = Manager();
        // The class's code keys it main; the node's key replaces that, and its class's title names it.
        var host = await Load(manager, Compiled("lamp", "Lamp", Entity("light", "Hall lamp"), () => new HostLamp()));
        var json = JsonNode.Parse(Syncs().Single())!;
        Check(json["devices"] is JsonArray { Count: 0 } && !json.AsObject().ContainsKey("device"), "a C# entity on no Device node syncs no device: " + json.ToJsonString());
        Check(json["entities"]![0]!.AsObject().TryGetPropertyValue("device", out var on) && on == null, "the C# entity is on no device: " + json.ToJsonString());
        var only = json["entities"]!.AsArray().Single()!;
        Check(only["uniqueId"]!.GetValue<string>() == "lamp:light" && only["key"]!.GetValue<string>() == "light" && only["name"]!.GetValue<string>() == "Hall lamp",
            "the C# entity's unique id is <id>:<node key>, its key the node's, its name its class's title: " + only.ToJsonString());
        Check(host.Entities.Single().UniqueId == "lamp:light" && host.Entities.Single().EntityId == "switch.lamp_light", "live entity of the C# entity");
        await manager.UnloadUserScriptsAsync();

        Reset();
        manager = Manager();
        await Load(manager, Compiled("lamp", "Lamp", Entity("light", "Hall lamp", device: "hall"), () => new HostLamp(),
            new DefinitionDevice("Ikea", null, null, null, null, "Hall", new DefinitionVia("abc123")) { Key = "hall", Name = "Hall light" }));
        var synced = JsonNode.Parse(Syncs().Single())!;
        var device = synced["devices"]!.ToJsonString();
        Check(device == """[{"key":"hall","name":"Hall light","manufacturer":"Ikea","model":null,"swVersion":null,"hwVersion":null,"configurationUrl":null,"suggestedArea":"Hall","via":{"deviceId":"abc123"}}]""", "the Device node of a C# entity: " + device);
        var wired = synced["entities"]![0]!;
        Check(wired["device"]?.GetValue<string>() == "hall" && wired["uniqueId"]!.GetValue<string>() == "lamp:light", "the C# entity is on its Device node: " + synced.ToJsonString());
        Console.WriteLine("PASS entity host: a C# entity alone syncs \"devices\":[] and \"device\":null and the unique id <id>:<node key>, or is on the Device node it is wired to");
        await manager.UnloadUserScriptsAsync();
    }

    static async Task Settings()
    {
        Reset();
        var manager = Manager();
        HostPlug? plug = null;
        await Load(manager, Compiled("plug3", "Plug three", Plug(
            new DefinitionSetting("Address", null), new DefinitionSetting("Channel", 3), new DefinitionSetting("Mode", "Eco"),
            new DefinitionSetting("Factor", 1.5), new DefinitionSetting("Inherited", true)), () => plug = new HostPlug()));
        Check(plug!.Address == "default" && plug.Channel == 3 && plug.Mode == HostMode.Eco && plug.Factor == 1.5 && plug.Inherited,
            $"settings applied: {plug.Address} {plug.Channel} {plug.Mode} {plug.Factor} {plug.Inherited}");
        await manager.UnloadUserScriptsAsync();

        foreach (var (setting, named) in new[] {
            (new DefinitionSetting("Nope", "x"), "Nope"), (new DefinitionSetting("Channel", "x"), "Channel"), (new DefinitionSetting("Channel", 1.5), "Channel"),
            (new DefinitionSetting("Mode", "Turbo"), "Mode"), (new DefinitionSetting("Mode", "1"), "Mode"), (new DefinitionSetting("Unmarked", "x"), "Unmarked") })
        {
            Reset();
            manager = Manager();
            var failure = await LoadFails(manager, Compiled("plug3", "Plug three", Plug(setting), () => new HostPlug()));
            Check(failure is InvalidOperationException && failure.Message.StartsWith("Plug three: Plug: ", StringComparison.Ordinal) && failure.Message.Contains(named),
                $"setting {setting.Key}={setting.Value?.ToJsonString()}: {failure}");
            Check(Syncs().Length == 0 && !Recorded().Contains("device.Setup"), "a bad setting fails before setup and sync");
        }
        // A saved value outside its setting's range (a file edited by hand, say) fails as the definition is applied, naming
        // the setting and the range, never the value.
        foreach (var (setting, message) in new[] {
            (new DefinitionSetting("Interval", 0), "Plug three: Plug: the saved value of setting Poll interval is not a whole number from 1 to 3600"),
            (new DefinitionSetting("Interval", 3601), "Plug three: Plug: the saved value of setting Poll interval is not a whole number from 1 to 3600"),
            (new DefinitionSetting("Gain", 0.25), "Plug three: Plug: the saved value of setting Gain is not a number of at least 0.5"),
            // An int's range given on one side only says that side, not the int's own bound on the other.
            (new DefinitionSetting("Retries", 0), "Plug three: Plug: the saved value of setting Retries is not a whole number of at least 1"),
            (new DefinitionSetting("Delay", 61), "Plug three: Plug: the saved value of setting Delay is not a whole number of at most 60") })
        {
            Reset();
            manager = Manager();
            var failure = await LoadFails(manager, Compiled("plug3", "Plug three", Plug(setting), () => new HostPlug()));
            Check(failure is InvalidOperationException && failure.Message == message, $"setting {setting.Key}={setting.Value?.ToJsonString()}: {failure.Message}");
            Check(Syncs().Length == 0 && !Recorded().Contains("device.Setup"), "a setting outside its range fails before setup and sync");
        }
        Reset();
        manager = Manager();
        await Load(manager, Compiled("plug3", "Plug three", Plug(new DefinitionSetting("Interval", 3600), new DefinitionSetting("Gain", 0.5)), () => plug = new HostPlug()));
        Check(plug.Interval == 3600 && plug.Gain == 0.5, $"values within their range are applied: {plug.Interval} {plug.Gain}");
        await manager.UnloadUserScriptsAsync();
        Console.WriteLine("PASS entity host: a C# device's settings fill its [Setting] properties by type (inherited ones too) and skip nulls; an unknown or ill-typed setting, or one outside its range, fails naming the node and the setting");
    }

    static async Task StartFails()
    {
        Reset();
        var manager = Manager();
        HostPlug.StartHook = _ => throw new InvalidOperationException("start broke");
        var host = await Load(manager, Compiled("plug4", "Plug four", Plug(), () => new HostPlug()));
        await Until(() => PushedAvailable("plug4:plug:relay", false) && PushedAvailable("plug4:plug:power", false), "every entity pushed unavailable");
        Check(host.Status == "failed" && host.Error == "start broke", $"status {host.Status}, error {host.Error}");
        Check(!PushedAvailable("plug4:plug:relay", true) && !PushedAvailable("plug4:plug:power", true), "nothing pushed available");
        Check(Logs().Contains("Plug four: starting failed: start broke"), "the failed start is logged with the definition name: " + string.Join(" | ", Logs()));
        if (!manager.Entities.TryGet("plug4:plug:relay", out var found, out var relay)) throw new Exception("a failed definition stays in the directory");
        Check(found == host, "the directory answers the failed host");
        var refused = await Throws<InvalidOperationException>(() => host.Command(relay, "turn_on", default), "command to a failed definition");
        Check(refused.Message == "The entity is not running" && !Recorded().Contains("relay.TurnOn"), "a failed definition refuses commands");
        Console.WriteLine("PASS entity host: a Start() that throws gives status failed with its message, is logged, and every entity is pushed unavailable");
        await manager.UnloadUserScriptsAsync();
    }

    static async Task SetupFails()
    {
        Reset();
        var manager = Manager();
        HostPlug.SetupHook = _ => throw new InvalidOperationException("setup broke");
        var failure = await LoadFails(manager, Compiled("plug5", "Plug five", Plug(), () => new HostPlug()));
        Check(failure.Message == "Plug five: Plug: setup broke", "the setup failure fails preparing, naming the node: " + failure);
        Check(Syncs().Length == 0 && !Recorded().Contains("device.Start") && Pushes().Length == 0, "nothing synced, started or pushed: " + string.Join(", ", Recorded()));
        Check(manager.Entities.Host("plug5") == null, "a failed setup is not in the directory");
        Reset();
        var neither = await LoadFails(Manager(), Compiled("plug5", "Plug five", Plug(), () => new object()));
        Check(neither is InvalidOperationException && neither.Message == "Plug five: Plug could not be created: Object is not a device" && Syncs().Length == 0, "a class of another type: " + neither);
        Console.WriteLine("PASS entity host: a C# node's Setup() that throws, or a class that is not of the node's kind, fails preparing, naming the node, and nothing is synced");
    }

    static async Task BadKeys()
    {
        var entities = typeof(SharpDevice).GetField("entities", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var key = typeof(SharpEntity).GetField("<Key>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!;
        // The base classes refuse both already; the host checks again, so these go around them, each added to the device
        // as Add() would have, so that its unique id is the device's.
        void Sneak(HostPlug plug, SharpEntity entity)
        {
            entity.AddedTo = plug;
            ((List<SharpEntity>)entities.GetValue(plug)!).Add(entity);
        }
        var hooks = new (Func<HostPlug, Task> Hook, string Key, string Message)[]
        {
            (plug => { Sneak(plug, new SharpSensor("power")); return Task.CompletedTask; }, "power", "Plug six: two entities have the key 'power'"),
            (plug => { var bad = new SharpSensor("temperature"); key.SetValue(bad, "Bad-Key"); Sneak(plug, bad); return Task.CompletedTask; }, "Bad-Key",
                "Plug six: entity key 'Bad-Key' must be a lower-case letter followed by up to 39 lower-case letters, digits or underscores"),
        };
        foreach (var (hook, named, message) in hooks)
        {
            Reset();
            var manager = Manager();
            HostPlug.SetupHook = hook;
            var failure = await LoadFails(manager, Compiled("plug6", "Plug six", Plug(), () => new HostPlug()));
            Check(failure is InvalidOperationException && failure.Message.Contains($"'{named}'") && failure.Message == message, $"key {named}: {failure}");
            Check(Syncs().Length == 0 && Pushes().Length == 0, "nothing synced for a bad key");
        }
        Console.WriteLine("PASS entity host: a duplicate key or one added in Setup that breaks the pattern fails naming the key, with no sync");
    }

    static async Task HotReplace()
    {
        Reset();
        var manager = Manager();
        HostPlug? first = null, second = null;
        var old = await Load(manager, Compiled("plug7", "Plug seven", Plug(), () => first = new HostPlug()));
        await Until(() => PushedAvailable("plug7:plug:relay", true) && PushedAvailable("plug7:plug:power", true), "the first instance pushed available");
        var pushedBefore = Pushes().Length;
        lock (Calls) Calls.Clear();
        // Sends what is pending as the new device starts, so a state the old instance or the new setup posted is not
        // hidden by the new start's states coalescing over it.
        HostPlug.StartHook = _ => manager.Pump.FlushAsync();
        await manager.UpdateUserScript(Compiled("plug7", "Plug seven", Plug(), () => second = new HostPlug()));
        await Until(() => Recorded().Length == 10, "the second instance started");
        string[] expected = ["device.Setup", "relay.Setup", "power.Setup", "sync", "relay.Stop", "power.Stop", "device.Stop", "device.Start", "relay.Start", "power.Start"];
        Check(Recorded().SequenceEqual(expected), "hot replace order: " + string.Join(", ", Recorded()));
        bool PushedAgain(string uniqueId) => Pushes().Skip(pushedBefore).SelectMany(batch => JsonNode.Parse(batch)!.AsArray())
            .Any(s => s!["uniqueId"]!.GetValue<string>() == uniqueId && s["available"]!.GetValue<bool>());
        await Until(() => PushedAgain("plug7:plug:relay") && PushedAgain("plug7:plug:power"), "the second instance pushed every entity available again");
        Check(!PushedAvailable("plug7:plug:relay", false) && !PushedAvailable("plug7:plug:power", false), "nothing pushed unavailable during the replace");
        Check(manager.Entities.TryGet("plug7:plug:relay", out var host, out var relay) && host != old && relay == second!.Relay && relay != first!.Relay,
            "the directory answers the new instance");
        Check(manager.Entities.Host("plug7") == host && old.Status != "running", "the old host is stopped and replaced");
        Console.WriteLine("PASS entity host: a hot replace prepares the new instance, stops the old one before starting the new one, pushes nothing unavailable, and the directory finds the new one");
        await manager.UnloadUserScriptsAsync();
    }

    static async Task Commands()
    {
        Reset();
        var manager = Manager();
        await Load(manager, Compiled("plug8", "Plug eight", Plug(), () => new HostPlug()));
        await Until(() => PushedAvailable("plug8:plug:relay", true), "started");
        if (!manager.Entities.TryGet("plug8:plug:relay", out var host, out var entity)) throw new Exception("the directory finds the relay");
        Check(!manager.Entities.TryGet("plug8:plug:nope", out _, out _) && !manager.Entities.TryGet("other:plug:relay", out _, out _) && !manager.Entities.TryGet("plug8", out _, out _)
            && !manager.Entities.TryGet("plug8:relay", out _, out _), "unknown unique ids are not found, nor the relay without its device's key");
        await host.Command(entity, "turn_on", default);
        Check(Recorded().Contains("relay.TurnOn"), "the command reached the instance");
        await Until(() => Snapshots().Any(s => s["uniqueId"]!.GetValue<string>() == "plug8:plug:relay" && s["values"]!["is_on"]?.GetValue<bool>() == true), "the state change pushed");
        await Throws<NotSupportedException>(() => host.Command(entity, "toggle", default), "an unknown command");
        await host.StopAsync();
        var error = await Throws<InvalidOperationException>(() => host.Command(entity, "turn_on", default), "a command after stop");
        Check(error.Message == "The entity is not running", "not running message: " + error.Message);
        Check(manager.Entities.Host("plug8") == null, "a stopped host leaves the directory");
        Console.WriteLine("PASS entity host: a command through the directory reaches the instance and its state is pushed; after stop it is refused");
        await manager.UnloadUserScriptsAsync();
    }

    // A device with an entity of each type that takes commands with data, run through the host as Home Assistant runs it.
    static async Task Controls()
    {
        Reset();
        var manager = Manager();
        HostControls? device = null;
        var host = await Load(manager, Compiled("panel", "Panel", Device("controls", "Controls"), () => device = new HostControls()));
        await Until(() => PushedAvailable("panel:controls:target", true), "started");
        var synced = JsonNode.Parse(Syncs().Single())!["entities"]!.AsArray().Select(e => $"{e!["domain"]} {e["commands"]!.ToJsonString()} {e["options"]!.ToJsonString()}");
        string[] described = [
            """button ["press"] {}""",
            """number ["set_value"] {"min":5,"max":30,"step":0.5,"mode":"box","unit":"C"}""",
            """select ["select_option"] {"options":["eco","comfort"]}""",
            """text ["set_value"] {"min":1,"max":20,"pattern":null,"mode":"text"}""",
            """light ["turn_on","turn_off"] {"dimmable":true}""",
            """cover ["open","close","stop","set_position"] {"positionable":true}"""];
        Check(synced.SequenceEqual(described), "each entity is synced with its domain, commands and options: " + string.Join(" | ", synced));
        SharpEntity Entity(string key) => manager.Entities.TryGet("panel:controls:" + key, out _, out var entity) ? entity : throw new Exception("the directory finds " + key);
        JsonElement Data(string json) => JsonDocument.Parse(json).RootElement.Clone();
        foreach (var (key, command, data) in new[] {
            ("bell", "press", "{}"), ("target", "set_value", """{"value":21.5}"""), ("mode", "select_option", """{"option":"comfort"}"""),
            ("message", "set_value", """{"value":"Hello"}"""), ("lamp", "turn_on", """{"brightness":128.0}"""), ("lamp", "turn_on", "{}"),
            ("blind", "set_position", """{"position":40}"""), ("blind", "stop", "{}") })
            await host.Command(Entity(key), command, Data(data));
        Check(Recorded().Where(c => c.StartsWith("panel ", StringComparison.Ordinal)).SequenceEqual(["panel bell pressed", "panel lamp on 128", "panel lamp on none", "panel blind stopped"]),
            "each command reached its delegate with its data: " + string.Join(", ", Recorded()));
        JsonNode? Last(string key) => Snapshots().LastOrDefault(s => s["uniqueId"]!.GetValue<string>() == "panel:controls:" + key)?["values"];
        string Shown() => string.Join(" ", new[] { "target", "mode", "message", "lamp", "blind" }.Select(k => Last(k)?.ToJsonString()));
        const string set = """{"value":21.5} {"option":"comfort"} {"value":"Hello"} {"is_on":true,"brightness":128} {"is_closed":false,"position":40}""";
        await Until(() => Shown() == set, "each new state pushed: " + Shown());
        // A value an entity does not take is refused with a message naming the entity and the value, and nothing changes.
        foreach (var (key, command, data, message) in new[] {
            ("target", "set_value", """{"value":45}""", "Target temperature (target) takes a number from 5 to 30, not 45"),
            ("mode", "select_option", """{"option":"boost"}""", "Heating mode (mode) takes one of eco or comfort, not \"boost\""),
            ("message", "set_value", """{"value":""}""", "Message (message) takes a text of 1 to 20 characters, not one of 0"),
            ("lamp", "turn_on", """{"brightness":300}""", "Lamp (lamp) takes a brightness from 0 to 255, in whole numbers, not 300"),
            ("blind", "set_position", """{"value":40}""", "Blind (blind) needs a position: it takes a position from 0 (closed) to 100 (open), in whole numbers") })
        {
            var refused = await Throws<ArgumentException>(() => host.Command(Entity(key), command, Data(data)), $"{key} {command} {data}");
            Check(refused.Message == message, $"{key} {command} {data}: {refused.Message}");
        }
        await manager.Pump.FlushAsync();
        Check(Shown() == set && device!.Target.Value == 21.5 && device.Lamp.Brightness == 128 && host.Status == "running", "a refused command changes nothing: " + Shown());
        Console.WriteLine("PASS entity host: a button, number, select, text, light and cover sync their options, take commands with data through the host, and refuse a value they do not take by message, changing nothing");
        await manager.UnloadUserScriptsAsync();
    }

    /// <summary>
    /// A poll that keeps failing (a device that does not answer) logs one error per new reason, naming the definition and
    /// the timer, then each failure whole at Debug only; once it works again, one line says so.
    /// </summary>
    static async Task AFailingEveryLogsOncePerReason()
    {
        Reset();
        var manager = Manager();
        await Load(manager, Compiled("poller", "Poller", Entity("poller", "Poll sensor"), () => new HostPoller()));
        async Task Ticks(int more)
        {
            var from = HostPoller.Ticks;
            await Until(() => HostPoller.Ticks >= from + more, $"{more} more ticks");
        }
        string[] Errors() => Logs(PyLogLevel.Error).Where(l => l.StartsWith("Poller: ", StringComparison.Ordinal)).ToArray();
        HostPoller.Failure = "the plug did not answer";
        await Ticks(3);
        Check(Errors().SequenceEqual(["Poller: the timer callback every 10 ms failed: the plug did not answer"]), "one error for three failures: " + string.Join(" | ", Errors()));
        Check(Logs(PyLogLevel.Debug).Count(l => l.StartsWith("Poller: the timer callback every 10 ms failed: System.InvalidOperationException: the plug did not answer", StringComparison.Ordinal)) >= 3,
            "each failure whole at Debug: " + string.Join(" | ", Logs(PyLogLevel.Debug)));
        HostPoller.Failure = "the plug refused the connection";
        await Ticks(3);
        Check(Errors().SequenceEqual(["Poller: the timer callback every 10 ms failed: the plug did not answer", "Poller: the timer callback every 10 ms failed: the plug refused the connection"]),
            "a second reason, a second error: " + string.Join(" | ", Errors()));
        HostPoller.Failure = null;
        await Ticks(3);
        Check(Logs(PyLogLevel.Info).Where(l => l.StartsWith("Poller: ", StringComparison.Ordinal)).SequenceEqual(["Poller: the timer callback every 10 ms works again"]),
            "one line says it works again: " + string.Join(" | ", Logs(PyLogLevel.Info)));
        HostPoller.Failure = "the plug did not answer";
        await Ticks(2);
        Check(Errors().Length == 3 && Logs(PyLogLevel.Warn).Length == 0, "a failure after it worked again is an error again, and nothing warns: " + string.Join(" | ", Logs()));
        await manager.UnloadUserScriptsAsync();
        Console.WriteLine("PASS entity host: a failing timer callback logs one error per new reason and one line once it works again, each failure whole at Debug");
    }

    static async Task TimersAndTracks()
    {
        Reset();
        var manager = Manager();
        var host = await Load(manager, Compiled("ticker", "Ticker", Entity("ticker", "Tick sensor"), () => new HostTicker()));
        await Until(() => HostTicker.Ticks >= 3, "the timer ticks again after throwing");
        Check(Logs().Contains("Ticker: the timer callback every 15 ms failed: tick broke"), "the timer failure is logged: " + string.Join(" | ", Logs()));
        Check(host.Status == "running", "the definition keeps running after a timer callback fails");
        Check(RuntimeCoreTests.Subscriptions.Contains("sensor.a"), "Track subscribes the entity");
        await manager.RunEntries("sensor.a", new(new() { EntityId = "sensor.a", State = "on", OldState = new() { EntityId = "sensor.a", State = "off" } }));
        Check(Recorded().Contains("track sensor.a off>on"), "the track received the change: " + string.Join(", ", Recorded()));
        await manager.RunEntries("sensor.a", new(new() { EntityId = "sensor.a", State = "bad" }));
        Check(Logs().Contains("Ticker: an entity callback failed: track broke"), "the track failure is logged: " + string.Join(" | ", Logs()));
        await manager.RunEntries("sensor.a", new(new() { EntityId = "sensor.a", State = "again" }));
        Check(Recorded().Contains("track sensor.a none>again") && host.Status == "running", "the track keeps working after a failure");
        await manager.UnloadScriptAsync("entity_ticker.cs");
        Check(!RuntimeCoreTests.Subscriptions.Contains("sensor.a"), "unloading unsubscribes the tracked entity");
        var ticks = HostTicker.Ticks;
        await Task.Delay(60);
        Check(HostTicker.Ticks == ticks, "the timer stops with the definition");
        Console.WriteLine("PASS entity host: a timer callback that throws is logged with the definition name and ticks again; a track gets changes through RunEntries, logs failures and unsubscribes on unload");
    }

    static async Task Stopping()
    {
        // Stopping a host that never started runs no Stop(), and twice is harmless.
        Reset();
        var manager = Manager();
        var staged = manager.LoadUserScript(Compiled("plug9", "Plug nine", Plug(), () => new HostPlug()), staged: true);
        await staged.PrepareAsync();
        Check(staged.Host!.Status == "setup" && staged.Host.Entities.Count == 2 && manager.Entities.Host("plug9") == null, "prepared, not yet started");
        await staged.ClearFromTrackingAsync();
        await staged.Host!.StopAsync();
        Check(!Recorded().Any(c => c.EndsWith(".Stop") || c.EndsWith(".Start")), "a host that never started runs no Start() or Stop(): " + string.Join(", ", Recorded()));
        staged.Unload();

        // Stop() waits for a start still running, so the two never overlap; a Stop() that throws is logged, not thrown.
        Reset();
        manager = Manager();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HostPlug.StartHook = _ => release.Task;
        HostPlug.StopHook = _ => throw new InvalidOperationException("stop broke");
        await Load(manager, Compiled("plug9", "Plug nine", Plug(), () => new HostPlug()));
        await Until(() => Recorded().Contains("device.Start"), "the start began");
        var unloading = manager.UnloadScriptAsync("entity_plug9.cs");
        await Task.Delay(100);
        Check(!Recorded().Any(c => c.EndsWith(".Stop")) && !unloading.IsCompleted, "Stop() waits for the start");
        release.SetResult();
        await unloading.WaitAsync(TimeSpan.FromSeconds(5));
        string[] expected = ["device.Start", "relay.Start", "power.Start", "relay.Stop", "power.Stop", "device.Stop"];
        Check(Recorded().Where(c => c.EndsWith(".Start") || c.EndsWith(".Stop")).SequenceEqual(expected), "stop after start: " + string.Join(", ", Recorded()));
        Check(Logs().Contains("Plug nine: stopping failed: stop broke"), "the failed Stop() is logged with the definition name: " + string.Join(" | ", Logs()));

        // A Stop() that does not end holds unloading back only for the drain timeout.
        Reset();
        UserScript.RunDrainTimeout = TimeSpan.FromMilliseconds(200);
        manager = Manager();
        HostPlug.StopHook = _ => new TaskCompletionSource().Task;
        await Load(manager, Compiled("plug9", "Plug nine", Plug(), () => new HostPlug()));
        await Until(() => Recorded().Contains("power.Start"), "started");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await manager.UnloadScriptAsync("entity_plug9.cs").WaitAsync(TimeSpan.FromSeconds(5));
        Check(clock.Elapsed < TimeSpan.FromSeconds(2), $"a Stop() that never ends held unloading {clock.Elapsed.TotalSeconds:F1} s");
        Check(Logs().Contains("Plug nine: Stop() did not finish within 0.2 s; unloading without waiting for it"), "the slow Stop() is logged: " + string.Join(" | ", Logs()));
        UserScript.RunDrainTimeout = TimeSpan.FromSeconds(10);
        Console.WriteLine("PASS entity host: stop waits for a running start, runs no Stop() for a host never started, is safe twice, and logs a Stop() that throws or does not end");
    }

    // A Track waiting on Python (to subscribe) must not hold up a Publish of the same host: from Home Assistant a command
    // publishes on a thread that holds the GIL, which the subscribing thread waits for.
    static async Task TrackOffLock()
    {
        Reset();
        var manager = Manager();
        await Load(manager, Compiled("lamp2", "Lamp two", Entity("lamp", "Lamp"), () => new HostLamp()));
        await Until(() => PushedAvailable("lamp2:lamp", true), "started");
        if (!manager.Entities.TryGet("lamp2:lamp", out var host, out var lamp)) throw new Exception("the directory finds the lamp");
        var subscribe = PyInterop.SubscribeToEntityTracking;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        PyInterop.SubscribeToEntityTracking = id => { if (id == "sensor.blocked") { entered.Set(); release.Wait(); } subscribe(id); };
        try
        {
            var tracking = Task.Run(() => host.Track("sensor.blocked", _ => Task.CompletedTask));
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "the track reached the subscription");
            var published = Task.Run(() => host.Publish(lamp));
            var completed = published.Wait(TimeSpan.FromSeconds(2));
            release.Set();
            await tracking.WaitAsync(TimeSpan.FromSeconds(5));
            Check(completed, "a Publish waited for a Track blocked in Python");
        }
        finally { release.Set(); PyInterop.SubscribeToEntityTracking = subscribe; }
        await manager.UnloadUserScriptsAsync();
        Check(!RuntimeCoreTests.Subscriptions.Contains("sensor.blocked"), "the blocked track is unsubscribed on unload");
        Console.WriteLine("PASS entity host: a Track waiting on Python to subscribe does not hold up a Publish of the same host");
    }

    // The host's own failure logging never throws: a failing log must not stop a timer or a failed start's publish.
    static async Task ThrowingLog()
    {
        var log = PyInterop.Log;
        Action<int, string> broken = (_, _) => throw new InvalidOperationException("log broke");
        Reset();
        var manager = Manager();
        PyInterop.Log = broken;
        try
        {
            var ticker = await Load(manager, Compiled("ticker2", "Ticker two", Entity("ticker", "Tick sensor"), () => new HostTicker()));
            await Until(() => HostTicker.Ticks >= 3, "the timer ticks again although logging its failure throws");
            Check(ticker.Status == "running", "still running");
        }
        // Unloading logs through the same delegate, which is not what is checked here.
        finally { PyInterop.Log = log; }
        await manager.UnloadUserScriptsAsync();

        Reset();
        manager = Manager();
        HostPlug.StartHook = _ => throw new InvalidOperationException("start broke");
        PyInterop.Log = broken;
        try
        {
            var plug = await Load(manager, Compiled("plug11", "Plug eleven", Plug(), () => new HostPlug()));
            await Until(() => PushedAvailable("plug11:plug:relay", false) && PushedAvailable("plug11:plug:power", false), "every entity pushed unavailable although logging throws");
            Check(plug.Status == "failed" && plug.Error == "start broke", $"status {plug.Status}, error {plug.Error}");
        }
        finally { PyInterop.Log = log; }
        await manager.UnloadUserScriptsAsync();
        Console.WriteLine("PASS entity host: a log that throws stops neither a failing timer nor the unavailable publish of a failed start");
    }

    // A Setup() that never ends fails its definition within the bound, and the definitions after it still load.
    static async Task SetupTimesOut()
    {
        Reset();
        var timeout = DefinitionHost.SetupTimeout;
        DefinitionHost.SetupTimeout = TimeSpan.FromMilliseconds(200);
        try
        {
            var manager = Manager();
            HostPlug.SetupHook = _ => Task.Delay(Timeout.Infinite);
            var failures = new List<(string Path, Exception Error)>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await manager.LoadUserScripts([Compiled("slow", "Slow plug", Plug(), () => new HostPlug()), Compiled("lamp3", "Lamp three", Entity("lamp", "Lamp"), () => new HostLamp())],
                (path, error) => failures.Add((path, error))).WaitAsync(TimeSpan.FromSeconds(10));
            Check(clock.Elapsed < TimeSpan.FromSeconds(3), $"a Setup() that never ends held the load {clock.Elapsed.TotalSeconds:F1} s");
            Check(failures.Count == 1 && failures[0].Path == "entity_slow.cs" && failures[0].Error is TimeoutException
                && failures[0].Error.Message == "Slow plug: Plug: Setup() did not finish within 0.2 s", "the slow setup's failure: " + string.Join(" | ", failures.Select(f => $"{f.Path} {f.Error}")));
            Check(manager.Entities.Host("slow") == null && manager.Entities.Host("lamp3") is { } lamp && Syncs().Length == 1
                && JsonNode.Parse(Syncs()[0])!["definitionId"]!.GetValue<string>() == "lamp3", "the definition after the slow one loads, and only it syncs");
            await Until(() => PushedAvailable("lamp3:lamp", true), "the definition after the slow one runs");
            Console.WriteLine("PASS entity host: a Setup() that never ends fails within the setup bound, naming the definition and its node, and the definitions after it load");
            await manager.UnloadUserScriptsAsync();
        }
        finally { DefinitionHost.SetupTimeout = timeout; }
    }

    // A Setup() that blocks its thread (a raw connect to a device that is off, a Wait()) fails its definition within the
    // bound too, and the definitions after it still load. The blocked thread is woken at the end.
    static async Task SetupThatBlocksItsThreadTimesOut()
    {
        Reset();
        var timeout = DefinitionHost.SetupTimeout;
        DefinitionHost.SetupTimeout = TimeSpan.FromMilliseconds(200);
        var blocked = new List<Thread>();
        try
        {
            var manager = Manager();
            HostPlug.SetupHook = _ =>
            {
                lock (blocked) blocked.Add(Thread.CurrentThread);
                Thread.Sleep(Timeout.Infinite);
                return Task.CompletedTask;
            };
            var failures = new List<(string Path, Exception Error)>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // Called from the pool: a Setup() run on the caller's thread would block this test, not fail it.
            await Task.Run(() => manager.LoadUserScripts([Compiled("blocking", "Blocking plug", Plug(), () => new HostPlug()), Compiled("lamp5", "Lamp five", Entity("lamp", "Lamp"), () => new HostLamp())],
                (path, error) => failures.Add((path, error)))).WaitAsync(TimeSpan.FromSeconds(10));
            Check(clock.Elapsed < TimeSpan.FromSeconds(3), $"a Setup() that blocks its thread held the load {clock.Elapsed.TotalSeconds:F1} s");
            Check(failures.Count == 1 && failures[0].Path == "entity_blocking.cs" && failures[0].Error is TimeoutException
                && failures[0].Error.Message == "Blocking plug: Plug: Setup() did not finish within 0.2 s", "the blocking setup's failure: " + string.Join(" | ", failures.Select(f => $"{f.Path} {f.Error}")));
            Check(manager.Entities.Host("blocking") == null && manager.Entities.Host("lamp5") is not null && Syncs().Length == 1
                && JsonNode.Parse(Syncs()[0])!["definitionId"]!.GetValue<string>() == "lamp5", "the definition after the blocking one loads, and only it syncs");
            await Until(() => PushedAvailable("lamp5:lamp", true), "the definition after the blocking one runs");
            // Its thread, maybe started only after the bound, is in the list before the end wakes it.
            await Until(() => { lock (blocked) return blocked.Count == 1; }, "the Setup() blocks its thread");
            Check(!blocked[0].IsThreadPoolThread, "the blocking Setup() holds a thread of the pool");
            Console.WriteLine("PASS entity host: a Setup() that blocks its thread fails within the setup bound, naming the definition and its node, and the definitions after it load");
            await manager.UnloadUserScriptsAsync();
        }
        finally
        {
            DefinitionHost.SetupTimeout = timeout;
            HostPlug.SetupHook = null;
            lock (blocked) foreach (var thread in blocked) thread.Interrupt();
        }
    }

    // A sync Home Assistant never answers fails the new revision within the bound; the revision running goes on.
    static async Task SyncTimesOut()
    {
        Reset();
        var manager = Manager();
        var old = await Load(manager, Compiled("plug12", "Plug twelve", Plug(), () => new HostPlug()));
        await Until(() => PushedAvailable("plug12:plug:relay", true), "the first revision runs");
        var (sync, timeout) = (PyInterop.BeginSyncEntities, DefinitionHost.SyncTimeout);
        PyInterop.BeginSyncEntities = (_, _, _) => { };
        DefinitionHost.SyncTimeout = TimeSpan.FromMilliseconds(200);
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var failure = await Throws<TimeoutException>(() => manager.UpdateUserScript(Compiled("plug12", "Plug twelve", Plug(), () => new HostPlug())).WaitAsync(TimeSpan.FromSeconds(10)), "an unanswered sync");
            Check(clock.Elapsed < TimeSpan.FromSeconds(3) && failure.Message == "Plug twelve: Home Assistant did not register the entities within 0.2 s", $"the unanswered sync ({clock.Elapsed.TotalSeconds:F1} s): {failure.Message}");
        }
        finally { PyInterop.BeginSyncEntities = sync; DefinitionHost.SyncTimeout = timeout; }
        if (!manager.Entities.TryGet("plug12:plug:relay", out var host, out var relay)) throw new Exception("the directory finds the previous revision");
        Check(host == old && old.Status == "running", "the previous revision keeps running");
        await host.Command(relay, "turn_on", default);
        Check(Recorded().Contains("relay.TurnOn"), "the previous revision takes commands");
        Console.WriteLine("PASS entity host: a sync that is never answered fails the new revision within the sync bound and the previous revision keeps running");
        await manager.UnloadUserScriptsAsync();
    }

    // Stop() never overlaps a command or a track callback still running: it waits for them, or for the drain timeout.
    static async Task StopWaitsForCallbacks()
    {
        foreach (var kind in new[] { "command", "track" })
        {
            Reset();
            var manager = Manager();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            HostWaiter.Gate = gate.Task;
            var host = await Load(manager, Compiled("waiter", "Waiter", Entity("waiter", "Wait switch"), () => new HostWaiter()));
            await Until(() => PushedAvailable("waiter:waiter", true), "started");
            Task running = kind == "command"
                ? host.Command((host.Entity("waiter:waiter") ?? throw new Exception("the host finds waiter:waiter")), "turn_on", default)
                : manager.RunEntries("sensor.w", new(new() { EntityId = "sensor.w", State = "on" }));
            await Until(() => Recorded().Contains($"{kind} begins"), $"the {kind} began");
            var unloading = manager.UnloadScriptAsync("entity_waiter.cs");
            await Task.Delay(100);
            Check(!Recorded().Contains("waiter.Stop") && !unloading.IsCompleted, $"Stop() ran while the {kind} still ran: " + string.Join(", ", Recorded()));
            gate.SetResult();
            await unloading.WaitAsync(TimeSpan.FromSeconds(5));
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            var calls = Recorded();
            Check(Array.IndexOf(calls, $"{kind} ends") >= 0 && Array.IndexOf(calls, $"{kind} ends") < Array.IndexOf(calls, "waiter.Stop"), $"the {kind} ends before Stop(): " + string.Join(", ", calls));
        }

        // One that never ends holds Stop() back only for the drain timeout, which is logged.
        Reset();
        UserScript.RunDrainTimeout = TimeSpan.FromMilliseconds(200);
        try
        {
            var manager = Manager();
            HostWaiter.Gate = new TaskCompletionSource().Task;
            var host = await Load(manager, Compiled("waiter", "Waiter", Entity("waiter", "Wait switch"), () => new HostWaiter()));
            await Until(() => PushedAvailable("waiter:waiter", true), "started");
            _ = host.Command((host.Entity("waiter:waiter") ?? throw new Exception("the host finds waiter:waiter")), "turn_on", default);
            await Until(() => Recorded().Contains("command begins"), "the command began");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await manager.UnloadScriptAsync("entity_waiter.cs").WaitAsync(TimeSpan.FromSeconds(5));
            Check(clock.Elapsed < TimeSpan.FromSeconds(2), $"a command that never ends held unloading {clock.Elapsed.TotalSeconds:F1} s");
            await Until(() => Recorded().Contains("waiter.Stop"), "Stop() runs after the drain timeout");
            Check(Logs().Count(m => m == "Waiter: 1 timer callback(s), track callback(s), command(s) or frame(s) did not finish within 0.2 s of stopping; going on without them") == 1,
                "the command that did not end is logged once: " + string.Join(" | ", Logs()));
        }
        finally { UserScript.RunDrainTimeout = TimeSpan.FromSeconds(10); }
        Console.WriteLine("PASS entity host: Stop() starts only after a running command or track callback ends, or after the drain timeout, which is logged once");
    }

    // A Start() that does not return leaves the entities unavailable: that is logged once.
    static async Task SlowStart()
    {
        Reset();
        var warning = DefinitionHost.StartWarning;
        DefinitionHost.StartWarning = TimeSpan.FromMilliseconds(100);
        try
        {
            var manager = Manager();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            HostPlug.StartHook = _ => release.Task;
            await Load(manager, Compiled("plug13", "Plug thirteen", Plug(), () => new HostPlug()));
            const string expected = "Plug thirteen: Start() has not returned after 0.1 s; its entities stay unavailable until it does. Start loops with Every().";
            await Until(() => Logs().Contains(expected), "the slow start is logged");
            await Task.Delay(300);
            Check(Logs().Count(m => m.Contains("has not returned")) == 1, "the slow start is logged once: " + string.Join(" | ", Logs()));
            release.SetResult();
            await Until(() => PushedAvailable("plug13:plug:relay", true), "the entities are published once the start returns");
            await manager.UnloadUserScriptsAsync();

            // One that returns in time is not logged.
            Reset();
            manager = Manager();
            await Load(manager, Compiled("plug13", "Plug thirteen", Plug(), () => new HostPlug()));
            await Until(() => PushedAvailable("plug13:plug:relay", true), "started");
            await Task.Delay(200);
            Check(!Logs().Any(m => m.Contains("has not returned")), "a start in time is logged: " + string.Join(" | ", Logs()));
            await manager.UnloadUserScriptsAsync();
        }
        finally { DefinitionHost.StartWarning = warning; }
        Console.WriteLine("PASS entity host: a Start() that has not returned is logged once at the warning bound, one that returns in time is not");
    }

    // A state that cannot be read never reaches the user's code or Home Assistant's process: it is logged once per entity
    // and the other entities are still published.
    static async Task PublishFailures()
    {
        Reset();
        var manager = Manager();
        HostSnapshots? device = null;
        await Load(manager, Compiled("snap", "Snapshots", Device("snapshots", "Snapshot board"), () => device = new HostSnapshots()));
        await Until(() => PushedAvailable("snap:snapshots:loop", true) && PushedAvailable("snap:snapshots:fine", true), "the entities after the failing one are published at start");
        Check(!Snapshots().Any(s => s["uniqueId"]!.GetValue<string>() == "snap:snapshots:bad"), "the entity whose snapshot throws was published");
        const string failed = "Snapshots: publishing the state of entity 'bad' failed: no text";
        Check(Logs().Count(m => m == failed) == 1, "the failing snapshot is logged: " + string.Join(" | ", Logs()));

        var loop = Snapshots().Last(s => s["uniqueId"]!.GetValue<string>() == "snap:snapshots:loop")["attributes"]!;
        Check(loop["cycle"]!.ToJsonString() == """{"self":null,"n":1}""" && loop["mine"]!["mine"] == null, "a dictionary inside itself is null there: " + loop.ToJsonString());
        var (depth, deep) = (0, loop["deep"]);
        while (deep is JsonArray array) { depth++; deep = array[0]; }
        Check(depth == SharpEntity.AttributeDepth && deep == null, $"lists nest {depth} deep, then null: " + loop["deep"]!.ToJsonString());

        // From a setter, the failure stays in the host; it is not logged again for the same entity.
        device!.Bad.Value = new ThrowingText();
        device.Bad.Available = false;
        device.Fine.IsOn = false;
        await Until(() => Snapshots().Any(s => s["uniqueId"]!.GetValue<string>() == "snap:snapshots:fine" && s["values"]!["is_on"]?.GetValue<bool>() == false), "the others publish on");
        Check(Logs().Count(m => m == failed) == 1, "the failing entity is logged again: " + string.Join(" | ", Logs()));
        await manager.UnloadUserScriptsAsync();

        // A failure whose message throws is still reported, and the entities are published unavailable.
        Reset();
        manager = Manager();
        HostPlug.StartHook = _ => throw new BadMessageException();
        var plug = await Load(manager, Compiled("plug14", "Plug fourteen", Plug(), () => new HostPlug()));
        await Until(() => PushedAvailable("plug14:plug:relay", false) && PushedAvailable("plug14:plug:power", false), "a failed start whose message throws publishes unavailable");
        Check(plug.Status == "failed" && plug.Error == nameof(BadMessageException) && Logs().Contains("Plug fourteen: starting failed: BadMessageException"),
            $"status {plug.Status}, error {plug.Error}, logs " + string.Join(" | ", Logs()));
        await manager.UnloadUserScriptsAsync();
        Console.WriteLine("PASS entity host: a snapshot that throws (a ToString that throws, or a message that does) is logged once and never thrown; a dictionary inside itself and lists past the depth limit are null; the other entities still publish");
    }

    // A service called from Stop() completes, and the code after it runs.
    static async Task ServiceFromStop()
    {
        Reset();
        var manager = Manager();
        var previous = PyInterop.CallService;
        PyInterop.CallService = (domain, service, json, done, failed) =>
        {
            Record($"call {domain}.{service} {json}");
            _ = Task.Delay(50).ContinueWith(_ => done?.Invoke(), TaskScheduler.Default);
        };
        try
        {
            HostPlug.StopHook = async plug => { await plug.Call("switch.turn_off", "switch.lamp"); Record("after the call"); };
            await Load(manager, Compiled("plug15", "Plug fifteen", Plug(), () => new HostPlug()));
            await Until(() => PushedAvailable("plug15:plug:relay", true), "started");
            await manager.UnloadScriptAsync("entity_plug15.cs").WaitAsync(TimeSpan.FromSeconds(5));
            Check(Recorded().Contains("call switch.turn_off {\"entity_id\":\"switch.lamp\"}") && Recorded().Contains("after the call"),
                "the call from Stop(): " + string.Join(", ", Recorded()));
            Check(!Logs().Any(m => m.Contains("stopping failed")), "stopping failed: " + string.Join(" | ", Logs()));
        }
        finally { PyInterop.CallService = previous; }
        Console.WriteLine("PASS entity host: CallService awaited inside Stop() completes and the code after it runs");
    }

    // An entity added once the entities are registered is refused, not silently ignored; one added in Setup() is set up.
    static async Task LateAdd()
    {
        Reset();
        var manager = Manager();
        HostPlug.SetupHook = plug => { plug.AddLater("early"); return Task.CompletedTask; };
        HostPlug.StartHook = plug =>
        {
            try { plug.AddLater("late"); Record("late added"); }
            catch (InvalidOperationException error) { Record("late refused: " + error.Message); }
            return Task.CompletedTask;
        };
        var host = await Load(manager, Compiled("plug16", "Plug sixteen", Plug(), () => new HostPlug()));
        await Until(() => Recorded().Any(c => c.StartsWith("late ")), "the late add");
        Check(Recorded().Contains("late refused: HostPlug cannot add entity 'late' now: entities are added in the constructor or in Setup(), before they are registered with Home Assistant."),
            "the late add: " + string.Join(", ", Recorded()));
        Check(host.Entities.Select(e => e.Key).SequenceEqual(["relay", "power", "early"]), "an entity added in Setup() is registered: " + string.Join(", ", host.Entities.Select(e => e.Key)));
        await manager.UnloadUserScriptsAsync();
        Console.WriteLine("PASS entity host: SharpDevice.Add after the entities are registered throws, saying where entities are added; in Setup() it registers the entity");
    }

    // The definition's own log lines name it, as the host's do.
    static async Task UserLog()
    {
        Reset();
        var manager = Manager();
        HostPlug.StartHook = plug => { plug.Say("polling 10.0.0.5"); return Task.CompletedTask; };
        await Load(manager, Compiled("plug17", "Plug seventeen", Plug(), () => new HostPlug()));
        await Until(() => Logs().Contains("Plug seventeen: polling 10.0.0.5"), "the user's log line, after the definition's name: " + string.Join(" | ", Logs()));
        await manager.UnloadUserScriptsAsync();
        Console.WriteLine("PASS entity host: a definition's Log() lines start with the definition's name");
    }

    // Without System.Text.Json's hot-reload handler a stop cannot empty its caches: that is logged once.
    /// <summary>
    /// A C# device's entities come back with the state they last showed while running: those whose RestoreState is true (a
    /// switch and a number unless set, a sensor when set) take it before any Setup(), which sees it, and publish it first;
    /// a sensor by default does not, and a value the entity no longer takes (a number beyond its new maximum) is left out,
    /// which the debug log says, and the definition runs.
    /// </summary>
    static async Task RestoredState()
    {
        Reset();
        var directory = Path.Combine(Path.GetTempPath(), "entity-host-restore-" + Guid.NewGuid());
        var file = Path.Combine(directory, "entity_states.json");
        try
        {
            UserScriptManager Kept() => new() { Pump = new EntityStatePump(TimeSpan.FromMilliseconds(5)), States = new EntityStateStore(file) };
            HostKept.Narrow = false;
            var manager = Kept();
            HostKept? first = null;
            await Load(manager, Compiled("kept", "Kept", Device("kept", "Memory board"), () => first = new HostKept()));
            await Until(() => PushedAvailable("kept:kept:dial", true), "started");
            Check(HostKept.SeenInSetup == null, "a first start has nothing to restore: " + HostKept.SeenInSetup);
            first!.Dial.Value = 12;
            first.Limit.Value = 50;
            first.Reading.Value = 5.0;
            first.Total.Value = 7.0;
            first.Relay.IsOn = true;
            await manager.Pump.FlushAsync();
            await manager.UnloadUserScriptsAsync();
            await manager.States!.FlushAsync();

            Reset();
            HostKept.Narrow = true;
            manager = Kept();
            HostKept? second = null;
            await Load(manager, Compiled("kept", "Kept", Device("kept", "Memory board"), () => second = new HostKept()));
            await Until(() => PushedAvailable("kept:kept:dial", true), "started again");
            Check(HostKept.SeenInSetup == 12, "Setup() sees the restored number: " + HostKept.SeenInSetup);
            JsonNode? First(string key) => Snapshots().First(s => s["uniqueId"]!.GetValue<string>() == "kept:kept:" + key)["values"];
            var shown = string.Join(" ", new[] { "dial", "limit", "reading", "total", "relay" }.Select(key => First(key)!.ToJsonString()));
            Check(shown == """{"value":12} {"value":null} {"value":null} {"value":7} {"is_on":true}""", "the first states after the restart: " + shown);
            Check(second!.Reading.RestoreState == false && second.Total.RestoreState && second.Dial.RestoreState && second.Relay.RestoreState, "RestoreState by type, unless set");
            Check(Logs().Any(m => m == "Kept: Limit (limit) starts without its last value: Limit (limit) takes a number from 0 to 10, not 50"),
                "the value left out is logged: " + string.Join(" | ", Logs()));
            Check(manager.Entities.Host("kept")!.Status == "running", "the definition runs");
            await manager.UnloadUserScriptsAsync();
            // Written now, so no write behind comes after the directory is gone.
            await manager.States!.FlushAsync();
            Console.WriteLine("PASS entity host: a C# device's switch and number come back with their state before Setup(), a sensor only when it asks to, and a value the entity no longer takes is left out");
        }
        finally
        {
            HostKept.Narrow = false;
            HostKept.SeenInSetup = null;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// What a Setup() sets wins over the state an entity comes back with: an entity added in the device's Setup(), or in
    /// another entity's, takes its stored state as it is added, before that Setup() sets its own. A value stored as none
    /// restores nothing: the entity keeps the one its constructor set.
    /// </summary>
    static async Task WhatSetupSetsWins()
    {
        Reset();
        var directory = Path.Combine(Path.GetTempPath(), "entity-host-fresh-" + Guid.NewGuid());
        var file = Path.Combine(directory, "entity_states.json");
        try
        {
            UserScriptManager Kept() => new() { Pump = new EntityStatePump(TimeSpan.FromMilliseconds(5)), States = new EntityStateStore(file) };
            JsonNode? First(string key) => Snapshots().First(s => s["uniqueId"]!.GetValue<string>() == "fresh:fresh:" + key)["values"];
            string Shown() => string.Join(" ", new[] { "relay", "level", "preset" }.Select(key => First(key)!.ToJsonString()));
            (HostFresh.Reading, HostFresh.Level) = (true, 30);
            var manager = Kept();
            await Load(manager, Compiled("fresh", "Fresh", Device("fresh", "Fresh board"), () => new HostFresh()));
            await Until(() => PushedAvailable("fresh:fresh:level", true), "started");
            Check(Shown() == """{"is_on":true} {"value":30} {"value":5}""", "the first run: " + Shown());
            await manager.UnloadUserScriptsAsync();
            // The preset is stored as none, as an entity unknown while it ran would be.
            manager.States!.Remember("fresh:fresh:preset", new JsonObject { ["value"] = null });
            await manager.States.CloseAsync();

            Reset();
            (HostFresh.Reading, HostFresh.Level) = (false, 55);
            manager = Kept();
            await Load(manager, Compiled("fresh", "Fresh", Device("fresh", "Fresh board"), () => new HostFresh()));
            await Until(() => PushedAvailable("fresh:fresh:level", true), "started again");
            Check(Shown() == """{"is_on":false} {"value":55} {"value":5}""", "what the Setup()s set wins, and none restores nothing: " + Shown());
            await manager.UnloadUserScriptsAsync();
            await manager.States!.CloseAsync();
            Console.WriteLine("PASS entity host: an entity added in a Setup() takes its stored state as it is added, so what that Setup() sets wins; a value stored as none restores nothing");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    static async Task MissingJsonCacheHandler()
    {
        var (handler, logged) = (DefinitionHost.JsonCacheHandler, DefinitionHost.JsonCacheHandlerMissingLogged);
        Check(handler != null, "this .NET's System.Text.Json has its hot-reload cache handler");
        DefinitionHost.JsonCacheHandler = null;
        DefinitionHost.JsonCacheHandlerMissingLogged = 0;
        try
        {
            Reset();
            var manager = Manager();
            for (var i = 0; i < 2; i++)
            {
                await Load(manager, Compiled("lamp4", "Lamp four", Entity("lamp", "Lamp"), () => new HostLamp()));
                await manager.UnloadScriptAsync("entity_lamp4.cs");
            }
            Check(Logs().Count(m => m.StartsWith("System.Text.Json has no cache handler for hot reload", StringComparison.Ordinal)) == 1,
                "the missing handler is logged once: " + string.Join(" | ", Logs()));
            await manager.UnloadUserScriptsAsync();
        }
        finally { (DefinitionHost.JsonCacheHandler, DefinitionHost.JsonCacheHandlerMissingLogged) = (handler, logged); }
        Console.WriteLine("PASS entity host: a missing System.Text.Json cache handler is logged once, at warning level");
    }

    static async Task Unloading()
    {
        Reset();
        var folder = Path.Combine(Path.GetTempPath(), "entity-host-" + Guid.NewGuid());
        try
        {
            var history = new RunHistoryStore(Path.Combine(folder, "runs.db"));
            var manager = Manager(history);
            var host = await Load(manager, Compiled("plug10", "Plug ten", Plug(), () => new HostPlug()));
            var id = history.Identity("entity_plug10.cs");
            Check(manager.GetUserScripts().Single().AutomationId == id, "the definition script has a history identity");
            await Until(() => PushedAvailable("plug10:plug:relay", true) && PushedAvailable("plug10:plug:power", true), "started");
            await manager.Pump.FlushAsync();
            var pushed = Pushes().Length;
            await manager.UnloadScriptAsync("entity_plug10.cs");
            Check(Recorded().Contains("device.Stop") && host.Status != "running", "unloading stops the host");
            Check(manager.GetUserScripts().Count == 0 && manager.Entities.Host("plug10") == null, "unloaded and out of the directory");
            Check(history.Identity("entity_plug10.cs") == id, "unloading leaves run history untouched");
            await Task.Delay(30);
            await manager.Pump.FlushAsync();
            Check(Pushes().Length == pushed, "unloading pushes nothing");

            await Load(manager, Compiled("plug10", "Plug ten", Plug(), () => new HostPlug()));
            await manager.DeleteScriptAsync("entity_plug10.cs");
            Check(history.Identity("entity_plug10.cs") != id, "deleting forgets run history");
            Console.WriteLine("PASS entity host: UnloadScriptAsync stops the host and keeps run history; DeleteScriptAsync forgets it");
            await manager.UnloadUserScriptsAsync();
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }
}

public enum HostMode { Normal, Eco }

/// <summary>A base with a setting of its own, so the host's settings include inherited ones.</summary>
public abstract class HostDeviceBase : SharpDevice
{
    [Setting] public bool Inherited { get; set; }
}

/// <summary>A plug: a device with a relay and a power reading that records its life cycle.</summary>
public class HostPlug : HostDeviceBase
{
    public static Func<HostPlug, Task>? SetupHook, StartHook, StopHook;
    string? address = "default";

    public HostPlug()
    {
        Manufacturer = "Shelly";
        Model = "Plug S";
        Relay = Add(new HostRelay());
        Power = Add(new HostPower());
    }

    public HostRelay Relay { get; }
    public HostPower Power { get; }
    public void AddLater(string key) => Add(new SharpSensor(key));
    public void Say(string message) => Log(message);
    public Task<JsonElement?> Call(string service, string entityId) => CallService(service, entityId);
    [Setting] public string? Address { get => address; set { address = value; EntityHostTests.Record("settings"); } }
    [Setting] public int Channel { get; set; }
    [Setting] public HostMode Mode { get; set; }
    [Setting] public double? Factor { get; set; }
    [Setting("Poll interval", Minimum = 1, Maximum = 3600)] public int Interval { get; set; } = 30;
    [Setting(Minimum = 0.5)] public double Gain { get; set; } = 1;
    [Setting(Minimum = 1)] public int Retries { get; set; } = 3;
    [Setting(Maximum = 60)] public int Delay { get; set; }
    public string? Unmarked { get; set; }

    protected internal override async Task Setup() { EntityHostTests.Record("device.Setup"); if (SetupHook != null) await SetupHook(this); }
    protected internal override async Task Start() { EntityHostTests.Record("device.Start"); if (StartHook != null) await StartHook(this); }
    protected internal override async Task Stop() { EntityHostTests.Record("device.Stop"); if (StopHook != null) await StopHook(this); }
}

public class HostRelay : SharpSwitch
{
    public HostRelay() : base("relay", "Relay") { }
    protected internal override Task Setup() { EntityHostTests.Record("relay.Setup"); return Task.CompletedTask; }
    protected internal override Task Start() { EntityHostTests.Record("relay.Start"); return Task.CompletedTask; }
    // Setting a state while stopping must publish nothing: a replaced revision never shows its entities unavailable.
    protected internal override Task Stop() { EntityHostTests.Record("relay.Stop"); IsOn = false; return Task.CompletedTask; }
    protected override Task TurnOn() { EntityHostTests.Record("relay.TurnOn"); IsOn = true; return Task.CompletedTask; }
}

public class HostPower : SharpSensor
{
    public HostPower() : base("power", "Power") { Unit = "W"; DeviceClass = "power"; StateClass = "measurement"; }
    // Setting a state before the start publishes nothing.
    protected internal override Task Setup() { EntityHostTests.Record("power.Setup"); Value = 0; return Task.CompletedTask; }
    protected internal override Task Start() { EntityHostTests.Record("power.Start"); return Task.CompletedTask; }
    protected internal override Task Stop() { EntityHostTests.Record("power.Stop"); return Task.CompletedTask; }
}

/// <summary>A device with an entity of each type that takes commands with data, each handled by a delegate that sets its state.</summary>
public class HostControls : SharpDevice
{
    public HostControls()
    {
        Bell = Add(new SharpButton("bell", "Bell") { OnPress = () => Did("bell pressed") });
        Target = Add(new SharpNumber("target", "Target temperature") { Min = 5, Max = 30, Step = 0.5, Mode = SharpNumberMode.Box, Unit = "C", OnSetValue = value => { Target!.Value = value; return Task.CompletedTask; } });
        Mode = Add(new SharpSelect("mode", "Heating mode") { Options = ["eco", "comfort"], OnSelectOption = option => { Mode!.Option = option; return Task.CompletedTask; } });
        Message = Add(new SharpText("message", "Message") { Min = 1, Max = 20, OnSetValue = text => { Message!.Value = text; return Task.CompletedTask; } });
        Lamp = Add(new SharpLight("lamp", "Lamp")
        {
            Dimmable = true,
            OnTurnOn = brightness => { Lamp!.IsOn = true; if (brightness != null) Lamp.Brightness = brightness; return Did($"lamp on {brightness?.ToString() ?? "none"}"); },
        });
        Blind = Add(new SharpCover("blind", "Blind")
        {
            Positionable = true,
            OnSetPosition = position => { Blind!.Position = position; Blind.IsClosed = position == 0; return Task.CompletedTask; },
            OnStopMoving = () => Did("blind stopped"),
        });
    }

    static Task Did(string what) { EntityHostTests.Record("panel " + what); return Task.CompletedTask; }

    public SharpButton Bell { get; }
    public SharpNumber Target { get; }
    public SharpSelect Mode { get; }
    public SharpText Message { get; }
    public SharpLight Lamp { get; }
    public SharpCover Blind { get; }
}

/// <summary>
/// A device whose number, switch and second sensor come back with their state, and whose first sensor does not; its
/// Setup() records the number it sees. <see cref="Narrow"/> gives its second number a maximum of 10.
/// </summary>
public class HostKept : SharpDevice
{
    public static bool Narrow;
    public static double? SeenInSetup;

    public HostKept()
    {
        Dial = Add(new SharpNumber("dial", "Dial"));
        Limit = Add(new SharpNumber("limit", "Limit") { Max = Narrow ? 10 : 100 });
        Reading = Add(new SharpSensor("reading", "Reading"));
        Total = Add(new SharpSensor("total", "Total") { RestoreState = true });
        Relay = Add(new SharpSwitch("relay", "Relay"));
    }

    public SharpNumber Dial { get; }
    public SharpNumber Limit { get; }
    public SharpSensor Reading { get; }
    public SharpSensor Total { get; }
    public SharpSwitch Relay { get; }

    protected internal override Task Setup() { SeenInSetup = Dial.Value; return Task.CompletedTask; }
}

/// <summary>
/// A device that adds its relay in its Setup() and sets it to what it reads (<see cref="Reading"/>), whose starter adds the
/// level in its own Setup() and sets it to <see cref="Level"/>, and whose preset its constructor sets to 5.
/// </summary>
public class HostFresh : SharpDevice
{
    public static bool? Reading;
    public static double? Level;

    public HostFresh()
    {
        Add(new HostFreshStarter(this));
        Add(new SharpNumber("preset", "Preset") { Value = 5 });
    }

    public T Adding<T>(T entity) where T : SharpEntity => Add(entity);

    protected internal override Task Setup()
    {
        Add(new SharpSwitch("relay", "Relay")).IsOn = Reading;
        return Task.CompletedTask;
    }
}

/// <summary>A switch whose Setup() adds the device's level and sets it.</summary>
public class HostFreshStarter(HostFresh device) : SharpSwitch("starter", "Starter")
{
    protected internal override Task Setup()
    {
        device.Adding(new SharpNumber("level", "Level")).Value = HostFresh.Level;
        return Task.CompletedTask;
    }
}

/// <summary>A switch that is its definition's only entity.</summary>
public class HostLamp : SharpSwitch { }

/// <summary>A sensor whose timer and track callbacks throw.</summary>
public class HostTicker : SharpSensor
{
    public static int Ticks;

    protected internal override Task Start()
    {
        Every(TimeSpan.FromMilliseconds(15), () => { Interlocked.Increment(ref Ticks); throw new InvalidOperationException("tick broke"); });
        Track("sensor.a", change =>
        {
            if (change.To.State == "bad") throw new InvalidOperationException("track broke");
            EntityHostTests.Record($"track {change.EntityId} {change.From?.State ?? "none"}>{change.To.State}");
            return Task.CompletedTask;
        });
        return Task.CompletedTask;
    }
}

/// <summary>A sensor that polls every 10 ms and fails with <see cref="Failure"/> while it is set.</summary>
public class HostPoller : SharpSensor
{
    public static int Ticks;
    public static volatile string? Failure;

    protected internal override Task Start()
    {
        Every(TimeSpan.FromMilliseconds(10), () =>
        {
            var failure = Failure;
            Interlocked.Increment(ref Ticks);
            return failure == null ? Task.CompletedTask : throw new InvalidOperationException(failure);
        });
        return Task.CompletedTask;
    }
}

/// <summary>A switch whose command and track callback wait on a gate, and which records when they and its Stop() run.</summary>
public class HostWaiter : SharpSwitch
{
    public static Task Gate = Task.CompletedTask;

    protected internal override Task Start()
    {
        Track("sensor.w", async _ => { EntityHostTests.Record("track begins"); await Gate; EntityHostTests.Record("track ends"); });
        return Task.CompletedTask;
    }
    protected override async Task TurnOn() { EntityHostTests.Record("command begins"); await Gate; EntityHostTests.Record("command ends"); }
    protected internal override Task Stop() { EntityHostTests.Record("waiter.Stop"); return Task.CompletedTask; }
}

/// <summary>A value whose text throws.</summary>
public sealed class ThrowingText
{
    public override string ToString() => throw new InvalidOperationException("no text");
}

/// <summary>An exception whose message throws.</summary>
public sealed class BadMessageException : Exception
{
    public override string Message => throw new InvalidOperationException("no message");
}

/// <summary>A device whose first entity's snapshot throws, and whose second has attributes that contain themselves or nest too deep.</summary>
public class HostSnapshots : SharpDevice
{
    public HostSnapshots()
    {
        Bad = Add(new SharpSensor("bad"));
        Loop = Add(new SharpSensor("loop"));
        Fine = Add(new SharpSwitch("fine"));
    }

    public SharpSensor Bad { get; }
    public SharpSensor Loop { get; }
    public SharpSwitch Fine { get; }

    protected internal override Task Start()
    {
        Bad.Attributes["text"] = new ThrowingText();
        var cycle = new Dictionary<string, object?>();
        cycle["self"] = cycle;
        cycle["n"] = 1;
        Loop.Attributes["cycle"] = cycle;
        Loop.Attributes["mine"] = Loop.Attributes;
        object? deep = "leaf";
        for (var i = 0; i < 40; i++) deep = new List<object?> { deep };
        Loop.Attributes["deep"] = deep;
        Fine.IsOn = true;
        return Task.CompletedTask;
    }
}
