using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>A binary sensor: something that is on or off, such as motion or an open door, that Home Assistant only reads.</summary>
public class SharpBinarySensor : SharpEntity
{
    bool? isOn;

    /// <summary>A binary sensor whose key is <c>main</c>.</summary>
    public SharpBinarySensor() { }
    /// <summary>A binary sensor with its own key, unique within its device.</summary>
    /// <param name="key">What identifies it within the device, such as <c>motion</c>.</param>
    /// <param name="name">What Home Assistant calls it; null for the device's name alone.</param>
    public SharpBinarySensor(string key, string? name = null) : base(key, name) { }

    /// <summary>Whether it is on; null while unknown. Setting it publishes.</summary>
    public bool? IsOn
    {
        get => isOn;
        set { isOn = value; Publish(); }
    }

    internal override string Domain => "binary_sensor";
    internal override JsonObject Values() => new() { ["is_on"] = IsOn };
}

/// <summary>A sensor: a reading such as a temperature, a power draw or a status text, that Home Assistant only reads.</summary>
public class SharpSensor : SharpEntity
{
    object? value;

    /// <summary>A sensor whose key is <c>main</c>.</summary>
    public SharpSensor() { }
    /// <summary>A sensor with its own key, unique within its device.</summary>
    /// <param name="key">What identifies it within the device, such as <c>power</c>.</param>
    /// <param name="name">What Home Assistant calls it; null for the device's name alone.</param>
    public SharpSensor(string key, string? name = null) : base(key, name) { }

    /// <summary>
    /// The reading; null while unknown. Numbers stay numbers, dates become ISO 8601 text, NaN and infinities count as
    /// unknown, and any other object is shown as its text. Setting it publishes.
    /// </summary>
    public object? Value
    {
        get => value;
        set { this.value = value; Publish(); }
    }

    /// <summary>The unit of the reading, such as <c>W</c> or <c>°C</c>; null for none.</summary>
    public string? Unit { get; init; }
    /// <summary>How Home Assistant keeps statistics of it: <c>measurement</c>, <c>total</c> or <c>total_increasing</c>; null for none.</summary>
    public string? StateClass { get; init; }
    /// <summary>The values an <c>enum</c> sensor can have; null for any other sensor.</summary>
    public IReadOnlyList<string>? Options { get; init; }

    internal override string Domain => "sensor";
    internal override JsonObject Values() => new() { ["value"] = ValueJson(Value) };
    internal override JsonObject DomainOptions() => new()
    {
        ["unit"] = Unit,
        ["stateClass"] = StateClass,
        ["options"] = Options == null ? null : new JsonArray(Options.Select(option => (JsonNode?)option).ToArray()),
    };
}

/// <summary>
/// A switch: something Home Assistant turns on and off. Handle the commands by overriding <see cref="TurnOn"/> and
/// <see cref="TurnOff"/>, or, for a switch inside a device, by setting <see cref="OnTurnOn"/> and <see cref="OnTurnOff"/>.
/// </summary>
public class SharpSwitch : SharpEntity
{
    static readonly IReadOnlyList<string> SwitchCommands = ["turn_on", "turn_off"];
    bool? isOn;

    /// <summary>A switch whose key is <c>main</c>.</summary>
    public SharpSwitch() { }
    /// <summary>A switch with its own key, unique within its device.</summary>
    /// <param name="key">What identifies it within the device, such as <c>relay</c>.</param>
    /// <param name="name">What Home Assistant calls it; null for the device's name alone.</param>
    public SharpSwitch(string key, string? name = null) : base(key, name) { }

    /// <summary>Whether it is on; null while unknown. Setting it publishes.</summary>
    public bool? IsOn
    {
        get => isOn;
        set { isOn = value; Publish(); }
    }

    /// <summary>What turning it on does, when <see cref="TurnOn"/> is not overridden.</summary>
    public Func<Task>? OnTurnOn { get; init; }
    /// <summary>What turning it off does, when <see cref="TurnOff"/> is not overridden.</summary>
    public Func<Task>? OnTurnOff { get; init; }

    /// <summary>Turns the switch on when Home Assistant asks; runs <see cref="OnTurnOn"/> unless overridden.</summary>
    protected virtual Task TurnOn() => OnTurnOn?.Invoke() ?? throw new NotSupportedException($"Switch '{Key}' cannot be turned on.");
    /// <summary>Turns the switch off when Home Assistant asks; runs <see cref="OnTurnOff"/> unless overridden.</summary>
    protected virtual Task TurnOff() => OnTurnOff?.Invoke() ?? throw new NotSupportedException($"Switch '{Key}' cannot be turned off.");

    internal override string Domain => "switch";
    internal override IReadOnlyList<string> Commands => SwitchCommands;
    internal override Task Command(string name, JsonElement data) => name switch
    {
        "turn_on" => TurnOn(),
        "turn_off" => TurnOff(),
        _ => base.Command(name, data),
    };
    internal override JsonObject Values() => new() { ["is_on"] = IsOn };
}

/// <summary>The Home Assistant domain of a custom entity type.</summary>
internal static class SharpDomains
{
    // The typed entity bases and their domains, read both by runtime type and by the type's name.
    static readonly (Type Type, string Domain)[] Typed =
    [
        (typeof(SharpBinarySensor), "binary_sensor"),
        (typeof(SharpSensor), "sensor"),
        (typeof(SharpSwitch), "switch"),
    ];

    /// <summary>Each typed entity base and its domain, as the catalog's <c>entityDomains</c> describes them.</summary>
    internal static IReadOnlyList<(Type Type, string Domain)> All => Typed;

    /// <summary>The domain of the nearest typed entity <paramref name="type"/> derives from, such as <c>switch</c>; null for any other type.</summary>
    public static string? Of(Type type)
    {
        for (var current = type; current != null; current = current.BaseType)
            foreach (var (typed, domain) in Typed)
                if (current == typed) return domain;
        return null;
    }

    /// <summary>
    /// The domain of the typed entity base with this full name, such as <c>HassSharp.SharpSwitch</c>; null for any other
    /// name. For a type known only by name, as the library compiler knows a base class.
    /// </summary>
    public static string? OfName(string typeName)
    {
        foreach (var (typed, domain) in Typed)
            if (typed.FullName == typeName) return domain;
        return null;
    }

    /// <summary>
    /// The typed entity a graph definition's document describes: its <c>key</c>, and its <c>name</c>, <c>icon</c>,
    /// <c>deviceClass</c>, <c>entityCategory</c>, <c>enabledByDefault</c> and <c>options</c> (a sensor's <c>unit</c>,
    /// <c>stateClass</c> and <c>options</c>), each absent or null for none. The document was checked when it was saved
    /// (<see cref="DefinitionDocument.Check"/>); a field of another type is read as absent.
    /// </summary>
    /// <exception cref="InvalidOperationException">The domain is not one a definition built from nodes creates.</exception>
    /// <exception cref="ArgumentException">The key is not an entity key.</exception>
    internal static SharpEntity Create(string domain, JsonObject description)
    {
        var described = new EntityDescription(description);
        return Graph.TryGetValue(domain, out var known) ? known.Create(described)
            : throw new InvalidOperationException($"Entity {described.Key} has the domain {domain}, which a definition built from nodes cannot create");
    }

    /// <summary>
    /// The domains a definition built from nodes creates: how to create each one's entity from the document's
    /// description, and, for each of its state keys, which values it takes and how it keeps one. A domain added later is
    /// one entry here, beside its <c>entityDomains</c> entry in the catalog (which gives its command labels).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, GraphDomain> Graph = new Dictionary<string, GraphDomain>(StringComparer.Ordinal)
    {
        ["binary_sensor"] = new(
            d => new SharpBinarySensor(d.Key, d.Name) { Icon = d.Icon, DeviceClass = d.DeviceClass, Category = d.Category, EnabledByDefault = d.EnabledByDefault },
            new Dictionary<string, GraphState>(StringComparer.Ordinal) { ["is_on"] = OnOff((entity, on) => ((SharpBinarySensor)entity).IsOn = on) }),
        ["sensor"] = new(
            d => new SharpSensor(d.Key, d.Name)
            {
                Icon = d.Icon, DeviceClass = d.DeviceClass, Category = d.Category, EnabledByDefault = d.EnabledByDefault,
                Unit = EntityDescription.Text(d.Options?["unit"]), StateClass = EntityDescription.Text(d.Options?["stateClass"]),
                Options = d.Options?["options"] is JsonArray values ? values.Select(EntityDescription.Text).OfType<string>().ToArray() : null,
            },
            new Dictionary<string, GraphState>(StringComparer.Ordinal) { ["value"] = new(SensorRefuses, (entity, value) => ((SharpSensor)entity).Value = value) }),
        ["switch"] = new(
            d => new SharpSwitch(d.Key, d.Name) { Icon = d.Icon, DeviceClass = d.DeviceClass, Category = d.Category, EnabledByDefault = d.EnabledByDefault },
            new Dictionary<string, GraphState>(StringComparer.Ordinal) { ["is_on"] = OnOff((entity, on) => ((SharpSwitch)entity).IsOn = on) }),
    };

    // A state that is on, off or unknown.
    static GraphState OnOff(Action<SharpEntity, bool?> keep) =>
        new((_, value) => value is null or bool ? null : "is neither true nor false", (entity, value) => keep(entity, (bool?)value));

    // A sensor's reading as Home Assistant takes it. With options: one of them as text (an enum sensor), or unknown.
    // With a unit or a state class: a number, or text that reads as a finite number in the invariant culture, kept as
    // given; other text Home Assistant refuses as it writes the state. Otherwise: any number or text. Unknown is always
    // taken, and a number that is not finite shows as unknown.
    static string? SensorRefuses(SharpEntity entity, object? value)
    {
        var sensor = (SharpSensor)entity;
        if (value is null) return null;
        if (value is not (double or string)) return "is neither a number nor text";
        if (sensor.Options is { } options)
            return value is string option && options.Contains(option, StringComparer.Ordinal) ? null : $"is not one of its options: {string.Join(", ", options)}";
        if (value is string text && (sensor.Unit != null || sensor.StateClass != null)
            && !(double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)))
            return $"is text, and a sensor with {(sensor.Unit != null ? "a unit" : "a state class")} needs a number";
        return null;
    }
}

/// <summary>The fields every entity of a graph definition's document has, read as <see cref="SharpDomains.Create"/> reads them.</summary>
internal sealed class EntityDescription(JsonObject description)
{
    internal static string? Text(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
    public string Key { get; } = Text(description["key"]) ?? "";
    public string? Name { get; } = Text(description["name"]);
    public string? Icon { get; } = Text(description["icon"]);
    public string? DeviceClass { get; } = Text(description["deviceClass"]);
    public EntityCategory? Category { get; } = Text(description["entityCategory"]) switch { "config" => EntityCategory.Config, "diagnostic" => EntityCategory.Diagnostic, _ => null };
    public bool EnabledByDefault { get; } = description["enabledByDefault"] is not JsonValue flag || flag.GetValueKind() != JsonValueKind.False;
    /// <summary>The domain's own options, such as a sensor's unit; null for none.</summary>
    public JsonObject? Options { get; } = description["options"] as JsonObject;
}

/// <summary>A domain a definition built from nodes creates: its entity from a description, and its state keys.</summary>
internal sealed record GraphDomain(Func<EntityDescription, SharpEntity> Create, IReadOnlyDictionary<string, GraphState> State);

/// <summary>
/// One state key of a <see cref="GraphDomain"/>: <paramref name="Refuses"/> says why the entity does not take a value
/// (worded to follow "The value 5 of Power (power)", such as "is neither true nor false"), or null when it does;
/// <paramref name="Keep"/> sets a value it takes on the entity.
/// </summary>
internal sealed record GraphState(Func<SharpEntity, object?, string?> Refuses, Action<SharpEntity, object?> Keep);

