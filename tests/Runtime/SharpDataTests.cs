using System.Text;
using HassSharp;

/// <summary>
/// The stores of device data on their own, each test over a temporary directory: what one instance writes another
/// reads back, a JSON value or bytes, under keys in ordinal order; names that are not ones are refused before the disk is
/// touched; a key holds one kind; a damaged value is named and leaves the others readable; a failed write and a write over
/// a limit keep the old value and leave no temporary file; a crash's leftovers are removed; two handles that update one
/// key lose nothing; another handle's write runs a handle's <c>Changed</c> actions, coalesced, and its own never; a closed
/// handle refuses everything; a shared store is not deleted while open; and a device's data is deleted and pruned, as the
/// stores remember it.
/// </summary>
static class SharpDataTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    static T Throws<T>(Action action, string what) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        catch (Exception error) { throw new Exception($"{what}: threw {error.GetType().Name} instead of {typeof(T).Name}: {error.Message}"); }
        throw new Exception($"{what}: did not throw {typeof(T).Name}");
    }

    sealed record Person(string Name, int Age);

    [RuntimeSuite("--sharp-data", Order = 41)]
    public static async Task Run()
    {
        var (valueLimit, storeLimit) = (DataStores.ValueLimit, DataStores.StoreLimit);
        Check(valueLimit == 8L * 1024 * 1024 && storeLimit == 512L * 1024 * 1024, $"the limits are {valueLimit} and {storeLimit}");
        try
        {
            ValuesComeBack();
            RemoveAndClear();
            BadNamesAreRefused();
            AKeyHoldsOneKind();
            ACorruptValueIsNamed();
            AFailedWriteKeepsTheOldValue();
            TheStoreLimitHolds();
            LeftoversAreRemoved();
            await UpdateDoesNotLoseWrites();
            await ChangedReachesTheOthersOnly();
            await ADisposedSubscriptionOwesNoRun();
            await ChangedSkipsTheWritersOtherHandles();
            await ClosedHandlesRefuse();
            await EqualUsersAreTwo();
            DeleteSharedIsRefusedWhileOpen();
            DeviceDataIsDeletedAndPruned();
        }
        finally
        {
            DataStores.ValueLimit = valueLimit;
            DataStores.StoreLimit = storeLimit;
        }
    }

    // A temporary directory for the stores' root, removed afterwards.
    sealed class Scratch : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "sharp-data-" + Guid.NewGuid().ToString("N"));
        public string Device(string definition, string node) => Path.Combine(Root, "devices", definition, node);
        public string Shared(string name) => Path.Combine(Root, "shared", name);
        // Every file and directory under the root, relative to it, in ordinal order.
        public string[] Entries() => Directory.Exists(Root)
            ? Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories).Select(entry => Path.GetRelativePath(Root, entry)).Order(StringComparer.Ordinal).ToArray()
            : [];
        public string[] Files(string directory) => Directory.Exists(directory) ? Directory.GetFiles(directory).Select(file => Path.GetFileName(file)).Order(StringComparer.Ordinal).ToArray() : [];
        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            if (!OperatingSystem.IsWindows())
                foreach (var directory in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories))
                    File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(Root, recursive: true);
        }
    }

    // A user that runs a Changed action off the writer's thread, as the host will.
    static DataUser User(string definition, string node = "node") => new(definition, node, action => Task.Run(action));

    static async Task Until(Func<bool> condition, string what)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Check(clock.Elapsed < TimeSpan.FromSeconds(5), "timed out waiting: " + what);
            await Task.Delay(10);
        }
    }

    /// <summary>A JSON value and bytes come back from a second instance on the same root, as after a restart.</summary>
    static void ValuesComeBack()
    {
        using var scratch = new Scratch();
        var data = new DataStores(scratch.Root).OpenDevice(User("def"));
        byte[] bytes = [0, 1, 2, 254, 255];
        data.Set("people/ivan", new Person("Ivan", 41));
        data.SetBytes("crops/1", bytes);
        Check(data.Get<Person>("people/ivan") == new Person("Ivan", 41), "read back at once");
        Check(Directory.Exists(scratch.Device("def", "node")), "the device's directory is " + string.Join(", ", scratch.Entries()));

        var again = new DataStores(scratch.Root).OpenDevice(User("def"));
        Check(again.Get<Person>("people/ivan") == new Person("Ivan", 41), "the record after a restart");
        Check(again.GetBytes("crops/1") is { } read && read.SequenceEqual(bytes), "the bytes after a restart");
        Check(again.Keys().SequenceEqual(["crops/1", "people/ivan"]), "keys: " + string.Join(", ", again.Keys()));
        Check(again.Keys("people/").SequenceEqual(["people/ivan"]), "keys by prefix: " + string.Join(", ", again.Keys("people/")));
        Check(again.Keys("nobody/").Count == 0, "keys of a prefix nothing has");
        Check(again.Get<Person>("people/ana") == null && again.Get<int>("count") == 0 && again.GetBytes("crops/2") == null, "a key without a value reads as nothing");
        // Another node's and another definition's stores are their own.
        Check(new DataStores(scratch.Root).OpenDevice(User("def", "other")).Keys().Count == 0, "another node sees this node's keys");
        Check(new DataStores(scratch.Root).OpenDevice(User("other")).Keys().Count == 0, "another definition sees this definition's keys");
        Console.WriteLine("PASS sharp data: a JSON value and bytes come back after a restart, under keys in ordinal order");
    }

    /// <summary>Remove answers whether there was a value; Clear leaves no keys and no files.</summary>
    static void RemoveAndClear()
    {
        using var scratch = new Scratch();
        var data = new DataStores(scratch.Root).OpenDevice(User("def"));
        data.Set("a", 1);
        data.SetBytes("b/c", [1, 2]);
        data.Set("d", "text");
        Check(data.Remove("a"), "a value was there");
        Check(!data.Remove("a"), "a value removed twice");
        Check(!data.Remove("never"), "a key never written");
        Check(data.Get<int?>("a") == null && data.Keys().SequenceEqual(["b/c", "d"]), "after a remove: " + string.Join(", ", data.Keys()));
        data.Clear();
        Check(data.Keys().Count == 0, "keys after a clear: " + string.Join(", ", data.Keys()));
        Check(!Directory.Exists(scratch.Root) || Directory.EnumerateFiles(scratch.Root, "*", SearchOption.AllDirectories).FirstOrDefault() is null,
            "files after a clear: " + string.Join(", ", scratch.Entries()));
        Check(new DataStores(scratch.Root).OpenDevice(User("def")).Keys().Count == 0, "keys after a clear and a restart");
        data.Set("e", 5);
        Check(data.Get<int>("e") == 5, "a write after a clear");
        Console.WriteLine("PASS sharp data: Remove answers whether there was a value, and Clear leaves no keys and no files");
    }

    /// <summary>A key or a shared store's name that is not one is refused before anything is written.</summary>
    static void BadNamesAreRefused()
    {
        using var scratch = new Scratch();
        var stores = new DataStores(scratch.Root);
        var data = stores.OpenDevice(User("def"));
        var before = scratch.Entries();
        foreach (var key in new[] { "", "../x", "/x", "x/", "a//b", "A", ".x", "a/.x", "a b", "a+b", "a\\b", "é", new string('k', 201), null! })
        {
            var shown = Shown(key);
            var error = Throws<ArgumentException>(() => data.Set(key, 1), "Set of " + shown);
            Check(!error.Message.Contains("Parameter", StringComparison.Ordinal), "the message names a parameter: " + error.Message);
            Throws<ArgumentException>(() => data.Get<int>(key), "Get of " + shown);
            Throws<ArgumentException>(() => data.SetBytes(key, [1]), "SetBytes of " + shown);
            Throws<ArgumentException>(() => data.GetBytes(key), "GetBytes of " + shown);
            Throws<ArgumentException>(() => data.Remove(key), "Remove of " + shown);
            Throws<ArgumentException>(() => data.Update<int>(key, n => n + 1), "Update of " + shown);
        }
        foreach (var name in new[] { "../x", "", "X", "a/b", ".x", "-x", new string('n', 65), null! })
        {
            var shown = Shown(name);
            var error = Throws<ArgumentException>(() => stores.OpenShared(name, User("def")), "the shared name " + shown);
            Check(!error.Message.Contains("Parameter", StringComparison.Ordinal), "the message names a parameter: " + error.Message);
            Throws<ArgumentException>(() => stores.DeleteShared(name), "deleting the shared name " + shown);
        }
        foreach (var (definition, node) in new[] { ("..", "node"), ("def", ".."), ("a/b", "node"), ("def", "a/b"), ("", "node"), ("def", "") })
            Throws<ArgumentException>(() => stores.OpenDevice(User(definition, node)), $"the device {definition}/{node}");
        Check(scratch.Entries().SequenceEqual(before), "a refused name wrote " + string.Join(", ", scratch.Entries().Except(before)));

        // The longest of each is a name, as are dots, dashes and underscores after the first character.
        data.Set(new string('k', 200), 1);
        data.Set("a.b/c_d-1/2..3", 2);
        Check(data.Keys().SequenceEqual(["a.b/c_d-1/2..3", new string('k', 200)]), "the longest key and one of every character: " + string.Join(", ", data.Keys()));
        stores.OpenShared(new string('n', 64), User("def")).Set("x", 1);
        stores.OpenShared("hass-sharp-vision.people_2", User("def")).Set("x", 1);
        Check(Directory.Exists(scratch.Shared(new string('n', 64))) && Directory.Exists(scratch.Shared("hass-sharp-vision.people_2")), "the shared stores' directories");
        Console.WriteLine("PASS sharp data: a key or a shared name that is not one is refused before anything is written");

        static string Shown(string? name) => name == null ? "null" : name.Length > 20 ? name.Length + " characters" : "'" + name + "'";
    }

    /// <summary>A key holds JSON or bytes; reading it the other way names the key.</summary>
    static void AKeyHoldsOneKind()
    {
        using var scratch = new Scratch();
        var data = new DataStores(scratch.Root).OpenDevice(User("def"));
        data.Set("json-key", 5);
        data.SetBytes("bytes-key", [1, 2, 3]);
        var error = Throws<InvalidOperationException>(() => data.GetBytes("json-key"), "GetBytes of a JSON value");
        Check(error.Message.Contains("json-key", StringComparison.Ordinal), "the message does not name the key: " + error.Message);
        error = Throws<InvalidOperationException>(() => data.Get<int>("bytes-key"), "Get of bytes");
        Check(error.Message.Contains("bytes-key", StringComparison.Ordinal), "the message does not name the key: " + error.Message);
        Throws<InvalidOperationException>(() => data.Update<int>("bytes-key", n => n + 1), "Update of bytes");
        Check(data.GetBytes("bytes-key")!.SequenceEqual(new byte[] { 1, 2, 3 }), "a refused update changed the bytes");
        // A write of the other kind replaces the value.
        data.SetBytes("json-key", [9]);
        Check(data.GetBytes("json-key")!.SequenceEqual(new byte[] { 9 }), "bytes over a JSON value");
        data.Set("bytes-key", "now text");
        Check(data.Get<string>("bytes-key") == "now text", "JSON over bytes");
        Check(new DataStores(scratch.Root).OpenDevice(User("def")).Get<string>("bytes-key") == "now text", "JSON over bytes after a restart");
        Console.WriteLine("PASS sharp data: a key holds JSON or bytes, and reading it the other way names the key");
    }

    /// <summary>A value whose file was damaged, or that is not the type asked for, is named; the others still read.</summary>
    static void ACorruptValueIsNamed()
    {
        using var scratch = new Scratch();
        var data = new DataStores(scratch.Root).OpenDevice(User("def"));
        data.Set("people/ivan", new Person("Ivan", 41));
        data.Set("people/ana", new Person("Ana", 30));
        var files = scratch.Files(scratch.Device("def", "node"));
        Check(files.Length == 2, "the files of two values: " + string.Join(", ", files));
        var ivan = Path.Combine(scratch.Device("def", "node"), files.Single(file => file.Contains("ivan", StringComparison.Ordinal)));
        foreach (var garbage in new[] { "{not json"u8.ToArray(), [], [0xFF, 0x00, 0x13] })
        {
            File.WriteAllBytes(ivan, garbage);
            var error = Throws<InvalidDataException>(() => data.Get<Person>("people/ivan"), "a damaged value");
            Check(error.Message.Contains("people/ivan", StringComparison.Ordinal), "the message does not name the key: " + error.Message);
            Check(!error.Message.Contains("not json", StringComparison.Ordinal), "the message quotes the value: " + error.Message);
            Throws<InvalidDataException>(() => data.GetBytes("people/ivan"), "a damaged value as bytes");
            Check(data.Get<Person>("people/ana") == new Person("Ana", 30), "another key after a damaged one");
            Check(data.Keys().SequenceEqual(["people/ana", "people/ivan"]), "keys with a damaged one: " + string.Join(", ", data.Keys()));
        }
        data.Set("people/ivan", new Person("Ivan", 42));
        Check(data.Get<Person>("people/ivan") == new Person("Ivan", 42), "a damaged value written again");

        // JSON that is not the type asked for is named likewise.
        data.Set("people/ivan", "a secret name");
        var wrong = Throws<InvalidDataException>(() => data.Get<Person>("people/ivan"), "a string read as a record");
        Check(wrong.Message.Contains("people/ivan", StringComparison.Ordinal) && !wrong.Message.Contains("secret", StringComparison.Ordinal),
            "the message: " + wrong.Message);
        Throws<InvalidDataException>(() => data.Update<Person>("people/ivan", person => person!), "an update of a string as a record");
        Check(data.Get<string>("people/ivan") == "a secret name", "a refused update changed the value");
        Console.WriteLine("PASS sharp data: a damaged value, or one that is not the type asked for, is named, and the others still read");
    }

    /// <summary>A write over the value limit, one that fails to serialise and one the disk refuses keep the old value and leave no temporary file.</summary>
    static void AFailedWriteKeepsTheOldValue()
    {
        using var scratch = new Scratch();
        var data = new DataStores(scratch.Root).OpenDevice(User("def"));
        DataStores.ValueLimit = 16;
        try
        {
            data.Set("k", "short");
            var error = Throws<InvalidOperationException>(() => data.Set("k", new string('x', 40)), "a value over the limit");
            Check(error.Message.Contains("'k'", StringComparison.Ordinal) && !error.Message.Contains("xxxx", StringComparison.Ordinal), "the message: " + error.Message);
            Throws<InvalidOperationException>(() => data.SetBytes("k", new byte[17]), "bytes over the limit");
            Throws<InvalidOperationException>(() => data.Update<string>("k", _ => new string('x', 40)), "an update over the limit");
            data.SetBytes("b", new byte[16]);
        }
        finally { DataStores.ValueLimit = 8L * 1024 * 1024; }
        Check(data.Get<string>("k") == "short", "the old value after a value over the limit");
        Check(scratch.Files(scratch.Device("def", "node")).Length == 2 && scratch.Files(scratch.Device("def", "node")).All(file => file[0] != '.'),
            "files after a refused write: " + string.Join(", ", scratch.Files(scratch.Device("def", "node"))));

        // A value that cannot be serialised writes nothing.
        Throws<NotSupportedException>(() => data.Set("k", typeof(string)), "a value that cannot be serialised");
        Check(data.Get<string>("k") == "short", "the old value after a failed serialisation");

        // A directory that cannot be written to keeps the old value and leaves no temporary file.
        if (!OperatingSystem.IsWindows() && Environment.UserName != "root")
        {
            var directory = scratch.Device("def", "node");
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try { Throws<UnauthorizedAccessException>(() => data.Set("k", "longer value"), "a write the disk refuses"); }
            finally { File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            Check(data.Get<string>("k") == "short", "the old value after a write the disk refused");
            Check(scratch.Files(directory).All(file => file[0] != '.'), "a temporary file after a write the disk refused: " + string.Join(", ", scratch.Files(directory)));
        }
        Console.WriteLine("PASS sharp data: a write over the limit, or one that fails, keeps the old value and leaves no temporary file");
    }

    /// <summary>A write that would take a store past its limit is refused; a smaller value replaces a larger one.</summary>
    static void TheStoreLimitHolds()
    {
        using var scratch = new Scratch();
        var stores = new DataStores(scratch.Root);
        var data = stores.OpenDevice(User("def"));
        DataStores.StoreLimit = 64;
        try
        {
            data.SetBytes("a", new byte[40]);
            var error = Throws<InvalidOperationException>(() => data.SetBytes("b", new byte[40]), "a write past the store's limit");
            Check(error.Message.Contains("'b'", StringComparison.Ordinal), "the message: " + error.Message);
            Check(data.Keys().SequenceEqual(["a"]), "a refused write left a key: " + string.Join(", ", data.Keys()));
            Throws<InvalidOperationException>(() => data.SetBytes("a", new byte[70]), "a replacement past the store's limit");
            Check(data.GetBytes("a")!.Length == 40, "a refused replacement changed the value");
            data.SetBytes("a", new byte[20]);
            data.SetBytes("b", new byte[40]);
            Check(data.GetBytes("a")!.Length == 20 && data.GetBytes("b")!.Length == 40, "a smaller value, then another within the limit");
            // Another store has its own limit; after a restart the store's size is read from its files.
            stores.OpenDevice(User("def", "other")).SetBytes("a", new byte[60]);
            var again = new DataStores(scratch.Root).OpenDevice(User("def"));
            Throws<InvalidOperationException>(() => again.SetBytes("c", new byte[10]), "a write past the limit after a restart");
            Check(again.Remove("b"), "removing a value");
            again.SetBytes("c", new byte[10]);
            Check(again.Keys().SequenceEqual(["a", "c"]), "a removed value frees its room: " + string.Join(", ", again.Keys()));
        }
        finally { DataStores.StoreLimit = 512L * 1024 * 1024; }
        Console.WriteLine("PASS sharp data: a write that would take a store past its limit is refused, and a smaller value replaces a larger one");
    }

    /// <summary>The temporary files a crash left are removed when the store opens and are never keys.</summary>
    static void LeftoversAreRemoved()
    {
        using var scratch = new Scratch();
        new DataStores(scratch.Root).OpenDevice(User("def")).Set("kept", 1);
        var directory = scratch.Device("def", "node");
        var leftover = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".partial");
        File.WriteAllText(leftover, "half a value");
        // A node whose directory holds only a leftover holds no data.
        var empty = scratch.Device("def", "empty");
        Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(empty, ".left.partial"), "x");
        var shared = scratch.Shared("people");
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, ".left.partial"), "x");

        var stores = new DataStores(scratch.Root);
        Check(stores.Stored().Select(entry => $"{entry.Key}:{string.Join(",", entry.Value)}").SequenceEqual(["def:node"]),
            "stored with a leftover: " + string.Join("; ", stores.Stored().Select(entry => $"{entry.Key}:{string.Join(",", entry.Value)}")));
        Check(stores.Shared().Single().Bytes == 0, "a leftover counted in a shared store's size");
        var data = stores.OpenDevice(User("def"));
        Check(!File.Exists(leftover), "the leftover after the store opened");
        Check(data.Keys().SequenceEqual(["kept"]) && data.Get<int>("kept") == 1, "keys with a leftover: " + string.Join(", ", data.Keys()));
        stores.OpenShared("people", User("def"));
        Check(scratch.Files(shared).Length == 0, "the shared store's leftover after it opened");
        Console.WriteLine("PASS sharp data: a crash's temporary files are removed when the store opens and are never keys");
    }

    /// <summary>Two handles that update one key of a shared store at once lose no update.</summary>
    static async Task UpdateDoesNotLoseWrites()
    {
        using var scratch = new Scratch();
        var stores = new DataStores(scratch.Root);
        var one = stores.OpenShared("counter", User("one"));
        var two = stores.OpenShared("counter", User("two"));
        var written = new System.Collections.Concurrent.ConcurrentBag<int>();
        Task Count(SharpData data) => Task.Run(() => { for (var i = 0; i < 200; i++) written.Add(data.Update<int>("n", n => n + 1)); });
        await Task.WhenAll(Count(one), Count(two));
        Check(one.Get<int>("n") == 400 && two.Get<int>("n") == 400, $"the count is {one.Get<int>("n")}");
        Check(written.Order().SequenceEqual(Enumerable.Range(1, 400)), "Update returns what it wrote, each value once");
        Check(new DataStores(scratch.Root).OpenShared("counter", User("three")).Get<int>("n") == 400, "the count after a restart");
        // An update whose change throws writes nothing.
        Throws<InvalidTimeZoneException>(() => one.Update<int>("n", _ => throw new InvalidTimeZoneException()), "a change that throws");
        Check(two.Get<int>("n") == 400, "a change that threw wrote");
        Console.WriteLine("PASS sharp data: two handles that update one key at once lose no update");
    }

    /// <summary>
    /// A write runs the Changed actions of the store's other handles, through their users, one at a time and coalesced,
    /// never the writer's own; a failing action breaks neither the store nor the writer; a disposed subscription ends.
    /// </summary>
    static async Task ChangedReachesTheOthersOnly()
    {
        using var scratch = new Scratch();
        var stores = new DataStores(scratch.Root);
        var (ranA, ranB, runsOfB, inB, mostInB) = (0, 0, 0, 0, 0);
        var a = stores.OpenShared("people", User("def-a"));
        var b = stores.OpenShared("people", new DataUser("def-b", "node", action => { Interlocked.Increment(ref runsOfB); return Task.Run(action); }));
        using var onA = a.Changed(() => { Interlocked.Increment(ref ranA); return Task.CompletedTask; });
        var onB = b.Changed(async () =>
        {
            var inside = Interlocked.Increment(ref inB);
            InterlockedMax(ref mostInB, inside);
            Interlocked.Increment(ref ranB);
            await Task.Delay(30);
            Interlocked.Decrement(ref inB);
        });
        a.Set("x", 1);
        a.SetBytes("y", [1]);
        a.Set("x", 2);
        await Until(() => Volatile.Read(ref ranB) >= 1, "B's action after A's writes");
        await Task.Delay(300);
        Check(ranB is >= 1 and <= 3 && runsOfB == ranB, $"B's action ran {ranB} times, through B's user {runsOfB} times, for three writes");
        Check(ranA == 0, $"A's own action ran {ranA} times for A's writes");
        Check(mostInB == 1, $"{mostInB} runs of B's action at once");

        // Many writes while one run is held cause one more run.
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldRuns = 0;
        using (var hold = b.Changed(async () => { Interlocked.Increment(ref heldRuns); await held.Task; }))
        {
            a.Set("x", 3);
            await Until(() => Volatile.Read(ref heldRuns) == 1, "the held action's first run");
            for (var i = 0; i < 10; i++) a.Update<int>("x", n => n + 1);
            Check(a.Remove("y"), "removing a value");
            a.Clear();
            held.SetResult();
            await Until(() => Volatile.Read(ref heldRuns) == 2, "the held action's second run");
            await Task.Delay(200);
            Check(heldRuns == 2, $"the held action ran {heldRuns} times for writes during one run");
        }

        // B's writes reach A; a remove or clear that changes nothing reaches no one.
        b.Set("z", 1);
        await Until(() => Volatile.Read(ref ranA) == 1, "A's action after B's write");
        b.Clear();
        await Until(() => Volatile.Read(ref ranA) == 2, "A's action after B's clear");
        b.Clear();
        Check(!b.Remove("z"), "a removed value");
        await Task.Delay(200);
        Check(ranA == 2, $"A's action ran {ranA} times, also for changes that changed nothing");

        // A failing action, thrown or faulted, or a user whose Run throws, breaks neither the store nor the writer, and runs again.
        var (failed, thrown, refused) = (0, 0, 0);
        var c = stores.OpenShared("people", User("def-c"));
        using var failing = c.Changed(() => { Interlocked.Increment(ref failed); return Task.FromException(new InvalidOperationException("an action failed")); });
        using var throwing = c.Changed(() => { Interlocked.Increment(ref thrown); throw new InvalidOperationException("an action threw"); });
        var d = stores.OpenShared("people", new DataUser("def-d", "node", _ => { Interlocked.Increment(ref refused); throw new InvalidOperationException("the user refused"); }));
        using var neverRun = d.Changed(() => throw new Exception("an action its user refused ran"));
        a.Set("w", 1);
        await Until(() => Volatile.Read(ref failed) == 1 && Volatile.Read(ref thrown) == 1 && Volatile.Read(ref refused) == 1, "the failing actions' first runs");
        a.Set("w", 2);
        await Until(() => Volatile.Read(ref failed) == 2 && Volatile.Read(ref thrown) == 2 && Volatile.Read(ref refused) == 2, "the failing actions' second runs");
        Check(a.Get<int>("w") == 2, "a write with failing actions");

        // A disposed subscription runs no more; disposing it twice is nothing. A run of it in flight as it was disposed may
        // finish, and is not counted: once it has ended, another's write runs nothing.
        onB.Dispose();
        onB.Dispose();
        var disposed = (DataStores.Subscription)onB;
        await Until(() => !disposed.Running, "B's run in flight as it was disposed to end");
        var before = Volatile.Read(ref ranB);
        a.Set("x", 100);
        await Task.Delay(300);
        Check(ranB == before && !disposed.Running, $"B's disposed action ran {ranB - before} times");
        Console.WriteLine("PASS sharp data: a write runs the other handles' Changed actions, one at a time and coalesced, and never the writer's own");
    }

    /// <summary>
    /// A subscription disposed while its action runs, with one more run owed for a change meanwhile, lets the run under way
    /// finish and begins no other: its action ran once.
    /// </summary>
    static async Task ADisposedSubscriptionOwesNoRun()
    {
        using var scratch = new Scratch();
        var stores = new DataStores(scratch.Root);
        var writer = stores.OpenShared("people", User("def-a"));
        var reader = stores.OpenShared("people", new DataUser("def-b", "node", action => action()));
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ran = 0;
        var subscription = (DataStores.Subscription)reader.Changed(async () => { Interlocked.Increment(ref ran); await open.Task; });
        writer.Set("x", 1);
        await Until(() => Volatile.Read(ref ran) == 1, "the action's first run");
        // A change while it runs: one more run is owed.
        writer.Set("x", 2);
        subscription.Dispose();
        open.SetResult();
        await Until(() => !subscription.Running, "the run under way to end");
        writer.Set("x", 3);
        await Task.Delay(200);
        Check(ran == 1 && !subscription.Running, $"the action disposed during its run ran {ran} times");
        Console.WriteLine("PASS sharp data: a subscription disposed during a run, with a run owed, finishes that run and begins no other");
    }

    /// <summary>A component that opened a store twice is not told of its own writes through either handle; another component's write tells it.</summary>
    static async Task ChangedSkipsTheWritersOtherHandles()
    {
        using var scratch = new Scratch();
        var stores = new DataStores(scratch.Root);
        var one = User("def-a");
        var first = stores.OpenShared("people", one);
        var second = stores.OpenShared("people", one);
        var other = stores.OpenShared("people", User("def-b"));
        var ran = 0;
        using var changed = second.Changed(() => { Interlocked.Increment(ref ran); return Task.CompletedTask; });
        first.Set("x", 1);
        first.Update<int>("x", n => n + 1);
        Check(first.Remove("x"), "removing a value");
        await Task.Delay(300);
        Check(ran == 0, $"the component's own writes through its other handle ran its action {ran} times");
        other.Set("x", 3);
        await Until(() => Volatile.Read(ref ran) == 1, "the action after another component's write");
        Console.WriteLine("PASS sharp data: a component's writes through any of its handles do not run its own Changed actions; another component's do");
    }

    /// <summary>Two users with equal ids and the same Run, such as an old and a new revision's node, are two: closing one leaves the other's handles open.</summary>
    static async Task EqualUsersAreTwo()
    {
        using var scratch = new Scratch();
        var stores = new DataStores(scratch.Root);
        Func<Func<Task>, Task> run = action => Task.Run(action);
        var (old, current) = (new DataUser("def", "node", run), new DataUser("def", "node", run));
        Check(old == current, "the two users are equal records");
        var oldShared = stores.OpenShared("people", old);
        var currentShared = stores.OpenShared("people", current);
        var oldOwn = stores.OpenDevice(old);
        var currentOwn = stores.OpenDevice(current);
        var ran = 0;
        using var changed = currentShared.Changed(() => { Interlocked.Increment(ref ran); return Task.CompletedTask; });
        // The old revision's write is another component's to the new one.
        oldShared.Set("x", 1);
        await Until(() => Volatile.Read(ref ran) == 1, "the new revision's action after the old one's write");
        stores.Close(old);
        Throws<ObjectDisposedException>(() => oldShared.Get<int>("x"), "the closed user's shared handle");
        Throws<ObjectDisposedException>(() => oldOwn.Get<int>("x"), "the closed user's own handle");
        Check(currentShared.Get<int>("x") == 1, "the other user's shared handle after an equal user closed");
        currentOwn.Set("k", 1);
        Check(currentOwn.Get<int>("k") == 1, "the other user's own handle after an equal user closed");
        Check(!stores.DeleteShared("people"), "a store deleted while the other user has it open");
        Check(stores.Shared() is [{ Name: "people", Users: ["def"] }], "the store no longer counted as open");
        stores.Close(current);
        Check(stores.DeleteShared("people"), "the store refused after both closed");
        Console.WriteLine("PASS sharp data: two users with equal ids and the same Run are two, and closing one leaves the other's handles open");
    }

    static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }

    /// <summary>After its user is closed, every method of a handle refuses, and another user's handles still work.</summary>
    static async Task ClosedHandlesRefuse()
    {
        using var scratch = new Scratch();
        var stores = new DataStores(scratch.Root);
        var one = User("one");
        var own = stores.OpenDevice(one);
        var shared = stores.OpenShared("people", one);
        var other = stores.OpenShared("people", User("two"));
        own.Set("k", 1);
        var ran = 0;
        shared.Changed(() => { Interlocked.Increment(ref ran); return Task.CompletedTask; });
        stores.Close(one);
        foreach (var (data, which) in new[] { (own, "the device's handle"), (shared, "the shared handle") })
        {
            Throws<ObjectDisposedException>(() => data.Get<int>("k"), which + ": Get");
            Throws<ObjectDisposedException>(() => data.Set("k", 2), which + ": Set");
            Throws<ObjectDisposedException>(() => data.GetBytes("k"), which + ": GetBytes");
            Throws<ObjectDisposedException>(() => data.SetBytes("k", [1]), which + ": SetBytes");
            Throws<ObjectDisposedException>(() => data.Remove("k"), which + ": Remove");
            Throws<ObjectDisposedException>(() => data.Keys(), which + ": Keys");
            Throws<ObjectDisposedException>(() => data.Update<int>("k", n => n + 1), which + ": Update");
            Throws<ObjectDisposedException>(() => data.Changed(() => Task.CompletedTask), which + ": Changed");
            Throws<ObjectDisposedException>(() => data.Clear(), which + ": Clear");
        }
        stores.Close(one);
        other.Set("k", 3);
        Check(other.Get<int>("k") == 3 && other.Keys().SequenceEqual(["k"]), "another user's handle after a close");
        await Task.Delay(200);
        Check(ran == 0, "a closed user's action ran");
        // The data stays: the user's store opens again with it.
        var reopened = stores.OpenDevice(one);
        Check(reopened.Get<int>("k") == 1, "the closed user's data, opened again");
        Console.WriteLine("PASS sharp data: a closed user's handles refuse every method, and another user's still work");
    }

    /// <summary>A shared store is listed with its size and users, refused deletion while open, and deleted after.</summary>
    static void DeleteSharedIsRefusedWhileOpen()
    {
        using var scratch = new Scratch();
        var stores = new DataStores(scratch.Root);
        var (a, a2, b) = (User("def-a"), User("def-a", "second"), User("def-b"));
        stores.OpenShared("people", b).SetBytes("x", new byte[10]);
        stores.OpenShared("people", a).Set("y", 1);
        stores.OpenShared("people", a2);
        stores.OpenShared("unwritten", a);
        Check(Listed(stores) == "people:11:def-a,def-b|unwritten:0:def-a", "listed while open: " + Listed(stores));
        Check(!stores.DeleteShared("people"), "deleted while open");
        Check(scratch.Files(scratch.Shared("people")).Length == 2, "files after a refused delete: " + string.Join(", ", scratch.Entries()));
        stores.Close(a);
        Check(!stores.DeleteShared("people"), "deleted while another node of the definition has it open");
        Check(Listed(stores) == "people:11:def-a,def-b", "listed after some closed: " + Listed(stores));
        stores.Close(a2);
        stores.Close(b);
        Check(Listed(stores) == "people:11:", "listed after all closed: " + Listed(stores));
        Check(new DataStores(scratch.Root).Shared() is [{ Name: "people", Bytes: 11, Users.Count: 0 }], "listed by a new instance");
        Check(stores.DeleteShared("people"), "refused after all closed");
        Check(!Directory.Exists(scratch.Shared("people")), "the directory after a delete");
        Check(Listed(stores) == "" && new DataStores(scratch.Root).Shared().Count == 0, "listed after a delete: " + Listed(stores));
        Check(stores.DeleteShared("never"), "deleting a store there is none of");
        Check(stores.OpenShared("people", a).Keys().Count == 0, "a deleted store opened again");
        Console.WriteLine("PASS sharp data: a shared store is listed with its size and users, refused deletion while open, and deleted after");

        static string Listed(DataStores stores) => string.Join("|", stores.Shared().Select(store => $"{store.Name}:{store.Bytes}:{string.Join(",", store.Users)}"));
    }

    /// <summary>A device's stores are listed from memory, pruned to the nodes kept, and deleted with the definition.</summary>
    static void DeviceDataIsDeletedAndPruned()
    {
        using var scratch = new Scratch();
        var stores = new DataStores(scratch.Root);
        var a = stores.OpenDevice(User("def", "a"));
        var b = stores.OpenDevice(User("def", "b"));
        var c = stores.OpenDevice(User("def", "c"));
        var kept = stores.OpenDevice(User("keep", "x"));
        stores.OpenDevice(User("def", "unwritten"));
        a.Set("k", 1);
        b.SetBytes("k", [1]);
        c.Set("k", 1);
        kept.Set("k", 1);
        Check(Listed(stores) == "def:a,b,c|keep:x", "stored: " + Listed(stores));
        Check(Listed(new DataStores(scratch.Root)) == "def:a,b,c|keep:x", "stored, read by a new instance: " + Listed(new DataStores(scratch.Root)));
        // Removing a node's last value leaves it holding none.
        c.Remove("k");
        Check(Listed(stores) == "def:a,b|keep:x", "stored after a node's last value was removed: " + Listed(stores));
        c.Set("k", 2);

        stores.PruneDevice("def", new HashSet<string> { "a" });
        Check(!Directory.Exists(scratch.Device("def", "b")) && !Directory.Exists(scratch.Device("def", "c")) && Directory.Exists(scratch.Device("def", "a")),
            "after a prune: " + string.Join(", ", scratch.Entries()));
        Check(Listed(stores) == "def:a|keep:x", "stored after a prune: " + Listed(stores));
        Check(b.Keys().Count == 0 && a.Get<int>("k") == 1, "the handles after a prune");

        stores.DeleteDevice("def");
        Check(!Directory.Exists(Path.Combine(scratch.Root, "devices", "def")) && Directory.Exists(scratch.Device("keep", "x")), "after a delete: " + string.Join(", ", scratch.Entries()));
        Check(Listed(stores) == "keep:x", "stored after a delete: " + Listed(stores));
        stores.DeleteDevice("never");
        kept.Clear();
        Check(Listed(stores) == "", "stored after a clear: " + Listed(stores));
        Check(Listed(new DataStores(scratch.Root)) == "", "stored, read by a new instance after the deletes: " + Listed(new DataStores(scratch.Root)));
        Check(new DataStores(Path.Combine(scratch.Root, "missing")).Stored().Count == 0, "stored of a root that is not there");
        Console.WriteLine("PASS sharp data: a device's data is listed from memory, pruned to the nodes kept, and deleted with its definition");

        static string Listed(DataStores stores) => string.Join("|", stores.Stored().Select(entry => $"{entry.Key}:{string.Join(",", entry.Value)}"));
    }
}
