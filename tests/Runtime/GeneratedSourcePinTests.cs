using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>
/// An automation's revision is the SHA-256 of its generated source, and startup regenerates and stores the source of
/// every saved automation. So whatever the generator emits for an existing graph, including the catalog sections the
/// source embeds, is part of every user's automations: a change gives each of them a new revision on upgrade.
/// tests/fixtures/generated-source-pins.json holds the SHA-256 of the generated source of each valid automation case of
/// visual-graph-validation.json (no <c>definition</c>, previewed valid), of <see cref="VisualTests.Graph"/>, and of the
/// graphs built here (keys <c>pin: …</c>) for the generator paths the cases do not reach: logic nodes, node-library
/// nodes, Custom C# value nodes and flows, Select property in scopes and service inputs, trigger modes.
/// </summary>
public static class GeneratedSourcePinTests
{
    const string GraphKey = "VisualTests.Graph";
    /// <summary>
    /// Built-in node kinds no automation graph can use, so no pin can hold them (such as kinds only an entity
    /// definition's graph takes). Every other built-in kind must appear in some pinned graph.
    /// </summary>
    static readonly HashSet<string> UnpinnableKinds = new(["entity_state", "entity_command", "entity_setup", "set_entity_state", "set_entity_available", "setting", "download_file", "camera_snapshot", "image_draw_rect", "image_draw_text"], StringComparer.Ordinal);
    static readonly JsonSerializerOptions Readable = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static string Line(string name, string hash) => JsonSerializer.Serialize(name, Readable) + ": " + JsonSerializer.Serialize(hash, Readable);
    /// <summary>The pin file for these pins: sorted by key, one entry per line, so adding a case is a one-line diff.</summary>
    static string Render(IEnumerable<KeyValuePair<string, string>> pins) =>
        "{\n" + string.Join(",\n", pins.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => "  " + Line(p.Key, p.Value))) + "\n}\n";

    [RuntimeSuite("--generated-source-pins", Order = 11)]
    public static Task Run()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (current, skipped) = Current();
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "generated-source-pins.json"));
        var pins = JsonNode.Parse(text)!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>(), StringComparer.Ordinal);
        var cases = Cases().Select(c => c["name"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        // Line endings aside: a checkout may turn the file's into CRLF.
        if (text.ReplaceLineEndings("\n") != Render(pins)) problems.Add("generated-source-pins.json is not sorted by key with one two-space indented entry per line and a final newline: put its entries in that form, without changing any hash");
        foreach (var (name, pinned) in pins.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (name != GraphKey && !name.StartsWith("pin: ", StringComparison.Ordinal) && !cases.Contains(name))
                problems.Add($"pinned case \"{name}\" no longer exists in visual-graph-validation.json: it stands for automations people have saved, so keep the case and its graph");
            else if (!current.TryGetValue(name, out var now))
                problems.Add($"pinned graph \"{name}\" no longer generates source as a valid automation: an existing automation's revision would change (its source would no longer build)");
            else if (now.Hash != pinned)
                problems.Add($"generated source of \"{name}\" changed (pinned {pinned}, now {now.Hash}): an existing automation's revision would change on upgrade; keep the generated source of existing graphs byte for byte");
        }
        foreach (var (name, now) in current.Where(p => !pins.ContainsKey(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
            problems.Add($"valid automation graph \"{name}\" has no pin: only for a new case, add this line to generated-source-pins.json: {Line(name, now.Hash)}");
        // Every built-in node kind is in some pinned graph, so a change to what any of them generates fails here.
        var pinnedKinds = current.Values.SelectMany(p => (p.Graph["nodes"] as JsonArray ?? []).Select(n => n?["kind"]?.GetValue<string>())).ToHashSet(StringComparer.Ordinal);
        foreach (var (kind, _) in VisualNodeCatalog.Root["nodes"]!.AsObject())
            if (!pinnedKinds.Contains(kind) && !UnpinnableKinds.Contains(kind))
                problems.Add($"built-in node kind \"{kind}\" is in no pinned graph: add a \"pin: …\" graph using it to GeneratedSourcePinTests, or list it in UnpinnableKinds if no automation can use it");
        if (problems.Count > 0) throw new Exception($"Generated source pins: {problems.Count} problem(s):\n" + string.Join("\n", problems));
        var dedicated = current.Keys.Count(k => k.StartsWith("pin: ", StringComparison.Ordinal));
        Console.WriteLine($"PASS generated source pins: {current.Count} graphs pinned ({current.Count - dedicated - 1} fixture cases, {GraphKey} and {dedicated} built here), every built-in node kind in one; {watch.ElapsedMilliseconds} ms");
        Console.WriteLine($"  skipped, as they do not preview as valid ({skipped.Count}): {string.Join(" | ", skipped)}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Not a test: prints the complete content of generated-source-pins.json for the current code. Only a new graph's
    /// line belongs in the file; a changed hash is a changed revision for existing automations.
    /// <c>dotnet run --project tests/Runtime -- --describe-generated-source-pins</c>
    /// </summary>
    [RuntimeSuite("--describe-generated-source-pins", RunByDefault = false)]
    public static Task Describe()
    {
        Console.Write(Render(Current().Pins.Select(p => KeyValuePair.Create(p.Key, p.Value.Hash))));
        return Task.CompletedTask;
    }

    static IEnumerable<JsonObject> Cases() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "visual-graph-validation.json")))!.AsArray().Select(c => c!.AsObject());

    /// <summary>
    /// Each pinnable graph's source hash and the graph itself, and the valid automation cases that do not preview as
    /// valid. A graph built here must preview as valid.
    /// </summary>
    static (Dictionary<string, (string Hash, JsonObject Graph)> Pins, List<string> Skipped) Current()
    {
        var diagnostics = new DiagnosticsProvider();
        var generator = new VisualGenerator(diagnostics);
        var pins = new Dictionary<string, (string Hash, JsonObject Graph)>(StringComparer.Ordinal);
        var skipped = new List<string>();
        foreach (var fixture in Cases())
        {
            var name = fixture["name"]!.GetValue<string>();
            var graph = fixture["graph"] as JsonObject;
            if (fixture["valid"]?.GetValue<bool>() != true || graph == null || graph["definition"] is JsonObject) continue;
            // The snapshot the contract suite validates the case with. It has no compiled libraries, so Preview compiles
            // the built-in catalog afresh and never sees a catalogPatch or the services: a patched case is hashed against
            // the built-in catalog, which is what production generates.
            var snapshot = fixture["catalogPatch"] is JsonObject patch ? VisualGraphContractTests.Patched(patch) : VisualCatalogSnapshot.BuiltIn;
            if (fixture["services"]?.GetValue<bool>() == true) snapshot = snapshot.WithServices(VisualGraphContractTests.Services(name));
            var preview = generator.Preview(graph, snapshot);
            if (preview.Valid && preview.Source.Length > 0) pins[name] = (Hash(preview.Source), preview.Graph ?? graph);
            else skipped.Add(name);
        }
        foreach (var (name, graph, snapshot) in Dedicated(diagnostics, generator))
        {
            var preview = generator.Preview(graph, snapshot);
            if (!preview.Valid || preview.Source.Length == 0) throw new Exception($"Generated source pins: {name} does not preview as valid: {JsonSerializer.Serialize(preview.Diagnostics)}");
            pins[name] = (Hash(preview.Source), preview.Graph ?? graph);
        }
        if (pins.Count < 50) throw new Exception($"Generated source pins: only {pins.Count} graphs preview as valid, too few to pin");
        return (pins, skipped);
    }

    /// <summary>
    /// The graphs pinned beside the fixture cases. Custom C# pins are written from the code by
    /// <see cref="VisualGenerator.DeriveSignatures(JsonObject, VisualCatalogSnapshot)"/>, as the editor's save does, so
    /// they cannot go stale.
    /// </summary>
    static IEnumerable<(string Name, JsonObject Graph, VisualCatalogSnapshot Snapshot)> Dedicated(DiagnosticsProvider diagnostics, VisualGenerator generator)
    {
        var builtIn = VisualCatalogSnapshot.BuiltIn;
        var empty = new NodeLibraryCompiler(diagnostics).Preview([]).Snapshot;
        JsonObject Derive(JsonObject graph, VisualCatalogSnapshot? snapshot = null) => generator.DeriveSignatures(graph, snapshot ?? empty);
        JsonObject Graph(string name, JsonObject[] nodes, params JsonObject[] edges) =>
            new() { ["version"] = 5, ["name"] = name, ["nodes"] = new JsonArray(nodes.ToArray<JsonNode>()), ["edges"] = new JsonArray(edges.ToArray<JsonNode>()) };
        // Edge ids are part of the embedded graph: fixed, never the random ones of the execution helpers.
        static JsonObject Next(string source, string target, string port = "next") => new() { ["id"] = $"{source}:{port}:{target}", ["source"] = source, ["target"] = target, ["port"] = port };
        static JsonObject Wire(string source, string target, string targetPort, string sourcePort = "value") =>
            new() { ["id"] = $"{source}.{sourcePort}:{target}.{targetPort}", ["kind"] = "data", ["source"] = source, ["target"] = target, ["sourcePort"] = sourcePort, ["targetPort"] = targetPort };
        var unset = new { kind = "unset" };
        JsonObject Manual(string id = "t") => N(id, "trigger", new { type = "manual" });
        JsonObject Log(string id, string text) => N(id, "log", new { value = Lit("string", text) });

        yield return (GraphKey, VisualTests.Graph(), builtIn);

        // and, or and xor feed an If; a State condition follows it.
        yield return ("pin: and, or, xor and condition", Graph("Logic", [
            Manual(),
            N("a", "and", new { left = Lit("boolean", true), right = Lit("boolean", false) }),
            N("o", "or", new { left = unset, right = Lit("boolean", false) }),
            N("x", "xor", new { left = unset, right = Lit("boolean", true) }),
            N("i", "if", new { value = unset }),
            N("c", "condition", new { entity = Lit("entity_id", "binary_sensor.door"), state = "on" }),
            Log("on", "door open"), Log("off", "not chosen")],
            Next("t", "i"), Next("i", "c", "true"), Next("i", "off", "false"), Next("c", "on", "true"),
            Wire("a", "o", "left"), Wire("o", "x", "left"), Wire("x", "i", "value")), builtIn);

        // A Custom C# value node, read by the step that uses it.
        yield return ("pin: pure custom node", Derive(Graph("Pure custom", [
            Manual(),
            N("half", "custom", new JsonObject { ["source"] = "public static class CustomNode\n{\n    [Node(\"Half\", Pure = true)]\n    public static double Run(double value) => value / 2;\n}", ["arguments"] = new JsonObject { ["value"] = Lit("number", 3) } }),
            N("l", "log", new { value = unset })],
            Next("t", "l"), Wire("half", "l", "value", "result"))), builtIn);

        // A Custom C# step whose flow is an execution port with a value of its own, and a step after it.
        yield return ("pin: custom node with flows", Derive(Graph("Custom flows", [
            Manual(),
            N("twice", "custom", new JsonObject { ["source"] = "public static class CustomNode\n{\n    [Node(\"Twice\")]\n    public static async Task Run([Port(\"Body\", FlowControl = true)] Func<double, Task> body)\n    {\n        await body(1);\n        await body(2);\n    }\n}", ["arguments"] = new JsonObject() }),
            N("l", "log", new { value = unset }), Log("after", "done")],
            Next("t", "twice"), Next("twice", "l", "body"), Next("twice", "after"), Wire("twice", "l", "value", "body"))), builtIn);

        // Select property of a Custom C# record: in a For each body, in a parallel branch, into a service field and target.
        yield return ("pin: select_property in a loop, a parallel branch and into a service field and target", Derive(Graph("Select", [
            Manual(),
            N("make", "custom", new JsonObject { ["source"] = "public record Reading(string Name, double Level, Entity Lamp);\npublic record Home(Reading[] Readings, Reading First);\npublic static class CustomNode\n{\n    [Node(\"Make\")]\n    public static Home Run() => new([new(\"hall\", 40, new(\"light.hall\"))], new(\"porch\", 60, new(\"light.porch\")));\n}", ["arguments"] = new JsonObject() }),
            N("readings", "select_property", new { property = "Readings" }),
            N("f", "foreach", new { items = unset, itemType = "global::NodeLib.Custom_make.Reading" }),
            N("name", "select_property", new { property = "Name" }),
            N("l", "log", new { value = unset }),
            N("p", "parallel", new { branches = new[] { "a", "b" } }),
            N("first", "select_property", new { property = "First" }),
            N("firstName", "select_property", new { property = "Name" }),
            N("la", "log", new { value = unset }), Log("lb", "other branch"),
            N("level", "select_property", new { property = "Level" }),
            N("lamp", "select_property", new { property = "Lamp" }),
            N("svc", "service", new JsonObject { ["service"] = "light.turn_on", ["fields"] = new JsonObject { ["brightness_pct"] = JsonSerializer.SerializeToNode(unset) }, ["returnResponse"] = false })],
            Next("t", "make"), Next("make", "f"), Next("f", "l", "body"), Next("f", "p"), Next("p", "la", "branch:a"), Next("p", "lb", "branch:b"), Next("p", "svc"),
            Wire("make", "readings", "value", "result"), Wire("readings", "f", "items"), Wire("f", "name", "value", "item"), Wire("name", "l", "value"),
            Wire("make", "first", "value", "result"), Wire("first", "firstName", "value"), Wire("firstName", "la", "value"),
            Wire("first", "level", "value"), Wire("level", "svc", "field:brightness_pct"), Wire("first", "lamp", "value"), Wire("lamp", "svc", "target"))), builtIn);

        // Several triggers: each mode the catalog offers besides Single, one Run now cannot start, one not in run history.
        yield return ("pin: trigger modes and manual off", Graph("Modes", [
            N("restart", "trigger", new { type = "manual", mode = "Restart" }),
            N("queued", "trigger", new { type = "entity", entity = "binary_sensor.door", mode = "Queued" }),
            N("parallel", "trigger", new { type = "startup", mode = "Parallel" }),
            N("auto", "trigger", new { type = "entity", entity = "sensor.power", manual = false }),
            N("quiet", "trigger", new { type = "entity", entity = "sensor.noise", trackHistory = false }),
            Log("l1", "restart"), Log("l2", "queued"), Log("l3", "parallel"), Log("l4", "auto"), Log("l5", "quiet")],
            Next("restart", "l1"), Next("queued", "l2"), Next("parallel", "l3"), Next("auto", "l4"), Next("quiet", "l5")), builtIn);

        // A node library's step and value node, and an automation beside the library that uses neither: the embedded
        // catalog carries every library node kind into every automation of someone who has a library.
        var compiled = new NodeLibraryCompiler(diagnostics).Preview([new("pins", "public static class PinNodes\n{\n    [Node(\"Announce\")]\n    public static async Task Announce(string text)\n    {\n        Logger.Info(text);\n        await Task.CompletedTask;\n    }\n    [Node(\"Twice\", Pure = true)]\n    public static double Twice(double value) => value * 2;\n}", 1)]);
        if (!compiled.Valid) throw new Exception("Generated source pins: the pinned node library does not compile: " + string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
        var library = compiled.Snapshot;
        string Kind(string method) => library.Methods.Values.Single(m => m.MethodName == method).Kind;
        yield return ("pin: node-library node", Graph("Library", [
            Manual(),
            N("announce", Kind("Announce"), new { arguments = new { text = Lit("string", "hello") } }),
            N("twice", Kind("Twice"), new { arguments = new { value = Lit("number", 21) } }),
            N("l", "log", new { value = unset })],
            Next("t", "announce"), Next("announce", "l"), Wire("twice", "l", "value", "result")), library);
        yield return ("pin: automation beside an unused node library", VisualTests.Graph(), library);
    }

    /// <summary>
    /// The pins suite's check for <see cref="VisualTests.Graph"/> alone, for a suite that changes what the generator
    /// emits for other graphs: the graph previews as valid against the built-in catalog, as pinned, and its source has
    /// the pinned hash.
    /// </summary>
    internal static void CheckGraphPin()
    {
        var pins = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "generated-source-pins.json")))!.AsObject();
        var preview = new VisualGenerator(new DiagnosticsProvider()).Preview(VisualTests.Graph(), VisualCatalogSnapshot.BuiltIn);
        if (!preview.Valid || preview.Source.Length == 0) throw new Exception($"Generated source pins: {GraphKey} does not preview as valid: {JsonSerializer.Serialize(preview.Diagnostics)}");
        var pinned = pins[GraphKey]!.GetValue<string>(); var now = Hash(preview.Source);
        if (now != pinned) throw new Exception($"generated source of \"{GraphKey}\" changed (pinned {pinned}, now {now}): an existing automation's revision would change on upgrade");
    }

    // The source exactly as generated, as UTF-8 without a byte order mark.
    static string Hash(string source) => Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false).GetBytes(source)));
}
