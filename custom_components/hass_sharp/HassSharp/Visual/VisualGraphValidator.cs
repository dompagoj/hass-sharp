using System.Globalization;
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
    /// cycle the wire itself lies on. A <c>property.*</c> diagnostic (a Select property node's property to choose) refuses
    /// the wire only when it is on the input the wire fills: a value is connected before its property is chosen.
    /// </summary>
    public static readonly string[] Codes =
    [
        "graph.version", "graph.name", "graph.shape", "graph.limit", "graph.noTrigger",
        "node.invalid", "node.duplicate", "node.unknownKind", "node.unreachable", "limit.nesting",
        "edge.invalid", "edge.duplicate", "edge.endpoint", "edge.unknownKind", "edge.valueWirePins", "edge.orderWirePins", "edge.orderPinName",
        "port.unknownExecution", "port.executionTaken", "port.unknownOutput", "port.unknownInput", "port.inputTaken", "trigger.incoming",
        "cycle.execution", "cycle.data", "scope.execution", "scope.data", "scope.conditionResult", "scope.loopResult", "scope.stateSource", "scope.flow", "scope.settingSource",
        "flow.pure", "flow.declaration", "pure.unused", "custom.signature", "csharp.signature", "property.unset", "property.unknown",
        "definition.invalid", "definition.unknownEntity", "definition.entityKey", "definition.duplicateEntity", "definition.entityDomain",
        "definition.deviceKey", "definition.duplicateDevice",
        "node.document", "node.singleton", "root.incoming",
        "setting.unknown", "setting.secretLiteral", "state.dynamicSource", "command.unhandled", "entity.stateless", "entity.no_value", "entity.name", "entity.partialState",
        "device.name", "device.via", "device.viaUnknown", "device.viaCycle", "device.configurationUrl", "device.empty",
        "http.url", "http.body", "download.checksum", "camera.entity", "camera.no_frame",
    ];

    /// <summary>
    /// The wording of validator messages, which people read on node cards and under fields. Byte-identical to
    /// the helpers in <c>frontend/src/visual/contract.ts</c>; type names are those of <c>typeLabels.ts</c>.
    /// </summary>
    internal static class Words
    {
        static readonly Dictionary<string, string> BuiltIn = new(StringComparer.Ordinal)
        {
            ["string"] = "Text", ["number"] = "Number", ["boolean"] = "True/False", ["object"] = "Object", ["array"] = "List",
            ["entity_id"] = "Entity", ["target"] = "Target", ["global::HassSharp.EntityStateSnapshot"] = "Entity state", ["image"] = "Image",
            ["device"] = "Device",
        };
        static readonly Dictionary<string, string> Primitive = new(StringComparer.Ordinal)
        { ["boolean"] = "True/False", ["number"] = "Number", ["integer"] = "Number", ["string"] = "Text", ["object"] = "Object", ["array"] = "List" };
        static readonly HashSet<string> Keywords = ["bool", "byte", "sbyte", "char", "decimal", "double", "float", "int", "uint", "long", "ulong", "short", "ushort", "nint", "nuint", "object", "string", "dynamic"];
        static readonly Regex Entity = new(@"^entity_id:([a-z0-9_]+)\z", RegexOptions.CultureInvariant);
        static readonly Regex Collection = new(@"^(?:global::)?System\.Collections\.Generic\.(?:IEnumerable|IReadOnlyList|IReadOnlyCollection|IList|ICollection|List)<([^\n\r\u2028\u2029]+)>\z", RegexOptions.CultureInvariant);
        static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
        /// <summary><c>binary_sensor</c> → <c>Binary sensor</c>.</summary>
        internal static string DomainWords(string domain) => Capital(domain.Replace('_', ' '));
        /// <summary>A key or value people did not name, in words: <c>needs_action</c> → <c>Needs action</c>; never empty.</summary>
        internal static string KeyWords(string key) => DomainWords(key) is var words && Trimmed(words).Length > 0 ? words : "Unnamed";
        static readonly string[] YouSound = ["uni", "use", "usa", "usu", "uti", "ura", "ure", "uri", "uro", "ubi"];
        /// <summary>
        /// "a" or "an" before a word, by its first sound: an input boolean entity, an update entity, a utility meter
        /// entity, a European one.
        /// </summary>
        internal static string Article(string word)
        {
            var w = word.ToLowerInvariant();
            if (w.StartsWith("eu", StringComparison.Ordinal) || w.StartsWith("one", StringComparison.Ordinal)) return "a";
            if (w.Length > 0 && w[0] is 'a' or 'e' or 'i' or 'o') return "an";
            return w.StartsWith('u') && !YouSound.Any(p => w.StartsWith(p, StringComparison.Ordinal)) ? "an" : "a";
        }
        /// <summary>
        /// A service as people know it: the name its description gives, else its key in words (<c>set_hvac_mode</c> →
        /// <c>Set hvac mode</c>) when it has none or the name is only its key or id, as the editor names it.
        /// </summary>
        internal static string ServiceName(JsonObject schema, string service)
        {
            var key = service[(service.IndexOf('.') + 1)..];
            var name = Trimmed(Text(schema, "name"));
            return name.Length > 0 && name != key && name != service ? name : KeyWords(key);
        }
        /// <summary>
        /// A step as people see it on the canvas: its own label, else (for a Custom C# node) its method's title, else its
        /// kind's catalog label, else its kind in words. A node that declares an entity or a device is named by it (<see cref="EntityName"/>).
        /// </summary>
        internal static string StepName(JsonObject node, JsonNode? spec) =>
            Declares(spec, "entity") || Declares(spec, "device") ? EntityName(node, spec)
            : node["label"] is JsonValue l && l.TryGetValue<string>(out var label) && Trimmed(label).Length > 0 ? Trimmed(label)
            : Text(CustomSignature(node, spec), "title") is { } title && Trimmed(title).Length > 0 ? Trimmed(title)
            : KindName(node, spec);
        /// <summary>A node's kind as people read it: its catalog label, else its kind in words.</summary>
        static string KindName(JsonObject node, JsonNode? spec) =>
            Text(spec, "label") is { } kindLabel && Trimmed(kindLabel).Length > 0 ? Trimmed(kindLabel)
            : KeyWords(node["kind"] is JsonValue k && k.TryGetValue<string>(out var kind) ? kind : "");
        /// <summary>
        /// An Entity node's entity as people read it, in the validator's messages and in its runs' names: its name, else its
        /// key, each trimmed, else its kind's label; never a label of the node's own. A Device node's device and a C# device's
        /// alike. A C# entity has no name of its own (its code names its entity): it is its class's title, else its key,
        /// else its kind's label. Mirrored by <c>entityNodeName</c> in <c>contract.ts</c>.
        /// </summary>
        internal static string EntityName(JsonObject node, JsonNode? spec) =>
            (IsClassNode(spec) && Declares(spec, "entity") ? Trimmed(Text(ClassSignature(node, spec), "title")) : Trimmed(Text(node["config"], "name"))) is { Length: > 0 } name ? name
            : Trimmed(Text(node["config"], "key")) is { Length: > 0 } key ? key
            : KindName(node, spec);
        /// <summary>A step that execution never reaches, and what to do about it.</summary>
        internal static string Unreachable(string step) => $"{step} is not connected into the flow; connect a step to its execution input";
        /// <summary>A step order wire into or out of a value node.</summary>
        internal static string NotInFlow(string node) => $"{node} is a value and is not part of the run order";
        /// <summary>A step order wire into or out of a Device node.</summary>
        internal static string DeviceNotInFlow(string device) => $"{device} is a device and is not part of the run order";
        /// <summary>A step order wire into or out of a C# entity.</summary>
        internal static string EntityNotInFlow(string entity) => $"{entity} is an entity and is not part of the run order";
        /// <summary>A step order wire into or out of a declaration, in the words of what it declares (<see cref="StepName"/>).</summary>
        internal static string DeclarationNotInFlow(JsonObject node, JsonNode? spec) =>
            Declares(spec, "entity") ? EntityNotInFlow(StepName(node, spec)) : DeviceNotInFlow(StepName(node, spec));
        /// <summary>A node on a cycle of value wires.</summary>
        internal static string OwnResult(string node) => $"{node} would depend on its own result";
        /// <summary>A value node none of whose outputs is connected.</summary>
        internal static string Unused(string node) => $"{node} is not used";
        /// <summary>A step reads a value node that uses an output the step could not read over a wire of its own.</summary>
        internal static string PureScope(string node, string source, string output, string step) =>
            $"{node} uses {source} · {output}, which {step} cannot use here: it is on the other side of a loop or parallel branch boundary";
        /// <summary>A library node whose method its node library no longer has (or has under another kind id).</summary>
        internal const string MissingLibraryNode = "This step is no longer in its node library; delete it and add it again from the node library";
        /// <summary>A Custom C# node whose stored signature is not one the compiler writes.</summary>
        internal const string MalformedSignature = "The pins of this node cannot be read; open Edit code and apply it";
        /// <summary>A C# entity or C# device node whose stored settings are not of the shape the compiler writes.</summary>
        internal const string UnreadableSettings = "The settings of this node cannot be read; open Edit code and apply it";
        /// <summary>A Custom C# node whose stored signature is not what its code gives (the backend compiles it to know).</summary>
        internal const string StaleSignature = "The pins are out of date with the code: open Edit code and apply it";
        /// <summary>
        /// A C# entity or C# device whose stored settings differ from its code's in what changes wires or values (the
        /// backend compiles it to know: <see cref="VisualGenerator.SameSettings"/>).
        /// </summary>
        internal const string OutOfDateSettings = "The settings are out of date with the code: open Edit code and apply it";
        /// <summary>
        /// A value given to a C# entity's or C# device's setting that the setting does not take (the backend checks it):
        /// what it takes, never the value, which may be a secret.
        /// </summary>
        internal static string SettingValue(string label, string phrase) => $"{label} takes {phrase}";
        /// <summary>The things hand-written automations left behind, and what to do instead.</summary>
        internal const string OldCustomCode = "Custom C# code is now one static method marked [Node]; rewrite this node's code in that form";
        internal const string Imports = "Usings are no longer part of an automation; put using lines at the top of a Custom C# node's code";
        internal const string Members = "Helper members are no longer part of an automation; write them in a Custom C# node's code or a node library";
        internal const string ExposedValue = "Steps no longer expose values to C# by name; connect a wire from this step to each step that uses its value";
        internal static string Variable(string subject) => $"{subject} reads a variable, and variables are gone; connect a wire to it instead";
        internal const string EntitiesExpression = "Entities expression is gone; choose the entity in Entity";
        internal const string SchedulesExpression = "Schedules expression is gone; set the time in Time of day";
        /// <summary>A name or id quoted as written, without surrounding whitespace; <c>Unnamed</c> when nothing is left.</summary>
        internal static string Name(string name) => Trimmed(name) is { Length: > 0 } trimmed ? trimmed : "Unnamed";
        /// <summary>JavaScript's whitespace (as <c>String.prototype.trim</c> removes it), spelled out: .NET's differs at U+FEFF.</summary>
        static bool Blank(char c) => c is '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or '\u00a0' or '\u1680' or (>= '\u2000' and <= '\u200a') or '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000' or '\ufeff';
        internal static string Trimmed(string text)
        {
            int start = 0, end = text.Length;
            while (start < end && Blank(text[start])) start++;
            while (end > start && Blank(text[end - 1])) end--;
            return text[start..end];
        }
        /// <summary>A time of day as the daily trigger runs it: 24-hour HH:MM.</summary>
        internal const string TimeOfDay = "^(?:[01][0-9]|2[0-3]):[0-5][0-9]$";
        /// <summary>A web address as an HTTP request or a download takes one: http:// or https://, a host, then anything without a space.</summary>
        internal const string WebAddressPattern = @"^[Hh][Tt][Tt][Pp][Ss]?://[^\x00-\x20\x7f/?#]+(?:[/?#][^\x00-\x20\x7f]*)?$";
        /// <summary>A SHA-256 checksum: 64 hexadecimal characters, in either case.</summary>
        internal const string ChecksumPattern = "^[0-9A-Fa-f]{64}$";
        /// <summary>An address typed into an HTTP request or a download that is not a web address.</summary>
        internal static string WebAddress(string label) => $"{label} must be an http:// or https:// address with a host and no spaces";
        internal static string BodyMethod(string label, string method) => $"{label} cannot be sent with {method}; choose another method or remove it";
        /// <summary>A download's checksum typed in that is not a SHA-256.</summary>
        internal static string Checksum(string label) => $"{label} must be 64 hexadecimal characters, the file's SHA-256";
        internal static string TimeOfDayMessage(string label) => $"{label} must be HH:MM in 24-hour time, from 00:00 to 23:59";
        /// <summary>A daily trigger's time zone that this machine does not know (the generator checks it, not the validators).</summary>
        internal static string UnknownTimeZone(string zone) => $"Time zone {Name(zone)} is not known; use a name such as Europe/Zagreb or UTC";
        /// <summary>A Get field path with a ~ that is not ~0 or ~1.</summary>
        internal const string PathTilde = "Path has a ~ that is not followed by 0 or 1; inside a name, write ~0 for a ~ and ~1 for a /, such as /data/on~1off for the name on/off";
        static string ClrWords(string id)
        {
            var trimmed = Regex.Replace(Regex.Replace(Regex.Replace(id, "^global::", ""), @"<.*\z", "", RegexOptions.Singleline), @"[?[\]]+\z", "");
            var name = trimmed.Split('.').LastOrDefault(s => s != "") ?? id;
            var bare = Regex.IsMatch(name, "^I[A-Z][a-z]") ? name[1..] : name;
            return Capital(Regex.Replace(Regex.Replace(bare, "([a-z0-9])([A-Z])", "$1 $2"), "([A-Z]+)([A-Z][a-z])", "$1 $2").Replace('_', ' ').ToLowerInvariant());
        }
        /// <summary>How many types deep a type is worded ("List of List of …"); deeper, and at a type that names itself, it reads as Object.</summary>
        internal const int LabelDepth = 16;
        /// <summary>The friendly name of a type, as <c>typeLabel</c> in <c>typeLabels.ts</c> gives it.</summary>
        internal static string TypeLabel(JsonObject catalog, string id) => TypeLabel(catalog, id, []);
        // Type entries come from stored signatures: one may name itself as its element, directly or through others, and
        // an id may be very long. A stack overflow cannot be caught (under Python.NET it ends Home Assistant), so the
        // words stop at a repeated id and at LabelDepth, the same on both sides.
        static string TypeLabel(JsonObject catalog, string id, HashSet<string> seen)
        {
            if (BuiltIn.TryGetValue(id, out var builtIn)) return builtIn;
            if (Entity.Match(id) is { Success: true } entity) return DomainWords(entity.Groups[1].Value) + " entity";
            if (seen.Contains(id) || seen.Count >= LabelDepth) return "Object";
            string Inner(string inner) => TypeLabel(catalog, inner, new HashSet<string>(seen, StringComparer.Ordinal) { id });
            var spec = VisualTypes.Resolve(catalog, id);
            if (id.EndsWith('?')) return Inner(id[..^1]);
            if (Text(spec, "elementType") is { Length: > 0 } element) return "List of " + Inner(element);
            if (id.EndsWith("[]", StringComparison.Ordinal)) return "List of " + Inner(id[..^2]);
            // Multi-dimensional arrays and tuples have no friendly element name: "List" and "Object".
            if (MultiDimensional.IsMatch(id)) return "List";
            if ((id.StartsWith("global::", StringComparison.Ordinal) ? id[8..] : id).StartsWith('(')) return "Object";
            if (Collection.Match(id) is { Success: true } generic) return "List of " + Inner(generic.Groups[1].Value);
            var kind = Text(spec, "literalKind").ToLowerInvariant(); var category = Text(spec, "category").ToLowerInvariant();
            if (Primitive.TryGetValue(kind, out var primitive)) return primitive;
            if (category != "object" && kind != "enum" && Primitive.TryGetValue(category, out var named)) return named;
            if (Text(spec, "label") is { Length: > 0 } label && !Regex.IsMatch(label, @"[.:<>?[\](),]") && !Keywords.Contains(label)) return label;
            var words = ClrWords(id);
            return Regex.IsMatch(words, @"[()[\],]") ? "Object" : words;
        }
        static readonly Regex MultiDimensional = new(@"\[,+\]\??\z", RegexOptions.CultureInvariant);
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
        /// <summary>A value in words: text as words (<c>twice_daily</c> → <c>Twice daily</c>), numbers as JavaScript writes them, True or False.</summary>
        internal static string Show(JsonNode? value) => value switch
        {
            null => "Nothing",
            JsonValue text when text.TryGetValue<string>(out var s) => s.Length == 0 ? "Empty text" : KeyWords(s),
            JsonValue flag when flag.TryGetValue<bool>(out var b) => b ? "True" : "False",
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
            "string" => "text", "boolean" => "true or false", _ => "the right kind of value",
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
        /// <summary>A wire into a Constant: it takes a typed-in value only, and Convert is the step that changes a value's type.</summary>
        internal const string IntoConstant = "Nothing can be connected to a Constant; remove this connection, or use Convert to change a value's type";
        /// <summary>
        /// A Constant whose stored value is of another type than its Type (a graph from before the Type set it): how to
        /// repair it. A value that may be empty, of a Type that may not, says so; two types that read alike are told apart.
        /// </summary>
        internal static string ConstantRetyped(JsonObject catalog, string label, string type, string expected)
        {
            var names = DistinctLabels(catalog, [type, expected]);
            if (VisualTypes.Nullable(catalog, type) && !VisualTypes.Nullable(catalog, expected) && TypeLabel(catalog, type) == TypeLabel(catalog, expected))
                return $"{label} may be empty, but this Constant's Type, {names[expected]}, is always set; type the value again, or change Type";
            return $"{label} is {names[type]}, but this Constant's Type is {names[expected]}; type the value again, or change Type";
        }
        /// <summary>
        /// A type mismatch: the accepted types and the one given, unless that is unknown. At most <see cref="Shown"/>
        /// accepted types are named, then how many more ("accepts True/False, Number, Text, Object and 2 more, not
        /// User"). An accepted type that reads as the given one does is told apart by its C# name
        /// (<see cref="DistinctLabels"/>): "accepts Mode (A.Mode), not Mode (B.Mode)".
        /// </summary>
        /// <summary>How many accepted types a type mismatch names before it says how many more (contract.ts <c>Shown</c>).</summary>
        const int Shown = 4;
        internal static string Incompatible(JsonObject catalog, string subject, string type, IEnumerable<string> expected)
        {
            if (VisualTypes.Chain(catalog, type).Count == 0) return $"{subject} is connected to a value of unknown type";
            var accepted = expected.ToList(); var label = TypeLabel(catalog, type);
            var plain = accepted.Select(t => TypeLabel(catalog, t)).Distinct().ToList();
            // The same kind of value, but one that may be empty where the pin needs it set (a nullable C# type).
            if (VisualTypes.Nullable(catalog, type) && plain.Contains(label))
                return $"{subject} needs a value that is always set, and this one may be empty";
            var apart = DistinctLabels(catalog, [type, .. accepted.Where(t => TypeLabel(catalog, t) == label)]);
            var names = accepted.Select(t => apart.TryGetValue(t, out var named) ? named : TypeLabel(catalog, t)).Distinct().ToList();
            if (names.Count == 0) return $"{subject} does not accept {apart[type]}";
            var listed = names.Count <= Shown ? Or(names) : $"{string.Join(", ", names.Take(Shown))} and {names.Count - Shown} more";
            return $"{subject} accepts {listed}, not {apart[type]}";
        }
        /// <summary>The parts of a dotted name, splitting only outside type arguments (<c>List&lt;System.String&gt;</c> stays one part).</summary>
        static List<string> NameParts(string name)
        {
            var parts = new List<string>(); int depth = 0, start = 0;
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (c is '<' or '[' or '(') depth++;
                else if (c is '>' or ']' or ')') depth--;
                else if (c == '.' && depth == 0) { parts.Add(name[start..i]); start = i + 1; }
            }
            parts.Add(name[start..]);
            return parts;
        }
        /// <summary>
        /// The C# names a type can go by after its label, shortest first: the last part of its catalog label (<c>int</c>),
        /// then its full name less <c>global::</c> from the last part outwards (<c>Mode</c>, <c>A.Mode</c>), then its id.
        /// </summary>
        static List<string> CSharpNames(JsonObject catalog, string id)
        {
            var full = id.StartsWith("global::", StringComparison.Ordinal) ? id[8..] : id;
            var parts = NameParts(full);
            var names = new List<string> { NameParts(catalog["types"] is JsonObject types && types[id]?["label"] is JsonValue l && l.TryGetValue<string>(out var label) ? label : full)[^1] };
            for (var k = 1; k <= parts.Count; k++) names.Add(string.Join('.', parts.Skip(parts.Count - k)));
            names.Add(id);
            return names.Distinct().ToList();
        }
        /// <summary>
        /// The names of types read side by side, so that none two read the same (the Constant's Type list names its types
        /// by the same rule): each type's label, and where several share one, each that a C# library brings (its id starts
        /// with <c>global::</c>) shows after it the shortest C# name no other of them goes by ("Mode (A.Mode)", "Number
        /// (int)"), never the label again. Mirrored by <c>distinctTypeLabels</c> in <c>typeLabels.ts</c>.
        /// </summary>
        internal static Dictionary<string, string> DistinctLabels(JsonObject catalog, IEnumerable<string> ids)
        {
            var unique = ids.Distinct().ToList();
            var plain = unique.ToDictionary(id => id, id => TypeLabel(catalog, id), StringComparer.Ordinal);
            var labels = new Dictionary<string, string>(plain, StringComparer.Ordinal);
            foreach (var label in unique.Select(id => plain[id]).Distinct())
            {
                var group = unique.Where(id => plain[id] == label).ToList();
                var named = group.Where(id => id.StartsWith("global::", StringComparison.Ordinal)).ToList();
                if (group.Count < 2 || named.Count == 0) continue;
                var choices = named.Select(id => CSharpNames(catalog, id).Where(name => name != label).ToList()).ToList();
                for (var i = 0; i < named.Count; i++)
                {
                    var mine = i;
                    var own = choices[i].FirstOrDefault(name => choices.Select((others, j) => j == mine || !others.Contains(name)).All(ok => ok)) ?? named[i];
                    labels[named[i]] = $"{label} ({own})";
                }
            }
            return labels;
        }
        /// <summary>A wire from a Select property node whose property is not chosen yet: its value has no type.</summary>
        internal static string ChooseProperty(string step) => $"Choose a property in {step} to use its value";
        /// <summary>A value typed into an input that takes a wire only (a Select property node's value): a record, class or struct comes from a step.</summary>
        internal static string WireOnly(string step) => $"Connect a value to {step}; nothing can be typed in here";
        /// <summary>A Select property node with a value connected and no property chosen.</summary>
        internal static string PropertyUnset(string type) => $"Choose a property of {type}";
        /// <summary>A Select property node whose chosen property the connected value's type does not have (the wire or the library changed).</summary>
        internal static string PropertyUnknown(string type, string name) => $"{type} has no property {Name(name)}; choose another";
        /// <summary>
        /// A value whose type has no properties, wired into a Select property node, and what reads such a value instead:
        /// Read entity for an entity, Get field for an object or a JSON list, For each for a type with an element type (what
        /// For each goes through).
        /// </summary>
        internal static string NoProperties(JsonObject catalog, string type)
        {
            var label = TypeLabel(catalog, type); var root = VisualTypes.RootId(catalog, type);
            var instead = VisualTypes.Refines(catalog, type, "entity_id") ? "use Read entity to get its state"
                : root == "object" ? "use Get field to take a value out of an object"
                : root == "array" ? "use Get field to take a value out of a list"
                : Text(VisualTypes.Resolve(catalog, type), "elementType") != "" ? "use For each to go through its items"
                : "connect a value that has some, such as a record from a node library";
            return $"{label} has no properties to select; {instead}";
        }
        /// <summary>A Repeat count typed in above the loop's Maximum iterations.</summary>
        internal static string RepeatCount(string label, double max, string maxLabel) => $"{label} must be at most {Number(max)}, as {maxLabel} is {Number(max)}";
        // An entity definition's graph: its definition (the settings composed into it) and its own steps.
        /// <summary>A graph past the catalog's limits; an entity definition's graph may also have no node.</summary>
        internal static string GraphLimit(string document) => document == "automation"
            ? "Graph limit: 1–256 nodes and at most 512 edges"
            : "Graph limit: at most 256 nodes and at most 512 edges";
        /// <summary>A definition that is not an object with a list of settings of the shape a graph reads.</summary>
        internal const string DefinitionUnreadable = "This definition's settings cannot be read: each setting needs a key and a type";
        /// <summary>Two settings of one key.</summary>
        internal static string RepeatedKey(string items, string key) => $"Two {items} have the key {Name(key)}; give each a key of its own";
        /// <summary>A setting of a type a graph cannot read.</summary>
        internal static string UnsupportedSettingType(string key, string type, IEnumerable<string> labels) => $"Setting {Name(key)} has the type {Name(type)}, which a graph cannot read; choose {Or(labels)}";
        /// <summary>A node of a kind only another kind of graph takes.</summary>
        internal static string WrongDocument(string step, string document) => document == "automation"
            ? $"{step} only works in the graph of an entity definition; delete it from this automation"
            : $"{step} only works in an automation; delete it from this entity definition";
        /// <summary>A second node of a kind a graph may hold once.</summary>
        internal static string Singleton(string label) => $"A graph can have only one {label} step; delete this one";
        /// <summary>A step order wire into a node that starts a flow of its own (other than a trigger).</summary>
        internal static string RootIncoming(string step) => $"{step} starts a flow of its own, so no step order wire can lead into it; remove this wire";
        /// <summary>A step naming an entity its definition does not have.</summary>
        internal static string UnknownEntity(string key, string label) => Trimmed(key).Length == 0 ? $"Choose one of this definition's entities in {label}" : $"This definition has no entity {Name(key)}; choose one of its entities or add it";
        /// <summary>A Setting node naming a setting its definition does not have.</summary>
        internal static string UnknownSetting(string key, string label) => Trimmed(key).Length == 0 ? $"Choose one of this definition's settings in {label}" : $"This definition has no setting {Name(key)}; choose one of its settings or add it";
        /// <summary>An entity reader an Entity node's state reads whose entity comes over a wire: what the state watches cannot be known.</summary>
        internal static string DynamicSource(string reader, string state, string input) => $"{reader} gets its entity over a wire, so {state} is not worked out again when that entity changes; choose the entity in {input} instead";
        /// <summary>A Set state step naming an entity whose type has no state, such as a button.</summary>
        internal static string Stateless(string type) => $"{Capital(Article(type))} {type.ToLowerInvariant()} has no state to set";
        /// <summary>A Set state step given none of its entity's state values.</summary>
        internal static string NoValue(string step) => $"{step} needs at least one value";
        /// <summary>A Show frame step naming an entity of the definition that is not a camera, by its name; whether the definition has one.</summary>
        internal static string NotACamera(string name, bool anyCamera) => anyCamera ? $"{Name(name)} is not a camera; choose one of this definition's cameras" : $"{Name(name)} is not a camera; add a camera to this definition first";
        /// <summary>A camera an Entity node declares that no Show frame step shows a frame for, named by its name.</summary>
        internal static string NoFrame(string name, string label) => $"{Name(name)} never shows a frame: add {Article(label)} {label} step for it";
        // An Entity node's entity, named by EntityName.
        /// <summary>What an Entity node's key must be, as an entity key is (<c>SharpEntity.KeyPattern</c>); a Device node's key is of the same form.</summary>
        internal const string EntityKeyPattern = "^[a-z][a-z0-9_]{0,39}$";
        /// <summary>An Entity node without a key.</summary>
        internal const string EntityKeyMissing = "Give this entity a key, such as power";
        /// <summary>An Entity node's key that is not of the form entity keys take, in the words of the document's check.</summary>
        internal static string EntityKey(string key) => $"Entity key {key} is not a key: start it with a lowercase letter and use only lowercase letters, digits and underscores, up to 40 characters";
        /// <summary>An Entity node of the key an earlier one declares.</summary>
        internal static string KeyTaken(string key, string first) => $"The key {key} is already used by {first}: give this entity its own";
        /// <summary>An Entity node of a type the catalog's <c>entityDomains</c> does not describe.</summary>
        internal static string NotAnEntityType(string domain) => $"{domain} is not an entity type: choose one from the list";
        /// <summary>An Entity node on no device (no Device node wired to it), with no name.</summary>
        internal const string EntityNameMissing = "Name this entity: it has no device, so Home Assistant shows it by its own name";
        // A Device node's device, named by EntityName.
        /// <summary>A Device node without a key.</summary>
        internal const string DeviceKeyMissing = "Give this device a key, such as plug";
        /// <summary>A Device node's key that is not of the form entity keys take (<see cref="EntityKeyPattern"/>).</summary>
        internal static string DeviceKey(string key) => $"Device key {key} is not a key: start it with a lowercase letter and use only lowercase letters, digits and underscores, up to 40 characters";
        /// <summary>A Device node of the key an earlier one has, and the name of that one.</summary>
        internal static string DeviceKeyTaken(string key, string first) => $"The key {key} is already used by {first}: give this device its own";
        /// <summary>A Device node with no name.</summary>
        internal const string DeviceNameMissing = "Name this device: Home Assistant shows it by this name";
        /// <summary>A Device node's Part of that is not nothing, <c>{deviceId}</c> or <c>{device}</c>.</summary>
        internal const string ViaShape = "Part of must be nothing, a Home Assistant device or another device of this definition";
        /// <summary>A Device node part of a device of a key no Device node of the graph has.</summary>
        internal static string ViaUnknown(string name, string key) => $"{name} is part of the device {key}, which is not a device of this definition: choose another, or none";
        /// <summary>A Device node on a circle of Part of.</summary>
        internal static string ViaCycle(string name) => $"{name} ends up being part of itself: change what one of these devices is part of";
        /// <summary>A Device node's configuration URL that Home Assistant would refuse.</summary>
        internal static string ConfigurationUrl(string name) => $"The configuration URL of {name} must start with http://, https:// or homeassistant://, followed by an address";
        /// <summary>A Device node no entity is wired to.</summary>
        internal static string DeviceEmpty(string name) => $"{name} has no entities: connect its Entities pin to an entity's Device pin";
        /// <summary>An Entity node given some of its state values (by their labels, joined) and not one its type needs.</summary>
        internal static string PartialState(string name, string given, string missing) => $"{name} has a value for {given} but none for {missing}: connect or set {missing} too";
        /// <summary>A state value of an Entity node that reads a step's output: its state is worked out in no flow.</summary>
        internal static string StateSource(string value, string name, string step) => $"{value} of {name} cannot read {step}, which runs in a flow: use a Set state step after {step} instead";
        /// <summary>A setting of a C# entity or a C# device wired from anything but a Setting node: it is set before anything runs.</summary>
        internal static string SettingSource(string label) => $"{label} takes a typed value or a Setting: it is set before anything runs";
        /// <summary>A value typed into a secret setting of a C# entity or a C# device, which only a Setting node may give; the value is not repeated.</summary>
        internal static string SecretLiteral(string label) => $"{label} is a secret: connect a Setting instead of typing it here";
        /// <summary>
        /// A step reading a command's value that another flow runs too, named by where that flow starts: <paramref name="also"/>
        /// when the command whose value it reads runs it as well, else it runs only from the other.
        /// </summary>
        internal static string FlowValue(string step, string value, string command, string name, string other, bool also) =>
            $"{step} reads {value} from {command} of {name} but {(also ? "also " : "")}runs from {other}, where there is no {value}";
        /// <summary>A member of an Entity node's run history that is not one of its flows.</summary>
        internal static string HistoryMember(string member) => $"Run history has no {member}; it takes state, commands or frame";
        /// <summary>A member of an Entity node's run history that is not true or false.</summary>
        internal static string HistoryValue(string member) => $"{KeyWords(member)} of Run history must be true or false";
        /// <summary>trackHistory on an Entity node, whose runs are recorded by its history.</summary>
        internal const string HistoryNotTrack = "An Entity node has no trackHistory: its history says which of its flows record their runs (state, commands, frame)";
        /// <summary>A command pin of an Entity node with no step connected.</summary>
        internal static string NothingOn(string name, string command) => $"{name} does nothing on {command}: connect a step to {command}";
    }

    /// <summary>
    /// The configuration URLs a Device node may have: http, https or homeassistant in any case, then :// and an ASCII
    /// host name or an address in brackets, an optional port up to 65535, and then nothing or a path, query or fragment
    /// with no white space, control character, backslash or surrogate. A lone surrogate cannot be encoded for Home
    /// Assistant, and the pattern does not tell it from one of a pair, so a character beyond U+FFFF (an emoji) in a path
    /// is refused too. It is narrower than Home Assistant's own check, which also takes a user before the host, a host
    /// that is not ASCII, an empty port or a trailing line break, so that it stays small; every URL it takes, Home
    /// Assistant takes.
    /// </summary>
    internal static readonly Regex ConfigurationUrlPattern = new(@"^(?:[Hh][Tt][Tt][Pp][Ss]?|[Hh][Oo][Mm][Ee][Aa][Ss][Ss][Ii][Ss][Tt][Aa][Nn][Tt])://(?:[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?|\[[0-9A-Fa-f.:]*:[0-9A-Fa-f.:]*\])(?::(?<port>[0-9]{1,5}))?(?:[/?#][^\s\x00-\x1F\x7F-\x9F\uD800-\uDFFF\uFEFF\\]*)?(?![\s\S])", RegexOptions.CultureInvariant);
    /// <summary>Whether a Device node may have <paramref name="url"/> as its configuration URL: <see cref="ConfigurationUrlPattern"/>, mirrored by
    /// <c>configurationUrlTaken</c> in <c>contract.ts</c>.</summary>
    internal static bool ConfigurationUrlTaken(string url) =>
        ConfigurationUrlPattern.Match(url) is { Success: true } taken
        && (taken.Groups["port"].Value is not { Length: > 0 } port || int.Parse(port, CultureInfo.InvariantCulture) <= 65535);

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
        /// <summary>A known type a value can have: not a structural one (a device), which only a wire carries.</summary>
        bool ValueType(string type) => KnownType(type) && !Structural(VisualTypes.Resolve(Root, type));
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
        string Prefix(JsonObject node) => ServiceName(node) is { } service ? ServiceTitle(service) + ": " : "";
        /// <summary>
        /// A service as every message names it: its name and then its id in brackets where the services section
        /// describes it ("Turn on (light.turn_on)"), else its id alone.
        /// </summary>
        string ServiceTitle(string service) => snapshot.Services?.Items[service] is JsonObject schema ? $"{Words.ServiceName(schema, service)} ({service})" : service;
        /// <summary>
        /// The message for a wire into a service's target that brings an entity of a domain the service does not take,
        /// worded as the value case is; null for any other mismatch.
        /// </summary>
        string? WrongDomain(JsonObject node, string port, string type)
        {
            if (port != "target" || ServiceName(node) is not { } service || snapshot.Services?.Items[service] is not JsonObject schema || ServiceTargetDomains(schema) is not { Length: > 0 } domains) return null;
            string carried;
            if (type == "entity_id") carried = "can be any entity";
            else if (EntityDomain.Match(type) is { Success: true } entity) { var named = Words.DomainWords(entity.Groups[1].Value).ToLowerInvariant() + " entity"; carried = $"is {Words.Article(named)} {named}"; }
            else return null;
            return $"{ServiceTitle(service)} only accepts {Words.Or(domains.Select(d => Words.DomainWords(d).ToLowerInvariant()))} entities; the connected value {carried}";
        }
        static readonly Regex EntityDomain = new(@"^entity_id:([a-z0-9_]+)\z", RegexOptions.CultureInvariant);
        /// <summary>An input that takes a value only (a Constant's value): a wire into it is refused as into an input the step does not have.</summary>
        static JsonNode? Connectable(JsonNode? input) => input is JsonObject spec && spec["connectable"] is JsonValue v && v.TryGetValue<bool>(out var connectable) && !connectable ? null : input;
        string ConfigLabel(JsonNode? rule, string field) => Text(rule, "label") is { Length: > 0 } label && label.Trim().Length > 0 ? label : Words.KeyWords(field);
        static string Kind(JsonObject node) => Text(node, "kind");
        static string Id(JsonObject node) => Text(node, "id");
        static JsonObject Config(JsonObject node) => node["config"] as JsonObject ?? [];
        static string FlowPort(JsonObject node, JsonObject edge) => Kind(node) == "condition" && Text(edge, "port", "next") == "next" ? "true" : Text(edge, "port", "next");
        bool ScopePort(JsonObject node, string port) => VisualNodeCatalog.ScopePorts(node, Spec(node)).Contains(port) || Kind(node) == "parallel" && port.StartsWith("branch:", StringComparison.Ordinal);
        static bool Ancestor((string Owner, string Port)[] a, (string Owner, string Port)[] b) => a.Length <= b.Length && a.SequenceEqual(b.Take(a.Length));
        JsonObject? Owner(JsonObject node) => scopes.TryGetValue(Id(node), out var scope) && scope.LastOrDefault() is var last && last.Port == "body" ? nodes[last.Owner] : null;
        bool Pure(JsonObject node) => IsPure(node, Spec(node));
        /// <summary>Whether the node is a declaration (<see cref="Declaration"/>): a Device node, neither a step nor a value.</summary>
        bool Declared(JsonObject node) => Declaration(Spec(node));
        bool Visible(JsonObject source, JsonObject target, JsonNode port) => scopes.TryGetValue(Id(target), out var b) && VisibleIn(source, b, port);
        /// <summary>Whether an output of <paramref name="source"/> may be read by a step in scope <paramref name="b"/>.</summary>
        bool VisibleIn(JsonObject source, (string Owner, string Port)[] b, JsonNode port)
        {
            if (!scopes.TryGetValue(Id(source), out var a) || !Ancestor(a, b)) return false;
            var owned = b.Length > a.Length && b[a.Length].Owner == Id(source);
            // A value set in one scope of its step (For each's item in Body, a flow's value on that flow) is read there only.
            if (Text(port, "scope") is { Length: > 0 } scope) return owned && b[a.Length].Port == scope;
            return Text(port, "visibility") switch
            {
                "outside" => !owned,
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
        public List<VisualDiagnostic> Run(JsonObject graph)
        {
            var version = Number(Root["graphVersion"]);
            if (Number(graph["version"]) != version) { Error(null, $"Expected graph version {version}", code: "graph.version"); return errors; }
            if (string.IsNullOrWhiteSpace(Text(graph, "name"))) Error(null, "Graph name is required", code: "graph.name");
            if (graph.ContainsKey("imports")) Error(null, Words.Imports);
            if (graph.ContainsKey("members")) Error(null, Words.Members);
            if (graph["nodes"] is not JsonArray ns || graph["edges"] is not JsonArray es) { Error(null, "Nodes and edges arrays are required", code: "graph.shape"); return errors; }
            // An entity definition's graph may be empty here: a definition with no Entity node is refused by the
            // definition check (definition.entitiesRequired, DefinitionDocument), not by this validator.
            document = GraphDocument(graph);
            if (ns.Count < 1 && document == "automation" || OverLimit(Root, graph)) { Error(null, Words.GraphLimit(document), code: "graph.limit"); return errors; }
            // Only an object definition makes an entity graph; any other value of the key is ignored, as it always was.
            if (EntityGraph) Definition(graph["definition"]);
            // The types of the graph's Custom C# nodes join the catalog's; they are compared by their entries alone. Only
            // once the graph's shape and size are known: nothing is resolved for a graph answered with graph.limit.
            if (WithGraphTypes(Root, graph) is var merged && !ReferenceEquals(merged, Root)) snapshot = snapshot.WithRoot(merged);
            foreach (var raw in ns)
            {
                if (raw is not JsonObject node || Id(node) == "" || node["config"] is not JsonObject) { Error(null, "Each node requires id and config", code: "node.invalid"); continue; }
                if (!nodes.TryAdd(Id(node), node)) Error(Id(node), "Duplicate node id", code: "node.duplicate");
                if (Spec(node) == null) Error(Id(node), Kind(node).StartsWith("library:", StringComparison.Ordinal) ? Words.MissingLibraryNode : "Unknown node kind", code: "node.unknownKind");
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
                    // A value node and a declaration (a Device node) have no execution pins; the wire is reported on them and
                    // otherwise ignored, so it puts neither in a scope.
                    if (Pure(nodes[source]) || Pure(nodes[target]) || Declared(nodes[source]) || Declared(nodes[target]))
                    {
                        if (Pure(nodes[source])) Error(source, Words.NotInFlow(Words.StepName(nodes[source], Spec(nodes[source]))), code: "flow.pure");
                        if (Pure(nodes[target]) && target != source) Error(target, Words.NotInFlow(Words.StepName(nodes[target], Spec(nodes[target]))), code: "flow.pure");
                        if (Declared(nodes[source])) Error(source, Words.DeclarationNotInFlow(nodes[source], Spec(nodes[source])), code: "flow.declaration");
                        if (Declared(nodes[target]) && target != source) Error(target, Words.DeclarationNotInFlow(nodes[target], Spec(nodes[target])), code: "flow.declaration");
                        continue;
                    }
                    var node = nodes[source]; var port = FlowPort(node, edge);
                    if (!ExecutionPorts(node).Contains(port)) Error(source, "Unknown execution port", port: port, code: "port.unknownExecution");
                    if (outgoing[source].Any(e => FlowPort(node, e) == port)) Error(source, "Only one execution edge per port", port: port, code: "port.executionTaken");
                    if (Kind(nodes[target]) == "trigger") Error(target, "Trigger cannot have incoming execution edges", code: "trigger.incoming");
                    else if (IsRoot(nodes[target], snapshot)) Error(target, Words.RootIncoming(Words.StepName(nodes[target], Spec(nodes[target]))), code: "root.incoming");
                    outgoing[source].Add(edge);
                }
                else Error(source, "This wire is neither a step order wire nor a value wire", code: "edge.unknownKind");
            }
            Cycles(outgoing.Values.SelectMany(e => e).ToList(), "Execution"); Cycles(data, "Data");
            // Flows start at the roots: triggers, and an entity graph's Entity nodes and When started. An
            // automation needs a trigger; an entity graph needs none (a definition may have no logic yet).
            var roots = nodes.Values.Where(n => IsRoot(n, snapshot)).ToArray();
            if (document == "automation" && !nodes.Values.Any(n => Kind(n) == "trigger")) Error(null, "At least one trigger is required", code: "graph.noTrigger");
            foreach (var root in roots) Scope(Id(root), []);
            // A value node is never reached by execution; it is a warning when nothing reads it. Nor is a declaration (a Device
            // node), which never runs.
            foreach (var (id, node) in nodes)
                if (Pure(node)) { if (!data.Any(e => Text(e, "source") == id)) errors.Add(new(id, Words.Unused(Words.StepName(node, Spec(node))), 2, Code: "pure.unused")); }
                else if (!scopes.ContainsKey(id) && !Declared(node)) Error(id, Words.Unreachable(Words.StepName(node, Spec(node))), code: "node.unreachable");
            foreach (var edge in data)
            {
                var source = nodes[Text(edge, "source")]; var target = nodes[Text(edge, "target")];
                var sp = Text(edge, "sourcePort"); var tp = Text(edge, "targetPort");
                var output = DataOutputs(source)[sp]; var input = Connectable(DataInputs(target, Owner(target))[tp]);
                if (output == null) Error(Id(source), "Unknown data output port", port: sp, code: "port.unknownOutput");
                if (input == null) Error(Id(target), Kind(target) == "constant" && tp == "value" ? Words.IntoConstant : $"This value wire goes into {Words.PortLabel(null, tp)}, which this step does not have", tp, tp, "port.unknownInput");
                if (!incoming.TryAdd((Id(target), tp), edge)) Error(Id(target), "Only one data source per input", tp, tp, "port.inputTaken");
                if (output != null && input != null)
                {
                    var type = Text(output, "type"); var expected = Strings(input["types"]);
                    // An output whose property is not chosen yet has no type: a wire from it says to choose one. When one is chosen
                    // but cannot be read (a property the value's type lacks), the node itself says so and the wire gets nothing.
                    if (PropertyOf(output) != "" && type == "") { if (Text(Config(source), "property") == "") Error(Id(target), Words.ChooseProperty(Words.StepName(source, Spec(source))), tp, tp, "property.unset"); }
                    // A Setting whose setting is unknown has no type; the Setting node itself says so.
                    else if (type == "" && Kind(source) == "setting") { }
                    // A Select property node reads a value whose own type has properties.
                    else if (SelectsProperties(Spec(target), tp) ? TypeProperties(Root, type).Length == 0 : !expected.Any(t => CompatibleTypes(type, t)))
                        Error(Id(target), WrongDomain(target, tp, type) ?? (SelectsProperties(Spec(target), tp) && KnownType(type) ? Words.NoProperties(Root, type) : Words.Incompatible(Root, Prefix(target) + Words.PortLabel(input, tp), type, expected)), tp, tp);
                    // A setting of a C# entity or a C# device is set before anything runs: it reads a Setting node, and no step's
                    // output or value worked out while a flow runs, nor a value node that passes a Setting's value on.
                    if (SettingSourceError(source, target, input, tp) is { } unset) Error(Id(target), unset, tp, tp, "scope.settingSource");
                    // A wire to or from a step not in the flow yet gets nothing: that step carries node.unreachable. A state value
                    // of an Entity node reads no step in a flow at all, which scope.stateSource says instead.
                    var stateValue = IsHub(Spec(target));
                    if (!stateValue && scopes.ContainsKey(Id(source)) && scopes.ContainsKey(Id(target)) && !Visible(source, target, output))
                        Error(Id(target), ScopeData(Prefix(target) + Words.PortLabel(input, tp)), tp, tp, "scope.data");
                    // A step reading a value node reads, in effect, every step output that value node uses.
                    if (!stateValue && Pure(source) && !Pure(target) && scopes.ContainsKey(Id(target)))
                        foreach (var (producer, port, spec) in PureSources(source))
                            if (scopes.ContainsKey(Id(producer)) && !Visible(producer, target, spec))
                                Error(Id(target), Words.PureScope(Words.StepName(source, Spec(source)), Words.StepName(producer, Spec(producer)), Words.PortLabel(spec, port), Words.StepName(target, Spec(target))), tp, tp, "scope.data");
                }
            }
            foreach (var node in nodes.Values) ValidateNode(node);
            GraphRules();
            return errors;
        }
        /// <summary>The kind of document the graph is (<see cref="GraphDocument"/>): <c>automation</c> or <c>entity</c>.</summary>
        string document = "automation";
        bool EntityGraph => document == "entity";
        static bool IsText(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out _);
        /// <summary>
        /// Whether an Entity node is on a Device node: the wire into its Device pin (the one it reads) comes from one. As
        /// <see cref="VisualNodeCatalog.DeviceOf"/> derives it from the graph.
        /// </summary>
        bool OnDevice(JsonObject hub) => incoming.TryGetValue((Id(hub), "device"), out var wire) && Declares(Spec(nodes[Text(wire, "source")]), "device");
        /// <summary>
        /// Whether a Device node's Part of, an object, is of its shape: <c>{deviceId}</c> (a Home Assistant device) or
        /// <c>{device}</c> (the key of another Device node), that one member, a text that is not blank once trimmed.
        /// </summary>
        static bool ViaShaped(JsonObject via) =>
            via.Count == 1 && via.First() is var (member, value) && member is "deviceId" or "device" && value is JsonValue v && v.TryGetValue<string>(out var text) && Words.Trimmed(text).Length > 0;
        /// <summary>The key of the Device node a Device node's Part of names (<c>{device: key}</c>, of its shape); else null.</summary>
        static string? ViaDevice(JsonNode? via) => via is JsonObject o && ViaShaped(o) && o["device"] is JsonValue v && v.TryGetValue<string>(out var key) ? key : null;
        /// <summary>
        /// The definition a graph carries: an object with a list of settings, each with a text key and type, each of a type a
        /// setting can have, and no key twice. Its entities are the graph's Entity nodes, and its devices its Device nodes:
        /// a list of entities, or a device, it has is ignored.
        /// </summary>
        void Definition(JsonNode? definition)
        {
            if (definition is not JsonObject d || d["settings"] is not JsonArray settings || !settings.All(s => s is JsonObject o && IsText(o["key"]) && IsText(o["type"])))
            { Error(null, Words.DefinitionUnreadable, code: "definition.invalid"); return; }
            var keys = new HashSet<string>(StringComparer.Ordinal); var repeated = new HashSet<string>(StringComparer.Ordinal);
            foreach (var setting in settings) if (!keys.Add(Text(setting, "key")) && repeated.Add(Text(setting, "key"))) Error(null, Words.RepeatedKey("settings", Text(setting, "key")), code: "definition.invalid");
            foreach (var setting in settings) if (!SettingTypes.Contains(Text(setting, "type")))
                Error(null, Words.UnsupportedSettingType(Text(setting, "key"), Text(setting, "type"), SettingTypes.Select(t => Words.TypeLabel(Root, t))), code: "definition.invalid");
        }
        /// <summary>The nodes of a kind, by id (ordinal): when a rule allows one, the first keeps it and each later one is reported.</summary>
        IEnumerable<JsonObject> OfKind(string kind) => nodes.Values.Where(n => Kind(n) == kind).OrderBy(Id, StringComparer.Ordinal);
        string KindLabel(string kind) => Text(Root["nodes"]?[kind], "label", Words.KeyWords(kind));
        /// <summary>
        /// The rules about the graph as a whole: a kind a graph holds once, and in an entity graph, an Entity node's state
        /// reading an entity given over a wire, a command no step handles, a camera no Show frame step shows a frame for,
        /// one Entity node per key, and the flows of Entity nodes (<see cref="EntityScopes"/>).
        /// </summary>
        void GraphRules()
        {
            // Each rule reports every node after the first of its key, in id order (as the editor's copy does).
            static IEnumerable<JsonObject> Later<T>(IEnumerable<JsonObject> list, Func<JsonObject, T> key) { var seen = new HashSet<T>(); foreach (var node in list) if (!seen.Add(key(node))) yield return node; }
            foreach (var kind in nodes.Values.Where(n => IsSingleton(Spec(n))).Select(Kind).Distinct(StringComparer.Ordinal).ToList())
                foreach (var node in Later(OfKind(kind), _ => kind)) Error(Id(node), Words.Singleton(Words.Name(KindLabel(kind))), code: "node.singleton");
            if (!EntityGraph) return;
            static string Choice(JsonObject node, string key) => Text(Config(node), key);
            // An Entity node whose state is worked out (a value of it is given) works it out again when an entity it reads
            // changes; only an entity chosen in a reader can be watched. A reader is any value node with an input that takes
            // only entity ids (built in, a library's or Custom C#), warned about for the first such input given over a wire,
            // unless that wire comes from a Constant of an entity id type, whose entity is followed as chosen
            // (StateDependencies.Of). The warning is the reader's own, on no input: a wire into it is allowed (connectData),
            // at the cost it names.
            var readers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var state in nodes.Values.Where(n => IsHub(Spec(n)) && StateInputs(n, Root).Any(p => Supplied(n, p.Key))).OrderBy(Id, StringComparer.Ordinal))
                foreach (var reader in ValueUpstream(state, nodes, data, Pure, n => DataInputs(n)))
                    if (!readers.Contains(Id(reader)) && DataInputs(reader).FirstOrDefault(p => TakesOnlyEntityIds(p.Value) && incoming.TryGetValue((Id(reader), p.Key), out var wire) &&
                            !FollowedSource(nodes[Text(wire, "source")], snapshot)) is { Key: not null } wired && readers.Add(Id(reader)))
                        errors.Add(new(Id(reader), Words.DynamicSource(Words.StepName(reader, Spec(reader)), Words.StepName(state, Spec(state)), Words.PortLabel(wired.Value, wired.Key)), 2, Code: "state.dynamicSource"));
            // Every node that declares an entity (an Entity node, a C# entity): a key no earlier one (in the graph's order)
            // declares. An Entity node's: a step on each command's pin, and for a camera, a Show frame step that names it.
            var keys = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var hub in nodes.Values.Where(n => Declares(Spec(n), "entity")))
            {
                var name = Words.StepName(hub, Spec(hub));
                if (Choice(hub, "key") is var key && Words.Trimmed(key).Length > 0 && !keys.TryAdd(key, hub))
                    Error(Id(hub), Words.KeyTaken(key, Words.StepName(keys[key], Spec(keys[key]))), "key", code: "definition.duplicateEntity");
                if (!IsHub(Spec(hub))) continue;
                foreach (var (command, entry) in DeclaredDomain(hub, Root)?["commands"] as JsonObject ?? [])
                    if (!outgoing[Id(hub)].Any(e => FlowPort(hub, e) == "command:" + command))
                        errors.Add(new(Id(hub), Words.NothingOn(name, Text(entry, "label", Words.KeyWords(command))), 2, PortId: "command:" + command, Code: "command.unhandled"));
                if (Choice(hub, "domain") == "camera" && !OfKind("show_frame").Any(n => Choice(n, "entity") == Choice(hub, "key")))
                    errors.Add(new(Id(hub), Words.NoFrame(name, KindLabel("show_frame")), 2, PortId: "frame", Code: "camera.no_frame"));
            }
            DeviceRules();
            EntityScopes();
        }
        /// <summary>
        /// The rules about Device nodes among each other, in the graph's order: a key no earlier Device node has; a Part of
        /// that names a device (<c>{device: key}</c>, the first Device node of that key) this graph has, and does not come
        /// back to the node it starts from (every device of such a circle is told, one part of itself included); and at
        /// least one entity wired to it, else a warning. A C# device is a device of these rules too (its key, its Part of, and
        /// the Part of of another), but its code adds its entities: it is never empty. Mirrored by <c>deviceRules</c> in <c>contract.ts</c>.
        /// </summary>
        void DeviceRules()
        {
            var devices = nodes.Values.Where(n => Declares(Spec(n), "device")).ToList();
            var byKey = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var node in devices)
                if (Text(Config(node), "key") is var key && Words.Trimmed(key).Length > 0 && !byKey.TryAdd(key, node))
                    Error(Id(node), Words.DeviceKeyTaken(key, Words.StepName(byKey[key], Spec(byKey[key]))), "key", code: "definition.duplicateDevice");
            JsonObject? Parent(JsonObject node) => ViaDevice(Config(node)["via"]) is { } key && byKey.TryGetValue(key, out var parent) ? parent : null;
            foreach (var node in devices)
            {
                var name = Words.StepName(node, Spec(node));
                if (ViaDevice(Config(node)["via"]) is { } named && !byKey.ContainsKey(named)) Error(Id(node), Words.ViaUnknown(name, named), "via", code: "device.viaUnknown");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var next = Parent(node); next != null && seen.Add(Id(next)); next = Parent(next))
                    if (ReferenceEquals(next, node)) { Error(Id(node), Words.ViaCycle(name), "via", code: "device.viaCycle"); break; }
                if (!IsClassNode(Spec(node)) && !data.Any(e => Text(e, "source") == Id(node) && Text(e, "sourcePort") == "entities")) errors.Add(new(Id(node), Words.DeviceEmpty(name), 2, Code: "device.empty"));
            }
        }
        /// <summary>
        /// The flows of Entity nodes: a state value reads no step that runs in a flow (<see cref="StateSource"/>), its state
        /// being worked out in none; and a step reads a command's value only when no other flow runs it (<see cref="FlowPins"/>),
        /// each reported once per input, for the first such value it reads.
        /// </summary>
        void EntityScopes()
        {
            // Its Device input reads no step: the Device node it is wired to is a declaration, not a flow's step.
            foreach (var hub in nodes.Values.Where(n => IsHub(Spec(n))))
                foreach (var (port, input) in StateInputs(hub, Root))
                    if (StateSource(hub, port) is { } step)
                        Error(Id(hub), Words.StateSource(Words.PortLabel(input, port), Words.StepName(hub, Spec(hub)), Words.StepName(step, Spec(step))), port, port, "scope.stateSource");
            var pins = FlowPins();
            foreach (var edge in data)
            {
                var reader = nodes[Text(edge, "target")]; var input = Text(edge, "targetPort");
                if (Pure(reader) || !ReferenceEquals(incoming[(Id(reader), input)], edge)) continue;
                var source = nodes[Text(edge, "source")];
                var read = Pure(source) ? PureSources(source) : DataOutputs(source)[Text(edge, "sourcePort")] is JsonObject output ? [(source, Text(edge, "sourcePort"), output)] : [];
                foreach (var (hub, port, value) in read)
                {
                    if (!IsHub(Spec(hub)) || Text(value, "group") is not { Length: > 0 } own) continue;
                    var other = pins.Find(p => p.Reached.Contains(Id(reader)) && !(p.Root == hub && p.Port == own));
                    if (other.Root == null) continue;
                    var also = pins.Any(p => p.Root == hub && p.Port == own && p.Reached.Contains(Id(reader)));
                    Error(Id(reader), Words.FlowValue(Words.StepName(reader, Spec(reader)), Words.PortLabel(value, port), PinLabel(hub, own), Words.StepName(hub, Spec(hub)),
                        other.Port == null ? Words.StepName(other.Root, Spec(other.Root)) : $"{PinLabel(other.Root, other.Port)} of {Words.StepName(other.Root, Spec(other.Root))}", also), input, input, "scope.flow");
                    break;
                }
            }
        }
        /// <summary>
        /// What a wire from <paramref name="source"/> into a setting pin of a class node (a C# entity's or a C# device's)
        /// says: such a pin reads a Setting node and nothing else. Null for any other wire. Mirrored by
        /// <c>settingSourceError</c> in <c>contract.ts</c>, which the connection index asks too.
        /// </summary>
        string? SettingSourceError(JsonObject source, JsonObject target, JsonNode input, string port) =>
            IsClassNode(Spec(target)) && input["setting"] is JsonObject && Kind(source) != "setting" ? Words.SettingSource(Words.PortLabel(input, port)) : null;
        /// <summary>
        /// The step a state value of an Entity node reads: the source of the wire into it, else the first step a value node
        /// on that wire reads, depth first in the order of the graph's value wires; null when nothing or only values are read.
        /// Mirrored by <c>stateSource</c> in <c>contract.ts</c>.
        /// </summary>
        JsonObject? StateSource(JsonObject hub, string port)
        {
            if (!incoming.TryGetValue((Id(hub), port), out var wire)) return null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            JsonObject? Visit(JsonObject source)
            {
                if (!Pure(source)) return source;
                if (!seen.Add(Id(source))) return null;
                foreach (var edge in data.Where(e => Text(e, "target") == Id(source)))
                    if (Visit(nodes[Text(edge, "source")]) is { } step) return step;
                return null;
            }
            return Visit(nodes[Text(wire, "source")]);
        }
        /// <summary>
        /// Where the graph's flows start, in node order: each pin of an Entity node (its port), and each other root as a whole
        /// (no port), each with the steps reached from it along step order wires. Mirrored by <c>flowPins</c> in <c>contract.ts</c>.
        /// </summary>
        List<(JsonObject Root, string? Port, HashSet<string> Reached)> FlowPins()
        {
            HashSet<string> Reached(IEnumerable<JsonObject> wires)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var stack = new Stack<string>(wires.Select(e => Text(e, "target")));
                while (stack.TryPop(out var id))
                    if (seen.Add(id) && outgoing.TryGetValue(id, out var next)) foreach (var edge in next) stack.Push(Text(edge, "target"));
                return seen;
            }
            var pins = new List<(JsonObject, string?, HashSet<string>)>();
            foreach (var root in nodes.Values.Where(n => IsRoot(n, snapshot)))
                if (IsHub(Spec(root))) foreach (var port in ExecutionPorts(root)) pins.Add((root, port, Reached(outgoing[Id(root)].Where(e => FlowPort(root, e) == port))));
                else pins.Add((root, null, Reached(outgoing[Id(root)])));
            return pins;
        }
        /// <summary>An Entity node's pin as people read it: its command's label, or its type's frame label.</summary>
        string PinLabel(JsonObject hub, string port) => DeclaredDomain(hub, Root) is var row && port.StartsWith("command:", StringComparison.Ordinal)
            ? Text(row?["commands"]?[port["command:".Length..]], "label", Words.KeyWords(port["command:".Length..]))
            : Text(row?["frame"], "label", "When a frame is needed");
        /// <summary>Whether the definition's entity with this key is a camera.</summary>
        bool IsCamera(string key) => Text(DefinitionEntity(Root, key), "domain") == "camera";
        /// <summary>The definition's entity with this key as a sentence names it: its name, else its key.</summary>
        string EntityName(string key) => Text(DefinitionEntity(Root, key), "name") is { } name && Words.Trimmed(name).Length > 0 ? name : key;
        /// <summary>Whether a step's input is given a value: a wire into it, or an operand that is not unset.</summary>
        bool Supplied(JsonObject node, string port) =>
            incoming.ContainsKey((Id(node), port)) || TryGetInputValue(node, port, out var operand) && operand is JsonObject given && Text(given, "kind") != "unset";
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
            // A secret setting of a C# entity or a C# device is given by a Setting node only: a value typed into it is refused, a
            // wire into it too or not, and nothing else is said of it, so that no message quotes it.
            if (spec["setting"] is JsonObject setting && Bool(setting, "secret") && raw is JsonObject typed && Text(typed, "kind") == "literal")
            {
                Error(Id(node), Words.SecretLiteral(Words.PortLabel(spec, port)), port, code: "setting.secretLiteral");
                if (!incoming.ContainsKey((Id(node), port))) return null;
            }
            if (incoming.TryGetValue((Id(node), port), out var edge)) return Text(DataOutputs(nodes[Text(edge, "source")])[Text(edge, "sourcePort")], "type");
            var required = Bool(spec, "required");
            if (!present && !required) return null;
            var label = Words.PortLabel(spec, port); var subject = Prefix(node) + label;
            if (raw is not JsonObject operand || Text(raw, "kind") == "unset")
            {
                if (required) Error(Id(node), ServiceName(node) is { } service ? $"{ServiceTitle(service)} needs {label}" : $"{label} is required", port, port);
                else if (present && raw is not JsonObject) Error(Id(node), $"{subject} has an invalid value", port, port);
                return null;
            }
            string? type = null;
            if (Text(operand, "kind") == "variable") { Error(Id(node), Words.Variable(subject), port, port); return null; }
            // A value with properties (a record, class or struct) comes from a step, and an Entity node's device from a Device node
            // (a structural input): nothing can be typed in, null included.
            if (SelectsProperties(Spec(node), port) || Structural(spec)) { Error(Id(node), Words.WireOnly(Words.StepName(node, Spec(node))), port, port); return null; }
            if (Text(operand, "kind") == "literal")
            {
                type = Text(operand, "type", "unknown"); var value = operand["value"];
                var valid = ValidLiteral(type, value, operand.ContainsKey("value"), snapshot);
                if (!valid) Error(Id(node), operand["type"] is not JsonValue tv || !tv.TryGetValue<string>(out var named) || named.Length == 0 ? $"{subject} has a value without a type"
                    : !KnownType(type) ? $"{subject} has a value of unknown type {Words.Name(type)}"
                    // A number that does not fit a narrower number type (2.5 for a C# int) says what does fit.
                    : Number(value) is not null && VisualTypes.Numeric(Root, type) && VisualTypes.Resolve(Root, type) is { } own
                        ? $"{subject} must be {Words.Range(Number(own["min"]), Number(own["max"]), Text(own, "literalKind") == "integer")}"
                    // A text an enum does not list (a state Home Assistant no longer has) is named.
                    : Text(VisualTypes.Resolve(Root, type), "literalKind") == "enum" && value is JsonValue listed && listed.TryGetValue<string>(out var unlisted)
                        ? $"{subject}: {unlisted} is not a value of {Words.TypeLabel(Root, type)}"
                    : $"{subject} is not a valid {Words.TypeLabel(Root, type)} value", port);
                if (Number(value) is { } number && (Number(spec["min"]) is { } min && number < min || Number(spec["max"]) is { } max && number > max || Bool(spec, "integer") && Math.Truncate(number) != number))
                    Error(Id(node), $"{subject} must be {Words.Range(Number(spec["min"]), Number(spec["max"]), Bool(spec, "integer"))}", port);
                // A number typed into a pin of a narrower number type (a C# int, say) must fit that type, as it must when it runs.
                if (valid && Number(value) is not null && VisualTypes.Numeric(Root, type) && Strings(spec["types"]).Where(t => t != type && CompatibleTypes(type, t)).ToArray() is { Length: > 0 } fits &&
                    fits.All(t => VisualTypes.Numeric(Root, t) && !ValidLiteral(t, value, true, snapshot)) && VisualTypes.Resolve(Root, fits[0]) is { } narrow)
                    Error(Id(node), $"{subject} must be {Words.Range(Number(narrow["min"]), Number(narrow["max"]), Text(narrow, "literalKind") == "integer")}", port);
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
            // An input that accepts no known type a value can have (a Constant whose Type is missing, unknown or a device) is
            // reported where that type is set.
            if (type != null && ValueType(type) && Strings(spec["types"]).Any(ValueType) && !Strings(spec["types"]).Any(t => CompatibleTypes(type, t)))
                Error(Id(node), Kind(node) == "constant" && port == "value" ? Words.ConstantRetyped(Root, label, type, Strings(spec["types"]).First(ValueType)) : Words.Incompatible(Root, subject, type, Strings(spec["types"])), port);
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
            foreach (var (key, _) in fields) if (!known.ContainsKey(key)) Error(id, $"{ServiceTitle(service)} has no field {Words.Name(key)}", "field:" + key);
            foreach (var (key, field) in known) if (Bool(field, "required") && !fields.ContainsKey(key)) Error(id, $"{ServiceTitle(service)} needs {Words.PortLabel(field, key)}", "fields");
            var wired = incoming.ContainsKey((id, "target"));
            var response = Text(schema, "response", "none"); var returnResponse = Bool(cfg, "returnResponse");
            if (returnResponse && response == "none") Error(id, $"{ServiceTitle(service)} returns no response; turn off Return response", "returnResponse");
            if (!returnResponse && response == "only") Error(id, $"{ServiceTitle(service)} only returns a response; turn on Return response", "returnResponse");
            // A target value object is checked by its entity_id domains; device, area, floor and label ids are not.
            var domains = ServiceTargetDomains(schema);
            if (domains.Length == 0 || wired || cfg["target"] is not JsonObject operand || Text(operand, "kind") != "literal" || operand["value"] is not JsonObject value) return;
            JsonNode?[] entities = value["entity_id"] switch { JsonArray list => list.ToArray(), JsonValue single => [single], _ => [] };
            var accepted = Words.Or(domains.Select(d => Words.DomainWords(d).ToLowerInvariant()));
            foreach (var entity in entities)
                if (entity is JsonValue ev && ev.TryGetValue<string>(out var entityId) && entityId is not ("all" or "none") && !domains.Contains(entityId.Split('.')[0]))
                    Error(id, $"{ServiceTitle(service)} only accepts {accepted} entities; {Words.Name(entityId)} is not " + (entityId.IndexOf('.') > 0 ? "one" : "an entity"), "target");
        }
        /// <summary>
        /// The target rules of a service node, with or without services. A target the service does not take is an
        /// error (with services only). A target object holds only <see cref="TargetKeys"/>, as the runtime enforces.
        /// A node whose target is set in any form (a value or a connection) may not also have a field named
        /// like a target key; a node without a target may (homeassistant.update_entity takes entity_id as a field).
        /// </summary>
        void TargetRules(JsonObject node, JsonObject spec, JsonObject cfg, string id)
        {
            var wired = incoming.ContainsKey((id, "target"));
            var targetSet = wired || cfg["target"] is JsonObject set && Text(set, "kind") != "unset";
            if (snapshot.Services is { } services && ServiceName(node) is { } named && Match(named, Text(spec["config"]?["service"], "pattern")) &&
                services.Items[named] is JsonObject schema && schema["target"] is not JsonObject && targetSet)
                Error(id, $"{ServiceTitle(named)} does not take a target", "target");
            if (!targetSet) return;
            if (!wired && cfg["target"] is JsonObject operand && Text(operand, "kind") == "literal" && operand["value"] is JsonObject value)
                foreach (var (key, _) in value) if (!TargetKeys.Contains(key)) Error(id, TargetKeyMessage(key), "target");
            var service = ServiceName(node) is { } serviceId ? ServiceTitle(serviceId) : "This service";
            if (cfg["fields"] is JsonObject fields)
                foreach (var (key, _) in fields) if (TargetKeys.Contains(key)) Error(id, $"{service} has a target, so it cannot also have a field named {key}", "target");
        }
        void ValidateNode(JsonObject node)
        {
            var spec = Spec(node); if (spec == null) return;
            var cfg = Config(node); var id = Id(node); var kind = Kind(node);
            // A statement-style Custom C# node is reported once, with what to do; nothing else about it is checked.
            if (Text(spec, "dynamicInputs") == "custom" && cfg.ContainsKey("code")) { Error(id, Words.OldCustomCode, "source"); return; }
            // A kind that lists the documents it works in (an entity definition's steps) is refused in any other.
            if (spec["documents"] is JsonArray documents && !Strings(documents).Contains(document)) Error(id, Words.WrongDocument(Words.StepName(node, spec), document), code: "node.document");
            // An entity graph's steps name its definition's entities and its settings.
            if (EntityGraph && spec["documents"] is JsonArray && spec["config"]?["entity"] != null && cfg["entity"] is JsonValue ev && ev.TryGetValue<string>(out var entityKey))
            {
                if (DefinitionEntity(Root, entityKey) == null) Error(id, Words.UnknownEntity(entityKey, ConfigLabel(spec["config"]?["entity"], "entity")), "entity", code: "definition.unknownEntity");
                // Show frame names one of the definition's cameras.
                else if (kind == "show_frame" && !IsCamera(entityKey))
                    Error(id, Words.NotACamera(EntityName(entityKey), DefinitionItems(Root, "entities").Any(e => IsCamera(Text(e, "key")))), "entity", code: "camera.entity");
            }
            // A Set state needs an entity whose type has a state, and at least one of its values.
            if (EntityGraph && kind == "set_entity_state" && DefinitionEntity(Root, Text(cfg, "entity")) is { } described && EntityDomain(Root, Text(cfg, "entity")) is { } row)
            {
                if (row["state"] is not JsonObject { Count: > 0 }) Error(id, Words.Stateless(Text(row, "label", Words.DomainWords(Text(described, "domain")))), "entity", code: "entity.stateless");
                else if (!StateInputs(node, Root).Any(p => Supplied(node, p.Key)))
                    Error(id, Words.NoValue(KindLabel(kind)), code: "entity.no_value");
            }
            // A text setting of an Entity or a Device node: "" when left out, null when it is not text (the config rule's below).
            string? TextSetting(string field) => !cfg.ContainsKey(field) ? "" : cfg[field] is JsonValue v && v.TryGetValue<string>(out var typed) ? typed : null;
            // Every node that declares an entity (an Entity node, a C# entity): a key of the form entity keys take. An Entity
            // node: a type Home Assistant has, a name unless it is on a device (a Device node wired to it), and none of its
            // state values or each one its type needs; a C# entity's code gives its type, name and state. A field that is
            // not text is the config rule's below.
            if (EntityGraph && Declares(spec, "entity"))
                if (TextSetting("key") is { } key)
                    if (Words.Trimmed(key).Length == 0) Error(id, Words.EntityKeyMissing, "key", code: "definition.entityKey");
                    else if (!Match(key, Words.EntityKeyPattern)) Error(id, Words.EntityKey(key), "key", code: "definition.entityKey");
            if (EntityGraph && IsHub(spec))
            {
                // A type left out is the config rule's; one given blank is said the same way.
                if (TextSetting("domain") is { } domain && cfg.ContainsKey("domain"))
                    if (Words.Trimmed(domain).Length == 0) Error(id, $"{ConfigLabel(spec["config"]?["domain"], "domain")} is required", "domain");
                    else if (DeclaredDomain(node, Root) == null) Error(id, Words.NotAnEntityType(domain), "domain", code: "definition.entityDomain");
                if (TextSetting("name") is { } name && Words.Trimmed(name).Length == 0 && !OnDevice(node)) Error(id, Words.EntityNameMissing, "name", code: "entity.name");
                // Its Device input is no state value: a wire into it neither counts as one given nor asks for the others.
                if (DeclaredDomain(node, Root)?["state"] is JsonObject state && StateInputs(node, Root).ToList() is var values && values.Where(p => Supplied(node, p.Key)).ToList() is { Count: > 0 } given)
                    foreach (var (port, input) in values)
                        if (!Supplied(node, port) && Bool(state[port], "required", true))
                            Error(id, Words.PartialState(Words.StepName(node, spec), string.Join(", ", given.Select(p => Words.PortLabel(p.Value, p.Key))), Words.PortLabel(input, port)), port, code: "entity.partialState");
                // Its run history: only its flows, each true or false (one that is not an object is the config rule's). A
                // step's trackHistory is not its: it would be ignored.
                if (cfg["history"] is JsonObject history)
                    foreach (var (member, value) in history)
                        if (member is not ("state" or "commands" or "frame")) Error(id, Words.HistoryMember(member), "history");
                        else if (value is not JsonValue flag || !flag.TryGetValue<bool>(out _)) Error(id, Words.HistoryValue(member), "history");
                if (cfg.ContainsKey("trackHistory")) Error(id, Words.HistoryNotTrack, "history");
            }
            // A Device node and a C# device: a key of the form entity keys take, a name, a Part of of its shape; a Device node
            // also a configuration URL Home Assistant takes (a C# device's code gives its own). A field that is not text, or a
            // Part of that is not an object, is the config rule's below; what Part of names, and its key among the others, are
            // the graph's (GraphRules).
            if (EntityGraph && Declares(spec, "device"))
            {
                if (TextSetting("key") is { } key)
                    if (Words.Trimmed(key).Length == 0) Error(id, Words.DeviceKeyMissing, "key", code: "definition.deviceKey");
                    else if (!Match(key, Words.EntityKeyPattern)) Error(id, Words.DeviceKey(key), "key", code: "definition.deviceKey");
                if (TextSetting("name") is { } name && Words.Trimmed(name).Length == 0) Error(id, Words.DeviceNameMissing, "name", code: "device.name");
                if (cfg["via"] is JsonObject via && !ViaShaped(via)) Error(id, Words.ViaShape, "via", code: "device.via");
                if (!IsClassNode(spec) && cfg.ContainsKey("configurationUrl") && TextSetting("configurationUrl") is { } url && !ConfigurationUrlTaken(url))
                    Error(id, Words.ConfigurationUrl(Words.StepName(node, spec)), "configurationUrl", code: "device.configurationUrl");
            }
            if (EntityGraph && kind == "setting" && cfg["key"] is JsonValue kv && kv.TryGetValue<string>(out var settingKey) && !DefinitionItems(Root, "settings").Any(s => Text(s, "key") == settingKey))
                Error(id, Words.UnknownSetting(settingKey, ConfigLabel(spec["config"]?["key"], "key")), "key", code: "setting.unknown");
            foreach (var (field, rawRule) in spec["config"]!.AsObject())
            {
                var rule = rawRule!; var value = cfg[field];
                var label = ConfigLabel(rule, field);
                if (value == null && !cfg.ContainsKey(field)) { if (Bool(rule, "required")) Error(id, $"{label} is required", field); continue; }
                // A rule that says "nullable" takes null as a value of its own (a Device node's Part of: nothing).
                if (value == null && Bool(rule, "nullable")) continue;
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
                // Type choices accept every resolvable id, so a parametric type can be selected for narrowing, but no structural
                // one (a device), which no value has.
                if (Text(rule, "enumSource") == "types") { if (value is not JsonValue tv || !tv.TryGetValue<string>(out var typeId) || !ValueType(typeId)) Error(id, $"{label} must be a known type", field); }
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
            // A Select property node: with a value connected whose type has properties, the chosen one must be one of them.
            foreach (var input in (spec["outputs"] as JsonObject ?? []).Select(o => PropertyOf(o.Value)).Where(p => p != "").Distinct())
                if (incoming.TryGetValue((id, input), out var wire) && Text(DataOutputs(nodes[Text(wire, "source")])[Text(wire, "sourcePort")], "type") is { Length: > 0 } valueType &&
                    TypeProperties(Root, valueType) is { Length: > 0 } members)
                {
                    var chosen = Text(cfg, "property");
                    if (chosen == "") Error(id, Words.PropertyUnset(Words.TypeLabel(Root, valueType)), "property", code: "property.unset");
                    else if (!members.Any(m => Text(m, "name") == chosen)) Error(id, Words.PropertyUnknown(Words.TypeLabel(Root, valueType), chosen), "property", code: "property.unknown");
                }
            // A Repeat count typed in above the loop's own Maximum iterations would fail when the loop starts.
            if (kind == "repeat" && !incoming.ContainsKey((id, "count")) && TryGetInputValue(node, "count", out var count) && count is JsonObject countOperand && Text(countOperand, "kind") == "literal" &&
                Number(countOperand["value"]) is { } times && Number(cfg["maxIterations"]) is { } most && times > most && DataInputs(node, Owner(node))["count"] is var countSpec && !(Number(countSpec?["max"]) is { } inputMax && times > inputMax))
                Error(id, Words.RepeatCount(Words.PortLabel(countSpec, "count"), most, ConfigLabel(spec["config"]?["maxIterations"], "maxIterations")), "count");
            if (spec["comparison"] is JsonObject comparison)
            {
                var leftInput = Text(comparison, "leftInput"); var typeConfig = Text(comparison, "typeConfig");
                var left = leftInput != "" ? resolved.GetValueOrDefault(leftInput) : Text(cfg, typeConfig);
                var rightInput = Text(comparison, "rightInput"); var right = resolved.GetValueOrDefault(rightInput);
                if (!string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right) && !CompatibleTypes(left, right) && !CompatibleTypes(right, left)) Error(id, "Both sides of the comparison must have the same type", rightInput);
                // Ordering takes any number type on either side (number and every CLR number type); the run time widens both.
                var orderingType = Text(comparison, "orderingType");
                bool Ordered(string? type) => type == orderingType || !string.IsNullOrEmpty(type) && VisualTypes.Numeric(Root, VisualTypes.RootId(Root, type));
                if (Strings(comparison["orderingOperators"]).Contains(Text(cfg, Text(comparison, "operatorConfig"))) && (!Ordered(left) || !string.IsNullOrEmpty(right) && !Ordered(right)))
                    Error(id, $"Less-than and greater-than comparisons need {Words.TypeLabel(Root, orderingType)} values", !Ordered(left) ? leftInput != "" ? leftInput : typeConfig : rightInput);
            }
            if (Text(spec, "dynamicInputs") == "service") { Service(node, spec, cfg, id); TargetRules(node, spec, cfg, id); }
            if (cfg.ContainsKey("output")) Error(id, Words.ExposedValue, "output");
            if (Text(spec, "dynamicInputs") == "custom" && cfg["signature"] is JsonObject signature && !WellFormedSignature(signature)) Error(id, Words.MalformedSignature, "source", code: "custom.signature");
            // A class node whose settings cannot be read has no setting pins; its code, applied again, writes them.
            if (IsClassNode(spec) && cfg["signature"] is JsonObject && ClassSignature(node, spec) == null) Error(id, Words.UnreadableSettings, "signature", code: "csharp.signature");
            if (kind == "trigger")
            {
                var type = TriggerType(node, snapshot);
                string[] required = type switch { "ha" => ["trigger"], "entity" => ["entity"], "daily" => ["time", "timeZone"], "once" => ["at"], _ => [] };
                foreach (var field in required) if (!cfg.ContainsKey(field) || cfg[field] is JsonValue v && v.TryGetValue<string>(out var s) && s == "") Error(id, $"{ConfigLabel(spec["config"]?[field], field)} is required", field);
                // The generator reads a daily time as exactly HH:MM.
                if (type == "daily" && cfg["time"] is JsonValue time && time.TryGetValue<string>(out var at) && at != "" && !Match(at, Words.TimeOfDay))
                    Error(id, Words.TimeOfDayMessage(ConfigLabel(spec["config"]?["time"], "time")), "time");
                if (type == "ha" && cfg["trigger"] is JsonObject trigger && string.IsNullOrWhiteSpace(Text(trigger, "trigger")) && string.IsNullOrWhiteSpace(Text(trigger, "platform"))) Error(id, "Home Assistant trigger needs a platform", "trigger");
                if (cfg.ContainsKey("entitiesExpression")) Error(id, Words.EntitiesExpression, "entitiesExpression");
                if (cfg.ContainsKey("schedulesExpression")) Error(id, Words.SchedulesExpression, "schedulesExpression");
            }
            if (kind is "entity_field" or "wait_until" && Text(cfg, "field") == "attribute" && string.IsNullOrWhiteSpace(Text(cfg, "attribute"))) Error(id, "Attribute is required", "attribute");
            // A path that starts with / can still be wrong in one way: a ~ that is not ~0 (a ~ in a key) or ~1 (a / in a key).
            if (kind == "get_field" && (cfg["pointer"] is not JsonValue pointer || !pointer.TryGetValue<string>(out var p) || !Match(p, "^(?:/(?:[^~]|~[01])*)*$")))
                Error(id, cfg["pointer"] is JsonValue path && path.TryGetValue<string>(out var text) && text.StartsWith('/') ? Words.PathTilde : "Path must start with /, such as /state", "pointer");
            // An address or a checksum typed in (not connected) that cannot be one: an HTTP request's or a download's.
            if (kind is "http_request" or "download_file")
            {
                string? Typed(string port) => !incoming.ContainsKey((id, port)) && TryGetInputValue(node, port, out var operand) && operand is JsonObject literal && Text(literal, "kind") == "literal"
                    && literal["value"] is JsonValue typed && typed.TryGetValue<string>(out var value) ? value : null;
                var inputs = DataInputs(node, Owner(node));
                if (Typed("url") is { } url && !Match(url, Words.WebAddressPattern)) Error(id, Words.WebAddress(Words.PortLabel(inputs["url"], "url")), "url", code: "http.url");
                // A body, connected or typed in, on a method that sends none.
                var method = cfg["method"] is JsonValue chosen && chosen.TryGetValue<string>(out var named) ? named : "GET";
                if (kind == "http_request" && method is "GET" or "HEAD" && Supplied(node, "body"))
                    Error(id, Words.BodyMethod(Words.PortLabel(inputs["body"], "body"), method), "body", code: "http.body");
                if (kind == "download_file" && Typed("sha256") is { } checksum && !Match(checksum, Words.ChecksumPattern)) Error(id, Words.Checksum(Words.PortLabel(inputs["sha256"], "sha256")), "sha256", code: "download.checksum");
            }
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
                        if (!Match(name, Text(Root["structures"]?["loopDeclaration"]?["name"], "pattern")) || names.Contains(name) || !KnownType(type)) { Error(id, $"The {(group == "carry" ? "carried" : "exported")} values of this loop need unique names and known types", group); continue; }
                        names.Add(name);
                        Operand(node, $"{group}.{name}.initial", decl["initial"], new JsonObject { ["types"] = new JsonArray(type), ["required"] = true, ["description"] = "Starting value", ["label"] = "Starting value of " + name });
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
