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
            await DerivesCustomPins(api);
            await RunsATriggerStep(api, home);
            await AListAndAnOlderDocumentAreRefusedInWords(api, home);
        }
        finally { await api.UnloadAsync(); }
    }

    /// <summary>
    /// A graph definition that lists its entities is refused by preview and save with the one sentence about
    /// <c>entities</c>. A version 2 graph document from an earlier development build (phase 2) is skipped by list (logged once, with
    /// the sentence), its read answers the sentence as its error, and delete at revision 0 removes it.
    /// </summary>
    static async Task AListAndAnOlderDocumentAreRefusedInWords(HassSharpManager api, EntityTestHome home)
    {
        const string listed = "Entities are Entity nodes in the graph now: leave entities out and add one Entity node for each";
        const string earlier = "saved by an earlier development build; delete it and build it again";
        async Task<JsonNode> Entity(object request) => JsonNode.Parse(await api.EntityRequest(JsonSerializer.Serialize(request)))!;
        JsonObject Door() => GraphDefinition("Door", Graph("Door", []), ("door", "binary_sensor"));
        foreach (var entities in new[] { "[]", """[{"key":"door","domain":"binary_sensor"}]""", "null" })
        {
            var definition = Door();
            definition["entities"] = JsonNode.Parse(entities);
            var preview = (await Entity(new { operation = "preview", definition }))["result"]!;
            var problems = preview["diagnostics"]!.AsArray().Select(d => $"{d!["code"]} {d["inputId"]} {d["message"]}").ToArray();
            Check(!preview["valid"]!.GetValue<bool>() && problems.SequenceEqual([$"definition.entitiesNotAllowed entities {listed}"]), $"a preview with entities {entities}: " + preview.ToJsonString());
            var save = await Entity(new { operation = "save", id = "door", expectedRevision = 0, definition });
            Check(save["error"]?["code"]?.GetValue<string>() == "validation" && save["error"]!["detail"]!["diagnostics"]!.AsArray().Single()!["message"]!.GetValue<string>() == listed,
                $"a save with entities {entities}: " + save.ToJsonString());
        }
        var saved = await Entity(new { operation = "save", id = "door", expectedRevision = 0, definition = Door() });
        Check(saved["ok"]!.GetValue<bool>(), "without the key it saves: " + saved.ToJsonString());
        // The read and list answers carry its devices (none: it has no Device node) and the device each entity is on.
        var current = (await Entity(new { operation = "read", id = "door" }))["result"]!;
        var summary = (await Entity(new { operation = "list" }))["result"]!.AsArray().Single(d => d!["id"]!.GetValue<string>() == "door")!;
        foreach (var (what, given) in new[] { ("read", current), ("list", summary) })
            Check(given["version"]!.GetValue<int>() == 3 && given["devices"] is JsonArray { Count: 0 } && given["entities"]![0]!.AsObject().TryGetPropertyValue("device", out var on) && on == null,
                $"its {what} answer is version 3, lists no devices, and its entity is on none: " + given.ToJsonString());

        // A phase 2 build's graph document is version 2.
        var path = Path.Combine(home.Root, ".storage", "hass_sharp", "entities", "door.json");
        var file = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        file["version"] = 2; file.Remove("devices");
        File.WriteAllText(path, file.ToJsonString());
        var list = (await Entity(new { operation = "list" }))["result"]!.AsArray();
        Check(!list.Any(d => d!["id"]!.GetValue<string>() == "door"), "list leaves it out: " + list.ToJsonString());
        await Entity(new { operation = "list" });
        Check(home.Logs().Count(l => l == $"Skipping entity definition {Path.GetFullPath(path)}: {earlier}") == 1, "it is logged once with the sentence: " + string.Join(" / ", home.Logs().Where(l => l.Contains("door.json", StringComparison.Ordinal))));
        var read = await Entity(new { operation = "read", id = "door" });
        Check(read.ToJsonString() == JsonSerializer.Serialize(new { version = 1, ok = false, error = new { code = "invalid_request", detail = earlier } }), "its read answers the sentence: " + read.ToJsonString());
        // The list names it apart from the readable ones, with why; so it does any other file it cannot read, in Read's words.
        var broken = Path.Combine(home.Root, ".storage", "hass_sharp", "entities", "broken.json");
        File.WriteAllText(broken, """{"version":2}""");
        // A file whose name is not a definition id cannot be deleted through the API: the list says so.
        var hall = Path.Combine(home.Root, ".storage", "hass_sharp", "entities", "Hall.json");
        File.WriteAllText(hall, """{"version":2}""");
        var answer = await Entity(new { operation = "list" });
        Check(answer["result"] is JsonArray && !answer["result"]!.AsArray().Any(d => d!["id"]!.GetValue<string>() is "door" or "broken" or "Hall"), "the readable ones are the result: " + answer.ToJsonString());
        Check(JsonNode.DeepEquals(answer["unreadable"], JsonNode.Parse($$"""[{"id":"Hall","reason":"Invalid definition ID","deletable":false},{"id":"broken","reason":"Invalid entity definition envelope","deletable":true},{"id":"door","reason":"{{earlier}}","deletable":true}]""")),
            "the list names each unreadable file with its reason and whether a delete can remove it: " + answer["unreadable"]?.ToJsonString());
        var refused = await Entity(new { operation = "delete", id = "Hall", expectedRevision = 0 });
        Check(refused["ok"]?.GetValue<bool>() == false && File.Exists(hall), "a delete of it is refused, as the list says: " + refused.ToJsonString());
        File.Delete(hall);
        var homeView = await Entity(new { operation = "list", view = "home", todayUtc = "2026-11-20T00:00:00Z" });
        Check(homeView["unreadable"]!.AsArray().Count == 2, "the entities page's list names them too: " + homeView["unreadable"]?.ToJsonString());
        var removals = home.Removals().Length;
        var deleted = await Entity(new { operation = "delete", id = "door", expectedRevision = 0 });
        Check(deleted["ok"]!.GetValue<bool>() && !File.Exists(path), "delete at revision 0 removes it: " + deleted.ToJsonString());
        Check(home.Removals().Length == removals + 1 && home.Removals()[^1] == "door", "and its entities from Home Assistant: " + string.Join(", ", home.Removals()));
        await Entity(new { operation = "delete", id = "broken", expectedRevision = 0 });
        var after = await Entity(new { operation = "list" });
        Check(after["unreadable"] is JsonArray { Count: 0 }, "with both deleted, the list names none: " + after.ToJsonString());
        Console.WriteLine("PASS entity api: a listed entities key is refused in words, and a version 2 graph document is skipped, listed apart with why, answered with why, and deleted at revision 0 with its entities");
    }

    static JsonObject N(string id, string kind, object? config = null) => VisualExecutionTests.N(id, kind, config);
    static JsonObject Lit(string type, object? value) => VisualExecutionTests.Lit(type, value);
    static JsonObject Next(string source, string target) { var wire = VisualExecutionTests.E(source, target); wire["id"] = $"{source}:next:{target}"; return wire; }
    static JsonObject Data(string source, string target, string targetPort, string sourcePort = "value")
    { var wire = VisualExecutionTests.D(source, target, targetPort, sourcePort); wire["id"] = $"{source}.{sourcePort}:{target}.{targetPort}"; return wire; }
    static JsonObject Graph(string name, JsonObject[] nodes, params JsonObject[] edges) =>
        new() { ["version"] = 5, ["name"] = name, ["nodes"] = new JsonArray(nodes), ["edges"] = new JsonArray(edges) };
    /// <summary>A graph definition with an Entity node, of the key's id and named by its key, for each of <paramref name="entities"/>.</summary>
    static JsonObject GraphDefinition(string name, JsonObject graph, params (string Key, string Domain)[] entities)
    {
        foreach (var (key, domain) in entities) graph["nodes"]!.AsArray().Add(VisualExecutionTests.Hub(key, key, domain));
        return new() { ["name"] = name, ["settings"] = new JsonArray(), ["logic"] = new JsonObject { ["kind"] = "graph", ["graph"] = graph } };
    }

    /// <summary>
    /// <c>run</c> runs a graph definition's Trigger step by hand, as Run now runs an automation's: answered with a run id
    /// once the run is recorded (also when the step records no runs of its own), which sets the step's state and has a
    /// trace. Refused, in words, for an unknown definition, a step that is no Trigger, a Trigger closed to manual runs, a
    /// definition that is off, one whose saved revision does not run, and a class definition. An automation of the same
    /// id is another document: each run lands in its own history.
    /// </summary>
    static async Task RunsATriggerStep(HassSharpManager api, EntityTestHome home)
    {
        async Task<JsonNode> Entity(object request) => JsonNode.Parse(await api.EntityRequest(JsonSerializer.Serialize(request)))!;
        async Task<JsonObject> Ok(object request)
        {
            var answer = await Entity(request);
            Check(answer["ok"]!.GetValue<bool>(), "the request failed: " + answer.ToJsonString());
            return answer["result"]!.AsObject();
        }
        async Task Refused(object request, string code, string detail, string what)
        {
            var answer = await Entity(request);
            Check(answer.ToJsonString() == JsonSerializer.Serialize(new { version = 1, ok = false, error = new { code, detail } }), what + ": " + answer.ToJsonString());
        }
        JsonObject[] History(string automationId) => JsonNode.Parse(api.GetRunHistory(automationId, 0, 100, null))!["runs"]!.AsArray().Select(r => r!.AsObject()).ToArray();
        var scripts = (UserScriptManager)typeof(HassSharpManager).GetField("_userScriptManager", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(api)!;
        Task Flushed() => scripts.Pump.FlushAsync();

        // A manual Trigger that records no runs of its own and sets the sensor, one closed to manual runs, and, when asked, a
        // When started flow that fails (a Convert step that cannot convert its text).
        JsonObject Button(bool failingSetup = false)
        {
            var press = N("press", "trigger", new { type = "manual", trackHistory = false });
            press["label"] = "Press";
            JsonObject[] nodes = [press, N("set", "set_entity_state", new JsonObject { ["entity"] = "last", ["value"] = Lit("string", "pressed") }),
                N("closed", "trigger", new { type = "manual", manual = false }), N("say", "log", new { value = Lit("string", "closed") })];
            JsonObject[] edges = [Next("press", "set"), Next("closed", "say")];
            if (failingSetup)
                (nodes, edges) = ([.. nodes, N("start", "entity_setup"), N("conv", "convert", new { type = "number", value = Lit("string", "warm") }),
                    N("first", "set_entity_state", new JsonObject { ["entity"] = "last" })], [.. edges, Next("start", "first"), Data("conv", "first", "value")]);
            return GraphDefinition("Button", Graph("Button", nodes, edges), ("last", "sensor"));
        }
        var saved = await Ok(new { operation = "save", id = "button", expectedRevision = 0, definition = Button() });
        Check(saved["document"]!["status"]!.GetValue<string>() == "running", "the button runs: " + saved.ToJsonString());
        var automationId = saved["document"]!["automationId"]!.GetValue<string>();

        var ran = await Ok(new { operation = "run", id = "button", triggerId = "press" });
        Check(ran.Select(p => p.Key).Order(StringComparer.Ordinal).SequenceEqual(["runId", "version"]) && ran["version"]!.GetValue<int>() == 1, "run answers a run id: " + ran.ToJsonString());
        var runId = ran["runId"]!.GetValue<string>();
        await EntityTestHome.Until(() => History(automationId).Any(r => r["runId"]!.GetValue<string>() == runId && r["status"]!.GetValue<string>() == "Succeeded"), "the run ends");
        await Flushed();
        Check(home.Snapshots().Any(s => s["uniqueId"]!.GetValue<string>() == "button:last" && s["values"]?["value"]?.GetValue<string>() == "pressed"), "the run set the sensor's state");
        Check(JsonNode.Parse(api.GetRunTrace(runId)) is { } trace && trace["run"]!["cause"]!.GetValue<string>() == "Manual" && trace["run"]!["triggerNodeId"]!.GetValue<string>() == "press"
            && trace["run"]!["visualId"]!.GetValue<string>() == "button" && trace["trace"] != null, "the run is recorded with its trace, though its step records none of its own: " + api.GetRunTrace(runId));
        Check(History(automationId).Length == 1, "only the manual run is recorded: " + string.Join(" | ", History(automationId).Select(r => r.ToJsonString())));

        await Refused(new { operation = "run", id = "nothing", triggerId = "press" }, "not_found", "Entity definition not found", "an unknown definition");
        await Refused(new { operation = "run", id = "button", triggerId = "set" }, "invalid_request", "Only a Trigger step can be run by hand; Set state is not one", "a step that is no Trigger");
        await Refused(new { operation = "run", id = "button", triggerId = "nope" }, "invalid_request", "Trigger not found", "a step the graph has not");
        await Refused(new { operation = "run", id = "button", triggerId = "closed" }, "invalid_request", "Trigger does not permit manual execution", "a Trigger closed to manual runs");

        await Ok(new { operation = "set_enabled", id = "button", enabled = false });
        await Refused(new { operation = "run", id = "button", triggerId = "press" }, "invalid_request", "The entity definition is off", "a definition that is off");
        await Ok(new { operation = "set_enabled", id = "button", enabled = true });

        // A new revision whose When started fails: the one before runs on, but it is not the saved one.
        var failing = await Ok(new { operation = "save", id = "button", expectedRevision = 1, definition = Button(failingSetup: true) });
        var reason = failing["activationError"]!.GetValue<string>();
        Check(!failing["active"]!.GetValue<bool>() && reason.StartsWith("Convert", StringComparison.Ordinal), "the new revision does not start: " + failing.ToJsonString());
        await Refused(new { operation = "run", id = "button", triggerId = "press" }, "invalid_request", "The saved revision of this entity definition is not running: " + reason, "a saved revision that does not run");
        Check(History(automationId).Count(r => r["cause"]!.GetValue<string>() == "Manual") == 1, "a refused run is recorded: " + string.Join(" | ", History(automationId).Select(r => r.ToJsonString())));
        await Ok(new { operation = "save", id = "button", expectedRevision = 2, definition = Button() });

        await Ok(new { operation = "save", id = "plain", expectedRevision = 0, definition = Definition("Plain plug", "10.0.0.5") });
        await Refused(new { operation = "run", id = "plain", triggerId = "press" }, "invalid_request", "A class definition has no steps to run", "a class definition");
        Console.WriteLine("PASS entity api: run runs a Trigger step by hand and records it with its trace; an unknown definition, a step that is no Trigger, a closed Trigger, a definition off or not running its saved revision, and a class are refused");

        // An automation and a definition of one id: each run is in its own history only.
        var automation = Graph("Porch", [N("t", "trigger", new { type = "manual" }), N("say", "log", new { value = Lit("string", "automation") })], Next("t", "say"));
        var visual = JsonNode.Parse(await api.VisualRequest(JsonSerializer.Serialize(new { operation = "save", id = "porch", expectedRevision = 0, graph = automation })))!;
        Check(visual["ok"]!.GetValue<bool>() && visual["result"]!["active"]!.GetValue<bool>(), "the automation porch saves: " + visual.ToJsonString());
        var porch = Graph("Porch", [N("t", "trigger", new { type = "manual" }), N("set", "set_entity_state", new JsonObject { ["entity"] = "seen", ["value"] = Lit("string", "yes") })], Next("t", "set"));
        var definition = await Ok(new { operation = "save", id = "porch", expectedRevision = 0, definition = GraphDefinition("Porch", porch, ("seen", "sensor")) });
        var (automationHistory, definitionHistory) = (visual["result"]!["document"]!["automationId"]!.GetValue<string>(), definition["document"]!["automationId"]!.GetValue<string>());
        Check(automationHistory != definitionHistory, "the automation and the definition share a history: " + automationHistory);
        var visualRun = JsonNode.Parse(await api.VisualRequest(JsonSerializer.Serialize(new { operation = "run", id = "porch", triggerId = "t" })))!["result"]!["runId"]!.GetValue<string>();
        var definitionRun = (await Ok(new { operation = "run", id = "porch", triggerId = "t" }))["runId"]!.GetValue<string>();
        await EntityTestHome.Until(() => History(automationHistory).Length == 1 && History(definitionHistory).Length == 1
            && History(automationHistory)[0]["status"]!.GetValue<string>() == "Succeeded" && History(definitionHistory)[0]["status"]!.GetValue<string>() == "Succeeded", "both runs end");
        Check(History(automationHistory).Single()["runId"]!.GetValue<string>() == visualRun && History(definitionHistory).Single()["runId"]!.GetValue<string>() == definitionRun
            && History(definitionHistory).Single()["visualId"]!.GetValue<string>() == "porch", "each history holds its own run only");
        Console.WriteLine("PASS entity api: run of a definition reaches the definition, not the automation of the same id, and each history holds its own run");
    }

    /// <summary>
    /// <c>deriveSignatures</c> on preview and save writes each Custom C# step's pins from its code first, as an automation's
    /// does: a step sent with its code and values alone previews invalid without it, valid with it, answered with the graph
    /// with those pins and nothing else of the definition; a save with it stores that graph. What deriving removed is
    /// answered as warnings.
    /// </summary>
    static async Task DerivesCustomPins(HassSharpManager api)
    {
        async Task<JsonObject> Ok(object request)
        {
            var answer = JsonNode.Parse(await api.EntityRequest(JsonSerializer.Serialize(request)))!;
            Check(answer["ok"]!.GetValue<bool>(), "the request failed: " + answer.ToJsonString());
            return answer["result"]!.AsObject();
        }
        const string twice = "public static class CustomNode\n{\n    [Node(\"Twice\", Pure = true)]\n    public static double Run(double value) => value * 2;\n}";
        var code = N("twice", "custom", new JsonObject { ["source"] = twice, ["arguments"] = new JsonObject { ["value"] = Lit("number", 3), ["gone"] = Lit("number", 1) } });
        var definition = GraphDefinition("Twice", Graph("Twice", [code, VisualExecutionTests.Hub("s", "level", "sensor")], Data("twice", "s", "value", "result")));
        static bool Removed(JsonNode? diagnostics) => diagnostics?.AsArray().Any(d => d!["nodeId"]?.GetValue<string>() == "twice" && d["severity"]!.GetValue<int>() == 2
            && d["message"]!.GetValue<string>() == "Removed the value of Gone: the code no longer has that input") == true;

        var plain = await Ok(new { operation = "preview", definition });
        Check(!plain["valid"]!.GetValue<bool>() && !plain.ContainsKey("graph"), "without deriveSignatures a step without its pins is invalid: " + plain.ToJsonString());
        var derived = await Ok(new { operation = "preview", definition, deriveSignatures = true });
        Check(derived["valid"]!.GetValue<bool>() && Removed(derived["diagnostics"]), "with it the preview is valid and says what it removed: " + derived["diagnostics"]!.ToJsonString());
        Check(derived.Select(p => p.Key).Order(StringComparer.Ordinal).SequenceEqual(["diagnostics", "graph", "source", "sourceMap", "valid"]) && !derived["graph"]!.AsObject().ContainsKey("definition"),
            "the preview answers the graph and nothing else of the definition: " + string.Join(", ", derived.Select(p => p.Key)));
        var node = derived["graph"]!["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "twice")!;
        Check(node["config"]!["signature"]?["inputs"]?["value"] != null && node["config"]!["signature"]!["outputs"]!["result"] != null && node["config"]!["arguments"]!.AsObject().Select(p => p.Key).SequenceEqual(["value"]),
            "the graph has the step's pins from its code: " + node.ToJsonString());

        var saved = await Ok(new { operation = "save", id = "twice", expectedRevision = 0, definition, deriveSignatures = true });
        Check(saved["active"]!.GetValue<bool>() && Removed(saved["diagnostics"]), "the save runs and says what it removed: " + saved.ToJsonString());
        var stored = (await Ok(new { operation = "read", id = "twice" }))["logic"]!["graph"]!["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "twice")!;
        Check(JsonNode.DeepEquals(stored["config"]!["signature"], node["config"]!["signature"]) && stored["config"]!["arguments"]!.AsObject().Count == 1, "the save stores the derived graph: " + stored.ToJsonString());
        Console.WriteLine("PASS entity api: deriveSignatures writes a Custom C# step's pins from its code on preview and save, answers the derived graph alone and warns of what it removed");
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
        // The old instance was on; the new one comes back on, as the switch last showed, until the command turns it off.
        await EntityTestHome.Until(() => home.Snapshots(batches).Any(s => IsOn(s, "plug1:relay", true)), "the new instance pushes its first state, on");
        Check(!home.Snapshots(batches).Any(s => IsOn(s, "plug1:relay", null)), "the new instance pushed unknown");
        batches = home.Pushes().Length;
        await api.EntityCommand("plug1:relay", "turn_off", null);
        Check(EntityTestHome.Commands().Contains("turn_off 10.0.0.11"), "the new instance has the new Host");
        await EntityTestHome.Until(() => home.Snapshots(batches).Any(s => IsOn(s, "plug1:relay", false)), "the new instance pushes off");
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
        Check(Status(broken["document"]!.AsObject()) == "failed" && Status(readBroken) == "failed" && readBroken["liveEntities"]!.AsArray().Count == 0 && !readBroken["active"]!.GetValue<bool>()
            && readBroken["activationError"]!.GetValue<string>() == broken["activationError"]!.GetValue<string>(), "the failed definition reads failed: " + readBroken.ToJsonString());
        await Task.Delay(60);
        Check(!home.Snapshots(batches).Any(s => s["uniqueId"]!.GetValue<string>().StartsWith("broken:", StringComparison.Ordinal)), "a definition that never ran pushed states");
        Console.WriteLine("PASS entity api: a failing Setup() saves, answers the error, syncs nothing and reads failed");

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

        // The id new is the panel's page for a new definition, so a save refuses it as it refuses an id outside the pattern.
        var badId = await Entity(new { operation = "save", id = "Bad Id", expectedRevision = 0, definition = Definition("Plug bad", "10.0.0.9") });
        Check(badId.ToJsonString() == """{"version":1,"ok":false,"error":{"code":"invalid_request","detail":"Invalid definition ID"}}""", "a save of an id outside the pattern: " + badId.ToJsonString());
        var reserved = await Entity(new { operation = "save", id = "new", expectedRevision = 0, definition = Definition("Plug new", "10.0.0.9") });
        Check(reserved.ToJsonString() == """{"version":1,"ok":false,"error":{"code":"invalid_request","detail":"Invalid definition ID: new is reserved"}}""", "a save of the id new: " + reserved.ToJsonString());
        Check(await Fails(new { operation = "read", id = "new" }) == "not_found" && home.Syncs("new").Length == 0, "nothing is stored or synced under the id new");
        Console.WriteLine("PASS entity api: a save refuses the reserved id new as it refuses an id outside the pattern");
    }

    static Exception? Thrown(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }
}
