using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis.CSharp;

namespace HassSharp;

/// <summary>
/// Typed service calls for node code, from the normalised service schema (<see cref="ServiceSchemaCatalog"/> items):
/// <c>context.Services.Light.TurnOn("light.kitchen", brightnessPct: 30)</c>. One property per domain and one method
/// per service; every call goes by its original service id through <see cref="NodeContext.CallService"/>.
/// <para>
/// Names: domain and service in PascalCase, fields in camelCase, splitting at every character that cannot be part of an
/// identifier and at <c>_</c>. A name that is empty becomes <c>_</c>; one that starts with a digit gets a <c>_</c> prefix;
/// a keyword is written with <c>@</c>. Names that are equal after this, or equal a name the type needs for itself, are
/// told apart by a numeric suffix (2, 3, …) given in ordinal order of the original ids; the first id of each name keeps it.
/// </para>
/// The output depends only on the schema: the same schema gives the same source, byte for byte.
/// <para>
/// The schema is text from Home Assistant and from every integration's <c>services.yaml</c>, so it is untrusted: it
/// reaches the source only as identifiers made by <see cref="Words"/>, as string literals made by
/// <see cref="SymbolDisplay.FormatLiteral(string, bool)"/>, and as doc comment text made by <see cref="Xml"/>.
/// </para>
/// </summary>
static class ServiceGenerator
{
    public const string Namespace = "HassSharp.ServiceCalls";
    const string Root = "Services";
    const string WithResponse = "WithResponse";
    const string Context = "global::HassSharp.NodeContext";
    const string Target = "global::HassSharp.ServiceTarget";
    const string Task = "global::System.Threading.Tasks.Task";
    const string Element = "global::System.Text.Json.JsonElement";
    // Members every class has: a method of the same name would hide or overload them.
    static readonly string[] ObjectMembers = ["Equals", "Finalize", "GetHashCode", "GetType", "MemberwiseClone", "ReferenceEquals", "ToString"];

    sealed record Field(string Key, string Name, string Type, bool Required, bool IsTarget, string Doc, int Order);
    sealed record Service(string Id, string ServiceName, string Name, string? ResponseName, string Response, bool HasTarget, string TargetDoc, string Summary, Field[] Fields);
    sealed record Domain(string Id, string Name, string Class, Service[] Services);

    /// <summary>
    /// Where a service's or a domain's code is in the source, by 1-based line: a compile error inside it is that
    /// service's (or, for a domain, all of its services').
    /// </summary>
    public sealed record Span(string Id, bool IsDomain, int StartLine, int EndLine);
    public sealed record Generated(string Source, IReadOnlyList<Span> Spans)
    {
        /// <summary>The services a compile error at <paramref name="line"/> belongs to; empty when it is in no service's code.</summary>
        public IEnumerable<string> ServicesAt(int line, IEnumerable<string> services)
        {
            var span = Spans.Where(s => line >= s.StartLine && line <= s.EndLine).OrderBy(s => s.IsDomain).FirstOrDefault();
            return span == null ? [] : span.IsDomain ? services.Where(id => id.StartsWith(span.Id + ".", StringComparison.Ordinal)) : [span.Id];
        }
    }

    /// <summary>The source of the services tree; <paramref name="services"/> are keyed by <c>domain.service</c>.</summary>
    public static string Generate(IEnumerable<KeyValuePair<string, JsonObject>> services) => GenerateWithSpans(services).Source;

    /// <summary>The source of the services tree, with where each service's and domain's code is.</summary>
    public static Generated GenerateWithSpans(IEnumerable<KeyValuePair<string, JsonObject>> services)
    {
        var byDomain = new SortedDictionary<string, SortedDictionary<string, JsonObject>>(StringComparer.Ordinal);
        foreach (var (id, schema) in services)
            if (id.Split('.') is [{ Length: > 0 } domain, { Length: > 0 } service])
            {
                if (!byDomain.TryGetValue(domain, out var list)) byDomain[domain] = list = new(StringComparer.Ordinal);
                list[service] = schema;
            }
        var domainNames = Assign(byDomain.Keys.Select(d => (d, Words(d, pascal: true))), [Root, .. ObjectMembers]);
        var domains = byDomain.Select(pair => DomainOf(pair.Key, domainNames[pair.Key], pair.Value)).Where(d => d.Services.Length > 0).ToArray();

        var sb = new StringBuilder();
        var lines = 0;
        var spans = new List<Span>();
        void Line(int indent, string text) { sb.Append(' ', indent * 4).Append(text).Append('\n'); lines++; }
        void Doc(int indent, string tag, string text, string? name = null)
        {
            // Schema text is made one line by Xml; this guards the generator's own text, so a doc line never ends early.
            if (text.AsSpan().IndexOfAny("\r\n\u0085\u2028\u2029") >= 0) throw new InvalidOperationException("A doc comment line of the typed service calls has a line break");
            Line(indent, name == null ? $"/// <{tag}>" : $"/// <{tag} name=\"{Xml(name)}\">");
            Line(indent, "/// " + text);
            Line(indent, $"/// </{tag}>");
        }
        Line(0, "// <auto-generated>");
        Line(0, "// Typed service calls for node code, generated from this Home Assistant's service descriptions.");
        Line(0, "// </auto-generated>");
        Line(0, "#nullable enable");
        Line(0, "namespace HassSharp");
        Line(0, "{");
        Doc(1, "summary", "Gives <see cref=\"global::HassSharp.NodeContext\"/> its <c>Services</c>.");
        Line(1, "public static class NodeContextServices");
        Line(1, "{");
        Line(2, $"extension({Context} context)");
        Line(2, "{");
        Doc(3, "summary", "This Home Assistant's services, by domain, with typed fields: <c>await context.Services.Light.TurnOn(\"light.kitchen\", brightnessPct: 30);</c> " +
            "The methods follow the instance's own services. When a service goes away, code that calls it stops compiling at its next compile; " +
            "an automation that is running keeps running. For a service id known only at run time, use <c>context.CallService</c>. " +
            "Each call waits until Home Assistant has run the service; when the run stops, the run stops waiting for it (the call already sent is not cancelled).");
        Line(3, $"public global::{Namespace}.{Root} Services => new global::{Namespace}.{Root}(context);");
        Line(2, "}");
        Line(1, "}");
        Line(0, "}");
        Line(0, "");
        Line(0, $"namespace {Namespace}");
        Line(0, "{");
        Doc(1, "summary", "This Home Assistant's services, one property per domain.");
        Line(1, $"public sealed class {Root}");
        Line(1, "{");
        Line(2, $"readonly {Context} __context;");
        Line(2, $"internal {Root}({Context} context) => __context = context;");
        // This assembly is loaded into each automation's own load context, so the serializer's caches of the automation's
        // types (an enum or anonymous object of its code) go when the context unloads.
        Line(2, "internal static readonly global::System.Text.Json.JsonSerializerOptions __json = global::HassSharp.ServiceData.Options();");
        foreach (var domain in domains)
        {
            Line(0, "");
            var start = lines + 1;
            Doc(2, "summary", DomainSummary(domain));
            Line(2, $"public global::{Namespace}.{domain.Class} {domain.Name} => new global::{Namespace}.{domain.Class}(__context);");
            spans.Add(new(domain.Id, true, start, lines));
        }
        Line(1, "}");
        foreach (var domain in domains)
        {
            Line(0, "");
            var start = lines + 1;
            Doc(1, "summary", DomainSummary(domain));
            Line(1, $"public sealed class {domain.Class}");
            Line(1, "{");
            Line(2, $"readonly {Context} __context;");
            Line(2, $"internal {domain.Class}({Context} context) => __context = context;");
            foreach (var service in domain.Services)
            {
                Method(service, service.Name, response: service.Response == "only");
                if (service.ResponseName != null) Method(service, service.ResponseName, response: true);
            }
            Line(1, "}");
            spans.Add(new(domain.Id, true, start, lines));
        }
        Line(0, "}");
        return new(sb.ToString(), spans);

        void Method(Service service, string name, bool response)
        {
            Line(0, "");
            var start = lines + 1;
            var summary = service.Summary;
            if (service.ResponseName != null && !response) summary += $" <c>{service.ResponseName}</c> also gives its response.";
            Doc(2, "summary", summary);
            var parameters = new List<string>();
            if (service.HasTarget)
            {
                Doc(2, "param", service.TargetDoc, "target");
                parameters.Add($"{Target}? target");
            }
            foreach (var field in service.Fields)
            {
                Doc(2, "param", field.Doc, Unescaped(field.Name));
                parameters.Add(field.Required ? $"{field.Type} {field.Name}" : $"{field.Type}? {field.Name} = null");
            }
            Doc(2, "returns", response ? "The service's response." : "Completes when Home Assistant has run the service.");
            Line(2, (response ? $"public async {Task}<{Element}> " : $"public {Task} ") + name + "(" + string.Join(", ", parameters) + ")");
            Line(2, "{");
            Line(3, "var __data = new global::System.Text.Json.Nodes.JsonObject();");
            foreach (var field in service.Fields)
            {
                // Values are sent as HassServices sends them: snake_case member names, enums by name.
                var value = field.IsTarget ? $"{field.Name}.ToJson()" : $"global::HassSharp.ServiceData.Value({field.Name}, global::{Namespace}.{Root}.__json)";
                var set = $"__data[{Literal(field.Key)}] = {value};";
                Line(3, field.Required ? set : $"if ({field.Name} is not null) {set}");
            }
            var call = $"__context.CallService({Literal(service.Id)}, {(service.HasTarget ? "target?.ToJson()" : "null")}, __data{(response ? ", true" : "")})";
            Line(3, response ? $"return await {call} ?? default;" : $"return {call};");
            Line(2, "}");
            spans.Add(new(service.Id, false, start, lines));
        }
    }

    static Domain DomainOf(string id, string name, SortedDictionary<string, JsonObject> services)
    {
        var @class = name + Root;
        var stems = Assign(services.Keys.Select(s => (s, Words(s, pascal: true))), [@class, .. ObjectMembers]);
        var taken = new HashSet<string>(stems.Values.Concat([@class, .. ObjectMembers]), StringComparer.Ordinal);
        var result = new List<Service>();
        foreach (var (service, schema) in services)
        {
            var response = Text(schema, "response") is "optional" or "only" ? Text(schema, "response")! : "none";
            string? responseName = null;
            if (response == "optional")
            {
                responseName = stems[service] + WithResponse;
                for (var n = 2; !taken.Add(responseName); n++) responseName = stems[service] + WithResponse + n.ToString(CultureInfo.InvariantCulture);
            }
            var target = schema["target"] as JsonObject;
            result.Add(new(id + "." + service, service, stems[service], responseName, response, target != null, TargetDoc(target),
                ServiceSummary(id + "." + service, schema), FieldsOf(schema, target != null)));
        }
        return new(id, name, @class, [.. result]);
    }

    static Field[] FieldsOf(JsonObject schema, bool hasTarget)
    {
        var fields = (schema["fields"] as JsonObject ?? []).Where(p => p.Value is JsonObject).Select(p => (p.Key, Spec: (JsonObject)p.Value!)).ToArray();
        var names = Assign(fields.Select(f => (f.Key, Words(f.Key, pascal: false))), hasTarget ? ["target"] : []);
        return fields.Select(f =>
        {
            var (type, isTarget) = ClrType(f.Spec);
            var order = f.Spec["order"] is JsonValue o && o.TryGetValue<int>(out var at) ? at : int.MaxValue;
            return new Field(f.Key, Escape(names[f.Key]), type, f.Spec["required"] is JsonValue r && r.TryGetValue<bool>(out var required) && required, isTarget, FieldDoc(f.Key, f.Spec), order);
        }).OrderBy(f => f.Required ? 0 : 1).ThenBy(f => f.Order).ThenBy(f => f.Key, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The CLR type of a field, as the Call service step maps its selector; several accepted types give <c>object</c>.</summary>
    static (string Type, bool IsTarget) ClrType(JsonObject field)
    {
        var types = (field["types"] as JsonArray ?? []).Select(t => t is JsonValue v && v.TryGetValue<string>(out var s) ? s : "").ToArray();
        if (types.Length != 1) return ("object", false);
        var selector = (field["selector"] as JsonObject)?.FirstOrDefault().Key;
        return types[0] switch
        {
            "boolean" => ("bool", false),
            "number" => (field["integer"] is JsonValue i && i.TryGetValue<bool>(out var integer) && integer ? "int" : "double", false),
            "string" => ("string", false),
            var entity when entity == "entity_id" || entity.StartsWith("entity_id:", StringComparison.Ordinal) => ("string", false),
            "target" => (Target, true),
            "array" => (selector switch
            {
                "text" or "state" or "select" or "entity" or "device" or "area" or "floor" or "label" => "string[]",
                "color_rgb" => "int[]",
                _ => "object[]",
            }, false),
            _ => ("object", false),
        };
    }

    static string DomainSummary(Domain domain) => $"The services of the <c>{Xml(domain.Id)}</c> domain.";

    static string ServiceSummary(string id, JsonObject schema)
    {
        var name = Xml(Text(schema, "name"));
        var service = id[(id.IndexOf('.') + 1)..];
        var head = name.Length == 0 || name == Xml(service) ? $"<c>{Xml(id)}</c>." : $"{name} (<c>{Xml(id)}</c>).";
        return Join(head, Xml(Text(schema, "description")));
    }

    static string TargetDoc(JsonObject? target)
    {
        var text = "What the service acts on: an entity id, an array or list of them, or a <see cref=\"global::HassSharp.ServiceTarget\"/> for areas, devices, floors and labels; null for none.";
        var domains = (target?["entity"] as JsonArray ?? []).OfType<JsonObject>().SelectMany(f => f["domain"] as JsonArray ?? [])
            .Select(d => d is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToArray();
        return domains.Length == 0 ? text : Join(text, $"Entities of the {string.Join(", ", domains.Select(d => $"<c>{Xml(d)}</c>"))} domain{(domains.Length > 1 ? "s" : "")}.");
    }

    static string FieldDoc(string key, JsonObject field)
    {
        var label = Xml(Text(field, "label"));
        var parts = new List<string> { label.Length == 0 || label == Xml(key) ? $"<c>{Xml(key)}</c>." : $"{label} (<c>{Xml(key)}</c>).", Xml(Text(field, "description")) };
        var unit = Xml(Text(field, "unit")) is { Length: > 0 } u ? " " + u : "";
        var (min, max) = (Number(field["min"]), Number(field["max"]));
        if (min != null && max != null) parts.Add($"From {min} to {max}{unit}.");
        else if (min != null) parts.Add($"At least {min}{unit}.");
        else if (max != null) parts.Add($"At most {max}{unit}.");
        else if (unit.Length > 0) parts.Add($"In{unit}.");
        if (field["enum"] is JsonArray values)
        {
            var labels = field["enumLabels"] as JsonArray;
            parts.Add((values.Count == 1 ? "Only " : "One of ") + string.Join(", ", values.Select((v, i) =>
            {
                var raw = v is JsonValue s && s.TryGetValue<string>(out var text) ? text : v?.ToJsonString() ?? "null";
                return labels?.ElementAtOrDefault(i) is JsonValue l && l.TryGetValue<string>(out var name) && Xml(name) is { Length: > 0 } shown && shown != Xml(raw) ? $"<c>{Xml(raw)}</c> ({shown})" : $"<c>{Xml(raw)}</c>";
            })) + ".");
        }
        var (fewest, most) = (Number(field["minItems"]), Number(field["maxItems"]));
        if (fewest != null && fewest == most) parts.Add($"Exactly {fewest} values.");
        else if (fewest != null || most != null) parts.Add(fewest != null && most != null ? $"From {fewest} to {most} values." : fewest != null ? $"At least {fewest} values." : $"At most {most} values.");
        parts.Add(((field["selector"] as JsonObject)?.FirstOrDefault().Key) switch
        {
            "time" => "As <c>HH:MM</c> or <c>HH:MM:SS</c>.",
            "date" => "As <c>YYYY-MM-DD</c>.",
            "datetime" => "As <c>YYYY-MM-DD HH:MM:SS</c>.",
            "duration" => "A number of seconds, or an object such as <c>new { minutes = 5 }</c>.",
            _ => "",
        });
        if (field["default"] is { } fallback) parts.Add($"Home Assistant's default is <c>{Xml(fallback.ToJsonString())}</c>.");
        return Join([.. parts]);
    }

    // Names are decided in ordinal order of the ids: the first id of a name keeps it, the others get the lowest free suffix.
    static Dictionary<string, string> Assign(IEnumerable<(string Id, string Name)> items, IEnumerable<string> reserved)
    {
        var sorted = items.OrderBy(i => i.Id, StringComparer.Ordinal).ToArray();
        var taken = new HashSet<string>(reserved, StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, name) in sorted) if (taken.Add(name)) result[id] = name;
        foreach (var (id, name) in sorted)
            if (!result.ContainsKey(id))
                for (var n = 2; ; n++)
                    if (taken.Add(name + n.ToString(CultureInfo.InvariantCulture))) { result[id] = name + n.ToString(CultureInfo.InvariantCulture); break; }
        return result;
    }

    /// <summary><c>brightness_pct</c> → <c>BrightnessPct</c> or <c>brightnessPct</c>; never empty, never starting with a digit.</summary>
    internal static string Words(string id, bool pascal)
    {
        var sb = new StringBuilder();
        var boundary = true;
        foreach (var c in id)
        {
            // Formatting characters are allowed in identifiers but ignored when comparing them: treat them as separators.
            if (c == '_' || !SyntaxFacts.IsIdentifierPartCharacter(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format) { boundary = true; continue; }
            sb.Append(boundary ? (sb.Length == 0 && !pascal ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)) : c);
            boundary = false;
        }
        if (sb.Length == 0) return "_";
        if (!SyntaxFacts.IsIdentifierStartCharacter(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    // "await" cannot name a parameter of an async method unless it is escaped.
    internal static string Escape(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || name == "await" ? "@" + name : name;
    static string Unescaped(string name) => name.TrimStart('@');

    static string Literal(string text) => SymbolDisplay.FormatLiteral(text, true);
    static string? Text(JsonObject owner, string key) => owner[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static string? Number(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.ToJsonString() : null;
    static string Join(params string[] parts) => string.Join(' ', parts.Where(p => p.Length > 0));

    /// <summary>
    /// The one way schema text reaches a doc comment: on one line (every line break, other white space and control
    /// character becomes a space, runs of them one), without what XML cannot hold (a lone surrogate, U+FFFE, U+FFFF
    /// become U+FFFD), and XML-escaped.
    /// </summary>
    internal static string Xml(string? text)
    {
        var raw = text ?? "";
        var sb = new StringBuilder(raw.Length);
        var space = true;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (char.IsHighSurrogate(c) && i + 1 < raw.Length && char.IsLowSurrogate(raw[i + 1])) { sb.Append(c).Append(raw[++i]); space = false; continue; }
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { if (!space) sb.Append(' '); space = true; continue; }
            space = false;
            if (char.IsSurrogate(c) || c is '\uFFFE' or '\uFFFF') sb.Append('\uFFFD');
            else if (c == '&') sb.Append("&amp;");
            else if (c == '<') sb.Append("&lt;");
            else if (c == '>') sb.Append("&gt;");
            else sb.Append(c);
        }
        return sb.ToString().TrimEnd(' ');
    }
}
