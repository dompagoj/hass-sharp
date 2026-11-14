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
        foreach (var action in _actions.Values)
        {
            foreach (var entity in action.Entities ?? [])
                Script.ScriptManager.TrackEntityCall(entity.EntityId, this, action.Method);
            var lifetime = Script.LifetimeToken;
            var triggers = action.Triggers ?? [];
            if (triggers.Length > 0)
            {
                var lease = await PyInterop.RegisterHaTriggersAsync(Script.Generation,
                    System.Text.Json.JsonSerializer.Serialize(triggers), payload => RunNative(action.Method, lifetime, payload));
                _nativeLeases.Add(lease);
                lifetime.ThrowIfCancellationRequested();
            }
            foreach (var schedule in action.Schedules ?? [])
                _scheduleUnsubs.Add(PyInterop.RegisterSchedule(System.Text.Json.JsonSerializer.Serialize(schedule),
                    planned => lifetime.IsCancellationRequested ? Task.CompletedTask : RunSchedule(action.Method, schedule.Id, planned)));
        }
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
            if (Script.ScriptManager.History is { } history && !history.ClaimOccurrence(occurrence)) return Task.CompletedTask;
            return Script.ScriptManager.SyncRunner.Run(this, Methods.Single(m => m.Name == method),
                _actions[method].Mode, InvocationCause.Schedule, TriggerPayload.Schedule(scheduleId, utc), occurrence, utc);
        }
    }

    internal Task StartupCompletion { get; private set; } = Task.CompletedTask;

    /// <summary>Runs the startup actions, unless the automation is off.</summary>
    internal void Start() => StartupCompletion = !Script.Enabled ? Task.CompletedTask : Task.WhenAll(
        _actions.Values.Where(a => a.Startup).Select(a => RunMethod(a.Method, InvocationCause.Startup)));

    /// <param name="payload">The normalised trigger payload; manual and startup runs build their own when omitted.</param>
    internal Task RunMethod(string method, InvocationCause cause = InvocationCause.Manual, string? runId = null, System.Text.Json.JsonElement? payload = null)
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
        return Script.ScriptManager.SyncRunner.Run(this, Methods.Single(m => m.Name == method), action.Mode, cause, normalised, runId);
    }

    internal string GetConcatedMethodNameWithClass(string methodName) => $"{ClassName}::{methodName}";
}
