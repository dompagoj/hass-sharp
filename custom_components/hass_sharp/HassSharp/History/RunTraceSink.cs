namespace HassSharp;

/// <summary>Receives a visual run's whole trace document (contract C10) each time the recorder flushes it.</summary>
public interface IRunTraceSink
{
    /// <param name="json">The complete trace document.</param>
    /// <param name="eventCount">Events in <paramref name="json"/>.</param>
    /// <param name="truncated">True once the event cap dropped events.</param>
    /// <param name="final">The run ended: this write is stored at once and later writes are ignored.</param>
    void Write(string json, int eventCount, bool truncated, bool final);
}

/// <summary>
/// Stores one run's trace at most once per <see cref="Interval"/>. A write inside the interval is held and
/// stored when the interval ends (only the latest is kept), so the last state before a long wait still lands.
/// A storage failure is logged and never reaches the run.
/// </summary>
public sealed class RunTraceSink : IRunTraceSink
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    readonly RunHistoryStore store;
    readonly string runId;
    readonly TimeSpan interval;
    readonly TimeProvider time;
    readonly Lock gate = new();
    (string Json, int Count, bool Truncated)? pending;
    ITimer? timer;
    DateTimeOffset? last;
    bool closed;

    public RunTraceSink(RunHistoryStore store, string runId) : this(store, runId, Interval, TimeProvider.System) { }
    internal RunTraceSink(RunHistoryStore store, string runId, TimeSpan interval, TimeProvider time)
    {
        this.store = store; this.runId = runId; this.interval = interval; this.time = time;
    }

    public void Write(string json, int eventCount, bool truncated, bool final)
    {
        lock (gate)
        {
            if (closed) return;
            var now = time.GetUtcNow();
            if (final || last == null || now - last.Value >= interval)
            {
                closed = final;
                pending = null;
                timer?.Dispose(); timer = null;
                Store(json, eventCount, truncated, now);
                return;
            }
            pending = (json, eventCount, truncated);
            timer ??= time.CreateTimer(_ => Flush(), null, last.Value + interval - now, Timeout.InfiniteTimeSpan);
        }
    }

    void Flush()
    {
        lock (gate)
        {
            timer?.Dispose(); timer = null;
            if (closed || pending is not { } held) return;
            pending = null;
            Store(held.Json, held.Count, held.Truncated, time.GetUtcNow());
        }
    }

    void Store(string json, int eventCount, bool truncated, DateTimeOffset now)
    {
        last = now;
        try { store.SaveTrace(runId, json, eventCount, truncated); }
        catch (Exception ex)
        {
            try { Logger.Error($"Cannot store the trace of run {runId}: {ex.Message}"); } catch { }
        }
    }
}
