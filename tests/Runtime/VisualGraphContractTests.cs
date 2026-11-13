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
            if (fixture["warning"] is { } warning && !diagnostics.Any(d => d.Severity == 2 && d.Message.Contains(warning.GetValue<string>(), StringComparison.OrdinalIgnoreCase) &&
                (fixture["warningInputId"] is not { } warningInput || d.InputId == warningInput.GetValue<string>())))
                throw new Exception($"Visual graph {name}: expected warning \"{warning.GetValue<string>()}\", got {string.Join("; ", diagnostics)}");
            if (fixture["noDiagnostics"]?.GetValue<bool>() == true && diagnostics.Count > 0)
                throw new Exception($"Visual graph {name}: expected no diagnostics, got {string.Join("; ", diagnostics)}");
            // Both validators word every message byte for byte alike; the case lists them sorted by code unit.
            // A case without "messages" expects none, so a warning only one validator raises cannot pass by omission.
            {
                var want = (fixture["messages"] as JsonArray ?? []).Select(m => m!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();
                var got = diagnostics.Select(d => d.Message).Order(StringComparer.Ordinal).ToArray();
                if (!want.SequenceEqual(got)) throw new Exception($"Visual graph {name}: expected messages [{string.Join(" | ", want)}], got [{string.Join(" | ", got)}]");
                // Structural problems carry the same codes in both validators.
                var wantCodes = (fixture["codes"] as JsonArray ?? []).Select(m => m!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();
                var gotCodes = diagnostics.Where(d => d.Code != null).Select(d => d.Code!).Order(StringComparer.Ordinal).ToArray();
                if (!wantCodes.SequenceEqual(gotCodes)) throw new Exception($"Visual graph {name}: expected codes [{string.Join(" | ", wantCodes)}], got [{string.Join(" | ", gotCodes)}]");
                foreach (var code in gotCodes) if (!VisualGraphValidator.Codes.Contains(code)) throw new Exception($"Visual graph {name}: undeclared code {code}");
            }
            foreach (var d in diagnostics) if (Jargon.IsMatch(d.Message)) throw new Exception($"Visual graph {name}: message is not worded for people: {d.Message}");
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

    /// <summary>
    /// The catalog's descriptions are what people read in the node library, quick-add and the inspector: none uses
    /// implementation words, the words the editor replaces ("yes/no value", "text"), pointer escapes or raw port ids.
    /// </summary>
    [RuntimeSuite("--catalog-wording", Order = 11)]
    public static Task CatalogWording()
    {
        var descriptions = new List<(string Path, string Text)>();
        void Collect(JsonNode? node, string path)
        {
            if (node is JsonObject obj)
                foreach (var (key, value) in obj)
                    if (key == "description" && value is JsonValue text && text.TryGetValue<string>(out var description)) descriptions.Add((path, description));
                    else Collect(value, path + "/" + key);
            else if (node is JsonArray list)
                for (var i = 0; i < list.Count; i++) Collect(list[i], path + "/" + i);
        }
        Collect(VisualNodeCatalog.GetCatalog(), "");
        if (descriptions.Count < 100) throw new Exception($"Catalog wording: expected the catalog's descriptions, found {descriptions.Count}");
        var problems = descriptions.Where(d => BannedWording.IsMatch(d.Text)).Select(d => $"{d.Path}: \"{BannedWording.Match(d.Text).Value}\" in {d.Text}").ToList();
        if (problems.Count > 0) throw new Exception("Catalog wording: descriptions use words people do not use:\n" + string.Join("\n", problems));
        Console.WriteLine($"PASS catalog wording: {descriptions.Count} catalog descriptions use plain words");
        return Task.CompletedTask;
    }
    static readonly System.Text.RegularExpressions.Regex BannedWording = new(
        @"\b(?:operands?|literals?|payloads?|invocations?|snapshot(?:s|ted)?|detached|concrete|CLR|typed|atomically|RFC|booleans?|strings?|legacy)\b|scope isolation|~[01]|\b(?:branch|field|item|carry|export):|\b[a-z_]+:<",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Messages are one short sentence for people: no full stop, no validator vocabulary, no CLR type names.</summary>
    static readonly System.Text.RegularExpressions.Regex Jargon = new(@"\b(?:literal|unset|operand|payload)\b|global::|System\.|\.$|\b(?:field|data):|type-checked|\b[0-9a-f]{32}\b|\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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
