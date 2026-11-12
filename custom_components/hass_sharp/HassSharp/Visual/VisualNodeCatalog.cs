using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>The packaged catalog is also imported by the editor and read by Python/MCP.</summary>
public static class VisualNodeCatalog
{
    internal static readonly JsonObject Root = Load();
    static JsonObject Load()
    {
        using var stream = typeof(VisualNodeCatalog).Assembly.GetManifestResourceStream("HassSharp.visual_node_catalog.json")
            ?? throw new InvalidOperationException("Embedded visual node catalog is missing");
        return JsonNode.Parse(stream)!.AsObject();
    }
    public static JsonObject GetCatalog() => (JsonObject)Root.DeepClone();
    public static JsonObject? GetNode(string kind) => Root["nodes"]?[kind]?.DeepClone() as JsonObject;
    internal static JsonObject? Spec(JsonObject node) => Root["nodes"]?[Text(node, "kind")] as JsonObject;
    internal static string Text(JsonNode? node, string key, string fallback = "") => node is JsonObject obj && obj[key] is JsonValue v && v.TryGetValue<string>(out var result) ? result : fallback;
    internal static IEnumerable<JsonObject> Declarations(JsonObject node, string key) => (node["config"]?[key] as JsonArray ?? []).OfType<JsonObject>();
    internal static string ResolveType(string type, JsonObject node) => type.StartsWith('$') ? Text(node["config"], type[1..]) : type;
    public static string EffectiveTriggerMethod(JsonObject node) => "Run_" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(Text(node, "id")));
    public static bool CompatibleTypes(string source, string target) => source == target || source == "entity_id" && target == "string";
    public static string[] ExecutionPorts(JsonObject node)
    {
        var spec = Spec(node);
        var ports = new List<string>();
        if (Text(spec, "dynamicExecution") == "branches" && node["config"]?["branches"] is JsonArray branches)
            ports.AddRange(branches.Select(id => "branch:" + id?.ToString()));
        ports.AddRange((spec?["execution"] as JsonArray ?? []).Select(p => p!.GetValue<string>()));
        return ports.ToArray();
    }
    public static JsonObject DataOutputs(JsonObject node)
    {
        var spec = Spec(node);
        var ports = (spec?["outputs"]?.DeepClone() as JsonObject) ?? [];
        foreach (var (_, value) in ports) if (value is JsonObject port) port["type"] = ResolveType(Text(port, "type"), node);
        if (Text(spec, "dynamicOutputs") == "loop") foreach (var key in new[] { "carry", "exports" }) foreach (var item in Declarations(node, key))
            ports[$"{(key == "carry" ? "carry" : "export")}:{Text(item, "name")}"] = new JsonObject { ["type"] = Text(item, "type"), ["description"] = $"{key} value {Text(item, "name")}", ["visibility"] = key == "carry" ? "loop" : "outside" };
        return ports;
    }
    public static JsonObject DataInputs(JsonObject node, JsonObject? loopOwner = null)
    {
        var spec = Spec(node);
        var ports = (spec?["inputs"]?.DeepClone() as JsonObject) ?? [];
        foreach (var (_, value) in ports) if (value is JsonObject port && port["types"] is JsonArray types)
            port["types"] = new JsonArray(types.Select(t => JsonValue.Create(ResolveType(t!.GetValue<string>(), node))).ToArray<JsonNode?>());
        JsonObject Input(IEnumerable<string> types, bool required, string description) => new() { ["types"] = new JsonArray(types.Select(t => JsonValue.Create(t)).ToArray<JsonNode?>()), ["required"] = required, ["description"] = description };
        var all = Root["types"]!.AsObject().Select(p => p.Key).ToArray();
        var dynamic = Text(spec, "dynamicInputs");
        if (dynamic == "fields" && node["config"]?["fields"] is JsonObject fields)
            foreach (var (key, _) in fields) ports["field:" + key] = Input(all, true, "Object field " + key);
        if (dynamic == "items" && node["config"]?["items"] is JsonArray items)
            for (var i = 0; i < items.Count; i++) ports["item:" + i] = Input(all, true, "Array item " + i);
        if (dynamic == "loop" && loopOwner != null) foreach (var key in new[] { "carry", "exports" }) foreach (var item in Declarations(loopOwner, key))
            ports[$"{(key == "carry" ? "carry" : "export")}:{Text(item, "name")}"] = Input([Text(item, "type")], false, "Next " + key + " value");
        return ports;
    }
    public static JsonNode? InputValue(JsonObject node, string port)
    {
        var cfg = node["config"] as JsonObject;
        if (port.StartsWith("field:")) return (cfg?["fields"] as JsonObject)?[port[6..]];
        if (port.StartsWith("item:") && int.TryParse(port[5..], out var i) && cfg?["items"] is JsonArray items) return i >= 0 && i < items.Count ? items[i] : null;
        return Text(node, "kind") == "loop_result" ? (cfg?["values"] as JsonObject)?[port] : cfg?[port];
    }
}
