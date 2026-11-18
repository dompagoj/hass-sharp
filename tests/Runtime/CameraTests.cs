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
        """;
    const string FeedKey = "HassSharp.CameraTests.Feed", DoorKey = "HassSharp.CameraTests.Door";

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
            foreach (var (id, name, type) in new[] { ("still", "Still", "Still"), ("dark", "Dark", "Dark"), ("feed", "Feed", "Feed"), ("door", "Door", "Door"), ("relay", "Relay", "Relay"), ("marked", "Marked", "Marked") })
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
            await Stopped(api, home, Ok);
            await Snapshots(api, home, Ok);
            await Options(api, home, Ok);
            await WithoutTheLibrary(api, home);
        }
        finally
        {
            await api.UnloadAsync();
            (CameraFrames.IntervalSecond, CameraFrames.MakeLimit, CameraFrames.GoodFor, UserScript.RunDrainTimeout) = (interval, limit, good, drain);
            AppContext.SetData(FeedKey, null);
            AppContext.SetData(DoorKey, null);
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

        // The last good frame is given for a while (150 ms here, 30 s in Home Assistant), then the request fails with the reason.
        CameraFrames.GoodFor = TimeSpan.FromMilliseconds(150);
        Feed(_ => throw new InvalidOperationException("the camera is off"));
        await Task.Delay(30);
        Check(Width(await api.CameraImage("feed:view")) == 25, "a failure soon after the last good frame answers it");
        await Task.Delay(200);
        var stale = await Throws<InvalidOperationException>(() => api.CameraImage("feed:view"), "a failure long after the last good frame");
        Check(stale.Message == "View (view) could not make a frame: the camera is off", "the request fails with the reason: " + stale.Message);
        Check(Errors().Length == 2, "another reason logs again: " + string.Join(" | ", Errors()));

        // A frame slower than the limit fails as a timeout; the next request starts a new one.
        CameraFrames.MakeLimit = TimeSpan.FromMilliseconds(200);
        Feed(async token => { await Task.Delay(Timeout.Infinite, token); return null; });
        before = Calls;
        await Task.Delay(30);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var slow =await Throws<TimeoutException>(() => api.CameraImage("feed:view"), "a frame slower than the limit");
        Check(slow.Message == "View (view) could not make a frame: it took longer than 0.2 s" && clock.Elapsed >= TimeSpan.FromMilliseconds(190) && clock.Elapsed < TimeSpan.FromSeconds(2),
            $"a frame slower than the limit fails after it ({clock.ElapsedMilliseconds} ms): " + slow.Message);
        await Task.Delay(30);
        await Throws<TimeoutException>(() => api.CameraImage("feed:view"), "the next frame, as slow");
        Check(Calls == before + 2, $"the next request starts a frame of its own, not {Calls - before - 1}");
        Check(Errors().Last() == "Feed: View (view) could not make a frame: it took longer than 0.2 s", "a timeout is logged: " + string.Join(" | ", Errors()));
        CameraFrames.GoodFor = TimeSpan.FromSeconds(30);
        Console.WriteLine("PASS camera: a frame that fails answers the last good one, logged once per reason and once when it works again; after a while the request fails with the reason, and a frame slower than the limit fails as a timeout and the next request starts another");
    }

    static async Task Stopped(HassSharpManager api, EntityTestHome home, Func<object, Task<JsonObject>> ok)
    {
        // Turned off while a frame is being made: the frame is cancelled, the request ends at once, a later one is refused.
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
        var ended = await Throws<InvalidOperationException>(() => waiting, "the request in flight");
        Check(ended.Message == "The entity is not running" && Volatile.Read(ref cancelled) >= 1, $"the request in flight ends, its frame cancelled ({cancelled}): " + ended.Message);
        var later = await Throws<InvalidOperationException>(() => api.CameraImage("feed:view"), "a request to the stopped definition");
        Check(later.Message == "The entity is not running", "a request to the stopped definition: " + later.Message);
        Check(!home.Logs(PyLogLevel.Error).Any(line => line.Contains("could not make a frame: ") && line.Contains("cancel")), "a cancelled frame is no failure: " + string.Join(" | ", home.Logs(PyLogLevel.Error)));

        // A new revision saved while a frame is being made: the request to the old one ends, the new one serves frames.
        var on = await ok(new { operation = "set_enabled", id = "feed", enabled = true });
        Check(on["active"]!.GetValue<bool>(), "the feed runs again: " + on.ToJsonString());
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Feed(async token => { await gate.Task.WaitAsync(token); return SharpImage.Blank(30, 8); });
        before = Calls;
        waiting = api.CameraImage("feed:view");
        await EntityTestHome.Until(() => Calls == before + 1, "a frame is being made");
        var revision = (await ok(new { operation = "read", id = "feed" }))["revision"]!.GetValue<long>();
        var saved = await ok(new { operation = "save", id = "feed", expectedRevision = revision, definition = Definition("Feed", "Feed") });
        Check(saved["active"]!.GetValue<bool>(), "the new revision runs: " + saved.ToJsonString());
        var replaced = await Throws<InvalidOperationException>(() => waiting, "the request to the replaced revision");
        Check(replaced.Message == "The entity is not running", "the request to the replaced revision ends: " + replaced.Message);
        Feed(Picture(31));
        Check(Width(await api.CameraImage("feed:view")) == 31, "the new revision serves frames");
        gate.SetResult();
        Console.WriteLine("PASS camera: turning a definition off cancels the frame being made and ends its request at once, later requests are refused, and a revision saved meanwhile serves frames");
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
        Check((await Throws<InvalidOperationException>(() => waiting, "the request")).Message == "The entity is not running", "the request ends");
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
        Console.WriteLine("PASS camera: a class takes a picture of another camera through Home Assistant and draws on it; Home Assistant's error and an empty answer fail with their reason, and a picture being taken is cut when the definition stops");
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

        // What a graph's frame flow is given: the host runs it before answering, under the same rule as a class's Frame().
        var flows = 0;
        host.FrameNeeded("lens", () => { Interlocked.Increment(ref flows); host.SetFrame("lens", SharpImage.Blank(12, 8, SharpColor.Red)); return Task.CompletedTask; });
        await Task.Delay(50);
        Check(Width(await api.CameraImage("lens:lens")) == 12 && Width(await api.CameraImage("lens:lens")) == 12 && flows == 1, $"the frame flow runs once for two requests within the interval ({flows})");
        await Task.Delay(50);
        Check(Width(await api.CameraImage("lens:lens")) == 12 && flows == 2, $"and again after the interval ({flows})");
        try { host.SetFrame("nothing", SharpImage.Blank(1, 1)); throw new Exception("a frame for an entity the definition does not have was taken"); }
        catch (InvalidOperationException refused) { Check(refused.Message == "Lens has no entity nothing", "a frame for an unknown entity: " + refused.Message); }
        Console.WriteLine("PASS camera: a frame interval outside 0.5 to 60 is refused at save; a camera built from nodes runs with its own, and the frame flow its graph gives runs before a request is answered");
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
