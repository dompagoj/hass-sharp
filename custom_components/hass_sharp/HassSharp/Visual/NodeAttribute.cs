namespace HassSharp;

/// <summary>
/// Makes a public static method a node: each parameter is an input pin, the return value is the output pin. On a custom
/// device or entity class it is only the class's title, which the entity panel shows (<see cref="Category"/> and
/// <see cref="Pure"/> mean nothing there); without it the title is the class's name in words.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class NodeAttribute(string title) : Attribute
{
    /// <summary>The node's name on the canvas and in the node list; on a class, the class's title.</summary>
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
    /// <summary>
    /// On an <c>Action</c>, <c>Action&lt;T&gt;</c>, <c>Func&lt;Task&gt;</c> or <c>Func&lt;T, Task&gt;</c> parameter: a flow output, not an
    /// input pin. Calling it runs the steps wired to that flow, in a scope of their own, and returns when they end; the
    /// value it is called with (one, or one per element of a named tuple) is a pin those steps read. When the method
    /// returns, the run goes on at Done.
    /// </summary>
    public bool FlowControl { get; set; }
}

/// <summary>
/// Runs once when an entity definition whose graph uses any <c>[Node]</c> of the class starts, before its When started
/// flow: the place for one-time work the class's nodes share, such as downloading a model. The method is
/// <c>public static Task Setup(NodeSetupContext context)</c>, one per class, in a node library. A node of such a class
/// works only in an entity definition's graph.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class NodeSetupAttribute : Attribute;
