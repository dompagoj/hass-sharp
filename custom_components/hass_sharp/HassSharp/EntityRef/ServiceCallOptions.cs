namespace HassSharp;

public class LightOnOpts
{
    public required string[] EntityId { get; init; }
    public double? Transition { get; init; }
    public int[]? RgbColor { get; init; }
    public int? BrightnessPct { get; init; }
    public double? BrightnessStepPct { get; init; }
    public int? ColorTempKelvin { get; init; }
    public string? Effect { get; init; }
    public int[]? RgbwColor { get; init; }
    public int[]? RgbwwColor { get; init; }
    public string? ColorName { get; init; }
    public double[]? HsColor { get; init; }
    public double[]? XyColor { get; init; }
    public int? ColorTemp { get; init; }
    public int? Brightness { get; init; }
    public int? BrightnessStep { get; init; }
    public bool? White { get; init; }
    public string? Profile { get; init; }
    public string? Flash { get; init; }
}