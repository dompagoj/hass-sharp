using System.Text;
using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>
/// Collects entity snapshots and sends them to Home Assistant in batches through
/// <see cref="PyInterop.PushEntityStates"/>. Posts for one id inside the debounce coalesce to the last, and a snapshot
/// equal to the last one sent for its id is left out. Posts and flushes may come from any thread.
/// </summary>
internal sealed class EntityStatePump(TimeSpan? debounce = null)
{
    readonly TimeSpan delay = debounce ?? TimeSpan.FromMilliseconds(25);
    readonly object gate = new();
    // One flush at a time, so batches reach Python in the order they were taken.
    readonly SemaphoreSlim sending = new(1, 1);
    // The JSON text pending per id, with the ids in first-posted order.
    readonly Dictionary<string, string> pending = [];
    readonly List<string> order = [];
    // The JSON text last sent per id.
    readonly Dictionary<string, string> sent = [];
    // Changes on every flush and reset, so a debounce scheduled before one does nothing.
    int round;
    bool scheduled;
    // Changes on every Forget and Reset, so a flush in flight forgets its batch's ids instead of remembering them.
    int forgets;

    /// <summary>Queues the snapshot for an id, replacing one already pending; its JSON text is taken now.</summary>
    public void Post(string uniqueId, JsonObject snapshot)
    {
        var text = snapshot.ToJsonString();
        lock (gate)
        {
            if (!pending.ContainsKey(uniqueId)) order.Add(uniqueId);
            pending[uniqueId] = text;
            if (scheduled) return;
            scheduled = true;
            _ = FlushLater(round);
        }
    }

    /// <summary>Forgets what was sent for these ids, so the next post for each is sent even if unchanged.</summary>
    public void Forget(IEnumerable<string> uniqueIds)
    {
        lock (gate)
        {
            foreach (var id in uniqueIds) sent.Remove(id);
            forgets++;
        }
    }

    /// <summary>Sends what is pending now, leaving out snapshots unchanged since they were last sent.</summary>
    public async Task FlushAsync()
    {
        await sending.WaitAsync();
        try
        {
            List<(string Id, string Text)> batch;
            int forgetsAtStart;
            lock (gate)
            {
                round++;
                scheduled = false;
                batch = order.Select(id => (Id: id, Text: pending[id]))
                    .Where(entry => !sent.TryGetValue(entry.Id, out var last) || last != entry.Text).ToList();
                pending.Clear();
                order.Clear();
                forgetsAtStart = forgets;
            }
            if (batch.Count == 0) return;
            var json = new StringBuilder("[");
            foreach (var (_, text) in batch) json.Append(json.Length > 1 ? "," : "").Append(text);
            try { PyInterop.PushEntityStates(json.Append(']').ToString()); }
            catch (Exception error)
            {
                Logger.Error($"Sending entity states to Home Assistant failed: {error}");
                return;
            }
            // After a Forget or Reset during the push, the batch's ids are forgotten too, so whatever is posted for
            // them next is sent rather than compared with a value older than the one Home Assistant now shows.
            lock (gate)
                foreach (var (id, text) in batch)
                    if (forgets == forgetsAtStart) sent[id] = text;
                    else sent.Remove(id);
        }
        finally { sending.Release(); }
    }

    /// <summary>Drops what is pending and what was remembered as sent, as when the definition stops.</summary>
    public void Reset()
    {
        lock (gate)
        {
            pending.Clear();
            order.Clear();
            sent.Clear();
            round++;
            scheduled = false;
            forgets++;
        }
    }

    async Task FlushLater(int scheduledRound)
    {
        await Task.Delay(delay);
        lock (gate)
            if (round != scheduledRound) return;
        await FlushAsync();
    }
}
