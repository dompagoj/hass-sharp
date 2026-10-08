using System.Reflection;
using System.Runtime.Loader;

namespace HassSharp;

/// <summary>
/// Collectible load context for user automations and optional published DLLs.
/// </summary>
public sealed class ScriptLoadContext : AssemblyLoadContext
{
    static readonly Assembly HassSharpAssembly = typeof(Automation).Assembly;

    readonly AssemblyDependencyResolver _resolver;
    readonly Dictionary<string, string> _assemblyPathsByName;

    public ScriptLoadContext() : this(HassSharpAssembly.Location, [])
    {
    }

    public ScriptLoadContext(string basePath, IEnumerable<string> assemblyPaths) : base(true)
    {
        _resolver = new AssemblyDependencyResolver(basePath);
        _assemblyPathsByName = assemblyPaths
            .Select(p => (Path.GetFileNameWithoutExtension(p), p))
            .GroupBy(t => t.Item1, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Item2, StringComparer.OrdinalIgnoreCase);
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
        if (path != null)
        {
            return LoadFromAssemblyPath(path);
        }

        if (_assemblyPathsByName.TryGetValue(assemblyName.Name!, out var candidatePath))
        {
            return LoadFromAssemblyPath(candidatePath);
        }

        return null;
    }
}
