using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static HassSharp.VisualNodeCatalog;

namespace HassSharp;

/// <summary>Pure graph validation for the catalog graph version. Does not compile, attach triggers, or execute data dependencies.</summary>
public static class VisualGraphValidator
{
    public static List<VisualDiagnostic> Validate(JsonObject graph, VisualCatalogSnapshot? snapshot = null) => new Validation(snapshot ?? VisualCatalogSnapshot.BuiltIn).Run(graph);

    // Generation consumes the types resolved by this same scope-aware validation pass.
    internal static List<VisualDiagnostic> Validate(JsonObject graph, VisualCatalogSnapshot snapshot,
        out IReadOnlyDictionary<(string Node,string Input),string> inputTypes)
    {
        var validation=new Validation(snapshot);var diagnostics=validation.Run(graph);
        inputTypes=validation.InputTypes;return diagnostics;
    }

    /// <summary>Keys a service target is flattened into; a field with one of them would be sent twice.</summary>
    static readonly string[] TargetKeys = ["entity_id", "device_id", "area_id", "floor_id", "label_id"];

    /// <summary>
    /// The wording of validator messages, which people read on node cards and under fields. Byte-identical to
    /// the helpers in <c>frontend/src/visual/contract.ts</c>; type names are those of <c>typeLabels.ts</c>.
    /// </summary>
    internal static class Words
    {
        static readonly Dictionary<string, string> BuiltIn = new(StringComparer.Ordinal)
        {
            ["string"] = "Text", ["number"] = "Number", ["boolean"] = "Yes/No", ["object"] = "Object", ["array"] = "List",
            ["entity_id"] = "Entity", ["target"] = "Target", ["global::HassSharp.EntityStateSnapshot"] = "Entity state",
        };
        static readonly Dictionary<string, string> Primitive = new(StringComparer.Ordinal)
        { ["boolean"] = "Yes/No", ["number"] = "Number", ["integer"] = "Number", ["string"] = "Text", ["object"] = "Object", ["array"] = "List" };
        static readonly HashSet<string> Keywords = ["bool", "byte", "sbyte", "char", "decimal", "double", "float", "int", "uint", "long", "ulong", "short", "ushort", "nint", "nuint", "object", "string", "dynamic"];
        static readonly Regex Entity = new("^entity_id:([a-z0-9_]+)$", RegexOptions.CultureInvariant);
        static readonly Regex Collection = new(@"^(?:global::)?System\.Collections\.Generic\.(?:IEnumerable|IReadOnlyList|IReadOnlyCollection|IList|ICollection|List)<(.+)>$", RegexOptions.CultureInvariant | RegexOptions.Singleline);
        static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
        /// <summary><c>binary_sensor</c> → <c>Binary sensor</c>.</summary>
        internal static string DomainWords(string domain) => Capital(domain.Replace('_', ' '));
        static string ClrWords(string id)
        {
            var trimmed = Regex.Replace(Regex.Replace(Regex.Replace(id, "^global::", ""), "<.*$", "", RegexOptions.Singleline), @"[?[\]]+$", "");
            var name = trimmed.Split('.').LastOrDefault(s => s != "") ?? id;
            var bare = Regex.IsMatch(name, "^I[A-Z][a-z]") ? name[1..] : name;
            return Capital(Regex.Replace(Regex.Replace(bare, "([a-z0-9])([A-Z])", "$1 $2"), "([A-Z]+)([A-Z][a-z])", "$1 $2").Replace('_', ' ').ToLowerInvariant());
        }
        /// <summary>The friendly name of a type, as <c>typeLabel</c> in <c>typeLabels.ts</c> gives it.</summary>
        internal static string TypeLabel(JsonObject catalog, string id)
        {
            if (BuiltIn.TryGetValue(id, out var builtIn)) return builtIn;
            if (Entity.Match(id) is { Success: true } entity) return DomainWords(entity.Groups[1].Value) + " entity";
            var spec = VisualTypes.Resolve(catalog, id);
            if (id.EndsWith('?')) return TypeLabel(catalog, id[..^1]);
            if (Text(spec, "elementType") is { Length: > 0 } element) return "List of " + TypeLabel(catalog, element);
            if (id.EndsWith("[]", StringComparison.Ordinal)) return "List of " + TypeLabel(catalog, id[..^2]);
            if (Collection.Match(id) is { Success: true } generic) return "List of " + TypeLabel(catalog, generic.Groups[1].Value);
            var kind = Text(spec, "literalKind").ToLowerInvariant(); var category = Text(spec, "category").ToLowerInvariant();
            if (Primitive.TryGetValue(kind, out var primitive)) return primitive;
            if (category != "object" && kind != "enum" && Primitive.TryGetValue(category, out var named)) return named;
            if (Text(spec, "label") is { Length: > 0 } label && !Regex.IsMatch(label, @"[.:<>?[\]]") && !Keywords.Contains(label)) return label;
            return ClrWords(id);
        }
        /// <summary>A label as a noun inside a sentence: <c>Light entity</c> → <c>light entity</c>; words with more capitals stay.</summary>
        internal static string Noun(string label) => label == "Yes/No" ? "yes/no value" : string.Join(' ', label.Split(' ').Select(w => Regex.IsMatch(w, "^[A-Z][^A-Z]*$") ? w.ToLowerInvariant() : w));
        /// <summary>The noun with its article; "text" takes none.</summary>
        internal static string Article(string noun) => noun == "text" ? noun : (Regex.IsMatch(noun, "^[aeiouAEIOU]") ? "an " : "a ") + noun;
        internal static string AType(JsonObject catalog, string id) => Article(Noun(TypeLabel(catalog, id)));
        internal static string Or(IEnumerable<string> items)
        {
            var list = items.ToList();
            return list.Count <= 1 ? string.Concat(list) : string.Join(", ", list.Take(list.Count - 1)) + " or " + list[^1];
        }
        internal static string Number(double value) => Math.Floor(value) == value && Math.Abs(value) < 1e15
            ? ((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture) : value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        internal static string Show(JsonNode? value) => value switch
        {
            null => "null",
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue flag when flag.TryGetValue<bool>(out var b) => b ? "true" : "false",
            JsonValue number when number.GetValueKind() == System.Text.Json.JsonValueKind.Number => Number(double.Parse(number.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture)),
            _ => value.ToJsonString(),
        };
        /// <summary>The allowed values: one, a short list, or a reference to the listed options.</summary>
        internal static string Choices(JsonArray values, string many) => values.Count == 1 ? Show(values[0]) : values.Count <= 6 ? many + Or(values.Select(Show)) : many + "the listed options";
        internal static string Range(double? min, double? max, bool integer) => (min, max) switch
        {
            ({ } a, { } b) => (integer ? "a whole number between " : "between ") + Number(a) + " and " + Number(b),
            ({ } a, null) => (integer ? "a whole number of at least " : "at least ") + Number(a),
            (null, { } b) => (integer ? "a whole number of at most " : "at most ") + Number(b),
            _ => integer ? "a whole number" : "a number",
        };
        static string ItemCount(double n) => Number(n) + (n == 1 ? " item" : " items");
        internal static string Items(double? min, double? max) => (min, max) switch
        {
            ({ } a, { } b) when a == b => "exactly " + ItemCount(a),
            ({ } a, { } b) => "between " + Number(a) + " and " + Number(b) + " items",
            ({ } a, null) => "at least " + ItemCount(a),
            (null, { } b) => "at most " + ItemCount(b),
            _ => "any number of items",
        };
        /// <summary>How a config rule's JSON type reads in a message.</summary>
        internal static string ConfigKind(string type) => type switch
        {
            "array" => "a list", "object" => "an object", "integer" => "a whole number", "number" => "a number",
            "string" => "text", "boolean" => "yes or no", _ => Article(type),
        };
        /// <summary>The label people see for an input: its spec label, else a name derived from the port id.</summary>
        internal static string PortLabel(JsonNode? spec, string port) => Text(spec, "label") is { Length: > 0 } label ? label
            : port.StartsWith("field:", StringComparison.Ordinal) ? port[6..]
            : port.StartsWith("item:", StringComparison.Ordinal) ? "Item " + port[5..]
            : port.StartsWith("carry:", StringComparison.Ordinal) ? port[6..]
            : port.StartsWith("export:", StringComparison.Ordinal) ? port[7..] : port;
        /// <summary>A type mismatch: the accepted types (named, at most four) and the one given, unless that is unknown.</summary>
        internal static string Incompatible(JsonObject catalog, string subject, string type, IEnumerable<string> expected)
        {
            if (VisualTypes.Chain(catalog, type).Count == 0) return $"{subject} is connected to a value of unknown type";
            var names = expected.Select(t => AType(catalog, t)).Distinct().ToList();
            return names.Count is > 0 and <= 4 ? $"{subject} must be {Or(names)}, not {AType(catalog, type)}" : $"{subject} cannot take {AType(catalog, type)}";
        }
    }

    sealed class Validation(VisualCatalogSnapshot snapshot)
    {
        JsonObject Root => snapshot.Root;
        JsonObject? Spec(JsonObject node) => VisualNodeCatalog.Spec(node, snapshot);
        string[] ExecutionPorts(JsonObject node) => VisualNodeCatalog.ExecutionPorts(node, snapshot);
        JsonObject DataOutputs(JsonObject node) => VisualNodeCatalog.DataOutputs(node, snapshot);
        JsonObject DataInputs(JsonObject node, JsonObject? owner = null) => VisualNodeCatalog.DataInputs(node, owner, snapshot);
        bool CompatibleTypes(string source, string target) => snapshot.IsAssignable(source, target);
        bool TryGetInputValue(JsonObject node, string port, out JsonNode? value) => VisualNodeCatalog.TryGetInputValue(node, port, out value, snapshot);
        internal readonly Dictionary<(string Node,string Input),string> InputTypes=[];
        readonly List<VisualDiagnostic> errors = [];
        readonly Dictionary<string, JsonObject> nodes = new(StringComparer.Ordinal);
        readonly Dictionary<string, List<JsonObject>> outgoing = new(StringComparer.Ordinal);
        readonly List<JsonObject> data = [];
        readonly Dictionary<string, (string Owner, string Port)[]> scopes = new(StringComparer.Ordinal);
        readonly Dictionary<(string Node, string Port), JsonObject> incoming = [];
        bool KnownType(string type) => VisualTypes.Chain(Root, type).Count > 0;
        void Error(string? node, string message, string? input = null, string? port = null) => errors.Add(new(node, message, InputId: input, PortId: port));
        static bool Bool(JsonNode? node, string key, bool fallback = false) => node is JsonObject o && o[key] is JsonValue v && v.TryGetValue<bool>(out var result) ? result : fallback;
        static double? Number(JsonNode? value) => value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number && double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
        static string[] Strings(JsonNode? value) => (value as JsonArray ?? []).OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : "").ToArray();
        static bool Match(string text, string pattern) => VisualTypes.Matches(text, pattern);
        /// <summary>The service a service node calls, named in its messages; null for other nodes or without a service id.</summary>
        string? ServiceName(JsonObject node) => Text(Spec(node), "dynamicInputs") == "service" && Text(Config(node), "service") is { Length: > 0 } service ? service : null;
        string Prefix(JsonObject node) => ServiceName(node) is { } service ? service + ": " : "";
        string ConfigLabel(JsonNode? rule, string field) => Text(rule, "label") is { Length: > 0 } label ? label : field;
        static string Kind(JsonObject node) => Text(node, "kind");
        static string Id(JsonObject node) => Text(node, "id");
        static JsonObject Config(JsonObject node) => node["config"] as JsonObject ?? [];
        static string FlowPort(JsonObject node, JsonObject edge) => Kind(node) == "condition" && Text(edge, "port", "next") == "next" ? "true" : Text(edge, "port", "next");
        bool ScopePort(JsonObject node, string port) => Strings(Spec(node)?["scopePorts"]).Contains(port) || Kind(node) == "parallel" && port.StartsWith("branch:", StringComparison.Ordinal);
        static bool Ancestor((string Owner, string Port)[] a, (string Owner, string Port)[] b) => a.Length <= b.Length && a.SequenceEqual(b.Take(a.Length));
        JsonObject? Owner(JsonObject node) => scopes.TryGetValue(Id(node), out var scope) && scope.LastOrDefault() is var last && last.Port == "body" ? nodes[last.Owner] : null;
        bool Visible(JsonObject source, JsonObject target, JsonNode port)
        {
            if (!scopes.TryGetValue(Id(source), out var a) || !scopes.TryGetValue(Id(target), out var b) || !Ancestor(a, b)) return false;
            var owned = b.Length > a.Length && b[a.Length].Owner == Id(source);
            return Text(port, "visibility") switch
            {
                "outside" => !owned,
                "body" => owned && b[a.Length].Port == "body",
                "loop" => owned && b[a.Length].Port is "body" or "condition",
                _ => true
            };
        }
        public List<VisualDiagnostic> Run(JsonObject graph)
        {
            var version = Number(Root["graphVersion"]);
            if (Number(graph["version"]) != version) { Error(null, $"Expected graph version {version}"); return errors; }
            if (string.IsNullOrWhiteSpace(Text(graph, "name"))) Error(null, "Graph name is required");
            if (graph["nodes"] is not JsonArray ns || graph["edges"] is not JsonArray es) { Error(null, "Nodes and edges arrays are required"); return errors; }
            if (ns.Count is < 1 or > 256 || es.Count > 512) { Error(null, "Graph limit: 1–256 nodes and at most 512 edges"); return errors; }
            foreach (var raw in ns)
            {
                if (raw is not JsonObject node || Id(node) == "" || node["config"] is not JsonObject) { Error(null, "Each node requires id and config"); continue; }
                if (!nodes.TryAdd(Id(node), node)) Error(Id(node), "Duplicate node id");
                if (Spec(node) == null) Error(Id(node), "Unknown node kind");
            }
            foreach (var id in nodes.Keys) outgoing[id] = [];
            var edgeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in es)
            {
                if (raw is not JsonObject edge || Id(edge) == "") { Error(null, "Edge id is required"); continue; }
                var source = Text(edge, "source"); var target = Text(edge, "target");
                if (!edgeIds.Add(Id(edge))) Error(source, "Duplicate edge id");
                if (!nodes.ContainsKey(source) || !nodes.ContainsKey(target)) { Error(null, "Edge endpoint missing"); continue; }
                if (Kind(edge) == "data")
                {
                    if (edge.ContainsKey("port") || Text(edge, "sourcePort") == "" || Text(edge, "targetPort") == "") Error(source, "Do not mix execution and data ports");
                    else data.Add(edge);
                }
                else if (!edge.ContainsKey("kind") || Kind(edge) == "execution")
                {
                    if (edge.ContainsKey("sourcePort") || edge.ContainsKey("targetPort")) Error(source, "Do not mix data and execution ports");
                    if (edge.ContainsKey("port") && (edge["port"] is not JsonValue pv || !pv.TryGetValue<string>(out _))) { Error(source, "Execution port must be a string"); continue; }
                    var node = nodes[source]; var port = FlowPort(node, edge);
                    if (!ExecutionPorts(node).Contains(port)) Error(source, "Unknown execution port", port: port);
                    if (outgoing[source].Any(e => FlowPort(node, e) == port)) Error(source, "Only one execution edge per port", port: port);
                    if (Kind(nodes[target]) == "trigger") Error(target, "Trigger cannot have incoming execution edges");
                    outgoing[source].Add(edge);
                }
                else Error(source, "Unknown edge kind");
            }
            Cycles(outgoing.Values.SelectMany(e => e).ToList(), "Execution"); Cycles(data, "Data");
            var roots = nodes.Values.Where(n => Kind(n) == "trigger").ToArray();
            if (roots.Length == 0) Error(null, "At least one trigger is required");
            foreach (var root in roots) Scope(Id(root), []);
            foreach (var id in nodes.Keys) if (!scopes.ContainsKey(id)) Error(id, "Every node must be reachable by execution from a trigger");
            foreach (var edge in data)
            {
                var source = nodes[Text(edge, "source")]; var target = nodes[Text(edge, "target")];
                var sp = Text(edge, "sourcePort"); var tp = Text(edge, "targetPort");
                var output = DataOutputs(source)[sp]; var input = DataInputs(target, Owner(target))[tp];
                if (output == null) Error(Id(source), "Unknown data output port", port: sp);
                if (input == null) Error(Id(target), "Unknown data input port", tp, tp);
                if (!incoming.TryAdd((Id(target), tp), edge)) Error(Id(target), "Only one data source per input", tp, tp);
                if (output != null && input != null)
                {
                    var type = Text(output, "type"); var expected = Strings(input["types"]);
                    if (!expected.Any(t => CompatibleTypes(type, t))) Error(Id(target), Words.Incompatible(Root, Prefix(target) + Words.PortLabel(input, tp), type, expected), tp, tp);
                    if (!Visible(source, target, output)) Error(Id(target), "Data cannot cross this structured scope", tp, tp);
                }
            }
            foreach (var node in nodes.Values) ValidateNode(node);
            return errors;
        }
        void Cycles(List<JsonObject> edges, string label)
        {
            var active = new HashSet<string>(); var done = new HashSet<string>();
            void Visit(string id)
            {
                if (active.Contains(id)) { Error(id, label + " cycle is forbidden"); return; }
                if (done.Contains(id)) return;
                active.Add(id);
                foreach (var edge in edges.Where(e => Text(e, "source") == id)) Visit(Text(edge, "target"));
                active.Remove(id); done.Add(id);
            }
            foreach (var id in nodes.Keys) Visit(id);
        }
        void Scope(string id, (string Owner, string Port)[] path)
        {
            if (path.Length > 8) { Error(id, "Structured nesting limit is 8"); return; }
            if (scopes.TryGetValue(id, out var old)) { if (!old.SequenceEqual(path)) Error(id, "Execution edge crosses a structured scope"); return; }
            scopes[id] = path;
            var node = nodes[id];
            foreach (var edge in outgoing[id])
            {
                var port = FlowPort(node, edge);
                Scope(Text(edge, "target"), ScopePort(node, port) ? [.. path, (id, port)] : path);
            }
        }
        string? Operand(JsonObject node, string port, JsonNode? raw, JsonNode spec, bool present = true)
        {
            if (incoming.TryGetValue((Id(node), port), out var edge)) return Text(DataOutputs(nodes[Text(edge, "source")])[Text(edge, "sourcePort")], "type");
            var required = Bool(spec, "required");
            if (!present && !required) return null;
            var label = Words.PortLabel(spec, port); var subject = Prefix(node) + label;
            if (raw is not JsonObject operand || Text(raw, "kind") == "unset")
            {
                if (required) Error(Id(node), ServiceName(node) is { } service ? $"{service} needs a value for {label}" : $"{label} needs a value", port, port);
                else if (present && raw is not JsonObject) Error(Id(node), $"{subject} has an invalid value", port, port);
                return null;
            }
            string? type = null;
            if (Text(operand, "kind") == "variable")
            {
                var name = Text(operand, "name");
                if (!Match(name, Text(Root["structures"]?["loopDeclaration"]?["name"], "pattern"))) { Error(Id(node), $"{subject} uses a variable without a valid name", port, port); return null; }
                var candidates = nodes.Values.Where(n => Config(n)["output"] is JsonValue outputName && outputName.TryGetValue<string>(out var named) && named == name && DataOutputs(n)["value"] is { } output && Visible(n, node, output)).ToArray();
                type = candidates.Length > 0 ? Text(DataOutputs(candidates[0])["value"], "type") : null;
                if (type == null || candidates.Any(n => Text(DataOutputs(n)["value"], "type") != type)) Error(Id(node), $"{subject} uses variable {name}, which no earlier step provides with a single type", port);
            }
            else if (Text(operand, "kind") == "literal")
            {
                type = Text(operand, "type", "unknown"); var value = operand["value"];
                var valid = ValidLiteral(type, value, operand.ContainsKey("value"), snapshot);
                if (!valid) Error(Id(node), operand["type"] is not JsonValue tv || !tv.TryGetValue<string>(out _) ? $"{subject} has a value without a type"
                    : !KnownType(type) ? $"{subject} has a value of unknown type {type}"
                    : Words.Noun(Words.TypeLabel(Root, type)) is "text" ? $"{subject} is not valid text" : $"{subject} is not a valid {Words.Noun(Words.TypeLabel(Root, type))}", port);
                if (Number(value) is { } number && (Number(spec["min"]) is { } min && number < min || Number(spec["max"]) is { } max && number > max || Bool(spec, "integer") && Math.Truncate(number) != number))
                    Error(Id(node), $"{subject} must be {Words.Range(Number(spec["min"]), Number(spec["max"]), Bool(spec, "integer"))}", port);
                // Input constraints restrict authored values only; a wire is constrained by its type. A list, where the
                // input accepts one, is checked item by item (a multiple select); anything else is one value.
                if (spec["enum"] is JsonArray allowed)
                {
                    if (value is JsonArray items && Strings(spec["types"]).Contains("array"))
                    { if (!items.All(item => allowed.Any(v => JsonNode.DeepEquals(v, item)))) Error(Id(node), $"{subject} may only contain {Words.Choices(allowed, "")}", port); }
                    else if (!allowed.Any(v => JsonNode.DeepEquals(v, value))) Error(Id(node), $"{subject} must be {Words.Choices(allowed, "one of ")}", port);
                }
                if (Text(spec, "pattern") is { Length: > 0 } pattern && value is JsonValue sv && sv.TryGetValue<string>(out var str) && !Match(str, pattern)) Error(Id(node), $"{subject} is not in the expected format", port);
                if (value is JsonArray list && (Number(spec["minItems"]) is { } minItems && list.Count < minItems || Number(spec["maxItems"]) is { } maxItems && list.Count > maxItems))
                    Error(Id(node), $"{subject} must have {Words.Items(Number(spec["minItems"]), Number(spec["maxItems"]))}", port);
            }
            else Error(Id(node), $"{subject} has an invalid value", port);
            if (type != null && KnownType(type) && !Strings(spec["types"]).Any(t => CompatibleTypes(type, t))) Error(Id(node), Words.Incompatible(Root, subject, type, Strings(spec["types"])), port);
            return type;
        }
        readonly Dictionary<(string Node, string Terminal, string Owner), bool> terminalMemo = [];
        readonly HashSet<(string Node, string Terminal, string Owner)> terminalActive = [];
        bool TerminalsValid(string start, string terminal, string owner)
        {
            var key = (start, terminal, owner);
            if (terminalMemo.TryGetValue(key, out var known)) return known;
            if (!terminalActive.Add(key)) return false;
            var node = nodes[start];
            var next = outgoing[start].Where(e => !ScopePort(node, FlowPort(node, e))).ToArray();
            var normalPorts = ExecutionPorts(node).Where(p => p != "error" && !ScopePort(node, p)).ToArray();
            var valid = Kind(node) == terminal ? scopes.TryGetValue(start, out var scope) && scope.LastOrDefault().Owner == owner
                : Kind(node) == "stop" || normalPorts.Length > 0 && normalPorts.All(p => next.Any(e => FlowPort(node, e) == p))
                    && next.All(e => TerminalsValid(Text(e, "target"), terminal, owner));
            terminalActive.Remove(key); terminalMemo[key] = valid;
            return valid;
        }
        /// <summary>
        /// Contract C5. Without a services section (Init, library rebuild) there is no service diagnostic at all;
        /// an unknown service is one warning. Field and target types were already refined by DataInputs.
        /// </summary>
        void Service(JsonObject node, JsonObject spec, JsonObject cfg, string id)
        {
            if (snapshot.Services is not { } services || cfg["service"] is not JsonValue sv || !sv.TryGetValue<string>(out var service) || !Match(service, Text(spec["config"]?["service"], "pattern"))) return;
            if (services.Items[service] is not JsonObject schema) { errors.Add(new(id, $"Unknown service {service}; fields are not type-checked", 2, InputId: "service")); return; }
            var known = schema["fields"] as JsonObject ?? [];
            var fields = cfg["fields"] as JsonObject ?? [];
            foreach (var (key, _) in fields) if (!known.ContainsKey(key)) Error(id, $"{service} has no field {key}", "field:" + key);
            foreach (var (key, field) in known) if (Bool(field, "required") && !fields.ContainsKey(key)) Error(id, $"{service} needs a value for {Words.PortLabel(field, key)}", "fields");
            var wired = incoming.ContainsKey((id, "target"));
            var response = Text(schema, "response", "none"); var returnResponse = Bool(cfg, "returnResponse");
            if (returnResponse && response == "none") Error(id, $"{service} returns no response; turn off Return response", "returnResponse");
            if (!returnResponse && response == "only") Error(id, $"{service} only returns a response; turn on Return response", "returnResponse");
            // A target value object is checked by its entity_id domains; device, area, floor and label ids are not.
            var domains = ServiceTargetDomains(schema);
            if (domains.Length == 0 || wired || cfg["target"] is not JsonObject operand || Text(operand, "kind") != "literal" || operand["value"] is not JsonObject value) return;
            JsonNode?[] entities = value["entity_id"] switch { JsonArray list => list.ToArray(), JsonValue single => [single], _ => [] };
            var accepted = Words.Or(domains.Select(d => Words.DomainWords(d).ToLowerInvariant()));
            foreach (var entity in entities)
                if (entity is JsonValue ev && ev.TryGetValue<string>(out var entityId) && entityId is not ("all" or "none") && !domains.Contains(entityId.Split('.')[0]))
                    Error(id, $"{service} only accepts {accepted} entities; " + (entityId.IndexOf('.') > 0 ? $"{entityId} is {Words.Article(Words.DomainWords(entityId.Split('.')[0]).ToLowerInvariant())}" : $"{entityId} is not an entity"), "target");
        }
        /// <summary>
        /// A target the service does not take (with services only), then the collision rule. The runtime flattens the target into the call next to the fields and rejects a key sent twice, so a field
        /// must not repeat a key the target brings: entity_id for an entity id, the object's keys for a target value,
        /// and every target key for a wired or variable target object. As at run time, a field or target counts when it
        /// is connected or set. It depends on the node alone, so it applies with or without services.
        /// </summary>
        void TargetCollisions(JsonObject node, JsonObject spec, JsonObject cfg, string id, string? targetType)
        {
            bool Supplied(string port, JsonNode? operand) => incoming.ContainsKey((id, port)) || operand is JsonObject && Text(operand, "kind") != "unset";
            var wired = incoming.ContainsKey((id, "target"));
            if (snapshot.Services is { } services && ServiceName(node) is { } named && Match(named, Text(spec["config"]?["service"], "pattern")) &&
                services.Items[named] is JsonObject schema && schema["target"] is not JsonObject && Supplied("target", cfg["target"]))
                Error(id, $"{named} does not take a target", "target");
            if (!Supplied("target", cfg["target"]) || cfg["fields"] is not JsonObject fields) return;
            var keys = !wired && Text(cfg["target"], "kind") == "literal"
                ? cfg["target"]!["value"] switch { JsonValue v when v.TryGetValue<string>(out _) => ["entity_id"], JsonObject o => o.Select(p => p.Key).ToArray(), _ => [] }
                : targetType != null && VisualTypes.Refines(Root, targetType, "string") ? ["entity_id"] : TargetKeys;
            var service = ServiceName(node) ?? "This service";
            foreach (var (key, operand) in fields) if (keys.Contains(key) && Supplied("field:" + key, operand)) Error(id, $"{service} sets {key} both as a field and in the target", "target");
        }
        void ValidateNode(JsonObject node)
        {
            var spec = Spec(node); if (spec == null) return;
            var cfg = Config(node); var id = Id(node); var kind = Kind(node);
            foreach (var (field, rawRule) in spec["config"]!.AsObject())
            {
                var rule = rawRule!; var value = cfg[field];
                var label = ConfigLabel(rule, field);
                if (value == null && !cfg.ContainsKey(field)) { if (Bool(rule, "required")) Error(id, $"{label} needs a value", field); continue; }
                var valid = Text(rule, "type") switch
                {
                    "array" => value is JsonArray,
                    "object" => value is JsonObject,
                    "integer" => Number(value) is { } n && Math.Truncate(n) == n,
                    "number" => Number(value) != null,
                    "string" => value is JsonValue sv && sv.TryGetValue<string>(out _),
                    "boolean" => value is JsonValue bv && bv.TryGetValue<bool>(out _),
                    _ => false
                };
                if (!valid) { Error(id, $"{label} must be {Words.ConfigKind(Text(rule, "type"))}", field); continue; }
                // Trigger type changes retain inactive fields so drafts and overrides are not lost.
                if (kind == "trigger" && field == "at" && TriggerType(node, snapshot) != "once") continue;
                // Type choices accept every resolvable id, so a parametric type can be selected for narrowing.
                if (Text(rule, "enumSource") == "types") { if (value is not JsonValue tv || !tv.TryGetValue<string>(out var typeId) || !KnownType(typeId)) Error(id, $"{label} must be a known type", field); }
                else if (rule["enum"] is JsonArray choices && !choices.Any(v => JsonNode.DeepEquals(v, value))) Error(id, $"{label} must be {Words.Choices(choices, "one of ")}", field);
                if (Number(value) is { } number && (Number(rule["min"]) is { } min && number < min || Number(rule["max"]) is { } max && number > max)) Error(id, $"{label} must be {Words.Range(Number(rule["min"]), Number(rule["max"]), false)}", field);
                if (value is JsonValue text && text.TryGetValue<string>(out var str) && Text(rule, "pattern") is { Length: > 0 } pattern && !Match(str, pattern)) Error(id, $"{label} is not in the expected format", field);
                // A value already out of format is not also judged as a date (JavaScript's Date.parse accepts 24:00).
                if (value is JsonValue date && date.TryGetValue<string>(out var timestamp) && Text(rule, "format") == "date-time-offset" &&
                    (Text(rule, "pattern") is not { Length: > 0 } format || Match(timestamp, format)) &&
                    !DateTimeOffset.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
                    Error(id, $"{label} must be a date and time with a time zone offset or Z", field);
                if (value is JsonArray list && (Number(rule["minItems"]) is { } minItems && list.Count < minItems || Number(rule["maxItems"]) is { } maxItems && list.Count > maxItems))
                    Error(id, $"{label} must have {Words.Items(Number(rule["minItems"]), Number(rule["maxItems"]))}", field);
            }
            foreach (var port in Strings(spec["requiredExecution"])) if (!outgoing[id].Any(e => FlowPort(node, e) == port)) Error(id, "Execution connection is required", port, port);
            if (Text(spec, "argumentStorage") == "arguments" && cfg["arguments"] is JsonObject arguments)
                foreach (var (name, _) in arguments) if (!DataInputs(node, Owner(node)).ContainsKey(name)) Error(id, $"Unknown input {name}", name);
            var resolved = new Dictionary<string, string?>();
            foreach (var (port, rule) in DataInputs(node, Owner(node)))
            {
                var present = TryGetInputValue(node, port, out var value);
                resolved[port] = Operand(node, port, value, rule!, present);
                if(resolved[port] is {} type)InputTypes[(id,port)]=type;
            }
            if (spec["comparison"] is JsonObject comparison)
            {
                var leftInput = Text(comparison, "leftInput"); var typeConfig = Text(comparison, "typeConfig");
                var left = leftInput != "" ? resolved.GetValueOrDefault(leftInput) : Text(cfg, typeConfig);
                var rightInput = Text(comparison, "rightInput"); var right = resolved.GetValueOrDefault(rightInput);
                if (!string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right) && !CompatibleTypes(left, right) && !CompatibleTypes(right, left)) Error(id, "Both sides of the comparison must have the same type", rightInput);
                var orderingType = Text(comparison, "orderingType");
                if (Strings(comparison["orderingOperators"]).Contains(Text(cfg, Text(comparison, "operatorConfig"))) && left != orderingType)
                    Error(id, $"Less-than and greater-than comparisons need {Words.AType(Root, orderingType)}", leftInput != "" ? leftInput : typeConfig);
            }
            if (Text(spec, "dynamicInputs") == "service") { Service(node, spec, cfg, id); TargetCollisions(node, spec, cfg, id, resolved.GetValueOrDefault("target")); }
            if (cfg.ContainsKey("output") && !Match(Text(cfg, "output"), "^[A-Za-z_][A-Za-z0-9_]{0,63}$")) Error(id, $"{ConfigLabel(spec["config"]?["output"], "output")} must use only letters, digits and underscores and not start with a digit", "output");
            if (kind == "trigger")
            {
                var type = TriggerType(node, snapshot);
                string[] required = type switch { "ha" => ["trigger"], "entity" => ["entity"], "daily" => ["time", "timeZone"], "once" => ["at"], _ => [] };
                foreach (var field in required) if (!cfg.ContainsKey(field) || cfg[field] is JsonValue v && v.TryGetValue<string>(out var s) && s == "") Error(id, $"{ConfigLabel(spec["config"]?[field], field)} needs a value", field);
                if (type == "ha" && cfg["trigger"] is JsonObject trigger && string.IsNullOrWhiteSpace(Text(trigger, "trigger")) && string.IsNullOrWhiteSpace(Text(trigger, "platform"))) Error(id, "Home Assistant trigger needs a platform", "trigger");
            }
            if (kind is "entity_field" or "wait_until" && Text(cfg, "field") == "attribute" && string.IsNullOrWhiteSpace(Text(cfg, "attribute"))) Error(id, "Attribute needs a value", "attribute");
            if (kind == "get_field" && (cfg["pointer"] is not JsonValue pointer || !pointer.TryGetValue<string>(out var p) || !Match(p, "^(?:/(?:[^~]|~[01])*)*$"))) Error(id, "Pointer must be a JSON pointer such as /state", "pointer");
            if (kind == "parallel" && cfg["branches"] is JsonArray branches)
            {
                var ids = new HashSet<string>();
                foreach (var branch in branches) if (branch is not JsonValue v || !v.TryGetValue<string>(out var s) || !Match(s, Text(Root["structures"]?["branchId"], "pattern")) || !ids.Add(s)) Error(id, "Branch ids must be unique stable names", "branches");
            }
            if (Text(spec, "dynamicOutputs") == "loop")
            {
                foreach (var group in new[] { "carry", "exports" })
                {
                    var names = new HashSet<string>();
                    if (cfg[group] is JsonArray declarations && declarations.Any(d => d is not JsonObject)) Error(id, "Invalid loop declaration", group);
                    foreach (var decl in Declarations(node, group))
                    {
                        var name = Text(decl, "name"); var type = Text(decl, "type");
                        if (!Match(name, Text(Root["structures"]?["loopDeclaration"]?["name"], "pattern")) || !names.Add(name) || !KnownType(type)) { Error(id, "Loop declarations require unique names and valid types", group); continue; }
                        Operand(node, $"{group}.{name}.initial", decl["initial"], new JsonObject { ["types"] = new JsonArray(type), ["required"] = true, ["label"] = "Initial " + name });
                    }
                }
                if (kind is "while" or "until")
                {
                    var condition = outgoing[id].Find(e => FlowPort(node, e) == "condition");
                    if (condition == null || !TerminalsValid(Text(condition, "target"), "condition_result", id)) Error(id, "Every condition path must end in a Condition result step", "condition", "condition");
                }
                var body = outgoing[id].Find(e => FlowPort(node, e) == "body");
                if ((Declarations(node, "carry").Any() || Declarations(node, "exports").Any()) && (body == null || !TerminalsValid(Text(body, "target"), "loop_result", id))) Error(id, "Every loop body path must end in a Loop result step when the loop carries or exports values", "body", "body");
            }
            if (kind == "condition_result")
            {
                var last = scopes.GetValueOrDefault(id)?.LastOrDefault();
                if (last == null || last.Value.Port != "condition" || Kind(nodes[last.Value.Owner]) is not ("while" or "until")) Error(id, "A Condition result step must be inside the condition of a While or Until loop");
            }
            if (kind == "loop_result")
            {
                var owner = Owner(node);
                if (owner == null || Text(Spec(owner), "dynamicOutputs") != "loop") Error(id, "A Loop result step must be inside the body of a loop");
                if (cfg["values"] is JsonObject values) foreach (var (name, _) in values) if (!DataInputs(node, owner).ContainsKey(name)) Error(id, $"Unknown loop value {name}", name);
            }
        }
    }
}
