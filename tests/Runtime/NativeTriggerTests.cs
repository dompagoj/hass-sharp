using HassSharp;
using System.Collections.Concurrent;
using System.Text.Json;

static class NativeTriggerTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static CompiledUserScript Compiled(string source = "test") => new() { FilePath = "native.cs", FileName = "native", SourceCode = source, Assembly = typeof(Probe).Assembly, WrittenToDisk = true };
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
        await calls[0].Fire("{\"trigger\":{\"id\":\"event\",\"event\":{\"data\":{\"value\":7}}},\"other\":[1,true]}");
        Check(Probe.TriggerPayloads.TryDequeue(out var startup) && startup.ValueKind == JsonValueKind.Undefined, "startup has no native data");
        Check(Probe.TriggerPayloads.TryDequeue(out var payload) && payload.GetProperty("other")[1].GetBoolean(), "full immutable native payload survives dispatch");
        Check(Probe.Completed.Contains(InvocationCause.HomeAssistantTrigger), "native cause reaches policy runner");
        var updating = manager.UpdateUserScript(Compiled("replacement"));
        Check(!updating.IsCompleted, "replacement remains staged while attachment pending");
        calls[1].Error("attachment failed");
        try { await updating; throw new Exception("replacement should fail"); } catch (InvalidOperationException) { }
        Check(ReferenceEquals(old, manager.GetUserScripts().Single()), "failed replacement retains old runtime");
        await calls[0].Fire("{\"trigger\":{\"id\":\"still-live\"}}");
        Check(Probe.TriggerPayloads.TryDequeue(out payload) && payload.GetProperty("trigger").GetProperty("id").GetString() == "still-live", "old subscription survives failed replacement");
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

        var folder = Path.Combine(Path.GetTempPath(), "hass-sharp-native-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "atomic.cs");
            File.WriteAllText(path, "old-source");
            var source = new string('x', 8 * 1024 * 1024);
            var atomic = new CompiledUserScript { FilePath = path, FileName = "atomic", SourceCode = source, Assembly = typeof(Probe).Assembly, WrittenToDisk = false };
            var writing = atomic.WriteToDisk();
            var sawPartial = false;
            while (!writing.IsCompleted)
            {
                var length = new FileInfo(path).Length;
                sawPartial |= length != 10 && length != source.Length;
                await Task.Yield();
            }
            await writing;
            Check(!sawPartial && File.ReadAllText(path) == source, "replacement source writes never expose truncated files");
        }
        finally { Directory.Delete(folder, true); }

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
        Probe.Triggers = null;
        Console.WriteLine("PASS native trigger Task bridge, payload, staged activation, replacement rollback, late lease/unload/stale generation");
    }
}
