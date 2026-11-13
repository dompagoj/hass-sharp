using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>
/// The readable <c>trigger</c> of a run (contract C9), built from the normalised payload (C6). The result is
/// always valid JSON of at most <see cref="MaxLength"/> chars: long strings are shortened, never the JSON.
/// </summary>
public static class TriggerSummary
{
    public const int MaxLength = 2048;
    static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Summarize(InvocationCause cause, JsonElement payload)
    {
        var fields = new List<(string Key, string Value)>();
        void Add(string key, string? value) { if (value != null) fields.Add((key, value)); }
        var kind = cause switch
        {
            InvocationCause.HomeAssistantTrigger => "ha", InvocationCause.Entity => "entity", InvocationCause.Schedule => "schedule",
            InvocationCause.Manual => "manual", _ => "startup",
        };
        if (kind == "ha")
        {
            Add("platform", Scalar(payload, "platform") ?? Scalar(payload, "trigger"));
            Add("id", Scalar(payload, "id"));
            Add("description", Scalar(payload, "description"));
        }
        if (kind is "ha" or "entity")
        {
            Add("entityId", Scalar(payload, "entity_id"));
            Add("from", State(payload, "from_state"));
            Add("to", State(payload, "to_state"));
        }
        if (kind == "ha" && Property(payload, "event") is { ValueKind: JsonValueKind.Object } native) Add("eventType", Scalar(native, "event_type"));
        if (kind == "schedule")
        {
            Add("scheduleId", Scalar(payload, "schedule_id"));
            Add("planned", Scalar(payload, "planned"));
        }
        // Shorten the longest value until the summary fits; "kind" and the keys alone are far below the cap.
        while (true)
        {
            var summary = new JsonObject { ["kind"] = kind };
            foreach (var (key, value) in fields) summary[key] = value;
            var json = summary.ToJsonString(Json);
            if (json.Length <= MaxLength) return json;
            var longest = fields.Select((f, i) => (f.Value.Length, i)).Max();
            fields[longest.i] = (fields[longest.i].Key, Shorten(fields[longest.i].Value, longest.Length / 2));
        }
    }

    static JsonElement? Property(JsonElement json, string key) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(key, out var value) ? value : null;
    static string? Scalar(JsonElement json, string key) => Property(json, key) switch
    {
        { ValueKind: JsonValueKind.String } text => text.GetString(),
        { ValueKind: JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False } value => value.GetRawText(),
        _ => null,
    };
    static string? State(JsonElement json, string key) => Property(json, key) is { ValueKind: JsonValueKind.Object } state ? Scalar(state, "state") : null;
    // Never splits a surrogate pair; the ellipsis marks a shortened value.
    static string Shorten(string value, int length)
    {
        if (length <= 1) return "";
        var end = length - 1;
        if (char.IsHighSurrogate(value[end - 1])) end--;
        return value[..end] + "…";
    }
}
