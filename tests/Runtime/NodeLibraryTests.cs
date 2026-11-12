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
    public static Task Run()
    {
        var output = VisualNodeCatalog.DataOutputs(new JsonObject { ["kind"] = "read_entity", ["config"] = new JsonObject() });
        Check(output["value"]?["type"]?.GetValue<string>() == "global::HassSharp.EntityStateSnapshot", "Read entity concrete snapshot");
        var compiler = new NodeLibraryCompiler(new DiagnosticsProvider());
        var preview = compiler.Preview([new NodeLibrarySource("sensors", Source, 7)]);
        Check(preview.Valid, string.Join("; ", preview.Diagnostics.Select(d => d.Message)));
        var snapshot = preview.Snapshot;
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
        Check(!snapshot.IsAssignable("global::System.Double", "global::System.Int32"), "numeric narrowing");
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
        var graph = JsonNode.Parse("""{"version":4,"name":"Rich","nodes":[{"id":"t","kind":"trigger","config":{"type":"manual"}},{"id":"r","kind":"read_entity","config":{"entity":{"kind":"literal","type":"entity_id","value":"sensor.test"}}}],"edges":[{"id":"e","source":"t","target":"r"}]}""")!.AsObject();
        var custom = VisualGraphContract.CreateNode(read.Kind, "custom", snapshot);
        graph["nodes"]!.AsArray().Add(custom);
        graph["edges"]!.AsArray().Add(JsonNode.Parse("""{"id":"e2","source":"r","target":"custom"}"""));
        graph["edges"]!.AsArray().Add(JsonNode.Parse("""{"id":"d","kind":"data","source":"r","sourcePort":"value","target":"custom","targetPort":"entity"}"""));
        Check(VisualGraphValidator.Validate(graph, snapshot).Count == 0, "typed entity to custom graph");
        Check(VisualGraphValidator.Validate(graph).Any(d => d.Message.Contains("Unknown node kind")), "catalog isolation");
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
        var passThrough = VisualGraphContract.CreateNode("set_value", "passthrough", snapshot);
        passThrough["config"]!["type"] = "global::NodeLib.Reading";
        var richGraph = (JsonObject)graph.DeepClone();
        richGraph["nodes"]!.AsArray().Add(passThrough);
        richGraph["edges"]!.AsArray().Add(JsonNode.Parse("""{"id":"e3","source":"custom","target":"passthrough"}"""));
        richGraph["edges"]!.AsArray().Add(JsonNode.Parse("""{"id":"d2","kind":"data","source":"custom","sourcePort":"result","target":"passthrough","targetPort":"value"}"""));
        Check(VisualGraphValidator.Validate(richGraph, snapshot).Count == 0, "rich value flows into generic built-in node");
        var literalMethod = snapshot.Methods.Values.Single(m => m.Method.Name == "Literal");
        var literalNode = VisualGraphContract.CreateNode(literalMethod.Kind, "literal", snapshot);
        var literalGraph = JsonNode.Parse("""{"version":4,"name":"Literal","nodes":[{"id":"t","kind":"trigger","config":{"type":"manual"}}],"edges":[{"id":"e","source":"t","target":"literal"}]}""")!.AsObject();
        literalGraph["nodes"]!.AsArray().Add(literalNode);
        foreach (var invalidNumber in new[] { 1.5, 2147483648d, -2147483649d })
        {
            literalNode["config"]!["arguments"]!["count"] = new JsonObject { ["kind"] = "literal", ["type"] = "global::System.Int32", ["value"] = invalidNumber };
            Check(VisualGraphValidator.Validate(literalGraph, snapshot).Any(d => d.InputId == "count" && d.Message.Contains("Invalid")), "Int32 rejects fractional/out-of-range literal");
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
        Check(fakeAttributes.Snapshot.Catalog["nodes"]!.AsObject().Last().Value!["inputs"]!["text"]!["label"]!.GetValue<string>() == "text", "only real Port attribute overrides labels");
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
        var defaultGraph = JsonNode.Parse("""{"version":4,"name":"Defaults","nodes":[{"id":"t","kind":"trigger","config":{"type":"manual"}}],"edges":[{"id":"e","source":"t","target":"defaults"}]}""")!.AsObject();
        defaultGraph["nodes"]!.AsArray().Add(defaultNode);
        Check(VisualGraphValidator.Validate(defaultGraph, specialDefaults.Snapshot).Count == 0, "nullable enum defaults and JSON defaults validate");
        var attributes = new Dictionary<string, object> { ["nested"] = new List<object> { "original" } };
        var entity = EntityStateSnapshot.From(new HasEntityState { EntityId = "sensor.test", State = "on", Attributes = attributes, LastChanged = 10, LastReported = 20 });
        ((List<object>)attributes["nested"])[0] = "mutated";
        Check(entity.Attributes.GetProperty("nested")[0].GetString() == "original" && entity.LastChanged == DateTimeOffset.FromUnixTimeSeconds(10), "detached immutable attributes/timestamp conversion");
        NodeLibraryFixture.Verify();
        Console.WriteLine("PASS rich node libraries: Roslyn discovery, docs, signatures, conversion, snapshots and graph validation");
        return Task.CompletedTask;
    }
}
