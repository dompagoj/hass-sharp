using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>An entity definition as its compiled script runs it: what the stored document says besides its code.</summary>
/// <param name="Id">The definition's id; each entity's unique id is <c>&lt;Id&gt;:&lt;key&gt;</c>.</param>
/// <param name="Name">What the definition is called.</param>
/// <param name="Settings">Its settings, which its Setting nodes read.</param>
public sealed record CompiledDefinition(string Id, string Name, IReadOnlyList<DefinitionSetting> Settings)
{
    /// <summary>
    /// The document's entity descriptions, which the host creates, each with the key of the device it is on (<c>device</c>,
    /// null for none).
    /// </summary>
    internal JsonArray Entities { get; init; } = [];
    /// <summary>
    /// The devices its Device nodes declare, each with its <see cref="DefinitionDevice.Key"/> and
    /// <see cref="DefinitionDevice.Name"/>, in node order (<see cref="DefinitionGraph.Devices"/>).
    /// </summary>
    internal IReadOnlyList<DefinitionDevice> Devices { get; init; } = [];
    /// <summary>
    /// The methods of its graph's When started step and of its Entity nodes' flows, which a compiled definition always has;
    /// null, as a host test may leave it, for none: no flow then runs.
    /// </summary>
    internal DefinitionRoots? Roots { get; init; }
    /// <summary>
    /// Its C# entity and C# device nodes, in node order, each with its settings resolved
    /// (<see cref="DefinitionGraph.Components"/>), which the host sets up beside its Entity nodes; none for a graph without
    /// such nodes.
    /// </summary>
    internal IReadOnlyList<DefinitionComponent> Components { get; init; } = [];
    /// <summary>
    /// For a definition with <see cref="Components"/>, what makes the class of the C# node with that id: the compiled
    /// graph's <c>SharpNodes.Create</c>, null for a node it has no class for; null for a definition without such nodes.
    /// The host calls it for each component as it sets the graph up.
    /// </summary>
    internal Func<string, object?>? Create { get; init; }
}

/// <summary>
/// Runs one entity definition inside its script: creates the entities its Entity nodes describe, which its graph's runs
/// set the states of through it (<see cref="IDefinitionRuntime"/>), and runs the graph's When started flow as it prepares
/// and an Entity node's command flow for each command; beside them it creates each C# entity and C# device node's class,
/// applies its settings and sets it up. It tells Home Assistant which entities exist, starts and stops them, and carries
/// their states, timers, tracks and commands. It owns the definition's HTTP client and downloads, which it ends
/// once the definition has stopped. It answers Home Assistant's requests for its cameras' pictures, making their frames
/// (<see cref="CameraImage"/>).
/// </summary>
internal sealed partial class DefinitionHost(UserScript script, CompiledDefinition definition) : IComponentHost, IDefinitionRuntime
{
    const string SettingUp = "setup", Running = "running", Failed = "failed", Stopped = "stopped";
    static readonly IDisposable Nothing = new Handle(() => { });

    /// <summary>
    /// How long the constructors and <c>Setup()</c>s of a definition's C# nodes may take together (the <c>Setup()</c> of
    /// each entity a C# device adds among them), and, on a clock of its own, its When started flow; settable for tests.
    /// </summary>
    internal static TimeSpan SetupTimeout { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>How long preparing waits for Home Assistant to register the entities; settable for tests.</summary>
    internal static TimeSpan SyncTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>How long the <c>Start()</c>s may take before the host warns that the entities stay unavailable; settable for tests.</summary>
    internal static TimeSpan StartWarning { get; set; } = TimeSpan.FromSeconds(10);

    // Guards the status, the start and stop, the timer and track lists, the commands, track callbacks and frames running,
    // the entities whose publish failed, the HTTP client and the downloads, and a graph's entities, their states, those
    // awaiting their first, the failing state flows and the frame flows. Never taken while a camera's own lock is wanted:
    // CameraFrames calls in here under its lock (Make), not the other way round.
    readonly Lock gate = new();
    readonly List<Repeating> timers = [];
    readonly List<IDisposable> tracks = [];
    readonly HashSet<Task> running = [];
    readonly HashSet<SharpEntity> unpublishable = new(ReferenceEqualityComparer.Instance);
    // What the host waits on for the component, such as a service call: cancelled once stopping stops waiting.
    readonly CancellationTokenSource calls = new();
    CancellationTokenSource stopping = new();
    volatile SharpEntity[] entities = [];
    // What starts and stops, in order: each device, if any, with its entities: the entities its Entity nodes create first
    // (with no device), then each C# node's class (a C# device with the entities it added).
    volatile (SharpDevice? Device, SharpEntity[] Entities)[] lifecycle = [];
    volatile string status = SettingUp;
    volatile string? error;
    Task? starting;
    TaskCompletionSource? stopped;
    // The definition's HTTP client and downloads, made on first use; once released (after the last Stop()) none is made again.
    HttpClient? http;
    SharpFiles? files;
    bool released;
    // Stopped because a new revision replaces this one: its cameras still answer their good frame until it has left.
    volatile bool replacing;
    /// <summary>Test seam: awaited by a request for a camera's picture once it has found the definition running, before it shares a frame.</summary>
    internal Func<Task>? FrameChecked;
    /// <summary>Test seam: runs as the definition stops, once its status says so and before its stop cancels what it runs.</summary>
    internal Action? StopMarked;
    // The clock of the Setup()s or the When started flow while the definition prepares, which a download's receiving stops; null after.
    volatile SetupClock? setupClock;
    // Why the When started flow last failed, as FlowFailed words it (the step's sentence, with its cause), for its start's error.
    volatile string? setupFailure;

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

    /// <inheritdoc/>
    public HttpClient Http
    {
        get
        {
            lock (gate)
            {
                if (released) throw new ObjectDisposedException(null, $"{definition.Name} has stopped: its HTTP client is disposed");
                return http ??= SharpHttp.Create();
            }
        }
    }

    /// <inheritdoc/>
    public SharpFiles Files
    {
        get
        {
            lock (gate)
            {
                if (released) throw new ObjectDisposedException(null, $"{definition.Name} has stopped: it downloads nothing more");
                return files ??= new SharpFiles(Manager.FilesRoot ?? throw new InvalidOperationException("Downloads have no directory here"),
                    line => Report(line, PyLogLevel.Info), () => setupClock?.Receiving() ?? Nothing);
            }
        }
    }

    /// <summary>
    /// Creates the entities its Entity nodes describe, creates each C# entity and C# device node's class, applies its
    /// settings and runs its <c>Setup()</c> (every C# node's creation and <c>Setup()</c> within one
    /// <see cref="SetupTimeout"/>), checks the entity
    /// keys, and runs its When started flow within a <see cref="SetupTimeout"/> of its own. Last, registers the entities
    /// with Home Assistant, waiting for it no longer than <see cref="SyncTimeout"/>. A failure leaves Home Assistant
    /// untouched. Cancelled with <paramref name="lifetime"/>.
    /// </summary>
    public async Task PrepareAsync(CancellationToken lifetime)
    {
        lifetime.ThrowIfCancellationRequested();
        stopping = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var all = await PrepareGraph();
        var devices = Devices();
        var keys = devices.Select(device => device.Key!).ToHashSet(StringComparer.Ordinal);
        var json = new JsonObject
        {
            ["definitionId"] = definition.Id,
            ["name"] = definition.Name,
            ["devices"] = new JsonArray(devices.Select(device => (JsonNode?)DeviceJson(device, keys)).ToArray()),
            ["entities"] = new JsonArray(all.Select(entity =>
            {
                var description = entity.Describe(UniqueId(entity));
                description["device"] = DeviceOf(entity, keys);
                return (JsonNode?)description;
            }).ToArray()),
        };
        var sync = PyInterop.SyncEntitiesAsync(json.ToJsonString());
        if (!await Within(sync, SyncTimeout))
            throw new TimeoutException($"{definition.Name}: Home Assistant did not register the entities within {Seconds(SyncTimeout)} s");
        var result = JsonNode.Parse(await sync)?["entities"];
        Entities = all.Select(entity => new LiveEntity(UniqueId(entity), entity.Key, entity.Domain, entity.Name,
            result?[UniqueId(entity)]?["entityId"] is JsonValue id && id.TryGetValue<string>(out var text) ? text : null)).ToArray();
    }

    /// <summary>
    /// The steps for one C# node's class: applies its settings, then <paramref name="adopt"/>s its entity (a C# entity,
    /// whose node gives its key and name);
    /// restores the state each entity last showed, before any <c>Setup()</c> that may see it; runs <c>Setup()</c> on the
    /// device, then on each entity, one added in a <c>Setup()</c> too, all within <see cref="SetupTimeout"/> on the clock
    /// the definition's set-up shares (the first makes it; the caller ends it); then seals a device. A <c>[Setting]</c>
    /// setter or a <c>Setup()</c> that throws fails after <paramref name="who"/>, as every failure of it does; so does a
    /// required setting that has no value (<paramref name="unset"/>), before any setting is applied.
    /// </summary>
    /// <param name="who">What its failures start with: the definition's name and the node's.</param>
    /// <param name="unset">The labels of its required settings that have no value (<see cref="DefinitionComponent.Unset"/>).</param>
    /// <returns>Its entities: the entity, or the device's in the order it added them.</returns>
    async Task<SharpEntity[]> SetUpComponent(SharpComponent root, string who, IReadOnlyList<DefinitionSetting> settings, IReadOnlyList<string> unset, Action<SharpEntity>? adopt = null)
    {
        try { DefinitionSettings.Apply(who, settings, unset, root); }
        // A setter that throws (user code: its message, read once here, never with the value) after the node's name;
        // Apply's own sentences already start with it.
        catch (Exception failure) when (Text(failure) is var message && !(failure is InvalidOperationException && message.StartsWith(who + ": ", StringComparison.Ordinal)))
        { throw new InvalidOperationException($"{who}: {message}", failure); }
        var owner = root as SharpDevice;
        if (root is SharpEntity alone) adopt?.Invoke(alone);
        SharpEntity[] Collect() => owner != null ? [.. owner.Entities] : [(SharpEntity)root];
        // Each entity takes the state it last showed before any Setup() that may see it: those of the constructor now,
        // before the device's; one added in a Setup() as it is added, so that whatever that Setup() then sets wins.
        foreach (var entity in Collect()) RestoreComponent(entity);
        if (owner != null) owner.Added = RestoreComponent;
        // One bound for every Setup() together: a device that does not answer must not hold up the definitions after it.
        // Each starts on a thread of its own, so one that blocks it (a raw connect, a Wait()) is bounded like one that
        // awaits and takes no thread from the pool for good; the async lambda makes a null task fail rather than look
        // cancelled. One that ignores Stopping cannot be stopped, and keeps its component (and so this host) until it
        // returns. The time a download receives is not counted (see SetupClock).
        var clock = setupClock ??= new SetupClock();
        async Task SetUp(SharpComponent component)
        {
            var setup = Task.Factory.StartNew(async () => await component.Setup(), CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
            bool ended;
            try { ended = await Within(setup, clock); }
            // Its message is user code, read once here. A cancellation is the host's stop only while the host is stopping;
            // one of its own (an HttpClient time-out) is a failure like any other.
            catch (Exception failure) when (!(failure is OperationCanceledException && Stopping.IsCancellationRequested))
            { throw new InvalidOperationException($"{who}: {Text(failure)}", failure); }
            if (!ended)
                throw new TimeoutException($"{who}: Setup() did not finish within {Seconds(SetupTimeout)} s");
        }
        if (owner != null)
        {
            owner.Host = this;
            await SetUp(owner);
        }
        // An entity added during a Setup() is set up too.
        var setUp = new HashSet<SharpEntity>(ReferenceEqualityComparer.Instance);
        while (Collect().FirstOrDefault(entity => !setUp.Contains(entity)) is { } next)
        {
            setUp.Add(next);
            next.Host = this;
            await SetUp(next);
        }
        // From now on the host would not see an entity added, so the device refuses one (and has none to restore).
        if (owner != null)
        {
            owner.Sealed = true;
            owner.Added = null;
        }
        return Collect();
    }

    /// <summary>
    /// Waits for <paramref name="task"/> as <see cref="Within(Task, TimeSpan)"/> does, until <see cref="SetupTimeout"/> has
    /// passed on <paramref name="clock"/>, which stands while a download receives.
    /// </summary>
    async Task<bool> Within(Task task, SetupClock clock)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(Stopping);
        while (!task.IsCompleted && !Stopping.IsCancellationRequested)
        {
            var (left, changed) = clock.Left(SetupTimeout);
            if (left <= TimeSpan.Zero) break;
            // While a download receives the clock stands, until it ends or another begins.
            await Task.WhenAny(task, changed, Task.Delay(left ?? Timeout.InfiniteTimeSpan, timer.Token));
        }
        timer.Cancel();
        if (task.IsCompleted)
        {
            await task;
            return true;
        }
        _ = task.ContinueWith(ended => _ = ended.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        Stopping.ThrowIfCancellationRequested();
        return false;
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
        var (cycle, all) = (lifecycle, entities);
        // The revision this one replaced has stopped by now.
        ReleasePredecessor();
        // A replaced revision may have sent the same states: they are sent again, as the new entities' first.
        try { Manager.Pump.Forget(all.Select(UniqueId)); }
        catch (Exception failure) { Report("forgetting the states of the replaced revision failed", failure, PyLogLevel.Warn); }
        // The states remembered for an entity this revision no longer has, or no longer brings back, are dropped.
        try
        {
            if (Manager.States is { } states)
            {
                var kept = all.Where(Restorable).Select(UniqueId).ToHashSet(StringComparer.Ordinal);
                states.Forget(states.Ids().Where(id => id.StartsWith(definition.Id + ":", StringComparison.Ordinal) && !kept.Contains(id)));
            }
        }
        catch (Exception failure) { Report("forgetting the states of entities the definition no longer has failed", failure, PyLogLevel.Warn); }
        using var started = CancellationTokenSource.CreateLinkedTokenSource(Stopping);
        _ = WarnSlowStart(started.Token);
        try
        {
            foreach (var (owner, members) in cycle)
            {
                if (owner != null) await owner.Start();
                foreach (var entity in members) await entity.Start();
            }
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
            lock (gate) waiting = IsHub(entity) && awaiting.Contains(entity.Key);
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
    /// <see cref="UserScript.RunDrainTimeout"/>, and leaves the directory unless replaced: a revision that replaces it and
    /// has not stopped takes its place there, and answers for its entities until it starts. What the host waits on for the
    /// component (<see cref="Calls"/>) ends when that time is up. Their failures are logged, never thrown. Pushes nothing
    /// to Home Assistant. A second call returns the first one's task.
    /// </summary>
    public Task StopAsync()
    {
        TaskCompletionSource completion;
        Task? start;
        Repeating[] ticking;
        IDisposable[] following;
        DefinitionHost? next;
        GraphEntityState[] final;
        lock (gate)
        {
            if (stopped != null) return stopped.Task;
            stopped = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            (next, successor) = (successor, null);
            // Before the status: a request that sees this one stopped also sees that it is replaced.
            replacing = next != null;
            status = Stopped;
            start = starting;
            ticking = [.. timers];
            following = [.. tracks];
            timers.Clear();
            tracks.Clear();
            final = next != null ? CurrentStates() : [];
        }
        // The revision replacing this one gets what changed while it was prepared, and its cameras the frames of these;
        // outside the lock, as it takes its own, and as each camera takes its own.
        next?.Inherit(this, final, next != null ? HandedFrames() : []);
        StopMarked?.Invoke();
        _ = StopCoreAsync(completion, start, ticking, following, next);
        return completion.Task;
    }

    // The heir is the revision replacing this one, which takes its place in the directory as it leaves (until it starts,
    // and registers itself); held only until this stop has ended.
    async Task StopCoreAsync(TaskCompletionSource completion, Task? start, Repeating[] ticking, IDisposable[] following, DefinitionHost? heir)
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
                Report($"{left} timer callback(s), track callback(s), command(s) or frame(s) did not finish within {Seconds()} s of stopping; going on without them", PyLogLevel.Warn);
            }
            if (start == null) return;
            var cycle = lifecycle;
            var stops = Task.Run(async () =>
            {
                foreach (var (owner, members) in cycle)
                {
                    foreach (var entity in members) await StopOne(entity);
                    if (owner != null) await StopOne(owner);
                }
            });
            if (!await UserScript.Drain([stops], Left()))
                Report($"Stop() did not finish within {Seconds()} s; unloading without waiting for it", PyLogLevel.Warn);
        }
        catch (Exception failure) { Report("stopping failed", failure, PyLogLevel.Error); }
        finally
        {
            Manager.Entities.Leave(this, heir);
            // A revision this one was to replace keeps nothing of it, nor it of that one.
            ReleasePredecessor();
            // Nothing of the user's code stays reachable from the host once stopped.
            entities = [];
            lifecycle = [];
            parts = [];
            nodes.Clear();
            // After the last Stop(), which may still make requests: disposing the client cancels what is still in flight on
            // it, and closing the downloads ends those still running.
            HttpClient? client;
            SharpFiles? downloads;
            lock (gate)
            {
                released = true;
                (client, http) = (http, null);
                (downloads, files) = (files, null);
            }
            try { client?.Dispose(); }
            catch (Exception failure) { Report("disposing the HTTP client failed", failure, PyLogLevel.Warn); }
            try { downloads?.Close(); }
            catch (Exception failure) { Report("ending the downloads failed", failure, PyLogLevel.Warn); }
            lock (gate)
            {
                running.Clear();
                unpublishable.Clear();
                graphEntities = new(StringComparer.Ordinal);
                awaiting.Clear();
                frameFlows.Clear();
                frameFailures.Clear();
                handed = [];
                own.Clear();
                successor = null;
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
    /// Runs a command Home Assistant sent to one of the definition's entities: a C# node's entity's own handler, or an
    /// Entity node's When commanded flow for it (<see cref="RunFlow"/>); stopping waits for it. What it throws is what Home Assistant shows.
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
            // An Entity node's command runs its flow; a C# node's entity takes its own, and refuses one it does not take.
            if (definition.Roots is { } roots && IsHub(entity) && entity.Commands.Contains(name, StringComparer.Ordinal)) await RunFlow(roots, entity, name, data);
            else await entity.Command(name, data);
        }
        finally
        {
            lock (gate) running.Remove(done.Task);
            done.TrySetResult();
        }
    }

    /// <summary>
    /// A picture of one of the definition's cameras for Home Assistant, as JPEG, by the rule of <see cref="CameraFrames"/>:
    /// for a camera that makes its frames (a C# node's <c>Frame()</c> or <c>OnFrame</c>, a graph's frame flow,
    /// <see cref="FrameNeeded"/>), its current frame once the making the request shares has ended; for any other, its
    /// current frame. A frame that cannot be made or encoded is logged at Error once for each new reason, naming the
    /// camera, and the first picture of a frame made or shown after one says once that it makes frames again; a frame
    /// that cannot be encoded leaves the last good one as the answer for a while. While the definition starts, the
    /// current frame (one the revision it replaces handed over) is the answer and none is made. Stopping cancels the
    /// frame being made (through <see cref="Stopping"/>) and waits for it as for a command; a request then waiting is
    /// answered with the last good frame while there is one, as when making one failed (a camera that makes none with its
    /// current frame, however old), and so is a request while a revision that a new one replaces is still stopping; once
    /// it has left, the new revision answers until it starts. One turned off or deleted refuses every later request.
    /// </summary>
    /// <exception cref="InvalidOperationException">The definition is not running (or stopped meanwhile, with no good frame), the camera has not shown a frame, or making or encoding one failed and the last good one is too old.</exception>
    /// <exception cref="TimeoutException">Making one took longer than <see cref="CameraFrames.MakeLimit"/> and the last good one is too old.</exception>
    public async Task<byte[]> CameraImage(SharpCamera camera)
    {
        var stopping = Stopping;
        var frames = camera.Frames;
        var what = $"{camera.Titled} could not make a frame";
        var interval = CameraFrames.IntervalSecond * camera.FrameInterval;
        var goodFor = CameraFrames.GoodWithin(interval);
        // A camera that makes no frames answers its current one however old, also when the definition stops.
        var makes = Maker(camera) != null;
        // The same frame fails the same way for every request: its whole failure is logged with its first line.
        string Unencoded(Exception failure) => Failing(frames.Failures, what, failure, everyTime: false);
        if (status == SettingUp)
        {
            // Starting: what the camera has, made by none, within the time a good frame is given when it makes its frames.
            try { return (await frames.Picture(camera.Titled, makes ? Starting : null, goodFor, Unencoded)).Jpeg; }
            catch (Exception) { throw NotRunning(); }
        }
        // Replaced by a new revision, still stopping (it may wait for a Stop() or a frame): the good frame, none made.
        if (status == Stopped && replacing) return await Overtaken(camera, makes, goodFor, Unencoded);
        if (status != Running) throw NotRunning();
        if (FrameChecked is { } hold) await hold();
        try
        {
            CameraFrames.Outcome? outcome = null;
            Task<CameraFrames.Outcome>? shared = null;
            if (Maker(camera) is { } make)
                outcome = await (shared = frames.Share(interval, () => Make(camera, make, what))).WaitAsync(stopping);
            // Also when the making saw the stop first.
            if (stopping.IsCancellationRequested) return await Overtaken(camera, makes, goodFor, Unencoded);
            var (jpeg, current) = await frames.Picture(camera.Titled, outcome, goodFor, Unencoded);
            // Not after a newer attempt failed meanwhile: the one this request shared is then no longer the news.
            if (current && (shared == null || outcome is { Failure: null } && frames.IsLast(shared))) Working(frames.Failures, $"{camera.Titled} makes frames again");
            return jpeg;
        }
        // A request that found the definition running and reaches making a frame as a new revision replaces it, before its
        // stop has cancelled what it runs, is refused there (Make): it is overtaken by the stop all the same.
        catch (Exception) when (stopping.IsCancellationRequested || replacing) { return await Overtaken(camera, makes, goodFor, Unencoded); }
    }

    // A request the definition's stop overtook: the camera's good frame, while there is one (the current frame, however
    // old, of a camera that makes none), else the refusal of a camera whose definition is not running.
    static async Task<byte[]> Overtaken(SharpCamera camera, bool makes, TimeSpan goodFor, Func<Exception, string> unencoded)
    {
        try { return (await camera.Frames.Picture(camera.Titled, makes ? Cancelled : null, goodFor, unencoded)).Jpeg; }
        catch (Exception) { throw NotRunning(); }
    }

    static InvalidOperationException NotRunning() => new("The entity is not running");

    // How the camera's frames are made when one is needed: the graph's frame flow for it, else a C# node's Frame(); null when
    // the camera only shows them.
    Func<Task<SharpImage?>>? Maker(SharpCamera camera)
    {
        Func<Task>? flow = null;
        // A frame flow is an Entity node's, for its own camera.
        if (IsHub(camera)) lock (gate) frameFlows.TryGetValue(camera.Key, out flow);
        if (flow != null) return async () => { await flow(); return null; };
        return camera.MakesFrames ? camera.MakeFrame : null;
    }

    // Begins making a frame of the camera, which the requests share (CameraFrames.Share, which calls this under the
    // camera's lock, so it never blocks): the code runs on the pool, counted among what stopping waits for until it has
    // ended, also after its outcome is known; until then no other frame of the camera starts, a graph's frame flow's too.
    CameraFrames.Making Make(SharpCamera camera, Func<Task<SharpImage?>> make, string what)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            // Under the lock that stopping takes: a frame either is refused or is one stopping waits for.
            if (status != Running) throw NotRunning();
            running.Add(done.Task);
        }
        // The async lambda makes a null task fail rather than look cancelled.
        var made = Task.Run(async () => await make());
        _ = made.ContinueWith(_ =>
        {
            lock (gate) running.Remove(done.Task);
            done.TrySetResult();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return new(Ended(camera, made, what), made);
    }

    // How making the frame ended, within CameraFrames.MakeLimit: a picture becomes the current frame (a copy, as Show
    // keeps one), none keeps it; a failure, or a frame later than the limit (left to finish, what it gives not shown), is
    // logged once per reason. A stop is no failure, and no request waits for it: the definition's (a cancellation through
    // Stopping, or its flow cancelled because its script's lifetime ended, as when Home Assistant stops), or a picture
    // Python did not take because Home Assistant or HassSharp is stopping.
    async Task<CameraFrames.Outcome> Ended(SharpCamera camera, Task<SharpImage?> made, string what)
    {
        // Off the camera's lock, which Share holds as it begins this: nothing of the user's, nor a log line, runs under it.
        await Task.Yield();
        var token = Stopping;
        using (var timer = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            if (await Task.WhenAny(made, Task.Delay(CameraFrames.MakeLimit, timer.Token)) != made)
            {
                _ = made.ContinueWith(ended => _ = ended.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                if (token.IsCancellationRequested) return Cancelled;
                var late = $"it took longer than {Seconds(CameraFrames.MakeLimit)} s";
                Failing(camera.Frames.Failures, what, late);
                return new(late, TimedOut: true);
            }
            timer.Cancel();
        }
        try
        {
            if (await made is { } image) camera.Frames.Show(image.Copy());
            else camera.Frames.Keep();
            return CameraFrames.Outcome.Worked;
        }
        catch (Exception failure)
        {
            if (failure is OperationCanceledException && (token.IsCancellationRequested || script.LifetimeToken.IsCancellationRequested)) return Cancelled;
            // The request still says why there is no new frame; nothing is logged.
            if (HassCameras.Stopping(failure) is { } stopped) return new(stopped.Message);
            return new(Failing(camera.Frames.Failures, what, failure));
        }
    }

    static readonly CameraFrames.Outcome Cancelled = new("the definition stopped"), Starting = new("the definition is starting");

    // Each of this revision's cameras' frames, by unique id, for the revision that replaces it (CameraFrames.TakeOver). Never throws.
    (string UniqueId, CameraFrames.Handover Frames)[] HandedFrames()
    {
        try { return [.. entities.OfType<SharpCamera>().Select(camera => (UniqueId(camera), camera.Frames.Handed()))]; }
        catch (Exception) { return []; }
    }

    /// <summary>The definition's entity with this unique id; null when it has none.</summary>
    internal SharpEntity? Entity(string uniqueId) => entities.FirstOrDefault(entity => UniqueId(entity) == uniqueId);

    /// <summary>
    /// Sends the entity's snapshot towards Home Assistant, and, while the definition runs and the entity is available,
    /// remembers its state values for it to come back with (<see cref="Restorable"/>). Never throws: a snapshot that
    /// fails, such as an attribute that changes while it is read, is logged once for the entity and nothing is sent for it.
    /// </summary>
    public void Publish(SharpEntity entity)
    {
        Exception? failed = null;
        lock (gate)
        {
            // Only once started, and never after stopping: a replaced revision must not show its entities unavailable.
            if (status is not (Running or Failed)) return;
            var id = UniqueId(entity);
            try
            {
                var snapshot = entity.Snapshot(id, running: status == Running);
                Manager.Pump.Post(id, snapshot);
                // What it shows while running and available is what it comes back with (see Restorable).
                if (status == Running && entity.Available && Restorable(entity) && snapshot["values"] is JsonObject { Count: > 0 } values)
                    Manager.States?.Remember(id, values);
            }
            catch (Exception failure) { if (unpublishable.Add(entity)) failed = failure; }
        }
        // Outside the lock: logging calls into Python.
        if (failed != null) Report($"publishing the state of entity '{entity.Key}' failed", failed, PyLogLevel.Error);
    }

    /// <summary>
    /// Whether the entity comes back with the state it last showed, and so has it remembered: of a graph's Entity nodes,
    /// each entity no State works out (one that does is worked out again as it starts); of a C# node, each whose
    /// <see cref="SharpEntity.RestoreState"/> is true.
    /// </summary>
    bool Restorable(SharpEntity entity) => IsHub(entity) ? !stateKeys.Contains(entity.Key) : entity.RestoreState;

    // A C# node's entity takes the values remembered for it, before its Setup(), through its own setters: nothing is
    // published before the start. A value it no longer takes is left out, which the debug log says. Never throws: it runs
    // inside the user's Add().
    void RestoreComponent(SharpEntity entity)
    {
        try
        {
            if (!entity.RestoreState || Manager.States is not { } states || !states.TryGet(UniqueId(entity), out var values)) return;
            foreach (var dropped in entity.Restore(values)) Report($"{entity.Called} starts without its last {dropped}", PyLogLevel.Debug);
        }
        catch (Exception failure) { Report($"restoring the state of entity '{entity.Key}' failed", failure, PyLogLevel.Warn); }
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
            var what = $"the timer callback every {Interval(interval)}";
            repeating.Loop = Task.Run(async () =>
            {
                try
                {
                    // One call at a time: a tick that comes while the action runs is taken after it.
                    if (immediately) await Tick(repeating, what, action, token);
                    while (await timer.WaitForNextTickAsync(token)) await Tick(repeating, what, action, token);
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

    // A timer's interval as its log lines name it: in seconds, or in milliseconds below one.
    static string Interval(TimeSpan interval) => interval >= TimeSpan.FromSeconds(1) ? $"{Seconds(interval)} s"
        : $"{interval.TotalMilliseconds.ToString("0.#", CultureInfo.InvariantCulture)} ms";

    /// <summary>
    /// One call of a timer's callback. A poll of a device that does not answer fails on every tick: the first failure
    /// logs an error naming the definition and the timer, and one for another reason does again, as a state flow's warning
    /// does; each failure is logged whole at Debug. A call that works after a failure says so, once.
    /// </summary>
    async Task Tick(Repeating repeating, string what, Func<Task> action, CancellationToken token)
    {
        try { await action(); }
        catch (Exception failure)
        {
            // A cancellation because the definition stops is no failure.
            if (failure is OperationCanceledException && token.IsCancellationRequested) return;
            Failing(repeating.Failures, $"{what} failed", failure);
            return;
        }
        Working(repeating.Failures, $"{what} works again");
    }

    /// <summary>
    /// A failure of something the host runs again and again, such as a timer's callback or a camera's frames: logged at
    /// Error as "<paramref name="what"/>: reason" once for each new reason (<see cref="Reason"/>), then whole at Debug,
    /// every time or, unless <paramref name="everyTime"/>, only with its Error line. Never throws.
    /// </summary>
    /// <returns>The reason.</returns>
    string Failing(FailureLog log, string what, Exception failure, bool everyTime = true)
    {
        // Worked out first: an exception's members are user code.
        string reason;
        try { reason = Reason(failure) ?? failure.GetType().Name; } catch { reason = "it failed"; }
        if (!Failing(log, what, reason) && !everyTime) return reason;
        string whole;
        try { whole = failure.ToString(); } catch { whole = failure.GetType().FullName ?? "an exception"; }
        Report($"{what}: {whole}", PyLogLevel.Debug);
        return reason;
    }

    // A failure for a reason: logged at Error when it is a new one. Whether it was.
    bool Failing(FailureLog log, string what, string reason)
    {
        if (!log.Fails(reason)) return false;
        Report($"{what}: {reason}", PyLogLevel.Error);
        return true;
    }

    // A success: after a failure, logged once at Info.
    void Working(FailureLog log, string line)
    {
        if (log.Works()) Report(line, PyLogLevel.Info);
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

    /// <summary>
    /// The entity's unique id, made here alone: <c>&lt;definition id&gt;:&lt;key&gt;</c>, but
    /// <c>&lt;definition id&gt;:&lt;device key&gt;:&lt;entity key&gt;</c> for an entity a graph's C# device added, so that two C#
    /// devices of one class are two devices, each with entities of its own.
    /// </summary>
    string UniqueId(SharpEntity entity) =>
        Node(entity) is { Kind: "device" } added ? $"{definition.Id}:{added.Key}:{entity.Key}" : $"{definition.Id}:{entity.Key}";

    void CheckKeys(SharpEntity[] all)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in all)
        {
            if (entity.Key == null || !SharpEntity.KeyPattern().IsMatch(entity.Key))
                throw new InvalidOperationException($"{definition.Name}: entity key '{entity.Key}' must be a lower-case letter followed by up to 39 lower-case letters, digits or underscores");
            // One unique id each: of the Entity nodes and C# entities, one key each; two C# devices may
            // each add an entity of one key.
            if (!seen.Add(UniqueId(entity))) throw new InvalidOperationException($"{definition.Name}: two entities have the key '{entity.Key}'");
        }
    }

    /// <summary>
    /// The devices Home Assistant gets for the definition, each after the one it is part of (<see cref="InOrder"/>): its
    /// Device nodes and then its C# devices (<see cref="ClassDevices"/>), ordered over both kinds.
    /// </summary>
    List<DefinitionDevice> Devices() => InOrder([.. definition.Devices, .. ClassDevices()]);

    /// <summary>
    /// The key of the device <paramref name="entity"/> is on, among <paramref name="keys"/>, or null for none: an Entity
    /// node's entity is on the device its description names, a C# entity on the Device node it is wired to, an entity a
    /// C# device added on that device, each else on none.
    /// </summary>
    string? DeviceOf(SharpEntity entity, IReadOnlySet<string> keys)
    {
        var on = Node(entity) is { } node ? node.Kind == "device" ? node.Key : node.Device
            : definition.Entities.OfType<JsonObject>().FirstOrDefault(e => VisualNodeCatalog.Text(e, "key") == entity.Key)?["device"] is JsonValue value
            && value.TryGetValue<string>(out var key) ? key : null;
        return on != null && keys.Contains(on) ? on : null;
    }

    /// <summary>
    /// The devices in the order Home Assistant needs them: each after the one it is part of (<c>via: {device: key}</c>), the
    /// others in the order given. Of two of one key, the first is kept, as a Part of names the first. A circle, or a parent
    /// that is no device of the list, is refused by the validator and does not loop or throw here: what can be ordered is,
    /// the rest follows in the order given, and a parent of an unknown key counts as none.
    /// </summary>
    internal static List<DefinitionDevice> InOrder(IEnumerable<DefinitionDevice> devices)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var left = devices.Where(d => d.Key is { } key && keys.Add(key)).ToList();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        List<DefinitionDevice> ordered = [];
        // Each round takes the first device whose parent is none, unknown or already placed, so roots keep their order.
        while (left.FindIndex(d => d.Via?.Device is not { } parent || !keys.Contains(parent) || placed.Contains(parent)) is var next and >= 0)
        {
            ordered.Add(left[next]);
            placed.Add(left[next].Key!);
            left.RemoveAt(next);
        }
        return [.. ordered, .. left];
    }

    /// <summary>
    /// A device as Home Assistant gets it: its key, name and every field, null where it has none, and what it is part of:
    /// another device of <paramref name="keys"/> (<c>{device}</c>) or a Home Assistant device (<c>{deviceId}</c>); null for
    /// none, for itself or for a key of no device of the definition.
    /// </summary>
    internal static JsonObject DeviceJson(DefinitionDevice device, IReadOnlySet<string> keys) => new()
    {
        ["key"] = device.Key,
        ["name"] = device.Name,
        ["manufacturer"] = device.Manufacturer,
        ["model"] = device.Model,
        ["swVersion"] = device.SwVersion,
        ["hwVersion"] = device.HwVersion,
        ["configurationUrl"] = device.ConfigurationUrl,
        ["suggestedArea"] = device.SuggestedArea,
        ["via"] = device.Via switch
        {
            { Device: { } key } => key != device.Key && keys.Contains(key) ? new JsonObject { ["device"] = key } : null,
            { DeviceId: { } id } => new JsonObject { ["deviceId"] = id },
            _ => null,
        },
    };

    sealed class Handle(Action dispose) : IDisposable
    {
        int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) dispose(); }
    }

    /// <summary>
    /// The clock of the setup limit: the time since the definition began to prepare, less the time during which a download
    /// receives (from the server's answer until the file is complete, or while it waits for another download of the same
    /// file), so that a large file does not fail the setup while a server that does not answer does. A download that
    /// receives nothing fails on its own (<see cref="SharpFiles.StallTimeout"/>).
    /// </summary>
    sealed class SetupClock
    {
        readonly Lock gate = new();
        readonly Stopwatch clock = Stopwatch.StartNew();
        TimeSpan stood, since;
        int receiving;
        TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>What is left of <paramref name="limit"/>, null while a download receives; and a task that ends when a download begins or ends receiving.</summary>
        public (TimeSpan? Left, Task Changed) Left(TimeSpan limit)
        {
            lock (gate) return (receiving > 0 ? null : limit - (clock.Elapsed - stood), changed.Task);
        }

        /// <summary>Stops the clock until the handle is disposed (with every other such handle).</summary>
        public IDisposable Receiving()
        {
            lock (gate)
                if (receiving++ == 0)
                {
                    since = clock.Elapsed;
                    Changed();
                }
            return new Handle(() =>
            {
                lock (gate)
                    if (--receiving == 0)
                    {
                        stood += clock.Elapsed - since;
                        Changed();
                    }
            });
        }

        // Under the gate; the waiters go on on their own threads.
        void Changed()
        {
            var was = changed;
            changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            was.TrySetResult();
        }
    }

    // A timer: disposing it ends its loop after the call in progress, if any.
    sealed class Repeating(PeriodicTimer timer, DefinitionHost host) : IDisposable
    {
        public Task Loop { get; set; } = Task.CompletedTask;
        /// <summary>Why its callback last failed, as last logged.</summary>
        public FailureLog Failures { get; } = new();
        public void Dispose()
        {
            timer.Dispose();
            lock (host.gate) host.timers.Remove(this);
        }
    }
}

/// <summary>
/// Why something that runs again and again last failed, as last logged, such as a timer's callback or a camera's frames:
/// a failure is logged once for each new reason, and the first success after one once. Thread-safe.
/// </summary>
internal sealed class FailureLog
{
    readonly Lock gate = new();
    string? reason;

    /// <summary>A failure for this reason: whether it is a new one, to be logged. It is the last reason from now on.</summary>
    public bool Fails(string why)
    {
        lock (gate)
        {
            if (reason == why) return false;
            reason = why;
            return true;
        }
    }

    /// <summary>A success: whether it ends a failure, to be logged.</summary>
    public bool Works()
    {
        lock (gate)
        {
            if (reason == null) return false;
            reason = null;
            return true;
        }
    }
}
