namespace HassSharp;

/// <summary>Makes a public static method a node: each parameter is an input pin, the return value is the output pin.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class NodeAttribute(string title) : Attribute
{
    /// <summary>The node's name on the canvas and in the node list.</summary>
    public string Title { get; } = title;
    /// <summary>The group the node is listed under. A Custom C# node ignores it.</summary>
    public string Category { get; set; } = "Custom";
    /// <summary>
    /// A value node: it has no place in the run order and is worked out each time a step uses its result. It must
    /// return a value.
    /// </summary>
    public bool Pure { get; set; }
}

/// <summary>The label of an input pin (on a parameter) or of the output pin (on the return value).</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = false, Inherited = false)]
public sealed class PortAttribute(string label) : Attribute
{
    /// <summary>The label people see next to the pin.</summary>
    public string Label { get; } = label;
}
