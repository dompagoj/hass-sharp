using HassSharp;
using System.Collections.Concurrent;
using System.Text.Json;

static class NativeTriggerTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static CompiledUserScript Compiled(string source = "test") => new() { FilePath = "native.cs", FileName = "native", SourceCode = source, Assembly = typeof(Probe).Assembly };
    static CompiledUserScript NamedCompiled(string name)
    {
        var compiled = Compiled(); compiled.FilePath = name + ".cs"; compiled.FileName = name; return compiled;
    }

    [RuntimeSuite("--native-trigger-review", RunByDefault = false)]
    public static async Task RunReview(string? scenario = null)
    {
        Probe.Mode = AutomationMode.Parallel;
        Probe.Release = new(TaskCreationOptions.RunContinuationsAsynchronously); Probe.Release.SetResult();
        Probe.Configuring = null; Probe.Schedules = null; Probe.DelayedExit = null;
        Probe.Triggers = [new HomeAssistantTrigger("event", "{\"trigger\":\"event\",\"event_type\":\"test\"}")];
        PyInterop.Entity = id => new() { EntityId = id, State = "fresh" };
        var unsubscribeEntity = PyInterop.UnSubscribeFromEntityTracking;
        var registerSchedule = PyInterop.RegisterSchedule;
        try
        {
            if (scenario == null || scenario == "cleanup-token") await ThrowingCleanup(true);
            if (scenario == null || scenario == "cleanup-entity") await ThrowingCleanup(false);
            if (scenario == null || scenario == "epoch-initial") await InvalidationDuringSlowCancellation(false);
            if (scenario == null || scenario == "epoch-direct") await InvalidationDuringSlowCancellation(true);
            if (scenario == null || scenario == "delete-initial") await DeleteDuringInitialRegistration();
            if (scenario == null || scenario == "schedule-drain") await SchedulePublicationDuringCleanup();
        }
        finally
        {
            Probe.Triggers = null; Probe.Schedules = null;
            PyInterop.UnSubscribeFromEntityTracking = unsubscribeEntity;
            PyInterop.RegisterSchedule = registerSchedule;
        }
    }

    static async Task ThrowingCleanup(bool throwingToken)
    {
        var removed = new ConcurrentQueue<(string Id, Action Done)>();
        var removalsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaseNumber = 0;
        PyInterop.BeginRegisterHaTriggers = (_, _, _, success, _) => success("throwing-" + ++leaseNumber);
        PyInterop.BeginRemoveHaTriggers = (id, done, _) => {
            removed.Enqueue((id, done));
            if (removed.Count == 2) removalsStarted.TrySetResult();
        };
        var entityUnsubscribe = PyInterop.UnSubscribeFromEntityTracking;
        var manager = new UserScriptManager();
        await manager.LoadUserScripts([NamedCompiled("throwing-first"), NamedCompiled("throwing-second")]);
        await Task.WhenAll(manager.GetUserScripts().SelectMany(s => s.Classes).Select(c => c.StartupCompletion));
        if (throwingToken) manager.GetUserScripts()[0].LifetimeToken.Register(() => throw new InvalidOperationException("token cleanup failed"));
        else PyInterop.UnSubscribeFromEntityTracking = _ => { entityUnsubscribe(_); throw new InvalidOperationException("entity cleanup failed"); };
        var unloading = Task.Run(manager.UnloadUserScriptsAsync);
        try
        {
            await Task.WhenAny(unloading, removalsStarted.Task).WaitAsync(TimeSpan.FromSeconds(3));
            Check(removed.Count == 2, "all native removals reached despite throwing " + (throwingToken ? "cancellation callback" : "entity unsubscribe"));
            Check(!unloading.IsCompleted, "cleanup errors are deferred until every native removal acknowledges");
            foreach (var removal in removed) removal.Done();
            try { await unloading; throw new Exception("cleanup exception was lost"); }
            catch (AggregateException error)
            {
                Check(error.Flatten().InnerExceptions.Any(e => e.Message == (throwingToken ? "token cleanup failed" : "entity cleanup failed")),
                    "reported cleanup aggregate retains the original callback fault");
            }
            Check(manager.GetUserScripts().Count == 0, "throwing cleanup leaves no active scripts");
        }
        finally
        {
            foreach (var removal in removed) removal.Done();
            PyInterop.UnSubscribeFromEntityTracking = entityUnsubscribe;
        }
        Console.WriteLine("PASS native cleanup continues after throwing " + (throwingToken ? "token callback" : "entity unsubscribe"));
    }

    static async Task InvalidationDuringSlowCancellation(bool directConfigure)
    {
        Probe.Starts = System.Threading.Channels.Channel.CreateUnbounded<InvocationCause>();
        Probe.TriggerPayloads = new();
        var calls = new List<(Func<string, Task> Fire, Action<string> Success)>();
        PyInterop.BeginRegisterHaTriggers = (_, _, fire, success, _) => calls.Add((fire, success));
        PyInterop.BeginRemoveHaTriggers = (_, done, _) => done();
        var manager = new UserScriptManager();
        var firstLoad = manager.LoadUserScripts([NamedCompiled("epoch-first")]);
        calls[0].Success("epoch-first"); await firstLoad;
        var first = manager.GetUserScripts().Single();
        await first.Classes.Single().StartupCompletion;
        while (Probe.Starts.Reader.TryRead(out _)) { }
        while (Probe.TriggerPayloads.TryDequeue(out _)) { }
        using var blocked = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.LifetimeToken.Register(() => { entered.TrySetResult(); if (!blocked.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("release cancellation callback"); });
        UserScript? direct = null;
        var pending = directConfigure
            ? (direct = manager.LoadUserScript(NamedCompiled("epoch-second"))).ConfigureAndStart()
            : manager.LoadUserScripts([NamedCompiled("epoch-second")]);
        var unloading = Task.Run(manager.UnloadUserScriptsAsync);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            calls[1].Success("epoch-second");
            try { await pending.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (InvalidOperationException) when (directConfigure) { }
            await calls[1].Fire("{\"trigger\":{\"id\":\"invalidated\"}}");
            Check(manager.GetUserScripts().Count == 0 && !Probe.Starts.Reader.TryRead(out _) && Probe.TriggerPayloads.IsEmpty,
                "unload epoch prevents " + (directConfigure ? "ConfigureAndStart" : "initial load") + " activation/start/dispatch before token cancellation");
        }
        finally
        {
            blocked.Set();
            await unloading.WaitAsync(TimeSpan.FromSeconds(3));
            if (direct != null) await direct.ClearFromTrackingAsync();
        }
        Console.WriteLine("PASS post-await invalidation during slow cancellation: " + (directConfigure ? "ConfigureAndStart" : "initial load"));
    }

    static async Task DeleteDuringInitialRegistration()
    {
        Probe.Starts = System.Threading.Channels.Channel.CreateUnbounded<InvocationCause>();
        Probe.TriggerPayloads = new();
        var registration = new TaskCompletionSource<(Func<string, Task> Fire, Action<string> Success)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var removal = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        PyInterop.BeginRegisterHaTriggers = (_, _, fire, success, _) => registration.TrySetResult((fire, success));
        PyInterop.BeginRemoveHaTriggers = (_, done, _) => removal.TrySetResult(done);
        var manager = new UserScriptManager();
        var load = manager.LoadUserScripts([NamedCompiled("pending")]);
        var pending = await registration.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var delete = manager.DeleteScriptAsync("pending.cs");
        Check(!delete.IsCompleted, "delete waits for first-load native registration");
        pending.Success("pending-delete");
        var done = await removal.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!delete.IsCompleted, "delete waits for native removal acknowledgement");
        done();
        await Task.WhenAll(load, delete).WaitAsync(TimeSpan.FromSeconds(5));
        Check(manager.GetUserScripts().Count == 0, "delete removes a preparing script");
        await pending.Fire("{\"trigger\":{}}");
        Check(!Probe.Starts.Reader.TryRead(out _) && Probe.TriggerPayloads.IsEmpty, "deleted first-load generation cannot start or dispatch stale callbacks");
        await manager.UnloadUserScriptsAsync();
        Console.WriteLine("PASS deletion drains pending first-load generation");
    }

    static async Task SchedulePublicationDuringCleanup()
    {
        Probe.Triggers = null;
        Probe.Schedules = [AutomationSchedule.Daily("first", new TimeOnly(12, 0), "UTC"), AutomationSchedule.Daily("second", new TimeOnly(13, 0), "UTC")];
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registered = 0;
        var removed = new ConcurrentQueue<int>();
        PyInterop.RegisterSchedule = (_, _) => {
            var id = Interlocked.Increment(ref registered);
            if (id == 2) { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("release schedule registration"); }
            return () => removed.Enqueue(id);
        };
        var manager = new UserScriptManager();
        var loading = Task.Run(() => manager.LoadUserScripts([NamedCompiled("schedule-publication")]));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var unloading = manager.UnloadUserScriptsAsync();
        try
        {
            Check(!unloading.IsCompleted && removed.IsEmpty, "schedule drain waits for in-progress registration publication");
        }
        finally { release.Set(); await Task.WhenAll(loading, unloading).WaitAsync(TimeSpan.FromSeconds(3)); Probe.Schedules = null; }
        Check(removed.Order().SequenceEqual(new[] { 1, 2 }), "every concurrently published schedule is removed exactly once");
        Console.WriteLine("PASS schedule registration and teardown publication/drain race");
    }

    [RuntimeSuite("--native-trigger", Order = 3)]
    public static async Task Run()
    {
        Probe.Mode = AutomationMode.Parallel;
        Probe.Release = new(TaskCreationOptions.RunContinuationsAsynchronously); Probe.Release.SetResult();
        Probe.Starts = System.Threading.Channels.Channel.CreateUnbounded<InvocationCause>();
        Probe.Triggers = [new HomeAssistantTrigger("event", "{\"trigger\":\"event\",\"event_type\":\"test\"}")];
        Probe.TriggerPayloads = new();
        var calls = new List<(Func<string, Task> Fire, Action<string> Success, Action<string> Error)>();
        var removed = new ConcurrentQueue<string>();
        PyInterop.BeginRegisterHaTriggers = (generation, json, fire, success, error) => calls.Add((fire, success, error));
        PyInterop.BeginRemoveHaTriggers = (lease, done, error) => { removed.Enqueue(lease); done(); };
        var manager = new UserScriptManager();
        var loading = manager.LoadUserScripts([Compiled()]);
        Check(!loading.IsCompleted && manager.GetUserScripts().Count == 0, "activation waits for registration acknowledgement");
        await calls[0].Fire("{\"trigger\":{\"id\":\"early\"}}");
        Check(Probe.TriggerPayloads.Count == 0, "staged callback ignored");
        calls[0].Success("old");
        await loading.WaitAsync(TimeSpan.FromSeconds(2));
        var old = manager.GetUserScripts().Single();
        await old.Classes.Single().StartupCompletion;
        await calls[0].Fire("{\"trigger\":{\"id\":\"event\",\"event\":{\"data\":{\"value\":7}}},\"context\":{\"id\":\"ctx\"},\"other\":[1,true]}");
        Check(Probe.TriggerPayloads.TryDequeue(out var startup) && startup.GetProperty("platform").GetString() == "startup", "startup carries its normalised payload");
        Check(Probe.TriggerPayloads.TryDequeue(out var payload) && payload.GetProperty("event").GetProperty("data").GetProperty("value").GetInt32() == 7
            && payload.GetProperty("context").GetProperty("id").GetString() == "ctx" && !payload.TryGetProperty("other", out _), "native payload is the trigger dict plus context");
        Check(Probe.Completed.Contains(InvocationCause.HomeAssistantTrigger), "native cause reaches policy runner");
        var updating = manager.UpdateUserScript(Compiled("replacement"));
        Check(!updating.IsCompleted, "replacement remains staged while attachment pending");
        calls[1].Error("attachment failed");
        try { await updating; throw new Exception("replacement should fail"); } catch (InvalidOperationException) { }
        Check(ReferenceEquals(old, manager.GetUserScripts().Single()), "failed replacement retains old runtime");
        await calls[0].Fire("{\"trigger\":{\"id\":\"still-live\"}}");
        Check(Probe.TriggerPayloads.TryDequeue(out payload) && payload.GetProperty("id").GetString() == "still-live", "old subscription survives failed replacement");
        updating = manager.UpdateUserScript(Compiled("cancelled"));
        var unloading = manager.UnloadUserScriptsAsync();
        Check(!unloading.IsCompleted, "unload drains pending registration");
        calls[2].Success("late");
        try { await updating; throw new Exception("unloaded update should fail"); } catch (OperationCanceledException) { } catch (InvalidOperationException) { }
        await unloading.WaitAsync(TimeSpan.FromSeconds(2));
        Check(removed.Contains("old") && removed.Contains("late"), "unload removes active and late leases");
        var count = Probe.TriggerPayloads.Count;
        foreach (var call in calls) await call.Fire("{\"trigger\":{}}");
        Check(Probe.TriggerPayloads.Count == count && manager.GetUserScripts().Count == 0, "stale callbacks cannot reactivate unloaded generation");
        calls.Clear();
        var batchManager = new UserScriptManager();
        var batch = batchManager.LoadUserScripts([Compiled(), Compiled("second")]);
        var batchUnload = batchManager.UnloadUserScriptsAsync();
        calls[0].Success("batch-late");
        await Task.WhenAll(batch, batchUnload).WaitAsync(TimeSpan.FromSeconds(2));
        Check(calls.Count == 1 && batchManager.GetUserScripts().Count == 0, "unload invalidates entire pending initial-load batch");

        calls.Clear();
        var deleteManager = new UserScriptManager();
        var initial = deleteManager.LoadUserScripts([Compiled()]);
        calls[0].Success("delete-old");
        await initial;
        await deleteManager.GetUserScripts().Single().Classes.Single().StartupCompletion;
        var replacementTask = deleteManager.UpdateUserScript(Compiled("delete-pending"));
        var delete = deleteManager.DeleteScriptAsync("native.cs");
        Check(!delete.IsCompleted, "delete drains concurrent staged registration");
        calls[1].Success("delete-late");
        try { await replacementTask; throw new Exception("deleted update should fail"); } catch (InvalidOperationException) { }
        await delete.WaitAsync(TimeSpan.FromSeconds(2));
        Check(deleteManager.GetUserScripts().Count == 0 && removed.Contains("delete-old") && removed.Contains("delete-late"), "delete cleans old and staged leases");
        var reload = deleteManager.LoadUserScripts([Compiled("reload")]);
        calls[2].Success("reloaded");
        await reload;
        await deleteManager.GetUserScripts().Single().Classes.Single().StartupCompletion;
        var successful = deleteManager.UpdateUserScript(Compiled("success"));
        calls[3].Success("replacement");
        await successful;
        Check(removed.Contains("reloaded"), "successful replacement awaits old cleanup");
        await deleteManager.GetUserScripts().Single().Classes.Single().StartupCompletion;
        while (Probe.TriggerPayloads.TryDequeue(out _)) { }
        foreach (var call in calls) await call.Fire("{\"trigger\":{\"id\":\"live-only\"}}");
        Check(Probe.TriggerPayloads.Count == 1, "reload and replacement retain exactly one live native generation");
        await deleteManager.UnloadUserScriptsAsync();
        Check(removed.Contains("replacement"), "native replacement lease removed on unload");

        // Completion must not run an awaiting managed continuation inline under Python's GIL.
        var continuationInline = false;
        var callbackThread = 0;
        PyInterop.BeginRegisterHaTriggers = (_, _, _, success, _) => {
            Task.Run(async () => { await Task.Delay(10); callbackThread = Environment.CurrentManagedThreadId; success("async"); callbackThread = 0; });
        };
        var leaseTask = PyInterop.RegisterHaTriggersAsync("generation", "[]", _ => Task.CompletedTask);
        await leaseTask.ContinueWith(_ => continuationInline = callbackThread == Environment.CurrentManagedThreadId, TaskContinuationOptions.ExecuteSynchronously);
        Check(!continuationInline, "registration continuations never run synchronously in Python callback");
        await (await leaseTask).DisposeAsync();
        await RunReview();
        Probe.Triggers = null;
        Console.WriteLine("PASS native trigger Task bridge, payload, staged activation, replacement rollback, late lease/unload/stale generation");
    }
}
