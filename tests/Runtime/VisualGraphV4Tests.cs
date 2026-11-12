using System.Text.Json.Nodes;
using HassSharp;

public static class VisualGraphV4Tests
{
    public static Task Run()
    {
        var fixtures = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "visual-v4-validation.json")))!.AsArray();
        foreach (var fixture in fixtures)
        {
            var errors = VisualGraphValidator.Validate(fixture!["graph"]!.AsObject());
            var name = fixture["name"]!.GetValue<string>();
            var valid = fixture["valid"]!.GetValue<bool>();
            if ((errors.Count == 0) != valid) throw new Exception($"V4 {name}: {string.Join("; ", errors.Select(e => e.Message))}");
            if (!valid && !errors.Any(e =>
                (fixture["nodeId"] == null || e.NodeId == fixture["nodeId"]!.GetValue<string>()) &&
                (fixture["inputId"] == null || e.InputId == fixture["inputId"]!.GetValue<string>()) &&
                (fixture["contains"] == null || e.Message.Contains(fixture["contains"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase))))
                throw new Exception($"V4 {name}: expected precise diagnostic, got {string.Join("; ", errors)}");
        }
        var graph = JsonNode.Parse("""{"version":4,"name":"Layout","nodes":[{"id":"trigger/é","kind":"trigger","config":{"type":"manual"},"position":{"x":13,"y":42}},{"id":"d","kind":"delay","config":{"seconds":{"kind":"literal","type":"number","value":2}}}],"edges":[]}""")!.AsObject();
        var laidOut = VisualGraphContract.FillMissingLayout(graph);
        if (graph["nodes"]![1]!["position"] != null || laidOut["nodes"]![0]!["position"]!["x"]!.GetValue<int>() != 13 || laidOut["nodes"]![1]!["position"]!["x"]!.GetValue<int>() != 360)
            throw new Exception("V4 layout must be immutable and preserve supplied positions while filling missing layout");
        if (VisualNodeCatalog.EffectiveTriggerMethod(laidOut["nodes"]![0]!.AsObject()) != "Run_747269676765722FC3A9") throw new Exception("Trigger method identity must use uppercase UTF8 hex");
        var created = VisualGraphContract.CreateNode("parallel", "p");
        if (created["config"]?["branches"]?.ToJsonString() != "[\"a\",\"b\"]") throw new Exception("V4 creation must use catalog defaults");
        Console.WriteLine($"PASS visual graph v4: {fixtures.Count} shared contract fixtures");
        return Task.CompletedTask;
    }
}
