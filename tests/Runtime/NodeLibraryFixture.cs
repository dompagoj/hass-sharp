using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;

/// <summary>Produces the checked-in cross-language fixture from the real Roslyn compiler.</summary>
public static class NodeLibraryFixture
{
    const string Source = """
        public interface IReading { }
        public record Reading(string State) : IReading;
        public enum Mode { Off, On }
        public static class Samples
        {
            /// <summary>Read a typed state.</summary>
            /// <param name="entity">A detached snapshot.</param>
            /// <returns>The reading.</returns>
            [Node("Describe", Category = "Fixture")]
            public static Reading Describe([Port("Entity state")] EntityStateSnapshot entity) => new(entity.State);
            [Node("Consume")]
            public static void Consume(IReading reading, int type = 3, string? output = null, string service = "light.turn_on", Mode mode = Mode.On) {}
            [Node("Nullable")] public static Reading? Nullable() => null;
            [Node("Required")] public static void Required(Reading reading) {}
            [Node("Factory")] public static int Factory() => 4;
            [Node("Widen")] public static void Widen(double number) {}
            [Node("Is on")] public static bool IsOn(IReading reading) => true;
            [Node("Nullable elements")] public static string?[] NullableElements() => new string?[] { null };
            [Node("Required elements")] public static string[] RequiredElements() => new[] { "text" };
            [Node("Nullable array")] public static string[]? NullableArray() => null;
            [Node("Required enumerable")] public static void RequiredEnumerable(IEnumerable<string> items) {}
            [Node("Required readonly list")] public static void RequiredReadOnlyList(IReadOnlyList<string> items) {}
            [Node("Required list")] public static void RequiredList(IList<string> items) {}
            [Node("Nullable enumerable")] public static void NullableEnumerable(IEnumerable<string?> items) {}
            [Node("Nonfinite defaults")] public static void NonfiniteDefaults(double? nan = double.NaN, float? positive = float.PositiveInfinity, double? negative = double.NegativeInfinity) {}
        }
        """;

    public static JsonObject Build()
    {
        var preview = new NodeLibraryCompiler(new DiagnosticsProvider()).Preview([new("shared", Source, 2)]);
        if (!preview.Valid) throw new Exception(string.Join("; ", preview.Diagnostics.Select(d => d.Message)));
        var snapshot = preview.Snapshot;
        string Kind(string name) => snapshot.Methods.Values.Single(m => m.Method.Name == name).Kind;
        JsonObject Node(string method, string id) => VisualGraphContract.CreateNode(Kind(method), id, snapshot);
        JsonObject Graph(params JsonObject[] nodes)
        {
            var all = new[] { VisualGraphContract.CreateNode("trigger", "t", snapshot) }.Concat(nodes).ToArray();
            all[0]["config"]!["type"] = "manual";
            var edges = new JsonArray();
            for (var i = 0; i < all.Length - 1; i++) edges.Add(new JsonObject { ["id"] = "flow" + i, ["source"] = all[i]["id"]!.GetValue<string>(), ["target"] = all[i+1]["id"]!.GetValue<string>() });
            return new() { ["version"] = 5, ["name"] = "Shared rich catalog", ["nodes"] = new JsonArray(all.Select(n => (JsonNode?)n).ToArray()), ["edges"] = edges };
        }
        void Wire(JsonObject graph, string source, string sourcePort, string target, string targetPort) => graph["edges"]!.AsArray().Add(new JsonObject { ["id"] = "wire" + graph["edges"]!.AsArray().Count, ["kind"] = "data", ["source"] = source, ["sourcePort"] = sourcePort, ["target"] = target, ["targetPort"] = targetPort });
        var cases = new JsonArray();
        void Case(string name, JsonObject graph, bool valid, string? input = null) => cases.Add(new JsonObject { ["name"] = name, ["graph"] = graph.DeepClone(), ["valid"] = valid, ["inputId"] = input });
        var rich = Graph(VisualGraphContract.CreateNode("read_entity", "r", snapshot), Node("Describe", "describe"), Node("Consume", "consume"));
        Wire(rich, "r", "value", "describe", "entity"); Wire(rich, "describe", "result", "consume", "reading");
        Case("entity snapshot to custom record to interface", rich, true);
        rich["nodes"]![3]!["config"]!["arguments"]!["type"]!["value"] = 1.5;
        Case("CLR integer rejects fraction", rich, false, "type");
        rich["nodes"]![3]!["config"]!["arguments"]!["type"]!["value"] = 2147483648d;
        Case("CLR integer rejects overflow", rich, false, "type");
        rich["nodes"]![3]!["config"]!["arguments"]!["type"]!["value"] = 3;
        rich["nodes"]![3]!["config"]!["arguments"]!["mode"]!["value"] = 9;
        Case("enum rejects unknown numeric member", rich, false, "mode");
        rich["nodes"]![3]!["config"]!["arguments"]!["mode"]!["value"] = 1;
        rich["nodes"]![3]!["config"]!["arguments"]!["service"]!["value"] = null;
        Case("non-null CLR string rejects null literal", rich, false, "service");
        rich["nodes"]![3]!["config"]!["arguments"] = JsonValue.Create(7);
        Case("malformed arguments are diagnosed", rich, false, "arguments");
        var nullable = Graph(Node("Nullable", "nullable"), Node("Required", "required"));
        Wire(nullable, "nullable", "result", "required", "reading");
        Case("nullable result cannot feed required reference", nullable, false, "reading");
        var widening = Graph(Node("Factory", "factory"), Node("Widen", "widen"));
        Wire(widening, "factory", "result", "widen", "number"); Case("standard numeric widening", widening, true);
        var missing = Graph(Node("Required", "required"));
        missing["nodes"]![1]!["config"]!["arguments"]!["reading"] = JsonNode.Parse("""{"kind":"literal","type":"global::NodeLib.Reading","value":{"State":"on"}}""");
        Case("arbitrary record is wire-only", missing, false, "reading");
        // Entity field is a value node: it stays out of the run order, and a Log reads it.
        var extraction = Graph(VisualGraphContract.CreateNode("read_entity", "r", snapshot), VisualGraphContract.CreateNode("log", "show", snapshot));
        extraction["nodes"]!.AsArray().Add(VisualGraphContract.CreateNode("entity_field", "field", snapshot));
        Wire(extraction, "r", "value", "field", "entity"); Wire(extraction, "field", "value", "show", "value"); Case("entity scalar extraction is separate", extraction, true);
        foreach (var consumer in new[] { "RequiredEnumerable", "RequiredReadOnlyList", "RequiredList" })
        {
            var arrayGraph = Graph(Node("NullableElements", "array"), Node(consumer, "consumer"));
            Wire(arrayGraph, "array", "result", "consumer", "items");
            Case("nullable array elements reject " + consumer, arrayGraph, false, "items");
        }
        var nullableElements = Graph(Node("NullableElements", "array"), Node("NullableEnumerable", "consumer"));
        Wire(nullableElements, "array", "result", "consumer", "items");
        Case("nullable array elements accept nullable enumerable", nullableElements, true);
        var requiredElements = Graph(Node("RequiredElements", "array"), Node("RequiredEnumerable", "consumer"));
        Wire(requiredElements, "array", "result", "consumer", "items");
        Case("required array elements accept required enumerable", requiredElements, true);
        Case("nonfinite nullable defaults remain optional", Graph(Node("NonfiniteDefaults", "defaults")), true);
        foreach(var (factory,itemType,valid) in new[]{("RequiredElements","string",true),("NullableElements","string",false),("NullableElements","global::System.String?",true),("RequiredElements","number",false),("NullableArray","string",false)})
        {
            var loop=VisualGraphContract.CreateNode("foreach","loop",snapshot);loop["config"]!["itemType"]=itemType;
            var body=VisualGraphContract.CreateNode("log","body",snapshot);
            var graph=Graph(Node(factory,"array"),loop,body);graph["edges"]![2]!["port"]="body";
            Wire(graph,"array","result","loop","items");
            Case("CLR array foreach "+factory+" to "+itemType,graph,valid,valid?null:"items");
        }
        // Custom C# nodes beside the library, with the pins the compiler writes for them (so their type ids are real).
        static JsonObject Custom(string id, string source) => new() { ["id"] = id, ["kind"] = "custom", ["config"] = new JsonObject { ["source"] = source, ["arguments"] = new JsonObject() } };
        static string Method(string returns, string parameters, string body, string before = "") =>
            before + "public static class CustomNode\n{\n    [Node(\"" + (returns == "void" ? "Take" : "Make") + "\")]\n    public static " + returns + " Run(" + parameters + ") => " + body + ";\n}";
        JsonObject Derived(JsonObject graph) => new VisualGenerator(new DiagnosticsProvider()).DeriveSignatures(graph, snapshot);
        void CustomCase(string name, JsonObject from, string port, JsonObject to, string input, bool valid)
        {
            // Describe reads an entity first.
            var entity = from["id"]!.GetValue<string>() == "describe";
            var graph = entity ? Graph(VisualGraphContract.CreateNode("read_entity", "r", snapshot), from, to) : Graph(from, to);
            if (entity) Wire(graph, "r", "value", "describe", "entity");
            Wire(graph, from["id"]!.GetValue<string>(), port, to["id"]!.GetValue<string>(), input);
            Case(name, Derived(graph), valid, valid ? null : input);
        }
        CustomCase("custom: a library int into a custom long", Node("Factory", "factory"), "result", Custom("take", Method("void", "long value", "Logger.Info(value.ToString())")), "value", true);
        CustomCase("custom: a library int into a custom nullable float", Node("Factory", "factory"), "result", Custom("take", Method("void", "float? value", "Logger.Info(value.ToString()!)")), "value", true);
        CustomCase("custom: a custom nullable int does not go into a library double", Custom("make", Method("int?", "", "null")), "result", Node("Widen", "widen"), "number", false);
        CustomCase("custom: a custom decimal into a library double", Custom("make", Method("decimal", "", "1.5m")), "result", Node("Widen", "widen"), "number", true);
        CustomCase("custom: a record the code declares into the library interface it implements", Custom("make", Method("Own", "", "new(\"on\")", "public sealed record Own(string State) : IReading;\n")), "result", Node("Consume", "consume"), "reading", true);
        CustomCase("custom: a library record into a custom pin of the interface it implements", Node("Describe", "describe"), "result", Custom("take", Method("void", "IReading value", "Logger.Info(value.ToString()!)")), "value", true);
        CustomCase("custom: a library record into a custom object pin", Node("Describe", "describe"), "result", Custom("take", Method("void", "object value", "Logger.Info(value.ToString()!)")), "value", true);
        CustomCase("custom: a library nullable record does not go into a custom interface pin", Node("Nullable", "nullable"), "result", Custom("take", Method("void", "IReading value", "Logger.Info(value.ToString()!)")), "value", false);
        CustomCase("custom: a library string array into a custom enumerable of object", Node("RequiredElements", "array"), "result", Custom("take", Method("void", "IEnumerable<object> value", "Logger.Info(string.Join(\",\", value))")), "value", true);
        CustomCase("custom: a custom list into a library enumerable", Custom("make", Method("List<string>", "", "[\"a\"]")), "result", Node("RequiredEnumerable", "consumer"), "items", true);
        CustomCase("custom: a library record does not go into a custom string pin", Node("Describe", "describe"), "result", Custom("take", Method("void", "string value", "Logger.Info(value)")), "value", false);
        foreach (var test in cases.Where(c => c!["name"]!.GetValue<string>().StartsWith("custom: ")))
            if (test!["graph"]!["nodes"]!.AsArray().Any(n => n!["kind"]!.GetValue<string>() == "custom" && n["config"]!["signature"] == null))
                throw new Exception($"{test["name"]}: the custom node's code does not compile");
        return new() { ["sourceId"] = "shared", ["revision"] = 2, ["source"] = Source, ["catalog"] = snapshot.Catalog, ["cases"] = cases };
    }

    public static void Verify()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "visual-rich-catalog.json")))!.AsObject();
        var preview = new NodeLibraryCompiler(new DiagnosticsProvider()).Preview([new(fixture["sourceId"]!.GetValue<string>(), fixture["source"]!.GetValue<string>(), fixture["revision"]!.GetValue<long>())]);
        if (!preview.Valid || !JsonNode.DeepEquals(preview.Snapshot.Catalog, fixture["catalog"])) throw new Exception("Shared catalog must match server-derived Roslyn metadata");
        foreach (var test in fixture["cases"]!.AsArray())
        {
            var diagnostics = VisualGraphValidator.Validate(test!["graph"]!.AsObject(), preview.Snapshot);
            if ((diagnostics.Count == 0) != test["valid"]!.GetValue<bool>() || test["inputId"] is { } input && !diagnostics.Any(d => d.InputId == input.GetValue<string>()))
                throw new Exception($"Shared rich fixture {test["name"]}: {string.Join("; ", diagnostics.Select(d => d.Message))}");
        }
        Console.WriteLine("PASS shared rich catalog fixtures: " + fixture["cases"]!.AsArray().Count);
    }
}
