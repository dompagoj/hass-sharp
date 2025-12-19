using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace HassSharp;

public class ProjectReferences
{
    public static List<MetadataReference> GetDefaultReferences()
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();

        references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(JsonSerializer).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(Task).Assembly.Location));
        return references;
    }
}
