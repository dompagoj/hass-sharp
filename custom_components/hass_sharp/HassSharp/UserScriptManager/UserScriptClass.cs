using System.Reflection;
namespace HassSharp;

class UserScriptClass
{
    internal UserScript Script { get; }
    public string ClassName => Instance.GetType().FullName ?? "Unknown";
    public MethodInfo[] Methods { get; private set; } = [];
    internal Automation Instance { get; }
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
        _actions = actions;
        Methods = methods;
    }

    internal void Subscribe()
    {
        foreach (var action in _actions.Values)
            foreach (var entity in action.Entities ?? [])
                Script.ScriptManager.TrackEntityCall(entity, this, action.Method);
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
