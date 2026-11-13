using System.Text.Json.Nodes;
using HassSharp;

public static class VisualGraphContractTests
{
    [RuntimeSuite("--visual-graph", Order = 11)]
    public static Task Run()
    {
        var fixtures = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "visual-graph-validation.json")))!.AsArray();
        foreach (var fixture in fixtures)
        {
            var name = fixture!["name"]!.GetValue<string>();
            // A case opts into the golden services section; otherwise the snapshot has none, as at Init.
            var snapshot = fixture["catalogPatch"] is JsonObject patch ? Patched(patch) : VisualCatalogSnapshot.BuiltIn;
            if (fixture["services"]?.GetValue<bool>() == true) snapshot = snapshot.WithServices(Services(name));
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var diagnostics = VisualGraphValidator.Validate(fixture["graph"]!.AsObject(), snapshot);
            if (fixture["maxValidationMs"] is { } limit && elapsed.ElapsedMilliseconds >= limit.GetValue<int>()) throw new Exception($"Visual graph {name}: validation exceeded bounded time");
            var errors = diagnostics.Where(d => d.Severity == 3).ToList();
            var valid = fixture["valid"]!.GetValue<bool>();
            if ((errors.Count == 0) != valid) throw new Exception($"Visual graph {name}: {string.Join("; ", errors.Select(e => e.Message))}");
            if (!valid && !errors.Any(e =>
                (fixture["nodeId"] == null || e.NodeId == fixture["nodeId"]!.GetValue<string>()) &&
                (fixture["inputId"] == null || e.InputId == fixture["inputId"]!.GetValue<string>()) &&
                (fixture["contains"] == null || e.Message.Contains(fixture["contains"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase))))
                throw new Exception($"Visual graph {name}: expected precise diagnostic, got {string.Join("; ", errors)}");
            if (fixture["warning"] is { } warning && !diagnostics.Any(d => d.Severity == 2 && d.Message.Contains(warning.GetValue<string>(), StringComparison.OrdinalIgnoreCase)))
                throw new Exception($"Visual graph {name}: expected warning \"{warning.GetValue<string>()}\", got {string.Join("; ", diagnostics)}");
            if (fixture["noDiagnostics"]?.GetValue<bool>() == true && diagnostics.Count > 0)
                throw new Exception($"Visual graph {name}: expected no diagnostics, got {string.Join("; ", diagnostics)}");
        }
        var graph = JsonNode.Parse("""{"version":5,"name":"Layout","nodes":[{"id":"trigger/é","kind":"trigger","config":{"type":"manual"},"position":{"x":13,"y":42},"size":{"width":300,"height":90}},{"id":"d","kind":"delay","config":{"seconds":{"kind":"literal","type":"number","value":2}}}],"edges":[]}""")!.AsObject();
        var laidOut = VisualGraphContract.FillMissingLayout(graph);
        if (graph["nodes"]![1]!["position"] != null || laidOut["nodes"]![0]!["position"]!["x"]!.GetValue<int>() != 13 || laidOut["nodes"]![1]!["position"]!["x"]!.GetValue<int>() != 360)
            throw new Exception("Visual graph layout must be immutable and preserve supplied positions while filling missing layout");
        // Nodes are measured by the editor: a size from an older client is accepted and dropped, never filled in.
        if (graph["nodes"]![0]!["size"] == null || laidOut["nodes"]!.AsArray().Any(n => n!.AsObject().ContainsKey("size")))
            throw new Exception("Visual graph layout must drop node size without changing the supplied graph");
        if (VisualNodeCatalog.EffectiveTriggerMethod(laidOut["nodes"]![0]!.AsObject()) != "Run_747269676765722FC3A9") throw new Exception("Trigger method identity must use uppercase UTF8 hex");
        var created = VisualGraphContract.CreateNode("parallel", "p");
        if (created["config"]?["branches"]?.ToJsonString() != "[\"a\",\"b\"]") throw new Exception("Visual graph creation must use catalog defaults");
        Console.WriteLine($"PASS visual graph contract: {fixtures.Count} shared contract fixtures");
        return Task.CompletedTask;
    }

    // A case may extend the built-in catalog, e.g. with input constraints no built-in node declares yet.
    static VisualCatalogSnapshot Patched(JsonObject patch)
    {
        static void Merge(JsonObject target, JsonObject source)
        {
            foreach (var (key, value) in source)
                if (value is JsonObject child && target[key] is JsonObject existing) Merge(existing, child);
                else target[key] = value?.DeepClone();
        }
        var catalog = VisualNodeCatalog.GetCatalog();
        Merge(catalog, patch);
        return new VisualCatalogSnapshot(catalog, [], [], [], null);
    }

    // The golden tests/fixtures/visual-services.json is the normalised form of ha-service-descriptions.json
    // (the service schema suite keeps them equal); parse the raw input and confirm it is that golden.
    static ServiceSchemaCatalog? services;
    static ServiceSchemaCatalog Services(string name)
    {
        if (services != null) return services;
        var path = Path.Combine(AppContext.BaseDirectory, "visual-services.json");
        if (!File.Exists(path)) throw new Exception($"Visual graph {name}: case sets \"services\": true but tests/fixtures/visual-services.json is missing");
        var golden = JsonNode.Parse(File.ReadAllText(path))!;
        var parsed = ServiceSchemaCatalog.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ha-service-descriptions.json")), _ => { });
        if (!JsonNode.DeepEquals(parsed.Section, golden)) throw new Exception($"Visual graph {name}: ha-service-descriptions.json no longer normalises to visual-services.json");
        return services = parsed;
    }
}
