using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace HassSharp;

/// <summary>
/// Records one visual run's per-node trace (contract C10) and hands the whole document to the run's
/// <see cref="IRunTraceSink"/>. Exists only when the run has a sink; <see cref="VisualRuntime"/> then calls it
/// from its hooks. Every entry point is thread-safe and never throws: a failure is logged once, recording
/// stops (statuses of events already listed still complete), and the stored trace stays valid and is marked
/// <c>truncated</c>.
/// </summary>
/// <remarks>
/// <para>Size rule: values are serialised once, when recorded, by <see cref="TraceJson"/> within the per-value caps.
/// The recorder keeps an upper bound of the document's size. A value that would take its event past 16 KiB
/// keeps its type and source but not its value (<c>trunc</c>). When an addition would take the trace past
/// 256 KiB, the oldest events lose their inputs and outputs first; they keep node, frame, status, port, timing
/// and error, and are marked <c>"trunc": true</c>. Only when no older values are left is a new event left out
/// (counted in <c>dropped</c>), a new value left out, or an error message shortened. The summary row of every
/// graph node is reserved up front so the summary always fits.</para>
/// <para>Storing: nodes only mark the trace changed. One writer at a time builds the latest state and stores it,
/// so documents are stored in the order they were built and a burst of changes costs one build. A store is due
/// one second after the last one, or 500 ms after it when a node has started waiting (at once if nothing was
/// stored yet); a timer stores what is still pending when it falls due. The run's end stores the final
/// document, and nothing is stored after it.</para>
/// </remarks>
sealed class VisualTrace
{
    internal const int MaxEvents = 2000, MaxEventBytes = 16 * 1024, MaxTraceBytes = 256 * 1024, MaxTriggerBytes = 16 * 1024;
    /// <summary>The shortest gap between a stored document and an immediate store for a waiting node.</summary>
    internal static readonly TimeSpan ImmediateGap = TimeSpan.FromMilliseconds(500);
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
    /// <summary>
    /// Test seam: called with <c>start:&lt;node&gt;</c>, <c>input:&lt;node&gt;</c>, <c>output:&lt;node&gt;</c> or
    /// <c>build</c>; a throw is a recorder failure at that point.
    /// </summary>
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

    readonly IRunTraceSink sink;
    readonly TimeProvider time;
    readonly string runId, root, startedUtc;
    readonly long origin;
    readonly DateTimeOffset created;
    readonly byte[] trigger;
    readonly Func<string, string, string?> inputType, outputType;
    readonly ConcurrentDictionary<(string, string, bool), string?> types = new();
    readonly Lock gate = new(), writeGate = new();
    readonly List<Event> events = [];
    readonly Dictionary<string, NodeSummary> summary = new(StringComparer.Ordinal);
    int seq, dropped, stripFrom, failures;
    long size;
    bool truncated, finalized;
    volatile bool failed;
    // Storing, all under gate: the state changed since the last store; a waiting node asked for a prompt store;
    // a writer is running; when the last document was built; the timer for a store not yet due.
    bool dirty, prompt, writing;
    DateTimeOffset? lastStored;
    ITimer? timer;
    DateTimeOffset timerDue;

    VisualTrace(IRunTraceSink sink, InvocationContext context, string root, IEnumerable<string> nodes,
        Func<string, string, string?> inputType, Func<string, string, string?> outputType, TimeProvider time)
    {
        this.sink = sink; this.time = time; runId = context.RunId; this.root = root; this.inputType = inputType; this.outputType = outputType;
        startedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        origin = Stopwatch.GetTimestamp();
        created = time.GetUtcNow();
        try { (trigger, truncated) = TraceJson.Trigger(context.Payload); }
        catch (Exception error) { trigger = "{}"u8.ToArray(); truncated = true; Fail(error); }
        size = HeaderBytes + TraceJson.Length(runId) + TraceJson.Length(root) + trigger.Length;
        foreach (var node in nodes) size += TraceJson.Length(node) + SummaryBytes;
    }

    /// <summary>The recorder of the current run, or null when it has no sink (or another runtime already records it).</summary>
    internal static VisualTrace? Create(string root, IEnumerable<string> nodes, Func<string, string, string?> inputType, Func<string, string, string?> outputType, TimeProvider? time = null)
    {
        if (InvocationContext.Current is not { TraceSink: { } sink } context) return null;
        try
        {
            if (!claimed.TryAdd(sink, context)) return null;
            Interlocked.Increment(ref Created);
            return new VisualTrace(sink, context, root, nodes, inputType, outputType, time ?? TimeProvider.System);
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
            Fault?.Invoke("start:" + node);
            lock (gate)
            {
                // Taken under the lock, so t never decreases in seq order.
                var now = Stopwatch.GetTimestamp();
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
            Fault?.Invoke("input:" + node);
            var type = types.GetOrAdd((node, port, true), key => inputType(key.Item1, key.Item2)) ?? TraceJson.Kind(value);
            var (full, bare) = TraceJson.Entry(port, type, value, source);
            lock (gate) Add(ev, ev.Inputs, port, full, bare);
        }
        catch (Exception error) { Fail(error); }
    }

    public void Output(Event ev, string node, string port, object? value)
    {
        if (failed || !ev.Listed || ev.Stripped || ev.Node != node) return;
        try
        {
            Fault?.Invoke("output:" + node);
            var type = types.GetOrAdd((node, port, false), key => outputType(key.Item1, key.Item2)) ?? TraceJson.Kind(value);
            var (full, bare) = TraceJson.Entry(port, type, value, null);
            lock (gate) Add(ev, ev.Outputs, port, full, bare);
        }
        catch (Exception error) { Fail(error); }
    }

    void Add(Event ev, List<Entry> list, string port, byte[] full, byte[] bare)
    {
        if (finalized || failed) return;
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

    /// <summary>
    /// The node ended with <paramref name="status"/>, leaving by <paramref name="port"/> (null for terminal and
    /// failed nodes). Runs even after a recorder failure, so no listed event is left running.
    /// </summary>
    public void End(Event ev, string status, string? port, Exception? error)
    {
        try
        {
            lock (gate)
            {
                if (finalized) return;
                var now = Stopwatch.GetTimestamp();
                var row = summary[ev.Node];
                row.Last = status;
                if (status is "error" or "failed") row.Failed++;
                if (ev.Listed)
                {
                    ev.Ms = Since(ev.Start, now); ev.Status = status; ev.Port = port;
                    // Measured fields are within the running bound; Fit covers the case they are not.
                    ev.Measure(); Fit(ev, 0); Resize(ev);
                    if (error != null && !failed) Record(ev, error);
                }
            }
            Request(false);
        }
        catch (Exception failure) { Fail(failure); }
    }

    // The error within the event cap, then the trace cap; what does not fit is cut from the message.
    void Record(Event ev, Exception error)
    {
        var type = TraceJson.Cut(error.GetType().Name, MaxErrorTypeChars, long.MaxValue);
        var fixedCost = ErrorBytes + TraceJson.Length(type);
        string text;
        try { text = error.Message; } catch { text = ""; }
        string? Message(long room) => room - fixedCost - 2 < 0 ? null : TraceJson.Cut(text, TraceJson.MaxChars, room - fixedCost - 2);
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

    /// <summary>The node is about to wait: its running state should be readable promptly.</summary>
    public void Waiting()
    {
        try { Request(true); }
        catch (Exception error) { Fail(error); }
    }

    /// <summary>Marks the trace changed and starts the writer when a store is due, else arms the timer.</summary>
    void Request(bool immediate)
    {
        if (failed) return;
        var start = false;
        lock (gate)
        {
            if (finalized) return;
            dirty = true; prompt |= immediate;
            var now = time.GetUtcNow();
            if (now >= Due()) { Disarm(); if (!writing) writing = start = true; }
            else Arm(now);
        }
        if (start) ThreadPool.UnsafeQueueUserWorkItem(_ => Drain(), null);
    }

    // When the next store may happen (under gate).
    DateTimeOffset Due() => lastStored is { } last ? last + (prompt ? ImmediateGap : RunTraceSink.Interval)
        : prompt ? DateTimeOffset.MinValue : created + RunTraceSink.Interval;

    void Arm(DateTimeOffset now)
    {
        var due = Due();
        if (timer != null && timerDue <= due) return;
        timer?.Dispose();
        timerDue = due;
        timer = time.CreateTimer(_ => Wake(), null, due - now, Timeout.InfiniteTimeSpan);
    }

    void Disarm() { timer?.Dispose(); timer = null; }

    void Wake()
    {
        lock (gate)
        {
            Disarm();
            if (finalized || failed || !dirty || writing) return;
            writing = true;
        }
        Drain();
    }

    // The single writer. It builds the latest state when it takes it, so a later state is never stored before
    // an earlier one, and stores outside the recorder's lock, so a node never waits for the database.
    void Drain()
    {
        try
        {
            while (true)
            {
                lock (writeGate)
                {
                    string json; int count; bool cut, immediate;
                    lock (gate)
                    {
                        if (finalized || failed || !dirty) { writing = false; return; }
                        var now = time.GetUtcNow();
                        if (now < Due()) { writing = false; Arm(now); return; }
                        try { Fault?.Invoke("build"); json = Build(false); }
                        catch (Exception error) { writing = false; Fail(error); return; }
                        immediate = prompt; dirty = prompt = false; lastStored = now;
                        count = events.Count; cut = truncated || failed;
                    }
                    try { sink.Write(json, count, cut, false, immediate); }
                    catch (Exception error) { Fail(error); }
                }
            }
        }
        catch (Exception error) { lock (gate) writing = false; Fail(error); }
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
                Disarm();
                Fault?.Invoke("build");
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
