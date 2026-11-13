using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>The C1 type rule in C#: refinement before Roslyn, parametric ids and runtime narrowing.</summary>
static class VisualTypesTests
{
    [RuntimeSuite("--visual-types", Order = 15)]
    public static async Task Run()
    {
        var previous = HassPath.HassConfiguration; HassPath.HassConfiguration = Path.Combine(Path.GetTempPath(), "visual-types-" + Guid.NewGuid());
        try { await Types(); } finally { HassPath.HassConfiguration = previous; }
    }

    static async Task Types()
    {
        var catalog = VisualNodeCatalog.GetCatalog();
        var light = VisualTypes.Resolve(catalog, "entity_id:light");
        Check(light?["refines"]?.GetValue<string>() == "entity_id" && light["pattern"]?.GetValue<string>() == "^light\\.[a-z0-9_]+$"
            && light["label"]?.GetValue<string>() == "Light entity" && light["clrType"]?.GetValue<string>() == "System.String", "parametric entity id synthesizes its refinement");
        Check(string.Join(",", VisualTypes.Chain(catalog, "entity_id:light")) == "entity_id:light,entity_id,string", "parametric chain ends at its root");
        Check(VisualTypes.RootId(catalog, "entity_id:binary_sensor") == "string" && VisualTypes.RootId(catalog, "target") == "object", "runtime lookups use the root");
        foreach (var id in new[] { "entity_id:Light", "foo:bar", "entity_id:", "string:x", "global::HassSharp.EntityStateSnapshot:x" })
            Check(VisualTypes.Resolve(catalog, id) == null && VisualTypes.Chain(catalog, id).Count == 0, "malformed parametric id is unknown: " + id);
        Check(VisualTypes.Resolve(catalog, "global::HassSharp.EntityStateSnapshot") != null, "colon-bearing CLR ids resolve exactly");
        foreach (var (source, target, expected) in new[] {
            ("entity_id:light", "entity_id", true), ("entity_id:light", "string", true), ("target", "object", true), ("entity_id", "entity_id", true),
            ("entity_id", "entity_id:light", false), ("string", "entity_id", false), ("entity_id:light", "entity_id:switch", false), ("object", "target", false), ("foo:bar", "foo:bar", false) })
            Check(VisualTypes.IsAssignable(catalog, source, target) == expected && VisualCatalogSnapshot.BuiltIn.IsAssignable(source, target) == expected, $"assignability {source} -> {target}");
        Check(VisualTypes.ValidLiteral(catalog, "entity_id:light", JsonValue.Create("light.kitchen"), true) && !VisualTypes.ValidLiteral(catalog, "entity_id:light", JsonValue.Create("switch.x"), true)
            && !VisualTypes.ValidLiteral(catalog, "entity_id:light", JsonValue.Create("bad id"), true), "literals satisfy every pattern in the chain");

        // A library string parameter is a root CLR type: refined wires reach it through their root.
        var library = new NodeLibraryCompiler(Diagnostics).Preview([new("types", """
            public static class Samples { [Node("Take text")] public static void Take(string text) {} }
            """, 1)]);
        Check(library.Valid, string.Join("; ", library.Diagnostics.Select(d => d.Message)));
        var take = library.Snapshot.Methods.Values.Single(m => m.Method.Name == "Take").Kind;
        Check(library.Snapshot.IsAssignable("entity_id:light", "string") && !library.Snapshot.IsAssignable("string", "entity_id") && !library.Snapshot.IsAssignable("object", "target"), "refinement precedes Roslyn conversions");
        var wired = G(N("s", "set_value", new { type = "entity_id:light", value = Lit("entity_id:light", "light.kitchen") }), VisualGraphContract.CreateNode(take, "take", library.Snapshot));
        wired["nodes"]![2]!["config"]!["arguments"] = new JsonObject { ["text"] = new JsonObject { ["kind"] = "unset" } };
        wired["edges"]!.AsArray().Add(D("s", "take", "text"));
        Check(VisualGraphValidator.Validate(wired, library.Snapshot).Count == 0, "library string parameter accepts an entity_id:light wire: " + JsonSerializer.Serialize(VisualGraphValidator.Validate(wired, library.Snapshot)));
        await Execute(wired, library.Snapshot);

        // A user-defined conversion from a refined value is compiled through its root, exactly as from entity_id.
        var wrapping = new NodeLibraryCompiler(Diagnostics).Preview([new("wrapping", """
            public sealed record Wrapped(string Value) { public static implicit operator Wrapped(string value) => new(value); }
            public static class Wrapping { [Node("Unwrap")] public static void Unwrap(Wrapped value, string expected) { if (value.Value != expected) throw new System.Exception("wrapped value lost"); } }
            """, 1)]);
        Check(wrapping.Valid, string.Join("; ", wrapping.Diagnostics.Select(d => d.Message)));
        var generated = "";
        var unwrap = wrapping.Snapshot.Methods.Values.Single(m => m.Method.Name == "Unwrap").Kind;
        foreach (var source in new[] { "entity_id", "entity_id:light" })
        {
            var consumer = VisualGraphContract.CreateNode(unwrap, "u", wrapping.Snapshot);
            consumer["config"]!["arguments"] = new JsonObject { ["value"] = new JsonObject { ["kind"] = "unset" }, ["expected"] = Lit("string", "light.kitchen") };
            var converted = G(N("s", "set_value", new { type = source, value = Lit(source, "light.kitchen") }), N("c", "convert", new { type = "global::NodeLib.Wrapped", value = new { kind = "unset" } }), consumer);
            converted["edges"]!.AsArray().Add(D("s", "c", "value")); converted["edges"]!.AsArray().Add(D("c", "u", "value"));
            var preview = new VisualGenerator(Diagnostics).Preview(converted, wrapping.Snapshot);
            Check(preview.Valid && preview.Source.Contains("(global::NodeLib.Wrapped)__visual_engine.Input<global::System.String>("), $"user-defined conversion from {source} is compiled: " + JsonSerializer.Serialize(preview.Diagnostics));
            await Execute(converted, wrapping.Snapshot);
            generated = preview.Source;
        }
        Check(!generated.Contains("var responses=") && !generated.Contains(".Responses"), "no legacy responses variable is generated");

        // Convert narrows only a value that satisfies every pattern in the target chain.
        JsonObject Narrow(string text) => G(N("s", "set_value", new { type = "string", value = Lit("string", text) }), N("c", "convert", new { type = "entity_id:light", value = new { kind = "unset" }, output = "light" }),
            N("verify", "custom", new { code = "if(values.Get<string>(\"light\",\"verify\")!=\"light.kitchen\")throw new Exception(\"narrowed value changed\");" }));
        var narrow = Narrow("light.kitchen"); narrow["edges"]!.AsArray().Add(D("s", "c", "value"));
        await Execute(narrow);
        var mismatch = Narrow("switch.kitchen"); mismatch["edges"]!.AsArray().Add(D("s", "c", "value"));
        try { await Execute(mismatch); throw new Exception("pattern mismatch narrowed"); }
        catch (InvalidOperationException error) when (error.Message.Contains("Cannot convert c.value to entity_id:light")) { }
        Console.WriteLine("PASS visual types: parametric resolution, refinement before Roslyn, library root wires and runtime narrowing");
    }
}
