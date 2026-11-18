using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HassSharp;
using static VisualExecutionTests;

/// <summary>
/// What a definition's code reaches the network with, against <see cref="LoopbackServer"/>: <see cref="SharpComponent.Http"/>,
/// one client for the device and its entities, disposed once the last <c>Stop()</c> has run (a request from there still
/// works), which cancels what is still in flight on it; and <see cref="SharpFiles.Download"/>, which fetches a file once
/// and caches it, never leaves a file at its path that does not match its checksum (wrong checksum, dropped connection,
/// stalled server, a leftover of a download that died), makes one request for two downloads of one checksum, refuses a
/// bad address, checksum or name before any request, and whose receiving does not count against the setup limit. A class
/// device of a saved library downloads in Setup() and polls with Http from Every(): it publishes what it read, logs a
/// server that is down once and its recovery once, a wrong checksum fails it, and turning it off cancels the request in
/// flight with nothing published after.
/// </summary>
static class EntityToolsTests
{
    [RuntimeSuite("--entity-tools", Order = 13)]
    public static async Task Run()
    {
        var (stall, size, drain) = (SharpFiles.StallTimeout, SharpFiles.SizeLimit, UserScript.RunDrainTimeout);
        await using var home = new EntityTestHome("entity-tools");
        await using var server = LoopbackServer.Start();
        try
        {
            await DownloadsOnce(home, server);
            await WrongChecksum(home, server);
            await DroppedConnection(home, server);
            await OneRequestForTwo(home, server);
            await Stalled(home, server);
            await RefusedBeforeAnyRequest(home, server);
            await TooLarge(home, server);
            await Leftovers(home, server);
            await Closed(home, server);
            await SetupLimit(home, server);
            await ClientOwnership(home, server);
            await LibraryDevice(home, server);
        }
        finally
        {
            SharpFiles.StallTimeout = stall;
            SharpFiles.SizeLimit = size;
            UserScript.RunDrainTimeout = drain;
        }
    }

    static string FilesRoot(EntityTestHome home) => Path.Combine(home.Root, ".storage", "hass_sharp", "files");
    static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static byte[] Bytes(int length, int seed) { var bytes = new byte[length]; new Random(seed).NextBytes(bytes); return bytes; }

    sealed class Done : IDisposable { public void Dispose() { } }

    static SharpFiles Files(EntityTestHome home, List<string>? lines = null) =>
        new(FilesRoot(home), line => { if (lines != null) lock (lines) lines.Add(line); }, () => new Done());

    static async Task<T> Throws<T>(Func<Task> action, string what) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        catch (Exception error) { throw new Exception($"{what}: threw {error.GetType().Name} instead of {typeof(T).Name}: {error.Message}"); }
        throw new Exception($"{what}: did not throw {typeof(T).Name}");
    }

    // Every file under a checksum's directory, by name; none when it does not exist.
    static string[] Listed(EntityTestHome home, string sha) =>
        Directory.Exists(Path.Combine(FilesRoot(home), sha)) ? Directory.GetFiles(Path.Combine(FilesRoot(home), sha)).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray() : [];

    static async Task DownloadsOnce(EntityTestHome home, LoopbackServer server)
    {
        var model = Bytes(100_000, 1);
        var sha = Sha(model);
        server.Respond("/models/model.bin", new() { Body = model });
        List<string> lines = [];
        var files = Files(home, lines);
        // The checksum in upper case is the same file: its directory is in lower case. The query carries a token.
        var url = server.Url("/models/model.bin?token=hunter2");
        var path = await files.Download(url, sha.ToUpperInvariant());
        Check(path == Path.Combine(FilesRoot(home), sha, "model.bin"), "the path of a download: " + path);
        Check(File.ReadAllBytes(path).SequenceEqual(model), "the downloaded file is the server's");
        Check(server.Count("/models/model.bin") == 1, "one request");
        var request = server.Requests().Single(r => r.Path == "/models/model.bin");
        Check(request.Method == "GET" && request.Headers["User-Agent"] == "HassSharp", "the request: " + request.Method + " " + string.Join(", ", request.Headers.Select(h => h.Key + "=" + h.Value)));
        string[] logged;
        lock (lines) logged = [.. lines];
        Check(logged.Length == 2 && logged[0] == "Downloading model.bin from 127.0.0.1"
            && Regex.IsMatch(logged[1], @"^Downloaded model\.bin from 127\.0\.0\.1: 97\.7 KiB in \d+(\.\d)? s$"), "a download logs its start and its end: " + string.Join(" | ", logged));
        Check(!logged.Any(line => line.Contains("hunter2") || line.Contains(server.Port.ToString()) || line.Contains("/models")), "the log names only the host: " + string.Join(" | ", logged));
        Check(Listed(home, sha).SequenceEqual(["model.bin"]), "only the file is left: " + string.Join(", ", Listed(home, sha)));
        Check(await files.Download(url, sha) == path && server.Count("/models/model.bin") == 1, "a second download finds the file and makes no request");
        lock (lines) Check(lines.Count == 2, "a download that finds the file logs nothing");
        // Another definition (another SharpFiles) finds it too.
        Check(await Files(home).Download(url, sha) == path && server.Count("/models/model.bin") == 1, "another definition's download finds the file");

        // The name: given, or the last path segment, else "file".
        var table = Encoding.UTF8.GetBytes("{\"a\":1}");
        server.Respond("/tables/", new() { Body = table });
        server.Respond("/download", new() { Body = table });
        var named = await files.Download(server.Url("/download"), Sha(table), "table.json");
        var plain = await files.Download(server.Url("/tables/"), Sha(table));
        Check(named == Path.Combine(FilesRoot(home), Sha(table), "table.json") && plain == Path.Combine(FilesRoot(home), Sha(table), "file")
            && File.ReadAllBytes(plain).SequenceEqual(table), "a download's name: " + named + ", " + plain);
        Console.WriteLine("PASS entity tools: a download fetches the file once to files/<sha256>/<name>, sends User-Agent HassSharp, logs its start and its end naming only the host, and a later one (from any definition) finds it without a request");
    }

    static async Task WrongChecksum(EntityTestHome home, LoopbackServer server)
    {
        var body = Encoding.UTF8.GetBytes("not the firmware");
        var sha = Sha(Encoding.UTF8.GetBytes("the firmware"));
        server.Respond("/firmware.bin", new() { Body = body });
        List<string> lines = [];
        var error = await Throws<InvalidDataException>(() => Files(home, lines).Download(server.Url("/firmware.bin?key=secret"), sha), "a wrong checksum");
        Check(error.Message == "The file from 127.0.0.1 does not match its checksum", "the message: " + error.Message);
        Check(!File.Exists(Path.Combine(FilesRoot(home), sha, "firmware.bin")) && Listed(home, sha).Length == 0, "a wrong checksum leaves nothing: " + string.Join(", ", Listed(home, sha)));
        lock (lines) Check(lines.Count == 2 && lines[1].StartsWith("Downloading firmware.bin from 127.0.0.1 failed after 16 bytes in ", StringComparison.Ordinal), "a failed download logs its end: " + string.Join(" | ", lines));
        Console.WriteLine("PASS entity tools: a file that does not match its checksum is refused by a message naming only the host, and nothing is left at its path");
    }

    static async Task DroppedConnection(EntityTestHome home, LoopbackServer server)
    {
        var body = Bytes(50_000, 2);
        var sha = Sha(body);
        var path = Path.Combine(FilesRoot(home), sha, "dropped.bin");
        server.Respond("/dropped.bin", new() { Body = body, DropAt = 20_000 });
        var files = Files(home);
        var error = await Throws<HttpRequestException>(() => files.Download(server.Url("/dropped.bin"), sha), "a dropped connection");
        Check(error.Message.StartsWith("The file from 127.0.0.1 could not be downloaded: ", StringComparison.Ordinal) && !error.Message.Contains("/dropped"), "the message: " + error.Message);
        Check(!File.Exists(path) && Listed(home, sha).Length == 0, "a dropped connection leaves nothing: " + string.Join(", ", Listed(home, sha)));
        // A server that is down.
        server.Down();
        try
        {
            var refused = await Throws<HttpRequestException>(() => files.Download(server.Url("/dropped.bin"), sha), "a server that is down");
            Check(refused.Message.StartsWith("The file from 127.0.0.1 could not be downloaded: ", StringComparison.Ordinal) && !refused.Message.Contains(server.Port.ToString()), "the message: " + refused.Message);
        }
        finally { server.Up(); }
        Check(Listed(home, sha).Length == 0, "a server that is down leaves nothing");
        // An error status.
        server.Respond("/dropped.bin", new() { Status = 404 });
        var missing = await Throws<HttpRequestException>(() => files.Download(server.Url("/dropped.bin"), sha), "a 404");
        Check(missing.Message == "HTTP 404 from 127.0.0.1" && missing.StatusCode == System.Net.HttpStatusCode.NotFound && Listed(home, sha).Length == 0, "a 404: " + missing.Message);
        server.Respond("/dropped.bin", new() { Body = body });
        Check(await files.Download(server.Url("/dropped.bin"), sha) == path && File.ReadAllBytes(path).SequenceEqual(body), "a retry succeeds");
        Check(server.Count("/dropped.bin") == 3, "each attempt the server took was a request: " + server.Count("/dropped.bin"));
        Console.WriteLine("PASS entity tools: a connection dropped mid-body, a server that is down or an error status leaves nothing at the path, and a retry succeeds");
    }

    static async Task OneRequestForTwo(EntityTestHome home, LoopbackServer server)
    {
        var body = Bytes(30_000, 3);
        var sha = Sha(body);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Respond("/shared.bin", new() { Body = body, Before = answer.Task });
        // Two definitions, each with its own SharpFiles.
        var first = Files(home).Download(server.Url("/shared.bin"), sha);
        var second = Files(home).Download(server.Url("/shared.bin"), sha);
        await server.Requested("/shared.bin");
        // A second request would arrive while the first is held; it is given a moment to.
        await Task.WhenAny(server.Requested("/shared.bin", 2), Task.Delay(200));
        Check(server.Count("/shared.bin") == 1 && !second.IsCompleted, "the second download waits for the first: " + server.Count("/shared.bin"));
        answer.SetResult();
        var paths = await Task.WhenAll(first, second);
        Check(paths.Distinct().Count() == 1 && File.ReadAllBytes(paths[0]).SequenceEqual(body) && server.Count("/shared.bin") == 1, "both have the file from one request: " + server.Count("/shared.bin"));
        Console.WriteLine("PASS entity tools: two concurrent downloads of one checksum, from two definitions, make one request and both get the file");
    }

    static async Task Stalled(EntityTestHome home, LoopbackServer server)
    {
        var body = Bytes(10_000, 4);
        var sha = Sha(body);
        var never = new TaskCompletionSource().Task;
        SharpFiles.StallTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            // Nothing at all, then the first bytes and nothing more.
            server.Respond("/silent.bin", new() { Body = body, Before = never });
            server.Respond("/stalled.bin", new() { Body = body, HoldAt = 1000, Hold = never });
            foreach (var path in new[] { "/silent.bin", "/stalled.bin" })
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var error = await Throws<TimeoutException>(() => Files(home).Download(server.Url(path), sha), "a stalled server " + path);
                Check(error.Message == "The file from 127.0.0.1 sent nothing for 0.3 s", "the message: " + error.Message);
                Check(clock.Elapsed >= TimeSpan.FromMilliseconds(290) && clock.Elapsed < TimeSpan.FromSeconds(5), $"{path} timed out after {clock.Elapsed.TotalMilliseconds} ms");
                Check(Listed(home, sha).Length == 0, "a stalled download leaves nothing: " + string.Join(", ", Listed(home, sha)));
            }
            // A slow file that keeps coming is no stall.
            server.Respond("/slow.bin", new() { Body = body, Pieces = 5, Pause = TimeSpan.FromMilliseconds(150) });
            Check(File.ReadAllBytes(await Files(home).Download(server.Url("/slow.bin"), sha)).SequenceEqual(body), "a slow download that keeps receiving");
        }
        finally { SharpFiles.StallTimeout = TimeSpan.FromSeconds(60); }
        Console.WriteLine("PASS entity tools: a server that answers nothing, or stops sending mid-body, for the stall limit times out with nothing left; one that keeps sending slowly does not");
    }

    static async Task RefusedBeforeAnyRequest(EntityTestHome home, LoopbackServer server)
    {
        var sha = Sha([1]);
        var before = server.Requests().Length;
        var files = Files(home);
        var url = server.Url("/refused.bin");
        (string? Url, string? Sha, string? Name, string What)[] bad =
        [
            ("ftp://127.0.0.1/refused.bin", sha, null, "an ftp address"),
            ("refused.bin", sha, null, "a relative address"),
            (null, sha, null, "no address"),
            (url, sha[..63], null, "a short checksum"),
            (url, new string('g', 64), null, "a checksum that is not hexadecimal"),
            (url, null, null, "no checksum"),
            (url, sha, "../refused.bin", "a name with a parent directory"),
            (url, sha, "a/b", "a name with a directory"),
            (url, sha, "a\\b", "a name with a backslash"),
            (url, sha, "", "an empty name"),
            (url, sha, ".hidden", "a hidden name"),
            (url, sha, " spaced", "a name with a leading space"),
            (url, sha, "line\nbreak", "a name with a control character"),
        ];
        foreach (var (address, checksum, name, what) in bad)
        {
            var error = await Throws<ArgumentException>(() => files.Download(address!, checksum!, name), what);
            Check(!error.Message.Contains("127.0.0.1"), $"{what}: the message quotes the address: " + error.Message);
        }
        Check(server.Requests().Length == before && server.Count("/refused.bin") == 0, "a bad download made a request");
        Check(!Directory.Exists(Path.Combine(FilesRoot(home), sha)), "a bad download made its directory");
        Console.WriteLine("PASS entity tools: a download with an address that is not http or https, a checksum that is not 64 hexadecimal characters or a name that is not a plain file name is refused before any request");
    }

    static async Task TooLarge(EntityTestHome home, LoopbackServer server)
    {
        var body = Bytes(5_000, 5);
        var sha = Sha(body);
        // Said to be 3 GiB.
        server.Respond("/huge.bin", new() { Body = body[..100], ContentLength = 3L * 1024 * 1024 * 1024 });
        var error = await Throws<InvalidDataException>(() => Files(home).Download(server.Url("/huge.bin"), sha), "a file said to be 3 GiB");
        Check(error.Message == "The file from 127.0.0.1 is larger than 2 GiB" && Listed(home, sha).Length == 0, "the message: " + error.Message);
        // One that says nothing of its length and goes on past the limit.
        SharpFiles.SizeLimit = 1000;
        try
        {
            server.Respond("/unsized.bin", new() { Body = body, Chunked = true, Pieces = 5 });
            var unsized = await Throws<InvalidDataException>(() => Files(home).Download(server.Url("/unsized.bin"), sha), "a file past the limit");
            Check(unsized.Message == "The file from 127.0.0.1 is larger than 1000 bytes" && Listed(home, sha).Length == 0, "the message: " + unsized.Message);
        }
        finally { SharpFiles.SizeLimit = 2L * 1024 * 1024 * 1024; }
        Console.WriteLine("PASS entity tools: a file larger than the limit, by its length or as it arrives, is refused with nothing left");
    }

    static async Task Leftovers(EntityTestHome home, LoopbackServer server)
    {
        var body = Bytes(2_000, 6);
        var sha = Sha(body);
        // What a download that died with the process left behind; and another file of the same checksum, which stays.
        var directory = Directory.CreateDirectory(Path.Combine(FilesRoot(home), sha)).FullName;
        File.WriteAllBytes(Path.Combine(directory, ".0123456789abcdef0123456789abcdef.partial"), body[..500]);
        File.WriteAllBytes(Path.Combine(directory, "other.bin"), body);
        server.Respond("/left.bin", new() { Body = body });
        await Files(home).Download(server.Url("/left.bin"), sha);
        Check(Listed(home, sha).SequenceEqual(["left.bin", "other.bin"]), "a leftover is removed before a download: " + string.Join(", ", Listed(home, sha)));
        Console.WriteLine("PASS entity tools: what a download that died left in its checksum's directory is removed before the next one starts");
    }

    static async Task Closed(EntityTestHome home, LoopbackServer server)
    {
        var body = Bytes(1_000, 7);
        var sha = Sha(body);
        var never = new TaskCompletionSource().Task;
        server.Respond("/closed.bin", new() { Body = body, Before = never });
        var files = Files(home);
        var download = files.Download(server.Url("/closed.bin"), sha);
        await server.Requested("/closed.bin");
        files.Close();
        await Throws<OperationCanceledException>(() => download, "a download when its definition stops");
        Check(Listed(home, sha).Length == 0, "a cancelled download leaves nothing");
        await Throws<ObjectDisposedException>(() => files.Download(server.Url("/closed.bin"), sha), "a download after its definition stopped");
        // The caller's own cancellation.
        using var cancel = new CancellationTokenSource();
        var cancelled = Files(home).Download(server.Url("/closed.bin"), sha, cancellationToken: cancel.Token);
        await server.Requested("/closed.bin", 2);
        cancel.Cancel();
        await Throws<OperationCanceledException>(() => cancelled, "a cancelled download");
        Check(Listed(home, sha).Length == 0 && server.Count("/closed.bin") == 2, "a cancelled download leaves nothing");
        Console.WriteLine("PASS entity tools: closing a definition's downloads cancels the one in flight and refuses any later one; a caller's cancellation ends one too");
    }

    // --- Through the host: the setup limit, and who owns the client. ---

    static CompiledUserScript Compiled(string id, string name, Func<object> factory, params DefinitionSetting[] settings) => new()
    {
        FilePath = $"entity_{id}.cs", FileName = $"entity_{id}", SourceCode = "", Assembly = typeof(EntityToolsTests).Assembly,
        DesignatedRootType = "SharpDefinition",
        Definition = new(id, name, null, settings) { Factory = factory },
    };

    static UserScriptManager Manager(EntityTestHome home) => new() { Pump = new EntityStatePump(TimeSpan.FromMilliseconds(5)), FilesRoot = FilesRoot(home) };

    static async Task<(DefinitionHost? Host, Exception? Failure)> Load(UserScriptManager manager, CompiledUserScript compiled)
    {
        Exception? failure = null;
        await manager.LoadUserScripts([compiled], (_, error) => failure = error);
        return (manager.GetUserScripts().SingleOrDefault(s => s.CompiledScript.FilePath == compiled.FilePath)?.Host, failure);
    }

    static async Task SetupLimit(EntityTestHome home, LoopbackServer server)
    {
        var body = Bytes(6_000, 8);
        var sha = Sha(body);
        DefinitionHost.SetupTimeout = TimeSpan.FromMilliseconds(300);
        // About 600 ms of receiving, twice the setup limit.
        server.Respond("/model.tflite", new() { Body = body, Pieces = 5, Pause = TimeSpan.FromMilliseconds(150) });
        var manager = Manager(home);
        var fetcher = new ToolFetcher(server.Url("/model.tflite"), sha);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (host, failure) = await Load(manager, Compiled("fetcher", "Fetcher", () => fetcher));
        Check(failure == null && host != null, "a download slower than the setup limit failed the setup: " + failure?.Message);
        Check(clock.Elapsed > TimeSpan.FromMilliseconds(500) && fetcher.Path == Path.Combine(FilesRoot(home), sha, "model.tflite"), $"the setup downloaded for {clock.Elapsed.TotalMilliseconds} ms: {fetcher.Path}");
        Check(home.Logs(PyLogLevel.Info).Contains("Fetcher: Downloading model.tflite from 127.0.0.1"), "the host logs a download after the definition's name: " + string.Join(" | ", home.Logs()));
        await manager.UnloadUserScriptsAsync();

        // A server that answers nothing: the wait for it counts.
        var never = new TaskCompletionSource().Task;
        var silentSha = Sha(Bytes(10, 9));
        server.Respond("/silent.tflite", new() { Body = Bytes(10, 9), Before = never });
        manager = Manager(home);
        clock.Restart();
        (host, failure) = await Load(manager, Compiled("silent", "Silent", () => new ToolFetcher(server.Url("/silent.tflite"), silentSha)));
        Check(host == null && failure is TimeoutException && failure.Message == "Silent: Setup() did not finish within 0.3 s", "a download that receives nothing fails the setup: " + failure);
        Check(clock.Elapsed < TimeSpan.FromSeconds(5), $"the setup failed after {clock.Elapsed.TotalMilliseconds} ms");
        // The download the abandoned Setup() still waits on ends with the host.
        await EntityTestHome.Until(() => home.Logs(PyLogLevel.Info).Any(line => line.StartsWith("Silent: Downloading silent.tflite from 127.0.0.1 failed after 0 bytes", StringComparison.Ordinal)), "the abandoned download is cancelled");
        Check(Listed(home, silentSha).Length == 0, "the abandoned download left nothing");
        await manager.UnloadUserScriptsAsync();
        DefinitionHost.SetupTimeout = TimeSpan.FromSeconds(60);
        Console.WriteLine("PASS entity tools: a download slower than the setup limit does not fail the setup, while one whose server answers nothing does, and is cancelled with the host");
    }

    static async Task ClientOwnership(EntityTestHome home, LoopbackServer server)
    {
        var never = new TaskCompletionSource().Task;
        server.Respond("/held", new() { Body = [1], Before = never });
        server.Respond("/stop", LoopbackServer.Reply.Text("bye"));
        // Not attached yet.
        var detached = new ToolPlug(server.Url(""));
        var notYet = await Throws<InvalidOperationException>(() => Task.FromResult(detached.Client), "Http before the component is attached");
        Check(notYet.Message == "ToolPlug is not running yet: use Http from Setup or later, not from the constructor.", "the message: " + notYet.Message);

        var manager = Manager(home);
        var plug = new ToolPlug(server.Url(""));
        var (host, failure) = await Load(manager, Compiled("tool", "Tool", () => plug));
        Check(failure == null && host != null, "the plug loads: " + failure);
        await host!.Started;
        Check(plug.DeviceClient != null && ReferenceEquals(plug.DeviceClient, plug.EntityClient) && ReferenceEquals(plug.DeviceClient, plug.Client), "one client for the device and its entities");
        await server.Requested("/held");
        await manager.UnloadUserScriptsAsync();
        Check(plug.StopRead == "bye", "a request from Stop() works: " + plug.StopRead);
        await Throws<OperationCanceledException>(() => plug.Held!.WaitAsync(TimeSpan.FromSeconds(5)), "the request still in flight when the definition stopped");
        Check(plug.HeldEndedAfterStop, "the request in flight ended only after Stop()");
        var disposed = await Throws<ObjectDisposedException>(() => Task.FromResult(plug.Client), "Http after the definition stopped");
        Check(disposed.Message.StartsWith("Tool has stopped: its HTTP client is disposed", StringComparison.Ordinal), "the message: " + disposed.Message);
        await Throws<ObjectDisposedException>(() => plug.DeviceClient!.GetStringAsync(server.Url("/stop")), "the client kept from before the stop");
        await Throws<ObjectDisposedException>(() => Task.FromResult(plug.Downloads), "Files after the definition stopped");

        // One that never used them: they are not made after the stop either.
        manager = Manager(home);
        var idle = new ToolIdle();
        (host, failure) = await Load(manager, Compiled("idle", "Idle", () => idle));
        Check(failure == null && host != null, "the idle device loads: " + failure);
        await host!.Started;
        await manager.UnloadUserScriptsAsync();
        await Throws<ObjectDisposedException>(() => Task.FromResult(idle.Client), "Http first used after the definition stopped");
        await Throws<ObjectDisposedException>(() => Task.FromResult(idle.Downloads), "Files first used after the definition stopped");
        Console.WriteLine("PASS entity tools: Http is one client for the device and its entities, works from Stop(), is disposed after it (cancelling the request still in flight), and throws ObjectDisposedException after, as Files does");
    }

    // --- A class of a saved library, in its own load context. ---

    const string Library = """
        public class Meter : SharpDevice
        {
            [Setting] public string Server { get; set; } = "";
            [Setting] public string Checksum { get; set; } = "";
            readonly SharpSensor power, table;
            public Meter()
            {
                power = Add(new SharpSensor("power") { Unit = "W" });
                table = Add(new SharpSensor("table"));
            }
            protected override async Task Setup()
            {
                var path = await Files.Download(Server + "/table.txt", Checksum, cancellationToken: Stopping);
                table.Value = System.IO.File.ReadAllText(path);
            }
            protected override Task Start()
            {
                Every(System.TimeSpan.FromMilliseconds(20), async () =>
                    power.Value = double.Parse(await Http.GetStringAsync(Server + "/power", Stopping), System.Globalization.CultureInfo.InvariantCulture));
                return Task.CompletedTask;
            }
        }
        """;

    static async Task LibraryDevice(EntityTestHome home, LoopbackServer server)
    {
        UserScript.RunDrainTimeout = TimeSpan.FromSeconds(3);
        var table = Encoding.UTF8.GetBytes("rates v1");
        server.Respond("/table.txt", new() { Body = table });
        server.Respond("/power", LoopbackServer.Reply.Text("12.5"));
        var api = new HassSharpManager(home.Root);
        try
        {
            await api.Init();
            async Task<JsonObject> Ok(Func<string, Task<string>> request, object body)
            {
                var answer = JsonNode.Parse(await request(JsonSerializer.Serialize(body)))!;
                Check(answer["ok"]!.GetValue<bool>(), "the request failed: " + answer.ToJsonString());
                return answer["result"]!.AsObject();
            }
            object Definition(string sha) => new
            {
                name = "Meter",
                settings = new object[] { new { key = "Server", value = server.Url("") }, new { key = "Checksum", value = sha } },
                logic = new { kind = "class", type = "library:meters:NodeLib.Meter" },
            };
            Check((await Ok(api.NodeLibraryRequest, new { operation = "save", id = "meters", name = "Meters", expectedRevision = 0, source = Library }))["saved"]!.GetValue<bool>(), "the library saves");
            var saved = await Ok(api.EntityRequest, new { operation = "save", id = "meter", expectedRevision = 0, definition = Definition(Sha(table)) });
            Check(saved["active"]!.GetValue<bool>() && saved["document"]!["status"]!.GetValue<string>() == "running", "the meter runs: " + saved.ToJsonString());
            JsonObject[] Meter(int from = 0) => home.Snapshots(from).Where(s => s["uniqueId"]!.GetValue<string>().StartsWith("meter:", StringComparison.Ordinal)).ToArray();
            string? Last(string key) => Meter().LastOrDefault(s => s["uniqueId"]!.GetValue<string>() == "meter:" + key && s["available"]!.GetValue<bool>())?["values"]?["value"]?.ToJsonString();
            await EntityTestHome.Until(() => Last("power") == "12.5" && Last("table") == "\"rates v1\"", "the meter publishes what it read and what it downloaded");
            Check(File.ReadAllBytes(Path.Combine(FilesRoot(home), Sha(table), "table.txt")).SequenceEqual(table), "the download is under the configuration's .storage/hass_sharp/files");
            Check(home.Logs(PyLogLevel.Info).Contains("Meter: Downloading table.txt from 127.0.0.1"), "the download is logged: " + string.Join(" | ", home.Logs()));
            Console.WriteLine("PASS entity tools: a library device downloads a file in Setup() under .storage/hass_sharp/files and publishes what it polls with Http from Every()");

            // The server goes down: the poll fails on every tick, logged once; then it works again, logged once.
            var errors = home.Logs(PyLogLevel.Error).Length;
            // Each failure is logged whole at debug level.
            int Refused() => home.Logs(PyLogLevel.Debug).Count(line => line.StartsWith("Meter: the timer callback every 20 ms failed: ", StringComparison.Ordinal));
            server.Down();
            await EntityTestHome.Until(() => Refused() >= 5, "five refused polls");
            var failed = home.Logs(PyLogLevel.Error).Skip(errors).ToArray();
            server.Up();
            Check(failed.Length == 1 && failed[0].StartsWith("Meter: the timer callback every 20 ms failed: ", StringComparison.Ordinal) && !failed[0].Contains(server.Url("")),
                "a server that is down is logged once: " + string.Join(" | ", failed));
            server.Respond("/power", LoopbackServer.Reply.Text("13"));
            await EntityTestHome.Until(() => Last("power") == "13", "the poll works again");
            Check(home.Logs(PyLogLevel.Info).Count(line => line == "Meter: the timer callback every 20 ms works again") == 1 && home.Logs(PyLogLevel.Error).Length == errors + 1,
                "the recovery is logged once: " + string.Join(" | ", home.Logs()));
            Console.WriteLine("PASS entity tools: a poll of a server that is down logs one error, not one per tick, and one line when it works again");

            // Turned off while a poll waits for the server: the request is cancelled and nothing is published after.
            var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.Respond("/power", new() { Body = Encoding.UTF8.GetBytes("99"), Before = answer.Task });
            var held = server.Count("/power") + 1;
            await server.Requested("/power", held);
            var warnings = home.Logs(PyLogLevel.Warn).Length;
            errors = home.Logs(PyLogLevel.Error).Length;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var off = await Ok(api.EntityRequest, new { operation = "set_enabled", id = "meter", enabled = false });
            Check(!off["active"]!.GetValue<bool>() && clock.Elapsed < TimeSpan.FromSeconds(2), $"turning off waited {clock.Elapsed.TotalMilliseconds} ms: " + off.ToJsonString());
            var batches = home.Pushes().Length;
            answer.SetResult();
            // The answer the cancelled request no longer reads, and a tick that would have come after it.
            await Task.Delay(100);
            Check(!Meter(batches).Any(s => s["available"]!.GetValue<bool>()), "a definition turned off published: " + string.Join(", ", Meter(batches).Select(s => s.ToJsonString())));
            Check(!home.Snapshots().Any(s => s["values"]?["value"]?.ToJsonString() == "99"), "the cancelled request's answer was published");
            Check(home.Logs(PyLogLevel.Warn).Length == warnings && home.Logs(PyLogLevel.Error).Length == errors && server.Count("/power") == held,
                "stopping waited for the poll or logged it: " + string.Join(" | ", home.Logs().TakeLast(5)));
            Console.WriteLine("PASS entity tools: turning a definition off cancels its poll in flight at once, logs nothing for it, and publishes nothing after");

            // A file that does not match its checksum: the definition cannot start, and says why.
            var wrong = Sha(Encoding.UTF8.GetBytes("rates v2"));
            var bad = await Ok(api.EntityRequest, new { operation = "save", id = "rates", expectedRevision = 0, definition = Definition(wrong) });
            Check(!bad["active"]!.GetValue<bool>() && bad["document"]!["status"]!.GetValue<string>() == "failed"
                && bad["activationError"]?.GetValue<string>().Contains("The file from 127.0.0.1 does not match its checksum") == true
                && !bad["activationError"]!.GetValue<string>().Contains(server.Url("")), "a wrong checksum fails the definition: " + bad.ToJsonString());
            Check(Listed(home, wrong).Length == 0, "the wrong file is not kept");
            Console.WriteLine("PASS entity tools: a definition whose Setup() downloads a file with the wrong checksum answers failed with the reason");
        }
        finally { await api.UnloadAsync(); }
    }
}

/// <summary>Downloads a file in its Setup().</summary>
sealed class ToolFetcher : SharpDevice
{
    readonly string url, sha;
    public volatile string? Path;

    public ToolFetcher(string url, string sha)
    {
        (this.url, this.sha) = (url, sha);
        Add(new SharpSensor("table"));
    }

    protected internal override async Task Setup() => Path = await Files.Download(url, sha, cancellationToken: Stopping);
}

/// <summary>
/// Leaves a request in flight from Start() that does not pass Stopping, and makes one from Stop(); shows its client and
/// its entity's.
/// </summary>
sealed class ToolPlug : SharpDevice
{
    readonly string server;
    readonly ToolReading reading;
    public HttpClient? DeviceClient, EntityClient;
    public Task? Held;
    public volatile string? StopRead;
    public volatile bool HeldEndedAfterStop;

    public ToolPlug(string server)
    {
        this.server = server;
        reading = Add(new ToolReading());
    }

    public HttpClient Client => Http;
    public SharpFiles Downloads => Files;

    protected internal override Task Start()
    {
        DeviceClient = Http;
        EntityClient = reading.Client;
        Held = Hold();
        return Task.CompletedTask;
    }

    async Task Hold()
    {
        try { await Http.GetStringAsync(server + "/held"); }
        finally { HeldEndedAfterStop = StopRead != null; }
    }

    protected internal override async Task Stop() => StopRead = await Http.GetStringAsync(server + "/stop");
}

sealed class ToolReading : SharpSensor
{
    public ToolReading() : base("reading") { }
    public HttpClient Client => Http;
}

/// <summary>Uses neither Http nor Files while it runs.</summary>
sealed class ToolIdle : SharpDevice
{
    public ToolIdle() { Add(new SharpSensor("idle")); }
    public HttpClient Client => Http;
    public SharpFiles Downloads => Files;
}
