using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>
/// Records one visual run's per-node trace (contract C10) and hands the whole document to the run's
/// <see cref="IRunTraceSink"/>. Exists only when the run has a sink; <see cref="VisualRuntime"/> then calls it
/// from its hooks. Every entry point is thread-safe and never throws: a failure is logged once, recording
/// stops, and the stored trace stays valid and is marked <c>truncated</c>.
/// </summary>
/// <remarks>
/// Size rule: values are serialised once, when recorded, by <see cref="TraceJson"/> within the per-value caps.
/// The recorder keeps an upper bound of the document's size. A value that would take its event past 16 KiB
/// keeps its type and source but not its value (<c>trunc</c>). When an addition would take the trace past
/// 256 KiB, the oldest events lose their inputs and outputs first; they keep node, frame, status, port, timing
/// and error, and are marked <c>"trunc": true</c>. Only when no older values are left is a new event left out
/// (counted in <c>dropped</c>), a new value left out, or an error message shortened. The summary row of every
/// graph node is reserved up front so the summary always fits.
/// </remarks>
sealed class VisualTrace
{
    internal const int MaxEvents = 2000, MaxEventBytes = 16 * 1024, MaxTraceBytes = 256 * 1024, MaxTriggerBytes = 16 * 1024;
    const int MaxErrorTypeChars = 200;
    // Upper bounds of the serialised parts that are not measured, separators included:
    // {"format":1,…,"dropped":100000,"trigger":,"events":[],"summary":{}} with a 28-character startedUtc;
    // {"seq":100000,"node":,"frame":,"t":<12>,"ms":<12>,"status":"cancelled","port":"error"},
    // ,"inputs":{} and ,"outputs":{}; ,"error":{"type":,"message":}; ,"trunc":true;
    // "<node>":{"count":100000,"failed":100000,"last":"cancelled"},
    const int HeaderBytes = 200, EventBytes = 104, InputsBytes = 12, OutputsBytes = 13, ErrorBytes = 32, TruncBytes = 13, SummaryBytes = 56;

    static readonly ConditionalWeakTable<IRunTraceSink, object> claimed = new();
    /// <summary>Recorders created in this process; a run without a sink never creates one.</summary>
    internal static int Created;
    /// <summary>Test seam: called with the node id when a node starts; a throw is a recorder failure.</summary>
    internal static Action<string>? Fault;

    internal sealed class Event(string node, string frame, int seq, double t, long start)
    {
        public readonly string Node = node, Frame = frame;
        public readonly int Seq = seq;
        public readonly double T = t;
        public readonly long Start = start;
        /// <summary>The fields other than inputs, outputs, error and trunc: an upper bound while running, measured once ended.</summary>
        public long Fixed = EventBytes + TraceJson.Length(node) + TraceJson.Length(frame);

        // {"seq":N,"node":N,"frame":F,"t":T,"ms":M,"status":S,"port":P}, with one spare byte per number.
        public void Measure()
        {
            static int Digits(double value) => value.ToString("R", CultureInfo.InvariantCulture).Length + 1;
            Fixed = 7 + Digits(Seq) + 8 + TraceJson.Length(Node) + 9 + TraceJson.Length(Frame) + 5 + Digits(T) + 6 + Digits(Ms ?? 0)
                + 10 + TraceJson.Length(Status) + (Port == null ? 0 : 8 + TraceJson.Length(Port)) + 2;
        }
        public double? Ms;
        public string Status = "running";
        public string? Port;
        public (string Type, string Message, long Cost)? Error;
        public readonly List<Entry> Inputs = [], Outputs = [];
        /// <summary>In <c>events</c>; an event over a cap only counts in the summary and is never serialised.</summary>
        public bool Listed;
        /// <summary>Its values were dropped to fit the trace (<c>trunc</c>); later values are not kept either.</summary>
        public bool Stripped;
        /// <summary>The size counted in the trace's total.</summary>
        public long Size;

        public long Cost()
        {
            var cost = Fixed + (Error?.Cost ?? 0) + (Stripped ? TruncBytes : 0);
            if (Inputs.Count > 0) { cost += InputsBytes; foreach (var entry in Inputs) cost += entry.Size; }
            if (Outputs.Count > 0) { cost += OutputsBytes; foreach (var entry in Outputs) cost += entry.Size; }
            return cost;
        }
    }

    internal sealed class Entry(string port, byte[] full, byte[] bare)
    {
        public readonly string Port = port;
        readonly long portCost = TraceJson.Length(port) + 2;
        /// <summary>Over the event cap: type and source only.</summary>
        public bool Bare;
        public byte[] Json => Bare ? bare : full;
        public long Size => portCost + Json.Length;
    }

    sealed class NodeSummary { public int Count, Failed; public string Last = "running"; }
    sealed record Document(string Json, int Count, bool Truncated, bool Immediate);

    readonly IRunTraceSink sink;
    readonly string runId, root, startedUtc;
    readonly long origin;
    readonly byte[] trigger;
    readonly Func<string, string, string?> inputType, outputType;
    readonly ConcurrentDictionary<(string, string, bool), string?> types = new();
    readonly Lock gate = new(), writeGate = new();
    readonly List<Event> events = [];
    readonly Dictionary<string, NodeSummary> summary = new(StringComparer.Ordinal);
    int seq, dropped, stripFrom, failures;
    long size, lastFlush;
    bool truncated, finalized, writing;
    volatile bool failed;
    Document? queued;

    VisualTrace(IRunTraceSink sink, InvocationContext context, string root, IEnumerable<string> nodes,
        Func<string, string, string?> inputType, Func<string, string, string?> outputType)
    {
        this.sink = sink; runId = context.RunId; this.root = root; this.inputType = inputType; this.outputType = outputType;
        startedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        origin = lastFlush = Stopwatch.GetTimestamp();
        try { (trigger, truncated) = TraceJson.Trigger(context.Payload); }
        catch (Exception error) { trigger = "{}"u8.ToArray(); truncated = true; Fail(error); }
        size = HeaderBytes + TraceJson.Length(runId) + TraceJson.Length(root) + trigger.Length;
        foreach (var node in nodes) size += TraceJson.Length(node) + SummaryBytes;
    }

    /// <summary>The recorder of the current run, or null when it has no sink (or another runtime already records it).</summary>
    internal static VisualTrace? Create(string root, IEnumerable<string> nodes, Func<string, string, string?> inputType, Func<string, string, string?> outputType)
    {
        if (InvocationContext.Current is not { TraceSink: { } sink } context) return null;
        try
        {
            if (!claimed.TryAdd(sink, context)) return null;
            Interlocked.Increment(ref Created);
            return new VisualTrace(sink, context, root, nodes, inputType, outputType);
        }
        catch (Exception error) { Log(context.RunId, error); return null; }
    }

    internal static string Frame(string parent, string segment) => parent.Length == 0 ? segment : parent + "/" + segment;

    static double Since(long from, long to) => Math.Round(Stopwatch.GetElapsedTime(from, to).TotalMilliseconds, 1);

    /// <summary>A node starts in <paramref name="frame"/>; the returned event receives its values and end.</summary>
    public Event? Start(string frame, string node)
    {
        if (failed) return null;
        try
        {
            Fault?.Invoke(node);
            var now = Stopwatch.GetTimestamp();
            lock (gate)
            {
                var ev = new Event(node, frame, ++seq, Since(origin, now), now);
                // Every graph node's summary row was reserved in the constructor.
                if (!summary.TryGetValue(node, out var row)) summary[node] = row = new();
                row.Count++; row.Last = "running";
                if (finalized) return ev;
                if (events.Count < MaxEvents && Fit(null, ev.Fixed)) { ev.Listed = true; ev.Size = ev.Fixed; size += ev.Size; events.Add(ev); }
                else { dropped++; truncated = true; }
                return ev;
            }
        }
        catch (Exception error) { Fail(error); return null; }
    }

    /// <summary>The node read <paramref name="port"/>; <paramref name="source"/> is the wire it came from.</summary>
    public void Input(Event ev, string node, string port, object? value, (string Node, string Port)? source)
    {
        if (failed || !ev.Listed || ev.Stripped || ev.Node != node) return;
        try
        {
            var type = types.GetOrAdd((node, port, true), key => inputType(key.Item1, key.Item2)) ?? TraceJson.Kind(value);
            var (full, bare) = TraceJson.Entry(type, value, source);
            lock (gate) Add(ev, ev.Inputs, port, full, bare);
        }
        catch (Exception error) { Fail(error); }
    }

    public void Output(Event ev, string node, string port, object? value)
    {
        if (failed || !ev.Listed || ev.Stripped || ev.Node != node) return;
        try
        {
            var type = types.GetOrAdd((node, port, false), key => outputType(key.Item1, key.Item2)) ?? TraceJson.Kind(value);
            var (full, bare) = TraceJson.Entry(type, value, null);
            lock (gate) Add(ev, ev.Outputs, port, full, bare);
        }
        catch (Exception error) { Fail(error); }
    }

    void Add(Event ev, List<Entry> list, string port, byte[] full, byte[] bare)
    {
        if (finalized) return;
        if (ev.Stripped) { truncated = true; return; }
        // A port read twice keeps the last value, in its first position.
        var index = list.FindIndex(e => e.Port == port);
        if (index >= 0) { list.RemoveAt(index); Resize(ev); }
        var entry = new Entry(port, full, bare);
        if (index >= 0) list.Insert(index, entry); else list.Add(entry);
        if (ev.Cost() > MaxEventBytes) { entry.Bare = true; truncated = true; }
        if (ev.Cost() > MaxEventBytes) { list.Remove(entry); return; }
        // Fit may drop this event's own values when it is the oldest left.
        if (!Fit(ev, 0) && !ev.Stripped)
        {
            truncated = true;
            entry.Bare = true;
            if (!Fit(ev, 0) && !ev.Stripped) list.Remove(entry);
        }
        Resize(ev);
    }

    void Resize(Event ev)
    {
        var cost = ev.Cost();
        size += cost - ev.Size; ev.Size = cost;
    }

    /// <summary>
    /// Drops the values of the oldest events until <paramref name="extra"/> bytes, plus the uncounted growth of
    /// <paramref name="pending"/>, fit in the trace.
    /// </summary>
    bool Fit(Event? pending, long extra)
    {
        long Needed() => extra + (pending == null ? 0 : pending.Cost() - pending.Size);
        while (size + Needed() > MaxTraceBytes && stripFrom < events.Count)
        {
            var victim = events[stripFrom++];
            if (victim.Inputs.Count == 0 && victim.Outputs.Count == 0) continue;
            victim.Inputs.Clear(); victim.Outputs.Clear(); victim.Stripped = true; truncated = true;
            Resize(victim);
        }
        return size + Needed() <= MaxTraceBytes;
    }

    /// <summary>The node ended with <paramref name="status"/>, leaving by <paramref name="port"/> (null for terminal and failed nodes).</summary>
    public void End(Event ev, string status, string? port, Exception? error)
    {
        if (failed) return;
        try
        {
            var now = Stopwatch.GetTimestamp();
            Document? document = null;
            lock (gate)
            {
                if (finalized) return;
                var row = summary[ev.Node];
                row.Last = status;
                if (status is "error" or "failed") row.Failed++;
                if (ev.Listed)
                {
                    ev.Ms = Since(ev.Start, now); ev.Status = status; ev.Port = port;
                    // Measured fields are within the running bound; Fit covers the case they are not.
                    ev.Measure(); Fit(ev, 0); Resize(ev);
                    if (error != null) Record(ev, error);
                }
                if (Stopwatch.GetElapsedTime(lastFlush, now) >= RunTraceSink.Interval) document = Snapshot(false, now);
            }
            if (document != null) Hand(document);
        }
        catch (Exception failure) { Fail(failure); }
    }

    // The error within the event cap, then the trace cap; what does not fit is cut from the message.
    void Record(Event ev, Exception error)
    {
        var type = TraceJson.Cut(error.GetType().Name, MaxErrorTypeChars, long.MaxValue);
        var fixedCost = ErrorBytes + TraceJson.Length(type);
        string? Message(long room) => room - fixedCost - 2 < 0 ? null : TraceJson.Cut(error.Message, TraceJson.MaxChars, room - fixedCost - 2);
        void Set(string? message) => ev.Error = message == null ? null : (type, message, fixedCost + TraceJson.Length(message));
        Set(Message(MaxEventBytes - ev.Cost()));
        if (ev.Error == null || !Fit(ev, 0))
        {
            truncated = true;
            Set(null);
            var room = Math.Min(MaxEventBytes - ev.Cost(), MaxTraceBytes - size - (ev.Cost() - ev.Size));
            Set(Message(room));
        }
        Resize(ev);
    }


    /// <summary>The node is about to wait: store what is known now, bypassing the once-per-second throttle.</summary>
    public void Waiting()
    {
        if (failed) return;
        try
        {
            Document document;
            lock (gate)
            {
                if (finalized) return;
                document = Snapshot(true, Stopwatch.GetTimestamp());
            }
            Hand(document);
        }
        catch (Exception error) { Fail(error); }
    }

    Document Snapshot(bool immediate, long now)
    {
        lastFlush = now;
        return new(Build(false), events.Count, truncated || failed, immediate);
    }

    // The latest document waits in one slot for one writer, so a database write never blocks a node and an
    // earlier document can never be stored after a later one.
    void Hand(Document document)
    {
        lock (gate)
        {
            if (finalized) return;
            queued = queued is { Immediate: true } && !document.Immediate ? document with { Immediate = true } : document;
            if (writing) return;
            writing = true;
        }
        _ = Task.Run(Drain);
    }

    void Drain()
    {
        while (true)
        {
            lock (writeGate)
            {
                Document? document;
                lock (gate)
                {
                    document = queued; queued = null;
                    if (document == null || finalized) { writing = false; return; }
                }
                try { sink.Write(document.Json, document.Count, document.Truncated, false, document.Immediate); }
                catch (Exception error) { Fail(error); }
            }
        }
    }

    /// <summary>The run ended: stores the complete document, after which nothing else is stored.</summary>
    public void Complete()
    {
        string json; int count; bool cut;
        try
        {
            lock (gate)
            {
                if (finalized) return;
                finalized = true;
                json = Build(true); count = events.Count; cut = truncated || failed;
            }
        }
        catch (Exception error)
        {
            Fail(error);
            try { json = Minimal(); } catch { return; }
            count = 0; cut = true;
        }
        try { lock (writeGate) sink.Write(json, count, cut, true); }
        catch (Exception error) { Fail(error); }
    }

    void Fail(Exception error)
    {
        failed = true;
        if (Interlocked.Exchange(ref failures, 1) == 0) Log(runId, error);
    }

    static void Log(string runId, Exception error)
    {
        try { Logger.Error($"Stopped recording the trace of run {runId}: {error.GetType().Name}: {error.Message}"); } catch { }
    }

    string Build(bool complete)
    {
        var buffer = new ArrayBufferWriter<byte>((int)Math.Min(size, MaxTraceBytes));
        using (var w = new Utf8JsonWriter(buffer, TraceJson.Options))
        {
            Header(w, complete, truncated || failed, dropped);
            w.WritePropertyName("trigger"); w.WriteRawValue(trigger, true);
            w.WriteStartArray("events");
            foreach (var ev in events)
            {
                w.WriteStartObject();
                w.WriteNumber("seq", ev.Seq); w.WriteString("node", ev.Node); w.WriteString("frame", ev.Frame); w.WriteNumber("t", ev.T);
                if (ev.Ms is { } ms) w.WriteNumber("ms", ms);
                w.WriteString("status", ev.Status);
                if (ev.Port != null) w.WriteString("port", ev.Port);
                Entries(w, "inputs", ev.Inputs); Entries(w, "outputs", ev.Outputs);
                if (ev.Error is { } error)
                {
                    w.WriteStartObject("error"); w.WriteString("type", error.Type); w.WriteString("message", error.Message); w.WriteEndObject();
                }
                if (ev.Stripped) w.WriteBoolean("trunc", true);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartObject("summary");
            foreach (var (node, row) in summary)
            {
                w.WriteStartObject(node); w.WriteNumber("count", row.Count); w.WriteNumber("failed", row.Failed); w.WriteString("last", row.Last); w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    static void Entries(Utf8JsonWriter w, string name, List<Entry> entries)
    {
        if (entries.Count == 0) return;
        w.WriteStartObject(name);
        foreach (var entry in entries) { w.WritePropertyName(entry.Port); w.WriteRawValue(entry.Json, true); }
        w.WriteEndObject();
    }

    void Header(Utf8JsonWriter w, bool complete, bool cut, int lost)
    {
        w.WriteStartObject();
        w.WriteNumber("format", 1); w.WriteString("runId", runId); w.WriteString("rootNodeId", root); w.WriteString("startedUtc", startedUtc);
        w.WriteBoolean("complete", complete); w.WriteBoolean("truncated", cut); w.WriteNumber("dropped", lost);
    }

    // The fallback when the document itself cannot be built: valid, complete, and marked truncated.
    string Minimal()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, TraceJson.Options))
        {
            Header(w, true, true, seq);
            w.WriteStartObject("trigger"); w.WriteEndObject();
            w.WriteStartArray("events"); w.WriteEndArray();
            w.WriteStartObject("summary"); w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>
/// Writes runtime values as JSON within the C10 caps, counting bytes while it writes: it never serialises
/// more of a value than fits, so a huge value costs no more than its first few kilobytes.
/// </summary>
static class TraceJson
{
    internal const int MaxDepth = 6, MaxItems = 50, MaxChars = 1000, MaxValueBytes = 4096;
    internal static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    // Weakly keyed: library and script types live in collectible assemblies.
    static readonly ConditionalWeakTable<Type, MemberInfo[]> members = new();

    /// <summary>An upper bound of the UTF-8 bytes one UTF-16 unit takes inside a JSON string.</summary>
    static int Bytes(char c) => c is >= ' ' and < (char)0x7f and not ('"' or '\\' or '<' or '>' or '&' or '\'' or '+' or '`') ? 1 : 6;

    /// <summary>An upper bound of the serialised length of <paramref name="text"/>, quotes included.</summary>
    internal static long Length(string text)
    {
        long total = 2;
        foreach (var c in text) total += Bytes(c);
        return total;
    }

    /// <summary>The longest prefix of at most <paramref name="chars"/> units whose serialised body fits <paramref name="bytes"/>, never splitting a surrogate pair.</summary>
    internal static string Cut(string text, int chars, long bytes)
    {
        long used = 0; var i = 0;
        while (i < text.Length && i < chars)
        {
            var pair = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
            var width = pair ? 2 : 1;
            var cost = pair ? 12 : Bytes(text[i]);
            if (i + width > chars || used + cost > bytes) break;
            used += cost; i += width;
        }
        return Clean(i == text.Length ? text : text[..i]);
    }

    // A lone surrogate is not valid UTF-16; the writer would reject it.
    static string Clean(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { i++; continue; }
            if (char.IsSurrogate(c))
            {
                var chars = text.ToCharArray();
                for (var j = i; j < chars.Length; j++)
                {
                    if (char.IsHighSurrogate(chars[j]) && j + 1 < chars.Length && char.IsLowSurrogate(chars[j + 1])) { j++; continue; }
                    if (char.IsSurrogate(chars[j])) chars[j] = '�';
                }
                return new string(chars);
            }
        }
        return text;
    }

    /// <summary>The type shown for a value whose port declares none.</summary>
    internal static string Kind(object? value) => value switch
    {
        null => "null",
        bool => "boolean",
        string or char => "string",
        JsonElement e => e.ValueKind switch { JsonValueKind.Object => "object", JsonValueKind.Array => "array", JsonValueKind.String => "string", JsonValueKind.Number => "number", JsonValueKind.True or JsonValueKind.False => "boolean", _ => "null" },
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v => v.GetValueKind() switch { JsonValueKind.String => "string", JsonValueKind.Number => "number", JsonValueKind.True or JsonValueKind.False => "boolean", _ => "null" },
        _ when Number(value) => "number",
        _ => "global::" + value.GetType().FullName?.Replace('+', '.'),
    };

    static bool Number(object value) => !value.GetType().IsEnum && Type.GetTypeCode(value.GetType()) is >= TypeCode.SByte and <= TypeCode.Decimal;

    /// <summary>One port: <c>{type, v, trunc?, src?}</c>, and the same without <c>v</c> for when values are dropped.</summary>
    internal static (byte[] Full, byte[] Bare) Entry(string type, object? value, (string Node, string Port)? source)
    {
        byte[] Write(bool full)
        {
            var buffer = new ArrayBufferWriter<byte>(64);
            using (var w = new Utf8JsonWriter(buffer, Options))
            {
                w.WriteStartObject();
                w.WriteString("type", type);
                var cut = !full;
                if (full) { w.WritePropertyName("v"); cut = new Writer(w, MaxValueBytes).Root(value); }
                if (cut) w.WriteBoolean("trunc", true);
                if (source is { } src) { w.WriteStartObject("src"); w.WriteString("node", src.Node); w.WriteString("port", src.Port); w.WriteEndObject(); }
                w.WriteEndObject();
            }
            return buffer.WrittenSpan.ToArray();
        }
        return (Write(true), Write(false));
    }

    /// <summary>The trigger payload as a JSON object within 16 KiB; true when it was cut.</summary>
    internal static (byte[] Json, bool Truncated) Trigger(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return ("{}"u8.ToArray(), payload.ValueKind != JsonValueKind.Undefined);
        return Value(payload, VisualTrace.MaxTriggerBytes);
    }

    /// <summary>One value within <paramref name="bytes"/>; true when it was cut.</summary>
    internal static (byte[] Json, bool Truncated) Value(object? value, int bytes)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        bool cut;
        using (var w = new Utf8JsonWriter(buffer, Options)) cut = new Writer(w, bytes).Root(value);
        return (buffer.WrittenSpan.ToArray(), cut);
    }

    /// <summary>
    /// Writes exactly one JSON value into <c>w</c> within <c>max</c> bytes. Before each token it checks the bytes
    /// still free, keeping one closing byte per open container and room for a separator and a <c>null</c>,
    /// so a value cut anywhere is still valid JSON. User code (getters, enumerators) runs before the writer
    /// is touched, so its exceptions only shorten the value.
    /// </summary>
    sealed class Writer(Utf8JsonWriter w, int max)
    {
        const int Least = 6;
        readonly long start = w.BytesCommitted + w.BytesPending;
        int open;
        bool full, cut;

        long Free => max - (w.BytesCommitted + w.BytesPending - start) - open;
        bool Fits(long bytes) { if (!full && bytes + 1 + Least <= Free) return true; full = cut = true; return false; }

        public bool Root(object? value) { Value(value, 0); return cut; }

        void Value(object? value, int depth)
        {
            if (full) { w.WriteNullValue(); return; }
            switch (value)
            {
                case null: w.WriteNullValue(); return;
                case bool b: w.WriteBooleanValue(b); return;
                case string s: String(s); return;
                case char c: String(c.ToString()); return;
                case JsonElement e: Element(e, depth); return;
                case JsonNode n: Node(n, depth); return;
                case EntityStateSnapshot entity:
                    Object(Pairs(("entity_id", entity.EntityId), ("state", entity.State), ("attributes", entity.Attributes),
                        ("last_changed", entity.LastChanged), ("last_updated", entity.LastUpdated), ("last_reported", entity.LastReported)), depth); return;
                case double d: Double(d); return;
                case float f: Double(f); return;
                case decimal m: if (Fits(32)) w.WriteNumberValue(m); else w.WriteNullValue(); return;
                case Enum: Value(Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType()), CultureInfo.InvariantCulture), depth); return;
                case ulong u: if (Fits(20)) w.WriteNumberValue(u); else w.WriteNullValue(); return;
                case sbyte or byte or short or ushort or int or uint or long: if (Fits(20)) w.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture)); else w.WriteNullValue(); return;
                case DateTime time: String(time.ToString("O", CultureInfo.InvariantCulture)); return;
                case DateTimeOffset time: String(time.ToString("O", CultureInfo.InvariantCulture)); return;
                case DateOnly date: String(date.ToString("O", CultureInfo.InvariantCulture)); return;
                case TimeOnly time: String(time.ToString("O", CultureInfo.InvariantCulture)); return;
                case TimeSpan span: String(span.ToString("c", CultureInfo.InvariantCulture)); return;
                case Guid guid: String(guid.ToString()); return;
                case Exception error: Object(Pairs(("type", error.GetType().Name), ("message", error.Message)), depth); return;
                case IDictionary map: Object(Entries(map), depth); return;
                case IEnumerable items: Array(items.GetEnumerator(), depth); return;
            }
            var type = value.GetType();
            if (type.Namespace?.StartsWith("Python", StringComparison.Ordinal) == true) { String(type.FullName ?? type.Name); return; }
            if (type.Assembly == typeof(object).Assembly || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true || type.Namespace?.StartsWith("Microsoft", StringComparison.Ordinal) == true)
            {
                string? text;
                try { text = value.ToString(); } catch { text = type.FullName; cut = true; }
                String(text ?? ""); return;
            }
            Object(Members(value), depth);
        }

        void Double(double d)
        {
            if (!double.IsFinite(d)) { String(d.ToString(CultureInfo.InvariantCulture)); return; }
            if (Fits(24)) w.WriteNumberValue(d); else w.WriteNullValue();
        }

        void String(string text)
        {
            if (!Fits(2)) { w.WriteNullValue(); return; }
            var body = Free - 1 - Least - 2;
            var result = Cut(text, MaxChars, body);
            if (result.Length < text.Length)
            {
                cut = true;
                // Cut by bytes rather than the character cap: nothing else fits either.
                if (result.Length < Math.Min(text.Length, MaxChars) - 1) full = true;
            }
            w.WriteStringValue(result);
        }

        // A raw JSON string token: copy a prefix of its escaped UTF-8 body without decoding the rest.
        void RawString(ReadOnlySpan<byte> raw)
        {
            if (!Fits(2)) { w.WriteNullValue(); return; }
            var inner = raw[1..^1];
            var limit = Free - 1 - Least - 2;
            int i = 0, chars = 0;
            while (i < inner.Length)
            {
                int length, units = 1;
                var b = inner[i];
                if (b == '\\')
                {
                    length = inner[i + 1] == 'u' ? 6 : 2;
                    if (length == 6 && Hex(inner.Slice(i + 2, 4)) is >= 0xD800 and <= 0xDBFF && i + 12 <= inner.Length && inner[i + 6] == '\\' && inner[i + 7] == 'u') { length = 12; units = 2; }
                }
                else if (b < 0x80) length = 1;
                else if ((b & 0xE0) == 0xC0) length = 2;
                else if ((b & 0xF0) == 0xE0) length = 3;
                else { length = 4; units = 2; }
                if (chars + units > MaxChars || i + length > limit) break;
                i += length; chars += units;
            }
            if (i == inner.Length) { w.WriteRawValue(raw, true); return; }
            cut = true;
            if (chars < MaxChars - 1) full = true;
            var token = new byte[i + 2];
            token[0] = token[^1] = (byte)'"';
            inner[..i].CopyTo(token.AsSpan(1));
            w.WriteRawValue(token, true);
        }

        static int Hex(ReadOnlySpan<byte> digits) =>
            int.TryParse(Encoding.ASCII.GetString(digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value) ? value : -1;

        void Element(JsonElement e, int depth)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object: Object(Properties(e), depth); return;
                case JsonValueKind.Array: Array(Items(e), depth); return;
                case JsonValueKind.String: RawString(JsonMarshal.GetRawUtf8Value(e)); return;
                case JsonValueKind.Number:
                    var raw = JsonMarshal.GetRawUtf8Value(e);
                    if (Fits(raw.Length)) w.WriteRawValue(raw, true); else w.WriteNullValue();
                    return;
                case JsonValueKind.True: w.WriteBooleanValue(true); return;
                case JsonValueKind.False: w.WriteBooleanValue(false); return;
                default: w.WriteNullValue(); return;
            }
        }

        void Node(JsonNode node, int depth)
        {
            switch (node)
            {
                case JsonObject o: Object(o.Select(p => new KeyValuePair<string, object?>(p.Key, p.Value)).GetEnumerator(), depth); return;
                case JsonArray a: Array(a.GetEnumerator(), depth); return;
                case JsonValue v when v.TryGetValue<JsonElement>(out var e): Element(e, depth); return;
                case JsonValue v when v.TryGetValue<string>(out var s): String(s); return;
                case JsonValue v:
                    var kind = v.GetValueKind();
                    if (kind is JsonValueKind.True or JsonValueKind.False) { w.WriteBooleanValue(kind == JsonValueKind.True); return; }
                    var text = v.ToJsonString();
                    if (kind == JsonValueKind.String) { String(JsonSerializer.Deserialize<string>(text) ?? ""); return; }
                    if (Fits(text.Length)) w.WriteRawValue(text, true); else w.WriteNullValue();
                    return;
            }
        }

        void Object(IEnumerator<KeyValuePair<string, object?>> items, int depth)
        {
            using var _ = items;
            if (depth >= MaxDepth) { cut = true; w.WriteStartObject(); w.WriteEndObject(); return; }
            w.WriteStartObject(); open++;
            for (var count = 0; ; count++)
            {
                if (!Next(items)) break;
                if (count == MaxItems) { cut = true; break; }
                var name = Cut(items.Current.Key, MaxChars, long.MaxValue);
                if (name.Length < items.Current.Key.Length) cut = true;
                if (!Fits(Length(name) + 1)) break;
                w.WritePropertyName(name);
                Value(items.Current.Value, depth + 1);
                if (full) break;
            }
            open--; w.WriteEndObject();
        }

        void Array(IEnumerator items, int depth)
        {
            using var _ = items as IDisposable;
            if (depth >= MaxDepth) { cut = true; w.WriteStartArray(); w.WriteEndArray(); return; }
            w.WriteStartArray(); open++;
            for (var count = 0; ; count++)
            {
                if (!Next(items)) break;
                if (count == MaxItems) { cut = true; break; }
                if (!Fits(0)) break;
                Value(items.Current, depth + 1);
                if (full) break;
            }
            open--; w.WriteEndArray();
        }

        bool Next(IEnumerator items)
        {
            try { return items.MoveNext(); }
            catch { cut = true; return false; }
        }

        static IEnumerator<KeyValuePair<string, object?>> Pairs(params (string Key, object? Value)[] pairs) =>
            pairs.Select(p => new KeyValuePair<string, object?>(p.Key, p.Value)).GetEnumerator();

        static IEnumerator<KeyValuePair<string, object?>> Properties(JsonElement e)
        {
            foreach (var property in e.EnumerateObject()) yield return new(property.Name, property.Value);
        }

        static IEnumerator Items(JsonElement e)
        {
            foreach (var item in e.EnumerateArray()) yield return item;
        }

        static IEnumerator<KeyValuePair<string, object?>> Entries(IDictionary map)
        {
            var entries = map.GetEnumerator();
            using var _ = entries as IDisposable;
            while (entries.MoveNext())
            {
                var entry = entries.Entry;
                yield return new(Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "", entry.Value);
            }
        }

        // Public instance properties and fields, as declared. A getter that throws leaves its member out.
        IEnumerator<KeyValuePair<string, object?>> Members(object value)
        {
            var list = members.GetValue(value.GetType(), type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.CanRead && p.GetMethod?.IsPublic == true && p.GetIndexParameters().Length == 0).Cast<MemberInfo>()
                .Concat(type.GetFields(BindingFlags.Instance | BindingFlags.Public)).ToArray());
            foreach (var member in list)
            {
                object? item; var ok = true;
                try { item = member is PropertyInfo property ? property.GetValue(value) : ((FieldInfo)member).GetValue(value); }
                catch { item = null; ok = false; cut = true; }
                if (ok) yield return new(member.Name, item);
            }
        }
    }
}
