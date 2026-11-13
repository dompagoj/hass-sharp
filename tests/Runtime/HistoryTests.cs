using HassSharp;
static class HistoryTests
{
    [RuntimeSuite("--history", Order = 4)]
    public static async Task Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "history.db");
            var store = new RunHistoryStore(path, 3);
            var id = store.Identity("one.cs");
            store.Rename("one.cs", "two.cs");
            Check(new RunHistoryStore(path, 3).Identity("two.cs") == id, "stable identity");
            var unfinished = new InvocationRecord { AutomationId = id, Revision = "r1", Action = "Run", Cause = "Manual" };
            store.Save(unfinished);
            store = new RunHistoryStore(path, 3);
            Check(store.Read(id, 0, 10).Single().Status == "Interrupted", "restart interrupts");
            await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => store.Save(new InvocationRecord {
                AutomationId = id, Revision = "r1", Action = "Run", Cause = "Manual", Status = "Succeeded", EndUtc = DateTime.UtcNow
            }))));
            Check(store.Read(id, 0, 100).Count == 3, "retention concurrent writes");
            Check(store.Read(id, 1, 1).Count == 1, "pagination");
            Check(System.Text.Json.JsonSerializer.Serialize(store.Read(id, 0, 1)[0]).Contains("Z\""), "serialized UTC marker");
            Check(store.Read(id, 0, 1)[0].ReceivedUtc.Kind == DateTimeKind.Utc, "UTC survives SQLite read");
            PyInterop.Entity = entity => new() { EntityId = entity, State = "fresh" };
            Probe.Mode = AutomationMode.Single;
            Probe.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Probe.Starts = System.Threading.Channels.Channel.CreateUnbounded<InvocationCause>();
            var manager = new UserScriptManager { History = new RunHistoryStore(Path.Combine(folder, "runtime.db")) };
            await manager.LoadUserScripts([new() { FilePath = "history.cs", FileName = "history", SourceCode = "revision", Assembly = typeof(Probe).Assembly, WrittenToDisk = true }]);
            var script = manager.GetUserScripts().Single();
            await Probe.Starts.Reader.ReadAsync();
            await manager.RunNow(script.AutomationId, nameof(Probe), nameof(Probe.Run));
            Check(manager.History.Read(script.AutomationId, 0, 100).Any(r => r.Status == "Skipped" && r.Cause == "Manual"), "manual skip recorded");
            manager.UnloadUserScripts();
            await script.Classes.Single().StartupCompletion;
            Check(manager.History.Read(script.AutomationId, 0, 100).Any(r => r.Status == "Cancelled"), "cancel recorded");
            Probe.Release.TrySetResult();
            await manager.LoadUserScripts([script.CompiledScript]);
            await manager.GetUserScripts().Single().Classes.Single().StartupCompletion;
            Check(manager.History.Read(script.AutomationId, 0, 100).Any(r => r.Status == "Succeeded" && r.DurationMs != null), "success duration");
            PyInterop.Entity = entity => new() { EntityId = entity, State = "wrong" };
            await manager.RunNow(script.AutomationId, nameof(Probe), nameof(Probe.Run));
            Check(manager.History.Read(script.AutomationId, 0, 100).Any(r => r.Status == "Failed" && r.Error!.Contains("refresh failed")), "failure recorded");
            var dispatched = manager.DispatchManual(script.AutomationId, nameof(Probe), nameof(Probe.Run));
            Check(manager.History.Read(script.AutomationId, 0, 100).Any(r => r.RunId == dispatched), "dispatch returns persisted run id");
            manager.UnloadUserScripts();
            // A failed initial history write must prevent any action from starting.
            var blockedPath = Path.Combine(folder, "blocked.db");
            var blocked = new RunHistoryStore(blockedPath);
            var blockedManager = new UserScriptManager { History = blocked };
            await blockedManager.LoadUserScripts([script.CompiledScript]);
            var blockedScript = blockedManager.GetUserScripts().Single();
            await blockedScript.Classes.Single().StartupCompletion;
            File.Delete(blockedPath);
            Directory.CreateDirectory(blockedPath);
            var count = Probe.Completed.Count;
            var rejected = false;
            try { await blockedManager.RunNow(blockedScript.AutomationId, nameof(Probe), nameof(Probe.Run)); }
            catch { rejected = true; }
            Check(rejected && Probe.Completed.Count == count, "database failure rejects invocation");
            blockedManager.UnloadUserScripts();
            Console.WriteLine("PASS history: identity, restart, retention, pagination, concurrency, manual, skip, cancel, success, failure");
        }
        finally { Directory.Delete(folder, true); }
    }
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); }
}
