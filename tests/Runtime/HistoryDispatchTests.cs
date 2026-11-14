using HassSharp;
using System.Text.Json;
using System.Text.Json.Nodes;
using static VisualExecutionTests;

/// <summary>
/// Run history is written off the trigger path: a trigger registers its run at once, under the activation gate, and
/// the run's first record (and a Skipped or Cancelled one) is written after a hop to the thread pool. So storage that
/// is slow (a busy SD card) holds back neither another automation's trigger nor the editor's list. A first record that
/// cannot be written still fails the run before it executes.
/// </summary>
static class HistoryDispatchTests
{
    [RuntimeSuite("--history-dispatch", Order = 35)]
    public static async Task Run()
    {
        var previous = HassPath.HassConfiguration;
        var root = Path.Combine(Path.GetTempPath(), "history-dispatch-" + Guid.NewGuid());
        var log = PyInterop.Log; var entity = PyInterop.Entity; var subscribe = PyInterop.SubscribeToEntityTracking; var schedule = PyInterop.RegisterSchedule;
        var schedules = new List<Func<string, Task>>();
        PyInterop.RegisterSchedule = (_, fire) => { lock (schedules) schedules.Add(fire); return () => { }; };
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        PyInterop.Log = (_, message) => messages.Enqueue(message);
        PyInterop.Entity = id => new() { EntityId = id, State = "on" };
        PyInterop.SubscribeToEntityTracking = _ => { };
        var manager = new HassSharpManager(root);
        var release = new ManualResetEventSlim(false);
        RunHistoryStore? store = null;
        try
        {
            HassPath.HassConfiguration = root;
            await manager.Init();
            async Task<JsonNode> Visual(object request) => JsonNode.Parse(await manager.VisualRequest(JsonSerializer.Serialize(request)))!;
            var automations = new Dictionary<string, string>();
            foreach (var name in new[] { "a", "b", "c", "r" })
            {
                // r restarts: each run waits until a newer one cancels it.
                var graph = G(Code("run", name == "r" ? "Logger.Info(\"r start\");try{await Task.Delay(60000,CancellationToken);}finally{Logger.Info(\"r end\");}" : $"Logger.Info(\"ran {name}\");"));
                graph["nodes"]![0]!["config"] = name == "c" ? new JsonObject { ["type"] = "daily", ["time"] = "07:30", ["timeZone"] = "UTC" }
                    : new JsonObject { ["type"] = "entity", ["entity"] = "binary_sensor." + name, ["mode"] = name == "r" ? "Restart" : "Single" };
                var saved = await Visual(new { operation = "save", id = name, graph = Derived(graph), expectedRevision = 0 });
                Check(saved["result"]?["active"]?.GetValue<bool>() == true, $"{name} is active: " + saved.ToJsonString());
                automations[name] = saved["result"]!["document"]!["automationId"]!.GetValue<string>();
            }
            store = (RunHistoryStore)typeof(HassSharpManager).GetField("_history", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(manager)!;
            var scripts = (UserScriptManager)typeof(HassSharpManager).GetField("_userScriptManager", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(manager)!;
            Task<Task> Change(string name) => Task.Factory.StartNew(() => manager.OnTrackedEntityChange("binary_sensor." + name,
                new() { EntityId = "binary_sensor." + name, State = "on" }, new() { EntityId = "binary_sensor." + name, State = "off" }), TaskCreationOptions.LongRunning);
            string[] Statuses(string name) => JsonNode.Parse(manager.GetRunHistory(automations[name], 0, 20, null))!["runs"]!.AsArray().Select(r => r!["status"]!.GetValue<string>()).ToArray();

            // Storage stops answering: every write waits until the test lets it go.
            var waiting = new ManualResetEventSlim(false);
            store.SaveHook = () => { waiting.Set(); release.Wait(TimeSpan.FromSeconds(30)); };
            var first = Change("a");
            Check(first.Wait(TimeSpan.FromSeconds(2)), "a trigger waited for its run's first history write");
            Check(waiting.Wait(TimeSpan.FromSeconds(5)), "the run's first history write did not start");
            // While that write waits: the editor's list answers, and another automation's trigger dispatches.
            Check(Task.Run(scripts.GetUserScripts).Wait(TimeSpan.FromSeconds(2)), "GetUserScripts waited for a history write");
            var second = Change("b");
            Check(second.Wait(TimeSpan.FromSeconds(2)), "another automation's trigger waited for a history write");
            // A schedule claims its occurrence in history: that waits too, but not in the trigger.
            Func<string, Task> daily; lock (schedules) daily = schedules.Single();
            var third = Task.Factory.StartNew(() => daily("2026-11-14T07:30:00+00:00"), TaskCreationOptions.LongRunning);
            Check(third.Wait(TimeSpan.FromSeconds(2)), "a schedule trigger waited for a history write");
            Check(Task.Run(scripts.GetUserScripts).Wait(TimeSpan.FromSeconds(2)), "GetUserScripts waited for a history write after the schedule fired");
            Check(!messages.Contains("ran a") && !messages.Contains("ran b") && !messages.Contains("ran c"), "a run executed before its first record was written");
            release.Set();
            await first.Result.WaitAsync(TimeSpan.FromSeconds(10)); await second.Result.WaitAsync(TimeSpan.FromSeconds(10)); await third.Result.WaitAsync(TimeSpan.FromSeconds(10));
            store.SaveHook = null;
            Check(messages.Contains("ran a") && messages.Contains("ran b") && messages.Contains("ran c"), "the runs did not execute once storage answered");
            Check(new[] { "a", "b", "c" }.All(name => Statuses(name).SequenceEqual(["Succeeded"])), "recorded: " + string.Join("; ", new[] { "a", "b", "c" }.Select(name => name + " " + string.Join(",", Statuses(name)))));
            // The occurrence was claimed once: the same instant again does not run.
            await daily("2026-11-14T07:30:00+00:00");
            Check(Statuses("c").Length == 1, "a claimed occurrence ran again");
            // The claim is SQLite's, not the loaded automation's: a new revision (which has seen no occurrence) firing
            // the same instant does not run it either.
            async Task Revise(string said, long expected)
            {
                var revised = G(Code("run", $"Logger.Info(\"{said}\");"));
                revised["nodes"]![0]!["config"] = new JsonObject { ["type"] = "daily", ["time"] = "07:30", ["timeZone"] = "UTC" };
                var saved = await Visual(new { operation = "save", id = "c", graph = Derived(revised), expectedRevision = expected });
                Check(saved["result"]?["active"]?.GetValue<bool>() == true, $"c revision {expected + 1} is active: " + saved.ToJsonString());
            }
            Func<string, Task> Latest() { lock (schedules) return schedules[^1]; }
            await Revise("ran c again", 1);
            await Latest()("2026-11-14T07:30:00+00:00");
            Check(!messages.Contains("ran c again") && Statuses("c").Length == 1, "a new revision ran an occurrence claimed in history");
            // Replaced between claim and dispatch: the old revision gives the claim back, and the new one runs the occurrence.
            var claiming = new ManualResetEventSlim(false); var proceed = new ManualResetEventSlim(false); var swapped = new ManualResetEventSlim(false);
            store.ClaimHook = () => { claiming.Set(); proceed.Wait(TimeSpan.FromSeconds(30)); };
            var stale = Task.Factory.StartNew(() => Latest()("2026-11-15T07:30:00+00:00"), TaskCreationOptions.LongRunning);
            Check(claiming.Wait(TimeSpan.FromSeconds(5)), "the schedule did not claim its occurrence");
            store.ClaimHook = null;
            scripts.Swapped = () => { scripts.Swapped = null; swapped.Set(); };
            var replacing = Revise("ran c third", 2);
            Check(swapped.Wait(TimeSpan.FromSeconds(10)), "the save did not swap the revision while the claim was in flight");
            proceed.Set();
            await replacing; await stale.Result.WaitAsync(TimeSpan.FromSeconds(10));
            Check(Statuses("c").Length == 1 && !messages.Contains("ran c again"), "the replaced revision ran the occurrence it claimed: " + string.Join(",", Statuses("c")));
            await Latest()("2026-11-15T07:30:00+00:00");
            Check(messages.Contains("ran c third") && Statuses("c").Length == 2, "the claim of the replaced revision was not given back: " + string.Join(",", Statuses("c")));

            // A first record that cannot be written fails the run before it executes: its code does not run. The Failed
            // record written in its place carries the revision's snapshot, as the first record would have (C8).
            var revised = G(Code("run", "Logger.Info(\"ran a again\");"));
            revised["nodes"]![0]!["config"] = new JsonObject { ["type"] = "entity", ["entity"] = "binary_sensor.a" };
            var resaved = await Visual(new { operation = "save", id = "a", graph = Derived(revised), expectedRevision = 1 });
            Check(resaved["result"]?["active"]?.GetValue<bool>() == true, "a's second revision is active: " + resaved.ToJsonString());
            var failOnce = 1;
            store.SaveHook = () => { if (Interlocked.Exchange(ref failOnce, 0) == 1) throw new IOException("disk full"); };
            var refused = Change("a").Result;
            try { await refused.WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("a run whose first record failed did not fail"); }
            catch (IOException error) { Check(error.Message == "disk full", "the run failed with another error: " + error.Message); }
            store.SaveHook = null;
            Check(!messages.Contains("ran a again"), "a run executed although its first record was not written");
            var failed = store.Read(automations["a"], 0, 1).Single();
            Check(failed.Status == "Failed" && store.ReadSnapshot(failed.Revision) != null, $"the failed run's record ({failed.Status}) has no snapshot of its revision");

            // Restart: a newer run cancels the one running only once its own first record is written. One whose first
            // record fails cancels nothing; the next cancels the run still going and starts only after it has ended.
            async Task Until(Func<bool> done, string what)
            {
                for (var started = DateTime.UtcNow; !done(); await Task.Delay(10))
                    Check(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), what);
            }
            // Each change's task is its run's: these are not awaited, the runs wait for a newer one.
            _ = Change("r").Result;
            await Until(() => messages.Contains("r start") && Statuses("r").SequenceEqual(["Running"]), "the first restart run did not start");
            failOnce = 1;
            store.SaveHook = () => { if (Interlocked.Exchange(ref failOnce, 0) == 1) throw new IOException("disk full"); };
            try { await Change("r").Result.WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("a restart run whose first record failed did not fail"); }
            catch (IOException) { }
            store.SaveHook = null;
            await Task.Delay(100);
            Check(!messages.Contains("r end") && Statuses("r").Count(s => s == "Running") == 1, "a restart run whose first record failed cancelled the run going: " + string.Join(",", Statuses("r")));
            _ = Change("r").Result;
            await Until(() => messages.Count(m => m == "r start") == 2, "the third restart run did not start");
            var order = messages.Where(m => m is "r start" or "r end").ToArray();
            Check(order.SequenceEqual(["r start", "r end", "r start"]), "the newer restart run started before the one it cancelled ended: " + string.Join(",", order));
            var restarted = store.Read(automations["r"], 0, 10);
            Check(restarted.Select(r => r.Status).OrderBy(s => s).SequenceEqual(["Cancelled", "Failed", "Running"]) && restarted.Single(r => r.Status == "Cancelled").Error == "Restarted by a newer trigger",
                "restart runs: " + string.Join("; ", restarted.Select(r => r.Status + " " + r.Error)));
            Console.WriteLine("PASS history dispatch: while a history write waits on storage, the trigger that caused it has returned, the editor's list answers and another automation's entity or schedule trigger dispatches; a schedule occurrence claimed in history is not run by a new revision, and one claimed by a revision replaced before it ran is given back to the new one; a first record that cannot be written fails the run before it executes, and its Failed record carries the revision's snapshot; a Restart run whose first record fails cancels nothing, and the next one starts only after the run it cancels has ended");
        }
        finally
        {
            release.Set();
            if (store != null) { store.SaveHook = null; store.ClaimHook = null; }
            await manager.UnloadAsync();
            PyInterop.Log = log; PyInterop.Entity = entity; PyInterop.SubscribeToEntityTracking = subscribe; PyInterop.RegisterSchedule = schedule;
            HassPath.HassConfiguration = previous;
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
