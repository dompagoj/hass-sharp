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
            ports[$"{(key == "carry" ? "carry" : "export")}:{Text(item, "name")}"] = new JsonObject { ["type"] = Text(item, "type"), ["description"] = (key == "carry" ? "Kept between iterations: " : "Available after the loop: ") + Text(item, "name"), ["visibility"] = key == "carry" ? "loop" : "outside" };
        if (Text(spec, "dynamicOutputs") == "trigger") foreach (var (key, value) in TriggerOutputs(node, snapshot?.Root ?? Root)) ports[key] = value?.DeepClone();
        // An output with "when" exists only while that boolean config is true (D3, D9).
        foreach (var key in ports.Where(p => Text(p.Value, "when") is { Length: > 0 } when && !((node["config"] as JsonObject)?[when] is JsonValue on && on.TryGetValue<bool>(out var enabled) && enabled)).Select(p => p.Key).ToArray())
            ports.Remove(key);
        return ports;
    }
    /// <summary>The C4 entry of the node's configured service, or null without a services section or for an unknown service.</summary>
    internal static JsonObject? ServiceSchema(JsonObject node, VisualCatalogSnapshot? snapshot) =>
        snapshot?.Services is { } services && (node["config"] as JsonObject)?["service"] is JsonValue id && id.TryGetValue<string>(out var service) ? services.Items[service] as JsonObject : null;
    /// <summary>Entity domains the service's target accepts; empty when any entity filter, or the target itself, is unrestricted by domain.</summary>
    internal static string[] ServiceTargetDomains(JsonObject schema)
    {
        var filters = (schema["target"]?["entity"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var domains = filters.Select(f => (f["domain"] as JsonArray ?? []).OfType<JsonValue>().Select(d => d.TryGetValue<string>(out var s) ? s : "").Where(s => s != "").ToArray()).ToArray();
        return domains.Length == 0 || domains.Any(d => d.Length == 0) ? [] : domains.SelectMany(d => d).Distinct().ToArray();
    }
    static readonly string[] ServiceFieldConstraints = ["min", "max", "integer", "enum", "enumLabels", "pattern", "minItems", "maxItems", "step", "unit", "selector", "advanced", "section", "example"];
    /// <summary>
    /// A known service field as an input spec, so catalog literal constraints (C2) apply to it. Its C4 default (a
    /// plain value) becomes an operand, the shape catalog inputs use: a literal of the first field type it is a valid
    /// value of; a default no field type accepts is left out.
    /// </summary>
    static JsonObject ServiceFieldInput(string key, JsonObject field, JsonObject catalog)
    {
        var port = new JsonObject
        {
            ["types"] = field["types"]?.DeepClone() ?? new JsonArray(), ["required"] = field["required"] is JsonValue r && r.TryGetValue<bool>(out var required) && required,
            ["label"] = Text(field, "label", key)
        };
        if (Text(field, "description") is { Length: > 0 } description) port["description"] = description;
        foreach (var name in ServiceFieldConstraints) if (field[name] is { } value) port[name] = value.DeepClone();
        if (field["default"] is { } fallback && (field["types"] as JsonArray ?? []).OfType<JsonValue>().Select(t => t.TryGetValue<string>(out var s) ? s : "")
                .FirstOrDefault(t => t != "" && VisualTypes.ValidLiteral(catalog, t, fallback, true)) is { } type)
            port["default"] = new JsonObject { ["kind"] = "literal", ["type"] = type, ["value"] = fallback.DeepClone() };
        return port;
    }
    /// <summary>
    /// Contract C6: the rows of <c>triggerOutputs</c> whose <c>|</c>-separated key lists the trigger type (or
    /// <c>ha:&lt;platform&gt;</c>), after <c>*</c>. Each port keeps its JSON pointer; <c>refineFrom</c> narrows its type.
    /// </summary>
    internal static JsonObject TriggerOutputs(JsonObject node, JsonObject catalog)
    {
        var config = node["config"] as JsonObject;
        var type = Text(config, "type", Text(catalog["nodes"]?[Text(node, "kind")]?["config"]?["type"], "default"));
        var native = config?["trigger"] as JsonObject;
        var key = type == "ha" ? "ha:" + (native?["trigger"] is JsonValue platform && platform.TryGetValue<string>(out var name) ? name : Text(native, "platform")) : type;
        var ports = new JsonObject();
        foreach (var (keys, rows) in catalog["triggerOutputs"] as JsonObject ?? [])
            if (rows is JsonObject outputs && (keys == "*" || keys.Split('|').Contains(key)))
                foreach (var (port, value) in outputs) if (value is JsonObject output)
                {
                    var copy = (JsonObject)output.DeepClone();
                    if (Text(output, "refineFrom") is { Length: > 0 } path && Refined(catalog, Text(output, "type"), ConfigValue(config, path)) is { } refined) copy["type"] = refined;
                    ports[port] = copy;
                }
        return ports;
    }
    static JsonNode? ConfigValue(JsonObject? config, string path) => path.Split('.').Aggregate((JsonNode?)config, (current, segment) => (current as JsonObject)?[segment]);
    /// <summary><c>&lt;type&gt;:&lt;param&gt;</c> when the value, or every item of a non-empty list, is a literal of that one parametric type.</summary>
    static string? Refined(JsonObject catalog, string type, JsonNode? value)
    {
        var items = value is JsonArray list ? list.ToArray() : [value];
        string? refined = null;
        foreach (var item in items)
        {
            if (!VisualTypes.ValidLiteral(catalog, type, item, true) || item is not JsonValue text || !text.TryGetValue<string>(out var literal) || literal.IndexOf('.') is not (> 0 and var dot)) return null;
            var candidate = type + ":" + literal[..dot];
            if (refined != null && refined != candidate || !VisualTypes.ValidLiteral(catalog, candidate, item, true)) return null;
            refined = candidate;
        }
        return refined;
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
        // A port without a description (an unknown service field) has none rather than one that repeats its label.
        JsonObject Input(IEnumerable<string> types, bool required, string? description)
        {
            var port = new JsonObject { ["types"] = new JsonArray(types.Select(t => JsonValue.Create(t)).ToArray<JsonNode?>()), ["required"] = required };
            if (description != null) port["description"] = description;
            return port;
        }
        var all = (snapshot?.Root ?? Root)["types"]!.AsObject().Select(p => p.Key).ToArray();
        var dynamic = Text(spec, "dynamicInputs");
        if (dynamic == "fields" && node["config"]?["fields"] is JsonObject fields)
            foreach (var (key, _) in fields) ports["field:" + key] = Input(all, true, "Field " + key);
        if (dynamic == "service")
        {
            // Ports are the keys of config.fields (D3); a known service only refines their types and the target's.
            var schema = ServiceSchema(node, snapshot);
            if (node["config"]?["fields"] is JsonObject serviceFields)
                foreach (var (key, _) in serviceFields)
                    ports["field:" + key] = schema?["fields"]?[key] is JsonObject field ? ServiceFieldInput(key, field, snapshot?.Root ?? Root) : Input(all, false, null);
            if (schema != null && ports["target"] is JsonObject target)
            {
                string[] types = ServiceTargetDomains(schema) is { Length: > 0 } domains ? ["target", .. domains.Select(d => "entity_id:" + d)] : ["target", "entity_id"];
                target["types"] = new JsonArray(types.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
            }
        }
        if (dynamic == "items" && node["config"]?["items"] is JsonArray items)
            // Labelled as in contract.ts: list items count from 1.
            for (var i = 0; i < items.Count; i++) { var item = Input(all, true, "Item " + (i + 1)); item["label"] = "Item " + (i + 1); ports["item:" + i] = item; }
        if (dynamic == "loop" && loopOwner != null) foreach (var key in new[] { "carry", "exports" }) foreach (var item in Declarations(loopOwner, key))
        {
            var port = Input([Text(item, "type")], false, "New value for " + Text(item, "name")); port["label"] = Text(item, "name");
            ports[$"{(key == "carry" ? "carry" : "export")}:{Text(item, "name")}"] = port;
        }
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
