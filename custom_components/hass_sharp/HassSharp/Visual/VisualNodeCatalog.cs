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
        var root = JsonNode.Parse(stream)!.AsObject();
        RefreshStructuralTypes(root);
        return root;
    }
    internal static void RefreshStructuralTypes(JsonObject catalog)
    {
        var types = catalog["types"]!.AsObject();
        // Loop declarations accept every resolvable type id, parametric ones included, so Convert can narrow.
        catalog["structures"]!["loopDeclaration"]!["type"] = new JsonObject { ["type"] = "string", ["enumSource"] = "types", ["required"] = true };
        catalog["structures"]!["operand"]!["literal"]!["types"] = new JsonArray(types
            .Where(t => Text(t.Value, "literalKind") != "" || t.Value?["nullable"]?.GetValue<bool>() == true)
            .Select(t => (JsonNode?)JsonValue.Create(t.Key)).ToArray());
    }
    public static JsonObject GetCatalog(VisualCatalogSnapshot? snapshot = null) => (JsonObject)(snapshot?.Root ?? Root).DeepClone();
    public static JsonObject? GetNode(string kind, VisualCatalogSnapshot? snapshot = null) => (snapshot?.Root ?? Root)["nodes"]?[kind]?.DeepClone() as JsonObject;
    internal static JsonObject? Spec(JsonObject node, VisualCatalogSnapshot? snapshot = null) => (snapshot?.Root ?? Root)["nodes"]?[Text(node, "kind")] as JsonObject;
    internal static string Text(JsonNode? node, string key, string fallback = "") => node is JsonObject obj && obj[key] is JsonValue v && v.TryGetValue<string>(out var result) ? result : fallback;
    internal static IEnumerable<JsonObject> Declarations(JsonObject node, string key) => (node["config"]?[key] as JsonArray ?? []).OfType<JsonObject>();
    internal static string ResolveType(string type, JsonObject node) => type.StartsWith('$') ? Text(node["config"], type[1..]) : type;
    public static string EffectiveTriggerMethod(JsonObject node) => "Run_" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(Text(node, "id")));
    internal static string TriggerType(JsonObject node, VisualCatalogSnapshot snapshot) => Text(node["config"], "type", Text(Spec(node, snapshot)?["config"]?["type"], "default"));
    public static bool CompatibleTypes(string source, string target, VisualCatalogSnapshot? snapshot = null) => (snapshot ?? VisualCatalogSnapshot.BuiltIn).IsAssignable(source, target);
    public static string[] ExecutionPorts(JsonObject node, VisualCatalogSnapshot? snapshot = null)
    {
        var spec = Spec(node, snapshot);
        var ports = new List<string>();
        if (Text(spec, "dynamicExecution") == "branches" && node["config"]?["branches"] is JsonArray branches)
            ports.AddRange(branches.Select(id => "branch:" + id?.ToString()));
        ports.AddRange((spec?["execution"] as JsonArray ?? []).Select(p => p!.GetValue<string>()));
        return ports.ToArray();
    }
    public static JsonObject DataOutputs(JsonObject node, VisualCatalogSnapshot? snapshot = null)
    {
        var spec = Spec(node, snapshot);
        var ports = (spec?["outputs"]?.DeepClone() as JsonObject) ?? [];
        foreach (var (_, value) in ports) if (value is JsonObject port) port["type"] = ResolveType(Text(port, "type"), node);
        if (Text(spec, "dynamicOutputs") == "loop") foreach (var key in new[] { "carry", "exports" }) foreach (var item in Declarations(node, key))
            ports[$"{(key == "carry" ? "carry" : "export")}:{Text(item, "name")}"] = new JsonObject { ["type"] = Text(item, "type"), ["description"] = $"{key} value {Text(item, "name")}", ["visibility"] = key == "carry" ? "loop" : "outside" };
        return ports;
    }
    public static JsonObject DataInputs(JsonObject node, JsonObject? loopOwner = null, VisualCatalogSnapshot? snapshot = null)
    {
        var spec = Spec(node, snapshot);
        var ports = (spec?["inputs"]?.DeepClone() as JsonObject) ?? [];
        foreach (var (_, value) in ports) if (value is JsonObject port && port["types"] is JsonArray types)
            port["types"] = new JsonArray(types.SelectMany(t => t!.GetValue<string>() == "$any" ? (snapshot?.Root ?? Root)["types"]!.AsObject().Select(p => p.Key) : new[] { ResolveType(t!.GetValue<string>(), node) }).Select(t => JsonValue.Create(t)).ToArray<JsonNode?>());
        if (Text(node,"kind") == "foreach" && ports["items"] is JsonObject itemsPort)
            itemsPort["types"] = new JsonArray(new[] { "array" }.Concat((snapshot?.Root ?? Root)["types"]!.AsObject()
                .Where(p => p.Value?["nullable"]?.GetValue<bool>() != true && p.Value?["elementType"] is JsonValue element && CompatibleTypes(element.GetValue<string>(), Text(node["config"],"itemType"), snapshot)).Select(p=>p.Key))
                .Select(t=>(JsonNode?)JsonValue.Create(t)).ToArray());
        JsonObject Input(IEnumerable<string> types, bool required, string description) => new() { ["types"] = new JsonArray(types.Select(t => JsonValue.Create(t)).ToArray<JsonNode?>()), ["required"] = required, ["description"] = description };
        var all = (snapshot?.Root ?? Root)["types"]!.AsObject().Select(p => p.Key).ToArray();
        var dynamic = Text(spec, "dynamicInputs");
        if (dynamic == "fields" && node["config"]?["fields"] is JsonObject fields)
            foreach (var (key, _) in fields) ports["field:" + key] = Input(all, true, "Object field " + key);
        if (dynamic == "items" && node["config"]?["items"] is JsonArray items)
            for (var i = 0; i < items.Count; i++) ports["item:" + i] = Input(all, true, "Array item " + i);
        if (dynamic == "loop" && loopOwner != null) foreach (var key in new[] { "carry", "exports" }) foreach (var item in Declarations(loopOwner, key))
            ports[$"{(key == "carry" ? "carry" : "export")}:{Text(item, "name")}"] = Input([Text(item, "type")], false, "Next " + key + " value");
        return ports;
    }
    public static JsonNode? InputValue(JsonObject node, string port, VisualCatalogSnapshot? snapshot = null) => TryGetInputValue(node, port, out var value, snapshot) ? value : null;
    /// <summary>Returns property presence separately because an explicit JSON null is not an absent operand.</summary>
    public static bool TryGetInputValue(JsonObject node, string port, out JsonNode? value, VisualCatalogSnapshot? snapshot = null)
    {
        value = null;
        var cfg = node["config"] as JsonObject;
        if (Text(Spec(node, snapshot), "argumentStorage") == "arguments") return (cfg?["arguments"] as JsonObject)?.TryGetPropertyValue(port, out value) ?? false;
        if (port.StartsWith("field:")) return (cfg?["fields"] as JsonObject)?.TryGetPropertyValue(port[6..], out value) ?? false;
        if (port.StartsWith("item:") && int.TryParse(port[5..], out var i) && cfg?["items"] is JsonArray items)
        {
            if (i < 0 || i >= items.Count) return false;
            value = items[i]; return true;
        }
        return Text(node, "kind") == "loop_result"
            ? (cfg?["values"] as JsonObject)?.TryGetPropertyValue(port, out value) ?? false
            : cfg?.TryGetPropertyValue(port, out value) ?? false;
    }
    internal static bool ValidLiteral(string type, JsonNode? value, bool present, VisualCatalogSnapshot snapshot) => ValidLiteral(type, value, present, snapshot.Root);
    internal static bool ValidLiteral(string type, JsonNode? value, bool present, JsonObject catalog) => VisualTypes.ValidLiteral(catalog, type, value, present);

}
