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
/// meets types of the automations' own assemblies. Every generation's automation load contexts must be collected, and
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
    const int Reloads = 30, From = 20;
    // What managed memory may grow between reload 20 and reload 30. Until about reload 20 Roslyn's process-wide caches fill
    // up to their fixed number of entries (see VisualGenerator.Pieces): about 5 MiB from reload 3 to 13. After that it is
    // flat (0.3 MiB over these ten, 40 reloads end where 20 do), so 1 MiB catches 0.1 MiB per reload. With the literals
    // whole it grew about 2 MiB per reload with these four automations; with Python.NET keeping the assemblies no load
    // context was collected at all.
    const double BoundMiB = 1;

    [RuntimeSuite("--reload-memory", Order = 30)]
    public static async Task Run()
    {
        var previous = HassPath.HassConfiguration;
        var root = Path.Combine(Path.GetTempPath(), "reload-memory-" + Guid.NewGuid());
        var log = PyInterop.Log; var schedule = PyInterop.RegisterSchedule;
        var register = PyInterop.BeginRegisterHaTriggers; var remove = PyInterop.BeginRemoveHaTriggers; var call = PyInterop.CallService;
        var calls = new List<(string Service, string Json)>();
        PyInterop.CallService = (domain, name, json, done, _) => { lock (calls) calls.Add((domain + "." + name, json ?? "")); done?.Invoke(); };
        PyInterop.Log = (_, _) => { };
        PyInterop.RegisterSchedule = (_, _) => () => { };
        PyInterop.BeginRegisterHaTriggers = (generation, _, _, success, _) => success(generation);
        PyInterop.BeginRemoveHaTriggers = (_, success, _) => success();
        var descriptions = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ha-service-descriptions.json"));
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

            Check(PythonNetAssemblies.IgnoreCollectible(), "Python.NET's assembly handler is wrapped");
            var contexts = new List<(WeakReference Context, string Name)>();
            var assemblies = new List<WeakReference>();
            var memory = new Dictionary<int, long>();
            for (var reload = 1; reload <= Reloads; reload++)
            {
                // async_unload_entry, then async_setup_entry: services.async_start pushes the descriptions before Init.
                await manager.UnloadAsync();
                manager.SetServiceDescriptions(descriptions, started: true);
                await manager.RetriesDone();
                await manager.Init();
                var loaded = Loaded(manager);
                Check(loaded.Count == Automations.Length && loaded.All(s => s.Active), $"reload {reload}: all {Automations.Length} automations are active again: " + string.Join(", ", loaded.Select(s => s.CompiledScript.FileName)));
                await RunAll(manager, calls, reload);
                contexts.AddRange(loaded.Select(s => (new WeakReference(s.CompiledScript.LoadContext ?? throw new Exception("a compiled automation without its load context")), $"reload {reload} {s.CompiledScript.FileName}")));
                assemblies.AddRange(loaded.Select(s => new WeakReference(s.CompiledScript.Assembly)));
                loaded.Clear();
                if (reload is From or Reloads) memory[reload] = Collected();
            }
            await manager.UnloadAsync();
            // System.Text.Json's process-wide member accessor cache (its compiled getters and constructors of the types it
            // writes) keeps an entry about a second after its last use and drops it at its next use after that: the last
            // automations' types go once anything is written to JSON again, as Home Assistant does all the time.
            await Task.Delay(1500);
            JsonSerializer.Serialize(new { Evict = 1 }, new JsonSerializerOptions());
            Collected();
            var alive = contexts.Count(c => c.Context.IsAlive);
            Check(alive == 0, $"{alive} of {contexts.Count} automation load contexts of earlier generations are still alive after the reloads: "
                + string.Join(", ", contexts.Where(c => c.Context.IsAlive).Select(c => c.Name)));
            var collectible = AssemblyLoadContext.All.OfType<ScriptLoadContext>().Count();
            Check(collectible == 0, $"{collectible} automation load contexts are left after the last unload");
            var aliveAssemblies = assemblies.Count(a => a.IsAlive);
            Check(aliveAssemblies == 0, $"{aliveAssemblies} of {assemblies.Count} automation assemblies of earlier generations are still alive after the reloads");
            var left = CollectibleAssemblies().Count(name => !earlier.Contains(name));
            Check(left == 0, $"{left} collectible assemblies are still loaded after the last unload");
            var growth = (memory[Reloads] - memory[From]) / 1048576.0;
            Check(growth <= BoundMiB, $"managed memory grew {growth:F2} MiB from reload {From} to reload {Reloads} ({memory[From] / 1048576.0:F1} to {memory[Reloads] / 1048576.0:F1} MiB); at most {BoundMiB} MiB is allowed");
            Console.WriteLine($"PASS reload memory: {Reloads} reloads with {Automations.Length} automations, each reload running those that call services (a typed call with an enum and an anonymous object of the node's own, a library record made an object for a Call service field, a Select property node reading a Custom C# class), every load context collected; managed memory {memory[From] / 1048576.0:F1} MiB after reload {From}, {memory[Reloads] / 1048576.0:F1} MiB after reload {Reloads}");
        }
        finally
        {
            await manager.UnloadAsync();
            if (startedPythonNet) StopPythonNetAssemblies();
            PyInterop.Log = log; PyInterop.RegisterSchedule = schedule; PyInterop.BeginRegisterHaTriggers = register; PyInterop.BeginRemoveHaTriggers = remove; PyInterop.CallService = call;
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

    static List<UserScript> Loaded(HassSharpManager manager) =>
        ((UserScriptManager)typeof(HassSharpManager).GetField("_userScriptManager", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(manager)!).GetUserScripts();

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
    }
}
