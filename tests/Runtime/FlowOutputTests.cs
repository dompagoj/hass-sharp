using HassSharp;
using System.Text.Json;
using System.Text.Json.Nodes;
using static VisualExecutionTests;

/// <summary>
/// Flow outputs: a [Node] method's Action and Func&lt;…, Task&gt; parameters marked [Port(..., FlowControl = true)]. Calling
/// one runs the steps on that flow there and then, in a scope of their own, and returns when they end; the run goes on
/// at Done once the method returns.
/// </summary>
static class FlowOutputTests
{
    const string Source = """
        public static class Flows
        {
            [Node("Classify temperature", Category = "Climate")]
            public static void Classify(
                [Port("Cold", FlowControl = true)] Action<double> cold,
                [Port("Comfortable", FlowControl = true)] Action ok,
                [Port("Hot", FlowControl = true)] Action<double> hot,
                double temperature, double low = 19, double high = 24)
            {
                if (temperature < low) cold(low - temperature);
                else if (temperature > high) hot(temperature - high);
                else ok();
            }

            [Node("Each light")]
            public static async Task EachLight([Port("Body", FlowControl = true)] Func<(string Light, double Index), Task> body, double count)
            {
                for (var i = 0; i < count; i++) await body(("light." + i, i));
            }

            [Node("If")]
            public static void Choose([Port("Yes", FlowControl = true)] Action @true, [Port("No", FlowControl = true)] Action @false, bool condition)
            {
                if (condition) @true(); else @false();
            }

            [Node("Together")]
            public static Task Together([Port("Each", FlowControl = true)] Func<double, Task> each, double count) =>
                Task.WhenAll(Enumerable.Range(0, (int)count).Select(i => each(i)));

            [Node("Guarded")]
            public static async Task Guarded([Port("Body", FlowControl = true)] Func<Task> body, bool swallow)
            {
                try { await body(); }
                catch (Exception error) when (swallow) { Logger.Info("caught " + error.Message); }
            }

            [Node("Many")]
            public static void Many([Port("Body", FlowControl = true)] Action body, double count)
            {
                for (var i = 0; i < count; i++) body();
            }

            static Action? kept;
            [Node("Keep")]
            public static void Keep([Port("Later", FlowControl = true)] Action later) => kept = later;
            [Node("Late")]
            public static void Late() => kept!();

            [Node("Timeout")]
            public static async Task Timeout([Port("Body", FlowControl = true)] Func<Task> body, double seconds) =>
                await Task.WhenAny(body(), Task.Delay(TimeSpan.FromSeconds(seconds)));

            [Node("Share")]
            public static async Task Share([Port("Each", FlowControl = true)] Func<List<string>, Task> each)
            {
                var items = new List<string>();
                await Task.WhenAll(each(items), each(items));
                Logger.Info("method " + items.Count);
            }

            [Node("Add item")]
            public static double AddItem(List<string> items) { items.Add("x"); return items.Count; }

            [Node("Average with flow")]
            public static double Measured([Port("Each", FlowControl = true)] Action<double> each, double value) { each(value); return value * 2; }
        }
        """;

    static NodeLibraryPreview Library(string source, string id = "flows") => new NodeLibraryCompiler(Diagnostics).Preview([new(id, source, 1)]);
    static string Messages(IEnumerable<NodeLibraryDiagnostic> diagnostics) => string.Join(" | ", diagnostics.Select(d => $"{d.StartLine}:{d.StartColumn} {d.Message}"));
    static string Messages(IEnumerable<VisualDiagnostic> diagnostics) => JsonSerializer.Serialize(diagnostics);
    static JsonObject Unset() => new() { ["kind"] = "unset" };
    static JsonObject Say(string id, string text) => N(id, "log", new { value = Lit("string", text) });
    static JsonObject Reader(string id) => N(id, "log", new { value = Unset() });
    static JsonObject Graph(JsonObject[] nodes, params JsonObject[] edges)
    {
        var graph = G(nodes);
        graph["edges"] = new JsonArray(edges.ToArray<JsonNode>());
        return graph;
    }
    static string[] Strings(JsonNode? array) => (array as JsonArray ?? []).Select(v => v!.GetValue<string>()).ToArray();
    static async Task<List<string>> Logged(JsonObject graph, VisualCatalogSnapshot snapshot)
    {
        var messages = new List<string>(); var previous = PyInterop.Log;
        PyInterop.Log = (_, message) => { lock (messages) messages.Add(message); };
        try { await Execute(graph, snapshot); }
        finally { PyInterop.Log = previous; }
        return messages;
    }

    [RuntimeSuite("--flow-outputs", Order = 22)]
    public static async Task Run()
    {
        var previousPath = HassPath.HassConfiguration; HassPath.HassConfiguration = Path.Combine(Path.GetTempPath(), "flow-outputs-" + Guid.NewGuid());
        try
        {
            var library = Library(Source);
            Check(library.Valid, "the flow library compiles: " + Messages(library.Diagnostics));
            Spec(library.Snapshot);
            Refusals();
            await Branches(library.Snapshot);
            await Loops(library.Snapshot);
            await Failures(library.Snapshot);
            Scopes(library.Snapshot);
            await CustomNode();
        }
        finally { HassPath.HassConfiguration = previousPath; }
    }

    static string Kind(VisualCatalogSnapshot snapshot, string method) => snapshot.Methods.Values.Single(m => m.MethodName == method).Kind;

    // The catalog spec: one execution port per flow (a scope each), then Done and On error; flows are never input pins.
    static void Spec(VisualCatalogSnapshot snapshot)
    {
        var classify = snapshot.Catalog["nodes"]![Kind(snapshot, "Classify")]!;
        Check(Strings(classify["execution"]).SequenceEqual(["cold", "ok", "hot", "next", "error"]), "flows, then Done and On error: " + classify.ToJsonString());
        Check(Strings(classify["scopePorts"]).SequenceEqual(["cold", "ok", "hot"]), "each flow is a scope: " + classify.ToJsonString());
        Check(JsonNode.DeepEquals(classify["executionLabels"], JsonNode.Parse("""{"cold":"Cold","ok":"Comfortable","hot":"Hot","next":"Done"}""")), "flow labels: " + classify["executionLabels"]?.ToJsonString());
        Check(classify["inputs"]!.AsObject().Select(p => p.Key).SequenceEqual(["temperature", "low", "high"]), "flows are not input pins: " + classify["inputs"]!.ToJsonString());
        var outputs = classify["outputs"]!.AsObject();
        Check(outputs.Select(p => p.Key).SequenceEqual(["cold", "hot"]) && outputs["hot"]!["scope"]!.GetValue<string>() == "hot" && outputs["hot"]!["label"]!.GetValue<string>() == "Hot"
            && outputs["hot"]!["type"]!.GetValue<string>() == "global::System.Double", "an Action<T> flow carries one value, read only on that flow: " + outputs.ToJsonString());
        var each = snapshot.Catalog["nodes"]![Kind(snapshot, "EachLight")]!;
        Check(each["outputs"]!.AsObject().Select(p => p.Key).SequenceEqual(["light", "index"]) && each["outputs"]!.AsObject().All(p => p.Value!["scope"]!.GetValue<string>() == "body")
            && each["outputs"]!["light"]!["label"]!.GetValue<string>() == "Light", "a named tuple gives one value per element: " + each["outputs"]!.ToJsonString());
        var choose = snapshot.Catalog["nodes"]![Kind(snapshot, "Choose")]!;
        Check(Strings(choose["execution"]).SequenceEqual(["true", "false", "next", "error"]), "@true and @false are the flows true and false: " + choose.ToJsonString());
        var measured = snapshot.Catalog["nodes"]![Kind(snapshot, "Measured")]!;
        Check(measured["outputs"]!.AsObject().Select(p => p.Key).SequenceEqual(["each", "result"]) && measured["outputs"]!["result"]!["scope"] == null, "a returned value sits beside a flow's value: " + measured["outputs"]!.ToJsonString());
        var plain = Library("public static class Plain { [Node(\"Plain\")] public static void Run() { } }", "plain").Snapshot.Catalog["nodes"]!.AsObject().Single(p => p.Key.StartsWith("library:plain:")).Value!;
        Check(Strings(plain["execution"]).SequenceEqual(["next", "error"]) && plain["scopePorts"] == null && plain["executionLabels"] == null, "a node without flows is as before: " + plain.ToJsonString());
        Console.WriteLine("PASS flow spec: flows are scopes with labels, Done and On error follow, values are scoped, plain nodes unchanged");
    }

    static void Refusals()
    {
        void Refused(string parameters, string message, string body = "")
        {
            var preview = Library("public static class Bad { [Node(\"Bad\")] public static void Run(" + parameters + ") { " + body + " } }", "refused");
            Check(!preview.Valid && preview.Diagnostics.Any(d => d.Message == message), $"expected \"{message}\" for ({parameters}): " + Messages(preview.Diagnostics));
        }
        // An unmarked Action stays an input pin, as before flows: the compiler only warns, at the parameter.
        foreach (var parameter in new[] { "Action body", "Func<double, Task> body" })
        {
            var warned = Library("public static class Warned\n{\n    [Node(\"Warned\")]\n    public static void Run(" + parameter + ") { }\n}", "warned");
            var warning = warned.Diagnostics.SingleOrDefault(d => d.Message == NodeLibraryCompiler.FlowUnmarkedMessage);
            var spec = warned.Snapshot.Catalog["nodes"]!.AsObject().Single(p => p.Key.StartsWith("library:warned:")).Value!;
            Check(warned.Valid && warning is { Severity: 2, StartLine: 4 } && warning.StartColumn == "    public static void Run(".Length + parameter.IndexOf("body") + 1
                && spec["inputs"]!["body"] != null && Strings(spec["execution"]).SequenceEqual(["next", "error"]), $"an unmarked {parameter} warns and stays an input pin: " + Messages(warned.Diagnostics));
        }
        Refused("[Port(\"Body\", FlowControl = true)] double body", NodeLibraryCompiler.FlowTypeMessage);
        Refused("[Port(\"Body\", FlowControl = true)] Func<double> body", NodeLibraryCompiler.FlowTypeMessage);
        Refused("[Port(\"Body\", FlowControl = true)] Action<double, double> body", NodeLibraryCompiler.FlowTypeMessage);
        Refused("[Port(\"Body\", FlowControl = true)] Action? body", NodeLibraryCompiler.FlowTypeMessage);
        Refused("[Port(\"Body\", FlowControl = true)] Action body = null!", NodeLibraryCompiler.FlowTypeMessage);
        Refused("[Port(\"Body\", FlowControl = true)] Action<(double, double)> body", NodeLibraryCompiler.FlowTupleMessage);
        Refused("[Port(\"Body\", FlowControl = true)] Action next", NodeLibraryCompiler.FlowNameMessage("next"));
        Refused("[Port(\"Body\", FlowControl = true)] Action error", NodeLibraryCompiler.FlowNameMessage("error"));
        Refused("[Port(\"Body\", FlowControl = true)] Action condition", NodeLibraryCompiler.FlowNameMessage("condition"));
        Refused("[Port(\"Body\", FlowControl = true)] Action širina", NodeLibraryCompiler.PinNameMessage("flow", "širina"));
        Refused("[Port(\"A\", FlowControl = true)] Action<double> value, [Port(\"B\", FlowControl = true)] Action<(double Value, double Other)> other", NodeLibraryCompiler.FlowDuplicateMessage);
        var pure = Library("public static class Bad { [Node(\"Bad\", Pure = true)] public static double Run([Port(\"Body\", FlowControl = true)] Action body) => 1; }", "refused");
        Check(!pure.Valid && pure.Diagnostics.Any(d => d.Message == NodeLibraryCompiler.FlowPureMessage), "a value node has no flows: " + Messages(pure.Diagnostics));
        var located = Library("public static class Bad\n{\n    [Node(\"Bad\")]\n    public static void Run([Port(\"Body\", FlowControl = true)] double body) { }\n}", "refused").Diagnostics.Single(d => d.Message == NodeLibraryCompiler.FlowTypeMessage);
        Check(located.StartLine == 4 && located.StartColumn == "    public static void Run([Port(\"Body\", FlowControl = true)] double ".Length + 1, "the problem is at the parameter: " + Messages([located]));
        Console.WriteLine("PASS flow refusals: unmarked delegates warn and stay pins; other types, nullable and default flows, unnamed tuples, taken and foreign names, duplicates, value nodes");
    }

    static async Task Branches(VisualCatalogSnapshot snapshot)
    {
        // Each flow runs its own steps with its value, then the run goes on at Done.
        async Task<List<string>> Classified(double temperature)
        {
            var graph = Graph([N("c", Kind(snapshot, "Classify"), new { arguments = new { temperature = Lit("number", temperature) } }), Reader("cold"), Say("ok", "comfortable"), Reader("hot"), Say("done", "done")],
                E("t", "c"), E("c", "cold", "cold"), E("c", "ok", "ok"), E("c", "hot", "hot"), E("c", "done"), D("c", "cold", "value", "cold"), D("c", "hot", "value", "hot"));
            return await Logged(graph, snapshot);
        }
        Check((await Classified(25.5)).SequenceEqual(["1.5", "done"]), "Hot runs with its value, then Done");
        Check((await Classified(17)).SequenceEqual(["2", "done"]), "Cold runs with its value, then Done");
        Check((await Classified(21)).SequenceEqual(["comfortable", "done"]), "Comfortable runs, then Done");
        // A flow with nothing wired runs nothing; the run still goes on at Done.
        var unwired = Graph([N("c", Kind(snapshot, "Classify"), new { arguments = new { temperature = Lit("number", 30) } }), Say("done", "done")], E("t", "c"), E("c", "done"));
        Check((await Logged(unwired, snapshot)).SequenceEqual(["done"]), "an unwired flow runs nothing");
        // A flow's steps can be more than one, and the method's returned value is set too.
        var measured = Graph([N("m", Kind(snapshot, "Measured"), new { arguments = new { value = Lit("number", 4) } }), Reader("a"), Say("b", "second"), Reader("after")],
            E("t", "m"), E("m", "a", "each"), E("a", "b"), E("m", "after"), D("m", "a", "value", "each"), D("m", "after", "value", "result"));
        Check((await Logged(measured, snapshot)).SequenceEqual(["4", "second", "8"]), "a chain on a flow, then the returned value after Done");
        Console.WriteLine("PASS flow branches: each flow with its value, then Done; an unwired flow; a chain on a flow and a returned value");
    }

    static async Task Loops(VisualCatalogSnapshot snapshot)
    {
        // A flow called once per item: each call is its own scope with its own values.
        var each = Graph([N("e", Kind(snapshot, "EachLight"), new { arguments = new { count = Lit("number", 3) } }), Reader("light"), Reader("index"), Say("done", "done")],
            E("t", "e"), E("e", "light", "body"), E("light", "index"), E("e", "done"), D("e", "light", "value", "light"), D("e", "index", "value", "index"));
        Check((await Logged(each, snapshot)).SequenceEqual(["light.0", "0", "light.1", "1", "light.2", "2", "done"]), "a body called three times, in order, then Done");
        // Calls at the same time each get a scope of their own.
        var together = Graph([N("g", Kind(snapshot, "Together"), new { arguments = new { count = Lit("number", 3) } }), N("wait", "delay", new { seconds = Lit("number", 0.02) }), Reader("each"), Say("done", "done")],
            E("t", "g"), E("g", "wait", "each"), E("wait", "each"), E("g", "done"), D("g", "each", "value", "each"));
        var concurrent = await Logged(together, snapshot);
        Check(concurrent.Take(3).Order().SequenceEqual(["0", "1", "2"]) && concurrent[^1] == "done", "concurrent calls keep their own values: " + string.Join(",", concurrent));
        // A sync Action waits for its steps, a delay among them.
        var many = Graph([N("m", Kind(snapshot, "Many"), new { arguments = new { count = Lit("number", 2) } }), N("wait", "delay", new { seconds = Lit("number", 0.01) }), Say("tick", "tick"), Say("done", "done")],
            E("t", "m"), E("m", "wait", "body"), E("wait", "tick"), E("m", "done"));
        Check((await Logged(many, snapshot)).SequenceEqual(["tick", "tick", "done"]), "a sync Action runs its steps, a delay among them, before it returns");
        // Each call gets its own copy of the value it carries: a step changing it changes neither a sibling's nor the method's.
        var share = Graph([N("s", Kind(snapshot, "Share"), new { arguments = new { } }), N("add", Kind(snapshot, "AddItem"), new { arguments = new { } }), Reader("count")],
            E("t", "s"), E("s", "add", "each"), E("add", "count"), D("s", "add", "items", "each"), D("add", "count", "value", "result"));
        var shared = await Logged(share, snapshot);
        Check(shared.SequenceEqual(["1", "1", "method 0"]), "each call has its own copy of the value: " + string.Join(",", shared));
        // A call the method does not wait for ends with its step: cancelled, never running on after Done.
        var timeout = Graph([N("o", Kind(snapshot, "Timeout"), new { arguments = new { seconds = Lit("number", 0.05) } }), N("wait", "delay", new { seconds = Lit("number", 0.3) }), Say("late", "late"), Say("done", "done")],
            E("t", "o"), E("o", "wait", "body"), E("wait", "late"), E("o", "done"));
        var orphaned = new List<string>(); var previous = PyInterop.Log;
        PyInterop.Log = (_, message) => { lock (orphaned) orphaned.Add(message); };
        try { await Execute(timeout, snapshot); await Task.Delay(500); }
        finally { PyInterop.Log = previous; }
        Check(orphaned.SequenceEqual(["done"]), "a flow the method left running is cancelled when the step ends: " + string.Join(",", orphaned));
        Console.WriteLine("PASS flow loops: a body per item, concurrent calls, a sync Action with a delay, a copy per call, no call outliving its step");
    }

    static async Task Failures(VisualCatalogSnapshot snapshot)
    {
        JsonObject Failing(string method, object arguments, bool recover) => recover
            ? Graph([N("g", Kind(snapshot, method), new { arguments }), N("x", "get_field", new { value = Lit("object", new { a = 1 }), pointer = "/missing", type = "number" }), Reader("bad"), Say("done", "done"), Say("recovered", "recovered")],
                E("t", "g"), E("g", "bad", "body"), E("g", "done"), E("g", "recovered", "error"), D("x", "bad", "value"))
            : Graph([N("g", Kind(snapshot, method), new { arguments }), N("x", "get_field", new { value = Lit("object", new { a = 1 }), pointer = "/missing", type = "number" }), Reader("bad"), Say("done", "done")],
                E("t", "g"), E("g", "bad", "body"), E("g", "done"), D("x", "bad", "value"));
        // A failure on a flow is thrown out of the call: the method may catch it and the run goes on at Done.
        var caught = await Logged(Failing("Guarded", new { swallow = Lit("boolean", true) }, false), snapshot);
        Check(caught.Count == 2 && caught[0].StartsWith("caught ") && caught[1] == "done", "the method catches a failure on its flow: " + string.Join(" | ", caught));
        // Uncaught, it fails the node: its On error flow runs, Done does not.
        Check((await Logged(Failing("Guarded", new { swallow = Lit("boolean", false) }, true), snapshot)).SequenceEqual(["recovered"]), "an uncaught failure takes On error");
        try { await Logged(Failing("Guarded", new { swallow = Lit("boolean", false) }, false), snapshot); throw new Exception("the run did not fail"); }
        catch (InvalidOperationException error) { Check(error.Message.StartsWith("Get field (used by Log): "), "the failure is the inner step's: " + error.Message); }
        // A flow called after its step has finished fails the step that calls it.
        var late = Graph([N("k", Kind(snapshot, "Keep"), new { arguments = new { } }), N("l", Kind(snapshot, "Late"), new { arguments = new { } }), Say("never", "never"), N("why", "log", new { value = Unset() }), N("message", "last_error")],
            E("t", "k"), E("k", "never", "later"), E("k", "l"), E("l", "why", "error"), D("message", "why", "value", "message"));
        var lateLog = await Logged(late, snapshot);
        Check(lateLog.Count == 1 && lateLog[0] == "Keep: Later was called after the step finished", "a call after the step finished: " + string.Join(" | ", lateLog));
        // At most 1000 calls per step.
        var many = Graph([N("m", Kind(snapshot, "Many"), new { arguments = new { count = Lit("number", 1001) } }), N("why", "log", new { value = Unset() }), N("message", "last_error")],
            E("t", "m"), E("m", "why", "error"), D("message", "why", "value", "message"));
        var limited = await Logged(many, snapshot);
        Check(limited.SequenceEqual(["Many called its flows more than 1000 times"]), "the call limit: " + string.Join(" | ", limited));
        // A Stop step on a flow stops the whole run.
        var stopped = Graph([N("m", Kind(snapshot, "Many"), new { arguments = new { count = Lit("number", 3) } }), Say("tick", "tick"), N("s", "stop"), Say("done", "done")],
            E("t", "m"), E("m", "tick", "body"), E("tick", "s"), E("m", "done"));
        var stoppedLog = new List<string>(); var previous = PyInterop.Log;
        PyInterop.Log = (_, message) => { lock (stoppedLog) stoppedLog.Add(message); };
        try { await Execute(stopped, snapshot); } catch (OperationCanceledException) { } finally { PyInterop.Log = previous; }
        Check(stoppedLog.SequenceEqual(["tick"]), "Stop on a flow stops the run: " + string.Join(" | ", stoppedLog));
        Console.WriteLine("PASS flow failures: caught by the method, uncaught to On error, a late call, the call limit, Stop");
    }

    // A flow's values are read on that flow only: not on another flow, not after Done.
    static void Scopes(VisualCatalogSnapshot snapshot)
    {
        JsonObject Reading(string port, string valuePort, bool throughValueNode = false) => throughValueNode
            ? Graph([N("c", Kind(snapshot, "Classify"), new { arguments = new { temperature = Lit("number", 30) } }), N("n", "compare", new { left = Unset(), right = Lit("number", 1), @operator = "gt" }), Reader("r")],
                E("t", "c"), port == "next" ? E("c", "r") : E("c", "r", port), D("c", "n", "left", valuePort), D("n", "r", "value"))
            : Graph([N("c", Kind(snapshot, "Classify"), new { arguments = new { temperature = Lit("number", 30) } }), Reader("r")],
                E("t", "c"), port == "next" ? E("c", "r") : E("c", "r", port), D("c", "r", "value", valuePort));
        foreach (var through in new[] { false, true })
        {
            Check(VisualGraphValidator.Validate(Reading("hot", "hot", through), snapshot).Count == 0, "Hot's value on Hot: " + Messages(VisualGraphValidator.Validate(Reading("hot", "hot", through), snapshot)));
            Check(VisualGraphValidator.Validate(Reading("cold", "hot", through), snapshot).Any(d => d.NodeId == "r" && d.Code == "scope.data"), "Hot's value on Cold: " + Messages(VisualGraphValidator.Validate(Reading("cold", "hot", through), snapshot)));
            Check(VisualGraphValidator.Validate(Reading("next", "hot", through), snapshot).Any(d => d.NodeId == "r" && d.Code == "scope.data"), "Hot's value after Done: " + Messages(VisualGraphValidator.Validate(Reading("next", "hot", through), snapshot)));
        }
        // For each's item, now a scoped value like a flow's, is read in Body only, as before.
        var item = Graph([N("f", "foreach", new { items = Lit("array", new[] { 1 }), itemType = "number" }), Reader("in"), Reader("after")], E("t", "f"), E("f", "in", "body"), E("f", "after"), D("f", "in", "value", "item"), D("f", "after", "value", "item"));
        var diagnostics = VisualGraphValidator.Validate(item, snapshot);
        Check(diagnostics.Count == 1 && diagnostics[0].NodeId == "after" && diagnostics[0].Code == "scope.data", "For each's item in Body only: " + Messages(diagnostics));
        Check(VisualNodeCatalog.Root["nodes"]!["foreach"]!["outputs"]!["item"]!["scope"]!.GetValue<string>() == "body", "For each's item is scoped to Body as a flow value is");
        Console.WriteLine("PASS flow scopes: a value on its flow only, also through a value node; For each's item alike");
    }

    // A Custom C# node with flows: the same contract, its ports from its signature.
    static async Task CustomNode()
    {
        const string Code = """
            public static class CustomNode
            {
                [Node("Twice")]
                public static async Task Run([Port("Body", FlowControl = true)] Func<double, Task> body)
                {
                    await body(1);
                    await body(2);
                }
            }
            """;
        var graph = Graph([N("c", "custom", new JsonObject { ["source"] = Code, ["arguments"] = new JsonObject() }), Reader("r"), Say("done", "done")],
            E("t", "c"), E("c", "r", "body"), E("c", "done"), D("c", "r", "value", "body"));
        var empty = new NodeLibraryCompiler(Diagnostics).Preview([]).Snapshot;
        var derived = Derived(graph, empty);
        var signature = derived["nodes"]![1]!["config"]!["signature"]!;
        Check(Strings(signature["execution"]).SequenceEqual(["body", "next", "error"]) && Strings(signature["scopePorts"]).SequenceEqual(["body"]), "a Custom C# node's flows are in its signature: " + signature.ToJsonString());
        Check(VisualNodeCatalog.ExecutionPorts(derived["nodes"]![1]!.AsObject(), empty).SequenceEqual(["body", "next", "error"]), "its execution ports come from the signature");
        Check(VisualGraphValidator.Validate(derived, empty).Count == 0, "a Custom C# node with a flow is valid: " + Messages(VisualGraphValidator.Validate(derived, empty)));
        Check((await Logged(graph, empty)).SequenceEqual(["1", "2", "done"]), "a Custom C# node's flow runs");
        Console.WriteLine("PASS flow custom node: flows in the signature, valid, run");
    }
}
