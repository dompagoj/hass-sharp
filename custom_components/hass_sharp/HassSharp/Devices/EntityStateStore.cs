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
    bool dirty, scheduled, failing;
    int writes;

    /// <summary>How many times the file was written; for tests.</summary>
    internal int Writes { get { lock (gate) return writes; } }

    /// <summary>
    /// Reads the file, unless it was read already: one that cannot be read is logged once as a warning and taken as empty
    /// (it is replaced at the next write). Done at startup, so that nothing else waits for the disk.
    /// </summary>
    internal void Load()
    {
        lock (gate) if (states != null) return;
        Dictionary<string, (string, DateTime)> read = new(StringComparer.Ordinal);
        string? problem = null;
        try { if (File.Exists(path)) read = Parse(File.ReadAllText(path)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        { problem = error.Message; }
        lock (gate)
        {
            if (states != null) return;
            states = read;
        }
        if (problem != null)
            try { Logger.Warn($"Cannot read the entities' last states ({path}); entities start without them: {problem}"); } catch { }
    }

    static Dictionary<string, (string, DateTime)> Parse(string text)
    {
        var root = JsonNode.Parse(text) as JsonObject ?? throw new JsonException("it is not a JSON object");
        if (root["version"] is not JsonValue version || version.GetValueKind() != JsonValueKind.Number || !version.TryGetValue<int>(out var given) || given != Version)
            throw new JsonException($"its version is not {Version}");
        var read = new Dictionary<string, (string, DateTime)>(StringComparer.Ordinal);
        foreach (var (id, entry) in root["states"] as JsonObject ?? throw new JsonException("it has no states"))
        {
            if (entry?["values"] is not JsonObject values) continue;
            var utc = entry["utc"] is JsonValue time && time.TryGetValue<string>(out var at)
                && DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when) ? when.ToUniversalTime() : DateTime.UtcNow;
            read[id] = (values.ToJsonString(), utc);
        }
        return read;
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
                if (!dirty || states == null) return;
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
                if (!scheduled)
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
