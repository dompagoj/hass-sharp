using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;

/// <summary>
/// Entity definitions as documents: the source a valid definition generates, the store's revisions, enabled flag,
/// unreadable files and recovery delete, and each problem the generator reports with its code. A definition whose logic
/// is a graph: its document's entities and settings composed into the graph for checking and generating (never their
/// values) and taken out again before it is stored, its document's problems, its roots, and its rebuild.
/// </summary>
static class EntityStoreTests
{
    const string Plug = "library:shelly:NodeLib.ShellyPlug";
    // The setting types the shelly library does not have, one with a label of its own.
    const string Heater = """
        public class Heater : SharpSwitch
        {
            public Heater() : base("heater") { }
            [Setting] public bool Eco { get; set; }
            [Setting("Target temperature")] public double Target { get; set; }
            [Setting] public long Energy { get; set; }
        }
        """;

    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    static T Throws<T>(Action action, string what) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        catch (Exception error) { throw new Exception($"{what}: threw {error.GetType().Name} instead of {typeof(T).Name}: {error.Message}"); }
        throw new Exception($"{what}: did not throw {typeof(T).Name}");
    }

    static DefinitionBody Body(string name = "Kitchen plug", string? type = Plug, List<DefinitionSetting>? settings = null,
        JsonArray? entities = null, string kind = "class") =>
        new(name, new DefinitionDevice("Shelly", "Plug S", null, null, null, "Kitchen", null), settings, entities, new DefinitionLogic(kind, type, null));

    static DefinitionSetting Setting(string key, string? json) => new(key, json == null ? null : JsonNode.Parse(json));

    static string Codes(DefinitionPreview preview) => string.Join(" | ", preview.Diagnostics.Select(d => $"{d.Code} {d.InputId} {d.Message}"));

    [RuntimeSuite("--entity-store", Order = 12)]
    public static Task Run()
    {
        var compiler = new NodeLibraryCompiler(new DiagnosticsProvider());
        var library = compiler.Preview([new NodeLibrarySource("shelly", EntityTypeTests.Shelly, 1)]);
        Check(library.Valid, "the shelly library compiles");
        var broken = compiler.Preview([new NodeLibrarySource("broken", EntityTypeTests.Broken, 1)]);
        Check(!broken.Valid, "the broken library has errors");
        var heater = compiler.Preview([new NodeLibrarySource("heater", Heater, 1)]);
        Check(heater.Valid, "the heater library compiles");
        Store(library.Snapshot);
        Diagnostics(library.Snapshot, broken.Snapshot, heater.Snapshot);
        ClassDefinitionFilesAreUnchanged(library.Snapshot);
        Graphs(compiler, library.Snapshot);
        return Task.CompletedTask;
    }

    // ---- Definitions whose logic is a graph ----

    static JsonObject N(string id, string kind, object? config = null) => VisualExecutionTests.N(id, kind, config);
    // Wires with ids of their own words, so that the same graph built twice is the same graph (and source).
    static JsonObject D(string source, string target, string targetPort, string sourcePort = "value")
    { var wire = VisualExecutionTests.D(source, target, targetPort, sourcePort); wire["id"] = $"{source}.{sourcePort}:{target}.{targetPort}"; return wire; }
    static JsonObject E(string source, string target) { var wire = VisualExecutionTests.E(source, target); wire["id"] = $"{source}:next:{target}"; return wire; }
    static JsonObject Lit(string type, object? value) => VisualExecutionTests.Lit(type, value);
    static JsonObject Graph(string name, JsonObject[] nodes, params JsonObject[] edges) =>
        new() { ["version"] = 5, ["name"] = name, ["nodes"] = new JsonArray(nodes), ["edges"] = new JsonArray(edges) };
    static JsonObject Entity(string key, string domain, object? more = null)
    {
        var entity = new JsonObject { ["key"] = key, ["domain"] = domain };
        if (more != null) foreach (var (name, value) in JsonSerializer.SerializeToNode(more)!.AsObject()) entity[name] = value?.DeepClone();
        return entity;
    }
    static DefinitionSetting Typed(string key, string? type, string? json = null, string? label = null, bool secret = false) =>
        new(key, json == null ? null : JsonNode.Parse(json)) { Type = type, Label = label, Secret = secret };
    static DefinitionBody GraphBody(JsonObject? graph, JsonArray? entities, List<DefinitionSetting>? settings = null, string name = "Any motion") =>
        new(name, null, settings, entities, new DefinitionLogic("graph", null, graph));

    /// <summary>The acceptance graph: a binary sensor that is on while either of two motion sensors is (the shared fixture's case).</summary>
    internal static JsonObject MotionGraph() => DefinitionGraph.Decompose(JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "visual-graph-validation.json")))!.AsArray()
        .Single(c => c!["name"]!.GetValue<string>() == "entity graph: derived binary sensor")!["graph"]!.AsObject());
    internal static JsonArray MotionEntities() => [Entity("any_motion", "binary_sensor", new { name = "Any motion" })];
    static DefinitionBody Motion() => GraphBody(MotionGraph(), MotionEntities());
    /// <summary>A sensor whose value is a setting, through <paramref name="through"/> when given (a value node reading <c>value</c>).</summary>
    static JsonObject LevelGraph(string? through = null) => through == null
        ? Graph("Level", [N("k", "setting", new { key = "offset" }), N("s", "entity_state", new { entity = "level" })], D("k", "s", "value"))
        : Graph("Level", [N("k", "setting", new { key = "offset" }), N("h", through, new { arguments = new { } }), N("s", "entity_state", new { entity = "level" })], D("k", "h", "value"), D("h", "s", "value", "result"));
    static JsonArray LevelEntities(object? more = null) => [Entity("level", "sensor", more)];
    static string Method(string id) => VisualNodeCatalog.EffectiveTriggerMethod(new JsonObject { ["id"] = id });

    internal const string Maths = "public static class Maths { [Node(\"Half\", Pure = true)] public static double Half(double value) => value / 2; }";

    static void Graphs(NodeLibraryCompiler compiler, VisualCatalogSnapshot snapshot)
    {
        // Every test runs, and each failure is reported under its name.
        var failures = new List<string>();
        foreach (var (name, test) in new (string, Action)[] {
            (nameof(GraphDefinitionSavesAndReadsBack), () => GraphDefinitionSavesAndReadsBack(snapshot)),
            (nameof(ComposeAndDecomposeAreInverse), ComposeAndDecomposeAreInverse),
            (nameof(ComposeCarriesNoSettingValuesOrSecrets), () => ComposeCarriesNoSettingValuesOrSecrets(snapshot)),
            (nameof(GraphDocumentChecks), () => GraphDocumentChecks(snapshot)),
            (nameof(OptionProblemsNameTheirOption), () => OptionProblemsNameTheirOption(snapshot)),
            (nameof(ValidDocumentsAreValidDefinitions), () => ValidDocumentsAreValidDefinitions(snapshot)),
            (nameof(RemovingAnEntityStillNamedByANodeIsRefused), () => RemovingAnEntityStillNamedByANodeIsRefused(snapshot)),
            (nameof(PreviewReturnsTheGraphsWarnings), () => PreviewReturnsTheGraphsWarnings(snapshot)),
            (nameof(RootsListSetupCommandsAndStates), () => RootsListSetupCommandsAndStates(snapshot)),
            (nameof(ClassSettingsKeepTheirShape), () => ClassSettingsKeepTheirShape(snapshot)),
            (nameof(KeepIsNeverStored), () => KeepIsNeverStored(snapshot)),
            (nameof(RebuiltRegeneratesAGraphDefinition), () => RebuiltRegeneratesAGraphDefinition(compiler)) })
            try { test(); }
            catch (Exception e) { failures.Add($"{name}: {e.Message}"); }
        if (failures.Count > 0) throw new Exception($"Graph definitions: {failures.Count} failing:\n" + string.Join("\n", failures));
    }

    static string TempDirectory(string what) => Path.Combine(Path.GetTempPath(), $"entity-store-{what}-" + Guid.NewGuid());

    static void GraphDefinitionSavesAndReadsBack(VisualCatalogSnapshot snapshot)
    {
        var directory = TempDirectory("graph");
        try
        {
            var store = new EntityDefinitionStore(directory, new DefinitionGenerator());
            var graph = MotionGraph();
            graph["comment"] = "kept as written";
            var saved = store.Save("motion", GraphBody(graph, MotionEntities()), 0, snapshot, "g1");
            Check(saved.Revision == 1 && saved.Source.Contains("public class VisualAutomation : global::HassSharp.Automation", StringComparison.Ordinal)
                && saved.SourceMap.Any(m => m.NodeId == "s"), "the acceptance definition is saved at revision 1 with the graph's source: " + saved.Source.Length);
            var read = store.Read("motion")!;
            Check(read is { Revision: 1, Logic.Kind: "graph", Logic.Type: null } && read.Logic.Graph is { } stored && !stored.ContainsKey("definition")
                && stored["comment"]?.GetValue<string>() == "kept as written" && read.Source == saved.Source, "the stored graph has no definition and keeps its unknown keys: " + read.Logic.Graph?.ToJsonString());
            Check(read.Logic.Graph!["nodes"]!.AsArray().All(n => n!["position"] is JsonObject), "the store fills the graph's layout as an automation's");
            Check(JsonNode.DeepEquals(read.Entities, MotionEntities()) && read.Settings.Count == 0 && read.Device == null, "the entities read back as written: " + read.Entities?.ToJsonString());
            var file = File.ReadAllText(store.FilePath("motion"));
            Check(!file.Contains("\"definition\"", StringComparison.Ordinal), "the file holds no composed definition: " + file);
            // The source embeds the graph with its definition composed from the document.
            var preview = new DefinitionGenerator().Preview(read.Body, snapshot);
            Check(preview.Valid && preview.Source == saved.Source && preview.Graph?["definition"] is JsonObject definition && JsonNode.DeepEquals(definition["entities"], MotionEntities())
                && preview.Source.Contains(VisualGenerator.Pieces(preview.Graph.ToJsonString()), StringComparison.Ordinal) && preview.EmbeddedCatalog?["entityDomains"] is JsonObject,
                "the read definition previews to the saved source, with the composed graph and its catalog");

            // A definition a client puts into the graph is never trusted: the document's entities replace it.
            var forged = MotionGraph();
            forged["comment"] = "kept as written";
            forged["definition"] = new JsonObject { ["entities"] = new JsonArray(Entity("evil", "switch")), ["settings"] = new JsonArray() };
            var resaved = store.Save("motion", GraphBody(forged, MotionEntities()), 1, snapshot, "g1");
            Check(resaved.Source == saved.Source && !resaved.Logic.Graph!.ContainsKey("definition") && !File.ReadAllText(store.FilePath("motion")).Contains("evil", StringComparison.Ordinal),
                "a client's definition in the graph is discarded");
            Console.WriteLine("PASS entity store: a graph definition saves at revision 1 with its source, its graph stored without the definition, laid out and with its unknown keys");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    static void ComposeAndDecomposeAreInverse()
    {
        var graph = MotionGraph();
        var before = graph.ToJsonString();
        var body = GraphBody(graph, MotionEntities(), [Typed("threshold", "number", "3"), Typed("host", "string")]);
        var composed = DefinitionGraph.Compose(body);
        Check(graph.ToJsonString() == before, "Compose leaves the body's graph as it was");
        Check(JsonNode.DeepEquals(composed["definition"], JsonNode.Parse("""{"entities":[{"key":"any_motion","domain":"binary_sensor","name":"Any motion"}],"settings":[{"key":"threshold","type":"number"},{"key":"host","type":"string"}]}""")),
            "the definition is the document's entities and each setting's key and type: " + composed["definition"]?.ToJsonString());
        Check(JsonNode.DeepEquals(DefinitionGraph.Decompose(composed), graph), "Decompose gives the graph back");
        Check(composed.ContainsKey("definition"), "Decompose leaves the composed graph as it was");
        var forged = (JsonObject)graph.DeepClone();
        forged["definition"] = new JsonObject { ["entities"] = new JsonArray(Entity("evil", "switch")), ["settings"] = new JsonArray() };
        Check(JsonNode.DeepEquals(DefinitionGraph.Compose(body with { Logic = body.Logic with { Graph = forged } }), composed), "Compose replaces a definition the graph has");
        Check(JsonNode.DeepEquals(DefinitionGraph.Compose(body with { Logic = body.Logic with { Graph = DefinitionGraph.Decompose(composed) } }), composed), "composing a decomposed graph again gives the same graph");
        Check(JsonNode.DeepEquals(DefinitionGraph.Compose(GraphBody(graph, null))["definition"], JsonNode.Parse("""{"entities":[],"settings":[]}""")), "no entities and no settings compose to empty lists");
        Console.WriteLine("PASS entity store: Compose writes the document's entities and settings into the graph, and Decompose takes them out again");
    }

    static void ComposeCarriesNoSettingValuesOrSecrets(VisualCatalogSnapshot snapshot)
    {
        var generator = new DefinitionGenerator();
        DefinitionBody Level(List<DefinitionSetting> settings, JsonArray? entities = null) => GraphBody(LevelGraph(), entities ?? LevelEntities(new { name = "Level", options = new { unit = "W" } }), settings, "Level");
        List<DefinitionSetting> Settings(string password, string offset, string offsetType = "number") =>
            [Typed("password", "string", password, label: "Device password", secret: true), Typed("offset", offsetType, offset, label: "Offset")];
        var body = Level(Settings("\"hunter2\"", "2.5"));
        var composed = DefinitionGraph.Compose(body).ToJsonString();
        Check(!composed.Contains("hunter2", StringComparison.Ordinal) && !composed.Contains("2.5", StringComparison.Ordinal) && !composed.Contains("Device password", StringComparison.Ordinal)
            && !composed.Contains("secret", StringComparison.Ordinal) && !composed.Contains("value\":", StringComparison.Ordinal), "the composed graph has no setting values, labels or secrets: " + composed);
        var preview = generator.Preview(body, snapshot);
        Check(preview.Valid && !preview.Source.Contains("hunter2", StringComparison.Ordinal) && !preview.Source.Contains("Device password", StringComparison.Ordinal), "the source has no setting value: " + Codes(preview));
        Check(generator.Preview(Level(Settings("\"swordfish\"", "7")), snapshot).Source == preview.Source, "changing a setting's value keeps the source");
        Check(generator.Preview(Level(Settings("null", "null")), snapshot).Source == preview.Source, "a setting without a value keeps the source");
        Check(generator.Preview(Level(Settings("\"hunter2\"", "\"2.5\"", "string")), snapshot) is { Valid: true } retyped && retyped.Source != preview.Source, "changing a setting's type changes the source");
        Check(generator.Preview(Level([.. Settings("\"hunter2\"", "2.5"), Typed("extra", "boolean")]), snapshot).Source != preview.Source, "adding a setting changes the source");
        Check(generator.Preview(Level([Typed("password", "string", "\"hunter2\""), Typed("shift", "number")]) with
            { Logic = new("graph", null, Graph("Level", [N("k", "setting", new { key = "shift" }), N("s", "entity_state", new { entity = "level" })], D("k", "s", "value"))) }, snapshot) is { Valid: true } renamed
            && renamed.Source != preview.Source, "renaming a setting changes the source");
        Check(generator.Preview(Level(Settings("\"hunter2\"", "2.5"), LevelEntities(new { name = "Power level", options = new { unit = "W" } })), snapshot).Source != preview.Source,
            "changing an entity's name changes the source");
        Check(generator.Preview(Level(Settings("\"hunter2\"", "2.5"), LevelEntities(new { name = "Level", options = new { unit = "kW" } })), snapshot).Source != preview.Source,
            "changing an entity's option changes the source");
        Console.WriteLine("PASS entity store: the composed graph and the source carry each setting's key and type only; a value change keeps the source, a change of type, settings or entities does not");
    }

    static void GraphDocumentChecks(VisualCatalogSnapshot snapshot)
    {
        var generator = new DefinitionGenerator();
        void Only(DefinitionBody body, string code, string? input, params string[] named)
        {
            var preview = generator.Preview(body, snapshot);
            Check(!preview.Valid && preview.Source == "" && preview.Diagnostics.Count == 1, $"{code}: exactly one diagnostic: " + Codes(preview));
            var diagnostic = preview.Diagnostics[0];
            Check(diagnostic is { Severity: 3, NodeId: null } && diagnostic.Code == code && diagnostic.InputId == input && named.All(n => diagnostic.Message.Contains(n, StringComparison.Ordinal)),
                $"{code}: expected input {input} and a message naming {string.Join(", ", named)}: {Codes(preview)}");
        }
        DefinitionBody With(params JsonObject[] entities) => GraphBody(MotionGraph(), [.. entities, Entity("any_motion", "binary_sensor")]);
        DefinitionBody Level(object? more = null, List<DefinitionSetting>? settings = null) => GraphBody(LevelGraph(), LevelEntities(more), settings ?? [Typed("offset", "number")], "Level");

        Only(GraphBody(Graph("Empty", []), null), "definition.entitiesRequired", null);
        Only(GraphBody(Graph("Empty", []), []), "definition.entitiesRequired", null);
        Only(GraphBody(null, MotionEntities()), "definition.graph", null);
        Only(With(Entity("Bad Key", "binary_sensor")), "definition.entityKey", "Bad Key", "Bad Key");
        Only(With(Entity("x" + new string('a', 40), "binary_sensor")), "definition.entityKey", "x" + new string('a', 40));
        Only(With(new JsonObject { ["domain"] = "binary_sensor" }), "definition.entityKey", null);
        Only(GraphBody(MotionGraph(), [JsonValue.Create("any_motion"), Entity("any_motion", "binary_sensor")]), "definition.entityKey", null);
        Only(With(Entity("any_motion", "binary_sensor")), "definition.duplicateEntity", "any_motion", "any_motion");
        Only(With(Entity("heater", "climate")), "definition.entityDomain", "heater", "heater", "climate", "binary_sensor", "sensor", "switch", "button", "number", "select", "text", "light", "cover");
        Only(With(new JsonObject { ["key"] = "lamp" }), "definition.entityDomain", "lamp", "lamp");
        Only(With(Entity("door", "binary_sensor", new { options = new { unit = "W" } })), "definition.entityOption", "door", "door", "unit");
        Only(Level(new { options = new { stateClass = "sometimes" } }), "definition.entityOption", "level", "State class of ", "measurement");
        Only(Level(new { options = new { unit = 5 } }), "definition.entityOption", "level", "Unit of ");
        Only(Level(new { options = new { options = new object[] { "low", 1 } } }), "definition.entityOption", "level", "Options of ");
        Only(Level(new { options = new { options = "low" } }), "definition.entityOption", "level", "Options of ");
        Only(Level(new { options = new[] { "unit" } }), "definition.entityOption", "level", "level");
        Only(Level(new { options = new { options = Array.Empty<string>() } }), "definition.entityOption", "level", "Options of level is an empty list; give it at least one text, or leave it out");
        // The options of the other domains: numbers, true or false, those a domain needs, a min below the max, a pattern.
        DefinitionBody One(string domain, object? options) => GraphBody(Graph("All", []), [Entity("e", domain, new { options })]);
        Only(One("button", new { unit = "W" }), "definition.entityOption", "e", "Entity e has the option unit, but a button takes no options; remove it");
        Only(One("number", new { min = "5" }), "definition.entityOption", "e", "Minimum of e takes a number");
        Only(One("number", new { step = true }), "definition.entityOption", "e", "Step of e takes a number");
        Only(One("number", new { mode = "dial" }), "definition.entityOption", "e", "Shown as of e takes one of auto, box, slider");
        Only(One("number", new { min = 10, max = 5 }), "definition.entityOption", "e", "Minimum of e is 10, which is not below its maximum of 5");
        Only(One("number", new { min = 2.5, max = 2.5 }), "definition.entityOption", "e", "Minimum of e is 2.5, which is not below its maximum of 2.5");
        Only(One("text", new { min = 8, max = 4 }), "definition.entityOption", "e", "Shortest length of e is 8, which is not below its longest length of 4");
        Only(One("number", new { min = 500 }), "definition.entityOption", "e", "Minimum of e is 500, which is not below its maximum of 100");
        Only(One("text", new { max = -1 }), "definition.entityOption", "e", "Shortest length of e is 0, which is not below its longest length of -1");
        Only(One("text", new { mode = "secret" }), "definition.entityOption", "e", "Shown as of e takes one of text, password");
        Only(One("text", new { pattern = "([" }), "definition.entityOption", "e", "Pattern of e is not a regular expression");
        Only(One("select", null), "definition.entityOption", "e", "e needs options: a select cannot work without it");
        Only(One("select", new { options = (string[]?)null }), "definition.entityOption", "e", "e needs options: a select cannot work without it");
        Only(One("select", new { options = Array.Empty<string>() }), "definition.entityOption", "e", "Options of e is an empty list; give it at least one text");
        Check(!generator.Preview(One("select", new { options = Array.Empty<string>() }), snapshot).Diagnostics[0].Message.Contains("leave it out", StringComparison.Ordinal), "a list an entity needs is not one to leave out");
        Only(One("select", new { options = new object[] { "a", 1 } }), "definition.entityOption", "e", "Options of e takes a list of texts");
        // What Home Assistant takes: a step above 0, a text of 0 to 255 characters, a length a whole number.
        var limits = new List<string>();
        void Limit(DefinitionBody body, params string[] messages)
        {
            var got = generator.Preview(body, snapshot).Diagnostics.Where(d => d.Severity == 3).Select(d => $"{d.Code} {d.InputId} {d.Message}").ToList();
            var want = messages.Select(m => $"definition.entityOption e {m}").ToList();
            if (!got.SequenceEqual(want)) limits.Add($"expected [{string.Join(" | ", want)}], got [{string.Join(" | ", got)}]");
        }
        Limit(One("number", new { step = 0 }), "Step of e takes a number above 0, not 0");
        Limit(One("number", new { step = -0.5 }), "Step of e takes a number above 0, not -0.5");
        Limit(One("text", new { max = 300 }), "Longest length of e takes 255 or less, as Home Assistant keeps at most 255 characters, not 300");
        Limit(One("text", new { min = -1 }), "Shortest length of e takes 0 or more, not -1");
        Limit(One("text", new { min = 0.5 }), "Shortest length of e takes a whole number, not 0.5");
        Limit(One("text", new { min = 0.5, max = 0.9 }), "Shortest length of e takes a whole number, not 0.5", "Longest length of e takes a whole number, not 0.9");
        Limit(One("text", new { min = 0, max = 255 }));
        Limit(One("number", new { min = -0.5, max = 0.5, step = 0.25 }));
        if (limits.Count > 0) throw new Exception($"option limits: {limits.Count} failing:\n" + string.Join("\n", limits));
        Only(One("light", new { dimmable = "yes" }), "definition.entityOption", "e", "Can be dimmed of e takes true or false");
        Only(One("cover", new { positionable = 1 }), "definition.entityOption", "e", "Has a position of e takes true or false");
        Only(Level(new { name = 5 }), "definition.entityField", "level", "level", "name");
        Only(Level(new { icon = true }), "definition.entityField", "level", "level", "icon");
        Only(Level(new { deviceClass = new { kind = "power" } }), "definition.entityField", "level", "level", "deviceClass");
        Only(Level(new { entityCategory = "sensor" }), "definition.entityField", "level", "level", "entityCategory", "config", "diagnostic");
        Only(Level(new { entityCategory = 1 }), "definition.entityField", "level", "level", "entityCategory");
        Only(Level(new { enabledByDefault = "yes" }), "definition.entityField", "level", "level", "enabledByDefault");
        Only(Level(new JsonObject { ["enabledByDefault"] = null }), "definition.entityField", "level", "level", "enabledByDefault");
        Check(generator.Preview(Level(new { name = (string?)null, icon = (string?)null, deviceClass = (string?)null, entityCategory = (string?)null, comment = 7 }), snapshot).Valid,
            "null description fields are none, and a member no check names is kept as given");
        foreach (var category in new[] { "config", "diagnostic" })
            Check(generator.Preview(Level(new { entityCategory = category, enabledByDefault = false }), snapshot).Valid, $"the category {category} is accepted");
        Only(Level(settings: [Typed("offset", null)]), "definition.settingType", "offset", "offset");
        Only(Level(settings: [Typed("offset", "integer")]), "definition.settingType", "offset", "offset", "integer");
        Only(Level(settings: [Typed("offset", "number", "\"x\"")]), "definition.settingType", "offset", "offset");
        Only(Level(settings: [Typed("offset", "number"), Typed("eco", "boolean", "\"yes\"")]), "definition.settingType", "eco", "eco");
        Only(Level(settings: [Typed("offset", "number"), Typed("host", "string", "5", label: "Host name")]), "definition.settingType", "host", "Host name");
        Only(Level(settings: [Typed("offset", "number"), Typed("offset", "number")]), "definition.duplicateSetting", "offset", "offset");
        Only(Level(settings: [Typed("offset", "number"), Typed("", "number")]), "definition.settingKey", null);
        Only(Level(settings: [Typed("offset", "number"), Typed(" ", "number")]), "definition.settingKey", null);
        Only(Level(settings: [Typed("offset", "number"), null!]), "definition.settingKey", null);
        Check(!generator.Preview(Body(kind: "graph", type: null), snapshot).Diagnostics.Any(d => d.Code == "definition.graphUnsupported"), "definition.graphUnsupported is gone");
        Console.WriteLine("PASS entity store: each problem of a graph definition's document gives its code, at the entity or setting it names");

        // The document's problems come first: the graph is not checked against a definition that would only repeat them.
        var both = generator.Preview(GraphBody(MotionGraph(), [Entity("door", "binary_sensor"), Entity("door", "binary_sensor")]), snapshot);
        Check(both.Diagnostics.Select(d => d.Code).SequenceEqual(["definition.duplicateEntity"]), "only the document's problem is reported: " + Codes(both));
        var many = generator.Preview(GraphBody(MotionGraph(), [Entity("Bad", "climate"), Entity("a", "sensor", new { options = new { color = "red" } })], [Typed("t", "date", "1"), Typed("t", "number")], name: ""), snapshot);
        Check(many.Diagnostics.Select(d => d.Code).SequenceEqual(["definition.name", "definition.entityKey", "definition.entityDomain", "definition.entityOption", "definition.settingType", "definition.duplicateSetting"]),
            "every problem of the document is reported, in a fixed order: " + Codes(many));
        Console.WriteLine("PASS entity store: a document with problems gets those alone, every one in a fixed order");
    }

    /// <summary>
    /// Each case of tests/fixtures/entity-option-problems.json, which the panel's tests read too: the problems of one
    /// entity's options, each at the entity (<c>inputId</c>) and the option it is about (<c>portId</c>), in the labels'
    /// words, and with that option in the JSON the API answers.
    /// </summary>
    static void OptionProblemsNameTheirOption(VisualCatalogSnapshot snapshot)
    {
        var generator = new DefinitionGenerator();
        var cases = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "entity-option-problems.json")))!["cases"]!.AsArray();
        var failures = new List<string>();
        foreach (var item in cases.OfType<JsonObject>())
        {
            var entity = new JsonObject { ["key"] = item["key"]!.DeepClone(), ["domain"] = item["domain"]!.DeepClone() };
            if (item["name"] is { } name) entity["name"] = name.DeepClone();
            if (item.ContainsKey("options")) entity["options"] = item["options"]?.DeepClone();
            var preview = generator.Preview(GraphBody(Graph("Options", []), [entity]), snapshot);
            var got = preview.Diagnostics.Select(d => $"{d.Code} {d.InputId} {d.PortId ?? "null"} {d.NodeId ?? "null"} {d.Message}").ToArray();
            var wanted = item["problems"]!.AsArray().OfType<JsonObject>().ToArray();
            bool Matches(VisualDiagnostic d, JsonObject w) => d.Code == w["code"]!.GetValue<string>() && d.InputId == w["inputId"]?.GetValue<string>()
                && d.PortId == w["portId"]?.GetValue<string>() && d.NodeId == null && d.Severity == 3
                && (w["message"] is { } message ? d.Message == message.GetValue<string>() : d.Message.StartsWith(w["messageStartsWith"]!.GetValue<string>(), StringComparison.Ordinal));
            if (preview.Diagnostics.Count != wanted.Length || !preview.Diagnostics.Zip(wanted).All(pair => Matches(pair.First, pair.Second)))
                failures.Add($"{item["what"]}: expected {string.Join(" | ", wanted.Select(w => w.ToJsonString()))}, got {string.Join(" | ", got)}");
            // The preview and save answers serialize the diagnostics as they are: the option goes out as portId.
            var answered = JsonSerializer.SerializeToNode(preview.Diagnostics, VisualStore.Json)!.AsArray();
            if (!answered.OfType<JsonObject>().Zip(wanted).All(pair => pair.First.ContainsKey("portId") && pair.First["portId"]?.ToJsonString() == pair.Second["portId"]?.ToJsonString()))
                failures.Add($"{item["what"]}: the answer's portId: {answered.ToJsonString()}");
        }
        if (failures.Count > 0) throw new Exception($"{failures.Count} of {cases.Count} option problem cases failing:\n" + string.Join("\n", failures));
        Console.WriteLine($"PASS entity store: each of {cases.Count} option problems of the shared fixture is at its entity and option, in the labels' words");
    }

    /// <summary>
    /// The document checks and the validator agree on what a well-formed definition is: each document that passes them
    /// composes to a graph the validator never answers with <c>definition.invalid</c>.
    /// </summary>
    static void ValidDocumentsAreValidDefinitions(VisualCatalogSnapshot snapshot)
    {
        var generator = new DefinitionGenerator();
        var documents = new List<(string What, DefinitionBody Body)>
        {
            ("the acceptance definition", Motion()),
            ("no graph nodes", GraphBody(Graph("Nothing yet", []), [Entity("a", "binary_sensor"), Entity("b", "sensor"), Entity("c", "switch")])),
            ("every entity field", GraphBody(Graph("All", []), [Entity("a", "sensor", new { name = "A", deviceClass = "power", icon = "mdi:flash", entityCategory = "diagnostic", enabledByDefault = false,
                options = new { unit = "W", stateClass = "total_increasing", options = new[] { "low", "high" } } })])),
            ("options left null", GraphBody(Graph("All", []), [Entity("a", "sensor", new { options = new { unit = (string?)null } })])),
            ("no options", GraphBody(Graph("All", []), [Entity("a", "switch", new { options = (object?)null })])),
            ("one entity of each type, every option", GraphBody(Graph("All", []), [
                Entity("bell", "button", new { options = new { } }),
                Entity("target", "number", new { options = new { min = 5, max = 30.5, step = 0.5, mode = "slider", unit = "C" } }),
                Entity("mode", "select", new { options = new { options = new[] { "low", "high" } } }),
                Entity("code", "text", new { options = new { min = 0, max = 8, pattern = "^[0-9]*$", mode = "password" } }),
                Entity("lamp", "light", new { options = new { dimmable = true } }),
                Entity("blind", "cover", new { options = new { positionable = false } })])),
            ("one entity of each type, no options but those needed", GraphBody(Graph("All", []), [Entity("bell", "button"), Entity("target", "number"),
                Entity("mode", "select", new { options = new { options = new[] { "low" } } }), Entity("code", "text"), Entity("lamp", "light"), Entity("blind", "cover")])),
            ("a min alone, a max alone", GraphBody(Graph("All", []), [Entity("low", "number", new { options = new { min = 99.5 } }), Entity("high", "text", new { options = new { max = 1, min = (int?)null } })])),
            ("every setting type", GraphBody(LevelGraph(), LevelEntities(), [Typed("offset", "number", "1.5"), Typed("host", "string", "\"a\"", label: "Host", secret: true), Typed("eco", "boolean", "false"), Typed("blank", "string", "null")], "Level")),
            ("a key of 40 characters", GraphBody(Graph("All", []), [Entity("a" + new string('_', 39), "binary_sensor")])),
            ("an entity and a setting of one key", GraphBody(LevelGraph(), LevelEntities(), [Typed("offset", "number"), Typed("level", "string")], "Level")),
        };
        foreach (var (what, body) in documents)
        {
            var preview = generator.Preview(body, snapshot);
            Check(!preview.Diagnostics.Any(d => d.Code == "definition.invalid" || d.Code?.StartsWith("definition.", StringComparison.Ordinal) == true && d.NodeId == null),
                $"{what}: no problem with the document or the definition composed from it: " + Codes(preview));
            Check(preview.Valid, $"{what}: valid: " + Codes(preview));
            Check(!VisualGraphValidator.Validate(DefinitionGraph.Compose(body), snapshot).Any(d => d.Code == "definition.invalid"), $"{what}: the validator reads the composed definition");
        }
        Console.WriteLine("PASS entity store: every document that passes the document checks composes to a definition the validator reads");
    }

    static void RemovingAnEntityStillNamedByANodeIsRefused(VisualCatalogSnapshot snapshot)
    {
        var directory = TempDirectory("removed");
        try
        {
            var store = new EntityDefinitionStore(directory, new DefinitionGenerator());
            store.Save("motion", Motion(), 0, snapshot, "g1");
            var refused = Throws<DefinitionValidationException>(() => store.Save("motion", GraphBody(MotionGraph(), [Entity("other", "binary_sensor")]), 1, snapshot, "g1"), "saving without the State's entity");
            Check(refused.Preview.Diagnostics.Any(d => d is { Code: "definition.unknownEntity", NodeId: "s", Severity: 3 }) && refused.Preview.Source == "", "the State node is named: " + Codes(refused.Preview));
            Check(store.Read("motion")!.Revision == 1, "the refused save changed nothing");
            Console.WriteLine("PASS entity store: removing an entity a node still names is refused at that node, and nothing is written");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    static void PreviewReturnsTheGraphsWarnings(VisualCatalogSnapshot snapshot)
    {
        var directory = TempDirectory("warnings");
        try
        {
            // A switch with no flow for turn_off: valid, with the validator's warning on it.
            var body = GraphBody(Graph("Relay", [N("on", "entity_command", new { entity = "relay", command = "turn_on" }),
                N("set", "set_entity_state", new JsonObject { ["entity"] = "relay", ["is_on"] = Lit("boolean", true) })], E("on", "set")), [Entity("relay", "switch")], name: "Relay");
            var preview = new DefinitionGenerator().Preview(body, snapshot);
            Check(preview.Valid && preview.Source.Length > 0 && preview.Diagnostics.Any(d => d is { Code: "command.unhandled", Severity: 2 }), "the warning comes back with a valid preview: " + Codes(preview));
            var store = new EntityDefinitionStore(directory, new DefinitionGenerator());
            store.Save("relay", body, 0, snapshot, "g1", null, out var saved);
            Check(saved.Diagnostics.Any(d => d.Code == "command.unhandled") && store.Read("relay")?.Revision == 1, "the save stores it and gives its warnings");
            Console.WriteLine("PASS entity store: a graph's advice comes back from preview and save as warnings and does not make it invalid");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    static void RootsListSetupCommandsAndStates(VisualCatalogSnapshot snapshot)
    {
        var graph = Graph("Relay and motion", [
            N("on", "entity_command", new { entity = "relay", command = "turn_on" }), N("on_set", "set_entity_state", new JsonObject { ["entity"] = "relay", ["is_on"] = Lit("boolean", true) }),
            N("off", "entity_command", new { entity = "relay", command = "turn_off" }), N("off_set", "set_entity_state", new JsonObject { ["entity"] = "relay", ["is_on"] = Lit("boolean", false) }),
            N("setup", "entity_setup"), N("first", "set_entity_available", new JsonObject { ["entity"] = "relay", ["available"] = Lit("boolean", true) }),
            N("m2", "entity_state", new JsonObject { ["entity"] = "motion", ["is_on"] = Lit("boolean", false) }),
            N("m1", "entity_state", new JsonObject { ["entity"] = "door", ["is_on"] = Lit("boolean", true) }),
            N("t", "trigger", new { type = "manual" }), N("poll", "set_entity_state", new JsonObject { ["entity"] = "motion", ["is_on"] = Lit("boolean", true) })],
            E("on", "on_set"), E("off", "off_set"), E("setup", "first"), E("t", "poll"));
        var body = GraphBody(graph, [Entity("relay", "switch"), Entity("motion", "binary_sensor"), Entity("door", "binary_sensor")], name: "Relay");
        var preview = new DefinitionGenerator().Preview(body, snapshot);
        Check(preview.Valid && preview.Diagnostics.Count == 0, "the graph is valid: " + Codes(preview));
        var roots = DefinitionGraph.Roots(preview.Graph!, snapshot);
        Check(roots.Setup == Method("setup"), "the setup method: " + roots.Setup);
        Check(roots.Commands.Count == 2 && roots.Commands["relay:turn_on"] == Method("on") && roots.Commands["relay:turn_off"] == Method("off"),
            "the commands, by entity and command: " + string.Join(", ", roots.Commands.Select(c => c.Key + "=" + c.Value)));
        Check(roots.States.SequenceEqual([Method("m1"), Method("m2")]), "the states, in id order, without the trigger: " + string.Join(", ", roots.States));
        Check(preview.Source.Contains($"Task {Method("t")}()", StringComparison.Ordinal) && !roots.States.Contains(Method("t")) && !roots.Commands.Values.Contains(Method("t")), "the trigger is the automation's to run");
        var none = DefinitionGraph.Roots(DefinitionGraph.Compose(GraphBody(Graph("Nothing", []), [Entity("a", "sensor")])), snapshot);
        Check(none.Setup == null && none.Commands.Count == 0 && none.States.Count == 0, "a graph without roots has none");
        Console.WriteLine("PASS entity store: Roots names the setup, command and state methods as the generator does, and leaves triggers out");
    }

    /// <summary>A class definition's settings are stored as phase 1 stored them, whatever else a client sends with them.</summary>
    static void ClassSettingsKeepTheirShape(VisualCatalogSnapshot snapshot)
    {
        var directory = TempDirectory("class-settings");
        try
        {
            var store = new EntityDefinitionStore(directory, new DefinitionGenerator());
            store.Save("plain", Body(settings: [Setting("Host", "\"10.0.0.5\"")]), 0, snapshot, "g1");
            store.Save("extra", Body(settings: [Typed("Host", "string", "\"10.0.0.5\"", label: "Host", secret: true)]), 0, snapshot, "g1");
            string Settings(string id) => JsonNode.Parse(File.ReadAllText(store.FilePath(id)))!["settings"]!.ToJsonString();
            Check(Settings("plain") == """[{"key":"Host","value":"10.0.0.5"}]""" && Settings("extra") == Settings("plain"), "a class definition's settings: " + Settings("extra"));
            Console.WriteLine("PASS entity store: a class definition's settings keep phase 1's shape");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    /// <summary>
    /// A setting saved with <c>keep</c> is stored with the value the stored definition has for its key (none when it has
    /// none, or when the definition is new), and the marker itself is never stored: not in the file, not in the document.
    /// </summary>
    static void KeepIsNeverStored(VisualCatalogSnapshot snapshot)
    {
        var directory = TempDirectory("keep");
        try
        {
            var store = new EntityDefinitionStore(directory, new DefinitionGenerator());
            DefinitionBody Level(params DefinitionSetting[] settings) => GraphBody(LevelGraph(), LevelEntities(), [Typed("offset", "number", "4"), .. settings], "Level");
            var created = store.Save("level", Level(Typed("token", "string", label: "Token", secret: true) with { Keep = true }), 0, snapshot, "g1");
            Check(created.Settings[1] is { Key: "token", Value: null, Keep: false, Secret: true }, "keep in a new definition stores no value");
            store.Save("level", Level(Typed("token", "string", "\"hunter2\"", label: "Token", secret: true)), 1, snapshot, "g1");
            var kept = store.Save("level", Level(Typed("token", "string", label: "Token", secret: true) with { Keep = true }, Typed("other", "string") with { Keep = true }), 2, snapshot, "g1");
            string Settings() => JsonNode.Parse(File.ReadAllText(store.FilePath("level")))!["settings"]!.ToJsonString();
            Check(Settings() == """[{"key":"offset","value":4,"type":"number"},{"key":"token","value":"hunter2","label":"Token","type":"string","secret":true},{"key":"other","value":null,"type":"string"}]""",
                "the stored settings: " + Settings());
            Check(kept.Settings.All(s => !s.Keep) && store.Read("level")!.Settings.All(s => !s.Keep) && kept.Settings[1].Value!.GetValue<string>() == "hunter2", "the document keeps no marker");
            // A setting sent back as an answer masks it, hasValue and no value, is kept alike; hasValue is not stored either.
            var answered = store.Save("level", Level(Typed("token", "string", label: "Token", secret: true) with { HasValue = true }, Typed("other", "string", "\"open\"") with { HasValue = true }), 3, snapshot, "g1");
            Check(Settings() == """[{"key":"offset","value":4,"type":"number"},{"key":"token","value":"hunter2","label":"Token","type":"string","secret":true},{"key":"other","value":"open","type":"string"}]"""
                && answered.Settings.All(s => !s.HasValue && !s.Keep), "hasValue keeps a value and a value given wins; neither marker is stored: " + Settings());

            // A class definition's kept setting is stored as its settings always are, a key and a value.
            store.Save("plug", Body(settings: [Setting("Host", "\"10.0.0.5\"")]), 0, snapshot, "g1");
            store.Save("plug", Body(settings: [Setting("Host", null) with { Keep = true }]), 1, snapshot, "g1");
            Check(JsonNode.Parse(File.ReadAllText(store.FilePath("plug")))!["settings"]!.ToJsonString() == """[{"key":"Host","value":"10.0.0.5"}]""", "the class definition kept its value: " + File.ReadAllText(store.FilePath("plug")));
            Console.WriteLine("PASS entity store: a kept setting is stored with the value saved before, and keep is never stored");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    static void RebuiltRegeneratesAGraphDefinition(NodeLibraryCompiler compiler)
    {
        var directory = TempDirectory("rebuilt");
        try
        {
            var first = compiler.Preview([new NodeLibrarySource("maths", Maths, 1)]);
            var second = compiler.Preview([new NodeLibrarySource("maths", Maths.Replace("value / 2", "value / 2.0"), 2)]);
            var gone = compiler.Preview([new NodeLibrarySource("maths", "public static class Maths { }", 3)]);
            Check(first.Valid && second.Valid && gone.Valid, "the libraries compile");
            var half = first.Snapshot.Root["nodes"]!.AsObject().Single(n => n.Key.StartsWith("library:maths:", StringComparison.Ordinal)).Key;
            var store = new EntityDefinitionStore(directory, new DefinitionGenerator());
            var body = GraphBody(LevelGraph(half), LevelEntities(), [Typed("offset", "number", "4")], "Level");
            var check = new DefinitionGenerator().Preview(body, first.Snapshot);
            Check(check.Valid, "the library graph is valid: " + Codes(check));
            var saved = store.Save("level", body, 0, first.Snapshot, "g1");
            Check(saved.Source.Contains("revision 1", StringComparison.Ordinal), "the source embeds the library at revision 1");
            var stored = store.Read("level")!;
            var preview = new DefinitionGenerator().Preview(stored.Body, second.Snapshot);
            var rebuilt = store.Rebuilt(stored, preview, "g2");
            Check(rebuilt is { Revision: 2, CatalogGeneration: "g2", Enabled: true } && rebuilt.SavedUtc == stored.SavedUtc && rebuilt.Source != stored.Source
                && rebuilt.Source == preview.Source && rebuilt.Source.Contains("revision 2", StringComparison.Ordinal) && rebuilt.SourceMap.SequenceEqual(preview.SourceMap),
                "Rebuilt gives the next revision with the source against the new library");
            Check(rebuilt.Logic.Graph is { } graph && !graph.ContainsKey("definition") && JsonNode.DeepEquals(graph, stored.Logic.Graph), "the rebuilt graph is stored without its definition");
            Check(store.Read("level")!.Revision == 1, "Rebuilt does not write");
            Check(store.RefreshDerived("level", 1, preview.Source, preview.SourceMap, "g2") && store.Read("level") is { Revision: 1 } refreshed && refreshed.Source == preview.Source
                && JsonNode.DeepEquals(refreshed.Logic.Graph, stored.Logic.Graph), "RefreshDerived stores the regenerated source at the same revision");
            var broken = new DefinitionGenerator().Preview(stored.Body, gone.Snapshot);
            Check(!broken.Valid && broken.Diagnostics.Any(d => d is { NodeId: "h", Code: "node.unknownKind" }), "a library without the node breaks the definition, at the node: " + Codes(broken));
            Throws<DefinitionValidationException>(() => store.Rebuilt(stored, broken, "g3"), "rebuilding against a library without the node");
            Console.WriteLine("PASS entity store: a graph definition using a library node is regenerated against a changed library, and refused by one without the node");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    /// <summary>
    /// A class definition's stored file, byte for byte apart from its save time, as phase 1 wrote it: its settings have
    /// no label, type or secret, and a label, type or secret a client sends with them is not stored.
    /// </summary>
    static void ClassDefinitionFilesAreUnchanged(VisualCatalogSnapshot snapshot)
    {
        var directory = Path.Combine(Path.GetTempPath(), "entity-store-class-" + Guid.NewGuid());
        try
        {
            var store = new EntityDefinitionStore(directory, new DefinitionGenerator());
            var settings = (List<DefinitionSetting>)[Setting("Host", "\"10.0.0.5\""), Setting("Token", null)];
            var source = string.Join('\n',
                "// HassSharp entity definition",
                "public static class SharpDefinition { public static object Create() => new global::NodeLib.ShellyPlug(); }",
                "// NodeLibrarySource \"shelly\" revision 1",
                NodeLibraryCompiler.WrapSource(EntityTypeTests.Shelly)) + "\n";
            string Expected(string id) => "{\"version\":1,\"id\":\"" + id + "\",\"revision\":1,\"name\":\"Kitchen plug\","
                + "\"device\":{\"manufacturer\":\"Shelly\",\"model\":\"Plug S\",\"swVersion\":null,\"hwVersion\":null,\"configurationUrl\":null,\"suggestedArea\":\"Kitchen\",\"via\":null},"
                + "\"settings\":[{\"key\":\"Host\",\"value\":\"10.0.0.5\"},{\"key\":\"Token\",\"value\":null}],\"entities\":null,"
                + "\"logic\":{\"kind\":\"class\",\"type\":\"" + Plug + "\",\"graph\":null},"
                + "\"source\":" + JsonSerializer.Serialize(source, VisualStore.Json) + ","
                + "\"sourceMap\":[{\"nodeId\":\"$library:shelly\",\"startLine\":4,\"endLine\":" + (source.Split('\n').Length - 1) + "}],"
                + "\"enabled\":true,\"savedUtc\":\"SAVED\",\"catalogGeneration\":\"g1\"}";
            string Stored(string id)
            {
                var json = JsonNode.Parse(File.ReadAllText(store.FilePath(id)))!.AsObject();
                Check(json["savedUtc"] is JsonValue, $"{id} has a save time");
                json["savedUtc"] = "SAVED";
                return json.ToJsonString(VisualStore.Json);
            }
            store.Save("plug9", Body(settings: settings), 0, snapshot, "g1");
            Check(Stored("plug9") == Expected("plug9"), "a class definition's file is as phase 1 wrote it:\n" + Stored("plug9") + "\n" + Expected("plug9"));
            Check(File.ReadAllText(store.FilePath("plug9")).Replace(JsonNode.Parse(File.ReadAllText(store.FilePath("plug9")))!["savedUtc"]!.ToJsonString(), "\"SAVED\"") == Expected("plug9"),
                "the file's own bytes match too");
            Console.WriteLine("PASS entity store: a class definition's stored file is byte-identical to phase 1's apart from its save time");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    static void Store(VisualCatalogSnapshot snapshot)
    {
        var directory = Path.Combine(Path.GetTempPath(), "entity-store-" + Guid.NewGuid());
        try
        {
            var store = new EntityDefinitionStore(directory, new DefinitionGenerator());
            var body = Body(settings: [Setting("Host", "\"10.0.0.5\""), Setting("Channel", "2"), Setting("Mode", "\"Boost\"")]);
            var saved = store.Save("plug1", body, 0, snapshot, "g1");
            var expected = string.Join('\n',
                "// HassSharp entity definition",
                "public static class SharpDefinition { public static object Create() => new global::NodeLib.ShellyPlug(); }",
                "// NodeLibrarySource \"shelly\" revision 1",
                NodeLibraryCompiler.WrapSource(EntityTypeTests.Shelly)) + "\n";
            Check(saved.Revision == 1 && saved.Source == expected, "a new definition is saved at revision 1 with the exact source:\n" + saved.Source);
            Check(File.Exists(store.FilePath("plug1")) && store.Read("plug1") is { Revision: 1, Name: "Kitchen plug", Enabled: true, CatalogGeneration: "g1" } read
                && read.Source == expected && read.Settings.Count == 3 && read.Settings[0].Value!.GetValue<string>() == "10.0.0.5"
                && read.Device is { Manufacturer: "Shelly", SuggestedArea: "Kitchen" } && read.Logic is { Kind: "class", Type: Plug } && read.SavedUtc != null,
                "the saved document reads back with its body");
            Check(saved.SourceMap.Count == 1 && saved.SourceMap[0] is { NodeId: "$library:shelly", StartLine: 4 }
                && saved.SourceMap[0].EndLine == expected.Split('\n').Length - 1, "the source map marks the library section: " + string.Join(", ", saved.SourceMap));
            var json = File.ReadAllText(store.FilePath("plug1"));
            Check(json.Contains("\"revision\":1") && json.Contains("\"suggestedArea\":\"Kitchen\"") && !json.Contains("\"body\""), "the file uses the automation documents' JSON names, without the body: " + json);
            Check(saved.Body is { Name: "Kitchen plug", Logic.Type: Plug } && saved.Body.Settings!.Count == 3, "Body gives the definition as written");
            Console.WriteLine("PASS entity store: saving at revision 0 writes revision 1 with the exact generated source");

            Throws<VisualConflictException>(() => store.Save("plug1", body, 0, snapshot, "g1"), "a save with the old revision");
            Check(store.Read("plug1")!.Revision == 1, "a conflicting save changes nothing");
            Console.WriteLine("PASS entity store: a save with a stale revision conflicts");

            Throws<ArgumentException>(() => store.Save("plug1", body, 1, snapshot, "g1", enabled: false), "enabled on an existing definition");
            Console.WriteLine("PASS entity store: enabled can only be given for a new definition");

            var resaved = store.Save("plug1", body, 1, snapshot, "g2");
            Check(resaved.Revision == 2 && resaved.Enabled, "a second save advances the revision and keeps it on");
            var off = store.SetEnabled("plug1", false);
            var reread = store.Read("plug1")!;
            Check(off is { Enabled: false, Revision: 2 } && reread is { Enabled: false, Revision: 2 } && reread.SavedUtc == resaved.SavedUtc && reread.Source == resaved.Source,
                "SetEnabled keeps the revision and save time");
            var kept = store.Save("plug1", body, 2, snapshot, "g2");
            Check(kept is { Revision: 3, Enabled: false }, "a save keeps a definition off");
            Console.WriteLine("PASS entity store: SetEnabled keeps the revision and save time, and a save keeps the flag");

            var created = store.Save("plug2", body, 0, snapshot, "g1", enabled: false);
            Check(!created.Enabled && !store.Read("plug2")!.Enabled, "a new definition can start off");
            Console.WriteLine("PASS entity store: a new definition can be saved off");

            var rebuilt = store.Rebuilt(kept, new DefinitionGenerator().Preview(kept.Body, snapshot), "g3");
            Check(rebuilt is { Revision: 4, Enabled: false, CatalogGeneration: "g3" } && rebuilt.SavedUtc == kept.SavedUtc && rebuilt.Source == expected
                && rebuilt.SourceMap.SequenceEqual(kept.SourceMap), "Rebuilt gives the next revision with the old flag and save time");
            Check(store.Read("plug1")!.Revision == 3, "Rebuilt does not write");
            Console.WriteLine("PASS entity store: Rebuilt returns revision + 1 with the old Enabled and SavedUtc, unwritten");

            Throws<VisualConflictException>(() => store.Delete("plug2", 7), "a delete with the wrong revision");
            Check(store.Read("plug2") != null, "a conflicting delete keeps the file");
            store.Delete("plug2", 1);
            Check(store.Read("plug2") == null, "a delete with the right revision removes it");
            Console.WriteLine("PASS entity store: Delete checks the revision");

            File.WriteAllText(Path.Combine(directory, "broken.json"), "{");
            Check(store.Ids().SequenceEqual(["broken", "plug1"]), "Ids lists every file: " + string.Join(", ", store.Ids()));
            Check(store.List().Select(d => d.Id).SequenceEqual(["plug1"]), "List skips the unreadable file");
            var unreadable = Throws<JsonException>(() => store.List(strict: true), "a strict List over an unreadable file");
            Check(unreadable.Message.StartsWith($"Cannot read entity definition {Path.GetFullPath(Path.Combine(directory, "broken.json"))}: ", StringComparison.Ordinal), "a strict List names the file: " + unreadable.Message);
            Console.WriteLine("PASS entity store: Ids includes an unreadable file, List skips it and a strict List throws");

            Throws<VisualConflictException>(() => store.Delete("broken", 1), "deleting an unreadable file at a revision");
            store.Delete("broken", 0);
            Check(store.Ids().SequenceEqual(["plug1"]), "revision 0 deletes an unreadable file");
            Console.WriteLine("PASS entity store: an unreadable file is deleted only at revision 0");

            Throws<ArgumentException>(() => store.Save("Bad Id", body, 0, snapshot, "g1"), "an id with a space");
            Throws<ArgumentException>(() => store.Read("plug:1"), "an id with a colon");
            Check(!Directory.GetFiles(directory).Any(f => f.EndsWith(".tmp")), "no temporary file is left behind");
            Console.WriteLine("PASS entity store: an invalid id is refused");

            Throws<DefinitionValidationException>(() => store.Save("plug3", Body(type: "library:shelly:NodeLib.Nope"), 0, snapshot, "g1"), "saving an invalid definition");
            Check(store.Read("plug3") == null, "an invalid definition is not stored");
            Console.WriteLine("PASS entity store: an invalid definition throws its preview and stores nothing");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    static void Diagnostics(VisualCatalogSnapshot snapshot, VisualCatalogSnapshot broken, VisualCatalogSnapshot heater)
    {
        var generator = new DefinitionGenerator();
        DefinitionPreview Only(DefinitionBody body, VisualCatalogSnapshot catalog, string code, string message, string? input = null)
        {
            var preview = generator.Preview(body, catalog);
            Check(!preview.Valid && preview.Source == "" && preview.Diagnostics.Count == 1, $"{code}: exactly one diagnostic: " + Codes(preview));
            var diagnostic = preview.Diagnostics[0];
            Check(diagnostic is { Severity: 3, NodeId: null } && diagnostic.Code == code && diagnostic.Message == message && diagnostic.InputId == input,
                $"{code}: {Codes(preview)}");
            return preview;
        }

        var valid = generator.Preview(Body(settings: [Setting("Channel", "-4"), Setting("Mode", "\"Eco\""), Setting("Token", null)]), snapshot);
        Check(valid.Valid && valid.Diagnostics.Count == 0 && valid.Type?.Id == Plug && valid.Source.Length > 0, "a valid definition: " + Codes(valid));
        Check(generator.Preview(Body(entities: []), snapshot).Valid, "an empty entities array counts as none");
        Console.WriteLine("PASS entity definitions: a valid definition previews with its type, and an empty entities array is none");

        Only(Body(name: ""), snapshot, "definition.name", "Give the definition a name of up to 120 characters");
        Only(Body(name: new string('x', 121)), snapshot, "definition.name", "Give the definition a name of up to 120 characters");
        Check(generator.Preview(Body(name: new string('x', 120)), snapshot).Valid, "a name of 120 characters is accepted");
        Only(Body(kind: "script"), snapshot, "definition.logic", "Logic is a graph or a class");
        Only(Body(entities: [new JsonObject { ["key"] = "relay" }]), snapshot, "definition.entitiesNotAllowed", "A class definition's entities come from its code");
        Only(Body(type: "library:broken:NodeLib.Odd"), broken, "definition.library", "The node libraries have errors; fix them first");
        Only(Body(type: "library:shelly:NodeLib.Nope"), snapshot, "definition.unknownType", "No entity class library:shelly:NodeLib.Nope in the node libraries");
        Only(Body(type: null), snapshot, "definition.unknownType", "No entity class was given: choose the class the definition creates");
        Only(Body(type: " "), snapshot, "definition.unknownType", "No entity class was given: choose the class the definition creates");
        Only(Body(settings: [Setting("Port", "80")]), snapshot, "definition.unknownSetting", "Shelly plug has no setting Port", "Port");
        Only(Body(settings: [Setting("Host", "\"a\""), Setting("Host", "\"b\"")]), snapshot, "definition.duplicateSetting", "Host is set twice", "Host");
        Console.WriteLine("PASS entity definitions: each problem gives its code and message");

        // Channel is an int: a whole number outside its range would save, then fail when the definition runs.
        foreach (var value in new[] { "3.5", "1e40", "\"x\"", "true", "9223372036854775808", "2147483648", "-2147483649", "3000000000" })
            Only(Body(settings: [Setting("Channel", value)]), snapshot, "definition.settingType", "Channel takes a whole number from -2147483648 to 2147483647", "Channel");
        foreach (var value in new[] { "2147483647", "-2147483648", "0" })
            Check(generator.Preview(Body(settings: [Setting("Channel", value)]), snapshot).Valid, $"{value} fits an int");
        const string HeaterType = "library:heater:NodeLib.Heater";
        // Energy is a long: any whole number a long holds.
        foreach (var value in new[] { "9223372036854775807", "-9223372036854775808", "3000000000" })
            Check(generator.Preview(Body(type: HeaterType, settings: [Setting("Energy", value)]), heater).Valid, $"{value} fits a long");
        foreach (var value in new[] { "9223372036854775808", "1.5" })
            Only(Body(type: HeaterType, settings: [Setting("Energy", value)]), heater, "definition.settingType", "Energy takes a whole number", "Energy");
        Only(Body(settings: [Setting("Mode", "\"Turbo\"")]), snapshot, "definition.settingType", "Mode takes one of Eco, Boost", "Mode");
        Only(Body(settings: [Setting("Mode", "1")]), snapshot, "definition.settingType", "Mode takes one of Eco, Boost", "Mode");
        Only(Body(settings: [Setting("Host", "5")]), snapshot, "definition.settingType", "Host takes a text", "Host");
        Only(Body(type: HeaterType, settings: [Setting("Eco", "\"yes\"")]), heater, "definition.settingType", "Eco takes true or false", "Eco");
        Only(Body(type: HeaterType, settings: [Setting("Target", "\"20\"")]), heater, "definition.settingType", "Target temperature takes a number", "Target");
        Check(generator.Preview(Body(type: HeaterType, settings: [Setting("Eco", "false"), Setting("Target", "1e40")]), heater).Valid, "a boolean and a number that fit");
        Console.WriteLine("PASS entity definitions: a value that does not fit its setting is reported at the setting");

        // A setting that declares a range takes only a value within it, said by its label and the range.
        var ranged = new NodeLibraryCompiler(new DiagnosticsProvider()).Preview([new NodeLibrarySource("ranged", EntityTypeTests.Ranged, 1)]).Snapshot;
        const string PollerType = "library:ranged:NodeLib.Poller";
        foreach (var value in new[] { "0", "3601", "-5", "1.5" })
            Only(Body(type: PollerType, settings: [Setting("Interval", value)]), ranged, "definition.settingType", "Poll interval takes a whole number from 1 to 3600", "Interval");
        Only(Body(type: PollerType, settings: [Setting("Gain", "0.25")]), ranged, "definition.settingType", "Gain takes a number of at least 0.5", "Gain");
        Only(Body(type: PollerType, settings: [Setting("Count", "11")]), ranged, "definition.settingType", "Count takes a whole number of at most 10", "Count");
        Only(Body(type: PollerType, settings: [Setting("Plain", "2147483648")]), ranged, "definition.settingType", "Plain takes a whole number from -2147483648 to 2147483647", "Plain");
        foreach (var (key, value) in new[] { ("Interval", "1"), ("Interval", "3600"), ("Gain", "0.5"), ("Gain", "1e40"), ("Count", "10"), ("Count", "-9223372036854775808"), ("Plain", "-2147483648") })
            Check(generator.Preview(Body(type: PollerType, settings: [Setting(key, value)]), ranged).Valid, $"{key} {value} is within its range");
        // An int's range given on one side only says that side, not the int's own bound on the other.
        var oneSided = new NodeLibraryCompiler(new DiagnosticsProvider()).Preview([new NodeLibrarySource("sided", """
            public class Sided : SharpDevice
            {
                [Setting(Minimum = 1)] public int Retries { get; set; } = 3;
                [Setting(Maximum = 60)] public int Delay { get; set; }
            }
            """, 1)]).Snapshot;
        Only(Body(type: "library:sided:NodeLib.Sided", settings: [Setting("Retries", "0")]), oneSided, "definition.settingType", "Retries takes a whole number of at least 1", "Retries");
        Only(Body(type: "library:sided:NodeLib.Sided", settings: [Setting("Delay", "61")]), oneSided, "definition.settingType", "Delay takes a whole number of at most 60", "Delay");
        Only(Body(type: "library:sided:NodeLib.Sided", settings: [Setting("Retries", "2147483648")]), oneSided, "definition.settingType", "Retries takes a whole number of at least 1", "Retries");
        Console.WriteLine("PASS entity definitions: a value outside its setting's range is refused naming the setting's label and the range");

        foreach (var key in new[] { "Host", "Token", "Channel", "Mode" })
            Check(generator.Preview(Body(settings: [Setting(key, null)]), snapshot).Valid, $"a null {key} leaves the class default");
        Console.WriteLine("PASS entity definitions: a null value is always accepted");

        var many = generator.Preview(Body(name: "", settings: [Setting("Port", "1"), Setting("Channel", "3.5"), Setting("Channel", "1")]), snapshot);
        Check(many.Diagnostics.Select(d => d.Code).SequenceEqual(["definition.name", "definition.unknownSetting", "definition.settingType", "definition.duplicateSetting"]),
            "every problem is reported, in the table's order: " + Codes(many));
        var unknown = generator.Preview(Body(type: "library:shelly:NodeLib.Nope", settings: [Setting("Port", "1")]), snapshot);
        Check(unknown.Diagnostics.Select(d => d.Code).SequenceEqual(["definition.unknownType"]), "no setting problems against an unknown type: " + Codes(unknown));
        Console.WriteLine("PASS entity definitions: one preview reports every problem, but no setting problems for an unknown type");
    }
}
