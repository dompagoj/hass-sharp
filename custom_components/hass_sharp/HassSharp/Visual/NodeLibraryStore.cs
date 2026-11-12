using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HassSharp;

public sealed record NodeLibraryDocument(int Version, string Id, string Name, long Revision, string Source)
{
    public NodeLibrarySource AsSource() => new(Id, Source, Revision);
}

/// <summary>All access belongs to the manager authoring gate, including recovery.</summary>
public sealed class NodeLibraryStore(string directory)
{
    public string DirectoryPath => Path.GetFullPath(directory);
    public string DocumentPath(string id)
    {
        ValidateId(id);
        return Path.Combine(DirectoryPath, id + ".json");
    }
    public static void ValidateId(string id)
    {
        if (!Regex.IsMatch(id, "^[a-z0-9][a-z0-9_-]{0,79}$")) throw new ArgumentException("Invalid library ID");
    }
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120 || name.IndexOfAny(['/', '\\', '\0', '\r', '\n']) >= 0)
            throw new ArgumentException("Invalid library name");
    }
    public NodeLibraryDocument? Read(string id)
    {
        var path = DocumentPath(id);
        if (!File.Exists(path)) return null;
        var d = JsonSerializer.Deserialize<NodeLibraryDocument>(File.ReadAllText(path), VisualStore.Json);
        if (d == null || d.Version != 1 || d.Id != id || d.Revision < 1 || d.Source == null) throw new JsonException("Invalid library document envelope");
        ValidateName(d.Name);
        return d;
    }
    public NodeLibraryDocument[] List() => !Directory.Exists(DirectoryPath) ? [] : Directory.GetFiles(DirectoryPath, "*.json")
        .Order(StringComparer.Ordinal).Select(p => Read(Path.GetFileNameWithoutExtension(p))!).ToArray();
    public static string Generation(IEnumerable<NodeLibraryDocument> documents) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(documents.OrderBy(d => d.Id, StringComparer.Ordinal), VisualStore.Json)))).ToLowerInvariant();
}

/// <summary>
/// Durable undo journal: journal removal is the commit point. An interrupted/failed
/// transaction restores every old file before any subsequent authoring/startup read.
/// Activation runs only after commit and is deliberately not part of the transaction.
/// </summary>
internal sealed class AuthoringTransaction(string root)
{
    string Journal => Path.Combine(root, "authoring-undo.json");
    public void Recover()
    {
        if (!File.Exists(Journal)) return;
        var old = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(Journal))
            ?? throw new IOException("Invalid authoring recovery journal");
        Restore(old);
        File.Delete(Journal);
    }
    void Restore(Dictionary<string, string?> old)
    {
        foreach (var (relative, content) in old)
        {
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new IOException("Invalid authoring recovery path");
            if (content == null) File.Delete(path); else AtomicWrite(path, content);
        }
    }
    public void Commit(IReadOnlyDictionary<string, string?> changes)
    {
        Recover();
        var old = changes.Keys.ToDictionary(p => Path.GetRelativePath(root, p), p => File.Exists(p) ? File.ReadAllText(p) : null);
        // Stage every replacement first, so ordinary storage errors leave no journal or changed files.
        var staged = new Dictionary<string, string>();
        try
        {
            foreach (var (path, content) in changes)
            {
                if (content == null) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                staged.Add(path, temp);
                WriteFlushed(temp, content);
            }
            AtomicWrite(Journal, JsonSerializer.Serialize(old));
            try
            {
                foreach (var (path, content) in changes)
                    if (content == null) File.Delete(path); else File.Move(staged[path], path, true);
                File.Delete(Journal);
            }
            catch
            {
                // If rollback itself fails, retain the journal and fail closed on future reads.
                Recover();
                throw;
            }
        }
        finally { foreach (var temp in staged.Values) File.Delete(temp); }
    }
    internal static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { WriteFlushed(temp, content); File.Move(temp, path, true); }
        finally { File.Delete(temp); }
    }
    static void WriteFlushed(string path, string content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes); stream.Flush(true);
    }
}
