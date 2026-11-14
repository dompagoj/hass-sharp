using HassSharp;
using System.Text.Json;
using System.Text.Json.Nodes;
using static VisualExecutionTests;

/// <summary>
/// Task 30: catalog type entries carry the members a Select property node can read, and the node reads one of them.
/// </summary>
static class SelectPropertyTests
{
    const string Source = """
        /// <summary>A person who uses Home Assistant.</summary>
        /// <param name="UserName">What the person is called.</param>
        public record User(string UserName, string? Email, int Age);
        public record Admin(string UserName, string? Email, int Age, int Level) : User(UserName, Email, Age);
        public class Room
        {
            public string Name = "kitchen";
            /// <summary>How warm it is.</summary>
            public double? Temperature { get; set; } = 21.5;
            public static int Count { get; set; }
            public const int Limit = 3;
            public string this[int index] => "";
            public string WriteOnly { set { } }
            protected int Hidden { get; set; }
            internal int Inner { get; set; }
        }
        public struct Point { public double X; public double Y; }
        public record Box<T>(T Value, Box<Box<T>>? Inner);
        public record Address(string City);
        public record Holder(Box<int> Box, Address Address);
        public enum Mode { Off, On }
        public static class People
        {
            [Node("User", Pure = true)] public static User User() => new("dompa", null, 40);
            [Node("Admin", Pure = true)] public static Admin Admin() => new("root", "root@example.com", 50, 9);
            [Node("Room", Pure = true)] public static Room Room() => new();
            [Node("Point", Pure = true)] public static Point? Point() => new Point { X = 1, Y = 2 };
            [Node("Boxed", Pure = true)] public static Box<string> Boxed() => new("inside", null);
            [Node("Holder", Pure = true)] public static Holder Holder() => new(new(7, null), new("Zagreb"));
            [Node("Mode", Pure = true)] public static Mode Mode() => global::NodeLib.Mode.On;
            [Node("Nobody", Pure = true)] public static User? Nobody() => null;
        }
        """;

    static NodeLibraryPreview Library() => new NodeLibraryCompiler(Diagnostics).Preview([new("people", Source, 1)]);
    static T Also<T>(this T value, Action<T> change) { change(value); return value; }
    static string Names(JsonNode? properties) => string.Join(",", (properties as JsonArray ?? []).Select(p => $"{p!["name"]}:{p["type"]}{(p["nullable"]!.GetValue<bool>() ? "?" : "")}"));

    [RuntimeSuite("--select-property", Order = 22)]
    public static async Task Run()
    {
        TypeProperties();
        Console.WriteLine("PASS select property: type entries list the members a value of the type has");
        Validation();
        Console.WriteLine("PASS select property: the validator types the output by the property and says what to choose or use instead");
        await Reads();
        Console.WriteLine("PASS select property: a run reads the chosen property of records, classes, structs, fields, entity states and Custom C# types");
    }

    static JsonObject Graph(JsonObject[] nodes, params JsonObject[] edges) => new() { ["version"] = 5, ["name"] = "Select", ["nodes"] = new JsonArray(new[] { N("t", "trigger", new { type = "manual" }) }.Concat(nodes).ToArray<JsonNode>()), ["edges"] = new JsonArray(edges) };
    static JsonObject Select(string id, string property) => N(id, "select_property", new { property });
    static string Kind(VisualCatalogSnapshot snapshot, string method) => snapshot.Methods.Values.Single(m => m.MethodName == method).Kind;
    static JsonObject Made(VisualCatalogSnapshot snapshot, string id, string method) => N(id, Kind(snapshot, method), new { arguments = new { } });
    /// <summary>A Custom C# value node whose code makes a class of its own.</summary>
    const string Making = """
        public class Reading { public double Value { get; init; } public string Unit = "C"; public System.DateTimeOffset At { get; init; } }
        public static class CustomNode { [Node("Make", Pure = true)] public static Reading Make() => new() { Value = 21.5 }; }
        """;
    /// <summary>A step that logs the value at its <c>got</c> pin, as JSON, as "read …".</summary>
    static JsonObject Reader(string id, string parameter) => Code(id, "Logger.Info(\"read \" + System.Text.Json.JsonSerializer.Serialize(got));", parameter);
    static List<VisualDiagnostic> Diagnose(JsonObject graph, VisualCatalogSnapshot snapshot) => new VisualGenerator(Diagnostics).Preview(Derived(graph, snapshot), snapshot).Diagnostics;
    static string Show(IEnumerable<VisualDiagnostic> diagnostics) => JsonSerializer.Serialize(diagnostics.Select(d => new { d.NodeId, d.InputId, d.Code, d.Message }));

    // Step 2: the output has the chosen property's type; what is not chosen, missing or has no properties is said so.
    static void Validation()
    {
        var s = Library().Snapshot;
        JsonObject Reading(string property, string parameter, JsonObject value) => Graph([value, Select("sel", property), Reader("c", parameter)], E("t", "c"), D(value["id"]!.GetValue<string>(), "sel", "value", value["kind"]!.GetValue<string>() == "constant" ? "value" : "result"), D("sel", "c", "got"));
        // The output is typed by the property: text into text is accepted, text into a number is not.
        Check(!Diagnose(Reading("UserName", "string got", Made(s, "u", "User")), s).Any(d => d.Severity == 3), "a chosen property's value is wired on: " + Show(Diagnose(Reading("UserName", "string got", Made(s, "u", "User")), s)));
        var wrong = Diagnose(Reading("UserName", "int got", Made(s, "u", "User")), s);
        Check(wrong.Any(d => d.NodeId == "c" && d.InputId == "got" && d.Severity == 3), "the output has the property's type: " + Show(wrong));
        Check(!Diagnose(Reading("Email", "string? got", Made(s, "u", "User")), s).Any(d => d.Severity == 3), "a property that may be empty keeps its nullability");
        // Nothing chosen: the node asks for a property, and a wire from its output says to choose one.
        var unset = Diagnose(Reading("", "string got", Made(s, "u", "User")), s);
        Check(unset.Any(d => d.NodeId == "sel" && d.InputId == "property" && d.Code == "property.unset" && d.Message == "Choose a property of User"), "no property chosen: " + Show(unset));
        Check(unset.Any(d => d.NodeId == "c" && d.InputId == "got" && d.Code == "property.unset" && d.Message == "Choose a property in Select property to use its value"), "a wire from a node with no property chosen: " + Show(unset));
        // A property the connected type does not have (the wire or the library changed).
        var unknown = Diagnose(Reading("Level", "int got", Made(s, "u", "User")), s);
        Check(unknown.Any(d => d.NodeId == "sel" && d.InputId == "property" && d.Code == "property.unknown" && d.Message == "User has no property Level; choose another"), "a property the type lacks: " + Show(unknown));
        Check(!unknown.Any(d => d.NodeId == "c"), "the reader of a node whose property is missing gets no second error: " + Show(unknown));
        Check(!Diagnose(Reading("Level", "int got", Made(s, "u", "Admin")), s).Any(d => d.Severity == 3), "the same property of a type that has it");
        // A value whose type has no properties, and what to use instead.
        void NoProperties(JsonObject value, string message)
        {
            var found = Diagnose(Reading("UserName", "string got", value), s);
            Check(found.Any(d => d.NodeId == "sel" && d.InputId == "value" && d.Message == message), "expected \"" + message + "\": " + Show(found));
        }
        NoProperties(Made(s, "m", "Mode"), "Mode has no properties to select; connect a value that has some, such as a record from a node library");
        NoProperties(N("o", "constant", new { type = "object", value = Lit("object", new { name = "x" }) }), "Object has no properties to select; use Get field to take a value out of an object");
        NoProperties(N("o", "constant", new { type = "array", value = Lit("array", new[] { 1 }) }), "List has no properties to select; use Get field to take a value out of a list");
        NoProperties(N("o", "constant", new { type = "entity_id", value = Lit("entity_id", "sensor.x") }), "Entity has no properties to select; use Read entity to get its state");
        NoProperties(N("o", "constant", new { type = "string", value = Lit("string", "x") }), "Text has no properties to select; connect a value that has some, such as a record from a node library");
        // Nothing connected: the value is required.
        var empty = Diagnose(Graph([Select("sel", ""), Reader("c", "string got")], E("t", "c"), D("sel", "c", "got")), s);
        Check(empty.Any(d => d.NodeId == "sel" && d.InputId == "value" && d.Severity == 3) && !empty.Any(d => d.NodeId == "sel" && d.InputId == "property"), "an unconnected value is required, nothing is chosen: " + Show(empty));
        // A Custom C# node's property: a change of its type in the code makes a stored signature stale; a new description does not.
        var custom = Derived(Graph([N("mk", "custom", new JsonObject { ["source"] = Making, ["arguments"] = new JsonObject() }), Select("sel", "Unit"), Reader("c", "string got")], E("t", "c"), D("mk", "sel", "value", "result"), D("sel", "c", "got")), s);
        Check(!Diagnose(custom, s).Any(d => d.Severity == 3), "a Custom C# type's property is read: " + Show(Diagnose(custom, s)));
        JsonObject Stored(JsonObject graph) => graph["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "mk")!["config"]!["signature"]!["types"]!["global::NodeLib.Custom_mk.Reading"]!["properties"]!.AsArray().OfType<JsonObject>().Single(p => p["name"]!.GetValue<string>() == "Unit");
        var stale = (JsonObject)custom.DeepClone(); Stored(stale)["type"] = "global::System.Int32";
        var staleFound = new VisualGenerator(Diagnostics).Preview(stale, s).Diagnostics;
        Check(staleFound.Any(d => d.NodeId == "mk" && d.Code == "custom.signature"), "a stored property type the code no longer gives is stale: " + Show(staleFound));
        var worded = (JsonObject)custom.DeepClone(); Stored(worded)["description"] = "Old words";
        var refreshed = new VisualGenerator(Diagnostics).Preview(worded, s);
        Check(refreshed.Valid && refreshed.Graph != null && Stored(refreshed.Graph)["description"] == null, "a stored property's words are refreshed from the code: " + Show(refreshed.Diagnostics));
        var other = (JsonObject)custom.DeepClone(); other["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "mk")!["config"]!["signature"]!["types"]!["global::NodeLib.Custom_mk.Reading"]!["properties"]!.AsArray().OfType<JsonObject>().Single(p => p["name"]!.GetValue<string>() == "At")["type"] = "global::System.Int32";
        Check(new VisualGenerator(Diagnostics).Preview(other, s).Valid, "a property no node reads is not compared");
    }

    // Step 2: what a run reads, through each kind of member and value, and where a value is read again.
    static async Task Reads()
    {
        var s = Library().Snapshot;
        var previous = HassPath.HassConfiguration; HassPath.HassConfiguration = Path.Combine(Path.GetTempPath(), "select-property-" + Guid.NewGuid());
        var log = PyInterop.Log; var entity = PyInterop.Entity; var messages = new System.Collections.Concurrent.ConcurrentQueue<string>(); PyInterop.Log = (_, m) => messages.Enqueue(m);
        PyInterop.Entity = id => new() { EntityId = id, State = "on", Attributes = new() { ["temperature"] = 21 } };
        try
        {
            async Task Expect(string expected, JsonObject graph)
            {
                messages.Clear(); await Execute(graph, s);
                var read = messages.Where(m => m.StartsWith("read ", StringComparison.Ordinal)).ToArray();
                Check(read.Length > 0 && read.All(m => m == "read " + expected), $"expected read {expected}, got {string.Join(" | ", messages)}");
            }
            // One value node, its property, a step reading it.
            Task One(string method, string property, string parameter, string expected) =>
                Expect(expected, Graph([Made(s, "v", method), Select("sel", property), Reader("c", parameter)], E("t", "c"), D("v", "sel", "value", "result"), D("sel", "c", "got")));
            await One("User", "UserName", "string got", "\"dompa\"");
            await One("User", "Email", "string? got", "null");
            await One("User", "Age", "int got", "40");
            await One("Admin", "UserName", "string got", "\"root\"");
            await One("Admin", "Level", "int got", "9");
            await One("Room", "Name", "string got", "\"kitchen\"");
            await One("Room", "Temperature", "double? got", "21.5");
            await One("Point", "Y", "double got", "2");
            await One("Boxed", "Value", "string got", "\"inside\"");
            // A property of a property.
            await Expect("\"Zagreb\"", Graph([Made(s, "v", "Holder"), Select("a", "Address"), Select("sel", "City"), Reader("c", "string got")], E("t", "c"), D("v", "a", "value", "result"), D("a", "sel", "value"), D("sel", "c", "got")));
            await Expect("7", Graph([Made(s, "v", "Holder"), Select("a", "Box"), Reader("c", "global::NodeLib.Box<int> got")], E("t", "c"), D("v", "a", "value", "result"), D("a", "c", "got")).Also(g => g["nodes"]![3]!["config"]!["source"] = CodeSource("Logger.Info(\"read \" + got.Value);", "global::NodeLib.Box<int> got")));
            // An entity state, as Read entity gives it.
            await Expect("\"on\"", Graph([N("e", "read_entity", new { entity = Lit("entity_id", "sensor.test") }), Select("sel", "State"), Reader("c", "string got")], E("t", "e"), E("e", "c"), D("e", "sel", "value"), D("sel", "c", "got")));
            await Expect("\"sensor.test\"", Graph([N("e", "read_entity", new { entity = Lit("entity_id", "sensor.test") }), Select("sel", "EntityId"), Reader("c", "string got")], E("t", "e"), E("e", "c"), D("e", "sel", "value"), D("sel", "c", "got")));
            // A Custom C# node's own class: a field and a property.
            JsonObject Making(string property, string parameter) => Graph([N("mk", "custom", new JsonObject { ["source"] = SelectPropertyTests.Making, ["arguments"] = new JsonObject() }), Select("sel", property), Reader("c", parameter)], E("t", "c"), D("mk", "sel", "value", "result"), D("sel", "c", "got"));
            await Expect("\"C\"", Making("Unit", "string got"));
            await Expect("21.5", Making("Value", "double got"));
            // Read again in each iteration and each branch.
            messages.Clear();
            await Execute(Graph([Made(s, "v", "User"), Select("sel", "UserName"), N("loop", "repeat", new { count = Lit("number", 3) }), Reader("c", "string got")], E("t", "loop"), E("loop", "c", "body"), D("v", "sel", "value", "result"), D("sel", "c", "got")), s);
            Check(messages.Count(m => m == "read \"dompa\"") == 3, "read in each iteration: " + string.Join(" | ", messages));
            messages.Clear();
            await Execute(Graph([Made(s, "v", "User"), Select("sel", "UserName"), N("p", "parallel", new { branches = new[] { "a", "b" } }), Reader("c", "string got"), Reader("d", "string got")], E("t", "p"), E("p", "c", "branch:a"), E("p", "d", "branch:b"), D("v", "sel", "value", "result"), D("sel", "c", "got"), D("sel", "d", "got")), s);
            Check(messages.Count(m => m == "read \"dompa\"") == 2, "read in each branch: " + string.Join(" | ", messages));
            // An empty value fails the step that reads the node, naming the node and its property.
            messages.Clear();
            await Execute(Graph([Made(s, "v", "Nobody"), Select("sel", "UserName"), Reader("c", "string got"),
                Code("err", "if(!lastError.Message.Contains(\"Select property · UserName: the value is empty\")) throw new Exception(lastError.Message); Logger.Info(\"empty caught\");")],
                E("t", "c"), E("c", "err", "error"), D("v", "sel", "value", "result"), D("sel", "c", "got")), s);
            Check(messages.Contains("empty caught") && !messages.Any(m => m.StartsWith("read ", StringComparison.Ordinal)), "an empty value fails the reading step: " + string.Join(" | ", messages));
        }
        finally { PyInterop.Log = log; PyInterop.Entity = entity; HassPath.HassConfiguration = previous; }
    }

    // Step 1: the members of each class, record and struct of a library, a Custom C# node and HassSharp, as the compiler finds them.
    static void TypeProperties()
    {
        var preview = Library();
        Check(preview.Valid, "the people library compiles: " + string.Join("; ", preview.Diagnostics.Select(d => d.Message)));
        var types = preview.Snapshot.Catalog["types"]!.AsObject();
        JsonNode? Of(string id) => types[id] is JsonObject entry ? entry["properties"] : throw new Exception("the catalog has no type " + id);
        // A record lists its positional properties in order, described by the record's param; no EqualityContract.
        Check(Names(Of("global::NodeLib.User")) == "UserName:global::System.String,Email:global::System.String??,Age:global::System.Int32", "a record's properties: " + Of("global::NodeLib.User")?.ToJsonString());
        Check(Of("global::NodeLib.User")![0]!["description"]?.GetValue<string>() == "What the person is called." && Of("global::NodeLib.User")![1]!["description"] == null,
            "a positional property is described by the record's param: " + Of("global::NodeLib.User")?.ToJsonString());
        // Inherited members come first, then the type's own.
        Check(Names(Of("global::NodeLib.Admin")) == "UserName:global::System.String,Email:global::System.String??,Age:global::System.Int32,Level:global::System.Int32", "inherited properties first: " + Of("global::NodeLib.Admin")?.ToJsonString());
        // Public instance fields and readable properties only: no static, constant, indexer, write-only, protected or internal member.
        Check(Names(Of("global::NodeLib.Room")) == "Name:global::System.String,Temperature:global::System.Double??", "a class's fields and readable properties: " + Of("global::NodeLib.Room")?.ToJsonString());
        Check(Of("global::NodeLib.Room")![1]!["description"]?.GetValue<string>() == "How warm it is.", "a property is described by its summary");
        // A struct that may be empty reads the struct's members.
        Check(Names(Of("global::NodeLib.Point?")) == "X:global::System.Double,Y:global::System.Double" && Names(Of("global::NodeLib.Point")) == Names(Of("global::NodeLib.Point?")), "a nullable struct has the struct's fields: " + Of("global::NodeLib.Point?")?.ToJsonString());
        Check(Names(Of("global::NodeLib.User?")) == Names(Of("global::NodeLib.User")), "a record that may be empty has the record's properties");
        // A generic type a pin names has its properties; one first met as a property's type is past the bound: listed, no properties.
        Check(Names(Of("global::NodeLib.Box<global::System.String>")) == "Value:global::System.String,Inner:global::NodeLib.Box<global::NodeLib.Box<global::System.String>>??", "a generic type a pin names: " + Of("global::NodeLib.Box<global::System.String>")?.ToJsonString());
        Check(types["global::NodeLib.Box<global::NodeLib.Box<global::System.String>>?"] is JsonObject past && past["properties"] == null, "a generic type met through a property is listed without properties");
        Check(Names(Of("global::NodeLib.Holder")) == "Box:global::NodeLib.Box<global::System.Int32>,Address:global::NodeLib.Address" && Of("global::NodeLib.Box<global::System.Int32>") == null,
            "a property of a generic type: the type is listed, past the bound");
        // A property's type joins the catalog as a pin's does, with its own properties when it is not generic.
        Check(Names(Of("global::NodeLib.Address")) == "City:global::System.String", "a property's record type has its properties: " + Of("global::NodeLib.Address")?.ToJsonString());
        // Enums, built-in scalars, JSON values and the base library's types have none.
        foreach (var id in new[] { "global::NodeLib.Mode", "global::System.String", "string", "number", "object", "array", "global::System.Int32", "global::System.Text.Json.Nodes.JsonObject" })
            Check(types[id] is JsonObject none && none["properties"] == null, id + " has no properties");
        // HassSharp's own types are read the same way, not written by hand; their property types join the catalog.
        var state = Of("global::HassSharp.EntityStateSnapshot");
        Check(Names(state) == "EntityId:global::System.String,State:global::System.String,Attributes:global::System.Text.Json.JsonElement,LastChanged:global::System.DateTimeOffset,LastUpdated:global::System.DateTimeOffset??,LastReported:global::System.DateTimeOffset",
            "an entity state's properties: " + state?.ToJsonString());
        foreach (var property in state!.AsArray()) Check(types.ContainsKey(property!["type"]!.GetValue<string>()), "the type of " + property["name"] + " is a catalog type");
        var builtIn = new NodeLibraryCompiler(Diagnostics).Preview([]).Snapshot.Catalog["types"]!.AsObject();
        Check(Names(builtIn["global::HassSharp.EntityStateSnapshot"]!["properties"]) == Names(state), "an entity state has its properties without any library");
        Check(VisualNodeCatalog.Root["types"]!["global::HassSharp.EntityStateSnapshot"]!["properties"] == null, "the static catalog is not given properties by hand");

        // A Custom C# node's own types carry their properties in its signature.
        var custom = new NodeLibraryCompiler(Diagnostics).Preview([], [new CustomNodeSource("make", """
            public class Reading { public double Value { get; init; } public string Unit = "C"; public System.DateTimeOffset At { get; init; } }
            public static class CustomNode { [Node("Make", Pure = true)] public static Reading Make() => new() { Value = 21.5 }; }
            """)]).Snapshot.Custom["make"];
        Check(custom.Valid, "the custom node compiles: " + string.Join("; ", custom.Diagnostics.Select(d => d.Message)));
        var own = custom.Signature!["types"]!.AsObject();
        Check(Names(own["global::NodeLib.Custom_make.Reading"]?["properties"]) == "Value:global::System.Double,Unit:global::System.String,At:global::System.DateTimeOffset", "a custom type's properties are in the signature: " + own.ToJsonString()[..Math.Min(600, own.ToJsonString().Length)]);
        Check(own.ContainsKey("global::System.DateTimeOffset") && !own.ContainsKey("global::System.String"), "a property's type beyond the static catalog is listed in the signature, a built-in one is not: " + string.Join(", ", own.Select(p => p.Key)));
    }
}
