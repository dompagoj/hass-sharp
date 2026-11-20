namespace HassSharp;

/// <summary>
/// The Home Assistant domains of the entity types HassSharp creates, one per entity class, such as <c>switch</c> for
/// <see cref="SharpSwitch"/>; not every domain Home Assistant has. Kept to the catalog's <c>entityDomains</c>, in its
/// order, by a runtime test.
/// </summary>
public static class HassDomains
{
    /// <summary>The domain of a binary sensor: <c>binary_sensor</c>.</summary>
    public const string BinarySensor = "binary_sensor";
    /// <summary>The domain of a sensor: <c>sensor</c>.</summary>
    public const string Sensor = "sensor";
    /// <summary>The domain of a switch: <c>switch</c>.</summary>
    public const string Switch = "switch";
    /// <summary>The domain of a button: <c>button</c>.</summary>
    public const string Button = "button";
    /// <summary>The domain of a number: <c>number</c>.</summary>
    public const string Number = "number";
    /// <summary>The domain of a select: <c>select</c>.</summary>
    public const string Select = "select";
    /// <summary>The domain of a text: <c>text</c>.</summary>
    public const string Text = "text";
    /// <summary>The domain of a light: <c>light</c>.</summary>
    public const string Light = "light";
    /// <summary>The domain of a cover: <c>cover</c>.</summary>
    public const string Cover = "cover";
    /// <summary>The domain of a camera: <c>camera</c>.</summary>
    public const string Camera = "camera";
}
