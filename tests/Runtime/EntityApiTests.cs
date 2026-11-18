using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;

/// <summary>
/// The entity definition API over a whole manager, with definitions compiled from a saved node library and recording
/// fakes for the entity bridge: the types the libraries declare; save, read, list and preview; each definition running
/// its own instance with its own settings; commands reaching the instance; a failing setup reported, not running and
/// syncing nothing; turning a definition off showing its entities unavailable; and delete removing its entities from
/// Home Assistant, loaded or not.
/// </summary>
static class EntityApiTests
{
    internal const string Plug = "library:shelly:NodeLib.FakePlug";
    const string Broken = "library:shelly:NodeLib.BrokenPlug";
    // Each definition compiles its own assembly, so each has its own Calls; EntityTestHome.Commands() reads them all.
    internal const string Library = """
        public class FakePlug : SharpDevice
        {
            public static readonly System.Collections.Concurrent.ConcurrentQueue<string> Calls = new();
            [Setting] public string Host { get; set; } = "";
            public FakePlug()
            {
                SharpSwitch relay = null!;
                relay = Add(new SharpSwitch("relay")
                {
                    OnTurnOn = () => { relay.IsOn = true; Calls.Enqueue("turn_on " + Host); return Task.CompletedTask; },
                    OnTurnOff = () => { relay.IsOn = false; Calls.Enqueue("turn_off " + Host); return Task.CompletedTask; },
                });
                Add(new SharpSensor("power") { Unit = "W" });
            }
        }
        public class BrokenPlug : SharpDevice
        {
            public BrokenPlug() { Add(new SharpSwitch("relay")); }
            protected override Task Setup() => throw new InvalidOperationException("no plug answers at this host");
        }
        public class StubbornPlug : SharpDevice
        {
            public StubbornPlug() { Add(new SharpSwitch("relay")); }
            protected override Task Start() => throw new InvalidOperationException("the plug refused to start");
        }
        """;
    const string Stubborn = "library:shelly:NodeLib.StubbornPlug";

    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    static bool IsOn(JsonObject snapshot, string uniqueId, bool? on) =>
        snapshot["uniqueId"]!.GetValue<string>() == uniqueId && snapshot["available"]!.GetValue<bool>()
        && JsonNode.DeepEquals(snapshot["values"]!["is_on"], on == null ? null : JsonValue.Create(on.Value));

    static async Task<T> Throws<T>(Func<Task> action, string what) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        catch (Exception error) { throw new Exception($"{what}: threw {error.GetType().Name} instead of {typeof(T).Name}: {error.Message}"); }
        throw new Exception($"{what}: did not throw {typeof(T).Name}");
    }

    internal static object Definition(string name, string? host, string type = Plug) => new
    {
        name,
        settings = host == null ? Array.Empty<object>() : [new { key = "Host", value = host }],
        logic = new { kind = "class", type },
    };

    [RuntimeSuite("--entity-api", Order = 14)]
    public static async Task Run()
    {
        await using var home = new EntityTestHome("entity-api");
        var api = new HassSharpManager(home.Root);
        try
        {
            await api.Init();
            await Scenario(api, home);
        }
        finally { await api.UnloadAsync(); }
    }

    static async Task Scenario(HassSharpManager api, EntityTestHome home)
    {
        async Task<JsonNode> Entity(object request) => JsonNode.Parse(await api.EntityRequest(JsonSerializer.Serialize(request)))!;
        async Task<JsonObject> Ok(object request)
        {
            var answer = await Entity(request);
            Check(answer["ok"]!.GetValue<bool>(), "the request failed: " + answer.ToJsonString());
            return answer["result"]!.AsObject();
        }
        async Task<string> Fails(object request)
        {
            var answer = await Entity(request);
            Check(!answer["ok"]!.GetValue<bool>(), "the request succeeded: " + answer.ToJsonString());
            return answer["error"]!["code"]!.GetValue<string>();
        }
        static string Status(JsonObject document) => document["status"]!.GetValue<string>();

        // An unknown operation is answered as such before any field it might read: with no id, and with an id and no revision.
        foreach (var operation in new object[] { new { operation = "rename" }, new { operation = "rename", id = "plug" } })
        {
            var answer = await Entity(operation);
            Check(answer.ToJsonString() == """{"version":1,"ok":false,"error":{"code":"invalid_request","detail":"Unknown entity operation"}}""", "an unknown operation: " + answer.ToJsonString());
        }

        var library = JsonNode.Parse(await api.NodeLibraryRequest(JsonSerializer.Serialize(new { operation = "save", id = "shelly", name = "Shelly", expectedRevision = 0, source = Library })))!;
        Check(library["ok"]!.GetValue<bool>() && library["result"]!["saved"]!.GetValue<bool>(), "the library saves: " + library.ToJsonString());
        var libraries = JsonNode.Parse(await api.NodeLibraryRequest("""{"operation":"list"}"""))!["result"]!;

        var types = await Ok(new { operation = "types" });
        var plugType = types["types"]!.AsArray().Single(t => t!["id"]!.GetValue<string>() == Plug)!;
        Check(plugType["kind"]!.GetValue<string>() == "device" && plugType["settings"]!.AsArray().Single()!["key"]!.GetValue<string>() == "Host"
            && plugType["settings"]![0]!["type"]!.GetValue<string>() == "string", "types lists FakePlug with its Host setting: " + plugType.ToJsonString());
        Check(types["generation"]!.GetValue<string>() == libraries["generation"]!.GetValue<string>(), "types answers the libraries' generation");
        Console.WriteLine("PASS entity api: types lists the library's classes with their settings and the library generation");

        // A new definition is stored and runs, and Home Assistant hears of its device and entities.
        var saved = await Ok(new { operation = "save", id = "plug1", expectedRevision = 0, definition = Definition("Plug one", "10.0.0.1") });
        var document = saved["document"]!.AsObject();
        Check(saved["saved"]!.GetValue<bool>() && saved["id"]!.GetValue<string>() == "plug1" && saved["active"]!.GetValue<bool>()
            && saved.ContainsKey("activationError") && saved["activationError"] == null, "a valid definition is saved and active: " + saved.ToJsonString());
        Check(document["revision"]!.GetValue<long>() == 1 && Status(document) == "running" && document["currentRevisionActive"]!.GetValue<bool>()
            && document["activationError"] == null && document["automationId"]!.GetValue<string>().Length > 0, "the saved document runs: " + document.ToJsonString());
        var live = document["liveEntities"]!.AsArray();
        Check(live.Select(e => e!["uniqueId"]!.GetValue<string>()).SequenceEqual(["plug1:relay", "plug1:power"])
            && live[0]!.ToJsonString() == """{"uniqueId":"plug1:relay","key":"relay","domain":"switch","name":null,"entityId":"switch.plug1_relay"}""",
            "the live entities: " + live.ToJsonString());
        var synced = JsonNode.Parse(home.Syncs("plug1").Single())!;
        Check(synced["name"]!.GetValue<string>() == "Plug one" && synced["device"]!["name"]!.GetValue<string>() == "Plug one"
            && synced["entities"]!.AsArray().Count == 2, "the sync names the device as the definition: " + synced.ToJsonString());
        Console.WriteLine("PASS entity api: save at revision 0 compiles and runs the definition, syncs its device and answers its live entities");

        // A command reaches the instance with its saved setting, and the new state is pushed.
        await api.EntityCommand("plug1:relay", "turn_on", null);
        Check(EntityTestHome.Commands().Contains("turn_on 10.0.0.1"), "the command ran with the saved Host: " + string.Join(", ", EntityTestHome.Commands()));
        await EntityTestHome.Until(() => home.Pushes().Any(batch => batch.Contains("\"uniqueId\":\"plug1:relay\"") && batch.Contains("\"is_on\":true")), "plug1:relay pushed on");
        await api.EntityCommand("plug1:relay", "turn_off", "");
        await api.EntityCommand("plug1:relay", "turn_off", """{"transition":2}""");
        Check(EntityTestHome.Commands().Count(c => c == "turn_off 10.0.0.1") == 2, "empty and given data reach the handler");
        var unknown = await Throws<InvalidOperationException>(() => api.EntityCommand("plug1:nope", "turn_on", null), "an unknown entity");
        Check(unknown.Message == "The entity is not running", "an unknown entity: " + unknown.Message);
        await Throws<InvalidOperationException>(() => api.EntityCommand("nothing", "turn_on", null), "a unique id without a definition");
        await Throws<NotSupportedException>(() => api.EntityCommand("plug1:relay", "toggle", null), "a command the switch has not");
        Console.WriteLine("PASS entity api: a command reaches the instance with its settings and pushes its state; an unknown entity or command faults");

        // A second definition of the same class runs beside the first with its own setting.
        var second = await Ok(new { operation = "save", id = "plug2", expectedRevision = 0, definition = Definition("Plug two", "10.0.0.2") });
        Check(Status(second["document"]!.AsObject()) == "running" && home.Syncs("plug2").Length == 1, "plug2 runs: " + second.ToJsonString());
        await api.EntityCommand("plug2:relay", "turn_on", null);
        await api.EntityCommand("plug1:relay", "turn_on", null);
        Check(EntityTestHome.Commands().Count(c => c == "turn_on 10.0.0.2") == 1 && EntityTestHome.Commands().Count(c => c == "turn_on 10.0.0.1") == 2, "each definition has its own Host: " + string.Join(", ", EntityTestHome.Commands()));
        Check(Status((await Ok(new { operation = "read", id = "plug1" }))) == "running", "plug1 still runs beside plug2");
        Console.WriteLine("PASS entity api: two definitions of one class run side by side, each with its own settings");

        // A resave whose Setup() now fails leaves the running revision as it is, and says why the new one does not run.
        var batches = home.Pushes().Length;
        var failedResave = await Ok(new { operation = "save", id = "plug2", expectedRevision = 1, definition = Definition("Plug two", null, Broken) });
        var failedDocument = failedResave["document"]!.AsObject();
        Check(failedResave["saved"]!.GetValue<bool>() && !failedResave["active"]!.GetValue<bool>()
            && failedResave["activationError"]?.GetValue<string>().Contains("no plug answers at this host") == true
            && failedDocument["revision"]!.GetValue<long>() == 2 && !failedDocument["currentRevisionActive"]!.GetValue<bool>()
            && failedDocument["active"]!.GetValue<bool>() && Status(failedDocument) == "running" && failedDocument["liveEntities"]!.AsArray().Count == 2,
            "a failed resave: " + failedResave.ToJsonString());
        await api.EntityCommand("plug2:relay", "turn_off", null);
        Check(EntityTestHome.Commands().Contains("turn_off 10.0.0.2") && home.Syncs("plug2").Length == 1, "the old revision of plug2 still takes commands: " + string.Join(", ", EntityTestHome.Commands()));
        await Task.Delay(60);
        Check(!home.Snapshots(batches).Any(s => s["uniqueId"]!.GetValue<string>().StartsWith("plug2:", StringComparison.Ordinal) && !s["available"]!.GetValue<bool>()),
            "a failed resave showed the running entities unavailable");
        Console.WriteLine("PASS entity api: a resave whose Setup() fails keeps the old revision running and answers the error");

        // Saving again needs the current revision; then a new instance replaces the old one.
        Check(await Fails(new { operation = "save", id = "plug1", expectedRevision = 0, definition = Definition("Plug one", "10.0.0.11") }) == "conflict", "a stale revision conflicts");
        Check(home.Syncs("plug1").Length == 1, "a conflicting save synced");
        batches = home.Pushes().Length;
        var resaved = await Ok(new { operation = "save", id = "plug1", expectedRevision = 1, definition = Definition("Plug one", "10.0.0.11") });
        Check(resaved["active"]!.GetValue<bool>() && resaved["document"]!["revision"]!.GetValue<long>() == 2 && Status(resaved["document"]!.AsObject()) == "running"
            && home.Syncs("plug1").Length == 2, "the resave runs and syncs again: " + resaved.ToJsonString());
        // The old instance was on; the new one starts unknown, until the command turns it on.
        await EntityTestHome.Until(() => home.Snapshots(batches).Any(s => IsOn(s, "plug1:relay", null)), "the new instance pushes its first state");
        batches = home.Pushes().Length;
        await api.EntityCommand("plug1:relay", "turn_on", null);
        Check(EntityTestHome.Commands().Contains("turn_on 10.0.0.11"), "the new instance has the new Host");
        await EntityTestHome.Until(() => home.Snapshots(batches).Any(s => IsOn(s, "plug1:relay", true)), "the new instance pushes on");
        var automationId = resaved["document"]!["automationId"]!.GetValue<string>();
        var runNow = Thrown(() => api.RunNow(automationId, "VisualAutomation", "Startup"));
        Check(runNow is InvalidOperationException { Message: "Automation is not active" }, "RunNow refuses a definition: " + runNow);
        Console.WriteLine("PASS entity api: a stale save conflicts; the current revision replaces the instance, which still takes commands");

        // A definition with problems is previewed as invalid and never stored.
        var badDefinition = new { name = "Plug three", settings = new[] { new { key = "Port", value = "80" } }, logic = new { kind = "class", type = Plug } };
        var preview = await Ok(new { operation = "preview", definition = badDefinition });
        Check(!preview["valid"]!.GetValue<bool>() && preview["diagnostics"]!.AsArray().Any(d => d!["code"]!.GetValue<string>() == "definition.unknownSetting")
            && preview.Select(p => p.Key).Order(StringComparer.Ordinal).SequenceEqual(["diagnostics", "source", "sourceMap", "valid"])
            && preview["sourceMap"]!.AsArray().Count == 0, "the preview: " + preview.ToJsonString());
        Check((await Ok(new { operation = "preview", definition = Definition("Plug three", "10.0.0.3") }))["valid"]!.GetValue<bool>(), "a good definition previews valid");
        // A valid preview maps its source as the saved definition's read does: one range per library.
        var previewedPlug = await Ok(new { operation = "preview", definition = Definition("Plug one", "10.0.0.11") });
        var readPlug = await Ok(new { operation = "read", id = "plug1" });
        Check(previewedPlug["sourceMap"]!.AsArray().Count == 1 && JsonNode.DeepEquals(previewedPlug["sourceMap"], readPlug["sourceMap"])
            && previewedPlug["source"]!.GetValue<string>() == readPlug["source"]!.GetValue<string>(),
            "the preview's source map is the saved one's: " + previewedPlug["sourceMap"]!.ToJsonString() + " / " + readPlug["sourceMap"]!.ToJsonString());
        var refused = await Entity(new { operation = "save", id = "plug3", expectedRevision = 0, definition = badDefinition });
        Check(refused["error"]?["code"]?.GetValue<string>() == "validation" && !refused["error"]!["detail"]!["valid"]!.GetValue<bool>()
            && refused["error"]!["detail"]!["diagnostics"]!.AsArray().Any(d => d!["code"]!.GetValue<string>() == "definition.unknownSetting"), "the save is refused: " + refused.ToJsonString());
        Check(await Fails(new { operation = "read", id = "plug3" }) == "not_found" && home.Syncs("plug3").Length == 0, "the refused definition was stored");
        Console.WriteLine("PASS entity api: preview reports an unknown setting and maps a valid source as read does; save refuses it with validation and stores nothing");

        // A setup that throws: saved, not active, the error answered, nothing synced or pushed.
        batches = home.Pushes().Length;
        var broken = await Ok(new { operation = "save", id = "broken", expectedRevision = 0, definition = Definition("Broken plug", null, Broken) });
        Check(broken["saved"]!.GetValue<bool>() && !broken["active"]!.GetValue<bool>()
            && broken["activationError"]?.GetValue<string>().Contains("no plug answers at this host") == true, "a failing setup is reported: " + broken.ToJsonString());
        Check(home.Syncs("broken").Length == 0, "a failing setup synced");
        var readBroken = await Ok(new { operation = "read", id = "broken" });
        Check(Status(readBroken) == "off" && readBroken["liveEntities"]!.AsArray().Count == 0 && !readBroken["active"]!.GetValue<bool>()
            && readBroken["activationError"]!.GetValue<string>() == broken["activationError"]!.GetValue<string>(), "the failed definition reads off: " + readBroken.ToJsonString());
        await Task.Delay(60);
        Check(!home.Snapshots(batches).Any(s => s["uniqueId"]!.GetValue<string>().StartsWith("broken:", StringComparison.Ordinal)), "a definition that never ran pushed states");
        Console.WriteLine("PASS entity api: a failing Setup() saves, answers the error, syncs nothing and reads off");

        // A Start() that throws: loaded, so active, but failed, with the reason in the answer and in a later read.
        var stubborn = await Ok(new { operation = "save", id = "stubborn", expectedRevision = 0, definition = Definition("Stubborn plug", null, Stubborn) });
        Check(stubborn["saved"]!.GetValue<bool>() && stubborn["active"]!.GetValue<bool>() && Status(stubborn["document"]!.AsObject()) == "failed"
            && stubborn["activationError"]?.GetValue<string>().Contains("the plug refused to start") == true
            && stubborn["document"]!["activationError"]?.GetValue<string>() == stubborn["activationError"]!.GetValue<string>(), "a failing Start() is reported: " + stubborn.ToJsonString());
        var readStubborn = await Ok(new { operation = "read", id = "stubborn" });
        Check(Status(readStubborn) == "failed" && readStubborn["active"]!.GetValue<bool>()
            && readStubborn["activationError"]?.GetValue<string>().Contains("the plug refused to start") == true, "a failed start reads with its reason: " + readStubborn.ToJsonString());
        await EntityTestHome.Until(() => home.Snapshots().Any(s => s["uniqueId"]!.GetValue<string>() == "stubborn:relay" && !s["available"]!.GetValue<bool>()), "a failed start pushes its entities unavailable");
        Console.WriteLine("PASS entity api: a failing Start() answers status failed with its reason, from save and from read");

        // list leaves the source out, read has it.
        var listed = (await Entity(new { operation = "list" }))["result"]!.AsArray().Select(d => d!.AsObject()).ToArray();
        Check(listed.Select(d => d["id"]!.GetValue<string>()).SequenceEqual(["broken", "plug1", "plug2", "stubborn"])
            && listed.Single(d => d["id"]!.GetValue<string>() == "stubborn")["activationError"]?.GetValue<string>().Contains("the plug refused to start") == true
            && listed.All(d => !d.ContainsKey("source") && !d.ContainsKey("sourceMap") && d.ContainsKey("status") && d.ContainsKey("liveEntities")),
            "list: " + string.Join(", ", listed.Select(d => d.ToJsonString())));
        var read = await Ok(new { operation = "read", id = "plug1" });
        Check(read["source"]!.GetValue<string>().Contains("new global::NodeLib.FakePlug()") && read.ContainsKey("sourceMap") && read["settings"]![0]!["value"]!.GetValue<string>() == "10.0.0.11",
            "read has the source: " + read.ToJsonString());
        Console.WriteLine("PASS entity api: list omits the source and its map; read includes them");

        // Off unloads and shows the entities unavailable; on runs it again.
        batches = home.Pushes().Length;
        var off = await Ok(new { operation = "set_enabled", id = "plug1", enabled = false });
        Check(off.ToJsonString() == """{"id":"plug1","enabled":false,"active":false,"activationError":null}""", "set_enabled answers: " + off.ToJsonString());
        Check((await Throws<InvalidOperationException>(() => api.EntityCommand("plug1:relay", "turn_on", null), "a command while off")).Message == "The entity is not running", "a command while off");
        await EntityTestHome.Until(() => home.Pushes().Skip(batches).Any(batch => batch.Contains("plug1:relay") && batch.Contains("plug1:power")), "both entities pushed unavailable in one batch");
        var unavailable = home.Pushes().Skip(batches).Single(batch => batch.Contains("plug1:relay"));
        Check(unavailable == """[{"uniqueId":"plug1:relay","available":false,"values":{},"attributes":{}},{"uniqueId":"plug1:power","available":false,"values":{},"attributes":{}}]""",
            "the unavailable batch: " + unavailable);
        var readOff = await Ok(new { operation = "read", id = "plug1" });
        Check(Status(readOff) == "off" && !readOff["enabled"]!.GetValue<bool>() && !readOff["active"]!.GetValue<bool>() && readOff["liveEntities"]!.AsArray().Count == 0
            && readOff["revision"]!.GetValue<long>() == 2, "plug1 reads off: " + readOff.ToJsonString());
        await api.EntityCommand("plug2:relay", "turn_off", null);
        var on = await Ok(new { operation = "set_enabled", id = "plug1", enabled = true });
        Check(on.ToJsonString() == """{"id":"plug1","enabled":true,"active":true,"activationError":null}""" && home.Syncs("plug1").Length == 3, "turning on syncs again: " + on.ToJsonString());
        Check(Status(await Ok(new { operation = "read", id = "plug1" })) == "running", "plug1 runs again");
        await api.EntityCommand("plug1:relay", "turn_on", null);
        var again = await Ok(new { operation = "set_enabled", id = "plug1", enabled = true });
        Check(home.Syncs("plug1").Length == 3, "turning on a running definition replaced it");
        Check(again.ToJsonString() == """{"id":"plug1","enabled":true,"active":true,"activationError":null}""", "turning on a running definition answers: " + again.ToJsonString());
        Console.WriteLine("PASS entity api: set_enabled false unloads and pushes the entities unavailable in one batch; true syncs and runs it again");

        // Turning on a definition whose activation fails says so, as a save does; one whose Start() failed, too.
        await Ok(new { operation = "set_enabled", id = "broken", enabled = false });
        var brokenOn = await Ok(new { operation = "set_enabled", id = "broken", enabled = true });
        Check(!brokenOn["active"]!.GetValue<bool>() && brokenOn["enabled"]!.GetValue<bool>()
            && brokenOn["activationError"]?.GetValue<string>().Contains("no plug answers at this host") == true, "turning on a definition whose Setup() fails: " + brokenOn.ToJsonString());
        var stubbornOn = await Ok(new { operation = "set_enabled", id = "stubborn", enabled = true });
        Check(stubbornOn["active"]!.GetValue<bool>() && stubbornOn["activationError"]?.GetValue<string>().Contains("the plug refused to start") == true,
            "turning on a running definition whose Start() failed: " + stubbornOn.ToJsonString());
        Console.WriteLine("PASS entity api: set_enabled answers whether the definition is active and why its activation failed");

        // A saved definition keeps its switch through a save: enabled is refused there, and plug1 runs on.
        Check(await Fails(new { operation = "save", id = "plug1", expectedRevision = 2, enabled = false, definition = Definition("Plug one", "10.0.0.11") }) == "invalid_request"
            && Status(await Ok(new { operation = "read", id = "plug1" })) == "running", "a save with enabled of a saved definition");
        // Activating a revision that is off, as a library rebuild may, unloads the running one and shows its entities unavailable.
        batches = home.Pushes().Length;
        var stored = JsonSerializer.Deserialize<EntityDefinitionDocument>((await Ok(new { operation = "read", id = "plug1" })).ToJsonString(), VisualStore.Json)!;
        var offAnswer = JsonSerializer.SerializeToNode(await api.ActivateDefinition(stored with { Enabled = false }), VisualStore.Json)!;
        Check(!offAnswer["active"]!.GetValue<bool>() && offAnswer["activationError"] == null && offAnswer["document"]!["status"]!.GetValue<string>() == "off",
            "activating a revision that is off: " + offAnswer.ToJsonString());
        Check((await Throws<InvalidOperationException>(() => api.EntityCommand("plug1:relay", "turn_on", null), "a command once off")).Message == "The entity is not running", "a command once off");
        await EntityTestHome.Until(() => home.Snapshots(batches).Count(s => s["uniqueId"]!.GetValue<string>().StartsWith("plug1:", StringComparison.Ordinal) && !s["available"]!.GetValue<bool>()) == 2,
            "both entities pushed unavailable");
        Check(Status(await Ok(new { operation = "read", id = "plug1" })) == "off", "plug1 reads off");
        await Ok(new { operation = "set_enabled", id = "plug1", enabled = true });
        Check(Status(await Ok(new { operation = "read", id = "plug1" })) == "running" && home.Syncs("plug1").Length == 4, "plug1 runs again");
        Console.WriteLine("PASS entity api: a save cannot turn a saved definition off; activating an off revision unloads it and pushes its entities unavailable");

        // A new definition saved off is stored and never runs; deleting it still removes its entities from Home Assistant.
        var dormant = await Ok(new { operation = "save", id = "plug4", expectedRevision = 0, enabled = false, definition = Definition("Plug four", "10.0.0.4") });
        Check(dormant["saved"]!.GetValue<bool>() && !dormant["active"]!.GetValue<bool>() && dormant["activationError"] == null
            && !dormant["document"]!["enabled"]!.GetValue<bool>() && Status(dormant["document"]!.AsObject()) == "off" && home.Syncs("plug4").Length == 0, "a definition saved off: " + dormant.ToJsonString());
        var resavedOff = await Ok(new { operation = "save", id = "plug4", expectedRevision = 1, definition = Definition("Plug four", "10.0.0.44") });
        Check(!resavedOff["active"]!.GetValue<bool>() && Status(resavedOff["document"]!.AsObject()) == "off" && home.Syncs("plug4").Length == 0, "a save of an off definition ran it: " + resavedOff.ToJsonString());
        Check((await Ok(new { operation = "delete", id = "plug4", expectedRevision = 2 }))["deleted"]!.GetValue<bool>() && home.Removals().Contains("plug4"), "deleting an unloaded definition removes its entities");
        Console.WriteLine("PASS entity api: a definition saved off never runs, and deleting it still removes its entities");

        // Delete checks the revision, unloads and removes the entities; a failing removal is logged.
        Check(await Fails(new { operation = "delete", id = "plug1", expectedRevision = 1 }) == "conflict" && !home.Removals().Contains("plug1"), "a stale delete");
        await api.EntityCommand("plug1:relay", "turn_off", null);
        Check((await Ok(new { operation = "delete", id = "plug1", expectedRevision = 2 })).ToJsonString() == """{"deleted":true}""", "delete answers deleted");
        Check(home.Removals().Contains("plug1") && await Fails(new { operation = "read", id = "plug1" }) == "not_found", "plug1 is gone from the store and Home Assistant");
        await Throws<InvalidOperationException>(() => api.EntityCommand("plug1:relay", "turn_on", null), "a command after delete");
        home.FailRemoval = true;
        Check((await Ok(new { operation = "delete", id = "plug2", expectedRevision = 2 }))["deleted"]!.GetValue<bool>(), "a failing removal still deletes");
        Check(home.Logs().Any(line => line.Contains("plug2") && line.Contains("the registry is busy")), "the failing removal is logged: " + string.Join(" | ", home.Logs()));
        // A removal Python never answers is waited for only so long: the delete answers, and the next request is served.
        home.FailRemoval = false; home.IgnoreRemoval = true;
        HassSharpManager.RemovalTimeout = TimeSpan.FromMilliseconds(200);
        var unanswered = await Entity(new { operation = "delete", id = "stubborn", expectedRevision = 1 }).WaitAsync(TimeSpan.FromSeconds(5));
        Check(unanswered["result"]?["deleted"]?.GetValue<bool>() == true && home.Removals().Contains("stubborn"), "an unanswered removal still deletes: " + unanswered.ToJsonString());
        Check(home.Logs().Any(line => line.Contains("stubborn") && line.Contains("did not answer")), "the unanswered removal is logged: " + string.Join(" | ", home.Logs()));
        Check((await Entity(new { operation = "list" }).WaitAsync(TimeSpan.FromSeconds(5)))["result"]!.AsArray().Count == 1, "the next request is served");
        Check(await Fails(new { operation = "delete", id = "nothing", expectedRevision = 1 }) == "not_found" && await Fails(new { operation = "dance" }) == "invalid_request"
            && await Fails(new { operation = "read", id = "Bad Id" }) == "invalid_request", "error codes");
        Console.WriteLine("PASS entity api: delete checks the revision, unloads, removes the entities, and logs a removal that fails or is never answered");
    }

    static Exception? Thrown(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }
}
