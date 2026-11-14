using System.Reflection;
using System.Runtime.Loader;

namespace HassSharp;

/// <summary>
/// Collectible load context for compiled automations.
/// </summary>
public sealed class ScriptLoadContext : AssemblyLoadContext
{
    static readonly Assembly HassSharpAssembly = typeof(Automation).Assembly;

    readonly AssemblyDependencyResolver _resolver = new(HassSharpAssembly.Location);

    public ScriptLoadContext() : base(true)
    {
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // User automations must use the host's HassSharp types. Loading another copy
        // would break type identity (for example, Automation.IsAssignableFrom()).
        if (AssemblyName.ReferenceMatchesDefinition(assemblyName, HassSharpAssembly.GetName()))
        {
            return HassSharpAssembly;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }
}
