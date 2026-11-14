using System.Reflection;
namespace HassSharp;

class UserScriptClass
{
    internal UserScript Script { get; }
    public string ClassName => Instance.GetType().FullName ?? "Unknown";
    public MethodInfo[] Methods { get; private set; } = [];
    internal Automation Instance { get; }
    readonly List<Action> _scheduleUnsubs = new();
    readonly List<HomeAssistantTriggerLease> _nativeLeases = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _occurrences = new();
    Dictionary<string, AutomationAction> _actions = new();

    internal UserScriptClass(Type classType, UserScript script)
    {
        Script = script;
        Instance = (Automation)Activator.CreateInstance(classType)!;
    }

    internal void Configure()
    {
        var actions = Instance.Configure().Actions.ToDictionary(a => a.Method);
        var methods = actions.Keys.Select(name => Instance.GetType().GetMethod(name)
            ?? throw new InvalidOperationException($"Unknown action {name}")).ToArray();
        foreach (var method in methods)
            if (method.IsStatic || method.GetParameters().Length != 0 ||
                (method.ReturnType != typeof(Task) && method.ReturnType != typeof(ValueTask)))
                throw new InvalidOperationException($"Action {method.Name} must return Task or ValueTask and take no parameters");
        foreach (var action in actions.Values)
        {
            var schedules = action.Schedules ?? [];
            if (schedules.Any(s => string.IsNullOrWhiteSpace(s.Id) ||
                (s.AtUtc.HasValue == (s.LocalTime != null && s.TimeZone != null))) ||
                schedules.Select(s => s.Id).Distinct().Count() != schedules.Length)
                throw new InvalidOperationException("Schedules need unique IDs and either AtUtc or LocalTime/TimeZone");
        }
        _actions = actions;
        Methods = methods;
    }

    internal async Task SubscribeAsync()
    {
        await Script.NativeSwitch.WaitAsync();
        try
        {
            // An automation that is off never attaches its Home Assistant triggers; turning it on does (AttachNativeAsync).
            var attach = Script.Enabled;
            foreach (var action in _actions.Values)
            {
                foreach (var entity in action.Entities ?? [])
                    Script.ScriptManager.TrackEntityCall(entity.EntityId, this, action.Method);
                var lifetime = Script.LifetimeToken;
                if (attach && (action.Triggers ?? []).Length > 0)
                {
                    _nativeLeases.Add(await RegisterNativeAsync(action, lifetime));
                    lifetime.ThrowIfCancellationRequested();
                }
                foreach (var schedule in action.Schedules ?? [])
                    _scheduleUnsubs.Add(PyInterop.RegisterSchedule(System.Text.Json.JsonSerializer.Serialize(schedule),
                        planned => lifetime.IsCancellationRequested ? Task.CompletedTask : RunSchedule(action.Method, schedule.Id, planned)));
            }
            _nativeAttached = attach;
        }
        finally { Script.NativeSwitch.Release(); }
    }

    Task<HomeAssistantTriggerLease> RegisterNativeAsync(AutomationAction action, CancellationToken lifetime) =>
        PyInterop.RegisterHaTriggersAsync(Script.Generation,
            System.Text.Json.JsonSerializer.Serialize(action.Triggers), payload => RunNative(action.Method, lifetime, payload));

    /// <summary>Whether this class's Home Assistant triggers are attached; read and written with <see cref="UserScript.NativeSwitch"/> held.</summary>
    bool _nativeAttached;

    /// <summary>
    /// Attaches the Home Assistant triggers of an automation turned on, once: a second call while attached does nothing.
    /// A registration that fails removes the ones this call made, so a later attach starts clean. Call with
    /// <see cref="UserScript.NativeSwitch"/> held.
    /// </summary>
    internal async Task AttachNativeAsync()
    {
        if (_nativeAttached) return;
        var lifetime = Script.LifetimeToken;
        try
        {
            foreach (var action in _actions.Values)
            {
                if ((action.Triggers ?? []).Length == 0) continue;
                if (lifetime.IsCancellationRequested) return;
                _nativeLeases.Add(await RegisterNativeAsync(action, lifetime));
            }
        }
        catch { await UnsubscribeNativeAsync(); throw; }
        _nativeAttached = true;
    }

    /// <summary>Removes the Home Assistant triggers of an automation turned off. Call with <see cref="UserScript.NativeSwitch"/> held.</summary>
    internal Task DetachNativeAsync()
    {
        _nativeAttached = false;
        return UnsubscribeNativeAsync();
    }

    internal void UnsubscribeSchedules()
    {
        var callbacks = _scheduleUnsubs.ToArray();
        _scheduleUnsubs.Clear();
        List<Exception> errors = [];
        foreach (var unsubscribe in callbacks)
            try { unsubscribe(); } catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    internal async Task UnsubscribeNativeAsync()
    {
        var leases = _nativeLeases.ToArray();
        _nativeLeases.Clear();
        var cleanup = leases.Select(async lease => await lease.DisposeAsync()).ToArray();
        await Task.WhenAll(cleanup);
    }

    internal Task RunNative(string method, CancellationToken lifetime, string payload)
    {
        lock (Script.ScriptManager.ActivationGate)
        {
            if (!Script.Active || !Script.Enabled || lifetime.IsCancellationRequested) return Task.CompletedTask;
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            return Script.ScriptManager.SyncRunner.Run(this, Methods.Single(m => m.Name == method),
                _actions[method].Mode, InvocationCause.HomeAssistantTrigger, TriggerPayload.Native(document.RootElement));
        }
    }

    internal Task RunSchedule(string method, string scheduleId, string planned)
    {
        lock (Script.ScriptManager.ActivationGate)
        {
            // Off: the occurrence is skipped and not claimed; it is not caught up when the automation is turned on.
            if (!Script.Active || !Script.Enabled || Script.LifetimeToken.IsCancellationRequested) return Task.CompletedTask;
            var utc = DateTimeOffset.Parse(planned, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime;
            var identity = System.Text.Json.JsonSerializer.Serialize(new[] {
                Script.AutomationId, ClassName, method, scheduleId, utc.ToString("O") });
            var occurrence = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
            if (!_occurrences.TryAdd(occurrence, 0)) return Task.CompletedTask;
            // Unloading waits for the claim as for a run, so nothing writes history after it returns.
            var claimed = ClaimAndRun(method, scheduleId, occurrence, utc);
            Script.TrackRun(claimed);
            return claimed;
        }
    }

    /// <summary>
    /// Claims the occurrence in run history, then runs it. The claim is a history write, so it happens after a hop off
    /// the trigger path and outside the activation gate, as a run's own records do: slow storage holds back no other
    /// trigger. If the automation stopped or was replaced meanwhile, the claim is given back for its replacement.
    /// </summary>
    async Task ClaimAndRun(string method, string scheduleId, string occurrence, DateTime utc)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var history = Script.ScriptManager.History;
        if (history != null && !history.ClaimOccurrence(occurrence)) return;
        Task? run = null;
        lock (Script.ScriptManager.ActivationGate)
        {
            if (Script.Active && Script.Enabled && !Script.LifetimeToken.IsCancellationRequested)
                run = Script.ScriptManager.SyncRunner.Run(this, Methods.Single(m => m.Name == method),
                    _actions[method].Mode, InvocationCause.Schedule, TriggerPayload.Schedule(scheduleId, utc), occurrence, utc);
        }
        if (run == null) { history?.ReleaseOccurrence(occurrence); return; }
        await run;
    }

    internal Task StartupCompletion { get; private set; } = Task.CompletedTask;

    /// <summary>Runs the startup actions, unless the automation is off.</summary>
    internal void Start() => StartupCompletion = !Script.Enabled ? Task.CompletedTask : Task.WhenAll(
        _actions.Values.Where(a => a.Startup).Select(a => RunMethod(a.Method, InvocationCause.Startup)));

    /// <param name="payload">The normalised trigger payload; manual and startup runs build their own when omitted.</param>
    /// <param name="recorded">See <see cref="ScriptSyncRunner.Run"/>.</param>
    internal Task RunMethod(string method, InvocationCause cause = InvocationCause.Manual, string? runId = null, System.Text.Json.JsonElement? payload = null, TaskCompletionSource? recorded = null)
    {
        var action = _actions[method];
        if (cause == InvocationCause.Manual && !action.Manual)
            throw new InvalidOperationException("Manual invocation is disabled");
        var normalised = payload ?? cause switch
        {
            InvocationCause.Manual => TriggerPayload.Manual(),
            InvocationCause.Startup => TriggerPayload.Startup(),
            _ => throw new ArgumentException($"A {cause} run requires its trigger payload", nameof(payload))
        };
        return Script.ScriptManager.SyncRunner.Run(this, Methods.Single(m => m.Name == method), action.Mode, cause, normalised, runId, recorded: recorded);
    }

    internal string GetConcatedMethodNameWithClass(string methodName) => $"{ClassName}::{methodName}";
}
