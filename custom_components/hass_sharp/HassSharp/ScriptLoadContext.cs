using System.Reflection;
using System.Runtime.Loader;

namespace HassSharp;

/// <summary>
/// Collectible load context for compiled automations, one per automation. The typed service calls and the entity classes
/// it was compiled against are loaded into it, from their bytes, the first time its code needs each; they go when it is
/// unloaded.
/// </summary>
public sealed class ScriptLoadContext : AssemblyLoadContext
{
    static readonly Assembly HassSharpAssembly = typeof(Automation).Assembly;

    readonly AssemblyDependencyResolver _resolver = new(HassSharpAssembly.Location);

    // The generated assemblies by name: only their names and bytes are kept, not the compiler's metadata.
    readonly (string Name, byte[] Image)[] _generated = [];

    public ScriptLoadContext() : base(true)
    {
    }

    /// <param name="serviceCalls">The generated code the automation was compiled against: the typed service calls and the
    /// entity classes they carry.</param>
    public ScriptLoadContext(GeneratedServiceCalls serviceCalls) : base(true)
    {
        _generated = [(serviceCalls.AssemblyName, serviceCalls.Image), (serviceCalls.Entities.AssemblyName, serviceCalls.Entities.Image)];
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // User automations must use the host's HassSharp types. Loading another copy
        // would break type identity (for example, Automation.IsAssignableFrom()).
        if (AssemblyName.ReferenceMatchesDefinition(assemblyName, HassSharpAssembly.GetName()))
        {
            return HassSharpAssembly;
        }

        foreach (var (name, bytes) in _generated)
            if (assemblyName.Name == name)
            {
                using var image = new MemoryStream(bytes, writable: false);
                return LoadFromStream(image);
            }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }
}
