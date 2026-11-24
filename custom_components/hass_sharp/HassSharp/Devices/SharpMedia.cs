using System.Text.Json;
using System.Text.RegularExpressions;

namespace HassSharp;

/// <summary>A file a C# node keeps in Home Assistant's media folder (<see cref="SharpMedia"/>).</summary>
/// <param name="Name">Its name, such as <c>visitor.jpg</c>.</param>
/// <param name="Path">The path Home Assistant serves it at, such as <c>/media/local/hass_sharp/porch/door/visitor.jpg</c>.</param>
/// <param name="Size">How many bytes it takes.</param>
/// <param name="Written">When it was last written, in UTC.</param>
public sealed record SharpMediaFile(string Name, string Path, long Size, DateTimeOffset Written);

/// <summary>
/// The files a C# node keeps in Home Assistant's media folder, in its own folder
/// <c>&lt;media folder&gt;/hass_sharp/&lt;definition&gt;/&lt;node&gt;/</c>, which Home Assistant serves to anyone logged in to it at
/// <c>/media/&lt;entry&gt;/hass_sharp/&lt;definition&gt;/&lt;node&gt;/&lt;name&gt;</c>, such as the picture a phone notification
/// shows. Every save is on disk, whole, before it returns. A file takes at most 10 MiB and a node keeps at most 200. Every
/// call is safe from any thread, and atomic against every other on the node's folder. A name is 1 to 100 letters, digits,
/// <c>.</c>, <c>_</c> and <c>-</c>, does not start with a dot, and ends with an extension, such as <c>visitor.jpg</c>.
/// </summary>
public sealed class SharpMedia
{
    readonly MediaFolders owner;
    readonly MediaFolders.Node node;

    internal SharpMedia(MediaFolders owner, MediaFolders.Node node)
    {
        this.owner = owner;
        this.node = node;
    }

    /// <summary>Set once its definition has stopped, under the node's lock.</summary>
    internal bool Closed { get; set; }

    /// <summary>Writes a file, replacing the one of that name: to a temporary file, then renamed into place, so it is never half-written.</summary>
    /// <param name="name">The file's name, such as <c>visitor.jpg</c>.</param>
    /// <param name="content">What it holds, such as a JPEG.</param>
    /// <returns>The path Home Assistant serves it at, such as <c>/media/local/hass_sharp/porch/door/visitor.jpg</c>.</returns>
    /// <exception cref="ArgumentException">The name is not one, or the content is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Home Assistant has no media folder, the content takes more than 10 MiB, or the node would keep more than 200 files;
    /// nothing was written.
    /// </exception>
    /// <exception cref="IOException">The disk refused the file, such as when it is full; the old file is kept.</exception>
    /// <exception cref="UnauthorizedAccessException">The media folder cannot be written; the old file is kept.</exception>
    /// <exception cref="ObjectDisposedException">The definition has stopped.</exception>
    public string Save(string name, byte[] content)
    {
        MediaFolders.CheckName(name);
        if (content == null) throw new ArgumentException("The content of a media file cannot be null: remove the file instead");
        return owner.Save(this, node, name, content);
    }

    /// <summary>The path Home Assistant serves a file at, while the file is there.</summary>
    /// <returns>The path; null when there is no file of that name.</returns>
    /// <exception cref="ArgumentException">The name is not one.</exception>
    /// <exception cref="InvalidOperationException">Home Assistant has no media folder.</exception>
    /// <exception cref="ObjectDisposedException">The definition has stopped.</exception>
    public string? Path(string name)
    {
        MediaFolders.CheckName(name);
        return owner.PathOf(this, node, name);
    }

    /// <summary>Removes a file.</summary>
    /// <returns>Whether there was a file of that name.</returns>
    /// <exception cref="ArgumentException">The name is not one.</exception>
    /// <exception cref="InvalidOperationException">Home Assistant has no media folder.</exception>
    /// <exception cref="ObjectDisposedException">The definition has stopped.</exception>
    public bool Remove(string name)
    {
        MediaFolders.CheckName(name);
        return owner.Remove(this, node, name);
    }

    /// <summary>The node's files, newest first, then by name.</summary>
    /// <exception cref="InvalidOperationException">Home Assistant has no media folder.</exception>
    /// <exception cref="ObjectDisposedException">The definition has stopped.</exception>
    public IReadOnlyList<SharpMediaFile> Files() => owner.Files(this, node);

    /// <summary>Removes every file of the node.</summary>
    /// <exception cref="InvalidOperationException">Home Assistant has no media folder.</exception>
    /// <exception cref="ObjectDisposedException">The definition has stopped.</exception>
    public void Clear() => owner.Clear(this, node);

    /// <summary>Closes the handle once its definition has stopped: it refuses from now on, and no call of it is in progress.</summary>
    internal void Close() => owner.Close(this, node);
}

/// <summary>
/// The media files of one HassSharp instance's C# nodes, in Home Assistant's media folder, which Python names: its
/// <c>media_dirs</c> entry <c>local</c>, else the first by name. Each node's folder is
/// <c>&lt;folder&gt;/hass_sharp/&lt;definitionId&gt;/&lt;nodeKey&gt;/</c>; one <see cref="Node"/> serves every handle on it, so
/// that their calls, and a deletion, are atomic against each other. A file is written to a temporary file whose name starts
/// with a dot, which no file's name does, then renamed into place.
/// </summary>
internal sealed partial class MediaFolders
{
    /// <summary>The most a file may take: what the phone apps show at most.</summary>
    internal const long FileLimit = 10L * 1024 * 1024;
    /// <summary>The most files a node may keep.</summary>
    internal const int CountLimit = 200;
    /// <summary>What every call throws without a media folder.</summary>
    internal const string NoFolder = "Home Assistant has no media folder, so no media file can be kept";
    // What a write writes before it is renamed into place.
    const string Partial = ".partial";

    [GeneratedRegex(@"^(?=.{1,100}\z)[A-Za-z0-9_-][A-Za-z0-9._-]*\.[A-Za-z0-9_-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
    // A definition's id or a node's key: as safe in a URL's path as in a file name. A definition's id is
    // [a-z0-9][a-z0-9_-]{0,79} and a node's key [a-z][a-z0-9_]{0,39}, so this refuses none.
    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]{0,79}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    /// <summary>Where the files go: the entry's name, as Home Assistant serves it, and its folder.</summary>
    sealed record Place(string Entry, string Folder);

    volatile Place? place;
    // Guards the nodes; taken before a node's own lock, never inside one.
    readonly Lock gate = new();
    // Every node opened or deleted in this process, by "<definition>/<node>": kept, so that each has one lock for good.
    readonly Dictionary<string, Node> nodes = new(StringComparer.Ordinal);

    /// <summary>
    /// Takes Home Assistant's media folders, a JSON object of each entry's name to its folder, such as
    /// <c>{"local": "/media"}</c>; an entry without a folder is none. From now on the files are in the entry
    /// <c>local</c>, else in the first by name, and none without any.
    /// </summary>
    /// <exception cref="ArgumentException">The text is not such an object.</exception>
    internal void Set(string json)
    {
        Dictionary<string, string?>? entries;
        try { entries = JsonSerializer.Deserialize<Dictionary<string, string?>>(json ?? "null"); }
        catch (JsonException) { entries = null; }
        if (entries == null) throw new ArgumentException("Home Assistant's media folders are a JSON object of each entry's name to its folder");
        var usable = entries.Where(entry => !string.IsNullOrEmpty(entry.Key) && !string.IsNullOrWhiteSpace(entry.Value))
            .OrderBy(entry => entry.Key == "local" ? 0 : 1).ThenBy(entry => entry.Key, StringComparer.Ordinal).ToList();
        place = usable.Count == 0 ? null : new Place(usable[0].Key, System.IO.Path.GetFullPath(usable[0].Value!));
    }

    /// <summary>Opens the folder of a definition's C# node; every handle on one node shares its lock.</summary>
    /// <exception cref="ArgumentException">The definition's id or the node's key is not safe in a path.</exception>
    internal SharpMedia Open(string definitionId, string nodeKey)
    {
        if (!Safe(definitionId) || !Safe(nodeKey))
            throw new ArgumentException("A media folder's definition and node must each be lower-case letters, digits, '_' and '-', starting with a letter or a digit");
        lock (gate) return new SharpMedia(this, NodeOf(definitionId, nodeKey));
    }

    /// <summary>Deletes the files of every node of a definition, and its folder; nothing without a media folder.</summary>
    /// <exception cref="ArgumentException">The definition's id is not one.</exception>
    internal void DeleteDevice(string definitionId)
    {
        if (!Safe(definitionId)) throw new ArgumentException("A definition's id must be lower-case letters, digits, '_' and '-', starting with a letter or a digit");
        if (place is not { } where) return;
        var directory = System.IO.Path.Combine(where.Folder, "hass_sharp", definitionId);
        lock (gate)
        {
            foreach (var node in Nodes(where, definitionId)) Delete(where, node);
            // Left in place when a handle still open has written since.
            try { if (Directory.Exists(directory)) Directory.Delete(directory); }
            catch (IOException) { }
        }
    }

    /// <summary>Deletes the files of a definition's nodes that are not listed; nothing without a media folder.</summary>
    /// <exception cref="ArgumentException">The definition's id is not one.</exception>
    internal void PruneDevice(string definitionId, IReadOnlySet<string> nodeKeys)
    {
        if (!Safe(definitionId)) throw new ArgumentException("A definition's id must be lower-case letters, digits, '_' and '-', starting with a letter or a digit");
        if (place is not { } where) return;
        lock (gate)
            foreach (var node in Nodes(where, definitionId))
                if (!nodeKeys.Contains(node.Key)) Delete(where, node);
    }

    /// <exception cref="ArgumentException">The name is not one.</exception>
    internal static void CheckName(string name)
    {
        if (name == null || !NamePattern().IsMatch(name))
            throw new ArgumentException($"{(name == null ? "A null name" : $"'{name}'")} is not a media file's name: a name is 1 to 100 letters, digits, '.', '_' and '-', does not start with a dot, and ends with an extension, such as 'visitor.jpg'");
    }

    internal string Save(SharpMedia by, Node node, string name, byte[] content)
    {
        lock (node.Gate)
        {
            var (where, directory) = Ready(by, node);
            if (content.LongLength > FileLimit)
                throw new InvalidOperationException($"'{name}' takes {SharpFiles.Size(content.LongLength)}, more than the {SharpFiles.Size(FileLimit)} a media file may take");
            Sweep(directory);
            var target = System.IO.Path.Combine(directory, name);
            if (!File.Exists(target) && Names(directory).Count() >= CountLimit)
                throw new InvalidOperationException($"Saving '{name}' would make {CountLimit + 1} media files, more than the {CountLimit} a node may keep: remove some first");
            Directory.CreateDirectory(directory);
            var temporary = System.IO.Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + Partial);
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(content);
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, target, overwrite: true);
            }
            catch
            {
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
            return Served(where, node, name);
        }
    }

    internal string? PathOf(SharpMedia by, Node node, string name)
    {
        lock (node.Gate)
        {
            var (where, directory) = Ready(by, node);
            return File.Exists(System.IO.Path.Combine(directory, name)) ? Served(where, node, name) : null;
        }
    }

    internal bool Remove(SharpMedia by, Node node, string name)
    {
        lock (node.Gate)
        {
            var (_, directory) = Ready(by, node);
            var file = System.IO.Path.Combine(directory, name);
            if (!File.Exists(file)) return false;
            File.Delete(file);
            return true;
        }
    }

    internal IReadOnlyList<SharpMediaFile> Files(SharpMedia by, Node node)
    {
        lock (node.Gate)
        {
            var (where, directory) = Ready(by, node);
            List<SharpMediaFile> files = [];
            foreach (var name in Names(directory))
            {
                var info = new FileInfo(System.IO.Path.Combine(directory, name));
                // An administrator may delete a file in Home Assistant's media browser meanwhile.
                try { files.Add(new SharpMediaFile(name, Served(where, node, name), info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero))); }
                catch (FileNotFoundException) { }
            }
            return [.. files.OrderByDescending(file => file.Written).ThenBy(file => file.Name, StringComparer.Ordinal)];
        }
    }

    internal void Clear(SharpMedia by, Node node)
    {
        lock (node.Gate)
        {
            var (_, directory) = Ready(by, node);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    internal void Close(SharpMedia handle, Node node)
    {
        lock (node.Gate) handle.Closed = true;
    }

    // The place and the node's folder, once the handle is open and there is a media folder; under the node's lock.
    (Place Where, string Directory) Ready(SharpMedia by, Node node)
    {
        if (by.Closed) throw new ObjectDisposedException(null, "The definition has stopped: its media folder is closed");
        var where = place ?? throw new InvalidOperationException(NoFolder);
        return (where, System.IO.Path.Combine(where.Folder, "hass_sharp", node.Definition, node.Key));
    }

    static string Served(Place where, Node node, string name) =>
        $"/media/{Uri.EscapeDataString(where.Entry)}/hass_sharp/{node.Definition}/{node.Key}/{name}";

    // The node of a definition's key, made on first use; under the gate.
    Node NodeOf(string definitionId, string nodeKey)
    {
        var id = definitionId + "/" + nodeKey;
        if (!nodes.TryGetValue(id, out var node)) nodes[id] = node = new Node(definitionId, nodeKey);
        return node;
    }

    // A definition's nodes with a folder on disk or opened in this process; under the gate.
    IEnumerable<Node> Nodes(Place where, string definitionId)
    {
        var directory = System.IO.Path.Combine(where.Folder, "hass_sharp", definitionId);
        var onDisk = Directory.Exists(directory) ? Directory.EnumerateDirectories(directory).Select(path => System.IO.Path.GetFileName(path)).Where(Safe) : [];
        var opened = nodes.Values.Where(node => node.Definition == definitionId).Select(node => node.Key);
        return onDisk.Union(opened, StringComparer.Ordinal).Select(key => NodeOf(definitionId, key)).ToList();
    }

    // Deletes a node's folder under its lock, so that no call of a handle on it lands in between; under the gate.
    static void Delete(Place where, Node node)
    {
        lock (node.Gate)
        {
            var directory = System.IO.Path.Combine(where.Folder, "hass_sharp", node.Definition, node.Key);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    // The files of a node's folder that are files: a name's, never a write's temporary file.
    static IEnumerable<string> Names(string directory) =>
        Directory.Exists(directory) ? Directory.EnumerateFiles(directory).Select(file => System.IO.Path.GetFileName(file)).Where(name => NamePattern().IsMatch(name)) : [];

    // Removes what a write that did not finish left; under the node's lock, so no write of this process is under way.
    static void Sweep(string directory)
    {
        foreach (var leftover in Directory.Exists(directory) ? Directory.EnumerateFiles(directory, ".*" + Partial) : [])
            try { File.Delete(leftover); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static bool Safe(string id) => id != null && IdPattern().IsMatch(id);

    /// <summary>One node's folder: its definition, its key, and the lock every handle on it takes.</summary>
    internal sealed class Node(string definition, string key)
    {
        internal string Definition { get; } = definition;
        internal string Key { get; } = key;
        internal readonly Lock Gate = new();
    }
}
