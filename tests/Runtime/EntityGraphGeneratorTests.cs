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
            (nameof(AutomationSourceIsUnchanged), () => { AutomationSourceIsUnchanged(); return Task.CompletedTask; }),
            (nameof(AnEntityGraphWithoutCSharpNodesKeepsItsSource), () => { AnEntityGraphWithoutCSharpNodesKeepsItsSource(); return Task.CompletedTask; }),
            (nameof(ADeviceNodeOutOfCatalogOrderKeepsItsSource), () => { ADeviceNodeOutOfCatalogOrderKeepsItsSource(); return Task.CompletedTask; }) })
            try { await test(); }
            catch (Exception e) { failures.Add($"{name}: {e.Message}"); }
        if (failures.Count > 0) throw new Exception($"Entity graph generator: {failures.Count} failing:\n" + string.Join("\n", failures));
        Console.WriteLine("PASS entity graph generator: one action and method per root and per Entity node flow in id order, state dependencies from chosen entities, triggers and automations unchanged");
        foreach (var (name, test) in new (string, Func<Task>)[] {
            (nameof(ACSharpNodeGetsAFactory), ACSharpNodeGetsAFactory),
            (nameof(AStaleSignatureIsToldSo), () => { AStaleSignatureIsToldSo(); return Task.CompletedTask; }),
            (nameof(ASettingValueMustFit), () => { ASettingValueMustFit(); return Task.CompletedTask; }),
            (nameof(AOneSidedRangeSaysItsSide), () => { AOneSidedRangeSaysItsSide(); return Task.CompletedTask; }),
            (nameof(AWholeNumberOfItsTypesOwnRangeSaysNoRange), () => { AWholeNumberOfItsTypesOwnRangeSaysNoRange(); return Task.CompletedTask; }),
            (nameof(ComponentsResolveTheirSettings), () => { ComponentsResolveTheirSettings(); return Task.CompletedTask; }),
            (nameof(ASecretIsNotInTheSource), () => { ASecretIsNotInTheSource(); return Task.CompletedTask; }),
            (nameof(DerivingDropsWhatTheCodeNoLongerHas), () => { DerivingDropsWhatTheCodeNoLongerHas(); return Task.CompletedTask; }),
            (nameof(DerivingKeepsAValueThatNoLongerFits), () => { DerivingKeepsAValueThatNoLongerFits(); return Task.CompletedTask; }),
            (nameof(PreviewRunsNoConstructor), PreviewRunsNoConstructor) })
            try { await test(); }
            catch (Exception e) { failures.Add($"{name}: {e.Message}"); }
        if (failures.Count > 0) throw new Exception($"C# nodes in the entity graph generator: {failures.Count} failing:\n" + string.Join("\n", failures));
        Console.WriteLine("PASS C# nodes generate: a factory of their classes, signatures checked against their code, values against their settings, components resolved, no secret in the source, nothing constructed");
        foreach (var (name, test) in new (string, Func<Task>)[] {
            (nameof(ALibraryClassNodeGetsAFactoryAndNoCode), ALibraryClassNodeGetsAFactoryAndNoCode),
            (nameof(ALibraryClassNodeBesideACSharpNode), ALibraryClassNodeBesideACSharpNode),
            (nameof(ALibraryClassNodesSettingValueMustFit), () => { ALibraryClassNodesSettingValueMustFit(); return Task.CompletedTask; }),
            (nameof(ALibraryClassNodesStrayCodeIsNotCompiled), () => { ALibraryClassNodesStrayCodeIsNotCompiled(); return Task.CompletedTask; }),
            (nameof(ComponentsResolveALibraryClassNodesSettings), () => { ComponentsResolveALibraryClassNodesSettings(); return Task.CompletedTask; }),
            (nameof(DevicesAndEntitiesListALibraryClassNode), () => { DevicesAndEntitiesListALibraryClassNode(); return Task.CompletedTask; }) })
            try { await test(); }
            catch (Exception e) { failures.Add($"{name}: {e.Message}"); }
        if (failures.Count > 0) throw new Exception($"Library class nodes in the entity graph generator: {failures.Count} failing:\n" + string.Join("\n", failures));
        Console.WriteLine("PASS library class nodes generate: a factory case creating the library's class, no code section, values checked against the entry's settings, components resolved, listed as C# nodes are");
    }

    // ---- Library class nodes ----

    /// <summary>The catalog of <see cref="CustomNodeTests.Relays"/>: its hub and relay are kinds of their own.</summary>
    static readonly Lazy<VisualCatalogSnapshot> RelaysCatalog = new(() =>
    {
        var preview = new NodeLibraryCompiler(Diagnostics).Preview([CustomNodeTests.Relays]);
        Check(preview.Valid, "the relays library compiles: " + string.Join(" | ", preview.Diagnostics.Select(d => d.Message)));
        return preview.Snapshot;
    });
    /// <summary>A node of the relays library's hub as the editor drops one, of the key, named Hub, with these values under arguments.</summary>
    static JsonObject RelayHub(string id, string key, JsonObject? arguments = null)
    {
        var node = VisualGraphContract.CreateNode(CustomNodeTests.HubKind, id, RelaysCatalog.Value);
        node.Remove("label");
        node["config"]!["key"] = key; node["config"]!["name"] = "Hub"; node["config"]!["arguments"] = arguments ?? new JsonObject();
        return node;
    }
    /// <summary>A node of the relays library's relay, an entity, of the key, with these values under arguments.</summary>
    static JsonObject Relay(string id, string key, JsonObject? arguments = null)
    {
        var node = VisualGraphContract.CreateNode(CustomNodeTests.RelayKind, id, RelaysCatalog.Value);
        node.Remove("label");
        node["config"]!["key"] = key; node["config"]!["arguments"] = arguments ?? new JsonObject();
        return node;
    }
    static DefinitionPreview PreviewedWithRelays(DefinitionBody body) => new DefinitionGenerator(Diagnostics).Preview(body, RelaysCatalog.Value);

    /// <summary>
    /// A graph of a library's hub alone previews valid (it needs no Entity node: its class makes its entities) and its
    /// factory creates the library's class directly, a case of its node's own; no code section is generated for it. The
    /// source compiles.
    /// </summary>
    static async Task ALibraryClassNodeGetsAFactoryAndNoCode()
    {
        var preview = PreviewedWithRelays(Body([RelayHub("hub", "hub", new JsonObject { ["Address"] = Lit("string", "10.0.0.2") })], []));
        Check(preview.Valid && preview.Diagnostics.Count == 0, "a graph of a hub alone previews valid with no problem: " + Shown(preview.Diagnostics));
        var lines = preview.Source.Split('\n');
        var factory = Array.IndexOf(lines, "public static class SharpNodes {");
        Check(factory >= 0 && lines[factory + 1] == "public static object? Create(string nodeId) => nodeId switch {"
            && lines[factory + 2] == "\"hub\" => new global::NodeLib.RelayHub(),"
            && lines[factory + 3] == "_ => null,",
            "the factory creates the library's class:\n" + string.Join("\n", lines.Skip(Math.Max(0, factory)).Take(6)));
        Check(!preview.Source.Contains("// Relay hub node \"hub\"", StringComparison.Ordinal) && !preview.Source.Contains("namespace NodeLib.Custom_hub", StringComparison.Ordinal),
            "no code section is generated for the hub");
        Check(preview.SourceMap.Count(m => m.NodeId == "hub") == 1, "the hub owns its case of the factory alone: " + string.Join(", ", preview.SourceMap.Select(m => $"{m.NodeId} {m.StartLine}-{m.EndLine}")));
        await CompilesWithAFactory(preview, ["hub"], "a graph of a hub alone");
    }

    /// <summary>
    /// A library's hub beside a C# device whose class derives it: each has its case of the factory, in node order, the
    /// hub's creating the library's class and the C# device's its own; the C# device's code is in the source, the hub has none.
    /// </summary>
    static async Task ALibraryClassNodeBesideACSharpNode()
    {
        const string source = "public class FrontHub : RelayHub { }";
        var compiled = new NodeLibraryCompiler(Diagnostics).Preview([CustomNodeTests.Relays], [new CustomNodeSource("c", source, "csharp_device")]).Snapshot.Custom["c"];
        Check(compiled.Valid, "the C# device's code compiles: " + string.Join(" | ", compiled.Diagnostics.Select(d => d.Message)));
        var address = new JsonObject { ["Address"] = Lit("string", "10.0.0.2") };
        var front = EntityNodeTests.CSharpDevice("front", "front", "Front", new JsonObject { ["source"] = source, ["signature"] = compiled.Signature!.DeepClone(), ["arguments"] = address.DeepClone() });
        var preview = PreviewedWithRelays(Body([RelayHub("hub", "hub", address), front], []));
        Check(preview.Valid && preview.Diagnostics.Count == 0, "the hub and the C# device preview valid with no problem: " + Shown(preview.Diagnostics));
        var lines = preview.Source.Split('\n');
        var factory = Array.IndexOf(lines, "public static class SharpNodes {");
        Check(factory >= 0 && lines[factory + 2] == "\"hub\" => new global::NodeLib.RelayHub(),"
            && lines[factory + 3] == "\"front\" => new global::NodeLib.Custom_front.FrontHub(),"
            && lines[factory + 4] == "_ => null,",
            "both cases are in the factory, in node order:\n" + string.Join("\n", lines.Skip(Math.Max(0, factory)).Take(6)));
        Check(preview.Source.Contains("// C# device node \"front\"\n", StringComparison.Ordinal) && preview.Source.Contains("namespace NodeLib.Custom_front\n", StringComparison.Ordinal)
            && !preview.Source.Contains("node \"hub\"", StringComparison.Ordinal), "the C# device's code section is there, and none for the hub");
        await CompilesWithAFactory(preview, ["hub", "front"], "the hub beside a C# device");
    }

    /// <summary>
    /// A value given to a library class node's setting is checked against the entry's setting as a C# node's is against
    /// its code's: Poll every typed 0 is <c>setting.value</c> on its pin, worded as the same setting of a C# device is.
    /// </summary>
    static void ALibraryClassNodesSettingValueMustFit()
    {
        var preview = PreviewedWithRelays(Body([RelayHub("hub", "hub", new JsonObject { ["Address"] = Lit("string", "10.0.0.2"), ["PollEvery"] = Lit("number", 0) })], []));
        const string poll = "public class Poller : SharpDevice\n{\n    [Setting(\"Poll every\", Minimum = 1, Maximum = 60)] public double PollEvery { get; set; } = 5;\n}";
        var csharp = Previewed(Body([CSharp("csharp_device", "hub", "hub", poll, new JsonObject { ["PollEvery"] = Lit("number", 0) })], []));
        Check(!preview.Valid && preview.Source == "" && Shown(preview.Diagnostics) == "setting.value@hub:PollEvery:3 Poll every takes a number from 1 to 60",
            "Poll every typed 0 does not fit: " + Shown(preview.Diagnostics));
        Check(Shown(csharp.Diagnostics) == Shown(preview.Diagnostics), "a C# device's same setting is told alike: " + Shown(csharp.Diagnostics));
        var fits = PreviewedWithRelays(Body([RelayHub("hub", "hub", new JsonObject { ["Address"] = Lit("string", "10.0.0.2"), ["PollEvery"] = Lit("number", 60) })], []));
        Check(fits.Valid, "a value on the bound fits: " + Shown(fits.Diagnostics));
    }

    /// <summary>
    /// A library class node's config that holds code and a signature (from an older or a hand-made graph) is not compiled
    /// and changes nothing: its settings are its entry's, and it previews valid with no code section.
    /// </summary>
    static void ALibraryClassNodesStrayCodeIsNotCompiled()
    {
        var hub = RelayHub("hub", "hub", new JsonObject { ["Address"] = Lit("string", "10.0.0.2") });
        hub["config"]!["source"] = "this is not C# at all {";
        hub["config"]!["signature"] = JsonNode.Parse("""{"title":"Old","description":"","domain":null,"settings":[{"key":"Old","label":"Old","description":"","type":"string","secret":false,"required":true}]}""");
        var body = Body([hub], []);
        var preview = PreviewedWithRelays(body);
        Check(preview.Valid && preview.Diagnostics.Count == 0, "the hub with stray code previews valid with no problem: " + Shown(preview.Diagnostics));
        Check(!preview.Source.Contains("namespace NodeLib.Custom_hub", StringComparison.Ordinal) && !preview.Source.Contains("// Relay hub node \"hub\"", StringComparison.Ordinal)
            && preview.Source.Contains("\"hub\" => new global::NodeLib.RelayHub(),", StringComparison.Ordinal),
            "the stray code has no section of the source, and the factory creates the library's class");
        var inputs = VisualNodeCatalog.DataInputs(hub, null, RelaysCatalog.Value).Select(p => p.Key).ToList();
        Same(inputs, ["Address", "Token", "PollEvery"], "its pins are the entry's three settings");
        Same(DefinitionGraph.Components(DefinitionGraph.Compose(body), RelaysCatalog.Value, []).Single().Settings.Select(s => s.Key).ToList(), ["Address"], "its component has the entry's settings, not the stray signature's");
    }

    /// <summary>
    /// <see cref="DefinitionGraph.Components"/> gives a library class node as it gives a C# device: its kind, key, name,
    /// its class's title, and its pins resolved (a typed value as typed, a wired secret Setting as the definition's saved
    /// value); a required setting without a value is listed in Unset by its label.
    /// </summary>
    static void ComponentsResolveALibraryClassNodesSettings()
    {
        var hub = RelayHub("hub", "hub", new JsonObject { ["Address"] = Lit("string", "10.0.0.2"), ["PollEvery"] = Lit("number", 10) });
        var body = Body([hub, N("token", "setting", new { key = "token" })], [D("token", "hub", "Token")], Setting("token", "string", "s3cret", secret: true));
        var preview = PreviewedWithRelays(body);
        Check(preview.Valid, "the graph previews valid: " + Shown(preview.Diagnostics));
        Check(!preview.Source.Contains("s3cret", StringComparison.Ordinal), "the source does not hold the secret");
        var components = DefinitionGraph.Components(preview.Graph ?? DefinitionGraph.Compose(body), RelaysCatalog.Value, body.Settings!);
        static string Of(DefinitionComponent c) => $"{c.NodeId} {c.Kind} {c.Key} {c.Name ?? "-"} {(c.Via == null ? "-" : JsonSerializer.Serialize(c.Via, VisualStore.Json))} {c.Device ?? "-"} {c.Title} "
            + "[" + string.Join(", ", c.Settings.Select(s => s.Key + "=" + s.Value?.ToJsonString())) + "]";
        Same(components.Select(Of).ToList(), ["""hub device hub Hub - - Relay hub [Address="10.0.0.2", Token="s3cret", PollEvery=10]"""], "the hub's component, its settings resolved");
        Check(components.Single().Unset.Count == 0, "nothing is unset: " + string.Join(", ", components.Single().Unset));
        Check(components.Single().KindLabel == "Relay hub", "its kind is called by its entry's label, its class's title: " + components.Single().KindLabel);
        var csharp = DefinitionGraph.Components(DefinitionGraph.Compose(Body([CSharp("csharp_device", "plug", "plug", PlugSource)], [])), BuiltIn, []).Single();
        Check(csharp.KindLabel == "C# device", "a C# device's kind is called C# device, as before: " + csharp.KindLabel);
        var unset = DefinitionGraph.Components(DefinitionGraph.Compose(Body([RelayHub("hub", "hub")], [])), RelaysCatalog.Value, []).Single();
        Same([.. unset.Unset], ["Address"], "Address without a value is listed in Unset");
    }

    /// <summary>
    /// A library's hub is listed among the definition's devices as a C# device is (its key and name, <c>csharp</c>); its
    /// relay among the C# entities as a C# entity is (its key, its entry's domain, no name, the key of the Device node it
    /// is on), and not among the Entity nodes' entities. A node of a class that is gone is in neither list, and nothing
    /// throws for it; a declaring kind that is no class node and that no list knows is still refused.
    /// </summary>
    static void DevicesAndEntitiesListALibraryClassNode()
    {
        var catalog = RelaysCatalog.Value.Root;
        var graph = DefinitionGraph.Compose(Body([N("strip", "device", new { key = "strip", name = "Strip" }), RelayHub("hub", "hub"), Relay("relay", "relay"), Lamp(),
            N("gone", "library:relays:global%3A%3ANodeLib.Gone", new { key = "gone", arguments = new { } })], [D("strip", "relay", "device", "entities")]));
        Same(VisualNodeCatalog.DerivedDevices(graph, catalog).Select(d => d.ToJsonString()).ToList(), ["""{"key":"strip","name":"Strip"}""", """{"key":"hub","name":"Hub","csharp":true}"""],
            "the Device node's device, then the hub's, marked csharp");
        Same(VisualNodeCatalog.CSharpEntities(graph, catalog).Select(e => e.ToJsonString()).ToList(), ["""{"key":"relay","domain":"switch","name":null,"device":"strip","csharp":true}"""],
            "the relay is listed as a C# entity is, on the Device node it is wired to");
        Same(VisualNodeCatalog.DeclaredEntities(graph, catalog).Select(e => e.NodeId).ToList(), ["lamp"], "the Entity nodes' entities are the lamp's alone");
        var odd = (JsonObject)catalog.DeepClone();
        odd["nodes"]!["odd"] = new JsonObject { ["label"] = "Odd", ["declares"] = "device", ["config"] = new JsonObject() };
        var refused = new JsonObject { ["version"] = 5, ["name"] = "Odd", ["nodes"] = new JsonArray(N("o", "odd", new { key = "o" })), ["edges"] = new JsonArray() };
        string Thrown(Action list) { try { list(); return "(nothing)"; } catch (InvalidOperationException e) { return e.Message; } }
        Check(Thrown(() => VisualNodeCatalog.DeclaredDevices(refused, odd).ToList()) == "The kind odd declares a device that no list knows how to describe",
            "a declaring kind that is no class node is refused: " + Thrown(() => VisualNodeCatalog.DeclaredDevices(refused, odd).ToList()));
        odd["nodes"]!["odd"]!["declares"] = "entity";
        Check(Thrown(() => VisualNodeCatalog.DeclaredEntities(refused, odd).ToList()) == "The kind odd declares an entity that no list knows how to describe",
            "an entity kind that is no class node is refused: " + Thrown(() => VisualNodeCatalog.DeclaredEntities(refused, odd).ToList()));
    }

    // ---- C# entity and C# device nodes ----

    /// <summary>The signature the compiler gives the code of a node of the kind, alone with the built-in catalog.</summary>
    /// <param name="provider">What the code compiles with; <see cref="NodeLibraryFixture.EntityProvider"/> for entity classes such as <c>LightEntity</c>.</param>
    static JsonObject Compiled(string kind, string source, DiagnosticsProvider? provider = null)
    {
        var descriptor = new NodeLibraryCompiler(provider ?? Diagnostics).Preview([], [new CustomNodeSource("c", source, kind)]).Snapshot.Custom["c"];
        Check(descriptor.Valid, $"the {kind} code compiles: " + string.Join(" | ", descriptor.Diagnostics.Select(d => d.Message)));
        return descriptor.Signature!;
    }
    /// <summary>A C# entity or C# device node of the code, with the signature its code gives and these values (typed literals) under arguments.</summary>
    static JsonObject CSharp(string kind, string id, string key, string source, JsonObject? arguments = null, string? name = null, DiagnosticsProvider? provider = null)
    {
        var more = new JsonObject { ["source"] = source, ["signature"] = Compiled(kind, source, provider), ["arguments"] = arguments ?? new JsonObject() };
        return kind == "csharp_entity" ? EntityNodeTests.CSharpEntity(id, key, more) : EntityNodeTests.CSharpDevice(id, key, name ?? key, more);
    }
    /// <summary>A graph definition named Hall of the nodes and wires (no definition in the graph: the generator composes it), with these settings.</summary>
    static DefinitionBody Body(JsonObject[] nodes, JsonObject[] edges, params DefinitionSetting[] settings) =>
        new("Hall", null, [.. settings], null, new DefinitionLogic("graph", new JsonObject { ["version"] = 5, ["name"] = "Hall", ["nodes"] = new JsonArray(nodes), ["edges"] = new JsonArray(edges) }));
    static DefinitionSetting Setting(string key, string type, JsonNode? value, bool secret = false) => new(key, value) { Label = key, Type = type, Secret = secret };
    static DefinitionPreview Previewed(DefinitionBody body) => new DefinitionGenerator(Diagnostics).Preview(body, BuiltIn);
    static string Shown(IEnumerable<VisualDiagnostic> diagnostics) => string.Join(" | ", diagnostics.Select(d => $"{d.Code}@{d.NodeId}:{d.InputId}:{d.Severity} {d.Message}"));
    static JsonObject Lamp() => Hub("lamp", "lamp", "binary_sensor", values: new JsonObject { ["is_on"] = Lit("boolean", true) });
    static string ThermometerSource => VisualNodeCatalog.Root["nodes"]!["csharp_entity"]!["config"]!["source"]!["default"]!.GetValue<string>();
    static string PlugSource => VisualNodeCatalog.Root["nodes"]!["csharp_device"]!["config"]!["source"]!["default"]!.GetValue<string>();

    /// <summary>
    /// A graph with a C# device and a C# entity generates <c>SharpNodes.Create</c>, one case per node by its id (as a C#
    /// string literal, whatever the id holds) creating its class, after the code of each in its own namespace; the source
    /// compiles. The same graph without them has no <c>SharpNodes</c>.
    /// </summary>
    static async Task ACSharpNodeGetsAFactory()
    {
        var plug = CSharp("csharp_device", "plug", "plug", PlugSource, new JsonObject { ["Address"] = Lit("string", "http://192.168.1.50") });
        var temp = CSharp("csharp_entity", "temp", "temp", ThermometerSource, new JsonObject { ["Address"] = Lit("string", "http://192.168.1.60/temperature") });
        var preview = Previewed(Body([Lamp(), plug, temp], []));
        Check(preview.Valid && preview.Diagnostics.Count == 0, "a graph with a C# device and a C# entity previews valid with no problem: " + Shown(preview.Diagnostics));
        var lines = preview.Source.Split('\n');
        var factory = Array.IndexOf(lines, "public static class SharpNodes {");
        Check(factory >= 0 && lines[factory + 1] == "public static object? Create(string nodeId) => nodeId switch {"
            && lines[factory + 2] == "\"plug\" => new global::NodeLib.Custom_plug.Plug(),"
            && lines[factory + 3] == "\"temp\" => new global::NodeLib.Custom_temp.Thermometer(),"
            && lines[factory + 4] == "_ => null,"
            && lines[factory + 5] == "};" && lines[factory + 6] == "}",
            "the source has SharpNodes.Create with a case for each node, in node order:\n" + string.Join("\n", lines.Skip(Math.Max(0, factory)).Take(8)));
        Check(preview.Source.Contains("namespace NodeLib.Custom_plug\n", StringComparison.Ordinal) && preview.Source.Contains("namespace NodeLib.Custom_temp\n", StringComparison.Ordinal),
            "each node's code is in the source, in its own namespace");
        Check(preview.SourceMap.Count(m => m.NodeId == "plug") == 2 && preview.SourceMap.Count(m => m.NodeId == "temp") == 2,
            "each node owns its case of the factory and its code in the source map: " + string.Join(", ", preview.SourceMap.Select(m => $"{m.NodeId} {m.StartLine}-{m.EndLine}")));
        await CompilesWithAFactory(preview, ["plug", "temp"], "the graph with C# nodes");

        var plain = Previewed(Body([Lamp()], []));
        Check(plain.Valid && !plain.Source.Contains("SharpNodes", StringComparison.Ordinal), "the same graph without them has no SharpNodes");
        var only = Previewed(Body([plug.DeepClone().AsObject()], []));
        Check(only.Valid && only.Source.Contains("\"plug\" => new global::NodeLib.Custom_plug.Plug(),", StringComparison.Ordinal), "a graph of a C# device alone generates its factory: " + Shown(only.Diagnostics));
        await CompilesWithAFactory(only, ["plug"], "a graph of a C# device alone");

        // An id is a C# string literal in the factory: a quote, a backslash or a line break in it cannot end the literal.
        const string odd = "a\"b\\c\nd";
        var quoted = CSharp("csharp_entity", odd, "odd", ThermometerSource);
        var oddPreview = Previewed(Body([quoted], []));
        Check(oddPreview.Valid, "an id with a quote, a backslash and a line break previews valid: " + Shown(oddPreview.Diagnostics));
        Check(oddPreview.Source.Contains("\"a\\u0022b\\\\c\\nd\" => new global::NodeLib.Custom_a_0022b_005Cc_000Ad.Thermometer(),", StringComparison.Ordinal),
            "the id is escaped in its case: " + string.Join("\n", oddPreview.Source.Split('\n').Where(l => l.Contains("=> new", StringComparison.Ordinal))));
        await CompilesWithAFactory(oddPreview, [odd], "the odd id");
    }

    /// <summary>
    /// The source compiles (through <see cref="CodeCompiler"/>, as a definition's does) to an assembly with
    /// <c>SharpNodes.Create(string)</c>; nothing is created: neither the factory nor any class of a node is called.
    /// </summary>
    static async Task CompilesWithAFactory(DefinitionPreview preview, string[] ids, string what)
    {
        var compiled = await new CodeCompiler(Diagnostics).CompileSingleFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cs"), preview.Source);
        try
        {
            var factory = compiled.Assembly.GetType("SharpNodes");
            var create = factory?.GetMethod("Create", [typeof(string)]);
            Check(compiled.Assembly.GetType("VisualAutomation") != null && create is { IsStatic: true } && create.ReturnType == typeof(object), $"{what}: the source compiles, with SharpNodes.Create(string)");
            foreach (var id in ids) Check(preview.Source.Contains(JsonSerializer.Serialize(id) + " => new ", StringComparison.Ordinal), $"{what}: the factory has {id}");
        }
        finally { compiled.Unload(); }
    }

    const string FanSource = """
        public enum Speed { Slow, Medium }
        /// <summary>A fan.</summary>
        public class Fan : SharpDevice
        {
            [Setting] public int Count { get; set; }
            [Setting(Minimum = 1, Maximum = 100)] public int Percent { get; set; } = 1;
            [Setting] public Speed Mode { get; set; }
            [Setting("Poll interval")] public int Interval { get; set; } = 10;
        }
        """;

    /// <summary>
    /// A stored signature that differs from the code's in what changes wires or values is <c>csharp.signature</c>, on the
    /// node's signature, alone for that node (no value is checked against pins the code does not have); one that differs
    /// in words only is refreshed and answered in the preview's graph, with no problem.
    /// </summary>
    static void AStaleSignatureIsToldSo()
    {
        const string stale = "The settings are out of date with the code: open Edit code and apply it";
        const string address = "public class Plug : SharpDevice\n{\n    [Setting] public string Address { get; set; } = \"\";\n}";
        var plug = CSharp("csharp_device", "plug", "plug", address);
        var compiled = plug["config"]!["signature"]!.AsObject();
        // Host for Address, an integer holding 1.5: out of date, and only that is said.
        var host = (JsonObject)compiled.DeepClone();
        host["settings"]![0]!["key"] = "Host"; host["settings"]![0]!["label"] = "Host"; host["settings"]![0]!["type"] = "integer";
        var renamed = (JsonObject)plug.DeepClone();
        renamed["config"]!["signature"] = host.DeepClone(); renamed["config"]!["arguments"] = new JsonObject { ["Host"] = Lit("number", 1.5) };
        var preview = Previewed(Body([renamed], []));
        Check(!preview.Valid && preview.Source == "" && Shown(preview.Diagnostics) == $"csharp.signature@plug:signature:3 {stale}",
            "a signature with Host for the code's Address is out of date, and nothing else is said: " + Shown(preview.Diagnostics));
        // Each member that changes wires or values, alone.
        foreach (var (what, change) in new (string, Action<JsonObject>)[] {
            ("domain", s => s["domain"] = "switch"),
            ("type", s => s["settings"]![0]!["type"] = "enum"),
            ("secret", s => s["settings"]![0]!["secret"] = true),
            ("options", s => s["settings"]![0]!["options"] = new JsonArray("a")),
            ("minimum", s => s["settings"]![0]!["minimum"] = 1),
            ("maximum", s => s["settings"]![0]!["maximum"] = 9),
            ("another setting", s => s["settings"]!.AsArray().Add(new JsonObject { ["key"] = "Port", ["label"] = "Port", ["description"] = "", ["type"] = "integer", ["secret"] = false })),
            ("no setting", s => s["settings"]!.AsArray().Clear()) })
        {
            var signature = (JsonObject)compiled.DeepClone(); change(signature);
            var node = (JsonObject)plug.DeepClone(); node["config"]!["signature"] = signature;
            var changed = Previewed(Body([node], []));
            Check(!changed.Valid && changed.Diagnostics.Any(d => d is { Code: "csharp.signature", NodeId: "plug", InputId: "signature", Severity: 3 } && d.Message == stale),
                $"a stored {what} that differs is out of date: " + Shown(changed.Diagnostics));
        }
        // Two settings in another order are out of date: the pins are in the class's order.
        const string two = "public class Pair : SharpDevice\n{\n    [Setting] public string First { get; set; } = \"\";\n    [Setting] public string Second { get; set; } = \"\";\n}";
        var pair = CSharp("csharp_device", "pair", "pair", two);
        var swapped = (JsonObject)pair.DeepClone(); var settings = swapped["config"]!["signature"]!["settings"]!.AsArray();
        var first = settings[0]!; settings.RemoveAt(0); settings.Add(first);
        Check(Previewed(Body([swapped], [])).Diagnostics.Any(d => d.Code == "csharp.signature" && d.NodeId == "pair"), "two settings in another order are out of date");
        // Words alone: refreshed in the preview's graph, valid, and nothing said.
        var worded = (JsonObject)plug.DeepClone();
        worded["config"]!["signature"]!["title"] = "Old title"; worded["config"]!["signature"]!["description"] = "Old words";
        worded["config"]!["signature"]!["settings"]![0]!["label"] = "Old label"; worded["config"]!["signature"]!["settings"]![0]!["description"] = "Old help";
        var refreshed = Previewed(Body([worded], []));
        Check(refreshed.Valid && refreshed.Diagnostics.Count == 0, "a signature differing in words only previews valid with no problem: " + Shown(refreshed.Diagnostics));
        var answered = refreshed.Graph!["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "plug")!["config"]!["signature"];
        Check(JsonNode.DeepEquals(answered, compiled), "the preview's graph has the code's words: " + answered?.ToJsonString());
        // A setting's default is for the editor to show: a stored signature without it (saved before signatures had
        // defaults), or with another, is refreshed with the code's and is no problem, and the source is the same either
        // way, since an unset setting keeps the value its class gives it.
        var port = CSharp("csharp_device", "port", "port", "public class Port : SharpDevice\n{\n    [Setting] public int Number { get; set; } = 80;\n}");
        var withDefault = port["config"]!["signature"]!.AsObject();
        Check(withDefault["settings"]![0]!["default"]?.GetValue<int>() == 80, "the code's signature has the default: " + withDefault.ToJsonString());
        var current = Previewed(Body([port], []));
        foreach (var (what, change) in new (string, Action<JsonObject>)[] { ("no default", s => s.Remove("default")), ("another default", s => s["default"] = 8080) })
        {
            var older = (JsonObject)port.DeepClone(); change(older["config"]!["signature"]!["settings"]![0]!.AsObject());
            var opened = Previewed(Body([older], []));
            var shown = opened.Graph?["nodes"]?.AsArray().Single(n => n!["id"]!.GetValue<string>() == "port")!["config"]!["signature"];
            Check(opened.Valid && opened.Diagnostics.Count == 0 && JsonNode.DeepEquals(shown, withDefault), $"a stored signature with {what} previews valid, refreshed with the code's: " + Shown(opened.Diagnostics) + " " + shown?.ToJsonString());
            Check(current.Valid && opened.Source == current.Source, $"a stored signature with {what} gives the same source");
        }
        // A node whose code does not compile is told that, and no value of it is checked.
        var broken = (JsonObject)plug.DeepClone();
        broken["config"]!["source"] = address.Replace("string Address", "Missing Address", StringComparison.Ordinal);
        broken["config"]!["signature"] = host.DeepClone(); broken["config"]!["arguments"] = new JsonObject { ["Host"] = Lit("number", 1.5) };
        var failed = Previewed(Body([broken], []));
        Check(!failed.Valid && failed.Diagnostics.Any(d => d.NodeId == "plug" && d.InputId == "source" && d.Severity == 3) && !failed.Diagnostics.Any(d => d.Code is "setting.value" or "csharp.signature"),
            "code that does not compile is told so, with no value or signature problem: " + Shown(failed.Diagnostics));
    }

    /// <summary>
    /// Each pin's value, typed or from the Setting wired to it, is checked against the code's setting: a fraction in an
    /// integer, a number outside the range, a text that is no member of the enum, a wired Setting's value alike; each is
    /// <c>setting.value</c> on its pin, <c>{Label} takes {Phrase}</c> (the setting's label, not its key), and the value is
    /// never quoted.
    /// </summary>
    static void ASettingValueMustFit()
    {
        var fan = CSharp("csharp_device", "fan", "fan", FanSource, new JsonObject
        {
            ["Count"] = Lit("number", 1.5), ["Percent"] = Lit("number", 200), ["Mode"] = Lit("string", "Fast"),
        });
        var preview = Previewed(Body([fan, N("every", "setting", new { key = "interval" })], [D("every", "fan", "Interval")], Setting("interval", "number", 1.5)));
        var values = preview.Diagnostics.Where(d => d.Code == "setting.value").ToArray();
        Check(!preview.Valid && preview.Source == "" && values.Length == 4 && values.All(d => d is { NodeId: "fan", Severity: 3 }), "four values do not fit: " + Shown(preview.Diagnostics));
        string Said(string pin) => values.SingleOrDefault(d => d.InputId == pin)?.Message ?? "(none)";
        Check(Said("Count") == "Count takes a whole number", "a fraction in an integer: " + Said("Count"));
        Check(Said("Percent") == "Percent takes a whole number from 1 to 100", "a number outside the range: " + Said("Percent"));
        Check(Said("Mode") == "Mode takes one of Slow, Medium", "a text that is no member of the enum: " + Said("Mode"));
        // A setting whose label is not its key is named by its label.
        Check(Said("Interval") == "Poll interval takes a whole number", "a wired Setting's fraction in an integer, on the pin, by its label: " + Said("Interval"));
        Check(!preview.Diagnostics.Any(d => d.Message.Contains("1.5", StringComparison.Ordinal) || d.Message.Contains("200", StringComparison.Ordinal) || d.Message.Contains("Fast", StringComparison.Ordinal)),
            "no message quotes a value: " + Shown(preview.Diagnostics));
        // Values that fit are no problem, typed or wired; an unset pin and a Setting without a value neither.
        var fitting = CSharp("csharp_device", "fan", "fan", FanSource, new JsonObject { ["Count"] = Lit("number", 3), ["Percent"] = Lit("number", 100), ["Mode"] = Lit("string", "Medium") });
        var fine = Previewed(Body([fitting, N("every", "setting", new { key = "interval" })], [D("every", "fan", "Interval")], Setting("interval", "number", 30)));
        Check(fine.Valid && !fine.Diagnostics.Any(d => d.Code == "setting.value"), "values that fit are no problem: " + Shown(fine.Diagnostics));
        var empty = Previewed(Body([CSharp("csharp_device", "fan", "fan", FanSource), N("every", "setting", new { key = "interval" })], [D("every", "fan", "Interval")], Setting("interval", "number", null)));
        Check(empty.Valid, "unset pins and a Setting without a value are no problem: " + Shown(empty.Diagnostics));
        // A value of the wrong type is the validator's type problem, said once: no setting.value beside it.
        var wrong = Previewed(Body([CSharp("csharp_device", "fan", "fan", FanSource, new JsonObject { ["Count"] = Lit("string", "three") })], []));
        Check(!wrong.Valid && wrong.Diagnostics.Count(d => d.InputId == "Count" && d.Severity == 3) == 1 && !wrong.Diagnostics.Any(d => d.Code == "setting.value"),
            "a text in an integer is one problem, not a value problem too: " + Shown(wrong.Diagnostics));

        // An entity setting takes an entity id, one of its domain when it has one. A value no rule of the validator sees on
        // the pin (from a Setting naming none of the graph's settings: that Setting node is told so, not the pin) is checked
        // against the setting all the same.
        var entities = NodeLibraryFixture.EntityProvider;
        const string meter = "public class Meter : SharpDevice\n{\n    [Setting] public SensorEntity Source { get; set; }\n    [Setting] public Entity? Any { get; set; }\n}";
        JsonObject Meter(JsonObject? arguments = null) => new()
        {
            ["version"] = 5, ["name"] = "Hall", ["definition"] = new JsonObject { ["settings"] = new JsonArray() },
            ["nodes"] = new JsonArray(CSharp("csharp_device", "meter", "meter", meter, arguments, provider: entities), N("source", "setting", new { key = "source" }), N("any", "setting", new { key = "any" })),
            ["edges"] = new JsonArray(D("source", "meter", "Source"), D("any", "meter", "Any")),
        };
        string Given(JsonNode? source, JsonNode? any) =>
            Shown(new VisualGenerator(entities).Preview(Meter(), BuiltIn, [new("source", source), new("any", any)]).Diagnostics.Where(d => d.Code == "setting.value"));
        const string both = "setting.value@meter:Source:3 Source takes an entity id that starts with sensor. | setting.value@meter:Any:3 Any takes an entity id";
        Check(Given(5, 5) == both, "a number in an entity setting, of a domain and of none: " + Given(5, 5));
        Check(Given("light.kitchen", "not an id") == both, "an entity of another domain, and a text that is no entity id: " + Given("light.kitchen", "not an id"));
        Check(Given("sensor.outside", "light.kitchen") == "", "an entity of its domain, and any entity where any is taken, fit: " + Given("sensor.outside", "light.kitchen"));
        // A pin that already has the validator's wrong-domain error is not checked again.
        var typed = Meter(new JsonObject { ["Source"] = Lit("entity_id:sensor", "light.kitchen") });
        typed["edges"] = new JsonArray(D("any", "meter", "Any"));
        var told = new VisualGenerator(entities).Preview(typed, BuiltIn, [new("any", "light.kitchen")]);
        Check(Shown(told.Diagnostics.Where(d => d.InputId == "Source")) == "@meter:Source:3 Source is not a valid Sensor entity value",
            "an entity of another domain typed in is the validator's problem alone, with no setting.value beside it: " + Shown(told.Diagnostics));
    }

    const string SidedSource = """
        public class Sided : SharpDevice
        {
            [Setting(Minimum = 1)] public int Retries { get; set; } = 3;
            [Setting(Maximum = 60)] public int Delay { get; set; }
            [Setting(Minimum = 0.5)] public double Gain { get; set; } = 1;
        }
        """;

    /// <summary>
    /// A range declared on one side only says that side, not the type's own bound on the other: <c>of at least 1</c>,
    /// <c>of at most 60</c> and <c>of at least 0.5</c>, typed on the pin or from the Setting wired to it, also for a whole
    /// number past an int's own bound.
    /// </summary>
    static void AOneSidedRangeSaysItsSide()
    {
        var sided = CSharp("csharp_device", "sided", "sided", SidedSource, new JsonObject { ["Retries"] = Lit("number", 0), ["Delay"] = Lit("number", 61) });
        var preview = Previewed(Body([sided, N("gain", "setting", new { key = "gain" })], [D("gain", "sided", "Gain")], Setting("gain", "number", 0.25)));
        var values = preview.Diagnostics.Where(d => d.Code == "setting.value").ToArray();
        Check(!preview.Valid && values.Length == 3 && values.All(d => d is { NodeId: "sided", Severity: 3 }), "three values do not fit: " + Shown(preview.Diagnostics));
        string Said(string pin) => values.SingleOrDefault(d => d.InputId == pin)?.Message ?? "(none)";
        Check(Said("Retries") == "Retries takes a whole number of at least 1", "a minimum alone: " + Said("Retries"));
        Check(Said("Delay") == "Delay takes a whole number of at most 60", "a maximum alone: " + Said("Delay"));
        Check(Said("Gain") == "Gain takes a number of at least 0.5", "a number's minimum alone, from the wired Setting: " + Said("Gain"));
        var past = Previewed(Body([CSharp("csharp_device", "sided", "sided", SidedSource, new JsonObject { ["Retries"] = Lit("number", 2147483648) })], []));
        Check(Shown(past.Diagnostics.Where(d => d.Code == "setting.value")) == "setting.value@sided:Retries:3 Retries takes a whole number of at least 1",
            "a whole number past an int's bound says the minimum alone: " + Shown(past.Diagnostics));
        var fits = Previewed(Body([CSharp("csharp_device", "sided", "sided", SidedSource, new JsonObject { ["Retries"] = Lit("number", 1), ["Delay"] = Lit("number", 60), ["Gain"] = Lit("number", 0.5) })], []));
        Check(fits.Valid, "values on the bound fit: " + Shown(fits.Diagnostics));
    }

    const string WholeSource = """
        public class Counter : SharpDevice
        {
            [Setting] public int Count { get; set; }
            [Setting] public long Total { get; set; }
            [Setting(Minimum = -5, Maximum = 5)] public long Declared { get; set; }
        }
        """;

    /// <summary>
    /// A whole number whose range is its type's own says no range: an int's is <c>a whole number</c>, as a long's (which
    /// has none) is; one past a long's bound, and a fraction, alike. A range the setting declares is said. A long's
    /// largest value fits.
    /// </summary>
    static void AWholeNumberOfItsTypesOwnRangeSaysNoRange()
    {
        static JsonNode Whole(string digits) => JsonNode.Parse("{\"kind\":\"literal\",\"type\":\"number\",\"value\":" + digits + "}")!;
        string Said(string pin, JsonNode value)
        {
            var preview = Previewed(Body([CSharp("csharp_device", "counter", "counter", WholeSource, new JsonObject { [pin] = value })], []));
            return preview.Diagnostics.SingleOrDefault(d => d.Code == "setting.value" && d.InputId == pin)?.Message ?? "(none)";
        }
        Check(Said("Count", Lit("number", 1.5)) == "Count takes a whole number", "an int's own range: " + Said("Count", Lit("number", 1.5)));
        Check(Said("Count", Lit("number", 2147483648)) == "Count takes a whole number", "an int's own range, a value past it: " + Said("Count", Lit("number", 2147483648)));
        Check(Said("Total", Lit("number", 1.5)) == "Total takes a whole number", "a long's fraction: " + Said("Total", Lit("number", 1.5)));
        Check(Said("Total", Whole("9223372036854775808")) == "Total takes a whole number", "a value past a long's bound: " + Said("Total", Whole("9223372036854775808")));
        Check(Said("Total", Whole("9223372036854775807")) == "(none)", "a long's largest value fits: " + Said("Total", Whole("9223372036854775807")));
        Check(Said("Declared", Lit("number", 6)) == "Declared takes a whole number from -5 to 5", "a declared range is said: " + Said("Declared", Lit("number", 6)));
    }

    /// <summary>
    /// <see cref="DefinitionGraph.Components"/>: each C# node in node order, with its kind, key, name, Part of, the key
    /// of the Device node a C# entity is wired to, its title, and its pins resolved: a typed value as typed, a wired
    /// Setting as the definition's saved value for its key, an unset pin left out. A Device node and an Entity node are none.
    /// </summary>
    static void ComponentsResolveTheirSettings()
    {
        const string source = "public class Plug : SharpDevice\n{\n    [Setting] public string Address { get; set; } = \"\";\n    [Setting] public int Interval { get; set; } = 10;\n    [Setting] public string Room { get; set; } = \"\";\n}";
        var plug = CSharp("csharp_device", "plug", "plug", source, new JsonObject { ["Interval"] = Lit("number", 30), ["Room"] = new JsonObject { ["kind"] = "unset" } }, name: "Hall plug");
        plug["config"]!["via"] = new JsonObject { ["device"] = "strip" };
        var temp = CSharp("csharp_entity", "temp", "thermometer", ThermometerSource, new JsonObject { ["Address"] = Lit("string", "http://192.168.1.60/temperature") });
        var body = Body([N("strip", "device", new { key = "strip", name = "Strip" }), plug, Lamp(), N("address", "setting", new { key = "address" }), temp],
            [D("address", "plug", "Address"), D("strip", "temp", "device", "entities")], Setting("address", "string", "http://192.168.1.50"));
        var preview = Previewed(body);
        Check(preview.Valid, "the graph previews valid: " + Shown(preview.Diagnostics));
        var components = DefinitionGraph.Components(preview.Graph!, BuiltIn, body.Settings!);
        static string Of(DefinitionComponent c) => $"{c.NodeId} {c.Kind} {c.Key} {c.Name ?? "-"} {(c.Via == null ? "-" : JsonSerializer.Serialize(c.Via, VisualStore.Json))} {c.Device ?? "-"} {c.Title} "
            + "[" + string.Join(", ", c.Settings.Select(s => s.Key + "=" + s.Value?.ToJsonString())) + "]";
        Same(components.Select(Of).ToList(), [
            """plug device plug Hall plug {"deviceId":null,"device":"strip"} - Plug [Address="http://192.168.1.50", Interval=30]""",
            """temp entity thermometer - - strip Thermometer [Address="http://192.168.1.60/temperature"]""",
        ], "the components in node order, their settings resolved");
        Check(DefinitionGraph.Components(Body([Lamp()], []).Logic.Graph!, BuiltIn, []).Count == 0, "a graph without C# nodes has no components");
        // A C# entity on no device, and a Setting with no value saved, which leaves its pin out.
        var alone = DefinitionGraph.Components(DefinitionGraph.Compose(Body([N("address", "setting", new { key = "address" }), CSharp("csharp_entity", "temp", "thermometer", ThermometerSource)], [D("address", "temp", "Address")], Setting("address", "string", null))), BuiltIn, [Setting("address", "string", null)]);
        Same(alone.Select(Of).ToList(), ["temp entity thermometer - - - Thermometer []"], "a C# entity alone, its Setting without a value left out");
        Check(components.All(c => c.Unset.Count == 0) && alone.Single().Unset.Count == 0, "a component without a required setting has nothing unset");

        // A typed entity arrives as its id. A required setting that has no value, its pin unset and unwired, or wired to
        // a Setting whose definition setting has none, is listed in Unset by its label (its key when it has none), in its
        // class's order; an optional one that has none is not.
        const string meter = "public class Meter : SharpDevice\n{\n    [Setting(\"Main light\")] public LightEntity Light { get; set; }\n    [Setting] public SensorEntity Source { get; set; }\n"
            + "    [Setting(\"Wired host\")] public string Host { get; set; }\n    [Setting] public Entity? Spare { get; set; }\n    [Setting] public string? Note { get; set; }\n}";
        var node = CSharp("csharp_device", "meter", "meter", meter, new JsonObject { ["Light"] = Lit("entity_id:light", "light.kitchen") }, provider: NodeLibraryFixture.EntityProvider);
        var signature = node["config"]!["signature"]!["settings"]!.AsArray();
        Check(signature.Count(s => s!["required"]?.GetValue<bool>() == true) == 3, "the signature marks Light, Source and Host required: " + signature.ToJsonString());
        var meters = DefinitionGraph.Components(DefinitionGraph.Compose(Body([node, N("host", "setting", new { key = "host" })], [D("host", "meter", "Host")], Setting("host", "string", null))),
            BuiltIn, [Setting("host", "string", null)]);
        var made = meters.Single();
        Same(made.Settings.Select(s => s.Key + "=" + s.Value?.ToJsonString()).ToList(), ["Light=\"light.kitchen\""], "a typed entity arrives as its id text");
        Same([.. made.Unset], ["Source", "Wired host"], "the required settings without a value, by label, in class order; not the optional ones");
    }

    /// <summary>
    /// A secret setting is given by a wired Setting: its saved value is in none of what a preview gives (its source, its
    /// graph, its embedded catalog, its problems), and is resolved for the run by <see cref="DefinitionGraph.Components"/>.
    /// A secret's value that does not fit is told without being quoted.
    /// </summary>
    static void ASecretIsNotInTheSource()
    {
        const string secret = "hunter2-graph-secret";
        const string source = "public class Router : SharpDevice\n{\n    [Setting(Secret = true)] public string Password { get; set; } = \"\";\n    [Setting(Secret = true)] public int Pin { get; set; }\n}";
        var router = CSharp("csharp_device", "router", "router", source);
        var body = Body([router, N("password", "setting", new { key = "password" })], [D("password", "router", "Password")], Setting("password", "string", secret, secret: true));
        var preview = Previewed(body);
        Check(preview.Valid, "a wired secret previews valid: " + Shown(preview.Diagnostics));
        Check(!preview.Source.Contains(secret, StringComparison.Ordinal), "the source does not hold the secret");
        Check(!(preview.Graph?.ToJsonString() ?? "").Contains(secret, StringComparison.Ordinal) && !(preview.EmbeddedCatalog?.ToJsonString() ?? "").Contains(secret, StringComparison.Ordinal),
            "nor does the preview's graph or embedded catalog");
        Check(!JsonSerializer.Serialize(preview.Diagnostics).Contains(secret, StringComparison.Ordinal), "nor do its problems");
        Check(DefinitionGraph.Components(preview.Graph!, BuiltIn, body.Settings!).Single().Settings.Single() is { Key: "Password" } resolved && resolved.Value!.GetValue<string>() == secret,
            "Components resolves it for the run");
        var pin = Previewed(Body([(JsonObject)router.DeepClone(), N("pin", "setting", new { key = "pin" })], [D("pin", "router", "Pin")], Setting("pin", "number", 1234.5, secret: true)));
        Check(!pin.Valid && pin.Diagnostics.Any(d => d is { Code: "setting.value", NodeId: "router", InputId: "Pin" } && d.Message == "Pin takes a whole number")
            && !JsonSerializer.Serialize(pin.Diagnostics).Contains("1234", StringComparison.Ordinal), "a secret's value that does not fit is told, not quoted: " + Shown(pin.Diagnostics));
    }

    /// <summary>
    /// Deriving (MCP and authoring) writes each C# node's signature from its code and removes the value and the wire of
    /// each setting the code no longer has, one warning each, naming the setting as the node showed it; a C# entity's
    /// Device wire stays. The result validates.
    /// </summary>
    static void DerivingDropsWhatTheCodeNoLongerHas()
    {
        const string source = "public class Plug : SharpDevice\n{\n    [Setting] public string Address { get; set; } = \"\";\n}";
        var plug = CSharp("csharp_device", "plug", "plug", source, new JsonObject { ["Address"] = Lit("string", "http://192.168.1.50"), ["Host"] = Lit("string", "plug.local") });
        var compiled = plug["config"]!["signature"]!.DeepClone();
        var settings = plug["config"]!["signature"]!["settings"]!.AsArray();
        settings.Add(new JsonObject { ["key"] = "Host", ["label"] = "Host name", ["description"] = "", ["type"] = "string", ["secret"] = false });
        settings.Add(new JsonObject { ["key"] = "Port", ["label"] = "Port", ["description"] = "", ["type"] = "integer", ["secret"] = false, ["minimum"] = 1, ["maximum"] = 65535 });
        var temp = CSharp("csharp_entity", "temp", "thermometer", ThermometerSource);
        temp["config"]!["signature"]!["title"] = "An old title";
        var graph = DefinitionGraph.Compose(Body([N("port", "setting", new { key = "port" }), plug, N("strip", "device", new { key = "strip", name = "Strip" }), temp],
            [D("port", "plug", "Port"), D("strip", "temp", "device", "entities")], Setting("port", "number", 80)));
        Check(Previewed(new("Hall", null, [Setting("port", "number", 80)], null, new("graph", DefinitionGraph.Decompose(graph)))).Diagnostics.Any(d => d.Code == "csharp.signature" && d.NodeId == "plug"),
            "without deriving, the plug's signature is out of date");
        var derived = new VisualGenerator(Diagnostics).DeriveSignatures(graph, BuiltIn, out var removed);
        Same(removed.Select(d => $"{d.NodeId}:{d.InputId}:{d.Severity} {d.Message}").ToList(), [
            "plug:Host:2 Removed the value of Host name: the code no longer has that setting",
            "plug:Port:2 Removed the connection to Port: the code no longer has that setting",
        ], "one warning for the value and one for the wire");
        var node = derived["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "plug")!;
        Check(JsonNode.DeepEquals(node["config"]!["signature"], compiled), "the plug's signature is the code's: " + node["config"]!["signature"]!.ToJsonString());
        Check(node["config"]!["arguments"]!.AsObject().Select(p => p.Key).SequenceEqual(["Address"]), "the value of Host is gone and Address's kept: " + node["config"]!["arguments"]!.ToJsonString());
        var edges = derived["edges"]!.AsArray().Select(e => $"{e!["source"]}>{e["target"]}.{e["targetPort"]}").ToList();
        Same(edges, ["strip>temp.device"], "the wire into Port is gone, the C# entity's Device wire kept");
        Check(derived["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "temp")!["config"]!["signature"]!["title"]!.GetValue<string>() == "Thermometer", "the C# entity's signature is written from its code too");
        Check(JsonNode.DeepEquals(graph["nodes"]![1]!["config"]!["signature"]!["settings"]!.AsArray().Count, 3), "the graph given is not changed");
        var again = Previewed(new("Hall", null, [Setting("port", "number", 80)], null, new("graph", DefinitionGraph.Decompose(derived))));
        Check(again.Valid && !again.Diagnostics.Any(d => d.Severity == 3), "the derived graph validates: " + Shown(again.Diagnostics));
        // Code that does not compile keeps its signature, and nothing of it is removed.
        var broken = (JsonObject)graph.DeepClone();
        broken["nodes"]![1]!["config"]!["source"] = source.Replace("string Address", "Missing Address", StringComparison.Ordinal);
        var kept = new VisualGenerator(Diagnostics).DeriveSignatures(broken, BuiltIn, out var none);
        Check(none.Count == 0 && JsonNode.DeepEquals(kept["nodes"]![1], broken["nodes"]![1]) && kept["edges"]!.AsArray().Count == 2, "code that does not compile keeps its signature, values and wires");
    }

    /// <summary>
    /// Deriving writes a setting's new type and whether it is required, and removes nothing a setting the code still has
    /// holds: a text left in a setting that became an entity stays, and the derived graph is refused with the validator's
    /// sentence on that pin; a setting that became required and has no value is refused as a required pin is.
    /// </summary>
    static void DerivingKeepsAValueThatNoLongerFits()
    {
        var entities = NodeLibraryFixture.EntityProvider;
        const string text = "public class Plug : SharpDevice\n{\n    [Setting] public string Host { get; set; } = \"\";\n}";
        DefinitionPreview Derived(string source, JsonObject arguments, out JsonObject node, out List<VisualDiagnostic> removed)
        {
            var plug = CSharp("csharp_device", "plug", "plug", text, arguments);
            plug["config"]!["source"] = source;
            var derived = new VisualGenerator(entities).DeriveSignatures(DefinitionGraph.Compose(Body([plug], [])), BuiltIn, out removed);
            node = derived["nodes"]!.AsArray().OfType<JsonObject>().Single(n => n["id"]!.GetValue<string>() == "plug");
            return new DefinitionGenerator(entities).Preview(new("Hall", null, [], null, new("graph", DefinitionGraph.Decompose(derived))), BuiltIn);
        }
        // string Host became LightEntity Host: the text typed into it is kept, and refused on its pin.
        var retyped = Derived(text.Replace("string Host { get; set; } = \"\";", "LightEntity Host { get; set; }", StringComparison.Ordinal),
            new JsonObject { ["Host"] = Lit("string", "192.168.1.50") }, out var node, out var removed);
        Check(removed.Count == 0 && JsonNode.DeepEquals(node["config"]!["arguments"], new JsonObject { ["Host"] = Lit("string", "192.168.1.50") }),
            "deriving keeps the text of a setting the code still has: " + node["config"]!["arguments"]!.ToJsonString() + " " + Shown(removed));
        Check(node["config"]!["signature"]!["settings"]![0]!.ToJsonString() == """{"key":"Host","label":"Host","description":"","type":"entity","secret":false,"domain":"light","required":true}""",
            "the derived signature's setting is the code's: " + node["config"]!["signature"]!.ToJsonString());
        Check(!retyped.Valid && Shown(retyped.Diagnostics) == "@plug:Host:3 Host accepts Light entity, not Text",
            "the text in an entity setting is refused on its pin, with the validator's sentence: " + Shown(retyped.Diagnostics));
        // Host = "" became Host with no initializer: required, and with no value refused as a required pin is.
        var required = Derived(text.Replace(" = \"\";", "", StringComparison.Ordinal), new JsonObject(), out node, out removed);
        Check(removed.Count == 0 && node["config"]!["signature"]!["settings"]![0]!["required"]?.GetValue<bool>() == true, "the derived setting is required: " + node["config"]!["signature"]!.ToJsonString());
        Check(!required.Valid && Shown(required.Diagnostics) == "@plug:Host:3 Host is required", "a setting that became required and has no value is refused: " + Shown(required.Diagnostics));
    }

    /// <summary>A class whose constructor and static constructor throw previews valid, and neither ran: preview only compiles.</summary>
    static async Task PreviewRunsNoConstructor()
    {
        const string ran = "HASS_SHARP_PREVIEW_CONSTRUCTED";
        Environment.SetEnvironmentVariable(ran, null);
        var source = "public class Bomb : SharpSwitch\n{\n"
            + "    static Bomb() { Environment.SetEnvironmentVariable(\"" + ran + "\", \"static\"); throw new InvalidOperationException(\"the static constructor ran\"); }\n"
            + "    public Bomb() { Environment.SetEnvironmentVariable(\"" + ran + "\", \"instance\"); throw new InvalidOperationException(\"the constructor ran\"); }\n"
            + "    [Setting] public string Host { get; set; } = \"\";\n}";
        var preview = Previewed(Body([CSharp("csharp_entity", "bomb", "bomb", source, new JsonObject { ["Host"] = Lit("string", "bomb.local") })], []));
        Check(preview.Valid && preview.Source.Contains("\"bomb\" => new global::NodeLib.Custom_bomb.Bomb(),", StringComparison.Ordinal), "a class that throws when created previews valid: " + Shown(preview.Diagnostics));
        Check(Environment.GetEnvironmentVariable(ran) == null, "previewing created nothing: " + Environment.GetEnvironmentVariable(ran));
        await CompilesWithAFactory(preview, ["bomb"], "the bomb");
        Check(Environment.GetEnvironmentVariable(ran) == null, "compiling and loading its source created nothing: " + Environment.GetEnvironmentVariable(ran));
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
    /// a command flow is queued and a camera's frame flow, there when its Image is wired, runs one at a time; none is run by hand.
    /// </summary>
    static async Task AnEntityNodesFlowsBecomeActions()
    {
        var graph = Entity(
            [Hub("cam", "door_cam", "camera"), Hub("lamp", "lamp", "light"), Is("e", "binary_sensor.door"),
             N("on", "log", new { value = Lit("string", "on") }), N("frame", "camera_frame", new JsonObject { ["entity"] = Lit("entity_id:camera", "camera.porch") })],
            [D("e", "lamp", "is_on"), Flow("lamp", "command:turn_on", "on"), D("frame", "cam", "image", "image")]);
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

    /// <summary>
    /// The SHA-256 of the source each graph example of <c>authoring_examples.json</c> that is an entity definition without
    /// a C# entity or C# device saves with, by title: recorded from the generator before it learned of C# nodes (at
    /// e6bd24a), so a graph without one keeps its bytes, and recorded again when a camera's frame became its Image (the
    /// source embeds every entity domain, and the camera's lost its frame pin and gained its picture row and interval of
    /// 1/30 s; "Camera with a box" is also a new graph, recorded again when Camera frame gained its size and the Home
    /// Assistant entity its label, which its source embeds). The pin file holds automations only.
    /// </summary>
    static readonly Dictionary<string, string> EntityGraphSources = new(StringComparer.Ordinal)
    {
        ["Motion in either room"] = "ef3e9fee446f2c36a4db723ef414bc912d76a34b2d0f59a98ba29f85c2db97ea",
        ["Plug over HTTP"] = "b91867b7b01310f4e8f9e9846b2db9dbac610ad2968cb30058f12c754cfea902",
        ["Camera with a box"] = "d128729494bb96f1da49d0ed4b0031ecc625a4d2c0c81fcdbc54c2c1fe926b62",
        ["File at setup"] = "95c826924fc7522c56d7cbf37c1848c6129b395cbc4f161e75fbf955deafeee5",
    };

    /// <summary>
    /// Each graph example that is an entity definition without C# nodes, saved through the store (the source
    /// <c>EntityApi.CompileDefinition</c> compiles is the one the save generates), has the source it had before C# nodes.
    /// </summary>
    static void AnEntityGraphWithoutCSharpNodesKeepsItsSource()
    {
        var examples = JsonNode.Parse(File.ReadAllText("custom_components/hass_sharp/authoring_examples.json"))!["definitions"]!.AsArray();
        var directory = Path.Combine(Path.GetTempPath(), "entity-graph-sources-" + Guid.NewGuid());
        try
        {
            var store = new EntityDefinitionStore(directory, new DefinitionGenerator(Diagnostics));
            var got = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var example in examples)
            {
                var body = example!["definition"]!.Deserialize<DefinitionBody>(VisualStore.Json)!;
                if (body.Logic.Kind != "graph" || body.Logic.Graph!["nodes"]!.AsArray().Any(n => n!["kind"]!.GetValue<string>() is "csharp_entity" or "csharp_device")) continue;
                var title = example["title"]!.GetValue<string>();
                var saved = store.Save("example_" + got.Count, body, 0, BuiltIn, null);
                got[title] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(saved.Source)));
            }
            Check(got.Keys.Order(StringComparer.Ordinal).SequenceEqual(EntityGraphSources.Keys.Order(StringComparer.Ordinal)), "the entity graph examples without C# nodes are the four: " + string.Join(", ", got.Keys));
            var changed = got.Where(p => EntityGraphSources[p.Key] != p.Value).Select(p => $"{p.Key}: expected {EntityGraphSources[p.Key]}, got {p.Value}").ToArray();
            Check(changed.Length == 0, "the source of each is unchanged:\n  " + string.Join("\n  ", changed));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    /// <summary>
    /// The source a Device node whose config is not in the catalog's order (its name before its key, its suggested area
    /// and Part of before its model) gives its graph, as it was before C# nodes: recorded from the generator that copied
    /// a Device node's whole config as it has it, and again when a camera's frame became its Image (the source embeds
    /// every entity domain).
    /// </summary>
    const string DeviceOutOfOrderSource = "902efa7356b9d92af61b7c57c5bccd6769e072f993658e5c38f1f052767302b8";

    /// <summary>
    /// A Device node's device keeps its config's order: the derived entry lists its members as the config has them, and
    /// the graph's source is the one it was before C# nodes.
    /// </summary>
    static void ADeviceNodeOutOfCatalogOrderKeepsItsSource()
    {
        var plug = N("plug", "device", new JsonObject
        {
            ["name"] = "Hall plug", ["suggestedArea"] = "Hall", ["key"] = "plug", ["via"] = new JsonObject { ["deviceId"] = "zigbee" },
            ["model"] = "P1", ["manufacturer"] = "Acme",
        });
        var graph = new JsonObject { ["version"] = 5, ["name"] = "Hall", ["nodes"] = new JsonArray(plug, Lamp()), ["edges"] = new JsonArray(D("plug", "lamp", "device", "entities")) };
        var device = VisualNodeCatalog.DeclaredDevices(graph, VisualNodeCatalog.Root).Single().Device;
        Same([.. device.Select(m => m.Key)], ["name", "suggestedArea", "key", "via", "model", "manufacturer"], "the derived entry's members, in the config's order");
        var body = Body([(JsonObject)plug.DeepClone(), Lamp()], [D("plug", "lamp", "device", "entities")]);
        body.Logic.Graph!["edges"]![0]!["id"] = "plug:entities:lamp:device";
        var preview = Previewed(body);
        Check(preview.Valid, "the graph previews valid: " + Shown(preview.Diagnostics));
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(preview.Source)));
        Check(hash == DeviceOutOfOrderSource, $"the source is the one before C# nodes: expected {DeviceOutOfOrderSource}, got {hash}");
    }
}
