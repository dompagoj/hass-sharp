using HassSharp;
static class HandoverTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static CompiledUserScript Compiled() => new() { FilePath = "handover.cs", FileName = "handover", SourceCode = "test", Assembly = typeof(Probe).Assembly, WrittenToDisk = true };
    [RuntimeSuite("--handover", Order = 2)]
    public static async Task Run()
    {
        foreach (var unload in new[] { false, true })
        {
            Probe.Mode = AutomationMode.Restart;
            Probe.Starts = System.Threading.Channels.Channel.CreateUnbounded<InvocationCause>();
            Probe.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Probe.DelayedExit = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var manager = new UserScriptManager();
            await manager.LoadUserScripts([Compiled()]);
            await Probe.Starts.Reader.ReadAsync();
            var klass = manager.GetUserScripts().Single().Classes.Single();
            var second = klass.RunMethod(nameof(Probe.Run));
            var third = klass.RunMethod(nameof(Probe.Run));
            // Give an incorrectly admitted replacement time to announce entry.
            await Task.Delay(100);
            var overlap = Probe.Starts.Reader.TryRead(out _);
            if (unload) manager.UnloadUserScripts();
            Probe.DelayedExit.SetResult();
            Probe.Release.SetResult();
            await Task.WhenAll(klass.StartupCompletion, second, third).WaitAsync(TimeSpan.FromSeconds(3));
            Check(!overlap, "Restart replacement entered before predecessor exited");
            Check(Probe.Starts.Reader.TryRead(out _) == !unload, "Only final restart may enter; unload suppresses it");
            manager.UnloadUserScripts();
            Probe.DelayedExit = null;
        }
        Probe.Mode = AutomationMode.Parallel;
        Probe.Release = new(TaskCreationOptions.RunContinuationsAsynchronously); Probe.Release.SetResult();
        Probe.Schedules = [AutomationSchedule.Daily("noon", new TimeOnly(12, 0), "UTC")];
        var callbacks = new List<Func<string, Task>>();
        PyInterop.RegisterSchedule = (_, fire) => { callbacks.Add(fire); return () => {}; };
        PyInterop.Entity = id => new() { EntityId = id, State = "fresh" };
        var oldManager = new UserScriptManager();
        await oldManager.LoadUserScripts([Compiled()]);
        var old = oldManager.GetUserScripts().Single();
        await old.Classes.Single().StartupCompletion;
        var before = Probe.Completed.Count;
        Probe.Configuring = () => {
            oldManager.RunEntries("sensor.test", new(new() { EntityId = "sensor.test" })).GetAwaiter().GetResult();
            callbacks[0]("2026-11-12T12:00:00Z").GetAwaiter().GetResult();
            throw new InvalidOperationException("activation rejected");
        };
        try { await oldManager.UpdateUserScript(Compiled()); throw new Exception("update should fail"); }
        catch (InvalidOperationException) { }
        finally { Probe.Configuring = null; }
        Check(Probe.Completed.Count == before + 2, "Old entity and schedule must remain active during failed preparation");
        Check(ReferenceEquals(oldManager.GetUserScripts().Single(), old), "failed update retains original instance");
        await oldManager.RunEntries("sensor.test", new(new() { EntityId = "sensor.test" }));
        Check(Probe.Completed.Count == before + 3, "rollback has one entity action");
        var preparing = true;
        var failSubscription = true;
        var unsubscribed = 0;
        PyInterop.RegisterSchedule = (_, fire) => {
            callbacks.Add(fire);
            if (preparing)
            {
                // Both versions have entity entries now, but only the old one may act.
                oldManager.RunEntries("sensor.test", new(new() { EntityId = "sensor.test" })).GetAwaiter().GetResult();
                callbacks[0]("2026-11-13T12:00:00Z").GetAwaiter().GetResult();
                fire("2026-11-13T12:00:00Z").GetAwaiter().GetResult();
                if (failSubscription) throw new InvalidOperationException("subscription rejected");
            }
            return () => unsubscribed++;
        };
        before = Probe.Completed.Count;
        try { await oldManager.UpdateUserScript(Compiled()); throw new Exception("subscribe should fail"); }
        catch (InvalidOperationException) { }
        Check(Probe.Completed.Count == before + 2, "failed subscription leaves old triggers uninterrupted, staged callback inert");
        Check(oldManager.DependencyTracking.GetEntries("sensor.test").Count == 1, "failed staging removes new entity entries");
        failSubscription = false;
        before = Probe.Completed.Count;
        await oldManager.UpdateUserScript(Compiled());
        var current = oldManager.GetUserScripts().Single();
        await current.Classes.Single().StartupCompletion;
        Check(current.AutomationId == old.AutomationId && !ReferenceEquals(current, old), "successful handover keeps identity");
        Check(Probe.Completed.Count == before + 2, "precommit entity and replacement startup run once; repeated schedule stays deduplicated");
        before = Probe.Completed.Count;
        await oldManager.RunEntries("sensor.test", new(new() { EntityId = "sensor.test" }));
        foreach (var callback in callbacks) await callback("2026-11-14T12:00:00Z");
        Check(Probe.Completed.Count == before + 2, "handover has one entity and one schedule action");
        preparing = false;
        oldManager.UnloadUserScripts();
        await oldManager.LoadUserScripts([Compiled()]);
        await oldManager.GetUserScripts().Single().Classes.Single().StartupCompletion;
        before = Probe.Completed.Count;
        foreach (var callback in callbacks) await callback("2026-11-15T12:00:00Z");
        Check(Probe.Completed.Count == before + 1, "reload only has one live schedule");
        oldManager.UnloadUserScripts();
        Check(unsubscribed == 2, "successful replacement and reload unsubscribe");
        await oldManager.LoadUserScripts([Compiled()]);
        await oldManager.GetUserScripts().Single().Classes.Single().StartupCompletion;
        Probe.Configuring = oldManager.UnloadUserScripts;
        var rejectedAfterUnload = false;
        try { await oldManager.UpdateUserScript(Compiled()); }
        catch (InvalidOperationException) { rejectedAfterUnload = true; }
        finally { Probe.Configuring = null; }
        Check(rejectedAfterUnload, "update interrupted by unload is rejected");
        Check(oldManager.GetUserScripts().Count == 0 && oldManager.DependencyTracking.GetEntries("sensor.test").Count == 0,
            "unload during preparation cleans staged subscriptions");
        before = Probe.Completed.Count;
        foreach (var callback in callbacks) await callback("2026-11-16T12:00:00Z");
        Check(Probe.Completed.Count == before, "unload during update leaves no live schedule callbacks");
        Probe.Schedules = null;
        Console.WriteLine("PASS restart delayed exit / rapid chain / unload and failed handover");
    }
}
