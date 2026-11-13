using System.Text.Json;
using System.Text.Json.Nodes;
using Python.Runtime;

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
        state.EntityId, state.State, NormalizeAttributes(state.Attributes),
        DateTimeOffset.UnixEpoch.AddSeconds(state.LastChanged), DateTimeOffset.UnixEpoch.AddSeconds(state.LastReported), lastUpdated);
    /// <summary>Converts a <c>State.as_dict</c> state. Missing attributes are empty; a missing last_reported falls back to last_updated, then last_changed.</summary>
    public static EntityStateSnapshot FromStateJson(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object) throw new ArgumentException("Entity state must be a JSON object", nameof(state));
        string Text(string key) => state.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!
            : throw new ArgumentException($"Entity state requires string {key}", nameof(state));
        DateTimeOffset? Time(string key)
        {
            if (!state.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var time)) return time;
            throw new ArgumentException($"Entity state {key} must be an ISO-8601 timestamp", nameof(state));
        }
        var attributes = state.TryGetProperty("attributes", out var raw) && raw.ValueKind != JsonValueKind.Null ? raw : JsonSerializer.SerializeToElement(new JsonObject());
        var changed = Time("last_changed") ?? throw new ArgumentException("Entity state requires last_changed", nameof(state));
        var updated = Time("last_updated");
        return new(Text("entity_id"), Text("state"), attributes, changed, Time("last_reported") ?? updated ?? changed, updated);
    }
    internal static JsonElement NormalizeAttributes(Dictionary<string, object>? attributes)
    {
        var result = new JsonObject();
        if(attributes != null) foreach(var pair in attributes) result[pair.Key] = Normalize(pair.Value);
        return JsonSerializer.SerializeToElement(result);
    }
    static JsonNode? Normalize(object? value)
    {
        if(value is PyObject python)
        {
            // The callback DTO may retain PyInt/PyFloat/containers. Detach under the GIL
            // on the managed worker; serializing PyObject itself exposes native handles.
            using var gil = Py.GIL();
            using var json = Py.Import("json");
            using var dumps = json.GetAttr("dumps");
            using var serialized = dumps.Invoke(python);
            return JsonNode.Parse(serialized.As<string>());
        }
        if(value is System.Collections.IDictionary dictionary)
        {
            var result = new JsonObject();
            foreach(System.Collections.DictionaryEntry entry in dictionary)
            {
                if(entry.Key is not string key) throw new InvalidOperationException("Entity attribute object keys must be strings.");
                result[key] = Normalize(entry.Value);
            }
            return result;
        }
        if(value is System.Collections.IEnumerable sequence && value is not string && value is not JsonNode)
        {
            var result = new JsonArray(); foreach(var item in sequence) result.Add(Normalize(item)); return result;
        }
        return value is JsonNode node ? node.DeepClone() : JsonSerializer.SerializeToNode(value);
    }
}
