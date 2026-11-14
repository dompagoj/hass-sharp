using System.Reflection;
using System.Runtime.Loader;

namespace HassSharp;

public class CompiledUserScript
{
    bool _unloaded;
    // Explicit metadata for visual artifacts; other compiled sources activate every Automation class they hold.
    public string? DesignatedRootType { get; set; }
    public long? VisualRevision { get; set; }
    public string? CatalogGeneration { get; set; }
    /// <summary>The saved on/off state the script starts with (see <see cref="UserScript.Enabled"/>).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>The visual document id (the editor's <c>/visual/:id</c>); null for a script compiled outside the visual store.</summary>
    public string? VisualId { get; set; }
    /// <summary>Trigger method name to its graph node id and label (null when the node has none).</summary>
    public IReadOnlyDictionary<string, VisualTrigger>? VisualTriggers { get; set; }
    /// <summary>Graph and pruned catalog JSON stored as the revision's snapshot with its first run.</summary>
    public string? VisualGraph { get; set; }
    public string? VisualCatalog { get; set; }

    public required string FilePath { get; set; }
    public required string FileName { get; set; }
    public required string SourceCode { get; init; }

    public required Assembly Assembly { get; init; }

    /// <summary>
    /// The load context <see cref="Assembly"/> is in, held for as long as the script is: a collectible context nothing
    /// references is finalised and starts unloading, after which it cannot load the typed service calls its code asks
    /// for on first use.
    /// </summary>
    public AssemblyLoadContext? LoadContext { get; init; }

    public string Id() => FilePath;

    public void Unload()
    {
        if (_unloaded) return;
        var loadContext = LoadContext ?? AssemblyLoadContext.GetLoadContext(Assembly);
        if (loadContext?.IsCollectible == true) loadContext.Unload();
        _unloaded = true;
    }
}

public sealed record VisualTrigger(string NodeId, string? Label);
