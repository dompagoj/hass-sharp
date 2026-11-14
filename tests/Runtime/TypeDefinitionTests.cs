using HassSharp;
using System.Text.Json;
using System.Text.Json.Nodes;
using static VisualExecutionTests;

/// <summary>
/// Task 31: the node library operation <c>type_definition</c>, which the panel's C# type links open: a type declared in a
/// node library or a Custom C# node's code as written, a type of HassSharp rebuilt from metadata, and a refusal for C#'s
/// own types. Also: a preview answers <c>graph.limit</c> before it compiles any Custom C# code.
/// </summary>
static class TypeDefinitionTests
{
    // The declarations a definition must give back exactly as written (doc comments included, indentation removed).
    const string PersonText = """
        /// <summary>A person who uses Home Assistant.</summary>
        /// <param name="Name">What the person is called.</param>
        [System.Serializable]
        public record Person(string Name, int Age);
        """;
    const string ModeText = """
        /// <summary>How a light is set.</summary>
        public enum Mode
        {
            /// <summary>Not lit.</summary>
            Off,
            On = 4,
        }
        """;
    const string FirstPart = """
        /// <summary>A type in two parts.</summary>
        public partial class Split
        {
            public int A { get; set; }
        }
        """;
    const string SecondPart = """
        public partial class Split
        {
            public int B { get; set; }
        }
        """;
    const string InnerText = """
        /// <summary>Declared inside another type.</summary>
        public record Inner(int X)
        {
            public int Twice => X * 2;
        }
        """;
    static string Indent(string text) => string.Join("\n", text.Split('\n').Select(line => line.Length == 0 ? line : "    " + line));
    static string Library => PersonText + "\n\n// A comment that is not the enum's.\n" + ModeText + "\n" + FirstPart + "\n" + SecondPart + "\n" + """
        public static class Outer
        {

        """ + Indent(InnerText) + "\n" + """
        }
        public record Box<T>(T Value);
        public static class Nodes
        {
            [Node("Make")] public static Person Make(Mode mode, Split split, Outer.Inner inner, Box<string> box, System.Collections.Generic.List<Person> people, Person? maybe) => new("a", 1);
        }
        """;
    const string CustomCode = """
        /// <summary>One reading.</summary>
        public record Reading(double Value);
        public static class CustomNode
        {
            [Node("Read", Pure = true)] public static Reading Read() => new(1);
        }
        """;

    [RuntimeSuite("--type-definition", Order = 23)]
    public static async Task Run()
    {
        await Definitions();
        Console.WriteLine("PASS type definition: library, Custom C# and HassSharp types are shown as declared or from metadata; C#'s own types and unknown ids are refused");
        LimitFirst();
        Console.WriteLine("PASS graph limit first: preview, deriving signatures and the entity check answer a graph past the limits, or of the wrong shape, without compiling or walking it");
        DeepSupertypes();
        Console.WriteLine("PASS supertypes nested deeper than a pin may be are not registered or compared, for libraries and Custom C# nodes");
        MetadataDetails();
        Console.WriteLine("PASS metadata definitions: summaries keep a see's own words, a failed documentation read gives none, overloads keep their own summaries, interfaces list only public members");
        MissingDocComments();
        Console.WriteLine("PASS missing doc comments are reported only on a node library's [Node] methods, and not in Custom C# code");
    }

    // A pin's type may nest 8 levels; a base type or interface of it nested deeper is left out, as a member of such a type is.
    static void DeepSupertypes()
    {
        const string Deep = "List<List<List<List<List<List<List<List<int>>>>>>>>";
        var types = $$"""
            public interface IDeep<T> { }
            public class Base<T> { }
            public class Shallow : Base<{{Deep}}>, IDeep<{{Deep}}>, System.IComparable { public int CompareTo(object? other) => 0; }
            """;
        var library = new NodeLibraryCompiler(Diagnostics).Preview([new("deep", types + "public static class DeepNodes { [Node(\"Shallow\")] public static Shallow Make() => new(); }", 1)]);
        Check(library.Valid, "the library compiles: " + string.Join(" | ", library.Diagnostics.Select(d => d.Message)));
        var ids = library.Snapshot.Types.Keys.ToArray();
        Check(ids.Contains("global::NodeLib.Shallow") && ids.Contains("global::System.IComparable"), "the pin's type and a shallow interface are registered: " + string.Join(", ", ids.Where(i => i.Contains("NodeLib"))));
        Check(!ids.Any(i => i.StartsWith("global::NodeLib.IDeep", StringComparison.Ordinal) || i.StartsWith("global::NodeLib.Base", StringComparison.Ordinal)), "deep supertypes are not registered: " + string.Join(", ", ids.Where(i => i.Contains("NodeLib"))));
        var entry = library.Snapshot.Catalog["types"]!["global::NodeLib.Shallow"]!;
        Check(!entry["assignableTo"]!.AsArray().Any(t => t!.GetValue<string>().Contains("IDeep") || t!.GetValue<string>().Contains("Base<")), "nor compared for conversions: " + entry["assignableTo"]!.ToJsonString());
        // A Custom C# node's own type: its signature's entry lists no such supertype either.
        var custom = new NodeLibraryCompiler(Diagnostics).Preview([], [new CustomNodeSource("d", types + "public static class CustomNode { [Node(\"Shallow\", Pure = true)] public static Shallow Make() => new(); }")]).Snapshot.Custom["d"];
        Check(custom.Signature != null, "the node's code compiles: " + string.Join(" | ", custom.Diagnostics.Select(d => d.Message)));
        var own = custom.Signature!["types"]!.AsObject();
        Check(!own.Any(t => t.Key.Contains("IDeep") || t.Key.Contains(".Base<")) && !own["global::NodeLib.Custom_d.Shallow"]!["assignableTo"]!.AsArray().Any(t => t!.GetValue<string>().Contains("IDeep") || t!.GetValue<string>().Contains("Base<")),
            "a Custom C# type lists no deep supertype: " + own.ToJsonString()[..Math.Min(1500, own.ToJsonString().Length)]);
    }

    // The parts of a metadata definition a HassSharp type of today does not show: read through the same code on a type
    // compiled here, with its summaries given by documentation id.
    static void MetadataDetails()
    {
        // A summary's words: a see with words of its own gives them; an empty one gives the last part of what it names.
        var summary = System.Xml.Linq.XElement.Parse("<summary>Reads <see cref=\"T:HassSharp.EntityStateSnapshot\">the entity state</see> and <see cref=\"M:HassSharp.X.Run(System.Int32)\"/>, see <seealso href=\"https://x\">the docs</seealso>.</summary>");
        var words = TypeDefinitions.Words(summary);
        Check(words == "Reads the entity state and Run, see the docs.", "a see keeps its own words: " + words);
        // A documentation file that cannot be read for any reason gives no summaries, never an error.
        Check(TypeDefinitions.ReadSummaries(() => throw new NotSupportedException("odd location")).Count == 0, "a failed documentation read gives no summaries");
        var read = TypeDefinitions.ReadSummaries(() => new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<doc><members><member name=\"T:A\"><summary>An A.</summary></member></members></doc>")));
        Check(read.Count == 1 && read["T:A"] == "An A.", "a documentation file is read: " + string.Join(", ", read));

        var code = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("""
            namespace Shown;
            public interface IThing
            {
                void Visible();
                int Count { get; }
                private void Hidden() { }
                internal int Secret => 1;
                protected static void Guarded() { }
            }
            public class Overloads
            {
                public void Run() { }
                public void Run(int times) { }
            }
            """);
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Shown", [code], NodeLibraryCompiler.FrameworkReferences());
        var thing = compilation.GetTypeByMetadataName("Shown.IThing")!;
        var (interfaceText, _) = TypeDefinitions.MetadataText(thing, new Dictionary<string, string>(), CancellationToken.None);
        Check(interfaceText.Contains("void Visible();", StringComparison.Ordinal) && interfaceText.Contains("int Count { get; }", StringComparison.Ordinal)
            && !interfaceText.Contains("Hidden", StringComparison.Ordinal) && !interfaceText.Contains("Secret", StringComparison.Ordinal) && !interfaceText.Contains("Guarded", StringComparison.Ordinal),
            "an interface lists only its public members: " + interfaceText);
        var overloads = compilation.GetTypeByMetadataName("Shown.Overloads")!;
        var summaries = new Dictionary<string, string> { ["M:Shown.Overloads.Run"] = "Runs once.", ["M:Shown.Overloads.Run(System.Int32)"] = "Runs a number of times." };
        var (overloadText, _) = TypeDefinitions.MetadataText(overloads, summaries, CancellationToken.None);
        Check(overloadText.Contains("    /// <summary>Runs once.</summary>\n    public void Run();", StringComparison.Ordinal)
            && overloadText.Contains("    /// <summary>Runs a number of times.</summary>\n    public void Run(int times);", StringComparison.Ordinal), "each overload has its own summary: " + overloadText);
    }

    // A missing doc comment matters only on a library's [Node] method (its summary is the node's description in the
    // editor): an undocumented record, its properties, its parameters or a class are not reported. The same compile gives
    // the editor's live diagnostics and a save's, so one check covers both.
    static void MissingDocComments()
    {
        const string Code = """
            public record Address(string City, int Zip);
            public record User(string UserName, int Age, Address Home)
            {
                public string Label => UserName;
            }
            public static class Users
            {
                [Node("Make user")] public static User MakeUser(string name) => new(name, 1, new("Zagreb", 10000));
                /// <summary>Documented.</summary>
                [Node("Documented")] public static void Documented(int count) { }
                public static int Helper() => 1;
            }
            """;
        static string[] Missing(IEnumerable<NodeLibraryDiagnostic> diagnostics) => diagnostics.Where(d => d.Message.StartsWith("Missing XML comment", StringComparison.Ordinal)).Select(d => $"{d.StartLine}: {d.Message}").ToArray();
        var library = new NodeLibraryCompiler(Diagnostics).Preview([new("users", Code, 1)]);
        var missing = Missing(library.Diagnostics);
        Check(missing.Length == 1 && missing[0].StartsWith("8: ", StringComparison.Ordinal) && missing[0].Contains("MakeUser", StringComparison.Ordinal), "one warning, on the undocumented [Node] method: " + string.Join(" | ", missing));
        var custom = new NodeLibraryCompiler(Diagnostics).Preview([], [new CustomNodeSource("u", Code.Replace("[Node(\"Documented\")]", "").Replace("Users", "CustomNode"))]).Snapshot.Custom["u"];
        var customMissing = Missing(custom.Diagnostics);
        // A Custom C# node's code is not asked to document itself at all (its card says what it is).
        Check(customMissing.Length == 0, "none in a Custom C# node's code: " + string.Join(" | ", customMissing));
    }

    static async Task Definitions()
    {
        var path = Path.Combine(HassPath.HassConfiguration, "types-" + Guid.NewGuid()); var previous = HassPath.HassConfiguration; HassPath.HassConfiguration = path;
        var manager = new HassSharpManager(path);
        try
        {
            await manager.Init();
            async Task<JsonNode> Library(object request) => JsonNode.Parse(await manager.NodeLibraryRequest(JsonSerializer.Serialize(request)))!;
            var saved = await Library(new { operation = "save", id = "people", name = "People.cs", expectedRevision = 0, source = TypeDefinitionTests.Library });
            Check(saved["ok"]!.GetValue<bool>(), "the library saves: " + saved.ToJsonString()[..Math.Min(1500, saved.ToJsonString().Length)]);
            async Task<JsonNode> Definition(object request)
            {
                var answer = await Library(request);
                Check(answer["ok"]!.GetValue<bool>(), "type_definition answers: " + answer.ToJsonString());
                return answer["result"]!;
            }
            async Task<(string Code, string Detail)> Refused(object request)
            {
                var answer = await Library(request);
                Check(!answer["ok"]!.GetValue<bool>(), "type_definition refuses: " + answer.ToJsonString()[..Math.Min(800, answer.ToJsonString().Length)]);
                return (answer["error"]!["code"]!.GetValue<string>(), answer["error"]!["detail"]!.GetValue<string>());
            }
            int Line(string text) => TypeDefinitionTests.Library.Replace("\r\n", "\n")[..TypeDefinitionTests.Library.Replace("\r\n", "\n").IndexOf(text.Split('\n')[0], StringComparison.Ordinal)].Count(c => c == '\n') + 1;

            // A record, as written: its doc comment and attribute too, where it is declared and on which line.
            var person = await Definition(new { operation = "type_definition", type = "global::NodeLib.Person" });
            Check(person["kind"]!.GetValue<string>() == "source" && person["name"]!.GetValue<string>() == "Person", "a library record is from source: " + person.ToJsonString());
            Check(person["text"]!.GetValue<string>() == PersonText, "a record's text is its declaration as written: " + person["text"]);
            Check(person["origin"]!["library"]!.GetValue<string>() == "people" && person["origin"]!["file"]!.GetValue<string>() == "People.cs" && person["origin"]!["nodeId"] == null && person["origin"]!["assembly"] == null,
                "a library type says which library declares it: " + person["origin"]!.ToJsonString());
            Check(person["line"]!.GetValue<int>() == 1, "the line where the declaration starts, in the library's own code: " + person["line"]);
            Check(person["truncated"] == null || !person["truncated"]!.GetValue<bool>(), "a short definition is whole");
            // An enum with its member docs and values, without the comment before its doc comment.
            var mode = await Definition(new { operation = "type_definition", type = "global::NodeLib.Mode" });
            Check(mode["text"]!.GetValue<string>() == ModeText && mode["line"]!.GetValue<int>() == Line(ModeText), "an enum's text is its declaration as written: " + mode.ToJsonString());
            // A partial type: every part, in order.
            var split = await Definition(new { operation = "type_definition", type = "global::NodeLib.Split" });
            Check(split["text"]!.GetValue<string>() == FirstPart + "\n\n" + SecondPart, "a partial type gives all its parts: " + split["text"]);
            // A nested type, indented in the file: the indentation is taken off, and its name says where it is.
            var inner = await Definition(new { operation = "type_definition", type = "global::NodeLib.Outer.Inner" });
            Check(inner["text"]!.GetValue<string>() == InnerText && inner["name"]!.GetValue<string>() == "Outer.Inner", "a nested type's text is dedented: " + inner.ToJsonString());
            // A constructed generic type is shown by its definition; the part of a composed type that names a library type works.
            var box = await Definition(new { operation = "type_definition", type = "global::NodeLib.Box<global::System.String>" });
            Check(box["text"]!.GetValue<string>() == "public record Box<T>(T Value);" && box["name"]!.GetValue<string>() == "Box<T>", "a constructed generic type shows its definition: " + box.ToJsonString());
            var maybe = await Definition(new { operation = "type_definition", type = "global::NodeLib.Person?" });
            Check(maybe["text"]!.GetValue<string>() == PersonText, "a nullable reference to a library type shows that type: " + maybe.ToJsonString());

            // A type a Custom C# node declares, from the code passed in (the editor's, applied or not), with no namespace wrapper.
            var reading = await Definition(new { operation = "type_definition", type = "global::NodeLib.Custom_c.Reading", nodeId = "c", source = CustomCode });
            Check(reading["kind"]!.GetValue<string>() == "source" && reading["text"]!.GetValue<string>() == "/// <summary>One reading.</summary>\npublic record Reading(double Value);"
                && reading["origin"]!["nodeId"]!.GetValue<string>() == "c" && reading["origin"]!["library"] == null && reading["line"]!.GetValue<int>() == 1, "a Custom C# node's own type is from its code: " + reading.ToJsonString());
            // Changed code is what is read: the same type, now with another member.
            var changed = await Definition(new { operation = "type_definition", type = "global::NodeLib.Custom_c.Reading", nodeId = "c", source = CustomCode.Replace("(double Value)", "(double Value, string Unit)") });
            Check(changed["text"]!.GetValue<string>().EndsWith("public record Reading(double Value, string Unit);", StringComparison.Ordinal), "the unapplied code passed in is the one read: " + changed["text"]);
            // A library type asked for beside a node's code is still the library's.
            var fromNode = await Definition(new { operation = "type_definition", type = "global::NodeLib.Person", nodeId = "c", source = CustomCode });
            Check(fromNode["origin"]!["library"]!.GetValue<string>() == "people", "a library type beside a node's code: " + fromNode.ToJsonString());
            // Code that does not compile still has its declarations.
            var broken = await Definition(new { operation = "type_definition", type = "global::NodeLib.Custom_c.Reading", nodeId = "c", source = CustomCode.Replace("=> new(1);", "=> ;") });
            Check(broken["text"]!.GetValue<string>().EndsWith("public record Reading(double Value);", StringComparison.Ordinal), "a type in code with errors: " + broken.ToJsonString());
            var (nodeCode, nodeDetail) = await Refused(new { operation = "type_definition", type = "global::NodeLib.Custom_c.Reading", nodeId = "c" });
            Check(nodeCode == "invalid_request" && nodeDetail.StartsWith("source is required", StringComparison.Ordinal), "a node's id comes with its code: " + nodeDetail);
            var (withoutCode, withoutDetail) = await Refused(new { operation = "type_definition", type = "global::NodeLib.Custom_c.Reading" });
            Check(withoutCode == "not_found" && withoutDetail == "There is no type NodeLib.Custom_c.Reading; it may have been renamed or removed", "a node's type without its code is unknown: " + withoutDetail);

            // HassSharp's own type, rebuilt from metadata: its kind, its public members with their signatures, and the
            // summaries the published documentation file has.
            var entity = await Definition(new { operation = "type_definition", type = "global::HassSharp.EntityStateSnapshot" });
            var text = entity["text"]!.GetValue<string>();
            Check(entity["kind"]!.GetValue<string>() == "metadata" && entity["name"]!.GetValue<string>() == "EntityStateSnapshot" && entity["origin"]!["assembly"]!.GetValue<string>() == "HassSharp" && entity["line"] == null,
                "HassSharp's type is from metadata: " + entity.ToJsonString()[..Math.Min(400, entity.ToJsonString().Length)]);
            foreach (var expected in (string[])["namespace HassSharp;", "public sealed class EntityStateSnapshot", "public string EntityId { get; }", "public DateTimeOffset? LastUpdated { get; }",
                "public JsonElement Attributes { get; }", "public EntityStateSnapshot(string entityId, string state, JsonElement attributes, DateTimeOffset lastChanged, DateTimeOffset lastReported, DateTimeOffset? lastUpdated = null);",
                "public static EntityStateSnapshot FromStateJson(JsonElement state);",
                "/// <summary>A detached, immutable Home Assistant state. Attributes contain JSON values, never Python handles.</summary>"])
                Check(text.Contains(expected, StringComparison.Ordinal), $"EntityStateSnapshot's definition has {expected}: {text}");
            Check(!text.Contains("NormalizeAttributes", StringComparison.Ordinal) && !text.Contains("get_EntityId", StringComparison.Ordinal) && !text.Contains("=>", StringComparison.Ordinal) && !text.Contains("return ", StringComparison.Ordinal),
                "only public members, no accessors as methods and no bodies: " + text);
            // A record of HassSharp: its primary constructor in the declaration, no compiler-made members.
            var source = await Definition(new { operation = "type_definition", type = "global::HassSharp.NodeLibrarySource" });
            Check(source["text"]!.GetValue<string>().Contains("public sealed record NodeLibrarySource(string Id, string Source, long Revision)", StringComparison.Ordinal)
                && !source["text"]!.GetValue<string>().Contains("EqualityContract", StringComparison.Ordinal) && !source["text"]!.GetValue<string>().Contains("Deconstruct", StringComparison.Ordinal)
                && !source["text"]!.GetValue<string>().Contains("<Clone>", StringComparison.Ordinal), "a HassSharp record has its primary constructor: " + source["text"]);
            foreach (var made in (string[])["ToString", "Equals", "GetHashCode", "PrintMembers", "op_Equality", "operator =="])
                Check(!source["text"]!.GetValue<string>().Contains(made, StringComparison.Ordinal), $"a HassSharp record lists none of the compiler's {made}: " + source["text"]);
            // An enum of HassSharp: its members with their values.
            var level = await Definition(new { operation = "type_definition", type = "global::HassSharp.PyLogLevel" });
            Check(level["text"]!.GetValue<string>().Contains("public enum PyLogLevel", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(level["text"]!.GetValue<string>(), @"\n    Info = \d+,"),
                "a HassSharp enum lists its members with values: " + level["text"]);

            // C#'s own types: keywords, the System namespaces, and composed types of them, are refused, saying why.
            foreach (var (type, name) in new[] { ("global::System.String", "string"), ("boolean", "bool"), ("number", "double"), ("global::System.Text.Json.Nodes.JsonObject", "JsonObject"),
                ("global::System.Collections.Generic.List<global::NodeLib.Person>", "List<Person>"), ("global::System.Int32?", "int?") })
            {
                var (code, detail) = await Refused(new { operation = "type_definition", type });
                Check(code == "invalid_request" && detail == $"{name} is a built-in C# type, so it has no definition to show", $"{type} is refused as built in: {code} {detail}");
            }
            // A type of another assembly that a pin uses (not System's, not HassSharp's): refused, saying which types have one.
            // An array of a library type: refused, pointing to the type of its items.
            var extra = "public static class ExtraNodes { [Node(\"Handle\")] public static void Handle(Microsoft.Win32.SafeHandles.SafeFileHandle handle) { } [Node(\"Modes\")] public static void Modes(Mode[] modes) { } }\n";
            Check((await Library(new { operation = "save", id = "extra", name = "Extra.cs", expectedRevision = 0, source = extra }))["ok"]!.GetValue<bool>(), "the extra library saves");
            var (otherCode, otherDetail) = await Refused(new { operation = "type_definition", type = "global::Microsoft.Win32.SafeHandles.SafeFileHandle" });
            Check(otherCode == "invalid_request" && otherDetail == "SafeFileHandle has no definition to show: only types of node libraries, Custom C# nodes and HassSharp have one", "another assembly's type: " + otherCode + " " + otherDetail);
            var (arrayCode, arrayDetail) = await Refused(new { operation = "type_definition", type = "global::NodeLib.Mode[]" });
            Check(arrayCode == "invalid_request" && arrayDetail == "Mode[] is an array: ask for Mode, the type of its items", "an array of a library type: " + arrayCode + " " + arrayDetail);
            var (unknownCode, unknownDetail) = await Refused(new { operation = "type_definition", type = "global::NodeLib.Nobody" });
            Check(unknownCode == "not_found" && unknownDetail == "There is no type NodeLib.Nobody; it may have been renamed or removed", "an unknown id: " + unknownDetail);
            var (emptyCode, _) = await Refused(new { operation = "type_definition" });
            Check(emptyCode == "invalid_request", "type is required");

            // Bounded: a definition over the cap is cut at a line, and says so.
            var big = "public class Big\n{\n" + string.Concat(Enumerable.Range(0, 4000).Select(i => $"    public int Field{i:D4} = {i}; // {new string('x', 20)}\n")) + "}\npublic static class BigNodes { [Node(\"Big\")] public static Big Make() => new(); }\n";
            Check((await Library(new { operation = "save", id = "big", name = "Big.cs", expectedRevision = 0, source = big }))["ok"]!.GetValue<bool>(), "the big library saves");
            var cut = await Definition(new { operation = "type_definition", type = "global::NodeLib.Big" });
            var cutText = cut["text"]!.GetValue<string>();
            Check(cut["truncated"]!.GetValue<bool>() && cutText.Length <= TypeDefinitions.MaxText && cutText.EndsWith("\n// The rest is not shown: the definition is longer than 64,000 characters", StringComparison.Ordinal)
                && cutText.StartsWith("public class Big\n{\n    public int Field0000 = 0;", StringComparison.Ordinal), "a long definition is cut: " + cutText.Length + " " + cutText[^120..]);
        }
        finally { await manager.UnloadAsync(); HassPath.HassConfiguration = previous; try { Directory.Delete(path, true); } catch { } }
    }

    // A graph past the size limits is answered with graph.limit before any Custom C# code is compiled (or signatures derived).
    static void LimitFirst()
    {
        var snapshot = new NodeLibraryCompiler(Diagnostics).Preview([]).Snapshot;
        // 300 Custom C# nodes whose code does not compile: compiling it would give each its own error.
        var nodes = Enumerable.Range(0, 300).Select(i => N("c" + i, "custom", new JsonObject { ["source"] = "public static class CustomNode { [Node(\"Broken\")] public static int Run() => ; }", ["arguments"] = new JsonObject() })).ToArray();
        var graph = G(nodes);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var preview = new VisualGenerator(Diagnostics).Preview(graph, snapshot);
        Check(!preview.Valid && preview.Diagnostics.Count == 1 && preview.Diagnostics[0].Code == "graph.limit", "only graph.limit, no compile diagnostic: " + JsonSerializer.Serialize(preview.Diagnostics.Take(3)));
        // Deriving signatures for such a graph compiles nothing either: the graph comes back as it was.
        var valid = G(Enumerable.Range(0, 300).Select(i => N("v" + i, "custom", new JsonObject { ["source"] = "public static class CustomNode { [Node(\"Two\", Pure = true)] public static int Run() => 2; }", ["arguments"] = new JsonObject() })).ToArray());
        var derived = new VisualGenerator(Diagnostics).DeriveSignatures(valid, snapshot, out var removed);
        Check(removed.Count == 0 && derived["nodes"]!.AsArray().Skip(1).All(n => n!["config"]!["signature"] == null) && SameJson(derived, valid), "an over-limit graph's signatures are not derived");
        // The wrong shape (no edges array) is the validator's graph.shape, before anything is compiled.
        var shapeless = (JsonObject)graph.DeepClone(); shapeless.Remove("edges");
        var shape = new VisualGenerator(Diagnostics).Preview(shapeless, snapshot);
        Check(!shape.Valid && shape.Diagnostics.Count == 1 && shape.Diagnostics[0].Code == "graph.shape", "only graph.shape: " + JsonSerializer.Serialize(shape.Diagnostics.Take(3)));
        Check(SameJson(new VisualGenerator(Diagnostics).DeriveSignatures(shapeless, snapshot, out _), shapeless), "a graph of the wrong shape gets no signatures");
        // The entity check walks neither: no entity is looked up for an over-limit graph that names one.
        var named = (JsonObject)graph.DeepClone(); named["nodes"]![0]!["config"] = new JsonObject { ["type"] = "entity", ["entity"] = "light.nowhere" };
        var lookups = 0;
        Check(HassSharpManager.UnknownEntities(named, snapshot, _ => { lookups++; return null; }).Count == 0 && lookups == 0, "an over-limit graph's entities are not looked up");
        var small = G(); small["nodes"]![0]!["config"] = new JsonObject { ["type"] = "entity", ["entity"] = "light.nowhere" };
        Check(HassSharpManager.UnknownEntities(small, snapshot, _ => { lookups++; return null; }).Count == 1 && lookups > 0, "a graph within the limits is checked");
        Check(watch.Elapsed < TimeSpan.FromSeconds(2), "answered quickly: " + watch.Elapsed);
    }
    static bool SameJson(JsonNode a, JsonNode b) => a.ToJsonString() == b.ToJsonString();
}
