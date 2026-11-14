using System.Reflection;
using System.Runtime.Loader;

namespace HassSharp;

/// <summary>
/// Collectible load context for compiled automations, one per automation. The typed service calls it was compiled
/// against are loaded into it, from their bytes, the first time its code needs them; they go when it is unloaded.
/// </summary>
public sealed class ScriptLoadContext : AssemblyLoadContext
{
    static readonly Assembly HassSharpAssembly = typeof(Automation).Assembly;

    readonly AssemblyDependencyResolver _resolver = new(HassSharpAssembly.Location);

    readonly string? _serviceCallsName;
    readonly byte[]? _serviceCallsImage;

    public ScriptLoadContext() : base(true)
    {
    }

    /// <param name="serviceCalls">The typed service calls the automation was compiled against: only their name and bytes
    /// are kept, not the compiler's metadata.</param>
    public ScriptLoadContext(GeneratedServiceCalls serviceCalls) : base(true)
    {
        (_serviceCallsName, _serviceCallsImage) = (serviceCalls.AssemblyName, serviceCalls.Image);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // User automations must use the host's HassSharp types. Loading another copy
        // would break type identity (for example, Automation.IsAssignableFrom()).
        if (AssemblyName.ReferenceMatchesDefinition(assemblyName, HassSharpAssembly.GetName()))
        {
            return HassSharpAssembly;
        }

        if (_serviceCallsImage != null && assemblyName.Name == _serviceCallsName)
        {
            using var image = new MemoryStream(_serviceCallsImage, writable: false);
            return LoadFromStream(image);
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }
}
