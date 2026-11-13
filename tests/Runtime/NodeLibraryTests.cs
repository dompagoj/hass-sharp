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
        var graph = JsonNode.Parse("""{"version":5,"name":"Rich","nodes":[{"id":"t","kind":"trigger","config":{"type":"manual"}},{"id":"r","kind":"read_entity","config":{"entity":{"kind":"literal","type":"entity_id","value":"sensor.test"}}}],"edges":[{"id":"e","source":"t","target":"r"}]}""")!.AsObject();
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
        var literalGraph = JsonNode.Parse("""{"version":5,"name":"Literal","nodes":[{"id":"t","kind":"trigger","config":{"type":"manual"}}],"edges":[{"id":"e","source":"t","target":"literal"}]}""")!.AsObject();
        literalGraph["nodes"]!.AsArray().Add(literalNode);
        foreach (var invalidNumber in new[] { 1.5, 2147483648d, -2147483649d })
        {
            literalNode["config"]!["arguments"]!["count"] = new JsonObject { ["kind"] = "literal", ["type"] = "global::System.Int32", ["value"] = invalidNumber };
            Check(VisualGraphValidator.Validate(literalGraph, snapshot).Any(d => d.InputId == "count" && d.Message.EndsWith(" is not a valid number", StringComparison.Ordinal)), "Int32 rejects fractional/out-of-range literal: " + string.Join("; ", VisualGraphValidator.Validate(literalGraph, snapshot)));
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
        var defaultGraph = JsonNode.Parse("""{"version":5,"name":"Defaults","nodes":[{"id":"t","kind":"trigger","config":{"type":"manual"}}],"edges":[{"id":"e","source":"t","target":"defaults"}]}""")!.AsObject();
        defaultGraph["nodes"]!.AsArray().Add(defaultNode);
        Check(VisualGraphValidator.Validate(defaultGraph, specialDefaults.Snapshot).Count == 0, "nullable enum defaults and JSON defaults validate");
        var attributes = new Dictionary<string, object> { ["nested"] = new List<object> { "original" } };
        var entity = EntityStateSnapshot.From(new HasEntityState { EntityId = "sensor.test", State = "on", Attributes = attributes, LastChanged = 10, LastReported = 20 });
        ((List<object>)attributes["nested"])[0] = "mutated";
        Check(entity.Attributes.GetProperty("nested")[0].GetString() == "original" && entity.LastChanged == DateTimeOffset.FromUnixTimeSeconds(10), "detached immutable attributes/timestamp conversion");
        NodeLibraryFixture.Verify();
        Console.WriteLine("PASS rich node libraries: Roslyn discovery, docs, signatures, conversion, snapshots and graph validation");
        await RunReview(null);
    }
    [RuntimeSuite("--node-library-review", RunByDefault = false)]
    public static async Task RunReview(string? scenario)
    {
        if (scenario is null or "generic")
        {
            // Run potentially unbounded discovery in a disposable child, so the regression itself
            // has a hard deadline and cannot exhaust the full test harness's stack or memory.
            using var process = new System.Diagnostics.Process { StartInfo = new("dotnet") {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
            process.StartInfo.ArgumentList.Add(typeof(NodeLibraryTests).Assembly.Location);
            process.StartInfo.ArgumentList.Add("--node-library-expanding-child");
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException)
            {
                process.Kill(entireProcessTree: true); await process.WaitForExitAsync();
                throw new Exception("Expanding generic preview must terminate within ten seconds");
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
