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
    const string Plug = "library:shelly:NodeLib.FakePlug";
    const string Broken = "library:shelly:NodeLib.BrokenPlug";
    // Each definition compiles its own assembly, so each has its own Calls; Commands() reads them all.
    const string Library = """
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
        """;

    static readonly List<string> Synced = [], Pushed = [], Removed = [], Logged = [];
    static volatile bool failRemoval;

    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    static string[] Syncs(string definitionId)
    {
        lock (Synced) return Synced.Where(json => JsonNode.Parse(json)!["definitionId"]!.GetValue<string>() == definitionId).ToArray();
    }
    static string[] Pushes() { lock (Pushed) return [.. Pushed]; }
    static string[] Removals() { lock (Removed) return [.. Removed]; }
    static string[] Logs() { lock (Logged) return [.. Logged]; }

    // Every snapshot pushed in the batches from the given one on, in order.
    static JsonObject[] Snapshots(int fromBatch = 0) =>
        Pushes().Skip(fromBatch).SelectMany(batch => JsonNode.Parse(batch)!.AsArray()).Select(node => node!.AsObject()).ToArray();

    static bool IsOn(JsonObject snapshot, string uniqueId, bool? on) =>
        snapshot["uniqueId"]!.GetValue<string>() == uniqueId && snapshot["available"]!.GetValue<bool>()
        && JsonNode.DeepEquals(snapshot["values"]!["is_on"], on == null ? null : JsonValue.Create(on.Value));

    // What every FakePlug instance's handlers recorded, from every compiled assembly still loaded.
    static string[] Commands() => AppDomain.CurrentDomain.GetAssemblies()
        .Select(assembly => assembly.GetType("NodeLib.FakePlug", false)).OfType<Type>()
        .SelectMany(type => (IEnumerable<string>)type.GetField("Calls")!.GetValue(null)!).ToArray();

    // Python's answer to a sync: an entity id for each unique id, as "<domain>.<definition>_<key>".
    static string Answer(string json)
    {
        var definition = JsonNode.Parse(json)!;
        var entities = new JsonObject();
        foreach (var entity in definition["entities"]!.AsArray())
            entities[entity!["uniqueId"]!.GetValue<string>()] = new JsonObject { ["entityId"] = $"{entity["domain"]}.{definition["definitionId"]}_{entity["key"]}" };
        return new JsonObject { ["entities"] = entities }.ToJsonString();
    }

    static async Task Until(Func<bool> condition, string what)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Check(clock.Elapsed < TimeSpan.FromSeconds(5), "timed out waiting: " + what);
            await Task.Delay(5);
        }
    }

    static async Task<T> Throws<T>(Func<Task> action, string what) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        catch (Exception error) { throw new Exception($"{what}: threw {error.GetType().Name} instead of {typeof(T).Name}: {error.Message}"); }
        throw new Exception($"{what}: did not throw {typeof(T).Name}");
    }

    static object Definition(string name, string? host, string type = Plug) => new
    {
        name,
        settings = host == null ? Array.Empty<object>() : [new { key = "Host", value = host }],
        logic = new { kind = "class", type },
    };

    [RuntimeSuite("--entity-api", Order = 14)]
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "entity-api-" + Guid.NewGuid());
        var previous = HassPath.HassConfiguration;
        var (sync, push, remove, log) = (PyInterop.BeginSyncEntities, PyInterop.PushEntityStates, PyInterop.BeginRemoveEntities, PyInterop.Log);
        PyInterop.BeginSyncEntities = (json, ok, _) => { lock (Synced) Synced.Add(json); ok(Answer(json)); };
        PyInterop.PushEntityStates = batch => { lock (Pushed) Pushed.Add(batch); };
        PyInterop.BeginRemoveEntities = (id, ok, fail) =>
        {
            lock (Removed) Removed.Add(id);
            if (failRemoval) fail("the registry is busy"); else ok();
        };
        PyInterop.Log = (_, message) => { lock (Logged) Logged.Add(message); };
        HassPath.HassConfiguration = root;
        var api = new HassSharpManager(root);
        try
        {
            await api.Init();
            await Scenario(api);
        }
        finally
        {
            await api.UnloadAsync();
            PyInterop.BeginSyncEntities = sync;
            PyInterop.PushEntityStates = push;
            PyInterop.BeginRemoveEntities = remove;
            PyInterop.Log = log;
            HassPath.HassConfiguration = previous;
            failRemoval = false;
            lock (Synced) Synced.Clear();
            lock (Pushed) Pushed.Clear();
            lock (Removed) Removed.Clear();
            lock (Logged) Logged.Clear();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    static async Task Scenario(HassSharpManager api)
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
        var synced = JsonNode.Parse(Syncs("plug1").Single())!;
        Check(synced["name"]!.GetValue<string>() == "Plug one" && synced["device"]!["name"]!.GetValue<string>() == "Plug one"
            && synced["entities"]!.AsArray().Count == 2, "the sync names the device as the definition: " + synced.ToJsonString());
        Console.WriteLine("PASS entity api: save at revision 0 compiles and runs the definition, syncs its device and answers its live entities");

        // A command reaches the instance with its saved setting, and the new state is pushed.
        await api.EntityCommand("plug1:relay", "turn_on", null);
        Check(Commands().Contains("turn_on 10.0.0.1"), "the command ran with the saved Host: " + string.Join(", ", Commands()));
        await Until(() => Pushes().Any(batch => batch.Contains("\"uniqueId\":\"plug1:relay\"") && batch.Contains("\"is_on\":true")), "plug1:relay pushed on");
        await api.EntityCommand("plug1:relay", "turn_off", "");
        await api.EntityCommand("plug1:relay", "turn_off", """{"transition":2}""");
        Check(Commands().Count(c => c == "turn_off 10.0.0.1") == 2, "empty and given data reach the handler");
        var unknown = await Throws<InvalidOperationException>(() => api.EntityCommand("plug1:nope", "turn_on", null), "an unknown entity");
        Check(unknown.Message == "The entity is not running", "an unknown entity: " + unknown.Message);
        await Throws<InvalidOperationException>(() => api.EntityCommand("nothing", "turn_on", null), "a unique id without a definition");
        await Throws<NotSupportedException>(() => api.EntityCommand("plug1:relay", "toggle", null), "a command the switch has not");
        Console.WriteLine("PASS entity api: a command reaches the instance with its settings and pushes its state; an unknown entity or command faults");

        // A second definition of the same class runs beside the first with its own setting.
        var second = await Ok(new { operation = "save", id = "plug2", expectedRevision = 0, definition = Definition("Plug two", "10.0.0.2") });
        Check(Status(second["document"]!.AsObject()) == "running" && Syncs("plug2").Length == 1, "plug2 runs: " + second.ToJsonString());
        await api.EntityCommand("plug2:relay", "turn_on", null);
        await api.EntityCommand("plug1:relay", "turn_on", null);
        Check(Commands().Count(c => c == "turn_on 10.0.0.2") == 1 && Commands().Count(c => c == "turn_on 10.0.0.1") == 2, "each definition has its own Host: " + string.Join(", ", Commands()));
        Check(Status((await Ok(new { operation = "read", id = "plug1" }))) == "running", "plug1 still runs beside plug2");
        Console.WriteLine("PASS entity api: two definitions of one class run side by side, each with its own settings");

        // Saving again needs the current revision; then a new instance replaces the old one.
        Check(await Fails(new { operation = "save", id = "plug1", expectedRevision = 0, definition = Definition("Plug one", "10.0.0.11") }) == "conflict", "a stale revision conflicts");
        Check(Syncs("plug1").Length == 1, "a conflicting save synced");
        var batches = Pushes().Length;
        var resaved = await Ok(new { operation = "save", id = "plug1", expectedRevision = 1, definition = Definition("Plug one", "10.0.0.11") });
        Check(resaved["active"]!.GetValue<bool>() && resaved["document"]!["revision"]!.GetValue<long>() == 2 && Status(resaved["document"]!.AsObject()) == "running"
            && Syncs("plug1").Length == 2, "the resave runs and syncs again: " + resaved.ToJsonString());
        // The old instance was on; the new one starts unknown, until the command turns it on.
        await Until(() => Snapshots(batches).Any(s => IsOn(s, "plug1:relay", null)), "the new instance pushes its first state");
        batches = Pushes().Length;
        await api.EntityCommand("plug1:relay", "turn_on", null);
        Check(Commands().Contains("turn_on 10.0.0.11"), "the new instance has the new Host");
        await Until(() => Snapshots(batches).Any(s => IsOn(s, "plug1:relay", true)), "the new instance pushes on");
        var automationId = resaved["document"]!["automationId"]!.GetValue<string>();
        var runNow = Thrown(() => api.RunNow(automationId, "VisualAutomation", "Startup"));
        Check(runNow is InvalidOperationException { Message: "Automation is not active" }, "RunNow refuses a definition: " + runNow);
        Console.WriteLine("PASS entity api: a stale save conflicts; the current revision replaces the instance, which still takes commands");

        // A definition with problems is previewed as invalid and never stored.
        var badDefinition = new { name = "Plug three", settings = new[] { new { key = "Port", value = "80" } }, logic = new { kind = "class", type = Plug } };
        var preview = await Ok(new { operation = "preview", definition = badDefinition });
        Check(!preview["valid"]!.GetValue<bool>() && preview["diagnostics"]!.AsArray().Any(d => d!["code"]!.GetValue<string>() == "definition.unknownSetting")
            && preview.Select(p => p.Key).Order(StringComparer.Ordinal).SequenceEqual(["diagnostics", "source", "valid"]), "the preview: " + preview.ToJsonString());
        Check((await Ok(new { operation = "preview", definition = Definition("Plug three", "10.0.0.3") }))["valid"]!.GetValue<bool>(), "a good definition previews valid");
        var refused = await Entity(new { operation = "save", id = "plug3", expectedRevision = 0, definition = badDefinition });
        Check(refused["error"]?["code"]?.GetValue<string>() == "validation" && !refused["error"]!["detail"]!["valid"]!.GetValue<bool>()
            && refused["error"]!["detail"]!["diagnostics"]!.AsArray().Any(d => d!["code"]!.GetValue<string>() == "definition.unknownSetting"), "the save is refused: " + refused.ToJsonString());
        Check(await Fails(new { operation = "read", id = "plug3" }) == "not_found" && Syncs("plug3").Length == 0, "the refused definition was stored");
        Console.WriteLine("PASS entity api: preview reports an unknown setting; save refuses it with validation and stores nothing");

        // A setup that throws: saved, not active, the error answered, nothing synced or pushed.
        batches = Pushes().Length;
        var broken = await Ok(new { operation = "save", id = "broken", expectedRevision = 0, definition = Definition("Broken plug", null, Broken) });
        Check(broken["saved"]!.GetValue<bool>() && !broken["active"]!.GetValue<bool>()
            && broken["activationError"]?.GetValue<string>().Contains("no plug answers at this host") == true, "a failing setup is reported: " + broken.ToJsonString());
        Check(Syncs("broken").Length == 0, "a failing setup synced");
        var readBroken = await Ok(new { operation = "read", id = "broken" });
        Check(Status(readBroken) == "off" && readBroken["liveEntities"]!.AsArray().Count == 0 && !readBroken["active"]!.GetValue<bool>()
            && readBroken["activationError"]!.GetValue<string>() == broken["activationError"]!.GetValue<string>(), "the failed definition reads off: " + readBroken.ToJsonString());
        await Task.Delay(60);
        Check(!Snapshots(batches).Any(s => s["uniqueId"]!.GetValue<string>().StartsWith("broken:", StringComparison.Ordinal)), "a definition that never ran pushed states");
        Console.WriteLine("PASS entity api: a failing Setup() saves, answers the error, syncs nothing and reads off");

        // list leaves the source out, read has it.
        var listed = (await Entity(new { operation = "list" }))["result"]!.AsArray().Select(d => d!.AsObject()).ToArray();
        Check(listed.Select(d => d["id"]!.GetValue<string>()).SequenceEqual(["broken", "plug1", "plug2"])
            && listed.All(d => !d.ContainsKey("source") && !d.ContainsKey("sourceMap") && d.ContainsKey("status") && d.ContainsKey("liveEntities")),
            "list: " + string.Join(", ", listed.Select(d => d.ToJsonString())));
        var read = await Ok(new { operation = "read", id = "plug1" });
        Check(read["source"]!.GetValue<string>().Contains("new global::NodeLib.FakePlug()") && read.ContainsKey("sourceMap") && read["settings"]![0]!["value"]!.GetValue<string>() == "10.0.0.11",
            "read has the source: " + read.ToJsonString());
        Console.WriteLine("PASS entity api: list omits the source and its map; read includes them");

        // Off unloads and shows the entities unavailable; on runs it again.
        batches = Pushes().Length;
        var off = await Ok(new { operation = "set_enabled", id = "plug1", enabled = false });
        Check(off.ToJsonString() == """{"id":"plug1","enabled":false}""", "set_enabled answers: " + off.ToJsonString());
        Check((await Throws<InvalidOperationException>(() => api.EntityCommand("plug1:relay", "turn_on", null), "a command while off")).Message == "The entity is not running", "a command while off");
        await Until(() => Pushes().Skip(batches).Any(batch => batch.Contains("plug1:relay") && batch.Contains("plug1:power")), "both entities pushed unavailable in one batch");
        var unavailable = Pushes().Skip(batches).Single(batch => batch.Contains("plug1:relay"));
        Check(unavailable == """[{"uniqueId":"plug1:relay","available":false,"values":{},"attributes":{}},{"uniqueId":"plug1:power","available":false,"values":{},"attributes":{}}]""",
            "the unavailable batch: " + unavailable);
        var readOff = await Ok(new { operation = "read", id = "plug1" });
        Check(Status(readOff) == "off" && !readOff["enabled"]!.GetValue<bool>() && !readOff["active"]!.GetValue<bool>() && readOff["liveEntities"]!.AsArray().Count == 0
            && readOff["revision"]!.GetValue<long>() == 2, "plug1 reads off: " + readOff.ToJsonString());
        await api.EntityCommand("plug2:relay", "turn_off", null);
        var on = await Ok(new { operation = "set_enabled", id = "plug1", enabled = true });
        Check(on.ToJsonString() == """{"id":"plug1","enabled":true}""" && Syncs("plug1").Length == 3, "turning on syncs again: " + on.ToJsonString());
        Check(Status(await Ok(new { operation = "read", id = "plug1" })) == "running", "plug1 runs again");
        await api.EntityCommand("plug1:relay", "turn_on", null);
        await Ok(new { operation = "set_enabled", id = "plug1", enabled = true });
        Check(Syncs("plug1").Length == 3, "turning on a running definition replaced it");
        Console.WriteLine("PASS entity api: set_enabled false unloads and pushes the entities unavailable in one batch; true syncs and runs it again");

        // A new definition saved off is stored and never runs; deleting it still removes its entities from Home Assistant.
        var dormant = await Ok(new { operation = "save", id = "plug4", expectedRevision = 0, enabled = false, definition = Definition("Plug four", "10.0.0.4") });
        Check(dormant["saved"]!.GetValue<bool>() && !dormant["active"]!.GetValue<bool>() && dormant["activationError"] == null
            && !dormant["document"]!["enabled"]!.GetValue<bool>() && Status(dormant["document"]!.AsObject()) == "off" && Syncs("plug4").Length == 0, "a definition saved off: " + dormant.ToJsonString());
        var resavedOff = await Ok(new { operation = "save", id = "plug4", expectedRevision = 1, definition = Definition("Plug four", "10.0.0.44") });
        Check(!resavedOff["active"]!.GetValue<bool>() && Status(resavedOff["document"]!.AsObject()) == "off" && Syncs("plug4").Length == 0, "a save of an off definition ran it: " + resavedOff.ToJsonString());
        Check((await Ok(new { operation = "delete", id = "plug4", expectedRevision = 2 }))["deleted"]!.GetValue<bool>() && Removals().Contains("plug4"), "deleting an unloaded definition removes its entities");
        Console.WriteLine("PASS entity api: a definition saved off never runs, and deleting it still removes its entities");

        // Delete checks the revision, unloads and removes the entities; a failing removal is logged.
        Check(await Fails(new { operation = "delete", id = "plug1", expectedRevision = 1 }) == "conflict" && !Removals().Contains("plug1"), "a stale delete");
        await api.EntityCommand("plug1:relay", "turn_off", null);
        Check((await Ok(new { operation = "delete", id = "plug1", expectedRevision = 2 })).ToJsonString() == """{"deleted":true}""", "delete answers deleted");
        Check(Removals().Contains("plug1") && await Fails(new { operation = "read", id = "plug1" }) == "not_found", "plug1 is gone from the store and Home Assistant");
        await Throws<InvalidOperationException>(() => api.EntityCommand("plug1:relay", "turn_on", null), "a command after delete");
        failRemoval = true;
        Check((await Ok(new { operation = "delete", id = "plug2", expectedRevision = 1 }))["deleted"]!.GetValue<bool>(), "a failing removal still deletes");
        Check(Logs().Any(line => line.Contains("plug2") && line.Contains("the registry is busy")), "the failing removal is logged: " + string.Join(" | ", Logs()));
        Check(await Fails(new { operation = "delete", id = "nothing", expectedRevision = 1 }) == "not_found" && await Fails(new { operation = "dance" }) == "invalid_request"
            && await Fails(new { operation = "read", id = "Bad Id" }) == "invalid_request", "error codes");
        Console.WriteLine("PASS entity api: delete checks the revision, unloads, removes the entities, and logs a failing removal");
    }

    static Exception? Thrown(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }
}
