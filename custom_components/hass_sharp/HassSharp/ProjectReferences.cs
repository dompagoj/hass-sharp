using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace HassSharp;

public class ProjectReferences
{
    static ReadOnlyCollection<MetadataReference>? _cached;

    /// <summary>
    /// Assemblies HassSharp uses that user code is not compiled against, loaded or not: their types never appear in an
    /// API user code sees, and a copy user code loaded itself would not be the one HassSharp's types use.
    /// </summary>
    internal static bool Hidden(string? assemblyName) => assemblyName == "StbTrueTypeSharp";

    public static ReadOnlyCollection<MetadataReference> GetDefaultReferences()
    {
        if (_cached != null) return _cached;

        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location) && !Hidden(a.GetName().Name))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();

        references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(JsonSerializer).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(Task).Assembly.Location));
        _cached = references.AsReadOnly();
        return _cached;
    }
}
