using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>
/// The camera of a class definition, through a whole manager (<see cref="EntityTestHome"/>) and
/// <see cref="HassSharpManager.CameraImage"/>: a camera answers with the frame it showed, and one that makes its frames
/// makes at most one at a time and one per interval, shared by the requests that come meanwhile; a frame it cannot make
/// leaves the last good one for a while, logged once per reason and once on recovery; making one is limited in time and
/// cancelled when the definition stops; a picture taken of another camera arrives through the stubbed delegate. Also the
/// option <c>frame_interval</c> of a camera built from nodes, the hook a graph's frame flow fills, and a camera without
/// libjpeg-turbo. The interval, the time limit and the time the last good frame is kept are shortened here.
/// </summary>
static class CameraTests
{
    const string Library = """
        public class Still : SharpCamera
        {
            protected override Task Start() { Show(SharpImage.Blank(16, 8, SharpColor.Blue)); return Task.CompletedTask; }
        }
        public class Dark : SharpCamera { }
        public class Feed : SharpDevice
        {
            public Feed()
            {
                Add(new SharpCamera("view", "View") { OnFrame = () => ((Func<CancellationToken, Task<SharpImage?>>)AppContext.GetData("HassSharp.CameraTests.Feed")!)(Stopping) });
                Add(new SharpSwitch("relay"));
            }
        }
        public class Door : SharpCamera
        {
            public Door() : base("main", "Door") { }
            protected override async Task<SharpImage?> Frame()
            {
                try { return (await CameraSnapshot("camera.porch")).DrawRect(2, 2, 8, 4); }
                catch (Exception failure) { AppContext.SetData("HassSharp.CameraTests.Door", failure.GetType().Name); throw; }
            }
        }
        public class Relay : SharpCamera
        {
            public Relay() : base("main", "Relay") { }
            protected override async Task<SharpImage?> Frame() => await CameraSnapshot("camera.porch");
        }
        public class Marked : SharpCamera
        {
            public Marked() : base("main", "Marked") { }
            protected override Task Start() { Show(SharpImage.Blank(16, 8).DrawRect(1, 1, 4, 4)); return Task.CompletedTask; }
        }
        public class Slow : SharpCamera
        {
            public Slow() : base("main", "Slow") { FrameInterval = 60; }
            protected override Task<SharpImage?> Frame() => ((Func<CancellationToken, Task<SharpImage?>>)AppContext.GetData("HassSharp.CameraTests.Slow")!)(Stopping);
        }
        public class Pusher : SharpCamera
        {
            public Pusher() : base("main", "Pusher") { }
            protected override async Task Start() { if (AppContext.GetData("HassSharp.CameraTests.Start") is Func<Task> wait) await wait(); }
            protected override async Task Stop() { if (AppContext.GetData("HassSharp.CameraTests.Stop") is Func<Task> wait) await wait(); }
        }
        """;
    const string FeedKey = "HassSharp.CameraTests.Feed", DoorKey = "HassSharp.CameraTests.Door", StartKey = "HassSharp.CameraTests.Start", StopKey = "HassSharp.CameraTests.Stop", SlowKey = "HassSharp.CameraTests.Slow";

    static int calls;
    // What the feed's next frames do; each call is counted.
    static void Feed(Func<CancellationToken, Task<SharpImage?>> frame) =>
        AppContext.SetData(FeedKey, new Func<CancellationToken, Task<SharpImage?>>(token => { Interlocked.Increment(ref calls); return frame(token); }));
    static int Calls => Volatile.Read(ref calls);
    static Func<CancellationToken, Task<SharpImage?>> Picture(int width) => _ => Task.FromResult<SharpImage?>(SharpImage.Blank(width, 8, SharpColor.Green));

    static int Width(byte[] jpeg)
    {
        TurboJpeg.Header(jpeg, out var width, out _);
        return width;
    }

    static async Task<T> Throws<T>(Func<Task> action, string what) where T : Exception
    {
        try { await action(); }
        catch (T error) when (error.GetType() == typeof(T)) { return error; }
        catch (Exception error) { throw new Exception($"{what}: threw {error.GetType().Name} instead of {typeof(T).Name}: {error.Message}"); }
        throw new Exception($"{what}: did not throw {typeof(T).Name}");
    }

    static object Definition(string name, string type) => new { name, settings = Array.Empty<object>(), logic = new { kind = "class", type = "library:cams:NodeLib." + type } };

    static UserScriptManager Scripts(HassSharpManager api) =>
        (UserScriptManager)typeof(HassSharpManager).GetField("_userScriptManager", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(api)!;

    static (DefinitionHost Host, SharpCamera Camera) Running(HassSharpManager api, string uniqueId) =>
        Scripts(api).Entities.TryGet(uniqueId, out var host, out var entity) ? (host, (SharpCamera)entity) : throw new Exception(uniqueId + " is not running");

    [RuntimeSuite("--camera", Order = 16)]
    public static async Task Run()
    {
        var (interval, limit, good, drain) = (CameraFrames.IntervalSecond, CameraFrames.MakeLimit, CameraFrames.GoodFor, UserScript.RunDrainTimeout);
        await using var home = new EntityTestHome("camera");
        var api = new HassSharpManager(home.Root);
        try
        {
            await api.Init();
            async Task<JsonObject> Ok(object request)
            {
                var answer = JsonNode.Parse(await api.EntityRequest(JsonSerializer.Serialize(request)))!;
                Check(answer["ok"]!.GetValue<bool>(), "the request failed: " + answer.ToJsonString());
                return answer["result"]!.AsObject();
            }
            var library = JsonNode.Parse(await api.NodeLibraryRequest(JsonSerializer.Serialize(new { operation = "save", id = "cams", name = "Cams", expectedRevision = 0, source = Library })))!;
            Check(library["ok"]!.GetValue<bool>() && library["result"]!["saved"]!.GetValue<bool>(), "the library saves: " + library.ToJsonString());
            Feed(Picture(20));
            foreach (var (id, name, type) in new[] { ("still", "Still", "Still"), ("dark", "Dark", "Dark"), ("feed", "Feed", "Feed"), ("door", "Door", "Door"), ("relay", "Relay", "Relay"), ("marked", "Marked", "Marked"), ("pusher", "Pusher", "Pusher") })
            {
                var saved = await Ok(new { operation = "save", id, expectedRevision = 0, definition = Definition(name, type) });
                Check(saved["active"]!.GetValue<bool>() && saved["document"]!["status"]!.GetValue<string>() == "running", $"{id} runs: " + saved.ToJsonString());
            }
            var synced = JsonNode.Parse(home.Syncs("feed").Last())!["entities"]!.AsArray().Single(e => e!["key"]!.GetValue<string>() == "view")!;
            Check(synced["domain"]!.GetValue<string>() == "camera" && synced["commands"]!.ToJsonString() == "[]" && synced["options"]!.ToJsonString() == """{"frame_interval":1}""",
                "a camera is synced with no commands and its frame interval: " + synced.ToJsonString());

            await Shown(api);
            await Shared(api);
            await Failing(api, home);
            await LongInterval(api, home, Ok);
            await Unencodable(api, home);
            await Saved(api, home, Ok);
            await Stopped(api, home, Ok);
            await Snapshots(api, home, Ok);
            await Options(api, home, Ok);
            await Recovered(api, home);
            await WithoutTheLibrary(api, home);
        }
        finally
        {
            await api.UnloadAsync();
            (CameraFrames.IntervalSecond, CameraFrames.MakeLimit, CameraFrames.GoodFor, UserScript.RunDrainTimeout) = (interval, limit, good, drain);
            AppContext.SetData(FeedKey, null);
            AppContext.SetData(DoorKey, null);
            AppContext.SetData(StartKey, null);
            AppContext.SetData(StopKey, null);
            AppContext.SetData(SlowKey, null);
        }
    }

    static async Task Shown(HassSharpManager api)
    {
        var expected = SharpImage.Blank(16, 8, SharpColor.Blue).ToJpeg();
        Check((await api.CameraImage("still:main")).AsSpan().SequenceEqual(expected) && (await api.CameraImage("still:main")).AsSpan().SequenceEqual(expected), "a camera answers with the frame it showed");
        Check(Running(api, "still:main").Camera.Frames.Begun == 0, "a camera that does not make its frames is never asked for one");
        var dark = await Throws<InvalidOperationException>(() => api.CameraImage("dark:main"), "a camera that has shown nothing");
        Check(dark.Message == "main has not shown a frame yet", "a camera that has shown nothing: " + dark.Message);
        var relay = await Throws<InvalidOperationException>(() => api.CameraImage("feed:relay"), "a switch");
        Check(relay.Message == "The entity is not a camera", "a switch is not a camera: " + relay.Message);
        var nothing = await Throws<InvalidOperationException>(() => api.CameraImage("nothing:main"), "an entity of no definition");
        Check(nothing.Message == "The entity is not running", "an entity of no definition: " + nothing.Message);
        Console.WriteLine("PASS camera: a camera answers the frame it showed without being asked to make one; one that showed nothing, a switch and an unknown entity are refused");
    }

    static async Task Shared(HassSharpManager api)
    {
        // Within 0.8 of an interval of one second: one frame for five requests one after the other.
        CameraFrames.IntervalSecond = TimeSpan.FromSeconds(1);
        var before = Calls;
        for (var i = 0; i < 5; i++) Check(Width(await api.CameraImage("feed:view")) == 20, "the feed's frame");
        Check(Calls == before + 1, $"five requests within 0.8 of an interval make one frame, not {Calls - before}");

        // An interval of 20 ms from now on. Five requests at once share the frame being made.
        CameraFrames.IntervalSecond = TimeSpan.FromMilliseconds(20);
        await Task.Delay(30);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Feed(async token => { await gate.Task.WaitAsync(token); return SharpImage.Blank(21, 8); });
        before = Calls;
        var requests = Enumerable.Range(0, 5).Select(_ => api.CameraImage("feed:view")).ToArray();
        await EntityTestHome.Until(() => Calls == before + 1, "the first request makes a frame");
        await Task.Delay(50);
        Check(Calls == before + 1 && requests.All(r => !r.IsCompleted), "the other requests wait for the frame being made");
        gate.SetResult();
        Check((await Task.WhenAll(requests)).All(jpeg => Width(jpeg) == 21) && Calls == before + 1, "five requests at once get the one frame made");

        // Requests an interval apart each get a frame of their own.
        var width = 22;
        Feed(_ => Task.FromResult<SharpImage?>(SharpImage.Blank(Interlocked.Increment(ref width) - 1, 8)));
        before = Calls;
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(30);
            Check(Width(await api.CameraImage("feed:view")) == 22 + i, $"request {i + 1} an interval after the one before gets a new frame");
        }
        Check(Calls == before + 3, $"three requests an interval apart make three frames, not {Calls - before}");

        // A frame of none keeps the frame shown.
        Feed(_ => Task.FromResult<SharpImage?>(null));
        await Task.Delay(30);
        Check(Width(await api.CameraImage("feed:view")) == 24 && Calls == before + 4, "a frame of none keeps the frame shown before");
        Console.WriteLine("PASS camera: requests within 0.8 of an interval share one frame, requests at once share the frame being made, requests an interval apart each make one, and a frame of none keeps the one shown");
    }

    static async Task Failing(HassSharpManager api, EntityTestHome home)
    {
        string[] Errors() => home.Logs(PyLogLevel.Error).Where(line => line.StartsWith("Feed: View (view) could not make a frame: ", StringComparison.Ordinal)).ToArray();
        Feed(_ => throw new InvalidOperationException("the lens is covered"));
        var before = Calls;
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(30);
            Check(Width(await api.CameraImage("feed:view")) == 24, $"failure {i + 1} answers the last good frame");
        }
        Check(Calls == before + 6, $"six requests an interval apart try six times, not {Calls - before}");
        Check(Errors().SequenceEqual(["Feed: View (view) could not make a frame: the lens is covered"]), "six failures for one reason log one error: " + string.Join(" | ", Errors()));
        Check(home.Logs(PyLogLevel.Debug).Count(line => line.StartsWith("Feed: View (view) could not make a frame: System.InvalidOperationException: the lens is covered", StringComparison.Ordinal)) == 6,
            "each failure is logged whole at Debug");
        Feed(Picture(25));
        await Task.Delay(30);
        Check(Width(await api.CameraImage("feed:view")) == 25, "the camera makes frames again");
        await Task.Delay(30);
        Check(Width(await api.CameraImage("feed:view")) == 25, "and again");
        Check(home.Logs(PyLogLevel.Info).Count(line => line == "Feed: View (view) makes frames again") == 1 && Errors().Length == 1, "the recovery is logged once: " + string.Join(" | ", home.Logs()));

        // The last good frame is given for a while (1 s here, 30 s in Home Assistant), then the request fails with the reason.
        CameraFrames.GoodFor = TimeSpan.FromSeconds(1);
        var since = System.Diagnostics.Stopwatch.StartNew();
        Feed(_ => throw new InvalidOperationException("the camera is off"));
        await Task.Delay(30);
        Check(Width(await api.CameraImage("feed:view")) == 25, "a failure soon after the last good frame answers it");
        await Task.Delay(TimeSpan.FromSeconds(1.2) - since.Elapsed);
        var stale = await Throws<InvalidOperationException>(() => api.CameraImage("feed:view"), "a failure long after the last good frame");
        Check(stale.Message == "View (view) could not make a frame: the camera is off", "the request fails with the reason: " + stale.Message);
        Check(Errors().Length == 2, "another reason logs again: " + string.Join(" | ", Errors()));

        // A frame slower than the limit fails as a timeout. While it still runs no other is started, however long it
        // hangs: the requests get its outcome, the last good frame for a while and then the timeout. Once it has ended,
        // late (what it gives is dropped), the next request starts a new one.
        CameraFrames.MakeLimit = TimeSpan.FromMilliseconds(200);
        CameraFrames.GoodFor = TimeSpan.FromSeconds(1.5);
        Feed(Picture(26));
        await Task.Delay(30);
        Check(Width(await api.CameraImage("feed:view")) == 26, "a good frame before the slow one");
        var good = System.Diagnostics.Stopwatch.StartNew();
        var late = new TaskCompletionSource<SharpImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Feed(token => late.Task.WaitAsync(token));
        before = Calls;
        await Task.Delay(30);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Check(Width(await api.CameraImage("feed:view")) == 26 && clock.Elapsed >= TimeSpan.FromMilliseconds(190) && clock.Elapsed < TimeSpan.FromSeconds(1),
            $"a frame slower than the limit answers the last good one after the limit ({clock.ElapsedMilliseconds} ms)");
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(30);
            Check(Width(await api.CameraImage("feed:view")) == 26, $"request {i + 1} while the slow frame still runs answers the last good one");
        }
        Check(Calls == before + 1, $"no second frame is started while the slow one still runs, not {Calls - before}");
        await Task.Delay(TimeSpan.FromSeconds(1.8) - good.Elapsed);
        var slow = await Throws<TimeoutException>(() => api.CameraImage("feed:view"), "a request long after the last good frame, the slow one still running");
        Check(slow.Message == "View (view) could not make a frame: it took longer than 0.2 s", "the request fails with the timeout: " + slow.Message);
        Check(Calls == before + 1, $"still no second frame is started, not {Calls - before}");
        Check(Errors().Count(line => line == "Feed: View (view) could not make a frame: it took longer than 0.2 s") == 1, "the timeout is logged once: " + string.Join(" | ", Errors()));
        late.SetResult(SharpImage.Blank(27, 8));
        Feed(Picture(28));
        // Its end reaches the host through a pool continuation.
        var view = Running(api, "feed:view").Camera;
        await EntityTestHome.Until(() => !view.Frames.Busy, "the slow frame has ended");
        Check(Width(await api.CameraImage("feed:view")) == 28, "once the slow frame has ended, the next request makes a new one, and the late frame is not shown");
        Check(Calls == before + 2, $"the next request starts a frame of its own, not {Calls - before - 1}");
        CameraFrames.GoodFor = TimeSpan.FromSeconds(30);
        Console.WriteLine("PASS camera: a frame that fails answers the last good one, logged once per reason and once when it works again; after a while the request fails with the reason, a frame slower than the limit fails as a timeout, no other starts while it runs, and the next request after it has ended starts another");
    }

    /// <summary>
    /// A camera of a long interval (60 s, here 60 × 20 ms) keeps its last good frame for two intervals, longer than the
    /// shortest time (here 100 ms), and tries again soon after a failure (within a second, here 20 ms) rather than sharing
    /// the failed attempt for 0.8 of its interval.
    /// </summary>
    static async Task LongInterval(HassSharpManager api, EntityTestHome home, Func<object, Task<JsonObject>> ok)
    {
        CameraFrames.IntervalSecond = TimeSpan.FromMilliseconds(20);
        CameraFrames.GoodFor = TimeSpan.FromMilliseconds(100);
        try
        {
            AppContext.SetData(SlowKey, new Func<CancellationToken, Task<SharpImage?>>(_ => Task.FromResult<SharpImage?>(SharpImage.Blank(40, 8))));
            var saved = await ok(new { operation = "save", id = "slow", expectedRevision = 0, definition = Definition("Slow", "Slow") });
            Check(saved["active"]!.GetValue<bool>(), "the slow camera runs: " + saved.ToJsonString());
            var (_, slow) = Running(api, "slow:main");
            Check(slow.FrameInterval == 60, "the camera's interval is 60 s");
            Check(Width(await api.CameraImage("slow:main")) == 40, "the first frame");
            var good = System.Diagnostics.Stopwatch.StartNew();
            var made = slow.Frames.Begun;
            // Past 0.8 of the interval (0.96 s here) and past the shortest time, within two intervals (2.4 s here).
            AppContext.SetData(SlowKey, new Func<CancellationToken, Task<SharpImage?>>(_ => throw new InvalidOperationException("the lens fogged up")));
            await Task.Delay(TimeSpan.FromSeconds(1.2) - good.Elapsed);
            Check(Width(await api.CameraImage("slow:main")) == 40 && slow.Frames.Begun == made + 1, $"one failure of a camera of a long interval answers its last good frame ({slow.Frames.Begun - made} made)");
            // A failed attempt is not shared for 0.8 of the interval: the next request a little later tries again.
            AppContext.SetData(SlowKey, new Func<CancellationToken, Task<SharpImage?>>(_ => Task.FromResult<SharpImage?>(SharpImage.Blank(41, 8))));
            await EntityTestHome.Until(() => !slow.Frames.Busy, "the failed attempt has ended");
            await Task.Delay(60);
            Check(Width(await api.CameraImage("slow:main")) == 41 && slow.Frames.Begun == made + 2, $"a failed attempt does not hold up the next one for 0.8 of the interval ({slow.Frames.Begun - made} made)");
            Check(home.Logs(PyLogLevel.Info).Contains("Slow: Slow (main) makes frames again"), "and it makes frames again: " + string.Join(" | ", home.Logs()));
        }
        finally
        {
            CameraFrames.GoodFor = TimeSpan.FromSeconds(30);
            AppContext.SetData(SlowKey, null);
        }
        Console.WriteLine("PASS camera: a camera of a long interval keeps its last good frame for two intervals, and tries again soon after a failure");
    }

    // A JPEG whose start-of-frame says side × side, with the data of a small picture.
    static byte[] Claims(int side)
    {
        var jpeg = SharpImage.Blank(64, 48).ToJpeg();
        for (var i = 2; i + 8 < jpeg.Length; i++)
            if (jpeg[i] == 0xFF && jpeg[i + 1] is 0xC0 or 0xC1 or 0xC2)
            {
                jpeg[i + 5] = (byte)(side >> 8); jpeg[i + 6] = (byte)(side & 0xFF);
                jpeg[i + 7] = (byte)(side >> 8); jpeg[i + 8] = (byte)(side & 0xFF);
                return jpeg;
            }
        throw new Exception("no start of frame");
    }

    // A JPEG that starts as one and whose start-of-frame names a precision of 7 bits, which no decoder takes: it cannot be read at all.
    static byte[] Damaged()
    {
        var jpeg = SharpImage.Blank(64, 48).ToJpeg();
        for (var i = 2; i + 4 < jpeg.Length; i++)
            if (jpeg[i] == 0xFF && jpeg[i + 1] is 0xC0 or 0xC1 or 0xC2)
            {
                jpeg[i + 4] = 7;
                return jpeg;
            }
        throw new Exception("no start of frame");
    }

    /// <summary>
    /// A frame that cannot be encoded (a picture that claims 12000 × 12000 with a rectangle drawn on it, a damaged JPEG
    /// that starts as one) leaves the last frame whose JPEG was made as the answer for the last-good time (here 1 s), then
    /// fails: one line for each reason, and one when a frame can be encoded again.
    /// </summary>
    static async Task Unencodable(HassSharpManager api, EntityTestHome home)
    {
        string[] Errors() => home.Logs(PyLogLevel.Error).Where(line => line.StartsWith("Pusher: Pusher (main) could not make a frame: ", StringComparison.Ordinal)).ToArray();
        CameraFrames.GoodFor = TimeSpan.FromSeconds(1);
        try
        {
            var (_, pusher) = Running(api, "pusher:main");
            pusher.Show(SharpImage.Blank(34, 8));
            Check(Width(await api.CameraImage("pusher:main")) == 34, "the good frame");
            var good = System.Diagnostics.Stopwatch.StartNew();
            pusher.Show(SharpImage.Load(Claims(12000)).DrawRect(1, 1, 4, 4));
            for (var i = 0; i < 2; i++) Check(Width(await api.CameraImage("pusher:main")) == 34, $"request {i + 1} for a picture over 40 megapixels answers the last good frame");
            pusher.Show(SharpImage.Load(Damaged()).DrawRect(1, 1, 4, 4));
            Check(Width(await api.CameraImage("pusher:main")) == 34, "a damaged picture answers the last good frame");
            Check(Errors().Length == 2 && Errors()[0] == "Pusher: Pusher (main) could not make a frame: The picture is larger than 40 megapixels" && Errors()[1].StartsWith("Pusher: Pusher (main) could not make a frame: The picture could not be read", StringComparison.Ordinal),
                "one line for each reason: " + string.Join(" | ", Errors()));
            await Task.Delay(TimeSpan.FromSeconds(1.3) - good.Elapsed);
            var failed = await Throws<InvalidOperationException>(() => api.CameraImage("pusher:main"), "a damaged picture long after the last good frame");
            Check(failed.Message.StartsWith("Pusher (main) could not make a frame: The picture could not be read", StringComparison.Ordinal), "the request fails with the reason: " + failed.Message);
            Check(Errors().Length == 2, "the same reason is not logged again: " + string.Join(" | ", Errors()));
            pusher.Show(SharpImage.Blank(36, 8));
            Check(Width(await api.CameraImage("pusher:main")) == 36 && Width(await api.CameraImage("pusher:main")) == 36, "a frame that can be encoded is the answer again");
            Check(home.Logs(PyLogLevel.Info).Count(line => line == "Pusher: Pusher (main) makes frames again") == 1, "and that is said once: " + string.Join(" | ", home.Logs()));
        }
        finally { CameraFrames.GoodFor = TimeSpan.FromSeconds(30); }
        Console.WriteLine("PASS camera: a frame that cannot be encoded answers the last good one for the last-good time and then fails, one line for each reason and one on recovery");
    }

    /// <summary>
    /// A new revision of a camera that shows its frames takes over the frame of the one it replaces: a request right after
    /// the save gets it, before anything shows a new one, and so does a request while the new revision starts.
    /// </summary>
    static async Task Saved(HassSharpManager api, EntityTestHome home, Func<object, Task<JsonObject>> ok)
    {
        var (_, pusher) = Running(api, "pusher:main");
        pusher.Show(SharpImage.Blank(35, 8));
        Check(Width(await api.CameraImage("pusher:main")) == 35, "the frame shown");
        var revision = (await ok(new { operation = "read", id = "pusher" }))["revision"]!.GetValue<long>();
        var saved = await ok(new { operation = "save", id = "pusher", expectedRevision = revision, definition = Definition("Pusher", "Pusher") });
        Check(saved["active"]!.GetValue<bool>(), "the new revision runs: " + saved.ToJsonString());
        Check(!ReferenceEquals(Running(api, "pusher:main").Camera, pusher), "the new revision has a camera of its own");
        Check(Width(await api.CameraImage("pusher:main")) == 35, "a request right after the save gets the frame the replaced revision showed");

        // While the new revision starts (its Start() waits here), a request gets the frame, and none is made.
        var starting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AppContext.SetData(StartKey, new Func<Task>(() => starting.Task));
        try
        {
            var before = Running(api, "pusher:main").Host;
            var again = ok(new { operation = "save", id = "pusher", expectedRevision = revision + 1, definition = Definition("Pusher", "Pusher") });
            await EntityTestHome.Until(() => Running(api, "pusher:main").Host is var host && host != before && host.Status == "setup", "the new revision starts");
            Check(Width(await api.CameraImage("pusher:main")) == 35, "a request while the new revision starts gets the frame");
            starting.SetResult();
            Check((await again)["active"]!.GetValue<bool>(), "the third revision runs");
        }
        finally { AppContext.SetData(StartKey, null); }
        Check(Width(await api.CameraImage("pusher:main")) == 35, "and keeps it once it runs");

        // While the replaced revision still stops (its Stop() waits here), a request to it gets the frame too.
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AppContext.SetData(StopKey, new Func<Task>(() => stopping.Task));
        try
        {
            var old = Running(api, "pusher:main").Host;
            var third = ok(new { operation = "save", id = "pusher", expectedRevision = revision + 2, definition = Definition("Pusher", "Pusher") });
            await EntityTestHome.Until(() => old.Status == "stopped", "the replaced revision stops");
            Check(Running(api, "pusher:main").Host == old, "the replaced revision still answers for the camera while it stops");
            Check(Width(await api.CameraImage("pusher:main")) == 35, "a request while the replaced revision stops gets the frame");
            stopping.SetResult();
            Check((await third)["active"]!.GetValue<bool>(), "the fourth revision runs");
        }
        finally { AppContext.SetData(StopKey, null); }
        Check(Width(await api.CameraImage("pusher:main")) == 35, "and the fourth revision has it too");
        Check(!home.Logs(PyLogLevel.Error).Any(line => line.StartsWith("Pusher: ", StringComparison.Ordinal) && !line.Contains("could not make a frame: The picture", StringComparison.Ordinal)), "nothing fails: " + string.Join(" | ", home.Logs(PyLogLevel.Error)));
        Console.WriteLine("PASS camera: a new revision of a camera takes over the frame of the one it replaces, also while it starts, and the one it replaces answers it while it stops");
    }

    static async Task Stopped(HassSharpManager api, EntityTestHome home, Func<object, Task<JsonObject>> ok)
    {
        // Turned off while a frame is being made: the frame is cancelled, the request is answered at once with the last
        // good frame, a later one is refused.
        var cancelled = 0;
        Feed(async token =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
            return null;
        });
        await Task.Delay(30);
        var before = Calls;
        var waiting = api.CameraImage("feed:view");
        await EntityTestHome.Until(() => Calls == before + 1, "a frame is being made");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var off = await ok(new { operation = "set_enabled", id = "feed", enabled = false });
        Check(!off["active"]!.GetValue<bool>() && clock.Elapsed < TimeSpan.FromSeconds(2), $"turning off took {clock.ElapsedMilliseconds} ms: " + off.ToJsonString());
        Check(Width(await waiting) == 28 && Volatile.Read(ref cancelled) >= 1, $"the request in flight gets the last good frame, its frame cancelled ({cancelled})");
        var later = await Throws<InvalidOperationException>(() => api.CameraImage("feed:view"), "a request to the stopped definition");
        Check(later.Message == "The entity is not running", "a request to the stopped definition: " + later.Message);
        Check(!home.Logs(PyLogLevel.Error).Any(line => line.Contains("could not make a frame: ") && line.Contains("cancel")), "a cancelled frame is no failure: " + string.Join(" | ", home.Logs(PyLogLevel.Error)));

        // Turned on again, it has no frame: a frame being made when it is turned off again ends its request with the refusal.
        var on = await ok(new { operation = "set_enabled", id = "feed", enabled = true });
        Check(on["active"]!.GetValue<bool>(), "the feed runs again: " + on.ToJsonString());
        await Task.Delay(30);
        before = Calls;
        waiting = api.CameraImage("feed:view");
        await EntityTestHome.Until(() => Calls == before + 1, "a frame is being made");
        await ok(new { operation = "set_enabled", id = "feed", enabled = false });
        var ended = await Throws<InvalidOperationException>(() => waiting, "the request in flight to a camera with no frame");
        Check(ended.Message == "The entity is not running", "the request in flight to a camera with no frame ends: " + ended.Message);
        on = await ok(new { operation = "set_enabled", id = "feed", enabled = true });
        Check(on["active"]!.GetValue<bool>(), "the feed runs again: " + on.ToJsonString());

        // A new revision saved while a frame is being made: the request to the old one gets its last good frame, the
        // new one serves frames.
        Feed(Picture(29));
        await Task.Delay(30);
        Check(Width(await api.CameraImage("feed:view")) == 29, "a good frame before the save");
        await Task.Delay(30);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Feed(async token => { await gate.Task.WaitAsync(token); return SharpImage.Blank(30, 8); });
        before = Calls;
        waiting = api.CameraImage("feed:view");
        await EntityTestHome.Until(() => Calls == before + 1, "a frame is being made");
        var revision = (await ok(new { operation = "read", id = "feed" }))["revision"]!.GetValue<long>();
        var saved = await ok(new { operation = "save", id = "feed", expectedRevision = revision, definition = Definition("Feed", "Feed") });
        Check(saved["active"]!.GetValue<bool>(), "the new revision runs: " + saved.ToJsonString());
        Check(Width(await waiting) == 29, "the request in flight to the replaced revision gets its last good frame");
        Feed(Picture(31));
        Check(Width(await api.CameraImage("feed:view")) == 31, "the new revision serves frames");
        gate.SetResult();
        Console.WriteLine("PASS camera: turning a definition off cancels the frame being made and answers its request at once with the last good frame, or refuses it when there is none; later requests are refused; a revision saved meanwhile answers the request in flight with the last good frame and serves frames");
    }

    static async Task Snapshots(HassSharpManager api, EntityTestHome home, Func<object, Task<JsonObject>> ok)
    {
        // A picture of another camera, drawn on.
        var porch = SharpImage.Blank(40, 30, SharpColor.White).ToJpeg();
        home.AnswerPictures = (_, _, answer, _) => answer(porch);
        var asked = home.Pictures().Length;
        var drawn = await api.CameraImage("door:main");
        Check(drawn is [0xFF, 0xD8, ..] && Width(drawn) == 40 && !drawn.AsSpan().SequenceEqual(porch), "the camera answers the picture it took, drawn on");
        Check(home.Pictures().Skip(asked).SequenceEqual([("door", "camera.porch")]), "the picture is asked for by the definition's id and the camera's entity id: " + string.Join(", ", home.Pictures()));

        // Home Assistant's error, and no picture at all, once the last good frame is too old.
        CameraFrames.GoodFor = TimeSpan.FromMilliseconds(50);
        await Task.Delay(80);
        home.AnswerPictures = (_, _, _, fail) => fail("Camera camera.porch is off");
        var off = await Throws<InvalidOperationException>(() => api.CameraImage("door:main"), "a camera that is off");
        Check(off.Message == "Door (main) could not make a frame: Camera camera.porch is off", "Home Assistant's error: " + off.Message);
        await Task.Delay(30);
        home.AnswerPictures = (_, _, answer, _) => answer([]);
        var empty = await Throws<InvalidOperationException>(() => api.CameraImage("door:main"), "a camera that gives nothing");
        Check(empty.Message == "Door (main) could not make a frame: camera.porch gave no picture", "no picture: " + empty.Message);
        // Home Assistant or HassSharp stopping while the picture is taken is no failure of the camera: no line is logged.
        string[] DoorErrors() => home.Logs(PyLogLevel.Error).Where(line => line.StartsWith("Door: ", StringComparison.Ordinal)).ToArray();
        var logged = DoorErrors().Length;
        foreach (var stopping in new[] { "Home Assistant is stopping, so no picture was taken", "HassSharp is stopping, so no picture was taken" })
        {
            await Task.Delay(30);
            home.AnswerPictures = (_, _, _, fail) => fail(stopping);
            var stopped = await Throws<InvalidOperationException>(() => api.CameraImage("door:main"), stopping);
            Check(stopped.Message == "Door (main) could not make a frame: " + stopping, "the request says why: " + stopped.Message);
        }
        Check(DoorErrors().Length == logged, "a picture not taken because Home Assistant or HassSharp stops is not logged as a failure: " + string.Join(" | ", DoorErrors()));
        CameraFrames.GoodFor = TimeSpan.FromSeconds(30);

        // A picture still being taken when the definition stops is cut: from Stop(), as a service call is, once stopping
        // stops waiting for the definition's code (300 ms here).
        UserScript.RunDrainTimeout = TimeSpan.FromMilliseconds(300);
        home.AnswerPictures = (_, _, _, _) => { };
        AppContext.SetData(DoorKey, null);
        await Task.Delay(30);
        asked = home.Pictures().Length;
        var waiting = api.CameraImage("door:main");
        await EntityTestHome.Until(() => home.Pictures().Length > asked, "the picture is asked for");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await ok(new { operation = "set_enabled", id = "door", enabled = false });
        Check(clock.Elapsed < TimeSpan.FromSeconds(2), $"turning off took {clock.ElapsedMilliseconds} ms");
        Check(Width(await waiting) == 40, "the request gets the last good frame");
        await EntityTestHome.Until(() => AppContext.GetData(DoorKey) as string == nameof(TaskCanceledException), "the picture being taken is cut");

        // Without the delegate: no pictures here.
        var begin = PyInterop.BeginCameraSnapshot;
        PyInterop.BeginCameraSnapshot = null!;
        try
        {
            var unavailable = await Throws<InvalidOperationException>(() => HassCameras.SnapshotAsync("", "camera.porch", CancellationToken.None), "no delegate");
            Check(unavailable.Message == "Pictures from cameras are not available here", "no delegate: " + unavailable.Message);
        }
        finally { PyInterop.BeginCameraSnapshot = begin; }
        Console.WriteLine("PASS camera: a class takes a picture of another camera through Home Assistant and draws on it; Home Assistant's error and an empty answer fail with their reason, and a picture being taken is cut when the definition stops, its request answered with the last good frame; a picture not taken because Home Assistant or HassSharp stops is not logged");
    }

    static async Task Options(HassSharpManager api, EntityTestHome home, Func<object, Task<JsonObject>> ok)
    {
        object Lens(object? frameInterval) => new
        {
            name = "Lens",
            settings = Array.Empty<object>(),
            entities = new[] { new { key = "lens", domain = "camera", name = "Lens", options = new { frame_interval = frameInterval } } },
            logic = new { kind = "graph", graph = new JsonObject { ["version"] = 5, ["name"] = "Lens", ["nodes"] = new JsonArray(), ["edges"] = new JsonArray() } },
        };
        foreach (var refused in new[] { 0.1, 100 })
        {
            var answer = JsonNode.Parse(await api.EntityRequest(JsonSerializer.Serialize(new { operation = "save", id = "lens", expectedRevision = 0, definition = Lens(refused) })))!;
            var problem = answer["error"]?["detail"]?["diagnostics"]?.AsArray().SingleOrDefault();
            var shown = refused.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Check(answer["ok"]!.GetValue<bool>() == false && answer["error"]!["code"]!.GetValue<string>() == "validation" && problem?["code"]?.GetValue<string>() == "definition.entityOption"
                && problem["inputId"]?.GetValue<string>() == "lens" && problem["portId"]?.GetValue<string>() == "frame_interval"
                && problem["message"]?.GetValue<string>() == $"Seconds between frames of Lens takes a number from 0.5 to 60, not {shown}", $"a frame interval of {shown} is refused: " + answer.ToJsonString());
        }
        var saved = await ok(new { operation = "save", id = "lens", expectedRevision = 0, definition = Lens(2) });
        Check(saved["active"]!.GetValue<bool>(), "a camera built from nodes runs: " + saved.ToJsonString());
        Check(JsonNode.Parse(home.Syncs("lens").Last())!["entities"]![0]!["options"]!.ToJsonString() == """{"frame_interval":2}""", "its frame interval is synced");
        var (host, camera) = Running(api, "lens:lens");
        Check(camera.FrameInterval == 2, "the camera has its frame interval");
        var dark = await Throws<InvalidOperationException>(() => api.CameraImage("lens:lens"), "a camera built from nodes that has shown nothing");
        Check(dark.Message == "Lens (lens) has not shown a frame yet", "a camera built from nodes that has shown nothing: " + dark.Message);

        // An interval of 200 ms here (2 × 100 ms): two requests at once are well within 0.8 of it, and 250 ms apart well past it.
        CameraFrames.IntervalSecond = TimeSpan.FromMilliseconds(100);
        var limit = CameraFrames.MakeLimit;
        CameraFrames.MakeLimit = TimeSpan.FromMilliseconds(100);

        // A frame flow that hangs on a camera that has never shown a frame: the request times out with no frame to fall
        // back on, and the next one gets the same outcome while it still runs, without starting another.
        var hangs = 0;
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.FrameNeeded("lens", () => { Interlocked.Increment(ref hangs); return never.Task; });
        var hanging = await Throws<TimeoutException>(() => api.CameraImage("lens:lens"), "a frame flow that hangs on a camera with no frame");
        Check(hanging.Message == "Lens (lens) could not make a frame: it took longer than 0.1 s" && hangs == 1, $"the request times out ({hangs}): " + hanging.Message);
        await Task.Delay(250);
        var still = await Throws<TimeoutException>(() => api.CameraImage("lens:lens"), "a request while the flow still hangs");
        Check(still.Message == hanging.Message && hangs == 1, $"the next request gets its outcome without starting another ({hangs}): " + still.Message);
        never.SetResult();
        await EntityTestHome.Until(() => !camera.Frames.Busy, "the hung flow has ended");

        // What a graph's frame flow is given: the host runs it before answering, under the same rule as a class's Frame().
        var flows = 0;
        host.FrameNeeded("lens", () => { Interlocked.Increment(ref flows); host.SetFrame("lens", SharpImage.Blank(12, 8, SharpColor.Red)); return Task.CompletedTask; });
        await Task.Delay(250);
        Check(Width(await api.CameraImage("lens:lens")) == 12 && Width(await api.CameraImage("lens:lens")) == 12 && flows == 1, $"the frame flow runs once for two requests within the interval ({flows})");
        await Task.Delay(250);
        Check(Width(await api.CameraImage("lens:lens")) == 12 && flows == 2, $"and again after the interval ({flows})");
        // A frame flow past the limit is still the one running: none other starts until it has ended.
        var hung = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.FrameNeeded("lens", () => { Interlocked.Increment(ref flows); return hung.Task; });
        await Task.Delay(250);
        Check(Width(await api.CameraImage("lens:lens")) == 12 && flows == 3, $"a frame flow past the limit answers the last good frame ({flows})");
        for (var i = 0; i < 2; i++)
        {
            await Task.Delay(250);
            Check(Width(await api.CameraImage("lens:lens")) == 12 && flows == 3, $"no other frame flow starts while it still runs ({flows})");
        }
        hung.SetResult();
        host.FrameNeeded("lens", () => { Interlocked.Increment(ref flows); host.SetFrame("lens", SharpImage.Blank(14, 8, SharpColor.Red)); return Task.CompletedTask; });
        await EntityTestHome.Until(() => !camera.Frames.Busy, "the flow past the limit has ended");
        Check(Width(await api.CameraImage("lens:lens")) == 14 && flows == 4, $"once it has ended, the next request runs the flow again ({flows})");
        CameraFrames.MakeLimit = limit;
        try { host.SetFrame("nothing", SharpImage.Blank(1, 1)); throw new Exception("a frame for an entity the definition does not have was taken"); }
        catch (InvalidOperationException refused) { Check(refused.Message == "Lens has no entity nothing", "a frame for an unknown entity: " + refused.Message); }
        Console.WriteLine("PASS camera: a frame interval outside 0.5 to 60 is refused at save; a camera built from nodes runs with its own, and the frame flow its graph gives runs before a request is answered, one at a time also past the limit, and one that hangs on a camera with no frame times out without another starting");
    }

    /// <summary>
    /// A request that shared a frame made well says the camera makes frames again only while that frame is the last one
    /// begun: not once a newer attempt has failed. Here the first request still encodes a large picture (24 megapixels)
    /// when a second request starts a frame that fails; the line that the camera works again never follows that failure.
    /// </summary>
    static async Task Recovered(HassSharpManager api, EntityTestHome home)
    {
        // Frames 2 ms apart: the second request starts an attempt of its own as soon as the first one has ended.
        CameraFrames.IntervalSecond = TimeSpan.FromMilliseconds(1);
        var (host, camera) = Running(api, "lens:lens");
        string[] Lines() => home.Logs().Where(line => line.StartsWith("Lens: Lens (lens) ", StringComparison.Ordinal)).ToArray();
        var from = Lines().Length;
        host.FrameNeeded("lens", () => { host.SetFrame("lens", SharpImage.Blank(6000, 4000).DrawRect(1, 1, 10, 10)); return Task.CompletedTask; });
        await Task.Delay(20);
        var first = api.CameraImage("lens:lens");
        await EntityTestHome.Until(() => !camera.Frames.Busy, "the large frame is made");
        host.FrameNeeded("lens", () => throw new InvalidOperationException("the rain came in"));
        var second = api.CameraImage("lens:lens");
        Check(Width(await first) == 6000 && Width(await second) == 6000, "both requests get the large frame");
        var lines = Lines().Skip(from).ToArray();
        var failed = Array.IndexOf(lines, "Lens: Lens (lens) could not make a frame: the rain came in");
        Check(failed >= 0, "the newer failure is logged: " + string.Join(" | ", lines));
        Check(!lines.Skip(failed + 1).Contains("Lens: Lens (lens) makes frames again"), "no line says the camera works again after the newer failure: " + string.Join(" | ", lines));
        Console.WriteLine("PASS camera: a request that shared a frame made well says the camera works again only while no newer attempt has failed");
    }

    static async Task WithoutTheLibrary(HassSharpManager api, EntityTestHome home)
    {
        var porch = SharpImage.Blank(40, 30, SharpColor.White).ToJpeg();
        home.AnswerPictures = (_, _, answer, _) => answer(porch);
        TurboJpeg.Disabled = true;
        try
        {
            // A camera that passes a picture on needs no library.
            await Task.Delay(30);
            Check((await api.CameraImage("relay:main")).AsSpan().SequenceEqual(porch), "a picture passed on is answered as it came without the library");
            // One that draws fails, with one line in the log that names the library.
            for (var i = 0; i < 3; i++)
            {
                var missing = await Throws<InvalidOperationException>(() => api.CameraImage("marked:main"), "a drawn frame without the library");
                Check(missing.Message == "Marked (main) could not make a frame: " + TurboJpeg.Missing, "a drawn frame without the library: " + missing.Message);
            }
            var lines = home.Logs(PyLogLevel.Error).Where(line => line.StartsWith("Marked: ", StringComparison.Ordinal)).ToArray();
            Check(lines.SequenceEqual(["Marked: Marked (main) could not make a frame: " + TurboJpeg.Missing]), "one line names the library: " + string.Join(" | ", lines));
        }
        finally { TurboJpeg.Disabled = false; }
        Console.WriteLine("PASS camera: without libjpeg-turbo the definition starts, a picture passed on is answered and a drawn one fails with one line in the log naming the library");
    }
}
