using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using static HassSharp.ServiceGenerator;

namespace HassSharp;

/// <summary>
/// Entity handle classes for node code, from the normalised entity schema (<see cref="EntitySchemaCatalog"/> items):
/// one sealed <c>LightEntity : Entity</c> per domain, with a typed <c>State</c> and one property per attribute, and
/// <c>EntityHandles.Create</c>, which gives the handle of an entity id.
/// <para>
/// Types: a domain other than <c>sensor</c> that lists states gets an enum of them (in ordinal order, then
/// <c>unknown</c> and <c>unavailable</c> when not listed, then a catch-all); otherwise <c>State</c> is the text.
/// An attribute whose values are exactly <c>false</c> and <c>true</c> is a <c>bool?</c>; one with other listed values
/// a nullable enum with a catch-all (when several attributes of a domain list the same values, only the shortest name
/// gets the enum); any other attribute a <c>JsonElement?</c>. Each is null when the entity has no such attribute. A flag
/// is also null for a value that is not JSON true or false; an enum is null for JSON null, and the catch-all for another
/// value that is not one of the listed texts. Each enum member but the catch-all carries Home Assistant's text and label
/// in an <see cref="Entities.EntityTextAttribute"/>.
/// </para>
/// <para>
/// Names follow <see cref="ServiceGenerator"/>'s: words in PascalCase, told apart by a numeric suffix in ordinal order
/// of the ids, the first id of a name keeping it. Type names keep clear of the hand-written types of the namespace,
/// property names of the base class's members, <c>State</c> and the class's own name.
/// </para>
/// The output depends only on the schema: the same schema gives the same source, byte for byte.
/// <para>
/// The schema is Home Assistant's translation text, so it is untrusted: it reaches the source only as identifiers made
/// by <see cref="ServiceGenerator.Words"/>, as string literals made by <see cref="ServiceGenerator.Literal"/>, and as
/// doc comment text made by <see cref="ServiceGenerator.Xml"/>.
/// </para>
/// </summary>
static class EntityGenerator
{
    public const string Namespace = "HassSharp.Entities";
    public const string Factory = "EntityHandles";
    const string Global = "global::" + Namespace + ".";
    const string Kind = "global::System.Text.Json.JsonValueKind";
    const string CatchAll = "Other";
    // The text of sensor states is a measurement as often as one of the listed words.
    const string TextState = "sensor";
    // The public types of the namespace written by hand.
    static readonly string[] OwnTypes = ["Entity", Factory, "EntityText", "EntityTextAttribute", "EntityDomainAttribute", "EntityJsonConverter"];
    // The members of Entity, and State, which every class has.
    static readonly string[] BaseMembers = ["EntityId", "RawState", "NumericState", "Attributes", "Attribute", "Snapshot", "State"];
    static readonly (string Text, string Label)[] Added = [("unknown", "Unknown"), ("unavailable", "Unavailable")];

    sealed record Option(string Text, string? Label);
    sealed record Member(string Name, string? Text, string? Label);
    sealed record EnumType(string Name, string Summary, Member[] Members)
    {
        public Member Other => Members[^1];
    }
    enum Shape { Json, Flag, Enum }
    sealed record Property(string Key, string Name, string Summary, Shape Shape, EnumType? Enum);
    sealed record Domain(string Id, string Class, EnumType? State, Property[] Properties);

    /// <summary>Where each domain's code is, by 1-based line: its class and enums, and its arm in the factory.</summary>
    public sealed record Generated(string Source, IReadOnlyList<Span> Spans)
    {
        /// <summary>The domains a compile error at <paramref name="line"/> belongs to; empty when it is in no domain's code.</summary>
        public IEnumerable<string> DomainsAt(int line) => Spans.Where(s => line >= s.StartLine && line <= s.EndLine).Select(s => s.Id).Distinct(StringComparer.Ordinal);
    }

    /// <summary>The source of the entity classes; <paramref name="domains"/> are keyed by domain.</summary>
    public static Generated Generate(IEnumerable<KeyValuePair<string, JsonObject>> domains)
    {
        var byDomain = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (id, schema) in domains) byDomain[id] = schema;

        var states = byDomain.ToDictionary(p => p.Key, p => p.Key == TextState ? [] : Options(p.Value["states"]), StringComparer.Ordinal);
        var attributes = byDomain.ToDictionary(p => p.Key, p => Attributes(p.Value), StringComparer.Ordinal);
        // Type names are given together, by ids "light", "light.state" and "light.state_attributes.<attribute>": as '.'
        // sorts before every character of a domain, a domain's ids come right after its own and before a longer domain's.
        var typeIds = new List<(string Id, string Name)>();
        foreach (var (domain, _) in byDomain)
        {
            var stem = Words(domain, pascal: true);
            typeIds.Add((domain, stem + "Entity"));
            if (states[domain].Length > 0) typeIds.Add((StateId(domain), stem + "State"));
            foreach (var (key, shape, _, _) in attributes[domain])
                if (shape == Shape.Enum) typeIds.Add((AttributeId(domain, key), stem + Words(key, pascal: true)));
        }
        var typeNames = Assign(typeIds, OwnTypes);
        var model = byDomain.Keys.Select(domain => DomainOf(domain, typeNames, states[domain], attributes[domain])).ToArray();

        var sb = new StringBuilder();
        var lines = 0;
        var spans = new List<Span>();
        void Line(int indent, string text) { sb.Append(' ', indent * 4).Append(text).Append('\n'); lines++; }
        void Doc(int indent, string text)
        {
            // Schema text is made one line by Xml; this guards the generator's own text, so a doc line never ends early.
            if (text.AsSpan().IndexOfAny("\r\n\u0085\u2028\u2029") >= 0) throw new InvalidOperationException("A doc comment line of the entity classes has a line break");
            Line(indent, "/// <summary>" + text + "</summary>");
        }
        Line(0, "// <auto-generated>");
        Line(0, "// Entity handles for node code, generated from this Home Assistant's entity states and attributes.");
        Line(0, "// </auto-generated>");
        Line(0, "#nullable enable");
        Line(0, $"namespace {Namespace}");
        Line(0, "{");
        foreach (var domain in model)
        {
            var start = lines + 1;
            WriteClass(domain);
            if (domain.State != null) WriteEnum(domain.State);
            foreach (var property in domain.Properties) if (property.Enum != null) WriteEnum(property.Enum);
            spans.Add(new(domain.Id, true, start, lines));
            Line(0, "");
        }
        Doc(1, $"Gives the handle of an entity id: the class of its domain, or <see cref=\"{Global}Entity\"/> for a domain without one.");
        Line(1, $"public static class {Factory}");
        Line(1, "{");
        Line(2, $"public static {Global}Entity Create(string entityId) => (entityId ?? throw new global::System.ArgumentNullException(nameof(entityId))).Split('.')[0] switch");
        Line(2, "{");
        foreach (var domain in model)
        {
            Line(3, $"{Literal(domain.Id)} => new {Global}{domain.Class}(entityId),");
            spans.Add(new(domain.Id, true, lines, lines));
        }
        Line(3, $"_ => new {Global}Entity(entityId),");
        Line(2, "};");
        Line(1, "}");
        Line(0, "}");
        return new(sb.ToString(), spans);

        void WriteClass(Domain domain)
        {
            Doc(1, $"A handle on an entity of the <c>{Xml(domain.Id)}</c> domain.");
            Line(1, $"[global::System.Text.Json.Serialization.JsonConverter(typeof({Global}EntityJsonConverter))]");
            Line(1, $"[{Global}EntityDomain({Literal(domain.Id)})]");
            Line(1, $"public sealed class {domain.Class} : {Global}Entity");
            Line(1, "{");
            Line(2, $"public {domain.Class}(string entityId) : base(entityId)");
            Line(2, "{");
            Line(3, $"if (!entityId.StartsWith({Literal(domain.Id + ".")}, global::System.StringComparison.Ordinal))");
            Line(4, $"throw new global::System.ArgumentException(\"'\" + entityId + {Literal("' is not an entity of the " + domain.Id + " domain")}, nameof(entityId));");
            Line(2, "}");
            if (domain.State is { } state)
            {
                Doc(2, $"The state now; <c>{state.Other.Name}</c> for a state Home Assistant does not list.");
                Line(2, $"public {Global}{state.Name} State => RawState switch");
                Cases(state, 2);
                Line(2, "};");
            }
            else
            {
                Doc(2, "The state now, as Home Assistant has it.");
                Line(2, "public string State => RawState;");
            }
            foreach (var property in domain.Properties)
            {
                var read = $"Attribute({Literal(property.Key)})";
                Doc(2, property.Summary);
                switch (property.Shape)
                {
                    case Shape.Flag:
                        Line(2, $"public bool? {Escape(property.Name)} => {read} switch");
                        Line(2, "{");
                        Line(3, $"{{ ValueKind: {Kind}.True }} => true,");
                        Line(3, $"{{ ValueKind: {Kind}.False }} => false,");
                        Line(3, "_ => null,");
                        Line(2, "};");
                        break;
                    case Shape.Enum:
                        var type = property.Enum!;
                        Line(2, $"public {Global}{type.Name}? {Escape(property.Name)} => {read} switch");
                        Line(2, "{");
                        Line(3, $"null or {{ ValueKind: {Kind}.Null }} => null,");
                        Line(3, $"{{ ValueKind: {Kind}.String }} __value => __value.GetString() switch");
                        Cases(type, 3);
                        Line(3, "},");
                        Line(3, $"_ => {Global}{type.Name}.{Escape(type.Other.Name)},");
                        Line(2, "};");
                        break;
                    default:
                        Line(2, $"public global::System.Text.Json.JsonElement? {Escape(property.Name)} => {read};");
                        break;
                }
            }
            Line(1, "}");
        }

        // The arms of a switch on the text, from its opening brace; the caller closes it.
        void Cases(EnumType type, int indent)
        {
            Line(indent, "{");
            foreach (var member in type.Members)
                if (member.Text != null) Line(indent + 1, $"{Literal(member.Text)} => {Global}{type.Name}.{Escape(member.Name)},");
            Line(indent + 1, $"_ => {Global}{type.Name}.{Escape(type.Other.Name)},");
        }

        void WriteEnum(EnumType type)
        {
            Doc(1, type.Summary);
            Line(1, $"public enum {type.Name}");
            Line(1, "{");
            foreach (var member in type.Members)
                Line(2, member.Text == null ? $"{Escape(member.Name)},"
                    : $"[{Global}EntityText({Literal(member.Text)}{(member.Label == null ? "" : $", Label = {Literal(member.Label)}")})] {Escape(member.Name)},");
            Line(1, "}");
        }
    }

    static string StateId(string domain) => domain + ".state";
    static string AttributeId(string domain, string key) => domain + ".state_attributes." + key;

    static Domain DomainOf(string id, Dictionary<string, string> typeNames, Option[] states, (string Key, Shape Shape, string? Label, Option[] Values)[] attributes)
    {
        var @class = typeNames[id];
        var state = states.Length == 0 ? null
            : EnumOf(typeNames[StateId(id)], $"The states of <c>{Xml(id)}</c> entities.", states, Added.Where(a => !states.Any(s => s.Text == a.Text)).Select(a => new Option(a.Text, a.Label)));
        var names = Assign(attributes.Select(a => (a.Key, Words(a.Key, pascal: true))), [.. BaseMembers, .. ObjectMembers, @class]);
        var properties = attributes.Select(a =>
        {
            var label = Xml(a.Label);
            var summary = label.Length == 0 || label == Xml(a.Key) ? $"<c>{Xml(a.Key)}</c>." : $"{label} (<c>{Xml(a.Key)}</c>).";
            var type = a.Shape == Shape.Enum
                ? EnumOf(typeNames[AttributeId(id, a.Key)], $"The values of the <c>{Xml(a.Key)}</c> attribute of <c>{Xml(id)}</c> entities.", a.Values, [])
                : null;
            summary += a.Shape switch
            {
                Shape.Flag => " Null when the entity has none, or it is not true or false.",
                Shape.Enum => $" Null when the entity has none; <c>{type!.Other.Name}</c> for a value Home Assistant does not list.",
                _ => " Null when the entity has none.",
            };
            return new Property(a.Key, names[a.Key], summary, a.Shape, type);
        }).ToArray();
        return new(id, @class, state, properties);
    }

    // The listed texts take their names first, then the added ones, then the catch-all, each the lowest free one.
    static EnumType EnumOf(string name, string summary, Option[] listed, IEnumerable<Option> added)
    {
        var names = Assign(listed.Select(o => (o.Text, Words(o.Text, pascal: true))), [name]);
        var taken = new HashSet<string>([.. names.Values, name], StringComparer.Ordinal);
        var members = listed.Select(o => new Member(names[o.Text], o.Text, o.Label)).ToList();
        foreach (var option in added) members.Add(new(Free(taken, Words(option.Text, pascal: true)), option.Text, option.Label));
        members.Add(new(Free(taken, CatchAll), null, null));
        return new(name, summary, [.. members]);
    }

    static string Free(HashSet<string> taken, string name)
    {
        if (taken.Add(name)) return name;
        for (var n = 2; ; n++)
            if (taken.Add(name + n.ToString(CultureInfo.InvariantCulture))) return name + n.ToString(CultureInfo.InvariantCulture);
    }

    // A domain's attributes in ordinal order, each with its shape: of attributes listing the same values, the shortest
    // name (the ordinal first of equal lengths) is the enum and the others JSON.
    static (string Key, Shape Shape, string? Label, Option[] Values)[] Attributes(JsonObject domain)
    {
        var listed = (domain["attributes"] as JsonObject ?? []).Where(p => p.Value is JsonObject)
            .Select(p => (p.Key, Label: Text(p.Value!, "label"), Values: Options(p.Value!["values"])))
            .OrderBy(a => a.Key, StringComparer.Ordinal).ToArray();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var enums = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, _, values) in listed.Where(a => a.Values.Length > 0 && !IsFlag(a.Values)).OrderBy(a => a.Key.Length).ThenBy(a => a.Key, StringComparer.Ordinal))
            if (claimed.Add(new JsonArray([.. values.Select(v => (JsonNode)v.Text)]).ToJsonString())) enums.Add(key);
        return listed.Select(a => (a.Key, IsFlag(a.Values) ? Shape.Flag : enums.Contains(a.Key) ? Shape.Enum : Shape.Json, a.Label, a.Values)).ToArray();
    }

    static bool IsFlag(Option[] values) => values is [{ Text: "false" }, { Text: "true" }];

    // Options in ordinal order of their text, each text once.
    static Option[] Options(JsonNode? list) => (list as JsonArray ?? []).OfType<JsonObject>()
        .Select(o => Text(o, "id") is { } text ? new Option(text, Text(o, "label")) : null).OfType<Option>()
        .DistinctBy(o => o.Text, StringComparer.Ordinal).OrderBy(o => o.Text, StringComparer.Ordinal).ToArray();

    static string? Text(JsonNode owner, string key) => owner[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
