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
            // Loaded for cases that opt in; the validator takes it once service-aware validation lands.
            if (fixture["services"]?.GetValue<bool>() == true) Services(name);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var diagnostics = VisualGraphValidator.Validate(fixture["graph"]!.AsObject());
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
        }
        var graph = JsonNode.Parse("""{"version":4,"name":"Layout","nodes":[{"id":"trigger/é","kind":"trigger","config":{"type":"manual"},"position":{"x":13,"y":42}},{"id":"d","kind":"delay","config":{"seconds":{"kind":"literal","type":"number","value":2}}}],"edges":[]}""")!.AsObject();
        var laidOut = VisualGraphContract.FillMissingLayout(graph);
        if (graph["nodes"]![1]!["position"] != null || laidOut["nodes"]![0]!["position"]!["x"]!.GetValue<int>() != 13 || laidOut["nodes"]![1]!["position"]!["x"]!.GetValue<int>() != 360)
            throw new Exception("Visual graph layout must be immutable and preserve supplied positions while filling missing layout");
        if (VisualNodeCatalog.EffectiveTriggerMethod(laidOut["nodes"]![0]!.AsObject()) != "Run_747269676765722FC3A9") throw new Exception("Trigger method identity must use uppercase UTF8 hex");
        var created = VisualGraphContract.CreateNode("parallel", "p");
        if (created["config"]?["branches"]?.ToJsonString() != "[\"a\",\"b\"]") throw new Exception("Visual graph creation must use catalog defaults");
        Console.WriteLine($"PASS visual graph contract: {fixtures.Count} shared contract fixtures");
        return Task.CompletedTask;
    }

    static JsonNode Services(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "visual-services.json");
        if (!File.Exists(path)) throw new Exception($"Visual graph {name}: case sets \"services\": true but tests/fixtures/visual-services.json is missing");
        return JsonNode.Parse(File.ReadAllText(path))!;
    }
}
