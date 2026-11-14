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

    /// <summary>The keys a service target may hold; a node with a target may not also have a field with one of them.</summary>
    internal static readonly string[] TargetKeys = ["entity_id", "device_id", "area_id", "floor_id", "label_id"];
    /// <summary>The message for a target object holding another key; the runtime throws the same sentence.</summary>
    internal static string TargetKeyMessage(string key) => $"Target can only contain entity_id, device_id, area_id, floor_id and label_id; found {key}";

    /// <summary>
    /// The codes of structural diagnostics, identical in <c>contract.ts</c>. The editor's connection check lives in
    /// <c>contract.ts</c> only (<c>connectData</c>) and does not read codes: it refuses a wire for any diagnostic the wire
    /// adds (compared by full content), any diagnostic on the input it fills or about the output it reads, and a data
    /// cycle the wire itself lies on.
    /// </summary>
    public static readonly string[] Codes =
    [
        "graph.version", "graph.name", "graph.shape", "graph.limit", "graph.noTrigger",
        "node.invalid", "node.duplicate", "node.unknownKind", "node.unreachable", "limit.nesting",
        "edge.invalid", "edge.duplicate", "edge.endpoint", "edge.unknownKind", "edge.valueWirePins", "edge.orderWirePins", "edge.orderPinName",
        "port.unknownExecution", "port.executionTaken", "port.unknownOutput", "port.unknownInput", "port.inputTaken", "trigger.incoming",
        "cycle.execution", "cycle.data", "scope.execution", "scope.data", "scope.conditionResult", "scope.loopResult",
        "flow.pure", "pure.unused",
    ];

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
        /// <summary>A key or value people did not name, in words: <c>needs_action</c> → <c>Needs action</c>; never empty.</summary>
        internal static string KeyWords(string key) => DomainWords(key) is var words && Trimmed(words).Length > 0 ? words : "Unnamed";
        /// <summary>A step as people see it on the canvas: its own label, else its kind's catalog label, else its kind in words.</summary>
        internal static string StepName(JsonObject node, JsonNode? spec) =>
            node["label"] is JsonValue l && l.TryGetValue<string>(out var label) && Trimmed(label).Length > 0 ? Trimmed(label)
            : Text(spec, "label") is { } kindLabel && Trimmed(kindLabel).Length > 0 ? Trimmed(kindLabel)
            : KeyWords(node["kind"] is JsonValue k && k.TryGetValue<string>(out var kind) ? kind : "");
        /// <summary>A step that execution never reaches, and what to do about it.</summary>
        internal static string Unreachable(string step) => $"{step} is not connected into the flow; connect a step to its execution input";
        /// <summary>A step order wire into or out of a value node.</summary>
        internal static string NotInFlow(string node) => $"{node} is a value and is not part of the run order";
        /// <summary>A node on a cycle of value wires.</summary>
        internal static string OwnResult(string node) => $"{node} would depend on its own result";
        /// <summary>A value node none of whose outputs is connected.</summary>
        internal static string Unused(string node) => $"{node} is not used";
        /// <summary>A step reads a value node that uses an output the step could not read over a wire of its own.</summary>
        internal static string PureScope(string node, string source, string output, string step) =>
            $"{node} uses {source} · {output}, which {step} cannot use here: it is on the other side of a loop or parallel branch boundary";
        /// <summary>A name or id quoted as written, without surrounding whitespace; <c>Unnamed</c> when nothing is left.</summary>
        internal static string Name(string name) => Trimmed(name) is { Length: > 0 } trimmed ? trimmed : "Unnamed";
        /// <summary>JavaScript's whitespace (as <c>String.prototype.trim</c> removes it), spelled out: .NET's differs at U+FEFF.</summary>
        static bool Blank(char c) => c is '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or '\u00a0' or '\u1680' or (>= '\u2000' and <= '\u200a') or '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000' or '\ufeff';
        static string Trimmed(string text)
        {
            int start = 0, end = text.Length;
            while (start < end && Blank(text[start])) start++;
            while (end > start && Blank(text[end - 1])) end--;
            return text[start..end];
        }
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
        internal static string Or(IEnumerable<string> items)
        {
            var list = items.ToList();
            return list.Count <= 1 ? string.Concat(list) : string.Join(", ", list.Take(list.Count - 1)) + " or " + list[^1];
        }
        /// <summary>
        /// A number as JavaScript's <c>String(n)</c> writes it, so both validators print it alike: plain digits from
        /// 1e-6 (inclusive) up to 1e21 (exclusive), exponent form (<c>1e-7</c>, <c>1.5e+21</c>) outside that range.
        /// </summary>
        internal static string Number(double value)
        {
            if (value == 0) return "0";
            if (double.IsNaN(value)) return "NaN";
            if (double.IsInfinity(value)) return value > 0 ? "Infinity" : "-Infinity";
            var sign = value < 0 ? "-" : "";
            var shortest = Math.Abs(value).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            var exponent = 0; var e = shortest.IndexOfAny(['E', 'e']);
            if (e >= 0) { exponent = int.Parse(shortest[(e + 1)..], System.Globalization.CultureInfo.InvariantCulture); shortest = shortest[..e]; }
            var dot = shortest.IndexOf('.');
            var whole = dot < 0 ? shortest : shortest[..dot]; var fraction = dot < 0 ? "" : shortest[(dot + 1)..];
            var digits = whole + fraction; var point = whole.Length + exponent;
            var lead = digits.Length - digits.TrimStart('0').Length; digits = digits[lead..].TrimEnd('0'); point -= lead;
            int k = digits.Length, n = point;
            if (k <= n && n <= 21) return sign + digits + new string('0', n - k);
            if (0 < n && n <= 21) return sign + digits[..n] + "." + digits[n..];
            if (-6 < n && n <= 0) return sign + "0." + new string('0', -n) + digits;
            var power = (n - 1 >= 0 ? "+" : "-") + Math.Abs(n - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return sign + (k == 1 ? digits : digits[..1] + "." + digits[1..]) + "e" + power;
        }
        /// <summary>A value in words: text as words (<c>twice_daily</c> → <c>Twice daily</c>), numbers as JavaScript writes them, Yes or No.</summary>
        internal static string Show(JsonNode? value) => value switch
        {
            null => "Nothing",
            JsonValue text when text.TryGetValue<string>(out var s) => s.Length == 0 ? "Empty text" : KeyWords(s),
            JsonValue flag when flag.TryGetValue<bool>(out var b) => b ? "Yes" : "No",
            JsonValue number when number.GetValueKind() == System.Text.Json.JsonValueKind.Number => Number(double.Parse(number.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture)),
            _ => value.ToJsonString(),
        };
        /// <summary>The allowed values by their labels where the input has them, else in words: one, a short list, or a reference to the listed options.</summary>
        internal static string Choices(JsonArray values, JsonNode? labels, string many)
        {
            var named = labels is JsonArray list && list.Count == values.Count && list.All(l => l is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0)
                ? list.Select(l => l!.GetValue<string>()).ToList() : values.Select(Show).ToList();
            return named.Count == 1 ? named[0] : named.Count <= 6 ? many + Or(named) : many + "the listed options";
        }
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
            "string" => "text", "boolean" => "yes or no", _ => "the right kind of value",
        };
        /// <summary>
        /// The label people see for an input: its spec label, else (or when the label is only the key) the key in
        /// words. List items count from 1, for an index of any length (exactly, as TS does with BigInt).
        /// </summary>
        internal static string PortLabel(JsonNode? spec, string port)
        {
            if (port.StartsWith("item:", StringComparison.Ordinal))
                return port.Length > 5 && port[5..].All(char.IsAsciiDigit) ? "Item " + (System.Numerics.BigInteger.Parse(port[5..], System.Globalization.CultureInfo.InvariantCulture) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "Item";
            var key = port.StartsWith("field:", StringComparison.Ordinal) || port.StartsWith("carry:", StringComparison.Ordinal) ? port[6..]
                : port.StartsWith("export:", StringComparison.Ordinal) ? port[7..] : port;
            return Text(spec, "label") is { Length: > 0 } label && label != key && label.Trim().Length > 0 ? label : KeyWords(key);
        }
        /// <summary>A type mismatch: the accepted types (named, at most four) and the one given, unless that is unknown.</summary>
        internal static string Incompatible(JsonObject catalog, string subject, string type, IEnumerable<string> expected)
        {
            if (VisualTypes.Chain(catalog, type).Count == 0) return $"{subject} is connected to a value of unknown type";
            var names = expected.Select(t => TypeLabel(catalog, t)).Distinct().ToList();
            return names.Count is > 0 and <= 4 ? $"{subject} accepts {Or(names)}, not {TypeLabel(catalog, type)}" : $"{subject} does not accept {TypeLabel(catalog, type)}";
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
        void Error(string? node, string message, string? input = null, string? port = null, string? code = null) => errors.Add(new(node, message, InputId: input, PortId: port, Code: code));
        static bool Bool(JsonNode? node, string key, bool fallback = false) => node is JsonObject o && o[key] is JsonValue v && v.TryGetValue<bool>(out var result) ? result : fallback;
        static double? Number(JsonNode? value) => value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number && double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
        static string[] Strings(JsonNode? value) => (value as JsonArray ?? []).OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : "").ToArray();
        static bool Match(string text, string pattern) => VisualTypes.Matches(text, pattern);
        /// <summary>Checks a value against an input's format rule; a rule that cannot be compiled is reported once, not thrown.</summary>
        bool Formatted(string? node, string subject, string text, string pattern, string? input)
        {
            if (VisualTypes.TryMatches(text, pattern) is { } matched) return matched;
            Error(node, $"{subject} has a format rule that cannot be checked", input);
            return true;
        }
        /// <summary>The service a service node calls, named in its messages; null for other nodes or without a service id.</summary>
        string? ServiceName(JsonObject node) => Text(Spec(node), "dynamicInputs") == "service" && Text(Config(node), "service") is { Length: > 0 } service ? service : null;
        string Prefix(JsonObject node) => ServiceName(node) is { } service ? service + ": " : "";
        string ConfigLabel(JsonNode? rule, string field) => Text(rule, "label") is { Length: > 0 } label && label.Trim().Length > 0 ? label : Words.KeyWords(field);
        static string Kind(JsonObject node) => Text(node, "kind");
        static string Id(JsonObject node) => Text(node, "id");
        static JsonObject Config(JsonObject node) => node["config"] as JsonObject ?? [];
        static string FlowPort(JsonObject node, JsonObject edge) => Kind(node) == "condition" && Text(edge, "port", "next") == "next" ? "true" : Text(edge, "port", "next");
        bool ScopePort(JsonObject node, string port) => Strings(Spec(node)?["scopePorts"]).Contains(port) || Kind(node) == "parallel" && port.StartsWith("branch:", StringComparison.Ordinal);
        static bool Ancestor((string Owner, string Port)[] a, (string Owner, string Port)[] b) => a.Length <= b.Length && a.SequenceEqual(b.Take(a.Length));
        JsonObject? Owner(JsonObject node) => scopes.TryGetValue(Id(node), out var scope) && scope.LastOrDefault() is var last && last.Port == "body" ? nodes[last.Owner] : null;
        bool Pure(JsonObject node) => IsPure(Spec(node));
        bool Visible(JsonObject source, JsonObject target, JsonNode port) => scopes.TryGetValue(Id(target), out var b) && VisibleIn(source, b, port);
        /// <summary>Whether an output of <paramref name="source"/> may be read by a step in scope <paramref name="b"/>.</summary>
        bool VisibleIn(JsonObject source, (string Owner, string Port)[] b, JsonNode port)
        {
            if (!scopes.TryGetValue(Id(source), out var a) || !Ancestor(a, b)) return false;
            var owned = b.Length > a.Length && b[a.Length].Owner == Id(source);
            return Text(port, "visibility") switch
            {
                "outside" => !owned,
                "body" => owned && b[a.Length].Port == "body",
                "loop" => owned && b[a.Length].Port is "body" or "condition",
                _ => true
            };
        }
        /// <summary>A wire whose value is not visible where it arrives.</summary>
        internal static string ScopeData(string subject) => $"{subject} cannot use this value here: it is on the other side of a loop or parallel branch boundary";
        /// <summary>
        /// The step outputs a value node reads, directly or through other value nodes: each once, in the order of the
        /// graph's value wires, depth first. A step reading the value node must be able to read each of them.
        /// </summary>
        List<(JsonObject Node, string Port, JsonObject Output)> PureSources(JsonObject pure)
        {
            var found = new List<(JsonObject, string, JsonObject)>(); var seen = new HashSet<string>(StringComparer.Ordinal) { Id(pure) };
            void Visit(JsonObject node)
            {
                foreach (var edge in data.Where(e => Text(e, "target") == Id(node)))
                {
                    var source = nodes[Text(edge, "source")]; var port = Text(edge, "sourcePort");
                    if (Pure(source)) { if (seen.Add(Id(source))) Visit(source); }
                    else if (DataOutputs(source)[port] is JsonObject output && !found.Any(f => f.Item1 == source && f.Item2 == port)) found.Add((source, port, output));
                }
            }
            Visit(pure);
            return found;
        }
        /// <summary>Where a node's values are worked out: a step's own scope; for a value node, the scope of every step that reads it, through other value nodes.</summary>
        List<(string Owner, string Port)[]> ReaderScopes(JsonObject node)
        {
            var found = new List<(string Owner, string Port)[]>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            void Visit(JsonObject current)
            {
                if (!seen.Add(Id(current))) return;
                if (!Pure(current)) { if (scopes.TryGetValue(Id(current), out var scope)) found.Add(scope); return; }
                foreach (var edge in data.Where(e => Text(e, "source") == Id(current))) Visit(nodes[Text(edge, "target")]);
            }
            Visit(node);
            return found;
        }
        /// <summary>
        /// Whether a variable a node publishes can be read by <paramref name="reader"/>: from every scope the reader is worked out
        /// in, the producer must be visible as a wire from it would be; a value node publishes its variable where it is worked out.
        /// </summary>
        bool VariableVisible(JsonObject producer, JsonObject reader, JsonNode output)
        {
            var readers = ReaderScopes(reader);
            if (readers.Count == 0) return false;
            if (!Pure(producer)) return readers.All(scope => VisibleIn(producer, scope, output));
            var producers = ReaderScopes(producer);
            return readers.All(scope => producers.Any(at => Ancestor(at, scope)));
        }
        public List<VisualDiagnostic> Run(JsonObject graph)
        {
            var version = Number(Root["graphVersion"]);
            if (Number(graph["version"]) != version) { Error(null, $"Expected graph version {version}", code: "graph.version"); return errors; }
            if (string.IsNullOrWhiteSpace(Text(graph, "name"))) Error(null, "Graph name is required", code: "graph.name");
            if (graph["nodes"] is not JsonArray ns || graph["edges"] is not JsonArray es) { Error(null, "Nodes and edges arrays are required", code: "graph.shape"); return errors; }
            if (ns.Count is < 1 or > 256 || es.Count > 512) { Error(null, "Graph limit: 1–256 nodes and at most 512 edges", code: "graph.limit"); return errors; }
            foreach (var raw in ns)
            {
                if (raw is not JsonObject node || Id(node) == "" || node["config"] is not JsonObject) { Error(null, "Each node requires id and config", code: "node.invalid"); continue; }
                if (!nodes.TryAdd(Id(node), node)) Error(Id(node), "Duplicate node id", code: "node.duplicate");
                if (Spec(node) == null) Error(Id(node), "Unknown node kind", code: "node.unknownKind");
            }
            foreach (var id in nodes.Keys) outgoing[id] = [];
            var edgeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in es)
            {
                if (raw is not JsonObject edge || Id(edge) == "") { Error(null, "Edge id is required", code: "edge.invalid"); continue; }
                var source = Text(edge, "source"); var target = Text(edge, "target");
                if (!edgeIds.Add(Id(edge))) Error(source, "Duplicate edge id", code: "edge.duplicate");
                if (!nodes.ContainsKey(source) || !nodes.ContainsKey(target)) { Error(null, "Edge endpoint missing", code: "edge.endpoint"); continue; }
                if (Kind(edge) == "data")
                {
                    if (edge.ContainsKey("port") || Text(edge, "sourcePort") == "" || Text(edge, "targetPort") == "") Error(source, "This value wire does not join two value pins; connect value pins to value pins", code: "edge.valueWirePins");
                    else data.Add(edge);
                }
                else if (!edge.ContainsKey("kind") || Kind(edge) == "execution")
                {
                    if (edge.ContainsKey("sourcePort") || edge.ContainsKey("targetPort")) Error(source, "This step order wire names value pins; connect step order pins to step order pins", code: "edge.orderWirePins");
                    if (edge.ContainsKey("port") && (edge["port"] is not JsonValue pv || !pv.TryGetValue<string>(out _))) { Error(source, "This step order wire leaves from a pin without a valid name", code: "edge.orderPinName"); continue; }
                    // A value node has no execution pins; the wire is reported on it and otherwise ignored.
                    if (Pure(nodes[source]) || Pure(nodes[target]))
                    {
                        if (Pure(nodes[source])) Error(source, Words.NotInFlow(Words.StepName(nodes[source], Spec(nodes[source]))), code: "flow.pure");
                        if (Pure(nodes[target]) && target != source) Error(target, Words.NotInFlow(Words.StepName(nodes[target], Spec(nodes[target]))), code: "flow.pure");
                        continue;
                    }
                    var node = nodes[source]; var port = FlowPort(node, edge);
                    if (!ExecutionPorts(node).Contains(port)) Error(source, "Unknown execution port", port: port, code: "port.unknownExecution");
                    if (outgoing[source].Any(e => FlowPort(node, e) == port)) Error(source, "Only one execution edge per port", port: port, code: "port.executionTaken");
                    if (Kind(nodes[target]) == "trigger") Error(target, "Trigger cannot have incoming execution edges", code: "trigger.incoming");
                    outgoing[source].Add(edge);
                }
                else Error(source, "This wire is neither a step order wire nor a value wire", code: "edge.unknownKind");
            }
            Cycles(outgoing.Values.SelectMany(e => e).ToList(), "Execution"); Cycles(data, "Data");
            var roots = nodes.Values.Where(n => Kind(n) == "trigger").ToArray();
            if (roots.Length == 0) Error(null, "At least one trigger is required", code: "graph.noTrigger");
            foreach (var root in roots) Scope(Id(root), []);
            // A value node is never reached by execution; it is a warning when nothing reads it.
            foreach (var (id, node) in nodes)
                if (Pure(node)) { if (!data.Any(e => Text(e, "source") == id)) errors.Add(new(id, Words.Unused(Words.StepName(node, Spec(node))), 2, Code: "pure.unused")); }
                else if (!scopes.ContainsKey(id)) Error(id, Words.Unreachable(Words.StepName(node, Spec(node))), code: "node.unreachable");
            foreach (var edge in data)
            {
                var source = nodes[Text(edge, "source")]; var target = nodes[Text(edge, "target")];
                var sp = Text(edge, "sourcePort"); var tp = Text(edge, "targetPort");
                var output = DataOutputs(source)[sp]; var input = DataInputs(target, Owner(target))[tp];
                if (output == null) Error(Id(source), "Unknown data output port", port: sp, code: "port.unknownOutput");
                if (input == null) Error(Id(target), $"This value wire goes into {Words.PortLabel(null, tp)}, which this step does not have", tp, tp, "port.unknownInput");
                if (!incoming.TryAdd((Id(target), tp), edge)) Error(Id(target), "Only one data source per input", tp, tp, "port.inputTaken");
                if (output != null && input != null)
                {
                    var type = Text(output, "type"); var expected = Strings(input["types"]);
                    if (!expected.Any(t => CompatibleTypes(type, t))) Error(Id(target), Words.Incompatible(Root, Prefix(target) + Words.PortLabel(input, tp), type, expected), tp, tp);
                    // A wire to or from a step not in the flow yet gets nothing: that step carries node.unreachable.
                    if (scopes.ContainsKey(Id(source)) && scopes.ContainsKey(Id(target)) && !Visible(source, target, output))
                        Error(Id(target), ScopeData(Prefix(target) + Words.PortLabel(input, tp)), tp, tp, "scope.data");
                    // A step reading a value node reads, in effect, every step output that value node uses.
                    if (Pure(source) && !Pure(target) && scopes.ContainsKey(Id(target)))
                        foreach (var (producer, port, spec) in PureSources(source))
                            if (scopes.ContainsKey(Id(producer)) && !Visible(producer, target, spec))
                                Error(Id(target), Words.PureScope(Words.StepName(source, Spec(source)), Words.StepName(producer, Spec(producer)), Words.PortLabel(spec, port), Words.StepName(target, Spec(target))), tp, tp, "scope.data");
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
                if (active.Contains(id)) { if (label == "Data") Error(id, Words.OwnResult(Words.StepName(nodes[id], Spec(nodes[id]))), code: "cycle.data"); else Error(id, "Execution cycle is forbidden", code: "cycle.execution"); return; }
                if (done.Contains(id)) return;
                active.Add(id);
                foreach (var edge in edges.Where(e => Text(e, "source") == id)) Visit(Text(edge, "target"));
                active.Remove(id); done.Add(id);
            }
            foreach (var id in nodes.Keys) Visit(id);
        }
        void Scope(string id, (string Owner, string Port)[] path)
        {
            if (path.Length > 8) { Error(id, "Structured nesting limit is 8", code: "limit.nesting"); return; }
            if (scopes.TryGetValue(id, out var old)) { if (!old.SequenceEqual(path)) Error(id, "A wire cannot go into or out of a loop or parallel branch from outside it", code: "scope.execution"); return; }
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
                if (required) Error(Id(node), ServiceName(node) is { } service ? $"{service} needs a value for {label}" : $"{label} is required", port, port);
                else if (present && raw is not JsonObject) Error(Id(node), $"{subject} has an invalid value", port, port);
                return null;
            }
            string? type = null;
            if (Text(operand, "kind") == "variable")
            {
                var name = Text(operand, "name");
                if (!Match(name, Text(Root["structures"]?["loopDeclaration"]?["name"], "pattern"))) { Error(Id(node), $"{subject} uses a variable without a valid name", port, port); return null; }
                var candidates = nodes.Values.Where(n => Config(n)["output"] is JsonValue outputName && outputName.TryGetValue<string>(out var named) && named == name && DataOutputs(n)["value"] is { } output && VariableVisible(n, node, output)).ToArray();
                type = candidates.Length > 0 ? Text(DataOutputs(candidates[0])["value"], "type") : null;
                if (type == null || candidates.Any(n => Text(DataOutputs(n)["value"], "type") != type)) Error(Id(node), $"{subject} uses variable {name}, which no earlier step provides with a single type", port);
            }
            else if (Text(operand, "kind") == "literal")
            {
                type = Text(operand, "type", "unknown"); var value = operand["value"];
                var valid = ValidLiteral(type, value, operand.ContainsKey("value"), snapshot);
                if (!valid) Error(Id(node), operand["type"] is not JsonValue tv || !tv.TryGetValue<string>(out var named) || named.Length == 0 ? $"{subject} has a value without a type"
                    : !KnownType(type) ? $"{subject} has a value of unknown type {Words.Name(type)}" : $"{subject} is not a valid {Words.TypeLabel(Root, type)} value", port);
                if (Number(value) is { } number && (Number(spec["min"]) is { } min && number < min || Number(spec["max"]) is { } max && number > max || Bool(spec, "integer") && Math.Truncate(number) != number))
                    Error(Id(node), $"{subject} must be {Words.Range(Number(spec["min"]), Number(spec["max"]), Bool(spec, "integer"))}", port);
                // Input constraints restrict authored values only; a wire is constrained by its type. When any allowed
                // value is itself a list (a constant list), only a value equal to one of them is accepted. When every
                // allowed value is a scalar, a list, where the input accepts one, is checked item by item (a multiple
                // select; an empty list passes and minItems decides), and anything else must equal an allowed value.
                var types = Strings(spec["types"]);
                if (spec["enum"] is JsonArray allowed && !allowed.Any(v => JsonNode.DeepEquals(v, value)))
                {
                    var labels = spec["enumLabels"];
                    if (value is JsonArray items && types.Contains("array") && !allowed.Any(v => v is JsonArray))
                    {
                        if (!items.All(item => allowed.Any(v => JsonNode.DeepEquals(v, item))))
                            Error(Id(node), $"{subject} may only contain {Words.Choices(allowed, labels, "")}", port);
                    }
                    else Error(Id(node), $"{subject} must be {Words.Choices(allowed, labels, "one of ")}", port);
                }
                // Items of a list of entities (an input taking entity ids or a list of them, as a multiple entity
                // selector does) must be entity ids of an accepted domain.
                var entityTypes = types.Where(t => t != "array").ToArray();
                if (value is JsonArray entities && types.Contains("array") && entityTypes.Length > 0 && entityTypes.All(t => VisualTypes.Refines(Root, t, "entity_id")) &&
                    !entities.All(item => entityTypes.Any(t => ValidLiteral(t, item, true, snapshot))))
                    Error(Id(node), $"{subject} may only contain {Words.Or(entityTypes.Select(t => Words.TypeLabel(Root, t)).Distinct())} values", port);
                if (Text(spec, "pattern") is { Length: > 0 } pattern && value is JsonValue sv && sv.TryGetValue<string>(out var str) && !Formatted(Id(node), subject, str, pattern, port)) Error(Id(node), $"{subject} is not in the expected format", port);
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
            if (services.Items[service] is not JsonObject schema) { errors.Add(new(id, $"{service} is not available in this Home Assistant, so its fields cannot be checked", 2, InputId: "service")); return; }
            var known = schema["fields"] as JsonObject ?? [];
            var fields = cfg["fields"] as JsonObject ?? [];
            foreach (var (key, _) in fields) if (!known.ContainsKey(key)) Error(id, $"{service} has no field {Words.Name(key)}", "field:" + key);
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
                    Error(id, $"{service} only accepts {accepted} entities; {Words.Name(entityId)} is not " + (entityId.IndexOf('.') > 0 ? "one" : "an entity"), "target");
        }
        /// <summary>
        /// The target rules of a service node, with or without services. A target the service does not take is an
        /// error (with services only). A target object holds only <see cref="TargetKeys"/>, as the runtime enforces.
        /// A node whose target is set in any form (a value, a connection or a variable) may not also have a field named
        /// like a target key; a node without a target may (homeassistant.update_entity takes entity_id as a field).
        /// </summary>
        void TargetRules(JsonObject node, JsonObject spec, JsonObject cfg, string id)
        {
            var wired = incoming.ContainsKey((id, "target"));
            var targetSet = wired || cfg["target"] is JsonObject set && Text(set, "kind") != "unset";
            if (snapshot.Services is { } services && ServiceName(node) is { } named && Match(named, Text(spec["config"]?["service"], "pattern")) &&
                services.Items[named] is JsonObject schema && schema["target"] is not JsonObject && targetSet)
                Error(id, $"{named} does not take a target", "target");
            if (!targetSet) return;
            if (!wired && cfg["target"] is JsonObject operand && Text(operand, "kind") == "literal" && operand["value"] is JsonObject value)
                foreach (var (key, _) in value) if (!TargetKeys.Contains(key)) Error(id, TargetKeyMessage(key), "target");
            var service = ServiceName(node) ?? "This service";
            if (cfg["fields"] is JsonObject fields)
                foreach (var (key, _) in fields) if (TargetKeys.Contains(key)) Error(id, $"{service} has a target, so it cannot also have a field named {key}", "target");
        }
        void ValidateNode(JsonObject node)
        {
            var spec = Spec(node); if (spec == null) return;
            var cfg = Config(node); var id = Id(node); var kind = Kind(node);
            foreach (var (field, rawRule) in spec["config"]!.AsObject())
            {
                var rule = rawRule!; var value = cfg[field];
                var label = ConfigLabel(rule, field);
                if (value == null && !cfg.ContainsKey(field)) { if (Bool(rule, "required")) Error(id, $"{label} is required", field); continue; }
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
                else if (rule["enum"] is JsonArray choices && !choices.Any(v => JsonNode.DeepEquals(v, value))) Error(id, $"{label} must be {Words.Choices(choices, rule["enumLabels"], "one of ")}", field);
                if (Number(value) is { } number && (Number(rule["min"]) is { } min && number < min || Number(rule["max"]) is { } max && number > max)) Error(id, $"{label} must be {Words.Range(Number(rule["min"]), Number(rule["max"]), false)}", field);
                if (value is JsonValue text && text.TryGetValue<string>(out var str) && Text(rule, "pattern") is { Length: > 0 } pattern && !Formatted(id, label, str, pattern, field)) Error(id, $"{label} is not in the expected format", field);
                // A value already out of format is not also judged as a date (JavaScript's Date.parse accepts 24:00).
                if (value is JsonValue date && date.TryGetValue<string>(out var timestamp) && Text(rule, "format") == "date-time-offset" &&
                    (Text(rule, "pattern") is not { Length: > 0 } format || VisualTypes.TryMatches(timestamp, format) != false) &&
                    !DateTimeOffset.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
                    Error(id, $"{label} must be a date and time with a time zone offset or Z", field);
                if (value is JsonArray list && (Number(rule["minItems"]) is { } minItems && list.Count < minItems || Number(rule["maxItems"]) is { } maxItems && list.Count > maxItems))
                    Error(id, $"{label} must have {Words.Items(Number(rule["minItems"]), Number(rule["maxItems"]))}", field);
            }
            foreach (var port in Strings(spec["requiredExecution"])) if (!outgoing[id].Any(e => FlowPort(node, e) == port)) Error(id, "Execution connection is required", port, port);
            if (Text(spec, "argumentStorage") == "arguments" && cfg["arguments"] is JsonObject arguments)
                foreach (var (name, _) in arguments) if (!DataInputs(node, Owner(node)).ContainsKey(name)) Error(id, $"Unknown input {Words.Name(name)}", name);
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
                    Error(id, $"Less-than and greater-than comparisons need {Words.TypeLabel(Root, orderingType)} values", leftInput != "" ? leftInput : typeConfig);
            }
            if (Text(spec, "dynamicInputs") == "service") { Service(node, spec, cfg, id); TargetRules(node, spec, cfg, id); }
            if (cfg.ContainsKey("output") && !Match(Text(cfg, "output"), "^[A-Za-z_][A-Za-z0-9_]{0,63}$")) Error(id, $"{ConfigLabel(spec["config"]?["output"], "output")} must use only letters, digits and underscores and not start with a digit", "output");
            if (kind == "trigger")
            {
                var type = TriggerType(node, snapshot);
                string[] required = type switch { "ha" => ["trigger"], "entity" => ["entity"], "daily" => ["time", "timeZone"], "once" => ["at"], _ => [] };
                foreach (var field in required) if (!cfg.ContainsKey(field) || cfg[field] is JsonValue v && v.TryGetValue<string>(out var s) && s == "") Error(id, $"{ConfigLabel(spec["config"]?[field], field)} is required", field);
                if (type == "ha" && cfg["trigger"] is JsonObject trigger && string.IsNullOrWhiteSpace(Text(trigger, "trigger")) && string.IsNullOrWhiteSpace(Text(trigger, "platform"))) Error(id, "Home Assistant trigger needs a platform", "trigger");
            }
            if (kind is "entity_field" or "wait_until" && Text(cfg, "field") == "attribute" && string.IsNullOrWhiteSpace(Text(cfg, "attribute"))) Error(id, "Attribute is required", "attribute");
            // A path that starts with / can still be wrong in one way: a ~ that is not ~0 (a ~ in a key) or ~1 (a / in a key).
            if (kind == "get_field" && (cfg["pointer"] is not JsonValue pointer || !pointer.TryGetValue<string>(out var p) || !Match(p, "^(?:/(?:[^~]|~[01])*)*$")))
                Error(id, cfg["pointer"] is JsonValue path && path.TryGetValue<string>(out var text) && text.StartsWith('/') ? "Path can use ~ only as ~0 for a ~ or ~1 for a / in a name" : "Path must start with /, such as /state", "pointer");
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
                    if (cfg[group] is JsonArray declarations && declarations.Any(d => d is not JsonObject)) Error(id, $"Each {(group == "carry" ? "carried" : "exported")} value of this loop needs a name and a type", group);
                    foreach (var decl in Declarations(node, group))
                    {
                        var name = Text(decl, "name"); var type = Text(decl, "type");
                        if (!Match(name, Text(Root["structures"]?["loopDeclaration"]?["name"], "pattern")) || !names.Add(name) || !KnownType(type)) { Error(id, $"The {(group == "carry" ? "carried" : "exported")} values of this loop need unique names and known types", group); continue; }
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
                if (last == null || last.Value.Port != "condition" || Kind(nodes[last.Value.Owner]) is not ("while" or "until")) Error(id, "A Condition result step must be inside the condition of a While or Until loop", code: "scope.conditionResult");
            }
            if (kind == "loop_result")
            {
                var owner = Owner(node);
                if (owner == null || Text(Spec(owner), "dynamicOutputs") != "loop") Error(id, "A Loop result step must be inside the body of a loop", code: "scope.loopResult");
                if (cfg["values"] is JsonObject values) foreach (var (name, _) in values) if (!DataInputs(node, owner).ContainsKey(name)) Error(id, $"Unknown loop value {Words.Name(name)}", name);
            }
        }
    }
}
