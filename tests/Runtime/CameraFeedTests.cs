using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>
/// A camera's live feed (<see cref="CameraFeeds"/>) and the frames it gives: the YUV4MPEG2 reader over a stream that
/// splits everything, a change of size, limited range and refusals; a frame encoded straight from its planes or drawn
/// on, also at 4K; reference counts that never let a buffer be used again while an image holds it, also in a camera's
/// frames (<see cref="CameraFrames"/>); and the feed itself over a fake ffmpeg (a Python script that writes YUV4MPEG2,
/// configured by the stream source it is given): each asker's frames newer than the last it got, how
/// <see cref="HassCameras.SnapshotAsync"/> chooses between a live frame, the newest frame and a snapshot, a feed closing
/// when idle and on shutdown with its process tree killed, ffmpeg started again after it exits or stalls, the snapshots
/// taken without a stream source or a working ffmpeg, and a C# camera's frames made live through a whole manager.
/// </summary>
static class CameraFeedTests
{
    // The fake ffmpeg. The stream source is fake://key=value,... with: w and h (the size, 64 × 48), fps (50), frames (how
    // many before it exits; 0 for no end), stall (the frame after which it hangs), resize (k:w:h, a new stream header
    // before frame k), limited (limited range), chunk (writes of at most this many bytes), pids (a file it adds its process
    // id to, and its child's), child (starts a child process that sleeps), fail (says so on stderr and exits), args (a file
    // it adds its arguments to, one line a run). A video's file holds such a stream source as its text. -stream_loop makes
    // frames endless, and -frames:v 1 makes it one frame. A -vf
    // scale=w='min(iw,W)':h='min(ih,H)':… fits the size in W × H (either side may be its own, iw or ih), never larger.
    // Frame i has every Y byte 20 + i % 200 and grey chroma.
    internal const string FakeFfmpeg = """
        #!/usr/bin/env python3
        import os, re, subprocess, sys, time
        args = sys.argv[1:]
        url = args[args.index('-i') + 1]
        spec = open(url).read().strip() if os.path.isfile(url) else url
        cfg = dict(kv.split('=', 1) for kv in spec.split('://', 1)[1].split(',') if kv)
        if cfg.get('args'):
            with open(cfg['args'], 'a') as file:
                file.write(' '.join(args) + '\n')
        w, h = int(cfg.get('w', 64)), int(cfg.get('h', 48))
        vf = args[args.index('-vf') + 1] if '-vf' in args else ''
        most_w, most_h = re.search(r"w='min\(iw,(\d+)\)'", vf), re.search(r"h='min\(ih,(\d+)\)'", vf)
        if most_w or most_h:
            f = min(1, int(most_w.group(1)) / w if most_w else 1, int(most_h.group(1)) / h if most_h else 1)
            w, h = max(1, round(w * f)), max(1, round(h * f))
        pids = cfg.get('pids')
        if pids:
            with open(pids, 'a') as file:
                file.write(f'{os.getpid()}\n')
        if cfg.get('child'):
            child = subprocess.Popen(['sleep', '600'])
            with open(pids, 'a') as file:
                file.write(f'{child.pid}\n')
        sys.stderr.write(f'fake ffmpeg reads {url}\n')
        sys.stderr.flush()
        if cfg.get('fail'):
            sys.stderr.write('Connection refused\n')
            sys.exit(1)
        out, chunk = sys.stdout.buffer, int(cfg.get('chunk', 0))
        def write(data):
            step = chunk or len(data)
            for i in range(0, len(data), step):
                out.write(data[i:i + step])
                out.flush()
        tag = ' XCOLORRANGE=LIMITED' if cfg.get('limited') else ' XCOLORRANGE=FULL'
        def header():
            write(f'YUV4MPEG2 W{w} H{h} F25:1 Ip A1:1 C420jpeg{tag}\n'.encode())
        fps, frames, stall, resize = float(cfg.get('fps', 50)), int(cfg.get('frames', 0)), int(cfg.get('stall', -1)), cfg.get('resize')
        if '-stream_loop' in args:
            frames = 0
        if '-frames:v' in args:
            frames = int(args[args.index('-frames:v') + 1])
        try:
            header()
            i = 0
            while frames == 0 or i < frames:
                if i == stall:
                    time.sleep(600)
                if resize and i == int(resize.split(':')[0]):
                    w, h = map(int, resize.split(':')[1:])
                    header()
                cw, ch = (w + 1) // 2, (h + 1) // 2
                write(b'FRAME\n' + bytes([20 + i % 200]) * (w * h) + bytes([128]) * (2 * cw * ch))
                i += 1
                time.sleep(1 / fps)
        except BrokenPipeError:
            pass
        """;

    [DllImport("libc", SetLastError = true)]
    static extern int kill(int pid, int signal);
    static bool Alive(int pid) => kill(pid, 0) == 0;

    static string directory = "", ffmpeg = "";
    // What the fake Home Assistant names for each camera: its stream source, or why there is none.
    static readonly Dictionary<string, string> sources = [];
    static readonly List<string> sourceAsks = [], snapshots = [];
    static readonly List<(string EntityId, int Width, int Height)> scaledSnapshots = [];

    static T Throws<T>(Action action, string what) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        catch (Exception error) { throw new Exception($"{what}: threw {error.GetType().Name} instead of {typeof(T).Name}: {error.Message}"); }
        throw new Exception($"{what}: did not throw {typeof(T).Name}");
    }

    // The Y value of a frame's first pixel, and of a JPEG's (grey: R = G = B = Y).
    static int Luma(SharpImage image) => image.IsFrame && Frame(image) is { } frame ? frame.Data[0] : Grey(image.ToJpeg());
    static int Grey(byte[] jpeg)
    {
        TurboJpeg.Header(jpeg, out var width, out var height);
        var rgb = new byte[width * height * 3];
        TurboJpeg.Decode(jpeg, rgb, width, height);
        return rgb[(height / 2 * width + width / 2) * 3];
    }
    static PixelFrame? Frame(SharpImage image) =>
        (PixelFrame?)typeof(SharpImage).GetField("frame", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(image);

    static PixelFrame Filled(int width, int height, byte luma, byte chroma = 128)
    {
        var frame = PixelFrame.Rent(width, height);
        frame.Data.AsSpan(0, frame.LumaLength).Fill(luma);
        frame.Data.AsSpan(frame.LumaLength, 2 * frame.ChromaLength).Fill(chroma);
        return frame;
    }

    [RuntimeSuite("--camera-feed", Order = 17)]
    public static async Task Run()
    {
        Check(TurboJpeg.Available, TurboJpeg.Missing + " (brew install jpeg-turbo on macOS, the distribution's libturbojpeg package on Linux)");
        Reader();
        await SplitReads();
        Limits();
        Encoded();
        Counted();
        Frames();
        Arguments();

        var saved = (CameraFeeds.IdleAfter, CameraFeeds.FirstFrameLimit, CameraFeeds.StallLimit, CameraFeeds.RestartDelays, CameraFeeds.Tries, CameraFeeds.RestAfter);
        var delegates = (PyInterop.BeginCameraSource, PyInterop.BeginCameraSnapshot, PyInterop.BeginCameraSnapshotScaled);
        directory = Path.Combine(Path.GetTempPath(), "camera-feed-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        ffmpeg = Path.Combine(directory, "ffmpeg");
        File.WriteAllText(ffmpeg, FakeFfmpeg);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            (CameraFeeds.IdleAfter, CameraFeeds.FirstFrameLimit, CameraFeeds.StallLimit) = (TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
            (CameraFeeds.RestartDelays, CameraFeeds.Tries, CameraFeeds.RestAfter) = ([TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)], 2, TimeSpan.FromSeconds(30));
            PyInterop.BeginCameraSource = (entityId, ok, fail) =>
            {
                lock (sources) sourceAsks.Add(entityId);
                string? found;
                lock (sources) sources.TryGetValue(entityId, out found);
                // Answered on another thread, as Python answers on its loop.
                _ = Task.Run(() => { if (found is { } url && url.Contains("://")) ok(url, url.StartsWith("fake://nobinary") ? "/nowhere/ffmpeg" : ffmpeg); else fail(found ?? "Camera not found"); });
            };
            var picture = SharpImage.Blank(8, 8, SharpColor.FromRgb(90, 90, 90)).ToJpeg();
            PyInterop.BeginCameraSnapshot = (_, entityId, ok, _) => { lock (snapshots) snapshots.Add(entityId); _ = Task.Run(() => ok(picture)); };
            PyInterop.BeginCameraSnapshotScaled = (_, entityId, width, height, ok, _) => { lock (snapshots) scaledSnapshots.Add((entityId, width, height)); _ = Task.Run(() => ok(picture)); };
            CameraFeeds.Open();

            await Newer();
            await Modes();
            await Sized();
            await Resized();
            await Held();
            await Restarted();
            await Stalled();
            await Fallback();
            await Idle();
            await Shutdown();
            Source("camera.graph", "fake://fps=40");
            await EntityGraphRuntimeTests.LiveFramesAreGivenBack("camera.graph", Frame);
            await Through();
        }
        finally
        {
            await CameraFeeds.CloseAllAsync();
            CameraFeeds.Open();
            (CameraFeeds.IdleAfter, CameraFeeds.FirstFrameLimit, CameraFeeds.StallLimit, CameraFeeds.RestartDelays, CameraFeeds.Tries, CameraFeeds.RestAfter) = saved;
            (PyInterop.BeginCameraSource, PyInterop.BeginCameraSnapshot, PyInterop.BeginCameraSnapshotScaled) = delegates;
            try { Directory.Delete(directory, recursive: true); } catch (Exception) { }
        }
    }

    // A stream of the bytes given, read in pieces of 1 to 13 bytes.
    sealed class Chunked(byte[] data, int seed) : Stream
    {
        readonly Random random = new(seed);
        int position;
        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = Math.Min(Math.Min(count, random.Next(1, 14)), data.Length - position);
            Array.Copy(data, position, buffer, offset, take);
            position += take;
            return take;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    static byte[] Stream(string header, params (int Width, int Height, byte Y, byte U, byte V)[] frames)
    {
        var bytes = new List<byte>(System.Text.Encoding.ASCII.GetBytes(header + "\n"));
        foreach (var (width, height, y, u, v) in frames)
        {
            if (width < 0) { bytes.AddRange(System.Text.Encoding.ASCII.GetBytes($"YUV4MPEG2 W{height} H{y} C420jpeg\n")); continue; }
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("FRAME Ixyz\n"));
            var chroma = (width + 1) / 2 * ((height + 1) / 2);
            bytes.AddRange(Enumerable.Repeat(y, width * height));
            bytes.AddRange(Enumerable.Repeat(u, chroma));
            bytes.AddRange(Enumerable.Repeat(v, chroma));
        }
        return [.. bytes];
    }

    static void Reader()
    {
        // An odd size: the chroma planes are rounded up (3 × 2 of a 5 × 3 frame).
        var data = Stream("YUV4MPEG2 W5 H3 F20:1 Ip A1:1 C420mpeg2 XYSCSS=420MPEG2", (5, 3, 10, 20, 30), (5, 3, 11, 21, 31));
        var reader = new Y4mReader(new MemoryStream(data));
        var first = reader.NextAsync().AsTask().Result!;
        Check((first.Width, first.Height, first.ChromaWidth, first.ChromaHeight, first.Length) == (5, 3, 3, 2, 27), $"an odd frame's planes: {first.Width}x{first.Height}, {first.Length} bytes");
        Check(first.Data.Take(15).All(b => b == 10) && first.Data.Skip(15).Take(6).All(b => b == 20) && first.Data.Skip(21).Take(6).All(b => b == 30), "the Y, U and V planes are read in order");
        Check(reader.NextAsync().AsTask().Result!.Data[0] == 11, "the next frame");
        Check(reader.NextAsync().AsTask().Result == null, "the end of the stream");
        first.Release();
        Console.WriteLine("PASS camera feed: a YUV4MPEG2 stream of an odd size reads as its Y, U and V planes, frame after frame, to its end");
    }

    static async Task SplitReads()
    {
        // A size change between frames, and a stream that ends inside a frame.
        var data = Stream("YUV4MPEG2 W6 H4 F25:1 C420jpeg XCOLORRANGE=FULL", (6, 4, 50, 60, 70), (-1, 4, 2, 0, 0), (4, 2, 51, 61, 71), (4, 2, 52, 62, 72));
        foreach (var seed in Enumerable.Range(0, 20))
        {
            var reader = new Y4mReader(new Chunked(data[..^3], seed));
            var a = (await reader.NextAsync())!;
            var b = (await reader.NextAsync())!;
            Check((a.Width, a.Height, a.Data[0], a.Data[a.LumaLength], a.Data[a.Length - 1]) == (6, 4, 50, 60, 70), $"seed {seed}: the first frame, read in pieces");
            Check((b.Width, b.Height, b.Data[0], b.Data[b.LumaLength], b.Data[b.Length - 1]) == (4, 2, 51, 61, 71) && (reader.Width, reader.Height) == (4, 2), $"seed {seed}: a new stream header gives the next frames its size");
            Check(await reader.NextAsync() == null, $"seed {seed}: a stream that ends inside a frame gives no frame");
            a.Release();
            b.Release();
        }
        // Limited range is stretched to full; full and unmarked stay as they are.
        var limited = (await new Y4mReader(new MemoryStream(Stream("YUV4MPEG2 W2 H2 C420jpeg XCOLORRANGE=LIMITED", (2, 2, 16, 16, 240)))).NextAsync())!;
        Check((limited.Data[0], limited.Data[4], limited.Data[5]) == (0, 0, 255), $"limited range is stretched: Y 16 is {limited.Data[0]}, U 16 is {limited.Data[4]}, V 240 is {limited.Data[5]}");
        var full = (await new Y4mReader(new MemoryStream(Stream("YUV4MPEG2 W2 H2", (2, 2, 16, 16, 240)))).NextAsync())!;
        Check((full.Data[0], full.Data[4], full.Data[5]) == (16, 16, 240), "a stream with no range is taken as full");
        Console.WriteLine("PASS camera feed: frames read in pieces of 1 to 13 bytes, a new stream header changes the size, a frame cut off is none, and limited range is stretched to full");
    }

    static void Limits()
    {
        async Task<string> Refused(string text)
        {
            try { await new Y4mReader(new MemoryStream(System.Text.Encoding.ASCII.GetBytes(text))).NextAsync(); }
            catch (InvalidDataException error) { return error.Message; }
            throw new Exception("not refused: " + text);
        }
        Check(Refused("YUV4MPEG2 W8 H8 C444\nFRAME\n").Result.Contains("not 4:2:0"), "4:4:4 is refused");
        Check(Refused("YUV4MPEG2 W8 H8 C420p10\nFRAME\n").Result.Contains("not 4:2:0"), "10-bit 4:2:0 is refused");
        Check(Refused("YUV4MPEG2 W9000 H8\nFRAME\n").Result.Contains("1 to 8192"), "a side over 8192 is refused");
        Check(Refused("YUV4MPEG2 W8000 H8000\nFRAME\n").Result.Contains("40 megapixels"), "over 40 megapixels is refused");
        Check(Refused("FRAME\n").Result.Contains("before the stream header"), "a frame before the header is refused");
        Check(Refused("<html>\n").Result.Contains("not a YUV4MPEG2"), "what is not YUV4MPEG2 is refused");
        Check(Refused(new string('x', 5000)).Result.Contains("not a YUV4MPEG2"), "a line without end is refused");
        Console.WriteLine("PASS camera feed: other subsamplings, sizes past the limits, a frame before the header and other streams are refused");
    }

    static void Encoded()
    {
        // Without drawings straight from the planes; with them, through RGB.
        var plain = SharpImage.Of(Filled(64, 48, 200));
        Check((plain.Width, plain.Height, plain.IsFrame) == (64, 48, true), "a frame's image has the frame's size");
        var jpeg = plain.ToJpeg();
        TurboJpeg.Header(jpeg, out var w, out var h);
        Check((w, h) == (64, 48) && Math.Abs(Grey(jpeg) - 200) <= 2 && ReferenceEquals(plain.ToJpeg(), jpeg), $"a frame with nothing drawn is encoded once: {w}x{h}, grey {Grey(jpeg)}");
        var drawn = SharpImage.Of(Filled(64, 48, 200)).DrawRect(0, 0, 64, 48, SharpColor.Red, 4);
        var marked = drawn.ToJpeg();
        TurboJpeg.Header(marked, out w, out h);
        var rgb = new byte[w * h * 3];
        TurboJpeg.Decode(marked, rgb, w, h);
        Check((w, h) == (64, 48) && rgb[0] > 180 && rgb[1] < 80 && Math.Abs(rgb[(24 * 64 + 32) * 3] - 200) <= 3, $"a frame drawn on: red edge {rgb[0]},{rgb[1]},{rgb[2]}, grey middle {rgb[(24 * 64 + 32) * 3]}");
        // A 4K frame, both ways.
        var clock = Stopwatch.StartNew();
        var uhd = SharpImage.Of(Filled(3840, 2160, 120));
        var direct = uhd.ToJpeg();
        var direct4k = clock.Elapsed;
        var boxed = uhd.Copy().DrawRect(100, 100, 400, 300).DrawText("Door", 120, 120, SharpColor.White, 48).ToJpeg();
        TurboJpeg.Header(direct, out w, out h);
        TurboJpeg.Header(boxed, out var bw, out var bh);
        Check((w, h, bw, bh) == (3840, 2160, 3840, 2160) && Math.Abs(Grey(direct) - 120) <= 2, "a 4K frame is encoded, with and without drawings");
        // Its RGB buffer (24 MiB) is kept for the next 4K frame drawn on, not made again for each.
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 3; i++) uhd.Copy().DrawRect(10, 10, 50, 50).ToJpeg();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated < 16L * 1024 * 1024, $"three 4K frames drawn on allocate {allocated / 1048576.0:F1} MiB, not an RGB buffer each");
        uhd.Release();
        Console.WriteLine($"PASS camera feed: a frame is encoded straight from its planes ({direct4k.TotalMilliseconds:F0} ms at 4K), and through RGB when drawn on, also at 3840 × 2160");
    }

    static void Counted()
    {
        var frame = Filled(16, 8, 77);
        var data = frame.Data;
        var image = SharpImage.Of(frame);
        var copy = image.Copy();
        Check(frame.References == 2, "a copy holds a reference of its own");
        image.Release();
        image.Release();
        Check(frame.References == 1 && !PixelFrame.InPool(data), "an image gives its reference back once, and the buffer stays out of the pool while a copy holds it");
        Check(Luma(copy) == 77, "the copy still reads the frame");
        Throws<InvalidOperationException>(() => image.Copy(), "a copy of an image given back");
        Throws<InvalidOperationException>(() => image.ToJpeg(), "the JPEG of an image given back before it was encoded");
        var jpeg = copy.ToJpeg();
        copy.Release();
        Check(frame.References == 0 && PixelFrame.InPool(data), "the last reference puts the buffer back in the pool");
        Check(ReferenceEquals(copy.ToJpeg(), jpeg), "what was encoded is still given once the frame is given back");
        Check(!frame.TryAddReference(), "a frame given back takes no new reference");
        var again = PixelFrame.Rent(16, 8);
        Check(ReferenceEquals(again.Data, data) && again.References == 1, "a buffer is rented again only once nobody holds it");
        again.Release();
        // One never given back is left to the collector: the pool makes another.
        var kept = Filled(16, 8, 1);
        var other = PixelFrame.Rent(16, 8);
        Check(!ReferenceEquals(other.Data, kept.Data), "a buffer held is never rented again");
        other.Release();
        GC.KeepAlive(kept);
        // Blank and JPEG pictures have nothing to give back.
        var blank = SharpImage.Blank(4, 4);
        blank.Release();
        Check(blank.Copy().ToJpeg().Length > 0 && !blank.IsFrame, "a blank picture is no frame and is not given back");
        Console.WriteLine("PASS camera feed: an image of a frame and each copy hold a reference; the buffer goes back to the pool at the last, never while one is held, and is then rented again");
    }

    // A camera's frames give a feed's buffer back once encoded, or once replaced unencoded, and keep the JPEG.
    static void Frames()
    {
        var frames = new CameraFrames();
        var first = Filled(16, 8, 30);
        frames.Show(SharpImage.Of(first));
        var second = Filled(16, 8, 40);
        frames.Show(SharpImage.Of(second));
        Check(first.References == 0 && PixelFrame.InPool(first.Data), "a frame replaced before it was encoded is given back");
        Check(second.References == 1, "the current frame is held until it is encoded");
        var (jpeg, current) = frames.Picture("View", null, TimeSpan.FromSeconds(30), _ => "").Result;
        Check(current && Math.Abs(Grey(jpeg) - 40) <= 2, "the current frame is the picture");
        Check(second.References == 0, "and is given back once it is encoded");
        Check(frames.Picture("View", null, TimeSpan.FromSeconds(30), _ => "").Result.Jpeg == jpeg, "its JPEG is still the answer");
        // The camera of the next revision takes over the JPEG.
        var handed = frames.Handed();
        Check(handed.Current is { Jpeg: not null } kept && !kept.Image.IsFrame && Grey(kept.Image.ToJpeg()) == Grey(jpeg), "a frame encoded is handed as its JPEG");
        var next = new CameraFrames();
        next.TakeOver(handed);
        Check(next.Picture("View", null, TimeSpan.FromSeconds(30), _ => "").Result.Jpeg == jpeg, "the next revision gives the frame handed over");
        // A frame shown unencoded is handed with a reference of its own, and given back when the next camera has one.
        var third = Filled(16, 8, 50);
        frames.Show(SharpImage.Of(third));
        var unencoded = frames.Handed();
        Check(third.References == 2, "a frame handed unencoded is a copy holding its own reference");
        var shown = new CameraFrames();
        shown.Show(SharpImage.Blank(4, 4));
        shown.TakeOver(unencoded);
        Check(third.References == 1, "a camera that shows a frame of its own gives back what it was handed");
        frames.Show(SharpImage.Blank(4, 4));
        Check(third.References == 0, "and the replaced frame is given back too");
        // A request that found the frame current keeps it from being given back until it is encoded.
        var fourth = Filled(1920, 1080, 60);
        frames.Show(SharpImage.Of(fourth));
        var asked = frames.Picture("View", null, TimeSpan.FromSeconds(30), _ => "");
        frames.Show(SharpImage.Blank(4, 4));
        Check(Math.Abs(Grey(asked.Result.Jpeg) - 60) <= 2 && fourth.References == 0, "a frame replaced while it is encoded is encoded whole, then given back");
        Console.WriteLine("PASS camera feed: a camera's frames give a buffer back once encoded or replaced unencoded, hand over its JPEG, and never while a request encodes it");
    }

    static void Arguments()
    {
        var rtsp = string.Join(' ', CameraFeed.Arguments("rtsp://door:8554/live", null));
        Check(rtsp == "-hide_banner -loglevel error -nostdin -rtsp_transport tcp -i rtsp://door:8554/live -an -vf scale=out_range=full -f yuv4mpegpipe -pix_fmt yuv420p -", "an RTSP source: " + rtsp);
        var http = string.Join(' ', CameraFeed.Arguments("http://door/video", (640, 360)));
        Check(http == "-hide_banner -loglevel error -nostdin -i http://door/video -an -vf scale=w='min(iw,640)':h='min(ih,360)':force_original_aspect_ratio=decrease:out_range=full -f yuv4mpegpipe -pix_fmt yuv420p -", "a sized HTTP source, never scaled up: " + http);
        // One side alone: the other is the frame's own, so the aspect ratio decides it.
        var wide = CameraFeed.Arguments("http://door/video", (640, 0))[^6];
        var high = CameraFeed.Arguments("http://door/video", (0, 360))[^6];
        Check(wide == "scale=w='min(iw,640)':h=ih:force_original_aspect_ratio=decrease:out_range=full" && high == "scale=w=iw:h='min(ih,360)':force_original_aspect_ratio=decrease:out_range=full", $"a width or a height alone: {wide}, {high}");
        var said = CameraFeed.Redacted("rtsp://admin:hunter2@door/live: Connection refused (also http://u:p@other/x)", "rtsp://admin:hunter2@door/live");
        Check(said == "<stream source>: Connection refused (also http://***@other/x)", "ffmpeg's messages leave out the address and passwords: " + said);
        Console.WriteLine("PASS camera feed: ffmpeg reads RTSP over TCP, scales to fit a size (or one side of it) at full range and never up, and its messages leave out the stream's address and any password");
    }

    static void Source(string entityId, string config) { lock (sources) sources[entityId] = config; }
    static CameraFeed Feed(string entityId, (int, int)? size = null) => CameraFeeds.All.Single(feed => feed.EntityId == entityId && feed.Size == size);
    static async Task<SharpImage> Live(string asker, string entityId, (int, int)? size = null)
    {
        HassCameras.MakingFrame.Value = asker;
        try { return await HassCameras.SnapshotAsync("def", entityId, CancellationToken.None, size).WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { HassCameras.MakingFrame.Value = null; }
    }
    static int Snapshots() { lock (snapshots) return snapshots.Count; }

    static async Task Newer()
    {
        Source("camera.door", "fake://fps=40");
        var images = new List<SharpImage>();
        for (var i = 0; i < 5; i++) images.Add(await Live("a", "camera.door"));
        var lumas = images.Select(Luma).ToArray();
        Check(images.All(image => image.IsFrame) && lumas.Zip(lumas.Skip(1)).All(pair => pair.Second > pair.First), "each frame an asker gets is newer than the last: " + string.Join(",", lumas));
        var other = await Live("b", "camera.door");
        Check(Luma(other) >= lumas[^1], "another asker gets the newest at once");
        Check(Feed("camera.door").Started == 1 && Snapshots() == 0, "one ffmpeg feeds both, and no snapshot is taken");
        images.ForEach(image => image.Release());
        other.Release();
        Console.WriteLine("PASS camera feed: an asker's frames are each newer than the last it got, waited for; another asker gets the newest; one ffmpeg feeds both");
    }

    static async Task Modes()
    {
        var before = Snapshots();
        // Open feed, no frame being made: its newest frame, no snapshot.
        var newest = await HassCameras.SnapshotAsync("def", "camera.door", CancellationToken.None);
        Check(newest.IsFrame && Snapshots() == before, "outside a frame, an open feed's newest frame is the picture");
        Check(newest.ToJpeg() is [0xFF, 0xD8, ..], "and is encoded as a JPEG when one is needed");
        newest.Release();
        // No feed: a snapshot, the JPEG as Home Assistant gave it.
        var still = await HassCameras.SnapshotAsync("def", "camera.yard", CancellationToken.None);
        Check(!still.IsFrame && Snapshots() == before + 1 && CameraFeeds.All.All(feed => feed.EntityId != "camera.yard"), "outside a frame and without a feed, a snapshot is taken and no feed starts");
        // Making a frame: live.
        var live = await Live("a", "camera.door");
        Check(live.IsFrame && Snapshots() == before + 1, "making a frame, a live frame");
        live.Release();
        Console.WriteLine("PASS camera feed: making a frame takes a live frame; otherwise an open feed's newest; otherwise a snapshot, and no feed starts for it");
    }

    static async Task Sized()
    {
        var before = Snapshots();
        // Another size is another feed, which ffmpeg scales to fit.
        var small = await Live("a", "camera.door", (32, 32));
        Check(small.IsFrame && (small.Width, small.Height) == (32, 24) && Feed("camera.door", (32, 32)).Started == 1, $"a sized feed's frames fit the size: {small.Width}x{small.Height}");
        small.Release();
        // One side alone keeps the aspect ratio; a size larger than the camera's is its own size.
        var narrow = await Live("a", "camera.door", (32, 0));
        var low = await Live("a", "camera.door", (0, 12));
        var large = await Live("a", "camera.door", (640, 480));
        Check((narrow.Width, narrow.Height, low.Width, low.Height, large.Width, large.Height) == (32, 24, 16, 12, 64, 48),
            $"a width alone, a height alone, a size larger than the camera's: {narrow.Width}x{narrow.Height}, {low.Width}x{low.Height}, {large.Width}x{large.Height}");
        foreach (var image in new[] { narrow, low, large }) image.Release();
        // Outside a frame, a size with no open feed of it is a snapshot Home Assistant scales.
        var scaled = await HassCameras.SnapshotAsync("def", "camera.door", CancellationToken.None, (100, 100));
        int count;
        lock (snapshots) count = scaledSnapshots.Count(s => s == ("camera.door", 100, 100));
        Check(!scaled.IsFrame && count == 1 && Snapshots() == before, "a size with no feed of it is a scaled snapshot");
        // Home Assistant scales only to both sides: one alone is a snapshot of the camera's own size, made to fit here (8 × 8 to 4 × 4).
        var halved = await HassCameras.SnapshotAsync("def", "camera.door", CancellationToken.None, (0, 4));
        lock (snapshots) count = scaledSnapshots.Count;
        Check(!halved.IsFrame && (halved.Width, halved.Height) == (4, 4) && Snapshots() == before + 1 && count == 1, $"a height alone is a snapshot made to fit: {halved.Width}x{halved.Height}");
        Console.WriteLine("PASS camera feed: each size has a feed of its own, scaled to fit, one side alone keeping the aspect ratio and none scaled up; a size without one is a snapshot Home Assistant scales, made to fit here");
    }

    static async Task Resized()
    {
        Source("camera.resize", "fake://w=40,h=30,resize=3:20:10,fps=60,chunk=7");
        var sizes = new List<(int, int)>();
        for (var i = 0; i < 8; i++)
        {
            var image = await Live("a", "camera.resize");
            sizes.Add((image.Width, image.Height));
            image.Release();
        }
        Check(sizes[0] == (40, 30) && sizes[^1] == (20, 10), "the frames after a new stream header have its size: " + string.Join(" ", sizes));
        Console.WriteLine("PASS camera feed: ffmpeg's output read in pieces of 7 bytes, and a size change in the stream, give frames of each size");
    }

    static async Task Held()
    {
        // Frames come on; one held keeps its pixels, however many follow.
        var held = await Live("hold", "camera.door");
        var data = Frame(held)!.Data;
        var luma = data[0];
        for (var i = 0; i < 20; i++) (await Live("hold", "camera.door")).Release();
        Check(Frame(held)!.Data == data && data.Take(Frame(held)!.Length).All(b => b == luma || b == 128) && data[0] == luma && !PixelFrame.InPool(data), "a frame held is never used again for a later one");
        held.Release();
        Console.WriteLine("PASS camera feed: a frame held keeps its buffer while 20 more frames come and go");
    }

    static async Task Restarted()
    {
        Source("camera.short", "fake://frames=3,fps=100");
        // Frames until a run after the first gave one; between runs, snapshots stand in.
        var seen = new List<string>();
        var clock = Stopwatch.StartNew();
        var again = false;
        while (!again && clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            var runs = CameraFeeds.All.FirstOrDefault(feed => feed.EntityId == "camera.short")?.Started ?? 0;
            var image = await Live("a", "camera.short");
            seen.Add(image.IsFrame ? Luma(image).ToString() : "snapshot");
            again = image.IsFrame && runs >= 2;
            image.Release();
        }
        Check(again, "ffmpeg that exits is started again and gives frames: " + string.Join(",", seen));
        Console.WriteLine($"PASS camera feed: ffmpeg that exits after 3 frames is started again ({Feed("camera.short").Started} runs), and frames keep coming");
    }

    static async Task Stalled()
    {
        Source("camera.stall", "fake://stall=1,fps=50");
        var first = await Live("a", "camera.stall");
        first.Release();
        // The next frame never comes from this run: after the stall limit ffmpeg is stopped, and meanwhile a snapshot
        // stands in for the frame.
        var before = Snapshots();
        var next = await Live("a", "camera.stall");
        Check(!next.IsFrame && Snapshots() == before + 1, "a feed that stalls gives a snapshot once ffmpeg was stopped");
        await EntityTestHome.Until(() => Feed("camera.stall").Started >= 2, "ffmpeg starts again after a stall");
        Console.WriteLine("PASS camera feed: ffmpeg that gives no frame for the stall limit is stopped, a snapshot stands in, and it starts again");
    }

    static async Task Fallback()
    {
        // No stream source: snapshots one after the other, the feed resting.
        Source("camera.still", "it has no stream source");
        var before = Snapshots();
        for (var i = 0; i < 3; i++) Check(!(await Live("a", "camera.still")).IsFrame, "a camera without a stream source gives snapshots");
        Check(Snapshots() == before + 3 && Feed("camera.still").Resting == "it has no stream source" && Feed("camera.still").Started == 0, "each frame a snapshot, no ffmpeg started: " + Feed("camera.still").Resting);
        int asks;
        lock (sources) asks = sourceAsks.Count(e => e == "camera.still");
        Check(asks == 1, "the stream source is asked once while the feed rests, not for each frame");
        // ffmpeg failing: each try gave no frame, then the feed rests.
        Source("camera.refused", "fake://fail=1");
        for (var i = 0; i < 3; i++) (await Live("a", "camera.refused")).Release();
        await EntityTestHome.Until(() => Feed("camera.refused").Started == CameraFeeds.Tries && Feed("camera.refused").Resting is { } why && why.Contains("Connection refused"), "ffmpeg that fails is tried twice, then the feed rests");
        Check(!(await Live("a", "camera.refused")).IsFrame, "and gives snapshots: " + Feed("camera.refused").Resting);
        Check(!Feed("camera.refused").Resting!.Contains("fake://"), "why it rests leaves out the address");
        // An ffmpeg that cannot be started.
        Source("camera.nobinary", "fake://nobinary");
        Check(!(await Live("a", "camera.nobinary")).IsFrame, "an ffmpeg that cannot be started gives snapshots");
        await EntityTestHome.Until(() => Feed("camera.nobinary").Resting?.Contains("could not start") == true, "the feed says ffmpeg could not start");
        // No way to ask Home Assistant: snapshots, and no feed.
        var ask = PyInterop.BeginCameraSource;
        PyInterop.BeginCameraSource = null;
        try { Check(!(await Live("a", "camera.other")).IsFrame && CameraFeeds.All.All(feed => feed.EntityId != "camera.other"), "without Home Assistant's stream sources, snapshots"); }
        finally { PyInterop.BeginCameraSource = ask; }
        Console.WriteLine("PASS camera feed: without a stream source, with an ffmpeg that fails or cannot start, and without Home Assistant to ask, frames are snapshots taken back to back");
    }

    static async Task Idle()
    {
        var idle = CameraFeeds.IdleAfter;
        CameraFeeds.IdleAfter = TimeSpan.FromMilliseconds(300);
        try
        {
            var pids = Path.Combine(directory, "idle.pids");
            Source("camera.idle", $"fake://pids={pids}");
            (await Live("a", "camera.idle")).Release();
            var feed = Feed("camera.idle");
            var pid = int.Parse(File.ReadAllLines(pids)[0]);
            Check(Alive(pid), "ffmpeg runs while frames are asked for");
            for (var i = 0; i < 5; i++) { await Task.Delay(100); (await Live("a", "camera.idle")).Release(); }
            Check(!feed.Closed, "a feed asked every 100 ms stays open past its idle time");
            await EntityTestHome.Until(() => feed.Closed && CameraFeeds.All.All(f => f != feed), "the feed closes once idle");
            await feed.Ended.WaitAsync(TimeSpan.FromSeconds(5));
            await EntityTestHome.Until(() => !Alive(pid), "its ffmpeg is gone");
        }
        finally { CameraFeeds.IdleAfter = idle; }
        Console.WriteLine("PASS camera feed: a feed closes once no frame was asked of it for its idle time, and its ffmpeg is killed and reaped");
    }

    static async Task Shutdown()
    {
        var pids = Path.Combine(directory, "shutdown.pids");
        Source("camera.tree", $"fake://pids={pids},child=1");
        (await Live("a", "camera.tree")).Release();
        await EntityTestHome.Until(() => File.Exists(pids) && File.ReadAllLines(pids).Length == 2, "ffmpeg and its child have started");
        var started = File.ReadAllLines(pids).Select(int.Parse).ToArray();
        Check(started.All(Alive), "ffmpeg and its child run");
        var feeds = CameraFeeds.All;
        await CameraFeeds.CloseAllAsync();
        Check(CameraFeeds.All.Length == 0 && feeds.All(feed => feed.Closed && feed.Ended.IsCompleted), "every feed closes, its task ended");
        await EntityTestHome.Until(() => !started.Any(Alive), "ffmpeg and its child are gone");
        var before = Snapshots();
        Check(!(await Live("a", "camera.door")).IsFrame && Snapshots() == before + 1 && CameraFeeds.All.Length == 0, "while closed, a frame is a snapshot and no feed starts");
        CameraFeeds.Open();
        (await Live("a", "camera.door")).Release();
        Check(CameraFeeds.All.Length == 1, "once open again, feeds start");
        Console.WriteLine("PASS camera feed: shutdown closes every feed and kills ffmpeg's process tree; until set up again, frames are snapshots");
    }

    // A C# camera whose Frame() takes a picture of camera.door, through a whole manager: its frames are live.
    static async Task Through()
    {
        const string code = """
            public class Door : SharpCamera
            {
                protected override async Task<SharpImage?> Frame() => await CameraSnapshot("camera.slow");
                protected override async Task Start() => AppContext.SetData("HassSharp.CameraFeedTests.Start", (await CameraSnapshot("camera.slow")).Width);
            }
            """;
        var interval = CameraFrames.IntervalSecond;
        CameraFrames.IntervalSecond = TimeSpan.FromMilliseconds(5);
        // Ten frames a second, so that their numbers do not come round while the manager starts; the feed is opened first.
        Source("camera.slow", "fake://fps=10");
        (await Live("a", "camera.slow")).Release();
        // EntityTestHome puts its own snapshot answer in place, which refuses: the frames are live or fail.
        await using var home = new EntityTestHome("camera-feed");
        var api = new HassSharpManager(home.Root);
        try
        {
            await api.Init();
            var descriptor = new NodeLibraryCompiler(Diagnostics).Preview([], [new CustomNodeSource("c", code, "csharp_entity")]).Snapshot.Custom["c"];
            Check(descriptor.Valid, "the camera's code compiles: " + string.Join(" | ", descriptor.Diagnostics.Select(d => d.Message)));
            var node = EntityNodeTests.CSharpEntity("camera", "camera", new() { ["source"] = code, ["signature"] = descriptor.Signature!.DeepClone(), ["arguments"] = new JsonObject() });
            var definition = new JsonObject
            {
                ["name"] = "Door view", ["settings"] = new JsonArray(),
                ["logic"] = new JsonObject { ["kind"] = "graph", ["graph"] = new JsonObject { ["version"] = 5, ["name"] = "Door view", ["nodes"] = new JsonArray(node), ["edges"] = new JsonArray() } },
            };
            var answer = JsonNode.Parse(await api.EntityRequest(JsonSerializer.Serialize(new { operation = "save", id = "doorview", expectedRevision = 0, definition })))!;
            Check(answer["ok"]!.GetValue<bool>() && answer["result"]!["active"]!.GetValue<bool>(), "the camera runs: " + answer.ToJsonString());
            // Its Start() is no frame: the open feed's newest frame, its size.
            Check(AppContext.GetData("HassSharp.CameraFeedTests.Start") is 64, "a picture taken outside a frame, while a feed is open, is its newest frame");
            var greys = new List<int>();
            // Each request past the camera's interval (5 ms here) makes a frame, which waits for a newer one of the feed.
            for (var i = 0; i < 4; i++)
            {
                greys.Add(Grey(await api.CameraImage("doorview:camera")));
                await Task.Delay(10);
            }
            Check(greys.Zip(greys.Skip(1)).All(pair => pair.Second > pair.First) && home.Pictures().Length == 0, "each frame of the camera is a newer live frame, and no snapshot is taken: " + string.Join(",", greys));
        }
        finally
        {
            await api.UnloadAsync();
            CameraFrames.IntervalSecond = interval;
            AppContext.SetData("HassSharp.CameraFeedTests.Start", null);
        }
        Check(CameraFeeds.All.Length == 0, "unloading the manager closes the feeds");
        Console.WriteLine("PASS camera feed: a C# camera's Frame() gets a newer live frame each time through a whole manager; its Start() gets the open feed's newest; unloading closes the feeds");
    }
}
