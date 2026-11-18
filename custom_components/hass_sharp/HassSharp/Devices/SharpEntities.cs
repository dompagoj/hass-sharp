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
    internal override string[] Restore(JsonObject values) => Restoring(values, ("is_on", node => IsOn = StoredFlag(node)));
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
    internal override string[] Restore(JsonObject values) => Restoring(values, ("value", node => Value = node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? StoredText(node) : StoredNumber(node)));
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
    internal override bool RestoresByDefault => true;
    internal override IReadOnlyList<string> Commands => SwitchCommands;
    internal override Task Command(string name, JsonElement data) => name switch
    {
        "turn_on" => TurnOn(),
        "turn_off" => TurnOff(),
        _ => base.Command(name, data),
    };
    internal override JsonObject Values() => new() { ["is_on"] = IsOn };
    internal override string[] Restore(JsonObject values) => Restoring(values, ("is_on", node => IsOn = StoredFlag(node)));
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
        (typeof(SharpButton), "button"),
        (typeof(SharpNumber), "number"),
        (typeof(SharpSelect), "select"),
        (typeof(SharpText), "text"),
        (typeof(SharpLight), "light"),
        (typeof(SharpCover), "cover"),
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

    /// <summary>The options of a domain's entity left as its class has them, such as a number's <c>min</c> of 0; none for a domain not typed here.</summary>
    internal static JsonObject DefaultOptions(string domain) =>
        Typed.FirstOrDefault(typed => typed.Domain == domain).Type is { } type ? ((SharpEntity)Activator.CreateInstance(type)!).DomainOptions() : [];

    /// <summary>
    /// The typed entity a graph definition's document describes: its <c>key</c>, and its <c>name</c>, <c>icon</c>,
    /// <c>deviceClass</c>, <c>entityCategory</c>, <c>enabledByDefault</c> and <c>options</c> (those its domain's
    /// <c>entityDomains</c> entry lists, such as a sensor's <c>unit</c>), each absent or null for none (the class's
    /// default). The document was checked when it was saved (<see cref="DefinitionDocument.Check"/>); a field of another
    /// type is read as absent.
    /// </summary>
    /// <exception cref="InvalidOperationException">The domain is not one a definition built from nodes creates.</exception>
    /// <exception cref="ArgumentException">The key is not an entity key, or a text's pattern is not a regular expression.</exception>
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
            new Dictionary<string, GraphState>(StringComparer.Ordinal) { ["is_on"] = OnOff(entity => ((SharpBinarySensor)entity).IsOn, (entity, on) => ((SharpBinarySensor)entity).IsOn = on) }),
        ["sensor"] = new(
            d => new SharpSensor(d.Key, d.Name)
            {
                Icon = d.Icon, DeviceClass = d.DeviceClass, Category = d.Category, EnabledByDefault = d.EnabledByDefault,
                Unit = d.Text("unit"), StateClass = d.Text("stateClass"), Options = d.Texts("options"),
            },
            new Dictionary<string, GraphState>(StringComparer.Ordinal) { ["value"] = new(SensorRefuses, (entity, value) => ((SharpSensor)entity).Value = value, entity => ((SharpSensor)entity).Value) }),
        ["switch"] = new(
            d => new SharpSwitch(d.Key, d.Name) { Icon = d.Icon, DeviceClass = d.DeviceClass, Category = d.Category, EnabledByDefault = d.EnabledByDefault },
            new Dictionary<string, GraphState>(StringComparer.Ordinal) { ["is_on"] = OnOff(entity => ((SharpSwitch)entity).IsOn, (entity, on) => ((SharpSwitch)entity).IsOn = on) }),
        ["button"] = new(
            d => new SharpButton(d.Key, d.Name) { Icon = d.Icon, DeviceClass = d.DeviceClass, Category = d.Category, EnabledByDefault = d.EnabledByDefault },
            new Dictionary<string, GraphState>(StringComparer.Ordinal)),
        ["number"] = new(
            d => new SharpNumber(d.Key, d.Name)
            {
                Icon = d.Icon, DeviceClass = d.DeviceClass, Category = d.Category, EnabledByDefault = d.EnabledByDefault,
                Min = d.Number("min") ?? 0, Max = d.Number("max") ?? 100, Step = d.Number("step") ?? 1, Unit = d.Text("unit"),
                Mode = d.Text("mode") switch { "box" => SharpNumberMode.Box, "slider" => SharpNumberMode.Slider, _ => SharpNumberMode.Auto },
            },
            new Dictionary<string, GraphState>(StringComparer.Ordinal) { ["value"] = new(NumberRefuses, (entity, value) => ((SharpNumber)entity).Value = (double?)value, entity => ((SharpNumber)entity).Value) }),
        ["select"] = new(
            d => new SharpSelect(d.Key, d.Name) { Icon = d.Icon, DeviceClass = d.DeviceClass, Category = d.Category, EnabledByDefault = d.EnabledByDefault, Options = d.Texts("options") ?? [] },
            new Dictionary<string, GraphState>(StringComparer.Ordinal) { ["option"] = new(SelectRefuses, (entity, value) => ((SharpSelect)entity).Option = (string?)value, entity => ((SharpSelect)entity).Option) }),
        ["text"] = new(
            d => new SharpText(d.Key, d.Name)
            {
                Icon = d.Icon, DeviceClass = d.DeviceClass, Category = d.Category, EnabledByDefault = d.EnabledByDefault,
                Min = d.Whole("min") ?? 0, Max = d.Whole("max") ?? 100, Pattern = d.Text("pattern"), Mode = d.Text("mode") == "password" ? SharpTextMode.Password : SharpTextMode.Text,
            },
            new Dictionary<string, GraphState>(StringComparer.Ordinal) { ["value"] = new(TextRefuses, (entity, value) => ((SharpText)entity).Value = (string?)value, entity => ((SharpText)entity).Value) }),
        // A brightness and a position are whole numbers on the class, and plain numbers (double) in a graph.
        ["light"] = new(
            d => new SharpLight(d.Key, d.Name) { Icon = d.Icon, DeviceClass = d.DeviceClass, Category = d.Category, EnabledByDefault = d.EnabledByDefault, Dimmable = d.Flag("dimmable") ?? false },
            new Dictionary<string, GraphState>(StringComparer.Ordinal)
            {
                ["is_on"] = OnOff(entity => ((SharpLight)entity).IsOn, (entity, on) => ((SharpLight)entity).IsOn = on),
                ["brightness"] = new((entity, value) => WholeRefuses(value, ((SharpLight)entity).Refuses), (entity, value) => ((SharpLight)entity).Brightness = (int?)(double?)value, entity => (double?)((SharpLight)entity).Brightness),
            }),
        ["cover"] = new(
            d => new SharpCover(d.Key, d.Name) { Icon = d.Icon, DeviceClass = d.DeviceClass, Category = d.Category, EnabledByDefault = d.EnabledByDefault, Positionable = d.Flag("positionable") ?? false },
            new Dictionary<string, GraphState>(StringComparer.Ordinal)
            {
                ["is_closed"] = OnOff(entity => ((SharpCover)entity).IsClosed, (entity, closed) => ((SharpCover)entity).IsClosed = closed),
                ["position"] = new((entity, value) => WholeRefuses(value, ((SharpCover)entity).Refuses), (entity, value) => ((SharpCover)entity).Position = (int?)(double?)value, entity => (double?)((SharpCover)entity).Position),
            }),
    };

    // A state that is on, off or unknown.
    static GraphState OnOff(Func<SharpEntity, bool?> read, Action<SharpEntity, bool?> keep) =>
        new((_, value) => value is null or bool ? null : "is neither true nor false", (entity, value) => keep(entity, (bool?)value), entity => read(entity));

    // A sensor's reading as Home Assistant takes it. With options: one of them as text (an enum sensor), or unknown.
    // With a unit, a state class or a device class other than date, timestamp or enum: a number, or text that reads as
    // a finite number in the invariant culture, kept as given; other text Home Assistant refuses as it writes the
    // state. Otherwise: any number or text. Unknown is always taken, and a number that is not finite shows as unknown.
    static string? SensorRefuses(SharpEntity entity, object? value)
    {
        var sensor = (SharpSensor)entity;
        if (value is null) return null;
        if (value is not (double or string)) return "is neither a number nor text";
        if (sensor.Options is { } options)
            return value is string option && options.Contains(option, StringComparer.Ordinal) ? null : $"is not one of its options: {string.Join(", ", options)}";
        var numeric = sensor.Unit != null ? "a unit" : sensor.StateClass != null ? "a state class"
            : sensor.DeviceClass is { } deviceClass and not ("date" or "timestamp" or "enum") ? $"the device class {deviceClass}" : null;
        if (value is string text && numeric != null
            && !(double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)))
            return $"is text, and a sensor with {numeric} needs a number";
        return null;
    }

    // A number within the entity's range, or unknown.
    static string? NumberRefuses(SharpEntity entity, object? value)
    {
        var number = (SharpNumber)entity;
        if (value is null) return null;
        if (value is not double given) return "is not a number";
        return number.Within(given) ? null : $"is not {number.Range}";
    }

    // One of the select's options, or unknown.
    static string? SelectRefuses(SharpEntity entity, object? value)
    {
        var select = (SharpSelect)entity;
        if (value is null) return null;
        if (value is not string option) return "is not text";
        return select.Offers(option) ? null : $"is not {select.Choices}";
    }

    // A text that fits the entity's lengths and pattern, or unknown.
    static string? TextRefuses(SharpEntity entity, object? value)
    {
        if (value is null) return null;
        if (value is not string text) return "is not text";
        return ((SharpText)entity).Refuses(text);
    }

    // A brightness or a position: a number the entity takes, or unknown.
    static string? WholeRefuses(object? value, Func<double, string?> refuses) =>
        value is null ? null : value is double number ? refuses(number) : "is not a number";
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

    /// <summary>The option as a text; null when absent or of another type.</summary>
    public string? Text(string option) => Text(Options?[option]);
    /// <summary>The option as a finite number; null when absent or of another type.</summary>
    public double? Number(string option) =>
        Options?[option] is JsonValue value && value.GetValueKind() == JsonValueKind.Number
        && double.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
    /// <summary>The option as a whole number, its fraction dropped and kept within the range of an <c>int</c>; null when absent or of another type.</summary>
    public int? Whole(string option) => Number(option) is { } number ? (int)Math.Clamp(Math.Truncate(number), int.MinValue, int.MaxValue) : null;
    /// <summary>The option as true or false; null when absent or of another type.</summary>
    public bool? Flag(string option) => Options?[option] is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? value.GetValue<bool>() : null;
    /// <summary>The option as a list of texts, any other item left out; null when absent or of another type.</summary>
    public string[]? Texts(string option) => Options?[option] is JsonArray values ? values.Select(Text).OfType<string>().ToArray() : null;
}

/// <summary>A domain a definition built from nodes creates: its entity from a description, and its state keys.</summary>
internal sealed record GraphDomain(Func<EntityDescription, SharpEntity> Create, IReadOnlyDictionary<string, GraphState> State);

/// <summary>
/// One state key of a <see cref="GraphDomain"/>: <paramref name="Refuses"/> says why the entity does not take a value
/// (worded to follow "The value 5 of Power (power)", such as "is neither true nor false"), or null when it does;
/// <paramref name="Keep"/> sets a value it takes on the entity; <paramref name="Read"/> gives the value the entity has.
/// </summary>
internal sealed record GraphState(Func<SharpEntity, object?, string?> Refuses, Action<SharpEntity, object?> Keep, Func<SharpEntity, object?> Read);

