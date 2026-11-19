using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>
/// The last state values of the entities that come back with them after Home Assistant restarts, by unique id, kept in
/// one file (<c>.storage/hass_sharp/entity_states.json</c>):
/// <c>{"version": 1, "states": {"&lt;uniqueId&gt;": {"values": {…}, "utc": "&lt;ISO&gt;"}}}</c>. Changes are written
/// behind, at most every <see cref="FlushInterval"/>, and on <see cref="FlushAsync"/>, through a temporary file and a
/// rename, one writer at a time. Holds JSON text and strings only, so that nothing of a definition's code stays
/// reachable from it. Safe to use from any thread; <see cref="Remember"/> never waits for the disk.
/// </summary>
internal sealed class EntityStateStore(string path)
{
    const int Version = 1;

    /// <summary>How long a change waits before it is written, with every change made meanwhile; settable for tests.</summary>
    internal static TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(10);

    readonly object gate = new();
    // One write at a time, so that an older state never replaces a newer one on disk.
    readonly SemaphoreSlim writing = new(1, 1);
    // Each id's values as JSON text and when they were remembered; null until the file is read.
    Dictionary<string, (string Values, DateTime Utc)>? states;
    bool dirty, scheduled, failing, closed;
    int writes;

    /// <summary>How many times the file was written; for tests.</summary>
    internal int Writes { get { lock (gate) return writes; } }

    /// <summary>
    /// Reads the file, unless it was read already (and reads it again once the store was closed, which this opens again):
    /// nothing in it ever throws out of here. A file that cannot be read at all (not JSON, another version, no states,
    /// unreadable, a directory) is logged once as a warning and taken as empty, and is replaced at the next write; an
    /// entry that is not well formed is left out, which the same warning says, and the others are kept. Done at startup,
    /// so that nothing else waits for the disk.
    /// </summary>
    internal void Load()
    {
        lock (gate)
        {
            if (closed)
            {
                closed = false;
                states = null;
            }
            if (states != null) return;
        }
        var read = new Dictionary<string, (string, DateTime)>(StringComparer.Ordinal);
        string? problem = null;
        try
        {
            if (Directory.Exists(path)) problem = "it is a directory";
            else if (File.Exists(path))
            {
                var skipped = Parse(File.ReadAllBytes(path), read);
                if (skipped > 0) problem = skipped == 1 ? "1 entry is not well formed and is left out" : $"{skipped} entries are not well formed and are left out";
            }
        }
        // Whatever the file holds, the entities start without what cannot be read; the load goes on.
        catch (Exception error)
        {
            read.Clear();
            problem = error.Message;
        }
        lock (gate)
        {
            if (states != null) return;
            states = read;
        }
        if (problem != null)
            try { Logger.Warn($"Cannot read the entities' last states ({path}); the entities start with no value until they are set again: {problem}"); } catch { }
    }

    // Reads the file's entries into read: an id written twice keeps the last, and an entry that is not an object of
    // values (an object without duplicated names) is skipped. How many were skipped.
    static int Parse(byte[] text, Dictionary<string, (string, DateTime)> read)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("it is not a JSON object");
        if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var given) || given != Version)
            throw new JsonException($"its version is not {Version}");
        if (!root.TryGetProperty("states", out var states) || states.ValueKind != JsonValueKind.Object) throw new JsonException("its states are not an object");
        var skipped = 0;
        foreach (var entry in states.EnumerateObject())
        {
            if (Entry(entry.Value) is { } kept) read[entry.Name] = kept;
            else skipped++;
        }
        return skipped;
    }

    // One entry as kept: its values as JSON text, when they are an object a JsonObject reads (no name twice), and when
    // they were remembered (now when not given as an ISO time); null for an entry not so.
    static (string, DateTime)? Entry(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object) return null;
        string text;
        try
        {
            // Read whole now: a JsonObject throws on a duplicated name only once its members are read.
            var node = JsonNode.Parse(values.GetRawText())!.AsObject();
            _ = node.Count;
            text = node.ToJsonString();
        }
        catch (Exception) { return null; }
        var utc = entry.TryGetProperty("utc", out var time) && time.ValueKind == JsonValueKind.String
            && DateTime.TryParse(time.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when) ? when.ToUniversalTime() : DateTime.UtcNow;
        return (text, utc);
    }

    Dictionary<string, (string Values, DateTime Utc)> States
    {
        get
        {
            if (states == null) Load();
            return states!;
        }
    }

    /// <summary>Keeps the entity's state values as they are now, to be written with the next flush; costs no disk access.</summary>
    public void Remember(string uniqueId, JsonObject values)
    {
        var text = values.ToJsonString();
        lock (gate)
        {
            if (closed) return;
            var all = States;
            if (all.TryGetValue(uniqueId, out var known) && known.Values == text) return;
            all[uniqueId] = (text, DateTime.UtcNow);
            Changed();
        }
    }

    /// <summary>The entity's last remembered state values, a copy of its own; false when none are.</summary>
    public bool TryGet(string uniqueId, out JsonObject values)
    {
        string? text;
        lock (gate) text = States.TryGetValue(uniqueId, out var known) ? known.Values : null;
        values = text == null ? [] : JsonNode.Parse(text) as JsonObject ?? [];
        return text != null;
    }

    /// <summary>The unique ids with remembered values.</summary>
    public string[] Ids() { lock (gate) return [.. States.Keys]; }

    /// <summary>Drops the entities' values, as when they or their definition are gone.</summary>
    public void Forget(IEnumerable<string> uniqueIds)
    {
        lock (gate)
        {
            if (closed) return;
            var all = States;
            var removed = false;
            foreach (var id in uniqueIds) removed |= all.Remove(id);
            if (removed) Changed();
        }
    }

    // Under the gate: a change is written once FlushInterval has passed, with the others made until then.
    void Changed()
    {
        dirty = true;
        if (scheduled) return;
        scheduled = true;
        _ = FlushLater();
    }

    async Task FlushLater()
    {
        try { await Task.Delay(FlushInterval); }
        finally { lock (gate) scheduled = false; }
        await FlushAsync();
    }

    /// <summary>
    /// Writes the changes not written yet, through a temporary file and a rename. A write that fails is logged once as a
    /// warning, until one succeeds, and tried again after <see cref="FlushInterval"/>. Never throws.
    /// </summary>
    public async Task FlushAsync()
    {
        string? problem = null;
        var warn = false;
        await writing.WaitAsync();
        try
        {
            KeyValuePair<string, (string Values, DateTime Utc)>[] all;
            lock (gate)
            {
                if (closed || !dirty || states == null) return;
                dirty = false;
                all = [.. states];
            }
            try { Write(all); }
            catch (Exception error) { problem = error.Message; }
            lock (gate)
            {
                if (problem == null)
                {
                    writes++;
                    failing = false;
                    return;
                }
                warn = !failing;
                failing = true;
                // Written again later, with whatever changes until then.
                dirty = true;
                if (!scheduled && !closed)
                {
                    scheduled = true;
                    _ = FlushLater();
                }
            }
        }
        finally
        {
            writing.Release();
            // After the write is let go: logging calls into Python, whose thread may be waiting for the next flush.
            if (warn)
                try { Logger.Warn($"Cannot write the entities' last states ({path}); trying again in {FlushInterval.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s: {problem}"); } catch { }
        }
    }

    /// <summary>
    /// Writes what is not written yet, then closes the store, as its manager unloads: from now on it writes nothing,
    /// schedules nothing and ignores a change, so that a store left behind by a reload never writes over the file its
    /// successor keeps (a write that failed is not tried again). <see cref="Load"/> opens it again. Never throws.
    /// </summary>
    public async Task CloseAsync()
    {
        await FlushAsync();
        // Under the write lock, so that no write of this store is under way once it is closed.
        await writing.WaitAsync();
        try { lock (gate) closed = true; }
        finally { writing.Release(); }
    }

    void Write(KeyValuePair<string, (string Values, DateTime Utc)>[] all)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("version", Version);
            json.WriteStartObject("states");
            foreach (var (id, (values, utc)) in all.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                json.WriteStartObject(id);
                json.WritePropertyName("values");
                json.WriteRawValue(values, skipInputValidation: true);
                json.WriteString("utc", utc.ToString("O", CultureInfo.InvariantCulture));
                json.WriteEndObject();
            }
            json.WriteEndObject();
            json.WriteEndObject();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, buffer.ToArray());
        File.Move(temporary, path, overwrite: true);
    }
}
