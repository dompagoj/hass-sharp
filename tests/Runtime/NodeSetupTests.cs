using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>
/// A node library class's <c>[NodeSetup]</c> method: the compiler checks its signature, that a class has one at most and
/// that it is in a library (not in a C# node's code), and warns at one in a class without nodes. Each <c>[Node]</c> of
/// such a class works only in an entity definition's graph, so an automation refuses it, and a library save that would
/// put one in an automation using it is refused. A definition using one is generated with its class's setup in
/// <c>SharpNodeSetups</c>; every other graph's source is as it was.
/// </summary>
static class NodeSetupTests
{
    const string SignatureMessage = "A setup method is public static Task Setup(NodeSetupContext context): public, static, returning Task and taking one NodeSetupContext";
    const string SecondMessage = "A class has at most one [NodeSetup] method; this is its second";
    const string NoNodesMessage = "[NodeSetup] runs for the [Node] methods of its class, and this class has none";
    const string LibraryOnlyMessage = "[NodeSetup] works in a node library only; move this class into one";

    /// <summary>Probe has a setup and a value node, Plain only a value node.</summary>
    const string Library = """
        public static class Probe
        {
            [NodeSetup] public static Task Setup(NodeSetupContext context) => Task.CompletedTask;
            /// <summary>Counts.</summary>
            [Node("Probe", Pure = true)] public static int Count() => 1;
        }
        public static class Plain
        {
            /// <summary>Counts too.</summary>
            [Node("Plain", Pure = true)] public static int Count() => 2;
        }
        """;
    /// <summary>The library without Probe's setup.</summary>
    static readonly string WithoutSetup = Library.Replace("    [NodeSetup] public static Task Setup(NodeSetupContext context) => Task.CompletedTask;\n", "");
    static string Kind(string type) => "library:probe:" + Uri.EscapeDataString("global::NodeLib." + type + ".Count");
    static NodeLibraryPreview Compile(string source, params CustomNodeSource[] custom) => new NodeLibraryCompiler(Diagnostics).Preview([new NodeLibrarySource("probe", source, 1)], custom);
    static string Listed(IEnumerable<NodeLibraryDiagnostic> diagnostics) => string.Join(" | ", diagnostics.Select(d => $"{d.Severity} {d.StartLine}:{d.StartColumn} {d.Message}"));

    [RuntimeSuite("--node-setup", Order = 14)]
    public static async Task Run()
    {
        var failures = new List<string>();
        foreach (var (name, test) in new (string, Func<Task>)[] {
            (nameof(CompilerChecksTheSetup), () => { CompilerChecksTheSetup(); return Task.CompletedTask; }),
            (nameof(RefusedInAnAutomation), () => { RefusedInAnAutomation(); return Task.CompletedTask; }),
            (nameof(AddingASetupToAClassAnAutomationUsesIsRefused), AddingASetupToAClassAnAutomationUsesIsRefused),
            (nameof(OnlyAGraphWithASetupClassGetsSharpNodeSetups), () => { OnlyAGraphWithASetupClassGetsSharpNodeSetups(); return Task.CompletedTask; }) })
            try { await test(); }
            catch (Exception e) { failures.Add($"{name}: {e.Message}"); }
        if (failures.Count > 0) throw new Exception($"Node setup: {failures.Count} failing:\n" + string.Join("\n", failures));
        Console.WriteLine("PASS node setup: the compiler checks [NodeSetup], its class's nodes work only in entity definitions, and a definition using one is generated with its setup");
    }

    static void CompilerChecksTheSetup()
    {
        var preview = Compile(Library);
        Check(preview.Valid && preview.Diagnostics.IsEmpty, "the library with a setup compiles: " + Listed(preview.Diagnostics));
        var setup = preview.Snapshot.Methods[Kind("Probe")].Setup;
        Check(setup == new NodeSetupDescriptor("Probe", "global::NodeLib.Probe", "Setup"), "Probe's node has its class's setup: " + setup);
        var nodes = preview.Snapshot.Catalog["nodes"]!;
        Check(nodes[Kind("Probe")]!["documents"] is JsonArray documents && documents.Select(d => d!.GetValue<string>()).SequenceEqual(["entity"]),
            "Probe's node works in entity definitions only: " + nodes[Kind("Probe")]!["documents"]?.ToJsonString());
        Check(preview.Snapshot.Methods[Kind("Plain")].Setup == null && !nodes[Kind("Plain")]!.AsObject().ContainsKey("documents"), "Plain's node has no setup and no documents");

        // A setup of any other shape is refused at its name, with the shape it must have.
        foreach (var member in new[] {
            "internal static Task Setup(NodeSetupContext c) => Task.CompletedTask;",
            "public static void Setup(NodeSetupContext c) { }",
            "public static Task Setup() => Task.CompletedTask;",
            "public static Task Setup(NodeSetupContext c, int x) => Task.CompletedTask;",
            "public static Task Setup<T>(NodeSetupContext c) => Task.CompletedTask;",
            "public Task Setup(NodeSetupContext c) => Task.CompletedTask;" })
        {
            var source = "public class Probe\n{\n    [NodeSetup] " + member + "\n    /// <summary>Counts.</summary>\n    [Node(\"Probe\", Pure = true)] public static int Count() => 1;\n}";
            var refused = Compile(source);
            Check(!refused.Valid && refused.Diagnostics is [{ Severity: 3 } only] && only.Message == SignatureMessage
                && only.StartLine == 3 && only.StartColumn == "    [NodeSetup] ".Length + member.IndexOf("Setup", StringComparison.Ordinal) + 1,
                $"{member} is refused at its name with the signature sentence: " + Listed(refused.Diagnostics));
        }
        // A setup in an internal class is refused too, as a [Node] there would be.
        var hidden = Compile("internal class Hidden\n{\n    [NodeSetup] public static Task Setup(NodeSetupContext c) => Task.CompletedTask;\n    public static int Count() => 1;\n}");
        Check(hidden.Diagnostics.Count(d => d.Message == SignatureMessage) == 1, "a setup in an internal class is refused: " + Listed(hidden.Diagnostics));

        // A second setup in one class is refused at the second.
        var two = Compile(Library.Replace("    /// <summary>Counts.</summary>", "    [NodeSetup] public static Task Prepare(NodeSetupContext context) => Task.CompletedTask;\n    /// <summary>Counts.</summary>"));
        Check(!two.Valid && two.Diagnostics is [{ Severity: 3, StartLine: 4 } second] && second.Message == SecondMessage, "a second setup is refused once, at the second: " + Listed(two.Diagnostics));

        // A setup in a class without nodes is a warning: it would never run.
        var lonely = Compile(Library + "\npublic static class Lonely\n{\n    [NodeSetup] public static Task Setup(NodeSetupContext context) => Task.CompletedTask;\n}");
        Check(lonely.Valid && lonely.Diagnostics is [{ Severity: 2 } warning] && warning.Message == NoNodesMessage, "a setup in a class without nodes is a warning: " + Listed(lonely.Diagnostics));

        // In a C# node's code it is refused: a setup belongs to a node library.
        const string setupLine = "    [NodeSetup] public static Task Setup(NodeSetupContext c) => Task.CompletedTask;\n";
        var customs = Compile(Library,
            new CustomNodeSource("step", "public static class CustomNode\n{\n" + setupLine + "    [Node(\"Check\")] public static void Run() { }\n}"),
            new CustomNodeSource("fan", "public class Fan : SharpSwitch\n{\n" + setupLine + "}", "csharp_entity"),
            new CustomNodeSource("box", "public class Box : SharpDevice\n{\n" + setupLine + "}", "csharp_device"));
        Check(customs.Valid, "the library stays valid: " + Listed(customs.Diagnostics));
        foreach (var id in new[] { "step", "fan", "box" })
        {
            var custom = customs.Snapshot.Custom[id];
            Check(!custom.Valid && custom.Diagnostics.Count(d => d.Severity == 3 && d.Message == LibraryOnlyMessage && d.StartLine == 3) == 1,
                $"{id}: a setup in a C# node's code is refused: " + Listed(custom.Diagnostics));
        }
        Console.WriteLine("PASS node setup: a [NodeSetup] is public static Task Setup(NodeSetupContext), one per class, in a class with nodes, in a node library; its class's nodes are for entity definitions");
    }

    /// <summary>A pure Probe of <paramref name="type"/> into a Log, run by a manual trigger.</summary>
    static JsonObject Counting(string type, bool definition)
    {
        var graph = new JsonObject
        {
            ["version"] = 5, ["name"] = "Counting",
            ["nodes"] = new JsonArray(N("t", "trigger", new { type = "manual" }), N("probe", Kind(type), new { arguments = new { } }), N("log", "log", new { value = new { kind = "unset" } })),
            ["edges"] = new JsonArray(E("t", "log"), D("probe", "log", "value", "result")),
        };
        if (definition) graph["definition"] = new JsonObject { ["settings"] = new JsonArray() };
        return graph;
    }

    static void RefusedInAnAutomation()
    {
        var preview = new VisualGenerator(Diagnostics).Preview(Counting("Probe", false), Compile(Library).Snapshot);
        Check(!preview.Valid && preview.Diagnostics is [{ Code: "node.document", NodeId: "probe" } only]
            && only.Message == "Probe only works in the graph of an entity definition; delete it from this automation",
            "an automation refuses Probe: " + JsonSerializer.Serialize(preview.Diagnostics));
        Check(new VisualGenerator(Diagnostics).Preview(Counting("Plain", false), Compile(Library).Snapshot).Valid, "an automation takes Plain");
        Console.WriteLine("PASS node setup: an automation refuses a node of a class with a setup, with node.document");
    }

    static async Task AddingASetupToAClassAnAutomationUsesIsRefused()
    {
        await using var home = new EntityTestHome("node-setup");
        var api = new HassSharpManager(home.Root);
        try
        {
            await api.Init();
            async Task<JsonObject> Library(object body) => JsonNode.Parse(await api.NodeLibraryRequest(JsonSerializer.Serialize(body)))!.AsObject();
            async Task<JsonObject> Visual(object body) => JsonNode.Parse(await api.VisualRequest(JsonSerializer.Serialize(body)))!.AsObject();
            JsonNode Ok(JsonObject answer) { Check(answer["ok"]!.GetValue<bool>(), "the request failed: " + answer.ToJsonString()); return answer["result"]!; }
            Ok(await Library(new { operation = "save", id = "probe", name = "Probe", expectedRevision = 0, source = WithoutSetup }));
            var saved = Ok(await Visual(new { operation = "save", id = "counts", graph = Counting("Probe", false), expectedRevision = 0 }));
            Ok(await Visual(new { operation = "set_enabled", id = "counts", enabled = true }));
            Check(saved["active"]!.GetValue<bool>(), "the automation using Probe runs: " + saved.ToJsonString());
            var refused = await Library(new { operation = "save", id = "probe", name = "Probe", expectedRevision = 1, source = NodeSetupTests.Library });
            Check(!refused["ok"]!.GetValue<bool>() && refused["error"]!["code"]!.GetValue<string>() == "validation", "adding the setup is refused: " + refused.ToJsonString());
            var automations = refused["error"]!["detail"]!["automations"]!.AsArray();
            Check(automations is [{ } automation] && automation["id"]!.GetValue<string>() == "counts"
                && automation["diagnostics"]!.AsArray().Any(d => d!["code"]?.GetValue<string>() == "node.document" && d["nodeId"]?.GetValue<string>() == "probe"),
                "the refusal names the automation and node.document: " + refused.ToJsonString());
            Check(Ok(await Library(new { operation = "read", id = "probe" }))["revision"]!.GetValue<long>() == 1, "the stored library is still revision 1");
            Check(Ok(await Visual(new { operation = "read", id = "counts" }))["active"]!.GetValue<bool>(), "the automation keeps running");
        }
        finally { await api.UnloadAsync(); }
        Console.WriteLine("PASS node setup: a library save that adds a setup to a class an automation uses is refused, naming it with node.document");
    }

    static void OnlyAGraphWithASetupClassGetsSharpNodeSetups()
    {
        var snapshot = Compile(Library).Snapshot;
        var probe = new VisualGenerator(Diagnostics).Preview(Counting("Probe", true), snapshot);
        Check(probe.Valid, "a definition takes Probe: " + JsonSerializer.Serialize(probe.Diagnostics));
        Check(probe.Source.Contains("public static class SharpNodeSetups", StringComparison.Ordinal) && probe.Source.Contains("(\"Probe\", global::NodeLib.Probe.Setup)", StringComparison.Ordinal),
            "a definition using Probe has its setup: " + probe.Source[Math.Max(0, probe.Source.IndexOf("SharpNodeSetups", StringComparison.Ordinal) - 40)..]);
        // The setup's entry is the section of the node that uses it.
        var line = probe.Source.Split('\n').ToList().FindIndex(l => l.Contains("(\"Probe\", global::NodeLib.Probe.Setup)", StringComparison.Ordinal)) + 1;
        Check(probe.SourceMap.Any(m => m.NodeId == "probe" && m.StartLine <= line && line <= m.EndLine), "the setup's entry is Probe's section");
        var plain = new VisualGenerator(Diagnostics).Preview(Counting("Plain", true), snapshot);
        Check(plain.Valid && !plain.Source.Contains("SharpNodeSetups", StringComparison.Ordinal), "a definition using only Plain has no SharpNodeSetups: " + JsonSerializer.Serialize(plain.Diagnostics));
        Console.WriteLine("PASS node setup: only a definition using a node of a class with a setup gets SharpNodeSetups, each entry its first node's");
    }
}
