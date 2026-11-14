using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>
/// Reads the fields of a visual or node library request (<see cref="HassSharpManager.VisualRequest"/>,
/// <see cref="HassSharpManager.NodeLibraryRequest"/>). A missing or wrongly typed field is an
/// <see cref="ArgumentException"/> whose message names the field, which the caller answers as <c>invalid_request</c>.
/// </summary>
static class ApiRequest
{
    public static JsonObject Parse(string json) => JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("The request must be a JSON object");

    /// <param name="hint">What the field is, added to the message when it is missing.</param>
    public static string String(JsonObject request, string key, string? hint = null) =>
        OptionalString(request, key) ?? throw new ArgumentException($"{key} is required" + (hint == null ? "" : ": " + hint));

    public static string? OptionalString(JsonObject request, string key) => request[key] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => throw new ArgumentException($"{key} must be a string"),
    };

    /// <param name="hint">What the field is, added to the message when it is missing.</param>
    public static long Long(JsonObject request, string key, string? hint = null) => request[key] switch
    {
        null => throw new ArgumentException($"{key} is required" + (hint == null ? "" : ": " + hint)),
        JsonValue value when value.TryGetValue<long>(out var number) => number,
        _ => throw new ArgumentException($"{key} must be a whole number"),
    };

    public static int? OptionalInt(JsonObject request, string key) => request[key] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<int>(out var number) => number,
        _ => throw new ArgumentException($"{key} must be a whole number"),
    };

    public static bool? OptionalBool(JsonObject request, string key) => request[key] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
        _ => throw new ArgumentException($"{key} must be true or false"),
    };

    public static bool Bool(JsonObject request, string key) => OptionalBool(request, key) ?? throw new ArgumentException($"{key} is required");

    public static JsonObject Object(JsonObject request, string key) => request[key] switch
    {
        null => throw new ArgumentException($"{key} is required"),
        JsonObject value => value,
        _ => throw new ArgumentException($"{key} must be an object"),
    };

    /// <summary>A list of objects; null when the field is absent.</summary>
    public static IEnumerable<JsonObject>? OptionalObjects(JsonObject request, string key) => request[key] switch
    {
        null => null,
        JsonArray items => items.Select(item => item as JsonObject ?? throw new ArgumentException($"Each item of {key} must be an object")).ToArray(),
        _ => throw new ArgumentException($"{key} must be a list"),
    };

    /// <summary>
    /// What a client is told of a defect in HassSharp itself (code <c>internal</c>, as the MCP tools answer theirs): never
    /// the runtime's own text, such as "Object reference not set to an instance of an object."; the log has the rest.
    /// </summary>
    public const string Internal = "HassSharp hit an internal error; see the Home Assistant log";
}
