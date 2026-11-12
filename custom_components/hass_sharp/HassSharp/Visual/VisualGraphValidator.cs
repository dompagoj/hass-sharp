using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static HassSharp.VisualNodeCatalog;

namespace HassSharp;

/// <summary>Pure graph v4 validation. Does not compile, attach triggers, or execute data dependencies.</summary>
public static class VisualGraphValidator
{
    public static List<VisualDiagnostic> Validate(JsonObject graph) => new Validation().Run(graph);

    sealed class Validation
    {
        readonly List<VisualDiagnostic> errors = [];
        readonly Dictionary<string, JsonObject> nodes = new(StringComparer.Ordinal);
        readonly Dictionary<string, List<JsonObject>> outgoing = new(StringComparer.Ordinal);
        readonly List<JsonObject> data = [];
        readonly Dictionary<string, (string Owner, string Port)[]> scopes = new(StringComparer.Ordinal);
        readonly Dictionary<(string Node, string Port), JsonObject> incoming = [];
        static readonly string[] Types = Root["types"]!.AsObject().Select(p => p.Key).ToArray();
        void Error(string? node, string message, string? input = null, string? port = null) => errors.Add(new(node, message, InputId: input, PortId: port));
        static bool Bool(JsonNode? node, string key, bool fallback = false) => node is JsonObject o && o[key] is JsonValue v && v.TryGetValue<bool>(out var result) ? result : fallback;
        static double? Number(JsonNode? value) => value is JsonValue v && v.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : null;
        static string[] Strings(JsonNode? value) => (value as JsonArray ?? []).OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : "").ToArray();
        static bool Match(string text, string pattern) => Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        static string Kind(JsonObject node) => Text(node, "kind");
        static string Id(JsonObject node) => Text(node, "id");
        static JsonObject Config(JsonObject node) => node["config"] as JsonObject ?? [];
        static string FlowPort(JsonObject node, JsonObject edge) => Kind(node) == "condition" && Text(edge, "port", "next") == "next" ? "true" : Text(edge, "port", "next");
        static bool ScopePort(JsonObject node, string port) => Strings(Spec(node)?["scopePorts"]).Contains(port) || Kind(node) == "parallel" && port.StartsWith("branch:", StringComparison.Ordinal);
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
            if (Number(graph["version"]) != 4) { Error(null, "Expected graph version 4"); return errors; }
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
                    if (!expected.Any(t => CompatibleTypes(type, t))) Error(Id(target), $"Incompatible input type {type}; expected {string.Join('/', expected)}", tp, tp);
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
        string? Operand(JsonObject node, string port, JsonNode? raw, JsonNode spec)
        {
            if (incoming.TryGetValue((Id(node), port), out var edge)) return Text(DataOutputs(nodes[Text(edge, "source")])[Text(edge, "sourcePort")], "type");
            var required = Bool(spec, "required");
            if (raw == null && !required) return null;
            if (raw is not JsonObject operand || Text(raw, "kind") == "unset")
            {
                if (required) Error(Id(node), "Input is required; connect a source or set a literal", port, port);
                else if (raw != null && raw is not JsonObject) Error(Id(node), "Expected a typed operand", port, port);
                return null;
            }
            string? type = null;
            if (Text(operand, "kind") == "variable")
            {
                var name = Text(operand, "name");
                var candidates = nodes.Values.Where(n => Text(Config(n), "output") == name && DataOutputs(n)["value"] is { } output && Visible(n, node, output)).ToArray();
                type = candidates.Length > 0 ? Text(DataOutputs(candidates[0])["value"], "type") : null;
                if (type == null || candidates.Any(n => Text(DataOutputs(n)["value"], "type") != type)) Error(Id(node), "Variable has no visible producer or conflicting types", port);
            }
            else if (Text(operand, "kind") == "literal")
            {
                type = Text(operand, "type"); var value = operand["value"];
                var valid = type switch
                {
                    "boolean" => value is JsonValue bv && bv.TryGetValue<bool>(out _),
                    "number" => Number(value) != null,
                    "string" => value is JsonValue sv && sv.TryGetValue<string>(out _),
                    "entity_id" => value is JsonValue ev && ev.TryGetValue<string>(out var entity) && Match(entity, Text(Root["types"]?["entity_id"], "pattern")),
                    "object" => value is JsonObject,
                    "array" => value is JsonArray,
                    _ => false
                };
                if (!valid) Error(Id(node), $"Invalid {type} literal", port);
                if (Number(value) is { } number && (Number(spec["min"]) is { } min && number < min || Number(spec["max"]) is { } max && number > max || Bool(spec, "integer") && Math.Truncate(number) != number))
                    Error(Id(node), $"Input must be {(Bool(spec, "integer") ? "an integer " : "")}between {spec["min"]?.ToString() ?? "-infinity"} and {spec["max"]?.ToString() ?? "infinity"}", port);
            }
            else Error(Id(node), "Operand kind must be literal, variable or unset", port);
            if (type != null && !Strings(spec["types"]).Any(t => CompatibleTypes(type, t))) Error(Id(node), $"Incompatible input type {type}; expected {string.Join('/', Strings(spec["types"]))}", port);
            return type;
        }
        bool TerminalsValid(string start, string terminal, string owner, HashSet<string>? active = null)
        {
            active ??= [];
            if (!active.Add(start)) return false;
            var node = nodes[start];
            if (Kind(node) == terminal) return scopes.TryGetValue(start, out var scope) && scope.LastOrDefault().Owner == owner;
            if (Kind(node) == "stop") return terminal == "loop_result";
            var next = outgoing[start].Where(e => !ScopePort(node, FlowPort(node, e))).ToArray();
            if (next.Length == 0) return false;
            if (Kind(node) is "if" or "condition" && !new[] { "true", "false" }.All(p => next.Any(e => FlowPort(node, e) == p))) return false;
            return next.All(e => TerminalsValid(Text(e, "target"), terminal, owner, new(active)));
        }
        void ValidateNode(JsonObject node)
        {
            var spec = Spec(node); if (spec == null) return;
            var cfg = Config(node); var id = Id(node); var kind = Kind(node);
            foreach (var (field, rawRule) in spec["config"]!.AsObject())
            {
                var rule = rawRule!; var value = cfg[field];
                if (value == null && !cfg.ContainsKey(field)) { if (Bool(rule, "required")) Error(id, field + " is required", field); continue; }
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
                if (!valid) { Error(id, $"{field} must be {Text(rule, "type")}", field); continue; }
                if (rule["enum"] is JsonArray choices && !choices.Any(v => JsonNode.DeepEquals(v, value))) Error(id, field + " must be one of " + string.Join(", ", choices), field);
                if (Number(value) is { } number && (Number(rule["min"]) is { } min && number < min || Number(rule["max"]) is { } max && number > max)) Error(id, $"{field} must be between {rule["min"]} and {rule["max"]}", field);
                if (value is JsonValue text && text.TryGetValue<string>(out var str) && Text(rule, "pattern") is { Length: > 0 } pattern && !Match(str, pattern)) Error(id, field + " has invalid format", field);
                if (value is JsonArray list && (Number(rule["minItems"]) is { } minItems && list.Count < minItems || Number(rule["maxItems"]) is { } maxItems && list.Count > maxItems)) Error(id, field + " item count is out of range", field);
            }
            foreach (var port in Strings(spec["requiredExecution"])) if (!outgoing[id].Any(e => FlowPort(node, e) == port)) Error(id, "Execution connection is required", port, port);
            var resolved = new Dictionary<string, string?>();
            foreach (var (port, rule) in DataInputs(node, Owner(node))) resolved[port] = Operand(node, port, InputValue(node, port), rule!);
            if (kind == "compare" && resolved.GetValueOrDefault("left") is { } left && resolved.GetValueOrDefault("right") is { } right)
            {
                if (!CompatibleTypes(left, right) && !CompatibleTypes(right, left)) Error(id, "Comparison operands must have matching types", "right");
                if (Text(cfg, "operator", "eq") is not ("eq" or "ne") && left != "number") Error(id, "Ordering comparison requires number type", "left");
            }
            if (cfg.ContainsKey("output") && !Match(Text(cfg, "output"), "^[A-Za-z_][A-Za-z0-9_]{0,63}$")) Error(id, "Invalid variable output name", "output");
            if (kind == "trigger")
            {
                var type = Text(cfg, "type", "entity");
                string[] required = type switch { "ha" => ["trigger"], "entity" => ["entity"], "daily" => ["time", "timeZone"], "once" => ["at"], _ => [] };
                foreach (var field in required) if (!cfg.ContainsKey(field) || cfg[field] is JsonValue v && v.TryGetValue<string>(out var s) && s == "") Error(id, field + " is required", field);
                if (type == "ha" && cfg["trigger"] is JsonObject trigger && string.IsNullOrWhiteSpace(Text(trigger, "trigger")) && string.IsNullOrWhiteSpace(Text(trigger, "platform"))) Error(id, "Native trigger requires a platform", "trigger");
            }
            if (kind is "read_entity" or "wait_until" && Text(cfg, "field") == "attribute" && string.IsNullOrWhiteSpace(Text(cfg, "attribute"))) Error(id, "Attribute name is required", "attribute");
            if (kind == "get_field" && (cfg["pointer"] is not JsonValue pointer || !pointer.TryGetValue<string>(out var p) || !Match(p, "^(?:/(?:[^~]|~[01])*)*$"))) Error(id, "Invalid JSON pointer", "pointer");
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
                        if (!Match(name, Text(Root["structures"]?["loopDeclaration"]?["name"], "pattern")) || !names.Add(name) || !Types.Contains(type)) { Error(id, "Loop declarations require unique names and valid types", group); continue; }
                        Operand(node, $"{group}.{name}.initial", decl["initial"], new JsonObject { ["types"] = new JsonArray(type), ["required"] = true });
                    }
                }
                if (kind is "while" or "until")
                {
                    var condition = outgoing[id].Find(e => FlowPort(node, e) == "condition");
                    if (condition == null || !TerminalsValid(Text(condition, "target"), "condition_result", id)) Error(id, "Every condition path must end in Boolean condition_result", "condition", "condition");
                }
                var body = outgoing[id].Find(e => FlowPort(node, e) == "body");
                if ((Declarations(node, "carry").Any() || Declarations(node, "exports").Any()) && (body == null || !TerminalsValid(Text(body, "target"), "loop_result", id))) Error(id, "Every loop body path must end in loop_result when carry/exports are declared", "body", "body");
            }
            if (kind == "condition_result")
            {
                var last = scopes.GetValueOrDefault(id)?.LastOrDefault();
                if (last == null || last.Value.Port != "condition" || Kind(nodes[last.Value.Owner]) is not ("while" or "until")) Error(id, "condition_result must be inside a loop condition scope");
            }
            if (kind == "loop_result")
            {
                var owner = Owner(node);
                if (owner == null || Text(Spec(owner), "dynamicOutputs") != "loop") Error(id, "loop_result must be inside a sequential loop body scope");
                if (cfg["values"] is JsonObject values) foreach (var (name, _) in values) if (!DataInputs(node, owner).ContainsKey(name)) Error(id, "Unknown loop carry/export input", name);
            }
        }
    }
}
