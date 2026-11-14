namespace HassSharp;

public enum InvocationCause { Startup, Entity, Manual, Schedule, HomeAssistantTrigger }

public sealed record AutomationAction(string Method, AutomationMode Mode = AutomationMode.Single,
    bool Startup = false, bool Manual = true, EntityReference[]? Entities = null, AutomationSchedule[]? Schedules = null, HomeAssistantTrigger[]? Triggers = null);

/// <summary>An entity whose state changes start an action: its id and nothing else.</summary>
public sealed record EntityReference(string EntityId);

public sealed class AutomationConfiguration(params AutomationAction[] actions)
{
    public IReadOnlyList<AutomationAction> Actions { get; } = actions;
}

/// <summary>Missed starts and DST gaps are skipped; folds fire at the first instant only.</summary>
public sealed record AutomationSchedule(string Id, DateTimeOffset? AtUtc = null,
    string? LocalTime = null, string? TimeZone = null)
{
    public static AutomationSchedule Once(string id, DateTimeOffset at) => new(id, at.ToUniversalTime());
    public static AutomationSchedule Daily(string id, TimeOnly time, string timeZone) =>
        new(id, LocalTime: time.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), TimeZone: timeZone);
}

/// <summary>A stable native HA trigger identity and its raw JSON configuration.</summary>
public sealed record HomeAssistantTrigger(string Id, string ConfigurationJson);
