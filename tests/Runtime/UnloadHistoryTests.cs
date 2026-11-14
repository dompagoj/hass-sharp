using HassSharp;
using System.Text.Json;
using System.Text.Json.Nodes;
using static VisualExecutionTests;

/// <summary>
/// When <see cref="HassSharpManager.UnloadAsync"/> returns, the runs it cancelled have finished and their history is
/// written: nothing writes run history (or anything else under storage) afterwards, so the entry can be set up again, or
/// its storage removed, at once.
/// </summary>
static class UnloadHistoryTests
{
    [RuntimeSuite("--unload-history", Order = 34)]
    public static async Task Run()
    {
        var previous = HassPath.HassConfiguration;
        var root = Path.Combine(Path.GetTempPath(), "unload-history-" + Guid.NewGuid());
        var log = PyInterop.Log; PyInterop.Log = (_, _) => { };
        var manager = new HassSharpManager(root);
        try
        {
            HassPath.HassConfiguration = root;
            await manager.Init();
            async Task<JsonNode> Visual(object request) => JsonNode.Parse(await manager.VisualRequest(JsonSerializer.Serialize(request)))!;
            // A run that waits, and whose cleanup after its cancellation takes a moment, as user code's may.
            var graph = Derived(G(Code("wait", "try { await Task.Delay(60000, CancellationToken); } finally { await Task.Delay(30); }")));
            var saved = await Visual(new { operation = "save", id = "slow", graph, expectedRevision = 0 });
            Check(saved["result"]?["active"]?.GetValue<bool>() == true, "the waiting automation is active: " + saved.ToJsonString());
            var automation = saved["result"]!["document"]!["automationId"]!.GetValue<string>();
            for (var round = 1; round <= 3; round++)
            {
                var runId = (await Visual(new { operation = "run", id = "slow", triggerId = "t" }))["result"]!["runId"]!.GetValue<string>();
                var started = DateTime.UtcNow;
                while (Status(manager, automation, runId) != "Running")
                {
                    Check(DateTime.UtcNow - started < TimeSpan.FromSeconds(20), "the run did not start");
                    await Task.Delay(10);
                }
                var runs = Path.Combine(root, ".storage", "hass_sharp");
                await manager.UnloadAsync();
                // At once: what unload returned with must be the end of it.
                var status = Status(manager, automation, runId);
                var written = Directory.GetFiles(runs, "runs.db*").ToDictionary(f => f, File.GetLastWriteTimeUtc);
                Check(status == "Cancelled", $"round {round}: when unload returns the cancelled run is recorded {status ?? "nowhere"}, not Cancelled");
                await Task.Delay(300);
                var later = Directory.GetFiles(runs, "runs.db*").Where(f => !written.TryGetValue(f, out var at) || File.GetLastWriteTimeUtc(f) != at).ToArray();
                Check(later.Length == 0, $"round {round}: run history was written after unload returned: " + string.Join(", ", later.Select(Path.GetFileName)));
                await manager.Init();
            }
            // A run that does not end holds unloading back only so long (visual steps abandon user code that ignores the
            // cancellation, so that is shown on the wait itself); a failed run is no failure of unloading.
            var never = new TaskCompletionSource().Task;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Check(!await UserScript.Drain([never, Task.CompletedTask], TimeSpan.FromMilliseconds(200)) && clock.Elapsed < TimeSpan.FromSeconds(2), $"a run that never ends held the drain {clock.Elapsed.TotalSeconds:F1} s");
            Check(await UserScript.Drain([Task.FromException(new InvalidOperationException("the run failed")), Task.CompletedTask], TimeSpan.FromSeconds(5)), "a failed run counts as ended");
            Console.WriteLine("PASS unload history: when unload returns, the runs it cancelled are recorded as Cancelled and nothing writes run history afterwards; a run that does not end delays it at most the drain timeout");
        }
        finally
        {
            await manager.UnloadAsync();
            PyInterop.Log = log;
            HassPath.HassConfiguration = previous;
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    static string? Status(HassSharpManager manager, string automation, string runId) =>
        JsonNode.Parse(manager.GetRunHistory(automation, 0, 20, null))!["runs"]!.AsArray().FirstOrDefault(r => r!["runId"]!.GetValue<string>() == runId)?["status"]?.GetValue<string>();
}
