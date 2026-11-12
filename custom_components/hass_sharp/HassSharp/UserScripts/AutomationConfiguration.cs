namespace HassSharp;

public enum InvocationCause { Startup, Entity, Manual }

public sealed record AutomationAction(string Method, AutomationMode Mode = AutomationMode.Single,
    bool Startup = false, bool Manual = true, string[]? Entities = null);

public sealed class AutomationConfiguration(params AutomationAction[] actions)
{
    public IReadOnlyList<AutomationAction> Actions { get; } = actions;
}
