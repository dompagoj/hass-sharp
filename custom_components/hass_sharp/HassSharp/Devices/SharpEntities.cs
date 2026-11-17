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
    /// <summary>The domain of the nearest typed entity <paramref name="type"/> derives from, such as <c>switch</c>; null for any other type.</summary>
    public static string? Of(Type type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (current == typeof(SharpBinarySensor)) return "binary_sensor";
            if (current == typeof(SharpSensor)) return "sensor";
            if (current == typeof(SharpSwitch)) return "switch";
        }
        return null;
    }
}
