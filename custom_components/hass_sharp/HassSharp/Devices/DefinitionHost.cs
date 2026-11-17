using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>An entity definition as its compiled script runs it: what the stored document says besides its code.</summary>
/// <param name="Id">The definition's id; each entity's unique id is <c>&lt;Id&gt;:&lt;key&gt;</c>.</param>
/// <param name="Name">What the definition and its device are called.</param>
/// <param name="Device">What the device page shows, each field in place of the class's own; null for the class's own.</param>
/// <param name="Settings">Values for the class's <c>[Setting]</c> properties; a null value keeps the class default.</param>
public sealed record CompiledDefinition(string Id, string Name, DefinitionDevice? Device, IReadOnlyList<DefinitionSetting> Settings)
{
    /// <summary>Builds the definition's device or entity; null for <c>SharpDefinition.Create()</c> of the compiled assembly. A test seam.</summary>
    internal Func<object>? Factory { get; init; }
}

/// <summary>An entity of a running definition, with the entity id Home Assistant gave it.</summary>
/// <param name="UniqueId"><c>&lt;definition id&gt;:&lt;key&gt;</c>.</param>
/// <param name="Key">The entity's key within its definition.</param>
/// <param name="Domain">Its Home Assistant domain, such as <c>switch</c>.</param>
/// <param name="Name">What Home Assistant calls it; null for the device's name alone.</param>
/// <param name="EntityId">Its entity id, such as <c>switch.plug_relay</c>; null when Home Assistant did not say.</param>
public sealed record LiveEntity(string UniqueId, string Key, string Domain, string? Name, string? EntityId);

/// <summary>The started definitions by id, so a command from Home Assistant finds its entity by unique id.</summary>
internal sealed class EntityDirectory
{
    readonly object gate = new();
    readonly Dictionary<string, DefinitionHost> hosts = new(StringComparer.Ordinal);

    /// <summary>The started definition and entity with this unique id, if there is one.</summary>
    public bool TryGet(string uniqueId, [MaybeNullWhen(false)] out DefinitionHost host, [MaybeNullWhen(false)] out SharpEntity entity)
    {
        // A definition id never holds a colon, so the first one ends it.
        var colon = uniqueId.IndexOf(':');
        host = colon < 0 ? null : Host(uniqueId[..colon]);
        entity = host?.Entity(uniqueId);
        if (host != null && entity != null) return true;
        host = null;
        return false;
    }

    /// <summary>The started host of the definition; null when none is.</summary>
    public DefinitionHost? Host(string definitionId) { lock (gate) return hosts.GetValueOrDefault(definitionId); }

    internal void Register(DefinitionHost host) { lock (gate) hosts[host.DefinitionId] = host; }

    /// <summary>Removes the host, unless another revision of its definition has replaced it.</summary>
    internal void Leave(DefinitionHost host)
    {
        lock (gate)
            if (hosts.TryGetValue(host.DefinitionId, out var current) && current == host) hosts.Remove(host.DefinitionId);
    }
}

/// <summary>
/// Runs one entity definition inside its script: builds the user's device or entity, applies the settings, sets it up,
/// tells Home Assistant which entities exist, starts and stops it, and carries its states, timers, tracks and commands.
/// </summary>
internal sealed class DefinitionHost(UserScript script, CompiledDefinition definition) : IComponentHost
{
    const string SettingUp = "setup", Running = "running", Failed = "failed", Stopped = "stopped";
    static readonly IDisposable Nothing = new Handle(() => { });

    // Guards the status, the start and stop, and the timer and track lists.
    readonly object gate = new();
    readonly List<Repeating> timers = [];
    readonly List<IDisposable> tracks = [];
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

    /// <summary>
    /// Builds the device or entity, applies the settings and runs <c>Setup()</c> on the device, then on each entity; then
    /// checks the entity keys and, last, registers the entities with Home Assistant. A failure leaves Home Assistant
    /// untouched. Cancelled with <paramref name="lifetime"/>.
    /// </summary>
    public async Task PrepareAsync(CancellationToken lifetime)
    {
        lifetime.ThrowIfCancellationRequested();
        stopping = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var root = (definition.Factory ?? CreateCompiled)();
        if (root is not (SharpDevice or SharpEntity))
            throw new InvalidOperationException($"{definition.Name}: {root?.GetType().Name ?? "null"} is neither a SharpDevice nor a SharpEntity");
        ApplySettings((SharpComponent)root);
        device = root as SharpDevice;
        SharpEntity[] Collect() => device != null ? [.. device.Entities] : [(SharpEntity)root];
        if (device != null)
        {
            device.Host = this;
            await device.Setup().WaitAsync(Stopping);
        }
        // An entity added during a Setup() is set up too.
        var setUp = new HashSet<SharpEntity>(ReferenceEqualityComparer.Instance);
        while (Collect().FirstOrDefault(entity => !setUp.Contains(entity)) is { } next)
        {
            setUp.Add(next);
            next.Host = this;
            await next.Setup().WaitAsync(Stopping);
        }
        var all = Collect();
        CheckKeys(all);
        entities = all;
        var json = new JsonObject
        {
            ["definitionId"] = definition.Id,
            ["name"] = definition.Name,
            ["device"] = DeviceJson(),
            ["entities"] = new JsonArray(all.Select(entity => (JsonNode?)entity.Describe(UniqueId(entity))).ToArray()),
        };
        var result = JsonNode.Parse(await PyInterop.SyncEntitiesAsync(json.ToJsonString()).WaitAsync(Stopping))?["entities"];
        Entities = all.Select(entity => new LiveEntity(UniqueId(entity), entity.Key, entity.Domain, entity.Name,
            result?[UniqueId(entity)]?["entityId"] is JsonValue id && id.TryGetValue<string>(out var text) ? text : null)).ToArray();
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
        catch (Exception failure) { Report($"starting failed: {failure.Message}", PyLogLevel.Error); }
    }

    async Task StartAsync()
    {
        var (root, all) = (device, entities);
        // A replaced revision may have sent the same states: they are sent again, as the new entities' first.
        Manager.Pump.Forget(all.Select(UniqueId));
        try
        {
            if (root != null) await root.Start();
            foreach (var entity in all) await entity.Start();
            lock (gate) if (stopped == null) status = Running;
        }
        catch (Exception failure)
        {
            lock (gate)
            {
                if (stopped != null) return;
                // The error first: whoever reads the status failed without the lock then finds it.
                error = failure.Message;
                status = Failed;
            }
            Report($"starting failed: {failure.Message}", PyLogLevel.Error);
        }
        foreach (var entity in all) Publish(entity);
    }

    /// <summary>
    /// Stops the definition: from now on it publishes nothing and refuses commands. Cancels <see cref="Stopping"/>, ends
    /// its timers and tracks, waits for a start still running, then runs <c>Stop()</c> on each entity and then the
    /// device, all within <see cref="UserScript.RunDrainTimeout"/>, and leaves the directory unless replaced. Their
    /// failures are logged, never thrown. Pushes nothing to Home Assistant. A second call returns the first one's task.
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
            try { stopping.Cancel(); }
            catch (Exception failure) { Report($"a cancellation callback failed: {failure.Message}", PyLogLevel.Warn); }
            foreach (var handle in following.Concat(ticking))
                try { handle.Dispose(); }
                catch (Exception failure) { Report($"ending a timer or track failed: {failure.Message}", PyLogLevel.Warn); }
            if (start == null) return;
            if (!await UserScript.Drain([start], Left()))
            {
                Report($"Start() did not finish within {Seconds()} s of stopping; Stop() is not called", PyLogLevel.Warn);
                return;
            }
            await UserScript.Drain([.. ticking.Select(timer => timer.Loop)], Left());
            var (root, all) = (device, entities);
            var stops = Task.Run(async () =>
            {
                foreach (var entity in all) await StopOne(entity);
                if (root != null) await StopOne(root);
            });
            if (!await UserScript.Drain([stops], Left()))
                Report($"Stop() did not finish within {Seconds()} s; unloading without waiting for it", PyLogLevel.Warn);
        }
        catch (Exception failure) { Report($"stopping failed: {failure.Message}", PyLogLevel.Error); }
        finally
        {
            Manager.Entities.Leave(this);
            // Nothing of the user's code stays reachable from the host once stopped.
            device = null;
            entities = [];
            completion.TrySetResult();
        }
    }

    async Task StopOne(SharpComponent component)
    {
        try { await component.Stop(); }
        catch (Exception failure) { Report($"stopping failed: {failure.Message}", PyLogLevel.Error); }
    }

    static string Seconds() => UserScript.RunDrainTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>Runs a command Home Assistant sent to one of the definition's entities.</summary>
    /// <exception cref="InvalidOperationException">The definition is not running.</exception>
    public async Task Command(SharpEntity entity, string name, JsonElement data)
    {
        if (Status != Running) throw new InvalidOperationException("The entity is not running");
        await entity.Command(name, data);
    }

    /// <summary>The definition's entity with this unique id; null when it has none.</summary>
    internal SharpEntity? Entity(string uniqueId) => entities.FirstOrDefault(entity => UniqueId(entity) == uniqueId);

    /// <inheritdoc/>
    public void Publish(SharpEntity entity)
    {
        lock (gate)
        {
            // Only once started, and never after stopping: a replaced revision must not show its entities unavailable.
            if (status is not (Running or Failed)) return;
            var id = UniqueId(entity);
            Manager.Pump.Post(id, entity.Snapshot(id, running: status == Running));
        }
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
            return Task.Run(() => Callback("an entity callback", () => action(change), token));
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

    /// <inheritdoc/>
    public void Log(string message, PyLogLevel level) => PyInterop.Log((int)level, message);

    // The host's own reports of failures: a log that fails must not stop a timer or skip a publish.
    void Report(string message, PyLogLevel level)
    {
        try { Log($"{definition.Name}: {message}", level); }
        catch { }
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
        Report($"{what} failed: {failure.Message}", PyLogLevel.Error);
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

    // The saved check of the settings can be stale once a library changes, so a setting that no longer fits fails here
    // rather than leaving the definition to run on defaults.
    void ApplySettings(SharpComponent root)
    {
        var type = root.GetType();
        foreach (var setting in definition.Settings)
        {
            if (setting.Value == null) continue;
            var property = SettingProperty(type, setting.Key)
                ?? throw new InvalidOperationException($"{definition.Name}: {type.Name} has no setting {setting.Key}");
            if (!TryConvert(setting.Value, property.PropertyType, out var value))
                throw new InvalidOperationException($"{definition.Name}: the saved value of setting {setting.Key} does not fit its type {property.PropertyType.Name}");
            property.SetMethod!.Invoke(root, BindingFlags.DoNotWrapExceptions, null, [value], null);
        }
    }

    // The public settable [Setting] property of that name, the most derived one first.
    static PropertyInfo? SettingProperty(Type type, string key)
    {
        for (var current = type; current != null; current = current.BaseType)
            foreach (var property in current.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (property.Name == key && property.GetIndexParameters().Length == 0 && property.SetMethod is { IsPublic: true }
                    && property.IsDefined(typeof(SettingAttribute), false))
                    return property;
        return null;
    }

    static bool TryConvert(JsonNode node, Type type, out object? value)
    {
        value = null;
        var target = Nullable.GetUnderlyingType(type) ?? type;
        using var document = JsonDocument.Parse(node.ToJsonString());
        var json = document.RootElement;
        switch (json.ValueKind)
        {
            case JsonValueKind.String when target == typeof(string): value = json.GetString(); return true;
            case JsonValueKind.String when target.IsEnum && Enum.GetNames(target).Contains(json.GetString(), StringComparer.Ordinal):
                value = Enum.Parse(target, json.GetString()!); return true;
            case JsonValueKind.True or JsonValueKind.False when target == typeof(bool): value = json.GetBoolean(); return true;
            case JsonValueKind.Number when target == typeof(int) && json.TryGetInt32(out var whole): value = whole; return true;
            case JsonValueKind.Number when target == typeof(long) && json.TryGetInt64(out var large): value = large; return true;
            case JsonValueKind.Number when target == typeof(double) && json.TryGetDouble(out var number): value = number; return true;
            default: return false;
        }
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
