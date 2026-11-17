using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace HassSharp;

/// <summary>What a definition previews to: whether it can be saved, its problems, the source it generates and its class.</summary>
/// <param name="Valid">True when no diagnostic is an error.</param>
/// <param name="Diagnostics">Its problems, each with a <c>definition.</c> code and, for a setting, the setting's key as its input.</param>
/// <param name="Source">The generated source; empty when the definition is not valid.</param>
/// <param name="Type">The class it creates; null when the type is not one the libraries declare.</param>
public sealed record DefinitionPreview(bool Valid, List<VisualDiagnostic> Diagnostics, string Source, EntityTypeDescriptor? Type)
{
    /// <summary>
    /// The sections of <see cref="Source"/>: for a class, one <c>$library:&lt;id&gt;</c> range per library; for a graph, the
    /// generator's (its nodes' and libraries'). Empty when it is not valid.
    /// </summary>
    public List<VisualSourceMap> SourceMap { get; init; } = [];
    /// <summary>For a graph, the graph as previewed, with its definition composed (<see cref="DefinitionGraph.Compose"/>); null for a class, or a document with problems.</summary>
    [JsonIgnore] public JsonObject? Graph { get; init; }
    /// <summary>For a valid graph, the pruned catalog its source embeds (the run snapshot's catalog); null otherwise.</summary>
    [JsonIgnore] public JsonObject? EmbeddedCatalog { get; init; }
}

/// <summary>
/// Checks an entity definition and generates its source. For a class: against the classes the node libraries declare,
/// a <c>SharpDefinition</c> class that creates it, with the libraries after it, as an automation embeds them. For a
/// graph: its document (entities and settings) first, then its graph with that definition composed into it, generated
/// as an automation's graph is.
/// </summary>
/// <param name="diagnostics">What a graph's Custom C# nodes compile with; a shared one of its own when null.</param>
/// <param name="serviceCalls">The typed service calls one request read once, for a graph's compile; the current ones when null.</param>
public sealed class DefinitionGenerator(DiagnosticsProvider? diagnostics = null, GeneratedServiceCalls? serviceCalls = null)
{
    // Only a caller without one, such as a test, previews a graph with this.
    static readonly Lazy<DiagnosticsProvider> Fallback = new(() => new DiagnosticsProvider());
    /// <summary>The form an entity key takes: a part of its unique id and of its entity id.</summary>
    static readonly System.Text.RegularExpressions.Regex EntityKey = new(@"^[a-z][a-z0-9_]{0,39}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Every problem of the definition, in a fixed order; a definition whose class is unknown, or whose libraries have
    /// errors, gets no setting problems. A setting it does not mention keeps the class default, as a null value does.
    /// </summary>
    public DefinitionPreview Preview(DefinitionBody body, VisualCatalogSnapshot snapshot)
    {
        var errors = new List<VisualDiagnostic>();
        void Error(string code, string message, string? input = null) => errors.Add(new(null, message, 3, InputId: input, Code: code));

        if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 120) Error("definition.name", "Give the definition a name of up to 120 characters");
        var kind = body.Logic?.Kind;
        if (kind is not ("class" or "graph")) Error("definition.logic", "Logic is a graph or a class");
        // An empty array is the same as none: a client may always send the field.
        if (kind == "class" && body.Entities is { Count: > 0 }) Error("definition.entitiesNotAllowed", "A class definition's entities come from its code");
        if (kind == "graph") Document(body, snapshot.Root, Error);

        EntityTypeDescriptor? type = null;
        if (snapshot.LibraryDiagnostics.Any(d => d.Severity == 3)) Error("definition.library", "The node libraries have errors; fix them first");
        else if (kind == "class")
        {
            var name = body.Logic!.Type;
            if (string.IsNullOrWhiteSpace(name)) Error("definition.unknownType", "No entity class was given: choose the class the definition creates");
            else if (!snapshot.EntityTypes.TryGetValue(name, out type)) Error("definition.unknownType", $"No entity class {name} in the node libraries");
            else Settings(type, body.Settings ?? [], Error);
        }

        // The document's problems come first: its graph is not checked against a definition that would only repeat them.
        if (errors.Count > 0) return new(false, errors, "", type);
        return kind == "graph" ? GenerateGraph(body, snapshot) : Generate(type!, snapshot);
    }

    /// <summary>
    /// The graph with the document's definition composed into it, previewed as an automation's graph is: its problems
    /// and warnings each on its node, and when valid its source, source map and embedded catalog.
    /// </summary>
    DefinitionPreview GenerateGraph(DefinitionBody body, VisualCatalogSnapshot snapshot)
    {
        var graph = DefinitionGraph.Compose(body);
        var preview = new VisualGenerator(diagnostics ?? Fallback.Value, serviceCalls).Preview(graph, snapshot);
        return new(preview.Valid, preview.Diagnostics, preview.Valid ? preview.Source : "", null)
        {
            SourceMap = preview.Valid ? preview.SourceMap : [],
            Graph = preview.Graph ?? graph,
            EmbeddedCatalog = preview.Valid && preview.EmbeddedCatalog != null ? JsonNode.Parse(preview.EmbeddedCatalog)!.AsObject() : null,
        };
    }

    /// <summary>
    /// The problems of a graph definition's document, before its graph is looked at: it has a graph, at least one entity,
    /// each with a key of its own of the form <see cref="EntityKey"/>, a domain the catalog's <c>entityDomains</c>
    /// describes and only options that domain takes, each of its type; and each setting has a key of its own and a type a
    /// graph reads, and a value of that type or none. A document without these problems composes to a definition the
    /// validator reads (it never answers <c>definition.invalid</c>).
    /// </summary>
    static void Document(DefinitionBody body, JsonObject catalog, Action<string, string, string?> error)
    {
        if (body.Logic!.Graph == null) error("definition.graph", "Give the definition a graph: its logic is a graph of nodes, and none was given", null);
        var domains = catalog["entityDomains"] as JsonObject ?? [];
        var entities = body.Entities ?? [];
        if (entities.Count == 0) error("definition.entitiesRequired", "Add an entity: a definition built from nodes creates the entities it lists, and lists none", null);
        var keys = new HashSet<string>(StringComparer.Ordinal); var repeated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (item, index) in entities.Select((e, i) => (e, i + 1)))
        {
            if (item is not JsonObject entity) { error("definition.entityKey", $"Entity {index} is not an entity: give each entity a key and a domain", null); continue; }
            if (!IsText(entity["key"], out var key))
            {
                error("definition.entityKey", $"Give entity {index} a key, such as {(index == 1 ? "power" : "power_" + index)}", null);
                continue;
            }
            if (!EntityKey.IsMatch(key)) error("definition.entityKey", $"Entity key {key} is not a key: start it with a lowercase letter and use only lowercase letters, digits and underscores, up to 40 characters", key);
            if (!keys.Add(key) && repeated.Add(key)) error("definition.duplicateEntity", $"Two entities have the key {key}; give each a key of its own", key);
            if (!IsText(entity["domain"], out var domain)) { error("definition.entityDomain", $"Choose a domain for entity {key}: {Or(domains.Select(d => d.Key))}", key); continue; }
            if (domains[domain] is not JsonObject spec) { error("definition.entityDomain", $"Entity {key} has the domain {domain}, which a definition built from nodes cannot create; choose {Or(domains.Select(d => d.Key))}", key); continue; }
            Options(key, spec, entity["options"], error);
        }
        var settings = body.Settings ?? [];
        var settingKeys = new HashSet<string>(StringComparer.Ordinal); var repeatedSettings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (setting, index) in settings.Select((s, i) => (s, i + 1)))
        {
            if (setting == null || string.IsNullOrWhiteSpace(setting.Key)) { error("definition.settingKey", $"Give setting {index} a key, such as threshold", setting?.Key); continue; }
            var key = setting.Key; var name = string.IsNullOrWhiteSpace(setting.Label) ? key : setting.Label;
            if (setting.Type is not ({ } type and ("string" or "boolean" or "number")))
                error("definition.settingType", setting.Type == null
                    ? $"Give setting {key} a type: string (a text), number or boolean (true or false)"
                    : $"Setting {key} has the type {setting.Type}, which a graph cannot read; choose string (a text), number or boolean (true or false)", key);
            else if (!FitsType(type, setting.Value)) error("definition.settingType", $"{name} takes {TypePhrase(type)}", key);
            if (!settingKeys.Add(key) && repeatedSettings.Add(key)) error("definition.duplicateSetting", $"Two settings have the key {key}; give each a key of its own", key);
        }
    }

    /// <summary>The problems of an entity's options: each must be one its domain takes, of that option's type and, with an enum, one of its values.</summary>
    static void Options(string key, JsonObject domain, JsonNode? given, Action<string, string, string?> error)
    {
        if (given == null || given.GetValueKind() == JsonValueKind.Null) return;
        var known = domain["options"] as JsonObject ?? [];
        var label = VisualNodeCatalog.Text(domain, "label", key);
        if (given is not JsonObject options) { error("definition.entityOption", $"The options of entity {key} are not a set of named values; give them as an object, such as {{\"unit\": \"W\"}}", key); return; }
        foreach (var (option, value) in options)
        {
            if (known[option] is not JsonObject rule)
            {
                error("definition.entityOption", known.Count == 0
                    ? $"Entity {key} has the option {option}, but {Article(label)} {label.ToLowerInvariant()} takes no options; remove it"
                    : $"Entity {key} has no option {option}; {Article(label)} {label.ToLowerInvariant()} takes {Or(known.Select(o => o.Key))}", key);
                continue;
            }
            if (value == null || value.GetValueKind() == JsonValueKind.Null) continue;
            var type = VisualNodeCatalog.Text(rule, "type");
            var allowed = (rule["enum"] as JsonArray)?.Select(v => v is JsonValue text && text.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToArray();
            bool Allowed(JsonNode? item) => allowed == null || IsText(item, out var text) && allowed.Contains(text, StringComparer.Ordinal);
            var fits = type == "array"
                ? value is JsonArray items && items.All(i => IsText(i, out _) && Allowed(i))
                : FitsType(type, value) && Allowed(value);
            if (!fits)
                error("definition.entityOption", $"Option {option} of entity {key} takes {(allowed != null && type != "array" ? "one of " + string.Join(", ", allowed) : type == "array" ? "a list of texts" : TypePhrase(type))}", key);
        }
    }

    static bool IsText(JsonNode? node, out string text)
    {
        text = "";
        return node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.TryGetValue(out text!);
    }

    // A null value fits every type: a graph setting without one gives its graph an empty value, an option without one is not set.
    static bool FitsType(string type, JsonNode? value)
    {
        if (value == null || value.GetValueKind() == JsonValueKind.Null) return true;
        var kind = value.GetValueKind();
        return type switch
        {
            "string" => kind == JsonValueKind.String,
            "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
            "number" => kind == JsonValueKind.Number && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number),
            "integer" => kind == JsonValueKind.Number && long.TryParse(value.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _),
            "object" => kind == JsonValueKind.Object,
            _ => false,
        };
    }

    static string TypePhrase(string type) => type switch
    {
        "string" => "a text",
        "boolean" => "true or false",
        "number" => "a number",
        "integer" => "a whole number",
        _ => type,
    };

    static string Or(IEnumerable<string> items) => VisualGraphValidator.Words.Or(items);
    static string Article(string word) => VisualGraphValidator.Words.Article(word);

    static void Settings(EntityTypeDescriptor type, List<DefinitionSetting> given, Action<string, string, string?> error)
    {
        var settings = given.Select(s => (Key: s?.Key ?? "", s?.Value)).ToArray();
        var known = type.Settings.ToDictionary(s => s.Key, StringComparer.Ordinal);
        foreach (var key in settings.Select(s => s.Key).Distinct(StringComparer.Ordinal).Where(k => !known.ContainsKey(k)))
            error("definition.unknownSetting", $"{type.Title} has no setting {key}", key);
        foreach (var (key, value) in settings)
            if (known.TryGetValue(key, out var setting) && !Fits(setting, value))
                error("definition.settingType", $"{setting.Label} takes {Phrase(setting)}", key);
        foreach (var key in settings.GroupBy(s => s.Key, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key))
            error("definition.duplicateSetting", $"{key} is set twice", key);
    }

    // JSON null leaves the class default, so it fits every setting. A whole number is one a long holds, written without a
    // fraction, within the setting's range when it has one (an int's).
    static bool Fits(EntitySettingDescriptor setting, JsonNode? value)
    {
        if (value == null || value.GetValueKind() == JsonValueKind.Null) return true;
        var kind = value.GetValueKind();
        return setting.Type switch
        {
            "string" => kind == JsonValueKind.String,
            "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
            "integer" => kind == JsonValueKind.Number && long.TryParse(value.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole)
                && whole >= (setting.Minimum ?? long.MinValue) && whole <= (setting.Maximum ?? long.MaxValue),
            "number" => kind == JsonValueKind.Number && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number),
            "enum" => kind == JsonValueKind.String && setting.Options.Contains(value.GetValue<string>(), StringComparer.Ordinal),
            _ => false,
        };
    }

    static string Phrase(EntitySettingDescriptor setting) => setting.Type switch
    {
        "string" => "a text",
        "boolean" => "true or false",
        "integer" when setting.Minimum != null || setting.Maximum != null =>
            $"a whole number from {(setting.Minimum ?? long.MinValue).ToString(CultureInfo.InvariantCulture)} to {(setting.Maximum ?? long.MaxValue).ToString(CultureInfo.InvariantCulture)}",
        "integer" => "a whole number",
        "number" => "a number",
        "enum" => "one of " + string.Join(", ", setting.Options),
        _ => setting.Type,
    };

    // The library sections are written exactly as VisualGenerator writes an automation's.
    static DefinitionPreview Generate(EntityTypeDescriptor type, VisualCatalogSnapshot snapshot)
    {
        var lines = new List<string>(); var maps = new List<VisualSourceMap>();
        void Add(string source) => lines.AddRange(source.Replace("\r\n", "\n").Split('\n'));
        Add("// HassSharp entity definition");
        Add($"public static class SharpDefinition {{ public static object Create() => new {type.TypeExpression}(); }}");
        foreach (var library in snapshot.Sources)
        {
            Add($"// NodeLibrarySource {JsonSerializer.Serialize(library.Id)} revision {library.Revision.ToString(CultureInfo.InvariantCulture)}");
            var start = lines.Count + 1;
            Add(NodeLibraryCompiler.WrapSource(library.Source));
            maps.Add(new("$library:" + library.Id, start, lines.Count));
        }
        return new(true, [], string.Join('\n', lines) + "\n", type) { SourceMap = maps };
    }
}
