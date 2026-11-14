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
            .Where(t => Text(t.Value, "literalKind") != "" || t.Value?["nullable"] is JsonValue nullable && nullable.TryGetValue<bool>(out var on) && on)
            .Select(t => (JsonNode?)JsonValue.Create(t.Key)).ToArray());
    }
    public static JsonObject GetCatalog(VisualCatalogSnapshot? snapshot = null) => (JsonObject)(snapshot?.Root ?? Root).DeepClone();
    public static JsonObject? GetNode(string kind, VisualCatalogSnapshot? snapshot = null) => (snapshot?.Root ?? Root)["nodes"]?[kind]?.DeepClone() as JsonObject;
    internal static JsonObject? Spec(JsonObject node, VisualCatalogSnapshot? snapshot = null) => (snapshot?.Root ?? Root)["nodes"]?[Text(node, "kind")] as JsonObject;
    internal static string Text(JsonNode? node, string key, string fallback = "") => node is JsonObject obj && obj[key] is JsonValue v && v.TryGetValue<string>(out var result) ? result : fallback;
    /// <summary>A value node (<c>"pure": true</c>): no execution pins, worked out each time a step that reads it runs.</summary>
    internal static bool IsPure(JsonNode? spec) => spec is JsonObject obj && obj["pure"] is JsonValue v && v.TryGetValue<bool>(out var pure) && pure;
    /// <summary>Whether this node is a value node: by its kind, or for a Custom C# node by its signature.</summary>
    internal static bool IsPure(JsonObject node, JsonNode? spec) => Text(spec, "dynamicInputs") == "custom" ? CustomSignature(node, spec)?["pure"] is JsonValue v && v.TryGetValue<bool>(out var pure) && pure : IsPure(spec);
    /// <summary>
    /// The pins of a Custom C# node, as the compiler derived them from its code, when the node's kind takes its pins
    /// from <c>config.signature</c> and that signature is well formed; else null. Mirrored by <c>customSignature</c> in
    /// <c>contract.ts</c>.
    /// </summary>
    internal static JsonObject? CustomSignature(JsonObject node, JsonNode? spec) =>
        Text(spec, "dynamicInputs") == "custom" && node["config"] is JsonObject config && config["signature"] is JsonObject signature && WellFormedSignature(signature) ? signature : null;
    /// <summary>
    /// A signature has a text <c>title</c> and <c>description</c>, a true/false <c>pure</c>, and <c>inputs</c>, <c>outputs</c>
    /// and <c>types</c> objects; each input has a non-empty list of type ids and a true/false <c>required</c>, each output a
    /// type id, each pin an identifier for its id, each type an object.
    /// </summary>
    internal static bool WellFormedSignature(JsonObject signature)
    {
        static bool Is<T>(JsonNode? value) => value is JsonValue v && v.TryGetValue<T>(out _);
        const string pin = "^[A-Za-z_][A-Za-z0-9_]*$";
        if (!Is<string>(signature["title"]) || !Is<string>(signature["description"]) || !Is<bool>(signature["pure"])) return false;
        if (signature["inputs"] is not JsonObject inputs || signature["outputs"] is not JsonObject outputs || signature["types"] is not JsonObject types) return false;
        foreach (var (id, input) in inputs)
            if (!VisualTypes.Matches(id, pin) || input is not JsonObject rule || rule["types"] is not JsonArray list || list.Count == 0 || !list.All(Is<string>) || !Is<bool>(rule["required"])) return false;
        foreach (var (id, output) in outputs)
            if (!VisualTypes.Matches(id, pin) || output is not JsonObject port || !Is<string>(port["type"])) return false;
        return types.All(t => t.Value is JsonObject);
    }
    /// <summary>
    /// The catalog with the types of the graph's Custom C# nodes added (C# and TS alike): each well-formed signature's
    /// <c>types</c> whose id the catalog does not have. Each node lists what its own compile knows a type converts to and
    /// from, so the entries of one id are merged: the first node's entry, with <c>assignableTo</c> and
    /// <c>assignableFrom</c> the union of every node's (in node order). The order of the nodes never changes what is
    /// accepted. The catalog itself when there are none, and for a graph past the catalog's limits: the validator answers
    /// that graph with <c>graph.limit</c> alone, and nothing is merged or resolved for it, whoever asks.
    /// </summary>
    internal static JsonObject WithGraphTypes(JsonObject catalog, JsonObject graph)
    {
        if (OverLimit(catalog, graph)) return catalog;
        var known = catalog["types"] as JsonObject ?? [];
        var added = new JsonObject();
        foreach (var node in graph["nodes"] as JsonArray ?? [])
            if (node is JsonObject n && CustomSignature(n, catalog["nodes"]?[Text(n, "kind")])?["types"] is JsonObject types)
                foreach (var (id, entry) in types)
                {
                    if (known.ContainsKey(id) || entry is not JsonObject next) continue;
                    if (added[id] is not JsonObject first) { added[id] = next.DeepClone(); continue; }
                    foreach (var key in (string[])["assignableTo", "assignableFrom"])
                        if (next[key] is JsonArray more)
                        {
                            if (first[key] is not JsonArray list) first[key] = list = [];
                            foreach (var item in more)
                                if (item is JsonValue v && v.TryGetValue<string>(out var target) && !list.Any(t => t is JsonValue e && e.TryGetValue<string>(out var have) && have == target))
                                    list.Add(target);
                        }
                }
        var selecting = SelectingNodes(catalog, graph);
        if (added.Count == 0 && selecting.Count == 0) return catalog;
        var merged = (JsonObject)catalog.DeepClone();
        foreach (var (id, entry) in added.ToArray()) { added.Remove(id); merged["types"]![id] = entry; }
        if (selecting.Count > 0) ResolveOutputs(merged, graph);
        return merged;
    }

    /// <summary>
    /// Whether the graph has more nodes or more value and step order wires than the catalog's <c>limits</c> allow (the
    /// validator's <c>graph.limit</c>). Mirrored by <c>overLimit</c> in <c>contract.ts</c>.
    /// </summary>
    internal static bool OverLimit(JsonObject catalog, JsonObject graph) =>
        graph["nodes"] is JsonArray nodes && nodes.Count > Limit(catalog, "nodes", 256) || graph["edges"] is JsonArray edges && edges.Count > Limit(catalog, "edges", 512);
    static int Limit(JsonObject catalog, string key, int fallback) =>
        catalog["limits"]?[key] is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number && int.TryParse(v.ToJsonString(), out var limit) ? limit : fallback;
    /// <summary>
    /// An output whose type is that of a property of the value at one of the node's inputs (<c>"propertyOf": "&lt;input&gt;"</c>):
    /// the property named by the node's <c>config.property</c>, of the type the wire into that input brings.
    /// </summary>
    internal static string PropertyOf(JsonNode? output) => Text(output, "propertyOf");
    /// <summary>The well-formed nodes of a graph by id (the first of an id), as both validators read them.</summary>
    internal static Dictionary<string, JsonObject> GraphNodes(JsonObject graph)
    {
        var nodes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var raw in graph["nodes"] as JsonArray ?? [])
            if (raw is JsonObject node && Text(node, "id") != "" && node["config"] is JsonObject) nodes.TryAdd(Text(node, "id"), node);
        return nodes;
    }
    static List<JsonObject> SelectingNodes(JsonObject catalog, JsonObject graph) =>
        GraphNodes(graph).Values.Where(n => (catalog["nodes"]?[Text(n, "kind")]?["outputs"] as JsonObject ?? []).Any(o => PropertyOf(o.Value) != "")).ToList();
    /// <summary>Whether the kind's input takes a value whose type has properties (<c>$properties</c>): a value of exactly such a type, by its own entry.</summary>
    internal static bool SelectsProperties(JsonNode? spec, string port) => (spec?["inputs"]?[port]?["types"] as JsonArray ?? []).Any(t => t is JsonValue v && v.TryGetValue<string>(out var s) && s == "$properties");
    /// <summary>The members a value of the type has, as its catalog entry lists them; empty for a type without any.</summary>
    internal static JsonObject[] TypeProperties(JsonObject catalog, string type) => (((catalog["types"] as JsonObject)?[type] as JsonObject)?["properties"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
    /// <summary>
    /// The type of each <c>propertyOf</c> output of the graph, under <c>resolvedOutputs</c> (node id, then port): the
    /// type of the chosen property of the type the first value wire into its input brings. A node reading another such
    /// node is resolved after it: each waits for the ones it reads, and is resolved once none is left (in the order the
    /// graph lists the ready ones), so no call goes one level deeper per node, however long the chain. A node on a cycle
    /// of such nodes, or reading one, is never ready and has none; so has an output whose input, property or source type
    /// is missing. Mirrored by <c>resolveOutputs</c> in <c>contract.ts</c>.
    /// </summary>
    internal static void ResolveOutputs(JsonObject catalog, JsonObject graph)
    {
        var resolved = new JsonObject(); catalog["resolvedOutputs"] = resolved;
        var nodes = GraphNodes(graph);
        var incoming = new Dictionary<(string, string), JsonObject>();
        foreach (var raw in graph["edges"] as JsonArray ?? [])
            if (raw is JsonObject edge && Text(edge, "id") != "" && Text(edge, "kind") == "data" && !edge.ContainsKey("port") && Text(edge, "sourcePort") != "" && Text(edge, "targetPort") != "" &&
                nodes.ContainsKey(Text(edge, "source")) && nodes.ContainsKey(Text(edge, "target")))
                incoming.TryAdd((Text(edge, "target"), Text(edge, "targetPort")), edge);
        // Each propertyOf output and the wire into its input.
        List<(string Port, JsonObject Edge)> Wires(JsonObject node)
        {
            var wires = new List<(string, JsonObject)>();
            foreach (var (port, output) in catalog["nodes"]?[Text(node, "kind")]?["outputs"] as JsonObject ?? [])
                if (PropertyOf(output) is { Length: > 0 } input && incoming.TryGetValue((Text(node, "id"), input), out var edge)) wires.Add((port, edge));
            return wires;
        }
        var selecting = SelectingNodes(catalog, graph);
        var ids = selecting.Select(n => Text(n, "id")).ToHashSet(StringComparer.Ordinal);
        var waiting = new Dictionary<string, int>(StringComparer.Ordinal);
        var readers = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        foreach (var node in selecting)
        {
            var sources = Wires(node).Select(w => Text(w.Edge, "source")).Where(ids.Contains).ToHashSet(StringComparer.Ordinal);
            waiting[Text(node, "id")] = sources.Count;
            foreach (var source in sources) { if (!readers.TryGetValue(source, out var list)) readers[source] = list = []; list.Add(node); }
        }
        var ready = new Queue<JsonObject>(selecting.Where(n => waiting[Text(n, "id")] == 0));
        while (ready.TryDequeue(out var node))
        {
            var id = Text(node, "id");
            foreach (var (port, edge) in Wires(node))
            {
                var type = Text(DataOutputs(nodes[Text(edge, "source")], catalog)[Text(edge, "sourcePort")], "type");
                if (TypeProperties(catalog, type).FirstOrDefault(p => Text(p, "name") == Text(node["config"], "property")) is { } property && Text(property, "type") is { Length: > 0 } found)
                    ((resolved[id] ??= new JsonObject()) as JsonObject)![port] = found;
            }
            foreach (var reader in readers.GetValueOrDefault(id) ?? []) if (--waiting[Text(reader, "id")] == 0) ready.Enqueue(reader);
        }
    }
    internal static IEnumerable<JsonObject> Declarations(JsonObject node, string key) => (node["config"]?[key] as JsonArray ?? []).OfType<JsonObject>();
    internal static string ResolveType(string type, JsonObject node) => type.StartsWith('$') ? Text(node["config"], type[1..]) : type;
    public static string EffectiveTriggerMethod(JsonObject node) => "Run_" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(Text(node, "id")));
    internal static string TriggerType(JsonObject node, VisualCatalogSnapshot snapshot) => Text(node["config"], "type", Text(Spec(node, snapshot)?["config"]?["type"], "default"));
    public static bool CompatibleTypes(string source, string target, VisualCatalogSnapshot? snapshot = null) => (snapshot ?? VisualCatalogSnapshot.BuiltIn).IsAssignable(source, target);
    public static string[] ExecutionPorts(JsonObject node, VisualCatalogSnapshot? snapshot = null)
    {
        var spec = Spec(node, snapshot);
        var ports = new List<string>();
        if (IsPure(node, spec)) return [];
        if (Text(spec, "dynamicExecution") == "branches" && node["config"]?["branches"] is JsonArray branches)
            // Only a text id names a branch, as in TypeScript; another value is reported by the validators and has no port.
            ports.AddRange(branches.OfType<JsonValue>().Select(id => id.TryGetValue<string>(out var branch) ? branch : null).OfType<string>().Select(id => "branch:" + id));
        ports.AddRange((spec?["execution"] as JsonArray ?? []).Select(p => p!.GetValue<string>()));
        return ports.ToArray();
    }
    public static JsonObject DataOutputs(JsonObject node, VisualCatalogSnapshot? snapshot = null) => DataOutputs(node, snapshot?.Root ?? Root);
    /// <summary>
    /// The node's value outputs under <paramref name="catalog"/>. A <c>propertyOf</c> output has the type the graph's
    /// catalog resolved for it (<see cref="WithGraphTypes"/>), and none (an empty type) until it is resolved.
    /// </summary>
    internal static JsonObject DataOutputs(JsonObject node, JsonObject catalog)
    {
        var spec = catalog["nodes"]?[Text(node, "kind")] as JsonObject;
        var ports = (spec?["outputs"]?.DeepClone() as JsonObject) ?? [];
        foreach (var (key, value) in ports) if (value is JsonObject port)
            port["type"] = PropertyOf(port) != "" ? Text(catalog["resolvedOutputs"]?[Text(node, "id")], key) : ResolveType(Text(port, "type"), node);
        if (Text(spec, "dynamicOutputs") == "custom" && CustomSignature(node, spec)?["outputs"] is JsonObject custom) foreach (var (key, value) in custom) ports[key] = value?.DeepClone();
        if (Text(spec, "dynamicOutputs") == "loop") foreach (var key in new[] { "carry", "exports" }) foreach (var item in Declarations(node, key))
            ports[$"{(key == "carry" ? "carry" : "export")}:{Text(item, "name")}"] = new JsonObject { ["type"] = Text(item, "type"), ["description"] = (key == "carry" ? "Kept between iterations: " : "Available after the loop: ") + Text(item, "name"), ["visibility"] = key == "carry" ? "loop" : "outside" };
        if (Text(spec, "dynamicOutputs") == "trigger") foreach (var (key, value) in TriggerOutputs(node, catalog)) ports[key] = value?.DeepClone();
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
        // $any is every type; $properties every type whose values have properties (a Select property node's input).
        foreach (var (_, value) in ports) if (value is JsonObject port && port["types"] is JsonArray types)
            port["types"] = new JsonArray(types.SelectMany(t => t!.GetValue<string>() switch
            {
                "$any" => (snapshot?.Root ?? Root)["types"]!.AsObject().Select(p => p.Key),
                "$properties" => (snapshot?.Root ?? Root)["types"]!.AsObject().Where(p => (p.Value as JsonObject)?["properties"] is JsonArray { Count: > 0 }).Select(p => p.Key),
                var type => [ResolveType(type, node)],
            }).Select(t => JsonValue.Create(t)).ToArray<JsonNode?>());
        if (Text(node,"kind") == "foreach" && ports["items"] is JsonObject itemsPort)
            itemsPort["types"] = new JsonArray(new[] { "array" }.Concat((snapshot?.Root ?? Root)["types"]!.AsObject()
                .Where(p => !VisualTypes.Nullable(snapshot?.Root ?? Root, p.Key) && Text(p.Value, "elementType") is { Length: > 0 } element && CompatibleTypes(element, Text(node["config"],"itemType"), snapshot)).Select(p=>p.Key))
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
        if (dynamic == "custom" && CustomSignature(node, spec)?["inputs"] is JsonObject custom) foreach (var (key, value) in custom) ports[key] = value?.DeepClone();
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
