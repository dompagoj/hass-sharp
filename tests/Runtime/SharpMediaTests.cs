using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>
/// The media files of C# nodes. On their own, over a temporary media folder: a save gives the path Home Assistant serves
/// the file at and makes the folder Home Assistant named when it is not there yet; a second save of a name replaces the
/// file; <c>Path</c>, <c>Files</c> (newest first), <c>Remove</c> and <c>Clear</c>; names, an 11 MiB file and a 201st file
/// refused in a sentence; the entry <c>local</c> preferred, else the first by name, and every call refused without one; a
/// folder that cannot be written refused; saves of one name at once, saves past the limit at once and saves during a clear
/// leave only whole files and never more than the limit; a crash's leftover is no file and goes; a closed handle refuses;
/// a definition's and a node's folders are deleted. Through a whole manager (<see cref="EntityTestHome"/>): <c>Media</c>
/// before <c>Start()</c> fails the start in a sentence; its files outlive a recompile, a restart and turning the
/// definition off, and go with the definition and with the node; a stopped definition's handle is closed; and the guide's
/// example compiles and runs.
/// </summary>
static class SharpMediaTests
{
    const string HandleKey = "HassSharp.SharpMediaTests.Handle";

    static T Throws<T>(Action action, string what) where T : Exception
    {
        try { action(); }
        catch (T error) when (error.GetType() == typeof(T)) { return error; }
        catch (Exception error) { throw new Exception($"{what}: threw {error.GetType().Name} instead of {typeof(T).Name}: {error.Message}"); }
        throw new Exception($"{what}: did not throw {typeof(T).Name}");
    }

    // A temporary directory holding the media folders, removed afterwards; the folder of the entry local is not made.
    sealed class Scratch : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "sharp-media-" + Guid.NewGuid().ToString("N"));
        public string Media => Path.Combine(Root, "media");
        public string Node(string definition, string node, string? folder = null) => Path.Combine(folder ?? Media, "hass_sharp", definition, node);
        // The files of a directory, by name in ordinal order, temporary ones too.
        public static string[] Names(string directory) =>
            Directory.Exists(directory) ? Directory.GetFiles(directory).Select(file => Path.GetFileName(file)).Order(StringComparer.Ordinal).ToArray() : [];
        public MediaFolders Folders(string? entries = null)
        {
            var folders = new MediaFolders();
            folders.Set(entries ?? new JsonObject { ["local"] = Media }.ToJsonString());
            return folders;
        }
        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            if (!OperatingSystem.IsWindows())
                foreach (var directory in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories).Prepend(Root))
                    File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(Root, recursive: true);
        }
    }

    static byte[] Bytes(int length, byte value) => Enumerable.Repeat(value, length).ToArray();

    [RuntimeSuite("--sharp-media", Order = 42)]
    public static async Task Run()
    {
        try
        {
            ASaveGivesTheServedPathAndMakesTheFolder();
            ASaveReplacesTheFileOfItsName();
            PathFilesRemoveAndClear();
            BadNamesAreRefused();
            TheLimitsHold();
            TheEntryLocalIsPreferred();
            WithoutAMediaFolderEveryCallIsRefused();
            AFolderThatCannotBeWrittenRefusesTheSave();
            await SavesAtOnceLeaveWholeFilesWithinTheLimit();
            LeftoversAreNoFilesAndGo();
            AClosedHandleRefuses();
            FoldersAreDeletedAndPruned();
            UnsafeIdsAreRefused();
            await MediaIsRefusedBeforeStart();
            await FilesOutliveARecompileARestartAndTurningOff();
            await FilesGoWithTheDefinitionAndWithTheNode();
            await AStoppedDefinitionsHandleIsClosed();
            await NoMediaFolderIsSaidInASentence();
            await TheGuidesExampleRuns();
        }
        finally { AppContext.SetData(HandleKey, null); }
    }

    // Review focus 1: the media folder Home Assistant names does not exist yet, as a core install's config/media.
    static void ASaveGivesTheServedPathAndMakesTheFolder()
    {
        using var scratch = new Scratch();
        var media = scratch.Folders().Open("porch", "door");
        Check(!Directory.Exists(scratch.Media), "the media folder is there before the first save");
        var path = media.Save("visitor.jpg", [1, 2, 3]);
        Check(path == "/media/local/hass_sharp/porch/door/visitor.jpg", "the served path: " + path);
        var file = Path.Combine(scratch.Node("porch", "door"), "visitor.jpg");
        Check(File.Exists(file) && File.ReadAllBytes(file).SequenceEqual(new byte[] { 1, 2, 3 }), "the bytes are on disk");
        Check(Scratch.Names(scratch.Node("porch", "door")).SequenceEqual(["visitor.jpg"]), "the node's folder holds the file alone: " + string.Join(", ", Scratch.Names(scratch.Node("porch", "door"))));
        Check(media.Save("empty.txt", []) == "/media/local/hass_sharp/porch/door/empty.txt" && File.ReadAllBytes(Path.Combine(scratch.Node("porch", "door"), "empty.txt")).Length == 0, "an empty file is saved");
        Console.WriteLine("PASS sharp media: a save gives /media/<entry>/hass_sharp/<definition>/<node>/<name>, puts the bytes on disk and makes a media folder that was not there");
    }

    static void ASaveReplacesTheFileOfItsName()
    {
        using var scratch = new Scratch();
        var media = scratch.Folders().Open("porch", "door");
        media.Save("visitor.jpg", Bytes(100, 1));
        var path = media.Save("visitor.jpg", Bytes(10, 2));
        Check(path == "/media/local/hass_sharp/porch/door/visitor.jpg", "the path of the second save: " + path);
        Check(File.ReadAllBytes(Path.Combine(scratch.Node("porch", "door"), "visitor.jpg")).SequenceEqual(Bytes(10, 2)), "the second save's bytes replace the first's");
        var files = media.Files();
        Check(files.Count == 1 && files[0].Size == 10, "one file of the second save's size: " + string.Join(", ", files));
        Check(scratch.Folders().Open("porch", "door").Files().Single().Name == "visitor.jpg", "another instance reads it back");
        Console.WriteLine("PASS sharp media: a second save of a name replaces the file");
    }

    static void PathFilesRemoveAndClear()
    {
        using var scratch = new Scratch();
        var media = scratch.Folders().Open("porch", "door");
        Check(media.Path("first.jpg") == null && media.Files().Count == 0 && !media.Remove("first.jpg"), "an empty folder has no file");
        var before = DateTimeOffset.UtcNow.AddSeconds(-2);
        foreach (var (name, size) in new[] { ("first.jpg", 1), ("second.jpg", 2), ("third.png", 3) })
        {
            media.Save(name, Bytes(size, 7));
            Thread.Sleep(30);
        }
        Check(media.Path("second.jpg") == "/media/local/hass_sharp/porch/door/second.jpg", "the path of a file there: " + media.Path("second.jpg"));
        Check(media.Path("fourth.jpg") == null, "no path for a file not there");
        var files = media.Files();
        Check(files.Select(file => file.Name).SequenceEqual(["third.png", "second.jpg", "first.jpg"]), "newest first: " + string.Join(", ", files.Select(file => file.Name)));
        Check(files.All(file => file.Path == "/media/local/hass_sharp/porch/door/" + file.Name), "each file's path: " + string.Join(", ", files.Select(file => file.Path)));
        Check(files.Select(file => file.Size).SequenceEqual([3L, 2L, 1L]), "each file's size: " + string.Join(", ", files.Select(file => file.Size)));
        Check(files.All(file => file.Written > before && file.Written <= DateTimeOffset.UtcNow.AddSeconds(2) && file.Written.Offset == TimeSpan.Zero), "each file's time: " + string.Join(", ", files.Select(file => file.Written)));
        media.Save("first.jpg", Bytes(4, 7));
        Check(media.Files().Select(file => file.Name).SequenceEqual(["first.jpg", "third.png", "second.jpg"]), "a file saved again is the newest: " + string.Join(", ", media.Files().Select(file => file.Name)));
        Check(media.Remove("second.jpg") && !media.Remove("second.jpg"), "a removal says whether the file was there");
        Check(media.Path("second.jpg") == null && !File.Exists(Path.Combine(scratch.Node("porch", "door"), "second.jpg")), "a removed file is gone");
        media.Clear();
        Check(media.Files().Count == 0 && Scratch.Names(scratch.Node("porch", "door")).Length == 0, "a clear removes every file");
        media.Clear();
        Check(media.Save("again.jpg", [1]) == "/media/local/hass_sharp/porch/door/again.jpg" && media.Files().Count == 1, "a save after a clear");
        var other = scratch.Folders().Open("porch", "window");
        other.Save("mine.jpg", [1]);
        media.Clear();
        Check(other.Files().Single().Name == "mine.jpg", "a clear leaves another node's files");
        Console.WriteLine("PASS sharp media: Path, Files (newest first, with path, size and time), Remove and Clear");
    }

    static void BadNamesAreRefused()
    {
        using var scratch = new Scratch();
        var media = scratch.Folders().Open("porch", "door");
        const string rule = "a name is 1 to 100 letters, digits, '.', '_' and '-', does not start with a dot, and ends with an extension, such as 'visitor.jpg'";
        foreach (var name in new[] { "", ".hidden.jpg", "no-extension", "ends-with.", "with space.jpg", "a/b.jpg", "..", "../up.jpg", "back\\slash.jpg", "é.jpg", "colon:.jpg", new string('a', 97) + ".jpg", null })
        {
            var shown = name == null ? "A null name" : $"'{name}'";
            var error = Throws<ArgumentException>(() => media.Save(name!, [1]), $"the name {shown}");
            Check(error.Message == $"{shown} is not a media file's name: {rule}", "the sentence: " + error.Message);
            Throws<ArgumentException>(() => media.Path(name!), $"Path of the name {shown}");
            Throws<ArgumentException>(() => media.Remove(name!), $"Remove of the name {shown}");
        }
        Check(!Directory.Exists(scratch.Media), "a refused name touches no disk");
        foreach (var name in new[] { "a.b", "Visitor-2026_11_24.JPG", "x.tar.gz", "-.jpg", new string('a', 96) + ".jpg" })
            Check(media.Save(name, [1]).EndsWith("/" + name, StringComparison.Ordinal), "the name " + name);
        Throws<ArgumentException>(() => media.Save("null.jpg", null!), "null content");
        Console.WriteLine("PASS sharp media: names that are not 1 to 100 of A-Z a-z 0-9 . _ - with an extension and no leading dot, and null content, are refused in a sentence");
    }

    static void TheLimitsHold()
    {
        Check(MediaFolders.FileLimit == 10L * 1024 * 1024 && MediaFolders.CountLimit == 200, $"the limits are {MediaFolders.FileLimit} and {MediaFolders.CountLimit}");
        using var scratch = new Scratch();
        var media = scratch.Folders().Open("porch", "door");
        media.Save("largest.bin", new byte[MediaFolders.FileLimit]);
        var large = Throws<InvalidOperationException>(() => media.Save("large.bin", new byte[11 * 1024 * 1024]), "an 11 MiB file");
        Check(large.Message == "'large.bin' takes 11 MiB, more than the 10 MiB a media file may take", "the sentence: " + large.Message);
        Throws<InvalidOperationException>(() => media.Save("largest.bin", new byte[MediaFolders.FileLimit + 1]), "a file over the limit in place of one");
        Check(new FileInfo(Path.Combine(scratch.Node("porch", "door"), "largest.bin")).Length == MediaFolders.FileLimit, "a refused file keeps the one it would replace");
        for (var i = 1; i < MediaFolders.CountLimit; i++) media.Save($"file-{i}.jpg", [1]);
        Check(media.Files().Count == 200, "200 files: " + media.Files().Count);
        var many = Throws<InvalidOperationException>(() => media.Save("one-more.jpg", [1]), "a 201st file");
        Check(many.Message == "Saving 'one-more.jpg' would make 201 media files, more than the 200 a node may keep: remove some first", "the sentence: " + many.Message);
        Check(media.Save("file-1.jpg", [2]).EndsWith("/file-1.jpg", StringComparison.Ordinal), "a file replaced at the limit");
        Check(media.Remove("file-2.jpg") && media.Save("one-more.jpg", [1]) != null, "a file saved once one was removed");
        Check(Scratch.Names(scratch.Node("porch", "door")).Length == 200, "200 files on disk and no temporary one: " + Scratch.Names(scratch.Node("porch", "door")).Length);
        Console.WriteLine("PASS sharp media: a file takes at most 10 MiB and a node keeps at most 200; a save past either is refused in a sentence and writes nothing");
    }

    // Review focus 2: the media folder's entry is not called local, or there are several.
    static void TheEntryLocalIsPreferred()
    {
        using var scratch = new Scratch();
        string Folder(string name) => Path.Combine(scratch.Root, name);
        var several = scratch.Folders(new JsonObject { ["nas"] = Folder("nas"), ["local"] = Folder("local"), ["alpha"] = Folder("alpha") }.ToJsonString()).Open("porch", "door");
        Check(several.Save("a.jpg", [1]) == "/media/local/hass_sharp/porch/door/a.jpg" && File.Exists(Path.Combine(scratch.Node("porch", "door", Folder("local")), "a.jpg")), "local is preferred");
        var named = scratch.Folders(new JsonObject { ["zeta"] = Folder("zeta"), ["recordings"] = Folder("recordings") }.ToJsonString());
        var media = named.Open("porch", "door");
        Check(media.Save("a.jpg", [1]) == "/media/recordings/hass_sharp/porch/door/a.jpg" && File.Exists(Path.Combine(scratch.Node("porch", "door", Folder("recordings")), "a.jpg")), "else the first by name, under its own name");
        Check(media.Files().Single().Path == "/media/recordings/hass_sharp/porch/door/a.jpg" && media.Path("a.jpg") == "/media/recordings/hass_sharp/porch/door/a.jpg", "Files and Path give the entry's name too");
        // Python gives the folders again on a restart of Home Assistant; an open handle follows.
        named.Set(new JsonObject { ["local"] = Folder("moved") }.ToJsonString());
        Check(media.Path("a.jpg") == null && media.Save("b.jpg", [1]) == "/media/local/hass_sharp/porch/door/b.jpg" && File.Exists(Path.Combine(scratch.Node("porch", "door", Folder("moved")), "b.jpg")),
            "a handle follows the folders given since");
        var empty = scratch.Folders(new JsonObject { ["blank"] = "", ["real"] = Folder("real") }.ToJsonString()).Open("porch", "door");
        Check(empty.Save("a.jpg", [1]) == "/media/real/hass_sharp/porch/door/a.jpg", "an entry without a folder is no entry");
        foreach (var text in new[] { "[]", "not json", """{"local": 3}""", "null" })
            Throws<ArgumentException>(() => new MediaFolders().Set(text), "the folders " + text);
        Console.WriteLine("PASS sharp media: the entry local is preferred, else the first by name, whose name the path carries");
    }

    static void WithoutAMediaFolderEveryCallIsRefused()
    {
        using var scratch = new Scratch();
        foreach (var (what, folders) in new[] { ("never given", new MediaFolders()), ("given none", scratch.Folders("{}")) })
        {
            var media = folders.Open("porch", "door");
            foreach (var (call, run) in new (string, Action)[]
            {
                ("Save", () => media.Save("a.jpg", [1])), ("Path", () => media.Path("a.jpg")), ("Remove", () => media.Remove("a.jpg")),
                ("Files", () => media.Files()), ("Clear", () => media.Clear()),
            })
            {
                var error = Throws<InvalidOperationException>(run, $"{call} with the folders {what}");
                Check(error.Message == "Home Assistant has no media folder, so no media file can be kept", "the sentence: " + error.Message);
            }
            folders.DeleteDevice("porch");
            folders.PruneDevice("porch", new HashSet<string>());
        }
        Check(!Directory.Exists(scratch.Root), "nothing is written");
        Console.WriteLine("PASS sharp media: without a media folder every call is refused in a sentence, and deleting has nothing to do");
    }

    // Review focus 3: the media folder cannot be written.
    static void AFolderThatCannotBeWrittenRefusesTheSave()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            Console.WriteLine("SKIP sharp media: a folder that cannot be written (Windows or root writes it all the same)");
            return;
        }
        using var scratch = new Scratch();
        var media = scratch.Folders().Open("porch", "door");
        media.Save("kept.jpg", [1]);
        var folder = scratch.Node("porch", "door");
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Throws<UnauthorizedAccessException>(() => media.Save("kept.jpg", [2]), "a save over a file the disk refuses");
            Throws<UnauthorizedAccessException>(() => media.Save("new.jpg", [2]), "a new file the disk refuses");
        }
        finally { File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        Check(Scratch.Names(folder).SequenceEqual(["kept.jpg"]) && File.ReadAllBytes(Path.Combine(folder, "kept.jpg")).SequenceEqual(new byte[] { 1 }), "the old file is kept and no temporary file is left: " + string.Join(", ", Scratch.Names(folder)));
        // A media folder that is not there and cannot be made.
        File.SetUnixFileMode(scratch.Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var unmade = scratch.Folders(new JsonObject { ["local"] = Path.Combine(scratch.Root, "missing") }.ToJsonString()).Open("porch", "door");
            Throws<UnauthorizedAccessException>(() => unmade.Save("a.jpg", [1]), "a media folder that cannot be made");
            Check(unmade.Files().Count == 0 && unmade.Path("a.jpg") == null, "it lists nothing");
        }
        finally { File.SetUnixFileMode(scratch.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        Console.WriteLine("PASS sharp media: a save the disk refuses throws, keeps the old file and leaves no temporary file");
    }

    // Review focus 4: two saves of one name at once, saves past the limit at once, and a save during a clear.
    static async Task SavesAtOnceLeaveWholeFilesWithinTheLimit()
    {
        using var scratch = new Scratch();
        var folders = scratch.Folders();
        // Two handles on one node, as two revisions' would be: they share the node's lock.
        var (one, two) = (folders.Open("porch", "door"), folders.Open("porch", "door"));
        var folder = scratch.Node("porch", "door");
        const int size = 1024 * 1024;
        await Task.WhenAll(Enumerable.Range(1, 16).Select(i => Task.Run(() => (i % 2 == 0 ? one : two).Save("same.jpg", Bytes(size, (byte)i)))));
        var same = File.ReadAllBytes(Path.Combine(folder, "same.jpg"));
        Check(same.Length == size && same.All(b => b == same[0]), "the file of one name saved at once is one save's, whole");
        Check(Scratch.Names(folder).SequenceEqual(["same.jpg"]), "no temporary file is left: " + string.Join(", ", Scratch.Names(folder)));

        for (var i = 2; i < MediaFolders.CountLimit; i++) one.Save($"file-{i}.jpg", [1]);
        var refused = 0;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
        {
            try { (i % 2 == 0 ? one : two).Save($"racing-{i}.jpg", [1]); }
            catch (InvalidOperationException) { Interlocked.Increment(ref refused); }
        })));
        Check(refused == 19 && Scratch.Names(folder).Length == 200 && one.Files().Count == 200, $"of 20 saves past the limit at once one is kept: {refused} refused, {Scratch.Names(folder).Length} files");

        one.Clear();
        using var stop = new CancellationTokenSource();
        var clearing = Task.Run(() => { while (!stop.IsCancellationRequested) two.Clear(); });
        var saving = Enumerable.Range(1, 4).Select(i => Task.Run(() => { for (var n = 0; n < 40; n++) one.Save($"during-{i}.jpg", Bytes(64 * 1024, (byte)n)); })).ToArray();
        await Task.WhenAll(saving);
        stop.Cancel();
        await clearing;
        foreach (var name in Scratch.Names(folder))
        {
            var bytes = File.ReadAllBytes(Path.Combine(folder, name));
            Check(name.StartsWith("during-", StringComparison.Ordinal) && bytes.Length == 64 * 1024 && bytes.All(b => b == bytes[0]), $"{name} on disk after saves during clears is whole, and no temporary file is left");
        }
        Console.WriteLine("PASS sharp media: saves of one name at once, saves past the limit at once and saves during a clear leave only whole files, never more than 200");
    }

    static void LeftoversAreNoFilesAndGo()
    {
        using var scratch = new Scratch();
        var media = scratch.Folders().Open("porch", "door");
        var folder = scratch.Node("porch", "door");
        Directory.CreateDirectory(folder);
        var leftover = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".partial");
        File.WriteAllBytes(leftover, [1, 2]);
        File.WriteAllBytes(Path.Combine(folder, "not a name"), [1]);
        Check(media.Files().Count == 0 && media.Path(Path.GetFileName(leftover)[1..]) == null, "a leftover is no file");
        for (var i = 1; i <= MediaFolders.CountLimit; i++) media.Save($"file-{i}.jpg", [1]);
        Check(media.Files().Count == 200, "a leftover does not count: " + media.Files().Count);
        Check(!File.Exists(leftover), "a save removes the leftover");
        File.WriteAllBytes(leftover, [1, 2]);
        media.Clear();
        Check(!Directory.Exists(folder), "a clear removes the folder and a leftover in it");
        Console.WriteLine("PASS sharp media: a crash's temporary file is no file, does not count, and goes with the next save or clear");
    }

    static void AClosedHandleRefuses()
    {
        using var scratch = new Scratch();
        var folders = scratch.Folders();
        var media = folders.Open("porch", "door");
        media.Save("a.jpg", [1]);
        media.Close();
        foreach (var (call, run) in new (string, Action)[]
        {
            ("Save", () => media.Save("b.jpg", [1])), ("Path", () => media.Path("a.jpg")), ("Remove", () => media.Remove("a.jpg")),
            ("Files", () => media.Files()), ("Clear", () => media.Clear()),
        })
        {
            var error = Throws<ObjectDisposedException>(run, call + " of a closed handle");
            Check(error.Message == "The definition has stopped: its media folder is closed", "the sentence: " + error.Message);
        }
        Check(folders.Open("porch", "door").Files().Single().Name == "a.jpg", "another handle still reads the folder");
        Console.WriteLine("PASS sharp media: a closed handle refuses every call");
    }

    static void FoldersAreDeletedAndPruned()
    {
        using var scratch = new Scratch();
        var folders = scratch.Folders();
        foreach (var (definition, node) in new[] { ("porch", "door"), ("porch", "window"), ("porch", "gate"), ("garden", "door") })
            folders.Open(definition, node).Save("a.jpg", [1]);
        // A node folder no handle opened in this process, as one left from before a restart.
        Directory.CreateDirectory(scratch.Node("porch", "shed"));
        File.WriteAllBytes(Path.Combine(scratch.Node("porch", "shed"), "a.jpg"), [1]);
        folders.PruneDevice("porch", new HashSet<string>(["door"], StringComparer.Ordinal));
        string Nodes(string definition) => string.Join(", ", Directory.Exists(Path.Combine(scratch.Media, "hass_sharp", definition))
            ? Directory.EnumerateDirectories(Path.Combine(scratch.Media, "hass_sharp", definition)).Select(Path.GetFileName).Order(StringComparer.Ordinal) : []);
        Check(Nodes("porch") == "door", "a prune keeps the nodes listed alone: " + Nodes("porch"));
        Check(Nodes("garden") == "door", "a prune keeps another definition's: " + Nodes("garden"));
        folders.DeleteDevice("porch");
        Check(!Directory.Exists(Path.Combine(scratch.Media, "hass_sharp", "porch")), "a delete removes the definition's folder");
        Check(Nodes("garden") == "door", "a delete keeps another definition's: " + Nodes("garden"));
        folders.DeleteDevice("porch");
        folders.DeleteDevice("never");
        folders.PruneDevice("never", new HashSet<string>());
        Throws<ArgumentException>(() => folders.DeleteDevice("../garden"), "a delete of a definition that is not one");
        Throws<ArgumentException>(() => folders.PruneDevice("..", new HashSet<string>()), "a prune of a definition that is not one");
        Check(Nodes("garden") == "door", "nothing outside a definition's folder is deleted");
        Console.WriteLine("PASS sharp media: a definition's folder is deleted, and a prune deletes the folders of nodes not listed");
    }

    static void UnsafeIdsAreRefused()
    {
        var folders = new MediaFolders();
        foreach (var (definition, node) in new[] { ("..", "door"), ("porch", ".."), ("a/b", "door"), ("porch", "do or"), ("Porch", "door"), ("porch", "dóor"), ("", "door"), ("porch", "door%2F"), ("porch", "x:y") })
            Throws<ArgumentException>(() => folders.Open(definition, node), $"the node {definition}/{node}");
        Console.WriteLine("PASS sharp media: a definition id or node key that is not safe in a file name and a path is refused");
    }

    // A device that saves a file in Start() and shows its path, the files it has and the media's sentence when it has none.
    const string KeeperCode = """
        public class Keeper : SharpDevice
        {
            readonly SharpSensor saved, count;
            public Keeper() { saved = Add(new SharpSensor("saved")); count = Add(new SharpSensor("count")); }
            protected override Task Start()
            {
                try
                {
                    saved.Value = Media.Save($"start-{Media.Files().Count + 1}.jpg", new byte[] { 1, 2, 3 });
                    count.Value = Media.Files().Count;
                }
                catch (InvalidOperationException error) { saved.Value = error.Message; }
                return Task.CompletedTask;
            }
        }
        """;
    const string ConstructorCode = """
        public class Early : SharpDevice
        {
            public Early() { _ = Media; }
        }
        """;
    const string SetupCode = """
        public class Eager : SharpDevice
        {
            protected override Task Setup() { _ = Media; return Task.CompletedTask; }
        }
        """;
    // A device whose switch saves through the entity's own Media, and that hands its handle to the test.
    const string HanderCode = """
        public class Note : SharpSwitch
        {
            public Note() : base("note") { }
            protected override Task TurnOn() { Media.Save("from-entity.jpg", new byte[] { 9 }); IsOn = true; return Task.CompletedTask; }
        }
        public class Hander : SharpDevice
        {
            public Hander() { Add(new Note()); }
            protected override Task Start()
            {
                AppContext.SetData("HassSharp.SharpMediaTests.Handle", Media);
                return Task.CompletedTask;
            }
        }
        """;

    static JsonObject Device(string key, string source)
    {
        var descriptor = new NodeLibraryCompiler(Diagnostics).Preview([], [new CustomNodeSource("c", source, "csharp_device")]).Snapshot.Custom["c"];
        Check(descriptor.Valid, "the device's code compiles: " + string.Join(" | ", descriptor.Diagnostics.Select(d => d.Message)));
        return EntityNodeTests.CSharpDevice(key, key, key, new() { ["source"] = source, ["signature"] = descriptor.Signature?.DeepClone() ?? new JsonObject(), ["arguments"] = new JsonObject() });
    }

    static JsonObject Definition(string name, params JsonObject[] nodes) => new()
    {
        ["name"] = name, ["settings"] = new JsonArray(),
        ["logic"] = new JsonObject { ["kind"] = "graph", ["graph"] = new JsonObject { ["version"] = 5, ["name"] = name, ["nodes"] = new JsonArray(nodes), ["edges"] = new JsonArray() } },
    };

    /// <summary>A whole manager over a home of its own, given the home's media folder unless told not to.</summary>
    sealed class Rig : IAsyncDisposable
    {
        readonly bool media;
        public EntityTestHome Home { get; }
        public HassSharpManager Api { get; private set; } = null!;
        public string Media { get; private set; } = "";
        Rig(string name, bool media) => (Home, this.media) = (new("sharp-media-" + name), media);
        public static async Task<Rig> Start(string name, bool media = true)
        {
            var rig = new Rig(name, media);
            await rig.Begin();
            return rig;
        }
        async Task Begin()
        {
            Api = new(Home.Root);
            if (media) Media = Home.MediaFolder(Api);
            await Api.Init();
        }
        /// <summary>Home Assistant restarts: a second manager over the same directory, given the folders again.</summary>
        public async Task Restart()
        {
            await Api.UnloadAsync();
            await Begin();
        }
        public async Task<JsonNode> Request(object request) => JsonNode.Parse(await Api.EntityRequest(JsonSerializer.Serialize(request)))!;
        public async Task<JsonObject> Ok(object request)
        {
            var answer = await Request(request);
            Check(answer["ok"]!.GetValue<bool>(), "the request failed: " + answer.ToJsonString());
            return answer["result"]!.AsObject();
        }
        public Task<JsonObject> Save(string id, JsonObject definition, long revision = 0) => Ok(new { operation = "save", id, expectedRevision = revision, definition });
        public async Task Running(string id, JsonObject definition, long revision = 0)
        {
            var saved = await Save(id, definition, revision);
            Check(saved["active"]!.GetValue<bool>() && saved["activationError"] == null, $"{id} runs: " + saved.ToJsonString());
        }
        public string? Shown(string uniqueId) =>
            Home.Snapshots().LastOrDefault(s => s["uniqueId"]!.GetValue<string>() == uniqueId)?["values"]?["value"]?.ToJsonString();
        public Task Shows(string uniqueId, string value, string what) =>
            EntityTestHome.Until(() => Shown(uniqueId) == value, $"{what}: {uniqueId} shows {value}, not {Shown(uniqueId) ?? "nothing"}");
        /// <summary>The folder of a node's media files, or of a definition's without a node.</summary>
        public string Folder(string definitionId, string? node = null) =>
            node == null ? Path.Combine(Media, "hass_sharp", definitionId) : Path.Combine(Media, "hass_sharp", definitionId, node);
        public async ValueTask DisposeAsync()
        {
            await Api.UnloadAsync();
            await Home.DisposeAsync();
        }
    }

    static async Task MediaIsRefusedBeforeStart()
    {
        await using var rig = await Rig.Start("refused");
        foreach (var (id, source, sentence) in new[]
        {
            ("early", ConstructorCode, "Early is not running yet: use Media from Start or later, not from Setup or the constructor."),
            ("eager", SetupCode, "Eager is not running yet: use Media from Start or later, not from Setup or the constructor."),
        })
        {
            var saved = await rig.Save(id, Definition(id, Device("box", source)));
            var error = saved["activationError"]?.GetValue<string>();
            Check(!saved["active"]!.GetValue<bool>() && error?.Contains(sentence, StringComparison.Ordinal) == true, $"{id} fails its start with the sentence: " + saved.ToJsonString());
        }
        await rig.Running("kept", Definition("Kept", Device("box", KeeperCode)));
        await rig.Shows("kept:box:saved", "\"/media/local/hass_sharp/kept/box/start-1.jpg\"", "a device that uses Media in Start() saves");
        Check(File.ReadAllBytes(Path.Combine(rig.Folder("kept", "box"), "start-1.jpg")).SequenceEqual(new byte[] { 1, 2, 3 }), "the file is in the node's folder");
        Console.WriteLine("PASS sharp media: Media in a constructor or Setup() fails the start with a sentence; in Start() a save gives the path Home Assistant serves");
    }

    static async Task FilesOutliveARecompileARestartAndTurningOff()
    {
        await using var rig = await Rig.Start("outlive");
        await rig.Running("kept", Definition("Kept", Device("box", KeeperCode)));
        await rig.Shows("kept:box:count", "1", "the first start");
        await rig.Running("kept", Definition("Kept", Device("box", KeeperCode)), 1);
        await rig.Shows("kept:box:count", "2", "the start after a save keeps the first file");
        await rig.Restart();
        await rig.Shows("kept:box:count", "3", "the start after a restart keeps both");
        await rig.Ok(new { operation = "set_enabled", id = "kept", enabled = false });
        Check(Scratch.Names(rig.Folder("kept", "box")).Length == 3, "turning it off keeps its files: " + string.Join(", ", Scratch.Names(rig.Folder("kept", "box"))));
        await rig.Ok(new { operation = "set_enabled", id = "kept", enabled = true });
        await rig.Shows("kept:box:count", "4", "the start after turning it on again");
        Console.WriteLine("PASS sharp media: a node's files outlive a recompile, a restart and turning the definition off");
    }

    static async Task FilesGoWithTheDefinitionAndWithTheNode()
    {
        await using var rig = await Rig.Start("deleted");
        await rig.Running("pair", Definition("Pair", Device("left", KeeperCode), Device("right", KeeperCode)));
        await rig.Shows("pair:left:count", "1", "the node to keep saves");
        await rig.Shows("pair:right:count", "1", "the node to remove saves");
        await rig.Running("pair", Definition("Pair", Device("left", KeeperCode)), 1);
        await rig.Shows("pair:left:count", "2", "the kept node starts again");
        Check(!Directory.Exists(rig.Folder("pair", "right")), "the removed node's files are gone once the next revision started");
        Check(Scratch.Names(rig.Folder("pair", "left")).Length == 2, "the kept node's files are kept");
        await rig.Running("other", Definition("Other", Device("box", KeeperCode)));
        await rig.Shows("other:box:count", "1", "another definition saves");
        await rig.Ok(new { operation = "delete", id = "pair", expectedRevision = 2 });
        Check(!Directory.Exists(rig.Folder("pair")), "the deleted definition's files are gone");
        Check(Scratch.Names(rig.Folder("other", "box")).Length == 1, "another definition's files are kept");
        Console.WriteLine("PASS sharp media: a node's files go with the definition, and with the node when a saved revision no longer has it");
    }

    static async Task AStoppedDefinitionsHandleIsClosed()
    {
        await using var rig = await Rig.Start("closed");
        await rig.Running("hander", Definition("Hander", Device("box", HanderCode)));
        await EntityTestHome.Until(() => AppContext.GetData(HandleKey) != null, "the device hands its handle over");
        var media = (SharpMedia)AppContext.GetData(HandleKey)!;
        await rig.Api.EntityCommand("hander:box:note", "turn_on", null);
        Check(media.Files().Single().Name == "from-entity.jpg", "an entity the device added saves in the device's folder: " + string.Join(", ", media.Files().Select(file => file.Name)));
        await rig.Ok(new { operation = "set_enabled", id = "hander", enabled = false });
        var error = Throws<ObjectDisposedException>(() => media.Save("late.jpg", [1]), "the handle once the definition was turned off");
        Check(error.Message == "The definition has stopped: its media folder is closed", "the sentence: " + error.Message);
        AppContext.SetData(HandleKey, null);
        Check(Scratch.Names(rig.Folder("hander", "box")).SequenceEqual(["from-entity.jpg"]), "its file is kept");
        Console.WriteLine("PASS sharp media: an entity a device added keeps its files in the device's folder, and a stopped definition's handle is closed");
    }

    static async Task NoMediaFolderIsSaidInASentence()
    {
        await using var rig = await Rig.Start("none", media: false);
        await rig.Running("kept", Definition("Kept", Device("box", KeeperCode)));
        await rig.Shows("kept:box:saved", "\"Home Assistant has no media folder, so no media file can be kept\"", "a save without a media folder");
        Console.WriteLine("PASS sharp media: without a media folder a device's save is refused in a sentence");
    }

    // The guide's example under "Files in Home Assistant's media folder", read from its block marked
    // <!-- compiled: DoorbellPicture -->: it compiles as a C# device without a diagnostic, and a ring of its doorbell
    // saves a picture of its camera, named on its sensor's attribute picture once the file is on disk.
    static async Task TheGuidesExampleRuns()
    {
        var source = ServiceCallTests.GuideCode("DoorbellPicture");
        await using var rig = await Rig.Start("guide");
        rig.Api.SetEntityDescriptions(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ha-entity-components.json")), started: true);
        await rig.Api.RetriesDone();
        var descriptor = new NodeLibraryCompiler(NodeLibraryFixture.EntityProvider).Preview([], [new CustomNodeSource("c", source, "csharp_device")]).Snapshot.Custom["c"];
        var shown = string.Join(" | ", descriptor.Diagnostics.Select(d => $"{d.Severity} {d.StartLine}:{d.StartColumn} {d.Message}"));
        Check(descriptor.Valid && descriptor.Diagnostics.IsEmpty, "the guide's device compiles without a diagnostic: " + shown);
        var jpeg = SharpImage.Blank(64, 48, SharpColor.Blue).ToJpeg();
        rig.Home.AnswerPictures = (_, entityId, ok, fail) => { if (entityId == "camera.front_door") ok(jpeg); else fail("No such camera"); };
        var node = EntityNodeTests.CSharpDevice("door", "door", "Door", new()
        {
            ["source"] = source, ["signature"] = descriptor.Signature!.DeepClone(),
            ["arguments"] = new JsonObject { ["Camera"] = Lit("entity_id:camera", "camera.front_door"), ["Doorbell"] = Lit("entity_id:binary_sensor", "binary_sensor.doorbell") },
        });
        await rig.Running("front", Definition("Front", node));
        string? Picture() => rig.Home.Snapshots().LastOrDefault(s => s["uniqueId"]!.GetValue<string>() == "front:door:last_ring")?["attributes"]?["picture"]?.GetValue<string>();
        string Ring(string picture) => Path.Combine(rig.Folder("front", "door"), picture[(picture.LastIndexOf('/') + 1)..]);
        // Each picture is on disk by the time a state names it.
        var missing = new System.Collections.Concurrent.ConcurrentBag<string>();
        var push = PyInterop.PushEntityStates;
        PyInterop.PushEntityStates = batch =>
        {
            foreach (var snapshot in JsonNode.Parse(batch)!.AsArray())
                if (snapshot!["attributes"]?["picture"]?.GetValue<string>() is { } named && !File.Exists(Ring(named))) missing.Add(named);
            push(batch);
        };
        await rig.Api.OnTrackedEntityChange("binary_sensor.doorbell", new() { EntityId = "binary_sensor.doorbell", State = "on" }, new() { EntityId = "binary_sensor.doorbell", State = "off" });
        await EntityTestHome.Until(() => Picture() != null, "the sensor names the picture");
        var picture = Picture()!;
        Check(picture.StartsWith("/media/local/hass_sharp/front/door/ring-", StringComparison.Ordinal) && picture.EndsWith(".jpg", StringComparison.Ordinal), "the picture's path: " + picture);
        Check(File.Exists(Ring(picture)) && SharpImage.Load(File.ReadAllBytes(Ring(picture))).Width == 64, "the picture is the camera's");
        Check(missing.IsEmpty, "a state named a picture not on disk: " + string.Join(", ", missing));
        Console.WriteLine("PASS sharp media: the guide's example compiles without a diagnostic, and a ring saves a picture and names it on its sensor once it is on disk");
    }
}
