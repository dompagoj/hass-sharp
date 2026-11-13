using System.Text.Json;

namespace HassSharp;

/// <summary>
/// The run in progress. <see cref="ScriptSyncRunner"/> sets it for the duration of one run; every cause
/// carries one normalised trigger <see cref="Payload"/> built by <see cref="TriggerPayload"/>.
/// </summary>
public sealed record InvocationContext(string RunId, InvocationCause Cause, DateTime? PlannedUtc, JsonElement Payload)
{
    static readonly AsyncLocal<InvocationContext?> current = new();

    /// <summary>The current run, or null outside a run.</summary>
    public static InvocationContext? Current { get => current.Value; internal set => current.Value = value; }
}
