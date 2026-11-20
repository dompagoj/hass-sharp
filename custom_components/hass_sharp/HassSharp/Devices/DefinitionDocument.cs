using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HassSharp;

/// <summary>
/// One problem of a definition. <c>code</c> and <c>message</c> say what it is. <c>input</c> is what it is about: a
/// setting's key, <c>option:&lt;option&gt;</c> for an option of an Entity node, or null for the definition as a whole.
/// <c>port</c> is used only inside the option check, which passes the entity's key as <c>input</c> and the option as
/// <c>port</c>; <see cref="DefinitionDocument.Check"/> turns that into <c>input</c> and leaves <c>port</c> null.
/// <c>node</c> is the Entity node the problem is on, if any.
/// </summary>
internal delegate void DefinitionProblem(string code, string message, string? input, string? port = null, string? node = null);

/// <summary>
/// The checks of a graph definition's document, before its graph is looked at: its graph, the options of its Entity
/// nodes and its settings, as <see cref="DefinitionGraph.Compose"/> puts the settings into the graph. A document that
/// passes them composes to a definition the validator reads (it never answers <c>definition.invalid</c>).
/// </summary>
internal static class DefinitionDocument
{
    /// <summary>
    /// Every problem of the document, in a fixed order: it has a graph with at least one Entity node; each Entity node of
    /// a type the catalog's <c>entityDomains</c> describes has only options that type takes, each of its type, and every
    /// option it needs (<see cref="Options"/>), each problem on the node, about <c>option:&lt;option&gt;</c> (the rest of the
    /// node, its key, type, name and fields, is the validator's, which checks it on the node too); each setting has a key
    /// of its own and a type a graph reads, and a value of that type or none.
    /// </summary>
    /// <param name="error">Called with each problem's code, message, the setting or option it is about and, for an option, its Entity node.</param>
    internal static void Check(DefinitionBody body, JsonObject catalog, DefinitionProblem error)
    {
        if (body.Logic!.Graph == null) error("definition.graph", "Give the definition a graph: its logic is a graph of nodes, and none was given", null);
        var domains = catalog["entityDomains"] as JsonObject ?? [];
        var nodes = body.Logic!.Graph is { } graph ? VisualNodeCatalog.DeclaredEntities(graph, catalog).ToList() : [];
        // A document with no graph is told to give one, and no more.
        if (body.Logic.Graph != null && nodes.Count == 0) error("definition.entitiesRequired", "Add an Entity node: an entity definition creates the entities on its canvas, and has none", null);
        // Options that are not an object are the node's Options field's problem, said by the validator.
        foreach (var (nodeId, entity) in nodes)
            if (IsText(entity["domain"], out var domain) && domains[domain] is JsonObject spec && (entity["options"] is JsonObject || !entity.ContainsKey("options")))
                Options(VisualNodeCatalog.Text(entity, "key"), entity, domain, spec, (code, message, _, option, _) => error(code, message, option == null ? null : "option:" + option, null, nodeId));
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
    /// The problems of an entity's options, given as an object or not at all: each must be one its domain takes, of that
    /// option's type (a list of texts for an array, which is not empty) and, with an enum, one of its values; each option
    /// the domain marks required is given; a number option is one Home Assistant takes (<see cref="Limit"/>) and within its
    /// rule's range when it has one (<see cref="Range"/>); a <c>min</c> is below the <c>max</c>, either given or its
    /// class's own; and a <c>pattern</c> is a regular expression. Each problem is about the entity's key and the option's
    /// (a min not below the max is about the min), and words the option by its label and the entity by its name, such as
    /// <c>Minimum of Target takes a number</c>. The panel words the ones it finds itself the same (<c>optionProblems</c> in
    /// <c>frontend/src/entities/definitionEdits.ts</c>).
    /// </summary>
    /// <param name="key">The entity's key.</param>
    /// <param name="entity">The entity an Entity node declares, whose name the messages use when it has one.</param>
    /// <param name="domainId">The entity's domain, such as <c>number</c>.</param>
    /// <param name="spec">What the catalog's <c>entityDomains</c> says of that domain.</param>
    static void Options(string key, JsonObject entity, string domainId, JsonObject spec, DefinitionProblem error)
    {
        var given = entity["options"];
        var known = spec["options"] as JsonObject ?? [];
        var label = VisualNodeCatalog.Text(spec, "label", key);
        // The entity as Words.EntityName names it: its name, else its key, each trimmed, else "Entity"; a sentence that
        // starts with it says "Entity" and its key when it has no name.
        var trimmedKey = VisualGraphValidator.Words.Trimmed(key);
        var hasName = IsText(entity["name"], out var name) && VisualGraphValidator.Words.Trimmed(name!).Length > 0;
        var named = hasName ? VisualGraphValidator.Words.Trimmed(name!) : trimmedKey.Length > 0 ? trimmedKey : "Entity";
        var subject = hasName ? named : trimmedKey.Length > 0 ? "Entity " + trimmedKey : "Entity";
        // An option as people read it: its label, else its key in words.
        string Option(string option) => known[option] is JsonObject rule && VisualNodeCatalog.Text(rule, "label") is { Length: > 0 } shown ? shown : VisualGraphValidator.Words.KeyWords(option);
        static string Lower(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
        void Problem(string option, string message) => error("definition.entityOption", message, key, option);
        var options = given as JsonObject ?? [];
        var bounded = false;
        foreach (var (option, value) in options)
        {
            if (known[option] is not JsonObject rule)
            {
                Problem(option, known.Count == 0
                    ? $"{(trimmedKey.Length > 0 ? "Entity " + trimmedKey : "Entity")} has the option {option}, but {Article(label)} {label.ToLowerInvariant()} takes no options; remove it"
                    : $"{(trimmedKey.Length > 0 ? "Entity " + trimmedKey : "Entity")} has no option {option}; {Article(label)} {label.ToLowerInvariant()} takes {Or(known.Select(o => o.Key))}");
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
            var of = $"{Option(option)} of {named}";
            if (!fits)
                Problem(option, $"{of} takes {(type == "array" ? "a list of texts" : allowed != null ? "one of " + string.Join(", ", allowed) : Phrase(type))}");
            else if (value is JsonArray { Count: 0 })
                Problem(option, $"{of} is an empty list; give it at least one text{(Required(rule) ? "" : ", or leave it out")}");
            else if (Number(value) is { } number && (Limit(domainId, option, number) ?? Range(rule, number)) is { } limit)
            {
                Problem(option, $"{of} takes {limit}, not {Shown(number)}");
                // A min or max refused here is not compared with the other one too.
                bounded |= option is "min" or "max";
            }
            else if (option == "pattern" && type == "string" && IsText(value, out var pattern))
                try { _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1)); }
                catch (ArgumentException problem) { Problem(option, $"{of} is not a regular expression: {problem.Message}"); }
        }
        foreach (var (option, rule) in known)
            if (rule is JsonObject required && Required(required) && (options[option] is not { } value || value.GetValueKind() == JsonValueKind.Null))
                // A sentence starts with the entity: its name, else "Entity" and its key, which may start lower-case.
                Problem(option, $"{subject} needs {Lower(Option(option))}: {Article(label)} {label.ToLowerInvariant()} cannot work without it");
        if (!bounded && known.ContainsKey("min") && known.ContainsKey("max") && (options.ContainsKey("min") || options.ContainsKey("max")))
        {
            var defaults = SharpDomains.DefaultOptions(domainId);
            if ((Number(options["min"]) ?? Number(defaults["min"])) is { } min && (Number(options["max"]) ?? Number(defaults["max"])) is { } max && !(min < max))
                Problem("min", $"{Option("min")} of {named} is {Shown(min)}, which is not below its {Lower(Option("max"))} of {Shown(max)}");
        }
    }

    /// <summary>
    /// What Home Assistant takes of a domain's number option beyond its type, when <paramref name="number"/> is not that;
    /// null when it is: a number's step above 0, and a text's lengths whole numbers from 0 to 255 (the most characters
    /// Home Assistant keeps in a state), as <see cref="SharpNumber.Step"/>, <see cref="SharpText.Min"/> and
    /// <see cref="SharpText.Max"/> take them.
    /// </summary>
    static string? Limit(string domain, string option, double number) => (domain, option) switch
    {
        ("number", "step") when !(number > 0) => "a number above 0",
        ("text", "min" or "max") when number != Math.Floor(number) => "a whole number",
        ("text", "min") when number < 0 => "0 or more",
        ("text", "max") when number > 255 => "255 or less, as Home Assistant keeps at most 255 characters",
        _ => null,
    };

    /// <summary>
    /// What a number option whose rule gives a <c>min</c> and a <c>max</c> takes, such as a camera's seconds between frames,
    /// when <paramref name="number"/> is outside them; null when it is within (or the rule gives no range).
    /// </summary>
    static string? Range(JsonObject rule, double number) =>
        Number(rule["min"]) is { } min && Number(rule["max"]) is { } max && (number < min || number > max) ? $"a number from {Shown(min)} to {Shown(max)}" : null;

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
