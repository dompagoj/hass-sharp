using System.Diagnostics;
using System.Text.Json.Nodes;
using HassSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// What the typed service calls cost (not run by default): the Home Assistant fixture scaled to about 600 services, the
/// size of its generated source, the time to generate it and build its assembly once, the time to preview and compile
/// one small automation with and without that assembly referenced (and one whose node calls a service), and Init with 20
/// saved automations. Medians of several runs, after one warm-up.
/// </summary>
static class ServiceCallCost
{
    [RuntimeSuite("--measure-service-calls", RunByDefault = false)]
    public static async Task Measure(string? argument)
    {
        var target = int.TryParse(argument, out var wanted) ? wanted : 600;
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ha-service-descriptions.json")))!.AsObject();
        // Copies of the fixture's domains under new names, until the normalised schema has the services wanted.
        var perCopy = ServiceSchemaCatalog.Parse(fixture.ToJsonString(), _ => { }).Count;
        var scaled = new JsonObject();
        for (var copy = 1; (copy - 1) * perCopy < target; copy++)
            foreach (var (domain, services) in fixture) scaled[copy == 1 ? domain : domain + "_" + copy] = services?.DeepClone();
        var schema = ServiceSchemaCatalog.Parse(scaled.ToJsonString(), _ => { });
        var items = schema.Items.Select(p => KeyValuePair.Create(p.Key, (JsonObject)p.Value!)).ToArray();
        Console.WriteLine($"services: {items.Length} in {scaled.Count} domains");

        static double Median(int runs, Action action)
        {
            action();
            var times = new List<double>();
            for (var i = 0; i < runs; i++) { var watch = Stopwatch.StartNew(); action(); times.Add(watch.Elapsed.TotalMilliseconds); }
            times.Sort();
            return times[times.Count / 2];
        }

        string source = "";
        var generate = Median(7, () => source = ServiceGenerator.Generate(items));
        Console.WriteLine($"generated source: {source.Length / 1024.0:F0} KiB, {source.Count(c => c == '\n')} lines; generate {generate:F0} ms");
        var parse = Median(7, () => CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)));
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path: "Services.g.cs");
        var check = Median(5, () => CSharpCompilation.Create("Services", [tree], NodeLibraryCompiler.FrameworkReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)).GetDiagnostics());
        byte[] image = [];
        var emit = Median(5, () =>
        {
            using var stream = new MemoryStream();
            CSharpCompilation.Create("Services", [tree], NodeLibraryCompiler.FrameworkReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable)).Emit(stream);
            image = stream.ToArray();
        });
        Console.WriteLine($"parse {parse:F0} ms; check alone (GetDiagnostics) {check:F0} ms; emit alone {emit:F0} ms, {image.Length / 1024.0:F0} KiB");

        var empty = new DiagnosticsProvider();
        var full = new DiagnosticsProvider();
        var installs = new List<double>();
        for (var i = 0; i < 6; i++)
        {
            full.UseServiceSchema([]);
            var watch = Stopwatch.StartNew();
            full.UseServiceSchema(items);
            if (i > 0) installs.Add(watch.Elapsed.TotalMilliseconds);
        }
        installs.Sort();
        Console.WriteLine($"UseServiceSchema (generate, parse, build the assembly once, install) {installs[installs.Count / 2]:F0} ms");

        var graph = JsonNode.Parse("""
            {"version":5,"name":"Small","nodes":[
              {"id":"t","kind":"trigger","config":{"type":"entity","entity":"binary_sensor.door"}},
              {"id":"l","kind":"log","config":{"value":{"kind":"literal","type":"string","value":"open"}}}
            ],"edges":[{"id":"tl","source":"t","target":"l"}]}
            """)!.AsObject();
        foreach (var (name, provider) in new[] { ("without", empty), ("with", full) })
        {
            var preview = new VisualGenerator(provider).Preview(graph);
            if (!preview.Valid) throw new Exception("the small graph does not preview: " + string.Join(" | ", preview.Diagnostics.Select(d => d.Message)));
            var previewTime = Median(7, () => new VisualGenerator(provider).Preview(graph));
            var compiler = new CodeCompiler(provider);
            var compileTime = Median(7, () => compiler.CompileSingleFile(Path.Combine(Path.GetTempPath(), "cost.cs"), preview.Source).Result.Unload());
            var library = Median(7, () => new NodeLibraryCompiler(provider).Preview([]));
            Console.WriteLine($"{name} the services: preview {previewTime:F0} ms, compile {compileTime:F0} ms, empty library preview {library:F0} ms");
        }
        {
            // One Custom C# node that does call a service, against the assembly.
            var snapshot = new NodeLibraryCompiler(full).Preview([]).Snapshot;
            var calling = new VisualGenerator(full).DeriveSignatures(VisualExecutionTests.G(VisualExecutionTests.N("c", "custom", new JsonObject
            {
                ["source"] = VisualExecutionTests.CodeSource("await context.Services.Light.TurnOn(\"light.kitchen\", brightnessPct: 30);"), ["arguments"] = new JsonObject(),
            })), snapshot);
            var preview = new VisualGenerator(full).Preview(calling, snapshot);
            if (!preview.Valid) throw new Exception("the calling graph does not preview: " + string.Join(" | ", preview.Diagnostics.Select(d => d.Message)));
            var previewTime = Median(7, () => new VisualGenerator(full).Preview(calling, snapshot));
            var compiler = new CodeCompiler(full);
            var compileTime = Median(7, () => compiler.CompileSingleFile(Path.Combine(Path.GetTempPath(), "cost.cs"), preview.Source).Result.Unload());
            Console.WriteLine($"with the services, a node calling one: preview {previewTime:F0} ms, compile {compileTime:F0} ms");
        }
        // Init with 20 saved automations (10 call a service, 10 do not), with the 600 services persisted and with none.
        var root = Path.Combine(Path.GetTempPath(), "service-call-cost-" + Guid.NewGuid());
        var file = Path.Combine(root, ".storage", "hass_sharp", "service_schema.json");
        try
        {
            PyInterop.Log = (_, _) => { };
            var setup = new HassSharpManager(root);
            await setup.Init();
            setup.SetServiceDescriptions(scaled.ToJsonString(), started: true);
            for (var i = 0; i < 20; i++)
            {
                var body = i % 2 == 0 ? "await context.Services.Light.TurnOn(light, brightnessPct: 30);" : "await Task.CompletedTask;";
                var code = "public static class CustomNode\n{\n    [Node(\"Call\")]\n    public static async Task Call(NodeContext context, string light)\n    {\n        " + body + "\n    }\n}";
                var automation = new JsonObject
                {
                    ["version"] = 5, ["name"] = "Cost " + i,
                    ["nodes"] = new JsonArray(
                        new JsonObject { ["id"] = "t", ["kind"] = "trigger", ["config"] = new JsonObject { ["type"] = "manual" } },
                        new JsonObject { ["id"] = "c", ["kind"] = "custom", ["config"] = new JsonObject { ["source"] = code, ["arguments"] = new JsonObject { ["light"] = new JsonObject { ["kind"] = "literal", ["type"] = "string", ["value"] = "light.kitchen" } } } }),
                    ["edges"] = new JsonArray(new JsonObject { ["id"] = "e", ["source"] = "t", ["target"] = "c", ["port"] = "next" }),
                };
                var saved = JsonNode.Parse(await setup.VisualRequest(new JsonObject { ["operation"] = "save", ["id"] = "cost" + i, ["expectedRevision"] = 0, ["deriveSignatures"] = true, ["graph"] = automation }.ToJsonString()))!;
                if (saved["result"]?["active"]?.GetValue<bool>() != true) throw new Exception("cost automation not active: " + saved.ToJsonString());
            }
            await setup.UnloadAsync();
            var persisted = File.ReadAllText(file);
            foreach (var (name, stored) in new[] { ("none", "{\"version\":1,\"items\":{}}"), ("600 services", persisted) })
            {
                var times = new List<double>();
                for (var i = 0; i < 4; i++)
                {
                    File.WriteAllText(file, stored);
                    var api = new HassSharpManager(root);
                    var watch = Stopwatch.StartNew();
                    await api.Init();
                    var elapsed = watch.Elapsed.TotalMilliseconds;
                    await api.UnloadAsync();
                    if (i > 0) times.Add(elapsed);
                }
                times.Sort();
                Console.WriteLine($"Init with 20 automations (10 call a service), {name} persisted: {times[times.Count / 2]:F0} ms");
            }
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
