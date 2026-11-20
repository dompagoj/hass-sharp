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
    bool? restoreState;
    string? name;
    readonly ConcurrentDictionary<string, object?> attributes = new();

    /// <summary>An entity whose key is <c>main</c>: such as a C# entity node's class, which the host gives its node's key (<see cref="Adopt"/>).</summary>
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
    public string Key { get; private set; }
    /// <summary>What Home Assistant calls the entity; null for the device's name alone.</summary>
    public string? Name { get => name; init => name = value; }

    /// <summary>The device that added the entity (<see cref="SharpDevice.Add{T}"/>); null for one no device added.</summary>
    internal SharpDevice? AddedTo { get; set; }

    /// <summary>
    /// For a C# entity node: the host gives the entity its node's key, in place of the one its code gave it (<c>main</c>
    /// unless it chose one), and <paramref name="defaultName"/> as its name when its code set none. A name the code sets wins.
    /// </summary>
    /// <param name="defaultName">The name of an entity whose code names none: its class's title when it stands alone, null
    /// on a device (Home Assistant then shows the device's name).</param>
    internal void Adopt(string key, string? defaultName)
    {
        Key = key;
        name ??= defaultName;
    }
    /// <summary>The entity's icon, such as <c>mdi:power-plug</c>; null for the default of its domain and device class.</summary>
    public string? Icon { get; init; }
    /// <summary>Home Assistant's device class, such as <c>power</c> or <c>motion</c>; null for none.</summary>
    public string? DeviceClass { get; init; }
    /// <summary>Whether the entity is a configuration or diagnostic one; null for a primary entity.</summary>
    public EntityCategory? Category { get; init; }
    /// <summary>Whether Home Assistant enables the entity when it first registers it; true unless set.</summary>
    public bool EnabledByDefault { get; init; } = true;

    /// <summary>
    /// Whether the entity comes back, after Home Assistant restarts or the definition is turned off and on, with the state
    /// it last showed while its definition ran: those values are set, without publishing, before its <c>Setup()</c> runs,
    /// which sees them and may change them, as <c>Start()</c> may. A value it no longer takes, such as an option no longer
    /// in its list, is left out. Its availability and attributes are not kept. True unless set for a switch, number,
    /// select, text, light and cover, whose state Home Assistant sets; false for a binary sensor, sensor and button.
    /// </summary>
    public bool RestoreState
    {
        get => restoreState ?? RestoresByDefault;
        init => restoreState = value;
    }

    /// <summary>Whether <see cref="RestoreState"/> is true unless set.</summary>
    internal virtual bool RestoresByDefault => false;

    /// <summary>
    /// Takes the state values the entity last showed, as stored (its <see cref="Values"/>), through its own setters
    /// before the definition runs, so nothing is published: each value it does not take now (of another kind, or one its
    /// setter refuses) is left out, and a value not stored, or stored as none, is left as it is (restoring never clears).
    /// </summary>
    /// <returns>Why each value left out was, as "brightness: Lamp (lamp) cannot be dimmed…"; none when every one was taken.</returns>
    internal virtual string[] Restore(JsonObject values) => [];

    /// <summary>For <see cref="Restore"/>: takes each stored value given through its setter, gathering the refusals.</summary>
    internal static string[] Restoring(JsonObject values, params (string Key, Action<JsonNode?> Keep)[] keys)
    {
        List<string> dropped = [];
        foreach (var (key, keep) in keys)
        {
            // A value stored as none restores nothing: the entity keeps what it has, such as one its constructor set.
            if (!values.TryGetPropertyValue(key, out var node) || node == null) continue;
            try { keep(node); }
            catch (ArgumentException refused) { dropped.Add($"{key}: {refused.Message}"); }
        }
        return [.. dropped];
    }

    /// <summary>A stored value as true, false or null.</summary>
    /// <exception cref="ArgumentException">It is something else.</exception>
    internal static bool? StoredFlag(JsonNode? node) =>
        node == null ? null : node is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? value.GetValue<bool>()
        : throw new ArgumentException($"{node.ToJsonString()} is neither true nor false");

    /// <summary>A stored value as a finite number or null.</summary>
    /// <exception cref="ArgumentException">It is something else.</exception>
    internal static double? StoredNumber(JsonNode? node) =>
        node == null ? null : node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<double>(out var number) && double.IsFinite(number) ? number
        : throw new ArgumentException($"{node.ToJsonString()} is not a number");

    /// <summary>A stored value as a whole number within an <c>int</c>, or null.</summary>
    /// <exception cref="ArgumentException">It is something else.</exception>
    internal static int? StoredWhole(JsonNode? node) =>
        StoredNumber(node) is not { } number ? null : number == Math.Floor(number) && number is >= int.MinValue and <= int.MaxValue ? (int)number
        : throw new ArgumentException($"{node!.ToJsonString()} is not a whole number");

    /// <summary>A stored value as a text or null.</summary>
    /// <exception cref="ArgumentException">It is something else.</exception>
    internal static string? StoredText(JsonNode? node) =>
        node == null ? null : node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>()
        : throw new ArgumentException($"{node.ToJsonString()} is not text");

    /// <summary>Whether the entity has a state to show; true unless set. Setting it publishes.</summary>
    public bool Available
    {
        get => available;
        set { available = value; Publish(); }
    }

    /// <summary>
    /// Extra attributes Home Assistant shows with the state: text, numbers, booleans, dates, JSON values, and lists or
    /// dictionaries of them, up to 32 levels deep (a list or dictionary deeper, or one inside itself, is sent as null). A
    /// change here is sent with the next <see cref="Publish"/>. Safe to change from any thread, also while another one
    /// publishes; a list or dictionary inside it is not, and one that changes while a publish reads it fails that publish.
    /// </summary>
    public IDictionary<string, object?> Attributes => attributes;

    /// <summary>
    /// Sends the current state and attributes to Home Assistant; does nothing before the definition runs. Never throws:
    /// a state that cannot be read, such as a value whose text throws, is logged and not sent.
    /// </summary>
    public void Publish() => Host?.Publish(this);

    /// <summary>The entity in a message: its name and key, such as <c>Power (power)</c>, or its key alone when it has no name.</summary>
    internal string Called => Name is { Length: > 0 } name ? $"{name} ({Key})" : Key;

    /// <summary>The entity as a camera's log lines name it: the name it has in Home Assistant, such as <c>Porch</c>, or its key alone when it has no name.</summary>
    internal string Titled => Name is { Length: > 0 } name ? name : Key;

    /// <summary>
    /// A value the entity does not take, from a command or a state its code sets, naming the entity, what it takes and
    /// the value: "Target (target) takes a number from 5 to 30, not 45".
    /// </summary>
    internal ArgumentException Refused(string takes, string given) => new($"{Called} takes {takes}, not {given}");

    /// <summary>The Home Assistant domain, such as <c>sensor</c>.</summary>
    internal abstract string Domain { get; }

    /// <summary>The commands Home Assistant can send the entity, such as <c>turn_on</c>; none unless a typed entity has them.</summary>
    internal virtual IReadOnlyList<string> Commands => [];

    /// <summary>Runs a command Home Assistant sent, with its service data.</summary>
    internal virtual Task Command(string name, JsonElement data) =>
        throw new NotSupportedException($"{Domain} entity '{Key}' has no command {name}.");

    /// <summary>
    /// Checks a command's data as <see cref="Command"/> does before its handler runs, without running it: for a definition
    /// built from nodes, whose Entity node's command flow is the handler. Data the command does not read is not checked.
    /// </summary>
    /// <exception cref="ArgumentException">The entity does not take the data; the message names the entity and the value.</exception>
    internal virtual void CheckData(string name, JsonElement data) { }

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
        var path = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var (name, value) in attributes.ToArray().OrderBy(attribute => attribute.Key, StringComparer.Ordinal)) json[name] = AttributeJson(value, 0, path);
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

    /// <summary>How deep lists and dictionaries in an attribute nest; one deeper is written as null.</summary>
    internal const int AttributeDepth = 32;

    // An attribute may also be a JSON value or a list or dictionary; written by hand rather than serialized, so that no
    // serializer cache holds a type from the definition's load context. A list or dictionary that contains itself, or
    // nests deeper than AttributeDepth, is null there: recursing on would overflow the stack and end Home Assistant.
    static JsonNode? AttributeJson(object? value, int depth, HashSet<object> path)
    {
        switch (value)
        {
            case JsonNode node: return node.DeepClone();
            case JsonElement element: return element.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(element.GetRawText());
            case IEnumerable items and not string:
                if (depth >= AttributeDepth || !path.Add(items)) return null;
                try
                {
                    if (items is IDictionary dictionary) return ObjectJson(dictionary, depth + 1, path);
                    return new JsonArray(items.Cast<object?>().Select(item => AttributeJson(item, depth + 1, path)).ToArray());
                }
                finally { path.Remove(items); }
            default: return ValueJson(value);
        }
    }

    static JsonObject ObjectJson(IDictionary dictionary, int depth, HashSet<object> path)
    {
        var json = new JsonObject();
        foreach (DictionaryEntry entry in dictionary) json[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? ""] = AttributeJson(entry.Value, depth, path);
        return json;
    }

    /// <summary>What an entity key must match.</summary>
    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,39}\z")]
    internal static partial Regex KeyPattern();
}
