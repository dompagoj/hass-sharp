using System.Text;
using System.Text.RegularExpressions;

namespace HassSharp;

/// <summary>A component that opens data stores: its definition, its C# node, and how a <see cref="SharpData.Changed"/> action of its runs.</summary>
/// <param name="DefinitionId">The definition's id.</param>
/// <param name="NodeKey">The key of the component's C# node.</param>
/// <param name="Run">Runs a <c>Changed</c> action as a track callback runs, reporting its failure itself.</param>
internal sealed record DataUser(string DefinitionId, string NodeKey, Func<Func<Task>, Task> Run);

/// <summary>A shared store as the Stored data dialog lists it.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Bytes">What its values take.</param>
/// <param name="Users">The ids of the definitions that have it open, in ordinal order, each once.</param>
internal sealed record SharedStoreInfo(string Name, long Bytes, IReadOnlyList<string> Users);

/// <summary>
/// The data stores of one HassSharp instance, under <c>.storage/hass_sharp/data/</c>: a store private to each C# node of
/// a definition, in <c>devices/&lt;definitionId&gt;/&lt;nodeKey&gt;/</c>, and the shared ones, in <c>shared/&lt;name&gt;/</c>.
/// A store is a file per key, named by the key with <c>+</c> for each <c>/</c>, holding one byte for its kind and then the
/// value. One <see cref="Store"/> serves every handle open on a directory, so their operations are atomic against each other.
/// </summary>
internal sealed partial class DataStores
{
    /// <summary>The most a value may take; settable for tests.</summary>
    internal static long ValueLimit { get; set; } = 8L * 1024 * 1024;
    /// <summary>The most a store's values may take together; settable for tests.</summary>
    internal static long StoreLimit { get; set; } = 512L * 1024 * 1024;

    // The first byte of a value's file: what follows is JSON, or bytes.
    internal const byte Json = (byte)'j';
    internal const byte Bytes = (byte)'b';
    // What a write writes before it is renamed into place; a key's file name never starts with a dot.
    const string Partial = ".partial";

    [GeneratedRegex(@"^(?=.{1,200}\z)[a-z0-9][a-z0-9_.-]*(/[a-z0-9][a-z0-9_.-]*)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
    [GeneratedRegex(@"^[a-z0-9][a-z0-9_.-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SharedPattern();

    readonly string devices;
    readonly string shared;
    // Guards the open stores and who opened which handle; taken before a store's own lock, never inside one.
    readonly Lock gate = new();
    // The stores that have a handle open, by directory.
    readonly Dictionary<string, Store> stores = new(StringComparer.Ordinal);
    // The handles each user opened, until it is closed; by reference, since an old and a new revision's users are equal records.
    readonly Dictionary<DataUser, List<SharpData>> opened = new(ReferenceEqualityComparer.Instance);
    // Guards stored; taken inside a store's lock, and nothing is taken inside it.
    readonly Lock storedGate = new();
    // Each definition's nodes whose store holds a value.
    readonly SortedDictionary<string, SortedSet<string>> stored = new(StringComparer.Ordinal);

    /// <param name="root">The directory the stores are under; nothing is written to it until a value is.</param>
    internal DataStores(string root)
    {
        devices = Path.Combine(root, "devices");
        shared = Path.Combine(root, "shared");
        foreach (var definition in Subdirectories(devices))
        foreach (var node in Subdirectories(Path.Combine(devices, definition)))
            if (Scan(Path.Combine(devices, definition, node)).Any()) Holds(definition, node, true);
    }

    /// <summary>Opens the store private to the user's C# node; the same directory as every earlier handle of that node.</summary>
    /// <exception cref="ArgumentException">The definition's id or the node's key is not a plain directory name.</exception>
    internal SharpData OpenDevice(DataUser user)
    {
        if (!Plain(user.DefinitionId) || !Plain(user.NodeKey)) throw new ArgumentException("A device's definition and node must each be a plain directory name");
        return Open(Path.Combine(devices, user.DefinitionId, user.NodeKey), (user.DefinitionId, user.NodeKey), null, user);
    }

    /// <summary>Opens the store shared by every component that names it.</summary>
    /// <exception cref="ArgumentException">The name is not a shared store's.</exception>
    internal SharpData OpenShared(string name, DataUser user)
    {
        CheckShared(name);
        return Open(Path.Combine(shared, name), null, name, user);
    }

    /// <summary>Closes every handle the user opened: each throws <see cref="ObjectDisposedException"/> after, and its <c>Changed</c> actions run no more.</summary>
    internal void Close(DataUser user)
    {
        List<SharpData>? handles;
        lock (gate)
            if (!opened.Remove(user, out handles)) return;
        // Marked closed before they stop counting as open, so a store is never deleted under a handle that may still write.
        foreach (var handle in handles) handle.Store.Close(handle);
        lock (gate)
            foreach (var handle in handles)
                if (handle.Store.Handles.Remove(handle) && handle.Store.Handles.Count == 0) stores.Remove(handle.Store.Location);
    }

    /// <summary>Deletes the stores of every node of a definition, also of nodes whose handles are open: those then hold nothing.</summary>
    internal void DeleteDevice(string definitionId)
    {
        if (!Plain(definitionId)) throw new ArgumentException("A definition's id must be a plain directory name");
        var directory = Path.Combine(devices, definitionId);
        lock (gate)
        {
            foreach (var node in Nodes(definitionId)) DeleteNode(definitionId, node);
            // Left in place when a node's open handle has written since.
            try { if (Directory.Exists(directory)) Directory.Delete(directory); }
            catch (IOException) { }
        }
    }

    /// <summary>Deletes the stores of a definition's nodes that are not listed, also of nodes whose handles are open: those then hold nothing.</summary>
    internal void PruneDevice(string definitionId, IReadOnlySet<string> nodeKeys)
    {
        if (!Plain(definitionId)) throw new ArgumentException("A definition's id must be a plain directory name");
        lock (gate)
            foreach (var node in Nodes(definitionId))
                if (!nodeKeys.Contains(node)) DeleteNode(definitionId, node);
    }

    /// <summary>Each definition whose C# nodes hold data, with those nodes' keys, in ordinal order: kept in memory, never read from disk.</summary>
    internal IReadOnlyDictionary<string, IReadOnlyList<string>> Stored()
    {
        lock (storedGate)
            return stored.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<string>)[.. entry.Value], StringComparer.Ordinal);
    }

    /// <summary>Every shared store, on disk or open, in ordinal order of name.</summary>
    internal IReadOnlyList<SharedStoreInfo> Shared()
    {
        lock (gate)
        {
            var open = stores.Values.Where(store => store.SharedName != null).ToDictionary(store => store.SharedName!, StringComparer.Ordinal);
            return Subdirectories(shared).Where(name => SharedPattern().IsMatch(name)).Union(open.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Select(name => open.TryGetValue(name, out var store)
                    ? new SharedStoreInfo(name, store.Size, store.Handles.Select(handle => handle.User.DefinitionId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList())
                    : new SharedStoreInfo(name, Scan(Path.Combine(shared, name)).Sum(value => value.Size), []))
                .ToList();
        }
    }

    /// <summary>Deletes a shared store and its directory.</summary>
    /// <returns>False, deleting nothing, while a handle on it is open; true once it is gone, also when there was none.</returns>
    /// <exception cref="ArgumentException">The name is not a shared store's.</exception>
    internal bool DeleteShared(string name)
    {
        CheckShared(name);
        var directory = Path.Combine(shared, name);
        lock (gate)
        {
            if (stores.ContainsKey(directory)) return false;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            return true;
        }
    }

    /// <exception cref="ArgumentException">The key is not one.</exception>
    internal static void CheckKey(string key)
    {
        if (key == null || !KeyPattern().IsMatch(key))
            throw new ArgumentException($"{(key == null ? "A null key" : $"'{key}'")} is not a key: a key is 1 to 200 characters of segments joined by '/', each of lower-case letters, digits, '_', '.' and '-', starting with a letter or a digit");
    }

    static void CheckShared(string name)
    {
        if (name == null || !SharedPattern().IsMatch(name))
            throw new ArgumentException($"{(name == null ? "A null name" : $"'{name}'")} is not a shared store's name: it is 1 to 64 lower-case letters, digits, '_', '.' and '-', starting with a letter or a digit");
    }

    SharpData Open(string directory, (string Definition, string Node)? device, string? sharedName, DataUser user)
    {
        lock (gate)
        {
            if (!stores.TryGetValue(directory, out var store)) stores[directory] = store = new Store(this, directory, device, sharedName);
            var handle = new SharpData(store, user);
            store.Handles.Add(handle);
            if (!opened.TryGetValue(user, out var handles)) opened[user] = handles = [];
            handles.Add(handle);
            return handle;
        }
    }

    // A definition's nodes on disk, open or remembered as holding data; under the gate.
    IEnumerable<string> Nodes(string definitionId)
    {
        List<string> remembered;
        lock (storedGate) remembered = stored.TryGetValue(definitionId, out var nodes) ? [.. nodes] : [];
        return Subdirectories(Path.Combine(devices, definitionId))
            .Union(stores.Values.Where(store => store.Device?.Definition == definitionId).Select(store => store.Device!.Value.Node), StringComparer.Ordinal)
            .Union(remembered, StringComparer.Ordinal)
            .ToList();
    }

    // Deletes a node's store, through its Store when a handle is open so that no write of one lands in between; under the gate.
    void DeleteNode(string definitionId, string node)
    {
        var directory = Path.Combine(devices, definitionId, node);
        if (stores.TryGetValue(directory, out var store)) store.Wipe();
        else
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            Holds(definitionId, node, false);
        }
    }

    // Remembers whether a node's store holds a value.
    void Holds(string definitionId, string node, bool holds)
    {
        lock (storedGate)
        {
            if (holds)
            {
                if (!stored.TryGetValue(definitionId, out var nodes)) stored[definitionId] = nodes = new(StringComparer.Ordinal);
                nodes.Add(node);
            }
            else if (stored.TryGetValue(definitionId, out var nodes) && nodes.Remove(node) && nodes.Count == 0) stored.Remove(definitionId);
        }
    }

    static IEnumerable<string> Subdirectories(string directory) =>
        Directory.Exists(directory) ? Directory.EnumerateDirectories(directory).Select(path => Path.GetFileName(path)).Where(Plain) : [];

    // The files of a store's directory that are values: not a write's temporary file.
    static IEnumerable<string> ValueFiles(string directory) =>
        Directory.Exists(directory) ? Directory.EnumerateFiles(directory).Where(file => Path.GetFileName(file)[0] != '.') : [];

    // The values of a store's directory, by key, with what each takes; files that are no key's are not values.
    static IEnumerable<(string Key, long Size)> Scan(string directory) =>
        ValueFiles(directory)
            .Select(file => (Key: Path.GetFileName(file).Replace('+', '/'), Size: Math.Max(0, new FileInfo(file).Length - 1)))
            .Where(value => KeyPattern().IsMatch(value.Key));

    // A directory name without a separator: not empty, not hidden (so never "." or ".."), no control character, and at
    // most the 255 bytes a Linux file name takes.
    static bool Plain(string name) =>
        !string.IsNullOrEmpty(name) && Encoding.UTF8.GetByteCount(name) <= 255 && name[0] != '.'
        && name.IndexOfAny(['/', '\\', ':']) < 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.Any(char.IsControl);

    /// <summary>
    /// One store's directory, served to every handle open on it: its operations take its lock, so each is atomic against
    /// the others, and it knows its keys and their sizes, read from the directory once when it opens.
    /// </summary>
    internal sealed class Store
    {
        readonly DataStores owner;
        // Guards the values, the files and Closed of its handles; a write holds it for its read, change and rename.
        readonly Lock gate = new();
        readonly SortedDictionary<string, long> sizes = new(StringComparer.Ordinal);
        long size;
        // Guards the subscriptions; taken inside gate or alone, and nothing is taken inside it.
        readonly Lock listening = new();
        readonly List<Subscription> subscriptions = [];

        internal Store(DataStores owner, string directory, (string Definition, string Node)? device, string? sharedName)
        {
            this.owner = owner;
            Location = directory;
            Device = device;
            SharedName = sharedName;
            Load();
        }

        internal string Location { get; }
        internal (string Definition, string Node)? Device { get; }
        internal string? SharedName { get; }
        /// <summary>Its open handles; under the stores' gate.</summary>
        internal List<SharpData> Handles { get; } = [];
        /// <summary>What its values take together.</summary>
        internal long Size => Interlocked.Read(ref size);

        // The file of a value: its kind's byte, then the value; null when the key has none.
        internal byte[]? Read(SharpData by, string key, byte kind)
        {
            byte[]? stored;
            lock (gate)
            {
                CheckOpen(by);
                stored = ReadFile(key);
            }
            return Checked(key, stored, kind);
        }

        internal void Write(SharpData by, string key, byte kind, byte[] value)
        {
            Subscription[] others;
            lock (gate)
            {
                CheckOpen(by);
                Put(key, kind, value);
                others = Others(by);
            }
            Signal(others);
        }

        // Reads the JSON value's file, gives it to the change and writes the JSON it returns, holding the store throughout.
        internal void Update(SharpData by, string key, Func<byte[]?, byte[]> change)
        {
            Subscription[] others;
            lock (gate)
            {
                CheckOpen(by);
                Put(key, Json, change(Checked(key, ReadFile(key), Json)));
                others = Others(by);
            }
            Signal(others);
        }

        internal bool Remove(SharpData by, string key)
        {
            Subscription[] others;
            lock (gate)
            {
                CheckOpen(by);
                if (!sizes.TryGetValue(key, out var removed)) return false;
                File.Delete(PathOf(key));
                sizes.Remove(key);
                Interlocked.Add(ref size, -removed);
                Holding();
                others = Others(by);
            }
            Signal(others);
            return true;
        }

        internal IReadOnlyList<string> Keys(SharpData by, string prefix)
        {
            lock (gate)
            {
                CheckOpen(by);
                return sizes.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            }
        }

        internal void Clear(SharpData by)
        {
            Subscription[] others;
            lock (gate)
            {
                CheckOpen(by);
                var had = sizes.Count > 0;
                Wipe();
                if (!had) return;
                others = Others(by);
            }
            Signal(others);
        }

        internal IDisposable Subscribe(SharpData by, Func<Task> action)
        {
            lock (gate)
            {
                CheckOpen(by);
                var subscription = new Subscription(this, by, action);
                lock (listening) subscriptions.Add(subscription);
                return subscription;
            }
        }

        // Closes a handle: it refuses from now on, no operation of it is in progress, and its subscriptions have ended.
        internal void Close(SharpData handle)
        {
            lock (gate)
            {
                handle.Closed = true;
                lock (listening)
                    foreach (var subscription in subscriptions.Where(subscription => subscription.Handle == handle).ToList()) End(subscription);
            }
        }

        // Deletes the directory and every value in it, whoever's handle is open; the next write makes it again.
        internal void Wipe()
        {
            lock (gate)
            {
                try { if (Directory.Exists(Location)) Directory.Delete(Location, recursive: true); }
                // What could not be deleted is what the store still holds.
                finally { Load(); }
            }
        }

        internal void Unsubscribe(Subscription subscription)
        {
            lock (listening) End(subscription);
        }

        // Under listening.
        void End(Subscription subscription)
        {
            subscriptions.Remove(subscription);
            subscription.End();
        }

        static void CheckOpen(SharpData by)
        {
            if (by.Closed) throw new ObjectDisposedException(null, "The definition has stopped: its data stores are closed");
        }

        // Reads the directory: removes what a write that did not finish left, and takes every value's size; under gate.
        void Load()
        {
            sizes.Clear();
            foreach (var leftover in Directory.Exists(Location) ? Directory.EnumerateFiles(Location, ".*" + Partial) : [])
                try { File.Delete(leftover); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            foreach (var (key, valueSize) in Scan(Location)) sizes[key] = valueSize;
            Interlocked.Exchange(ref size, sizes.Values.Sum());
            Holding();
        }

        // The file of a key's value, null when it has none; under gate.
        byte[]? ReadFile(string key)
        {
            if (!sizes.ContainsKey(key)) return null;
            try { return File.ReadAllBytes(PathOf(key)); }
            catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException) { return null; }
        }

        static byte[]? Checked(string key, byte[]? stored, byte kind)
        {
            if (stored == null) return null;
            if (stored.Length == 0 || stored[0] is not (Json or Bytes)) throw new InvalidDataException($"The stored value of '{key}' is damaged: write it again");
            if (stored[0] != kind)
                throw new InvalidOperationException(kind == Json
                    ? $"'{key}' holds bytes, not a JSON value: read it with GetBytes"
                    : $"'{key}' holds a JSON value, not bytes: read it with Get");
            return stored;
        }

        // Writes a value: to a temporary file beside its own, flushed to disk, then renamed into place, so a crash never
        // leaves a half-written value; a write that fails leaves the old value and no temporary file. Under gate.
        void Put(string key, byte kind, byte[] value)
        {
            if (value.LongLength > ValueLimit)
                throw new InvalidOperationException($"The value for '{key}' takes {SharpFiles.Size(value.LongLength)}, more than the {SharpFiles.Size(ValueLimit)} a stored value may take");
            var after = Size - sizes.GetValueOrDefault(key) + value.LongLength;
            if (after > StoreLimit)
                throw new InvalidOperationException($"Writing '{key}' would take its store past the {SharpFiles.Size(StoreLimit)} a store may take");
            Directory.CreateDirectory(Location);
            var temporary = Path.Combine(Location, "." + Guid.NewGuid().ToString("N") + Partial);
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.WriteByte(kind);
                    file.Write(value);
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, PathOf(key), overwrite: true);
            }
            catch
            {
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
            sizes[key] = value.LongLength;
            Interlocked.Exchange(ref size, after);
            Holding();
        }

        string PathOf(string key) => Path.Combine(Location, key.Replace('/', '+'));

        // Tells the stores whether a device's store holds a value; under gate.
        void Holding()
        {
            if (Device is var (definition, node)) owner.Holds(definition, node, sizes.Count > 0);
        }

        // The subscriptions of another component's handles: of every user but the writer's, by reference; under gate.
        Subscription[] Others(SharpData by)
        {
            lock (listening) return subscriptions.Where(subscription => !ReferenceEquals(subscription.Handle.User, by.User)).ToArray();
        }

        static void Signal(Subscription[] subscriptions)
        {
            foreach (var subscription in subscriptions) subscription.Signal();
        }
    }

    /// <summary>
    /// A <c>Changed</c> action: run through its user's <see cref="DataUser.Run"/> after another handle changed the store,
    /// one run at a time; changes while it runs cause one more run. Once it has ended (disposed, or its handle closed), no
    /// run begins: each is decided under its lock just before it runs, and one decided before may finish.
    /// </summary>
    internal sealed class Subscription(Store store, SharpData handle, Func<Task> action) : IDisposable
    {
        readonly Lock gate = new();
        bool running, again, ended;

        internal SharpData Handle => handle;
        /// <summary>Whether a run is waiting to begin or under way; for tests, which wait for one in flight to end.</summary>
        internal bool Running { get { lock (gate) return running; } }

        internal void Signal()
        {
            lock (gate)
            {
                if (ended) return;
                if (running) { again = true; return; }
                running = true;
            }
            // Off the writer's thread, so that neither the action nor its user's Run holds the writer up.
            _ = Task.Run(Runs);
        }

        async Task Runs()
        {
            while (true)
            {
                // Decided here, under the lock that ending takes: a run queued, or owed for a change during the last one,
                // does not begin once the subscription has ended.
                lock (gate)
                    if (ended)
                    {
                        running = false;
                        return;
                    }
                // The user's Run reports a failure of the action; a failure of the Run itself is the user's too, and
                // neither reaches the store or the writer.
                try { await handle.User.Run(action); }
                catch (Exception) { }
                lock (gate)
                {
                    if (!again) { running = false; return; }
                    again = false;
                }
            }
        }

        internal void End()
        {
            lock (gate) ended = true;
        }

        public void Dispose() => store.Unsubscribe(this);
    }
}
