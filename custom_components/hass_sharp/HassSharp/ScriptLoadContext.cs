using System.Reflection;
using System.Runtime.Loader;

namespace HassSharp;

/// <summary>
/// Collectible load context for user automations and optional published DLLs.
/// </summary>
public sealed class ScriptLoadContext : AssemblyLoadContext
{
    readonly AssemblyDependencyResolver _resolver;
    readonly Dictionary<string, string> _assemblyPathsByName;

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