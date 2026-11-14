using HassSharp;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using static VisualExecutionTests;

/// <summary>
/// Reloading the HassSharp entry many times does not keep memory: the sequence Python runs on a reload (UnloadAsync, the
/// service description push, Init) repeated with a few automations, a node library and Custom C# and library nodes that
/// call a service. Every generation's automation load contexts must be collected, and managed memory must stop growing.
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
        var register = PyInterop.BeginRegisterHaTriggers; var remove = PyInterop.BeginRemoveHaTriggers;
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
            var contexts = new List<WeakReference>();
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
                Check(loaded.Count == 4 && loaded.All(s => s.Active), $"reload {reload}: all four automations are active again: " + string.Join(", ", loaded.Select(s => s.CompiledScript.FileName)));
                contexts.AddRange(loaded.Select(s => new WeakReference(s.CompiledScript.LoadContext ?? throw new Exception("a compiled automation without its load context"))));
                assemblies.AddRange(loaded.Select(s => new WeakReference(s.CompiledScript.Assembly)));
                loaded.Clear();
                if (reload is From or Reloads) memory[reload] = Collected();
            }
            await manager.UnloadAsync();
            Collected();
            var alive = contexts.Count(c => c.IsAlive);
            Check(alive == 0, $"{alive} of {contexts.Count} automation load contexts of earlier generations are still alive after the reloads");
            var collectible = AssemblyLoadContext.All.OfType<ScriptLoadContext>().Count();
            Check(collectible == 0, $"{collectible} automation load contexts are left after the last unload");
            var aliveAssemblies = assemblies.Count(a => a.IsAlive);
            Check(aliveAssemblies == 0, $"{aliveAssemblies} of {assemblies.Count} automation assemblies of earlier generations are still alive after the reloads");
            var left = CollectibleAssemblies().Count(name => !earlier.Contains(name));
            Check(left == 0, $"{left} collectible assemblies are still loaded after the last unload");
            var growth = (memory[Reloads] - memory[From]) / 1048576.0;
            Check(growth <= BoundMiB, $"managed memory grew {growth:F2} MiB from reload {From} to reload {Reloads} ({memory[From] / 1048576.0:F1} to {memory[Reloads] / 1048576.0:F1} MiB); at most {BoundMiB} MiB is allowed");
            Console.WriteLine($"PASS reload memory: {Reloads} reloads with four automations (Custom C# and library nodes calling a service), every load context collected; managed memory {memory[From] / 1048576.0:F1} MiB after reload {From}, {memory[Reloads] / 1048576.0:F1} MiB after reload {Reloads}");
        }
        finally
        {
            await manager.UnloadAsync();
            if (startedPythonNet) StopPythonNetAssemblies();
            PyInterop.Log = log; PyInterop.RegisterSchedule = schedule; PyInterop.BeginRegisterHaTriggers = register; PyInterop.BeginRemoveHaTriggers = remove;
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

    // A node library whose node calls a service, and four automations: that node, a Custom C# node that calls a service,
    // a built-in Call service node behind an entity trigger, and a log.
    static async Task Populate(HassSharpManager manager)
    {
        const string library = "public static class Lights\n{\n    [Node(\"Light up\")]\n    public static async Task<string> LightUp(NodeContext context, string light, double level)\n    {\n        await context.Services.Light.TurnOn(light, brightnessPct: level);\n        return light;\n    }\n}";
        await Request(manager.NodeLibraryRequest, new { operation = "save", id = "lights", name = "Lights.cs", expectedRevision = 0, source = library });
        var catalog = await Request(manager.NodeLibraryRequest, new { operation = "catalog" });
        var kind = catalog["catalog"]!["nodes"]!.AsObject().Single(p => p.Key.StartsWith("library:lights:", StringComparison.Ordinal)).Key;
        var usesLibrary = G(N("l", kind, new { arguments = new { light = Lit("string", "light.kitchen"), level = Lit("global::System.Double", 30) } }));
        usesLibrary["name"] = "Library node";
        var custom = G(N("c", "custom", new JsonObject
        {
            ["source"] = "public static class CustomNode\n{\n    [Node(\"Dim\")]\n    public static async Task Dim(NodeContext context, string light)\n    {\n        await context.Services.Light.TurnOn(light, brightnessPct: 30);\n    }\n}",
            ["arguments"] = new JsonObject { ["light"] = Lit("string", "light.kitchen") },
        }));
        custom["name"] = "Custom node";
        var service = VisualTests.Graph(); service["name"] = "Service node";
        var plain = G(N("log", "log", new { value = Lit("string", "hello") })); plain["name"] = "Log";
        foreach (var (id, graph) in new[] { ("library", usesLibrary), ("custom", custom), ("service", service), ("plain", plain) })
        {
            var saved = await Request(manager.VisualRequest, new { operation = "save", id, graph, expectedRevision = 0, deriveSignatures = true });
            Check(saved["active"]!.GetValue<bool>(), $"{id} is active after its save: " + saved.ToJsonString()[..Math.Min(1200, saved.ToJsonString().Length)]);
        }
    }
}
