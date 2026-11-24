using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;

public static class NodeLibraryTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static readonly string Source = """
        public enum Mode { Off = 0, On = 1 }
        public interface IReading { }
        public record Reading(string State) : IReading;
        public static class Nodes
        {
            static Nodes() => throw new Exception("preview ran static initializer");
            /// <summary>Summarize a detached state.</summary>
            /// <param name="entity">The entity snapshot.</param>
            /// <returns>The typed reading.</returns>
            [Node("Read summary", Category = "Sensors")]
            public static Reading Read([Port("Entity state")] EntityStateSnapshot entity) => throw new Exception("preview invoked method");
            [Node("Consume")]
            public static void Consume(IReading reading, int type = 3, string? output = null, string service = "light.turn_on", CancellationToken cancellationToken = default) { }
            [Node("Async")]
            public static Task<Reading?> Async() => throw new Exception();
            [Node("Value async")]
            public static ValueTask<List<Reading>> ValueAsync() => throw new Exception();
            [Node("Wait")] public static Task Wait() => throw new Exception();
            [Node("Value wait")] public static ValueTask ValueWait() => throw new Exception();
            [Node("Variance")] public static void Variance(Action<string> requiredHandler, Action<string?> nullableHandler, List<string> requiredList, List<string?> nullableList, int? nullableNumber, object requiredObject, object? optionalObject) {}
            [Node("Tuple")] public static (int one, string two) Tuple((int x, string y) input) => input;
            [Node("Literal")] public static void Literal(int count, Mode mode = Mode.On, int? optional = null, char letter = 'x') {}
            [Node("Types")] public static double Types(int integer, double number, string text, string? nullable, List<string?> list, IEnumerable<string> items, Reading[] readings, Reading? optional = null) => 1;
        }
        namespace Other { public record Reading(string Other); public static class Nodes { [Node("Other")] public static Reading Make() => new("x"); } }
        public class Convertible { public static implicit operator Reading(Convertible value) => new("effect"); }
        public static class Conversions { [Node("Convertible")] public static Convertible Make() => new(); }
        """;
    [RuntimeSuite("--node-library", Order = 10)]
    public static async Task Run()
    {
        var output = VisualNodeCatalog.DataOutputs(new JsonObject { ["kind"] = "read_entity", ["config"] = new JsonObject() });
        Check(output["value"]?["type"]?.GetValue<string>() == "global::HassSharp.EntityStateSnapshot", "Read entity concrete snapshot");
        var compiler = new NodeLibraryCompiler(new DiagnosticsProvider());
        var preview = compiler.Preview([new NodeLibrarySource("sensors", Source, 7)]);
        Check(preview.Valid, string.Join("; ", preview.Diagnostics.Select(d => d.Message)));
        var snapshot = preview.Snapshot;
        foreach (var (id, label) in new[] { ("boolean", "Boolean"), ("number", "Number"), ("string", "String"), ("object", "Object"), ("array", "Array"), ("entity_id", "Entity ID") })
            Check(snapshot.Catalog["types"]![id]!["label"]?.GetValue<string>() == label, "merged catalog preserves built-in display label: " + id);
        Check(snapshot.Catalog["types"]!["global::System.Double"]!["label"]!.GetValue<string>() == "double", "canonical CLR type retains Roslyn display label");
        Check(snapshot.Catalog["types"]!["global::NodeLib.Reading"]!["label"]!.GetValue<string>() == "NodeLib.Reading", "arbitrary CLR type retains Roslyn display label");
        var structures = snapshot.Catalog["structures"]!;
        Check(structures["loopDeclaration"]!["type"]!["enumSource"]?.GetValue<string>() == "types" && snapshot.Catalog["types"]!["global::NodeLib.Reading"] != null, "structural loop schema accepts registered custom CLR types");
        var literalTypes = structures["operand"]!["literal"]!["types"]!.AsArray().Select(t => t!.GetValue<string>()).ToArray();
        foreach (var type in new[] { "global::System.Int32", "global::NodeLib.Mode", "global::NodeLib.Reading?" })
            Check(literalTypes.Contains(type), "structural literal schema exposes representable CLR type: " + type);
        Check(!literalTypes.Contains("global::NodeLib.Reading"), "non-null rich class cannot advertise JSON literals");
        Check(structures["operand"]!["literal"]!["constraintsSource"]?.GetValue<string>() == "types", "literal value restrictions reference the authoritative registry");
        Check(VisualNodeCatalog.ValidLiteral("global::System.Int32", JsonValue.Create(3), true, snapshot), "integer literal advertised and accepted");
        Check(VisualNodeCatalog.ValidLiteral("global::NodeLib.Mode", JsonValue.Create(1), true, snapshot), "enum literal advertised and accepted");
        Check(!VisualNodeCatalog.ValidLiteral("global::NodeLib.Mode", JsonValue.Create(42), true, snapshot), "enum membership remains enforced");
        Check(VisualNodeCatalog.ValidLiteral("global::NodeLib.Reading?", null, true, snapshot), "nullable rich literal allows null");
        Check(!VisualNodeCatalog.ValidLiteral("global::NodeLib.Reading?", new JsonObject(), true, snapshot), "nullable rich literal rejects object JSON");
        var read = snapshot.Methods.Values.Single(m => m.Method.Name == "Read");
        var consume = snapshot.Methods.Values.Single(m => m.Method.Name == "Consume");
        Check(read.InvocationType == "global::NodeLib.Nodes" && read.ResultType == "global::NodeLib.Reading", "exact invocation and result type");
        var spec = snapshot.Catalog["nodes"]![read.Kind]!;
        Check(spec["description"]!.GetValue<string>() == "Summarize a detached state.", "XML method docs");
        Check(spec["inputs"]!["entity"]!["label"]!.GetValue<string>() == "Entity state", "port label");
        Check(spec["inputs"]!["entity"]!["description"]!.GetValue<string>() == "The entity snapshot.", "XML parameter docs");
        Check(spec["outputs"]!["result"]!["description"]!.GetValue<string>() == "The typed reading.", "XML result docs");
        Check(consume.Parameters.Single(p => p.Name == "cancellationToken").IsCancellationToken, "injected cancellation");
        Check(snapshot.Catalog["nodes"]![consume.Kind]!["inputs"]!["cancellationToken"] == null, "token has no input");
        Check(snapshot.IsAssignable("global::NodeLib.Reading", "global::NodeLib.IReading"), "interface conversion");
        Check(snapshot.IsAssignable("global::System.Int32", "global::System.Double"), "numeric widening");
        // Any number goes into any numeric pin; a value the pin cannot hold fails the step when it runs.
        Check(snapshot.IsAssignable("global::System.Double", "global::System.Int32"), "numeric narrowing is checked when it runs");
        Check(!snapshot.IsAssignable("global::System.Double?", "global::System.Int32"), "a nullable number does not go into a pin that cannot hold null");
        Check(!snapshot.IsAssignable("global::NodeLib.Reading?", "global::NodeLib.Reading"), "nullable cannot flow to required");
        Check(!snapshot.IsAssignable("global::System.Collections.Generic.List<global::System.String?>", "global::System.Collections.Generic.IEnumerable<global::System.String>"), "nested nullable cannot flow to required");
        Check(!snapshot.IsAssignable("global::NodeLib.Other.Reading", "global::NodeLib.Reading"), "namespace collision");
        Check(!snapshot.IsAssignable("global::NodeLib.Convertible", "global::NodeLib.Reading"), "no user operator on wire");
        Check(!snapshot.IsAssignable("made-up", "made-up"), "unknown type not authoritative");
        Check(snapshot.IsAssignable("global::System.Action<global::System.String?>", "global::System.Action<global::System.String>"), "contravariant nullable consumer accepts required input");
        Check(!snapshot.IsAssignable("global::System.Action<global::System.String>", "global::System.Action<global::System.String?>"), "contravariance cannot pass nullable values to required consumer");
        Check(!snapshot.IsAssignable("global::System.Collections.Generic.List<global::System.String>", "global::System.Collections.Generic.List<global::System.String?>"), "invariant mutable generic nullability preserved");
        Check(!snapshot.IsAssignable("global::System.Int32?", "global::System.Object"), "nullable boxed value cannot flow to required object");
        var tuple = snapshot.Methods.Values.Single(m => m.Method.Name == "Tuple");
        Check(tuple.ResultType == tuple.Parameters[0].TypeId && tuple.ResultType == "global::System.ValueTuple<global::System.Int32, global::System.String>", "tuple identity ignores element names");
        var asyncMethod = snapshot.Methods.Values.Single(m => m.Method.Name == "Async");
        Check(asyncMethod.AwaitResult && asyncMethod.ResultType == "global::NodeLib.Reading?", "Task unwrapped nullable");
        Check(snapshot.Methods.Values.Single(m => m.Method.Name == "ValueAsync").ResultType == "global::System.Collections.Generic.List<global::NodeLib.Reading>", "ValueTask closed generic unwrapped");
        Check(snapshot.Methods.Values.Where(m => m.Method.Name is "Wait" or "ValueWait" or "Consume").All(m => m.ResultType == null), "void and async void-like have no result");
        var created = VisualGraphContract.CreateNode(consume.Kind, "consumer", snapshot);
        Check(created["config"]!["arguments"]!["type"]!["value"]!.GetValue<int>() == 3 && created["config"]!["type"] == null, "reserved names stored in arguments");
        var graph = JsonNode.Parse("""{"version":5,"name":"Rich","nodes":[{"id":"t","kind":"trigger","config":{"type":"manual"}},{"id":"r","kind":"read_entity","config":{"entity":{"kind":"literal","type":"entity_id","value":"sensor.test"}}}],"edges":[{"id":"e","source":"t","target":"r"}]}""")!.AsObject();
        var custom = VisualGraphContract.CreateNode(read.Kind, "custom", snapshot);
        graph["nodes"]!.AsArray().Add(custom);
        graph["edges"]!.AsArray().Add(JsonNode.Parse("""{"id":"e2","source":"r","target":"custom"}"""));
        graph["edges"]!.AsArray().Add(JsonNode.Parse("""{"id":"d","kind":"data","source":"r","sourcePort":"value","target":"custom","targetPort":"entity"}"""));
        Check(VisualGraphValidator.Validate(graph, snapshot).Count == 0, "typed entity to custom graph");
        Check(VisualGraphValidator.Validate(graph).Any(d => d.Message == VisualGraphValidator.Words.MissingLibraryNode && d.Code == "node.unknownKind"), "catalog isolation");
        var leaked = snapshot.Catalog; leaked["nodes"]!.AsObject().Clear();
        Check(snapshot.Catalog["nodes"]!.AsObject().Count > 0 && snapshot.Sources.Single().Revision == 7, "immutable snapshot");
        var again = compiler.Preview([new NodeLibrarySource("sensors", Source, 8)]);
        Check(again.Snapshot.Methods.Keys.Order().SequenceEqual(snapshot.Methods.Keys.Order()), "stable identities independent of preview and revision");
        foreach (var signature in new[] { "public static T Bad<T>(T x) => x;", "public static void Bad(ref int x) {}", "public static void Bad(out int x) { x=0; }", "public static unsafe int* Bad(int* x) => x;", "public static async void Bad() { await Task.Yield(); }", "public int Bad(int x) => x;", "public static Span<int> Bad() => default;" })
        {
            var invalid = compiler.Preview([new("invalid", "public class BadNodes { [Node(\"Bad\")] " + signature + " }", 1)]);
            Check(!invalid.Valid && invalid.Diagnostics.Any(d => d.SourceId == "invalid" && d.StartLine == 1 && d.Severity == 3), "invalid signature precisely rejected: " + signature);
        }
        var sourceError = compiler.Preview([new("broken", "public static class Broken {\n [Node(\"Bad\")] public static Missing Bad() => null;\n}", 1)]);
        Check(!sourceError.Valid && sourceError.Diagnostics.Any(d => d.SourceId == "broken" && d.StartLine == 2), "mapped compiler errors");
        JsonObject RichInto(string kind)
        {
            var passThrough = VisualGraphContract.CreateNode(kind, "passthrough", snapshot);
            passThrough["config"]!["type"] = "global::NodeLib.Reading";
            var rich = (JsonObject)graph.DeepClone();
            rich["nodes"]!.AsArray().Add(passThrough);
            rich["edges"]!.AsArray().Add(JsonNode.Parse("""{"id":"d2","kind":"data","source":"custom","sourcePort":"result","target":"passthrough","targetPort":"value"}"""));
            return rich;
        }
        // The Convert is a value node outside the run order; nothing reads it, which is only a warning.
        Check(VisualGraphValidator.Validate(RichInto("convert"), snapshot).Select(d => d.Code).SequenceEqual(["pure.unused"]), "rich value flows into generic built-in node");
        // A Constant's value is typed in: nothing, a rich value included, can be wired into it.
        Check(VisualGraphValidator.Validate(RichInto("constant"), snapshot).Any(d => d.Code == "port.unknownInput" && d.NodeId == "passthrough" && d.InputId == "value"), "nothing can be wired into a Constant");
        var literalMethod = snapshot.Methods.Values.Single(m => m.Method.Name == "Literal");
        var literalNode = VisualGraphContract.CreateNode(literalMethod.Kind, "literal", snapshot);
        var literalGraph = JsonNode.Parse("""{"version":5,"name":"Literal","nodes":[{"id":"t","kind":"trigger","config":{"type":"manual"}}],"edges":[{"id":"e","source":"t","target":"literal"}]}""")!.AsObject();
        literalGraph["nodes"]!.AsArray().Add(literalNode);
        foreach (var invalidNumber in new[] { 1.5, 2147483648d, -2147483649d })
        {
            literalNode["config"]!["arguments"]!["count"] = new JsonObject { ["kind"] = "literal", ["type"] = "global::System.Int32", ["value"] = invalidNumber };
            Check(VisualGraphValidator.Validate(literalGraph, snapshot).Any(d => d.InputId == "count" && d.Message == "Count must be a whole number between -2147483648 and 2147483647"),"Int32 rejects fractional/out-of-range literal: " + string.Join("; ", VisualGraphValidator.Validate(literalGraph, snapshot)));
        }
        literalNode["config"]!["arguments"]!["count"]!["value"] = 4;
        Check(VisualGraphValidator.Validate(literalGraph, snapshot).Count == 0, "numeric, nullable, enum, char defaults accepted");
        literalNode["config"]!["arguments"]!["mode"]!["value"] = 8;
        Check(VisualGraphValidator.Validate(literalGraph, snapshot).Any(d => d.InputId == "mode"), "unknown enum literal rejected");
        literalNode["config"]!["arguments"]!["letter"]!["value"] = "long";
        Check(VisualGraphValidator.Validate(literalGraph, snapshot).Any(d => d.InputId == "letter"), "char requires one UTF-16 code unit");
        foreach (var invalidSource in new[] { "public static class Duplicate { [Node(\"A\")] public static void Same() {} [Node(\"B\")] public static void Same() {} }", "public static class Broken { [Node] public static void Bad() {} }" })
            Check(!compiler.Preview([new("invalid", invalidSource, 1)]).Valid, "invalid library returns diagnostics without throwing");
        foreach (var invalid in new[] { "internal class Outer { public static class Inner { [Node(\"Bad\")] public static void Run() {} } }", "public class Outer<T> { public static class Inner { [Node(\"Bad\")] public static void Run() {} } }", "public class Outer { private class Inner { [Node(\"Bad\")] public static void Run() {} } }" })
            Check(!compiler.Preview([new("inaccessible", invalid, 1)]).Valid, "inaccessible/open containing type rejected");
        Check(!compiler.Preview([new("abstract", "public interface AbstractNodes { [Node(\"Abstract\")] static abstract int Read(); }", 1)]).Valid, "static abstract interface methods cannot be directly invoked");
        var fakeAttributes = compiler.Preview([new("fake", """
            namespace Fake {
              public sealed class NodeAttribute(string title) : Attribute { }
              public sealed class PortAttribute(string label) : Attribute { }
            }
            public static class RealNodes {
              [Fake.Node("Fake")] public static void Fake() => throw new Exception();
              [Node("Real")] public static string Real([Fake.Port("Wrong")] string text) => text;
            }
            """, 1)]);
        Check(fakeAttributes.Valid && fakeAttributes.Snapshot.Methods.Count == 1, "only real Node attribute defines nodes");
        Check(fakeAttributes.Snapshot.Catalog["nodes"]!.AsObject().Last().Value!["inputs"]!["text"]!["label"]!.GetValue<string>() == "Text", "only real Port attribute overrides labels; a parameter name is put in words");
        var spoof = compiler.Preview([new("spoof", """
            }
            namespace HassSharp { public sealed class NodeAttribute(string title) : Attribute { } }
            namespace NodeLib {
              public static class Spoof { [HassSharp.Node("Spoof")] public static void Run() {} }
            """, 1)]);
        Check(!spoof.Valid && spoof.Snapshot.Methods.Count == 0, "source cannot spoof framework attributes or escape wrapper");
        var framework = compiler.Preview([new("framework", "public static class Framework { [Node(\"HTTP\")] public static System.Net.Http.HttpClient Make() => new(); }", 1)]);
        Check(framework.Valid, "closed framework types use complete framework references: " + string.Join("; ", framework.Diagnostics.Select(d => d.Message)));
        var specialDefaults = compiler.Preview([new("defaults", """
            public enum Choice { First, Second }
            public static class Defaults {
              [Node("Defaults")] public static void Run(double number = double.NaN, long large = long.MaxValue, Choice? choice = Choice.Second, System.Text.Json.Nodes.JsonObject? json = null) {}
            }
            """, 1)]);
        Check(specialDefaults.Valid, "valid CLR defaults outside JSON editor range do not crash preview");
        var defaultMethod = specialDefaults.Snapshot.Methods.Values.Single();
        var defaultNode = VisualGraphContract.CreateNode(defaultMethod.Kind, "defaults", specialDefaults.Snapshot);
        Check(defaultNode["config"]!["arguments"]!["number"] == null && defaultNode["config"]!["arguments"]!["large"] == null, "unrepresentable literal defaults remain omitted CLR defaults");
        var defaultGraph = JsonNode.Parse("""{"version":5,"name":"Defaults","nodes":[{"id":"t","kind":"trigger","config":{"type":"manual"}}],"edges":[{"id":"e","source":"t","target":"defaults"}]}""")!.AsObject();
        defaultGraph["nodes"]!.AsArray().Add(defaultNode);
        Check(VisualGraphValidator.Validate(defaultGraph, specialDefaults.Snapshot).Count == 0, "nullable enum defaults and JSON defaults validate");
        var attributes = new Dictionary<string, object> { ["nested"] = new List<object> { "original" } };
        var entity = EntityStateSnapshot.From(new HasEntityState { EntityId = "sensor.test", State = "on", Attributes = attributes, LastChanged = 10, LastReported = 20 });
        ((List<object>)attributes["nested"])[0] = "mutated";
        Check(entity.Attributes.GetProperty("nested")[0].GetString() == "original" && entity.LastChanged == DateTimeOffset.FromUnixTimeSeconds(10), "detached immutable attributes/timestamp conversion");
        NodeLibraryFixture.Verify();
        Console.WriteLine("PASS rich node libraries: Roslyn discovery, docs, signatures, conversion, snapshots and graph validation");
        PinDepth(compiler);
        Console.WriteLine("PASS rich node libraries: a pin whose type nests more than 8 levels is refused before any conversion is classified");
        ADescriptionShowsWhatItRefersToByName();
        AClassDescriptionShowsASettingItRefersToByItsLabel();
        await RunReview(null);
        await ALibrarySaveThatRemovesAUsedClassIsRefused();
        await ALibrarySaveThatRemovesAUsedParameterIsRefused();
        await ALibrarySaveThatRemovesAUsedSettingBehavesAsARemovedParameter();
        await ALibrarySaveThatAddsARequiredSettingIsRefused();
        await ALibrarySaveTakesANewSettingIntoACSharpDeviceThatDerivesTheClass();
        await ALibrarySaveThatAddsARequiredSettingToADerivedClassIsRefused();
        await ALibrarySaveThatRemovesASettingOfADerivedClassIsRefusedOnlyWhenTheNodeSetsIt();
        await ALibrarySaveTakesANewSettingIntoADerivingDefinitionThatIsOff();
        await ADefinitionsOwnSaveOfAStaleSignatureIsStillRefused();
        await ALibrarySaveTakesANewSettingIntoACSharpEntityThatDerivesTheClass();
    }

    // ---- What a description refers to ----

    /// <summary>
    /// A library whose documentation refers to members, types, parameters and words in each form XML documentation has:
    /// a <c>[Node]</c> method, and a <c>[Node]</c> device class deriving a base class with a setting of its own.
    /// </summary>
    const string ReferringSource = """
        public record Face(string Name);
        public class PeopleList { }
        public class Box<T> { }
        public static class Faces
        {
            /// <summary>
            /// <para>Counts the faces of <paramref name="frame"/> that the <see cref="PeopleList"/> does not know, as
            /// <see cref="Face"/> values.</para>
            /// <para>See <see href="https://example.com/faces">the faces guide</see> or <see href="https://example.com/more"/>.</para>
            /// </summary>
            /// <param name="frame">The frame, its faces found by <see cref="Find(string, CancellationToken)"/>; <see langword="null"/> for none.</param>
            /// <param name="limit">At most this many, <c>0</c> for no limit, each in a <see cref="Box{T}"/>.</param>
            /// <param name="cancellationToken">Stops the count.</param>
            /// <returns>How many faces of <paramref name="frame"/> are strangers, as <see cref="Face">a face each</see>.</returns>
            [Node("Strangers")]
            public static int Strangers(string? frame, [Port("At most")] int limit = 0, CancellationToken cancellationToken = default) => 0;
            /// <summary>The faces of <paramref name="frame"/>, of no <typeparamref name="T"/>; <paramref name="cancellationToken"/> is not a pin.</summary>
            /// <param name="frame">The frame, as <seealso cref="Strangers"/> takes it.</param>
            /// <param name="cancellationToken">Stops the search.</param>
            [Node("Find")]
            public static string[] Find(string frame, CancellationToken cancellationToken = default) => [];
        }
        /// <summary>A watcher of frames.</summary>
        public abstract class Watcher : SharpDevice
        {
            /// <summary>How many seconds pass between two frames.</summary>
            [Setting("Look every")] public double Every { get; set; } = 1;
        }
        /// <summary>
        /// Reports whether one is seen (held on for <see cref="StayOnFor"/> after the last), looking every
        /// <see cref="Watcher.Every"/> seconds, against the <see cref="PeopleList"/> every People detector shares, its
        /// <see cref="Seen"/> made by <see cref="PeopleDetector()"/>.
        /// </summary>
        [Node("People detector", Category = "People")]
        public class PeopleDetector : Watcher
        {
            /// <summary>Whether one is seen.</summary>
            public bool Seen { get; private set; }
            /// <summary>The camera to watch, a frame every <see cref="Watcher.Every"/> seconds, held for <see cref="StayOnFor"/>.</summary>
            [Setting] public string Camera { get; set; } = "";
            /// <summary>How long one is held on for after the last time one is seen; <c>0</c> for not at all.</summary>
            [Setting("Stay on for", Minimum = 0)] public double StayOnFor { get; set; } = 10;
            /// <summary>Makes a detector that sees no one.</summary>
            public PeopleDetector() { }
        }
        """;

    /// <summary>The words of a catalog entry's text <paramref name="entry"/>, checked to read as plain sentences: no gap a reference left.</summary>
    static string Plain(JsonNode? entry, string what)
    {
        var text = entry?.GetValue<string>() ?? "";
        Check(text.Length > 0 && !text.Contains("()") && !text.Contains("  ") && !text.Contains(" ,") && !text.Contains(" ;") && !text.Contains(" .") && text == text.Trim(),
            $"{what} reads as a plain sentence: \"{text}\"");
        return text;
    }

    // A [Node] method's summary, parameter and return texts show each reference in them: a member or type by its short
    // name, a parameter by its pin's label (by its name when it is no pin), a link by its words or else its address, a
    // keyword as itself; text and code as they are, the paragraphs one space apart.
    static void ADescriptionShowsWhatItRefersToByName()
    {
        var preview = new NodeLibraryCompiler(new DiagnosticsProvider()).Preview([new NodeLibrarySource("faces", ReferringSource, 1)]);
        Check(preview.Valid, "the library compiles: " + string.Join("; ", preview.Diagnostics.Select(d => d.Message)));
        var nodes = preview.Snapshot.Catalog["nodes"]!;
        var strangers = nodes[preview.Snapshot.Methods.Values.Single(m => m.Method.Name == "Strangers").Kind]!;
        var summary = Plain(strangers["description"], "the method's summary");
        Check(summary == "Counts the faces of Frame that the PeopleList does not know, as Face values. See the faces guide or https://example.com/more.",
            "a summary names its references: " + summary);
        var frame = Plain(strangers["inputs"]!["frame"]!["description"], "a parameter's text");
        Check(frame == "The frame, its faces found by Find; null for none.", "a parameter's text names a method by its name and a keyword as itself: " + frame);
        var limit = Plain(strangers["inputs"]!["limit"]!["description"], "a parameter's text");
        Check(limit == "At most this many, 0 for no limit, each in a Box.", "a parameter's text keeps its code and names a generic type without its arity: " + limit);
        var returns = Plain(strangers["outputs"]!["result"]!["description"], "the return's text");
        Check(returns == "How many faces of Frame are strangers, as a face each.", "the return's text names a parameter by its pin's label and gives a see's own words: " + returns);
        var find = nodes[preview.Snapshot.Methods.Values.Single(m => m.Method.Name == "Find").Kind]!;
        var findSummary = Plain(find["description"], "the second method's summary");
        Check(findSummary == "The faces of Frame, of no T; cancellationToken is not a pin.", "a type parameter is named, and a parameter that is no pin by its name: " + findSummary);
        var findFrame = Plain(find["inputs"]!["frame"]!["description"], "a parameter's text");
        Check(findFrame == "The frame, as Strangers takes it.", "a seealso names its member: " + findFrame);
        Console.WriteLine("PASS rich node libraries: a node's description, parameter and return texts show each member, type, parameter, link and keyword they refer to by name, leaving no gap");
    }

    // A [Node] class's summary and its settings' summaries show a setting they refer to by its label, one of a base class
    // too, and any other member by its name: "held on for Stay on for after the last", not "held on for after the last".
    static void AClassDescriptionShowsASettingItRefersToByItsLabel()
    {
        var preview = new NodeLibraryCompiler(new DiagnosticsProvider()).Preview([new NodeLibrarySource("faces", ReferringSource, 1)]);
        Check(preview.Valid, "the library compiles: " + string.Join("; ", preview.Diagnostics.Select(d => d.Message)));
        var detector = preview.Snapshot.Catalog["nodes"]!.AsObject().Select(n => n.Value).Single(n => VisualNodeCatalog.Text(n, "label") == "People detector")!;
        var summary = Plain(detector["description"], "the class's summary");
        Check(summary == "Reports whether one is seen (held on for Stay on for after the last), looking every Look every seconds, against the PeopleList every People detector shares, its Seen made by PeopleDetector.",
            "a class's summary names a setting by its label, a base class's too, and other members by their names: " + summary);
        Check(VisualNodeCatalog.Text(detector["classSignature"], "description") == summary, "the class signature has the same summary: " + detector["classSignature"]!.ToJsonString());
        var settings = detector["classSignature"]!["settings"]!.AsArray();
        var camera = Plain(settings.Single(s => VisualNodeCatalog.Text(s, "key") == "Camera")!["description"], "a setting's summary");
        Check(camera == "The camera to watch, a frame every Look every seconds, held for Stay on for.", "a setting's summary names the settings it refers to by their labels: " + camera);
        var stay = Plain(settings.Single(s => VisualNodeCatalog.Text(s, "key") == "StayOnFor")!["description"], "a setting's summary");
        Check(stay == "How long one is held on for after the last time one is seen; 0 for not at all.", "a setting's summary without references is as it was: " + stay);
        Console.WriteLine("PASS rich node libraries: a class's summary and its settings' summaries show a setting they refer to by its label, a base class's too, and other members by name");
    }

    // ---- A library save over a definition that uses a library class node ----

    /// <summary>The definition the library save tests run: the relays library's hub keyed hub, its node id front, with these values.</summary>
    static JsonObject Hubs(JsonObject? arguments = null) => RelayHubFixture.Definition("Relays", [RelayHubFixture.Hub("front", "hub", arguments)]);

    /// <summary>
    /// The refusal of a library save that breaks the definition <c>relays</c>, named as a library save names a definition
    /// it would break, with a problem on the node <paramref name="node"/>; its problems' messages.
    /// </summary>
    static string[] Refused(JsonObject answer, string node, string what)
    {
        var named = answer["error"]?["detail"]?["definitions"]?.AsArray().SingleOrDefault(d => d?["id"]?.GetValue<string>() == "relays");
        Check(answer["ok"]?.GetValue<bool>() == false && answer["error"]!["code"]!.GetValue<string>() == "validation" && named?["name"]?.GetValue<string>() == "Relays"
            && named["diagnostics"]!.AsArray().Any(d => d!["nodeId"]?.GetValue<string>() == node && d["severity"]!.GetValue<int>() == 3),
            $"{what}: the save is refused naming the definition: " + answer.ToJsonString());
        return [.. named!["diagnostics"]!.AsArray().Where(d => d!["nodeId"]?.GetValue<string>() == node).Select(d => d!["message"]!.GetValue<string>())];
    }

    /// <summary>
    /// The definition <c>relays</c> still runs the library as saved, after a refused save: its revision and host are as
    /// they were, its switch is reported, and the hub's action still runs and flips it.
    /// </summary>
    static async Task StillRuns(RelayHubFixture.Rig rig, DefinitionHost host, string what)
    {
        var read = await rig.Read("relays");
        Check(read["revision"]!.GetValue<long>() == 1 && read["status"]!.GetValue<string>() == "running" && read["currentRevisionActive"]!.GetValue<bool>(), $"{what}: the definition runs as it was: " + read.ToJsonString());
        Check(rig.Scripts.Entities.Host("relays") == host && host.Status == "running" && host.Entities.Select(e => e.UniqueId).SequenceEqual(["relays:hub:relay"]),
            $"{what}: the same host runs and reports the switch: {host.Status} " + string.Join(", ", host.Entities.Select(e => e.UniqueId)));
        var flipped = rig.Shown("relays:hub:relay", "is_on") == "true" ? "false" : "true";
        var answer = await host.UiAct("hub", "relays", "relay", "switch", new JsonObject());
        Check(answer.Status == "done", $"{what}: the hub's action still runs: {answer.Status} {answer.Message}");
        await EntityTestHome.Until(() => rig.Shown("relays:hub:relay", "is_on") == flipped, $"{what}: the action flipped the switch to {flipped}");
    }

    // A library save that takes away the class of a library class node a definition uses, or its [Node], is refused with
    // the missing library node message, naming the definition; the definition keeps running on the library as saved. The
    // hub's Token is wired from a Setting: the hub is told missing and nothing is said of that wire, into a pin no one can
    // know any more.
    static async Task ALibrarySaveThatRemovesAUsedClassIsRefused()
    {
        await using var rig = await RelayHubFixture.Rig.Start("class-gone");
        await rig.Saved("relays", 0, RelayHubFixture.Code);
        var token = new JsonObject { ["key"] = "token", ["type"] = "string", ["label"] = "Token", ["value"] = "s3cret", ["secret"] = true };
        var host = await rig.Running("relays", RelayHubFixture.Definition("Relays", [RelayHubFixture.Hub("front", "hub"), VisualExecutionTests.N("token", "setting", new { key = "token" })],
            [VisualExecutionTests.D("token", "front", "Token")], token));
        const string mark = "[Node(\"Relay hub\", Category = \"Relays\")]\n";
        Check(RelayHubFixture.Code.Contains(mark, StringComparison.Ordinal), "the hub is marked [Node]");
        foreach (var (what, code) in new[] { ("without the hub", RelayHubFixture.OtherCode), ("with the hub's [Node] removed", RelayHubFixture.Code.Replace(mark, "")) })
        {
            var answer = await rig.Library("relays", 1, code);
            var messages = Refused(answer, "front", what);
            Check(messages.SequenceEqual([VisualGraphValidator.Words.MissingLibraryNode]), $"{what}: the hub's node is told missing, and only that: " + string.Join(" | ", messages));
            var all = answer["error"]!["detail"]!["definitions"]!.AsArray().Single(d => d!["id"]!.GetValue<string>() == "relays")!["diagnostics"]!.AsArray().Select(d => d!["message"]!.GetValue<string>()).ToArray();
            Check(!all.Any(m => m.Contains("does not have", StringComparison.Ordinal)), $"{what}: nothing is said of the wire into Token: " + string.Join(" | ", all));
            await StillRuns(rig, host, what);
        }
        Console.WriteLine("PASS rich node libraries: a library save that takes away a used class, or its [Node], is refused naming the definition, which keeps running; nothing is said of the Setting wired into its Token");
    }

    // What a library save does today to a definition whose library step (here beside the relays library's hub, which makes
    // its entities) has a value for a parameter the save removes: the step's value is for an input it no longer has, the
    // definition would no longer be valid, and the save is refused naming it, as for any change that breaks a definition;
    // the definition keeps running on the library as saved.
    static async Task ALibrarySaveThatRemovesAUsedParameterIsRefused()
    {
        await using var rig = await RelayHubFixture.Rig.Start("parameter-gone");
        const string steps = "public static class Steps { [Node(\"Note\")] public static void Note(string text = \"hello\", int times = 2) { } }";
        await rig.Saved("relays", 0, RelayHubFixture.Code);
        await rig.Saved("steps", 0, steps);
        var snapshot = new NodeLibraryCompiler(new DiagnosticsProvider()).Preview([new NodeLibrarySource("steps", steps, 1)]).Snapshot;
        var note = VisualGraphContract.CreateNode(snapshot.Methods.Values.Single().Kind, "note", snapshot);
        Check(note["config"]!["arguments"]!["times"] is JsonObject, "the step has a value for times: " + note.ToJsonString());
        var definition = RelayHubFixture.Definition("Relays", [RelayHubFixture.Hub("front", "hub"), VisualExecutionTests.N("start", "entity_setup"), note], [new JsonObject { ["id"] = "start:note", ["source"] = "start", ["target"] = "note" }]);
        await rig.Running("relays", definition);
        var answer = await rig.Library("steps", 1, steps.Replace(", int times = 2", ""));
        var named = answer["error"]?["detail"]?["definitions"]?.AsArray().SingleOrDefault(d => d?["id"]?.GetValue<string>() == "relays");
        Check(answer["ok"]?.GetValue<bool>() == false && answer["error"]!["code"]!.GetValue<string>() == "validation" && named?["name"]?.GetValue<string>() == "Relays",
            "the save is refused naming the definition: " + answer.ToJsonString());
        var problems = named!["diagnostics"]!.AsArray().Where(d => d!["nodeId"]?.GetValue<string>() == "note").Select(d => $"{d!["inputId"]} {d["message"]}").ToArray();
        Check(problems.SequenceEqual(["times Unknown input times"]), "the step's value is for an input it no longer has: " + string.Join(" | ", problems));
        var read = await rig.Read("relays");
        Check(read["revision"]!.GetValue<long>() == 1 && read["status"]!.GetValue<string>() == "running", "the definition runs as it was: " + read.ToJsonString());
        Console.WriteLine("PASS rich node libraries: a library save that removes a parameter a library step has a value for is refused naming the definition, which keeps running");
    }

    // A library save that removes a setting a library class node has a value for is handled as a removed parameter of a
    // library step is (above): the node's value is for an input it no longer has, and the save is refused naming the
    // definition, which keeps running on the library as saved.
    static async Task ALibrarySaveThatRemovesAUsedSettingBehavesAsARemovedParameter()
    {
        await using var rig = await RelayHubFixture.Rig.Start("setting-gone");
        await rig.Saved("relays", 0, RelayHubFixture.Code);
        var host = await rig.Running("relays", Hubs(new JsonObject { ["Address"] = VisualExecutionTests.Lit("string", "10.0.0.2"), ["PollEvery"] = VisualExecutionTests.Lit("number", 10) }));
        const string poll = "    [Setting(\"Poll every\", Minimum = 1, Maximum = 60)] public double PollEvery { get; set; } = 5;\n";
        Check(RelayHubFixture.Code.Contains(poll, StringComparison.Ordinal), "the hub has Poll every");
        var answer = await rig.Library("relays", 1, RelayHubFixture.Code.Replace(poll, ""));
        var problems = answer["error"]?["detail"]?["definitions"]?.AsArray().SingleOrDefault(d => d?["id"]?.GetValue<string>() == "relays")?["diagnostics"]?.AsArray()
            .Where(d => d!["nodeId"]?.GetValue<string>() == "front").Select(d => $"{d!["inputId"]} {d["message"]}").ToArray() ?? [];
        Refused(answer, "front", "Poll every removed");
        Check(problems.SequenceEqual(["PollEvery Unknown input PollEvery"]), "the hub's value is for an input it no longer has: " + string.Join(" | ", problems));
        await StillRuns(rig, host, "Poll every removed");
        Console.WriteLine("PASS rich node libraries: a library save that removes a setting a library class node has a value for is refused naming the definition, as for a removed parameter");
    }

    // A library save that gives the class of a library class node a definition uses a new required setting without a
    // default makes the definition invalid, so it is refused naming it; one with a default goes ahead, and the definition
    // is rebuilt and runs the new code.
    static async Task ALibrarySaveThatAddsARequiredSettingIsRefused()
    {
        await using var rig = await RelayHubFixture.Rig.Start("setting-added");
        await rig.Saved("relays", 0, RelayHubFixture.Code);
        var host = await rig.Running("relays", Hubs());
        const string constructor = "    public RelayHub()";
        Check(RelayHubFixture.Code.Contains(constructor, StringComparison.Ordinal), "the hub has its constructor");
        var messages = Refused(await rig.Library("relays", 1, RelayHubFixture.Code.Replace(constructor, "    [Setting] public string Room { get; set; }\n" + constructor)), "front", "a required Room");
        Check(messages.SequenceEqual(["Room is required"]), "the hub's Room is told required: " + string.Join(" | ", messages));
        await StillRuns(rig, host, "a required Room");
        var saved = await rig.Saved("relays", 1, RelayHubFixture.Code.Replace(constructor, "    [Setting] public string Room { get; set; } = \"hall\";\n" + constructor));
        var rebuilt = saved["definitions"]!.AsArray().SingleOrDefault(d => d!["id"]!.GetValue<string>() == "relays");
        Check(rebuilt?["active"]?.GetValue<bool>() == true && rebuilt["document"]!["revision"]!.GetValue<long>() == 2, "the definition is rebuilt and runs: " + saved.ToJsonString());
        var next = await rig.Started("relays");
        var instance = next.Entity("relays:hub:relay")?.AddedTo ?? throw new Exception("the switch was added by no device");
        Check(next != host && instance.GetType().GetProperty("Room")?.GetValue(instance) as string == "hall", "the new code runs, Room its default");
        Check(rig.Stored("relays", "hub", "starts") == "j2", "the rebuilt hub counts on in its data: " + rig.Stored("relays", "hub", "starts"));
        Check((await next.UiAct("hub", "relays", "relay", "switch", new JsonObject())).Status == "done", "the rebuilt hub's action runs");
        Console.WriteLine("PASS rich node libraries: a library save that adds a required setting without a default is refused naming the definition; with a default it goes ahead and the definition runs the new code");
    }

    // ---- A library save over a definition whose C# node's class derives a library class ----

    /// <summary>The code of the C# device the tests below run: a class that only derives the relays library's hub.</summary>
    const string DerivedHub = "public class FrontHub : RelayHub { }";
    const string HubConstructor = "    public RelayHub()";
    const string TokenLine = "    [Setting(Secret = true)] public string? Token { get; set; }\n";
    const string PollLine = "    [Setting(\"Poll every\", Minimum = 1, Maximum = 60)] public double PollEvery { get; set; } = 5;\n";
    /// <summary>The relays library with a Room setting on the hub, of the initializer given (<c> = "hall";</c>, or none).</summary>
    static string WithRoom(string initializer)
    {
        Check(RelayHubFixture.Code.Contains(HubConstructor, StringComparison.Ordinal), "the hub has its constructor");
        return RelayHubFixture.Code.Replace(HubConstructor, $"    [Setting] public string Room {{ get; set; }}{initializer}\n" + HubConstructor);
    }
    /// <summary>The definition <c>relays</c>: a C# device of id front, keyed hub, deriving the hub, with these values.</summary>
    static JsonObject DerivedHubs(JsonObject? arguments = null) => RelayHubFixture.Definition("Relays", [RelayHubFixture.Derived("csharp_device", "front", "hub", DerivedHub, arguments)]);
    /// <summary>The settings' keys of the stored node front of the definition <c>relays</c>, in order, as its signature has them.</summary>
    static async Task<string> StoredSettings(RelayHubFixture.Rig rig) =>
        string.Join(",", StoredNode(await rig.Read("relays"))["config"]!["signature"]!["settings"]!.AsArray().Select(s => s!["key"]!.GetValue<string>()));
    static JsonObject StoredNode(JsonObject read) => read["logic"]!["graph"]!["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "front")!.AsObject();
    /// <summary>The value of a property of the class the running definition's device (or, for an entity, the entity) has.</summary>
    static object? Property(DefinitionHost host, string uniqueId, string name, bool device = true)
    {
        var entity = host.Entity(uniqueId) ?? throw new Exception($"{uniqueId} is not reported");
        object instance = device ? entity.AddedTo ?? throw new Exception($"{uniqueId} was added by no device") : entity;
        return instance.GetType().GetProperty(name)?.GetValue(instance);
    }

    // A library save that gives a class a C# device derives a new setting with a default writes that setting into the
    // node's signature as its code now gives it, nobody having touched the code: the definition is rebuilt at its next
    // revision and runs the new code, the setting at its default, the node's value and its wired Setting as they were.
    // A later save that removes the setting a Setting is wired into is refused, naming the definition.
    static async Task ALibrarySaveTakesANewSettingIntoACSharpDeviceThatDerivesTheClass()
    {
        await using var rig = await RelayHubFixture.Rig.Start("derived-setting-added");
        await rig.Saved("relays", 0, RelayHubFixture.Code);
        var token = new JsonObject { ["key"] = "token", ["type"] = "string", ["label"] = "Token", ["value"] = "s3cret", ["secret"] = true };
        var host = await rig.Running("relays", RelayHubFixture.Definition("Relays", [RelayHubFixture.Derived("csharp_device", "front", "hub", DerivedHub), VisualExecutionTests.N("token", "setting", new { key = "token" })],
            [VisualExecutionTests.D("token", "front", "Token")], token));
        Check(await StoredSettings(rig) == "Address,Token,PollEvery", "the C# device has the hub's settings: " + await StoredSettings(rig));
        var saved = await rig.Saved("relays", 1, WithRoom(" = \"hall\";"));
        var rebuilt = saved["definitions"]!.AsArray().SingleOrDefault(d => d!["id"]!.GetValue<string>() == "relays");
        Check(rebuilt?["active"]?.GetValue<bool>() == true && rebuilt["document"]!["revision"]!.GetValue<long>() == 2, "the definition is rebuilt and runs: " + saved.ToJsonString());
        var read = await rig.Read("relays");
        var node = StoredNode(read);
        Check(read["revision"]!.GetValue<long>() == 2 && await StoredSettings(rig) == "Address,Token,PollEvery,Room", "the stored node's signature has Room: " + node.ToJsonString());
        Check(node["config"]!["source"]!.GetValue<string>() == DerivedHub && JsonNode.DeepEquals(node["config"]!["arguments"], new JsonObject { ["Address"] = VisualExecutionTests.Lit("string", "10.0.0.2") }),
            "the node's code and its value of Address are as they were: " + node.ToJsonString());
        Check(read["logic"]!["graph"]!["edges"]!.AsArray().Any(e => e!["source"]!.GetValue<string>() == "token" && e["target"]!.GetValue<string>() == "front" && e["targetPort"]!.GetValue<string>() == "Token"),
            "the Setting is still wired into Token: " + read["logic"]!["graph"]!["edges"]!.ToJsonString());
        var next = await rig.Started("relays");
        Check(next != host && Property(next, "relays:hub:relay", "Room") as string == "hall" && Property(next, "relays:hub:relay", "Address") as string == "10.0.0.2" && Property(next, "relays:hub:relay", "Token") as string == "s3cret",
            "the new code runs, Room its default, Address and Token as they were");
        var gone = Refused(await rig.Library("relays", 2, WithRoom(" = \"hall\";").Replace(TokenLine, "")), "front", "Token removed under a wire");
        Check(gone.SequenceEqual(["This value wire goes into Token, which this step does not have"]), "the wire into Token is told: " + string.Join(" | ", gone));
        Check((await rig.Read("relays"))["revision"]!.GetValue<long>() == 2 && rig.Scripts.Entities.Host("relays") == next, "the definition runs as it was");
        Console.WriteLine("PASS rich node libraries: a library save that adds a setting with a default to a class a C# device derives takes it into the node's signature, and the definition runs the new code at its next revision, its values and wires kept; removing a wired setting is refused");
    }

    // A library save that gives a class a C# device derives a new required setting is refused naming the definition, the
    // setting told required as on the class's own node; the library and the definition stay as they were, which runs on.
    static async Task ALibrarySaveThatAddsARequiredSettingToADerivedClassIsRefused()
    {
        await using var rig = await RelayHubFixture.Rig.Start("derived-required");
        await rig.Saved("relays", 0, RelayHubFixture.Code);
        var host = await rig.Running("relays", DerivedHubs());
        var messages = Refused(await rig.Library("relays", 1, WithRoom("")), "front", "a required Room under a C# device");
        Check(messages.SequenceEqual(["Room is required"]), "the C# device's Room is told required: " + string.Join(" | ", messages));
        await StillRuns(rig, host, "a required Room under a C# device");
        var library = JsonNode.Parse(await rig.Api.NodeLibraryRequest(JsonSerializer.Serialize(new { operation = "read", id = "relays" })))!;
        Check(library["result"]?["revision"]?.GetValue<long>() == 1 && library["result"]!["source"]!.GetValue<string>() == RelayHubFixture.Code, "the library is as it was: " + library.ToJsonString());
        Check(await StoredSettings(rig) == "Address,Token,PollEvery", "the stored node is as it was: " + await StoredSettings(rig));
        Console.WriteLine("PASS rich node libraries: a library save that adds a required setting to a class a C# device derives is refused naming the definition; nothing stored changes and it runs on");
    }

    // A library save that removes a setting of a class a C# device derives is refused, naming the definition, when the
    // node has a value for it, as for the class's own node; one the node does not set goes, and the definition is rebuilt.
    static async Task ALibrarySaveThatRemovesASettingOfADerivedClassIsRefusedOnlyWhenTheNodeSetsIt()
    {
        await using var rig = await RelayHubFixture.Rig.Start("derived-setting-gone");
        await rig.Saved("relays", 0, RelayHubFixture.Code);
        var host = await rig.Running("relays", DerivedHubs(new JsonObject { ["Address"] = VisualExecutionTests.Lit("string", "10.0.0.2"), ["PollEvery"] = VisualExecutionTests.Lit("number", 10) }));
        Check(RelayHubFixture.Code.Contains(PollLine, StringComparison.Ordinal) && RelayHubFixture.Code.Contains(TokenLine, StringComparison.Ordinal), "the hub has Poll every and Token");
        var answer = await rig.Library("relays", 1, RelayHubFixture.Code.Replace(PollLine, ""));
        Refused(answer, "front", "Poll every removed under a C# device");
        var problems = answer["error"]!["detail"]!["definitions"]!.AsArray().Single(d => d!["id"]!.GetValue<string>() == "relays")!["diagnostics"]!.AsArray()
            .Where(d => d!["nodeId"]?.GetValue<string>() == "front").Select(d => $"{d!["inputId"]} {d["message"]}").ToArray();
        Check(problems.SequenceEqual(["PollEvery Unknown input PollEvery"]), "the C# device's value is for an input it no longer has: " + string.Join(" | ", problems));
        await StillRuns(rig, host, "Poll every removed under a C# device");
        var saved = await rig.Saved("relays", 1, RelayHubFixture.Code.Replace(TokenLine, ""));
        Check(saved["definitions"]!.AsArray().Single(d => d!["id"]!.GetValue<string>() == "relays")!["document"]!["revision"]!.GetValue<long>() == 2, "the definition is rebuilt: " + saved.ToJsonString());
        Check(await StoredSettings(rig) == "Address,PollEvery", "the stored node's signature has no Token: " + await StoredSettings(rig));
        var next = await rig.Started("relays");
        Check(Property(next, "relays:hub:relay", "PollEvery") is 10.0, "the new code runs with Poll every as it was");
        Console.WriteLine("PASS rich node libraries: a library save that removes a setting of a class a C# device derives is refused naming the definition when the node sets it, and goes ahead when it does not");
    }

    // A definition that is off is rebuilt by a library save as one that is on: a new setting with a default is taken into
    // its C# device's signature, and turned on afterwards the definition runs the new code.
    static async Task ALibrarySaveTakesANewSettingIntoADerivingDefinitionThatIsOff()
    {
        await using var rig = await RelayHubFixture.Rig.Start("derived-off");
        await rig.Saved("relays", 0, RelayHubFixture.Code);
        await rig.Running("relays", DerivedHubs());
        await rig.Ok(new { operation = "set_enabled", id = "relays", enabled = false });
        var saved = await rig.Saved("relays", 1, WithRoom(" = \"hall\";"));
        var rebuilt = saved["definitions"]!.AsArray().SingleOrDefault(d => d!["id"]!.GetValue<string>() == "relays");
        Check(rebuilt?["enabled"]?.GetValue<bool>() == false && rebuilt["document"]!["revision"]!.GetValue<long>() == 2, "the definition that is off is rebuilt: " + saved.ToJsonString());
        Check(await StoredSettings(rig) == "Address,Token,PollEvery,Room", "the stored node's signature has Room: " + await StoredSettings(rig));
        await rig.Ok(new { operation = "set_enabled", id = "relays", enabled = true });
        var host = await rig.Started("relays");
        Check(Property(host, "relays:hub:relay", "Room") as string == "hall", "turned on, it runs the new code, Room its default");
        Console.WriteLine("PASS rich node libraries: a library save takes a new setting with a default into the C# device of a definition that is off, which runs the new code once turned on");
    }

    // A definition's own save of a C# device whose signature is not what its code gives is refused as out of date, as
    // before: there the person who edited the code is asked to apply it. Only a library save refreshes the signature.
    static async Task ADefinitionsOwnSaveOfAStaleSignatureIsStillRefused()
    {
        await using var rig = await RelayHubFixture.Rig.Start("derived-stale");
        await rig.Saved("relays", 0, RelayHubFixture.Code);
        var stale = RelayHubFixture.Definition("Relays", [RelayHubFixture.Derived("csharp_device", "front", "hub", DerivedHub, code: WithRoom(" = \"hall\";"))]);
        var answer = await rig.Request(new { operation = "save", id = "relays", expectedRevision = 0, definition = stale });
        Check(answer["ok"]?.GetValue<bool>() == false && answer["error"]!["code"]!.GetValue<string>() == "validation"
            && answer["error"]!["detail"]!["diagnostics"]!.AsArray().Any(d => d!["code"]?.GetValue<string>() == "csharp.signature" && d["nodeId"]!.GetValue<string>() == "front"),
            "the stale signature is refused: " + answer.ToJsonString());
        Console.WriteLine("PASS rich node libraries: a definition's own save of a C# device whose signature is out of date with its code is still refused");
    }

    // A library save that gives a library entity class a C# entity derives a new setting with a default takes it into the
    // node's signature, as for a C# device: the definition is rebuilt and runs the new code, the setting at its default.
    static async Task ALibrarySaveTakesANewSettingIntoACSharpEntityThatDerivesTheClass()
    {
        await using var rig = await RelayHubFixture.Rig.Start("derived-entity");
        await rig.Saved("relays", 0, RelayHubFixture.Code);
        const string address = "[Setting] public string Address { get; set; } = \"\";";
        Check(RelayHubFixture.OtherCode.Contains(address, StringComparison.Ordinal) && !RelayHubFixture.HubCode.Contains(address, StringComparison.Ordinal), "the relay has Address");
        await rig.Running("relays", RelayHubFixture.Definition("Relays", [RelayHubFixture.Derived("csharp_entity", "front", "relay", "public class FrontRelay : Relay { }")]));
        var saved = await rig.Saved("relays", 1, RelayHubFixture.Code.Replace(address, address + "\n    [Setting] public int Channel { get; set; } = 1;"));
        var rebuilt = saved["definitions"]!.AsArray().SingleOrDefault(d => d!["id"]!.GetValue<string>() == "relays");
        Check(rebuilt?["active"]?.GetValue<bool>() == true && rebuilt["document"]!["revision"]!.GetValue<long>() == 2, "the definition is rebuilt and runs: " + saved.ToJsonString());
        Check(await StoredSettings(rig) == "Address,Channel", "the stored node's signature has Channel: " + await StoredSettings(rig));
        var host = await rig.Started("relays");
        Check(Property(host, "relays:relay", "Channel", device: false) is 1 && Property(host, "relays:relay", "Address", device: false) as string == "10.0.0.2", "the new code runs, Channel its default, Address as it was");
        Console.WriteLine("PASS rich node libraries: a library save that adds a setting with a default to a class a C# entity derives takes it into the node's signature, and the definition runs the new code");
    }
    /// <summary>
    /// A pin type nested <paramref name="levels"/> levels deep: <c>int</c> is one level, and each list, array, tuple or
    /// nullable wrapper around it one more.
    /// </summary>
    static string Nested(int levels, string wrap = "List<{0}>", string inner = "int") { var type = inner; for (var i = 1; i < levels; i++) type = string.Format(wrap, type); return type; }
    // Fix 11: the time to compare deeply nested types doubles per level, and nothing can cancel it; such a pin is refused first.
    static void PinDepth(NodeLibraryCompiler compiler)
    {
        const string TooDeep = "This type is nested too deeply to be a pin; use a simpler type";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var fits = compiler.Preview([new("depth", $"public static class Deep {{ [Node(\"Eight\")] public static int Eight({Nested(8)} items) => 1; [Node(\"Out\")] public static {Nested(8, "{0}[]")} Out() => null!; }}", 1)]);
        Check(fits.Valid, "a pin nested 8 levels compiles: " + string.Join("; ", fits.Diagnostics.Select(d => d.Message)));
        void Refused(string name, string code, string where)
        {
            var preview = compiler.Preview([new("depth", code, 1)]);
            var found = preview.Diagnostics.Where(d => d.Message == TooDeep).ToArray();
            Check(!preview.Valid && found.Length == 1 && preview.Snapshot.Methods.Count == 0, name + ": " + string.Join("; ", preview.Diagnostics.Select(d => d.Message)));
            var line = code.Split('\n')[found[0].StartLine - 1];
            Check(line.Substring(found[0].StartColumn - 1).StartsWith(where, StringComparison.Ordinal), $"{name}: reported at {where}, not at {line.Substring(found[0].StartColumn - 1)}");
        }
        Refused("a parameter nested 9 levels", $"public static class Deep {{ [Node(\"Nine\")] public static int Nine({Nested(9)} items) => 1; }}", "items");
        Refused("a result nested 9 levels", $"public static class Deep {{ [Node(\"Nine\")] public static {Nested(9, "{0}[]")} Nine() => null!; }}", Nested(9, "{0}[]"));
        Refused("a tuple element nested 9 levels", $"public static class Deep {{ [Node(\"Nine\")] public static (int Count, {Nested(9, "Dictionary<string, {0}>")} Deep) Nine() => default; }}", "Deep");
        // int, then four times a one-element tuple that may be empty (a nullable wrapper around a tuple): 9 levels.
        Refused("nullable wrappers counted", $"public static class Deep {{ [Node(\"Nine\")] public static int Nine({Nested(5, "System.ValueTuple<{0}>?")} value) => 1; }}", "value");
        // Far deeper: refused as quickly (comparing such types would take minutes).
        Refused("a parameter nested 30 levels", $"public static class Deep {{ [Node(\"Thirty\")] public static int Thirty({Nested(30)} items) => 1; }}", "items");
        // A Custom C# node's pins are held to the same bound.
        var custom = compiler.Preview([], [new CustomNodeSource("deep", $"public static class CustomNode {{ [Node(\"Deep\")] public static int Run({Nested(9)} items) => 1; }}")]).Snapshot.Custom["deep"];
        Check(custom.Signature == null && custom.Diagnostics.Any(d => d.Message == TooDeep), "a Custom C# pin nested 9 levels: " + string.Join("; ", custom.Diagnostics.Select(d => d.Message)));
        // A property nested deeper than a pin may be is skipped, as an unsupported member is; the others stay listed.
        var record = compiler.Preview([new("depth", $"public record Holder(string Name, {Nested(9)} Deep, {Nested(8)} Fits); public static class Deep {{ [Node(\"Holder\", Pure = true)] public static Holder Make() => null!; }}", 1)]);
        Check(record.Valid && string.Join(",", record.Snapshot.Catalog["types"]!["global::NodeLib.Holder"]!["properties"]!.AsArray().Select(p => p!["name"]!.GetValue<string>())) == "Name,Fits",
            "a property nested 9 levels is not listed: " + record.Snapshot.Catalog["types"]!["global::NodeLib.Holder"]?["properties"]?.ToJsonString() + string.Join("; ", record.Diagnostics.Select(d => d.Message)));
        Check(watch.Elapsed < TimeSpan.FromSeconds(15), "the pin depth checks took " + watch.Elapsed);
    }
    [RuntimeSuite("--node-library-review", RunByDefault = false)]
    public static async Task RunReview(string? scenario)
    {
        if (scenario is null or "generic")
        {
            // Run potentially unbounded discovery in a disposable child, so the regression itself
            // has a hard deadline and cannot exhaust the full test harness's stack or memory. The child takes about a
            // second here and 14 s with every core busy (a process start and a first compile); unbounded, it never ends.
            using var process = new System.Diagnostics.Process { StartInfo = new("dotnet") {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
            process.StartInfo.ArgumentList.Add(typeof(NodeLibraryTests).Assembly.Location);
            process.StartInfo.ArgumentList.Add("--node-library-expanding-child");
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
            catch (TimeoutException)
            {
                process.Kill(entireProcessTree: true); await process.WaitForExitAsync();
                throw new Exception("Expanding generic preview must terminate within a minute");
            }
            Check(process.ExitCode == 0, "Expanding generic preview must terminate successfully: " + (await error)[..Math.Min(500, (await error).Length)]);
            Check((await output).Contains("PASS expanding generic preview"), "expanding generic child completed assertions");
            Console.WriteLine("PASS review: bounded expanding generic metadata");
        }
        if (scenario is null or "array")
        {
            var preview = new NodeLibraryCompiler(new DiagnosticsProvider()).Preview([new("arrays", """
                public static class Arrays {
                    [Node("Nullable array")] public static string?[] NullableArray() => new string?[] { null };
                    [Node("Required array")] public static string[] RequiredArray() => new[] { "text" };
                    [Node("Consumers")] public static void Consumers(IEnumerable<string> requiredEnumerable, IReadOnlyList<string> requiredReadOnlyList, IList<string> requiredList, IEnumerable<string?> nullableEnumerable, IReadOnlyList<string?> nullableReadOnlyList, IList<string?> nullableList) {}
                }
                """, 1)]);
            Check(preview.Valid, string.Join("; ", preview.Diagnostics.Select(d => d.Message)));
            foreach (var container in new[] { "IEnumerable", "IReadOnlyList", "IList" })
            {
                var required = $"global::System.Collections.Generic.{container}<global::System.String>";
                var nullable = $"global::System.Collections.Generic.{container}<global::System.String?>";
                Check(!preview.Snapshot.IsAssignable("global::System.String?[]", required), "nullable array elements cannot feed required " + container);
                Check(!(preview.Snapshot.Catalog["types"]!["global::System.String?[]"]!["assignableTo"]!.AsArray()).Any(t => t!.GetValue<string>() == required), "published array relation preserves nested nullability");
                Check(preview.Snapshot.IsAssignable("global::System.String?[]", nullable), "nullable array elements can feed nullable " + container);
                Check(preview.Snapshot.IsAssignable("global::System.String[]", required), "required array elements can feed required " + container);
            }
            Console.WriteLine("PASS review: array/interface nullable element conversions");
        }
        if (scenario is null or "defaults")
        {
            var preview = new NodeLibraryCompiler(new DiagnosticsProvider()).Preview([new("defaults", """
                public static class Defaults {
                    [Node("Nonfinite")] public static void Nonfinite(double? nan = double.NaN, double? positive = double.PositiveInfinity, double? negative = double.NegativeInfinity, float? singleNan = float.NaN, float? singlePositive = float.PositiveInfinity, float? singleNegative = float.NegativeInfinity) {}
                    [Node("Null")] public static void Null(double? noDefault, double? explicitNull = null) {}
                }
                """, 1)]);
            Check(preview.Valid, "nullable non-finite CLR defaults are valid signatures");
            var method = preview.Snapshot.Methods.Values.Single(m => m.Method.Name == "Nonfinite");
            var node = VisualGraphContract.CreateNode(method.Kind, "d", preview.Snapshot);
            foreach (var parameter in method.Parameters)
            {
                Check(parameter.HasDefaultValue && parameter.DefaultValue != null, "explicit non-finite default remains in descriptor");
                Check(parameter.Name switch {
                    "nan" => parameter.DefaultValue is double d && double.IsNaN(d),
                    "positive" => parameter.DefaultValue is double d && d == double.PositiveInfinity,
                    "negative" => parameter.DefaultValue is double d && d == double.NegativeInfinity,
                    "singleNan" => parameter.DefaultValue is float f && float.IsNaN(f),
                    "singlePositive" => parameter.DefaultValue is float f && f == float.PositiveInfinity,
                    "singleNegative" => parameter.DefaultValue is float f && f == float.NegativeInfinity,
                    _ => false }, "exact non-finite CLR default survives: " + parameter.Name);
                Check(preview.Snapshot.Catalog["nodes"]![method.Kind]!["inputs"]![parameter.Name]!["default"] == null, "unrepresentable explicit default must not become a null literal: " + parameter.Name);
                Check(!node["config"]!["arguments"]!.AsObject().ContainsKey(parameter.Name), "created node omits explicit non-finite default: " + parameter.Name);
            }
            var nullMethod = preview.Snapshot.Methods.Values.Single(m => m.Method.Name == "Null");
            var nullNode = VisualGraphContract.CreateNode(nullMethod.Kind, "n", preview.Snapshot);
            foreach (var name in new[] { "noDefault", "explicitNull" })
                Check(nullNode["config"]!["arguments"]![name] is JsonObject operand && operand.ContainsKey("value") && operand["value"] == null, "true nullable null defaults remain explicit literals");
            Console.WriteLine("PASS review: nullable non-finite default omission");
        }
    }

    [RuntimeSuite("--node-library-expanding-child", RunByDefault = false)]
    public static void ExpandingGenericPreview()
    {
        var preview = new NodeLibraryCompiler(new DiagnosticsProvider()).Preview([new("expanding", """
            public interface I<T> { }
            public class Grow<T> : I<Grow<Grow<T>>> { }
            public static class Expanding {
                [Node("Grow")] public static Grow<int> Produce() => new();
                [Node("Deep")] public static Grow<Grow<Grow<int>>> Deep(Grow<Grow<int>> input) => new();
            }
            """, 1)]);
        Check(preview.Valid, string.Join("; ", preview.Diagnostics.Select(d => d.Message)));
        Check(preview.Snapshot.Types.Count < 250, "expanding generic discovery has a finite type registry");
        foreach (var method in preview.Snapshot.Methods.Values)
        {
            Check(preview.Snapshot.Types.ContainsKey(method.ResultType!), "declared closed result type preserved");
            foreach (var parameter in method.Parameters) Check(preview.Snapshot.Types.ContainsKey(parameter.TypeId), "declared closed parameter type preserved");
        }
        Check(preview.Snapshot.Types.ContainsKey("global::NodeLib.Grow<global::NodeLib.Grow<global::NodeLib.Grow<global::System.Int32>>>"), "deep explicitly declared constructed type preserved");
        Check(preview.Snapshot.IsAssignable("global::NodeLib.Grow<global::System.Int32>", "global::NodeLib.I<global::NodeLib.Grow<global::NodeLib.Grow<global::System.Int32>>>"), "declared expanding type retains its implemented interface conversion");
        Console.WriteLine("PASS expanding generic preview");
    }

}
