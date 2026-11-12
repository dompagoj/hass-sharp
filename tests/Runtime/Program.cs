using HassSharp;
using System.Collections.Concurrent;

PyInterop.Log = (_, _) => {};
var subscriptions = new HashSet<string>();
PyInterop.SubscribeToEntityTracking = id => { if (!subscriptions.Add(id)) throw new Exception("duplicate subscription"); };
PyInterop.UnSubscribeFromEntityTracking = id => subscriptions.Remove(id);
PyInterop.UnSubscribeAllFromEntityTracking = () => subscriptions.Clear();
PyInterop.Entity = id => new() { EntityId = id, State = "fresh" };
foreach (var mode in Enum.GetValues<AutomationMode>())
{
    Probe.Mode = mode;
    Probe.Starts = System.Threading.Channels.Channel.CreateUnbounded<InvocationCause>(); Probe.Completed = new(); Probe.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var manager = new UserScriptManager();
    var compiled = new CompiledUserScript { FilePath = "test.cs", FileName = "test", SourceCode = "", Assembly = typeof(Probe).Assembly, WrittenToDisk = true };
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
    Check(subscriptions.Count == 0, "unsubscribed");
    await manager.RunEntries("sensor.test", new(new() { EntityId = "sensor.test" }));
    await manager.LoadUserScripts([compiled]);
    Check(subscriptions.Count == 1, "reload resubscribed");
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
await queueManager.LoadUserScripts([new() { FilePath = "queue.cs", FileName = "queue", SourceCode = "", Assembly = typeof(Probe).Assembly, WrittenToDisk = true }]);
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
    await isolated.LoadUserScripts([new() { FilePath = id, FileName = id, SourceCode = "", Assembly = typeof(Probe).Assembly, WrittenToDisk = true }]);
    await Probe.Starts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
}
var startupTasks = isolated.GetUserScripts().SelectMany(s => s.Classes).Select(c => c.StartupCompletion).ToArray();
var beforeCancel = Probe.Completed.Count;
isolated.UnloadUserScripts();
await Task.WhenAll(startupTasks);
Check(Probe.Completed.Count == beforeCancel, "unload cancels pending delay before effects");
Console.WriteLine("PASS instance policy isolation");
var assembly = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new("MotionFixture"), System.Reflection.Emit.AssemblyBuilderAccess.Run);
var motionType = assembly.DefineDynamicModule("main").DefineType("MotionFixture", System.Reflection.TypeAttributes.Public, typeof(HassSharp.HelperBaseAutomations.MotionLightAutomation)).CreateType()!;
var motion = (HassSharp.HelperBaseAutomations.MotionLightAutomation)Activator.CreateInstance(motionType)!;
motion.MotionSensor = new HaBinarySensor("binary_sensor.motion");
motion.Light = new HaLight("light.test");
motion.TurnOffDelay = TimeSpan.FromMilliseconds(1);
PyInterop.Entity = id => new() { EntityId = id, State = "off" };
var turnedOff = false;
PyInterop.CallService = (_, service, _, _, _) => turnedOff |= service == "turn_off";
await motion.Run();
Check(turnedOff, "motion-off trigger must finish turn-off after restart");
Console.WriteLine("PASS motion-off delay");
await HistoryTests.Run();
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
public class Probe : Automation
{
    public static ConcurrentQueue<int> Order = new();
    public static AutomationMode Mode;
    public static System.Threading.Channels.Channel<InvocationCause> Starts = System.Threading.Channels.Channel.CreateUnbounded<InvocationCause>();
    public static ConcurrentBag<InvocationCause> Completed = new();
    public static TaskCompletionSource Release = new();
    public override AutomationConfiguration Configure() => new(new AutomationAction(nameof(Run), Mode, true, true, ["sensor.test", "sensor.test"]));
    public async Task Run()
    {
        var cause = Cause;
        Starts.Writer.TryWrite(cause);
        await Release.Task.WaitAsync(CancellationToken);
        await Task.Delay(10, CancellationToken);
        if (Entity("sensor.test").Refresh().RawState != "fresh") throw new Exception("refresh failed");
        if (int.TryParse(TriggeredBy()?.RawState, out var number)) Order.Enqueue(number);
        Completed.Add(cause);
    }
}
