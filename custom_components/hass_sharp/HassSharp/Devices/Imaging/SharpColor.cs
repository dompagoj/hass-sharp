namespace HassSharp;

/// <summary>A colour to draw on a <see cref="SharpImage"/> with: one of the seven named ones, or any from <see cref="FromRgb"/>.</summary>
/// <param name="R">Red, 0–255.</param>
/// <param name="G">Green, 0–255.</param>
/// <param name="B">Blue, 0–255.</param>
public readonly record struct SharpColor(byte R, byte G, byte B)
{
    /// <summary>Red, the colour a drawing has when none is given.</summary>
    public static SharpColor Red { get; } = new(255, 0, 0);
    /// <summary>Green.</summary>
    public static SharpColor Green { get; } = new(0, 255, 0);
    /// <summary>Blue.</summary>
    public static SharpColor Blue { get; } = new(0, 0, 255);
    /// <summary>Yellow.</summary>
    public static SharpColor Yellow { get; } = new(255, 255, 0);
    /// <summary>Orange.</summary>
    public static SharpColor Orange { get; } = new(255, 165, 0);
    /// <summary>White.</summary>
    public static SharpColor White { get; } = new(255, 255, 255);
    /// <summary>Black.</summary>
    public static SharpColor Black { get; } = new(0, 0, 0);

    /// <summary>A colour from its red, green and blue, each 0–255.</summary>
    public static SharpColor FromRgb(byte r, byte g, byte b) => new(r, g, b);

    /// <summary>Reads one of the seven names (Red, Green, Blue, Yellow, Orange, White, Black) in any case.</summary>
    /// <param name="name">The name.</param>
    /// <param name="color">The colour, or black when the name is not one of them.</param>
    /// <returns>Whether the name is one of the seven.</returns>
    public static bool TryParse(string name, out SharpColor color)
    {
        (var known, color) = name?.ToLowerInvariant() switch
        {
            "red" => (true, Red),
            "green" => (true, Green),
            "blue" => (true, Blue),
            "yellow" => (true, Yellow),
            "orange" => (true, Orange),
            "white" => (true, White),
            "black" => (true, Black),
            _ => (false, default(SharpColor)),
        };
        return known;
    }
}
