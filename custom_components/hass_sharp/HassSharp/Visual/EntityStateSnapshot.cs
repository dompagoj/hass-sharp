using System.Text.Json;

namespace HassSharp;

/// <summary>A detached, immutable Home Assistant state. Attributes contain JSON values, never Python handles.</summary>
public sealed class EntityStateSnapshot
{
    public string EntityId { get; }
    public string State { get; }
    public JsonElement Attributes { get; }
    public DateTimeOffset LastChanged { get; }
    public DateTimeOffset? LastUpdated { get; }
    public DateTimeOffset LastReported { get; }

    public EntityStateSnapshot(string entityId, string state, JsonElement attributes, DateTimeOffset lastChanged,
        DateTimeOffset lastReported, DateTimeOffset? lastUpdated = null)
    {
        if (attributes.ValueKind != JsonValueKind.Object) throw new ArgumentException("Attributes must be a JSON object", nameof(attributes));
        EntityId = entityId ?? throw new ArgumentNullException(nameof(entityId));
        State = state ?? throw new ArgumentNullException(nameof(state));
        Attributes = attributes.Clone();
        LastChanged = lastChanged; LastReported = lastReported; LastUpdated = lastUpdated;
    }

    /// <summary>Copies the bridge state without subscriptions. The current bridge does not provide last_updated.</summary>
    public static EntityStateSnapshot From(HasEntityState state, DateTimeOffset? lastUpdated = null) => new(
        state.EntityId, state.State, JsonSerializer.SerializeToElement(state.Attributes ?? new()),
        DateTimeOffset.UnixEpoch.AddSeconds(state.LastChanged), DateTimeOffset.UnixEpoch.AddSeconds(state.LastReported), lastUpdated);
}
