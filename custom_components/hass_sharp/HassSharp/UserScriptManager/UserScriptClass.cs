using System.Reflection;
namespace HassSharp;

class UserScriptClass
{
    internal UserScript Script { get; }
    public string ClassName => Instance.GetType().FullName ?? "Unknown";
    public MethodInfo[] Methods { get; private set; } = [];
    internal Automation Instance { get; }
    readonly List<Action> _scheduleUnsubs = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _occurrences = new();
    Dictionary<string, AutomationAction> _actions = new();

    internal UserScriptClass(Type classType, UserScript script)
    {
        Script = script;
        Instance = (Automation)Activator.CreateInstance(classType)!;
        Instance.UserScriptClass = this;
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

    internal void Subscribe()
    {
        foreach (var action in _actions.Values)
        {
            foreach (var entity in action.Entities ?? [])
                Script.ScriptManager.TrackEntityCall(entity, this, action.Method);
            var lifetime = Script.LifetimeToken;
            foreach (var schedule in action.Schedules ?? [])
                _scheduleUnsubs.Add(PyInterop.RegisterSchedule(System.Text.Json.JsonSerializer.Serialize(schedule),
                    planned => lifetime.IsCancellationRequested ? Task.CompletedTask : RunSchedule(action.Method, schedule.Id, planned)));
        }
    }

    internal void UnsubscribeSchedules()
    {
        foreach (var unsubscribe in _scheduleUnsubs) unsubscribe();
        _scheduleUnsubs.Clear();
    }

    internal Task RunSchedule(string method, string scheduleId, string planned)
    {
        lock (Script.ScriptManager.ActivationGate)
        {
            if (!Script.Active || Script.LifetimeToken.IsCancellationRequested) return Task.CompletedTask;
            var utc = DateTimeOffset.Parse(planned, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime;
            var identity = System.Text.Json.JsonSerializer.Serialize(new[] {
                Script.AutomationId, ClassName, method, scheduleId, utc.ToString("O") });
            var occurrence = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
            if (!_occurrences.TryAdd(occurrence, 0)) return Task.CompletedTask;
            if (Script.ScriptManager.History is { } history && !history.ClaimOccurrence(occurrence)) return Task.CompletedTask;
            return Script.ScriptManager.SyncRunner.Run(this, Methods.Single(m => m.Name == method),
                _actions[method].Mode, InvocationCause.Schedule, occurrence, utc,
                System.Text.Json.JsonSerializer.Serialize(new { ScheduleId = scheduleId, OccurrenceId = occurrence }));
        }
    }

    internal Task StartupCompletion { get; private set; } = Task.CompletedTask;

    internal void Start() => StartupCompletion = Task.WhenAll(
        _actions.Values.Where(a => a.Startup).Select(a => RunMethod(a.Method, InvocationCause.Startup)));

    internal Task RunMethod(string method, InvocationCause cause = InvocationCause.Manual, string? runId = null)
    {
        var action = _actions[method];
        if (cause == InvocationCause.Manual && !action.Manual)
            throw new InvalidOperationException("Manual invocation is disabled");
        return Script.ScriptManager.SyncRunner.Run(this, Methods.Single(m => m.Name == method), action.Mode, cause, runId);
    }

    internal string GetConcatedMethodNameWithClass(string methodName) => $"{ClassName}::{methodName}";
}
