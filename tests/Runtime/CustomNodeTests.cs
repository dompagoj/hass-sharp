using HassSharp;
using System.Text.Json;
using System.Text.Json.Nodes;
using static VisualExecutionTests;

/// <summary>
/// [Node] methods as the one way to write C# (Task 25): the authoring contract a library and a Custom C# node share,
/// the Custom C# node's signature, and the removed parts of the old design.
/// </summary>
static class CustomNodeTests
{
    /// <summary>The catalog as the backend serves it with no libraries: the built-in nodes and every CLR type they use.</summary>
    static VisualCatalogSnapshot Empty => new NodeLibraryCompiler(Diagnostics).Preview([]).Snapshot;
    static JsonObject Unset() => new() { ["kind"] = "unset" };
    static JsonObject Say(string id, string text) => N(id, "log", new { value = Lit("string", text) });
    static JsonObject Reader(string id) => N(id, "log", new { value = Unset() });
    static JsonObject Custom(string id, string source) => N(id, "custom", new JsonObject { ["source"] = source, ["arguments"] = new JsonObject() });
    static JsonObject Graph(JsonObject[] nodes, params JsonObject[] edges)
    {
        var graph = G(nodes);
        graph["edges"] = new JsonArray(edges.ToArray<JsonNode>());
        return graph;
    }
    static NodeLibraryPreview Library(string source, string id = "contract") => new NodeLibraryCompiler(Diagnostics).Preview([new(id, source, 1)]);
    static CustomNodeDescriptor Compiled(string source, string id = "c", NodeLibrarySource[]? libraries = null) =>
        new NodeLibraryCompiler(Diagnostics).Preview(libraries ?? [], [new CustomNodeSource(id, source)]).Snapshot.Custom[id];
    static string Messages(IEnumerable<NodeLibraryDiagnostic> diagnostics) => string.Join(" | ", diagnostics.Select(d => $"{d.StartLine}:{d.StartColumn} {d.Message}"));
    static string Messages(IEnumerable<VisualDiagnostic> diagnostics) => JsonSerializer.Serialize(diagnostics);
    static async Task<List<string>> Logged(JsonObject graph, VisualCatalogSnapshot? snapshot = null)
    {
        var messages = new List<string>(); var previous = PyInterop.Log;
        PyInterop.Log = (_, message) => { lock (messages) messages.Add(message); };
        try { await Execute(graph, snapshot); }
        finally { PyInterop.Log = previous; }
        return messages;
    }

    [RuntimeSuite("--custom-nodes", Order = 21)]
    public static async Task Run()
    {
        var previousPath = HassPath.HassConfiguration; HassPath.HassConfiguration = Path.Combine(Path.GetTempPath(), "custom-nodes-" + Guid.NewGuid());
        var previousEntity = PyInterop.Entity;
        try
        {
            Template();
            Contract();
            await Numbers();
            await Context();
            await CustomNodes();
            await Signatures();
            await ErrorDetails();
            Removed();
            await Api();
        }
        finally { PyInterop.Entity = previousEntity; HassPath.HassConfiguration = previousPath; }
    }

    // The catalog's default Custom C# node is the template and the signature the compiler gives it, so a new node is valid at once.
    static void Template()
    {
        var config = VisualNodeCatalog.Root["nodes"]!["custom"]!["config"]!;
        var source = config["source"]!["default"]!.GetValue<string>();
        var stored = config["signature"]!["default"]!;
        var derived = Compiled(source);
        Check(derived.Valid && derived.Signature != null, "the template compiles: " + Messages(derived.Diagnostics));
        Check(VisualGenerator.SameJson(stored, derived.Signature), "the catalog's default signature is the template's; derived: " + derived.Signature!.ToJsonString());
        var node = VisualGraphContract.CreateNode("custom", "c", VisualCatalogSnapshot.BuiltIn);
        Check(node["label"] == null, "a new Custom C# node has no label of its own, so its card shows the method's title: " + node.ToJsonString());
        var graph = G(node);
        Check(VisualGraphValidator.Validate(graph, Empty).Count == 0 && new VisualGenerator(Diagnostics).Preview(graph).Valid,
            "a new Custom C# node is valid at once: " + Messages(VisualGraphValidator.Validate(graph, Empty)) + Messages(new VisualGenerator(Diagnostics).Preview(graph).Diagnostics));
        Console.WriteLine("PASS custom node template: the default signature is the template's, a new node is valid");
    }

    // The rules a [Node] method follows, in a library and in a Custom C# node alike.
    static void Contract()
    {
        var library = Library("""
            public enum Mode { Off, HeatPump, AutoAway }
            public static class Rooms
            {
                [Node("Average", Pure = true)]
                public static (double Average, double Max) Stats(double roomTemperature, double outsideTemperature) => ((roomTemperature + outsideTemperature) / 2, Math.Max(roomTemperature, outsideTemperature));
                [Node("Pick")]
                public static Mode Pick(Mode mode, CancellationToken cancellationToken, NodeContext context) => mode;
            }
            """);
        Check(library.Valid, "contract library compiles: " + Messages(library.Diagnostics));
        var nodes = library.Snapshot.Catalog["nodes"]!.AsObject();
        // Each part is escaped as before, so the colons in global:: stay apart from the separators; there are no parameter types.
        static string Kind(string method) => "library:contract:" + Uri.EscapeDataString("global::NodeLib.Rooms." + method);
        Check(nodes.ContainsKey(Kind("Stats")) && nodes.ContainsKey(Kind("Pick")),
            "kind is library:<sourceId>:<Type>.<Method>: " + string.Join(", ", nodes.Select(p => p.Key).Where(k => k.StartsWith("library:"))));
        var stats = nodes[Kind("Stats")]!;
        Check(stats["pure"]?.GetValue<bool>() == true && stats["execution"] == null, "a pure method is a value node without execution pins: " + stats.ToJsonString());
        Check(stats["inputs"]!["roomTemperature"]!["label"]!.GetValue<string>() == "Room temperature", "labels are put in words: " + stats["inputs"]!.ToJsonString());
        Check(stats["outputs"]!.AsObject().Select(p => p.Key).SequenceEqual(["average", "max"]) && stats["outputs"]!["max"]!["label"]!.GetValue<string>() == "Max",
            "a tuple with named elements gives one output each: " + stats["outputs"]!.ToJsonString());
        var pick = nodes[Kind("Pick")]!;
        Check(pick["inputs"]!.AsObject().Select(p => p.Key).SequenceEqual(["mode"]) && pick["execution"]!.AsArray().Select(e => e!.GetValue<string>()).SequenceEqual(["next", "error"]),
            "CancellationToken and NodeContext parameters are injected, not pins: " + pick.ToJsonString());
        var mode = library.Snapshot.Catalog["types"]!["global::NodeLib.Mode"]!;
        Check(mode["enumLabels"] is JsonArray labels && labels.Select(l => l!.GetValue<string>()).SequenceEqual(["Off", "Heat pump", "Auto away"]) && mode["enumValues"]!.AsArray().Count == 3,
            "an enum type lists its members in words, beside enumValues: " + mode.ToJsonString());

        void Refused(string source, string message)
        {
            var preview = Library(source, "refused");
            Check(!preview.Valid && preview.Diagnostics.Any(d => d.Message == message), $"expected \"{message}\": " + Messages(preview.Diagnostics));
        }
        Refused("public static class Bad { [Node(\"Nothing\", Pure = true)] public static void Nothing() { } }", "A value node (Pure = true) must return a value");
        Refused("public static class Bad { [Node(\"Pair\")] public static (double, double) Pair() => (1, 2); }", "Name each value the method returns, such as (double Average, double Max)");
        Refused("public static class Bad { [Node(\"Pair\")] public static (double Max, double max) Pair() => (1, 2); }", "Give each value the method returns its own name");
        Refused("public static class Bad { [Node(\"One\")] public static int Run(int value) => value; [Node(\"Two\")] public static string Run(string text) => text; }", "Give each node method its own name");

        // A Custom C# node: exactly one [Node] method, in code that keeps to its own namespace.
        void CustomRefused(string source, string message, int? line = null)
        {
            var custom = Compiled(source);
            Check(!custom.Valid && custom.Signature == null && custom.Diagnostics.Any(d => d.Message == message && (line == null || d.StartLine == line)), $"expected \"{message}\": " + Messages(custom.Diagnostics));
        }
        CustomRefused("public static class CustomNode { public static int Run() => 1; }", "Mark one method with [Node]: it becomes this node");
        CustomRefused("public static class CustomNode\n{\n    [Node(\"A\")] public static int A() => 1;\n    [Node(\"B\")] public static int B() => 2;\n}", "Mark only one method with [Node]; this code has 2", 4);
        CustomRefused("public static class CustomNode { [Node(\"A\")] public static int A() => 1; } } namespace Escaped { public static class X { }", "The code must stay inside the namespace it is placed in; check its braces");
        var usings = Compiled("using System.Text;\npublic static class CustomNode\n{\n    [Node(\"Join\")]\n    public static string Join(string first, string second) => new StringBuilder(first).Append(second).ToString();\n}");
        Check(usings.Valid && usings.Signature!["inputs"]!.AsObject().Select(p => p.Key).SequenceEqual(["first", "second"]), "using lines at the top of the code work: " + Messages(usings.Diagnostics));
        var signature = Compiled("public static class CustomNode\n{\n    /// <summary>Halves it.</summary>\n    [Node(\"Half\", Pure = true, Category = \"Ignored\")]\n    public static double Half(double value) => value / 2;\n}").Signature!;
        Check(signature.Select(p => p.Key).SequenceEqual(["title", "description", "pure", "inputs", "outputs", "types"]) && signature["title"]!.GetValue<string>() == "Half"
            && signature["description"]!.GetValue<string>() == "Halves it." && signature["pure"]!.GetValue<bool>() && signature["outputs"]!.AsObject().Select(p => p.Key).SequenceEqual(["result"]),
            "the signature is {title, description, pure, inputs, outputs, types}: " + signature.ToJsonString());
        Console.WriteLine("PASS node contract: kinds, pure value nodes, labels in words, tuple outputs, injected parameters, enum labels, one method per name, custom code rules");
    }

    // The built-in number reaches CLR numeric pins and back; a value the pin cannot hold fails the step, naming it and the pin.
    static async Task Numbers()
    {
        var library = Library("""
            public static class Counts
            {
                [Node("Count")]
                public static void Count([Port("How many")] int value) => Logger.Info("count " + value);
                [Node("Small")]
                public static byte Small() => 200;
            }
            """, "numbers");
        Check(library.Valid, "numbers library compiles: " + Messages(library.Diagnostics));
        var snapshot = library.Snapshot; string Kind(string method) => snapshot.Methods.Values.Single(m => m.MethodName == method).Kind;
        Check(snapshot.IsAssignable("number", "global::System.Int32") && snapshot.IsAssignable("global::System.Byte", "number") && !snapshot.IsAssignable("global::System.Nullable<global::System.Int32>", "number"),
            "number is assignable to and from CLR numbers");
        JsonObject Counting(double value) => Graph([N("c", "constant", new { type = "number", value = Lit("number", value) }), N("count", Kind("Count"), new { arguments = new { } })],
            E("t", "count"), D("c", "count", "value"));
        Check((await Logged(Counting(3), snapshot)).Contains("count 3"), "a whole number reaches an int pin");
        foreach (var (value, text) in new[] { (2.5, "2.5"), (3e10, "30000000000") })
        {
            try { await Logged(Counting(value), snapshot); throw new Exception("the step accepted " + value); }
            catch (InvalidOperationException error)
            {
                Check(error.Message == $"Count · How many needs a whole number from -2147483648 to 2147483647, not {text}", "the narrowing message names the step and pin: " + error.Message);
            }
        }
        var back = Graph([N("small", Kind("Small"), new { arguments = new { } }), N("cmp", "compare", new { left = Unset(), right = Lit("number", 200), @operator = "eq" }), N("i", "if", new { value = Unset() }), Say("yes", "equal")],
            E("t", "small"), E("small", "i"), E("i", "yes", "true"), D("small", "cmp", "left", "result"), D("cmp", "i", "value"));
        Check((await Logged(back, snapshot)).Contains("equal"), "a CLR number reaches a number pin");
        Console.WriteLine("PASS numbers: number to and from CLR numeric pins, narrowing failures name the step and pin");
    }

    // NodeContext: the run's token, id, trigger, failure, entity states, service calls and log.
    static async Task Context()
    {
        var calls = new List<(string Service, JsonObject Payload)>(); var previousCall = PyInterop.CallService;
        PyInterop.CallService = (domain, name, json, done, _) => { lock (calls) calls.Add((domain + "." + name, JsonNode.Parse(json!)!.AsObject())); done?.Invoke(); };
        PyInterop.Entity = id => new() { EntityId = id, State = "21" };
        var prior = InvocationContext.Current;
        try
        {
            InvocationContext.Current = new("run-7", InvocationCause.Manual, null, TriggerPayload.Manual());
            var graph = G(Custom("c", """
                public static class CustomNode
                {
                    [Node("Use the context")]
                    public static async Task Run(NodeContext context)
                    {
                        if (!context.CancellationToken.CanBeCanceled) throw new Exception("no token");
                        if (context.Error != null) throw new Exception("an error before any failure");
                        await context.CallService("light.turn_on", "light.hall", new System.Text.Json.Nodes.JsonObject { ["brightness_pct"] = 40 });
                        context.Log($"context {context.RunId} {context.Trigger.GetProperty("platform").GetString()} {context.State("sensor.test").State}");
                    }
                }
                """));
            var logged = await Logged(graph);
            Check(logged.Contains("context run-7 manual 21"), "NodeContext gives the run id, trigger and states: " + string.Join(" | ", logged));
            Check(calls.Count == 1 && calls[0].Service == "light.turn_on" && JsonNode.DeepEquals(calls[0].Payload, JsonNode.Parse("""{"brightness_pct":40,"entity_id":"light.hall"}""")), "NodeContext calls services: " + string.Join(",", calls.Select(c => c.Service + c.Payload.ToJsonString())));
        }
        finally { InvocationContext.Current = prior; PyInterop.CallService = previousCall; }
        Console.WriteLine("PASS node context: token, run id, trigger, error, states, service calls and log");
    }

    static async Task CustomNodes()
    {
        // Two nodes whose code declares the same class and method: each is its own.
        const string Same = "public static class CustomNode\n{\n    [Node(\"Same\")]\n    public static void Run() => Logger.Info(\"SAY\");\n}";
        var twins = G(Custom("one", Same.Replace("SAY", "first")), Custom("two", Same.Replace("SAY", "second")));
        Check((await Logged(twins)).SequenceEqual(["first", "second"]), "two custom nodes with the same class name each run their own code");

        // One step's two values (a tuple) feed two steps.
        var pair = Graph([Custom("pair", "public static class CustomNode\n{\n    [Node(\"Pair\")]\n    public static (string Greeting, double Count) Run() => (\"hello\", 3);\n}"), Reader("a"), Reader("b")],
            E("t", "pair"), E("pair", "a"), E("a", "b"), D("pair", "a", "value", "greeting"), D("pair", "b", "value", "count"));
        Check((await Logged(pair)).SequenceEqual(["hello", "3"]), "a tuple-returning custom node feeds two steps");

        // A pure custom node is a value node: no place in the run order, worked out for each reader.
        var twice = Custom("twice", "public static class CustomNode\n{\n    [Node(\"Twice\", Pure = true)]\n    public static double Run(double value) => value * 2;\n}");
        var pure = Graph([N("c", "constant", new { type = "number", value = Lit("number", 7.5) }), twice, Reader("a"), Reader("b")],
            E("t", "a"), E("a", "b"), D("c", "twice", "value"), D("twice", "a", "value", "result"), D("twice", "b", "value", "result"));
        Check((await Logged(pure)).Count(m => m == "15") == 2, "a pure custom node is worked out for each reader");
        var ordered = Derived(pure); ordered["edges"]!.AsArray().Add(E("b", "twice"));
        Check(new VisualGenerator(Diagnostics).Preview(ordered).Diagnostics.Any(d => d.NodeId == "twice" && d.Message == "Twice is a value and is not part of the run order"), "a pure custom node takes no step order wire");

        // A pure library method alike.
        var library = Library("public static class Maths { [Node(\"Half\", Pure = true)] public static double Half(double value) => value / 2; }", "maths");
        var half = library.Snapshot.Methods.Values.Single().Kind;
        var halved = Graph([N("c", "constant", new { type = "number", value = Lit("number", 8) }), N("h", half, new { arguments = new { } }), Reader("a")], E("t", "a"), D("c", "h", "value"), D("h", "a", "value", "result"));
        Check((await Logged(halved, library.Snapshot)).Contains("4"), "a pure library method is a value node");

        // A record declared in the code is the type of its pin: the validators know it from the signature's types.
        const string Readings = "public sealed record Reading(string Name, double Value);\npublic static class CustomNode\n{\n    [Node(\"Make\")]\n    public static Reading Run() => new(\"kitchen\", 21);\n}";
        var record = Graph([Custom("make", Readings), Custom("show", "public static class CustomNode\n{\n    [Node(\"Show\")]\n    public static void Run(NodeLib.Custom_make.Reading reading) => Logger.Info(reading.Name + \" \" + reading.Value);\n}")],
            E("t", "make"), E("make", "show"), D("make", "show", "reading", "result"));
        var derived = Derived(record);
        var type = derived["nodes"]![1]!["config"]!["signature"]!["outputs"]!["result"]!["type"]!.GetValue<string>();
        Check(type == "global::NodeLib.Custom_make.Reading" && derived["nodes"]![1]!["config"]!["signature"]!["types"]![type] is JsonObject, "a record declared in the code is in the signature's types: " + derived["nodes"]![1]!["config"]!.ToJsonString());
        Check((await Logged(record)).Contains("kitchen 21"), "a record declared in a custom node's code travels on a wire");
        var mismatch = Graph([Custom("make", Readings), Custom("text", "public static class CustomNode\n{\n    [Node(\"Text\")]\n    public static void Run(string text) { }\n}")],
            E("t", "make"), E("make", "text"), D("make", "text", "text", "result"));
        var refused = VisualGraphValidator.Validate(Derived(mismatch), Empty);
        Check(refused.Any(d => d.NodeId == "text" && d.InputId == "text"), "the validator compares a custom type by the signature's types: " + Messages(refused));
        Console.WriteLine("PASS custom nodes: own namespaces, tuple outputs, pure custom and library nodes, records declared in code");
    }

    // The stored signature is what the backend checks against the code; deriveSignatures writes it.
    static async Task Signatures()
    {
        var graph = Derived(G(Code("c", "Logger.Info(text);", "string text")));
        var node = graph["nodes"]![1]!.AsObject();
        node["config"]!["arguments"] = new JsonObject { ["text"] = Lit("string", "hi") };
        Check(new VisualGenerator(Diagnostics).Preview(graph).Valid, "a derived graph is valid");
        // Code changed without its pins being applied.
        var stale = (JsonObject)graph.DeepClone(); stale["nodes"]![1]!["config"]!["source"] = CodeSource("Logger.Info(words);", "string words");
        var diagnostics = new VisualGenerator(Diagnostics).Preview(stale).Diagnostics;
        Check(diagnostics.Any(d => d.NodeId == "c" && d.Code == "custom.signature" && d.InputId == "source" && d.Message == "The pins are out of date with the code: open Edit code and apply it"), "a stale signature is an error: " + Messages(diagnostics));
        // The same signature written in another key order and with 1.0 for 1 is not stale.
        var reordered = (JsonObject)graph.DeepClone(); var stored = reordered["nodes"]![1]!["config"]!["signature"]!.AsObject();
        reordered["nodes"]![1]!["config"]!["signature"] = new JsonObject(stored.Reverse().Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
        Check(new VisualGenerator(Diagnostics).Preview(reordered).Valid, "a signature in another key order is the same signature");
        // A signature the editor cannot read.
        var malformed = (JsonObject)graph.DeepClone(); malformed["nodes"]![1]!["config"]!["signature"] = new JsonObject { ["title"] = "c" };
        var broken = VisualGraphValidator.Validate(malformed, Empty);
        Check(broken.Any(d => d.NodeId == "c" && d.Code == "custom.signature" && d.InputId == "source" && d.Message == "The pins of this node cannot be read; open Edit code and apply it"), "a malformed signature is an error: " + Messages(broken));
        // deriveSignatures prunes values and wires of pins that are gone, and a node that became a value node leaves the run order.
        var wired = G(N("c1", "constant", new { type = "string", value = Lit("string", "x") }), Code("c", "Logger.Info(text);", "string text"), Say("after", "after"));
        wired["edges"]!.AsArray().Add(D("c1", "c", "text"));
        var first = Derived(wired);
        first["nodes"]![2]!["config"]!["source"] = "public static class CustomNode\n{\n    [Node(\"Now pure\", Pure = true)]\n    public static double Run(double amount) => amount;\n}";
        first["nodes"]![2]!["config"]!["arguments"] = new JsonObject { ["text"] = Lit("string", "gone") };
        var second = Derived(first);
        Check(second["nodes"]![2]!["config"]!["arguments"]!.AsObject().Count == 0 && second["edges"]!.AsArray().All(e => e!["source"]!.GetValue<string>() != "c" && e["target"]!.GetValue<string>() != "c")
            && second["nodes"]![2]!["config"]!["signature"]!["pure"]!.GetValue<bool>(), "deriveSignatures prunes what the code no longer has: " + second.ToJsonString());
        // A node whose code does not compile keeps its signature; the compile error is on the node, at its own line and column.
        var bad = (JsonObject)graph.DeepClone(); bad["nodes"]![1]!["config"]!["source"] = "public static class CustomNode\n{\n    [Node(\"Bad\")]\n    public static void Run() { undefined(); }\n}";
        Check(JsonNode.DeepEquals(Derived(bad)["nodes"]![1]!["config"]!["signature"], graph["nodes"]![1]!["config"]!["signature"]), "code that does not compile keeps its signature");
        var errors = new VisualGenerator(Diagnostics).Preview(bad).Diagnostics;
        Check(errors.Any(d => d.NodeId == "c" && d.InputId == "source" && d.StartLine == 4 && d.StartColumn == 32 && d.Message.Contains("undefined")), "a compile error is on the node at its own position: " + Messages(errors));
        await Task.CompletedTask;
        Console.WriteLine("PASS custom signatures: stale and malformed signatures, key order, deriveSignatures pruning, compile errors at the node's own position");
    }

    // Error details is a value node: empty before a failure, the failure and its step after one.
    static async Task ErrorDetails()
    {
        var graph = Graph([N("e", "last_error"), Reader("before"), Code("x", "throw new InvalidOperationException(\"boom\");"), Reader("after"), Reader("step")],
            E("t", "before"), E("before", "x"), E("x", "after", "error"), E("after", "step"), D("e", "before", "value", "message"), D("e", "after", "value", "message"), D("e", "step", "value", "step"));
        var logged = await Logged(graph);
        Check(logged.SequenceEqual(["", "boom", "Check"]), "Error details before and after a failure: " + string.Join(" | ", logged.Select(m => "[" + m + "]")));
        Console.WriteLine("PASS error details: empty before a failure, the message and step after one");
    }

    // Graphs that still use a removed part are refused with what to do instead.
    static void Removed()
    {
        void Refused(JsonObject graph, string message, string? nodeId, string? inputId)
        {
            var diagnostics = VisualGraphValidator.Validate(graph, Empty);
            Check(diagnostics.Any(d => d.Message == message && d.NodeId == nodeId && d.InputId == inputId), $"expected \"{message}\" on {nodeId}/{inputId}: " + Messages(diagnostics));
        }
        var imports = G(Say("l", "x")); imports["imports"] = "using System.Text;";
        Refused(imports, "Usings are no longer part of an automation; put using lines at the top of a Custom C# node's code", null, null);
        var members = G(Say("l", "x")); members["members"] = "int count;";
        Refused(members, "Helper members are no longer part of an automation; write them in a Custom C# node's code or a node library", null, null);
        Refused(G(N("c", "custom", new { code = "Logger.Info(\"old\");" })), "Custom C# code is now one static method marked [Node]; rewrite this node's code in that form", "c", "source");
        Refused(G(N("r", "read_entity", new { entity = Lit("entity_id", "sensor.test"), output = "reading" })), "Steps no longer expose values to C# by name; connect a wire from this step to each step that uses its value", "r", "output");
        Refused(G(N("l", "log", new { value = new { kind = "variable", name = "reading" } })), "Message reads a variable, and variables are gone; connect a wire to it instead", "l", "value");
        var entities = G(); entities["nodes"]![0]!["config"] = new JsonObject { ["type"] = "entity", ["entity"] = "sensor.test", ["entitiesExpression"] = "[Entities.Sensor.Test]" };
        Refused(entities, "Entities expression is gone; choose the entity in Entity", "t", "entitiesExpression");
        var schedules = G(); schedules["nodes"]![0]!["config"] = new JsonObject { ["type"] = "daily", ["time"] = "07:30", ["timeZone"] = "UTC", ["schedulesExpression"] = "[]" };
        Refused(schedules, "Schedules expression is gone; set the time in Time", "t", "schedulesExpression");
        Console.WriteLine("PASS removed parts: usings, helper members, statement code, exposed values, variables and raw trigger expressions are refused with what to do instead");
    }

    // custom_preview, deriveSignatures on preview and save, and the language services on a Custom C# node's code.
    static async Task Api()
    {
        var path = Path.Combine(HassPath.HassConfiguration, "api-" + Guid.NewGuid()); var previous = HassPath.HassConfiguration; HassPath.HassConfiguration = path;
        var manager = new HassSharpManager(path);
        try
        {
            await manager.Init([]);
            async Task<JsonNode> Library(object request) => JsonNode.Parse(await manager.NodeLibraryRequest(JsonSerializer.Serialize(request)))!;
            async Task<JsonNode> Visual(object request) => JsonNode.Parse(await manager.VisualRequest(JsonSerializer.Serialize(request)))!;
            Check((await Library(new { operation = "save", id = "helpers", name = "Helpers", expectedRevision = 0, source = "public static class Helper { public static int Twice(int value) => value * 2; }" }))["ok"]!.GetValue<bool>(), "helper library saves");
            const string source = "public static class CustomNode\n{\n    [Node(\"Twice\")]\n    public static int Run(int value) => Helper.Twice(value);\n}";
            var preview = await Library(new { operation = "custom_preview", source });
            Check(preview["ok"]!.GetValue<bool>() && preview["result"]!["valid"]!.GetValue<bool>() && preview["result"]!["signature"]!["title"]!.GetValue<string>() == "Twice"
                && preview["result"]!["diagnostics"]!.AsArray().Count == 0, "custom_preview gives the signature, compiled with the saved libraries: " + preview.ToJsonString());
            var failing = await Library(new { operation = "custom_preview", source = source.Replace("Helper.Twice(value)", "Missing(value)") });
            Check(failing["ok"]!.GetValue<bool>() && !failing["result"]!["valid"]!.GetValue<bool>() && failing["result"]!["signature"] == null
                && failing["result"]!["diagnostics"]!.AsArray().Any(d => d!["startLine"]!.GetValue<int>() == 4), "custom_preview reports errors at the code's own lines: " + failing.ToJsonString());
            var position = source.IndexOf("Helper.") + "Helper.".Length;
            var completion = await Library(new { operation = "completion", custom = true, source, position, workspaceVersion = "1" });
            Check(completion["ok"]!.GetValue<bool>() && completion["result"]!["data"]!.AsArray().Any(i => i!["displayText"]!.GetValue<string>() == "Twice"), "completion works on a custom node's code: " + completion.ToJsonString()[..Math.Min(600, completion.ToJsonString().Length)]);

            var graph = G(Code("c", "Logger.Info(text);", "string text"));
            graph["nodes"]![1]!["config"]!["arguments"] = new JsonObject { ["text"] = Lit("string", "hi") };
            var plain = await Visual(new { operation = "preview", graph });
            Check(!plain["result"]!["valid"]!.GetValue<bool>(), "without deriveSignatures a node without a signature is refused: " + plain.ToJsonString());
            var derived = await Visual(new { operation = "preview", graph, deriveSignatures = true });
            Check(derived["result"]!["valid"]!.GetValue<bool>() && derived["result"]!["graph"]!["nodes"]![1]!["config"]!["signature"]!["inputs"]!["text"] != null, "preview with deriveSignatures returns the graph with its pins: " + derived.ToJsonString()[..Math.Min(800, derived.ToJsonString().Length)]);
            var saved = await Visual(new { operation = "save", id = "derived", graph, expectedRevision = 0, deriveSignatures = true });
            Check(saved["ok"]!.GetValue<bool>() && saved["result"]!["document"]!["graph"]!["nodes"]![1]!["config"]!["signature"]!["title"]!.GetValue<string>() == "Check", "save with deriveSignatures stores the pins: " + saved.ToJsonString()[..Math.Min(800, saved.ToJsonString().Length)]);
            Console.WriteLine("PASS custom node API: custom_preview, language services on custom code, deriveSignatures on preview and save");
        }
        finally { await manager.UnloadAsync(); HassPath.HassConfiguration = previous; try { Directory.Delete(path, true); } catch { } }
    }
}
