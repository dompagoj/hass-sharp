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
            foreach (var name in new[] { "a", "b", "c" })
            {
                var graph = G(Code("run", $"Logger.Info(\"ran {name}\");"));
                graph["nodes"]![0]!["config"] = name == "c" ? new JsonObject { ["type"] = "daily", ["time"] = "07:30", ["timeZone"] = "UTC" }
                    : new JsonObject { ["type"] = "entity", ["entity"] = "binary_sensor." + name };
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

            // A first record that cannot be written fails the run before it executes: its code does not run.
            var failOnce = 1;
            store.SaveHook = () => { if (Interlocked.Exchange(ref failOnce, 0) == 1) throw new IOException("disk full"); };
            var refused = Change("a").Result;
            try { await refused.WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("a run whose first record failed did not fail"); }
            catch (IOException error) { Check(error.Message == "disk full", "the run failed with another error: " + error.Message); }
            store.SaveHook = null;
            Check(messages.Count(m => m == "ran a") == 1, "a run executed although its first record was not written");
            Console.WriteLine("PASS history dispatch: while a history write waits on storage, the trigger that caused it has returned, the editor's list answers and another automation's entity or schedule trigger dispatches; a first record that cannot be written fails the run before it executes");
        }
        finally
        {
            release.Set();
            if (store != null) store.SaveHook = null;
            await manager.UnloadAsync();
            PyInterop.Log = log; PyInterop.Entity = entity; PyInterop.SubscribeToEntityTracking = subscribe; PyInterop.RegisterSchedule = schedule;
            HassPath.HassConfiguration = previous;
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
