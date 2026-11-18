using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HassSharp;

// The two modes are named after their class: NumberMode and TextMode are the names of the generated types of Home
// Assistant's own number and text entities (HassSharp.Entities), which keep clear of every public type here.

/// <summary>How Home Assistant shows a <see cref="SharpNumber"/> for setting it.</summary>
public enum SharpNumberMode
{
    /// <summary>A slider when the range has few steps, else a box: Home Assistant chooses.</summary>
    Auto,
    /// <summary>A box to type the number in.</summary>
    Box,
    /// <summary>A slider.</summary>
    Slider,
}

/// <summary>How Home Assistant shows a <see cref="SharpText"/> for setting it.</summary>
public enum SharpTextMode
{
    /// <summary>A box that shows the text.</summary>
    Text,
    /// <summary>A box that hides the text, as for a password.</summary>
    Password,
}

/// <summary>
/// A button: something Home Assistant presses, such as a restart or a chime, with no state of its own. Handle the press
/// by overriding <see cref="Press"/> or, for a button inside a device, by setting <see cref="OnPress"/>.
/// </summary>
public class SharpButton : SharpEntity
{
    static readonly IReadOnlyList<string> ButtonCommands = ["press"];

    /// <summary>A button whose key is <c>main</c>.</summary>
    public SharpButton() { }
    /// <summary>A button with its own key, unique within its device.</summary>
    /// <param name="key">What identifies it within the device, such as <c>restart</c>.</param>
    /// <param name="name">What Home Assistant calls it; null for the device's name alone.</param>
    public SharpButton(string key, string? name = null) : base(key, name) { }

    /// <summary>What pressing it does, when <see cref="Press"/> is not overridden.</summary>
    public Func<Task>? OnPress { get; init; }

    /// <summary>Presses the button when Home Assistant asks; runs <see cref="OnPress"/> unless overridden.</summary>
    protected virtual Task Press() => OnPress?.Invoke() ?? throw new NotSupportedException($"Button '{Key}' cannot be pressed.");

    internal override string Domain => "button";
    internal override IReadOnlyList<string> Commands => ButtonCommands;
    internal override Task Command(string name, JsonElement data) => name switch
    {
        "press" => Press(),
        _ => base.Command(name, data),
    };
}

/// <summary>
/// A number Home Assistant sets, such as a target temperature or a volume, within <see cref="Min"/> and
/// <see cref="Max"/>. Handle a new value by overriding <see cref="SetValue"/> or, for a number inside a device, by
/// setting <see cref="OnSetValue"/>; a value outside the range is refused before either runs.
/// </summary>
public class SharpNumber : SharpEntity
{
    static readonly IReadOnlyList<string> NumberCommands = ["set_value"];
    double? value;
    double step = 1;

    /// <summary>A number whose key is <c>main</c>.</summary>
    public SharpNumber() { }
    /// <summary>A number with its own key, unique within its device.</summary>
    /// <param name="key">What identifies it within the device, such as <c>target</c>.</param>
    /// <param name="name">What Home Assistant calls it; null for the device's name alone.</param>
    public SharpNumber(string key, string? name = null) : base(key, name) { }

    /// <summary>
    /// The number; null while unknown. It lies from <see cref="Min"/> to <see cref="Max"/>, so in an initializer set
    /// those first. Setting it publishes.
    /// </summary>
    /// <exception cref="ArgumentException">The number is outside the range, or not a finite number.</exception>
    public double? Value
    {
        get => value;
        set
        {
            if (value is { } number && !Within(number)) throw Refused(Range, CommandData.Shown(number));
            this.value = value;
            Publish();
        }
    }

    /// <summary>The lowest value; 0 unless set.</summary>
    public double Min { get; init; } = 0;
    /// <summary>The highest value; 100 unless set.</summary>
    public double Max { get; init; } = 100;
    /// <summary>How far apart the values Home Assistant offers are, above 0; 1 unless set.</summary>
    /// <exception cref="ArgumentException">The step is not a number above 0.</exception>
    public double Step
    {
        get => step;
        init => step = double.IsFinite(value) && value > 0 ? value : throw Refused("a step above 0", CommandData.Shown(value));
    }
    /// <summary>Whether Home Assistant shows a box or a slider; it chooses unless set.</summary>
    public SharpNumberMode Mode { get; init; }
    /// <summary>The unit of the number, such as <c>°C</c> or <c>%</c>; null for none.</summary>
    public string? Unit { get; init; }

    /// <summary>What setting a value does, when <see cref="SetValue"/> is not overridden.</summary>
    public Func<double, Task>? OnSetValue { get; init; }

    /// <summary>
    /// Sets the number when Home Assistant asks, with a value from <see cref="Min"/> to <see cref="Max"/>; runs
    /// <see cref="OnSetValue"/> unless overridden. Set <see cref="Value"/> to show the new value.
    /// </summary>
    protected virtual Task SetValue(double value) => OnSetValue?.Invoke(value) ?? throw new NotSupportedException($"Number '{Key}' cannot be set.");

    /// <summary>Whether the number lies within the range (never NaN).</summary>
    internal bool Within(double number) => number >= Min && number <= Max;
    /// <summary>What the number takes, in a message.</summary>
    internal string Range => $"a number from {CommandData.Shown(Min)} to {CommandData.Shown(Max)}";

    internal override string Domain => "number";
    internal override bool RestoresByDefault => true;
    internal override IReadOnlyList<string> Commands => NumberCommands;
    internal override Task Command(string name, JsonElement data) => name switch
    {
        "set_value" => SetValue(Given(data)),
        _ => base.Command(name, data),
    };
    internal override void CheckData(string name, JsonElement data) { if (name == "set_value") _ = Given(data); }
    internal override JsonObject Values() => new() { ["value"] = Value };
    internal override string[] Restore(JsonObject values) => Restoring(values, ("value", node => Value = StoredNumber(node)));
    internal override JsonObject DomainOptions() => new()
    {
        ["min"] = Min,
        ["max"] = Max,
        ["step"] = Step,
        ["mode"] = Mode switch { SharpNumberMode.Box => "box", SharpNumberMode.Slider => "slider", _ => "auto" },
        ["unit"] = Unit,
    };

    double Given(JsonElement data)
    {
        var given = CommandData.Required(this, data, "value", Range);
        return CommandData.Number(given) is { } number && Within(number) ? number : throw Refused(Range, CommandData.Shown(given));
    }
}

/// <summary>
/// A choice of one of a list of <see cref="Options"/>, such as a mode, that Home Assistant sets. Handle a new choice by
/// overriding <see cref="SelectOption"/> or, for a select inside a device, by setting <see cref="OnSelectOption"/>; an
/// option not in the list is refused before either runs.
/// </summary>
public class SharpSelect : SharpEntity
{
    static readonly IReadOnlyList<string> SelectCommands = ["select_option"];
    string? option;
    IReadOnlyList<string> options = [];

    /// <summary>A select whose key is <c>main</c>.</summary>
    public SharpSelect() { }
    /// <summary>A select with its own key, unique within its device.</summary>
    /// <param name="key">What identifies it within the device, such as <c>mode</c>.</param>
    /// <param name="name">What Home Assistant calls it; null for the device's name alone.</param>
    public SharpSelect(string key, string? name = null) : base(key, name) { }

    /// <summary>
    /// The option chosen, one of <see cref="Options"/>; null while unknown. In an initializer set the options first.
    /// Setting it publishes.
    /// </summary>
    /// <exception cref="ArgumentException">The option is not one of <see cref="Options"/>.</exception>
    public string? Option
    {
        get => option;
        set
        {
            if (value != null && !Offers(value)) throw Refused(Choices, $"\"{value}\"");
            option = value;
            Publish();
        }
    }

    /// <summary>The options to choose from, in the order Home Assistant lists them; none unless set.</summary>
    public IReadOnlyList<string> Options { get => options; init => options = value ?? []; }

    /// <summary>What choosing an option does, when <see cref="SelectOption"/> is not overridden.</summary>
    public Func<string, Task>? OnSelectOption { get; init; }

    /// <summary>
    /// Chooses an option when Home Assistant asks, one of <see cref="Options"/>; runs <see cref="OnSelectOption"/> unless
    /// overridden. Set <see cref="Option"/> to show the new choice.
    /// </summary>
    protected virtual Task SelectOption(string option) => OnSelectOption?.Invoke(option) ?? throw new NotSupportedException($"Select '{Key}' cannot be set.");

    /// <summary>Whether the option is one of the list.</summary>
    internal bool Offers(string option) => Options.Contains(option, StringComparer.Ordinal);
    /// <summary>What the select takes, in a message.</summary>
    internal string Choices => Options.Count == 0 ? "one of its options, and it has none" : "one of " + VisualGraphValidator.Words.Or(Options);

    internal override string Domain => "select";
    internal override bool RestoresByDefault => true;
    internal override IReadOnlyList<string> Commands => SelectCommands;
    internal override Task Command(string name, JsonElement data) => name switch
    {
        "select_option" => SelectOption(Given(data)),
        _ => base.Command(name, data),
    };
    internal override void CheckData(string name, JsonElement data) { if (name == "select_option") _ = Given(data); }
    internal override JsonObject Values() => new() { ["option"] = Option };
    internal override string[] Restore(JsonObject values) => Restoring(values, ("option", node => Option = StoredText(node)));
    internal override JsonObject DomainOptions() => new() { ["options"] = new JsonArray(Options.Select(o => (JsonNode?)o).ToArray()) };

    string Given(JsonElement data)
    {
        var given = CommandData.Required(this, data, "option", Choices);
        return CommandData.Text(given) is { } text && Offers(text) ? text : throw Refused(Choices, CommandData.Shown(given));
    }
}

/// <summary>
/// A text Home Assistant sets, such as a message to show, from <see cref="Min"/> to <see cref="Max"/> characters long
/// and matching <see cref="Pattern"/> when it has one. Handle a new text by overriding <see cref="SetValue"/> or, for a
/// text inside a device, by setting <see cref="OnSetValue"/>; a text that does not fit is refused before either runs.
/// </summary>
public class SharpText : SharpEntity
{
    static readonly IReadOnlyList<string> TextCommands = ["set_value"];
    static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);
    /// <summary>The most characters Home Assistant keeps in a state.</summary>
    const int Longest = 255;
    // A password's value given as something other than a text, in a message.
    const string Hidden = "the hidden value given";
    string? value, pattern;
    int min, max = 100;
    Regex? matcher;

    /// <summary>A text whose key is <c>main</c>.</summary>
    public SharpText() { }
    /// <summary>A text with its own key, unique within its device.</summary>
    /// <param name="key">What identifies it within the device, such as <c>message</c>.</param>
    /// <param name="name">What Home Assistant calls it; null for the device's name alone.</param>
    public SharpText(string key, string? name = null) : base(key, name) { }

    /// <summary>
    /// The text; null while unknown. It fits <see cref="Min"/>, <see cref="Max"/> and <see cref="Pattern"/>, so in an
    /// initializer set those first. Setting it publishes.
    /// </summary>
    /// <exception cref="ArgumentException">The text is too short, too long or does not match the pattern.</exception>
    public string? Value
    {
        get => value;
        set
        {
            if (value != null && Refusal(value) is { } refused) throw refused;
            this.value = value;
            Publish();
        }
    }

    /// <summary>The fewest characters the text has, 0 or more; 0 unless set.</summary>
    /// <exception cref="ArgumentException">The length is below 0.</exception>
    public int Min
    {
        get => min;
        init => min = value >= 0 ? value : throw Refused("a shortest length of 0 or more", value.ToString(CultureInfo.InvariantCulture));
    }
    /// <summary>The most characters the text has, at most 255 (as many as Home Assistant keeps); 100 unless set.</summary>
    /// <exception cref="ArgumentException">The length is above 255.</exception>
    public int Max
    {
        get => max;
        init => max = value <= Longest ? value
            : throw Refused($"a longest length of {Longest} or less, as Home Assistant keeps at most {Longest} characters", value.ToString(CultureInfo.InvariantCulture));
    }
    /// <summary>
    /// A regular expression the text matches, from its start as Home Assistant matches it (end it with <c>$</c> to match
    /// the whole text); null for any text.
    /// </summary>
    /// <exception cref="ArgumentException">The pattern is not a regular expression.</exception>
    public string? Pattern
    {
        get => pattern;
        init
        {
            // Checked as written, so that anchoring it never makes a pattern of what is not one.
            if (value != null) _ = new Regex(value, RegexOptions.None, MatchTimeout);
            matcher = value == null ? null : Anchored(value);
            pattern = value;
        }
    }
    /// <summary>Whether Home Assistant shows the text or hides it, as a password; shown unless set.</summary>
    public SharpTextMode Mode { get; init; }

    /// <summary>What setting a text does, when <see cref="SetValue"/> is not overridden.</summary>
    public Func<string, Task>? OnSetValue { get; init; }

    /// <summary>
    /// Sets the text when Home Assistant asks, with a text that fits; runs <see cref="OnSetValue"/> unless overridden.
    /// Set <see cref="Value"/> to show the new text.
    /// </summary>
    protected virtual Task SetValue(string value) => OnSetValue?.Invoke(value) ?? throw new NotSupportedException($"Text '{Key}' cannot be set.");

    // The pattern matched only at the start of the text, as Python's re.match matches it, so that a text it does not fit
    // is not searched on to its end: \G(?:…), which reads as the same pattern. A pattern it does not read as one (a
    // verbose pattern ending in a comment, which would take in the closing parenthesis) is matched as written, and its
    // match must then start at the start.
    static Regex Anchored(string pattern)
    {
        try { return new Regex($@"\G(?:{pattern})", RegexOptions.None, MatchTimeout); }
        catch (ArgumentException) { return new Regex(pattern, RegexOptions.None, MatchTimeout); }
    }

    enum Fit { Fits, Length, Pattern, Slow }

    // Whether the text fits; a match that takes longer than MatchTimeout does not.
    Fit Check(string text)
    {
        // Counted as Home Assistant counts them, in Unicode characters rather than UTF-16 units.
        var length = text.EnumerateRunes().Count();
        if (length < Min || length > Max) return Fit.Length;
        try { return matcher == null || matcher.Match(text) is { Success: true, Index: 0 } ? Fit.Fits : Fit.Pattern; }
        catch (RegexMatchTimeoutException) { return Fit.Slow; }
    }

    string Length => $"a text of {Min} to {Max} characters";
    string Matching => $"a text that matches {pattern}";
    string Seconds => CommandData.Shown(MatchTimeout.TotalSeconds);

    /// <summary>Why the text does not fit, worded to follow "The value "ab" of Code (code)"; null when it does.</summary>
    internal string? Refuses(string text) => Check(text) switch
    {
        Fit.Length => $"is not {Length}",
        Fit.Pattern => $"is not {Matching}",
        Fit.Slow => $"took longer than {Seconds} s to match the pattern {pattern}",
        _ => null,
    };

    // The refusal of a text that does not fit, naming the entity: its length when that is what is wrong, else the text
    // itself unless it is a password's; null when it fits.
    ArgumentException? Refusal(string text) => Check(text) switch
    {
        Fit.Length => Refused(Length, $"one of {text.EnumerateRunes().Count()}"),
        Fit.Pattern => Refused(Matching, Mode == SharpTextMode.Password ? "the hidden text given" : $"\"{text}\""),
        Fit.Slow => new ArgumentException($"{Called} cannot take the text: matching it to its pattern {pattern} took longer than {Seconds} s"),
        _ => null,
    };

    internal override string Domain => "text";
    internal override bool RestoresByDefault => true;
    internal override IReadOnlyList<string> Commands => TextCommands;
    internal override Task Command(string name, JsonElement data) => name switch
    {
        "set_value" => SetValue(Given(data)),
        _ => base.Command(name, data),
    };
    internal override void CheckData(string name, JsonElement data) { if (name == "set_value") _ = Given(data); }
    internal override JsonObject Values() => new() { ["value"] = Value };
    internal override string[] Restore(JsonObject values) => Restoring(values, ("value", node => Value = StoredText(node)));
    internal override JsonObject DomainOptions() => new()
    {
        ["min"] = Min,
        ["max"] = Max,
        ["pattern"] = Pattern,
        ["mode"] = Mode == SharpTextMode.Password ? "password" : "text",
    };

    string Given(JsonElement data)
    {
        var given = CommandData.Required(this, data, "value", Length);
        if (CommandData.Text(given) is not { } text) throw Refused(Length, Mode == SharpTextMode.Password ? Hidden : CommandData.Shown(given));
        return Refusal(text) is { } refused ? throw refused : text;
    }
}

/// <summary>
/// A light Home Assistant turns on and off and, when it is <see cref="Dimmable"/>, dims. Handle the commands by
/// overriding <see cref="TurnOn"/> and <see cref="TurnOff"/>, or, for a light inside a device, by setting
/// <see cref="OnTurnOn"/> and <see cref="OnTurnOff"/>.
/// </summary>
public class SharpLight : SharpEntity
{
    static readonly IReadOnlyList<string> LightCommands = ["turn_on", "turn_off"];
    const string Levels = "a brightness from 0 to 255, in whole numbers";
    bool? isOn;
    int? brightness;

    /// <summary>A light whose key is <c>main</c>.</summary>
    public SharpLight() { }
    /// <summary>A light with its own key, unique within its device.</summary>
    /// <param name="key">What identifies it within the device, such as <c>lamp</c>.</param>
    /// <param name="name">What Home Assistant calls it; null for the device's name alone.</param>
    public SharpLight(string key, string? name = null) : base(key, name) { }

    /// <summary>Whether it is on; null while unknown. Setting it publishes.</summary>
    public bool? IsOn
    {
        get => isOn;
        set { isOn = value; Publish(); }
    }

    /// <summary>
    /// How bright it is, from 0 to 255 as Home Assistant has it; null while unknown, and always for a light that cannot
    /// be dimmed (so in an initializer set <see cref="Dimmable"/> first). Setting it publishes.
    /// </summary>
    /// <exception cref="ArgumentException">The light cannot be dimmed, or the brightness is outside 0 to 255.</exception>
    public int? Brightness
    {
        get => brightness;
        set
        {
            if (value is { } level)
            {
                if (!Dimmable) throw NoBrightness(level.ToString(CultureInfo.InvariantCulture));
                if (level is < 0 or > 255) throw Refused(Levels, level.ToString(CultureInfo.InvariantCulture));
            }
            brightness = value;
            Publish();
        }
    }

    /// <summary>Whether it can be dimmed, and so has a <see cref="Brightness"/>; false unless set.</summary>
    public bool Dimmable { get; init; }

    /// <summary>What turning it on does, when <see cref="TurnOn"/> is not overridden: given the brightness asked for, or null for none.</summary>
    public Func<int?, Task>? OnTurnOn { get; init; }
    /// <summary>What turning it off does, when <see cref="TurnOff"/> is not overridden.</summary>
    public Func<Task>? OnTurnOff { get; init; }

    /// <summary>
    /// Turns the light on when Home Assistant asks, at a brightness from 0 to 255, or null when none was asked for (always
    /// for a light that cannot be dimmed); runs <see cref="OnTurnOn"/> unless overridden.
    /// </summary>
    protected virtual Task TurnOn(int? brightness) => OnTurnOn?.Invoke(brightness) ?? throw new NotSupportedException($"Light '{Key}' cannot be turned on.");
    /// <summary>Turns the light off when Home Assistant asks; runs <see cref="OnTurnOff"/> unless overridden.</summary>
    protected virtual Task TurnOff() => OnTurnOff?.Invoke() ?? throw new NotSupportedException($"Light '{Key}' cannot be turned off.");

    /// <summary>Why the light does not take a brightness, worded to follow "The brightness 128 of Lamp (lamp)"; null when it does.</summary>
    internal string? Refuses(double level) =>
        !Dimmable ? "is given, and a light that cannot be dimmed has none"
        : level == Math.Floor(level) && level is >= 0 and <= 255 ? null : "is not a whole number from 0 to 255";

    ArgumentException NoBrightness(string given) => new($"{Called} cannot be dimmed, so it takes no brightness, not {given}");

    internal override string Domain => "light";
    internal override bool RestoresByDefault => true;
    internal override IReadOnlyList<string> Commands => LightCommands;
    internal override Task Command(string name, JsonElement data) => name switch
    {
        "turn_on" => TurnOn(Given(data)),
        "turn_off" => TurnOff(),
        _ => base.Command(name, data),
    };
    internal override void CheckData(string name, JsonElement data) { if (name == "turn_on") _ = Given(data); }
    internal override JsonObject Values() => new() { ["is_on"] = IsOn, ["brightness"] = Brightness };
    internal override string[] Restore(JsonObject values) =>
        Restoring(values, ("is_on", node => IsOn = StoredFlag(node)), ("brightness", node => Brightness = StoredWhole(node)));
    internal override JsonObject DomainOptions() => new() { ["dimmable"] = Dimmable };

    int? Given(JsonElement data)
    {
        if (CommandData.Given(data, "brightness") is not { } given) return null;
        if (!Dimmable) throw NoBrightness(CommandData.Shown(given));
        return CommandData.Whole(given, 0, 255) ?? throw Refused(Levels, CommandData.Shown(given));
    }
}

/// <summary>
/// A cover, such as a blind, a shutter or a garage door, that Home Assistant opens, closes and stops and, when it is
/// <see cref="Positionable"/>, moves to a position. Handle the commands by overriding <see cref="Open"/>,
/// <see cref="Close"/>, <see cref="StopMoving"/> and <see cref="SetPosition"/>, or, for a cover inside a device, by
/// setting <see cref="OnOpen"/>, <see cref="OnClose"/>, <see cref="OnStopMoving"/> and <see cref="OnSetPosition"/>.
/// </summary>
public class SharpCover : SharpEntity
{
    static readonly IReadOnlyList<string> CoverCommands = ["open", "close", "stop", "set_position"];
    const string Positions = "a position from 0 (closed) to 100 (open), in whole numbers";
    bool? isClosed;
    int? position;

    /// <summary>A cover whose key is <c>main</c>.</summary>
    public SharpCover() { }
    /// <summary>A cover with its own key, unique within its device.</summary>
    /// <param name="key">What identifies it within the device, such as <c>blind</c>.</param>
    /// <param name="name">What Home Assistant calls it; null for the device's name alone.</param>
    public SharpCover(string key, string? name = null) : base(key, name) { }

    /// <summary>Whether it is closed; null while unknown. Setting it publishes.</summary>
    public bool? IsClosed
    {
        get => isClosed;
        set { isClosed = value; Publish(); }
    }

    /// <summary>
    /// How far open it is, from 0 (closed) to 100 (open) as Home Assistant has it; null while unknown, and always for a
    /// cover without a position (so in an initializer set <see cref="Positionable"/> first). Setting it publishes.
    /// </summary>
    /// <exception cref="ArgumentException">The cover has no position, or the position is outside 0 to 100.</exception>
    public int? Position
    {
        get => position;
        set
        {
            if (value is { } at)
            {
                if (!Positionable) throw NoPosition(at.ToString(CultureInfo.InvariantCulture));
                if (at is < 0 or > 100) throw Refused(Positions, at.ToString(CultureInfo.InvariantCulture));
            }
            position = value;
            Publish();
        }
    }

    /// <summary>Whether it can be moved to a position, and so has a <see cref="Position"/>; false unless set.</summary>
    public bool Positionable { get; init; }

    /// <summary>What opening it does, when <see cref="Open"/> is not overridden.</summary>
    public Func<Task>? OnOpen { get; init; }
    /// <summary>What closing it does, when <see cref="Close"/> is not overridden.</summary>
    public Func<Task>? OnClose { get; init; }
    /// <summary>What stopping it does, when <see cref="StopMoving"/> is not overridden.</summary>
    public Func<Task>? OnStopMoving { get; init; }
    /// <summary>What moving it to a position does, when <see cref="SetPosition"/> is not overridden.</summary>
    public Func<int, Task>? OnSetPosition { get; init; }

    /// <summary>Opens the cover when Home Assistant asks; runs <see cref="OnOpen"/> unless overridden.</summary>
    protected virtual Task Open() => OnOpen?.Invoke() ?? throw new NotSupportedException($"Cover '{Key}' cannot be opened.");
    /// <summary>Closes the cover when Home Assistant asks; runs <see cref="OnClose"/> unless overridden.</summary>
    protected virtual Task Close() => OnClose?.Invoke() ?? throw new NotSupportedException($"Cover '{Key}' cannot be closed.");
    /// <summary>
    /// Stops the cover moving when Home Assistant asks; runs <see cref="OnStopMoving"/> unless overridden. (Not named
    /// <c>Stop</c>: that is what runs as the definition stops.)
    /// </summary>
    protected virtual Task StopMoving() => OnStopMoving?.Invoke() ?? throw new NotSupportedException($"Cover '{Key}' cannot be stopped.");
    /// <summary>
    /// Moves the cover when Home Assistant asks, to a position from 0 (closed) to 100 (open); runs
    /// <see cref="OnSetPosition"/> unless overridden. Only a cover with a position is asked.
    /// </summary>
    protected virtual Task SetPosition(int position) => OnSetPosition?.Invoke(position) ?? throw new NotSupportedException($"Cover '{Key}' cannot be moved to a position.");

    /// <summary>Why the cover does not take a position, worded to follow "The position 40 of Blind (blind)"; null when it does.</summary>
    internal string? Refuses(double at) =>
        !Positionable ? "is given, and a cover without a position has none"
        : at == Math.Floor(at) && at is >= 0 and <= 100 ? null : "is not a whole number from 0 to 100";

    ArgumentException NoPosition(string? given) => new(given == null
        ? $"{Called} has no position, so it cannot be moved to one"
        : $"{Called} has no position, so it takes none, not {given}");

    internal override string Domain => "cover";
    internal override bool RestoresByDefault => true;
    internal override IReadOnlyList<string> Commands => CoverCommands;
    internal override Task Command(string name, JsonElement data) => name switch
    {
        "open" => Open(),
        "close" => Close(),
        "stop" => StopMoving(),
        "set_position" => SetPosition(Given(data)),
        _ => base.Command(name, data),
    };
    internal override void CheckData(string name, JsonElement data) { if (name == "set_position") _ = Given(data); }
    internal override JsonObject Values() => new() { ["is_closed"] = IsClosed, ["position"] = Position };
    internal override string[] Restore(JsonObject values) =>
        Restoring(values, ("is_closed", node => IsClosed = StoredFlag(node)), ("position", node => Position = StoredWhole(node)));
    internal override JsonObject DomainOptions() => new() { ["positionable"] = Positionable };

    int Given(JsonElement data)
    {
        if (!Positionable) throw NoPosition(CommandData.Given(data, "position") is { } any ? CommandData.Shown(any) : null);
        var given = CommandData.Required(this, data, "position", Positions);
        return CommandData.Whole(given, 0, 100) ?? throw Refused(Positions, CommandData.Shown(given));
    }
}

/// <summary>
/// Reads the service data of a command as Home Assistant sends it: an object of named values, where a whole number may
/// come as <c>5</c> or <c>5.0</c>, and a value that is null counts as not given.
/// </summary>
internal static class CommandData
{
    /// <summary>The named value; null when it is not given, or the data is not an object.</summary>
    internal static JsonElement? Given(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    /// <summary>The named value, which the command needs.</summary>
    /// <exception cref="ArgumentException">It is not given; the message names the entity and what it takes.</exception>
    internal static JsonElement Required(SharpEntity entity, JsonElement data, string name, string takes) =>
        Given(data, name) ?? throw new ArgumentException($"{entity.Called} needs {VisualGraphValidator.Words.Article(name)} {name}: it takes {takes}");

    /// <summary>The value as a finite number; null for anything else.</summary>
    internal static double? Number(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;

    /// <summary>The value as a whole number from <paramref name="min"/> to <paramref name="max"/>; null for anything else.</summary>
    internal static int? Whole(JsonElement value, int min, int max) =>
        Number(value) is { } number && number == Math.Floor(number) && number >= min && number <= max ? (int)number : null;

    /// <summary>The value as a text; null for anything else.</summary>
    internal static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A given value in a message, as JSON writes it: <c>45</c>, <c>"medium"</c>.</summary>
    internal static string Shown(JsonElement value) => value.GetRawText();

    /// <summary>A number in a message, such as <c>22.5</c>.</summary>
    internal static string Shown(double number) => number.ToString(CultureInfo.InvariantCulture);
}
