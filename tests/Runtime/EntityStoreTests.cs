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

    /// <summary>
    /// The acceptance graph: a binary sensor that is on while either of two motion sensors is (the shared fixture's case),
    /// whose Entity node <c>s</c> declares it.
    /// </summary>
    internal static JsonObject MotionGraph() => DefinitionGraph.Decompose(JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "visual-graph-validation.json")))!.AsArray()
        .Single(c => c!["name"]!.GetValue<string>() == "entity graph: derived binary sensor")!["graph"]!.AsObject());
    /// <summary>The entities the acceptance graph's Entity node declares, as a definition's read answer lists them.</summary>
    internal static JsonArray MotionEntities() => [Entity("any_motion", "binary_sensor", new { name = "Any motion", device = (string?)null })];
    static DefinitionBody Motion() => GraphBody(MotionGraph(), null);
    /// <summary>
    /// A sensor whose value is a setting, through <paramref name="through"/> when given (a value node reading <c>value</c>):
    /// the Entity node <c>s</c> of the key <c>level</c>, with <paramref name="more"/> in place of its own settings.
    /// </summary>
    static JsonObject LevelGraph(string? through = null, object? more = null)
    {
        var level = Hub("s", "level", "sensor", more == null ? null : JsonSerializer.SerializeToNode(more)!.AsObject());
        return through == null
            ? Graph("Level", [N("k", "setting", new { key = "offset" }), level], D("k", "s", "value"))
            : Graph("Level", [N("k", "setting", new { key = "offset" }), N("h", through, new { arguments = new { } }), level], D("k", "h", "value"), D("h", "s", "value", "result"));
    }
    static JsonObject Hub(string id, string key, string domain, JsonObject? more = null, JsonObject? values = null) => VisualExecutionTests.Hub(id, key, domain, more, values);
    static JsonObject Flow(string hub, string port, string target) { var wire = VisualExecutionTests.Flow(hub, port, target); wire["id"] = $"{hub}:{port}:{target}"; return wire; }
    static string Method(string id) => VisualNodeCatalog.EffectiveTriggerMethod(new JsonObject { ["id"] = id });

    internal const string Maths = "public static class Maths { [Node(\"Half\", Pure = true)] public static double Half(double value) => value / 2; }";

    static void Graphs(NodeLibraryCompiler compiler, VisualCatalogSnapshot snapshot)
    {
        // Every test runs, and each failure is reported under its name.
        var failures = new List<string>();
        foreach (var (name, test) in new (string, Action)[] {
            (nameof(GraphDefinitionSavesAndReadsBack), () => GraphDefinitionSavesAndReadsBack(snapshot)),
            (nameof(AStoredGraphOfEntityNodesRegeneratesItsSavedSource), () => AStoredGraphOfEntityNodesRegeneratesItsSavedSource(snapshot)),
            (nameof(ComposeAndDecomposeAreInverse), ComposeAndDecomposeAreInverse),
            (nameof(ComposeCarriesNoSettingValuesOrSecrets), () => ComposeCarriesNoSettingValuesOrSecrets(snapshot)),
            (nameof(GraphDocumentChecks), () => GraphDocumentChecks(snapshot)),
            (nameof(OptionProblemsNameTheirOption), () => OptionProblemsNameTheirOption(snapshot)),
            (nameof(ANodesOptionProblemIsOnItsNode), () => ANodesOptionProblemIsOnItsNode(snapshot)),
            (nameof(AnEntityNodesFieldsAreOfTheirType), () => AnEntityNodesFieldsAreOfTheirType(snapshot)),
            (nameof(ADeviceMarksTheGraph), ADeviceMarksTheGraph),
            (nameof(ValidDocumentsAreValidDefinitions), () => ValidDocumentsAreValidDefinitions(snapshot)),
            (nameof(RemovingAnEntityStillNamedByANodeIsRefused), () => RemovingAnEntityStillNamedByANodeIsRefused(snapshot)),
            (nameof(PreviewReturnsTheGraphsWarnings), () => PreviewReturnsTheGraphsWarnings(snapshot)),
            (nameof(RootsListSetupCommandsAndStates), () => RootsListSetupCommandsAndStates(snapshot)),
            (nameof(ClassSettingsKeepTheirShape), () => ClassSettingsKeepTheirShape(snapshot)),
            (nameof(KeepIsNeverStored), () => KeepIsNeverStored(snapshot)),
            (nameof(RebuiltRegeneratesAGraphDefinition), () => RebuiltRegeneratesAGraphDefinition(compiler)),
            (nameof(ARequestThatListsEntitiesIsRefused), () => ARequestThatListsEntitiesIsRefused(snapshot)),
            (nameof(AClassRequestKeepsItsOwnRefusal), () => AClassRequestKeepsItsOwnRefusal(snapshot)),
            (nameof(ADefinitionWithoutAnEntityNodeAsksForOne), () => ADefinitionWithoutAnEntityNodeAsksForOne(snapshot)),
            (nameof(ASaveWritesVersionThreeWithTheDerivedDevices), () => ASaveWritesVersionThreeWithTheDerivedDevices(snapshot)),
            (nameof(AnOlderGraphDocumentSaysToBuildItAgain), () => AnOlderGraphDocumentSaysToBuildItAgain(snapshot)),
            (nameof(AVersionTwoGraphDocumentSaysToBuildItAgain), () => AVersionTwoGraphDocumentSaysToBuildItAgain(snapshot)),
            (nameof(AnOlderClassDocumentStillLoads), () => AnOlderClassDocumentStillLoads(snapshot)),
            (nameof(ARequestThatSendsDevicesBackIsAccepted), () => ARequestThatSendsDevicesBackIsAccepted(snapshot)),
            (nameof(ComposeGivesNoEntities), ComposeGivesNoEntities) })
            try { test(); }
            catch (Exception e) { failures.Add($"{name}: {e.Message}"); }
        if (failures.Count > 0) throw new Exception($"Graph definitions: {failures.Count} failing:\n" + string.Join("\n", failures));
    }

    static string TempDirectory(string what) => Path.Combine(Path.GetTempPath(), $"entity-store-{what}-" + Guid.NewGuid());

    /// <summary>
    /// An Entity node's own fields are checked as its kind declares them, by the graph's config check: a category outside
    /// its choices, an <c>enabledByDefault</c> that is not true or false, and a name, icon or device class that is not text
    /// are each refused on its field.
    /// </summary>
    static void AnEntityNodesFieldsAreOfTheirType(VisualCatalogSnapshot snapshot)
    {
        foreach (var (field, value, message) in new (string, JsonNode, string)[] {
            ("entityCategory", "system", "Category must be one of Config or Diagnostic"),
            ("enabledByDefault", "yes", "Enabled by default must be true or false"),
            ("name", 5, "Name must be text"), ("icon", 5, "Icon must be text"), ("deviceClass", 5, "Device class must be text") })
        {
            var preview = new DefinitionGenerator().Preview(GraphBody(Graph("Door", [Hub("door", "door", "binary_sensor", new JsonObject { [field] = value.DeepClone() })]), null), snapshot);
            Check(!preview.Valid && preview.Diagnostics.Any(d => d is { Severity: 3, NodeId: "door" } && d.InputId == field && d.Message == message),
                $"{field} {value.ToJsonString()} is refused on its field: " + Codes(preview));
        }
        Check(new DefinitionGenerator().Preview(GraphBody(Graph("Door", [Hub("door", "door", "binary_sensor", new JsonObject {
            ["name"] = "Door", ["icon"] = "mdi:door", ["deviceClass"] = "door", ["entityCategory"] = "diagnostic", ["enabledByDefault"] = false })]), null), snapshot).Valid,
            "the same fields of their types are valid");
    }

    internal const string ListedEntities = "Entities are Entity nodes in the graph now: leave entities out and add one Entity node for each";
    internal const string EarlierBuild = "saved by an earlier development build; delete it and build it again";

    /// <summary>
    /// A graph request that has an <c>entities</c> key, even an empty list, is refused with one problem about
    /// <c>entities</c>, by the preview and by the save, which writes nothing.
    /// </summary>
    static void ARequestThatListsEntitiesIsRefused(VisualCatalogSnapshot snapshot)
    {
        var directory = TempDirectory("listed");
        try
        {
            var store = new EntityDefinitionStore(directory, new DefinitionGenerator());
            foreach (var (what, entities) in new (string, JsonArray)[] { ("an empty list", []), ("a list of one", MotionEntities()) })
            {
                var body = GraphBody(MotionGraph(), entities);
                var preview = new DefinitionGenerator().Preview(body, snapshot);
                Check(!preview.Valid && preview.Source == "" && preview.Diagnostics.Count == 1, $"{what}: one problem: " + Codes(preview));
                Check(preview.Diagnostics[0] is { Severity: 3, NodeId: null, InputId: "entities", Code: "definition.entitiesNotAllowed" } && preview.Diagnostics[0].Message == ListedEntities,
                    $"{what}: the refusal of a list: " + Codes(preview));
                var refused = Throws<DefinitionValidationException>(() => store.Save("motion", body, 0, snapshot, "g1"), $"saving {what}");
                Check(refused.Preview.Diagnostics.Select(d => $"{d.Code} {d.InputId} {d.Message}").SequenceEqual([$"definition.entitiesNotAllowed entities {ListedEntities}"]),
                    $"{what}: the save answers the same one problem: " + Codes(refused.Preview));
                Check(store.Read("motion") == null, $"{what}: nothing is written");
            }
            Check(new DefinitionGenerator().Preview(GraphBody(MotionGraph(), null), snapshot).Valid, "the same definition without the key is valid");
            // The refusal alone: a listed graph with no Entity node and a repeated setting is not checked further.
            var bare = new DefinitionGenerator().Preview(GraphBody(Graph("Empty", []), [], [Typed("host", "string"), Typed("host", "string")]), snapshot);
            Check(bare.Diagnostics.Select(d => d.Code).SequenceEqual(["definition.entitiesNotAllowed"]), "a listed graph gets the refusal alone: " + Codes(bare));
            var unnamed = new DefinitionGenerator().Preview(GraphBody(Graph("Empty", []), []) with { Name = "" }, snapshot);
            Check(unnamed.Diagnostics.Select(d => d.Code).SequenceEqual(["definition.name", "definition.entitiesNotAllowed"]), "beside its name's problem: " + Codes(unnamed));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    /// <summary>A class request keeps its own rule and sentence: a list of entities is refused, an empty one is none.</summary>
    static void AClassRequestKeepsItsOwnRefusal(VisualCatalogSnapshot snapshot)
    {
        var generator = new DefinitionGenerator();
        var listed = generator.Preview(Body(entities: [Entity("relay", "switch")]), snapshot);
        Check(listed.Diagnostics.Select(d => $"{d.Code} {d.InputId} {d.Message}").SequenceEqual(["definition.entitiesNotAllowed  A class definition's entities come from its code"]),
            "a class definition's list is refused in its own words: " + Codes(listed));
        Check(generator.Preview(Body(entities: []), snapshot).Valid, "an empty list is none for a class");
    }

    /// <summary>A graph definition without an Entity node is asked for one, about no input.</summary>
    static void ADefinitionWithoutAnEntityNodeAsksForOne(VisualCatalogSnapshot snapshot)
    {
        var preview = new DefinitionGenerator().Preview(GraphBody(Graph("Empty", [N("setup", "entity_setup")]), null), snapshot);
        Check(preview.Diagnostics.Select(d => $"{d.Code}|{d.InputId}|{d.NodeId}|{d.Message}").SequenceEqual(
            ["definition.entitiesRequired|||Add an Entity node: an entity definition creates the entities on its canvas, and has none"]), "the one problem: " + Codes(preview));
    }

    /// <summary>
    /// A switch <c>relay</c> on the Device node <c>plug</c> (Hall plug, model P1, a manufacturer of spaces and Part of
    /// nothing), and a switch <c>bare</c> on none.
    /// </summary>
    static JsonObject PlugGraph() => Graph("Plug",
        [N("plug", "device", new JsonObject { ["key"] = "plug", ["name"] = "Hall plug", ["manufacturer"] = " ", ["model"] = "P1", ["via"] = null }),
            Hub("relay", "relay", "switch"), Hub("bare", "bare", "switch")],
        D("plug", "relay", "device", "entities"));
    /// <summary>The devices <see cref="PlugGraph"/> lists: its Device node's config, without the blank manufacturer and the null Part of.</summary>
    static JsonArray PlugDevices() => [new JsonObject { ["key"] = "plug", ["name"] = "Hall plug", ["model"] = "P1" }];

    /// <summary>
    /// The stored file is version 3, lists the devices its Device nodes declare and the entities its Entity nodes declare,
    /// each with the key of the device it is on or null; a class definition's file is version 3 too and lists no devices.
    /// </summary>
    static void ASaveWritesVersionThreeWithTheDerivedDevices(VisualCatalogSnapshot snapshot)
    {
        var directory = TempDirectory("version");
        try
        {
            var store = new EntityDefinitionStore(directory, new DefinitionGenerator());
            var saved = store.Save("plug", GraphBody(PlugGraph(), null, name: "Plug"), 0, snapshot, "g1");
            var file = JsonNode.Parse(File.ReadAllText(store.FilePath("plug")))!;
            Check(file["version"]!.GetValue<int>() == 3 && saved.Version == 3, "the file is version 3: " + file["version"]?.ToJsonString());
            Check(JsonNode.DeepEquals(file["devices"], PlugDevices()) && JsonNode.DeepEquals(saved.Devices, PlugDevices()), "it lists the Device node's device: " + file["devices"]?.ToJsonString());
            var entities = file["entities"]!.AsArray();
            Check(entities.Select(e => e!["key"]!.GetValue<string>()).SequenceEqual(["relay", "bare"]) && entities[0]!["device"]!.GetValue<string>() == "plug"
                && entities[1]!.AsObject().TryGetPropertyValue("device", out var none) && none == null, "relay is on plug and bare on none: " + entities.ToJsonString());
            var read = store.Read("plug")!;
            Check(JsonNode.DeepEquals(read.Devices, PlugDevices()) && JsonNode.DeepEquals(read.Entities, entities), "they read back as stored: " + read.Devices?.ToJsonString());
            Check(read.Body.Entities == null && !JsonSerializer.SerializeToNode(read.Body, VisualStore.Json)!.AsObject().ContainsKey("devices"),
                "the body of a stored graph has neither: a regeneration composes from the graph alone");
            Check(JsonNode.DeepEquals(store.Save("motion", Motion(), 0, snapshot, "g1").Devices, new JsonArray()) && JsonNode.Parse(File.ReadAllText(store.FilePath("motion")))!["devices"] is JsonArray { Count: 0 },
                "a graph without Device nodes lists none");
            store.Save("lamp", Body(), 0, snapshot, "g1");
            var lamp = JsonNode.Parse(File.ReadAllText(store.FilePath("lamp")))!.AsObject();
            Check(lamp["version"]!.GetValue<int>() == 3 && !lamp.ContainsKey("devices") && store.Read("lamp")!.Devices == null, "a class definition is saved at version 3 too, with no devices: " + lamp.ToJsonString());
            Check(store.Read("lamp")!.Body.Entities == null, "a class definition's body has its own, none here");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    /// <summary>
    /// A version 2 graph document, which a phase 2 build saved, is skipped by List and reported as unreadable with the
    /// sentence, can be deleted at revision 0, and is built again as a new definition.
    /// </summary>
    static void AVersionTwoGraphDocumentSaysToBuildItAgain(VisualCatalogSnapshot snapshot)
    {
        var directory = TempDirectory("version-two");
        try
        {
            var store = new EntityDefinitionStore(directory, new DefinitionGenerator());
            store.Save("motion", Motion(), 0, snapshot, "g1");
            store.Save("lamp", Body(), 0, snapshot, "g1");
            // As a phase 2 build wrote it: version 2, without devices.
            var json = JsonNode.Parse(File.ReadAllText(store.FilePath("motion")))!.AsObject();
            json["version"] = 2; json.Remove("devices");
            File.WriteAllText(store.FilePath("motion"), json.ToJsonString(VisualStore.Json));
            var unreadable = new List<UnreadableDefinition>();
            Check(store.List(unreadable: unreadable).Select(d => d.Id).SequenceEqual(["lamp"]), "List skips it");
            Check(unreadable.SequenceEqual([new UnreadableDefinition("motion", EarlierBuild, true)]), "and reports it as unreadable, with the sentence, deletable: " + string.Join(", ", unreadable));
            Check(Throws<JsonException>(() => store.Read("motion"), "reading it").Message == EarlierBuild, "reading it says why");
            Throws<VisualConflictException>(() => store.Delete("motion", 1), "deleting it at its revision");
            store.Delete("motion", 0);
            Check(!File.Exists(store.FilePath("motion")) && store.Ids().SequenceEqual(["lamp"]), "revision 0 deletes it");
            Check(store.Save("motion", Motion(), 0, snapshot, "g1") is { Version: 3, Revision: 1 }, "it is built again as a new definition");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    /// <summary>
    /// A read answer's definition sent back with its <c>devices</c> (and without <c>entities</c>, which is still refused)
    /// is accepted: the devices sent are ignored, and those of the graph's Device nodes are stored.
    /// </summary>
    static void ARequestThatSendsDevicesBackIsAccepted(VisualCatalogSnapshot snapshot)
    {
        var directory = TempDirectory("devices-back");
        try
        {
            var store = new EntityDefinitionStore(directory, new DefinitionGenerator());
            var saved = store.Save("plug", GraphBody(PlugGraph(), null, name: "Plug"), 0, snapshot, "g1");
            var answer = JsonSerializer.SerializeToNode(store.Read("plug"), VisualStore.Json)!.AsObject();
            Check(answer["devices"] is JsonArray && answer["entities"]![0]!["device"]!.GetValue<string>() == "plug", "the read document has devices and each entity's device: " + answer.ToJsonString());
            var request = new JsonObject { ["name"] = answer["name"]!.DeepClone(), ["settings"] = answer["settings"]!.DeepClone(), ["logic"] = answer["logic"]!.DeepClone(), ["devices"] = answer["devices"]!.DeepClone() };
            request["devices"]!.AsArray().Add(new JsonObject { ["key"] = "evil", ["name"] = "Not on the canvas" });
            // As the API reads a request's definition.
            var body = request.Deserialize<DefinitionBody>(VisualStore.Json)!;
            var preview = new DefinitionGenerator().Preview(body, snapshot);
            Check(preview.Valid && preview.Source == saved.Source, "the definition with devices previews to the source saved: " + Codes(preview));
            var again = store.Save("plug", body, 1, snapshot, "g1");
            Check(again.Revision == 2 && again.Source == saved.Source && JsonNode.DeepEquals(again.Devices, PlugDevices())
                && !File.ReadAllText(store.FilePath("plug")).Contains("evil", StringComparison.Ordinal), "it saves, with the Device nodes' devices and not those sent: " + again.Devices?.ToJsonString());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    /// <summary>The definition's file rewritten with <paramref name="version"/>, as an earlier build wrote it.</summary>
    static void Downgrade(EntityDefinitionStore store, string id, int version)
    {
        var json = JsonNode.Parse(File.ReadAllText(store.FilePath(id)))!.AsObject();
        json["version"] = version;
        File.WriteAllText(store.FilePath(id), json.ToJsonString(VisualStore.Json));
    }

    /// <summary>
    /// A version 1 graph document, from an earlier development build, is skipped by List, says so when read (also in a
    /// strict List), and is deleted at revision 0.
    /// </summary>
    static void AnOlderGraphDocumentSaysToBuildItAgain(VisualCatalogSnapshot snapshot)
    {
        var directory = TempDirectory("older-graph");
        try
        {
            var store = new EntityDefinitionStore(directory, new DefinitionGenerator());
            store.Save("motion", Motion(), 0, snapshot, "g1");
            store.Save("plug", Body(), 0, snapshot, "g1");
            Downgrade(store, "motion", 1);
            Check(store.List().Select(d => d.Id).SequenceEqual(["plug"]), "List skips it: " + string.Join(", ", store.List().Select(d => d.Id)));
            Check(Throws<JsonException>(() => store.Read("motion"), "reading it").Message == EarlierBuild, "reading it says why");
            var strict = Throws<JsonException>(() => store.List(strict: true), "a strict List");
            Check(strict.Message == $"Cannot read entity definition {Path.GetFullPath(store.FilePath("motion"))}: {EarlierBuild}", "a strict List names the file and says why: " + strict.Message);
            Throws<VisualConflictException>(() => store.Delete("motion", 1), "deleting it at its revision");
            store.Delete("motion", 0);
            Check(!File.Exists(store.FilePath("motion")) && store.Ids().SequenceEqual(["plug"]), "revision 0 deletes it");
            Check(store.Save("motion", Motion(), 0, snapshot, "g1") is { Version: 3, Revision: 1 }, "it is built again as a new definition");
            // One with no Entity node is refused alike.
            var empty = JsonNode.Parse(File.ReadAllText(store.FilePath("motion")))!.AsObject();
            empty["version"] = 1; empty["logic"]!["graph"]!["nodes"] = new JsonArray(); empty["entities"] = MotionEntities();
            File.WriteAllText(store.FilePath("listed"), empty.ToJsonString(VisualStore.Json).Replace("\"id\":\"motion\"", "\"id\":\"listed\"", StringComparison.Ordinal));
            Check(Throws<JsonException>(() => store.Read("listed"), "reading a listed one").Message == EarlierBuild, "a version 1 graph document that lists its entities says the same");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    /// <summary>A version 1 or 2 class document, whose document is the same as a version 3 one, is read and listed as it is.</summary>
    static void AnOlderClassDocumentStillLoads(VisualCatalogSnapshot snapshot)
    {
        foreach (var version in (int[])[1, 2])
        {
            var directory = TempDirectory("older-class");
            try
            {
                var store = new EntityDefinitionStore(directory, new DefinitionGenerator());
                var saved = store.Save("plug", Body(settings: [Setting("Host", "\"10.0.0.5\"")]), 0, snapshot, "g1");
                Downgrade(store, "plug", version);
                Check(store.Read("plug") is { Revision: 1, Logic: { Kind: "class", Type: Plug } } read && read.Version == version && read.Source == saved.Source && read.Settings[0].Value!.GetValue<string>() == "10.0.0.5",
                    $"version {version}: it reads back as it was");
                Check(store.List().Select(d => d.Id).SequenceEqual(["plug"]), $"version {version}: it is listed");
                Check(new DefinitionGenerator().Preview(store.Read("plug")!.Body, snapshot) is { Valid: true } preview && preview.Source == saved.Source, $"version {version}: it regenerates its source");
                Check(store.Save("plug", store.Read("plug")!.Body, 1, snapshot, "g1").Version == 3, $"version {version}: saved again, it is version 3");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
    }

    /// <summary>The composed definition is the settings (and a device marker): its entities are the graph's Entity nodes.</summary>
    static void ComposeGivesNoEntities()
    {
        var composed = DefinitionGraph.Compose(GraphBody(MotionGraph(), null, [Typed("host", "string")]));
        Check(composed["definition"] is JsonObject definition && !definition.ContainsKey("entities") && JsonNode.DeepEquals(definition["settings"], JsonNode.Parse("""[{"key":"host","type":"string"}]""")),
            "the definition has settings and no entities: " + composed["definition"]?.ToJsonString());
    }

    static void GraphDefinitionSavesAndReadsBack(VisualCatalogSnapshot snapshot)
    {
        var directory = TempDirectory("graph");
        try
        {
            var store = new EntityDefinitionStore(directory, new DefinitionGenerator());
            var graph = MotionGraph();
            graph["comment"] = "kept as written";
            var saved = store.Save("motion", GraphBody(graph, null), 0, snapshot, "g1");
            Check(saved.Revision == 1 && saved.Source.Contains("public class VisualAutomation : global::HassSharp.Automation", StringComparison.Ordinal)
                && saved.SourceMap.Any(m => m.NodeId == "s"), "the acceptance definition is saved at revision 1 with the graph's source: " + saved.Source.Length);
            var read = store.Read("motion")!;
            Check(read is { Revision: 1, Logic.Kind: "graph", Logic.Type: null } && read.Logic.Graph is { } stored && !stored.ContainsKey("definition")
                && stored["comment"]?.GetValue<string>() == "kept as written" && read.Source == saved.Source, "the stored graph has no definition and keeps its unknown keys: " + read.Logic.Graph?.ToJsonString());
            Check(read.Logic.Graph!["nodes"]!.AsArray().All(n => n!["position"] is JsonObject), "the store fills the graph's layout as an automation's");
            Check(JsonNode.DeepEquals(read.Entities, MotionEntities()) && read.Settings.Count == 0 && read.Device == null, "the entities read back as its Entity node declares them: " + read.Entities?.ToJsonString());
            var file = File.ReadAllText(store.FilePath("motion"));
            Check(!file.Contains("\"definition\"", StringComparison.Ordinal), "the file holds no composed definition: " + file);
            // The source embeds the graph with its definition composed from the document.
            var preview = new DefinitionGenerator().Preview(read.Body, snapshot);
            Check(preview.Valid && preview.Source == saved.Source && preview.Graph?["definition"] is JsonObject definition && !definition.ContainsKey("entities")
                && JsonNode.DeepEquals(DefinitionGraph.Entities(preview.Graph, snapshot), MotionEntities())
                && preview.Source.Contains(VisualGenerator.Pieces(preview.Graph.ToJsonString()), StringComparison.Ordinal) && preview.EmbeddedCatalog?["entityDomains"] is JsonObject,
                "the read definition previews to the saved source, with the composed graph and its catalog");

            // A definition a client puts into the graph is never trusted: the document's own replaces it.
            var forged = MotionGraph();
            forged["comment"] = "kept as written";
            forged["definition"] = new JsonObject { ["entities"] = new JsonArray(Entity("evil", "switch")), ["settings"] = new JsonArray() };
            var resaved = store.Save("motion", GraphBody(forged, null), 1, snapshot, "g1");
            Check(resaved.Source == saved.Source && !resaved.Logic.Graph!.ContainsKey("definition") && !File.ReadAllText(store.FilePath("motion")).Contains("evil", StringComparison.Ordinal),
                "a client's definition in the graph is discarded");
            Console.WriteLine("PASS entity store: a graph definition saves at revision 1 with its source, its graph stored without the definition, laid out and with its unknown keys");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    /// <summary>
    /// A stored definition lists the entities its Entity nodes declare (a read answer has them), but its body has none:
    /// the stored document, and its read answer saved back without them, regenerate the source it was saved with, so a
    /// restart has nothing to write again.
    /// </summary>
    static void AStoredGraphOfEntityNodesRegeneratesItsSavedSource(VisualCatalogSnapshot snapshot)
    {
        var directory = TempDirectory("regenerated");
        try
        {
            var store = new EntityDefinitionStore(directory, new DefinitionGenerator());
            var saved = store.Save("motion", GraphBody(MotionGraph(), null), 0, snapshot, "g1");
            var read = store.Read("motion")!;
            Check(JsonNode.DeepEquals(read.Entities, MotionEntities()), "the stored document lists the node's entity: " + read.Entities?.ToJsonString());
            Check(read.Body.Entities == null && JsonNode.DeepEquals(DefinitionGraph.Compose(read.Body), DefinitionGraph.Compose(GraphBody(read.Logic.Graph, null))),
                "the stored document's body composes from its graph alone: " + DefinitionGraph.Compose(read.Body)["definition"]?.ToJsonString());
            var preview = new DefinitionGenerator().Preview(read.Body, snapshot);
            Check(preview.Valid && preview.Source == saved.Source, "the stored document regenerates the source it was saved with");
            Check(!store.RefreshDerived("motion", 1, preview.Source, preview.SourceMap, "g1"), "so regenerating it, as a restart does, writes nothing");
            Check(store.Save("motion", read.Body, 1, snapshot, "g1").Source == saved.Source, "its read answer saved back without its entities has the same source");
            Console.WriteLine("PASS entity store: a stored graph of Entity nodes, and its read answer saved back, regenerate the source it was saved with");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    static void ComposeAndDecomposeAreInverse()
    {
        var graph = MotionGraph();
        var before = graph.ToJsonString();
        var body = GraphBody(graph, null, [Typed("threshold", "number", "3"), Typed("host", "string")]);
        var composed = DefinitionGraph.Compose(body);
        Check(graph.ToJsonString() == before, "Compose leaves the body's graph as it was");
        Check(JsonNode.DeepEquals(composed["definition"], JsonNode.Parse("""{"settings":[{"key":"threshold","type":"number"},{"key":"host","type":"string"}]}""")),
            "the definition is each setting's key and type, and lists no entity, its Entity node declaring it: " + composed["definition"]?.ToJsonString());
        Check(JsonNode.DeepEquals(DefinitionGraph.Entities(composed, VisualCatalogSnapshot.BuiltIn), MotionEntities()), "the composed graph's entities are its Entity node's: " + DefinitionGraph.Entities(composed, VisualCatalogSnapshot.BuiltIn).ToJsonString());
        Check(JsonNode.DeepEquals(DefinitionGraph.Decompose(composed), graph), "Decompose gives the graph back");
        Check(composed.ContainsKey("definition"), "Decompose leaves the composed graph as it was");
        var forged = (JsonObject)graph.DeepClone();
        forged["definition"] = new JsonObject { ["entities"] = new JsonArray(Entity("evil", "switch")), ["settings"] = new JsonArray() };
        Check(JsonNode.DeepEquals(DefinitionGraph.Compose(body with { Logic = body.Logic with { Graph = forged } }), composed), "Compose replaces a definition the graph has");
        Check(JsonNode.DeepEquals(DefinitionGraph.Compose(body with { Logic = body.Logic with { Graph = DefinitionGraph.Decompose(composed) } }), composed), "composing a decomposed graph again gives the same graph");
        Check(JsonNode.DeepEquals(DefinitionGraph.Compose(GraphBody(graph, null))["definition"], JsonNode.Parse("""{"settings":[]}""")), "no settings compose to an empty list, and the definition has no entities");
        Console.WriteLine("PASS entity store: Compose writes the document's settings into the graph beside its Entity nodes, and Decompose takes them out again");
    }

    static void ComposeCarriesNoSettingValuesOrSecrets(VisualCatalogSnapshot snapshot)
    {
        var generator = new DefinitionGenerator();
        DefinitionBody Level(List<DefinitionSetting> settings, object? entity = null) => GraphBody(LevelGraph(more: entity ?? new { name = "Level", options = new { unit = "W" } }), null, settings, "Level");
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
            { Logic = new("graph", null, Graph("Level", [N("k", "setting", new { key = "shift" }), Hub("s", "level", "sensor", new JsonObject { ["name"] = "Level", ["options"] = new JsonObject { ["unit"] = "W" } })], D("k", "s", "value"))) }, snapshot) is { Valid: true } renamed
            && renamed.Source != preview.Source, "renaming a setting changes the source");
        Check(generator.Preview(Level(Settings("\"hunter2\"", "2.5"), new { name = "Power level", options = new { unit = "W" } }), snapshot).Source != preview.Source,
            "changing an entity's name changes the source");
        Check(generator.Preview(Level(Settings("\"hunter2\"", "2.5"), new { name = "Level", options = new { unit = "kW" } }), snapshot).Source != preview.Source,
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
        // An Entity node's options, the one problem on the node, about the option; a definition with a device, so the
        // entity needs no name, and its commands with no step on them are warnings only.
        void OnNode(DefinitionBody body, string option, params string[] named)
        {
            var preview = generator.Preview(body, snapshot);
            var errors = preview.Diagnostics.Where(d => d.Severity == 3).ToList();
            Check(!preview.Valid && preview.Source == "" && errors.Count == 1, $"option {option}: exactly one problem: " + Codes(preview));
            Check(errors[0] is { NodeId: "e", PortId: null, Code: "definition.entityOption" } && errors[0].InputId == "option:" + option && named.All(n => errors[0].Message.Contains(n, StringComparison.Ordinal)),
                $"option {option}: expected a message naming {string.Join(", ", named)}: {Codes(preview)}");
        }
        DefinitionBody Settings(List<DefinitionSetting> settings) => GraphBody(LevelGraph(), null, settings, "Level");

        Only(GraphBody(Graph("Empty", []), null), "definition.entitiesRequired", null);
        // A graph whose nodes are not a list is told so by the validator, not told to add an Entity node.
        {
            var shapeless = Graph("Shapeless", []);
            shapeless["nodes"] = "none";
            var preview = generator.Preview(GraphBody(shapeless, null), snapshot);
            Check(!preview.Valid && preview.Diagnostics.Any(d => d.Code == "graph.shape") && preview.Diagnostics.All(d => d.Code != "definition.entitiesRequired"),
                "nodes that are not a list: the shape problem, not entitiesRequired: " + Codes(preview));
        }
        Only(GraphBody(null, null), "definition.graph", null);
        // The options of each type: of its type, those a type needs, a min below the max, a pattern.
        OnNode(One("button", new { unit = "W" }), "unit", "Entity e has the option unit, but a button takes no options; remove it");
        OnNode(One("binary_sensor", new { unit = "W" }), "unit", "unit");
        OnNode(One("sensor", new { stateClass = "sometimes" }), "stateClass", "State class of ", "measurement");
        OnNode(One("sensor", new { unit = 5 }), "unit", "Unit of ");
        OnNode(One("sensor", new { options = new object[] { "low", 1 } }), "options", "Options of ");
        OnNode(One("sensor", new { options = "low" }), "options", "Options of ");
        OnNode(One("sensor", new { options = Array.Empty<string>() }), "options", "Options of e is an empty list; give it at least one text, or leave it out");
        OnNode(One("number", new { min = "5" }), "min", "Minimum of e takes a number");
        OnNode(One("number", new { step = true }), "step", "Step of e takes a number");
        OnNode(One("number", new { mode = "dial" }), "mode", "Shown as of e takes one of auto, box, slider");
        OnNode(One("number", new { min = 10, max = 5 }), "min", "Minimum of e is 10, which is not below its maximum of 5");
        OnNode(One("number", new { min = 2.5, max = 2.5 }), "min", "Minimum of e is 2.5, which is not below its maximum of 2.5");
        OnNode(One("text", new { min = 8, max = 4 }), "min", "Shortest length of e is 8, which is not below its longest length of 4");
        OnNode(One("number", new { min = 500 }), "min", "Minimum of e is 500, which is not below its maximum of 100");
        OnNode(One("text", new { max = -1 }), "min", "Shortest length of e is 0, which is not below its longest length of -1");
        OnNode(One("text", new { mode = "secret" }), "mode", "Shown as of e takes one of text, password");
        OnNode(One("text", new { pattern = "([" }), "pattern", "Pattern of e is not a regular expression");
        OnNode(One("select", null), "options", "Entity e needs options: a select cannot work without it");
        OnNode(One("select", new { options = (string[]?)null }), "options", "Entity e needs options: a select cannot work without it");
        OnNode(One("select", new { options = Array.Empty<string>() }), "options", "Options of e is an empty list; give it at least one text");
        Check(!generator.Preview(One("select", new { options = Array.Empty<string>() }), snapshot).Diagnostics.Single(d => d.Severity == 3).Message.Contains("leave it out", StringComparison.Ordinal), "a list an entity needs is not one to leave out");
        OnNode(One("select", new { options = new object[] { "a", 1 } }), "options", "Options of e takes a list of texts");
        // What Home Assistant takes: a step above 0, a text of 0 to 255 characters, a length a whole number.
        var limits = new List<string>();
        void Limit(DefinitionBody body, params (string Option, string Message)[] messages)
        {
            var got = generator.Preview(body, snapshot).Diagnostics.Where(d => d.Severity == 3).Select(d => $"{d.Code} {d.NodeId} {d.InputId} {d.Message}").ToList();
            var want = messages.Select(m => $"definition.entityOption e option:{m.Option} {m.Message}").ToList();
            if (!got.SequenceEqual(want)) limits.Add($"expected [{string.Join(" | ", want)}], got [{string.Join(" | ", got)}]");
        }
        Limit(One("number", new { step = 0 }), ("step", "Step of e takes a number above 0, not 0"));
        Limit(One("number", new { step = -0.5 }), ("step", "Step of e takes a number above 0, not -0.5"));
        Limit(One("text", new { max = 300 }), ("max", "Longest length of e takes 255 or less, as Home Assistant keeps at most 255 characters, not 300"));
        Limit(One("text", new { min = -1 }), ("min", "Shortest length of e takes 0 or more, not -1"));
        Limit(One("text", new { min = 0.5 }), ("min", "Shortest length of e takes a whole number, not 0.5"));
        Limit(One("text", new { min = 0.5, max = 0.9 }), ("min", "Shortest length of e takes a whole number, not 0.5"), ("max", "Longest length of e takes a whole number, not 0.9"));
        Limit(One("text", new { min = 0, max = 255 }));
        Limit(One("number", new { min = -0.5, max = 0.5, step = 0.25 }));
        if (limits.Count > 0) throw new Exception($"option limits: {limits.Count} failing:\n" + string.Join("\n", limits));
        OnNode(One("light", new { dimmable = "yes" }), "dimmable", "Can be dimmed of e takes true or false");
        OnNode(One("cover", new { positionable = 1 }), "positionable", "Has a position of e takes true or false");
        Only(Settings([Typed("offset", null)]), "definition.settingType", "offset", "offset");
        Only(Settings([Typed("offset", "integer")]), "definition.settingType", "offset", "offset", "integer");
        Only(Settings([Typed("offset", "number", "\"x\"")]), "definition.settingType", "offset", "offset");
        Only(Settings([Typed("offset", "number"), Typed("eco", "boolean", "\"yes\"")]), "definition.settingType", "eco", "eco");
        Only(Settings([Typed("offset", "number"), Typed("host", "string", "5", label: "Host name")]), "definition.settingType", "host", "Host name");
        Only(Settings([Typed("offset", "number"), Typed("offset", "number")]), "definition.duplicateSetting", "offset", "offset");
        Only(Settings([Typed("offset", "number"), Typed("", "number")]), "definition.settingKey", null);
        Only(Settings([Typed("offset", "number"), Typed(" ", "number")]), "definition.settingKey", null);
        Only(Settings([Typed("offset", "number"), null!]), "definition.settingKey", null);
        Check(!generator.Preview(Body(kind: "graph", type: null), snapshot).Diagnostics.Any(d => d.Code == "definition.graphUnsupported"), "definition.graphUnsupported is gone");
        Console.WriteLine("PASS entity store: each problem of a graph definition's document gives its code, at the Entity node option or setting it names");

        // The document's problems come first: the graph is not checked against a definition that would only repeat them.
        var many = generator.Preview(GraphBody(Graph("Many", [Hub("e", "e", "sensor", new JsonObject { ["options"] = new JsonObject { ["color"] = "red" } }), N("log", "log", new { value = Lit("string", "Ran") })]),
            null, [Typed("t", "date", "1"), Typed("t", "number")], name: ""), snapshot);
        Check(many.Diagnostics.Select(d => d.Code).SequenceEqual(["definition.name", "definition.entityOption", "definition.settingType", "definition.duplicateSetting"]),
            "every problem of the document is reported, in a fixed order, and none of the graph's: " + Codes(many));
        Console.WriteLine("PASS entity store: a document with problems gets those alone, every one in a fixed order");
    }

    /// <summary>
    /// A definition with a device whose one Entity node <c>e</c>, of no name, is of the type <paramref name="domain"/> with
    /// <paramref name="options"/> (none when null).
    /// </summary>
    static DefinitionBody One(string domain, object? options)
    {
        var hub = Hub("e", "e", domain);
        var config = hub["config"]!.AsObject();
        config.Remove("name");
        if (options == null) config.Remove("options");
        else config["options"] = JsonSerializer.SerializeToNode(options);
        return GraphBody(Graph("One", [hub]), null) with { Device = new DefinitionDevice(null, null, null, null, null, null, null) };
    }

    /// <summary>
    /// Each case of tests/fixtures/entity-option-problems.json, which the panel's tests read too: the problems of one
    /// Entity node's options, each on the node and about its option (<c>option:&lt;option&gt;</c>), in the labels' words,
    /// and with that input in the JSON the API answers.
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
            // The graph's own problems (it has no step, and may have no name) are not the case's.
            var preview = generator.Preview(GraphBody(Graph("Options", [new JsonObject { ["id"] = "e", ["kind"] = "entity", ["config"] = entity }]), null), snapshot);
            var diagnostics = preview.Diagnostics.Where(d => d.Code == "definition.entityOption").ToList();
            var got = diagnostics.Select(d => $"{d.Code} {d.InputId} {d.PortId ?? "null"} {d.NodeId ?? "null"} {d.Message}").ToArray();
            var wanted = item["problems"]!.AsArray().OfType<JsonObject>().Select(w => (Want: w, Input: "option:" + w["option"]!.GetValue<string>())).ToArray();
            bool Matches(VisualDiagnostic d, (JsonObject Want, string Input) w) => d.Code == w.Want["code"]!.GetValue<string>() && d.InputId == w.Input
                && d.PortId == null && d.NodeId == "e" && d.Severity == 3
                && (w.Want["message"] is { } message ? d.Message == message.GetValue<string>() : d.Message.StartsWith(w.Want["messageStartsWith"]!.GetValue<string>(), StringComparison.Ordinal));
            if (diagnostics.Count != wanted.Length || !diagnostics.Zip(wanted).All(pair => Matches(pair.First, pair.Second)))
                failures.Add($"{item["what"]}: expected {string.Join(" | ", wanted.Select(w => w.Want.ToJsonString()))}, got {string.Join(" | ", got)}");
            if (preview.Valid || preview.Source != "")
                failures.Add($"{item["what"]}: a problem on a node leaves the preview invalid, with no source");
            // The preview and save answers serialize the diagnostics as they are: the option goes out as the input.
            var answered = JsonSerializer.SerializeToNode(diagnostics, VisualStore.Json)!.AsArray();
            if (!answered.OfType<JsonObject>().Zip(wanted).All(pair => pair.First["inputId"]?.GetValue<string>() == pair.Second.Input && pair.First["nodeId"]?.GetValue<string>() == "e"))
                failures.Add($"{item["what"]}: the answer's inputId and nodeId: {answered.ToJsonString()}");
        }
        if (failures.Count > 0) throw new Exception($"{failures.Count} of {cases.Count} option problem cases failing:\n" + string.Join("\n", failures));
        Console.WriteLine($"PASS entity store: each of {cases.Count} option problems of the shared fixture is at its entity and option, in the labels' words");
    }

    /// <summary>
    /// An Entity node's options are checked as a listed entity's, and a problem with one is on its node, about
    /// <c>option:&lt;option&gt;</c>: the graph is checked all the same, its problems reported with it, and the preview has no
    /// source.
    /// </summary>
    static void ANodesOptionProblemIsOnItsNode(VisualCatalogSnapshot snapshot)
    {
        var generator = new DefinitionGenerator();
        var select = new JsonObject { ["id"] = "mode", ["kind"] = "entity", ["config"] = new JsonObject { ["key"] = "mode", ["domain"] = "select", ["name"] = "Mode" } };
        var preview = generator.Preview(GraphBody(Graph("Mode", [select]), null), snapshot);
        var errors = preview.Diagnostics.Where(d => d.Severity == 3).ToList();
        Check(errors.Count == 1, "a select without options has one problem: " + Codes(preview));
        Check(errors[0] is { NodeId: "mode", InputId: "option:options", PortId: null, Code: "definition.entityOption" } && errors[0].Message == "Mode needs options: a select cannot work without it",
            $"the problem is on the node, about its option: {errors[0].NodeId} {errors[0].InputId} {errors[0].PortId} {errors[0].Code} {errors[0].Message}");
        Check(!preview.Valid && preview.Source == "" && preview.SourceMap.Count == 0 && preview.EmbeddedCatalog == null, "a problem on a node leaves the preview invalid, with no source");
        // The graph is checked with it: a problem elsewhere in it is reported too, not hidden behind the node's.
        var log = N("log", "log", new { value = Lit("string", "Ran") });
        var both = generator.Preview(GraphBody(Graph("Mode", [(JsonObject)select.DeepClone(), (JsonObject)log.DeepClone()]), null), snapshot);
        var codes = both.Diagnostics.Where(d => d.Severity == 3).Select(d => $"{d.Code} {d.NodeId} {d.InputId}").ToList();
        Check(codes.Contains("definition.entityOption mode option:options") && codes.Contains("node.unreachable log ") && codes.Count == 2, "the node's problem and the graph's are both reported: " + Codes(both));
        Check(!both.Valid && both.Source == "", "with both, the preview is invalid, with no source");
        // A problem of the document on no node still comes alone, before the graph is looked at.
        var named = generator.Preview(GraphBody(Graph("Mode", [(JsonObject)select.DeepClone(), (JsonObject)log.DeepClone()]), null, name: ""), snapshot);
        Check(named.Diagnostics.Select(d => d.Code).SequenceEqual(["definition.name", "definition.entityOption"]), "a problem on no node answers the document's problems alone: " + Codes(named));
        Console.WriteLine("PASS entity store: a problem with an Entity node's option is on its node, reported with the graph's own");
    }

    /// <summary>The graph composed for a definition with a device marks it with an empty <c>device</c>, so that its entities need no names of their own.</summary>
    static void ADeviceMarksTheGraph()
    {
        var body = GraphBody(MotionGraph(), null);
        Check(DefinitionGraph.Compose(body)["definition"] is JsonObject without && !without.ContainsKey("device"), "without a device, the definition has no device key");
        var device = body with { Device = new DefinitionDevice("Acme", "Motion", null, null, null, null, null) };
        Check(DefinitionGraph.Compose(device)["definition"]?["device"] is JsonObject { Count: 0 }, "with one, its device is an empty object: " + DefinitionGraph.Compose(device)["definition"]?.ToJsonString());
        var nameless = new JsonObject { ["id"] = "door", ["kind"] = "entity", ["config"] = new JsonObject { ["key"] = "door", ["domain"] = "binary_sensor", ["name"] = "" } };
        var generator = new DefinitionGenerator();
        Check(generator.Preview(GraphBody(Graph("Door", [nameless]), null), VisualCatalogSnapshot.BuiltIn).Diagnostics.Any(d => d is { Code: "entity.name", NodeId: "door", InputId: "name" }), "an entity of no device needs a name");
        Check(generator.Preview(GraphBody(Graph("Door", [(JsonObject)nameless.DeepClone()]), null) with { Device = new DefinitionDevice(null, null, null, null, null, null, null) }, VisualCatalogSnapshot.BuiltIn)
            is { Valid: true } valid && !valid.Diagnostics.Any(d => d.Code == "entity.name"), "an entity of a device needs none");
        Console.WriteLine("PASS entity store: a definition's device is marked on its composed graph, and an entity of one needs no name");
    }

    /// <summary>
    /// The document checks and the validator agree on what a well-formed definition is: each document that passes them
    /// composes to a graph the validator never answers with <c>definition.invalid</c>.
    /// </summary>
    static void ValidDocumentsAreValidDefinitions(VisualCatalogSnapshot snapshot)
    {
        var generator = new DefinitionGenerator();
        // Entity nodes, one per entity, each keyed and named by its key, with its own settings.
        static DefinitionBody Nodes(params JsonObject[] entities) => GraphBody(Graph("All", [.. entities.Select(e =>
        {
            var hub = Hub(e["key"]!.GetValue<string>(), e["key"]!.GetValue<string>(), e["domain"]!.GetValue<string>());
            foreach (var (name, value) in e) hub["config"]![name] = value?.DeepClone();
            return hub;
        })]), null);
        var documents = new List<(string What, DefinitionBody Body)>
        {
            ("the acceptance definition", Motion()),
            ("Entity nodes and no other step", Nodes(Entity("a", "binary_sensor"), Entity("b", "sensor"), Entity("c", "switch"))),
            ("every entity field", Nodes(Entity("a", "sensor", new { name = "A", deviceClass = "power", icon = "mdi:flash", entityCategory = "diagnostic", enabledByDefault = false,
                options = new { unit = "W", stateClass = "total_increasing", options = new[] { "low", "high" } } }))),
            ("the config category", Nodes(Entity("a", "sensor", new { entityCategory = "config" }))),
            ("options left null", Nodes(Entity("a", "sensor", new { options = new { unit = (string?)null } }))),
            ("one entity of each type, every option", Nodes(
                Entity("bell", "button", new { options = new { } }),
                Entity("target", "number", new { options = new { min = 5, max = 30.5, step = 0.5, mode = "slider", unit = "C" } }),
                Entity("mode", "select", new { options = new { options = new[] { "low", "high" } } }),
                Entity("code", "text", new { options = new { min = 0, max = 8, pattern = "^[0-9]*$", mode = "password" } }),
                Entity("lamp", "light", new { options = new { dimmable = true } }),
                Entity("blind", "cover", new { options = new { positionable = false } }))),
            ("one entity of each type, no options but those needed", Nodes(Entity("bell", "button"), Entity("target", "number"),
                Entity("mode", "select", new { options = new { options = new[] { "low" } } }), Entity("code", "text"), Entity("lamp", "light"), Entity("blind", "cover"))),
            ("a min alone, a max alone", Nodes(Entity("low", "number", new { options = new { min = 99.5 } }), Entity("high", "text", new { options = new { max = 1, min = (int?)null } }))),
            ("every setting type", GraphBody(LevelGraph(), null, [Typed("offset", "number", "1.5"), Typed("host", "string", "\"a\"", label: "Host", secret: true), Typed("eco", "boolean", "false"), Typed("blank", "string", "null")], "Level")),
            ("a key of 40 characters", Nodes(Entity("a" + new string('_', 39), "binary_sensor"))),
            ("an entity and a setting of one key", GraphBody(LevelGraph(), null, [Typed("offset", "number"), Typed("level", "string")], "Level")),
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
            // A When started flow sets the door's state; the door's Entity node is then replaced by another entity's.
            JsonObject Door(string key) => Graph("Door", [Hub(key, key, "binary_sensor"), N("setup", "entity_setup"),
                N("set", "set_entity_state", new JsonObject { ["entity"] = "door", ["is_on"] = Lit("boolean", true) })], E("setup", "set"));
            store.Save("motion", GraphBody(Door("door"), null), 0, snapshot, "g1");
            var refused = Throws<DefinitionValidationException>(() => store.Save("motion", GraphBody(Door("other"), null), 1, snapshot, "g1"), "saving without the Set state's entity");
            Check(refused.Preview.Diagnostics.Any(d => d is { Code: "definition.unknownEntity", NodeId: "set", Severity: 3 }) && refused.Preview.Source == "", "the Set state node is named: " + Codes(refused.Preview));
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
            // A switch with no step on Turn off: valid, with the validator's warning on its pin.
            var body = GraphBody(Graph("Relay", [Hub("relay", "relay", "switch"),
                N("set", "set_entity_state", new JsonObject { ["entity"] = "relay", ["is_on"] = Lit("boolean", true) })], Flow("relay", "command:turn_on", "set")), null, name: "Relay");
            var preview = new DefinitionGenerator().Preview(body, snapshot);
            Check(preview.Valid && preview.Source.Length > 0 && preview.Diagnostics.Any(d => d is { Code: "command.unhandled", Severity: 2, NodeId: "relay", PortId: "command:turn_off" }), "the warning comes back with a valid preview: " + Codes(preview));
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
            Hub("relay", "relay", "switch"), N("on_set", "set_entity_state", new JsonObject { ["entity"] = "relay", ["is_on"] = Lit("boolean", true) }),
            N("off_set", "set_entity_state", new JsonObject { ["entity"] = "relay", ["is_on"] = Lit("boolean", false) }),
            N("setup", "entity_setup"), N("first", "set_entity_available", new JsonObject { ["entity"] = "relay", ["available"] = Lit("boolean", true) }),
            Hub("m2", "motion", "binary_sensor", values: new JsonObject { ["is_on"] = Lit("boolean", false) }),
            Hub("m1", "door", "binary_sensor", values: new JsonObject { ["is_on"] = Lit("boolean", true) }),
            N("t", "trigger", new { type = "manual" }), N("poll", "set_entity_state", new JsonObject { ["entity"] = "motion", ["is_on"] = Lit("boolean", true) })],
            Flow("relay", "command:turn_on", "on_set"), Flow("relay", "command:turn_off", "off_set"), E("setup", "first"), E("t", "poll"));
        var body = GraphBody(graph, null, name: "Relay");
        var preview = new DefinitionGenerator().Preview(body, snapshot);
        Check(preview.Valid && preview.Diagnostics.Count == 0, "the graph is valid: " + Codes(preview));
        var roots = DefinitionGraph.Roots(preview.Graph!, snapshot);
        static string Flowing(string node, string port) => VisualNodeCatalog.FlowMethod(new JsonObject { ["id"] = node }, port);
        Check(roots.Setup == Method("setup"), "the setup method: " + roots.Setup);
        Check(roots.Commands.Count == 2 && roots.Commands["relay:turn_on"] == Flowing("relay", "command:turn_on") && roots.Commands["relay:turn_off"] == Flowing("relay", "command:turn_off"),
            "the commands, by entity and command: " + string.Join(", ", roots.Commands.Select(c => c.Key + "=" + c.Value)));
        Check(roots.States.SequenceEqual([Flowing("m1", "state"), Flowing("m2", "state")]), "the states, in id order, without the trigger: " + string.Join(", ", roots.States));
        Check(preview.Source.Contains($"Task {Method("t")}()", StringComparison.Ordinal) && !roots.States.Contains(Method("t")) && !roots.Commands.Values.Contains(Method("t")), "the trigger is the automation's to run");
        var none = DefinitionGraph.Roots(DefinitionGraph.Compose(GraphBody(Graph("Nothing", [Hub("a", "a", "sensor")]), null)), snapshot);
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
            DefinitionBody Level(params DefinitionSetting[] settings) => GraphBody(LevelGraph(), null, [Typed("offset", "number", "4"), .. settings], "Level");
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
            // Its sensor on a Device node, whose device the rebuild keeps as it keeps the entities.
            var level = LevelGraph(half);
            level["nodes"]!.AsArray().Insert(0, N("meter", "device", new { key = "meter", name = "Meter" }));
            level["edges"]!.AsArray().Add(D("meter", "s", "device", "entities"));
            var body = GraphBody(level, null, [Typed("offset", "number", "4")], "Level");
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
            Check(JsonNode.DeepEquals(stored.Devices, JsonNode.Parse("""[{"key":"meter","name":"Meter"}]""")) && JsonNode.DeepEquals(rebuilt.Devices, stored.Devices)
                && JsonNode.DeepEquals(rebuilt.Entities, stored.Entities) && stored.Entities![0]!["device"]!.GetValue<string>() == "meter",
                "Rebuilt keeps the stored devices and entities: " + rebuilt.Devices?.ToJsonString());
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
    /// A class definition's stored file, byte for byte apart from its save time, as phase 1 wrote it but for its version, 3: it lists no devices, its settings have
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
            string Expected(string id) => "{\"version\":3,\"id\":\"" + id + "\",\"revision\":1,\"name\":\"Kitchen plug\","
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
            Check(Stored("plug9") == Expected("plug9"), "a class definition's file is as phase 1 wrote it, at version 3 (no devices, no key or name on its device):\n" + Stored("plug9") + "\n" + Expected("plug9"));
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
