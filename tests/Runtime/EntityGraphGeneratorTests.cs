using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>
/// What the generator emits for the roots of an entity definition's graph: one action and one <c>Run_&lt;hex&gt;</c>
/// method per root, in id order, and one of each per flow of an Entity node. An Entity node's state is worked out at
/// startup and again, restarting, whenever an entity its values read changes: <see cref="StateDependencies.Of"/> names
/// those entities, the ones chosen (not connected) on the entity id inputs of the value nodes upstream of it
/// (<see cref="VisualNodeCatalog.ValueUpstream(JsonObject, string, VisualCatalogSnapshot)"/>). A command flow is queued,
/// a When started flow runs once, and neither is run by hand or at startup. A trigger keeps the action it has in an
/// automation, and an automation's source does not change.
/// </summary>
static class EntityGraphGeneratorTests
{
    const string Action = "new global::HassSharp.AutomationAction(";
    static readonly VisualCatalogSnapshot BuiltIn = VisualCatalogSnapshot.BuiltIn;

    static JsonObject Case(string name) => Fixture(name).Graph;
    /// <summary>A case of visual-graph-validation.json: its graph and the snapshot the contract suite validates it with.</summary>
    static (JsonObject Graph, VisualCatalogSnapshot Snapshot) Fixture(string name)
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "visual-graph-validation.json")))!.AsArray()
            .Single(c => c!["name"]!.GetValue<string>() == name)!;
        return (fixture["graph"]!.AsObject(), fixture["catalogPatch"] is JsonObject patch ? VisualGraphContractTests.Patched(patch) : BuiltIn);
    }
    /// <summary>An entity definition's graph: the nodes, whose Entity nodes declare its entities, the wires, and a definition of these settings.</summary>
    static JsonObject Entity(JsonObject[] nodes, JsonObject[] edges, params (string Key, string Type)[] settings) => new()
    {
        ["version"] = 5, ["name"] = "Entity", ["nodes"] = new JsonArray(nodes), ["edges"] = new JsonArray(edges),
        ["definition"] = new JsonObject
        {
            ["settings"] = new JsonArray(settings.Select(s => (JsonNode)new JsonObject { ["key"] = s.Key, ["type"] = s.Type }).ToArray()),
        },
    };
    static readonly object Unset = new { kind = "unset" };
    static JsonObject Is(string id, string entity) => N(id, "entity_is", new JsonObject { ["entity"] = Lit("entity_id", entity), ["state"] = Lit("string", "on") });
    static JsonObject Not(string id) => N(id, "not", new { value = Unset });
    static JsonObject Two(string id, string kind) => N(id, kind, new { left = Unset, right = Unset });
    /// <summary>The binary sensor's Entity node <paramref name="id"/> of the key, its state worked out from the values wired to it.</summary>
    static JsonObject State(string id, string key) => Hub(id, key, "binary_sensor");
    static string Ids(IEnumerable<JsonObject> nodes) => string.Join(", ", nodes.Select(n => n["id"]!.GetValue<string>()));
    static string Texts(IEnumerable<string> items) => "[" + string.Join(", ", items) + "]";

    static VisualPreview Preview(JsonObject graph, string what)
    {
        var preview = new VisualGenerator(Diagnostics).Preview(graph, BuiltIn);
        Check(preview.Valid && preview.Source.Length > 0, $"{what} previews as valid: {JsonSerializer.Serialize(preview.Diagnostics)}");
        return preview;
    }
    /// <summary>The generated action lines, each without the comma that separates it from the next.</summary>
    static List<string> ActionLines(VisualPreview preview) =>
        preview.Source.Split('\n').Where(l => l.StartsWith(Action, StringComparison.Ordinal)).Select(l => l.TrimEnd(',')).ToList();
    static string ActionLine(string node, string mode, bool startup, bool manual, params string[] entities) =>
        $"{Action}nameof({VisualNodeCatalog.EffectiveTriggerMethod(new JsonObject { ["id"] = node })}),global::HassSharp.AutomationMode.{mode},Startup:{(startup ? "true" : "false")},Manual:{(manual ? "true" : "false")}," +
        $"Entities:[{string.Join(",", entities.Select(e => $"new global::HassSharp.EntityReference({JsonSerializer.Serialize(e)})"))}],Schedules:[],Triggers:[])";
    static string Describe(AutomationAction a) =>
        $"{a.Method} {a.Mode} startup={a.Startup} manual={a.Manual} entities={Texts((a.Entities ?? []).Select(e => e.EntityId))} schedules={a.Schedules?.Length ?? 0} triggers={a.Triggers?.Length ?? 0}";
    static string Describe(string node, AutomationMode mode, bool startup, bool manual, params string[] entities) =>
        $"{VisualNodeCatalog.EffectiveTriggerMethod(new JsonObject { ["id"] = node })} {mode} startup={startup} manual={manual} entities={Texts(entities)} schedules=0 triggers=0";
    /// <summary>The method of the Entity node <paramref name="node"/>'s flow on <paramref name="port"/>.</summary>
    static string Method(string node, string port) => VisualNodeCatalog.FlowMethod(new JsonObject { ["id"] = node }, port);
    /// <summary>The generated action line of an Entity node's flow, which is never run by hand.</summary>
    static string Line(string node, string port, string mode, bool startup, params string[] entities) =>
        $"{Action}nameof({Method(node, port)}),global::HassSharp.AutomationMode.{mode},Startup:{(startup ? "true" : "false")},Manual:false," +
        $"Entities:[{string.Join(",", entities.Select(e => $"new global::HassSharp.EntityReference({JsonSerializer.Serialize(e)})"))}],Schedules:[],Triggers:[])";
    /// <summary>An Entity node's flow as its compiled action describes it.</summary>
    static string Described(string node, string port, AutomationMode mode, bool startup, params string[] entities) =>
        $"{Method(node, port)} {mode} startup={startup} manual=False entities={Texts(entities)} schedules=0 triggers=0";

    /// <summary>
    /// GeneratedSourceCompiles: the preview's source compiles as an automation would (through <see cref="CodeCompiler"/>),
    /// and its configuration's actions, each naming a public method of the class that takes nothing and returns a Task.
    /// Nothing is run: the runtime of the entity kinds is not this suite's.
    /// </summary>
    static async Task<List<string>> Compiled(VisualPreview preview, string what)
    {
        var compiled = await new CodeCompiler(Diagnostics).CompileSingleFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cs"), preview.Source);
        try
        {
            var type = compiled.Assembly.GetType("VisualAutomation");
            Check(type != null, $"{what}: the generated source declares VisualAutomation");
            var actions = ((Automation)Activator.CreateInstance(type!)!).Configure().Actions;
            foreach (var action in actions)
                Check(type!.GetMethod(action.Method) is { } method && method.ReturnType == typeof(Task) && method.GetParameters().Length == 0,
                    $"{what}: action {action.Method} names a public method taking nothing and returning a Task");
            return actions.Select(Describe).ToList();
        }
        finally { compiled.Unload(); }
    }
    static void Same(IReadOnlyList<string> got, IReadOnlyList<string> want, string what) =>
        Check(got.SequenceEqual(want), $"{what}:\n  expected {string.Join("\n           ", want)}\n  got      {string.Join("\n           ", got)}");
    /// <summary>
    /// Each root, and each flow of an Entity node (a root with its port), has a section of the source map for its action
    /// and one for its method, in root order.
    /// </summary>
    static void Mapped(VisualPreview preview, (string Node, string? Port)[] roots, string what)
    {
        var ids = roots.Select(r => r.Node).ToList();
        var mapped = preview.SourceMap.Select(m => m.NodeId).Where(ids.Contains).ToList();
        Same(mapped, ids.Concat(ids).ToList(), $"{what}: the source map has each root's action, then each root's method");
        foreach (var (root, port) in roots)
            Check(preview.Source.Contains(port == null
                ? $"public global::System.Threading.Tasks.Task {VisualNodeCatalog.EffectiveTriggerMethod(new JsonObject { ["id"] = root })}() => __visual_run({JsonSerializer.Serialize(root)});"
                : $"public global::System.Threading.Tasks.Task {Method(root, port)}() => __visual_run({JsonSerializer.Serialize(root)},{JsonSerializer.Serialize(port)});", StringComparison.Ordinal),
                $"{what}: root {root} {port} has its Run method");
    }

    [RuntimeSuite("--entity-graph-generator", Order = 11)]
    public static async Task Run()
    {
        // Every test runs, and each failure is reported under its name.
        var failures = new List<string>();
        foreach (var (name, test) in new (string, Func<Task>)[] {
            (nameof(DerivedSensorHasOneRestartStartupActionWithBothSources), DerivedSensorHasOneRestartStartupActionWithBothSources),
            (nameof(DependenciesComeFromChosenEntitiesOnly), DependenciesComeFromChosenEntitiesOnly),
            (nameof(ValueUpstreamWalksValueWiresOnly), () => { ValueUpstreamWalksValueWiresOnly(); return Task.CompletedTask; }),
            (nameof(CommandAndSetupRootsBecomeActions), CommandAndSetupRootsBecomeActions),
            (nameof(AnEntityNodesFlowsBecomeActions), AnEntityNodesFlowsBecomeActions),
            (nameof(ATriggerInAnEntityGraphKeepsItsOwnAction), ATriggerInAnEntityGraphKeepsItsOwnAction),
            (nameof(AutomationSourceIsUnchanged), () => { AutomationSourceIsUnchanged(); return Task.CompletedTask; }) })
            try { await test(); }
            catch (Exception e) { failures.Add($"{name}: {e.Message}"); }
        if (failures.Count > 0) throw new Exception($"Entity graph generator: {failures.Count} failing:\n" + string.Join("\n", failures));
        Console.WriteLine("PASS entity graph generator: one action and method per root and per Entity node flow in id order, state dependencies from chosen entities, triggers and automations unchanged");
    }

    static async Task DerivedSensorHasOneRestartStartupActionWithBothSources()
    {
        var graph = Case("entity graph: derived binary sensor");
        Same(StateDependencies.Of(graph, "s", BuiltIn), ["binary_sensor.motion_a", "binary_sensor.motion_b"], "the derived sensor depends on both sources");
        var preview = Preview(graph, "the derived sensor");
        Same(ActionLines(preview), [Line("s", "state", "Restart", true, "binary_sensor.motion_a", "binary_sensor.motion_b")], "the derived sensor's one action");
        Mapped(preview, [("s", "state")], "the derived sensor");
        Same(await Compiled(preview, "the derived sensor"), [Described("s", "state", AutomationMode.Restart, true, "binary_sensor.motion_a", "binary_sensor.motion_b")], "the derived sensor's compiled configuration");
        Embeds(preview, graph, ["entity", "entity_is", "or"], "the derived sensor");
    }

    /// <summary>
    /// What the runtime reads an entity graph's nodes from: the source embeds the graph with its definition, and the
    /// embedded catalog has the entity domains whole and the spec of each kind the graph uses.
    /// </summary>
    static void Embeds(VisualPreview preview, JsonObject graph, string[] kinds, string what)
    {
        var embedded = JsonNode.Parse(preview.EmbeddedCatalog!)!.AsObject();
        Check(JsonNode.DeepEquals(embedded["entityDomains"], VisualNodeCatalog.Root["entityDomains"]), $"{what}: the embedded catalog has the entity domains whole");
        foreach (var kind in kinds)
            Check(embedded["nodes"]?[kind] != null && JsonNode.DeepEquals(embedded["nodes"]?[kind], VisualNodeCatalog.Root["nodes"]![kind]), $"{what}: the embedded catalog has the {kind} spec");
        var embeddedGraph = preview.Graph ?? graph;
        Check(embeddedGraph["definition"] is JsonObject && preview.Source.Contains(VisualGenerator.Pieces(embeddedGraph.ToJsonString()), StringComparison.Ordinal),
            $"{what}: the source embeds the graph with its definition");
    }

    static async Task DependenciesComeFromChosenEntitiesOnly()
    {
        // s <- or1 <- (and1 <- (a, n1 <- n2 <- c), or2 <- (b, d)): a is two value nodes up, c four; d repeats a's entity.
        // Ordinal order puts a1 before a_b before ab, which a culture's order does not.
        var graph = Entity(
            [Is("a", "binary_sensor.ab"), Is("b", "binary_sensor.a_b"), Is("c", "binary_sensor.a1"), Is("d", "binary_sensor.ab"),
             Two("and1", "and"), Two("or1", "or"), Two("or2", "or"), Not("n1"), Not("n2"), State("s", "any")],
            [D("or1", "s", "is_on"), D("and1", "or1", "left"), D("or2", "or1", "right"), D("a", "and1", "left"), D("n1", "and1", "right"),
             D("n2", "n1", "value"), D("c", "n2", "value"), D("b", "or2", "left"), D("d", "or2", "right")]);
        string[] want = ["binary_sensor.a1", "binary_sensor.a_b", "binary_sensor.ab"];
        Same(StateDependencies.Of(graph, "s", BuiltIn), want, "a State's dependencies are its chosen entities, each once, in ordinal order");
        var preview = Preview(graph, "the deep State");
        Same(ActionLines(preview), [Line("s", "state", "Restart", true, want)], "the deep State's action");
        Same(await Compiled(preview, "the deep State"), [Described("s", "state", AutomationMode.Restart, true, want)], "the deep State's compiled configuration");

        // An entity input that is connected adds nothing, even with a chosen value left in its node: the value it is given
        // is read as it is. Only an input that takes nothing but entity ids is a reader's choice of entity.
        var wired = Entity(
            [N("w", "entity_is", new JsonObject { ["entity"] = Lit("entity_id", "binary_sensor.stale"), ["state"] = Lit("string", "on") }), N("k", "setting", new { key = "source" }),
             N("cmp", "compare", new JsonObject { ["operator"] = "eq", ["left"] = Lit("entity_id", "binary_sensor.compared"), ["right"] = Lit("entity_id", "binary_sensor.other") }),
             N("obj", "object_create", new JsonObject { ["fields"] = new JsonObject { ["id"] = Lit("entity_id", "binary_sensor.field") } }),
             Two("o", "or"), Two("o2", "or"), State("s", "any")],
            [D("k", "w", "entity"), D("w", "o", "left"), D("cmp", "o", "right"), D("o", "o2", "left"), D("obj", "o2", "right"), D("o2", "s", "is_on")],
            ("source", "string"));
        Same(StateDependencies.Of(wired, "s", BuiltIn), [], "a connected entity input, a comparison of entity ids and an object field add no dependency");

        // The validator's state.dynamicSource case: the reader given its entity over a wire (from a Convert) adds none.
        var dynamic = Case("code state.dynamicSource: connected entity");
        Check(VisualGraphValidator.Validate(dynamic, BuiltIn).Any(d => d.Code == "state.dynamicSource" && d.NodeId == "is"), "the dynamic source case warns on its reader");
        Same(StateDependencies.Of(dynamic, "s", BuiltIn), [], "an entity given over a wire is no dependency");
        var dynamicPreview = Preview(dynamic, "the dynamic source case");
        Same(ActionLines(dynamicPreview), [Line("s", "state", "Restart", true)], "the dynamic source case's action");
        Same(await Compiled(dynamicPreview, "the dynamic source case"), [Described("s", "state", AutomationMode.Restart, true)], "the dynamic source case's compiled configuration");
        // The same reader wired from an entity id Constant: the Constant's chosen entity is the dependency, so the
        // validators do not warn.
        var constantSource = Case("entity graph: an entity from a Constant is followed without a warning");
        Check(VisualGraphValidator.Validate(constantSource, BuiltIn).Count == 0, "a reader wired from an entity id Constant has no warning");
        Same(StateDependencies.Of(constantSource, "s", BuiltIn), ["binary_sensor.door"], "an entity id Constant's entity is a dependency");
        var constantPreview = Preview(constantSource, "the Constant source case");
        Same(ActionLines(constantPreview), [Line("s", "state", "Restart", true, "binary_sensor.door")], "the Constant source case's action");
        Same(await Compiled(constantPreview, "the Constant source case"), [Described("s", "state", AutomationMode.Restart, true, "binary_sensor.door")], "the Constant source case's compiled configuration");
        // A node library's or Custom C# value node's entity pin is a reader's like any other: chosen, it is a dependency;
        // wired (from a node that is not a Constant), it is none, and the validators warn on the node.
        var libraryChosen = Fixture("entity graph: an entity chosen on a library value node is followed without a warning");
        Same(StateDependencies.Of(libraryChosen.Graph, "s", libraryChosen.Snapshot), ["light.hall"], "a library value node's chosen entity is a dependency");
        var libraryWired = Fixture("code state.dynamicSource: connected entity on a library value node");
        Same(StateDependencies.Of(libraryWired.Graph, "s", libraryWired.Snapshot), [], "a library value node's wired entity is no dependency");
        Check(VisualGraphValidator.Validate(libraryWired.Graph, libraryWired.Snapshot).Any(d => d.Code == "state.dynamicSource" && d.NodeId == "bright"), "the validator warns on the library reader");
        var customWired = Case("code state.dynamicSource: connected entity on a Custom C# value node");
        Same(StateDependencies.Of(customWired, "s", BuiltIn), [], "a Custom C# value node's wired entity is no dependency");
        var customChosen = (JsonObject)customWired.DeepClone();
        customChosen["edges"]!.AsArray().RemoveAll(e => e!["target"]!.GetValue<string>() == "lit");
        customChosen["nodes"]!.AsArray().RemoveAll(n => n!["id"]!.GetValue<string>() == "pick");
        customChosen["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "lit")!["config"]!["arguments"]!["lamp"] = Lit("entity_id", "light.porch");
        Check(VisualGraphValidator.Validate(customChosen, BuiltIn).Count == 0, "a Custom C# reader with its entity chosen has no warning: " + string.Join("; ", VisualGraphValidator.Validate(customChosen, BuiltIn).Select(d => d.Message)));
        Same(StateDependencies.Of(customChosen, "s", BuiltIn), ["light.porch"], "a Custom C# value node's chosen entity is a dependency");

        // A State of settings only, or of a value chosen in it, still starts at startup, watching nothing.
        var setting = Case("entity graph: sensor state from a setting");
        Same(StateDependencies.Of(setting, "s", BuiltIn), [], "a State of a setting has no dependencies");
        var settingPreview = Preview(setting, "the setting State");
        Embeds(settingPreview, setting, ["setting", "entity"], "the setting State");
        Same(ActionLines(settingPreview), [Line("s", "state", "Restart", true)], "the setting State's action");
        Same(await Compiled(settingPreview, "the setting State"), [Described("s", "state", AutomationMode.Restart, true)], "the setting State's compiled configuration");
        var constant = Entity([Hub("s", "door", "binary_sensor", values: new JsonObject { ["is_on"] = Lit("boolean", true) })], []);
        Same(await Compiled(Preview(constant, "the constant State"), "the constant State"), [Described("s", "state", AutomationMode.Restart, true)], "the constant State's compiled configuration");

        // One value node read by two States is a dependency of each.
        var shared = Entity([Is("e", "binary_sensor.shared"), State("one", "first"), State("two", "second")], [D("e", "one", "is_on"), D("e", "two", "is_on")]);
        Same(StateDependencies.Of(shared, "one", BuiltIn), ["binary_sensor.shared"], "the first State reads the shared value");
        Same(StateDependencies.Of(shared, "two", BuiltIn), ["binary_sensor.shared"], "the second State reads the shared value");
        var sharedPreview = Preview(shared, "two States of one value");
        Same(await Compiled(sharedPreview, "two States of one value"),
            [Described("one", "state", AutomationMode.Restart, true, "binary_sensor.shared"), Described("two", "state", AutomationMode.Restart, true, "binary_sensor.shared")], "two States of one value");

        // A chosen entity that is empty or not text is none; a graph of the wrong shape, or an unknown node, has none.
        var odd = Entity([N("x", "entity_is", new JsonObject { ["entity"] = Lit("entity_id", ""), ["state"] = Lit("string", "on") }),
                N("y", "entity_is", new JsonObject { ["entity"] = Lit("entity_id", 3), ["state"] = Lit("string", "on") }),
                N("z", "entity_current", new JsonObject { ["entity"] = new JsonObject { ["kind"] = "literal", ["type"] = "entity_id" } }), Two("o", "or"), Two("o2", "or"), State("s", "any")],
            [D("x", "o", "left"), D("y", "o", "right"), D("o", "o2", "left"), D("z", "o2", "right"), D("o2", "s", "is_on")]);
        Same(StateDependencies.Of(odd, "s", BuiltIn), [], "an empty, number or missing entity id is no dependency");
        Same(StateDependencies.Of(odd, "missing", BuiltIn), [], "an unknown node has no dependencies");
        foreach (var shape in new[] { """{"nodes":3,"edges":{}}""", """{"nodes":[null,1,{"id":"s"},{"id":"s","kind":"entity","config":{}}],"edges":[null,{"kind":"data","source":"q","target":"s"}]}""" })
            Same(StateDependencies.Of(JsonNode.Parse(shape)!.AsObject(), "s", BuiltIn), [], "a graph of the wrong shape has no dependencies: " + shape);
    }

    /// <summary><see cref="VisualNodeCatalog.ValueUpstream(JsonObject, string, VisualCatalogSnapshot)"/> on its own: the value nodes a node reads, through value wires only.</summary>
    static void ValueUpstreamWalksValueWiresOnly()
    {
        static string Up(JsonObject graph, string id) => Ids(VisualNodeCatalog.ValueUpstream(graph, id, BuiltIn));
        // A chain, two value nodes deep, through a wire into an input the State has from its entity's domain (is_on).
        var chain = Entity([Is("e", "binary_sensor.door"), Not("n1"), Not("n2"), State("s", "door")], [D("n1", "s", "is_on"), D("n2", "n1", "value"), D("e", "n2", "value")]);
        Check(Up(chain, "s") == "n1, n2, e", $"a chain is walked to its end, each node before those it reads: {Up(chain, "s")}");
        // A diamond: the node both sides read is found once.
        var diamond = Entity([Is("e", "binary_sensor.door"), Not("l"), Not("r"), Two("and", "and"), State("s", "door")],
            [D("and", "s", "is_on"), D("l", "and", "left"), D("r", "and", "right"), D("e", "l", "value"), D("e", "r", "value")]);
        Check(Up(diamond, "s") == "and, l, e, r", $"a diamond's shared node is found once: {Up(diamond, "s")}");
        Same(StateDependencies.Of(diamond, "s", BuiltIn), ["binary_sensor.door"], "a diamond's shared entity is one dependency");
        // One value node read by two States is upstream of each.
        var shared = Entity([Is("e", "binary_sensor.shared"), State("one", "first"), State("two", "second")], [D("e", "one", "is_on"), D("e", "two", "is_on")]);
        Check(Up(shared, "one") == "e" && Up(shared, "two") == "e", $"a value node read by two States is upstream of each: {Up(shared, "one")} | {Up(shared, "two")}");
        // A step's output is read as it is: the walk stops there, and the entity the step reads is no dependency.
        var step = Entity(
            [N("t", "trigger", new { type = "manual" }), N("r", "read_entity", new JsonObject { ["entity"] = Lit("entity_id", "sensor.step") }), Is("e", "binary_sensor.kept"), Two("and", "and"), State("s", "door")],
            [E("t", "r"), D("r", "and", "left"), D("e", "and", "right"), D("and", "s", "is_on")]);
        Check(Up(step, "s") == "and, e", $"the walk stops at a step: {Up(step, "s")}");
        Same(StateDependencies.Of(step, "s", BuiltIn), ["binary_sensor.kept"], "a step's chosen entity is no dependency");
        // A Device wire is no value wire into the State: what is upstream, and what it depends on, is its value's only. A
        // value node wired into the Device pin (which the validators refuse) is not walked either.
        var device = Entity([N("plug", "device", new { key = "plug", name = "Hall plug" }), Is("e", "binary_sensor.door"), State("s", "door")],
            [D("plug", "s", "device", "entities"), D("e", "s", "is_on")]);
        Check(Up(device, "s") == "e", $"a Device wire is not walked: {Up(device, "s")}");
        Same(StateDependencies.Of(device, "s", BuiltIn), ["binary_sensor.door"], "a Device wire adds no dependency");
        var stray = Entity([Is("x", "binary_sensor.stray"), Is("e", "binary_sensor.door"), State("s", "door")], [D("x", "s", "device"), D("e", "s", "is_on")]);
        Check(Up(stray, "s") == "e", $"a value wired into the Device pin is not walked: {Up(stray, "s")}");
        Same(StateDependencies.Of(stray, "s", BuiltIn), ["binary_sensor.door"], "a value wired into the Device pin adds no dependency");
        // A Setting is a value node, upstream like any other, and chooses no entity.
        var setting = Case("entity graph: sensor state from a setting");
        Check(Up(setting, "s") == "k", $"a Setting is upstream: {Up(setting, "s")}");
        // The validator's dynamic source case: the wired reader and the Convert above it.
        var dynamic = Case("code state.dynamicSource: connected entity");
        Check(Up(dynamic, "s") == "not, is, c", $"the dynamic source case's readers: {Up(dynamic, "s")}");
        // Wires the validators do not accept are not walked: without an id, of another kind, an order wire, or from a node not in the graph.
        var rejected = Entity([Is("e", "binary_sensor.door"), State("s", "door")],
            [new JsonObject { ["kind"] = "data", ["source"] = "e", ["target"] = "s", ["sourcePort"] = "value", ["targetPort"] = "is_on" },
             new JsonObject { ["id"] = "x", ["kind"] = "data", ["source"] = "e", ["target"] = "s", ["sourcePort"] = "", ["targetPort"] = "is_on" },
             new JsonObject { ["id"] = "y", ["kind"] = "data", ["source"] = "e", ["target"] = "s", ["sourcePort"] = "value", ["targetPort"] = "is_on", ["port"] = "next" },
             new JsonObject { ["id"] = "z", ["source"] = "e", ["target"] = "s", ["port"] = "next" },
             D("ghost", "s", "is_on")]);
        Check(Up(rejected, "s") == "", $"wires the validators reject are not walked: {Up(rejected, "s")}");
        Same(StateDependencies.Of(rejected, "s", BuiltIn), [], "wires the validators reject bring no dependency");
    }

    static async Task CommandAndSetupRootsBecomeActions()
    {
        // Roots of every kind come in id order, an Entity node's flows in its pins' order: the relay's Turn on and Turn off, then setup.
        var graph = Case("entity graph: command and setup roots");
        var preview = Preview(graph, "the relay");
        Same(ActionLines(preview), [Line("relay", "command:turn_on", "Queued", false), Line("relay", "command:turn_off", "Queued", false), ActionLine("setup", "Single", false, false)], "the relay's actions");
        Mapped(preview, [("relay", "command:turn_on"), ("relay", "command:turn_off"), ("setup", null)], "the relay");
        Check(VisualNodeCatalog.EffectiveTriggerMethod(new JsonObject { ["id"] = "setup" }) == "Run_7365747570", "a root's method is Run_ and the hex of its id's UTF-8");
        Embeds(preview, graph, ["entity", "entity_setup", "set_entity_state", "set_entity_available"], "the relay");
        Same(await Compiled(preview, "the relay"),
            [Described("relay", "command:turn_on", AutomationMode.Queued, false), Described("relay", "command:turn_off", AutomationMode.Queued, false), Describe("setup", AutomationMode.Single, false, false)], "the relay's compiled configuration");
    }

    /// <summary>
    /// An Entity node's flows, each an action named by node and port (<see cref="VisualNodeCatalog.FlowMethod"/>): its state is
    /// worked out at startup and again, restarting, when an entity its values read changes (<see cref="StateDependencies.Of"/>),
    /// a command flow is queued and a camera's frame flow runs one at a time; none is run by hand.
    /// </summary>
    static async Task AnEntityNodesFlowsBecomeActions()
    {
        var graph = Entity(
            [Hub("cam", "door_cam", "camera"), Hub("lamp", "lamp", "light"), Is("e", "binary_sensor.door"),
             N("on", "log", new { value = Lit("string", "on") }), N("frame", "log", new { value = Lit("string", "frame") })],
            [D("e", "lamp", "is_on"), Flow("lamp", "command:turn_on", "on"), Flow("cam", "frame", "frame")]);
        Same(StateDependencies.Of(graph, "lamp", BuiltIn), ["binary_sensor.door"], "the lamp's state depends on the entity its value reads");
        var preview = Preview(graph, "the lamp and the camera");
        // The nodes in id order, each node's flows in its order: state, then each command wired, then the frame.
        Same(ActionLines(preview), [Line("cam", "frame", "Single", false), Line("lamp", "state", "Restart", true, "binary_sensor.door"), Line("lamp", "command:turn_on", "Queued", false)], "the flows' actions");
        Same(await Compiled(preview, "the lamp and the camera"),
            [Described("cam", "frame", AutomationMode.Single, false), Described("lamp", "state", AutomationMode.Restart, true, "binary_sensor.door"), Described("lamp", "command:turn_on", AutomationMode.Queued, false)],
            "the flows' compiled configuration");
    }

    static async Task ATriggerInAnEntityGraphKeepsItsOwnAction()
    {
        // The polled door, with an Entity node of a second entity whose state reads the trigger flow's value node, and an entity trigger.
        var graph = Case("entity graph: trigger root sets state");
        graph["nodes"]!.AsArray().Add(State("s", "door2"));
        graph["nodes"]!.AsArray().Add(N("u", "trigger", new { type = "entity", entity = "binary_sensor.window", mode = "Parallel" }));
        graph["edges"]!.AsArray().Add(D("is", "s", "is_on"));
        var preview = Preview(graph, "the polled door");
        var lines = ActionLines(preview);
        // Each trigger's action is the one it has alone in an automation; the value node both read is the State's alone.
        string Alone(string id)
        {
            var trigger = graph["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == id)!.DeepClone();
            var automation = new JsonObject { ["version"] = 5, ["name"] = "Alone", ["nodes"] = new JsonArray(trigger), ["edges"] = new JsonArray() };
            return ActionLines(Preview(automation, "trigger " + id + " in an automation")).Single();
        }
        Same(lines, [Line("s", "state", "Restart", true, "binary_sensor.door"), Alone("t"), Alone("u")], "the polled door's actions, in id order");
        Check(lines[1].Contains("Triggers:[new global::HassSharp.HomeAssistantTrigger(", StringComparison.Ordinal) && lines[1].Contains("Entities:[]", StringComparison.Ordinal), "the polled trigger keeps its native trigger and reads no entity");
        Check(lines[2].Contains("AutomationMode.Parallel,Startup:false,Manual:true,Entities:[new global::HassSharp.EntityReference(\"binary_sensor.window\")]", StringComparison.Ordinal), "the entity trigger keeps its mode, flags and entity");
        Mapped(preview, [("s", "state"), ("t", null), ("u", null)], "the polled door");
        var compiled = await Compiled(preview, "the polled door");
        Check(compiled.Count == 3 && compiled[0] == Described("s", "state", AutomationMode.Restart, true, "binary_sensor.door"), "the polled door's compiled state action: " + string.Join(" | ", compiled));
        Check(compiled[1] == $"{VisualNodeCatalog.EffectiveTriggerMethod(new JsonObject { ["id"] = "t" })} Single startup=False manual=True entities=[] schedules=0 triggers=1", "the polled trigger's compiled action: " + compiled[1]);
        Check(compiled[2] == $"{VisualNodeCatalog.EffectiveTriggerMethod(new JsonObject { ["id"] = "u" })} Parallel startup=False manual=True entities=[binary_sensor.window] schedules=0 triggers=0", "the entity trigger's compiled action: " + compiled[2]);
    }

    static void AutomationSourceIsUnchanged() => GeneratedSourcePinTests.CheckGraphPin();
}
