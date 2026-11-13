using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HassSharp;

/// <summary>
/// Home Assistant service descriptions normalised once into the visual services section (contract C4).
/// Immutable: built whole from the raw <c>async_get_all_descriptions</c> JSON and replaced whole.
/// It never enters a catalog snapshot's root, generated source or the library generation.
/// </summary>
public sealed class ServiceSchemaCatalog
{
    static readonly Regex Slug = new("^[a-z0-9_]+$", RegexOptions.CultureInvariant);
    static readonly JsonSerializerOptions CanonicalText = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly string[] AnyType = ["object", "array", "string", "number", "boolean"];
    const string TimePattern = @"^([01]?\d|2[0-3]):[0-5]\d(:[0-5]\d)?$";
    const string DatePattern = @"^\d{4}-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])$";
    const string DateTimePattern = @"^\d{4}-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])[T ]([01]\d|2[0-3]):[0-5]\d(:[0-5]\d(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})?$";

    readonly JsonObject items;
    /// <summary>Normalised services keyed by <c>domain.service</c>; shared, never mutated.</summary>
    internal JsonObject Items => items;
    /// <summary>Compact JSON of <see cref="Items"/> with every object key in a fixed order.</summary>
    public string CanonicalJson { get; }
    /// <summary>SHA-256 (lowercase hex) of <see cref="CanonicalJson"/>; independent of input key order.</summary>
    public string Generation { get; }
    public int Count => items.Count;
    public static ServiceSchemaCatalog Empty { get; } = new(new JsonObject());

    /// <summary>The C4 services section as a defensive copy.</summary>
    public JsonObject Section => new() { ["servicesGeneration"] = Generation, ["items"] = items.DeepClone() };

    ServiceSchemaCatalog(JsonObject items)
    {
        this.items = items;
        CanonicalJson = items.ToJsonString(CanonicalText);
        Generation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson))).ToLowerInvariant();
    }

    /// <summary>
    /// Normalises <c>{domain: {service: description}}</c>. A malformed domain, service or field is skipped
    /// with one log line; only input that is not a JSON object at all throws.
    /// </summary>
    public static ServiceSchemaCatalog Parse(string json, Action<string>? log = null)
    {
        // Logging crosses into Python; a failing log line must not reject the rest of the input.
        log ??= message => { try { Logger.Warn(message); } catch { } };
        if (JsonNode.Parse(json) is not JsonObject domains) throw new ArgumentException("Service descriptions must be a JSON object keyed by domain");
        var services = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (domain, byService) in domains)
        {
            if (!Slug.IsMatch(domain) || byService is not JsonObject described) { log($"Skipping service domain {domain}: expected a slug mapped to an object of services"); continue; }
            foreach (var (service, description) in described)
            {
                var id = domain + "." + service;
                try
                {
                    if (!Slug.IsMatch(service)) throw new FormatException("the service name is not a slug");
                    services[id] = Service(domain, service, description, id, log);
                }
                catch (Exception ex) { log($"Skipping service {id}: {ex.Message}"); }
            }
        }
        var items = new JsonObject();
        foreach (var (id, service) in services) items[id] = service;
        return new(items);
    }

    static JsonObject Service(string domain, string service, JsonNode? node, string id, Action<string> log)
    {
        var description = node as JsonObject ?? throw new FormatException("the description is not an object");
        var raw = Object(description, "fields") ?? new JsonObject();
        // Top-level fields first, then sections by name: a key repeated across sections resolves
        // the same way whatever order the input lists them in. Sections exist only for display.
        var topLevel = new List<KeyValuePair<string, JsonNode?>>();
        var groups = new List<(string? Section, IEnumerable<KeyValuePair<string, JsonNode?>> Fields)> { (null, topLevel) };
        foreach (var (key, field) in raw)
            if (field is JsonObject section && section.ContainsKey("fields"))
            {
                if (section["fields"] is JsonObject fields) groups.Add((key, fields));
                else log($"Skipping section {key} of {id}: its fields are not an object");
            }
            else topLevel.Add(KeyValuePair.Create(key, field));
        var normalised = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (section, fields) in groups.OrderBy(g => g.Section, StringComparer.Ordinal))
        foreach (var (key, field) in fields)
        {
            if (normalised.ContainsKey(key)) { log($"Skipping field {key} of {id}: the name is already used by another section"); continue; }
            try { normalised[key] = Field(key, field, section); }
            catch (Exception ex) { log($"Skipping field {key} of {id}: {ex.Message}"); }
        }
        var result = new JsonObject
        {
            ["domain"] = domain, ["service"] = service,
            ["name"] = String(description, "name") ?? service, ["description"] = String(description, "description") ?? "",
            ["target"] = Target(description["target"]), ["response"] = Response(description["response"]),
            ["fields"] = new JsonObject(),
        };
        foreach (var (key, field) in normalised) result["fields"]![key] = field;
        return result;
    }

    // Contract C3: the selector decides the accepted types and the literal constraints.
    static JsonObject Field(string key, JsonNode? node, string? section)
    {
        var field = node as JsonObject ?? throw new FormatException("the field is not an object");
        var (selector, config) = Selector(field["selector"]);
        var spec = new JsonObject
        {
            ["label"] = String(field, "name") ?? key, ["description"] = String(field, "description") ?? "", ["types"] = null,
            ["required"] = Bool(field, "required") ?? false, ["advanced"] = Bool(field, "advanced") ?? false, ["section"] = section,
            ["min"] = null, ["max"] = null, ["integer"] = false, ["step"] = null, ["unit"] = null, ["enum"] = null, ["enumLabels"] = null,
            ["pattern"] = null, ["minItems"] = null, ["maxItems"] = null,
            ["default"] = Canonical(field["default"]), ["example"] = Canonical(field["example"]),
            ["selector"] = selector == null ? null : new JsonObject { [selector] = Canonical(config) ?? new JsonObject() },
            ["filter"] = FieldFilter(field["filter"]),
        };
        var multiple = Bool(config, "multiple") ?? false;
        string[] types;
        switch (selector)
        {
            case "boolean": types = ["boolean"]; break;
            case "number": NumberConstraints(spec, config); types = ["number"]; break;
            case "text" or "state": types = [multiple ? "array" : "string"]; break;
            case "select": SelectOptions(spec, config); types = [multiple ? "array" : "string"]; break;
            case "entity": types = multiple ? [EntityType(config), "array"] : [EntityType(config)]; break;
            case "device" or "area" or "floor" or "label": types = multiple ? ["string", "array"] : ["string"]; break;
            case "target": types = ["target"]; break;
            case "duration": types = ["number", "object"]; break;
            case "time": types = ["string"]; spec["pattern"] = TimePattern; break;
            case "date": types = ["string"]; spec["pattern"] = DatePattern; break;
            case "datetime": types = ["string"]; spec["pattern"] = DateTimePattern; break;
            case "color_rgb": types = ["array"]; spec["minItems"] = 3; spec["maxItems"] = 3; break;
            case "color_temp": types = ["number"]; break;
            case "constant":
                var value = config?["value"] ?? throw new FormatException("a constant selector needs a value");
                types = [TypeOf(value)];
                spec["enum"] = new JsonArray(Canonical(value));
                if (String(config, "label") is { } label) spec["enumLabels"] = new JsonArray(label);
                break;
            default: types = AnyType; break;
        }
        spec["types"] = new JsonArray(types.Select(t => (JsonNode?)t).ToArray());
        return spec;
    }

    static (string? Name, JsonObject? Config) Selector(JsonNode? node)
    {
        if (node == null) return (null, null);
        if (node is not JsonObject { Count: 1 } selector) throw new FormatException("the selector must be an object with exactly one selector type");
        var (name, config) = selector.First();
        return (name, config switch { null => null, JsonObject options => options, _ => throw new FormatException($"the {name} selector options are not an object") });
    }

    // HA's normalised selectors always state a step; integer literals are demanded only when one is
    // stated, whole, and the mode does not ask for floats. An omitted step still reports HA's default of 1.
    static void NumberConstraints(JsonObject spec, JsonObject? config)
    {
        var stated = config?["step"];
        double? step = stated is JsonValue any && any.GetValueKind() == JsonValueKind.String
            ? any.GetValue<string>() == "any" ? null : throw new FormatException("the number step must be a number or \"any\"")
            : Number(config, "step") ?? 1;
        spec["min"] = Number(config, "min"); spec["max"] = Number(config, "max");
        spec["step"] = step; spec["unit"] = String(config, "unit_of_measurement");
        spec["integer"] = stated != null && step is { } whole && whole > 0 && whole == Math.Floor(whole) && String(config, "mode") != "float";
    }

    static void SelectOptions(JsonObject spec, JsonObject? config)
    {
        if (config?["options"] is not JsonArray options) throw new FormatException("a select selector needs an options array");
        var values = new JsonArray(); var labels = new JsonArray(); var labelled = false;
        foreach (var option in options)
            if (option is JsonValue text && text.GetValueKind() == JsonValueKind.String) { values.Add(text.GetValue<string>()); labels.Add(text.GetValue<string>()); }
            else if (option is JsonObject pair && String(pair, "value") is { } value) { values.Add(value); labels.Add(String(pair, "label") ?? value); labelled = true; }
            else throw new FormatException("a select option must be a string or an object with a string value");
        if (Bool(config, "custom_value") == true) return;
        spec["enum"] = values;
        spec["enumLabels"] = labelled ? labels : null;
    }

    // A single permitted domain narrows to the parametric entity type (contract C1).
    static string EntityType(JsonObject? config)
    {
        var domains = new SortedSet<string>(StringComparer.Ordinal);
        bool unrestricted;
        if (Strings(config, "domain") is { } top) { domains.UnionWith(top); unrestricted = top.Count == 0; }
        else
        {
            var filters = Filters(config?["filter"], "entity filter");
            unrestricted = filters.Count == 0;
            foreach (var filter in filters)
                if (Strings(filter, "domain") is { Count: > 0 } allowed) domains.UnionWith(allowed);
                else unrestricted = true;
        }
        return !unrestricted && domains.Count == 1 && Slug.IsMatch(domains.Min!) ? "entity_id:" + domains.Min : "entity_id";
    }

    static string TypeOf(JsonNode value) => value.GetValueKind() switch
    {
        JsonValueKind.String => "string", JsonValueKind.Number => "number", JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Array => "array", JsonValueKind.Object => "object", _ => throw new FormatException("a constant value cannot be null"),
    };

    // Target filters arrive as one object or a list of them (D13).
    static JsonObject? Target(JsonNode? node)
    {
        if (node == null) return null;
        var target = node as JsonObject ?? throw new FormatException("the target is not an object");
        return new JsonObject
        {
            ["entity"] = new JsonArray(Filters(target["entity"], "target entity filter").Select(f => (JsonNode?)new JsonObject
            {
                ["domain"] = Array(Strings(f, "domain")), ["deviceClass"] = Array(Strings(f, "device_class")),
                ["supportedFeatures"] = Features(f["supported_features"]),
            }).ToArray()),
            ["device"] = new JsonArray(Filters(target["device"], "target device filter").Select(f => (JsonNode?)new JsonObject
            {
                ["integration"] = String(f, "integration"), ["manufacturer"] = String(f, "manufacturer"),
                ["model"] = String(f, "model"), ["modelId"] = String(f, "model_id"),
            }).ToArray()),
        };
    }

    static JsonObject? FieldFilter(JsonNode? node)
    {
        if (node == null) return null;
        var filter = node as JsonObject ?? throw new FormatException("the field filter is not an object");
        var result = new JsonObject();
        if (filter.ContainsKey("supported_features")) result["supportedFeatures"] = Features(filter["supported_features"]);
        if (filter.ContainsKey("attribute"))
        {
            var attributes = filter["attribute"] as JsonObject ?? throw new FormatException("the attribute filter is not an object");
            var normalised = new JsonObject();
            foreach (var (name, values) in attributes.OrderBy(p => p.Key, StringComparer.Ordinal))
                normalised[name] = values is JsonArray list ? (JsonArray)Canonical(list)! : new JsonArray(Canonical(values ?? throw new FormatException($"the {name} attribute filter is null")));
            result["attribute"] = normalised;
        }
        return result;
    }

    static string Response(JsonNode? node) => node switch
    {
        null => "none",
        JsonObject response => Bool(response, "optional") switch
        {
            true => "optional", false => "only", null => throw new FormatException("the response does not say whether it is optional"),
        },
        _ => throw new FormatException("the response is not an object"),
    };

    static List<JsonObject> Filters(JsonNode? node, string what) => node switch
    {
        null => [],
        JsonObject one => [one],
        JsonArray many => many.Select(f => f as JsonObject ?? throw new FormatException($"a {what} is not an object")).ToList(),
        _ => throw new FormatException($"the {what} is neither an object nor a list"),
    };

    static JsonArray Features(JsonNode? node)
    {
        // Typed arms: a JsonArray arm would make [node] a new JsonArray that re-parents the input node.
        IEnumerable<JsonNode?> features = node switch { null => System.Array.Empty<JsonNode?>(), JsonArray list => list, _ => new[] { node } };
        return new JsonArray(features.Select(f => f is JsonValue v && v.GetValueKind() is JsonValueKind.Number or JsonValueKind.String
            ? Canonical(v) : throw new FormatException("a supported feature must be a number or a feature name")).ToArray());
    }

    static JsonArray Array(IReadOnlyList<string>? values) => new(values?.Select(v => (JsonNode?)v).ToArray() ?? []);

    static IReadOnlyList<string>? Strings(JsonObject? owner, string key) => owner?[key] switch
    {
        null => null,
        JsonValue one when one.GetValueKind() == JsonValueKind.String => [one.GetValue<string>()],
        JsonArray many => many.Select(v => v is JsonValue s && s.GetValueKind() == JsonValueKind.String ? s.GetValue<string>() : throw Malformed(key, "a string list")).ToList(),
        _ => throw Malformed(key, "a string or a string list"),
    };

    static string? String(JsonObject? owner, string key) => owner?[key] switch
    {
        null => null, JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(), _ => throw Malformed(key, "a string"),
    };

    static bool? Bool(JsonObject? owner, string key) => owner?[key] switch
    {
        null => null, JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => v.GetValue<bool>(), _ => throw Malformed(key, "a boolean"),
    };

    static double? Number(JsonObject? owner, string key) => owner?[key] switch
    {
        null => null, JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.GetValue<double>(), _ => throw Malformed(key, "a number"),
    };

    static JsonObject? Object(JsonObject owner, string key) => owner[key] switch
    {
        null => null, JsonObject o => o, _ => throw Malformed(key, "an object"),
    };

    static FormatException Malformed(string key, string expected) => new($"{key} is not {expected}");

    // Raw values (selector options, defaults, examples) with sorted object keys and one spelling per number,
    // so the generation does not depend on how the producer ordered keys or wrote 1 versus 1.0.
    static JsonNode? Canonical(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Canonical(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Canonical).ToArray()),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.TryGetValue<long>(out var whole) ? JsonValue.Create(whole) : JsonValue.Create(v.GetValue<double>()),
        _ => node.DeepClone(),
    };
}
