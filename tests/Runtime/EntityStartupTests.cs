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
    // Init and a retry start a definition without waiting for the start, which runs on a thread of its own (a save waits
    // for it, briefly, to answer how it went); the status is set before the entities are published.
    static Task Published(EntityTestHome home, int batches, string uniqueId) =>
        EntityTestHome.Until(() => home.Snapshots(batches).Any(s => s["uniqueId"]!.GetValue<string>() == uniqueId), uniqueId + " is published");
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
        await PruneWithoutDirectory();
        await SlowSetup();
        await SetupThatBlocksItsThreadTimesOut();
        await PruneFails();
        await UnknownClass();
        await LibraryRebuild();
        await Retry();
        await EditedOnDisk();
        await ForgetsTheStatesOfDefinitionsGone();
        await CannotStartWarnsOnceInPlainWords();
        await DoesNotCompileWarnsWithoutAPath();
        LocatesAStepWithoutALibrary();
        await ManyCompilerErrorsAreCapped();
        await ManyGraphProblemsAreCapped();
    }

    /// <summary>
    /// A compiler error on a line the source map gives to a graph step, whose id is short or long and never starts with
    /// <c>$library:</c>, is worded as any other: no exception, and no node library is named.
    /// </summary>
    static void LocatesAStepWithoutALibrary()
    {
        var located = typeof(HassSharpManager).GetMethod("Located", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
            [typeof(EntityDefinitionDocument), typeof(string)])!;
        var document = new EntityDefinitionDocument(1, "steps", 1, "Steps", null, [], null, new DefinitionLogic("graph", null, null), "",
            [new VisualSourceMap("n1", 1, 2), new VisualSourceMap("a_step_with_a_long_id", 3, 4), new VisualSourceMap("$library:maths", 5, 6)]);
        string Located(int line) => (string)located.Invoke(null, [document, $"/config/.storage/entity_steps.cs({line},5): error CS0103: The name 'x' does not exist in the current context"])!;
        Check(Located(1) == "The name 'x' does not exist in the current context (CS0103)", "a short step id: " + Located(1));
        Check(Located(3) == "The name 'x' does not exist in the current context (CS0103)", "a long step id: " + Located(3));
        Check(Located(5) == "The name 'x' does not exist in the current context (CS0103, node library maths)", "a library: " + Located(5));
        Console.WriteLine("PASS entity startup: a compiler error on a graph step's line, whatever its id, names no node library and throws nothing");
    }

    // The wording and limits an automation's startup line uses: the first 3, each cut with an ellipsis to 200 characters
    // (each of these is longer, and its cut part all its own), then how many more.
    static void CheckCapped(string? reason, string[] starts, int more, string what)
    {
        var length = HassSharpManager.LoggedProblemLength - 1;
        Check(starts.Length == 3 && starts.All(s => s.Length >= length), $"{what}: the test's starts are too short");
        var expected = string.Join("; ", starts.Select(s => s[..length] + "\u2026")) + $"; and {more} more";
        Check(reason == expected, $"{what}: not the first 3, each cut at {HassSharpManager.LoggedProblemLength} characters, and {more} more: {reason}");
    }

    /// <summary>
    /// A class definition whose code has 12 compiler errors (a library class with 12 required members, each with a long
    /// name, which the definition's code creates without setting them), loaded at startup: one warning with the first 3
    /// errors, each cut at 200 characters, and "; and 9 more"; its <c>activationError</c> is the same.
    /// </summary>
    static async Task ManyCompilerErrorsAreCapped()
    {
        await using var home = new EntityTestHome("entity-startup-many-errors");
        var members = Enumerable.Range(1, 12).Select(i => $"Part{i:00}_" + new string('x', 200)).ToArray();
        var library = "public class NeedyPlug : SharpDevice { public NeedyPlug() { Add(new SharpSwitch(\"relay\")); } "
            + string.Concat(members.Select(m => $"public required string {m} {{ get; set; }} ")) + "}";
        var first = new HassSharpManager(home.Root);
        try
        {
            await first.Init();
            await SaveLibrary(first, "needy", 0, library);
            await SaveDefinition(first, "needy", "Needy plug", null, "library:needy:NodeLib.NeedyPlug");
        }
        finally { await first.UnloadAsync(); }

        var mark = home.Logs(PyLogLevel.Warn).Length;
        var api = new HassSharpManager(home.Root);
        try
        {
            await api.Init();
            var read = await Read(api, "needy");
            var reason = read["activationError"]?.GetValue<string>();
            Check(Status(read) == "failed", "the definition answers: " + read.ToJsonString());
            CheckCapped(reason, [.. members.Take(3).Select(m => $"Required member 'NeedyPlug.{m}")], 9, "its activationError");
            var warned = home.Logs(PyLogLevel.Warn).Skip(mark).Where(l => l.StartsWith("Entity definition ", StringComparison.Ordinal)).ToArray();
            Check(warned.SequenceEqual([$"Entity definition \"Needy plug\" (needy) cannot start: {reason}"]), "one warning: " + string.Join(" | ", warned));
        }
        finally { await api.UnloadAsync(); }
        Console.WriteLine("PASS entity startup: a definition with 12 compiler errors warns once at startup with the first 3, each cut at 200 characters, and how many more; its activationError is the same");
    }

    /// <summary>
    /// A graph definition edited on disk to 12 problems (12 steps not connected into a flow, each with a long id), loaded
    /// at startup: one warning with the first 3, each cut at 200 characters, and "; and 9 more"; its <c>activationError</c>
    /// is the same.
    /// </summary>
    static async Task ManyGraphProblemsAreCapped()
    {
        await using var home = new EntityTestHome("entity-startup-many-problems");
        var definition = new
        {
            name = "Graph power",
            settings = Array.Empty<object>(),
            logic = new { kind = "graph", graph = new JsonObject { ["version"] = 5, ["name"] = "Graph power", ["edges"] = new JsonArray(),
                ["nodes"] = new JsonArray(VisualExecutionTests.Hub("p", "power", "sensor", new JsonObject { ["name"] = "Power" }, new JsonObject { ["value"] = Lit("number", 42) })) } },
        };
        var first = new HassSharpManager(home.Root);
        try
        {
            await first.Init();
            var saved = await Ok(first.EntityRequest, new { operation = "save", id = "power", expectedRevision = 0, definition });
            Check(saved["active"]!.GetValue<bool>(), "the graph does not run: " + saved.ToJsonString());
        }
        finally { await first.UnloadAsync(); }

        var ids = Enumerable.Range(1, 12).Select(i => $"gone{i:00}_" + new string('x', 220)).ToArray();
        var path = Storage(home, "entities", "power.json");
        var stored = JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var id in ids) stored["logic"]!["graph"]!["nodes"]!.AsArray().Add(N(id, "log", new JsonObject { ["value"] = Lit("string", "hi") }));
        File.WriteAllText(path, stored.ToJsonString());

        var mark = home.Logs(PyLogLevel.Warn).Length;
        var api = new HassSharpManager(home.Root);
        try
        {
            await api.Init();
            var read = await Read(api, "power");
            var reason = read["activationError"]?.GetValue<string>();
            Check(Status(read) == "failed", "the definition answers: " + read.ToJsonString());
            CheckCapped(reason, [.. ids.Take(3)], 9, "its activationError");
            var warned = home.Logs(PyLogLevel.Warn).Skip(mark).Where(l => l.StartsWith("Entity definition ", StringComparison.Ordinal)).ToArray();
            Check(warned.SequenceEqual([$"Entity definition \"Graph power\" (power) cannot start: {reason}"]), "one warning: " + string.Join(" | ", warned));
        }
        finally { await api.UnloadAsync(); }
        Console.WriteLine("PASS entity startup: a graph definition with 12 problems warns once at startup with the first 3, each cut at 200 characters, and how many more; its activationError is the same");
    }

    // A library that compiles, whose class cannot be created: its constructor is obsolete as an error, so the definition's
    // generated source, which creates it, does not compile (CS0619), at its save and at every startup.
    const string Obsolete = """
        public class OldPlug : SharpDevice
        {
            [System.Obsolete("Use the new plug", true)] public OldPlug() { Add(new SharpSwitch("relay")); }
        }
        """;

    /// <summary>
    /// A definition whose code does not compile says why as the startup line always worded it (the compiler's message, its
    /// code and the library, never the generated file's path), in its <c>activationError</c> and in the one warning a save,
    /// a startup or a reload logs; a retry for the same reason says nothing, and nothing is logged at error.
    /// </summary>
    static async Task DoesNotCompileWarnsWithoutAPath()
    {
        await using var home = new EntityTestHome("entity-startup-no-compile");
        int Mark() => home.Logs(PyLogLevel.Warn).Length;
        string[] Warned(int from) => [.. home.Logs(PyLogLevel.Warn).Skip(from).Where(l => l.StartsWith("Entity definition ", StringComparison.Ordinal))];
        void Plain(string when)
        {
            var errors = home.Logs(PyLogLevel.Error);
            Check(errors.Length == 0, $"{when}: logged at error: " + string.Join(" | ", errors));
            var shown = home.Logs(PyLogLevel.Warn).Concat(home.Logs(PyLogLevel.Critical)).ToArray();
            Check(!shown.Any(l => l.Contains(".cs", StringComparison.Ordinal) || l.Contains(home.Root, StringComparison.Ordinal) || l.Contains(".storage", StringComparison.Ordinal) || l.Contains("   at ", StringComparison.Ordinal)),
                $"{when}: a path or a stack above Debug: " + string.Join(" | ", shown));
        }
        static bool Worded(string? reason) => reason != null && reason.Contains("Use the new plug", StringComparison.Ordinal) && reason.Contains("(CS0619", StringComparison.Ordinal)
            && !reason.Contains(".cs", StringComparison.Ordinal) && !reason.Contains(".storage", StringComparison.Ordinal) && !reason.Contains('/');
        var first = new HassSharpManager(home.Root);
        try
        {
            await first.Init();
            await SaveLibrary(first, "old", 0, Obsolete);
            var mark = Mark();
            var saved = await SaveDefinition(first, "old", "Old plug", null, "library:old:NodeLib.OldPlug");
            var reason = saved["document"]?["activationError"]?.GetValue<string>() ?? saved["activationError"]?.GetValue<string>();
            Check(Worded(reason), "the save's answer: " + saved.ToJsonString());
            Check(Warned(mark).SequenceEqual([$"Entity definition \"Old plug\" (old) cannot start: {reason}"]), "the save warns once, without a path: " + string.Join(" | ", Warned(mark)));
            Plain("the save");
        }
        finally { await first.UnloadAsync(); }

        foreach (var when in new[] { "at startup", "after a reload" })
        {
            var mark = Mark();
            var api = new HassSharpManager(home.Root);
            try
            {
                await api.Init();
                var read = await Read(api, "old");
                var reason = read["activationError"]?.GetValue<string>();
                Check(Status(read) == "failed" && Worded(reason), $"{when}: the definition answers: " + read.ToJsonString());
                Check(Warned(mark).SequenceEqual([$"Entity definition \"Old plug\" (old) cannot start: {reason}"]), $"{when}: one warning: " + string.Join(" | ", home.Logs().TakeLast(8)));
                mark = Mark();
                await api.RetryFailedLoads();
                Check(Warned(mark).Length == 0, $"{when}: a retry for the same reason warned: " + string.Join(" | ", Warned(mark)));
                Plain(when);
            }
            finally { await api.UnloadAsync(); }
        }
        Console.WriteLine("PASS entity startup: a definition whose code does not compile warns once, at its save, at startup and after a reload, without the generated file's path; a retry for the same reason says nothing");
    }

    // Definitions that cannot start: one whose Setup() throws a sentence of its own over a .NET failure, one whose Setup()
    // downloads from a port nothing listens on, and one that starts only while its marker file exists.
    const string Failing = """
        public class FailingPlug : SharpDevice
        {
            public FailingPlug() { Add(new SharpSwitch("relay")); }
            protected override Task Setup() => throw new InvalidOperationException("The plug at 10.0.9.1 does not answer",
                new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));
        }
        public class FetchingPlug : SharpDevice
        {
            [Setting] public string Host { get; set; } = "";
            public FetchingPlug() { Add(new SharpSensor("table")); }
            protected override async Task Setup() => await Files.Download(Host + "/table.bin", new string('0', 64), cancellationToken: Stopping);
        }
        public class FlakyPlug : SharpDevice
        {
            [Setting] public string Host { get; set; } = "";
            public FlakyPlug() { Add(new SharpSwitch("relay")); }
            protected override Task Setup() => System.IO.File.Exists(Host) ? Task.CompletedTask : throw new InvalidOperationException("The plug is not ready");
        }
        """;

    /// <summary>
    /// A definition that cannot start, at startup, after a reload and on a retry, logs one warning in the words a save
    /// uses, once per reason, with why in plain words (the outer sentence, not the innermost .NET failure); nothing at
    /// error, no stack and no file path above Debug. A graph's failing When started flow is not logged at error too. A
    /// retry that works forgets the reason, so the same failure later warns again.
    /// </summary>
    static async Task CannotStartWarnsOnceInPlainWords()
    {
        await using var home = new EntityTestHome("entity-startup-cannot-start");
        var timeout = DefinitionHost.SetupTimeout;
        DefinitionHost.SetupTimeout = TimeSpan.FromSeconds(60);
        // A port nothing listens on: a download from it is refused.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var closed = $"http://127.0.0.1:{((System.Net.IPEndPoint)listener.LocalEndpoint).Port}";
        listener.Stop();
        var marker = Path.Combine(home.Root, "flaky-ready");
        var download = new
        {
            name = "Graph download",
            settings = Array.Empty<object>(),
            logic = new { kind = "graph", graph = new JsonObject { ["version"] = 5, ["name"] = "Graph download", ["edges"] = new JsonArray(E("setup", "download")),
                ["nodes"] = new JsonArray(VisualExecutionTests.Hub("model", "model", "sensor", new JsonObject { ["name"] = "Model" }), N("setup", "entity_setup"), N("download", "download_file", new JsonObject { ["url"] = Lit("string", closed + "/model.bin"), ["sha256"] = Lit("string", new string('0', 64)) })) } },
        };
        const string refused = "The file from 127.0.0.1 could not be downloaded: Connection refused";
        var reasons = new Dictionary<string, (string Name, string Reason)>
        {
            ["failing"] = ("Failing plug", "The plug at 10.0.9.1 does not answer (Connection refused)"),
            ["fetch"] = ("Fetching plug", refused),
            ["graphdl"] = ("Graph download", refused),
            ["flaky"] = ("Flaky plug", "The plug is not ready"),
        };
        (int Warn, int Error, int Info) Mark() => (home.Logs(PyLogLevel.Warn).Length, home.Logs(PyLogLevel.Error).Length, home.Logs(PyLogLevel.Info).Length);
        string[] CannotStart((int Warn, int, int) from) => [.. home.Logs(PyLogLevel.Warn).Skip(from.Warn).Where(l => l.StartsWith("Entity definition ", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
        // Every line from the mark on above Debug: none at error, none with a stack or a path.
        void Quiet((int Warn, int Error, int Info) from, string when)
        {
            var errors = home.Logs(PyLogLevel.Error).Skip(from.Error).ToArray();
            Check(errors.Length == 0, $"{when}: logged at error: " + string.Join(" | ", errors));
            var above = home.Logs(PyLogLevel.Warn).Skip(from.Warn).Concat(home.Logs(PyLogLevel.Info).Skip(from.Info)).ToArray();
            Check(!above.Any(l => l.Contains("   at ", StringComparison.Ordinal) || l.Contains(".cs", StringComparison.Ordinal) || l.Contains(home.Root, StringComparison.Ordinal)),
                $"{when}: a stack or a path above Debug: " + string.Join(" | ", above));
        }
        string[] Expected(params string[] ids) => [.. ids.Select(id => $"Entity definition \"{reasons[id].Name}\" ({id}) cannot start: {reasons[id].Reason}").Order(StringComparer.Ordinal)];
        async Task Failed(HassSharpManager api, string when)
        {
            foreach (var (id, (_, reason)) in reasons)
            {
                var read = await Read(api, id);
                Check(Status(read) == "failed" && read["activationError"]?.GetValue<string>() == reason, $"{when}: {id} answers: " + read.ToJsonString());
            }
        }
        var first = new HassSharpManager(home.Root);
        try
        {
            await first.Init();
            await SaveLibrary(first, "plugs", 0, Failing);
            var mark = Mark();
            await SaveDefinition(first, "failing", "Failing plug", null, "library:plugs:NodeLib.FailingPlug");
            await SaveDefinition(first, "fetch", "Fetching plug", closed, "library:plugs:NodeLib.FetchingPlug");
            await Ok(first.EntityRequest, new { operation = "save", id = "graphdl", expectedRevision = 0, definition = download });
            await SaveDefinition(first, "flaky", "Flaky plug", marker, "library:plugs:NodeLib.FlakyPlug");
            Check(CannotStart(mark).SequenceEqual(Expected("failing", "fetch", "graphdl", "flaky")), "a save warns once of each: " + string.Join(" | ", home.Logs()));
            Quiet(mark, "the saves");
            await Failed(first, "saved");
        }
        finally { await first.UnloadAsync(); }

        try
        {
            foreach (var when in new[] { "at startup", "after a reload" })
            {
                var mark = Mark();
                var api = new HassSharpManager(home.Root);
                try
                {
                    await api.Init();
                    Check(CannotStart(mark).SequenceEqual(Expected("failing", "fetch", "graphdl", "flaky")), $"{when}: one warning for each: " + string.Join(" | ", home.Logs().TakeLast(12)));
                    Quiet(mark, when);
                    await Failed(api, when);
                    Console.WriteLine($"PASS entity startup: {when}, a definition that cannot start logs one warning in plain words, nothing at error, and no stack or path");
                    if (when == "at startup") continue;

                    // A retry for the same reasons says nothing more; one that works forgets the reason, so the same failure warns again.
                    mark = Mark();
                    await api.RetryFailedLoads();
                    Check(CannotStart(mark).Length == 0, "a retry for the same reason warned: " + string.Join(" | ", CannotStart(mark)));
                    File.WriteAllText(marker, "");
                    var batches = home.Pushes().Length;
                    await api.RetryFailedLoads();
                    await Published(home, batches, "flaky:relay");
                    Check(Status(await Read(api, "flaky")) == "running", "the flaky plug does not run after the retry");
                    File.Delete(marker);
                    await Ok(api.EntityRequest, new { operation = "save", id = "flaky", expectedRevision = 1, definition = EntityApiTests.Definition("Flaky plug", marker, "library:plugs:NodeLib.FlakyPlug") });
                    var again = CannotStart(mark);
                    Check(again.Length == 1 && again[0].StartsWith("Entity definition \"Flaky plug\" (flaky) cannot start", StringComparison.Ordinal) && again[0].EndsWith(": The plug is not ready", StringComparison.Ordinal),
                        "a failure after a retry that worked does not warn again: " + string.Join(" | ", again));
                    Quiet(mark, "the retries");
                    Console.WriteLine("PASS entity startup: a retry for the same reason warns no more, and one that works forgets it, so the same failure later warns again");
                }
                finally { await api.UnloadAsync(); }
            }
        }
        finally { DefinitionHost.SetupTimeout = timeout; }
    }

    /// <summary>
    /// At startup the last states of entities no definition has are dropped, with the prune; those of a definition that
    /// exists, on or off, are kept, and a class's switch comes back with its own.
    /// </summary>
    static async Task ForgetsTheStatesOfDefinitionsGone()
    {
        await using var home = new EntityTestHome("entity-startup-states");
        static EntityStateStore States(HassSharpManager api) =>
            ((UserScriptManager)typeof(HassSharpManager).GetField("_userScriptManager", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(api)!).States!;
        var first = new HassSharpManager(home.Root);
        try
        {
            await first.Init();
            await SaveLibrary(first, "shelly", 0, EntityApiTests.Library);
            await SaveDefinition(first, "plug1", "Plug one", "10.0.2.1");
            await SaveDefinition(first, "plug2", "Plug two", "10.0.2.2", enabled: false);
            await first.EntityCommand("plug1:relay", "turn_on", null);
            await EntityTestHome.Until(() => States(first).Ids().Contains("plug1:relay"), "the switch remembered");
            // A definition deleted while Home Assistant was not running, as a restored backup may leave it, and one that is off.
            States(first).Remember("gone:relay", new JsonObject { ["is_on"] = true });
            States(first).Remember("plug2:relay", new JsonObject { ["is_on"] = true });
        }
        finally { await first.UnloadAsync(); }

        var batches = home.Pushes().Length;
        var api = new HassSharpManager(home.Root);
        try
        {
            await api.Init();
            var ids = States(api).Ids().Order(StringComparer.Ordinal).ToArray();
            Check(ids.SequenceEqual(["plug1:relay", "plug2:relay"]), "the states kept at startup: " + string.Join(", ", ids));
            await EntityTestHome.Until(() => home.Snapshots(batches).Any(s => s["uniqueId"]!.GetValue<string>() == "plug1:relay"), "plug1 starts");
            var relay = home.Snapshots(batches).First(s => s["uniqueId"]!.GetValue<string>() == "plug1:relay");
            Check(relay["values"]!["is_on"]?.GetValue<bool>() == true, "the class's switch comes back on: " + relay.ToJsonString());
            await States(api).FlushAsync();
            Check(!File.ReadAllText(Storage(home, "entity_states.json")).Contains("gone:"), "the file keeps the states of a definition gone");
            Console.WriteLine("PASS entity startup: the last states of definitions gone are dropped at startup, and a class's switch comes back with its own");
        }
        finally { await api.UnloadAsync(); }
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
            Check(home.Prunes().Length == 0, "a start without the definitions' directory prunes: " + string.Join(" | ", home.Prunes()));
            await SaveLibrary(first, "shelly", 0, EntityApiTests.Library);
            await SaveDefinition(first, "plug1", "Plug one", "10.0.1.1");
            await SaveDefinition(first, "plug2", "Plug two", "10.0.1.2");
            await SaveDefinition(first, "plug3", "Plug three", "10.0.1.3", enabled: false);
        }
        finally { await first.UnloadAsync(); }

        File.WriteAllText(Storage(home, "entities", "plug2.json"), "{");
        var (syncs, broken, batches) = (home.Syncs("plug1").Length, home.Syncs("plug2").Length, home.Pushes().Length);
        var api = new HassSharpManager(home.Root);
        try
        {
            await api.Init();
            await Published(home, batches, "plug1:relay");
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

            // Listed again, it is not logged again; a library save, which must check every definition, names the file.
            await Request(api.EntityRequest, new { operation = "list" });
            await Request(api.EntityRequest, new { operation = "list" });
            var skipped = home.Logs().Where(line => line.Contains("Skipping entity definition")).ToArray();
            Check(skipped.Length == 1 && skipped[0].Contains("plug2.json: ") && !skipped[0].Contains("JsonException") && !skipped[0].Contains("   at "),
                "the unreadable file is logged once, without the stack: " + string.Join(" | ", skipped));
            var refused = await Request(api.NodeLibraryRequest, new { operation = "save", id = "shelly", name = "shelly", expectedRevision = 1, source = EntityApiTests.Library });
            Check(refused["ok"]!.GetValue<bool>() == false && refused["error"]!["code"]!.GetValue<string>() == "invalid_request"
                && refused["error"]!["detail"]!.GetValue<string>().StartsWith("Cannot read entity definition " + Storage(home, "entities", "plug2.json") + ": ", StringComparison.Ordinal),
                "a library save over an unreadable definition: " + refused.ToJsonString());
            Console.WriteLine("PASS entity startup: an unreadable definition file is logged once without its stack, and a library save it blocks names it");

            var plug3 = await Read(api, "plug3");
            Check(Status(plug3) == "off" && !plug3["active"]!.GetValue<bool>() && !plug3["enabled"]!.GetValue<bool>() && home.Syncs("plug3").Length == 0,
                "a disabled definition loaded at startup: " + plug3.ToJsonString());
            Console.WriteLine("PASS entity startup: a disabled definition is not loaded at startup and is still kept by the prune");
        }
        finally { await api.UnloadAsync(); }
    }

    // Without the definitions' directory, as after a partial restore of .storage, nothing is pruned; with an empty one,
    // every definition's entities are, as before.
    static async Task PruneWithoutDirectory()
    {
        await using var home = new EntityTestHome("entity-startup-no-directory");
        var api = new HassSharpManager(home.Root);
        try { await api.Init(); }
        finally { await api.UnloadAsync(); }
        Check(home.Prunes().Length == 0, "a start without the definitions' directory prunes: " + string.Join(" | ", home.Prunes()));
        // A fresh install has no directory either, so the skip is information, and nothing at warning or above is logged.
        const string skipped = "No entity definitions directory: nothing is pruned from Home Assistant";
        Check(home.Logs(PyLogLevel.Info).Contains(skipped), "the skipped prune is not logged at info: " + string.Join(" | ", home.Logs()));
        var louder = new[] { PyLogLevel.Warn, PyLogLevel.Error, PyLogLevel.Critical }.SelectMany(home.Logs).ToArray();
        Check(louder.Length == 0, "a start without the definitions' directory logs at warning or above: " + string.Join(" | ", louder));
        Directory.CreateDirectory(Storage(home, "entities"));
        api = new HassSharpManager(home.Root);
        try { await api.Init(); }
        finally { await api.UnloadAsync(); }
        Check(home.Prunes().SequenceEqual(["[]"]), "a start with an empty definitions' directory prunes everything: " + string.Join(" | ", home.Prunes()));
        Console.WriteLine("PASS entity startup: a start without the definitions' directory prunes nothing and logs that at info; one with an empty directory prunes every definition");
    }

    // A definition whose Setup() never ends fails at startup within the setup bound, and the definition after it runs.
    static async Task SlowSetup()
    {
        const string Hanging = """
            public class HangingPlug : SharpDevice
            {
                public HangingPlug() { Add(new SharpSwitch("relay")); }
                protected override Task Setup() => Task.Delay(Timeout.Infinite);
            }
            """;
        await using var home = new EntityTestHome("entity-startup-slow-setup");
        DefinitionHost.SetupTimeout = TimeSpan.FromMilliseconds(200);
        const string message = "Hanging plug: Setup() did not finish within 0.2 s";
        var first = new HassSharpManager(home.Root);
        try
        {
            await first.Init();
            await SaveLibrary(first, "shelly", 0, EntityApiTests.Library + Hanging);
            var saved = await SaveDefinition(first, "hang", "Hanging plug", null, "library:shelly:NodeLib.HangingPlug");
            Check(!saved["active"]!.GetValue<bool>() && saved["activationError"]?.GetValue<string>() == message, "saving the hanging definition: " + saved.ToJsonString());
            await SaveDefinition(first, "plug1", "Plug one", "10.0.4.1");
        }
        finally { await first.UnloadAsync(); }

        var batches = home.Pushes().Length;
        var api = new HassSharpManager(home.Root);
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await api.Init().WaitAsync(TimeSpan.FromSeconds(30));
            Check(clock.Elapsed < TimeSpan.FromSeconds(15), $"a Setup() that never ends held startup {clock.Elapsed.TotalSeconds:F1} s");
            var hang = await Read(api, "hang");
            Check(Status(hang) == "failed" && hang["activationError"]?.GetValue<string>() == message, "the hanging definition after the restart: " + hang.ToJsonString());
            await Published(home, batches, "plug1:relay");
            Check(Status(await Read(api, "plug1")) == "running", "the definition after the hanging one does not run");
            Console.WriteLine("PASS entity startup: a definition whose Setup() never ends fails within the setup bound and the definitions after it load at Init");
        }
        finally { await api.UnloadAsync(); }
    }

    // A definition whose Setup() blocks its thread (a raw connect to a device that is off, a Wait()) fails within the setup
    // bound too, when saved and at startup, and the definition after it runs.
    static async Task SetupThatBlocksItsThreadTimesOut()
    {
        const string Blocking = """
            public class BlockingPlug : SharpDevice
            {
                public static readonly System.Collections.Concurrent.ConcurrentQueue<Thread> Blocked = new();
                public BlockingPlug() { Add(new SharpSwitch("relay")); }
                protected override Task Setup()
                {
                    Blocked.Enqueue(Thread.CurrentThread);
                    Thread.Sleep(Timeout.Infinite);
                    return Task.CompletedTask;
                }
            }
            """;
        await using var home = new EntityTestHome("entity-startup-blocking-setup");
        DefinitionHost.SetupTimeout = TimeSpan.FromMilliseconds(200);
        const string message = "Blocking plug: Setup() did not finish within 0.2 s";
        // Wakes each blocked Setup() once (its Sleep throws), before an unload: a Setup() still blocking the caller would
        // otherwise hold that unload, and a thread blocked for good would keep the library's load context. A Setup() not
        // started yet when the bound passed is waited for, up to five seconds, until this many have been woken in all;
        // answers how many have.
        var woken = 0;
        async Task<int> Wake(int expected)
        {
            for (var clock = System.Diagnostics.Stopwatch.StartNew(); ; await Task.Delay(5))
            {
                foreach (var type in AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("NodeLib.BlockingPlug", false)).OfType<Type>())
                    for (var blocked = (System.Collections.Concurrent.ConcurrentQueue<Thread>)type.GetField("Blocked")!.GetValue(null)!; blocked.TryDequeue(out var thread); woken++)
                        thread.Interrupt();
                if (woken >= expected || clock.Elapsed >= TimeSpan.FromSeconds(5)) return woken;
            }
        }
        var first = new HassSharpManager(home.Root);
        try
        {
            await first.Init();
            await SaveLibrary(first, "shelly", 0, EntityApiTests.Library + Blocking);
            // Called from the pool: a Setup() run on the caller's thread would block this test, not fail it.
            var saved = await Task.Run(() => SaveDefinition(first, "block", "Blocking plug", null, "library:shelly:NodeLib.BlockingPlug")).WaitAsync(TimeSpan.FromSeconds(30));
            Check(!saved["active"]!.GetValue<bool>() && saved["activationError"]?.GetValue<string>() == message, "saving the blocking definition: " + saved.ToJsonString());
            Check(await Wake(1) == 1, "the saved definition's Setup() never blocked its thread");
            await SaveDefinition(first, "plug1", "Plug one", "10.0.5.1");
        }
        finally { await Wake(1); await first.UnloadAsync(); }

        var batches = home.Pushes().Length;
        var api = new HassSharpManager(home.Root);
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await Task.Run(api.Init).WaitAsync(TimeSpan.FromSeconds(30));
            Check(clock.Elapsed < TimeSpan.FromSeconds(15), $"a Setup() that blocks its thread held startup {clock.Elapsed.TotalSeconds:F1} s");
            var block = await Read(api, "block");
            Check(Status(block) == "failed" && block["activationError"]?.GetValue<string>() == message, "the blocking definition after the restart: " + block.ToJsonString());
            await Published(home, batches, "plug1:relay");
            Check(Status(await Read(api, "plug1")) == "running", "the definition after the blocking one does not run");
            Check(await Wake(2) == 2, "the Setup() at Init never blocked its thread");
            Console.WriteLine("PASS entity startup: a definition whose Setup() blocks its thread fails within the setup bound when saved and at Init, and the definitions after it load");
        }
        finally { await Wake(2); await api.UnloadAsync(); }
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
        var batches = home.Pushes().Length;
        var failing = new HassSharpManager(home.Root);
        try
        {
            await failing.Init().WaitAsync(TimeSpan.FromSeconds(30));
            Check(home.Logs().Any(line => line.Contains("Cannot prune") && line.Contains("the registry is locked")), "the failing prune is not logged: " + string.Join(" | ", home.Logs()));
            await Published(home, batches, "plug1:relay");
            Check(Status(await Read(failing, "plug1")) == "running", "plug1 does not run after a failing prune");
        }
        finally { await failing.UnloadAsync(); }

        home.FailPrune = false; home.IgnorePrune = true;
        HassSharpManager.RemovalTimeout = TimeSpan.FromMilliseconds(200);
        batches = home.Pushes().Length;
        var quiet = new HassSharpManager(home.Root);
        try
        {
            await quiet.Init().WaitAsync(TimeSpan.FromSeconds(5));
            Check(home.Logs().Any(line => line.Contains("Cannot prune") && line.Contains("did not answer")), "the unanswered prune is not logged: " + string.Join(" | ", home.Logs()));
            await Published(home, batches, "plug1:relay");
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

        var batches = home.Pushes().Length;
        var api = new HassSharpManager(home.Root);
        try
        {
            await api.Init();
            var plug1 = await Read(api, "plug1");
            var unknown = $"No entity class {EntityApiTests.Plug} in the node libraries";
            Check(Status(plug1) == "failed" && !plug1["active"]!.GetValue<bool>() && plug1["activationError"]?.GetValue<string>() == unknown,
                "plug1's load error: " + plug1.ToJsonString());
            // One warning, as for any definition that cannot start; nothing at error. A retry for the same reason says nothing.
            var warned = $"Entity definition \"Plug one\" (plug1) cannot start: {unknown}";
            Check(home.Logs(PyLogLevel.Warn).Count(l => l == warned) == 1 && !home.Logs(PyLogLevel.Error).Any(l => l.Contains("entity definition", StringComparison.OrdinalIgnoreCase)),
                "the load failure is not warned of once: " + string.Join(" | ", home.Logs()));
            var before = home.Logs(PyLogLevel.Warn).Length;
            await api.RetryFailedLoads();
            Check(home.Logs(PyLogLevel.Warn).Length == before && !home.Logs(PyLogLevel.Error).Any(l => l.Contains("entity definition", StringComparison.OrdinalIgnoreCase)),
                "a retry for the same reason logged: " + string.Join(" | ", home.Logs().TakeLast(4)));
            await Published(home, batches, "spare:relay");
            Check(Status(await Read(api, "spare")) == "running" && VisualTests.Loaded(api, "auto") != null, "the other definition or the automation does not run");
            Check(home.Prunes()[^1] == """["plug1","spare"]""", "the prune: " + home.Prunes()[^1]);
            Console.WriteLine("PASS entity startup: a definition whose class is gone records why and the other definitions and automations load");

            // Turned off and on again, it still does not load: it records why rather than run the code its file embeds.
            await Ok(api.EntityRequest, new { operation = "set_enabled", id = "plug1", enabled = false });
            var on = await Ok(api.EntityRequest, new { operation = "set_enabled", id = "plug1", enabled = true });
            Check(on.ToJsonString() == JsonSerializer.Serialize(new { id = "plug1", enabled = true, active = false, activationError = unknown }), "turning on a definition whose class is gone answers: " + on.ToJsonString());
            plug1 = await Read(api, "plug1");
            Check(Status(plug1) == "failed" && plug1["activationError"]?.GetValue<string>() == unknown && Revision(plug1) == 1, "plug1 turned on with its class gone: " + plug1.ToJsonString());
            Console.WriteLine("PASS entity startup: turning on a definition whose class is gone records why and runs nothing");

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
            Check(Ids(changed["definitions"]).SequenceEqual(["plug1", "plug3"]), "the library save lists every definition it rebuilt: " + changed.ToJsonString());
            var activation = changed["definitions"]![0]!;
            Check(activation["id"]!.GetValue<string>() == "plug1" && activation["enabled"]!.GetValue<bool>() && activation["active"]!.GetValue<bool>() && activation["activationError"] == null
                && Revision(activation["document"]!.AsObject()) == 2 && changed["invalidDefinitions"]!.AsArray().Count == 0, "the rebuild: " + changed.ToJsonString());
            // One that is off is rebuilt and listed, off: it does not run, and nothing says it failed.
            var dormant = changed["definitions"]![1]!;
            Check(!dormant["enabled"]!.GetValue<bool>() && !dormant["active"]!.GetValue<bool>() && dormant["activationError"] == null
                && Revision(dormant["document"]!.AsObject()) == 2 && Status(dormant["document"]!.AsObject()) == "off" && home.Syncs("plug3").Length == 0,
                "the library save lists a definition that is off: " + dormant.ToJsonString());
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

    // A library edited on disk, then a restart: every definition runs the code of the libraries as now saved, whether it
    // was on at the restart, turned off and on after it, or turned on only after it; the stored revision stays.
    static async Task EditedOnDisk()
    {
        await using var home = new EntityTestHome("entity-startup-edited");
        var first = new HassSharpManager(home.Root);
        try
        {
            await first.Init();
            await SaveLibrary(first, "shelly", 0, EntityApiTests.Library);
            await SaveDefinition(first, "plug1", "Plug one", "10.0.3.1");
            await SaveDefinition(first, "plug4", "Plug four", "10.0.3.4", enabled: false);
        }
        finally { await first.UnloadAsync(); }

        // FakePlug as a direct file edit leaves it: same revision, another turn_on.
        void Edit(string word)
        {
            var path = Storage(home, "libraries", "shelly.json");
            var library = JsonNode.Parse(File.ReadAllText(path))!;
            library["source"] = EntityApiTests.Library.Replace("Calls.Enqueue(\"turn_on \" + Host)", $"Calls.Enqueue(\"{word} turn_on \" + Host)");
            File.WriteAllText(path, library.ToJsonString());
        }
        int Ran(string command) => EntityTestHome.Commands().Count(c => c == command);
        Edit("disk");
        var batches = home.Pushes().Length;
        var api = new HassSharpManager(home.Root);
        try
        {
            await api.Init();
            await Published(home, batches, "plug1:relay");
            var plug1 = await Read(api, "plug1");
            Check(Status(plug1) == "running" && plug1["currentRevisionActive"]!.GetValue<bool>() && Revision(plug1) == 1 && plug1["source"]!.GetValue<string>().Contains("disk turn_on"),
                "plug1 after the restart: " + plug1.ToJsonString());
            await api.EntityCommand("plug1:relay", "turn_on", null);
            Check(Ran("disk turn_on 10.0.3.1") == 1 && Ran("turn_on 10.0.3.1") == 0, "plug1 after the restart: " + string.Join(", ", EntityTestHome.Commands()));
            Console.WriteLine("PASS entity startup: a restart after a library edited on disk runs and stores the new code at the same revision");

            await Ok(api.EntityRequest, new { operation = "set_enabled", id = "plug1", enabled = false });
            await Ok(api.EntityRequest, new { operation = "set_enabled", id = "plug1", enabled = true });
            // The assembly turned off took the first call with it if it was collected: count from here.
            EntityTestHome.ClearCommands();
            await api.EntityCommand("plug1:relay", "turn_on", null);
            Check(Ran("disk turn_on 10.0.3.1") == 1 && Ran("turn_on 10.0.3.1") == 0, "plug1 turned off and on: " + string.Join(", ", EntityTestHome.Commands()));
            var plug4 = await Read(api, "plug4");
            Check(Revision(plug4) == 1 && plug4["source"]!.GetValue<string>().Contains("disk turn_on"), "the disabled plug4's stored source: " + plug4.ToJsonString());
            await Ok(api.EntityRequest, new { operation = "set_enabled", id = "plug4", enabled = true });
            await api.EntityCommand("plug4:relay", "turn_on", null);
            Check(Ran("disk turn_on 10.0.3.4") == 1 && Ran("turn_on 10.0.3.4") == 0 && (await Read(api, "plug4"))["currentRevisionActive"]!.GetValue<bool>(),
                "plug4 turned on after the restart: " + string.Join(", ", EntityTestHome.Commands()));
            Console.WriteLine("PASS entity startup: after that restart, a definition turned off and on, or on for the first time, runs the new code");

            // Edited again while running: turning it off and on runs the libraries as saved now.
            Edit("again");
            await Ok(api.EntityRequest, new { operation = "set_enabled", id = "plug1", enabled = false });
            await Ok(api.EntityRequest, new { operation = "set_enabled", id = "plug1", enabled = true });
            await api.EntityCommand("plug1:relay", "turn_on", null);
            plug1 = await Read(api, "plug1");
            Check(Ran("again turn_on 10.0.3.1") == 1 && plug1["currentRevisionActive"]!.GetValue<bool>() && Revision(plug1) == 1, "plug1 turned on after another edit: " + plug1.ToJsonString());
            Console.WriteLine("PASS entity startup: turning a definition on runs the libraries as saved at that moment");
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
            Check(Status(failed) == "failed" && failed["activationError"]?.GetValue<string>().Contains("The node libraries have errors") == true,
                "lamp1 loaded without the entity classes: " + failed.ToJsonString());
            var batches = home.Pushes().Length;
            api.SetEntityDescriptions(Fixture("ha-entity-components.json"), started: true);
            Check(api.RetriesQueued == 1, "the push did not retry: " + api.RetriesQueued);
            await api.RetriesDone();
            await Published(home, batches, "lamp1:relay");
            var lamp1 = await Read(api, "lamp1");
            Check(Status(lamp1) == "running" && lamp1["activationError"] == null && home.Syncs("lamp1").Length == syncs + 1, "lamp1 after the retry: " + lamp1.ToJsonString());
            Console.WriteLine("PASS entity startup: a definition that failed to load for want of the entity classes loads when they are pushed");
        }
        finally { await api.UnloadAsync(); }
    }
}
