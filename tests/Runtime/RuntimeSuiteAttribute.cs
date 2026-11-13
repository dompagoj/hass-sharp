// Marks a static runtime test entry point. Program.cs discovers every marked method:
// without arguments it runs the default suites in Order (then by flag), and with flags it
// runs only the suites whose flag was passed. A method with one string parameter receives
// the value of "--flag=value" (null when the flag is passed bare or in the default run).
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RuntimeSuiteAttribute(string flag) : Attribute
{
    public string Flag { get; } = flag;
    public int Order { get; set; } = int.MaxValue;
    public bool RunByDefault { get; set; } = true;
}
