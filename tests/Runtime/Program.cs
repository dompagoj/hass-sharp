using HassSharp;
using System.Collections.Concurrent;
using System.Reflection;

if (args.Contains("--write-node-library-fixture")) { File.WriteAllText("tests/fixtures/visual-rich-catalog.json", NodeLibraryFixture.Build().ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n"); return 0; }
if (args.Contains("--write-services-fixture")) { File.WriteAllText("tests/fixtures/visual-services.json", ServiceSchemaTests.Golden(File.ReadAllText("tests/fixtures/ha-service-descriptions.json"))); return 0; }

var suites = typeof(RuntimeSuiteAttribute).Assembly.GetTypes()
    .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
    .Where(method => method.IsDefined(typeof(RuntimeSuiteAttribute)))
    .Select(method => (Method: method, Suite: method.GetCustomAttribute<RuntimeSuiteAttribute>()!))
    .OrderBy(entry => entry.Suite.Order).ThenBy(entry => entry.Suite.Flag, StringComparer.Ordinal)
    .ToList();
if (suites.GroupBy(entry => entry.Suite.Flag).FirstOrDefault(group => group.Count() > 1) is { } duplicate) throw new Exception($"Duplicate runtime suite flag {duplicate.Key}");
if (args.Where(arg => !suites.Any(entry => Matches(arg, entry.Suite.Flag))).ToList() is { Count: > 0 } unknown)
{
    Console.Error.WriteLine($"Unknown runtime suite flag: {string.Join(", ", unknown)}. Known flags: {string.Join(", ", suites.Select(entry => entry.Suite.Flag).Order(StringComparer.Ordinal))}, --write-node-library-fixture, --write-services-fixture");
    return 1;
}

PyInterop.Log = (_, _) => {};
PyInterop.SubscribeToEntityTracking = id => { if (!RuntimeCoreTests.Subscriptions.Add(id)) throw new Exception("duplicate subscription"); };
PyInterop.UnSubscribeFromEntityTracking = id => RuntimeCoreTests.Subscriptions.Remove(id);
PyInterop.UnSubscribeAllFromEntityTracking = () => RuntimeCoreTests.Subscriptions.Clear();
PyInterop.Entity = id => new() { EntityId = id, State = "fresh" };
foreach (var (method, suite) in suites)
{
    var flag = args.FirstOrDefault(arg => Matches(arg, suite.Flag));
    if (args.Length == 0 ? !suite.RunByDefault : flag == null) continue;
    var parameters = method.GetParameters().Length == 0 ? null : new object?[] { flag?.Split('=').ElementAtOrDefault(1) };
    if (method.Invoke(null, BindingFlags.DoNotWrapExceptions, null, parameters, null) is Task task) await task;
}
return 0;

static bool Matches(string arg, string flag) => arg == flag || arg.StartsWith(flag + "=", StringComparison.Ordinal);

static class RuntimeCoreTests
{
    internal static readonly HashSet<string> Subscriptions = new();
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    [RuntimeSuite("--runtime-core", Order = 0)]
    public static async Task Run()
    {
        foreach (var mode in Enum.GetValues<AutomationMode>())
        {
            Probe.Mode = mode;
            Probe.Starts = System.Threading.Channels.Channel.CreateUnbounded<InvocationCause>(); Probe.Completed = new(); Probe.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var manager = new UserScriptManager();
            var compiled = new CompiledUserScript { FilePath = "test.cs", FileName = "test", SourceCode = "", Assembly = typeof(Probe).Assembly };
            await manager.LoadUserScripts([compiled]).WaitAsync(TimeSpan.FromSeconds(2));
            var klass = manager.GetUserScripts().Single().Classes.Single();
            var startup = await Probe.Starts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Check(startup == InvocationCause.Startup, "startup cause");
            Check(manager.DependencyTracking.GetEntries("sensor.test").Count == 1, "duplicate declaration deduplicated");
            var entity = manager.RunEntries("sensor.test", new(new() { EntityId = "sensor.test" }));
            if (mode is AutomationMode.Restart or AutomationMode.Parallel)
                Check(await Probe.Starts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)) == InvocationCause.Entity, "entity during startup");
            if (mode == AutomationMode.Single) await entity.WaitAsync(TimeSpan.FromSeconds(2));
            Probe.Release.SetResult();
            await entity.WaitAsync(TimeSpan.FromSeconds(2));
            if (mode == AutomationMode.Queued)
                Check(await Probe.Starts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)) == InvocationCause.Entity, "queued trigger");
            await klass.StartupCompletion;
            Check(Probe.Completed.Contains(InvocationCause.Startup) == (mode != AutomationMode.Restart), "startup completion obeys mode");
            Check(Probe.Completed.Contains(InvocationCause.Entity) == (mode != AutomationMode.Single), "entity completion obeys mode");
            await klass.RunMethod(nameof(Probe.Run));
            Check(Probe.Completed.Contains(InvocationCause.Manual), "manual cause");
            if (mode == AutomationMode.Restart) Check(!Probe.Completed.Contains(InvocationCause.Startup), "restart cancels startup delay");
            var completedBeforeUnload = Probe.Completed.Count;
            manager.UnloadUserScripts();
            await klass.RunMethod(nameof(Probe.Run)); // stale callback must be harmless
            Check(Probe.Completed.Count == completedBeforeUnload, "stale callback cannot execute");
            Check(Subscriptions.Count == 0, "unsubscribed");
            await manager.RunEntries("sensor.test", new(new() { EntityId = "sensor.test" }));
            await manager.LoadUserScripts([compiled]);
            Check(Subscriptions.Count == 1, "reload resubscribed");
            var reloadedClass = manager.GetUserScripts().Single().Classes.Single();
            manager.UnloadUserScripts();
            await reloadedClass.StartupCompletion;
            Console.WriteLine($"PASS {mode}: startup/entity/manual, delay cancellation, duplicates, reload/stale callbacks");
        }
        Probe.Mode = AutomationMode.Queued;
        Probe.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Probe.Starts = System.Threading.Channels.Channel.CreateUnbounded<InvocationCause>();
        Probe.Order = new();
        var queueManager = new UserScriptManager();
        await queueManager.LoadUserScripts([new() { FilePath = "queue.cs", FileName = "queue", SourceCode = "", Assembly = typeof(Probe).Assembly }]);
        await Probe.Starts.Reader.ReadAsync();
        var pending = Enumerable.Range(0, 100).Select(i => queueManager.RunEntries("sensor.test", new(new() { EntityId = "sensor.test", State = i.ToString() }))).ToArray();
        Probe.Release.SetResult();
        await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
        Check(Probe.Order.SequenceEqual(Enumerable.Range(0, 100)), "Queued must execute in trigger arrival order");
        queueManager.UnloadUserScripts();
        Console.WriteLine("PASS queue arrival order (100 triggers)");
        Probe.Mode = AutomationMode.Single;
        Probe.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Probe.Starts = System.Threading.Channels.Channel.CreateUnbounded<InvocationCause>();
        var isolated = new UserScriptManager();
        foreach (var id in new[] { "first", "second" })
        {
            await isolated.LoadUserScripts([new() { FilePath = id, FileName = id, SourceCode = "", Assembly = typeof(Probe).Assembly }]);
            await Probe.Starts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
        var startupTasks = isolated.GetUserScripts().SelectMany(s => s.Classes).Select(c => c.StartupCompletion).ToArray();
        var beforeCancel = Probe.Completed.Count;
        isolated.UnloadUserScripts();
        await Task.WhenAll(startupTasks);
        Check(Probe.Completed.Count == beforeCancel, "unload cancels pending delay before effects");
        Console.WriteLine("PASS instance policy isolation");
    }
}


public class Probe : Automation
{
    public static Action? Configuring;
    public static TaskCompletionSource? DelayedExit;
    public static ConcurrentQueue<int> Order = new();
    public static AutomationMode Mode;
    public static AutomationSchedule[]? Schedules;
    public static HomeAssistantTrigger[]? Triggers;
    public static ConcurrentQueue<System.Text.Json.JsonElement> TriggerPayloads = new();
    public static System.Threading.Channels.Channel<InvocationCause> Starts = System.Threading.Channels.Channel.CreateUnbounded<InvocationCause>();
    public static ConcurrentBag<InvocationCause> Completed = new();
    public static TaskCompletionSource Release = new();
    public override AutomationConfiguration Configure() { Configuring?.Invoke(); return new(new AutomationAction(nameof(Run), Mode, true, true, [new EntityRefWrapper<HaSensor>("sensor.test"), new EntityRefWrapper<HaSensor>("sensor.test")], Schedules, Triggers)); }
    public async Task Run()
    {
        var cause = Cause;
        if (Triggers != null) TriggerPayloads.Enqueue(TriggerData);
        Starts.Writer.TryWrite(cause);
        if (DelayedExit is { } exit) await exit.Task;
        await Release.Task.WaitAsync(CancellationToken);
        await Task.Delay(10, CancellationToken);
        if (Entity("sensor.test").Refresh().RawState != "fresh") throw new Exception("refresh failed");
        if (int.TryParse(TriggeredBy()?.RawState, out var number)) Order.Enqueue(number);
        Completed.Add(cause);
    }
}
