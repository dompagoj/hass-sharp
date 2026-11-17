using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HassSharp;

/// <summary>Where Home Assistant files an entity that is not the device's main purpose.</summary>
public enum EntityCategory
{
    /// <summary>A setting of the device, such as its brightness at power-on.</summary>
    Config,
    /// <summary>Something that tells how the device is doing, such as its signal strength.</summary>
    Diagnostic,
}

/// <summary>
/// A custom Home Assistant entity. Derive from one of the typed entities, such as <see cref="SharpSensor"/> or
/// <see cref="SharpSwitch"/>, or add them to a <see cref="SharpDevice"/>. Setting a state property publishes the new
/// state while the definition runs; before then, nothing is published.
/// </summary>
public abstract partial class SharpEntity : SharpComponent
{
    bool available = true;
    readonly ConcurrentDictionary<string, object?> attributes = new();

    /// <summary>An entity whose key is <c>main</c>: the one entity of a definition without a device.</summary>
    protected SharpEntity() : this("main") { }

    /// <summary>An entity with its own key, unique within its device.</summary>
    /// <param name="key">What identifies it within the device: a lower-case letter, then up to 39 lower-case letters,
    /// digits or underscores, such as <c>relay</c> or <c>power_1</c>. It is part of the entity's unique id, so keep it
    /// once the entity is in use.</param>
    /// <param name="name">What Home Assistant calls it; null for the device's name alone.</param>
    protected SharpEntity(string key, string? name = null)
    {
        if (key == null || !KeyPattern().IsMatch(key))
            throw new ArgumentException($"Entity key '{key}' must be a lower-case letter followed by up to 39 lower-case letters, digits or underscores.", nameof(key));
        Key = key;
        Name = name;
    }

    /// <summary>What identifies the entity within its device; part of its unique id.</summary>
    public string Key { get; }
    /// <summary>What Home Assistant calls the entity; null for the device's name alone.</summary>
    public string? Name { get; init; }
    /// <summary>The entity's icon, such as <c>mdi:power-plug</c>; null for the default of its domain and device class.</summary>
    public string? Icon { get; init; }
    /// <summary>Home Assistant's device class, such as <c>power</c> or <c>motion</c>; null for none.</summary>
    public string? DeviceClass { get; init; }
    /// <summary>Whether the entity is a configuration or diagnostic one; null for a primary entity.</summary>
    public EntityCategory? Category { get; init; }
    /// <summary>Whether Home Assistant enables the entity when it first registers it; true unless set.</summary>
    public bool EnabledByDefault { get; init; } = true;

    /// <summary>Whether the entity has a state to show; true unless set. Setting it publishes.</summary>
    public bool Available
    {
        get => available;
        set { available = value; Publish(); }
    }

    /// <summary>
    /// Extra attributes Home Assistant shows with the state: text, numbers, booleans, dates, JSON values, and lists or
    /// dictionaries of them. A change here is sent with the next <see cref="Publish"/>. Safe to change from any thread,
    /// also while another one publishes.
    /// </summary>
    public IDictionary<string, object?> Attributes => attributes;

    /// <summary>Sends the current state and attributes to Home Assistant; does nothing before the definition runs.</summary>
    public void Publish() => Host?.Publish(this);

    /// <summary>The Home Assistant domain, such as <c>sensor</c>.</summary>
    internal abstract string Domain { get; }

    /// <summary>The commands Home Assistant can send the entity, such as <c>turn_on</c>; none unless a typed entity has them.</summary>
    internal virtual IReadOnlyList<string> Commands => [];

    /// <summary>Runs a command Home Assistant sent, with its service data.</summary>
    internal virtual Task Command(string name, JsonElement data) =>
        throw new NotSupportedException($"{Domain} entity '{Key}' has no command {name}.");

    /// <summary>The domain's state fields, such as <c>is_on</c>.</summary>
    internal virtual JsonObject Values() => new();

    /// <summary>
    /// The domain's description fields, such as a sensor's unit. Not named <c>Options</c> as the other members are:
    /// a sensor has a public property of that name.
    /// </summary>
    internal virtual JsonObject DomainOptions() => new();

    /// <summary>What Home Assistant needs to register the entity.</summary>
    internal JsonObject Describe(string uniqueId) => new()
    {
        ["uniqueId"] = uniqueId,
        ["key"] = Key,
        ["domain"] = Domain,
        ["name"] = Name,
        ["icon"] = Icon,
        ["deviceClass"] = DeviceClass,
        ["entityCategory"] = Category switch { EntityCategory.Config => "config", EntityCategory.Diagnostic => "diagnostic", _ => null },
        ["enabledByDefault"] = EnabledByDefault,
        ["commands"] = new JsonArray(Commands.Select(command => (JsonNode?)command).ToArray()),
        ["options"] = DomainOptions(),
    };

    /// <summary>The entity's current state as Home Assistant shows it; unavailable whenever the definition is not running.</summary>
    internal JsonObject Snapshot(string uniqueId, bool running)
    {
        // The dictionary's own ToArray is one consistent copy while other threads write (a LINQ copy through ICollection
        // is not); it has no order, so sorted by key the same attributes always give the same snapshot.
        var json = new JsonObject();
        foreach (var (name, value) in attributes.ToArray().OrderBy(attribute => attribute.Key, StringComparer.Ordinal)) json[name] = AttributeJson(value);
        return new() { ["uniqueId"] = uniqueId, ["available"] = Available && running, ["values"] = Values(), ["attributes"] = json };
    }

    /// <summary>
    /// A state value as JSON: text, booleans and numbers stay as they are, dates become ISO 8601 text, NaN and
    /// infinities become null, and any other object its text.
    /// </summary>
    internal static JsonNode? ValueJson(object? value) => value switch
    {
        null => null,
        string text => text,
        bool flag => flag,
        double number => double.IsFinite(number) ? number : null,
        float number => float.IsFinite(number) ? number : null,
        decimal number => number,
        int number => number,
        long number => number,
        short number => number,
        byte number => number,
        sbyte number => number,
        uint number => number,
        ulong number => number,
        ushort number => number,
        DateTime time => time.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset time => time.ToString("O", CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    // An attribute may also be a JSON value or a list or dictionary; written by hand rather than serialized, so that no
    // serializer cache holds a type from the definition's load context.
    static JsonNode? AttributeJson(object? value) => value switch
    {
        JsonNode node => node.DeepClone(),
        JsonElement element => element.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(element.GetRawText()),
        IDictionary dictionary => ObjectJson(dictionary),
        IEnumerable items and not string => new JsonArray(items.Cast<object?>().Select(AttributeJson).ToArray()),
        _ => ValueJson(value),
    };

    static JsonObject ObjectJson(IDictionary dictionary)
    {
        var json = new JsonObject();
        foreach (DictionaryEntry entry in dictionary) json[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? ""] = AttributeJson(entry.Value);
        return json;
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,39}\z")]
    private static partial Regex KeyPattern();
}
