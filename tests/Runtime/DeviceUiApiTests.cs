using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>
/// The panel's requests for device pages and stored data through a whole manager (<see cref="EntityTestHome"/>),
/// <see cref="HassSharpManager.DeviceUiRequest"/>: <c>read</c> answers the running definition's pages, or that they are
/// unchanged, or that it is not running; <c>image</c> answers a current card's JPEG; <c>act</c> answers how a press
/// ended; <c>counts</c> answers the attention of the running definitions and the nodes that hold data; <c>stores</c> and
/// <c>delete_store</c> list and delete the shared stores; none waits behind a save; and a bad request is answered as
/// one. The device is Task 5's Board (<see cref="DeviceUiHostTests.BoardCode"/>), with Desk and Keeper for the data.
/// </summary>
static class DeviceUiApiTests
{
    // A device that keeps a value and draws two cards waiting for the user.
    const string DeskCode = """
        public class Desk : SharpDevice
        {
            protected override Task Start()
            {
                Data.Set("seen", 1);
                Ui.List("todo", "To do", [new UiCard("a").Text("A"), new UiCard("b").Text("B")], attention: true);
                return Task.CompletedTask;
            }
        }
        """;
    // A device that keeps a value, and "hello" in the shared store test.ui.
    const string KeeperCode = """
        public class Keeper : SharpDevice
        {
            protected override Task Start()
            {
                Data.Set("seen", 1);
                Shared("test.ui").Set("x", "hello");
                return Task.CompletedTask;
            }
        }
        """;
    // A device whose Start() fails once it has drawn.
    const string FailingCode = """
        public class Failing : SharpDevice
        {
            protected override Task Start()
            {
                Ui.List("drawn", "Drawn", [new UiCard("a").Text("A")]);
                throw new InvalidOperationException("no board answers");
            }
        }
        """;

    /// <summary>A whole manager over a home of its own.</summary>
    sealed class Rig : IAsyncDisposable
    {
        public EntityTestHome Home { get; }
        public HassSharpManager Api { get; private set; } = null!;
        Rig(string name) => Home = new("device-ui-api-" + name);
        public static async Task<Rig> Start(string name)
        {
            var rig = new Rig(name);
            rig.Api = new(rig.Home.Root);
            await rig.Api.Init();
            return rig;
        }
        public async Task<JsonObject> Entities(object request)
        {
            var answer = JsonNode.Parse(await Api.EntityRequest(JsonSerializer.Serialize(request)))!;
            Check(answer["ok"]!.GetValue<bool>(), "the entity request failed: " + answer.ToJsonString());
            return answer["result"]!.AsObject();
        }
        /// <summary>Saves the definition, checks that it runs and gives its host once it has started.</summary>
        public async Task<DefinitionHost> Running(string id, JsonObject definition)
        {
            var saved = await Entities(new { operation = "save", id, expectedRevision = 0, definition });
            Check(saved["active"]!.GetValue<bool>() && saved["activationError"] == null, $"{id} runs: " + saved.ToJsonString());
            var host = Scripts.Entities.Host(id) ?? throw new Exception($"{id} has no host");
            await host.Started;
            Check(host.Status == "running", $"{id} is running: {host.Status} {host.Error}");
            return host;
        }
        public Task Off(string id) => Entities(new { operation = "set_enabled", id, enabled = false });
        public UserScriptManager Scripts => (UserScriptManager)typeof(HassSharpManager).GetField("_userScriptManager", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Api)!;
        /// <summary>The authoring gate a save holds.</summary>
        public SemaphoreSlim Gate => (SemaphoreSlim)typeof(HassSharpManager).GetField("_visualGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Api)!;
        public async Task<JsonObject> Raw(string request) => JsonNode.Parse(await Api.DeviceUiRequest(request))!.AsObject();
        public Task<JsonObject> Request(object request) => Raw(JsonSerializer.Serialize(request));
        public async Task<JsonObject> Ok(object request)
        {
            var answer = await Request(request);
            Check(answer["version"]!.GetValue<int>() == 1 && answer["ok"]!.GetValue<bool>(), "the device ui request failed: " + answer.ToJsonString());
            return answer["result"]!.AsObject();
        }
        /// <summary>The code and detail of a request answered with an error.</summary>
        public static (string Code, string Detail) Failed(JsonObject answer)
        {
            Check(answer["version"]!.GetValue<int>() == 1 && !answer["ok"]!.GetValue<bool>(), "the request answers an error: " + answer.ToJsonString());
            return ((string)answer["error"]!["code"]!, answer["error"]!["detail"]!.ToString());
        }
        public async Task<(string Code, string Detail)> Error(object request) => Failed(await Request(request));
        public string SharedStore(string name) => Path.Combine(Home.Root, ".storage", "hass_sharp", "data", "shared", name);
        public async ValueTask DisposeAsync()
        {
            await Api.UnloadAsync();
            await Home.DisposeAsync();
        }
    }

    static JsonObject Board(string name = "Board") => DeviceUiHostTests.Definition(name, DeviceUiHostTests.Device("board", "Front board", DeviceUiHostTests.BoardCode));

    [RuntimeSuite("--device-ui-api", Order = 45)]
    public static async Task Run()
    {
        await ReadAnswersThePages();
        await ReadOfAnOffOrUnknownDefinition();
        await ImageAnswersTheJpeg();
        await ActAnswersTheStatus();
        await CountsAnswerAttentionAndStored();
        await CountsAnswerWhileASaveHoldsTheGate();
        await StoresListAndDelete();
        await BadRequests();
    }

    static async Task ReadAnswersThePages()
    {
        await using var rig = await Rig.Start("read");
        var host = await rig.Running("board", Board());
        var read = await rig.Ok(new { operation = "read", definitionId = "board" });
        var revision = (string)read["revision"]!;
        Check(read["running"]!.GetValue<bool>() && revision == host.UiRevision, "the read is running with the host's revision: " + read.ToJsonString());
        var pages = read["pages"]!.AsArray();
        Check(pages.Count == 1 && JsonNode.DeepEquals(pages[0], host.UiPages()[0]), "the read answers the host's one page: " + read.ToJsonString());
        var unchanged = await rig.Ok(new { operation = "read", definitionId = "board", knownRevision = revision });
        Check(JsonNode.DeepEquals(unchanged, new JsonObject { ["running"] = true, ["revision"] = revision, ["unchanged"] = true }), "the read of the known revision is unchanged, without pages: " + unchanged.ToJsonString());
        var pressed = await rig.Ok(new { operation = "act", definitionId = "board", nodeKey = "board", list = "items", card = "1", action = "save", values = new { name = "Ivan" } });
        Check((string)pressed["status"]! == "done", "the press that redraws: " + pressed.ToJsonString());
        var again = await rig.Ok(new { operation = "read", definitionId = "board", knownRevision = revision });
        Check(again["running"]!.GetValue<bool>() && (string)again["revision"]! != revision && again["unchanged"] == null, "the old revision answers a new one: " + again.ToJsonString());
        Check(DeviceUiHostTests.Cards(again["pages"]![0]!.AsObject(), "items") == "1=Ivan, 2=Marko", "the pages again, as redrawn: " + again.ToJsonString());
        Console.WriteLine("PASS device ui api: read answers the running definition's pages and revision, unchanged for the known one, and the pages again once it changed");
    }

    static async Task ReadOfAnOffOrUnknownDefinition()
    {
        await using var rig = await Rig.Start("off");
        await rig.Running("board", Board());
        await rig.Off("board");
        var off = await rig.Ok(new { operation = "read", definitionId = "board" });
        Check(off.ToJsonString() == """{"running":false}""", "a definition turned off: " + off.ToJsonString());
        var unknown = await rig.Ok(new { operation = "read", definitionId = "nobody", knownRevision = "x" });
        Check(unknown.ToJsonString() == """{"running":false}""", "an unknown definition: " + unknown.ToJsonString());
        var saved = await rig.Entities(new { operation = "save", id = "failing", expectedRevision = 0, definition = DeviceUiHostTests.Definition("Failing", DeviceUiHostTests.Device("box", "Box", FailingCode)) });
        Check(saved["activationError"]?.GetValue<string>().Contains("no board answers", StringComparison.Ordinal) == true, "the failing definition's start fails: " + saved["activationError"]);
        var failing = rig.Scripts.Entities.Host("failing") ?? throw new Exception("the failing definition has no host");
        Check(failing.Status == "failed" && failing.UiPages().Count == 1, $"the failed host keeps the page it drew: {failing.Status}, {failing.UiPages().Count}");
        var failed = await rig.Ok(new { operation = "read", definitionId = "failing" });
        Check(failed.ToJsonString() == """{"running":false}""", "a definition whose start failed: " + failed.ToJsonString());
        Console.WriteLine("PASS device ui api: read of a definition that is off, unknown or failed answers that it is not running");
    }

    static async Task ImageAnswersTheJpeg()
    {
        await using var rig = await Rig.Start("image");
        await rig.Running("board", Board());
        var read = await rig.Ok(new { operation = "read", definitionId = "board" });
        var hash = (string)read["pages"]![0]!["lists"]![0]!["cards"]![0]!["image"]!;
        var image = await rig.Ok(new { operation = "image", definitionId = "board", nodeKey = "board", hash });
        Check(Convert.FromBase64String((string)image["jpeg"]!) is [0xFF, 0xD8, 1, (byte)'1'], "the image decodes to the card's bytes: " + image.ToJsonString());
        foreach (var (definitionId, nodeKey, what) in new[] { ("board", "board", "an unknown hash"), ("board", "nobody", "a node with no page"), ("nobody", "board", "an unknown definition") })
        {
            var missing = await rig.Error(new { operation = "image", definitionId, nodeKey, hash = what == "an unknown hash" ? new string('0', 64) : hash });
            Check(missing.Code == "not_found", $"{what}: " + missing);
        }
        await rig.Off("board");
        Check((await rig.Error(new { operation = "image", definitionId = "board", nodeKey = "board", hash })).Code == "not_found", "the image of a definition turned off");
        Console.WriteLine("PASS device ui api: image answers a current card's JPEG as base64, and not_found for another hash, node or definition");
    }

    static async Task ActAnswersTheStatus()
    {
        await using var rig = await Rig.Start("act");
        await rig.Running("board", Board());
        object Press(string card, string action, object values) => new { operation = "act", definitionId = "board", nodeKey = "board", list = "items", card, action, values };
        var done = await rig.Ok(Press("1", "save", new { name = "Ivan" }));
        Check(done.ToJsonString() == """{"status":"done","message":null}""", "a press that is done: " + done.ToJsonString());
        var changed = await rig.Ok(Press("9", "save", new { name = "Ivan" }));
        Check(changed.ToJsonString() == """{"status":"changed","message":null}""", "a press of a card the page no longer has: " + changed.ToJsonString());
        var refused = await rig.Ok(Press("1", "taken", new { name = "Marko" }));
        Check(refused.ToJsonString() == """{"status":"refused","message":"Name is taken"}""", "a press its handler refuses: " + refused.ToJsonString());
        // The values as the request's text has them are checked against the card's fields.
        var blank = await rig.Raw("""{"operation":"act","definitionId":"board","nodeKey":"board","list":"items","card":"1","action":"save","values":{"name":"  "}}""");
        Check(blank["result"]?.ToJsonString() == """{"status":"refused","message":"Name is required."}""", "a value that does not fit its field: " + blank.ToJsonString());
        // A value that is an object, as a pick field's is, reaches the card's fields as sent: here a text field's, which refuses it.
        var nested = await rig.Raw("""{"operation":"act","definitionId":"board","nodeKey":"board","list":"items","card":"1","action":"save","values":{"name":{"new":{"first_name":"Ana"}}}}""");
        Check(nested["result"]?.ToJsonString() == """{"status":"refused","message":"Name must be text."}""", "a value that is an object: " + nested.ToJsonString());
        await rig.Off("board");
        var off = await rig.Error(Press("1", "save", new { name = "Ivan" }));
        Check(off.Code == "not_running", "a press of a definition turned off: " + off);
        Check((await rig.Error(new { operation = "act", definitionId = "nobody", nodeKey = "board", list = "items", card = "1", action = "save", values = new { } })).Code == "not_running", "a press of an unknown definition");
        Console.WriteLine("PASS device ui api: act answers done, changed and refused with its message, and not_running for a definition that is off");
    }

    static async Task CountsAnswerAttentionAndStored()
    {
        await using var rig = await Rig.Start("counts");
        await rig.Running("one", DeviceUiHostTests.Definition("One", DeviceUiHostTests.Device("d", "Desk", DeskCode)));
        await rig.Running("two", DeviceUiHostTests.Definition("Two", DeviceUiHostTests.Device("d", "Keeper", KeeperCode)));
        var counts = await rig.Ok(new { operation = "counts" });
        Check(counts.ToJsonString() == """{"attention":{"one":{"d":2}},"stored":{"one":["d"],"two":["d"]}}""", "the counts: " + counts.ToJsonString());
        await rig.Off("one");
        var off = await rig.Ok(new { operation = "counts" });
        Check(off.ToJsonString() == """{"attention":{},"stored":{"one":["d"],"two":["d"]}}""", "the counts once the definition with attention is off: " + off.ToJsonString());
        Console.WriteLine("PASS device ui api: counts answers the attention of each running definition's nodes and the nodes that hold data, only where there is some");
    }

    static async Task CountsAnswerWhileASaveHoldsTheGate()
    {
        await using var rig = await Rig.Start("gate");
        await rig.Running("board", Board());
        var gate = rig.Gate;
        await gate.WaitAsync();
        try
        {
            var limit = TimeSpan.FromSeconds(2);
            var counts = await rig.Request(new { operation = "counts" }).WaitAsync(limit);
            Check(counts["ok"]!.GetValue<bool>(), "counts while the gate is held: " + counts.ToJsonString());
            var read = await rig.Request(new { operation = "read", definitionId = "board" }).WaitAsync(limit);
            Check(read["result"]?["running"]?.GetValue<bool>() == true, "read while the gate is held: " + read.ToJsonString());
            var act = await rig.Request(new { operation = "act", definitionId = "board", nodeKey = "board", list = "items", card = "1", action = "save", values = new { name = "Ivan" } }).WaitAsync(limit);
            Check((string?)act["result"]?["status"] == "done", "act while the gate is held: " + act.ToJsonString());
        }
        finally { gate.Release(); }
        Console.WriteLine("PASS device ui api: counts, read and act answer while a save holds the authoring gate");
    }

    static async Task StoresListAndDelete()
    {
        await using var rig = await Rig.Start("stores");
        await rig.Running("keeper", DeviceUiHostTests.Definition("Keeper", DeviceUiHostTests.Device("d", "Keeper", KeeperCode)));
        var stores = await rig.Ok(new { operation = "stores" });
        // "hello" is stored as its JSON, 7 bytes.
        Check(stores.ToJsonString() == """{"shared":[{"name":"test.ui","bytes":7,"users":["keeper"]}]}""", "the shared stores: " + stores.ToJsonString());
        var inUse = await rig.Error(new { operation = "delete_store", name = "test.ui" });
        Check(inUse == ("in_use", "test.ui is in use by Keeper: turn it off first."), "deleting a store in use: " + inUse);
        Check(Directory.Exists(rig.SharedStore("test.ui")), "the store in use is kept");
        await rig.Running("another", DeviceUiHostTests.Definition("Another", DeviceUiHostTests.Device("d", "Keeper", KeeperCode)));
        await rig.Running("binder", DeviceUiHostTests.Definition("Binder", DeviceUiHostTests.Device("d", "Keeper", KeeperCode)));
        var three = await rig.Error(new { operation = "delete_store", name = "test.ui" });
        Check(three == ("in_use", "test.ui is in use by Another, Binder and Keeper: turn them off first."), "deleting a store three definitions use: " + three);
        await rig.Off("another");
        var two = await rig.Error(new { operation = "delete_store", name = "test.ui" });
        Check(two == ("in_use", "test.ui is in use by Binder and Keeper: turn them off first."), "deleting a store two definitions use: " + two);
        await rig.Off("binder");
        await rig.Off("keeper");
        var deleted = await rig.Ok(new { operation = "delete_store", name = "test.ui" });
        Check(deleted.ToJsonString() == """{"deleted":true}""", "deleting the store once it is off: " + deleted.ToJsonString());
        Check(!Directory.Exists(rig.SharedStore("test.ui")), "the store's directory is gone");
        Check((await rig.Error(new { operation = "delete_store", name = "test.ui" })).Code == "not_found", "deleting it again");
        Check((await rig.Ok(new { operation = "stores" })).ToJsonString() == """{"shared":[]}""", "no shared store is left");
        // A name from the browser that is no shared store's never reaches outside the shared stores.
        var outside = Path.Combine(rig.Home.Root, ".storage", "hass_sharp", "data", "outside");
        Directory.CreateDirectory(outside);
        foreach (var name in new[] { "../outside", "..", "/tmp", "" })
        {
            var refused = await rig.Error(new { operation = "delete_store", name });
            Check(refused.Code is "not_found" or "invalid_request", $"deleting the store '{name}': " + refused);
        }
        Check(Directory.Exists(outside), "nothing outside the shared stores is deleted");
        Console.WriteLine("PASS device ui api: stores lists each shared store with its size and users; delete_store refuses one in use, naming its definitions, deletes it once off, and answers not_found after");
    }

    static async Task BadRequests()
    {
        await using var rig = await Rig.Start("bad");
        foreach (var request in new[]
        {
            "[]", "{}", "not json", """{"operation":"nope"}""", """{"operation":"read"}""",
            """{"operation":"act","definitionId":"board","nodeKey":"board","list":"items","card":"1","action":"save"}""",
            """{"operation":"act","definitionId":"board","nodeKey":"board","list":"items","card":"1","action":"save","values":[]}""",
        })
        {
            var answer = Rig.Failed(await rig.Raw(request));
            Check(answer.Code == "invalid_request", $"the request {request}: " + answer);
        }
        Console.WriteLine("PASS device ui api: a request that is not an object, lacks a field or names an unknown operation answers invalid_request");
    }
}
