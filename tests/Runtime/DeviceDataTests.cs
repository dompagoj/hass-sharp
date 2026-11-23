using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>
/// The data of C# device nodes through a whole manager (<see cref="EntityTestHome"/>): <c>Data</c> and <c>Shared</c>
/// are refused before <c>Start()</c>; a node's store survives a recompile and a restart; two nodes have two stores; an
/// entity a device added keeps its data in the device's; a shared store reaches the <c>Changed</c> actions of another
/// definition, not the writer's own; a node removed from a saved definition loses its data when the next revision starts;
/// a deleted definition loses its data and keeps the shared stores; turning a definition off, a save that does not
/// compile and a start that fails delete nothing; and a stopped definition's handles are closed. Each device counts its
/// starts in its store and shows the count on its sensor <c>starts</c>.
/// </summary>
static class DeviceDataTests
{
    // A device that counts its starts in its store and shows the count; its Stop() notes that it stopped there too.
    const string CounterCode = """
        public class Counter : SharpDevice
        {
            readonly SharpSensor starts;
            public Counter() { starts = Add(new SharpSensor("starts")); }
            protected override Task Start() { starts.Value = Data.Update<int>("starts", n => n + 1); return Task.CompletedTask; }
            protected override Task Stop() { Data.Set("stopped", true); return Task.CompletedTask; }
        }
        """;
    // A device that reads its data too early: in its constructor, or in Setup().
    const string ConstructorCode = """
        public class Early : SharpDevice
        {
            public Early() { _ = Data; }
        }
        """;
    const string SetupCode = """
        public class Eager : SharpDevice
        {
            protected override Task Setup() { _ = Data; return Task.CompletedTask; }
        }
        """;
    const string SharedSetupCode = """
        public class Sharing : SharpDevice
        {
            protected override Task Setup() { _ = Shared("test.early"); return Task.CompletedTask; }
        }
        """;
    // A device whose switch note writes through the entity's own Data; its switch check reads it through the device's.
    const string NoteCode = """
        public class Note : SharpSwitch
        {
            public Note() : base("note") { }
            protected override Task TurnOn() { Data.Set("from-entity", "hello"); IsOn = true; return Task.CompletedTask; }
        }
        public class Pad : SharpDevice
        {
            readonly SharpSensor read;
            public Pad()
            {
                Add(new Note());
                read = Add(new SharpSensor("read"));
                Add(new SharpSwitch("check") { OnTurnOn = () => { read.Value = Data.Get<string>("from-entity"); return Task.CompletedTask; } });
            }
        }
        """;
    // A device whose switch write counts up in the shared store test.people, and whose sensor seen shows the count each
    // time another component changes it.
    const string PeopleCode = """
        public class People : SharpDevice
        {
            readonly SharpSensor seen;
            public People()
            {
                seen = Add(new SharpSensor("seen"));
                Add(new SharpSwitch("write") { OnTurnOn = () => { Shared("test.people").Update<int>("count", n => n + 1); return Task.CompletedTask; } });
            }
            protected override Task Start()
            {
                Shared("test.people").Changed(() => { seen.Value = Shared("test.people").Get<int>("count"); return Task.CompletedTask; });
                return Task.CompletedTask;
            }
        }
        """;
    // A counter that also writes to the shared store test.kept.
    const string KeeperCode = """
        public class Keeper : SharpDevice
        {
            readonly SharpSensor starts;
            public Keeper() { starts = Add(new SharpSensor("starts")); }
            protected override Task Start()
            {
                Shared("test.kept").Set("x", 1);
                starts.Value = Data.Update<int>("starts", n => n + 1);
                return Task.CompletedTask;
            }
        }
        """;
    // A counter whose Setup() fails, and one whose code does not compile.
    const string FailingCode = """
        public class Counter : SharpDevice
        {
            protected override Task Setup() => throw new InvalidOperationException("no counter answers");
        }
        """;
    const string BrokenCode = "public class Counter : SharpDevice { int x = ; }";
    // A device that hands its own and a shared handle to the test, having written to both.
    const string HanderCode = """
        public class Hander : SharpDevice
        {
            protected override Task Start()
            {
                Data.Set("x", 1);
                Shared("test.closing").Set("x", 1);
                AppContext.SetData("HassSharp.DeviceDataTests.Own", Data);
                AppContext.SetData("HassSharp.DeviceDataTests.Shared", Shared("test.closing"));
                return Task.CompletedTask;
            }
        }
        """;
    const string OwnKey = "HassSharp.DeviceDataTests.Own", SharedKey = "HassSharp.DeviceDataTests.Shared";

    static T Throws<T>(Action action, string what) where T : Exception
    {
        try { action(); }
        catch (T error) when (error.GetType() == typeof(T)) { return error; }
        catch (Exception error) { throw new Exception($"{what}: threw {error.GetType().Name} instead of {typeof(T).Name}: {error.Message}"); }
        throw new Exception($"{what}: did not throw {typeof(T).Name}");
    }

    /// <summary>A C# device node of the key and code, its signature the one the compiler derives from the code, as the editor stores it.</summary>
    static JsonObject Device(string key, string source)
    {
        var descriptor = new NodeLibraryCompiler(Diagnostics).Preview([], [new CustomNodeSource("c", source, "csharp_device")]).Snapshot.Custom["c"];
        var signature = descriptor.Signature?.DeepClone() ?? new JsonObject();
        return EntityNodeTests.CSharpDevice(key, key, key, new() { ["source"] = source, ["signature"] = signature, ["arguments"] = new JsonObject() });
    }

    /// <summary>A definition of the C# device nodes, each keyed and named as given.</summary>
    static JsonObject Definition(string name, params JsonObject[] nodes) => new()
    {
        ["name"] = name, ["settings"] = new JsonArray(),
        ["logic"] = new JsonObject { ["kind"] = "graph", ["graph"] = new JsonObject { ["version"] = 5, ["name"] = name, ["nodes"] = new JsonArray(nodes), ["edges"] = new JsonArray() } },
    };

    /// <summary>A whole manager over a home of its own.</summary>
    sealed class Rig : IAsyncDisposable
    {
        public EntityTestHome Home { get; }
        public HassSharpManager Api { get; private set; } = null!;
        Rig(string name) => Home = new("device-data-" + name);
        public static async Task<Rig> Start(string name)
        {
            var rig = new Rig(name);
            rig.Api = new(rig.Home.Root);
            await rig.Api.Init();
            return rig;
        }
        /// <summary>Home Assistant reloads the entry: a second manager over the same directory.</summary>
        public async Task Restart()
        {
            await Api.UnloadAsync();
            Api = new(Home.Root);
            await Api.Init();
        }
        public async Task<JsonNode> Request(object request) => JsonNode.Parse(await Api.EntityRequest(JsonSerializer.Serialize(request)))!;
        public async Task<JsonObject> Ok(object request)
        {
            var answer = await Request(request);
            Check(answer["ok"]!.GetValue<bool>(), "the request failed: " + answer.ToJsonString());
            return answer["result"]!.AsObject();
        }
        public Task<JsonObject> Save(string id, JsonObject definition, long revision = 0) => Ok(new { operation = "save", id, expectedRevision = revision, definition });
        /// <summary>Saves the definition and checks that it runs.</summary>
        public async Task Running(string id, JsonObject definition, long revision = 0)
        {
            var saved = await Save(id, definition, revision);
            Check(saved["active"]!.GetValue<bool>() && saved["activationError"] == null, $"{id} runs: " + saved.ToJsonString());
        }
        public UserScriptManager Scripts => (UserScriptManager)typeof(HassSharpManager).GetField("_userScriptManager", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(Api)!;
        public DataStores Data => Scripts.Data ?? throw new Exception("the manager has no data stores");
        /// <summary>The value the entity last showed, as JSON; null before it showed one.</summary>
        public string? Shown(string uniqueId) =>
            Home.Snapshots().LastOrDefault(s => s["uniqueId"]!.GetValue<string>() == uniqueId)?["values"]?["value"]?.ToJsonString();
        public Task Shows(string uniqueId, string value, string what) =>
            EntityTestHome.Until(() => Shown(uniqueId) == value, $"{what}: {uniqueId} shows {value}, not {Shown(uniqueId) ?? "nothing"}");
        /// <summary>The directory of a node's store, or of a definition's stores without a node.</summary>
        public string Devices(string definitionId, string? node = null) =>
            node == null ? Path.Combine(Home.Root, ".storage", "hass_sharp", "data", "devices", definitionId) : Path.Combine(Home.Root, ".storage", "hass_sharp", "data", "devices", definitionId, node);
        public string SharedStore(string name) => Path.Combine(Home.Root, ".storage", "hass_sharp", "data", "shared", name);
        public async ValueTask DisposeAsync()
        {
            await Api.UnloadAsync();
            await Home.DisposeAsync();
        }
    }

    [RuntimeSuite("--device-data", Order = 42)]
    public static async Task Run()
    {
        try
        {
            await DataIsRefusedBeforeStart();
            await DataSurvivesARecompileAndARestart();
            await TwoNodesHaveTwoStores();
            await AnAddedEntitySharesItsDevicesStore();
            await SharedIsOneStoreForTwoDefinitions();
            await DataOfARemovedNodeGoesAtTheNextStart();
            await DataGoesWithTheDefinition();
            await OffAndFailedKeepData();
            await AStoppedDefinitionsHandlesAreClosed();
            await SwappingACSharpNodeForTheLibraryNodeKeepsEntitiesAndData();
        }
        finally
        {
            AppContext.SetData(OwnKey, null);
            AppContext.SetData(SharedKey, null);
        }
    }

    static async Task DataIsRefusedBeforeStart()
    {
        await using var rig = await Rig.Start("refused");
        foreach (var (id, source, sentence) in new[]
        {
            ("early", ConstructorCode, "Early is not running yet: use Data from Start or later, not from Setup or the constructor."),
            ("eager", SetupCode, "Eager is not running yet: use Data from Start or later, not from Setup or the constructor."),
            ("sharing", SharedSetupCode, "Sharing is not running yet: use Shared from Start or later, not from Setup or the constructor."),
        })
        {
            var saved = await rig.Save(id, Definition(id, Device("box", source)));
            var error = saved["activationError"]?.GetValue<string>();
            Check(!saved["active"]!.GetValue<bool>() && error?.Contains(sentence, StringComparison.Ordinal) == true, $"{id} fails its start with the sentence: " + saved.ToJsonString());
        }
        await rig.Running("counted", Definition("Counted", Device("box", CounterCode)));
        await rig.Shows("counted:box:starts", "1", "a device that uses Data in Start() starts");
        Console.WriteLine("PASS device data: Data and Shared in a constructor or Setup() fail the start with a sentence; in Start() they work");
    }

    static async Task DataSurvivesARecompileAndARestart()
    {
        await using var rig = await Rig.Start("survives");
        await rig.Running("count", Definition("Count", Device("box", CounterCode)));
        await rig.Shows("count:box:starts", "1", "the first start");
        await rig.Running("count", Definition("Count", Device("box", CounterCode)), 1);
        await rig.Shows("count:box:starts", "2", "the start after a save");
        Check(File.Exists(Path.Combine(rig.Devices("count", "box"), "stopped")), "Stop() of the replaced revision wrote to its store");
        await rig.Restart();
        await rig.Shows("count:box:starts", "3", "the start after a restart");
        Console.WriteLine("PASS device data: a node's data survives a recompile and a restart, and Stop() writes to it");
    }

    static async Task TwoNodesHaveTwoStores()
    {
        await using var rig = await Rig.Start("two");
        await rig.Running("pair", Definition("Pair", Device("left", CounterCode), Device("right", CounterCode)));
        await rig.Shows("pair:left:starts", "1", "the first node counts from 1");
        await rig.Shows("pair:right:starts", "1", "the second node counts from 1");
        var stores = Directory.EnumerateDirectories(rig.Devices("pair")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Check(stores.SequenceEqual(["left", "right"]), "each node has its directory: " + string.Join(", ", stores));
        Console.WriteLine("PASS device data: two C# devices of one class in one definition have a store each");
    }

    static async Task AnAddedEntitySharesItsDevicesStore()
    {
        await using var rig = await Rig.Start("added");
        await rig.Running("pad", Definition("Pad", Device("pad", NoteCode)));
        await rig.Api.EntityCommand("pad:pad:note", "turn_on", null);
        await rig.Api.EntityCommand("pad:pad:check", "turn_on", null);
        await rig.Shows("pad:pad:read", "\"hello\"", "the device reads what its entity wrote");
        Check(Directory.EnumerateDirectories(rig.Devices("pad")).Select(Path.GetFileName).SequenceEqual(["pad"]), "the entity's data is in the device's store");
        Console.WriteLine("PASS device data: an entity a device added keeps its data in the device's store");
    }

    static async Task SharedIsOneStoreForTwoDefinitions()
    {
        await using var rig = await Rig.Start("shared");
        await rig.Running("writer", Definition("Writer", Device("hub", PeopleCode)));
        await rig.Running("reader", Definition("Reader", Device("hub", PeopleCode)));
        await rig.Api.EntityCommand("writer:hub:write", "turn_on", null);
        await rig.Shows("reader:hub:seen", "1", "the other definition's Changed action runs");
        await Task.Delay(200);
        Check(rig.Shown("writer:hub:seen") == null, "the writer's own Changed action ran: " + rig.Shown("writer:hub:seen"));
        var listed = rig.Data.Shared().Single(store => store.Name == "test.people");
        Check(listed.Users.SequenceEqual(["reader", "writer"]), "both definitions use the store: " + string.Join(", ", listed.Users));
        Console.WriteLine("PASS device data: a shared store is one for two definitions; a write runs the other's Changed action, not the writer's");
    }

    static async Task DataOfARemovedNodeGoesAtTheNextStart()
    {
        await using var rig = await Rig.Start("removed");
        await rig.Running("pair", Definition("Pair", Device("left", CounterCode), Device("right", CounterCode)));
        await rig.Shows("pair:right:starts", "1", "the node to remove counts");
        await rig.Shows("pair:left:starts", "1", "the node to keep counts");
        await rig.Running("pair", Definition("Pair", Device("left", CounterCode)), 1);
        await rig.Shows("pair:left:starts", "2", "the kept node starts again");
        Check(!Directory.Exists(rig.Devices("pair", "right")), "the removed node's data is gone once the next revision started");
        Check(File.Exists(Path.Combine(rig.Devices("pair", "left"), "starts")), "the kept node's data is kept");
        Console.WriteLine("PASS device data: the data of a node removed from a saved definition goes when the next revision starts");
    }

    static async Task DataGoesWithTheDefinition()
    {
        await using var rig = await Rig.Start("deleted");
        await rig.Running("keeper", Definition("Keeper", Device("box", KeeperCode)));
        await rig.Shows("keeper:box:starts", "1", "the keeper starts");
        Check(Directory.Exists(rig.Devices("keeper", "box")) && Directory.Exists(rig.SharedStore("test.kept")), "the keeper wrote both stores");
        await rig.Ok(new { operation = "delete", id = "keeper", expectedRevision = 1 });
        Check(!Directory.Exists(rig.Devices("keeper")), "the deleted definition's data is gone");
        Check(File.Exists(Path.Combine(rig.SharedStore("test.kept"), "x")), "the shared store it used is kept");
        Check(rig.Data.Shared().Single(store => store.Name == "test.kept").Users.Count == 0, "the deleted definition no longer uses the shared store");
        Console.WriteLine("PASS device data: a deleted definition's data goes with it; the shared stores it used stay");
    }

    static async Task OffAndFailedKeepData()
    {
        await using var rig = await Rig.Start("kept");
        await rig.Running("keep", Definition("Keep", Device("box", CounterCode)));
        await rig.Shows("keep:box:starts", "1", "the first start");
        var file = Path.Combine(rig.Devices("keep", "box"), "starts");
        string Stored() => File.Exists(file) ? File.ReadAllText(file) : "nothing";
        await rig.Ok(new { operation = "set_enabled", id = "keep", enabled = false });
        Check(Stored() == "j1", "turning it off keeps its data: " + Stored());
        long Revision(JsonObject read) => read["revision"]!.GetValue<long>();
        var revision = Revision(await rig.Ok(new { operation = "read", id = "keep" }));
        var broken = await rig.Request(new { operation = "save", id = "keep", expectedRevision = revision, definition = Definition("Keep", Device("box", BrokenCode)) });
        Check(broken["error"]?["code"]?.GetValue<string>() == "validation"
            && broken["error"]!["detail"]!["diagnostics"]!.AsArray().Any(d => d!["message"]!.GetValue<string>() == "Invalid expression term ';'"),
            "a save that does not compile is refused: " + broken.ToJsonString());
        Check(Stored() == "j1", "a save that does not compile keeps its data: " + Stored());
        await rig.Save("keep", Definition("Keep", Device("box", FailingCode)), revision);
        var failed = await rig.Ok(new { operation = "set_enabled", id = "keep", enabled = true });
        Check(!failed["active"]!.GetValue<bool>() && failed["activationError"]?.GetValue<string>().Contains("no counter answers", StringComparison.Ordinal) == true, "a start that fails: " + failed.ToJsonString());
        Check(Stored() == "j1" && Directory.Exists(rig.Devices("keep", "box")), "a start that fails keeps its data: " + Stored());
        Console.WriteLine("PASS device data: turning a definition off, a save that does not compile and a start that fails keep its data");
    }

    static async Task AStoppedDefinitionsHandlesAreClosed()
    {
        await using var rig = await Rig.Start("closed");
        await rig.Running("hander", Definition("Hander", Device("box", HanderCode)));
        await EntityTestHome.Until(() => AppContext.GetData(SharedKey) != null, "the device hands its handles over");
        var own = (SharpData)AppContext.GetData(OwnKey)!;
        var shared = (SharpData)AppContext.GetData(SharedKey)!;
        Check(own.Get<int>("x") == 1 && shared.Get<int>("x") == 1, "the handles work while the definition runs");
        Check(!rig.Data.DeleteShared("test.closing"), "a shared store in use is not deleted");
        await rig.Ok(new { operation = "set_enabled", id = "hander", enabled = false });
        Throws<ObjectDisposedException>(() => own.Get<int>("x"), "the node's handle once the definition was turned off");
        Throws<ObjectDisposedException>(() => shared.Set("x", 2), "the shared handle once the definition was turned off");
        AppContext.SetData(OwnKey, null);
        AppContext.SetData(SharedKey, null);
        Check(rig.Data.Shared().Single(store => store.Name == "test.closing").Users.Count == 0, "no definition uses the shared store once it was turned off");
        Check(rig.Data.DeleteShared("test.closing") && !Directory.Exists(rig.SharedStore("test.closing")), "the shared store is deleted once no definition uses it");
        Console.WriteLine("PASS device data: a stopped definition's handles are closed and its shared stores can be deleted");
    }

    // A C# device whose class derives the relays library's hub runs the hub's code; replaced by the library's hub node,
    // of another node id and the same key, the definition keeps its device, its switch's unique id, its page and its data,
    // which are keyed by the definition and the node's key.
    static async Task SwappingACSharpNodeForTheLibraryNodeKeepsEntitiesAndData()
    {
        await using var rig = await RelayHubFixture.Rig.Start("swap");
        await rig.Saved("relays", 0, RelayHubFixture.Code);
        const string source = "public class MyHub : RelayHub { }";
        var compiled = new NodeLibraryCompiler(Diagnostics).Preview([RelayHubFixture.Library], [new CustomNodeSource("c", source, "csharp_device")]).Snapshot.Custom["c"];
        Check(compiled.Valid, "the C# device's code compiles: " + string.Join(" | ", compiled.Diagnostics.Select(d => d.Message)));
        var mine = EntityNodeTests.CSharpDevice("mine", "hub", "Hub", new() { ["source"] = source, ["signature"] = compiled.Signature!.DeepClone(), ["arguments"] = new JsonObject { ["Address"] = Lit("string", "10.0.0.2") } });
        var before = await rig.Running("swap", Definition("Swap", mine));
        string Ids(DefinitionHost host) => string.Join(", ", host.Entities.Select(e => e.UniqueId));
        string Class(DefinitionHost host) => host.Entity("swap:hub:relay")?.AddedTo?.GetType().FullName ?? "no device";
        Check(Ids(before) == "swap:hub:relay" && Class(before) == "NodeLib.Custom_mine.MyHub", $"the C# device runs its class: {Ids(before)} {Class(before)}");
        Check(rig.Stored("swap", "hub", "starts") == "j1", "the C# device's start is counted: " + rig.Stored("swap", "hub", "starts"));
        var after = await rig.Running("swap", Definition("Swap", RelayHubFixture.Hub("library", "hub")), 1);
        Check(Ids(after) == Ids(before) && Class(after) == "NodeLib.RelayHub", $"the library's hub runs, its switch's unique id the same: {Ids(after)} {Class(after)}");
        Check(rig.Stored("swap", "hub", "starts") == "j2", "the library's hub counts on in the same data: " + rig.Stored("swap", "hub", "starts"));
        Check(JsonNode.Parse(rig.Home.Syncs("swap").Last())!["devices"]!.AsArray().Single()!["key"]!.GetValue<string>() == "hub", "the device is reported under the same key");
        Check((string)after.UiPages().Single()["nodeKey"]! == "hub", "its page is under the same key");
        Console.WriteLine("PASS device data: a C# device deriving a library's hub, swapped for the hub's node of the same key, keeps its switch's unique id, its device, its page and its data");
    }
}
