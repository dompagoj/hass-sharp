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
    /// <summary>The code of a C# entity (<paramref name="kind"/> <c>csharp_entity</c>) or C# device (<c>csharp_device</c>) node, compiled as its node.</summary>
    /// <remarks>With <paramref name="provider"/>, against its entity classes (<see cref="NodeLibraryFixture.EntityProvider"/> has <c>LightEntity</c> and the rest).</remarks>
    static CustomNodeDescriptor ClassCompiled(string kind, string source, string id = "c", NodeLibrarySource[]? libraries = null, DiagnosticsProvider? provider = null) =>
        new NodeLibraryCompiler(provider ?? Diagnostics).Preview(libraries ?? [], [new CustomNodeSource(id, source, kind)]).Snapshot.Custom[id];
    /// <summary>A provider with the captured schema's entity classes, so <c>LightEntity</c> and <c>SensorEntity</c> resolve.</summary>
    static DiagnosticsProvider Entities => NodeLibraryFixture.EntityProvider;
    /// <summary>The column a name starts at on a line (1-based) of the code.</summary>
    static int Column(string source, int line, string name) => source.Split('\n')[line - 1].IndexOf(name, StringComparison.Ordinal) + 1;
    /// <summary>The names of the properties a nullability warning (CS8618) names, in name order (the compiler's own order is no contract).</summary>
    static string NullWarned(IEnumerable<NodeLibraryDiagnostic> diagnostics) => string.Join(",", diagnostics.Where(d => d.Severity == 2 && d.Message.StartsWith("Non-nullable property '", StringComparison.Ordinal))
        .Select(d => d.Message["Non-nullable property '".Length..d.Message.IndexOf('\'', "Non-nullable property '".Length)]).Order(StringComparer.Ordinal));
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
            ClassTemplates();
            ClassSignatures();
            LibraryBaseSettings();
            ClassTitles();
            ClassProblems();
            RequiredSettingsDrawNoWarning();
            TwoNodesOneClassName();
            NothingRuns();
            ClassNodesInAGraph();
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
        // A pin is named by its parameter or returned value; C# takes letters of any language, a pin only English ones. The
        // problem is said where the name is written, in a node library and in Custom C# code alike.
        Refused("public static class Bad { [Node(\"Area\")] public static double Area(double širina, double visina) => širina * visina; }", NodeLibraryCompiler.PinNameMessage("input", "širina"));
        Refused("public static class Bad { [Node(\"Size\")] public static (double Širina, double Visina) Size() => (1, 2); }", NodeLibraryCompiler.PinNameMessage("value", "Širina"));
        CustomRefused("public static class CustomNode\n{\n    [Node(\"Area\")]\n    public static double Area(double širina, double visina) => širina * visina;\n}", NodeLibraryCompiler.PinNameMessage("input", "širina"), 4);
        CustomRefused("public static class CustomNode\n{\n    [Node(\"Size\")]\n    public static (double Širina, double Visina) Size() => (1, 2);\n}", NodeLibraryCompiler.PinNameMessage("value", "Širina"), 4);
        var located = Compiled("public static class CustomNode\n{\n    [Node(\"Area\")]\n    public static double Area(double širina, double visina) => širina * visina;\n}").Diagnostics.Single();
        Check(located.StartColumn == "    public static double Area(double ".Length + 1 && located.EndColumn == located.StartColumn + "širina".Length && located.Severity == 3,
            "the problem is at the parameter's name: " + Messages([located]));
        var usings = Compiled("using System.Text;\npublic static class CustomNode\n{\n    [Node(\"Join\")]\n    public static string Join(string first, string second) => new StringBuilder(first).Append(second).ToString();\n}");
        Check(usings.Valid && usings.Signature!["inputs"]!.AsObject().Select(p => p.Key).SequenceEqual(["first", "second"]), "using lines at the top of the code work: " + Messages(usings.Diagnostics));
        var signature = Compiled("public static class CustomNode\n{\n    /// <summary>Halves it.</summary>\n    [Node(\"Half\", Pure = true, Category = \"Ignored\")]\n    public static double Half(double value) => value / 2;\n}").Signature!;
        Check(signature.Select(p => p.Key).SequenceEqual(["title", "description", "pure", "inputs", "outputs", "types"]) && signature["title"]!.GetValue<string>() == "Half"
            && signature["description"]!.GetValue<string>() == "Halves it." && signature["pure"]!.GetValue<bool>() && signature["outputs"]!.AsObject().Select(p => p.Key).SequenceEqual(["result"]),
            "the signature is {title, description, pure, inputs, outputs, types}: " + signature.ToJsonString());
        Console.WriteLine("PASS node contract: kinds, pure value nodes, labels in words, tuple outputs, injected parameters, enum labels, one method per name, custom code rules");
    }

    // The template of a C# entity and of a C# device, compiled as its node, gives exactly the catalog's default signature,
    // with no diagnostic, so a new node of either kind is valid at once.
    static void ClassTemplates()
    {
        foreach (var (kind, type) in new[] { ("csharp_entity", "Thermometer"), ("csharp_device", "Plug") })
        {
            var config = VisualNodeCatalog.Root["nodes"]![kind]!["config"]!;
            var derived = ClassCompiled(kind, config["source"]!["default"]!.GetValue<string>());
            var stored = config["signature"]!["default"]!;
            Check(derived.Valid && derived.Diagnostics.IsEmpty && derived.Signature != null, $"the {kind} template compiles without a diagnostic: " + Messages(derived.Diagnostics));
            Check(VisualGenerator.SameJson(stored, derived.Signature), $"the catalog's default {kind} signature is the template's; derived: " + derived.Signature!.ToJsonString());
            Check(VisualNodeCatalog.WellFormedClassSignature(derived.Signature), $"the {kind} template's signature is well formed: " + derived.Signature.ToJsonString());
            Check(derived.TypeExpression == "global::NodeLib.Custom_c." + type && derived.Method == null && derived.Types.IsEmpty,
                $"the {kind} template's class is named in its node's namespace, and it has no method or types of a step: " + derived.TypeExpression);
        }
        Console.WriteLine("PASS class templates: each template compiles as its node to the catalog's default signature, which is well formed");
    }

    // A class node's signature: its title, summary, domain, and each [Setting] property (base classes first) with its type,
    // whether it is secret, an enum's options and a number's range. A secret's value is in nothing the compiler gives.
    static void ClassSignatures()
    {
        const string fan = """
            public enum Speed { Low, High }
            public abstract class Room : SharpSwitch
            {
                [Setting] public string Area { get; set; } = "";
            }
            /// <summary>A fan with a speed.</summary>
            [Node("Ceiling fan")]
            public class Fan : Room
            {
                /// <summary>Where it answers.</summary>
                [Setting("Host")] public string Host { get; set; } = "";
                [Setting(Secret = true)] public string Token { get; set; } = "hunter2-not-shown";
                [Setting("Poll interval", Minimum = 1, Maximum = 3600)] public int Interval { get; set; } = 30;
                [Setting(Description = "How strong it is.", Minimum = 0.5)] public double Gain { get; set; } = 1;
                [Setting] public long Count { get; set; }
                [Setting] public bool Loud { get; set; }
                [Setting] public Speed Speed { get; set; } = Speed.High;
                public string NotASetting { get; set; } = "";
            }
            """;
        var compiled = ClassCompiled("csharp_entity", fan);
        Check(compiled.Valid, "the fan compiles: " + Messages(compiled.Diagnostics));
        var expected = JsonNode.Parse("""
            {"title": "Ceiling fan", "description": "A fan with a speed.", "domain": "switch", "settings": [
              {"key": "Area", "label": "Area", "description": "", "type": "string", "secret": false},
              {"key": "Host", "label": "Host", "description": "Where it answers.", "type": "string", "secret": false},
              {"key": "Token", "label": "Token", "description": "", "type": "string", "secret": true},
              {"key": "Interval", "label": "Poll interval", "description": "", "type": "integer", "secret": false, "minimum": 1, "maximum": 3600},
              {"key": "Gain", "label": "Gain", "description": "How strong it is.", "type": "number", "secret": false, "minimum": 0.5},
              {"key": "Count", "label": "Count", "description": "", "type": "integer", "secret": false},
              {"key": "Loud", "label": "Loud", "description": "", "type": "boolean", "secret": false},
              {"key": "Speed", "label": "Speed", "description": "", "type": "enum", "secret": false, "options": ["Low", "High"]}]}
            """);
        Check(VisualGenerator.SameJson(expected, compiled.Signature), "an entity's settings, base classes first, with their types, secret flag, options and ranges: " + compiled.Signature?.ToJsonString());
        Check(VisualNodeCatalog.WellFormedClassSignature(compiled.Signature), "the signature is well formed: " + compiled.Signature?.ToJsonString());
        Check(!compiled.Signature!.ToJsonString().Contains("hunter2") && !Messages(compiled.Diagnostics).Contains("hunter2"), "a secret's initial value is in neither the signature nor a diagnostic");
        // An int setting without a range keeps an int's.
        var plain = ClassCompiled("csharp_device", "public class Box : SharpDevice\n{\n    [Setting] public int Channel { get; set; }\n}");
        Check(plain.Valid && VisualGenerator.SameJson(plain.Signature!["settings"]![0], JsonNode.Parse("""{"key": "Channel", "label": "Channel", "description": "", "type": "integer", "secret": false, "minimum": -2147483648, "maximum": 2147483647}""")),
            "an int setting without a range has an int's: " + plain.Signature?.ToJsonString());
        // A long with a Maximum alone has that side only (a long has no range of its own); an int whose Minimum is NaN, as
        // none, keeps an int's.
        var sided = ClassCompiled("csharp_device", "public class Box : SharpDevice\n{\n    [Setting(Maximum = 10)] public long Count { get; set; }\n    [Setting(Minimum = double.NaN)] public int Plain { get; set; }\n}");
        Check(sided.Valid && VisualGenerator.SameJson(sided.Signature!["settings"], JsonNode.Parse("""
            [{"key": "Count", "label": "Count", "description": "", "type": "integer", "secret": false, "maximum": 10},
             {"key": "Plain", "label": "Plain", "description": "", "type": "integer", "secret": false, "minimum": -2147483648, "maximum": 2147483647}]
            """)), "a long's Maximum alone, and an int's NaN Minimum as none: " + Messages(sided.Diagnostics) + " " + sided.Signature?.ToJsonString());
        // A camera is an entity of the camera domain; a device has none, and the entity classes beside it are its own helpers.
        var camera = ClassCompiled("csharp_entity", "public class Doorbell : SharpCamera { }");
        Check(camera.Valid && VisualGenerator.SameJson(camera.Signature, JsonNode.Parse("""{"title": "Doorbell", "description": "", "domain": "camera", "settings": []}""")),
            "a camera class gives domain camera: " + Messages(camera.Diagnostics) + " " + camera.Signature?.ToJsonString());
        var device = ClassCompiled("csharp_device", "public class Relay : SharpSwitch { }\n/// <summary>Two relays.</summary>\npublic class DoublePlug : SharpDevice\n{\n    public DoublePlug() { Add(new Relay()); }\n}");
        Check(device.Valid && device.TypeExpression == "global::NodeLib.Custom_c.DoublePlug"
            && VisualGenerator.SameJson(device.Signature, JsonNode.Parse("""{"title": "Double plug", "description": "Two relays.", "domain": null, "settings": []}""")),
            "a device gives domain null, its entity classes being its helpers: " + Messages(device.Diagnostics) + " " + device.Signature?.ToJsonString());
        // An entity class of a node library is used by deriving from it in a node's code: its settings come first.
        var library = new NodeLibrarySource("plugs", "public abstract class HttpRelay : SharpSwitch\n{\n    [Setting(\"Address\")] public string Address { get; set; } = \"\";\n}", 1);
        var derived = ClassCompiled("csharp_entity", "public class Kitchen : HttpRelay\n{\n    [Setting] public bool Loud { get; set; }\n}", libraries: [library]);
        Check(derived.Valid && VisualGenerator.SameJson(derived.Signature, JsonNode.Parse("""{"title": "Kitchen", "description": "", "domain": "switch", "settings": [{"key": "Address", "label": "Address", "description": "", "type": "string", "secret": false}, {"key": "Loud", "label": "Loud", "description": "", "type": "boolean", "secret": false}]}""")),
            "a C# entity derives from a library's entity class, its settings first: " + Messages(derived.Diagnostics) + " " + derived.Signature?.ToJsonString());
        // A setting can be an entity handle: type entity, with the domain of a domain's class (none for Entity). A string or
        // a handle that is not nullable and has no initializer is required; a nullable one, one with an initializer and a
        // number are not, and say nothing of it.
        var lamp = ClassCompiled("csharp_entity", """
            public class Lamp : SharpLight
            {
                [Setting] public LightEntity Light { get; set; }
                [Setting] public Entity? Any { get; set; }
                [Setting] public string Host { get; set; }
                [Setting] public string Zone { get; set; } = "UTC";
                [Setting] public double Rate { get; set; }
            }
            """, provider: Entities);
        string[] entries = [
            """{"key":"Light","label":"Light","description":"","type":"entity","secret":false,"domain":"light","required":true}""",
            """{"key":"Any","label":"Any","description":"","type":"entity","secret":false}""",
            """{"key":"Host","label":"Host","description":"","type":"string","secret":false,"required":true}""",
            """{"key":"Zone","label":"Zone","description":"","type":"string","secret":false}""",
            """{"key":"Rate","label":"Rate","description":"","type":"number","secret":false}""",
        ];
        Check(lamp.Valid && lamp.Signature!["settings"]!.AsArray().Select(s => s!.ToJsonString()).SequenceEqual(entries),
            "an entity setting has its domain, and a string or entity setting without a ? or an initializer is required: " + Messages(lamp.Diagnostics) + " " + lamp.Signature?["settings"]?.ToJsonString());
        // In code whose nullability is off, a string is not known to be non-nullable: it is not required.
        var oblivious = ClassCompiled("csharp_device", "#nullable disable\npublic class Box : SharpDevice\n{\n    [Setting] public string Host { get; set; }\n}");
        Check(oblivious.Valid && oblivious.Signature!["settings"]![0]!.ToJsonString() == """{"key":"Host","label":"Host","description":"","type":"string","secret":false}""",
            "a string setting in code without nullability is not required: " + Messages(oblivious.Diagnostics) + " " + oblivious.Signature?.ToJsonString());
        // What a stored signature is compared with: a setting's domain and whether it is required are part of it.
        static JsonObject Signed(string setting) => JsonNode.Parse("""{"title":"Lamp","description":"","domain":"light","settings":[""" + setting + "]}")!.AsObject();
        var light = Signed("""{"key":"Light","label":"Light","description":"","type":"entity","secret":false,"domain":"light","required":true}""");
        Check(VisualGenerator.SameSettings(light, Signed("""{"key":"Light","label":"Light","description":"","type":"entity","secret":false,"domain":"light","required":true}""")), "a signature has the same settings as itself");
        Check(!VisualGenerator.SameSettings(light, Signed("""{"key":"Light","label":"Light","description":"","type":"entity","secret":false,"domain":"light"}""")), "a setting that is no longer required is a change of settings");
        Check(!VisualGenerator.SameSettings(light, Signed("""{"key":"Light","label":"Light","description":"","type":"entity","secret":false,"domain":"sensor","required":true}""")), "a setting of another domain is a change of settings");
        Console.WriteLine("PASS class signatures: title, summary, domain, and each setting's type, secret flag, options and range, a library base's first; a secret's value is never shown; an entity setting's domain and a required setting");
    }

    // A C# node whose class derives a concrete class of a node library has that class's [Setting]s in its signature, the
    // library's first, with their labels, secret flag and range, though the library's class is no node's own code. A
    // [Setting] of a library's class that a setting cannot be is reported in the library, at the property, and left out:
    // the node deriving the class has the library class's other settings and no problem of its own.
    static void LibraryBaseSettings()
    {
        static string Where(IEnumerable<NodeLibraryDiagnostic> diagnostics) => string.Join(" | ", diagnostics.Select(d => $"{d.SourceId} {d.StartLine}:{d.StartColumn} {d.Message}"));
        var meters = new NodeLibrarySource("meters", """
            /// <summary>A meter.</summary>
            public class Meter : SharpDevice
            {
                [Setting("Server address")] public string Server { get; set; } = "";
                [Setting(Secret = true)] public string Checksum { get; set; } = "";
                [Setting(Minimum = 1)] public int Retries { get; set; } = 3;
            }
            """, 1);
        var kitchen = ClassCompiled("csharp_device", "public class Kitchen : Meter\n{\n    [Setting] public bool Loud { get; set; }\n}", libraries: [meters]);
        Check(kitchen.Valid && kitchen.Diagnostics.IsEmpty && VisualGenerator.SameJson(kitchen.Signature, JsonNode.Parse("""
            {"title": "Kitchen", "description": "", "domain": null, "settings": [
              {"key": "Server", "label": "Server address", "description": "", "type": "string", "secret": false},
              {"key": "Checksum", "label": "Checksum", "description": "", "type": "string", "secret": true},
              {"key": "Retries", "label": "Retries", "description": "", "type": "integer", "secret": false, "minimum": 1, "maximum": 2147483647},
              {"key": "Loud", "label": "Loud", "description": "", "type": "boolean", "secret": false}]}
            """)), "a C# device deriving a library's device has its settings, the library's first: " + Messages(kitchen.Diagnostics) + " " + kitchen.Signature?.ToJsonString());

        var broken = new NodeLibrarySource("broken", "public class Odd : SharpDevice\n{\n    [Setting] public DateTime When { get; set; }\n    [Setting] public bool On { get; set; }\n}", 1);
        var preview = new NodeLibraryCompiler(Diagnostics).Preview([broken], [new CustomNodeSource("c", "public class Mine : Odd\n{\n    [Setting] public bool Loud { get; set; }\n}", "csharp_device")]);
        var wrong = $"broken 3:31 {NodeLibraryCompiler.SettingTypeMessage("DateTime")}";
        Check(!preview.Valid && Where(preview.Diagnostics.Where(d => d.Severity == 3)) == wrong,
            "a library's bad setting is reported in the library, at the property: " + Where(preview.Diagnostics));
        var mine = preview.Snapshot.Custom["c"];
        Check(mine.Valid && mine.Diagnostics.IsEmpty && mine.Signature!["settings"]!.AsArray().Select(s => s!["key"]!.GetValue<string>()).SequenceEqual(["On", "Loud"]),
            "the node deriving the library's class has its good setting and its own, and no problem: " + Where(mine.Diagnostics) + " " + mine.Signature?.ToJsonString());
        // The library alone reports it the same.
        var alone = new NodeLibraryCompiler(Diagnostics).Preview([broken]);
        Check(!alone.Valid && Where(alone.Diagnostics.Where(d => d.Severity == 3)) == wrong, "the library alone reports it at the property: " + Where(alone.Diagnostics));
        // A library base's setting without an initializer is required in the node's signature, read from the library's own
        // lines; an entity setting of a library's class is accepted as one of the node's. A setting the node's class declares
        // again is as the node's class declares it: here without the base's initializer, so required.
        var remotes = new NodeLibrarySource("remotes", "public abstract class Remote : SharpDevice\n{\n    [Setting] public string Host { get; set; }\n    [Setting] public SensorEntity Source { get; set; }\n    [Setting] public string Zone { get; set; } = \"UTC\";\n}", 1);
        var box = ClassCompiled("csharp_device", "public class Box : Remote\n{\n    [Setting] public new string Zone { get; set; }\n}", libraries: [remotes], provider: Entities);
        string[] inherited = [
            """{"key":"Host","label":"Host","description":"","type":"string","secret":false,"required":true}""",
            """{"key":"Source","label":"Source","description":"","type":"entity","secret":false,"domain":"sensor","required":true}""",
            """{"key":"Zone","label":"Zone","description":"","type":"string","secret":false,"required":true}""",
        ];
        Check(box.Valid && box.Signature!["settings"]!.AsArray().Select(s => s!.ToJsonString()).SequenceEqual(inherited),
            "a library base's setting without an initializer is required, its entity setting has its domain, and a setting declared again is the derived class's: " + Where(box.Diagnostics) + " " + box.Signature?["settings"]?.ToJsonString());
        Console.WriteLine("PASS class signatures: a C# node deriving a library's class has its settings in its signature, the library's first, and a bad [Setting] of the library's class is reported in the library at the property");
    }

    // A class node's title is its class's name in words (an acronym kept whole), or the title a [Node] on the class gives;
    // [Node] on a class, in a node's code or in a library, makes no step.
    static void ClassTitles()
    {
        var plug = ClassCompiled("csharp_device", "public class ShellyPlugPro : SharpDevice { }");
        var sensor = ClassCompiled("csharp_entity", "public class HTTPSensor : SharpSensor { }");
        var titled = ClassCompiled("csharp_device", "[Node(\"Poller box\")]\npublic class Poller : SharpDevice { }");
        Check(plug.Signature?["title"]?.GetValue<string>() == "Shelly plug pro" && sensor.Signature?["title"]?.GetValue<string>() == "HTTP sensor"
            && titled.Signature?["title"]?.GetValue<string>() == "Poller box",
            "a class's title is its name in words, else its [Node] title: " + string.Join(", ", new[] { plug, sensor, titled }.Select(c => c.Signature?["title"]?.ToJsonString() ?? Messages(c.Diagnostics))));
        Check(titled.Valid && titled.Method == null && titled.Types.IsEmpty, "[Node] on a node's class makes it no step: " + titled.Method);
        var library = Library("[Node(\"Poller box\")]\npublic class Poller : SharpDevice { }\npublic class ShellyPlugPro : SharpDevice { }", "ranged");
        var nodes = library.Snapshot.Catalog["nodes"]!.AsObject();
        Check(library.Valid && !nodes.Any(n => n.Key.StartsWith("library:", StringComparison.Ordinal)) && !nodes.ToJsonString().Contains("Poller", StringComparison.Ordinal),
            "[Node] on a library's class makes no step: " + string.Join(", ", nodes.Select(n => n.Key).Where(k => k.StartsWith("library:", StringComparison.Ordinal))));
        Console.WriteLine("PASS class titles: a class's name in words, an acronym whole, or its [Node] title; [Node] on a class makes no step");
    }

    // Each problem a class node's code can have, said in the node's own lines.
    static void ClassProblems()
    {
        void Refused(string kind, string source, string message, int line)
        {
            var compiled = ClassCompiled(kind, source);
            Check(!compiled.Valid && compiled.Signature == null && compiled.TypeExpression == null
                && compiled.Diagnostics.Any(d => d.Message == message && d.StartLine == line && d.SourceId == "c" && d.Severity == 3),
                $"{kind}: expected \"{message}\" on line {line}: " + Messages(compiled.Diagnostics));
        }
        const string NoEntity = "Write one public class that derives from an entity class, such as SharpSwitch: it becomes this entity";
        const string NoDevice = "Write one public class that derives from SharpDevice: it becomes this device";
        const string Uncreatable = " must be public, not abstract or generic, and have a public constructor without parameters";
        Refused("csharp_entity", "public static class Helper { }", NoEntity, 1);
        Refused("csharp_entity", "// Nothing here yet.\n", NoEntity, 1);
        // SharpEntity itself is no entity class: a class deriving it has no domain (and only an abstract one compiles).
        Refused("csharp_entity", "\npublic abstract class Odd : SharpEntity { }", NoEntity, 2);
        Refused("csharp_device", "public static class Helper { }", NoDevice, 1);
        Refused("csharp_device", "public class Lamp : SharpLight { }", NoDevice, 1);
        Refused("csharp_entity", "public class A : SharpSwitch { }\npublic class B : SharpLight { }", "Only one class can be this entity; this code has 2", 2);
        Refused("csharp_device", "public class A : SharpDevice { }\npublic class B : SharpDevice { }\npublic class C : SharpDevice { }", "Only one class can be this device; this code has 3", 2);
        Refused("csharp_entity", "public class Lamp : SharpLight { }\npublic class Plug : SharpDevice { }", "Plug is a device: write it in a C# device node", 2);
        Refused("csharp_entity", "public class Plug : SharpDevice { }", "Plug is a device: write it in a C# device node", 1);
        Refused("csharp_entity", "class Lamp : SharpLight { }", "Lamp" + Uncreatable, 1);
        Refused("csharp_entity", "public class Lamp<T> : SharpLight { }", "Lamp" + Uncreatable, 1);
        Refused("csharp_entity", "public static class Outer\n{\n    public class Lamp : SharpLight { }\n}", "Lamp" + Uncreatable, 3);
        Refused("csharp_device", "public abstract class Plug : SharpDevice { }", "Plug" + Uncreatable, 1);
        Refused("csharp_device", "public class Plug : SharpDevice\n{\n    public Plug(string host) { }\n}", "Plug" + Uncreatable, 1);
        Refused("csharp_entity", "public class Lamp : SharpLight\n{\n    [Setting] public string device { get; set; } = \"\";\n}", "A setting cannot be called device: that is the pin the entity's device connects to", 3);
        // The [Setting] sentences, word for word, on the property's line.
        Refused("csharp_entity", "public class Lamp : SharpLight\n{\n    [Setting] public DateTime When { get; set; }\n}", NodeLibraryCompiler.SettingTypeMessage("DateTime"), 3);
        Refused("csharp_device", "public class Plug : SharpDevice\n{\n\n    [Setting] public string X { get; } = \"\";\n}", NodeLibraryCompiler.SettingSetterMessage, 4);
        Refused("csharp_device", "public class Plug : SharpDevice\n{\n    [Setting] public static string Shared { get; set; } = \"\";\n}", NodeLibraryCompiler.SettingAccessMessage("Shared"), 3);
        Refused("csharp_device", "public class Plug : SharpDevice\n{\n    [Setting] internal string Hidden { get; set; } = \"\";\n}", NodeLibraryCompiler.SettingAccessMessage("Hidden"), 3);
        Refused("csharp_device", "public class Plug : SharpDevice\n{\n    [Setting(Minimum = 1)] public string Host { get; set; } = \"\";\n}", NodeLibraryCompiler.SettingRangeTypeMessage("string"), 3);
        Refused("csharp_device", "public class Plug : SharpDevice\n{\n    [Setting(Minimum = 5, Maximum = 1)] public int Upside { get; set; }\n}", NodeLibraryCompiler.SettingRangeOrderMessage, 3);
        Refused("csharp_device", "public class Plug : SharpDevice\n{\n    [Setting(Maximum = 2.5)] public long Fraction { get; set; }\n}", NodeLibraryCompiler.SettingRangeWholeMessage, 3);
        // A setting's key is its pin's id, which takes English letters only, as a Custom C# step's parameter's does.
        Refused("csharp_device", "public class Plug : SharpDevice\n{\n    [Setting] public string Širina { get; set; } = \"\";\n}", NodeLibraryCompiler.PinNameMessage("setting", "Širina"), 3);
        // Code that does not compile has its compiler's errors, on its own lines, and no signature.
        Refused("csharp_device", "public class Plug : SharpDevice\n{\n    public Plug() { Missing(); }\n}", "The name 'Missing' does not exist in the current context", 3);
        // A setting's problem is the only error, at the property's name: line and column.
        void Exactly(string kind, string source, string message, int line, int column, DiagnosticsProvider? provider = null)
        {
            var errors = ClassCompiled(kind, source, provider: provider).Diagnostics.Where(d => d.Severity == 3).ToArray();
            Check(errors.Length == 1 && errors[0] is { SourceId: "c" } only && only.Message == message && only.StartLine == line && only.StartColumn == column,
                $"{kind}: expected only \"{message}\" at {line}:{column}: " + string.Join(" | ", errors.Select(d => $"{d.StartLine}:{d.StartColumn} {d.Message}")));
        }
        Exactly("csharp_entity", "public class Lamp : SharpLight\n{\n    [Setting] public DateTime When { get; set; }\n}", "A setting is a string, bool, int, long, double, enum or an entity class such as LightEntity; DateTime cannot be one", 3, 31);
        Check(NodeLibraryCompiler.SettingTypeMessage("DateTime") == "A setting is a string, bool, int, long, double, enum or an entity class such as LightEntity; DateTime cannot be one", "the setting type sentence names the entity classes");
        // An entity setting is one entity, never a secret, and takes no required modifier (one without a ? or an initializer
        // is required already). Each is the only error, at the property's name.
        const string secretLight = "public class Lamp : SharpLight\n{\n    [Setting(Secret = true)] public LightEntity Light { get; set; }\n}";
        Exactly("csharp_entity", secretLight, "An entity setting cannot be secret", 3, Column(secretLight, 3, "Light {"), Entities);
        Check(NodeLibraryCompiler.SettingSecretEntityMessage == "An entity setting cannot be secret", "the secret entity sentence");
        const string lights = "public class Lamp : SharpLight\n{\n    [Setting] public LightEntity[] Lights { get; set; }\n}";
        Exactly("csharp_entity", lights, "A setting is a string, bool, int, long, double, enum or an entity class such as LightEntity; LightEntity[] cannot be one", 3, Column(lights, 3, "Lights {"), Entities);
        const string required = "public class Lamp : SharpLight\n{\n    [Setting] public required LightEntity Light { get; set; }\n}";
        Exactly("csharp_entity", required, "A setting does not take the required modifier: one that is not nullable and has no initializer is required already", 3, Column(required, 3, "Light {"), Entities);
        // A range is for a number, not an entity.
        const string ranged = "public class Lamp : SharpLight\n{\n    [Setting(Minimum = 1)] public LightEntity Light { get; set; }\n}";
        Exactly("csharp_entity", ranged, NodeLibraryCompiler.SettingRangeTypeMessage("LightEntity"), 3, Column(ranged, 3, "Light {"), Entities);
        Exactly("csharp_device", "public class Plug : SharpDevice\n{\n    [Setting] internal string Hidden { get; set; } = \"\";\n}", NodeLibraryCompiler.SettingAccessMessage("Hidden"), 3, 31);
        Exactly("csharp_device", "public class Plug : SharpDevice\n{\n\n    [Setting] public string X { get; } = \"\";\n}", NodeLibraryCompiler.SettingSetterMessage, 4, 29);
        Exactly("csharp_device", "public class Plug : SharpDevice\n{\n    [Setting] public static string Shared { get; set; } = \"\";\n}", NodeLibraryCompiler.SettingAccessMessage("Shared"), 3, 36);
        Exactly("csharp_device", "public class Plug : SharpDevice\n{\n    [Setting(Minimum = 1)] public string Host { get; set; } = \"\";\n}", NodeLibraryCompiler.SettingRangeTypeMessage("string"), 3, 42);
        Exactly("csharp_device", "public class Plug : SharpDevice\n{\n    [Setting(Minimum = 5, Maximum = 1)] public int Upside { get; set; }\n}", NodeLibraryCompiler.SettingRangeOrderMessage, 3, 52);
        Exactly("csharp_device", "public class Plug : SharpDevice\n{\n    [Setting(Maximum = 2.5)] public long Fraction { get; set; }\n}", NodeLibraryCompiler.SettingRangeWholeMessage, 3, 42);
        // A class of the right kind beside an abstract helper base, or a nested helper of another kind, is the one.
        Check(ClassCompiled("csharp_entity", "public abstract class Base : SharpSwitch { }\npublic class Mine : Base { }").Valid, "an abstract base of an entity class is a helper");
        Check(ClassCompiled("csharp_device", "public class Plug : SharpDevice\n{\n    public class Relay : SharpSwitch { }\n}").Valid, "an entity class inside a device class is its helper");
        Console.WriteLine("PASS class problems: no class, several, a device in a C# entity, a class that cannot be created, a setting called device, the [Setting] sentences, all on the node's own lines");
    }

    // A required setting is given its value before Setup(), so its property draws no nullability warning (CS8618), with or
    // without a constructor (which the compiler reports it at), in a C# entity's or C# device's code and in a library's
    // class; a property that is no setting keeps it, and so does every property of a Custom C# step's code and of a
    // library's class that is no SharpComponent.
    static void RequiredSettingsDrawNoWarning()
    {
        foreach (var (kind, type, constructor) in new[] { ("csharp_entity", "SharpLight", ""), ("csharp_entity", "SharpLight", "    public Lamp() { }\n"), ("csharp_device", "SharpDevice", ""), ("csharp_device", "SharpDevice", "    public Lamp() { }\n") })
        {
            var source = "public class Lamp : " + type + "\n{\n" + constructor + "    [Setting] public LightEntity Light { get; set; }\n    [Setting] public string Host { get; set; }\n    public string Other { get; set; }\n}";
            var compiled = ClassCompiled(kind, source, provider: Entities);
            Check(compiled.Valid && NullWarned(compiled.Diagnostics) == "Other",
                $"{kind}{(constructor.Length > 0 ? " with a constructor" : "")}: only the property that is no setting is warned of: " + Messages(compiled.Diagnostics));
        }
        // A library's classes: one deriving SharpComponent warns of no required setting, any other keeps every warning.
        var library = new NodeLibraryCompiler(Entities).Preview([new("meters", """
            public class Meter : SharpDevice
            {
                public Meter() { }
                [Setting] public string Host { get; set; }
                [Setting] public SensorEntity Source { get; set; }
                public string Other { get; set; }
            }
            public abstract class Probe : SharpSensor
            {
                [Setting] public string Path { get; set; }
            }
            public class Plain
            {
                [Setting] public string Address { get; set; }
            }
            """, 1)]);
        Check(library.Valid && NullWarned(library.Diagnostics) == "Address,Other", "a library's SharpComponent warns only of a property that is no setting, any other class of every property: " + Messages(library.Diagnostics));
        // A graph's preview compiles each library again inside its source: there too a required setting of a library's
        // SharpComponent draws no warning, with or without a constructor, and every other warning of the library section
        // is listed as it was, at the same place.
        foreach (var constructor in new[] { "    public Meter() { }\n", "" })
        {
            var meters = new NodeLibrarySource("meters", "public class Meter : SharpDevice\n{\n" + constructor
                + "    [Setting] public string Host { get; set; }\n    [Setting] public SensorEntity Source { get; set; }\n    public string Other { get; set; }\n}\n"
                + "public class Plain\n{\n    [Setting] public string Address { get; set; }\n}", 1);
            var graph = new VisualGenerator(Entities).Preview(G(Say("say", "hi")), [meters]);
            var section = graph.Diagnostics.Where(d => d.NodeId == "$library:meters" && d.Message.StartsWith("Non-nullable property '", StringComparison.Ordinal)).ToArray();
            var kept = string.Join(" | ", section.Select(d => $"{d.Severity} {d.StartLine}:{d.StartColumn} {d.Message}"));
            // The two warnings kept, as the preview gave them before required settings lost theirs: the property's
            // (or, with a constructor, the constructor's) line and column in the generated source, word for word.
            const string Tail = " must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring the property as nullable.";
            var expected = constructor.Length == 0
                ? $"2 {{1}}:29 Non-nullable property 'Address'{Tail} | 2 {{0}}:19 Non-nullable property 'Other'{Tail}"
                : $"2 {{1}}:29 Non-nullable property 'Address'{Tail} | 2 {{0}}:12 Non-nullable property 'Other'{Tail}";
            var start = graph.SourceMap.Single(m => m.NodeId == "$library:meters").StartLine;
            // The library's lines in the source: the wrapper's three, then the code. Other's warning is on Other's line (the
            // code's fifth) or, with a constructor, on the constructor's (its third); Address's on the last but one.
            int Line(int code) => start + NodeLibraryCompiler.SourceLineOffset + code - 1;
            var otherLine = Line(constructor.Length == 0 ? 5 : 3);
            var addressLine = Line(constructor.Length == 0 ? 9 : 10);
            Check(graph.Valid && kept == string.Format(expected, otherLine, addressLine),
                $"a graph's preview{(constructor.Length > 0 ? ", with a constructor," : "")} lists the library's warnings but a required setting's: " + kept + " ; all: " + Messages(graph.Diagnostics));
        }
        // A Custom C# step's code is no class node's: a SharpComponent class in it keeps its warning.
        var step = Compiled("public class Helper : SharpDevice\n{\n    [Setting] public string Host { get; set; }\n}\npublic static class CustomNode\n{\n    [Node(\"Take\")]\n    public static void Run(string value) { }\n}");
        Check(step.Valid && NullWarned(step.Diagnostics) == "Host", "a Custom C# step's code keeps its nullability warnings: " + Messages(step.Diagnostics));
        Console.WriteLine("PASS required settings: no nullability warning for a required setting of a C# entity, a C# device or a library's class, with or without a constructor; every other property keeps it");
    }

    // The types a node's code declares are its own: two nodes with a class of one name compile side by side, and another
    // node's type is not one of this node's settings.
    static void TwoNodesOneClassName()
    {
        const string plug = "public enum Speed { Low, High }\npublic class Plug : SharpDevice\n{\n    [Setting] public Speed Speed { get; set; }\n}";
        var preview = new NodeLibraryCompiler(Diagnostics).Preview([], [new CustomNodeSource("a", plug, "csharp_device"), new CustomNodeSource("b", plug, "csharp_device")]);
        var (a, b) = (preview.Snapshot.Custom["a"], preview.Snapshot.Custom["b"]);
        Check(preview.Valid && a.Valid && b.Valid && a.TypeExpression == "global::NodeLib.Custom_a.Plug" && b.TypeExpression == "global::NodeLib.Custom_b.Plug",
            "two device nodes each with a class Plug are both valid, as two classes: " + Messages(a.Diagnostics) + Messages(b.Diagnostics) + $" {a.TypeExpression} {b.TypeExpression}");
        var borrowing = new NodeLibraryCompiler(Diagnostics).Preview([], [new CustomNodeSource("a", plug, "csharp_device"),
            new CustomNodeSource("b", "public class Fan : SharpSwitch\n{\n    [Setting] public NodeLib.Custom_a.Speed Speed { get; set; }\n}", "csharp_entity")]).Snapshot.Custom["b"];
        Check(!borrowing.Valid && borrowing.Diagnostics.Any(d => d.Message == "NodeLib.Custom_a.Speed belongs to the C# device node a; declare the type in this code or in a node library to use it here"),
            "a setting of a type another node declares is refused, naming that node's kind: " + Messages(borrowing.Diagnostics));
        // The sentence names the kind of the node that declares the type: a C# entity's, a C# device's, a Custom C# step's.
        const string fan = "public enum Level { Low, High }\npublic class Fan : SharpSwitch\n{\n    [Setting] public Level Level { get; set; }\n}";
        var fromEntity = new NodeLibraryCompiler(Diagnostics).Preview([], [new CustomNodeSource("e", fan, "csharp_entity"),
            new CustomNodeSource("d", "public class Hub : SharpDevice\n{\n    [Setting] public NodeLib.Custom_e.Level Level { get; set; }\n}", "csharp_device")]).Snapshot.Custom["d"];
        Check(fromEntity.Diagnostics.Any(d => d.Message == "NodeLib.Custom_e.Level belongs to the C# entity node e; declare the type in this code or in a node library to use it here"),
            "a type a C# entity node declares is named as that node's: " + Messages(fromEntity.Diagnostics));
        var fromStep = new NodeLibraryCompiler(Diagnostics).Preview([], [new CustomNodeSource("s", "public enum Mode { Off, On }\npublic static class Step\n{\n    [Node(\"Mode\")]\n    public static Mode Run() => Mode.On;\n}"),
            new CustomNodeSource("d", "public class Hub : SharpDevice\n{\n    [Setting] public NodeLib.Custom_s.Mode Mode { get; set; }\n}", "csharp_device")]).Snapshot.Custom["d"];
        Check(fromStep.Diagnostics.Any(d => d.Message == "NodeLib.Custom_s.Mode belongs to the Custom C# node s; declare the type in this code or in a node library to use it here"),
            "a type a Custom C# step declares keeps the step's sentence: " + Messages(fromStep.Diagnostics));
        Console.WriteLine("PASS two class nodes with one class name: both valid, each its own class; another node's type is refused, naming that node's kind");
    }

    // Compiling a class node's code never creates its class: a constructor and a static constructor that throw (and say
    // they ran) leave a valid node with its signature.
    static void NothingRuns()
    {
        const string Variable = "HASS_SHARP_CLASS_NODE_RAN";
        Environment.SetEnvironmentVariable(Variable, null);
        foreach (var (kind, type) in new[] { ("csharp_entity", "SharpSwitch"), ("csharp_device", "SharpDevice") })
        {
            var source = "public class Bomb : " + type + "\n{\n"
                + "    static Bomb() { Environment.SetEnvironmentVariable(\"" + Variable + "\", \"static\"); throw new InvalidOperationException(\"the static constructor ran\"); }\n"
                + "    public Bomb() { Environment.SetEnvironmentVariable(\"" + Variable + "\", \"instance\"); throw new InvalidOperationException(\"the constructor ran\"); }\n"
                + "    [Setting] public string Host { get; set; } = \"\";\n}";
            var compiled = ClassCompiled(kind, source);
            Check(compiled.Valid && compiled.Signature!["settings"]!.AsArray().Single()!["key"]!.GetValue<string>() == "Host", $"{kind}: a class that throws when created is valid, with its signature: " + Messages(compiled.Diagnostics));
            Check(Environment.GetEnvironmentVariable(Variable) == null, $"{kind}: nothing of the class ran: " + Environment.GetEnvironmentVariable(Variable));
        }
        Console.WriteLine("PASS class nodes: compiling one never creates its class");
    }

    // A graph's C# device is compiled as a class when the graph is previewed, its problems on its node; the same code in a
    // Custom C# step and in a C# device of the same id are two compiles, not one kept for both.
    static void ClassNodesInAGraph()
    {
        const string plug = "public class Plug : SharpDevice\n{\n    [Setting] public string Address { get; set; } = \"\";\n}";
        static JsonObject Definition(JsonObject node) => new() { ["version"] = 5, ["name"] = "Nodes", ["nodes"] = new JsonArray(node), ["edges"] = new JsonArray(), ["definition"] = new JsonObject { ["settings"] = new JsonArray() } };
        var generator = new VisualGenerator(Diagnostics);
        var asStep = generator.Preview(G(Custom("c", plug)), Empty);
        Check(asStep.Diagnostics.Any(d => d.NodeId == "c" && d.Message == "Mark one method with [Node]: it becomes this node"), "as a step's code the class is refused: " + Messages(asStep.Diagnostics));
        var asDevice = generator.Preview(Definition(EntityNodeTests.CSharpDevice("c", "plug", "Plug", new() { ["source"] = plug })), Empty);
        Check(asDevice.Valid && asDevice.Diagnostics.Count == 0 && asDevice.Source.Contains("\"c\" => new global::NodeLib.Custom_c.Plug(),", StringComparison.Ordinal),
            "the same code in a C# device of the same id is compiled again, as a class: " + Messages(asDevice.Diagnostics));
        var broken = generator.Preview(Definition(EntityNodeTests.CSharpDevice("c", "plug", "Plug", new() { ["source"] = plug.Replace("SharpDevice", "SharpLight") })), Empty);
        Check(broken.Diagnostics.Any(d => d.NodeId == "c" && d.InputId == "source" && d.Message == NodeLibraryCompiler.NoDeviceClassMessage && d.StartLine == 1),
            "a C# device's problems are on its node's code: " + Messages(broken.Diagnostics));
        Console.WriteLine("PASS class nodes in a graph: compiled as classes on preview, apart from a step's code of the same id");
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
        // The editor types a literal as the pin's own type: the refusal still says what fits, not "not a valid Number value".
        literal["nodes"]![1]!["config"]!["arguments"] = new JsonObject { ["value"] = Lit("global::System.Int32", 2.5) };
        fit = VisualGraphValidator.Validate(literal, snapshot);
        Check(fit.Any(d => d.NodeId == "take" && d.InputId == "value" && d.Message == "Amount must be a whole number between -2147483648 and 2147483647")
            && !fit.Any(d => d.Message.Contains("not a valid")), "a fractional literal of the int type itself is refused with the range: " + Messages(fit));
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
        Refused(schedules, "Schedules expression is gone; set the time in Time of day", "t", "schedulesExpression");
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
            // The Node library page's new-file template (tests/fixtures/node-library-template.json, which the frontend
            // keeps equal to its own) compiles with no diagnostic at all: no error and no warning, such as a missing doc comment.
            var template = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "node-library-template.json")))!["source"]!.GetValue<string>();
            var templated = await Library(new { operation = "preview", id = "template", source = template, workspaceVersion = "1" });
            Check(templated["ok"]!.GetValue<bool>() && templated["result"]!["data"]!["valid"]!.GetValue<bool>() && templated["result"]!["data"]!["diagnostics"]!.AsArray().Count == 0,
                "the new-library template compiles without diagnostics: " + templated.ToJsonString()[..Math.Min(1200, templated.ToJsonString().Length)]);
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
            // A C# device's code (kind): its class signature, compiled beside the saved libraries; its class is never created,
            // and a secret's value is not in the answer. Without kind the same code is a step's, as before.
            const string plug = "public class Plug : SharpDevice\n{\n    [Setting(\"Host\")] public string Host { get; set; } = \"\";\n    [Setting(Secret = true)] public string Token { get; set; } = \"hunter2-not-shown\";\n"
                + "    public Plug() { Helper.Twice(1); throw new InvalidOperationException(\"constructed\"); }\n}";
            var classPreview = await Library(new { operation = "custom_preview", nodeId = "p", source = plug, kind = "csharp_device" });
            Check(classPreview["ok"]!.GetValue<bool>() && classPreview["result"]!["valid"]!.GetValue<bool>() && classPreview["result"]!["diagnostics"]!.AsArray().Count == 0
                && VisualGenerator.SameJson(classPreview["result"]!["signature"], JsonNode.Parse("""{"title": "Plug", "description": "", "domain": null, "settings": [{"key": "Host", "label": "Host", "description": "", "type": "string", "secret": false}, {"key": "Token", "label": "Token", "description": "", "type": "string", "secret": true}]}"""))
                && !classPreview.ToJsonString().Contains("hunter2"), "custom_preview with kind csharp_device gives the class signature: " + classPreview.ToJsonString());
            var asStep = await Library(new { operation = "custom_preview", nodeId = "p", source = plug });
            Check(asStep["ok"]!.GetValue<bool>() && !asStep["result"]!["valid"]!.GetValue<bool>() && asStep["result"]!["signature"] == null
                && asStep["result"]!["diagnostics"]!.AsArray().Any(d => d!["message"]!.GetValue<string>() == "Mark one method with [Node]: it becomes this node"), "without kind the code is a step's: " + asStep.ToJsonString());
            var entityProblem = await Library(new { operation = "custom_preview", nodeId = "p", source = plug, kind = "csharp_entity" });
            Check(entityProblem["ok"]!.GetValue<bool>() && !entityProblem["result"]!["valid"]!.GetValue<bool>()
                && entityProblem["result"]!["diagnostics"]!.AsArray().Any(d => d!["message"]!.GetValue<string>() == "Plug is a device: write it in a C# device node" && d["startLine"]!.GetValue<int>() == 1),
                "with kind csharp_entity a device class is refused: " + entityProblem.ToJsonString());
            var unknownKind = await Library(new { operation = "custom_preview", nodeId = "p", source = plug, kind = "device" });
            Check(!unknownKind["ok"]!.GetValue<bool>() && unknownKind["error"]!["detail"]!.GetValue<string>() == "kind must be custom, csharp_entity or csharp_device", "an unknown kind is refused: " + unknownKind.ToJsonString());
            var inClass = plug.IndexOf("Helper.") + "Helper.".Length;
            var classCompletion = await Library(new { operation = "completion", custom = true, nodeId = "p", kind = "csharp_device", source = plug, position = inClass, workspaceVersion = "1" });
            Check(classCompletion["ok"]!.GetValue<bool>() && classCompletion["result"]!["data"]!["items"]!.AsArray().Any(i => i!["displayText"]!.GetValue<string>() == "Twice"), "completion works in a class node's code: " + classCompletion.ToJsonString()[..Math.Min(600, classCompletion.ToJsonString().Length)]);
            var position = source.IndexOf("Helper.") + "Helper.".Length;
            var completion = await Library(new { operation = "completion", custom = true, nodeId = "c", source, position, workspaceVersion = "1" });
            Check(completion["ok"]!.GetValue<bool>() && completion["result"]!["data"]!["items"]!.AsArray().Any(i => i!["displayText"]!.GetValue<string>() == "Twice"), "completion works on a custom node's code: " + completion.ToJsonString()[..Math.Min(600, completion.ToJsonString().Length)]);

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
                // Named as a person knows them, not only by id.
                && breaking["error"]!["detail"]!["automations"]!.AsArray().Select(a => a!["name"]?.GetValue<string>()).SequenceEqual(["Execution"])
                // Each problem names its step by its title, as the editor does, not only by the node's id.
                && breaking["error"]!["detail"]!["automations"]![0]!["diagnostics"]!.AsArray().Where(d => d!["nodeId"]?.GetValue<string>() == "u").Select(d => d!["step"]?.GetValue<string>()).ToArray() is [_, ..] steps && steps.All(step => step == "Check")
                && breaking["error"]!["detail"]!["invalid"]!.AsArray().Select(a => a!["id"]!.GetValue<string>()).SequenceEqual(["derived"])
                && breaking["error"]!["detail"]!["invalid"]!.AsArray().Select(a => a!["name"]?.GetValue<string>()).SequenceEqual(["Execution"]), "a save that breaks a valid graph is refused: " + breaking.ToJsonString()[..Math.Min(1200, breaking.ToJsonString().Length)]);
            var allowed = await Library(new { operation = "save", id = "helpers", name = "Helpers", expectedRevision = 1, source = "public static class Helper { public static int Twice(int value) => value * 2; public static int Thrice(int value) => value * 3; }" });
            Check(allowed["ok"]!.GetValue<bool>() && allowed["result"]!["invalid"]!.AsArray().Select(a => a!["id"]!.GetValue<string>()).SequenceEqual(["derived"])
                && allowed["result"]!["invalid"]!.AsArray().Select(a => a!["name"]?.GetValue<string>()).SequenceEqual(["Execution"])
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
