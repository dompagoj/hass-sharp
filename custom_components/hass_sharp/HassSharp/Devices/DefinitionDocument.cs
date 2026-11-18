using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HassSharp;

/// <summary>
/// The checks of a graph definition's document, before its graph is looked at: its entities and settings, as
/// <see cref="DefinitionGraph.Compose"/> puts them into the graph. A document that passes them composes to a definition
/// the validator reads (it never answers <c>definition.invalid</c>).
/// </summary>
internal static class DefinitionDocument
{
    /// <summary>The entity description fields given as a text, each absent or null for none.</summary>
    static readonly string[] TextFields = ["name", "icon", "deviceClass"];
    /// <summary>The categories an entity can have, as <see cref="SharpEntity"/> describes them to Home Assistant.</summary>
    static readonly string[] Categories = ["config", "diagnostic"];

    /// <summary>
    /// Every problem of the document, in a fixed order: it has a graph and at least one entity; each entity has a key of
    /// its own of the form entity keys take, its description fields of their types, a domain the catalog's
    /// <c>entityDomains</c> describes and only options that domain takes, each of its type, and every option it needs
    /// (<see cref="Options"/>); each setting has a key of its own and a type a graph reads, and a value of that type or
    /// none. An entity member none of these name is kept as given.
    /// </summary>
    /// <param name="error">Called with each problem's code, message and the key of the entity or setting it is about.</param>
    internal static void Check(DefinitionBody body, JsonObject catalog, Action<string, string, string?> error)
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
            if (!SharpEntity.KeyPattern().IsMatch(key)) error("definition.entityKey", $"Entity key {key} is not a key: start it with a lowercase letter and use only lowercase letters, digits and underscores, up to 40 characters", key);
            if (!keys.Add(key) && repeated.Add(key)) error("definition.duplicateEntity", $"Two entities have the key {key}; give each a key of its own", key);
            Fields(key, entity, error);
            if (!IsText(entity["domain"], out var domain)) { error("definition.entityDomain", $"Choose a domain for entity {key}: {Or(domains.Select(d => d.Key))}", key); continue; }
            if (domains[domain] is not JsonObject spec) { error("definition.entityDomain", $"Entity {key} has the domain {domain}, which a definition built from nodes cannot create; choose {Or(domains.Select(d => d.Key))}", key); continue; }
            Options(key, domain, spec, entity["options"], error);
        }
        var settings = body.Settings ?? [];
        var settingKeys = new HashSet<string>(StringComparer.Ordinal); var repeatedSettings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (setting, index) in settings.Select((s, i) => (s, i + 1)))
        {
            if (setting == null || string.IsNullOrWhiteSpace(setting.Key)) { error("definition.settingKey", $"Give setting {index} a key, such as threshold", null); continue; }
            var key = setting.Key; var name = string.IsNullOrWhiteSpace(setting.Label) ? key : setting.Label;
            if (setting.Type is not ({ } type and ("string" or "boolean" or "number")))
                error("definition.settingType", setting.Type == null
                    ? $"Give setting {key} a type: string (a text), number or boolean (true or false)"
                    : $"Setting {key} has the type {setting.Type}, which a graph cannot read; choose string (a text), number or boolean (true or false)", key);
            else if (!Fits(type, setting.Value)) error("definition.settingType", $"{name} takes {Phrase(type)}", key);
            if (!settingKeys.Add(key) && repeatedSettings.Add(key)) error("definition.duplicateSetting", $"Two settings have the key {key}; give each a key of its own", key);
        }
    }

    /// <summary>
    /// The problems of an entity's description fields, each absent or null for none: a name, icon and device class are
    /// texts, the category is one Home Assistant has, and whether it is enabled by default is true or false (absent: it is).
    /// </summary>
    static void Fields(string key, JsonObject entity, Action<string, string, string?> error)
    {
        foreach (var field in TextFields)
            if (!Fits("string", entity[field])) error("definition.entityField", $"The {field} of entity {key} is not a text; give it as a text, or leave it out", key);
        if (entity["entityCategory"] is { } category && category.GetValueKind() != JsonValueKind.Null && !(IsText(category, out var text) && Categories.Contains(text, StringComparer.Ordinal)))
            error("definition.entityField", $"The entityCategory of entity {key} is not one Home Assistant has; choose {Or(Categories)}, or leave it out", key);
        if (entity.TryGetPropertyValue("enabledByDefault", out var enabled) && enabled?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
            error("definition.entityField", $"The enabledByDefault of entity {key} is not true or false; give true or false, or leave it out", key);
    }

    /// <summary>
    /// The problems of an entity's options: each must be one its domain takes, of that option's type (a list of texts
    /// for an array, which is not empty) and, with an enum, one of its values; each option the domain marks required is
    /// given; a <c>min</c> is below the <c>max</c>, either given or its class's own; and a <c>pattern</c> is a regular
    /// expression.
    /// </summary>
    static void Options(string key, string name, JsonObject domain, JsonNode? given, Action<string, string, string?> error)
    {
        var known = domain["options"] as JsonObject ?? [];
        var label = VisualNodeCatalog.Text(domain, "label", key);
        var options = given as JsonObject ?? [];
        if (given != null && given.GetValueKind() != JsonValueKind.Null && given is not JsonObject)
        { error("definition.entityOption", $"The options of entity {key} are not a set of named values; give them as an object, such as {{\"unit\": \"W\"}}", key); return; }
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
            var allowed = (rule["enum"] as JsonArray)?.Select(v => IsText(v, out var s) ? s : null).OfType<string>().ToArray();
            bool Allowed(JsonNode? item) => allowed == null || IsText(item, out var text) && allowed.Contains(text, StringComparer.Ordinal);
            // An option of type array is a list of texts.
            var fits = type == "array"
                ? value is JsonArray items && items.All(i => IsText(i, out _) && Allowed(i))
                : Fits(type, value) && Allowed(value);
            if (!fits)
                error("definition.entityOption", $"Option {option} of entity {key} takes {(type == "array" ? "a list of texts" : allowed != null ? "one of " + string.Join(", ", allowed) : Phrase(type))}", key);
            else if (value is JsonArray { Count: 0 })
                error("definition.entityOption", $"Option {option} of entity {key} is an empty list; give it at least one text{(Required(rule) ? "" : ", or leave it out")}", key);
            else if (option == "pattern" && type == "string" && IsText(value, out var pattern))
                try { _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1)); }
                catch (ArgumentException problem) { error("definition.entityOption", $"Option pattern of entity {key} is not a regular expression: {problem.Message}", key); }
        }
        foreach (var (option, rule) in known)
            if (rule is JsonObject required && Required(required) && (options[option] is not { } value || value.GetValueKind() == JsonValueKind.Null))
                error("definition.entityOption", $"Give entity {key} the option {option}: {Article(label)} {label.ToLowerInvariant()} needs it", key);
        if (known.ContainsKey("min") && known.ContainsKey("max") && (options.ContainsKey("min") || options.ContainsKey("max")))
        {
            var defaults = SharpDomains.DefaultOptions(name);
            if ((Number(options["min"]) ?? Number(defaults["min"])) is { } min && (Number(options["max"]) ?? Number(defaults["max"])) is { } max && !(min < max))
                error("definition.entityOption", $"Entity {key} has a min of {Shown(min)}, which is not below its max of {Shown(max)}; give a min below the max", key);
        }
    }

    static bool Required(JsonObject rule) => rule["required"] is JsonValue flag && flag.GetValueKind() == JsonValueKind.True;
    // Read through its JSON text, as Fits reads it: a class's default may be held as an int.
    static double? Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
    static string Shown(double number) => number.ToString(CultureInfo.InvariantCulture);

    static bool IsText(JsonNode? node, out string text)
    {
        text = "";
        return node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.TryGetValue(out text!);
    }

    /// <summary>
    /// Whether a value is a text, true or false, or a finite number, as <paramref name="type"/> (<c>string</c>,
    /// <c>boolean</c>, <c>number</c>) says; null always fits (no value), and any other type never does. A class
    /// definition's settings of these types are checked by it too.
    /// </summary>
    internal static bool Fits(string type, JsonNode? value)
    {
        if (value == null || value.GetValueKind() == JsonValueKind.Null) return true;
        var kind = value.GetValueKind();
        return type switch
        {
            "string" => kind == JsonValueKind.String,
            "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
            "number" => kind == JsonValueKind.Number && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number),
            _ => false,
        };
    }

    /// <summary>What a value of the type is, in a message: "a text", "true or false", "a number"; any other type by its name.</summary>
    internal static string Phrase(string type) => type switch
    {
        "string" => "a text",
        "boolean" => "true or false",
        "number" => "a number",
        _ => type,
    };

    static string Or(IEnumerable<string> items) => VisualGraphValidator.Words.Or(items);
    static string Article(string word) => VisualGraphValidator.Words.Article(word);
}
