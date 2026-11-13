using HassSharp;
static class ScheduleTests
{
    [RuntimeSuite("--schedule", Order = 5)]
    public static async Task Run()
    {
        var callbacks = new List<Func<string, Task>>();
        var removed = 0;
        PyInterop.RegisterSchedule = (json, fire) => { callbacks.Add(fire); return () => removed++; };
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            var history = new RunHistoryStore(path);
            var manager = new UserScriptManager { History = history };
            Probe.Mode = AutomationMode.Single;
            Probe.Schedules = [AutomationSchedule.Daily("noon", new TimeOnly(12, 0), "UTC")];
            Probe.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Probe.Starts = System.Threading.Channels.Channel.CreateUnbounded<InvocationCause>();
            PyInterop.Entity = id => new() { EntityId = id, State = "fresh" };
            var compiled = new CompiledUserScript { FilePath = "scheduled.cs", FileName = "scheduled", SourceCode = "schedule", Assembly = typeof(Probe).Assembly, WrittenToDisk = true };
            await manager.LoadUserScripts([compiled]);
            await Probe.Starts.Reader.ReadAsync();
            var planned = "2026-11-12T12:00:00+00:00";
            await callbacks[0](planned);
            await callbacks[0](planned);
            var id = manager.GetUserScripts().Single().AutomationId;
            var record = history.Read(id, 0, 100).Single(r => r.Cause == "Schedule");
            Check(record.Status == "Skipped" && record.PlannedUtc == DateTime.Parse(planned).ToUniversalTime() && record.Trigger == "{\"kind\":\"schedule\",\"scheduleId\":\"noon\",\"planned\":\"2026-11-12T12:00:00.0000000Z\"}", "collision and planned history");
            manager.UnloadUserScripts();
            Check(removed == 1, "schedule unsubscribe");
            await callbacks[0]("2026-11-13T12:00:00+00:00");
            Check(history.Read(id, 0, 100).Count(r => r.Cause == "Schedule") == 1, "stale schedule ignored");
            Probe.Release.TrySetResult();
            await manager.LoadUserScripts([compiled]);
            await manager.GetUserScripts().Single().Classes.Single().StartupCompletion;
            await callbacks[1](planned);
            Check(history.Read(id, 0, 100).Count(r => r.Cause == "Schedule") == 1, "reload occurrence dedup");
            Probe.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Probe.Starts = System.Threading.Channels.Channel.CreateUnbounded<InvocationCause>();
            var entityRun = manager.RunEntries("sensor.test", new(new() { EntityId = "sensor.test" }));
            Check(await Probe.Starts.Reader.ReadAsync() == InvocationCause.Entity, "entity running");
            await callbacks[1]("2026-11-13T12:00:00+00:00");
            Check(history.Read(id, 0, 100).Count(r => r.Cause == "Schedule" && r.Status == "Skipped") == 2,
                "entity/schedule collision shares mode");
            Probe.Release.TrySetResult();
            await entityRun;
            await callbacks[1]("2026-11-14T12:00:00+00:00");
            Check(history.Read(id, 0, 100).Any(r => r.Cause == "Schedule" && r.Status == "Succeeded"), "scheduled shared runner succeeds");
            manager.UnloadUserScripts();
            var restarted = new RunHistoryStore(path);
            Check(!restarted.ClaimOccurrence(record.RunId), "persistent claim survives process restart");
            Probe.Schedules = null;
            Console.WriteLine("PASS schedules: collision, history, dedup, reload, stale callback, unsubscribe");
        }
        finally { File.Delete(path); }
    }
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
