namespace HassSharp;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class NodeAttribute(string title) : Attribute
{
    public string Title { get; } = title;
    public string Category { get; set; } = "Custom";
}

[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = false, Inherited = false)]
public sealed class PortAttribute(string label) : Attribute
{
    public string Label { get; } = label;
}
