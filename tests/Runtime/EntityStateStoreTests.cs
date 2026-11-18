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

    /// <summary>A file that cannot be read is logged once and taken as empty; the next write replaces it.</summary>
    static async Task CorruptFile()
    {
        using var scratch = new Scratch();
        Directory.CreateDirectory(Path.GetDirectoryName(scratch.File)!);
        foreach (var broken in new[] { "{", """{"version":2,"states":{}}""", "[]" })
        {
            File.WriteAllText(scratch.File, broken);
            lock (scratch.Logged) scratch.Logged.Clear();
            var store = new EntityStateStore(scratch.File);
            store.Load();
            store.Load();
            Check(!store.TryGet("relay:relay", out _) && store.Ids().Length == 0, $"{broken}: an unreadable file has states");
            var warnings = scratch.Warnings();
            Check(warnings.Length == 1 && warnings[0].StartsWith("Cannot read the entities' last states (", StringComparison.Ordinal) && warnings[0].Contains("entities start without them"),
                $"{broken}: warned " + string.Join(" | ", warnings));
            store.Remember("relay:relay", Values("""{"is_on":true}"""));
            await store.FlushAsync();
            var again = new EntityStateStore(scratch.File);
            Check(again.TryGet("relay:relay", out var values) && values.ToJsonString() == """{"is_on":true}""", $"{broken}: the next write replaces the file");
        }
        Console.WriteLine("PASS entity restore: a file that cannot be read is logged once, taken as empty and replaced by the next write");
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
