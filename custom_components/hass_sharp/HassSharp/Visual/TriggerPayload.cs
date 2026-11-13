using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>
/// Builds the normalised trigger payload (contract C6) every run carries, whatever its cause.
/// Trigger outputs are JSON pointers into it; the whole payload is the trigger's <c>data</c> output.
/// </summary>
public static class TriggerPayload
{
    /// <summary>The <c>trigger</c> dict of HA run variables, plus the run <c>context</c>.</summary>
    public static JsonElement Native(JsonElement variables)
    {
        var payload = variables.ValueKind == JsonValueKind.Object && variables.TryGetProperty("trigger", out var trigger) && trigger.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(trigger.GetRawText())!.AsObject() : new JsonObject();
        if (variables.ValueKind == JsonValueKind.Object && variables.TryGetProperty("context", out var context))
            payload["context"] = JsonNode.Parse(context.GetRawText());
        return JsonSerializer.SerializeToElement(payload);
    }

    /// <summary>A tracked entity changed; states use the <c>State.as_dict</c> shape and a missing old state is null.</summary>
    public static JsonElement Entity(HasEntityState newState, HasEntityState? oldState) => JsonSerializer.SerializeToElement(EntityObject(newState, oldState));

    static JsonObject EntityObject(HasEntityState newState, HasEntityState? oldState) => new()
    {
        ["platform"] = "entity", ["entity_id"] = newState.EntityId,
        ["from_state"] = oldState == null ? null : State(oldState), ["to_state"] = State(newState),
    };

    /// <summary>A <c>daily</c> or <c>once</c> occurrence; <c>planned</c> is ISO-8601 UTC.</summary>
    public static JsonElement Schedule(string scheduleId, DateTime plannedUtc) => JsonSerializer.SerializeToElement(new JsonObject
    {
        ["platform"] = "schedule", ["schedule_id"] = scheduleId,
        ["planned"] = (plannedUtc.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(plannedUtc, DateTimeKind.Utc) : plannedUtc.ToUniversalTime()).ToString("O", CultureInfo.InvariantCulture),
    });

    public static JsonElement Manual() => Platform("manual");

    /// <summary>
    /// A manual run of a visual trigger. An <c>entity</c> trigger, or a native <c>state</c> or <c>numeric_state</c>
    /// trigger (the first of its entities), gets the payload a real firing would carry, with the entity's current
    /// state as both the previous and the new state, and <c>manual: true</c>. Any other trigger, or an entity
    /// without a state, gets <see cref="Manual()"/>.
    /// </summary>
    /// <param name="type">The trigger's config type, as <c>VisualNodeCatalog.TriggerType</c> resolves it.</param>
    /// <param name="currentState">Reads an entity's current state, null when it has none.</param>
    public static JsonElement Manual(string type, JsonObject? config, Func<string, HasEntityState?> currentState)
    {
        var native = type == "ha" ? config?["trigger"] as JsonObject : null;
        var platform = native == null ? null : Text(native["trigger"]) ?? Text(native["platform"]);
        var entityId = type switch
        {
            "entity" when Text(config?["entitiesExpression"]) is null or "" => Text(config?["entity"]),
            "ha" when platform is "state" or "numeric_state" => native!["entity_id"] switch
            {
                JsonArray list => list.Count > 0 ? Text(list[0]) : null,
                var single => Text(single)?.Split(',')[0].Trim(),
            },
            _ => null,
        };
        if (string.IsNullOrEmpty(entityId) || currentState(entityId) is not { State: not null } state) return Manual();
        var payload = type == "entity" ? EntityObject(state, state) : new JsonObject
        {
            ["platform"] = platform, ["entity_id"] = entityId, ["from_state"] = State(state), ["to_state"] = State(state),
        };
        if (platform == "numeric_state")
            foreach (var bound in new[] { "below", "above" })
                if (native![bound] is { } value) payload[bound] = value.DeepClone();
        payload["manual"] = true;
        return JsonSerializer.SerializeToElement(payload);
    }

    public static JsonElement Startup() => Platform("startup");

    static JsonElement Platform(string platform) => JsonSerializer.SerializeToElement(new JsonObject { ["platform"] = platform });

    // The bridge does not report last_updated, so it stays null, as in EntityStateSnapshot.From.
    static JsonObject State(HasEntityState state) => new()
    {
        ["entity_id"] = state.EntityId, ["state"] = state.State,
        ["attributes"] = JsonNode.Parse(EntityStateSnapshot.NormalizeAttributes(state.Attributes).GetRawText()),
        ["last_changed"] = Time(state.LastChanged), ["last_updated"] = null, ["last_reported"] = Time(state.LastReported),
    };

    static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    static string Time(double unixSeconds) => DateTimeOffset.UnixEpoch.AddSeconds(unixSeconds).ToString("O", CultureInfo.InvariantCulture);
}
