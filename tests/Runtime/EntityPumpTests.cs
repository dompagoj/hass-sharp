using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>
/// The state pump and the entity bridge calls, against a recording <see cref="PyInterop.PushEntityStates"/>: posts
/// for one id coalesce to the last, unchanged snapshots are left out until forgotten, a batch keeps post order, a
/// reset drops what is pending, a failing push is logged and not remembered, the debounce sends on its own, flushes
/// race posts safely, and the sync call completes or faults with the text Python gives.
/// </summary>
static class EntityPumpTests
{
    // Long enough that only an explicit flush sends, so a check never races the timer.
    static readonly TimeSpan Manual = TimeSpan.FromHours(1);

    static JsonObject Snapshot(string uniqueId, object value) =>
        new() { ["uniqueId"] = uniqueId, ["available"] = true, ["values"] = new JsonObject { ["value"] = JsonValue.Create(value) }, ["attributes"] = new JsonObject() };

    static JsonArray Values(string batch) => JsonNode.Parse(batch)!.AsArray();

    static string Describe(JsonArray batch) =>
        string.Join(",", batch.Select(item => $"{item!["uniqueId"]}={item["values"]!["value"]!.ToJsonString()}"));

    [RuntimeSuite("--entity-pump", Order = 12)]
    public static async Task Run()
    {
        var pushed = new List<string>();
        var previous = PyInterop.PushEntityStates;
        PyInterop.PushEntityStates = batch => { lock (pushed) pushed.Add(batch); };
        try
        {
            await Coalesces(pushed);
            await Unchanged(pushed);
            await Forgotten(pushed);
            await Ordered(pushed);
            await Captured(pushed);
            await Resets(pushed);
            await Debounced(pushed);
            await Concurrent(pushed);
        }
        finally { PyInterop.PushEntityStates = previous; }
        await FailingPush();
        await Sync();
    }

    static async Task Coalesces(List<string> pushed)
    {
        pushed.Clear();
        var pump = new EntityStatePump(Manual);
        pump.Post("plug:power", Snapshot("plug:power", 1));
        pump.Post("plug:power", Snapshot("plug:power", 2));
        pump.Post("plug:power", Snapshot("plug:power", 3));
        await pump.FlushAsync();
        Check(pushed.Count == 1, $"one batch, pushed {pushed.Count}");
        Check(Describe(Values(pushed[0])) == "plug:power=3", "the batch carries the last: " + pushed[0]);
        Check(pushed[0] == new JsonArray(Snapshot("plug:power", 3)).ToJsonString(), "the batch is a JSON array of snapshots: " + pushed[0]);
        await pump.FlushAsync();
        Check(pushed.Count == 1, "a flush with nothing pending sends nothing");
        Console.WriteLine("PASS entity pump: three posts for one id before a flush send one batch with the last");
    }

    static async Task Unchanged(List<string> pushed)
    {
        pushed.Clear();
        var pump = new EntityStatePump(Manual);
        pump.Post("plug:power", Snapshot("plug:power", 1));
        await pump.FlushAsync();
        pump.Post("plug:power", Snapshot("plug:power", 1));
        await pump.FlushAsync();
        Check(pushed.Count == 1, $"an unchanged repost sends nothing, pushed {pushed.Count}");
        pump.Post("plug:power", Snapshot("plug:power", 2));
        pump.Post("plug:power", Snapshot("plug:power", 1));
        await pump.FlushAsync();
        Check(pushed.Count == 1, "a change changed back before the flush sends nothing");
        Console.WriteLine("PASS entity pump: a snapshot equal to the last one sent for its id is left out");
    }

    static async Task Forgotten(List<string> pushed)
    {
        pushed.Clear();
        var pump = new EntityStatePump(Manual);
        pump.Post("plug:power", Snapshot("plug:power", 1));
        pump.Post("plug:relay", Snapshot("plug:relay", true));
        await pump.FlushAsync();
        pump.Forget(["plug:power"]);
        pump.Post("plug:power", Snapshot("plug:power", 1));
        pump.Post("plug:relay", Snapshot("plug:relay", true));
        await pump.FlushAsync();
        Check(pushed.Count == 2 && Describe(Values(pushed[1])) == "plug:power=1", "after Forget the same snapshot is sent again, and only that one: " + string.Join(" | ", pushed));
        Console.WriteLine("PASS entity pump: after Forget the next post for an id is sent even when unchanged");
    }

    static async Task Ordered(List<string> pushed)
    {
        pushed.Clear();
        var pump = new EntityStatePump(Manual);
        pump.Post("plug:relay", Snapshot("plug:relay", true));
        pump.Post("plug:power", Snapshot("plug:power", 1));
        pump.Post("plug:relay", Snapshot("plug:relay", false));
        await pump.FlushAsync();
        Check(pushed.Count == 1 && Describe(Values(pushed[0])) == "plug:relay=false,plug:power=1", "two ids in one array, in first-posted order: " + string.Join(" | ", pushed));
        Console.WriteLine("PASS entity pump: two ids arrive in one array in the order they were first posted");
    }

    static async Task Captured(List<string> pushed)
    {
        pushed.Clear();
        var pump = new EntityStatePump(Manual);
        var snapshot = Snapshot("plug:power", 1);
        pump.Post("plug:power", snapshot);
        snapshot["values"]!["value"] = 99;
        await pump.FlushAsync();
        Check(pushed.Count == 1 && Describe(Values(pushed[0])) == "plug:power=1", "the snapshot as it was posted: " + string.Join(" | ", pushed));
        Console.WriteLine("PASS entity pump: changing a snapshot after posting it does not change what is sent");
    }

    static async Task Resets(List<string> pushed)
    {
        pushed.Clear();
        var pump = new EntityStatePump(TimeSpan.FromMilliseconds(20));
        pump.Post("plug:power", Snapshot("plug:power", 1));
        pump.Reset();
        await Task.Delay(150);
        await pump.FlushAsync();
        Check(pushed.Count == 0, "a reset drops the pending post, and its timer sends nothing: " + string.Join(" | ", pushed));

        pump = new EntityStatePump(Manual);
        pump.Post("plug:power", Snapshot("plug:power", 1));
        await pump.FlushAsync();
        pump.Reset();
        pump.Post("plug:power", Snapshot("plug:power", 1));
        await pump.FlushAsync();
        Check(pushed.Count == 2, "a reset forgets what was sent, so the same snapshot goes again");
        Console.WriteLine("PASS entity pump: Reset drops a pending post and the values remembered as sent");
    }

    static async Task Debounced(List<string> pushed)
    {
        pushed.Clear();
        var pump = new EntityStatePump(TimeSpan.FromMilliseconds(10));
        pump.Post("plug:power", Snapshot("plug:power", 1));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline) { lock (pushed) if (pushed.Count > 0) break; await Task.Delay(5); }
        lock (pushed) Check(pushed.Count == 1 && Describe(Values(pushed[0])) == "plug:power=1", "the debounce sends without a flush: " + string.Join(" | ", pushed));
        Console.WriteLine("PASS entity pump: a post is sent by itself once the debounce has passed");
    }

    // Posts come from timer, entity-change and command threads while flushes run: each batch names an id once, and
    // the last value sent for each id is the last one posted.
    static async Task Concurrent(List<string> pushed)
    {
        pushed.Clear();
        var pump = new EntityStatePump(TimeSpan.FromMilliseconds(1));
        const int Writers = 4, Posts = 2000;
        using var done = new CancellationTokenSource();
        var flusher = Task.Run(async () => { while (!done.IsCancellationRequested) await pump.FlushAsync(); });
        await Task.WhenAll(Enumerable.Range(0, Writers).Select(writer => Task.Run(() =>
        {
            for (var i = 1; i <= Posts; i++) pump.Post($"plug:w{writer}", Snapshot($"plug:w{writer}", i));
        })));
        done.Cancel();
        await flusher;
        await pump.FlushAsync();
        var last = new Dictionary<string, int>();
        lock (pushed)
            foreach (var batch in pushed)
            {
                var ids = Values(batch).Select(item => (string)item!["uniqueId"]!).ToList();
                Check(ids.Distinct().Count() == ids.Count, "a batch names each id once: " + batch);
                foreach (var item in Values(batch))
                {
                    var id = (string)item!["uniqueId"]!; var value = (int)item["values"]!["value"]!;
                    Check(!last.TryGetValue(id, out var before) || value > before, $"values for {id} are sent in post order: {before} then {value}");
                    last[id] = value;
                }
            }
        Check(last.Count == Writers && last.Values.All(value => value == Posts), "the last post of each id is sent: " + string.Join(",", last.Select(pair => $"{pair.Key}={pair.Value}")));
        Console.WriteLine("PASS entity pump: flushes racing posts from other threads lose nothing and keep each id's order");
    }

    static async Task FailingPush()
    {
        var previousPush = PyInterop.PushEntityStates; var previousLog = PyInterop.Log;
        var logged = new List<string>(); var pushed = new List<string>(); var fail = true;
        PyInterop.PushEntityStates = batch => { if (fail) throw new InvalidOperationException("loop closed"); pushed.Add(batch); };
        PyInterop.Log = (level, message) => { if (level == (int)PyLogLevel.Error) logged.Add(message); };
        try
        {
            var pump = new EntityStatePump(Manual);
            pump.Post("plug:power", Snapshot("plug:power", 1));
            await pump.FlushAsync();
            Check(logged.Count == 1 && logged[0].Contains("loop closed"), "the failure is logged as an error: " + string.Join(" | ", logged));
            fail = false;
            pump.Post("plug:power", Snapshot("plug:power", 1));
            await pump.FlushAsync();
            Check(pushed.Count == 1 && Describe(Values(pushed[0])) == "plug:power=1", "a failed push is not remembered, so the identical post is sent: " + string.Join(" | ", pushed));
        }
        finally { PyInterop.PushEntityStates = previousPush; PyInterop.Log = previousLog; }
        Console.WriteLine("PASS entity pump: a throwing push is logged, not thrown, and the next identical post is sent");
    }

    static async Task Sync()
    {
        var previous = PyInterop.BeginSyncEntities;
        try
        {
            string? given = null;
            PyInterop.BeginSyncEntities = (definition, ok, _) => { given = definition; ok("""{"entities":{"plug:relay":"switch.relay"}}"""); };
            var result = await PyInterop.SyncEntitiesAsync("""{"id":"plug"}""").WaitAsync(TimeSpan.FromSeconds(2));
            Check(given == """{"id":"plug"}""" && result == """{"entities":{"plug:relay":"switch.relay"}}""", "sync passes the definition and completes with the result: " + result);

            PyInterop.BeginSyncEntities = (_, _, error) => error("no platform for domain light");
            Exception? fault = null;
            try { await PyInterop.SyncEntitiesAsync("{}").WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception error) { fault = error; }
            Check(fault is InvalidOperationException { Message: "no platform for domain light" }, "sync faults with InvalidOperationException carrying the error: " + fault);

            PyInterop.BeginSyncEntities = (_, _, _) => throw new InvalidOperationException("not ready");
            fault = null;
            try { await PyInterop.SyncEntitiesAsync("{}").WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception error) { fault = error; }
            Check(fault is InvalidOperationException { Message: "not ready" }, "a throwing delegate faults the task instead of throwing: " + fault);
        }
        finally { PyInterop.BeginSyncEntities = previous; }

        await PyInterop.RemoveEntitiesAsync("plug").WaitAsync(TimeSpan.FromSeconds(2));
        await PyInterop.PruneEntitiesAsync("""["plug"]""").WaitAsync(TimeSpan.FromSeconds(2));
        Console.WriteLine("PASS entity pump: SyncEntitiesAsync completes with the success text and faults with the error text");
    }
}
