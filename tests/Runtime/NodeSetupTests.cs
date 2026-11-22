using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>
/// A node library class's <c>[NodeSetup]</c> method: the compiler checks its signature, that a class has one at most and
/// that it is in a library (not in a C# node's code), and warns at one in a class without nodes. Each <c>[Node]</c> of
/// such a class works only in an entity definition's graph, so an automation refuses it, and a library save that would
/// put one in an automation using it is refused. A definition using one is generated with its class's setup in
/// <c>SharpNodeSetups</c>; every other graph's source is as it was. Its host runs each such setup once as the definition
/// prepares, before its C# nodes and its When started flow and on their set-up clock, and disposes what the setup owns
/// when the definition stops; a setup that throws or runs out of time fails the start, naming its class.
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
            (nameof(OnlyAGraphWithASetupClassGetsSharpNodeSetups), () => { OnlyAGraphWithASetupClassGetsSharpNodeSetups(); return Task.CompletedTask; }),
            (nameof(RunsOncePerDefinitionBeforeWhenStarted), RunsOncePerDefinitionBeforeWhenStarted),
            (nameof(OwnDisposesOnStop), OwnDisposesOnStop),
            (nameof(OwnAfterStopDisposesAtOnce), OwnAfterStopDisposesAtOnce),
            (nameof(SetupClockExcludesDownloadTime), SetupClockExcludesDownloadTime),
            (nameof(FailureFailsTheStart), FailureFailsTheStart),
            (nameof(DisposesInReverseThroughAFailure), DisposesInReverseThroughAFailure),
            (nameof(NodeSetupsOfReadsAll), () => { NodeSetupsOfReadsAll(); return Task.CompletedTask; }),
            (nameof(AContextThatCannotBeMadeNamesTheClass), AContextThatCannotBeMadeNamesTheClass) })
            try { await test(); }
            catch (Exception e) { failures.Add($"{name}: {e.Message}"); }
        if (failures.Count > 0) throw new Exception($"Node setup: {failures.Count} failing:\n" + string.Join("\n", failures));
        Console.WriteLine("PASS node setup: the compiler checks [NodeSetup], its class's nodes work only in entity definitions, a definition using one is generated with its setup, and its host runs it and disposes what it owns");
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
    // --- Through the host: a definition runs its setups as it prepares and disposes what they own when it stops. ---

    /// <summary>
    /// Probe's library: its setup counts itself, logs <c>probe set up on {runtime}</c>, owns a <c>Closer</c> (which logs
    /// <c>probe closed</c> when disposed, or the line it is given) and then runs <paramref name="then"/>; a <c>Refusing</c>
    /// logs <c>second disposing</c> and throws <c>second refused</c> when disposed; its pure node <c>Count()</c> is how many
    /// setups ran in this load context (each definition compiles the library into its own assembly).
    /// </summary>
    static string HostLibrary(string then = "") => """
        public static class Probe
        {
            static int setups;
            [NodeSetup] public static async Task Setup(NodeSetupContext context)
            {
                System.Threading.Interlocked.Increment(ref setups);
                context.Log("probe set up on " + context.RuntimeIdentifier);
                context.Own(new Closer(context));
                await Task.Yield();
        """ + then + """

            }
            /// <summary>How many setups ran.</summary>
            [Node("Probe", Pure = true)] public static int Count() => System.Threading.Volatile.Read(ref setups);
        }
        public sealed class Closer(NodeSetupContext context, string said = "probe closed") : System.IDisposable
        {
            public void Dispose() => context.Log(said);
        }
        public sealed class Refusing(NodeSetupContext context) : System.IDisposable
        {
            public void Dispose()
            {
                context.Log("second disposing");
                throw new System.InvalidOperationException("second refused");
            }
        }
        """;

    static JsonObject Next(string source, string target) { var wire = E(source, target); wire["id"] = $"{source}:next:{target}"; return wire; }
    static JsonObject Data(string source, string target, string targetPort, string sourcePort = "value") { var wire = D(source, target, targetPort, sourcePort); wire["id"] = $"{source}.{sourcePort}:{target}.{targetPort}"; return wire; }
    static JsonObject SetState(string id, string entity) => N(id, "set_entity_state", new JsonObject { ["entity"] = entity });

    /// <summary>
    /// A definition whose When started flow sets sensor <c>count</c> from one Probe and sensor <c>again</c> from another,
    /// then logs <c>When started ran</c>; with <paramref name="more"/>, such as a C# entity, beside them.
    /// </summary>
    static JsonObject Counted(string name, params JsonObject[] more)
    {
        JsonObject[] nodes =
        [
            Hub("entity_count", "count", "sensor"), Hub("entity_again", "again", "sensor"), N("setup", "entity_setup"),
            N("probe", Kind("Probe"), new { arguments = new { } }), N("probe_again", Kind("Probe"), new { arguments = new { } }),
            SetState("set", "count"), SetState("set_again", "again"), N("log", "log", new JsonObject { ["value"] = Lit("string", "When started ran") }),
            .. more,
        ];
        JsonObject[] edges =
        [
            Next("setup", "set"), Next("set", "set_again"), Next("set_again", "log"),
            Data("probe", "set", "value", "result"), Data("probe_again", "set_again", "value", "result"),
        ];
        return new()
        {
            ["name"] = name, ["settings"] = new JsonArray(),
            ["logic"] = new JsonObject { ["kind"] = "graph", ["graph"] = new JsonObject { ["version"] = 5, ["name"] = name, ["nodes"] = new JsonArray(nodes), ["edges"] = new JsonArray(edges) } },
        };
    }

    sealed class Rig(EntityTestHome home, HassSharpManager api)
    {
        public EntityTestHome Home => home;
        public HassSharpManager Api => api;
        public async Task<JsonObject> Request(Func<string, Task<string>> request, object body)
        {
            var answer = JsonNode.Parse(await request(JsonSerializer.Serialize(body)))!;
            Check(answer["ok"]!.GetValue<bool>(), "the request failed: " + answer.ToJsonString());
            return answer["result"]!.AsObject();
        }
        public Task<JsonObject> Entity(object body) => Request(api.EntityRequest, body);
        public async Task Library(string source) =>
            Check((await Request(api.NodeLibraryRequest, new { operation = "save", id = "probe", name = "Probe", expectedRevision = 0, source }))["saved"]!.GetValue<bool>(), "the library saves");
        public Task<JsonObject> Save(string id, JsonObject definition, long revision = 0) => Entity(new { operation = "save", id, expectedRevision = revision, definition });
        public int Count(string line) => home.Logs(PyLogLevel.Info).Count(l => l == line);
        public string Lines() => string.Join(" | ", home.Logs());
    }

    /// <summary>Runs <paramref name="test"/> on a manager of a home of its own whose library is <paramref name="library"/>; unloads it after, unless the test did.</summary>
    static async Task WithRig(string library, Func<Rig, Task> test)
    {
        await using var home = new EntityTestHome("node-setup-host");
        var api = new HassSharpManager(home.Root);
        try
        {
            await api.Init();
            var rig = new Rig(home, api);
            await rig.Library(library);
            await test(rig);
        }
        finally { await api.UnloadAsync(); }
    }

    static string SetUpLine(string name) => $"{name}: probe set up on {RuntimeInformation.RuntimeIdentifier}";

    static Task RunsOncePerDefinitionBeforeWhenStarted() => WithRig(HostLibrary(), async rig =>
    {
        var saved = await rig.Save("counts", Counted("Counts"));
        Check(saved["active"]!.GetValue<bool>() && saved["document"]!["status"]!.GetValue<string>() == "running", "the definition runs: " + saved.ToJsonString());
        string? Value(string uniqueId) => rig.Home.Snapshots().LastOrDefault(s => s["uniqueId"]!.GetValue<string>() == uniqueId && s["available"]!.GetValue<bool>())?["values"]?["value"]?.ToJsonString();
        await EntityTestHome.Until(() => Value("counts:count") != null && Value("counts:again") != null, "the When started flow sets both sensors");
        Check(Value("counts:count") == "1" && Value("counts:again") == "1", $"two Probe nodes, one setup: count {Value("counts:count")}, again {Value("counts:again")}");
        var info = rig.Home.Logs(PyLogLevel.Info);
        var setUp = Array.IndexOf(info, SetUpLine("Counts"));
        Check(rig.Count(SetUpLine("Counts")) == 1 && setUp >= 0 && setUp < Array.IndexOf(info, "When started ran"),
            "the setup logs once, before the When started flow's Log: " + string.Join(" | ", info));

        // Another definition using it runs it again, for itself.
        Check((await rig.Save("again", Counted("Again")))["active"]!.GetValue<bool>(), "the second definition runs");
        await EntityTestHome.Until(() => Value("again:count") != null, "the second definition sets its sensor");
        Check(Value("again:count") == "1" && rig.Count(SetUpLine("Again")) == 1 && rig.Count(SetUpLine("Counts")) == 1
            && rig.Home.Logs(PyLogLevel.Info).Count(l => l.Contains(": probe set up on ", StringComparison.Ordinal)) == 2,
            "one setup per definition: " + rig.Lines());
        Console.WriteLine("PASS node setup: a definition runs the setup of a class its graph uses twice once, before its When started flow; another definition runs its own");
    });

    static async Task OwnDisposesOnStop()
    {
        await using var home = new EntityTestHome("node-setup-own");
        var api = new HassSharpManager(home.Root);
        var unloaded = false;
        try
        {
            await api.Init();
            var rig = new Rig(home, api);
            await rig.Library(HostLibrary());
            const string closed = "Owning: probe closed";
            Check((await rig.Save("owning", Counted("Owning")))["active"]!.GetValue<bool>(), "the definition runs");
            Check(rig.Count(closed) == 0, "nothing is disposed while it runs: " + rig.Lines());
            // Saved again: the replaced revision disposes what its setup owned, and the new one sets up its own.
            Check((await rig.Save("owning", Counted("Owning"), 1))["active"]!.GetValue<bool>(), "the new revision runs");
            await EntityTestHome.Until(() => rig.Count(closed) == 1, "the replaced revision disposes what it owned");
            Check(rig.Count(SetUpLine("Owning")) == 2, "the new revision set up: " + rig.Lines());
            // Turned off.
            Check(!(await rig.Entity(new { operation = "set_enabled", id = "owning", enabled = false }))["active"]!.GetValue<bool>(), "turned off");
            await EntityTestHome.Until(() => rig.Count(closed) == 2, "turning it off disposes what it owned");
            // On again, then unloaded while it runs.
            Check((await rig.Entity(new { operation = "set_enabled", id = "owning", enabled = true }))["active"]!.GetValue<bool>(), "turned on again");
            Check(rig.Count(SetUpLine("Owning")) == 3 && rig.Count(closed) == 2, "turned on, it sets up again: " + rig.Lines());
            unloaded = true;
            await api.UnloadAsync();
            Check(rig.Count(closed) == 3, "unloading disposes what it owned: " + rig.Lines());
        }
        finally { if (!unloaded) await api.UnloadAsync(); }
        Console.WriteLine("PASS node setup: what a setup owns is disposed once when its revision is replaced, turned off or unloaded");
    }

    static Task OwnAfterStopDisposesAtOnce() => WithRig(HostLibrary("""
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(System.Threading.Timeout.Infinite, context.Stopping); } catch (System.OperationCanceledException) { }
                    // Until the stop has released what the setups own: an Own before that is kept, and disposed with the rest.
                    for (var attempt = 1; attempt <= 1000; attempt++)
                    {
                        try { context.Own(new Closer(context, "late closed " + attempt)); }
                        catch (System.Exception refused) { context.Log("late own refused: " + refused.GetType().Name + " at " + attempt); return; }
                        await Task.Delay(5);
                    }
                    context.Log("late own kept");
                });
        """), async rig =>
    {
        Check((await rig.Save("late", Counted("Late")))["active"]!.GetValue<bool>(), "the definition runs");
        Check(!(await rig.Entity(new { operation = "set_enabled", id = "late", enabled = false }))["active"]!.GetValue<bool>(), "turned off");
        const string refused = "Late: late own refused: ObjectDisposedException at ";
        string? Refused() => rig.Home.Logs(PyLogLevel.Info).FirstOrDefault(l => l.StartsWith(refused, StringComparison.Ordinal));
        await EntityTestHome.Until(() => Refused() != null || rig.Count("Late: late own kept") > 0, "an Own after the stop is refused");
        var last = Refused() is { } line && int.TryParse(line[refused.Length..], out var at) ? at : 0;
        Check(last > 0, "an Own after the stop is refused: " + rig.Lines());
        // Every late object is disposed: those kept before the stop released them with the rest, the refused one at once,
        // before Own threw.
        await EntityTestHome.Until(() => rig.Home.Logs(PyLogLevel.Info).Count(l => l.StartsWith("Late: late closed ", StringComparison.Ordinal)) == last, $"the {last} late objects are disposed");
        var info = rig.Home.Logs(PyLogLevel.Info);
        Check(rig.Count("Late: probe closed") == 1 && Array.IndexOf(info, $"Late: late closed {last}") is >= 0 and var closed && closed < Array.IndexOf(info, refused + last),
            "the refused object is disposed at once: " + rig.Lines());
        Console.WriteLine("PASS node setup: an Own after the definition stopped disposes its object at once and throws ObjectDisposedException");
    });

    static async Task SetupClockExcludesDownloadTime()
    {
        await using var server = LoopbackServer.Start();
        try
        {
            // About 1.6 s of receiving, past the 1 s limit, which the clock does not count.
            var model = new byte[6_000];
            new Random(11).NextBytes(model);
            server.Respond("/model.onnx", new() { Body = model, Pieces = 9, Pause = TimeSpan.FromMilliseconds(200) });
            var download = $"""
                        await context.Files.Download("{server.Url("/model.onnx")}", "{Convert.ToHexStringLower(SHA256.HashData(model))}", cancellationToken: context.Stopping);
                """;
            await WithRig(HostLibrary(download), async rig =>
            {
                DefinitionHost.SetupTimeout = TimeSpan.FromSeconds(1);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var saved = await rig.Save("fetch", Counted("Fetch"));
                Check(saved["active"]!.GetValue<bool>() && saved["document"]!["status"]!.GetValue<string>() == "running", "a setup downloading longer than the limit runs: " + saved.ToJsonString());
                Check(clock.Elapsed > TimeSpan.FromMilliseconds(1500), $"the setup downloaded for {clock.Elapsed.TotalMilliseconds} ms");
            });

            await WithRig(HostLibrary("""
                        await Task.Delay(3000, context.Stopping);
                """), async rig =>
            {
                DefinitionHost.SetupTimeout = TimeSpan.FromSeconds(1);
                var saving = rig.Save("slow", Counted("Slow"));
                // Timed from the setup's start, not the compile before it.
                var compiling = System.Diagnostics.Stopwatch.StartNew();
                while (rig.Count(SetUpLine("Slow")) == 0 && !saving.IsCompleted && compiling.Elapsed < TimeSpan.FromSeconds(60)) await Task.Delay(5);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var saved = await saving;
                Check(!saved["active"]!.GetValue<bool>() && saved["activationError"]?.GetValue<string>() == "Slow: Probe: its [NodeSetup] did not finish within 1 s",
                    "a setup past the limit fails the start: " + saved.ToJsonString());
                Check(clock.Elapsed < TimeSpan.FromSeconds(2.5), $"the setup failed {clock.Elapsed.TotalMilliseconds} ms after it started");
                Check(rig.Home.Syncs("slow").Length == 0, "a definition whose setup ran out of time synced");
            });

            // 600 ms of the setup and 600 ms of a C# entity's Setup(): one clock.
            const string waiting = """
                public class Waiting : SharpSensor
                {
                    protected override async Task Setup() => await Task.Delay(600, Stopping);
                }
                """;
            var descriptor = new NodeLibraryCompiler(Diagnostics).Preview([], [new CustomNodeSource("c", waiting, "csharp_entity")]).Snapshot.Custom["c"];
            Check(descriptor.Valid, "the C# entity's code compiles: " + string.Join(" | ", descriptor.Diagnostics.Select(d => d.Message)));
            var entity = EntityNodeTests.CSharpEntity("waiting", "waiting", new() { ["source"] = waiting, ["signature"] = descriptor.Signature!.DeepClone(), ["arguments"] = new JsonObject() });
            var title = descriptor.Signature!["title"]!.GetValue<string>();
            await WithRig(HostLibrary("""
                        await Task.Delay(600, context.Stopping);
                """), async rig =>
            {
                DefinitionHost.SetupTimeout = TimeSpan.FromSeconds(1);
                var saved = await rig.Save("shared", Counted("Shared", entity));
                Check(!saved["active"]!.GetValue<bool>() && saved["activationError"]?.GetValue<string>() == $"Shared: {title}: Setup() did not finish within 1 s",
                    "the setup and the C# entity's Setup() share one clock: " + saved.ToJsonString());
            });
        }
        finally { DefinitionHost.SetupTimeout = TimeSpan.FromSeconds(60); }
        Console.WriteLine("PASS node setup: a setup runs on the C# nodes' set-up clock, which a download's receiving stops; past it, the start fails naming the class or the C# node");
    }

    static Task FailureFailsTheStart() => WithRig(HostLibrary("""
                throw new System.InvalidOperationException("no model");
        """), async rig =>
    {
        var saved = await rig.Save("failing", Counted("Failing"));
        Check(saved["saved"]!.GetValue<bool>() && !saved["active"]!.GetValue<bool>() && saved["activationError"]?.GetValue<string>() == "Failing: Probe: no model",
            "a setup that throws fails the start: " + saved.ToJsonString());
        Check(rig.Home.Syncs("failing").Length == 0, "a failing setup synced");
        var read = await rig.Entity(new { operation = "read", id = "failing" });
        Check(read["status"]!.GetValue<string>() == "failed", "the definition reads failed: " + read.ToJsonString());
        await EntityTestHome.Until(() => rig.Count("Failing: probe closed") == 1, "what the failed setup owned is disposed");
        Console.WriteLine("PASS node setup: a setup that throws fails the start with its class and message, syncs nothing, reads failed, and what it owned is disposed");
    });
    static Task DisposesInReverseThroughAFailure() => WithRig(HostLibrary("""
                context.Own(new Closer(context, "first closed"));
                context.Own(new Refusing(context));
        """), async rig =>
    {
        Check((await rig.Save("two", Counted("Two")))["active"]!.GetValue<bool>(), "the definition runs");
        Check(!(await rig.Entity(new { operation = "set_enabled", id = "two", enabled = false }))["active"]!.GetValue<bool>(), "turned off");
        await EntityTestHome.Until(() => rig.Count("Two: probe closed") == 1, "what the setup owned first is disposed");
        const string warned = "Two: disposing what a node setup owns failed: second refused";
        var all = rig.Home.Logs();
        int At(string line) => Array.IndexOf(all, line);
        // Last owned first: the one that throws, then the one before it, then the setup's first.
        Check(rig.Home.Logs(PyLogLevel.Warn).Count(l => l == warned) == 1
            && At("Two: second disposing") >= 0 && At("Two: second disposing") < At(warned) && At(warned) < At("Two: first closed") && At("Two: first closed") < At("Two: probe closed"),
            "disposed in reverse, past the one that throws: " + rig.Lines());
        Console.WriteLine("PASS node setup: what a setup owns is disposed last first, and one whose Dispose throws is warned of and the rest are still disposed");
    });

    /// <summary>An assembly of <paramref name="source"/>, loaded.</summary>
    static System.Reflection.Assembly Assembled(string source)
    {
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("NodeSetups" + Guid.NewGuid().ToString("N"),
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source)], NodeLibraryCompiler.FrameworkReferences(),
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        Check(emitted.Success, "the assembly compiles: " + string.Join(" | ", emitted.Diagnostics));
        return System.Reflection.Assembly.Load(image.ToArray());
    }

    static void NodeSetupsOfReadsAll()
    {
        Check(HassSharpManager.NodeSetupsOf(Assembled("public static class Other { }")).Count == 0, "an assembly without SharpNodeSetups has no setups");
        var read = HassSharpManager.NodeSetupsOf(Assembled("""
            public static class SharpNodeSetups
            {
                public static readonly (string Class, System.Func<HassSharp.NodeSetupContext, System.Threading.Tasks.Task> Setup)[] All =
                    [("B", _ => System.Threading.Tasks.Task.CompletedTask), ("A", _ => System.Threading.Tasks.Task.CompletedTask)];
            }
            """));
        Check(read.Select(setup => setup.Class).SequenceEqual(["B", "A"]) && read.All(setup => setup.Run != null), "the setups are All's, in order: " + string.Join(", ", read));
        foreach (var (source, message) in new[] {
            ("public static class SharpNodeSetups { }", "The compiled definition's SharpNodeSetups has no public static field All"),
            ("public static class SharpNodeSetups { public static readonly string[] All = []; }", "The compiled definition's SharpNodeSetups.All is a System.String[], not an array of (string Class, Func<NodeSetupContext, Task> Setup)"),
            ("public static class SharpNodeSetups { public static readonly (string, System.Func<HassSharp.NodeSetupContext, System.Threading.Tasks.Task>)[]? All = null; }", "The compiled definition's SharpNodeSetups.All is null") })
        {
            try
            {
                HassSharpManager.NodeSetupsOf(Assembled(source));
                throw new Exception("a SharpNodeSetups without a readable All was taken for none: " + source);
            }
            catch (InvalidOperationException refused) { Check(refused.Message == message, "the refusal: " + refused.Message); }
        }
        Console.WriteLine("PASS node setup: the setups of a compiled definition are its SharpNodeSetups.All, none without it; a SharpNodeSetups without a readable All is an error");
    }
    /// <summary>On a manager without a directory for downloads the setup's context cannot be made: that fails the start naming the class, as the setup's own failure would.</summary>
    static async Task AContextThatCannotBeMadeNamesTheClass()
    {
        await using var home = new EntityTestHome("node-setup-context");
        var manager = new UserScriptManager { Pump = new EntityStatePump(TimeSpan.FromMilliseconds(5)) };
        var compiled = EntityHostTests.Compiled("bare", "Bare", EntityHostTests.Device("tool", "Meter"), () => new ToolIdle());
        var ran = false;
        compiled.Definition = compiled.Definition! with { NodeSetups = [new("Probe", _ => { ran = true; return Task.CompletedTask; })] };
        Exception? failure = null;
        await manager.LoadUserScripts([compiled], (_, error) => failure = error);
        Check(failure?.Message == "Bare: Probe: Downloads have no directory here" && !ran, "the start fails naming the class: " + failure);
        await manager.UnloadUserScriptsAsync();
        Console.WriteLine("PASS node setup: a setup's context that cannot be made fails the start naming the class");
    }
}
