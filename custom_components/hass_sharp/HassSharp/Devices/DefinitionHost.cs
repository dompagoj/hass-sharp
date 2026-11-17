using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>An entity definition as its compiled script runs it: what the stored document says besides its code.</summary>
/// <param name="Id">The definition's id; each entity's unique id is <c>&lt;Id&gt;:&lt;key&gt;</c>.</param>
/// <param name="Name">What the definition and its device are called.</param>
/// <param name="Device">What the device page shows, each field in place of the class's own; null for the class's own.</param>
/// <param name="Settings">For a class, values for its <c>[Setting]</c> properties, a null value keeping the class default; for a
/// graph, its settings, which its Setting nodes read.</param>
public sealed record CompiledDefinition(string Id, string Name, DefinitionDevice? Device, IReadOnlyList<DefinitionSetting> Settings)
{
    /// <summary>Builds the definition's device or entity; null for <c>SharpDefinition.Create()</c> of the compiled assembly. A test seam.</summary>
    internal Func<object>? Factory { get; init; }
    /// <summary>For a definition built from nodes, the document's entity descriptions, which the host creates; null for a class definition.</summary>
    internal JsonArray? Entities { get; init; }
    /// <summary>For a definition built from nodes, the methods of its graph's When started, When commanded and State roots; null for a class definition.</summary>
    internal DefinitionRoots? Roots { get; init; }
}

/// <summary>
/// Runs one entity definition inside its script: builds the user's device or entity, applies the settings, sets it up,
/// tells Home Assistant which entities exist, starts and stops it, and carries its states, timers, tracks and commands.
/// A definition built from nodes has no user class: the host creates the entities its document describes, its graph's
/// runs set their states through it (<see cref="IDefinitionRuntime"/>), and it runs the graph's When started flow as it
/// prepares and a When commanded flow for each command.
/// </summary>
internal sealed partial class DefinitionHost(UserScript script, CompiledDefinition definition) : IComponentHost, IDefinitionRuntime
{
    const string SettingUp = "setup", Running = "running", Failed = "failed", Stopped = "stopped";
    static readonly IDisposable Nothing = new Handle(() => { });

    /// <summary>How long the <c>Setup()</c> of a definition's device and entities may take together; settable for tests.</summary>
    internal static TimeSpan SetupTimeout { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>How long preparing waits for Home Assistant to register the entities; settable for tests.</summary>
    internal static TimeSpan SyncTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>How long the <c>Start()</c>s may take before the host warns that the entities stay unavailable; settable for tests.</summary>
    internal static TimeSpan StartWarning { get; set; } = TimeSpan.FromSeconds(10);

    // Guards the status, the start and stop, the timer and track lists, the commands and track callbacks running, the
    // entities whose publish failed, and a graph's entities, their states, those awaiting their first and the failing States.
    readonly object gate = new();
    readonly List<Repeating> timers = [];
    readonly List<IDisposable> tracks = [];
    readonly HashSet<Task> running = [];
    readonly HashSet<SharpEntity> unpublishable = new(ReferenceEqualityComparer.Instance);
    // What the host waits on for the component, such as a service call: cancelled once stopping stops waiting.
    readonly CancellationTokenSource calls = new();
    CancellationTokenSource stopping = new();
    SharpDevice? device;
    volatile SharpEntity[] entities = [];
    volatile string status = SettingUp;
    volatile string? error;
    Task? starting;
    TaskCompletionSource? stopped;

    UserScriptManager Manager => script.ScriptManager;

    /// <summary>The definition's id.</summary>
    public string DefinitionId => definition.Id;
    /// <summary>The definition's entities as Home Assistant registered them; empty until <see cref="PrepareAsync"/> ends.</summary>
    public IReadOnlyList<LiveEntity> Entities { get; private set; } = [];
    /// <summary><c>setup</c> until started, then <c>running</c>, or <c>failed</c> when a <c>Start()</c> threw; <c>stopped</c> once stopping.</summary>
    public string Status => status;
    /// <summary>Why the start failed; null otherwise.</summary>
    public string? Error => error;
    /// <summary>What <see cref="Start"/> began: done once every <c>Start()</c> ended and the entities were published; a completed task before it.</summary>
    internal Task Started { get { lock (gate) return starting ?? Task.CompletedTask; } }
    /// <inheritdoc/>
    public CancellationToken Stopping => stopping.Token;
    /// <inheritdoc/>
    public CancellationToken Calls => calls.Token;

    /// <summary>
    /// For a class, builds the device or entity, applies the settings and runs <c>Setup()</c> on the device, then on each
    /// entity, all within <see cref="SetupTimeout"/>, then checks the entity keys; for a graph, creates the entities its
    /// document describes and runs its When started flow within <see cref="SetupTimeout"/>. Last, registers the entities
    /// with Home Assistant, waiting for it no longer than <see cref="SyncTimeout"/>. A failure leaves Home Assistant
    /// untouched. Cancelled with <paramref name="lifetime"/>.
    /// </summary>
    public async Task PrepareAsync(CancellationToken lifetime)
    {
        lifetime.ThrowIfCancellationRequested();
        stopping = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var all = definition.Entities is { } described ? await PrepareGraph(described) : await PrepareClass();
        var json = new JsonObject
        {
            ["definitionId"] = definition.Id,
            ["name"] = definition.Name,
            ["device"] = DeviceJson(),
            ["entities"] = new JsonArray(all.Select(entity => (JsonNode?)entity.Describe(UniqueId(entity))).ToArray()),
        };
        var sync = PyInterop.SyncEntitiesAsync(json.ToJsonString());
        if (!await Within(sync, SyncTimeout))
            throw new TimeoutException($"{definition.Name}: Home Assistant did not register the entities within {Seconds(SyncTimeout)} s");
        var result = JsonNode.Parse(await sync)?["entities"];
        Entities = all.Select(entity => new LiveEntity(UniqueId(entity), entity.Key, entity.Domain, entity.Name,
            result?[UniqueId(entity)]?["entityId"] is JsonValue id && id.TryGetValue<string>(out var text) ? text : null)).ToArray();
    }

    /// <summary>A class definition: builds its device or entity, applies the settings and runs every <c>Setup()</c>.</summary>
    async Task<SharpEntity[]> PrepareClass()
    {
        var root = (definition.Factory ?? CreateCompiled)();
        if (root is not (SharpDevice or SharpEntity))
            throw new InvalidOperationException($"{definition.Name}: {root?.GetType().Name ?? "null"} is neither a SharpDevice nor a SharpEntity");
        DefinitionSettings.Apply(definition.Name, definition.Settings, (SharpComponent)root);
        device = root as SharpDevice;
        SharpEntity[] Collect() => device != null ? [.. device.Entities] : [(SharpEntity)root];
        // One bound for every Setup() together: a device that does not answer must not hold up the definitions after it.
        // Each starts on a thread of its own, so one that blocks it (a raw connect, a Wait()) is bounded like one that
        // awaits and takes no thread from the pool for good; the async lambda makes a null task fail rather than look
        // cancelled. One that ignores Stopping cannot be stopped, and keeps its component (and so this host) until it
        // returns.
        var clock = Stopwatch.StartNew();
        async Task SetUp(SharpComponent component)
        {
            var left = SetupTimeout - clock.Elapsed;
            var setup = Task.Factory.StartNew(async () => await component.Setup(), CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
            if (!await Within(setup, left > TimeSpan.Zero ? left : TimeSpan.Zero))
                throw new TimeoutException($"{definition.Name}: Setup() did not finish within {Seconds(SetupTimeout)} s");
        }
        if (device != null)
        {
            device.Host = this;
            await SetUp(device);
        }
        // An entity added during a Setup() is set up too.
        var setUp = new HashSet<SharpEntity>(ReferenceEqualityComparer.Instance);
        while (Collect().FirstOrDefault(entity => !setUp.Contains(entity)) is { } next)
        {
            setUp.Add(next);
            next.Host = this;
            await SetUp(next);
        }
        // From now on the host would not see an entity added, so the device refuses one.
        if (device != null) device.Sealed = true;
        var all = Collect();
        CheckKeys(all);
        entities = all;
        return all;
    }

    /// <summary>
    /// Waits for <paramref name="task"/>, throwing its failure: true when it ended, false when <paramref name="timeout"/>
    /// passed first (its later failure is observed). Throws <see cref="OperationCanceledException"/> once stopping.
    /// </summary>
    async Task<bool> Within(Task task, TimeSpan timeout)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(Stopping);
        var first = await Task.WhenAny(task, Task.Delay(timeout, timer.Token));
        timer.Cancel();
        if (first == task)
        {
            await task;
            return true;
        }
        // Observed before the stopping check, so an abandoned task's fault is observed on both paths.
        _ = task.ContinueWith(ended => _ = ended.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        Stopping.ThrowIfCancellationRequested();
        return false;
    }

    /// <summary>
    /// Enters the directory, then, on another thread, runs <c>Start()</c> on the device and then each entity and publishes
    /// every entity: available when all started, unavailable when one threw. Never blocks and never throws.
    /// </summary>
    public void Start()
    {
        try
        {
            lock (gate)
            {
                if (stopped != null || starting != null) return;
                Manager.Entities.Register(this);
                starting = Task.Run(StartAsync);
            }
        }
        catch (Exception failure) { Report("starting failed", failure, PyLogLevel.Error); }
    }

    async Task StartAsync()
    {
        var (root, all) = (device, entities);
        // A replaced revision may have sent the same states: they are sent again, as the new entities' first.
        try { Manager.Pump.Forget(all.Select(UniqueId)); }
        catch (Exception failure) { Report("forgetting the states of the replaced revision failed", failure, PyLogLevel.Warn); }
        using var started = CancellationTokenSource.CreateLinkedTokenSource(Stopping);
        _ = WarnSlowStart(started.Token);
        try
        {
            if (root != null) await root.Start();
            foreach (var entity in all) await entity.Start();
            lock (gate) if (stopped == null) status = Running;
        }
        catch (Exception failure)
        {
            // Read before the lock: a user exception's message is user code.
            var message = Text(failure);
            lock (gate)
            {
                if (stopped != null) return;
                // The error first: whoever reads the status failed without the lock then finds it.
                error = message;
                status = Failed;
            }
            Report($"starting failed: {message}", PyLogLevel.Error);
        }
        finally { started.Cancel(); }
        // Publish never throws: an entity whose snapshot fails is reported and the others are still published. A graph's
        // entity whose State has not worked it out yet is left to that State, which publishes it once it has; one a run
        // worked out before now (a startup run of its State, or the When started flow) is published with that state here.
        foreach (var entity in all)
        {
            bool waiting;
            lock (gate) waiting = awaiting.Contains(entity.Key);
            if (!waiting) Publish(entity);
        }
    }

    // Until every Start() has returned nothing is published: a loop written in Start() rather than with Every() would
    // leave the entities unavailable for good without a word.
    async Task WarnSlowStart(CancellationToken started)
    {
        try { await Task.Delay(StartWarning, started); }
        catch (OperationCanceledException) { return; }
        Report($"Start() has not returned after {Seconds(StartWarning)} s; its entities stay unavailable until it does. Start loops with Every().", PyLogLevel.Warn);
    }

    /// <summary>
    /// Stops the definition: from now on it publishes nothing and refuses commands. Cancels <see cref="Stopping"/>, ends
    /// its timers and tracks, waits for a start still running and then for the timer callbacks, track callbacks and
    /// commands still running, then runs <c>Stop()</c> on each entity and then the device, all within
    /// <see cref="UserScript.RunDrainTimeout"/>, and leaves the directory unless replaced. What the host waits on for the
    /// component (<see cref="Calls"/>) ends when that time is up. Their failures are logged, never thrown. Pushes nothing
    /// to Home Assistant. A second call returns the first one's task.
    /// </summary>
    public Task StopAsync()
    {
        TaskCompletionSource completion;
        Task? start;
        Repeating[] ticking;
        IDisposable[] following;
        lock (gate)
        {
            if (stopped != null) return stopped.Task;
            stopped = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            status = Stopped;
            start = starting;
            ticking = [.. timers];
            following = [.. tracks];
            timers.Clear();
            tracks.Clear();
        }
        _ = StopCoreAsync(completion, start, ticking, following);
        return completion.Task;
    }

    async Task StopCoreAsync(TaskCompletionSource completion, Task? start, Repeating[] ticking, IDisposable[] following)
    {
        try
        {
            var clock = Stopwatch.StartNew();
            TimeSpan Left()
            {
                var left = UserScript.RunDrainTimeout - clock.Elapsed;
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
            // A wait the host makes for the component, such as a service call from Stop(), lasts as long as stopping waits.
            try { calls.CancelAfter(UserScript.RunDrainTimeout); }
            catch (Exception failure) { Report("bounding the component's calls failed", failure, PyLogLevel.Warn); }
            try { stopping.Cancel(); }
            catch (Exception failure) { Report("a cancellation callback failed", failure, PyLogLevel.Warn); }
            foreach (var handle in following.Concat(ticking))
                try { handle.Dispose(); }
                catch (Exception failure) { Report("ending a timer or track failed", failure, PyLogLevel.Warn); }
            if (start != null && !await UserScript.Drain([start], Left()))
            {
                Report($"Start() did not finish within {Seconds()} s of stopping; Stop() is not called", PyLogLevel.Warn);
                return;
            }
            // Stop() may dispose what a callback or a command still uses, so they end first; none starts any more.
            Task[] busy;
            lock (gate) busy = [.. running, .. ticking.Select(timer => timer.Loop)];
            if (!await UserScript.Drain(busy, Left()))
            {
                int left;
                lock (gate) left = running.Count + ticking.Count(timer => !timer.Loop.IsCompleted);
                Report($"{left} timer callback(s), track callback(s) or command(s) did not finish within {Seconds()} s of stopping; going on without them", PyLogLevel.Warn);
            }
            if (start == null) return;
            var (root, all) = (device, entities);
            var stops = Task.Run(async () =>
            {
                foreach (var entity in all) await StopOne(entity);
                if (root != null) await StopOne(root);
            });
            if (!await UserScript.Drain([stops], Left()))
                Report($"Stop() did not finish within {Seconds()} s; unloading without waiting for it", PyLogLevel.Warn);
        }
        catch (Exception failure) { Report("stopping failed", failure, PyLogLevel.Error); }
        finally
        {
            Manager.Entities.Leave(this);
            // Nothing of the user's code stays reachable from the host once stopped.
            device = null;
            entities = [];
            lock (gate)
            {
                running.Clear();
                unpublishable.Clear();
                graphEntities = new(StringComparer.Ordinal);
                awaiting.Clear();
            }
            try { calls.Cancel(); } catch { }
            ClearJsonCaches();
            completion.TrySetResult();
        }
    }

    // System.Text.Json keeps the metadata of each type it writes with JsonSerializerOptions.Default (and options equal to
    // it) for the life of the process, so a definition that writes a type of its own library that way, as
    // JsonSerializer.Serialize(value) does, would keep its load context. Once its code has stopped those caches are
    // emptied, through the ClearCache the library declares for hot reload (MetadataUpdateHandlerAttribute, called as hot
    // reload calls it, with null for every type); they fill again as they are used. Null when the library has none, which
    // is logged once; settable for tests.
    internal static Action<Type[]?>? JsonCacheHandler = FindJsonCacheHandler();
    // Whether the missing handler was logged; settable for tests.
    internal static int JsonCacheHandlerMissingLogged;

    static Action<Type[]?>? FindJsonCacheHandler()
    {
        try
        {
            return typeof(JsonSerializer).Assembly.GetCustomAttributes<System.Reflection.Metadata.MetadataUpdateHandlerAttribute>()
                .Select(attribute => attribute.HandlerType.GetMethod("ClearCache", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, [typeof(Type[])]))
                .OfType<MethodInfo>().Select(method => method.CreateDelegate<Action<Type[]?>>()).FirstOrDefault();
        }
        catch (Exception) { return null; }
    }

    static void ClearJsonCaches()
    {
        if (JsonCacheHandler is not { } handler)
        {
            if (Interlocked.Exchange(ref JsonCacheHandlerMissingLogged, 1) == 0)
                try { Logger.Warn("System.Text.Json has no cache handler for hot reload (renamed or removed in this .NET version): an entity definition that serializes a type of its own with the default options stays in memory after it stops, until Home Assistant restarts"); }
                catch { }
            return;
        }
        try { handler(null); }
        catch (Exception failure) { try { Logger.Warn($"Clearing System.Text.Json's caches failed: {Text(failure)}"); } catch { } }
    }

    async Task StopOne(SharpComponent component)
    {
        try { await component.Stop(); }
        catch (Exception failure) { Report("stopping failed", failure, PyLogLevel.Error); }
    }

    static string Seconds(TimeSpan? span = null) => (span ?? UserScript.RunDrainTimeout).TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>
    /// Runs a command Home Assistant sent to one of the definition's entities: a class's handler, or a graph's When
    /// commanded flow for it (<see cref="RunFlow"/>); stopping waits for it. What it throws is what Home Assistant shows.
    /// </summary>
    /// <exception cref="InvalidOperationException">The definition is not running, or a graph's flow failed or has none.</exception>
    /// <exception cref="NotSupportedException">The entity has no such command.</exception>
    /// <exception cref="TimeoutException">A graph's flow did not finish within <see cref="CommandTimeout"/>.</exception>
    public async Task Command(SharpEntity entity, string name, JsonElement data)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            // Under the lock that stopping takes: a command either is refused or is one stopping waits for.
            if (status != Running) throw new InvalidOperationException("The entity is not running");
            running.Add(done.Task);
        }
        try
        {
            // A command the entity does not take is refused by the entity, as a class's is.
            if (definition.Roots is { } roots && entity.Commands.Contains(name, StringComparer.Ordinal)) await RunFlow(roots, entity, name, data);
            else await entity.Command(name, data);
        }
        finally
        {
            lock (gate) running.Remove(done.Task);
            done.TrySetResult();
        }
    }

    /// <summary>The definition's entity with this unique id; null when it has none.</summary>
    internal SharpEntity? Entity(string uniqueId) => entities.FirstOrDefault(entity => UniqueId(entity) == uniqueId);

    /// <summary>
    /// Sends the entity's snapshot towards Home Assistant. Never throws: a snapshot that fails, such as an attribute
    /// that changes while it is read, is logged once for the entity and nothing is sent for it.
    /// </summary>
    public void Publish(SharpEntity entity)
    {
        Exception? failed = null;
        lock (gate)
        {
            // Only once started, and never after stopping: a replaced revision must not show its entities unavailable.
            if (status is not (Running or Failed)) return;
            var id = UniqueId(entity);
            try { Manager.Pump.Post(id, entity.Snapshot(id, running: status == Running)); }
            catch (Exception failure) { if (unpublishable.Add(entity)) failed = failure; }
        }
        // Outside the lock: logging calls into Python.
        if (failed != null) Report($"publishing the state of entity '{entity.Key}' failed", failed, PyLogLevel.Error);
    }

    /// <inheritdoc/>
    public IDisposable Every(TimeSpan interval, Func<Task> action, bool immediately)
    {
        ArgumentNullException.ThrowIfNull(action);
        var timer = new PeriodicTimer(interval);
        var token = Stopping;
        lock (gate)
        {
            if (stopped != null) { timer.Dispose(); return Nothing; }
            var repeating = new Repeating(timer, this);
            timers.Add(repeating);
            repeating.Loop = Task.Run(async () =>
            {
                try
                {
                    // One call at a time: a tick that comes while the action runs is taken after it.
                    if (immediately) await Callback("a timer callback", action, token);
                    while (await timer.WaitForNextTickAsync(token)) await Callback("a timer callback", action, token);
                }
                catch (OperationCanceledException) { }
            });
            return repeating;
        }
    }

    /// <inheritdoc/>
    public IDisposable Track(string entityId, Func<EntityChange, Task> action)
    {
        ArgumentNullException.ThrowIfNull(entityId);
        ArgumentNullException.ThrowIfNull(action);
        var token = Stopping;
        Task Changed(TriggerContext trigger)
        {
            if (token.IsCancellationRequested) return Task.CompletedTask;
            EntityChange change;
            // On the caller's thread, as the automations' payload is: detaching attributes may take the GIL.
            try { change = new(entityId, EntityStateSnapshot.From(trigger.State), trigger.State.OldState is { } old ? EntityStateSnapshot.From(old) : null); }
            catch (Exception failure) { CallbackFailed("an entity callback", failure, token); return Task.CompletedTask; }
            // Stopping waits for the callback: under the lock it takes, the callback either does not start or is counted.
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                if (stopped != null) return Task.CompletedTask;
                running.Add(done.Task);
            }
            return Task.Run(async () =>
            {
                try { await Callback("an entity callback", () => action(change), token); }
                finally
                {
                    lock (gate) running.Remove(done.Task);
                    done.TrySetResult();
                }
            });
        }
        lock (gate) if (stopped != null) return Nothing;
        // Subscribing calls into Python, which waits for the GIL: never under the lock, which a publish from a thread
        // holding the GIL takes. A stop in between ends the track here, as it would have in the stop.
        var registration = Manager.DependencyTracking.TrackDelegate(entityId, script, Changed);
        IDisposable? handle = null;
        handle = new Handle(() => { registration.Dispose(); lock (gate) tracks.Remove(handle!); });
        lock (gate)
            if (stopped == null)
            {
                tracks.Add(handle);
                return handle;
            }
        handle.Dispose();
        return Nothing;
    }

    /// <summary>Writes to the Home Assistant log on behalf of the definition, after its name, as the host's own lines are.</summary>
    public void Log(string message, PyLogLevel level) => PyInterop.Log((int)level, $"{definition.Name}: {message}");

    // The host's own reports of failures: a log that fails must not stop a timer or skip a publish.
    void Report(string message, PyLogLevel level)
    {
        try { Log(message, level); }
        catch { }
    }

    // A failure, with the message of the exception, which never throws from here.
    void Report(string what, Exception failure, PyLogLevel level) => Report($"{what}: {Text(failure)}", level);

    // An exception's message: user code, which may itself throw.
    static string Text(Exception failure)
    {
        try { return failure.Message; }
        catch { return failure.GetType().Name; }
    }

    async Task Callback(string what, Func<Task> action, CancellationToken token)
    {
        try { await action(); }
        catch (Exception failure) { CallbackFailed(what, failure, token); }
    }

    // A cancellation because the definition stops is no failure.
    void CallbackFailed(string what, Exception failure, CancellationToken token)
    {
        if (failure is OperationCanceledException && token.IsCancellationRequested) return;
        Report($"{what} failed", failure, PyLogLevel.Error);
    }

    string UniqueId(SharpEntity entity) => $"{definition.Id}:{entity.Key}";

    object CreateCompiled()
    {
        var create = script.CompiledScript.Assembly.GetType("SharpDefinition")?.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes)
            ?? throw new InvalidOperationException($"{definition.Name}: the compiled definition has no SharpDefinition.Create()");
        return create.Invoke(null, BindingFlags.DoNotWrapExceptions, null, null, null)
            ?? throw new InvalidOperationException($"{definition.Name}: SharpDefinition.Create() returned null");
    }

    void CheckKeys(SharpEntity[] all)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in all)
        {
            if (entity.Key == null || !SharpEntity.KeyPattern().IsMatch(entity.Key))
                throw new InvalidOperationException($"{definition.Name}: entity key '{entity.Key}' must be a lower-case letter followed by up to 39 lower-case letters, digits or underscores");
            if (!seen.Add(entity.Key)) throw new InvalidOperationException($"{definition.Name}: two entities have the key '{entity.Key}'");
        }
    }

    // A device's own metadata, each field replaced by the document's when it gives one; an entity has only the document's.
    JsonObject? DeviceJson()
    {
        var given = definition.Device;
        if (device == null && given == null) return null;
        return new()
        {
            ["name"] = definition.Name,
            ["manufacturer"] = given?.Manufacturer ?? device?.Manufacturer,
            ["model"] = given?.Model ?? device?.Model,
            ["swVersion"] = given?.SwVersion ?? device?.SwVersion,
            ["hwVersion"] = given?.HwVersion ?? device?.HwVersion,
            ["configurationUrl"] = given?.ConfigurationUrl ?? device?.ConfigurationUrl,
            ["suggestedArea"] = given?.SuggestedArea ?? device?.SuggestedArea,
            ["via"] = given?.Via switch
            {
                { Definition: { } id } => new JsonObject { ["definition"] = id },
                { DeviceId: { } id } => new JsonObject { ["deviceId"] = id },
                _ => null,
            },
        };
    }

    sealed class Handle(Action dispose) : IDisposable
    {
        int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) dispose(); }
    }

    // A timer: disposing it ends its loop after the call in progress, if any.
    sealed class Repeating(PeriodicTimer timer, DefinitionHost host) : IDisposable
    {
        public Task Loop { get; set; } = Task.CompletedTask;
        public void Dispose()
        {
            timer.Dispose();
            lock (host.gate) host.timers.Remove(this);
        }
    }
}
