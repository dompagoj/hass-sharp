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
    static bool Flag(JsonNode? spec, string key) => spec is JsonObject obj && obj[key] is JsonValue v && v.TryGetValue<bool>(out var on) && on;
    /// <summary>
    /// Whether the node starts a flow of its own, from which scopes and reachability start: a trigger, or a kind whose
    /// entry says <c>"root": true</c> (an entity definition's When started and Entity node). Mirrored by
    /// <c>isRoot</c> in <c>contract.ts</c>.
    /// </summary>
    internal static bool IsRoot(JsonObject node, VisualCatalogSnapshot? snapshot) => Text(node, "kind") == "trigger" || Flag(Spec(node, snapshot), "root");
    /// <summary>Whether a graph may hold at most one node of the kind (<c>"singleton": true</c>).</summary>
    internal static bool IsSingleton(JsonNode? spec) => Flag(spec, "singleton");
    /// <summary>Whether a node of the kind declares one of its definition's <paramref name="what"/> (<c>"declares": "entity"</c>).</summary>
    internal static bool Declares(JsonNode? spec, string what) => Text(spec, "declares") == what;
    /// <summary>
    /// Whether a node of the kind is a declaration (<c>"declaration": true</c>, a Device node): neither a step nor a value. It
    /// has no execution pins, no step order wire goes into or out of it, and it never runs. Mirrored by <c>isDeclaration</c>
    /// in <c>contract.ts</c>.
    /// </summary>
    internal static bool Declaration(JsonObject? spec) => Flag(spec, "declaration");
    /// <summary>
    /// Whether the kind is an Entity node, the hub of its entity's flows: a root that declares an entity. A C# entity
    /// declares one too, but its code is its behaviour: it has no flows, state values or commands. Mirrored by <c>isHub</c>
    /// in <c>contract.ts</c>.
    /// </summary>
    internal static bool IsHub(JsonNode? spec) => Flag(spec, "root") && Declares(spec, "entity");
    /// <summary>
    /// Whether the kind is a class node (<c>"dynamicInputs": "settings"</c>, a C# entity, a C# device or a library's class):
    /// it is one class, and its pins after its kind's own are the class's settings (<see cref="ClassSignature"/>). Mirrored
    /// by <c>isClassNode</c> in <c>contract.ts</c>.
    /// </summary>
    internal static bool IsClassNode(JsonNode? spec) => Text(spec, "dynamicInputs") == "settings";
    /// <summary>
    /// Whether a node of the kind holds C# code: a Custom C# step, or a class node whose entry has no
    /// <c>classSignature</c> (a C# entity or C# device; a library's class is the library's code). Mirrored by
    /// <c>hasCode</c> in <c>contract.ts</c>.
    /// </summary>
    internal static bool HasCode(JsonNode? spec) => Text(spec, "dynamicInputs") switch
    {
        "custom" => true,
        "settings" => spec?["classSignature"] == null,
        _ => false,
    };
    /// <summary>
    /// The pin type of a class's setting, from its signature's <c>type</c> (and an entity's <c>domain</c>): text for a string
    /// or an enum, a number for an integer or a number, true or false for a boolean, an entity id for an entity, and an
    /// entity id of the domain (<c>entity_id:light</c>) for an entity of one; null for a type it does not know. The one
    /// place a setting's type becomes a pin type. Mirrored by <c>settingPinType</c> in <c>contract.ts</c>.
    /// </summary>
    internal static string? SettingPinType(JsonObject setting) => setting["type"] is JsonValue t && t.TryGetValue<string>(out var type) ? type switch
    {
        "string" or "enum" => "string",
        "integer" or "number" => "number",
        "boolean" => "boolean",
        "entity" => setting["domain"] is JsonValue d && d.TryGetValue<string>(out var domain) ? "entity_id:" + domain : "entity_id",
        _ => null,
    } : null;
    /// <summary>
    /// A class node's signature (<c>config.signature</c>) has a text <c>title</c> and <c>description</c>, a <c>domain</c>
    /// that is a text or null, and <c>settings</c>, a list. Each setting has an identifier for its <c>key</c>, no key twice,
    /// a text <c>label</c> and <c>description</c>, a <c>type</c> of string, integer, number, boolean, enum or entity, a
    /// true/false <c>secret</c>, and may have <c>options</c>, a list of text, a number <c>minimum</c> and <c>maximum</c>,
    /// a true/false <c>required</c> and, an entity only, a <c>domain</c> that is a domain's word (lower-case letters,
    /// digits and underscores). Mirrored by <c>wellFormedClassSignature</c> in <c>contract.ts</c>.
    /// </summary>
    internal static bool WellFormedClassSignature(JsonNode? signature)
    {
        static bool Is<T>(JsonNode? value) => value is JsonValue v && v.TryGetValue<T>(out _);
        static bool Number(JsonNode? value) => value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number;
        if (signature is not JsonObject s || !Is<string>(s["title"]) || !Is<string>(s["description"])) return false;
        if (!s.TryGetPropertyValue("domain", out var domain) || domain != null && !Is<string>(domain)) return false;
        if (s["settings"] is not JsonArray settings) return false;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in settings)
            if (raw is not JsonObject setting || setting["key"] is not JsonValue k || !k.TryGetValue<string>(out var key) || !VisualTypes.Matches(key, "^[A-Za-z_][A-Za-z0-9_]*$") || !keys.Add(key)
                || !Is<string>(setting["label"]) || !Is<string>(setting["description"]) || !Is<bool>(setting["secret"])
                || SettingPinType(setting) == null
                || setting.TryGetPropertyValue("options", out var options) && (options is not JsonArray list || !list.All(Is<string>))
                || setting.TryGetPropertyValue("minimum", out var minimum) && !Number(minimum)
                || setting.TryGetPropertyValue("maximum", out var maximum) && !Number(maximum)
                || setting.TryGetPropertyValue("required", out var required) && !Is<bool>(required)
                || setting.TryGetPropertyValue("domain", out var entityDomain) && (Text(setting, "type") != "entity" || entityDomain is not JsonValue d || !d.TryGetValue<string>(out var word) || !VisualTypes.Matches(word, "^[a-z0-9_]+$")))
                return false;
        return true;
    }
    /// <summary>
    /// A class node's signature, when the node's kind is a class node's (<see cref="IsClassNode"/>), its signature is well
    /// formed (<see cref="WellFormedClassSignature"/>) and no setting has the key of one of its kind's own pins; else null.
    /// A library class's is its kind's entry's (<c>classSignature</c>), whatever the node's config holds; a C# entity's or
    /// C# device's is its config's. Mirrored by <c>classSignature</c> in <c>contract.ts</c>.
    /// </summary>
    internal static JsonObject? ClassSignature(JsonObject node, JsonNode? spec) =>
        IsClassNode(spec) && (spec?["classSignature"] is { } entry ? entry : node["config"] is JsonObject config ? config["signature"] : null) is JsonObject signature
            && WellFormedClassSignature(signature) && !signature["settings"]!.AsArray().Any(s => (spec?["inputs"] as JsonObject)?.ContainsKey(Text(s, "key")) == true) ? signature : null;
    /// <summary>
    /// The pin of a class's setting: of the setting's pin type (<see cref="SettingPinType"/>), required when the signature
    /// says so (else optional), labelled and described as the setting is, with the setting's type, secret flag, options,
    /// range, entity domain and whether it is required under <c>setting</c>. Mirrored by <c>settingInput</c> in
    /// <c>contract.ts</c>.
    /// </summary>
    static JsonObject SettingInput(JsonObject setting)
    {
        var about = new JsonObject { ["type"] = Text(setting, "type"), ["secret"] = setting["secret"]!.DeepClone() };
        foreach (var member in new[] { "options", "minimum", "maximum", "domain", "required" }) if (setting.TryGetPropertyValue(member, out var value)) about[member] = value?.DeepClone();
        return new JsonObject
        {
            ["types"] = new JsonArray(JsonValue.Create(SettingPinType(setting))), ["required"] = setting["required"] is JsonValue r && r.TryGetValue<bool>(out var required) && required,
            ["label"] = Text(setting, "label"), ["description"] = Text(setting, "description"), ["setting"] = about,
        };
    }
    /// <summary>
    /// Whether a type or an input is structural (<c>"structural": true</c>, a device): a wire of it says what belongs to what
    /// and carries no value. No list of every type has a structural type, and nothing that reads an Entity node's state
    /// values reads a structural input. Mirrored by <c>structural</c> in <c>contract.ts</c>.
    /// </summary>
    internal static bool Structural(JsonNode? typeOrInput) => Flag(typeOrInput, "structural");
    /// <summary>
    /// The node's data inputs (<see cref="DataInputs"/> under <paramref name="catalog"/>) without the structural ones and
    /// the pictures it shows (<see cref="PictureInput"/>): an Entity node's or a Set state's state values. A class node has none: its pins are its settings, and its code gives its
    /// state. Mirrored by <c>stateInputs</c> in <c>contract.ts</c>.
    /// </summary>
    internal static IEnumerable<KeyValuePair<string, JsonNode?>> StateInputs(JsonObject node, JsonObject catalog) => StateInputs(node, VisualCatalogSnapshot.BuiltIn.WithRoot(catalog));
    /// <summary>The node's state values (<see cref="StateInputs(JsonObject, JsonObject)"/>) under the catalog of <paramref name="snapshot"/>.</summary>
    internal static IEnumerable<KeyValuePair<string, JsonNode?>> StateInputs(JsonObject node, VisualCatalogSnapshot snapshot) =>
        IsClassNode(Spec(node, snapshot)) ? [] : DataInputs(node, null, snapshot).Where(p => !Structural(p.Value) && !PictureInput(p.Value)).ToList();
    /// <summary>
    /// Whether an input is a picture an Entity node shows (<c>"picture": true</c>, a camera's Image, from its type's
    /// <c>picture</c> row): no state value, it is read in the node's frame flow alone. Mirrored by <c>pictureInput</c> in
    /// <c>contract.ts</c>.
    /// </summary>
    internal static bool PictureInput(JsonNode? input) => Flag(input, "picture");
    /// <summary>
    /// The pictures an Entity node shows (<see cref="PictureInput"/>): a camera's Image, which its frame flow reads each
    /// time a frame is needed; none for any other node. Mirrored by <c>pictureInputs</c> in <c>contract.ts</c>.
    /// </summary>
    internal static IEnumerable<KeyValuePair<string, JsonNode?>> PictureInputs(JsonObject node, VisualCatalogSnapshot snapshot) =>
        DataInputs(node, null, snapshot).Where(p => PictureInput(p.Value)).ToList();
    /// <summary>
    /// The entity an Entity node declares, as a definition lists one (the shape <see cref="SharpDomains.Create"/> reads):
    /// a copy of its settings without its run history and state values, and without a name that is blank once trimmed
    /// (no name: Home Assistant, the messages and the run names see none); empty when its config is not an object.
    /// Mirrored by <c>entityDescription</c> in <c>contract.ts</c>.
    /// </summary>
    internal static JsonObject EntityDescription(JsonObject node)
    {
        if (node["config"] is not JsonObject config) return [];
        var entity = (JsonObject)config.DeepClone();
        entity.Remove("history"); entity.Remove("arguments");
        if (entity["name"] is JsonValue name && name.TryGetValue<string>(out var text) && VisualGraphValidator.Words.Trimmed(text).Length == 0) entity.Remove("name");
        return entity;
    }
    /// <summary>
    /// The entities the graph's Entity nodes declare, each with its node's id, in the order of the graph's <c>nodes</c>;
    /// none for a graph of the wrong shape. A class node's entity (a C# entity's, a library's entity class's) is its class's:
    /// the definition's entities, which steps name and the host builds, are its Entity nodes', and a class node is listed
    /// for reading only (<see cref="CSharpEntities"/>). Every other kind that declares an entity is named here on purpose:
    /// one this does not know is refused, never copied as it is (its config may hold code). Mirrored by
    /// <c>declaredEntities</c> in <c>contract.ts</c>.
    /// </summary>
    internal static IEnumerable<(string NodeId, JsonObject Entity)> DeclaredEntities(JsonObject graph, JsonObject catalog)
    {
        foreach (var raw in graph["nodes"] as JsonArray ?? [])
        {
            if (raw is not JsonObject node || catalog["nodes"]?[Text(node, "kind")] is not { } spec || !Declares(spec, "entity") || IsClassNode(spec)) continue;
            if (Text(node, "kind") == "entity") yield return (Text(node, "id"), EntityDescription(node));
            else throw new InvalidOperationException($"The kind {Text(node, "kind")} declares an entity that no list knows how to describe");
        }
    }
    /// <summary>
    /// The devices the graph's device nodes declare (<c>"declares": "device"</c>), each with its node's id, in the order of
    /// the graph's <c>nodes</c>: a Device node's the members of a device its config has (<see cref="DeviceMembers"/>), as it
    /// has them, and no other; a class node's (a C# device's, a library's device class's) the key, name and Part of its
    /// config has, as it has them, marked <c>csharp</c> (<see cref="IsCSharpDevice"/>), and never its code, settings or
    /// their values. Each is empty of what its config does not have, all of it when the config is not an object. None for
    /// a graph of the wrong shape. Any other declaring kind this does not know is refused, as in
    /// <see cref="DeclaredEntities"/>. Mirrored by <c>declaredDevices</c> in <c>contract.ts</c>.
    /// </summary>
    internal static IEnumerable<(string NodeId, JsonObject Device)> DeclaredDevices(JsonObject graph, JsonObject catalog)
    {
        foreach (var raw in graph["nodes"] as JsonArray ?? [])
        {
            if (raw is not JsonObject node || catalog["nodes"]?[Text(node, "kind")] is not { } spec || !Declares(spec, "device")) continue;
            if (IsClassNode(spec))
            {
                var picked = Picked(node["config"], CSharpDeviceMembers);
                picked["csharp"] = true;
                yield return (Text(node, "id"), picked);
            }
            else if (Text(node, "kind") == "device") yield return (Text(node, "id"), Picked(node["config"], DeviceMembers));
            else throw new InvalidOperationException($"The kind {Text(node, "kind")} declares a device that no list knows how to describe");
        }
    }
    /// <summary>
    /// The members a Device node's device has: those of the Device kind's config in the built-in catalog (key, name,
    /// manufacturer… Part of), whatever a graph's own catalog says. Mirrored by <c>deviceMembers</c> in <c>contract.ts</c>.
    /// </summary>
    static IEnumerable<string> DeviceMembers => (Root["nodes"]?["device"]?["config"] as JsonObject ?? []).Select(member => member.Key);
    /// <summary>The members of a C# device's config, or a library device class's, its device lists: its class gives the rest.</summary>
    static readonly string[] CSharpDeviceMembers = ["key", "name", "via"];
    /// <summary>
    /// A copy of the <paramref name="members"/> the config has, as it has them and in its own order (a Device node's
    /// source is the one it was when its whole config was copied); empty when it is not an object.
    /// </summary>
    static JsonObject Picked(JsonNode? config, IEnumerable<string> members)
    {
        var picked = new JsonObject();
        var wanted = members.ToHashSet(StringComparer.Ordinal);
        if (config is JsonObject own) foreach (var (member, value) in own) if (wanted.Contains(member)) picked[member] = value?.DeepClone();
        return picked;
    }
    /// <summary>
    /// The id of the Device node an Entity node is on: the source of the first value wire into its Device pin, as the
    /// validators read the graph's nodes and wires (<see cref="GraphNodes"/>, <see cref="ValueWires"/>), when that source is
    /// a Device node; null without the wire, when something else is wired there, or for an id that is no Entity node.
    /// Mirrored by <c>deviceNodeOf</c> in <c>contract.ts</c>.
    /// </summary>
    internal static string? DeviceOf(JsonObject graph, string entityNodeId, JsonObject catalog)
    {
        var nodes = GraphNodes(graph);
        if (!nodes.TryGetValue(entityNodeId, out var hub) || !Declares(catalog["nodes"]?[Text(hub, "kind")], "entity")) return null;
        var wire = ValueWires(graph, nodes).Find(e => Text(e, "target") == entityNodeId && Text(e, "targetPort") == "device");
        return wire != null && Declares(catalog["nodes"]?[Text(nodes[Text(wire, "source")], "kind")], "device") ? Text(wire, "source") : null;
    }
    /// <summary>
    /// A device's key as the definition's devices list it: a text that is not blank once trimmed, as given; null for none
    /// (blank, absent or not a text), when the device is no device of the list and an entity on it is on none.
    /// </summary>
    internal static string? DeviceKey(JsonNode? config) =>
        config?["key"] is JsonValue value && value.TryGetValue<string>(out var key) && VisualGraphValidator.Words.Trimmed(key).Length > 0 ? key : null;
    /// <summary>
    /// The device a Device node's config describes, as a definition lists one: a copy of it without a text that is blank
    /// once trimmed and without a Part of of null (none: nothing to show). Mirrored by <c>deviceDescription</c> in <c>contract.ts</c>.
    /// </summary>
    static JsonObject DeviceDescription(JsonObject config)
    {
        var device = (JsonObject)config.DeepClone();
        foreach (var (member, value) in device.ToArray())
            if (value is JsonValue text && text.TryGetValue<string>(out var words) && VisualGraphValidator.Words.Trimmed(words).Length == 0 || member == "via" && value == null)
                device.Remove(member);
        return device;
    }
    /// <summary>
    /// The definition's devices as a graph gives them: those of its Device nodes with a key (<see cref="DeviceKey"/>), in
    /// node order, then those of its C# devices alike, each described (<see cref="DeviceDescription"/>); two of one key
    /// are both listed (the validator refuses the second). Mirrored by <c>derivedDevices</c> in <c>contract.ts</c>.
    /// </summary>
    internal static IEnumerable<JsonObject> DerivedDevices(JsonObject graph, JsonObject catalog) =>
        DeclaredDevices(graph, catalog).Where(d => DeviceKey(d.Device) != null).OrderBy(d => IsCSharpDevice(d.Device)).Select(d => DeviceDescription(d.Device));
    /// <summary>
    /// Whether a device a graph lists is a C# device's or a library device class's (<c>"csharp": true</c>), which its own
    /// class sets up. Only <see cref="DeclaredDevices"/> sets the member, for a class node: a Device node's config that
    /// holds one does not pass it on.
    /// </summary>
    internal static bool IsCSharpDevice(JsonNode? device) => Flag(device, "csharp");
    /// <summary>
    /// The entities the graph's C# entities declare, as a definition lists them for reading (its stored document and its
    /// answers, never the catalog's <c>definition</c>, whose entities steps name): for each class node that declares an
    /// entity (a C# entity, a library's entity class), in the order of the graph's <c>nodes</c>,
    /// <c>{key, domain, name, device, csharp}</c>: its config's key as given (none when it has none), the domain its
    /// signature gives (<see cref="ClassSignature"/>: a library class's its entry's; null when that cannot be read), no
    /// name (null: its class names it, else its title, as it runs), the key of the Device node it is on
    /// (<see cref="DeviceOf"/>, <see cref="DeviceKey"/>) or null, and <c>csharp</c>, set here by the node's kind alone,
    /// never read from its config. Never its code, signature, settings or their values. Mirrored by
    /// <c>csharpEntities</c> in <c>contract.ts</c>.
    /// </summary>
    internal static IEnumerable<JsonObject> CSharpEntities(JsonObject graph, JsonObject catalog)
    {
        var nodes = GraphNodes(graph);
        foreach (var raw in graph["nodes"] as JsonArray ?? [])
        {
            if (raw is not JsonObject node || catalog["nodes"]?[Text(node, "kind")] is not { } spec || !IsClassNode(spec) || !Declares(spec, "entity")) continue;
            var entity = Picked(node["config"], ["key"]);
            entity["domain"] = ClassSignature(node, spec)?["domain"]?.DeepClone();
            entity["name"] = null;
            entity["device"] = DeviceOf(graph, Text(node, "id"), catalog) is { } device ? DeviceKey(nodes[device]["config"]) : null;
            entity["csharp"] = true;
            yield return entity;
        }
    }
    /// <summary>
    /// The definition's entities as a graph gives them: those its Entity nodes declare (<see cref="DeclaredEntities"/>),
    /// each with <c>device</c>, the key of the Device node it is on (<see cref="DeviceOf"/>), or null when it is on none or
    /// on one without a key (<see cref="DeviceKey"/>).
    /// </summary>
    static IEnumerable<JsonObject> DerivedEntities(JsonObject graph, JsonObject catalog)
    {
        var nodes = GraphNodes(graph);
        foreach (var (nodeId, entity) in DeclaredEntities(graph, catalog))
        {
            entity["device"] = DeviceOf(graph, nodeId, catalog) is { } device ? DeviceKey(nodes[device]["config"]) : null;
            yield return entity;
        }
    }
    /// <summary>
    /// The kind of document a graph is: <c>entity</c> when it has an object <c>definition</c> (an entity definition's
    /// logic, its settings composed into it), else <c>automation</c>.
    /// </summary>
    internal static string GraphDocument(JsonObject graph) => graph["definition"] is JsonObject ? "entity" : "automation";
    /// <summary>The types a definition's setting can have, and so the setting's value in its graph.</summary>
    internal static readonly string[] SettingTypes = ["string", "number", "boolean"];
    /// <summary>
    /// The well-formed entries (an object with a text <c>key</c>) of one list of the definition the graph's catalog
    /// carries (<see cref="WithGraphTypes"/>), the first of each key; empty without one.
    /// </summary>
    internal static List<JsonObject> DefinitionItems(JsonObject catalog, string list)
    {
        var items = new List<JsonObject>(); var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in (catalog["definition"] as JsonObject)?[list] as JsonArray ?? [])
            if (raw is JsonObject item && item["key"] is JsonValue k && k.TryGetValue<string>(out var key) && keys.Add(key)) items.Add(item);
        return items;
    }
    /// <summary>The definition's entity with this key; null when it has none.</summary>
    internal static JsonObject? DefinitionEntity(JsonObject catalog, string key) => DefinitionItems(catalog, "entities").Find(e => Text(e, "key") == key);
    /// <summary>The <c>entityDomains</c> entry of the definition's entity with this key; null for an unknown entity or domain.</summary>
    internal static JsonObject? EntityDomain(JsonObject catalog, string key) =>
        DefinitionEntity(catalog, key) is { } entity && Text(entity, "domain") is { Length: > 0 } domain ? DomainRow(catalog, domain) : null;
    /// <summary>The <c>entityDomains</c> row of a domain; null for one the catalog does not have.</summary>
    static JsonObject? DomainRow(JsonObject catalog, string domain) =>
        catalog["entityDomains"] is JsonObject domains && domains.TryGetPropertyValue(domain, out var entry) ? entry as JsonObject : null;
    /// <summary>
    /// The <c>entityDomains</c> row of the type an Entity node (<c>"dynamicInputs": "declared"</c>) declares, read from
    /// its own <c>config.domain</c>, never by its key; null for an unknown type. Mirrored by <c>declaredDomain</c> in <c>contract.ts</c>.
    /// </summary>
    internal static JsonObject? DeclaredDomain(JsonObject node, JsonObject catalog) => DomainRow(catalog, Text(node["config"], "domain"));
    /// <summary>
    /// An Entity node's execution pins: <c>command:&lt;command&gt;</c> per command of its type, in the row's order; none for
    /// an unknown type. A camera has no pin for its frames: its Image input says what it shows (<see cref="PictureInputs"/>).
    /// Mirrored by <c>declaredExecution</c> in <c>contract.ts</c>.
    /// </summary>
    static IEnumerable<string> DeclaredExecution(JsonObject node, JsonObject catalog)
    {
        if (DeclaredDomain(node, catalog) is not { } row) yield break;
        foreach (var (command, _) in row["commands"] as JsonObject ?? []) yield return "command:" + command;
    }
    /// <summary>The type of the definition's setting with this key, when it is one a setting can have; null otherwise.</summary>
    internal static string? SettingType(JsonObject catalog, string key) =>
        DefinitionItems(catalog, "settings").Find(s => Text(s, "key") == key) is { } setting && Text(setting, "type") is var type && SettingTypes.Contains(type) ? type : null;
    /// <summary>
    /// The value nodes upstream of <paramref name="start"/> through value wires: each once, depth first in the order of
    /// <paramref name="data"/>, a node before those it reads; the walk stops at a step (whose output is read as it is).
    /// <paramref name="data"/> holds the graph's value wires whose two ends are in <paramref name="nodes"/>; a wire into a
    /// structural input (an Entity node's Device pin, among a node's <paramref name="inputs"/>) carries no value and is
    /// not walked. Mirrored by <c>valueUpstream</c> in <c>contract.ts</c>.
    /// </summary>
    internal static List<JsonObject> ValueUpstream(JsonObject start, IReadOnlyDictionary<string, JsonObject> nodes, IReadOnlyList<JsonObject> data, Func<JsonObject, bool> pure, Func<JsonObject, JsonObject> inputs)
    {
        var found = new List<JsonObject>(); var seen = new HashSet<string>(StringComparer.Ordinal) { Text(start, "id") };
        var stack = new Stack<IEnumerator<JsonObject>>();
        IEnumerator<JsonObject> Readers(JsonObject node)
        {
            var ports = inputs(node);
            return data.Where(e => Text(e, "target") == Text(node, "id") && !Structural(ports[Text(e, "targetPort")])).Select(e => nodes[Text(e, "source")]).GetEnumerator();
        }
        // An explicit stack: a long chain of value nodes must not overflow the thread's stack.
        stack.Push(Readers(start));
        while (stack.TryPeek(out var current))
        {
            if (!current.MoveNext()) { stack.Pop(); continue; }
            var source = current.Current;
            if (!pure(source) || !seen.Add(Text(source, "id"))) continue;
            found.Add(source);
            stack.Push(Readers(source));
        }
        return found;
    }
    /// <summary>The value nodes upstream of the graph's node <paramref name="id"/> (<see cref="ValueUpstream(JsonObject, IReadOnlyDictionary{string, JsonObject}, IReadOnlyList{JsonObject}, Func{JsonObject, bool}, Func{JsonObject, JsonObject})"/>), as the validators read the graph.</summary>
    internal static List<JsonObject> ValueUpstream(JsonObject graph, string id, VisualCatalogSnapshot snapshot)
    {
        var nodes = GraphNodes(graph);
        if (!nodes.TryGetValue(id, out var start)) return [];
        return ValueUpstream(start, nodes, ValueWires(graph, nodes), n => IsPure(n, Spec(n, snapshot)), n => DataInputs(n, null, snapshot));
    }
    /// <summary>
    /// Whether an input (as <see cref="DataInputs"/> gives it) takes nothing but entity ids: every one of its types is
    /// <c>entity_id</c> or <c>entity_id:&lt;domain&gt;</c>, built in, a node library's or a Custom C# node's alike. The entity
    /// chosen in such an input is one an Entity node's state follows; one given over a wire is not. An input open to every type (a
    /// comparison's, an object's field) holds an entity id as a value instead. Mirrored by <c>takesOnlyEntityIds</c> in
    /// <c>contract.ts</c>.
    /// </summary>
    internal static bool TakesOnlyEntityIds(JsonNode? input) => input?["types"] is JsonArray { Count: > 0 } types &&
        types.All(t => t is JsonValue v && v.TryGetValue<string>(out var type) && (type == "entity_id" || type.StartsWith("entity_id:", StringComparison.Ordinal)));
    /// <summary>
    /// Whether a wire from this node into an entity id input still lets an Entity node's state follow the entity: a Constant of an
    /// entity id type, or an Entity (<c>ha_entity</c>), whose chosen entity <see cref="StateDependencies.Of"/> counts. Mirrored
    /// by <c>followedSource</c> in <c>contract.ts</c>.
    /// </summary>
    internal static bool FollowedSource(JsonObject node, VisualCatalogSnapshot? snapshot) =>
        Text(node, "kind") == "ha_entity" || Text(node, "kind") == "constant" && TakesOnlyEntityIds(DataInputs(node, null, snapshot)["value"]);
    /// <summary>
    /// The domain of an entity id as an Entity (<c>ha_entity</c>) takes one: <c>camera</c> for <c>camera.front_door</c>; null for a
    /// text that is not an entity id (the pattern of the catalog's <c>entity_id</c>). Mirrored by <c>entityIdDomain</c> in <c>contract.ts</c>.
    /// </summary>
    internal static string? EntityIdDomain(string id) => VisualTypes.Matches(id, EntityIdPattern) ? id[..id.IndexOf('.')] : null;
    /// <summary>An entity id: a domain, a dot and an object id, each of lowercase letters, digits and underscores.</summary>
    internal const string EntityIdPattern = "^[a-z0-9_]+\\.[a-z0-9_]+$";
    /// <summary>
    /// The graph's value wires as the validators accept them: with an id, of kind <c>data</c> and no execution port, both
    /// ports named, and both ends among <paramref name="nodes"/> (<see cref="GraphNodes"/>).
    /// </summary>
    internal static List<JsonObject> ValueWires(JsonObject graph, IReadOnlyDictionary<string, JsonObject> nodes) =>
        (graph["edges"] as JsonArray ?? []).OfType<JsonObject>().Where(e => Text(e, "id") != "" && Text(e, "kind") == "data" && !e.ContainsKey("port") && Text(e, "sourcePort") != "" && Text(e, "targetPort") != "" &&
            nodes.ContainsKey(Text(e, "source")) && nodes.ContainsKey(Text(e, "target"))).ToList();
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
    /// type id, each pin an identifier for its id, each type an object. A method with flows adds <c>execution</c> and
    /// <c>scopePorts</c>, lists of text, and <c>executionLabels</c>, an object of text; a signature without them has none.
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
        static bool Texts(JsonNode? value) => value == null || value is JsonArray list && list.All(Is<string>);
        if (!Texts(signature["execution"]) || !Texts(signature["scopePorts"])) return false;
        if (signature["executionLabels"] is { } labels && (labels is not JsonObject named || !named.All(p => Is<string>(p.Value)))) return false;
        return types.All(t => t.Value is JsonObject);
    }
    /// <summary>
    /// The catalog with the types of the graph's Custom C# nodes added (C# and TS alike): each well-formed signature's
    /// <c>types</c> whose id the catalog does not have. Each node lists what its own compile knows a type converts to and
    /// from, so the entries of one id are merged: the first node's entry, with <c>assignableTo</c> and
    /// <c>assignableFrom</c> the union of every node's (in node order). The order of the nodes never changes what is
    /// accepted. An entity graph's <c>definition</c> is carried too, its <c>entities</c> those its nodes declare, each with
    /// the key of its device (<see cref="DerivedEntities"/>), and its <c>devices</c> those of its Device nodes
    /// (<see cref="DerivedDevices"/>), whatever the graph's own lists; the type of each Entity's (<c>ha_entity</c>) output is its
    /// entity's (<see cref="ResolveEntities"/>). The catalog itself when there are none, and for a graph past the catalog's limits:
    /// the validator answers that graph with <c>graph.limit</c> alone, and nothing is merged or resolved for it, whoever asks.
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
        var definition = graph["definition"] as JsonObject;
        var picked = GraphNodes(graph).Values.Any(n => Text(n, "kind") == "ha_entity");
        if (added.Count == 0 && selecting.Count == 0 && definition == null && !picked) return catalog;
        var merged = (JsonObject)catalog.DeepClone();
        foreach (var (id, entry) in added.ToArray()) { added.Remove(id); merged["types"]![id] = entry; }
        // An Entity's type before the Select property nodes that may read it, which keep it (ResolveOutputs).
        if (picked) { merged.Remove("resolvedOutputs"); ResolveEntities(merged, graph); }
        if (selecting.Count > 0) ResolveOutputs(merged, graph);
        if (definition != null)
        {
            var copy = (JsonObject)definition.DeepClone();
            copy["entities"] = new JsonArray([.. DerivedEntities(graph, catalog)]);
            copy["devices"] = new JsonArray([.. DerivedDevices(graph, catalog)]);
            merged["definition"] = copy;
            ResolveSettings(merged, graph);
        }
        return merged;
    }
    /// <summary>
    /// The graph's Entity nodes (<c>ha_entity</c>): the output of each has the type of an entity id of its entity's domain
    /// (<c>entity_id:camera</c> for <c>camera.front_door</c>), under <c>resolvedOutputs</c>; one whose entity is not an
    /// entity id (<see cref="EntityIdDomain"/>) has none (an empty type). Mirrored by <c>resolveEntities</c> in <c>contract.ts</c>.
    /// </summary>
    static void ResolveEntities(JsonObject catalog, JsonObject graph)
    {
        foreach (var node in GraphNodes(graph).Values)
            if (Text(node, "kind") == "ha_entity" && catalog["nodes"]?["ha_entity"] is JsonObject && EntityIdDomain(Text(node["config"], "entity")) is { } domain)
            {
                if (catalog["resolvedOutputs"] is not JsonObject resolved) catalog["resolvedOutputs"] = resolved = new JsonObject();
                ((resolved[Text(node, "id")] ??= new JsonObject()) as JsonObject)!["entity"] = "entity_id:" + domain;
            }
    }
    /// <summary>
    /// An entity graph's Setting nodes: the value of each has the type of the definition's setting it names, under
    /// <c>resolvedOutputs</c>; one naming no setting of a type a setting can have has none (an empty type).
    /// </summary>
    static void ResolveSettings(JsonObject catalog, JsonObject graph)
    {
        foreach (var node in GraphNodes(graph).Values)
            if (Text(node, "kind") == "setting" && catalog["nodes"]?["setting"] is JsonObject && SettingType(catalog, Text(node["config"], "key")) is { } type)
            {
                if (catalog["resolvedOutputs"] is not JsonObject resolved) catalog["resolvedOutputs"] = resolved = new JsonObject();
                ((resolved[Text(node, "id")] ??= new JsonObject()) as JsonObject)!["value"] = type;
            }
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
    /// <summary>
    /// The members a value of the type has, as its resolved entry lists them (<see cref="VisualTypes.Resolve"/>: an
    /// entity id of a domain has its class's); empty for a type without any. Mirrored by <c>typeProperties</c> in <c>contract.ts</c>.
    /// </summary>
    internal static JsonObject[] TypeProperties(JsonObject catalog, string type) => (VisualTypes.Resolve(catalog, type)?["properties"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
    /// <summary>
    /// The type of each <c>propertyOf</c> output of the graph, under <c>resolvedOutputs</c> (node id, then port): the
    /// type of the chosen property of the type the first value wire into its input brings. A node reading another such
    /// node is resolved after it: each waits for the ones it reads, and is resolved once none is left (in the order the
    /// graph lists the ready ones), so no call goes one level deeper per node, however long the chain. A node on a cycle
    /// of such nodes, or reading one, is never ready and has none; so has an output whose input, property or source type
    /// is missing. The types already resolved (an Entity's, <see cref="ResolveEntities"/>) are kept, so that a property of
    /// an Entity's entity can be selected. Mirrored by <c>resolveOutputs</c> in <c>contract.ts</c>.
    /// </summary>
    internal static void ResolveOutputs(JsonObject catalog, JsonObject graph)
    {
        if (catalog["resolvedOutputs"] is not JsonObject resolved) catalog["resolvedOutputs"] = resolved = new JsonObject();
        var nodes = GraphNodes(graph);
        var incoming = new Dictionary<(string, string), JsonObject>();
        foreach (var edge in ValueWires(graph, nodes)) incoming.TryAdd((Text(edge, "target"), Text(edge, "targetPort")), edge);
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
    /// <summary>
    /// The method of one flow of a node that starts several (an Entity node: its state, each command, its frame): the
    /// node's (<see cref="EffectiveTriggerMethod"/>), then <c>_</c> and the port in hex, so no two ports share one.
    /// </summary>
    public static string FlowMethod(JsonObject node, string port) => EffectiveTriggerMethod(node) + "_" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(port));
    internal static string TriggerType(JsonObject node, VisualCatalogSnapshot snapshot) => Text(node["config"], "type", Text(Spec(node, snapshot)?["config"]?["type"], "default"));
    public static bool CompatibleTypes(string source, string target, VisualCatalogSnapshot? snapshot = null) => (snapshot ?? VisualCatalogSnapshot.BuiltIn).IsAssignable(source, target);
    public static string[] ExecutionPorts(JsonObject node, VisualCatalogSnapshot? snapshot = null)
    {
        var spec = Spec(node, snapshot);
        var ports = new List<string>();
        // A value node and a declaration (a Device node) have no place in the run order.
        if (IsPure(node, spec) || Declaration(spec)) return [];
        if (Text(spec, "dynamicExecution") == "branches" && node["config"]?["branches"] is JsonArray branches)
            // Only a text id names a branch, as in TypeScript; another value is reported by the validators and has no port.
            ports.AddRange(branches.OfType<JsonValue>().Select(id => id.TryGetValue<string>(out var branch) ? branch : null).OfType<string>().Select(id => "branch:" + id));
        if (Text(spec, "dynamicExecution") == "declared") ports.AddRange(DeclaredExecution(node, snapshot?.Root ?? Root));
        // A Custom C# node with flows has its ports in its signature, as its pins are.
        ports.AddRange(((CustomSignature(node, spec)?["execution"] ?? spec?["execution"]) as JsonArray ?? []).Select(p => p!.GetValue<string>()));
        return ports.ToArray();
    }
    /// <summary>The execution ports that open a scope of their own: a loop's body or condition, a node method's flows.</summary>
    internal static string[] ScopePorts(JsonObject node, JsonNode? spec) =>
        ((CustomSignature(node, spec)?["scopePorts"] ?? spec?["scopePorts"]) as JsonArray ?? []).Select(p => p is JsonValue v && v.TryGetValue<string>(out var port) ? port : null).OfType<string>().ToArray();
    public static JsonObject DataOutputs(JsonObject node, VisualCatalogSnapshot? snapshot = null) => DataOutputs(node, snapshot?.Root ?? Root);
    /// <summary>
    /// The node's value outputs under <paramref name="catalog"/>. A <c>propertyOf</c> output has the type the graph's
    /// catalog resolved for it (<see cref="WithGraphTypes"/>), and none (an empty type) until it is resolved.
    /// </summary>
    internal static JsonObject DataOutputs(JsonObject node, JsonObject catalog)
    {
        var spec = catalog["nodes"]?[Text(node, "kind")] as JsonObject;
        var ports = (spec?["outputs"]?.DeepClone() as JsonObject) ?? [];
        // An output without a type of its own (a Select property's, a Setting's value) has the one the graph's catalog resolved.
        foreach (var (key, value) in ports) if (value is JsonObject port)
            port["type"] = PropertyOf(port) != "" || Text(port, "type") == "" ? Text(catalog["resolvedOutputs"]?[Text(node, "id")], key) : ResolveType(Text(port, "type"), node);
        if (Text(spec, "dynamicOutputs") == "custom" && CustomSignature(node, spec)?["outputs"] is JsonObject custom) foreach (var (key, value) in custom) ports[key] = value?.DeepClone();
        if (Text(spec, "dynamicOutputs") == "loop") foreach (var key in new[] { "carry", "exports" }) foreach (var item in Declarations(node, key))
            ports[$"{(key == "carry" ? "carry" : "export")}:{Text(item, "name")}"] = new JsonObject { ["type"] = Text(item, "type"), ["description"] = (key == "carry" ? "Kept between iterations: " : "Available after the loop: ") + Text(item, "name"), ["visibility"] = key == "carry" ? "loop" : "outside" };
        if (Text(spec, "dynamicOutputs") == "trigger") foreach (var (key, value) in TriggerOutputs(node, catalog)) ports[key] = value?.DeepClone();
        if (Text(spec, "dynamicOutputs") == "declared") foreach (var (key, value) in DeclaredOutputs(node, catalog)) ports[key] = value?.DeepClone();
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
    /// <summary>
    /// An Entity node's value outputs: for each data value of each command of its type, <c>command:&lt;command&gt;:&lt;value&gt;</c>,
    /// of its first type and read from the run's trigger (<see cref="CommandValueOutput"/>), grouped under its command's execution pin
    /// (<c>group</c>, layout only: it is not a scope). Mirrored by <c>declaredOutputs</c> in <c>contract.ts</c>.
    /// </summary>
    static JsonObject DeclaredOutputs(JsonObject node, JsonObject catalog)
    {
        var ports = new JsonObject();
        foreach (var (command, entry) in DeclaredDomain(node, catalog)?["commands"] as JsonObject ?? [])
            foreach (var (name, value) in (entry as JsonObject)?["data"] as JsonObject ?? [])
            {
                var port = CommandValueOutput(name, value);
                port["group"] = "command:" + command;
                ports[$"command:{command}:{name}"] = port;
            }
        return ports;
    }
    /// <summary>One data value of a command as an output: of its first type, with its label, read from the run's trigger at <c>/data/&lt;name&gt;</c>.</summary>
    static JsonObject CommandValueOutput(string name, JsonNode? value)
    {
        var port = new JsonObject { ["type"] = TypeIds(value?["types"]).FirstOrDefault() ?? "", ["pointer"] = "/data/" + name.Replace("~", "~0").Replace("/", "~1") };
        if (Text(value, "label") is { Length: > 0 } label) port["label"] = label;
        return port;
    }
    /// <summary>The text items of a list of type ids, such as a state value's <c>types</c>; none for anything else.</summary>
    static IEnumerable<string> TypeIds(JsonNode? types) => (types as JsonArray ?? []).OfType<JsonValue>().Select(t => t.TryGetValue<string>(out var s) ? s : "").Where(s => s != "");
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
        // $any is every type but a structural one (a device, which carries no value); $properties every type whose values
        // have properties (a Select property node's input).
        foreach (var (_, value) in ports) if (value is JsonObject port && port["types"] is JsonArray types)
            port["types"] = new JsonArray(types.SelectMany(t => t!.GetValue<string>() switch
            {
                "$any" => (snapshot?.Root ?? Root)["types"]!.AsObject().Where(p => !Structural(p.Value)).Select(p => p.Key),
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
        // Every type a value can have, for an object's field, an unknown service field and a list's item: none is structural.
        var all = (snapshot?.Root ?? Root)["types"]!.AsObject().Where(p => !Structural(p.Value)).Select(p => p.Key).ToArray();
        var dynamic = Text(spec, "dynamicInputs");
        if (dynamic == "custom" && CustomSignature(node, spec)?["inputs"] is JsonObject custom) foreach (var (key, value) in custom) ports[key] = value?.DeepClone();
        // A class node's settings follow its kind's own pins, in the class's order.
        if (dynamic == "settings" && ClassSignature(node, spec) is { } classSignature)
            foreach (var setting in classSignature["settings"]!.AsArray().OfType<JsonObject>()) ports[Text(setting, "key")] = SettingInput(setting);
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
        // The state values, each without a default and none required: a Set state's of the domain of the definition's
        // entity config.entity names (what it is not given, the entity keeps); an Entity node's of the type its own
        // config.domain names. Mirrored by dataInputs in contract.ts.
        var stateRow = dynamic switch { "entity" => EntityDomain(snapshot?.Root ?? Root, Text(node["config"], "entity")), "declared" => DeclaredDomain(node, snapshot?.Root ?? Root), _ => null };
        if (stateRow?["state"] is JsonObject state)
            foreach (var (key, value) in state)
            {
                var port = Input(TypeIds(value?["types"]), false, null);
                if (Text(value, "label") is { Length: > 0 } label) port["label"] = label;
                ports[key] = port;
            }
        // An Entity node's pictures (a camera's Image), after its state values, none required, each marked picture: they are
        // read in its frame flow, never in its state's (PictureInput). A Set state sets no picture.
        if (dynamic == "declared" && stateRow?["picture"] is JsonObject pictures)
            foreach (var (key, value) in pictures)
            {
                var port = Input(TypeIds(value?["types"]), false, Text(value, "description") is { Length: > 0 } description ? description : null);
                if (Text(value, "label") is { Length: > 0 } label) port["label"] = label;
                port["picture"] = true;
                ports[key] = port;
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
