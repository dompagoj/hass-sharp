using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;

/// <summary>
/// Entity definitions as documents: the source a valid definition generates, the store's revisions, enabled flag,
/// unreadable files and recovery delete, and each problem the generator reports with its code.
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
        return Task.CompletedTask;
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
            Throws<JsonException>(() => store.List(strict: true), "a strict List over an unreadable file");
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
        Only(Body(kind: "graph", type: null), snapshot, "definition.graphUnsupported", "Graph definitions are not available yet");
        Only(Body(entities: [new JsonObject { ["key"] = "relay" }]), snapshot, "definition.entitiesNotAllowed", "A class definition's entities come from its code");
        Only(Body(type: "library:broken:NodeLib.Odd"), broken, "definition.library", "The node libraries have errors; fix them first");
        Only(Body(type: "library:shelly:NodeLib.Nope"), snapshot, "definition.unknownType", "No entity class library:shelly:NodeLib.Nope in the node libraries");
        Only(Body(settings: [Setting("Port", "80")]), snapshot, "definition.unknownSetting", "ShellyPlug has no setting Port", "Port");
        Only(Body(settings: [Setting("Host", "\"a\""), Setting("Host", "\"b\"")]), snapshot, "definition.duplicateSetting", "Host is set twice", "Host");
        Console.WriteLine("PASS entity definitions: each problem gives its code and message");

        foreach (var value in new[] { "3.5", "1e40", "\"x\"", "true", "9223372036854775808" })
            Only(Body(settings: [Setting("Channel", value)]), snapshot, "definition.settingType", "Channel takes a whole number", "Channel");
        Check(generator.Preview(Body(settings: [Setting("Channel", "9223372036854775807")]), snapshot).Valid, "the largest long is a whole number");
        Only(Body(settings: [Setting("Mode", "\"Turbo\"")]), snapshot, "definition.settingType", "Mode takes one of Eco, Boost", "Mode");
        Only(Body(settings: [Setting("Mode", "1")]), snapshot, "definition.settingType", "Mode takes one of Eco, Boost", "Mode");
        Only(Body(settings: [Setting("Host", "5")]), snapshot, "definition.settingType", "Host takes a text", "Host");
        const string HeaterType = "library:heater:NodeLib.Heater";
        Only(Body(type: HeaterType, settings: [Setting("Eco", "\"yes\"")]), heater, "definition.settingType", "Eco takes true or false", "Eco");
        Only(Body(type: HeaterType, settings: [Setting("Target", "\"20\"")]), heater, "definition.settingType", "Target temperature takes a number", "Target");
        Check(generator.Preview(Body(type: HeaterType, settings: [Setting("Eco", "false"), Setting("Target", "1e40")]), heater).Valid, "a boolean and a number that fit");
        Console.WriteLine("PASS entity definitions: a value that does not fit its setting is reported at the setting");

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
