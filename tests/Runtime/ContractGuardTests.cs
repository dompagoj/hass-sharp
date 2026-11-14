using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>
/// Crafted type entries and ids cannot crash the process or make the validators disagree: the type label rule ends on
/// a cycle and on very deep ids (a stack overflow cannot be caught, and under Python.NET it ends Home Assistant), and
/// every anchored pattern ends at the end of the text, not before a final newline.
/// </summary>
static class ContractGuardTests
{
    [RuntimeSuite("--contract-guards", Order = 15)]
    public static Task Run()
    {
        var catalog = VisualNodeCatalog.GetCatalog();
        var types = catalog["types"]!.AsObject();
        types["X"] = new JsonObject { ["elementType"] = "X", ["description"] = "" };
        types["A"] = new JsonObject { ["elementType"] = "B", ["description"] = "" };
        types["B"] = new JsonObject { ["elementType"] = "A", ["description"] = "" };
        string Label(string id) => VisualGraphValidator.Words.TypeLabel(catalog, id);
        // A type that names itself as its element, directly or through another, reads as an object at the repeat.
        Check(Label("X") == "List of Object", "a self-referencing element type: " + Label("X"));
        Check(Label("A") == "List of List of Object" && Label("B") == "List of List of Object", "two element types naming each other: " + Label("A"));
        // A long chain of distinct element types, and very long ids, are worded only so deep (as typeLabels.ts does).
        for (var i = 0; i < 20000; i++) types["T" + i] = new JsonObject { ["elementType"] = "T" + (i + 1), ["description"] = "" };
        var deep = Label("T0");
        Check(deep == string.Concat(Enumerable.Repeat("List of ", 16)) + "Object", "a chain of 20,000 element types: " + deep[..Math.Min(200, deep.Length)]);
        Check(Label("int" + new string('?', 200000)) == "Object" && Label("string" + string.Concat(Enumerable.Repeat("[]", 100000))).EndsWith("List of Object", StringComparison.Ordinal)
            && Label(string.Concat(Enumerable.Repeat("System.Collections.Generic.List<", 50000)) + "int" + new string('>', 50000)).EndsWith("Object", StringComparison.Ordinal), "very long type ids are worded without overflowing");
        // The rules of typeLabels.ts: a tuple is an object, a multi-dimensional array a list, a CLR spelling never shows.
        Check(Label("(int A, double B)") == "Object" && Label("global::(int, int)") == "Object" && Label("global::System.Int32[,]") == "List" && Label("global::System.Int32[,]?") == "List",
            $"tuple and multi-dimensional array labels: {Label("(int A, double B)")}, {Label("global::System.Int32[,]")}");
        types["global::NodeLib.Tuple"] = new JsonObject { ["label"] = "(int A, double B)", ["description"] = "" };
        Check(Label("global::NodeLib.Tuple") == "Tuple", "a label written with brackets or a comma is not shown: " + Label("global::NodeLib.Tuple"));
        // A service whose name is only its id is not named twice ("Turn on (light.turn_on)"); a name with brackets is kept.
        string Service(string name) => VisualGraphValidator.Words.ServiceName(new JsonObject { ["name"] = name }, "light.turn_on");
        Check(Service("light.turn_on") == "Turn on" && Service("turn_on") == "Turn on" && Service("Turn on (beta)") == "Turn on (beta)", "service names: " + Service("light.turn_on") + ", " + Service("Turn on (beta)"));
        // The request that reached it: a preview (as save, websocket and MCP make it) of a Custom C# node whose code does
        // not compile, so its stored signature is validated as it is, with an output of such a type wired into a number.
        var signature = new JsonObject
        {
            ["title"] = "Make", ["description"] = "", ["pure"] = false, ["inputs"] = new JsonObject(),
            ["outputs"] = new JsonObject { ["result"] = new JsonObject { ["type"] = "X", ["label"] = "Result", ["description"] = "" } },
            ["types"] = new JsonObject { ["X"] = new JsonObject { ["elementType"] = "X", ["label"] = "X", ["description"] = "", ["category"] = "Object", ["nullable"] = false, ["literalKind"] = null } },
        };
        var graph = G(N("make", "custom", new JsonObject { ["source"] = "this does not compile", ["signature"] = signature, ["arguments"] = new JsonObject() }),
            N("d", "delay", new { seconds = new { kind = "unset" } }));
        graph["edges"]!.AsArray().Add(D("make", "d", "seconds", "result"));
        var preview = new VisualGenerator(Diagnostics).Preview(graph);
        Check(!preview.Valid && preview.Diagnostics.Any(d => d.Message == "Seconds accepts Number, not List of Object"), "a preview with a self-referencing type is answered: " + string.Join("; ", preview.Diagnostics.Select(d => d.Message)));
        // Code whose pin types nest deeply is answered too: refused (more than 8 levels), before any conversion is
        // classified. The depths are kept small because Roslyn's own binding grows fast with depth (2,000 array ranks
        // take minutes): a slow answer, not a crash.
        foreach (var type in new[] { "int" + string.Concat(Enumerable.Repeat("[]", 64)), string.Concat(Enumerable.Repeat("System.Collections.Generic.List<", 12)) + "int" + new string('>', 12) })
        {
            var code = G(N("deep", "custom", new JsonObject { ["source"] = "public static class CustomNode { [Node(\"Deep\")] public static void Run(" + type + " value) { } }", ["arguments"] = new JsonObject() }));
            var compiled = new VisualGenerator(Diagnostics).Preview(Derived(code));
            Check(compiled.Diagnostics.Any(d => d.Message == NodeLibraryCompiler.TooDeepMessage), "a deeply nested pin type is refused: " + string.Join("; ", compiled.Diagnostics.Select(d => d.Message).Take(3)));
        }
        Console.WriteLine("PASS contract guards: type labels end on cycles, long element chains and very long ids; tuples and multi-dimensional arrays are worded as typeLabels.ts words them; a service name that is only its id is not written twice; a preview naming a self-referencing type is answered, and code with very deeply nested pin types is refused");
        return Task.CompletedTask;
    }
}
