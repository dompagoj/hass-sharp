using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace HassSharp;

public sealed class VisualConflictException() : Exception("Revision conflict; reload the current document");
public sealed class VisualValidationException(VisualPreview preview) : Exception("Graph validation failed") { public VisualPreview Preview { get; } = preview; }
public sealed record VisualDocument(int Version, string Id, long Revision, JsonObject Graph, string Source, List<VisualSourceMap> SourceMap)
{
    public string? AutomationId { get; init; }
}

public sealed class VisualStore(string directory, VisualGenerator generator)
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static readonly ConcurrentDictionary<string, object> Gates = new();
    object Gate => Gates.GetOrAdd(Path.GetFullPath(directory), _ => new());
    string FilePath(string id)
    {
        if (!Regex.IsMatch(id, "^[a-z0-9][a-z0-9_-]{0,79}$")) throw new ArgumentException("Invalid visual ID");
        return Path.Combine(directory, id + ".json");
    }
    public VisualDocument? Read(string id)
    {
        lock (Gate)
        {
            var path = FilePath(id);
            return File.Exists(path) ? JsonSerializer.Deserialize<VisualDocument>(File.ReadAllText(path), Json) : null;
        }
    }
    public VisualDocument[] List()
    {
        lock (Gate)
        {
            var documents = new List<VisualDocument>();
            if (!Directory.Exists(directory)) return [];
            foreach (var path in Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal))
            {
                try
                {
                    var id = Path.GetFileNameWithoutExtension(path);
                    var document = Read(id);
                    if (document == null || document.Version != 1 || document.Id != id ||
                        document.Graph == null || document.Source == null || document.SourceMap == null || document.Revision < 1)
                        throw new JsonException("Invalid visual document envelope");
                    documents.Add(document);
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
                { Logger.Error($"Skipping visual document {path}: {ex}"); }
            }
            return documents.ToArray();
        }
    }
    public VisualDocument Save(string id, JsonObject graph, long expectedRevision)
    {
        lock (Gate)
        {
            var path = FilePath(id); var old = Read(id);
            if ((old?.Revision ?? 0) != expectedRevision) throw new VisualConflictException();
            var snapshot = (JsonObject)graph.DeepClone();
            var preview = generator.Preview(snapshot);
            if (!preview.Valid) throw new VisualValidationException(preview);
            var document = new VisualDocument(1, id, checked(expectedRevision + 1), snapshot, preview.Source, preview.SourceMap);
            Directory.CreateDirectory(directory);
            var temporary = path + "." + Guid.NewGuid() + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(file, document, Json); file.Flush(true); }
                File.Move(temporary, path, true);
            }
            finally { File.Delete(temporary); }
            return document;
        }
    }
    public void Delete(string id, long expectedRevision)
    {
        lock (Gate)
        {
            var old = Read(id) ?? throw new FileNotFoundException("Visual document not found");
            if (old.Revision != expectedRevision) throw new VisualConflictException();
            File.Delete(FilePath(id));
        }
    }
}
