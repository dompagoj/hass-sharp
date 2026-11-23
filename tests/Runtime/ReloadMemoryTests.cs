using HassSharp;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using static VisualExecutionTests;

/// <summary>
/// Reloading the HassSharp entry many times does not keep memory: the sequence Python runs on a reload (UnloadAsync, the
/// service description push, Init) repeated with a few automations, a node library and Custom C# and library nodes that
/// call a service. Each generation runs the automations that call services: a Custom C# node passes an enum and an
/// anonymous object of its own to a typed call, and a library record, made an object by Convert, goes into a Call service field, so System.Text.Json
/// meets types of the automations' own assemblies. Each generation also runs an entity definition whose C# device node,
/// deriving a class of its own node library, starts a 20 ms timer and a track of an entity, calls a service from Start() with a record of
/// its library serialized by System.Text.Json, reads a value with its HTTP client from a loopback server and leaves a
/// request open to a server that never answers (it does not pass Stopping, so only disposing the client ends it, which
/// every reload must see happen), and takes a command on its switch; it stores a record of its own code in its Data and
/// reads it back, and follows a shared store, whose change by another component runs its action; its camera shows a frame, and a frame request is
/// left waiting across the reload, which the stop answers with that frame; a library save then rebuilds it
/// (and the automations) and replaces the one Init loaded. After the reloads, a definition built from nodes is saved 100 times,
/// each revision replacing the one before, taking a command on its switch and working out its State from its setting;
/// its camera's Image is a Camera frame, whose picture its first frame shows, and a request then leaves its frame flow
/// waiting across the save for a picture that never comes, which the stop cancels and answers with that picture; last
/// it is turned off while a frame flow waits.
/// Every generation's automation and definition load contexts, and every graph revision's, must be collected, and
/// managed memory must stop growing.
/// <para>
/// Python.NET's assembly manager is started as Home Assistant starts it (it records every assembly loaded into the
/// process): before <see cref="PythonNetAssemblies"/> it held every automation assembly, so no load context unloaded.
/// </para>
/// <para>
/// What this cannot see: the interop callbacks are C# stubs here, so nothing is held by Python. In Home Assistant the
/// trigger and schedule callbacks Python keeps capture the automation's class until unload removes them; that part is
/// shown only in the container (<c>memory_report.py</c>'s debug line: the collectible assemblies stay at the current
/// automations). Native memory is not measured either.
/// </para>
/// </summary>
static class ReloadMemoryTests
{
    const int Reloads = 50, From = 20, Window = 10;
    // What managed memory may grow in the quietest of the three ten-reload windows after reload 20. Memory that is kept
    // grows in every window; what the process fills once does not. Roslyn's process-wide caches fill up to their fixed
    // number of entries (see VisualGenerator.Pieces), and measured with these seven automations memory rises about 7 MiB
    // in a few steps of 1 to 3 MiB, which land at different reloads from run to run (one as late as reload 45), to a
    // level it then keeps: 100 reloads end where 50 do. One window alone therefore failed about every second run of this
    // suite on its own. A quiet window measured 0.3 MiB at most, so 0.5 MiB catches 0.05 MiB per reload. With the
    // literals whole it grew about 2 MiB per reload with four automations; with Python.NET keeping the assemblies no
    // load context was collected at all. With the definition and its library saved each reload (which recompiles the
    // automations too), the level is about 93 to 106 MiB after reload 20 and a step can reach about 6 MiB, but the
    // quietest window still grew 0.1 MiB at most. With the definition's types kept by System.Text.Json, no definition
    // load context was collected.
    const double BoundMiB = 0.5;

    [RuntimeSuite("--reload-memory", Order = 30)]
    public static async Task Run()
    {
        var previous = HassPath.HassConfiguration;
        var root = Path.Combine(Path.GetTempPath(), "reload-memory-" + Guid.NewGuid());
        var log = PyInterop.Log; var schedule = PyInterop.RegisterSchedule;
        var register = PyInterop.BeginRegisterHaTriggers; var remove = PyInterop.BeginRemoveHaTriggers; var call = PyInterop.CallService;
        var push = PyInterop.PushEntityStates;
        // The picture the gauge's camera takes, until it is held: then none comes.
        var snapshot = PyInterop.BeginCameraSnapshot;
        var picture = SharpImage.Blank(24, 16).ToJpeg();
        var held = false;
        PyInterop.BeginCameraSnapshot = (_, _, ok, _) => { if (!Volatile.Read(ref held)) ok(picture); };
        // A camera's interval of half a second lasts 5 ms here.
        var second = CameraFrames.IntervalSecond;
        CameraFrames.IntervalSecond = TimeSpan.FromMilliseconds(10);
        var calls = new List<(string Service, string Json)>();
        PyInterop.CallService = (domain, name, json, done, _) => { lock (calls) calls.Add((domain + "." + name, json ?? "")); done?.Invoke(); };
        // Only the last snapshot pushed for each entity: the timer pushes all the time, and a list of them would be memory kept.
        var states = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        PyInterop.PushEntityStates = batch =>
        {
            foreach (var snapshot in JsonNode.Parse(batch)!.AsArray())
                lock (states) states[snapshot!["uniqueId"]!.GetValue<string>()] = snapshot.AsObject();
        };
        PyInterop.Log = (_, _) => { };
        PyInterop.RegisterSchedule = (_, _) => () => { };
        PyInterop.BeginRegisterHaTriggers = (generation, _, _, success, _) => success(generation);
        PyInterop.BeginRemoveHaTriggers = (_, success, _) => success();
        var descriptions = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ha-service-descriptions.json"));
        // What the definition reads, and where it leaves a request open, which nothing answers.
        await using var server = LoopbackServer.Start();
        await using var silent = SilentServer.Start();
        (Server, Silent) = (server.Url(""), silent.Url(""));
        server.Respond("/reading", LoopbackServer.Reply.Text("42 W"));
        // Other suites' collectible assemblies may still be loaded: only the ones loaded from here on count.
        var earlier = CollectibleAssemblies();
        var startedPythonNet = StartPythonNetAssemblies();
        var manager = new HassSharpManager(root);
        try
        {
            HassPath.HassConfiguration = root;
            await manager.Init();
            manager.SetServiceDescriptions(descriptions, started: true);
            await manager.RetriesDone();
            await Populate(manager);
            await MeterCalled(calls, 0, Marker(0), "the definition's first start");

            Check(PythonNetAssemblies.IgnoreCollectible(), "Python.NET's assembly handler is wrapped");
            var contexts = new List<(WeakReference Context, string Name)>();
            var assemblies = new List<WeakReference>();
            var memory = new Dictionary<int, long>();
            // The camera's frame request left in flight by the generation before.
            Task<byte[]>? pending = null;
            void Watch(IEnumerable<UserScript> scripts, string name)
            {
                foreach (var s in scripts)
                {
                    contexts.Add((new WeakReference(s.CompiledScript.LoadContext ?? throw new Exception("a compiled script without its load context")), $"{name} {s.CompiledScript.FileName}"));
                    assemblies.Add(new WeakReference(s.CompiledScript.Assembly));
                }
            }
            for (var reload = 1; reload <= Reloads; reload++)
            {
                int before; lock (calls) before = calls.Count;
                lock (states) states.Clear();
                // async_unload_entry, then async_setup_entry: services.async_start pushes the descriptions before Init.
                await manager.UnloadAsync();
                if (pending != null) await Answered(pending, null, $"reload {reload}");
                // The requests the stopped definitions left open end as their clients are disposed. The servers forget what
                // they recorded, so that they keep no memory across the reloads.
                await EntityTestHome.Until(() => silent.Open == 0, $"reload {reload}: the requests the stopped definitions left open are cancelled ({silent.Open} open)");
                server.Clear();
                silent.Clear();
                manager.SetServiceDescriptions(descriptions, started: true);
                await manager.RetriesDone();
                await manager.Init();
                // Init runs the definition as the library was saved last; its Start() has run once its call is made.
                var initial = Scripts(manager);
                Check(initial.Count(s => s.Host != null) == 1, $"reload {reload}: Init loads the definition: " + string.Join(", ", initial.Select(s => s.CompiledScript.FileName)));
                await MeterCalled(calls, before, Marker(reload - 1), $"reload {reload}: the definition Init loaded");
                Watch(initial, $"reload {reload} at Init");
                initial.Clear();
                // A library save rebuilds the definition (and the automations, which embed every library) and replaces it.
                var saved = await Request(manager.NodeLibraryRequest, new { operation = "save", id = "meters", name = "Meters.cs", expectedRevision = reload, source = MeterLibrary(reload) });
                Check(saved["definitions"]!.AsArray().Single()!["active"]!.GetValue<bool>(), $"reload {reload}: the library save runs the rebuilt definition: " + saved.ToJsonString()[..Math.Min(1200, saved.ToJsonString().Length)]);
                pending = await Exercise(manager, silent, calls, states, before, reload);
                var loaded = Loaded(manager);
                Check(loaded.Count == Automations.Length && loaded.All(s => s.Active), $"reload {reload}: all {Automations.Length} automations are active again: " + string.Join(", ", loaded.Select(s => s.CompiledScript.FileName)));
                await RunAll(manager, calls, reload);
                Watch(Scripts(manager), $"reload {reload}");
                loaded.Clear();
                if (reload >= From && (reload - From) % Window == 0) memory[reload] = Collected();
            }
            // A definition built from nodes, replaced by a save again and again (a new setting value each time), takes a
            // command on its switch and works out its State, and leaves a frame flow waiting across the save: every
            // revision's load context must go too.
            Task<byte[]>? flowing = null;
            for (var revision = 0; revision < GraphRevisions; revision++)
            {
                var saved = await Request(manager.EntityRequest, new { operation = "save", id = "gauge", expectedRevision = revision, definition = GraphDefinition(revision) });
                Check(saved["active"]!.GetValue<bool>(), $"graph revision {revision + 1} runs: " + saved.ToJsonString()[..Math.Min(1200, saved.ToJsonString().Length)]);
                if (flowing != null) await Answered(flowing, picture, $"graph revision {revision + 1}");
                Volatile.Write(ref held, false);
                Check((await manager.CameraImage("gauge:view")).AsSpan().SequenceEqual(picture), $"graph revision {revision + 1}: the camera's first frame is the picture its Image takes");
                Volatile.Write(ref held, true);
                await Task.Delay(5);
                flowing = await Waiting(manager, "gauge:view", $"graph revision {revision + 1}");
                await manager.EntityCommand("gauge:relay", "turn_on", null);
                var level = revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await EntityTestHome.Until(() => GraphPushed(states, "level", "value") == level, $"graph revision {revision + 1}: the State pushes the setting's value {level}");
                await EntityTestHome.Until(() => GraphPushed(states, "relay", "is_on") == "true", $"graph revision {revision + 1}: the turn on flow pushes on");
                var gauge = Scripts(manager).Where(s => s.Host != null && s.CompiledScript.FilePath.EndsWith("entity_gauge.cs", StringComparison.Ordinal)).ToList();
                Check(gauge.Count == 1, $"graph revision {revision + 1}: one gauge is loaded, not {gauge.Count}");
                Watch(gauge, $"graph revision {revision + 1}");
                gauge.Clear();
                lock (states) states.Clear();
            }
            // Turned off while its frame flow waits: the request gets the picture, a later one is refused.
            await Request(manager.EntityRequest, new { operation = "set_enabled", id = "gauge", enabled = false });
            if (flowing != null) await Answered(flowing, picture, "the gauge turned off");
            flowing = null;
            try { await manager.CameraImage("gauge:view"); throw new Exception("a request to the gauge turned off was answered"); }
            catch (InvalidOperationException refused) when (refused.Message == "The entity is not running") { }
            await manager.UnloadAsync();
            if (pending != null) await Answered(pending, null, "the last unload");
            pending = null;
            // System.Text.Json's process-wide member accessor cache (its compiled getters and constructors of the types it
            // writes) keeps an entry about a second after its last use and drops it at its next use after that: the last
            // automations' types go once anything is written to JSON again, as Home Assistant does all the time. That
            // policy is the library's own, so this polls: wait, write something, collect, look, for up to about 10 s.
            // Each write uses options equal to no others, so its metadata (and the cache use that evicts) is made anew.
            var alive = contexts.Count;
            for (var started = DateTime.UtcNow; alive > 0 && DateTime.UtcNow - started < TimeSpan.FromSeconds(10);)
            {
                await Task.Delay(500);
                JsonSerializer.Serialize(new { Evict = 1 }, new JsonSerializerOptions { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() });
                Collected();
                alive = contexts.Count(c => c.Context.IsAlive);
            }
            Check(alive == 0, $"{alive} of {contexts.Count} automation and definition load contexts of earlier generations are still alive after the reloads: "
                + string.Join(", ", contexts.Where(c => c.Context.IsAlive).Select(c => c.Name)));
            var collectible = AssemblyLoadContext.All.OfType<ScriptLoadContext>().Count();
            Check(collectible == 0, $"{collectible} automation load contexts are left after the last unload");
            var aliveAssemblies = assemblies.Count(a => a.IsAlive);
            Check(aliveAssemblies == 0, $"{aliveAssemblies} of {assemblies.Count} automation and definition assemblies of earlier generations are still alive after the reloads");
            var left = CollectibleAssemblies().Count(name => !earlier.Contains(name));
            Check(left == 0, $"{left} collectible assemblies are still loaded after the last unload");
            var samples = string.Join(", ", memory.OrderBy(m => m.Key).Select(m => $"{m.Value / 1048576.0:F1} MiB after reload {m.Key}"));
            var growth = memory.Keys.Where(reload => reload > From).Min(reload => (memory[reload] - memory[reload - Window]) / 1048576.0);
            Check(growth <= BoundMiB, $"managed memory grew in every {Window} reloads from reload {From} to reload {Reloads}, {growth:F2} MiB at the least ({samples}); a window that grows at most {BoundMiB} MiB is required");
            Console.WriteLine($"PASS reload memory: {Reloads} reloads with {Automations.Length} automations, each reload running those that call services (a typed call with an enum and an anonymous object of the node's own, a library record made an object for a Call service field, a Select property node reading a Custom C# class), and an entity definition rebuilt by a library save whose C# device runs a timer and a track, calls a service with a record of its library, reads over HTTP and leaves a request open across the reload, takes a switch command, stores a record of its own code in its Data and runs a Changed action of a shared store, and has a camera that shows a frame with a frame request in flight across the reload, then a definition built from nodes saved {GraphRevisions} times, each revision taking a command and working out its State, every load context collected; managed memory {samples}");
        }
        finally
        {
            await manager.UnloadAsync();
            if (startedPythonNet) StopPythonNetAssemblies();
            PyInterop.Log = log; PyInterop.RegisterSchedule = schedule; PyInterop.BeginRegisterHaTriggers = register; PyInterop.BeginRemoveHaTriggers = remove; PyInterop.CallService = call;
            PyInterop.PushEntityStates = push;
            PyInterop.BeginCameraSnapshot = snapshot;
            CameraFrames.IntervalSecond = second;
            HassPath.HassConfiguration = previous;
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    static readonly Type AssemblyManager = typeof(Python.Runtime.PyObject).Assembly.GetType("Python.Runtime.AssemblyManager", throwOnError: true)!;
    const System.Reflection.BindingFlags Internal = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;

    // What Python.NET does as Home Assistant loads it: start recording every assembly the process loads. Not when it already does.
    static bool StartPythonNetAssemblies()
    {
        if (AssemblyManager.GetField("lhandler", Internal)!.GetValue(null) != null) return false;
        AssemblyManager.GetMethod("Initialize", Internal)!.Invoke(null, null);
        return true;
    }

    // Python.NET's own shutdown: it removes whichever handler its field holds, the wrapped one included.
    static void StopPythonNetAssemblies()
    {
        AssemblyManager.GetMethod("Shutdown", Internal)!.Invoke(null, null);
        AssemblyManager.GetField("lhandler", Internal)!.SetValue(null, null);
    }

    // By name, so that the list keeps none of them loaded.
    static HashSet<string> CollectibleAssemblies() =>
        [.. AppDomain.CurrentDomain.GetAssemblies().Where(a => a.IsCollectible).Select(a => a.FullName ?? "")];

    // Every loaded script: the automations and the definition.
    static List<UserScript> Scripts(HassSharpManager manager) =>
        ((UserScriptManager)typeof(HassSharpManager).GetField("_userScriptManager", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(manager)!).GetUserScripts();

    // The automations: a definition's script has its host.
    static List<UserScript> Loaded(HassSharpManager manager) => [.. Scripts(manager).Where(s => s.Host == null)];

    // Live managed bytes after full, compacting collections (the large object heap too) and finalisers.
    static long Collected()
    {
        for (var i = 0; i < 3; i++)
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    static async Task<JsonNode> Request(Func<string, Task<string>> api, object request)
    {
        var reply = JsonNode.Parse(await api(JsonSerializer.Serialize(request)))!;
        Check(reply["ok"]!.GetValue<bool>(), "request failed: " + reply.ToJsonString()[..Math.Min(1200, reply.ToJsonString().Length)]);
        return reply["result"]!;
    }

    // The automations Populate saves; RunAll runs each that calls a service, with the call it makes.
    static readonly (string Id, string? Service)[] Automations =
        [("library", "light.turn_on"), ("custom", "light.turn_on"), ("service", null), ("plain", null), ("typed", "notify.notify"), ("record", "notify.notify"), ("select", "notify.notify")];

    // Run now each automation that calls a service, and wait for its call: the values of the automations' own types go
    // through System.Text.Json on every reload.
    static async Task RunAll(HassSharpManager manager, List<(string Service, string Json)> calls, int reload)
    {
        int before; lock (calls) before = calls.Count;
        var running = Automations.Where(a => a.Service != null).ToArray();
        foreach (var (id, _) in running) await Request(manager.VisualRequest, new { operation = "run", id, triggerId = "t" });
        var until = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            List<(string Service, string Json)> made; lock (calls) made = calls.Skip(before).ToList();
            if (made.Count >= running.Length)
            {
                Check(made.Count == running.Length && running.All(a => made.Count(m => m.Service == a.Service) == running.Count(r => r.Service == a.Service)), $"reload {reload}: the calls made: " + string.Join(", ", made.Select(m => m.Service)));
                // Values go out as every service call sends them: the enum by name, the anonymous object and the record with snake_case names.
                Check(made.Any(m => m.Json.Contains("\"target\":\"night\"") && m.Json.Contains("\"data\":{\"minutes\":5,\"mode\":\"night\"}")), $"reload {reload}: the typed call's values: " + string.Join(" | ", made.Select(m => m.Json)));
                Check(made.Any(m => m.Json.Contains("21.5") && m.Json.Contains("\"C\"")), $"reload {reload}: the library record in the field: " + string.Join(" | ", made.Select(m => m.Json)));
                Check(made.Any(m => m.Json.Contains("\"message\":\"kelvin\"")), $"reload {reload}: the property of the Custom C# class in the field: " + string.Join(" | ", made.Select(m => m.Json)));
                break;
            }
            Check(DateTime.UtcNow < until, $"reload {reload}: the automations did not make their calls: " + string.Join(", ", made.Select(m => m.Service)));
            await Task.Delay(5);
        }
        // Each run records its end before the next unload, which would otherwise cancel it.
        await Task.Delay(20);
    }

    // The loopback servers' addresses, which the definition's library names: one that answers, one that never does.
    static string Server = "", Silent = "";
    static string Marker(int generation) => "gen" + generation;

    // The definition's node library as generation N saves it: only its marker changes, so each save rebuilds the
    // definition. Start() starts a 20 ms timer and a track, and calls a service with a record of the library made JSON
    // by System.Text.Json's default options; the switch's handler and the track show what they ran on the "seen" sensor.
    // It also leaves a request open that does not pass Stopping, and shows what it read over HTTP on the "reading" sensor.
    static string MeterLibrary(int generation) => $$"""
        public record Brightness(int BrightnessPct, string Marker);

        public class MemoryMeter : SharpDevice
        {
            const string Marker = "{{Marker(generation)}}";
            readonly SharpSwitch relay;
            readonly SharpSensor level, seen, reading;
            readonly SharpCamera view;
            int ticks, frames;

            public MemoryMeter()
            {
                relay = Add(new SharpSwitch("relay") { OnTurnOn = () => { relay.IsOn = true; seen.Value = "command " + Marker; return Task.CompletedTask; } });
                level = Add(new SharpSensor("level"));
                seen = Add(new SharpSensor("seen"));
                reading = Add(new SharpSensor("reading"));
                // Its first frame keeps the one shown; the next waits until the definition stops.
                view = Add(new SharpCamera("view") { FrameInterval = 0.5, OnFrame = async () =>
                {
                    if (++frames == 1) return null;
                    await Task.Delay(Timeout.Infinite, Stopping);
                    return null;
                } });
            }

            protected override async Task Start()
            {
                view.Show(SharpImage.Blank(32, 16).DrawText(Marker, 1, 1, SharpColor.White, 8));
                Every(System.TimeSpan.FromMilliseconds(20), () => { level.Value = Marker + " tick " + (++ticks); return Task.CompletedTask; });
                Track("sensor.memory", change => { seen.Value = "track " + change.To.State + " " + Marker; return Task.CompletedTask; });
                var data = System.Text.Json.JsonSerializer.SerializeToNode(new Brightness(40, Marker))!.AsObject();
                await CallService("light.turn_on", "light.memory", data);
                _ = Http.GetStringAsync("{{Silent}}/hold?m=" + Marker);
                reading.Value = Marker + " " + await Http.GetStringAsync("{{Server}}/reading", Stopping);
            }
        }
        """;

    // The definition Memory meter: one C# device node, key box named Meter box, whose class derives the library's
    // MemoryMeter; its signature is the one the compiler derives from its code against the library, as the editor stores it.
    // Its Start() stores a record of its own code in its Data and reads it back to its sensor person, and follows the
    // shared store memory.people, whose count it shows there each time another component changes it.
    static JsonObject MeterDefinition()
    {
        const string source = """
            public record Person(string Name, int Age);

            public class Box : MemoryMeter
            {
                readonly SharpSensor person;
                public Box() { person = Add(new SharpSensor("person")); }
                protected override async Task Start()
                {
                    await base.Start();
                    Data.Set("p", new Person("Ivan", 40));
                    person.Value = Data.Get<Person>("p")!.Name;
                    Shared("memory.people").Changed(() => { person.Value = "seen " + Shared("memory.people").Get<int>("n"); return Task.CompletedTask; });
                }
            }
            """;
        var descriptor = new NodeLibraryCompiler(Diagnostics).Preview([new NodeLibrarySource("meters", MeterLibrary(0), 1)], [new CustomNodeSource("box", source, "csharp_device")]).Snapshot.Custom["box"];
        Check(descriptor.Valid, "the C# device's code compiles: " + string.Join(" | ", descriptor.Diagnostics.Select(d => d.Message)));
        var box = EntityNodeTests.CSharpDevice("box", "box", "Meter box", new JsonObject { ["source"] = source, ["signature"] = descriptor.Signature!.DeepClone(), ["arguments"] = new JsonObject() });
        return new()
        {
            ["name"] = "Memory meter", ["settings"] = new JsonArray(),
            ["logic"] = new JsonObject { ["kind"] = "graph", ["graph"] = new JsonObject { ["version"] = 5, ["name"] = "Memory meter", ["nodes"] = new JsonArray(box), ["edges"] = new JsonArray() } },
        };
    }

    // The definition's calls since the given one, by marker.
    static string[] MeterCalls(List<(string Service, string Json)> calls, int from)
    {
        lock (calls)
            return [.. calls.Skip(from).Where(c => c.Service == "light.turn_on" && c.Json.Contains("\"BrightnessPct\":40"))
                .Select(c => JsonNode.Parse(c.Json)!["Marker"]?.GetValue<string>() ?? "")];
    }

    static Task MeterCalled(List<(string Service, string Json)> calls, int from, string marker, string what) =>
        EntityTestHome.Until(() => MeterCalls(calls, from).Contains(marker), $"{what}: its Start() calls light.turn_on with marker {marker}");

    const int GraphRevisions = 100;

    // A gauge whose level is its setting's value and whose switch's turn on flow sets it on; its camera's Image is a
    // Camera frame of camera.memory.
    static object GraphDefinition(int value) => new
    {
        name = "Gauge",
        settings = new[] { new { key = "level", type = "number", value } },
        logic = new { kind = "graph", graph = new JsonObject
        {
            ["version"] = 5, ["name"] = "Gauge",
            ["nodes"] = new JsonArray(N("k", "setting", new { key = "level" }), VisualExecutionTests.Hub("s", "level", "sensor"),
                VisualExecutionTests.Hub("relay", "relay", "switch"), N("set", "set_entity_state", new JsonObject { ["entity"] = "relay", ["is_on"] = Lit("boolean", true) }),
                N("snap", "camera_frame", new JsonObject { ["entity"] = Lit("entity_id:camera", "camera.memory") }), VisualExecutionTests.Hub("view", "view", "camera")),
            ["edges"] = new JsonArray(D("k", "s", "value"), E("relay", "set", "command:turn_on"), D("snap", "view", "image", "image")),
        } },
    };

    // The last value pushed for one of the gauge's entities.
    static string? GraphPushed(Dictionary<string, JsonObject> states, string key, string field)
    {
        lock (states) return states.GetValueOrDefault("gauge:" + key)?["values"]?[field]?.ToJsonString();
    }

    // The data stores of the manager, and the test's own user of them, which writes to the definition's shared store.
    static DataStores Stores(HassSharpManager manager) =>
        ((UserScriptManager)typeof(HassSharpManager).GetField("_userScriptManager", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(manager)!).Data
        ?? throw new Exception("the manager has no data stores");
    static readonly DataUser Writer = new("reload-memory", "writer", action => action());

    // The last value pushed for one of the definition's entities, which its C# device box adds.
    static string? Pushed(Dictionary<string, JsonObject> states, string key, string field)
    {
        lock (states) return states.GetValueOrDefault("meter:box:" + key)?["values"]?[field]?.ToJsonString();
    }

    // The rebuilt definition: its Start() called the service once, read over HTTP and left its request open, its timer
    // ticks, its track gets a change and its switch takes a command, each seen in what it pushes or what the server got.
    // Its camera answers the frame it showed, and a second request is left waiting for a frame across the reload: the
    // request, which the stop ends.
    static async Task<Task<byte[]>> Exercise(HassSharpManager manager, SilentServer silent, List<(string Service, string Json)> calls, Dictionary<string, JsonObject> states, int from, int reload)
    {
        var marker = Marker(reload);
        await MeterCalled(calls, from, marker, $"reload {reload}: the rebuilt definition");
        await EntityTestHome.Until(() => Pushed(states, "reading", "value") == $"\"{marker} 42 W\"", $"reload {reload}: the rebuilt definition reads over HTTP");
        await EntityTestHome.Until(() => silent.Targets().Contains("/hold?m=" + marker), $"reload {reload}: the rebuilt definition leaves a request open");
        Check(MeterCalls(calls, from).SequenceEqual([Marker(reload - 1), marker]), $"reload {reload}: the definition's calls: " + string.Join(", ", MeterCalls(calls, from)));
        await EntityTestHome.Until(() => Pushed(states, "level", "value") is { } level && level.StartsWith($"\"{marker} tick ", StringComparison.Ordinal)
            && int.Parse(level.Split(' ')[^1].TrimEnd('"')) >= 2, $"reload {reload}: the rebuilt definition's timer ticks twice");
        await manager.OnTrackedEntityChange("sensor.memory", new HasEntityState { EntityId = "sensor.memory", State = "r" + reload }, null);
        await EntityTestHome.Until(() => Pushed(states, "seen", "value") == $"\"track r{reload} {marker}\"", $"reload {reload}: the rebuilt definition's track gets the change");
        await manager.EntityCommand("meter:box:relay", "turn_on", null);
        await EntityTestHome.Until(() => Pushed(states, "seen", "value") == $"\"command {marker}\"" && Pushed(states, "relay", "is_on") == "true",
            $"reload {reload}: the rebuilt definition's switch takes the command");
        await EntityTestHome.Until(() => Pushed(states, "person", "value") == "\"Ivan\"", $"reload {reload}: the rebuilt definition reads back the record it stored");
        // Another component's write to the shared store runs the definition's Changed action.
        var people = Stores(manager).OpenShared("memory.people", Writer);
        try
        {
            people.Set("n", reload);
            await EntityTestHome.Until(() => Pushed(states, "person", "value") == $"\"seen {reload}\"", $"reload {reload}: the rebuilt definition's Changed action runs");
        }
        finally { Stores(manager).Close(Writer); }
        var shown = await manager.CameraImage("meter:box:view");
        Check(shown is [0xFF, 0xD8, ..] && shown.Length > 100, $"reload {reload}: the rebuilt definition's camera answers the frame it showed");
        await Task.Delay(10);
        var pending = manager.CameraImage("meter:box:view");
        await Task.Delay(10);
        Check(!pending.IsCompleted, $"reload {reload}: the camera's second request waits for its frame");
        return pending;
    }

    // A frame request to a definition that has stopped since, answered with the camera's last good frame (this one when given).
    static async Task Answered(Task<byte[]> request, byte[]? frame, string when)
    {
        byte[] answer;
        try { answer = await request.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception failure) { throw new Exception($"{when}: the camera's request in flight was not answered with its frame as its definition stopped: {failure.Message}"); }
        Check(answer is [0xFF, 0xD8, ..] && (frame == null || answer.AsSpan().SequenceEqual(frame)), $"{when}: the camera's request in flight is answered with its last good frame");
    }

    // A frame request that waits for the camera's frame flow, which waits for a picture.
    static async Task<Task<byte[]>> Waiting(HassSharpManager manager, string uniqueId, string when)
    {
        var waiting = manager.CameraImage(uniqueId);
        await Task.Delay(10);
        Check(!waiting.IsCompleted, $"{when}: the camera's request waits for its frame flow");
        return waiting;
    }

    // A node library whose nodes call a service and make a record, and seven automations: the first node, a Custom C# node
    // that calls a service, a built-in Call service node behind an entity trigger, a log, a Custom C# node passing an enum
    // and an anonymous object of its own to a typed call, the record made an object and wired into a Call service field,
    // and a Select property node reading a field of a Custom C# value node's own class into a Call service field (its
    // property is looked up on each read, so no type of the automation's load context is held across a reload).
    static async Task Populate(HassSharpManager manager)
    {
        const string library = "public record Reading(double Value, string Unit);\n\npublic static class Lights\n{\n    [Node(\"Light up\")]\n    public static async Task<string> LightUp(NodeContext context, string light, double level)\n    {\n        await context.Services.Light.TurnOn(light, brightnessPct: level);\n        return light;\n    }\n\n    [Node(\"Reading\")]\n    public static Reading Read() => new(21.5, \"C\");\n}";
        await Request(manager.NodeLibraryRequest, new { operation = "save", id = "lights", name = "Lights.cs", expectedRevision = 0, source = library });
        var catalog = await Request(manager.NodeLibraryRequest, new { operation = "catalog" });
        var nodes = catalog["catalog"]!["nodes"]!.AsObject().Where(p => p.Key.StartsWith("library:lights:", StringComparison.Ordinal)).ToArray();
        string Kind(string label) => nodes.Single(p => p.Value?["label"]?.GetValue<string>() == label).Key;
        var usesLibrary = G(N("l", Kind("Light up"), new { arguments = new { light = Lit("string", "light.kitchen"), level = Lit("global::System.Double", 30) } }));
        usesLibrary["name"] = "Library node";
        var custom = G(N("c", "custom", new JsonObject
        {
            ["source"] = "public static class CustomNode\n{\n    [Node(\"Dim\")]\n    public static async Task Dim(NodeContext context, string light)\n    {\n        await context.Services.Light.TurnOn(light, brightnessPct: 30);\n    }\n}",
            ["arguments"] = new JsonObject { ["light"] = Lit("string", "light.kitchen") },
        }));
        custom["name"] = "Custom node";
        var service = VisualTests.Graph(); service["name"] = "Service node";
        var plain = G(N("log", "log", new { value = Lit("string", "hello") })); plain["name"] = "Log";
        var typed = G(N("n", "custom", new JsonObject
        {
            ["source"] = "public enum Mode { Day, Night }\n\npublic static class CustomNode\n{\n    [Node(\"Notify\")]\n    public static async Task Send(NodeContext context)\n    {\n        await context.Services.Notify.Notify(\"hi\", target: Mode.Night, data: new { Minutes = 5, Mode = Mode.Night });\n    }\n}",
            ["arguments"] = new JsonObject(),
        }));
        typed["name"] = "Typed call";
        var record = G(N("r", Kind("Reading"), new { arguments = new { } }), N("o", "convert", new { type = "object", value = new { kind = "unset" } }), N("s", "service", new JsonObject
        {
            ["service"] = "notify.notify", ["fields"] = new JsonObject { ["message"] = Lit("string", "hi"), ["data"] = new JsonObject { ["kind"] = "unset" } },
            ["target"] = new JsonObject { ["kind"] = "unset" }, ["returnResponse"] = false,
        }));
        record["edges"]!.AsArray().Add(D("r", "o", "value", "result"));
        record["edges"]!.AsArray().Add(D("o", "s", "field:data"));
        record["name"] = "Record in a field";
        var select = G(N("mk", "custom", new JsonObject
        {
            ["source"] = "public class Reading { public double Value { get; init; } public string Unit = \"kelvin\"; }\n\npublic static class CustomNode\n{\n    [Node(\"Make\", Pure = true)]\n    public static Reading Make() => new() { Value = 300 };\n}",
            ["arguments"] = new JsonObject(),
        }), N("p", "select_property", new { property = "Unit" }), N("s", "service", new JsonObject
        {
            ["service"] = "notify.notify", ["fields"] = new JsonObject { ["message"] = new JsonObject { ["kind"] = "unset" } },
            ["target"] = new JsonObject { ["kind"] = "unset" }, ["returnResponse"] = false,
        }));
        // The Custom C# node is a value node: out of the run order.
        select["edges"] = new JsonArray(E("t", "s"), D("mk", "p", "value", "result"), D("p", "s", "field:message"));
        select["name"] = "Select property";
        var graphs = new Dictionary<string, JsonObject> { ["library"] = usesLibrary, ["custom"] = custom, ["service"] = service, ["plain"] = plain, ["typed"] = typed, ["record"] = record, ["select"] = select };
        foreach (var (id, _) in Automations)
        {
            var saved = await Request(manager.VisualRequest, new { operation = "save", id, graph = graphs[id], expectedRevision = 0, deriveSignatures = true });
            Check(saved["active"]!.GetValue<bool>(), $"{id} is active after its save: " + saved.ToJsonString()[..Math.Min(1200, saved.ToJsonString().Length)]);
        }
        // The entity definition, after the automations: its library's save would rebuild them.
        await Request(manager.NodeLibraryRequest, new { operation = "save", id = "meters", name = "Meters.cs", expectedRevision = 0, source = MeterLibrary(0) });
        var meter = await Request(manager.EntityRequest, new { operation = "save", id = "meter", expectedRevision = 0, definition = MeterDefinition() });
        Check(meter["active"]!.GetValue<bool>() && meter["document"]!["status"]!.GetValue<string>() == "running", "the definition runs after its save: " + meter.ToJsonString()[..Math.Min(1200, meter.ToJsonString().Length)]);
    }
}
