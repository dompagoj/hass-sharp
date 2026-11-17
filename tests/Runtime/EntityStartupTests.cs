using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>
/// Entity definitions across a restart and a library save, over a whole manager: a second manager over the same
/// directory runs the saved definitions again, skips an unreadable file and leaves a disabled definition off, and tells
/// Home Assistant every stored id, which a prune that fails or never answers does not stop; a definition whose class is
/// gone records why and leaves the others and the automations running; a library save rebuilds and reactivates the
/// definitions, refuses one that would break a valid definition with nothing on disk changed, and lists one that was
/// already invalid; and a load that failed for want of Home Assistant's entity classes loads once they are pushed.
/// </summary>
static class EntityStartupTests
{
    const string Spare = "library:extra:NodeLib.SparePlug";
    const string Extra = """
        public class SparePlug : SharpDevice
        {
            public SparePlug() { Add(new SharpSwitch("relay")); }
        }
        """;
    // FakePlug's turn_on records another word: a library save that changes its behaviour.
    static readonly string Changed = EntityApiTests.Library.Replace("Calls.Enqueue(\"turn_on \" + Host)", "Calls.Enqueue(\"v2 turn_on \" + Host)");
    // The library without FakePlug.
    const string Without = "public class OtherPlug : SharpDevice { public OtherPlug() { Add(new SharpSwitch(\"relay\")); } }";
    // A class whose library compiles only once Home Assistant's entity classes are known.
    const string Lamp = "library:lamps:NodeLib.LampPlug";
    const string Lamps = """
        public class LampPlug : SharpDevice
        {
            public static string Kind => nameof(global::HassSharp.Entities.LightEntity);
            public LampPlug() { Add(new SharpSwitch("relay")); }
        }
        """;

    static async Task<JsonNode> Request(Func<string, Task<string>> api, object request) => JsonNode.Parse(await api(JsonSerializer.Serialize(request)))!;
    static async Task<JsonObject> Ok(Func<string, Task<string>> api, object request)
    {
        var answer = await Request(api, request);
        Check(answer["ok"]!.GetValue<bool>(), "the request failed: " + answer.ToJsonString());
        return answer["result"]!.AsObject();
    }
    static Task<JsonObject> Read(HassSharpManager api, string id) => Ok(api.EntityRequest, new { operation = "read", id });
    static string Status(JsonObject document) => document["status"]!.GetValue<string>();
    static long Revision(JsonObject document) => document["revision"]!.GetValue<long>();
    static string[] Ids(JsonNode? list) => list!.AsArray().Select(item => item!["id"]!.GetValue<string>()).ToArray();
    static string Storage(EntityTestHome home, params string[] parts) => Path.Combine([home.Root, ".storage", "hass_sharp", .. parts]);
    static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name));

    static Task<JsonObject> SaveLibrary(HassSharpManager api, string id, long revision, string source) =>
        Ok(api.NodeLibraryRequest, new { operation = "save", id, name = id, expectedRevision = revision, source });
    static Task<JsonObject> SaveDefinition(HassSharpManager api, string id, string name, string? host, string type = EntityApiTests.Plug, bool? enabled = null) =>
        Ok(api.EntityRequest, new { operation = "save", id, expectedRevision = 0, enabled, definition = EntityApiTests.Definition(name, host, type) });

    [RuntimeSuite("--entity-startup", Order = 15)]
    public static async Task Run()
    {
        await Restart();
        await PruneFails();
        await UnknownClass();
        await LibraryRebuild();
        await Retry();
    }

    // A restart runs the saved definitions again; an unreadable file is skipped, a disabled definition stays off, and the
    // prune keeps every stored id.
    static async Task Restart()
    {
        await using var home = new EntityTestHome("entity-startup-restart");
        var first = new HassSharpManager(home.Root);
        try
        {
            await first.Init();
            Check(home.Prunes().SequenceEqual(["[]"]), "a start with no definitions prunes: " + string.Join(" | ", home.Prunes()));
            await SaveLibrary(first, "shelly", 0, EntityApiTests.Library);
            await SaveDefinition(first, "plug1", "Plug one", "10.0.1.1");
            await SaveDefinition(first, "plug2", "Plug two", "10.0.1.2");
            await SaveDefinition(first, "plug3", "Plug three", "10.0.1.3", enabled: false);
        }
        finally { await first.UnloadAsync(); }

        File.WriteAllText(Storage(home, "entities", "plug2.json"), "{");
        var (syncs, broken) = (home.Syncs("plug1").Length, home.Syncs("plug2").Length);
        var api = new HassSharpManager(home.Root);
        try
        {
            await api.Init();
            var plug1 = await Read(api, "plug1");
            Check(Status(plug1) == "running" && plug1["currentRevisionActive"]!.GetValue<bool>() && plug1["activationError"] == null && Revision(plug1) == 1,
                "plug1 runs after the restart: " + plug1.ToJsonString());
            Check(home.Syncs("plug1").Length == syncs + 1, "plug1 is not synced again");
            await api.EntityCommand("plug1:relay", "turn_on", null);
            Check(EntityTestHome.Commands().Count(c => c == "turn_on 10.0.1.1") == 1, "the command after the restart: " + string.Join(", ", EntityTestHome.Commands()));
            Console.WriteLine("PASS entity startup: a restart runs the saved definition again, syncs it and takes its commands");

            Check(home.Prunes()[^1] == """["plug1","plug2","plug3"]""", "the prune keeps every stored id: " + string.Join(" | ", home.Prunes()));
            Check(home.Logs().Any(line => line.Contains("Skipping entity definition") && line.Contains("plug2.json")), "the unreadable file is not logged: " + string.Join(" | ", home.Logs()));
            Check(home.Syncs("plug2").Length == broken, "the unreadable definition synced");
            Console.WriteLine("PASS entity startup: an unreadable definition file is logged and skipped, its id still kept by the prune, and the others run");

            var plug3 = await Read(api, "plug3");
            Check(Status(plug3) == "off" && !plug3["active"]!.GetValue<bool>() && !plug3["enabled"]!.GetValue<bool>() && home.Syncs("plug3").Length == 0,
                "a disabled definition loaded at startup: " + plug3.ToJsonString());
            Console.WriteLine("PASS entity startup: a disabled definition is not loaded at startup and is still kept by the prune");
        }
        finally { await api.UnloadAsync(); }
    }

    // A prune that fails, or that Python never answers, is logged; startup goes on and the definitions run.
    static async Task PruneFails()
    {
        await using var home = new EntityTestHome("entity-startup-prune");
        var first = new HassSharpManager(home.Root);
        try
        {
            await first.Init();
            await SaveLibrary(first, "shelly", 0, EntityApiTests.Library);
            await SaveDefinition(first, "plug1", "Plug one", "10.0.1.11");
        }
        finally { await first.UnloadAsync(); }

        home.FailPrune = true;
        var failing = new HassSharpManager(home.Root);
        try
        {
            await failing.Init().WaitAsync(TimeSpan.FromSeconds(30));
            Check(home.Logs().Any(line => line.Contains("Cannot prune") && line.Contains("the registry is locked")), "the failing prune is not logged: " + string.Join(" | ", home.Logs()));
            Check(Status(await Read(failing, "plug1")) == "running", "plug1 does not run after a failing prune");
        }
        finally { await failing.UnloadAsync(); }

        home.FailPrune = false; home.IgnorePrune = true;
        HassSharpManager.RemovalTimeout = TimeSpan.FromMilliseconds(200);
        var quiet = new HassSharpManager(home.Root);
        try
        {
            await quiet.Init().WaitAsync(TimeSpan.FromSeconds(5));
            Check(home.Logs().Any(line => line.Contains("Cannot prune") && line.Contains("did not answer")), "the unanswered prune is not logged: " + string.Join(" | ", home.Logs()));
            Check(Status(await Read(quiet, "plug1").WaitAsync(TimeSpan.FromSeconds(5))) == "running", "plug1 does not run after an unanswered prune");
        }
        finally { await quiet.UnloadAsync(); }
        Console.WriteLine("PASS entity startup: a prune that fails or never answers is logged and does not fail or hold up startup");
    }

    // A definition whose class is gone records why it does not load; the other definitions and the automations load. A
    // library save then leaves it as it is and lists it, running or not.
    static async Task UnknownClass()
    {
        await using var home = new EntityTestHome("entity-startup-unknown");
        var first = new HassSharpManager(home.Root);
        try
        {
            await first.Init();
            await SaveLibrary(first, "shelly", 0, EntityApiTests.Library);
            await SaveLibrary(first, "extra", 0, Extra);
            await SaveDefinition(first, "plug1", "Plug one", "10.0.1.21");
            await SaveDefinition(first, "spare", "Spare plug", null, Spare);
            var automation = await Ok(first.VisualRequest, new { operation = "save", id = "auto", graph = G(N("l", "log", new { value = Lit("string", "hello") })), expectedRevision = 0 });
            Check(automation["active"]!.GetValue<bool>(), "the automation does not run: " + automation.ToJsonString());

            // The library is deleted on disk while plug1 runs: a library save leaves the running copy as it is.
            File.Delete(Storage(home, "libraries", "shelly.json"));
            var running = await SaveLibrary(first, "extra", 1, Extra);
            var listed = running["invalidDefinitions"]!.AsArray().Single()!;
            Check(listed["id"]!.GetValue<string>() == "plug1" && listed["name"]!.GetValue<string>() == "Plug one" && listed["running"]!.GetValue<bool>()
                && listed["message"]!.GetValue<string>() == "Not rebuilt, because the definition has errors: it is still running the previous version, with the previous library code"
                && Ids(running["definitions"]).SequenceEqual(["spare"]), "a library save with a running invalid definition: " + running.ToJsonString());
            Check(Status(await Read(first, "plug1")) == "running" && Revision(await Read(first, "plug1")) == 1, "the running invalid definition was rebuilt or stopped");
        }
        finally { await first.UnloadAsync(); }

        var api = new HassSharpManager(home.Root);
        try
        {
            await api.Init();
            var plug1 = await Read(api, "plug1");
            var unknown = $"No entity class {EntityApiTests.Plug} in the node libraries";
            Check(Status(plug1) == "off" && !plug1["active"]!.GetValue<bool>() && plug1["activationError"]?.GetValue<string>() == unknown,
                "plug1's load error: " + plug1.ToJsonString());
            Check(home.Logs().Contains($"Cannot load entity definition \"Plug one\" (plug1): 1 problem: {unknown}"), "the load failure is not logged: " + string.Join(" | ", home.Logs()));
            Check(Status(await Read(api, "spare")) == "running" && VisualTests.Loaded(api, "auto") != null, "the other definition or the automation does not run");
            Check(home.Prunes()[^1] == """["plug1","spare"]""", "the prune: " + home.Prunes()[^1]);
            Console.WriteLine("PASS entity startup: a definition whose class is gone records why and the other definitions and automations load");

            var saved = await SaveLibrary(api, "extra", 2, Extra);
            var listed = saved["invalidDefinitions"]!.AsArray().Single()!;
            Check(listed["id"]!.GetValue<string>() == "plug1" && !listed["running"]!.GetValue<bool>()
                && listed["message"]!.GetValue<string>() == "Not rebuilt, because the definition has errors; it is not running"
                && listed["diagnostics"]!.AsArray().Any(d => d!["code"]!.GetValue<string>() == "definition.unknownType")
                && Ids(saved["definitions"]).SequenceEqual(["spare"]) && saved["definitions"]![0]!["active"]!.GetValue<bool>(),
                "a library save with an invalid definition: " + saved.ToJsonString());
            Check(Revision(await Read(api, "plug1")) == 1 && Revision(await Read(api, "spare")) == 3, "the revisions after the save");
            Console.WriteLine("PASS entity startup: a library save goes ahead past a definition that was already invalid and lists it, running or not");
        }
        finally { await api.UnloadAsync(); }
    }

    // A library save rebuilds the definitions and runs the new code; one that breaks a valid definition is refused.
    static async Task LibraryRebuild()
    {
        await using var home = new EntityTestHome("entity-startup-rebuild");
        var api = new HassSharpManager(home.Root);
        try
        {
            await api.Init();
            var first = await SaveLibrary(api, "shelly", 0, EntityApiTests.Library);
            Check(first["definitions"]!.AsArray().Count == 0 && first["invalidDefinitions"]!.AsArray().Count == 0
                && first["automations"]!.AsArray().Count == 0 && first["invalid"]!.AsArray().Count == 0, "a library save with no definitions: " + first.ToJsonString());
            await SaveDefinition(api, "plug1", "Plug one", "10.0.2.1");
            await SaveDefinition(api, "plug3", "Plug three", "10.0.2.3", enabled: false);

            var changed = await SaveLibrary(api, "shelly", 1, Changed);
            var activation = changed["definitions"]!.AsArray().Single()!;
            Check(activation["id"]!.GetValue<string>() == "plug1" && activation["active"]!.GetValue<bool>() && activation["activationError"] == null
                && Revision(activation["document"]!.AsObject()) == 2 && changed["invalidDefinitions"]!.AsArray().Count == 0, "the rebuild: " + changed.ToJsonString());
            var plug1 = await Read(api, "plug1");
            Check(Revision(plug1) == 2 && Status(plug1) == "running" && plug1["currentRevisionActive"]!.GetValue<bool>() && plug1["source"]!.GetValue<string>().Contains("v2 turn_on"),
                "plug1 after the rebuild: " + plug1.ToJsonString());
            await api.EntityCommand("plug1:relay", "turn_on", null);
            Check(EntityTestHome.Commands().Contains("v2 turn_on 10.0.2.1"), "the command does not run the new code: " + string.Join(", ", EntityTestHome.Commands()));
            var plug3 = await Read(api, "plug3");
            Check(Revision(plug3) == 2 && Status(plug3) == "off" && plug3["source"]!.GetValue<string>().Contains("v2 turn_on") && home.Syncs("plug3").Length == 0,
                "the disabled definition after the rebuild: " + plug3.ToJsonString());
            Console.WriteLine("PASS entity startup: a library save rebuilds the definitions, reactivates the enabled one with the new code and leaves the disabled one off");

            string[] Files() => [File.ReadAllText(Storage(home, "libraries", "shelly.json")), File.ReadAllText(Storage(home, "entities", "plug1.json")), File.ReadAllText(Storage(home, "entities", "plug3.json"))];
            var before = Files();
            var refused = await Request(api.NodeLibraryRequest, new { operation = "save", id = "shelly", name = "shelly", expectedRevision = 2, source = Without });
            var definitions = refused["error"]?["detail"]?["definitions"];
            Check(refused["error"]?["code"]?.GetValue<string>() == "validation" && Ids(definitions).SequenceEqual(["plug1", "plug3"])
                && definitions![0]!["name"]!.GetValue<string>() == "Plug one"
                && definitions[0]!["diagnostics"]!.AsArray().Any(d => d!["code"]!.GetValue<string>() == "definition.unknownType"), "the refusal: " + refused.ToJsonString());
            Check(Files().SequenceEqual(before), "a refused save changed the files on disk");
            var libraries = await Ok(api.NodeLibraryRequest, new { operation = "list" });
            Check(libraries["documents"]![0]!["revision"]!.GetValue<long>() == 2 && Revision(await Read(api, "plug1")) == 2 && Status(await Read(api, "plug1")) == "running",
                "the library or definition after the refusal: " + libraries.ToJsonString());
            Console.WriteLine("PASS entity startup: a library save that breaks a valid definition is refused and leaves the library and definitions as they were");
        }
        finally { await api.UnloadAsync(); }
    }

    // A definition that failed to load because its library names an entity class not known yet loads once Home Assistant
    // pushes the entity classes.
    static async Task Retry()
    {
        await using var home = new EntityTestHome("entity-startup-retry");
        var first = new HassSharpManager(home.Root);
        try
        {
            await first.Init();
            first.SetEntityDescriptions(Fixture("ha-entity-components.json"), started: true);
            await SaveLibrary(first, "lamps", 0, Lamps);
            Check(Status((await SaveDefinition(first, "lamp1", "Lamp one", null, Lamp))["document"]!.AsObject()) == "running", "lamp1 does not run");
        }
        finally { await first.UnloadAsync(); }

        File.Delete(Storage(home, "entity_schema.json"));
        var syncs = home.Syncs("lamp1").Length;
        var api = new HassSharpManager(home.Root);
        try
        {
            await api.Init();
            var failed = await Read(api, "lamp1");
            Check(Status(failed) == "off" && failed["activationError"]?.GetValue<string>().Contains("The node libraries have errors") == true,
                "lamp1 loaded without the entity classes: " + failed.ToJsonString());
            api.SetEntityDescriptions(Fixture("ha-entity-components.json"), started: true);
            Check(api.RetriesQueued == 1, "the push did not retry: " + api.RetriesQueued);
            await api.RetriesDone();
            var lamp1 = await Read(api, "lamp1");
            Check(Status(lamp1) == "running" && lamp1["activationError"] == null && home.Syncs("lamp1").Length == syncs + 1, "lamp1 after the retry: " + lamp1.ToJsonString());
            Console.WriteLine("PASS entity startup: a definition that failed to load for want of the entity classes loads when they are pushed");
        }
        finally { await api.UnloadAsync(); }
    }
}
