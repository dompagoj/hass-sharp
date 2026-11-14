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
    public long? ActiveRevision { get; init; }
    public string? ActiveSourceHash { get; init; }
    public bool CurrentRevisionActive { get; init; }
    public string? CatalogGeneration { get; init; }
    /// <summary>
    /// Off: nothing starts the automation by itself (its triggers and startup run are withheld); a manual run still
    /// works. Stored with the document, not in the graph: a document without it is on, and saving keeps it.
    /// </summary>
    public bool Enabled { get; init; } = true;
    /// <summary>When the graph was last saved (null for a document saved before this was recorded); toggling never changes it.</summary>
    public DateTime? SavedUtc { get; init; }
    /// <summary>The pruned catalog the generated source embeds, for the run snapshot; derived, never stored.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? EmbeddedCatalog { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? Active { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ActivationError { get; init; }
}

public sealed class VisualStore(string directory, VisualGenerator generator)
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static readonly ConcurrentDictionary<string, object> Gates = new();
    object Gate => Gates.GetOrAdd(Path.GetFullPath(directory), _ => new());
    internal string FilePath(string id)
    {
        if (!Regex.IsMatch(id, "^[a-z0-9][a-z0-9_-]{0,79}$")) throw new ArgumentException("Invalid visual ID");
        return Path.Combine(directory, id + ".json");
    }
    public VisualDocument? Read(string id)
    {
        lock (Gate)
        {
            var path = FilePath(id);
            if (!File.Exists(path)) return null;
            var document = JsonSerializer.Deserialize<VisualDocument>(File.ReadAllText(path), Json);
            if (document == null || document.Version != 1 || document.Id != id ||
                document.Graph == null || document.Source == null || document.SourceMap == null || document.Revision < 1)
                throw new JsonException("Invalid visual document envelope");
            return document;
        }
    }
    public VisualDocument[] List(bool strict = false)
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
                { if (strict) throw; Logger.Error($"Skipping visual document {path}: {ex}"); }
            }
            return documents.ToArray();
        }
    }
    public VisualDocument Save(string id, JsonObject graph, long expectedRevision, VisualCatalogSnapshot? catalog = null, string? catalogGeneration = null)
    {
        lock (Gate)
        {
            var path = FilePath(id); var old = Read(id);
            if ((old?.Revision ?? 0) != expectedRevision) throw new VisualConflictException();
            var snapshot = VisualGraphContract.FillMissingLayout(graph);
            var preview = catalog == null ? generator.Preview(snapshot) : generator.Preview(snapshot, catalog);
            if (!preview.Valid) throw new VisualValidationException(preview);
            var document = new VisualDocument(1, id, checked(expectedRevision + 1), snapshot, preview.Source, preview.SourceMap)
                { CatalogGeneration = catalogGeneration, EmbeddedCatalog = preview.EmbeddedCatalog, Enabled = old?.Enabled ?? true, SavedUtc = DateTime.UtcNow };
            Directory.CreateDirectory(directory);
            Write(path, document);
            return document;
        }
    }
    // Startup regeneration: derived fields follow the current generator without bumping the revision.
    public bool RefreshDerived(string id, long expectedRevision, string source, List<VisualSourceMap> sourceMap, string? catalogGeneration)
    {
        lock (Gate)
        {
            var path = FilePath(id); var old = Read(id);
            if (old == null || old.Revision != expectedRevision) return false;
            if (old.Source == source && old.SourceMap.SequenceEqual(sourceMap) && old.CatalogGeneration == catalogGeneration) return false;
            Write(path, new VisualDocument(1, id, old.Revision, old.Graph, source, sourceMap) { CatalogGeneration = catalogGeneration, Enabled = old.Enabled, SavedUtc = old.SavedUtc });
            return true;
        }
    }
    /// <summary>Turns the automation on or off at its current revision; the graph, revision and save time stay as they are.</summary>
    public VisualDocument SetEnabled(string id, bool enabled)
    {
        lock (Gate)
        {
            var path = FilePath(id);
            var old = Read(id) ?? throw new FileNotFoundException("Visual document not found");
            if (old.Enabled == enabled) return old;
            var document = new VisualDocument(1, id, old.Revision, old.Graph, old.Source, old.SourceMap)
                { CatalogGeneration = old.CatalogGeneration, Enabled = enabled, SavedUtc = old.SavedUtc };
            Write(path, document);
            return document;
        }
    }
    static void Write(string path, VisualDocument document)
    {
        var temporary = path + "." + Guid.NewGuid() + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, document, Json); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }
    public void Delete(string id, long expectedRevision)
    {
        lock (Gate)
        {
            VisualDocument old;
            try { old = Read(id) ?? throw new FileNotFoundException("Visual document not found"); }
            catch (JsonException)
            {
                // Revision zero is an explicit recovery request, never a wildcard for valid documents.
                if (expectedRevision != 0) throw new VisualConflictException();
                File.Delete(FilePath(id));
                return;
            }
            if (old.Revision != expectedRevision) throw new VisualConflictException();
            File.Delete(FilePath(id));
        }
    }
}
