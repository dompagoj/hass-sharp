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
    public static JsonElement Entity(HasEntityState newState, HasEntityState? oldState) => JsonSerializer.SerializeToElement(new JsonObject
    {
        ["platform"] = "entity", ["entity_id"] = newState.EntityId,
        ["from_state"] = oldState == null ? null : State(oldState), ["to_state"] = State(newState),
    });

    /// <summary>A <c>daily</c> or <c>once</c> occurrence; <c>planned</c> is ISO-8601 UTC.</summary>
    public static JsonElement Schedule(string scheduleId, DateTime plannedUtc) => JsonSerializer.SerializeToElement(new JsonObject
    {
        ["platform"] = "schedule", ["schedule_id"] = scheduleId,
        ["planned"] = (plannedUtc.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(plannedUtc, DateTimeKind.Utc) : plannedUtc.ToUniversalTime()).ToString("O", CultureInfo.InvariantCulture),
    });

    public static JsonElement Manual() => Platform("manual");
    public static JsonElement Startup() => Platform("startup");

    static JsonElement Platform(string platform) => JsonSerializer.SerializeToElement(new JsonObject { ["platform"] = platform });

    // The bridge does not report last_updated, so it stays null, as in EntityStateSnapshot.From.
    static JsonObject State(HasEntityState state) => new()
    {
        ["entity_id"] = state.EntityId, ["state"] = state.State,
        ["attributes"] = JsonNode.Parse(EntityStateSnapshot.NormalizeAttributes(state.Attributes).GetRawText()),
        ["last_changed"] = Time(state.LastChanged), ["last_updated"] = null, ["last_reported"] = Time(state.LastReported),
    };

    static string Time(double unixSeconds) => DateTimeOffset.UnixEpoch.AddSeconds(unixSeconds).ToString("O", CultureInfo.InvariantCulture);
}
