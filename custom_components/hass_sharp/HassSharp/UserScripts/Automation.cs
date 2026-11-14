namespace HassSharp;

public enum AutomationMode
{
    Single,
    Restart,
    Queued,
    Parallel
}

public abstract class Automation
{
    public virtual AutomationConfiguration Configure() => new();
    public InvocationCause Cause => InvocationContext.Current?.Cause ?? default;
    /// <summary>The normalised trigger payload of the current run (contract C6); undefined outside a run.</summary>
    public System.Text.Json.JsonElement TriggerData => InvocationContext.Current?.Payload ?? default;

    internal readonly AsyncLocal<CancellationToken> _cancellationToken = new();
    public CancellationToken CancellationToken => _cancellationToken.Value;
}
