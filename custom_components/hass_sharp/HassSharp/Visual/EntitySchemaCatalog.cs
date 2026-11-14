using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HassSharp;

/// <summary>
/// Home Assistant's entity_component translations normalised per domain: the states and state attributes (with the
/// values an attribute can take) that the typed entity handles are generated from. Immutable and built whole from
/// <c>{"domains": [...], "translations": {flat key: text}}</c>. The text is untrusted and stays JSON data here.
/// </summary>
public sealed class EntitySchemaCatalog
{
    static readonly Regex Slug = new(@"^[a-z0-9_]+\z", RegexOptions.CultureInvariant);
    const int MaxName = 100;
    const string Prefix = "component.";
    const string Component = ".entity_component.";

    /// <summary>Normalised domains keyed by domain in ordinal order; shared, never mutated.</summary>
    public JsonObject Items { get; }
    public int Count => Items.Count;
    public static EntitySchemaCatalog Empty { get; } = new(new JsonObject());

    EntitySchemaCatalog(JsonObject items) => Items = items;

    /// <summary>
    /// Normalises the input. Keys of another shape, malformed domains and over-long or empty names are dropped silently;
    /// only input that is not an object with a <c>domains</c> array and a <c>translations</c> object throws.
    /// </summary>
    public static EntitySchemaCatalog Parse(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root) throw new JsonException("The entity components must be a JSON object");
        if (root["domains"] is not JsonArray domainList) throw new JsonException("The entity components need a domains array");
        if (root["translations"] is not JsonObject translations) throw new JsonException("The entity components need a translations object");

        var domains = new SortedDictionary<string, Domain>(StringComparer.Ordinal);
        foreach (var node in domainList)
            if (node is JsonValue value && value.TryGetValue<string>(out var name) && Slug.IsMatch(name)) domains.TryAdd(name, new());
        foreach (var (key, text) in translations)
            if (text is JsonValue label && label.TryGetValue<string>(out var display) && Read(key) is { } entry)
            {
                if (!domains.TryGetValue(entry.Domain, out var domain)) domains[entry.Domain] = domain = new();
                domain.Add(entry, display);
            }

        var items = new JsonObject();
        foreach (var (name, domain) in domains) items[name] = domain.ToJson();
        return new(items);
    }

    enum Kind { State, AttributeName, AttributeValue }
    readonly record struct Entry(string Domain, string Class, Kind Kind, string Attribute, string Id);

    // component.<domain>.entity_component.<class>.state.<id>
    // component.<domain>.entity_component.<class>.state_attributes.<attr>.name
    // component.<domain>.entity_component.<class>.state_attributes.<attr>.state.<id>
    static Entry? Read(string key)
    {
        if (!key.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var split = key.IndexOf(Component, Prefix.Length - 1, StringComparison.Ordinal);
        if (split < 0) return null;
        var domain = key[Prefix.Length..split];
        if (!Slug.IsMatch(domain)) return null;
        var rest = key[(split + Component.Length)..];
        var dot = rest.IndexOf('.');
        if (dot <= 0) return null;
        var (cls, tail) = (rest[..dot], rest[(dot + 1)..]);
        if (tail.StartsWith("state.", StringComparison.Ordinal))
            return Valid(tail[6..]) ? new(domain, cls, Kind.State, "", tail[6..]) : null;
        if (!tail.StartsWith("state_attributes.", StringComparison.Ordinal)) return null;
        tail = tail[17..];
        dot = tail.IndexOf('.');
        if (dot <= 0) return null;
        var (attribute, after) = (tail[..dot], tail[(dot + 1)..]);
        if (!Valid(attribute)) return null;
        if (after == "name") return new(domain, cls, Kind.AttributeName, attribute, "");
        if (after.StartsWith("state.", StringComparison.Ordinal) && Valid(after[6..])) return new(domain, cls, Kind.AttributeValue, attribute, after[6..]);
        return null;
    }

    static bool Valid(string name) => name.Length is > 0 and <= MaxName;

    // A label comes from device class "_" when it has one, else from the first class in ordinal order.
    static int Rank(string cls) => cls == "_" ? 0 : 1;

    sealed class Domain
    {
        readonly List<(Entry Entry, string Label)> entries = [];
        public void Add(Entry entry, string label) => entries.Add((entry, label));

        public JsonObject ToJson()
        {
            var states = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var attributes = new SortedDictionary<string, (string? Label, SortedDictionary<string, string> Values)>(StringComparer.Ordinal);
            foreach (var (entry, label) in entries.OrderBy(e => Rank(e.Entry.Class)).ThenBy(e => e.Entry.Class, StringComparer.Ordinal))
            {
                if (entry.Kind == Kind.State) { states.TryAdd(entry.Id, label); continue; }
                if (!attributes.TryGetValue(entry.Attribute, out var attribute)) attributes[entry.Attribute] = attribute = (null, new(StringComparer.Ordinal));
                if (entry.Kind == Kind.AttributeName) attributes[entry.Attribute] = (attribute.Label ?? label, attribute.Values);
                else attribute.Values.TryAdd(entry.Id, label);
            }
            var attributeItems = new JsonObject();
            foreach (var (name, (label, values)) in attributes)
                attributeItems[name] = new JsonObject { ["label"] = label ?? name, ["values"] = Options(values) };
            return new JsonObject { ["states"] = Options(states), ["attributes"] = attributeItems };
        }

        static JsonArray Options(SortedDictionary<string, string> options)
        {
            var array = new JsonArray();
            foreach (var (id, label) in options) array.Add(new JsonObject { ["id"] = id, ["label"] = label });
            return array;
        }
    }
}
