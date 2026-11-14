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
        // The static catalog (the editor's until the server's arrives) knows the CLR types the compiler always adds, as the
        // compiler describes them, so the template is valid against it too.
        var builtIn = VisualCatalogSnapshot.BuiltIn.Catalog["types"]!.AsObject(); var served = Empty.Catalog["types"]!.AsObject();
        foreach (var (id, entry) in builtIn)
        {
            Check(served[id] is JsonObject, "the compiler writes " + id);
            var listed = served[id]!["assignableTo"]!.AsArray().Select(t => t!.GetValue<string>()).Where(builtIn.ContainsKey).Order(StringComparer.Ordinal);
            if (entry!["refines"] == null)
                Check(entry["assignableTo"]!.AsArray().Select(t => t!.GetValue<string>()).Order(StringComparer.Ordinal).SequenceEqual(listed), $"the static {id} converts to what the compiler says, among the static types: " + entry.ToJsonString());
            if (id.StartsWith("global::", StringComparison.Ordinal))
                foreach (var (key, value) in entry.AsObject().Where(p => p.Key != "assignableTo"))
                    Check(JsonNode.DeepEquals(value, served[id]![key]), $"the static {id}.{key} is the compiler's: {value?.ToJsonString()} vs {served[id]![key]?.ToJsonString()}");
        }
        foreach (var id in new[] { "global::System.Boolean", "global::System.Double", "global::System.String", "global::System.Text.Json.Nodes.JsonObject", "global::System.Text.Json.Nodes.JsonArray" })
            Check(builtIn.ContainsKey(id), "the static catalog has " + id);
        var statically = VisualGraphValidator.Validate(graph, VisualCatalogSnapshot.BuiltIn);
        Check(statically.Count == 0, "a new Custom C# node is valid against the static catalog: " + Messages(statically));
        Console.WriteLine("PASS custom node template: the default signature is the template's, a new node is valid, also against the static catalog");
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

        // After a failure that a step's error pin recovered, NodeContext.Error is that failure.
        var recovered = Graph([Custom("x", "public static class CustomNode\n{\n    [Node(\"Fail\")]\n    public static void Run() => throw new InvalidOperationException(\"boom\");\n}"),
                Custom("c", "public static class CustomNode\n{\n    [Node(\"Report\")]\n    public static void Run(NodeContext context) => context.Log(\"error \" + context.Error?.Message);\n}")],
            E("t", "x"), E("x", "c", "error"));
        Check((await Logged(recovered)).SequenceEqual(["error boom"]), "NodeContext.Error is the recovered failure");

        // CallService with returnResponse gives the service's response.
        var previousResponse = PyInterop.CallServiceWithResponse; string? asked = null;
        PyInterop.CallServiceWithResponse = (domain, name, json, done, _) => { asked = domain + "." + name + " " + json; done("""{"weather.home":{"forecast":[{"temperature":3},{"temperature":5}]}}"""); };
        try
        {
            var response = G(Custom("c", """
                public static class CustomNode
                {
                    [Node("Forecast")]
                    public static async Task Run(NodeContext context)
                    {
                        var response = await context.CallService("weather.get_forecasts", "weather.home", new System.Text.Json.Nodes.JsonObject { ["type"] = "daily" }, returnResponse: true);
                        context.Log("days " + response!.Value.GetProperty("weather.home").GetProperty("forecast").GetArrayLength());
                    }
                }
                """));
            var answered = await Logged(response); Check(answered.Contains("days 2"), "CallService with returnResponse gives the response: " + string.Join(" | ", answered) + " " + asked);
            Check(asked != null && asked.StartsWith("weather.get_forecasts ") && JsonNode.DeepEquals(JsonNode.Parse(asked["weather.get_forecasts ".Length..]), JsonNode.Parse("""{"type":"daily","entity_id":"weather.home"}""")), "the response call carries the target and data: " + asked);
        }
        finally { PyInterop.CallServiceWithResponse = previousResponse; }

        // Stopping the run cancels NodeContext.CancellationToken.
        var waiting = G(Custom("c", """
            public static class CustomNode
            {
                [Node("Wait")]
                public static async Task Run(NodeContext context)
                {
                    try { await Task.Delay(10000, context.CancellationToken); }
                    catch (OperationCanceledException) { context.Log("cancelled"); throw; }
                }
            }
            """));
        var cancelled = new List<string>(); var previousLog = PyInterop.Log;
        PyInterop.Log = (_, message) => { lock (cancelled) cancelled.Add(message); };
        try { await Execute(waiting, cancel: true); }
        finally { PyInterop.Log = previousLog; }
        Check(cancelled.SequenceEqual(["cancelled"]), "NodeContext.CancellationToken is cancelled when the run stops: " + string.Join(" | ", cancelled));
        Console.WriteLine("PASS node context: token, run id, trigger, error, states, service calls and log, a recovered error, a service response, cancellation");
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
        static string Takes(string parameter, string body) => "public static class CustomNode\n{\n    [Node(\"Show\")]\n    public static void Run(" + parameter + ") => Logger.Info(" + body + ");\n}";
        var record = Graph([Custom("make", Readings), Custom("show", Takes("object reading", "reading.ToString()"))],
            E("t", "make"), E("make", "show"), D("make", "show", "reading", "result"));
        var derived = Derived(record);
        var type = derived["nodes"]![1]!["config"]!["signature"]!["outputs"]!["result"]!["type"]!.GetValue<string>();
        Check(type == "global::NodeLib.Custom_make.Reading" && derived["nodes"]![1]!["config"]!["signature"]!["types"]![type] is JsonObject, "a record declared in the code is in the signature's types: " + derived["nodes"]![1]!["config"]!.ToJsonString());
        Check((await Logged(record)).Contains("Reading { Name = kitchen, Value = 21 }"), "a record declared in a custom node's code travels on a wire into an object pin");
        var mismatch = Graph([Custom("make", Readings), Custom("text", "public static class CustomNode\n{\n    [Node(\"Text\")]\n    public static void Run(string text) { }\n}")],
            E("t", "make"), E("make", "text"), D("make", "text", "text", "result"));
        var refused = VisualGraphValidator.Validate(Derived(mismatch), Empty);
        Check(refused.Any(d => d.NodeId == "text" && d.InputId == "text"), "the validator compares a custom type by the signature's types: " + Messages(refused));
        // A type one Custom C# node declares is its own: another node's code cannot name it.
        var foreign = Derived(Graph([Custom("make", Readings), Custom("show", Takes("NodeLib.Custom_make.Reading reading", "reading.Name"))], E("t", "make"), E("make", "show"), D("make", "show", "reading", "result")));
        var privacy = new VisualGenerator(Diagnostics).Preview(foreign).Diagnostics;
        Check(privacy.Any(d => d.NodeId == "show" && d.InputId == "source" && d.Message == "NodeLib.Custom_make.Reading belongs to the Custom C# node make; declare the type in this code or in a node library to use it here"),
            "a type another Custom C# node declares is refused: " + Messages(privacy));
        await Conversions();
        Console.WriteLine("PASS custom nodes: own namespaces, tuple outputs, pure custom and library nodes, records declared in code");
    }

    // Wires C# accepts between Custom C# pins and library pins: numbers both ways, a declared type into what it implements,
    // a catalog or library type into a custom pin it converts to. Each value arrives.
    static async Task Conversions()
    {
        var library = Library("""
            public interface IReading { string Name { get; } }
            public static class Readings
            {
                [Node("Count")]
                public static int Count() => 7;
                [Node("Show reading")]
                public static void Show(IReading reading) => Logger.Info("reading " + reading.Name);
                [Node("Names")]
                public static List<string> Names() => ["a", "b"];
                [Node("Fraction")]
                public static double Fraction() => 2.5;
                [Node("Whole")]
                public static void Whole([Port("Count")] int value) => Logger.Info("whole " + value);
            }
            """, "readings");
        Check(library.Valid, "readings library compiles: " + Messages(library.Diagnostics));
        var snapshot = library.Snapshot; string Kind(string method) => snapshot.Methods.Values.Single(m => m.MethodName == method).Kind;
        static string Make(string returns, string value) => "public static class CustomNode\n{\n    [Node(\"Make\")]\n    public static " + returns + " Run() => " + value + ";\n}";
        static string Take(string parameter) => "public static class CustomNode\n{\n    [Node(\"Take\")]\n    public static void Run([Port(\"Amount\")] " + parameter + " value) => Logger.Info(\"took \" + ((object?)value is System.Collections.IEnumerable e and not string ? string.Join(\",\", e.Cast<object>()) : value!.ToString()));\n}";
        async Task Arrives(JsonObject source, string parameter, string expected, string what, string port = "result")
        {
            var graph = Graph([source, Custom("take", Take(parameter))], E("t", source["id"]!.GetValue<string>()), E(source["id"]!.GetValue<string>(), "take"), D(source["id"]!.GetValue<string>(), "take", "value", port));
            var derived = Derived(graph, snapshot);
            var diagnostics = new VisualGenerator(Diagnostics).Preview(derived, snapshot).Diagnostics;
            Check(diagnostics.Count == 0, what + " is accepted: " + Messages(diagnostics));
            var logged = await Logged(graph, snapshot);
            Check(logged.Contains("took " + expected), what + " arrives: " + string.Join(" | ", logged));
        }
        await Arrives(N("count", Kind("Count"), new { arguments = new { } }), "long", "7", "a library int into a custom long");
        await Arrives(Custom("make", Make("int", "5")), "long", "5", "a custom int into a custom long");
        await Arrives(Custom("make", Make("int", "5")), "double", "5", "a custom int into a custom double");
        await Arrives(Custom("make", Make("double", "4")), "int", "4", "a custom double into a custom int");
        await Arrives(Custom("make", Make("long", "6")), "double?", "6", "a custom long into a custom double?");
        await Arrives(Custom("make", Make("List<string>", "[\"x\", \"y\"]")), "IEnumerable<string>", "x,y", "a custom List<string> into a custom IEnumerable<string>");
        await Arrives(N("names", Kind("Names"), new { arguments = new { } }), "IEnumerable<string>", "a,b", "a library List<string> into a custom IEnumerable<string>");
        await Arrives(Custom("make", Make("string", "\"text\"")), "object", "text", "a custom string into a custom object");
        // A nullable number is not taken by a pin that cannot hold null.
        var nullable = Derived(Graph([Custom("make", Make("int?", "null")), Custom("take", Take("long"))], E("t", "make"), E("make", "take"), D("make", "take", "value", "result")), snapshot);
        Check(VisualGraphValidator.Validate(nullable, snapshot).Any(d => d.NodeId == "take" && d.InputId == "value"), "a custom int? is refused by a custom long");
        // Narrower at run time: the fit check names the step and pin.
        try { await Logged(Graph([Custom("make", Make("double", "2.5")), Custom("take", Take("int"))], E("t", "make"), E("make", "take"), D("make", "take", "value", "result")), snapshot); throw new Exception("the step accepted 2.5"); }
        catch (InvalidOperationException error) { Check(error.Message == "Take · Amount needs a whole number from -2147483648 to 2147483647, not 2.5", "a custom double that does not fit a custom int fails, naming the step and pin: " + error.Message); }
        // Between two library steps alike.
        try { await Logged(Graph([N("f", Kind("Fraction"), new { arguments = new { } }), N("w", Kind("Whole"), new { arguments = new { } })], E("t", "f"), E("f", "w"), D("f", "w", "value", "result")), snapshot); throw new Exception("the library step accepted 2.5"); }
        catch (InvalidOperationException error) { Check(error.Message == "Whole · Count needs a whole number from -2147483648 to 2147483647, not 2.5", "a library double that does not fit a library int fails, naming the step and pin: " + error.Message); }
        // A record the code declares into the library interface it implements.
        var reading = Custom("make", "public sealed record Reading(string Name) : IReading;\npublic static class CustomNode\n{\n    [Node(\"Make\")]\n    public static Reading Run() => new(\"hall\");\n}");
        var implemented = Graph([reading, N("show", Kind("Show"), new { arguments = new { } })], E("t", "make"), E("make", "show"), D("make", "show", "reading", "result"));
        Check(VisualGraphValidator.Validate(Derived(implemented, snapshot), snapshot).Count == 0, "a custom record into the library interface it implements is accepted: " + Messages(VisualGraphValidator.Validate(Derived(implemented, snapshot), snapshot)));
        Check((await Logged(implemented, snapshot)).Contains("reading hall"), "a custom record arrives at a library interface pin");
        // And into another Custom C# node's pin of that interface, which only the library and the first node's types name.
        var both = Graph([reading.DeepClone().AsObject(), Custom("take", "public static class CustomNode\n{\n    [Node(\"Take\")]\n    public static void Run(IReading value) => Logger.Info(\"took \" + value.Name);\n}")],
            E("t", "make"), E("make", "take"), D("make", "take", "value", "result"));
        Check((await Logged(both, snapshot)).Contains("took hall"), "a custom record arrives at another custom node's interface pin");
        // A literal that cannot fit a whole-number pin is refused when validating.
        var literal = Derived(G(Custom("take", Take("int"))), snapshot);
        literal["nodes"]![1]!["config"]!["arguments"] = new JsonObject { ["value"] = Lit("number", 2.5) };
        var fit = VisualGraphValidator.Validate(literal, snapshot);
        Check(fit.Any(d => d.NodeId == "take" && d.InputId == "value" && d.Message == "Amount must be a whole number between -2147483648 and 2147483647"), "a fractional literal for an int pin is refused: " + Messages(fit));
        Console.WriteLine("PASS custom conversions: numbers both ways, collections, object, library interfaces, narrowing at run time, literal fit");
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
        var second = new VisualGenerator(Diagnostics).DeriveSignatures(first, Empty, out var removed);
        // One warning per value and wire removed, on the node, naming the pin.
        Check(removed.All(d => d.Severity == 2 && d.NodeId == "c") && removed.Select(d => d.Message).Order(StringComparer.Ordinal).SequenceEqual(new[] {
                "Removed a step order wire: this node is now a value node, which has no place in the run order",
                "Removed a step order wire: this node is now a value node, which has no place in the run order",
                "Removed the connection to Text: the code no longer has that input",
                "Removed the value of Text: the code no longer has that input" }.Order(StringComparer.Ordinal)),
            "deriveSignatures says what it removed: " + Messages(removed));
        Check(second["nodes"]![2]!["config"]!["arguments"]!.AsObject().Count == 0 && second["edges"]!.AsArray().All(e => e!["source"]!.GetValue<string>() != "c" && e["target"]!.GetValue<string>() != "c")
            && second["nodes"]![2]!["config"]!["signature"]!["pure"]!.GetValue<bool>(), "deriveSignatures prunes what the code no longer has: " + second.ToJsonString());
        // A node whose code does not compile keeps its signature; the compile error is on the node, at its own line and column.
        var bad = (JsonObject)graph.DeepClone(); bad["nodes"]![1]!["config"]!["source"] = "public static class CustomNode\n{\n    [Node(\"Bad\")]\n    public static void Run() { undefined(); }\n}";
        Check(JsonNode.DeepEquals(Derived(bad)["nodes"]![1]!["config"]!["signature"], graph["nodes"]![1]!["config"]!["signature"]), "code that does not compile keeps its signature");
        var errors = new VisualGenerator(Diagnostics).Preview(bad).Diagnostics;
        Check(errors.Any(d => d.NodeId == "c" && d.InputId == "source" && d.StartLine == 4 && d.StartColumn == 32 && d.Message.Contains("undefined")), "a compile error is on the node at its own position: " + Messages(errors));
        // Previews of a graph whose code is unchanged reuse the compile: the editor previews after every edit.
        var three = Derived(G(Code("a", "Logger.Info(text);", "string text"), Code("b", "Logger.Info(text);", "string text"), Code("c", "Logger.Info(text);", "string text")));
        foreach (var each in three["nodes"]!.AsArray().Skip(1)) each!["config"]!["arguments"] = new JsonObject { ["text"] = Lit("string", "one") };
        three["nodes"]![1]!["config"]!["source"] = three["nodes"]![1]!["config"]!["source"]!.GetValue<string>().Replace("Logger.Info(text);", "Logger.Info(text + \"!\");");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var initial = new VisualGenerator(Diagnostics).Preview(three);
        Check(initial.Valid, "three custom nodes preview");
        var cold = clock.Elapsed.TotalMilliseconds;
        three["nodes"]![2]!["config"]!["arguments"] = new JsonObject { ["text"] = Lit("string", "two") };
        clock.Restart();
        var again = new VisualGenerator(Diagnostics).Preview(three);
        var warm = clock.Elapsed.TotalMilliseconds;
        Check(again.Valid && again.Source != initial.Source, "a preview after a value changed has the new value");
        Console.WriteLine($"MEASURE custom node preview, three custom nodes: {cold:F0} ms compiling, {warm:F0} ms with the code unchanged");
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
        // Inside a loop body or a parallel branch, the step that failed is the inner one, not the loop or the parallel.
        var failing = Custom("x", "public static class CustomNode\n{\n    [Node(\"Inner step\")]\n    public static void Run() => throw new InvalidOperationException(\"inner boom\");\n}");
        var loop = Graph([N("e", "last_error"), N("r", "repeat", new { count = Lit("number", 2) }), failing, Reader("step")],
            E("t", "r"), E("r", "x", "body"), E("r", "step", "error"), D("e", "step", "value", "step"));
        var looped = await Logged(loop);
        Check(looped.SequenceEqual(["Inner step"]), "Error details names the failing step in a loop body: " + string.Join(" | ", looped));
        var parallel = Graph([N("e", "last_error"), N("p", "parallel", new { branches = new[] { "a", "b" } }), failing.DeepClone().AsObject(), Say("b", "fine"), Reader("step")],
            E("t", "p"), E("p", "x", "branch:a"), E("p", "b", "branch:b"), E("p", "step", "error"), D("e", "step", "value", "step"));
        var branched = await Logged(parallel);
        Check(branched.Contains("Inner step"), "Error details names the failing step in a parallel branch: " + string.Join(" | ", branched));
        // Two branches that both fail: the first one's step.
        static JsonObject Late(string id, string title, int ms) => Custom(id, "public static class CustomNode\n{\n    [Node(\"" + title + "\")]\n    public static async Task Run() { await Task.Delay(" + ms + "); throw new InvalidOperationException(\"" + title + "\"); }\n}");
        var both = Graph([N("e", "last_error"), N("p", "parallel", new { branches = new[] { "a", "b" } }), Late("x", "First step", 50), Late("y", "Second step", 250), Reader("step")],
            E("t", "p"), E("p", "x", "branch:a"), E("p", "y", "branch:b"), E("p", "step", "error"), D("e", "step", "value", "step"));
        var failedTwice = await Logged(both);
        Check(failedTwice.SequenceEqual(["First step"]), "when two parallel branches fail, Error details names the first one's step: " + string.Join(" | ", failedTwice));
        // A recovered failure thrown again by a later step is that step's.
        var rethrow = Custom("re", "public static class CustomNode\n{\n    [Node(\"Throw again\")]\n    public static void Run(NodeContext context) => throw context.Error!;\n}");
        var again = Graph([N("e", "last_error"), N("r", "repeat", new { count = Lit("number", 1) }), failing.DeepClone().AsObject(), rethrow, Reader("step")],
            E("t", "r"), E("r", "x", "body"), E("r", "re", "error"), E("re", "step", "error"), D("e", "step", "value", "step"));
        var thrownAgain = await Logged(again);
        Check(thrownAgain.SequenceEqual(["Throw again"]), "a recovered failure thrown again is named after the step that threw it: " + string.Join(" | ", thrownAgain));
        Console.WriteLine("PASS error details: empty before a failure, the message and step after one, the inner step in a loop body and a parallel branch");
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
        var entities = G(); entities["nodes"]![0]!["config"] = new JsonObject { ["type"] = "entity", ["entity"] = "sensor.test", ["entitiesExpression"] = "[new EntityReference(\"sensor.test\")]" };
        Refused(entities, "Entities expression is gone; choose the entity in Entity", "t", "entitiesExpression");
        var schedules = G(); schedules["nodes"]![0]!["config"] = new JsonObject { ["type"] = "daily", ["time"] = "07:30", ["timeZone"] = "UTC", ["schedulesExpression"] = "[]" };
        Refused(schedules, "Schedules expression is gone; set the time in Time", "t", "schedulesExpression");
        Refused(G(N("l", "library:gone:" + Uri.EscapeDataString("global::NodeLib.Gone.Run"), new { arguments = new { } })), "This step is no longer in its node library; delete it and add it again from the node library", "l", null);
        Check(VisualGraphValidator.Validate(G(N("l", "library:gone:x", new { arguments = new { } })), Empty).Single().Code == "node.unknownKind", "a step whose library node is gone keeps the code node.unknownKind");
        Console.WriteLine("PASS removed parts: usings, helper members, statement code, exposed values, variables and raw trigger expressions are refused with what to do instead");
    }

    // custom_preview, deriveSignatures on preview and save, and the language services on a Custom C# node's code.
    static async Task Api()
    {
        var path = Path.Combine(HassPath.HassConfiguration, "api-" + Guid.NewGuid()); var previous = HassPath.HassConfiguration; HassPath.HassConfiguration = path;
        var manager = new HassSharpManager(path);
        try
        {
            await manager.Init();
            async Task<JsonNode> Library(object request) => JsonNode.Parse(await manager.NodeLibraryRequest(JsonSerializer.Serialize(request)))!;
            async Task<JsonNode> Visual(object request) => JsonNode.Parse(await manager.VisualRequest(JsonSerializer.Serialize(request)))!;
            Check((await Library(new { operation = "save", id = "helpers", name = "Helpers", expectedRevision = 0, source = "public static class Helper { public static int Twice(int value) => value * 2; }" }))["ok"]!.GetValue<bool>(), "helper library saves");
            const string source = "public static class CustomNode\n{\n    [Node(\"Twice\")]\n    public static int Run(int value) => Helper.Twice(value);\n}";
            var preview = await Library(new { operation = "custom_preview", nodeId = "c", source });
            Check(preview["ok"]!.GetValue<bool>() && preview["result"]!["valid"]!.GetValue<bool>() && preview["result"]!["signature"]!["title"]!.GetValue<string>() == "Twice"
                && preview["result"]!["diagnostics"]!.AsArray().Count == 0, "custom_preview gives the signature, compiled with the saved libraries: " + preview.ToJsonString());
            var failing = await Library(new { operation = "custom_preview", nodeId = "c", source = source.Replace("Helper.Twice(value)", "Missing(value)") });
            Check(failing["ok"]!.GetValue<bool>() && !failing["result"]!["valid"]!.GetValue<bool>() && failing["result"]!["signature"] == null
                && failing["result"]!["diagnostics"]!.AsArray().Any(d => d!["startLine"]!.GetValue<int>() == 4), "custom_preview reports errors at the code's own lines: " + failing.ToJsonString());
            // Without the node's id the type ids cannot be written: refused, saying what is missing.
            var anonymous = await Library(new { operation = "custom_preview", source });
            Check(!anonymous["ok"]!.GetValue<bool>() && anonymous["error"]!["detail"]!.GetValue<string>().StartsWith("nodeId is required"), "custom_preview requires nodeId: " + anonymous.ToJsonString());
            // An unsaved library with errors: one diagnostic that says to fix the library first, not an empty list.
            var unsaved = await Library(new { operation = "custom_preview", nodeId = "c", source, sources = new[] { new { id = "helpers", source = "public static class Helper { public static int Twice(int value) => value * ; }" } } });
            var first = unsaved["result"]?["diagnostics"]?.AsArray().FirstOrDefault();
            Check(unsaved["ok"]!.GetValue<bool>() && !unsaved["result"]!["valid"]!.GetValue<bool>() && first?["message"]?.GetValue<string>() == "The node library helpers has errors; fix the node library first, then this code can be checked"
                && first["severity"]!.GetValue<int>() == 3, "custom_preview says a library has errors: " + unsaved.ToJsonString());
            var position = source.IndexOf("Helper.") + "Helper.".Length;
            var completion = await Library(new { operation = "completion", custom = true, nodeId = "c", source, position, workspaceVersion = "1" });
            Check(completion["ok"]!.GetValue<bool>() && completion["result"]!["data"]!.AsArray().Any(i => i!["displayText"]!.GetValue<string>() == "Twice"), "completion works on a custom node's code: " + completion.ToJsonString()[..Math.Min(600, completion.ToJsonString().Length)]);

            var graph = G(Code("c", "Logger.Info(text);", "string text, System.Uri? link = null"));
            graph["nodes"]![1]!["config"]!["arguments"] = new JsonObject { ["text"] = Lit("string", "hi") };
            var plain = await Visual(new { operation = "preview", graph });
            Check(!plain["result"]!["valid"]!.GetValue<bool>(), "without deriveSignatures a node without a signature is refused: " + plain.ToJsonString());
            var derived = await Visual(new { operation = "preview", graph, deriveSignatures = true });
            Check(derived["result"]!["valid"]!.GetValue<bool>() && derived["result"]!["graph"]!["nodes"]![1]!["config"]!["signature"]!["inputs"]!["text"] != null, "preview with deriveSignatures returns the graph with its pins: " + derived.ToJsonString()[..Math.Min(800, derived.ToJsonString().Length)]);
            var extra = (JsonObject)graph.DeepClone(); extra["nodes"]![1]!["config"]!["arguments"]!["gone"] = Lit("string", "x");
            var warned = await Visual(new { operation = "preview", graph = extra, deriveSignatures = true });
            Check(warned["result"]!["valid"]!.GetValue<bool>() && warned["result"]!["diagnostics"]!.AsArray().Any(d => d!["severity"]!.GetValue<int>() == 2 && d["nodeId"]!.GetValue<string>() == "c"
                && d["message"]!.GetValue<string>() == "Removed the value of Gone: the code no longer has that input"), "preview with deriveSignatures returns what it removed: " + warned["result"]!["diagnostics"]!.ToJsonString());
            var saved = await Visual(new { operation = "save", id = "derived", graph, expectedRevision = 0, deriveSignatures = true });
            Check(saved["ok"]!.GetValue<bool>() && saved["result"]!["document"]!["graph"]!["nodes"]![1]!["config"]!["signature"]!["title"]!.GetValue<string>() == "Check", "save with deriveSignatures stores the pins: " + saved.ToJsonString()[..Math.Min(800, saved.ToJsonString().Length)]);

            // Words and type entries the code does not decide (a release may write them differently) do not make a stored
            // signature out of date: it loads at Init, runs, and the next save stores them as this release writes them.
            await manager.UnloadAsync();
            var file = Path.Combine(path, ".storage", "hass_sharp", "visual", "derived.json");
            var original = File.ReadAllText(file);
            var edited = JsonNode.Parse(original)!;
            var signature = edited["graph"]!["nodes"]![1]!["config"]!["signature"]!;
            signature["title"] = "Old title"; signature["description"] = "Old words";
            signature["inputs"]!["text"]!["label"] = "Old label"; signature["inputs"]!["text"]!["description"] = "Old pin words";
            var link = signature["types"]!.AsObject().Single(p => p.Key.Contains("Uri"));
            link.Value!["label"] = "Old type label";
            File.WriteAllText(file, edited.ToJsonString());
            var messages = new List<string>(); var previousLog = PyInterop.Log;
            PyInterop.Log = (_, message) => { lock (messages) messages.Add(message); };
            try
            {
                manager = new HassSharpManager(path); await manager.Init();
                var read = await Visual(new { operation = "read", id = "derived" });
                Check(read["result"]!["currentRevisionActive"]!.GetValue<bool>(), "a signature with other words and type details loads at Init: " + read.ToJsonString()[..Math.Min(800, read.ToJsonString().Length)]);
                Check((await Visual(new { operation = "run", id = "derived", triggerId = "t" }))["ok"]!.GetValue<bool>(), "it runs");
                for (var i = 0; i < 100 && !messages.Contains("hi"); i++) await Task.Delay(50);
                Check(messages.Contains("hi"), "its code ran: " + string.Join(" | ", messages));
                var resaved = await Visual(new { operation = "save", id = "derived", graph = read["result"]!["graph"], expectedRevision = read["result"]!["revision"]!.GetValue<long>() });
                var refreshed = resaved["result"]?["document"]?["graph"]?["nodes"]?[1]?["config"]?["signature"];
                Check(resaved["ok"]!.GetValue<bool>() && refreshed?["title"]?.GetValue<string>() == "Check" && refreshed["inputs"]!["text"]!["label"]!.GetValue<string>() == "Text"
                    && refreshed["types"]![link.Key]!["label"]!.GetValue<string>() != "Old type label", "the next save stores the words as the compiler writes them: " + resaved.ToJsonString()[..Math.Min(800, resaved.ToJsonString().Length)]);
                // A changed pin type is what the code decides: still out of date, and not loaded.
                await manager.UnloadAsync();
                var retyped = JsonNode.Parse(original)!;
                retyped["graph"]!["nodes"]![1]!["config"]!["signature"]!["inputs"]!["text"]!["types"] = new JsonArray("number");
                File.WriteAllText(file, retyped.ToJsonString());
                manager = new HassSharpManager(path); await manager.Init();
                var stale = await Visual(new { operation = "read", id = "derived" });
                Check(!stale["result"]!["currentRevisionActive"]!.GetValue<bool>() && stale["result"]!["activationError"]!.GetValue<string>().Contains("The pins are out of date with the code"),
                    "a stored signature with another pin type is out of date: " + stale.ToJsonString()[..Math.Min(800, stale.ToJsonString().Length)]);
            }
            finally { PyInterop.Log = previousLog; }

            // A library save is refused only when it breaks a graph that is valid now. "derived" is already invalid (its pins
            // are out of date): it does not block the save and is reported in "invalid".
            var uses = G(Code("u", "Logger.Info(Helper.Twice(2).ToString());"));
            Check((await Visual(new { operation = "save", id = "uses", graph = uses, expectedRevision = 0, deriveSignatures = true }))["ok"]!.GetValue<bool>(), "a graph using the helper library saves");
            var breaking = await Library(new { operation = "save", id = "helpers", name = "Helpers", expectedRevision = 1, source = "public static class Helper { }" });
            Check(!breaking["ok"]!.GetValue<bool>() && breaking["error"]!["code"]!.GetValue<string>() == "validation"
                && breaking["error"]!["detail"]!["automations"]!.AsArray().Select(a => a!["id"]!.GetValue<string>()).SequenceEqual(["uses"])
                && breaking["error"]!["detail"]!["invalid"]!.AsArray().Select(a => a!["id"]!.GetValue<string>()).SequenceEqual(["derived"]), "a save that breaks a valid graph is refused: " + breaking.ToJsonString()[..Math.Min(1200, breaking.ToJsonString().Length)]);
            var allowed = await Library(new { operation = "save", id = "helpers", name = "Helpers", expectedRevision = 1, source = "public static class Helper { public static int Twice(int value) => value * 2; public static int Thrice(int value) => value * 3; }" });
            Check(allowed["ok"]!.GetValue<bool>() && allowed["result"]!["invalid"]!.AsArray().Select(a => a!["id"]!.GetValue<string>()).SequenceEqual(["derived"])
                && allowed["result"]!["automations"]!.AsArray().Any(a => a!["id"]!.GetValue<string>() == "uses"), "an already invalid graph does not block a library save: " + allowed.ToJsonString()[..Math.Min(1200, allowed.ToJsonString().Length)]);
            var notRunning = allowed["result"]!["invalid"]![0]!;
            Check(!notRunning["running"]!.GetValue<bool>() && notRunning["message"]!.GetValue<string>() == "Not rebuilt, because it has errors; it is not running", "an invalid graph that is not running says so: " + notRunning.ToJsonString()[..Math.Min(400, notRunning.ToJsonString().Length)]);
            // A graph made invalid on disk while it runs (as a release or a hand edit can): the save leaves it running the previous version, and says so.
            var usesFile = Path.Combine(path, ".storage", "hass_sharp", "visual", "uses.json");
            var broken = JsonNode.Parse(File.ReadAllText(usesFile))!;
            broken["graph"]!["nodes"]![1]!["config"]!["signature"]!["outputs"] = new JsonObject { ["result"] = new JsonObject { ["type"] = "number", ["label"] = "Result", ["description"] = "" } };
            File.WriteAllText(usesFile, broken.ToJsonString());
            var stillRunning = await Library(new { operation = "save", id = "helpers", name = "Helpers", expectedRevision = 2, source = "public static class Helper { public static int Twice(int value) => value * 2; }" });
            var entry = stillRunning["result"]?["invalid"]?.AsArray().SingleOrDefault(a => a!["id"]!.GetValue<string>() == "uses");
            Check(stillRunning["ok"]!.GetValue<bool>() && entry?["running"]?.GetValue<bool>() == true
                && entry["message"]!.GetValue<string>() == "Not rebuilt, because it has errors: it is still running the previous version, with the previous library code",
                "a running graph that the save leaves invalid is still running the previous version: " + stillRunning.ToJsonString()[..Math.Min(1200, stillRunning.ToJsonString().Length)]);
            Console.WriteLine("PASS custom node API: custom_preview, language services on custom code, deriveSignatures on preview and save");
        }
        finally { await manager.UnloadAsync(); HassPath.HassConfiguration = previous; try { Directory.Delete(path, true); } catch { } }
    }
}
