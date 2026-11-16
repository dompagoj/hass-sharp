using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

static class HistoryTrackingTests
{
    static async Task Until(Func<bool> done, string message)
    {
        for (var start = DateTime.UtcNow; !done(); await Task.Delay(10))
            Check(DateTime.UtcNow - start < TimeSpan.FromSeconds(10), message);
    }

    [RuntimeSuite("--history-tracking", Order = 36)]
    public static async Task Run()
    {
        var previous = HassPath.HassConfiguration;
        var root = Path.Combine(Path.GetTempPath(), "history-tracking-" + Guid.NewGuid());
        var log = PyInterop.Log; var entity = PyInterop.Entity;
        var subscribe = PyInterop.SubscribeToEntityTracking; var unsubscribe = PyInterop.UnSubscribeFromEntityTracking;
        var register = PyInterop.BeginRegisterHaTriggers; var remove = PyInterop.BeginRemoveHaTriggers;
        var schedule = PyInterop.RegisterSchedule;
        var messages = new ConcurrentQueue<string>();
        var natives = new List<Func<string, Task>>();
        var schedules = new List<Func<string, Task>>();
        PyInterop.Log = (_, message) => messages.Enqueue(message);
        PyInterop.Entity = id => new() { EntityId = id, State = "on" };
        PyInterop.SubscribeToEntityTracking = _ => { };
        PyInterop.UnSubscribeFromEntityTracking = _ => { };
        PyInterop.BeginRegisterHaTriggers = (_, _, fire, success, _) => { natives.Add(fire); success(Guid.NewGuid().ToString()); };
        PyInterop.BeginRemoveHaTriggers = (_, success, _) => success();
        PyInterop.RegisterSchedule = (_, fire) => { schedules.Add(fire); return () => { }; };
        HassPath.HassConfiguration = root;
        var manager = new HassSharpManager(root);
        try
        {
            await manager.Init();
            var store = (RunHistoryStore)typeof(HassSharpManager).GetField("_history", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
            async Task<JsonNode> Visual(object request)
            {
                var response = JsonNode.Parse(await manager.VisualRequest(JsonSerializer.Serialize(request)))!;
                Check(response["ok"]!.GetValue<bool>(), "visual request failed: " + response);
                return response["result"]!;
            }
            async Task<JsonNode> Save(string id, JsonObject graph, long revision = 0)
            {
                var saved = await Visual(new { operation = "save", id, graph = Derived(graph), expectedRevision = revision });
                Check(saved["active"]!.GetValue<bool>(), "automation did not activate: " + saved);
                return saved["document"]!;
            }
            Task Fire(string id) => manager.OnTrackedEntityChange(id, new() { EntityId = id, State = "on" }, new() { EntityId = id, State = "off" });
            InvocationRecord[] Runs(string id) => store.Read(VisualTests.Loaded(manager, id)!.AutomationId, 0, 100).ToArray();
            JsonObject Quiet(string name, object config, string? code = null)
            {
                var graph = G(code == null ? N("log", "log", new { value = Lit("string", name) }) : Code("run", code));
                graph["name"] = name;
                graph["nodes"]![0]!["config"] = JsonSerializer.SerializeToNode(config);
                return graph;
            }
            void NoRecording(string id, int tracesBefore)
            {
                Check(Runs(id).Length == 0, id + " stored an automatic run despite tracking being off");
                Check(VisualTrace.Created == tracesBefore, id + " created a trace recorder despite tracking being off");
                Check(store.ReadSnapshot(VisualTests.Loaded(manager, id)!.Revision) == null, id + " stored an untracked revision's snapshot");
            }

            // The screenshot's pattern: one meaningful trigger and two supporting triggers in the same graph.
            var graph = new JsonObject
            {
                ["version"] = 5, ["name"] = "Three triggers",
                ["nodes"] = new JsonArray(
                    N("primary", "trigger", new { type = "entity", entity = "binary_sensor.primary" }),
                    N("startup", "trigger", new { type = "startup", trackHistory = false }),
                    N("native", "trigger", new { type = "ha", trigger = new { trigger = "event", event_type = "history_tracking" }, trackHistory = false }),
                    N("p", "log", new { value = Lit("string", "primary ran") }),
                    N("s", "log", new { value = Lit("string", "startup ran") }),
                    N("n", "log", new { value = Lit("string", "native ran") })),
                ["edges"] = new JsonArray(E("primary", "p"), E("startup", "s"), E("native", "n")),
            };
            var before = VisualTrace.Created;
            await Save("mixed", graph);
            await VisualTests.Loaded(manager, "mixed")!.Classes.Single().StartupCompletion;
            Check(messages.Contains("startup ran"), "untracked startup did not execute");
            NoRecording("mixed", before);
            await natives.Single()("{\"platform\":\"event\",\"event\":{\"event_type\":\"history_tracking\"}}");
            Check(messages.Contains("native ran"), "untracked native trigger did not execute");
            NoRecording("mixed", before);
            await Fire("binary_sensor.primary");
            Check(messages.Contains("primary ran"), "tracked entity trigger did not execute");
            var primary = Runs("mixed").Single();
            Check(primary is { TriggerNodeId: "primary", Status: "Succeeded", HasTrace: true }, "the default trigger lost its history or trace");

            // A manual invocation is always recorded, including its trace and the ID returned by Run now.
            var script = VisualTests.Loaded(manager, "mixed")!;
            var startupMethod = VisualNodeCatalog.EffectiveTriggerMethod(graph["nodes"]![1]!.AsObject());
            var manual = JsonNode.Parse(manager.RunNow(script.AutomationId, "VisualAutomation", startupMethod))!["runId"]!.GetValue<string>();
            Check(store.ReadRun(manual) != null, "Run now returned an ID that was not persisted");
            await Until(() => store.ReadRun(manual)?.EndUtc != null, "manual run did not end");
            Check(store.ReadRun(manual) is { Cause: "Manual", Status: "Succeeded", HasTrace: true }, "untracked trigger's manual run lost its result or trace");
            Check(JsonNode.Parse(manager.GetRunTrace(manual))!["trace"] != null, "manual trace cannot be read");

            // Saving/reopening and integration reload must preserve false; true must restore recording.
            graph["nodes"]![0]!["config"]!["trackHistory"] = false;
            await Save("mixed", graph, 1);
            var read = await Visual(new { operation = "read", id = "mixed" });
            Check(read["graph"]!["nodes"]![0]!["config"]!["trackHistory"]!.GetValue<bool>() == false, "save/read lost tracking off");
            await manager.UnloadAsync(); await manager.Init();
            await VisualTests.Loaded(manager, "mixed")!.Classes.Single().StartupCompletion;
            var retained = Runs("mixed").Length;
            before = VisualTrace.Created;
            await Fire("binary_sensor.primary");
            Check(Runs("mixed").Length == retained && VisualTrace.Created == before, "reload lost tracking off");
            graph["nodes"]![0]!["config"]!["trackHistory"] = true;
            await Save("mixed", graph, 2);
            await VisualTests.Loaded(manager, "mixed")!.Classes.Single().StartupCompletion;
            await Fire("binary_sensor.primary");
            Check(Runs("mixed").Length == retained + 1 && Runs("mixed")[0].HasTrace, "tracking on did not restore history and traces");

            // Deduplication remains persistent even when scheduled runs have no history rows.
            var daily = Quiet("daily ran", new { type = "daily", time = "07:30", timeZone = "UTC", trackHistory = false });
            await Save("daily", daily);
            before = VisualTrace.Created;
            const string planned = "2026-11-16T07:30:00+00:00";
            await schedules[^1](planned); await schedules[^1](planned);
            Check(messages.Count(m => m == "daily ran") == 1, "an untracked scheduled occurrence ran twice");
            NoRecording("daily", before);
            await Save("daily", daily, 1);
            await schedules[^1](planned);
            Check(messages.Count(m => m == "daily ran") == 1, "a new revision repeated an untracked occurrence");
            await manager.UnloadAsync(); await manager.Init();
            await VisualTests.Loaded(manager, "mixed")!.Classes.Single().StartupCompletion;
            before = VisualTrace.Created;
            await schedules[^1](planned);
            await schedules[^1]("2026-11-17T07:30:00+00:00");
            Check(messages.Count(m => m == "daily ran") == 2, "persistent deduplication or the next untracked occurrence failed");
            NoRecording("daily", before);

            // An automatic failure still reaches the error log without creating a history row or trace.
            await Save("failing", Quiet("failure", new { type = "entity", entity = "binary_sensor.failing", trackHistory = false }, "throw new InvalidOperationException(\"untracked failure\");"));
            before = VisualTrace.Created;
            await Fire("binary_sensor.failing");
            Check(messages.Any(m => m.Contains("failed:") && m.Contains("untracked failure")), "untracked failure was not logged");
            NoRecording("failing", before);

            // Admission and cancellation use the same runner with recording off, in all four run modes.
            foreach (var mode in new[] { "Single", "Restart", "Queued", "Parallel" })
            {
                var id = "mode-" + mode.ToLowerInvariant();
                var entityId = "binary_sensor." + mode.ToLowerInvariant();
                await Save(id, Quiet(id, new { type = "entity", entity = entityId, mode, trackHistory = false },
                    $"Logger.Info(\"{id} start\"); try {{ await Task.Delay(60000, CancellationToken); }} finally {{ Logger.Info(\"{id} end\"); }}"));
                before = VisualTrace.Created;
                var first = Fire(entityId);
                await Until(() => messages.Contains(id + " start"), mode + " did not start");
                var second = Fire(entityId);
                if (mode == "Single") await second.WaitAsync(TimeSpan.FromSeconds(10));
                if (mode is "Restart" or "Parallel")
                    await Until(() => messages.Count(m => m == id + " start") == 2, mode + " did not admit the next run");
                if (mode is "Single" or "Queued")
                    Check(messages.Count(m => m == id + " start") == 1, mode + " overlapped its first run");
                if (mode == "Restart")
                    Check(messages.Where(m => m.StartsWith(id)).SequenceEqual([id + " start", id + " end", id + " start"]), "Restart did not finish cancellation before the next run");
                NoRecording(id, before);
                VisualTests.Loaded(manager, id)!.CancelLifetime();
                await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
                NoRecording(id, before);
                Check(messages.Count(m => m == id + " end") == (mode is "Restart" or "Parallel" ? 2 : 1), mode + " did not drain cancelled runs");
            }
            Console.WriteLine("PASS per-trigger history: defaults, three triggers, automatic startup/native/entity suppression, manual traces, save/reload, schedules and persistent deduplication, failure logs, all run modes and cancellation");
        }
        finally
        {
            await manager.UnloadAsync();
            PyInterop.Log = log; PyInterop.Entity = entity;
            PyInterop.SubscribeToEntityTracking = subscribe; PyInterop.UnSubscribeFromEntityTracking = unsubscribe;
            PyInterop.BeginRegisterHaTriggers = register; PyInterop.BeginRemoveHaTriggers = remove; PyInterop.RegisterSchedule = schedule;
            HassPath.HassConfiguration = previous;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
