using HassSharp;
using System.Text.Json.Nodes;
using static VisualExecutionTests;

/// <summary>
/// Value nodes (catalog <c>"pure": true</c>) have no place in the run order: each step works out the value nodes it
/// reads just before it runs, in its own scope. A Log step reading a value shows what the step saw.
/// </summary>
static class PureNodeTests
{
    static JsonObject Unset() => new() { ["kind"] = "unset" };
    static JsonObject Reader(string id) => N(id, "log", new { value = Unset() });
    static JsonObject Custom(string id, string code) => N(id, "custom", new { code });
    static JsonObject Graph(JsonObject[] nodes, params JsonObject[] edges)
    {
        var graph = G(nodes);
        graph["edges"] = new JsonArray(edges.ToArray<JsonNode>());
        return graph;
    }
    /// <summary>Runs the graph and returns what its Log steps wrote, in order.</summary>
    static async Task<List<string>> Logged(JsonObject graph, bool cancel = false)
    {
        var messages = new List<string>(); var previous = PyInterop.Log;
        PyInterop.Log = (level, message) => { lock (messages) messages.Add(message); };
        try { await Execute(graph, cancel: cancel); }
        finally { PyInterop.Log = previous; }
        return messages;
    }

    [RuntimeSuite("--pure-nodes", Order = 20)]
    public static async Task Run()
    {
        var previousEntity = PyInterop.Entity;
        var previousPath = HassPath.HassConfiguration; HassPath.HassConfiguration = Path.Combine(Path.GetTempPath(), "pure-nodes-" + Guid.NewGuid());
        try
        {
            // A Constant read by two steps: each works it out.
            {
                var graph = Graph([N("c", "constant", new { type = "number", value = Lit("number", 7.5) }), Reader("a"), Reader("b")],
                    E("t", "a"), E("a", "b"), D("c", "a", "value"), D("c", "b", "value"));
                var logged = await Logged(graph);
                Check(logged.Count(m => m == "7.5") == 2, "a Constant read by two steps: " + string.Join(" | ", logged));
            }
            // Constant → Compare → If: the If takes the branch the comparison gives.
            foreach (var (level, branch) in new[] { (3.0, "below"), (9.0, "above") })
            {
                var graph = Graph([N("c", "constant", new { type = "number", value = Lit("number", level) }), N("cmp", "compare", new { left = Unset(), right = Lit("number", 5), @operator = "lt" }),
                    N("i", "if", new { value = Unset() }), N("yes", "log", new { value = Lit("string", "below") }), N("no", "log", new { value = Lit("string", "above") })],
                    E("t", "i"), E("i", "yes", "true"), E("i", "no", "false"), D("c", "cmp", "left"), D("cmp", "i", "value"));
                var logged = await Logged(graph);
                Check(logged.Contains(branch) && !logged.Contains(branch == "below" ? "above" : "below"), $"Constant {level} → Compare → If: " + string.Join(" | ", logged));
            }
            // In a loop body, a value node reading the item gives each iteration's value.
            {
                var graph = Graph([N("f", "foreach", new { items = Lit("array", new[] { 1, 2, 3 }), itemType = "number" }), N("cmp", "compare", new { left = Unset(), right = Lit("number", 2), @operator = "eq" }),
                    N("n", "not", new { value = Unset() }), Reader("l")],
                    E("t", "f"), E("f", "l", "body"), D("f", "cmp", "left", "item"), D("cmp", "n", "value"), D("n", "l", "value"));
                var logged = (await Logged(graph)).Where(m => m is "True" or "False").ToArray();
                Check(logged.SequenceEqual(["True", "False", "True"]), "each iteration's item: " + string.Join(" | ", logged));
            }
            // A value node is worked out again after the step it reads runs again: a fresh reading each time.
            {
                var reads = 0; PyInterop.Entity = id => new() { EntityId = id, State = (++reads * 10).ToString() };
                var graph = Graph([N("r", "repeat", new { count = Lit("number", 3) }), N("read", "read_entity", new { entity = Lit("entity_id", "sensor.test") }),
                    N("state", "entity_field", new { entity = Unset(), field = "state", type = "number" }), Reader("l")],
                    E("t", "r"), E("r", "read", "body"), E("read", "l"), D("read", "state", "entity"), D("state", "l", "value"));
                var logged = (await Logged(graph)).Where(m => m is "10" or "20" or "30").ToArray();
                Check(logged.SequenceEqual(["10", "20", "30"]) && reads == 3, "re-evaluated after the step it reads ran again: " + string.Join(" | ", logged));
            }
            // A value node that fails is the step's failure: the step's error path is taken, and the message names both.
            {
                var graph = Graph([N("o", "object", new { value = Lit("object", new { present = 1 }) }), N("g", "get_field", new { value = Unset(), pointer = "/missing", type = "number" }), Reader("l"),
                    Custom("recover", "if(!lastError.Message.StartsWith(\"Get field (used by Log): \"))throw new Exception(\"wrong message: \"+lastError.Message);Logger.Info(\"recovered\");"),
                    N("after", "log", new { value = Lit("string", "went on") })],
                    E("t", "l"), E("l", "after"), E("l", "recover", "error"), D("o", "g", "value"), D("g", "l", "value"));
                var logged = await Logged(graph);
                Check(logged.Contains("recovered") && !logged.Contains("went on"), "a failing value node takes the step's error path: " + string.Join(" | ", logged));
                // Without an error wire the run fails, with the same message.
                graph["edges"]!.AsArray().RemoveAt(2); graph["nodes"]!.AsArray().RemoveAt(4);
                try { await Logged(graph); throw new Exception("the run did not fail"); }
                catch (InvalidOperationException error) { Check(error.Message.StartsWith("Get field (used by Log): "), "run failure message: " + error.Message); }
            }
            // Every value node worked out counts as a step against the 100000-step limit.
            {
                JsonObject Budget(bool pure)
                {
                    var graph = Graph([N("outer", "repeat", new { count = Lit("number", 200) }), N("inner", "repeat", new { count = Lit("number", 200) }),
                        N("c", "constant", new { type = "boolean", value = Lit("boolean", true) }), N("n", "not", new { value = Unset() }),
                        N("i", "if", new { value = pure ? Unset() : Lit("boolean", true) })],
                        E("t", "outer"), E("outer", "inner", "body"), E("inner", "i", "body"), D("c", "n", "value"));
                    if (pure) graph["edges"]!.AsArray().Add(D("n", "i", "value"));
                    else graph["nodes"]!.AsArray().Add(Reader("l"));
                    if (!pure) { graph["edges"]!.AsArray().Add(E("outer", "l")); graph["edges"]!.AsArray().Add(D("n", "l", "value")); }
                    return graph;
                }
                // 40,000 If steps alone stay well inside the limit; with two value nodes each, 120,000 steps do not.
                await Logged(Budget(false));
                try { await Logged(Budget(true)); throw new Exception("value nodes were not counted as steps"); }
                catch (InvalidOperationException error) { Check(error.Message.Contains("100000"), "step limit message: " + error.Message); }
            }
            Console.WriteLine("PASS value nodes: worked out per reading step, in loop bodies, again after their source reruns, failures on the reader, step limit");
        }
        finally { PyInterop.Entity = previousEntity; HassPath.HassConfiguration = previousPath; }
    }
}
