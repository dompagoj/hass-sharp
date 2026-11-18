using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>
/// The store of the entities' last states on its own, over a temporary directory: what one instance writes another
/// reads back, in the file's documented shape; changes are written behind, once for many, and at once on a flush; a file
/// that cannot be read is logged once and taken as empty, then replaced; forgotten ids are gone from the file; and a
/// write that fails is logged once and tried again until it succeeds.
/// </summary>
static class EntityStateStoreTests
{
    [RuntimeSuite("--entity-restore", Order = 17)]
    public static async Task Run()
    {
        var (log, interval) = (PyInterop.Log, EntityStateStore.FlushInterval);
        try
        {
            await RoundTrip();
            await WriteBehind();
            await CorruptFile();
            await Forget();
            await FailedWrite();
            await ClosingStopsTheRetries();
            await ClosedOnUnload();
        }
        finally
        {
            PyInterop.Log = log;
            EntityStateStore.FlushInterval = interval;
        }
    }

    // A temporary directory and the store's file in it; logs recorded meanwhile, by level.
    sealed class Scratch : IDisposable
    {
        public readonly List<(int Level, string Message)> Logged = [];
        public Scratch()
        {
            Directory = Path.Combine(Path.GetTempPath(), "entity-restore-" + Guid.NewGuid());
            PyInterop.Log = (level, message) => { lock (Logged) Logged.Add((level, message)); };
        }
        public string Directory { get; }
        public string File => Path.Combine(Directory, ".storage", "hass_sharp", "entity_states.json");
        public string[] Warnings() { lock (Logged) return Logged.Where(l => l.Level == (int)PyLogLevel.Warn).Select(l => l.Message).ToArray(); }
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true); }
    }

    static JsonObject Values(string json) => JsonNode.Parse(json)!.AsObject();
    // Sets who may read and write the file (the suites run on Linux and macOS).
    static void Mode(string path, UnixFileMode mode) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode); }

    static async Task Until(Func<bool> condition, string what)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Check(clock.Elapsed < TimeSpan.FromSeconds(5), "timed out waiting: " + what);
            await Task.Delay(10);
        }
    }

    /// <summary>What one instance remembers and flushes another reads back, in the file's documented shape.</summary>
    static async Task RoundTrip()
    {
        using var scratch = new Scratch();
        var before = DateTime.UtcNow;
        var store = new EntityStateStore(scratch.File);
        store.Load();
        store.Remember("relay:relay", Values("""{"is_on":true}"""));
        store.Remember("panel:lamp", Values("""{"is_on":false,"brightness":120}"""));
        store.Remember("panel:mode", Values("""{"option":"high"}"""));
        await store.FlushAsync();
        var file = JsonNode.Parse(File.ReadAllText(scratch.File))!.AsObject();
        Check(file["version"]!.GetValue<int>() == 1 && file["states"]!.AsObject().Select(s => s.Key).SequenceEqual(["panel:lamp", "panel:mode", "relay:relay"]),
            "the file's shape: " + file.ToJsonString());
        var lamp = file["states"]!["panel:lamp"]!;
        Check(lamp["values"]!.ToJsonString() == """{"is_on":false,"brightness":120}""" && DateTime.Parse(lamp["utc"]!.GetValue<string>()).ToUniversalTime() >= before.AddSeconds(-1),
            "an entry is its values and when: " + lamp.ToJsonString());
        Check(!File.Exists(scratch.File + ".tmp"), "the temporary file is left behind");

        var again = new EntityStateStore(scratch.File);
        again.Load();
        Check(again.TryGet("panel:lamp", out var values) && values.ToJsonString() == """{"is_on":false,"brightness":120}""", "read back: " + values.ToJsonString());
        Check(again.TryGet("relay:relay", out values) && values.ToJsonString() == """{"is_on":true}""", "read back: " + values.ToJsonString());
        Check(!again.TryGet("relay:other", out values) && values.Count == 0, "an id never remembered");
        // What TryGet gives is the caller's own: changing it changes nothing kept.
        values = again.TryGet("panel:mode", out var mode) ? mode : [];
        values["option"] = "low";
        Check(again.TryGet("panel:mode", out mode) && mode.ToJsonString() == """{"option":"high"}""", "a copy was changed in the store");
        Check(scratch.Warnings().Length == 0, "a round trip warned: " + string.Join(" | ", scratch.Warnings()));
        Console.WriteLine("PASS entity restore: what one store remembers and flushes, another reads back, in the documented shape");
    }

    /// <summary>Many changes are written once, after the interval; a flush writes at once, and only when something changed.</summary>
    static async Task WriteBehind()
    {
        using var scratch = new Scratch();
        EntityStateStore.FlushInterval = TimeSpan.FromMilliseconds(300);
        var store = new EntityStateStore(scratch.File);
        store.Load();
        for (var i = 0; i <= 20; i++) store.Remember("dial:dial", Values($$"""{"value":{{i}}}"""));
        Check(store.Writes == 0 && !File.Exists(scratch.File), "a change was written at once");
        await Until(() => store.Writes == 1, "the changes written behind");
        await Task.Delay(500);
        Check(store.Writes == 1, $"{store.Writes} writes for many changes");
        Check(JsonNode.Parse(File.ReadAllText(scratch.File))!["states"]!["dial:dial"]!["values"]!.ToJsonString() == """{"value":20}""", "the last change is written");
        // The same values again are no change.
        store.Remember("dial:dial", Values("""{"value":20}"""));
        await store.FlushAsync();
        Check(store.Writes == 1, "unchanged values were written again");
        store.Remember("dial:dial", Values("""{"value":21}"""));
        await store.FlushAsync();
        Check(store.Writes == 2 && File.ReadAllText(scratch.File).Contains("\"value\":21"), "a flush writes at once");
        await Task.Delay(500);
        Check(store.Writes == 2, "the change a flush wrote was written again");
        Console.WriteLine("PASS entity restore: changes are written behind, once for many, and at once on a flush");
    }

    /// <summary>
    /// Nothing a file holds throws out of the store. One that cannot be read at all is logged once and taken as empty,
    /// and the next write replaces it; one with entries that are not well formed keeps the others, and says so once.
    /// </summary>
    static async Task CorruptFile()
    {
        const string Good = """{"values":{"is_on":true},"utc":"2026-11-18T10:00:00.0000000Z"}""";
        foreach (var (what, broken) in new[] {
            ("not JSON", "{"), ("empty", ""), ("another version", """{"version":2,"states":{}}"""), ("no version", """{"states":{}}"""),
            ("an array", "[]"), ("states that are not an object", """{"version":1,"states":[]}"""), ("no states", """{"version":1}"""),
            ("a directory", null), ("unreadable", "unreadable") })
        {
            using var scratch = new Scratch();
            Directory.CreateDirectory(Path.GetDirectoryName(scratch.File)!);
            if (what == "a directory") Directory.CreateDirectory(scratch.File);
            else File.WriteAllText(scratch.File, what == "unreadable" ? $$$"""{"version":1,"states":{"relay:relay":{{{Good}}}}}""" : broken);
            if (what == "unreadable") Mode(scratch.File, UnixFileMode.None);
            try
            {
                var store = new EntityStateStore(scratch.File);
                store.Load();
                store.Load();
                Check(!store.TryGet("relay:relay", out _) && store.Ids().Length == 0, $"{what}: an unreadable file has states");
                var warnings = scratch.Warnings();
                Check(warnings.Length == 1 && warnings[0].StartsWith("Cannot read the entities' last states (", StringComparison.Ordinal) && warnings[0].Contains("entities start without them"),
                    $"{what}: warned " + string.Join(" | ", warnings));
                if (what == "a directory") Directory.Delete(scratch.File);
                if (what == "unreadable") Mode(scratch.File, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                store.Remember("relay:relay", Values("""{"is_on":false}"""));
                await store.FlushAsync();
                var again = new EntityStateStore(scratch.File);
                Check(again.TryGet("relay:relay", out var values) && values.ToJsonString() == """{"is_on":false}""", $"{what}: the next write replaces the file");
            }
            finally { if (File.Exists(scratch.File)) Mode(scratch.File, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        }
        // An entry that is not well formed is left out, and the others kept: an array, values that are not an object, values
        // with a name twice. An id written twice keeps the last.
        using (var scratch = new Scratch())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(scratch.File)!);
            File.WriteAllText(scratch.File, $$$"""
                {"version":1,"states":{
                  "a:list":[1,2],
                  "a:text":{"values":"on"},
                  "a:twice":{"values":{"is_on":true,"is_on":false}},
                  "a:none":{"utc":"2026-11-18T10:00:00Z"},
                  "a:kept":{{{Good}}},
                  "a:again":{"values":{"value":1}},
                  "a:again":{"values":{"value":2}},
                  "a:undated":{"values":{"value":3}}
                }}
                """);
            var store = new EntityStateStore(scratch.File);
            store.Load();
            var ids = store.Ids().Order(StringComparer.Ordinal).ToArray();
            Check(ids.SequenceEqual(["a:again", "a:kept", "a:undated"]), "the well-formed entries kept: " + string.Join(", ", ids));
            Check(store.TryGet("a:again", out var again) && again.ToJsonString() == """{"value":2}""", "an id written twice keeps the last: " + again.ToJsonString());
            Check(store.TryGet("a:kept", out var kept) && kept.ToJsonString() == """{"is_on":true}""", "a well-formed entry: " + kept.ToJsonString());
            var warnings = scratch.Warnings();
            Check(warnings.Length == 1 && warnings[0].EndsWith("entities start without them: 4 entries are not well formed and are left out", StringComparison.Ordinal),
                "the entries left out are warned of once: " + string.Join(" | ", warnings));
        }
        Console.WriteLine("PASS entity restore: nothing a state file holds throws: one that cannot be read is logged once, taken as empty and replaced; an entry not well formed is left out and the others kept");
    }

    /// <summary>Forgotten ids are gone from the file; forgetting an id never remembered writes nothing.</summary>
    static async Task Forget()
    {
        using var scratch = new Scratch();
        var store = new EntityStateStore(scratch.File);
        store.Remember("relay:relay", Values("""{"is_on":true}"""));
        store.Remember("relay:spare", Values("""{"is_on":false}"""));
        store.Remember("dial:dial", Values("""{"value":3}"""));
        await store.FlushAsync();
        store.Forget(["relay:relay", "relay:spare"]);
        await store.FlushAsync();
        Check(store.Writes == 2, "the forget was not written");
        store.Forget(["nothing:here"]);
        await store.FlushAsync();
        Check(store.Writes == 2, "forgetting nothing wrote");
        var again = new EntityStateStore(scratch.File);
        Check(again.Ids().SequenceEqual(["dial:dial"]), "after forgetting: " + string.Join(", ", again.Ids()));
        Console.WriteLine("PASS entity restore: forgotten ids are gone from the file");
    }

    /// <summary>A closed store writes nothing more: neither a write that failed before nor a change after.</summary>
    static async Task ClosingStopsTheRetries()
    {
        using var scratch = new Scratch();
        EntityStateStore.FlushInterval = TimeSpan.FromMilliseconds(100);
        var blocked = Path.GetDirectoryName(scratch.File)!;
        Directory.CreateDirectory(Path.GetDirectoryName(blocked)!);
        File.WriteAllText(blocked, "");
        var store = new EntityStateStore(scratch.File);
        store.Remember("relay:relay", Values("""{"is_on":true}"""));
        await store.CloseAsync();
        File.Delete(blocked);
        store.Remember("relay:relay", Values("""{"is_on":false}"""));
        await store.FlushAsync();
        await Task.Delay(400);
        Check(store.Writes == 0 && !File.Exists(scratch.File), "a closed store wrote");
        Console.WriteLine("PASS entity restore: a closed store tries no failed write again and takes no change");
    }

    /// <summary>
    /// Unloading writes the store and closes it: a change made to the old store afterwards writes nothing, the file
    /// staying as the last flush left it; initialising the manager again opens it, reading the file.
    /// </summary>
    static async Task ClosedOnUnload()
    {
        EntityStateStore.FlushInterval = TimeSpan.FromMilliseconds(100);
        await using var home = new EntityTestHome("entity-restore-unload");
        var api = new HassSharpManager(home.Root);
        try
        {
            await api.Init();
            var store = ((UserScriptManager)typeof(HassSharpManager).GetField("_userScriptManager", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(api)!).States!;
            store.Remember("relay:relay", Values("""{"is_on":true}"""));
            await api.UnloadAsync();
            var file = Path.Combine(home.Root, ".storage", "hass_sharp", "entity_states.json");
            var written = File.ReadAllText(file);
            var writes = store.Writes;
            Check(written.Contains("\"is_on\":true"), "the unload wrote the states: " + written);
            store.Remember("relay:relay", Values("""{"is_on":false}"""));
            store.Forget(["relay:relay"]);
            await store.FlushAsync();
            await Task.Delay(400);
            Check(store.Writes == writes && File.ReadAllText(file) == written, "the store of an unloaded manager wrote: " + File.ReadAllText(file));
            await api.Init();
            Check(store.TryGet("relay:relay", out var values) && values.ToJsonString() == """{"is_on":true}""", "initialised again, the store reads the file: " + values.ToJsonString());
            store.Remember("relay:relay", Values("""{"is_on":false}"""));
        }
        finally { await api.UnloadAsync(); }
        Check(File.ReadAllText(Path.Combine(home.Root, ".storage", "hass_sharp", "entity_states.json")).Contains("\"is_on\":false"), "the store opened again writes");
        Console.WriteLine("PASS entity restore: unloading writes the store and closes it; initialising again opens it");
    }

    /// <summary>A write that fails is logged once as a warning, tried again after the interval, and succeeds once it can.</summary>
    static async Task FailedWrite()
    {
        using var scratch = new Scratch();
        EntityStateStore.FlushInterval = TimeSpan.FromMilliseconds(200);
        // The store's directory is a file: nothing can be written under it.
        var blocked = Path.GetDirectoryName(scratch.File)!;
        Directory.CreateDirectory(Path.GetDirectoryName(blocked)!);
        File.WriteAllText(blocked, "");
        var store = new EntityStateStore(scratch.File);
        store.Remember("relay:relay", Values("""{"is_on":true}"""));
        await store.FlushAsync();
        await store.FlushAsync();
        await Task.Delay(500);
        var warnings = scratch.Warnings();
        Check(store.Writes == 0 && warnings.Length == 1 && warnings[0].StartsWith("Cannot write the entities' last states (", StringComparison.Ordinal) && warnings[0].Contains("trying again in 0.2 s"),
            "a failing write warns once: " + string.Join(" | ", warnings));
        File.Delete(blocked);
        await Until(() => store.Writes == 1, "the write tried again");
        Check(new EntityStateStore(scratch.File).TryGet("relay:relay", out var values) && values.ToJsonString() == """{"is_on":true}""", "the retried write: " + values.ToJsonString());
        Check(scratch.Warnings().Length == 1, "the retry warned again");
        Console.WriteLine("PASS entity restore: a write that fails is logged once and tried again until it succeeds");
    }
}
