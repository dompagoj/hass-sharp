using HassSharp;
using System.Text.Json;
using System.Text.Json.Nodes;
using static VisualExecutionTests;

/// <summary>
/// "Any number into any number" decides which wires are accepted (Task 25), never how a value is rounded: Compare meets
/// two numbers in the type both widen to, and every place a value is changed to a declared number type that may be
/// narrower checks the value fits, failing the step with its name and the pin's.
/// </summary>
static class NumberWiringTests
{
    static JsonObject Unset() => new() { ["kind"] = "unset" };
    static JsonObject Graph(JsonObject[] nodes, params JsonObject[] edges) { var graph = G(nodes); graph["edges"] = new JsonArray(edges.ToArray<JsonNode>()); return graph; }
    static JsonObject Say(string id, string text) => N(id, "log", new { value = Lit("string", text) });
    static JsonObject Reader(string id) => N(id, "log", new { value = Unset() });
    static async Task<List<string>> Logged(JsonObject graph, VisualCatalogSnapshot snapshot)
    {
        var messages = new List<string>(); var previous = PyInterop.Log;
        PyInterop.Log = (_, message) => { lock (messages) messages.Add(message); };
        try { await Execute(graph, snapshot); }
        finally { PyInterop.Log = previous; }
        return messages;
    }
    static async Task Fails(JsonObject graph, VisualCatalogSnapshot snapshot, string message, string what)
    {
        try { await Logged(graph, snapshot); }
        catch (InvalidOperationException error) { Check(error.Message == message, what + ": " + error.Message); return; }
        throw new Exception(what + ": the run did not fail");
    }

    sealed class Sink : IRunTraceSink
    {
        public string? Last;
        void IRunTraceSink.Write(string json, int eventCount, bool truncated, bool final, bool immediate) { if (final) Last = json; }
    }

    [RuntimeSuite("--number-wiring", Order = 22)]
    public static async Task Run()
    {
        var previousPath = HassPath.HassConfiguration; HassPath.HassConfiguration = Path.Combine(Path.GetTempPath(), "number-wiring-" + Guid.NewGuid());
        try
        {
            var library = new NodeLibraryCompiler(Diagnostics).Preview([new("numbers", """
                public static class Numbers
                {
                    [Node("Half", Pure = true)] public static double Half() => 2.5;
                    [Node("Four", Pure = true)] public static double Four() => 4;
                    [Node("Two", Pure = true)] public static int Two() => 2;
                    [Node("Big", Pure = true)] public static long Big() => 5000000000;
                    [Node("Exact", Pure = true)] public static long Exact() => 9007199254740993;
                    [Node("Near", Pure = true)] public static long Near() => 9007199254740992;
                    [Node("Money", Pure = true)] public static decimal Money() => 2.5m;
                    [Node("Large", Pure = true)] public static uint Large() => 4000000000;
                    [Node("Minus", Pure = true)] public static int Minus() => -1;
                    [Node("Huge", Pure = true)] public static ulong Huge() => ulong.MaxValue;
                    [Node("Maybe", Pure = true)] public static int? Maybe() => 3;
                    [Node("Fractions", Pure = true)] public static double[] Fractions() => [1, 2.5];
                    [Node("Wholes", Pure = true)] public static double[] Wholes() => [1, 2];
                    [Node("Count")] public static void Count(int value) => Logger.Info("count " + value);
                }
                """, 1)]);
            Check(library.Valid, "numbers library compiles: " + string.Join(" | ", library.Diagnostics.Select(d => d.Message)));
            var snapshot = library.Snapshot; string Kind(string method) => snapshot.Methods.Values.Single(m => m.MethodName == method).Kind;
            JsonObject Value(string id, string method) => N(id, Kind(method), new { arguments = new { } });

            // The comparison type: widening only, whatever the side order.
            var root = snapshot.Root;
            foreach (var (left, right, expected) in new[] {
                ("global::System.Double", "global::System.Int32", "global::System.Double"), ("global::System.Int32", "global::System.Double", "global::System.Double"),
                ("number", "global::System.Int64", "global::System.Double"), ("global::System.Int32", "global::System.Int64", "global::System.Int64"),
                ("global::System.Decimal", "global::System.Double", "global::System.Decimal"), ("global::System.UInt32", "global::System.Int32", "global::System.Int64"),
                ("global::System.UInt64", "global::System.Int32", "global::System.Decimal"), ("global::System.Int32?", "global::System.Int64", "global::System.Int64?") })
                Check(VisualGenerator.ComparisonType(root, left, right) == expected, $"{left} and {right} compare as {expected}, got {VisualGenerator.ComparisonType(root, left, right)}");
            Check(VisualGenerator.ComparisonType(root, "string", "global::System.Int32") == null, "only numbers are widened");

            // Compare, both side orders, with eq, gt and lt.
            async Task Compares(JsonObject left, JsonObject right, string op, bool expected, string what)
            {
                var graph = Graph([left, right, N("cmp", "compare", new { left = Unset(), right = Unset(), @operator = op }), N("i", "if", new { value = Unset() }), Say("yes", "true"), Say("no", "false")],
                    E("t", "i"), E("i", "yes", "true"), E("i", "no", "false"), D(left["id"]!.GetValue<string>(), "cmp", "left", Port(left)), D(right["id"]!.GetValue<string>(), "cmp", "right", Port(right)), D("cmp", "i", "value"));
                var logged = await Logged(graph, snapshot);
                Check(logged.SequenceEqual([expected ? "true" : "false"]), $"{what}: " + string.Join(" | ", logged));
            }
            static string Port(JsonObject node) => node["kind"]!.GetValue<string>() == "constant" ? "value" : "result";
            JsonObject Number(string id, double value) => N(id, "constant", new { type = "number", value = Lit("number", value) });
            // Less than and greater than take a Number on the left (both validators), so those orders are Number first.
            await Compares(Number("a", 2.5), Value("b", "Two"), "gt", true, "number 2.5 > int 2");
            await Compares(Number("a", 2.5), Value("b", "Two"), "lt", false, "number 2.5 < int 2 is false");
            await Compares(Number("a", 1.5), Value("b", "Two"), "lt", true, "number 1.5 < int 2");
            await Compares(Number("a", 2.5), Value("b", "Two"), "eq", false, "number 2.5 = int 2 is false");
            await Compares(Value("a", "Two"), Number("b", 2.5), "eq", false, "int 2 = number 2.5 is false");
            await Compares(Value("a", "Two"), Number("b", 2), "eq", true, "int 2 = number 2");
            await Compares(Value("a", "Half"), Value("b", "Two"), "eq", false, "double 2.5 = int 2 is false");
            await Compares(Value("a", "Two"), Value("b", "Half"), "eq", false, "int 2 = double 2.5 is false");
            await Compares(Number("a", 5000000000), Value("b", "Big"), "eq", true, "number 5e9 = long 5e9");
            await Compares(Number("a", 5000000001), Value("b", "Big"), "gt", true, "number 5e9+1 > long 5e9");
            await Compares(Number("a", 2), Value("b", "Big"), "lt", true, "number 2 < long 5e9");
            await Compares(Value("a", "Big"), Value("b", "Two"), "eq", false, "long 5e9 = int 2 is false (no overflow)");
            await Compares(Value("a", "Two"), Value("b", "Big"), "eq", false, "int 2 = long 5e9 is false (no overflow)");
            await Compares(Value("a", "Money"), Value("b", "Half"), "eq", true, "decimal 2.5 = double 2.5");
            await Compares(Value("a", "Half"), Value("b", "Money"), "eq", true, "double 2.5 = decimal 2.5");
            await Compares(Number("a", 3), Value("b", "Money"), "gt", true, "number 3 > decimal 2.5");
            await Compares(Number("a", 2.5), Value("b", "Money"), "gt", false, "number 2.5 > decimal 2.5 is false");
            await Compares(Value("a", "Large"), Value("b", "Minus"), "eq", false, "uint 4e9 = int -1 is false");
            await Compares(Value("a", "Minus"), Value("b", "Huge"), "eq", false, "int -1 = ulong max is false");
            await Compares(Value("a", "Exact"), Value("b", "Near"), "eq", false, "two longs past 2^53 compare exactly");
            await Compares(Value("a", "Maybe"), Value("b", "Big"), "eq", false, "int? 3 = long 5e9 is false");
            await Compares(Value("a", "Maybe"), Number("b", 3), "eq", true, "int? 3 = number 3");

            // The trace shows the values as they came, not widened.
            {
                var graph = Graph([Number("a", 2.5), Value("b", "Two"), N("cmp", "compare", new { left = Unset(), right = Unset(), @operator = "gt" }), N("i", "if", new { value = Unset() })],
                    E("t", "i"), D("a", "cmp", "left"), D("b", "cmp", "right", "result"), D("cmp", "i", "value"));
                var derived = Derived(graph, snapshot);
                var preview = new VisualGenerator(Diagnostics).Preview(derived, snapshot);
                Check(preview.Valid, "traced compare compiles: " + JsonSerializer.Serialize(preview.Diagnostics));
                var compiled = await new CodeCompiler(Diagnostics).CompileSingleFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cs"), preview.Source);
                var sink = new Sink(); var prior = InvocationContext.Current;
                try
                {
                    var instance = (Automation)Activator.CreateInstance(compiled.Assembly.GetType("VisualAutomation")!)!;
                    InvocationContext.Current = new InvocationContext("run-compare", InvocationCause.Manual, null, TriggerPayload.Manual()) { TraceSink = sink };
                    Task task;
                    try { task = (Task)instance.GetType().GetMethod("Run_74")!.Invoke(instance, null)!; }
                    finally { InvocationContext.Current = prior; }
                    await task.WaitAsync(TimeSpan.FromSeconds(10));
                }
                finally { compiled.Unload(); }
                var compare = JsonNode.Parse(sink.Last!)!["events"]!.AsArray().Single(e => e!["node"]!.GetValue<string>() == "cmp")!;
                Check(JsonNode.DeepEquals(compare["inputs"]!["left"]!["v"], JsonValue.Create(2.5)) && JsonNode.DeepEquals(compare["inputs"]!["right"]!["v"], JsonValue.Create(2)),
                    "the trace shows the compared values as given: " + compare.ToJsonString());
            }

            // A narrowing wire into a loop's kept or exported value: checked, naming the loop and the value.
            JsonObject Declaring(string group) => N("r", "repeat", new JsonObject { ["count"] = Lit("number", 1), [group] = new JsonArray(new JsonObject { ["name"] = "total", ["type"] = "global::System.Int32", ["initial"] = Lit("global::System.Int32", 1) }) });
            var port = "export:total";
            var exported = Graph([Declaring("exports"), N("res", "loop_result", new { values = new { } }), Value("v", "Four"), Reader("after")],
                E("t", "r"), E("r", "res", "body"), E("r", "after"), D("v", "res", port, "result"), D("r", "after", "value", port));
            Check((await Logged(exported, snapshot)).SequenceEqual(["4"]), "a whole double goes into an exported whole number");
            var fraction = (JsonObject)exported.DeepClone(); fraction["nodes"]![3]!["kind"] = Kind("Half");
            await Fails(fraction, snapshot, "Repeat · Total needs a whole number from -2147483648 to 2147483647, not 2.5", "a fractional export update fails, naming the loop and the value");
            var carried = Graph([Declaring("carry"), N("res", "loop_result", new { values = new { } }), Value("v", "Half")],
                E("t", "r"), E("r", "res", "body"), D("v", "res", "carry:total", "result"));
            await Fails(carried, snapshot, "Repeat · Total needs a whole number from -2147483648 to 2147483647, not 2.5", "a fractional carry update fails, naming the loop and the value");

            // Foreach items, from a CLR array and from a JSON array.
            JsonObject Each(JsonObject items) => N("f", "foreach", new JsonObject { ["items"] = items, ["itemType"] = "global::System.Int32" });
            var each = Graph([Each(Unset()), Value("v", "Wholes"), N("count", Kind("Count"), new { arguments = new { } })],
                E("t", "f"), E("f", "count", "body"), D("v", "f", "items", "result"), D("f", "count", "value", "item"));
            Check((await Logged(each, snapshot)).SequenceEqual(["count 1", "count 2"]), "whole doubles go through a foreach of whole numbers");
            var fractional = (JsonObject)each.DeepClone(); fractional["nodes"]![2]!["kind"] = Kind("Fractions");
            await Fails(fractional, snapshot, "For each · Items, item 2 needs a whole number from -2147483648 to 2147483647, not 2.5", "a fractional foreach item fails, naming the step, Items and the item");
            var json = Graph([Each(Lit("array", new JsonArray(1, 2.5))), N("count", Kind("Count"), new { arguments = new { } })],
                E("t", "f"), E("f", "count", "body"), D("f", "count", "value", "item"));
            await Fails(json, snapshot, "For each · Items, item 2 needs a whole number from -2147483648 to 2147483647, not 2.5", "a fractional JSON foreach item fails alike");

            // Convert into a whole-number type.
            var converted = Graph([Value("v", "Half"), N("c", "convert", new { type = "global::System.Int32", value = Unset() }), N("count", Kind("Count"), new { arguments = new { } })],
                E("t", "count"), D("v", "c", "value", "result"), D("c", "count", "value"));
            await Fails(converted, snapshot, "Convert (used by Count): Convert · Value needs a whole number from -2147483648 to 2147483647, not 2.5", "Convert does not round a fraction into a whole number");
            Console.WriteLine("PASS number wiring: Compare widens both sides (both orders, eq/gt/lt, overflow, decimal, unsigned, exact longs) and traces values as given; loop values, foreach items, and Convert check the fit");
        }
        finally { HassPath.HassConfiguration = previousPath; }
    }
}
