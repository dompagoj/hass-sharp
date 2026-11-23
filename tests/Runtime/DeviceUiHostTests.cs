using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>
/// The pages of C# device nodes in their host, through a whole manager (<see cref="EntityTestHome"/>): <c>Ui</c> is
/// refused before <c>Start()</c>; a started device has its page, its attention and a revision that changes with each
/// redraw and with each save; a press runs the handler of the card as drawn now with the values it sent, answers
/// <c>changed</c> for a card the device removed, and presses take turns; a refusal and a failure are answered as such,
/// the failure logged without the values; a handler past its time is answered as failed and keeps its page's turn until
/// it ends; stopping waits for a press and refuses one after it began; an entity a device added draws on the device's
/// page; and a stopped host keeps no page. The device Board draws its list <c>items</c> from a field of its class, each
/// card with an image, and the list <c>slow</c>, whose one action waits on the test's gate; its sensors show what its
/// handlers saw.
/// </summary>
static class DeviceUiHostTests
{
    const string GateKey = "HassSharp.DeviceUiHostTests.Gate", StoppedKey = "HassSharp.DeviceUiHostTests.Stopped";

    // The device: Save renames an item and redraws, showing the name it got on seen; Dismiss, after a while, removes the
    // item and redraws, counting its runs on runs; Taken refuses; Break throws. Wait shows "waiting" on seen and waits on
    // the test's gate. Its Stop() notes that it ran.
    internal const string BoardCode = """
        public class Board : SharpDevice
        {
            readonly Dictionary<string, string> items = new() { ["1"] = "Ana", ["2"] = "Marko" };
            readonly SharpSensor seen, runs;
            int dismissed;
            public Board()
            {
                seen = Add(new SharpSensor("seen"));
                runs = Add(new SharpSensor("runs"));
            }
            protected override Task Start()
            {
                Draw();
                Ui.List("slow", "Slow", [new UiCard("wait").Text("Wait").Action("Wait", _ =>
                {
                    seen.Value = "waiting";
                    return ((TaskCompletionSource)AppContext.GetData("HassSharp.DeviceUiHostTests.Gate")!).Task;
                })]);
                return Task.CompletedTask;
            }
            protected override Task Stop()
            {
                AppContext.SetData("HassSharp.DeviceUiHostTests.Stopped", true);
                return Task.CompletedTask;
            }
            void Draw() => Ui.List("items", "Items", items.Select(item => new UiCard(item.Key)
                .Image(new byte[] { 0xFF, 0xD8, 1, (byte)item.Key[0] })
                .Text(item.Value)
                .Field("name", "Name", item.Value, required: true)
                .Action("Save", values =>
                {
                    seen.Value = values["name"];
                    items[item.Key] = values["name"];
                    Draw();
                    return Task.CompletedTask;
                })
                .Action("Dismiss", async _ =>
                {
                    await Task.Delay(50);
                    runs.Value = ++dismissed;
                    items.Remove(item.Key);
                    Draw();
                })
                .Action("Taken", _ => throw new UiRefusal("Name is taken"))
                .Action("Break", _ => throw new InvalidOperationException("boom"))), attention: true);
        }
        """;
    // A device that draws too early: in its constructor, or in Setup().
    const string ConstructorCode = """
        public class Early : SharpDevice
        {
            public Early() { _ = Ui; }
        }
        """;
    const string SetupCode = """
        public class Eager : SharpDevice
        {
            protected override Task Setup() { _ = Ui; return Task.CompletedTask; }
        }
        """;
    // A device that draws its list mine, and adds an entity that draws its own list entity.
    const string PadCode = """
        public class Lister : SharpSensor
        {
            public Lister() : base("lister") { }
            protected override Task Start() { Ui.List("entity", "From the entity", [new UiCard("a").Text("A")]); return Task.CompletedTask; }
        }
        public class Pad : SharpDevice
        {
            public Pad() { Add(new Lister()); }
            protected override Task Start() { Ui.List("mine", "From the device", [new UiCard("b").Text("B")]); return Task.CompletedTask; }
        }
        """;

    static async Task<T> ThrowsAsync<T>(Func<Task> action, string what) where T : Exception
    {
        try { await action(); }
        catch (T error) when (error.GetType() == typeof(T)) { return error; }
        catch (Exception error) { throw new Exception($"{what}: threw {error.GetType().Name} instead of {typeof(T).Name}: {error.Message}"); }
        throw new Exception($"{what}: did not throw {typeof(T).Name}");
    }

    /// <summary>A C# device node of the key, name and code, its signature the one the compiler derives from the code, as the editor stores it.</summary>
    internal static JsonObject Device(string key, string name, string source)
    {
        var descriptor = new NodeLibraryCompiler(Diagnostics).Preview([], [new CustomNodeSource("c", source, "csharp_device")]).Snapshot.Custom["c"];
        Check(descriptor.Valid, $"the code of {key} compiles: " + string.Join(" | ", descriptor.Diagnostics.Select(d => d.Message)));
        var signature = descriptor.Signature?.DeepClone() ?? new JsonObject();
        return EntityNodeTests.CSharpDevice(key, key, name, new() { ["source"] = source, ["signature"] = signature, ["arguments"] = new JsonObject() });
    }

    /// <summary>A definition of the C# device nodes.</summary>
    internal static JsonObject Definition(string name, params JsonObject[] nodes) => new()
    {
        ["name"] = name, ["settings"] = new JsonArray(),
        ["logic"] = new JsonObject { ["kind"] = "graph", ["graph"] = new JsonObject { ["version"] = 5, ["name"] = name, ["nodes"] = new JsonArray(nodes), ["edges"] = new JsonArray() } },
    };

    /// <summary>The values a press sends, as the panel's request is parsed.</summary>
    static JsonObject Sent(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>A whole manager over a home of its own.</summary>
    sealed class Rig : IAsyncDisposable
    {
        public EntityTestHome Home { get; }
        public HassSharpManager Api { get; private set; } = null!;
        Rig(string name) => Home = new("device-ui-host-" + name);
        public static async Task<Rig> Start(string name)
        {
            var rig = new Rig(name);
            rig.Api = new(rig.Home.Root);
            await rig.Api.Init();
            return rig;
        }
        public async Task<JsonObject> Ok(object request)
        {
            var answer = JsonNode.Parse(await Api.EntityRequest(JsonSerializer.Serialize(request)))!;
            Check(answer["ok"]!.GetValue<bool>(), "the request failed: " + answer.ToJsonString());
            return answer["result"]!.AsObject();
        }
        public Task<JsonObject> Save(string id, JsonObject definition, long revision = 0) => Ok(new { operation = "save", id, expectedRevision = revision, definition });
        /// <summary>Saves the definition, checks that it runs and gives its host once it has started.</summary>
        public async Task<DefinitionHost> Running(string id, JsonObject definition, long revision = 0)
        {
            var saved = await Save(id, definition, revision);
            Check(saved["active"]!.GetValue<bool>() && saved["activationError"] == null, $"{id} runs: " + saved.ToJsonString());
            var host = Scripts.Entities.Host(id) ?? throw new Exception($"{id} has no host");
            await host.Started;
            Check(host.Status == "running", $"{id} is running: {host.Status} {host.Error}");
            return host;
        }
        public Task Off(string id) => Ok(new { operation = "set_enabled", id, enabled = false });
        UserScriptManager Scripts => (UserScriptManager)typeof(HassSharpManager).GetField("_userScriptManager", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(Api)!;
        /// <summary>The value the entity last showed, as JSON; null before it showed one.</summary>
        public string? Shown(string uniqueId) =>
            Home.Snapshots().LastOrDefault(s => s["uniqueId"]!.GetValue<string>() == uniqueId)?["values"]?["value"]?.ToJsonString();
        public Task Shows(string uniqueId, string value, string what) =>
            EntityTestHome.Until(() => Shown(uniqueId) == value, $"{what}: {uniqueId} shows {value}, not {Shown(uniqueId) ?? "nothing"}");
        public async ValueTask DisposeAsync()
        {
            await Api.UnloadAsync();
            await Home.DisposeAsync();
        }
    }

    // The cards of a page's list, by id, with their titles.
    internal static string Cards(JsonObject page, string list) =>
        string.Join(", ", page["lists"]!.AsArray().Single(each => (string)each!["id"]! == list)!["cards"]!.AsArray().Select(card => $"{card!["id"]}={card["title"]}"));

    static string Answer(UiActResult result) => result.Message == null ? result.Status : $"{result.Status}: {result.Message}";

    [RuntimeSuite("--device-ui-host", Order = 44)]
    public static async Task Run()
    {
        var timeout = DefinitionHost.UiActionTimeout;
        try
        {
            await UiIsRefusedBeforeStart();
            await AStartedDeviceHasItsPage();
            await APressRunsTheHandlerWithItsValues();
            await AStalePressChangesNothing();
            await TwoPressesTakeTurns();
            await RefusalsAndFailures();
            await ASlowHandlerTimesOut();
            await StoppingWaitsForAPress();
            await AnAddedEntityDrawsOnItsDevicesPage();
            await AStoppedHostKeepsNoPage();
            await TheGuidesSnapshotNamerRuns();
        }
        finally
        {
            DefinitionHost.UiActionTimeout = timeout;
            AppContext.SetData(GateKey, null);
            AppContext.SetData(StoppedKey, null);
        }
    }

    static async Task UiIsRefusedBeforeStart()
    {
        await using var rig = await Rig.Start("refused");
        foreach (var (id, source, sentence) in new[]
        {
            ("early", ConstructorCode, "Early is not running yet: use Ui from Start or later, not from Setup or the constructor."),
            ("eager", SetupCode, "Eager is not running yet: use Ui from Start or later, not from Setup or the constructor."),
        })
        {
            var saved = await rig.Save(id, Definition(id, Device("box", "Box", source)));
            var error = saved["activationError"]?.GetValue<string>();
            Check(!saved["active"]!.GetValue<bool>() && error?.Contains(sentence, StringComparison.Ordinal) == true, $"{id} fails its start with the sentence: " + saved.ToJsonString());
        }
        Console.WriteLine("PASS device ui host: Ui in a constructor or Setup() fails the start with a sentence");
    }

    static async Task AStartedDeviceHasItsPage()
    {
        await using var rig = await Rig.Start("page");
        var host = await rig.Running("board", Definition("Board", Device("board", "Front board", BoardCode)));
        var pages = host.UiPages();
        Check(pages.Count == 1, "one page: " + string.Join(" | ", pages.Select(page => page.ToJsonString())));
        var page = pages[0];
        Check((string)page["nodeKey"]! == "board" && (string)page["title"]! == "Front board", "the page has the node's key and title: " + page.ToJsonString());
        Check(Cards(page, "items") == "1=Ana, 2=Marko", "the page has the drawn cards: " + Cards(page, "items"));
        Check(page["lists"]!.AsArray().Select(list => (string)list!["id"]!).SequenceEqual(["items", "slow"]), "the lists in the order drawn: " + page.ToJsonString());
        var attention = host.UiAttention();
        Check(attention.Count == 1 && attention["board"] == 2, "the attention: " + string.Join(", ", attention.Select(a => $"{a.Key}={a.Value}")));
        var before = host.UiRevision;
        Check(host.UiRevision == before, "the revision stays while nothing changes");
        Check(Answer(await host.UiAct("board", "items", "1", "save", Sent("""{"name":"Ivan"}"""))) == "done", "the press that redraws");
        var redrawn = host.UiRevision;
        Check(redrawn != before, "the revision changes when the device redraws: " + redrawn);
        var next = await rig.Running("board", Definition("Board", Device("board", "Front board", BoardCode)), 1);
        Check(next != host && next.UiRevision != redrawn && next.UiRevision != before, $"the revision differs after a save: {next.UiRevision}, {redrawn}");
        Console.WriteLine("PASS device ui host: a started device has its page with its node's key and title, its attention, and a revision that changes with a redraw and a save");
    }

    static async Task APressRunsTheHandlerWithItsValues()
    {
        await using var rig = await Rig.Start("press");
        var host = await rig.Running("board", Definition("Board", Device("board", "Board", BoardCode)));
        var result = await host.UiAct("board", "items", "1", "save", Sent("""{"name":"Ivan"}"""));
        Check(Answer(result) == "done", "the press is done: " + Answer(result));
        Check(Cards(host.UiPages()[0], "items") == "1=Ivan, 2=Marko", "the page shows Ivan: " + Cards(host.UiPages()[0], "items"));
        await rig.Shows("board:board:seen", "\"Ivan\"", "the handler saw the value sent");
        Console.WriteLine("PASS device ui host: a press runs the card's handler with the values it sent, and the page shows what it drew");
    }

    static async Task AStalePressChangesNothing()
    {
        await using var rig = await Rig.Start("stale");
        var host = await rig.Running("board", Definition("Board", Device("board", "Board", BoardCode)));
        Check(Answer(await host.UiAct("board", "items", "2", "dismiss", Sent("{}"))) == "done", "the press that removes card 2");
        Check(Cards(host.UiPages()[0], "items") == "1=Ana", "card 2 is gone: " + Cards(host.UiPages()[0], "items"));
        foreach (var (list, card, action) in new[] { ("items", "2", "save"), ("items", "1", "rename"), ("gone", "1", "save") })
        {
            var result = await host.UiAct("board", list, card, action, Sent("""{"name":"Ivan"}"""));
            Check(Answer(result) == "changed", $"a press of {list}/{card}/{action}, which the page no longer has: " + Answer(result));
        }
        Check(Answer(await host.UiAct("nobody", "items", "1", "save", Sent("{}"))) == "changed", "a press of a node with no page");
        await Task.Delay(100);
        Check(rig.Shown("board:board:seen") == null, "the handler of the removed card never ran: " + rig.Shown("board:board:seen"));
        Console.WriteLine("PASS device ui host: a press of a card, action, list or node the page no longer has answers changed and runs nothing");
    }

    static async Task TwoPressesTakeTurns()
    {
        await using var rig = await Rig.Start("turns");
        var host = await rig.Running("board", Definition("Board", Device("board", "Board", BoardCode)));
        var results = await Task.WhenAll(host.UiAct("board", "items", "1", "dismiss", Sent("{}")), host.UiAct("board", "items", "1", "dismiss", Sent("{}")));
        Check(results.Select(Answer).SequenceEqual(["done", "changed"]), "the first press is done, the second finds the card gone: " + string.Join(", ", results.Select(Answer)));
        await rig.Shows("board:board:runs", "1", "the handler ran");
        await Task.Delay(100);
        Check(rig.Shown("board:board:runs") == "1", "exactly one handler ran: " + rig.Shown("board:board:runs"));
        Console.WriteLine("PASS device ui host: two presses sent together take turns in the order they came, the second looking the card up when its turn comes");
    }

    static async Task RefusalsAndFailures()
    {
        await using var rig = await Rig.Start("failures");
        var host = await rig.Running("board", Definition("Board", Device("board", "Board", BoardCode)));
        var missing = await host.UiAct("board", "items", "1", "save", Sent("""{"name":"  "}"""));
        Check(Answer(missing) == "refused: Name is required.", "a missing required field: " + Answer(missing));
        var taken = await host.UiAct("board", "items", "1", "taken", Sent("""{"name":"Marko"}"""));
        Check(Answer(taken) == "refused: Name is taken", "a handler that refuses: " + Answer(taken));
        var broken = await host.UiAct("board", "items", "1", "break", Sent("""{"name":"Secret value 42"}"""));
        Check(Answer(broken) == "failed: The action failed; see the Home Assistant log.", "a handler that throws: " + Answer(broken));
        var lines = rig.Home.Logs(PyLogLevel.Error).Where(line => line.Contains("boom", StringComparison.Ordinal)).ToArray();
        Check(lines.Length == 1 && lines[0].StartsWith("Board: ", StringComparison.Ordinal) && lines[0].Contains("'Break'", StringComparison.Ordinal),
            "one error line for the definition naming the action, with the exception: " + string.Join(" | ", lines));
        Check(!rig.Home.Logs().Any(line => line.Contains("Secret value 42", StringComparison.Ordinal)), "no log line has the value sent: " + string.Join(" | ", rig.Home.Logs()));
        Check(rig.Shown("board:board:seen") == null, "no handler that saves ran: " + rig.Shown("board:board:seen"));
        Console.WriteLine("PASS device ui host: a missing field and a UiRefusal are refused with their sentence; another exception fails, logged once with the action and not the values");
    }

    static async Task ASlowHandlerTimesOut()
    {
        DefinitionHost.UiActionTimeout = TimeSpan.FromMilliseconds(200);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AppContext.SetData(GateKey, gate);
        try
        {
            await using var rig = await Rig.Start("slow");
            var host = await rig.Running("board", Definition("Board", Device("board", "Board", BoardCode)));
            var waiting = host.UiAct("board", "slow", "wait", "wait", Sent("{}"));
            // A press behind it waits its turn, and is refused once the handler has run past its time.
            var queued = host.UiAct("board", "items", "1", "save", Sent("""{"name":"Ivan"}"""));
            var first = await waiting;
            Check(Answer(first).StartsWith("failed: The action did not finish within", StringComparison.Ordinal), "the slow press fails: " + Answer(first));
            Check(Answer(await queued) == "refused: An earlier action of this device is still running.", "the press waiting behind it is refused: " + Answer(await queued));
            var second = await host.UiAct("board", "items", "1", "save", Sent("""{"name":"Ivan"}"""));
            Check(Answer(second) == "refused: An earlier action of this device is still running.", "a press while the handler runs is refused: " + Answer(second));
            Check(rig.Shown("board:board:seen") == "\"waiting\"", "no other handler ran beside it: " + rig.Shown("board:board:seen"));
            gate.SetResult();
            // The handler's end gives its turn back on the pool.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            UiActResult third;
            while (Answer(third = await host.UiAct("board", "items", "1", "save", Sent("""{"name":"Ivan"}"""))) != "done" && clock.Elapsed < TimeSpan.FromSeconds(5))
            {
                Check(Answer(third) == "refused: An earlier action of this device is still running.", "a press as the handler ends: " + Answer(third));
                await Task.Delay(5);
            }
            Check(Answer(third) == "done", "a press once the handler ended: " + Answer(third));
            await rig.Shows("board:board:seen", "\"Ivan\"", "the press once the handler ended ran");
        }
        finally
        {
            DefinitionHost.UiActionTimeout = TimeSpan.FromSeconds(30);
            gate.TrySetResult();
            AppContext.SetData(GateKey, null);
        }
        Console.WriteLine("PASS device ui host: a handler past its time fails the press and keeps the page's turn until it ends; the presses meanwhile are refused");
    }

    static async Task StoppingWaitsForAPress()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AppContext.SetData(GateKey, gate);
        AppContext.SetData(StoppedKey, null);
        try
        {
            await using var rig = await Rig.Start("stopping");
            var host = await rig.Running("board", Definition("Board", Device("board", "Board", BoardCode)));
            var pressed = host.UiAct("board", "slow", "wait", "wait", Sent("{}"));
            await rig.Shows("board:board:seen", "\"waiting\"", "the handler runs");
            var off = rig.Off("board");
            await EntityTestHome.Until(() => host.Status == "stopped", "stopping begins");
            var refused = await ThrowsAsync<InvalidOperationException>(() => host.UiAct("board", "items", "1", "save", Sent("{}")), "a press once stopping began");
            Check(refused.Message == "The entity definition is not running", "the press after stopping began: " + refused.Message);
            await Task.Delay(200);
            Check(AppContext.GetData(StoppedKey) == null && !off.IsCompleted, "Stop() waits for the press");
            gate.SetResult();
            await off;
            Check(AppContext.GetData(StoppedKey) is true, "Stop() ran once the press ended");
            Check(Answer(await pressed) == "done", "the press it waited for is done: " + Answer(await pressed));
        }
        finally
        {
            gate.TrySetResult();
            AppContext.SetData(GateKey, null);
            AppContext.SetData(StoppedKey, null);
        }
        Console.WriteLine("PASS device ui host: stopping waits for a press before Stop(), and refuses a press once it began");
    }

    static async Task AnAddedEntityDrawsOnItsDevicesPage()
    {
        await using var rig = await Rig.Start("added");
        var host = await rig.Running("pad", Definition("Pad", Device("pad", "Pad", PadCode)));
        var pages = host.UiPages();
        Check(pages.Count == 1 && (string)pages[0]["nodeKey"]! == "pad", "one page, the device's: " + string.Join(" | ", pages.Select(page => page.ToJsonString())));
        Check(pages[0]["lists"]!.AsArray().Select(list => (string)list!["id"]!).Order(StringComparer.Ordinal).SequenceEqual(["entity", "mine"]),
            "the device's page has the entity's list: " + pages[0].ToJsonString());
        Console.WriteLine("PASS device ui host: an entity a device added draws on the device's page");
    }

    static async Task AStoppedHostKeepsNoPage()
    {
        await using var rig = await Rig.Start("stopped");
        var host = await rig.Running("board", Definition("Board", Device("board", "Board", BoardCode)));
        var hash = (string)host.UiPages()[0]["lists"]![0]!["cards"]![0]!["image"]!;
        Check(host.UiImage("board", hash) is [0xFF, 0xD8, 1, (byte)'1'], "the image of a current card");
        Check(host.UiImage("board", "0000") == null && host.UiImage("nobody", hash) == null, "no image for another hash or node");
        await rig.Off("board");
        Check(host.UiPages().Count == 0, "no page once stopped: " + string.Join(" | ", host.UiPages().Select(page => page.ToJsonString())));
        Check(host.UiAttention().Count == 0, "no attention once stopped");
        Check(host.UiImage("board", hash) == null, "no image once stopped");
        Console.WriteLine("PASS device ui host: a stopped host has no page, attention or image");
    }

    // The cards of a page's list, each as its id, its title and whether it has an image.
    static (string Id, string? Title, string? Image)[] CardsOf(JsonObject page, string list) =>
        [.. page["lists"]!.AsArray().Single(each => (string)each!["id"]! == list)!["cards"]!.AsArray()
            .Select(card => ((string)card!["id"]!, (string?)card["title"], (string?)card["image"]))];

    /// <summary>
    /// The Snapshot namer the custom entities guide shows, as written there (<see cref="ServiceCallTests.GuideCode"/>), on
    /// two definitions over one camera whose pictures are 640 pixels wide: each draws its first snapshot at once, at most
    /// 320 wide and waiting; Save refuses an empty name, and a name saved moves the snapshot to Named on both, the other
    /// redrawn by Changed; a second Save of that card answers changed; a rename on one shows on the other, and a delete on
    /// the other empties Named on the first.
    /// </summary>
    static async Task TheGuidesSnapshotNamerRuns()
    {
        await using var rig = await Rig.Start("guide");
        rig.Api.SetEntityDescriptions(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ha-entity-components.json")), started: true);
        await rig.Api.RetriesDone();
        var picture = SharpImage.Blank(640, 480, SharpColor.Blue).ToJpeg();
        rig.Home.AnswerPictures = (_, camera, ok, fail) => { if (camera == "camera.door") ok(picture); else fail("No such camera"); };
        var source = ServiceCallTests.GuideCode("SnapshotNamer");
        var descriptor = new NodeLibraryCompiler(NodeLibraryFixture.EntityProvider).Preview([], [new CustomNodeSource("c", source, "csharp_device")]).Snapshot.Custom["c"];
        Check(descriptor.Valid, "the guide's device compiles: " + string.Join(" | ", descriptor.Diagnostics.Select(d => d.Message)));
        JsonObject Namer() => EntityNodeTests.CSharpDevice("namer", "namer", "Snapshot namer", new()
        {
            ["source"] = source, ["signature"] = descriptor.Signature!.DeepClone(),
            ["arguments"] = new JsonObject { ["Camera"] = Lit("entity_id:camera", "camera.door") },
        });

        var door = await rig.Running("door", Definition("Door", Namer()));
        JsonObject Page(DefinitionHost host) => host.UiPages().Single();
        await EntityTestHome.Until(() => CardsOf(Page(door), "unnamed").Length == 1, "the first snapshot is drawn: " + Page(door).ToJsonString());
        var (snapshot, _, image) = CardsOf(Page(door), "unnamed")[0];
        Check(image != null && SharpImage.Load(door.UiImage("namer", image)!).Width == 320, "the snapshot's picture is 320 pixels wide");
        Check(door.UiAttention()["namer"] == 1 && CardsOf(Page(door), "named").Length == 0, "the snapshot waits, and nothing is named yet: " + Page(door).ToJsonString());

        Check(Answer(await door.UiAct("namer", "unnamed", snapshot, "save", Sent("""{"name":"  "}"""))) == "refused: Type a name first.", "Save refuses an empty name");
        Check(Answer(await door.UiAct("namer", "unnamed", snapshot, "save", Sent("""{"name":" Ivan "}"""))) == "done", "Save of a name");
        Check(CardsOf(Page(door), "unnamed").Length == 0 && CardsOf(Page(door), "named") is [(var named, "Ivan", not null)] && named == snapshot,
            "the snapshot is named Ivan, with its picture: " + Page(door).ToJsonString());
        Check(Answer(await door.UiAct("namer", "unnamed", snapshot, "save", Sent("""{"name":"Marko"}"""))) == "changed", "a second Save of the card answers changed");

        var hall = await rig.Running("hall", Definition("Hall", Namer()));
        Check(CardsOf(Page(hall), "named") is [(var shared, "Ivan", not null)] && shared == snapshot, "another namer lists the name with its picture: " + Page(hall).ToJsonString());
        Check(Answer(await door.UiAct("namer", "named", snapshot, "rename", Sent("""{"name":"Ana"}"""))) == "done", "Rename");
        await EntityTestHome.Until(() => CardsOf(Page(hall), "named") is [(_, "Ana", _)], "the other namer shows the new name: " + Page(hall).ToJsonString());
        Check(Answer(await hall.UiAct("namer", "named", snapshot, "delete", Sent("{}"))) == "done", "Delete");
        await EntityTestHome.Until(() => CardsOf(Page(door), "named").Length == 0, "the first namer no longer lists it: " + Page(door).ToJsonString());
        Check(CardsOf(Page(hall), "named").Length == 0, "nor the one that deleted it: " + Page(hall).ToJsonString());
        Console.WriteLine("PASS device ui host: the guide's Snapshot namer draws a snapshot to name, saves, renames and deletes names, and two of them share the names");
    }
}
