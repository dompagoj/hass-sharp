using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>
/// Frames from any source, for code: pictures made of pixels (<see cref="SharpImage.FromRgb"/> and the other factories,
/// each round-tripping through <see cref="SharpImage.CopyPixels"/> with strides and in every layout), a writer that fills a
/// pooled buffer with no copy, frames shared and not copied by a copy and by a camera's <see cref="SharpCamera.Show"/>, and
/// given back by <see cref="SharpImage.Dispose"/>; the sources a video is read from (<see cref="VideoSources"/>: the schemes
/// taken and refused, the protocols ffmpeg may open, a file inside the folders Home Assistant allows once its links and
/// <c>..</c> are resolved); <see cref="SharpVideo"/> over the fake ffmpeg of <see cref="CameraFeedTests"/> (each frame newer,
/// a file looped or played once, a reader's last disposal and unloading killing ffmpeg, a refusal); a Video frame's frames
/// live in a camera's frame, the newest elsewhere, else one read by a short run; and a push camera through a whole manager.
/// </summary>
static class VideoTests
{
    [DllImport("libc", SetLastError = true)]
    static extern int kill(int pid, int signal);
    static bool Alive(int pid) => kill(pid, 0) == 0;

    static string directory = "", ffmpeg = "", allowed = "";
    static int videoAsks;

    static T Throws<T>(Action action, string what) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        catch (Exception error) { throw new Exception($"{what}: threw {error.GetType().Name} instead of {typeof(T).Name}: {error.Message}"); }
        throw new Exception($"{what}: did not throw {typeof(T).Name}");
    }

    static async Task<T> ThrowsAsync<T>(Func<Task> action, string what) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        catch (Exception error) { throw new Exception($"{what}: threw {error.GetType().Name} instead of {typeof(T).Name}: {error.Message}"); }
        throw new Exception($"{what}: did not throw {typeof(T).Name}");
    }

    static PixelFrame? Frame(SharpImage image) =>
        (PixelFrame?)typeof(SharpImage).GetField("frame", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(image);

    [RuntimeSuite("--video", Order = 18)]
    public static async Task Run()
    {
        Check(TurboJpeg.Available, TurboJpeg.Missing + " (brew install jpeg-turbo on macOS, the distribution's libturbojpeg package on Linux)");
        Factories();
        Strides();
        Converted();
        Drawn();
        Refused();
        Writer();
        Shared();

        var saved = (CameraFeeds.IdleAfter, CameraFeeds.FirstFrameLimit, CameraFeeds.StallLimit, CameraFeeds.RestartDelays, CameraFeeds.Tries, CameraFeeds.RestAfter);
        var delegates = (PyInterop.BeginVideoSource, PyInterop.BeginCameraSource);
        directory = Path.Combine(Path.GetTempPath(), "video-" + Guid.NewGuid());
        allowed = Path.Combine(directory, "media");
        Directory.CreateDirectory(allowed);
        ffmpeg = Path.Combine(directory, "ffmpeg");
        File.WriteAllText(ffmpeg, CameraFeedTests.FakeFfmpeg);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            // Home Assistant's ffmpeg is the fake, and the folder allowed is media/ (a link to it is resolved, so the
            // folder is named through one here).
            var linked = Path.Combine(directory, "media-link");
            File.CreateSymbolicLink(linked, allowed);
            Sources();
            Paths();
            (CameraFeeds.IdleAfter, CameraFeeds.FirstFrameLimit, CameraFeeds.StallLimit) = (TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));
            (CameraFeeds.RestartDelays, CameraFeeds.Tries, CameraFeeds.RestAfter) = ([TimeSpan.FromMilliseconds(50)], 2, TimeSpan.FromSeconds(30));
            PyInterop.BeginVideoSource = (ok, fail) =>
            {
                Interlocked.Increment(ref videoAsks);
                _ = Task.Run(() => ok(ffmpeg, JsonSerializer.Serialize(new[] { linked, "relative/ignored" })));
            };
            CameraFeeds.Open();
            await Next();
            await Looped();
            await Once();
            await Closed();
            await Outside();
            await Graph();
            await Push();
            await Pull();
        }
        finally
        {
            await CameraFeeds.CloseAllAsync();
            CameraFeeds.Open();
            (CameraFeeds.IdleAfter, CameraFeeds.FirstFrameLimit, CameraFeeds.StallLimit, CameraFeeds.RestartDelays, CameraFeeds.Tries, CameraFeeds.RestAfter) = saved;
            (PyInterop.BeginVideoSource, PyInterop.BeginCameraSource) = delegates;
            try { Directory.Delete(directory, recursive: true); } catch (Exception) { }
        }
    }

    // A test pattern: each pixel's colour from its place, so that a row or a byte out of place shows.
    static byte[] Pattern(int width, int height, int bytes, int stride = 0)
    {
        stride = stride == 0 ? width * bytes : stride;
        var pixels = new byte[stride * height];
        Array.Fill(pixels, (byte)0xEE);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                for (var c = 0; c < bytes; c++) pixels[y * stride + x * bytes + c] = (byte)(x * 7 + y * 13 + c * 50);
        return pixels;
    }

    static void Factories()
    {
        foreach (var (format, make, bytes) in new (SharpPixelFormat, Func<int, int, byte[], int, SharpImage>, int)[]
        {
            (SharpPixelFormat.Rgb, (w, h, p, s) => SharpImage.FromRgb(w, h, p, s), 3), (SharpPixelFormat.Bgr, (w, h, p, s) => SharpImage.FromBgr(w, h, p, s), 3),
            (SharpPixelFormat.Rgba, (w, h, p, s) => SharpImage.FromRgba(w, h, p, s), 4), (SharpPixelFormat.Bgra, (w, h, p, s) => SharpImage.FromBgra(w, h, p, s), 4),
        })
        {
            var pixels = Pattern(9, 5, bytes);
            using var image = make(9, 5, pixels, 0);
            Check((image.Width, image.Height, image.PixelsLength(format)) == (9, 5, 9 * 5 * bytes), $"{format}: the size, and its bytes");
            Check(image.ToPixels(format).AsSpan().SequenceEqual(pixels), $"{format}: read in its own layout, the pixels it was made of");
            Check(Frame(image) is { Format: var kept } && kept == format, $"{format}: kept in its own layout");
            // Encoded from its own layout: a JPEG of its size.
            var jpeg = image.ToJpeg();
            TurboJpeg.Header(jpeg, out var w, out var h);
            Check((w, h) == (9, 5), $"{format}: encoded as {w}x{h}");
        }
        // The general factory takes each layout, YUV 4:2:0 as packed planes.
        var yuv = Pattern(SharpImage.PixelsLength(6, 4, SharpPixelFormat.Yuv420), 1, 1);
        using var planes = SharpImage.FromPixels(6, 4, SharpPixelFormat.Yuv420, yuv);
        Check(planes.ToPixels(SharpPixelFormat.Yuv420).AsSpan().SequenceEqual(yuv) && SharpImage.PixelsLength(6, 4, SharpPixelFormat.Yuv420) == 24 + 2 * 6, "YUV 4:2:0 packed planes round-trip");
        Console.WriteLine("PASS video: each factory (RGB, BGR, RGBA, BGRA, packed YUV 4:2:0) keeps its layout and reads back the same bytes");
    }

    static void Strides()
    {
        // Rows padded to 64 bytes, the last row ending at its last pixel.
        var padded = Pattern(10, 4, 3, 64);
        using var rgb = SharpImage.FromRgb(10, 4, padded.AsSpan(0, 64 * 3 + 30), 64);
        Check(rgb.ToPixels().AsSpan().SequenceEqual(Pattern(10, 4, 3)), "RGB rows with a stride are copied packed");
        var bgra = Pattern(7, 3, 4, 40);
        using var withStride = SharpImage.FromBgra(7, 3, bgra, 40);
        Check(withStride.ToPixels(SharpPixelFormat.Bgra).AsSpan().SequenceEqual(Pattern(7, 3, 4)), "BGRA rows with a stride are copied packed");
        // YUV planes of an odd size, each with a stride of its own.
        int width = 5, height = 3, cw = 3, ch = 2;
        var y = Pattern(width, height, 1, 8);
        var u = Pattern(cw, ch, 1, 4);
        var v = Pattern(cw, ch, 1, 6).Select(b => (byte)(b ^ 0x55)).ToArray();
        for (var row = 0; row < ch; row++) for (var x = 0; x < cw; x++) v[row * 6 + x] = (byte)(200 - row * 6 - x);
        using var image = SharpImage.FromYuv420(width, height, y, u, v, 8, 4, 6);
        var packed = image.ToPixels(SharpPixelFormat.Yuv420);
        var expected = Pattern(width, height, 1).Concat(Pattern(cw, ch, 1)).Concat(Enumerable.Range(0, ch).SelectMany(row => Enumerable.Range(0, cw).Select(x => (byte)(200 - row * 6 - x)))).ToArray();
        Check(packed.AsSpan().SequenceEqual(expected), "YUV planes with strides of their own are copied packed: " + string.Join(",", packed));
        // Limited range is stretched to full.
        using var limited = SharpImage.FromYuv420(2, 2, new byte[] { 16, 16, 235, 235 }, new byte[] { 16 }, new byte[] { 240 }, limitedRange: true);
        Check(limited.ToPixels(SharpPixelFormat.Yuv420) is [0, 0, 255, 255, 0, 255], "limited range is stretched to full");
        Console.WriteLine("PASS video: rows and planes with strides are copied packed, and YUV of limited range is stretched to full");
    }

    static void Converted()
    {
        // Packed layouts convert exactly into each other; alpha is kept between four-byte layouts and 255 from three.
        var rgb = Pattern(8, 6, 3);
        using var image = SharpImage.FromRgb(8, 6, rgb);
        var bgr = image.ToPixels(SharpPixelFormat.Bgr);
        var rgba = image.ToPixels(SharpPixelFormat.Rgba);
        var bgra = image.ToPixels(SharpPixelFormat.Bgra);
        Check(Enumerable.Range(0, 48).All(i => bgr[i * 3] == rgb[i * 3 + 2] && bgr[i * 3 + 1] == rgb[i * 3 + 1] && bgr[i * 3 + 2] == rgb[i * 3]), "RGB read as BGR swaps red and blue");
        Check(Enumerable.Range(0, 48).All(i => rgba[i * 4] == rgb[i * 3] && rgba[i * 4 + 2] == rgb[i * 3 + 2] && rgba[i * 4 + 3] == 255), "RGB read as RGBA has alpha 255");
        Check(Enumerable.Range(0, 48).All(i => bgra[i * 4] == rgb[i * 3 + 2] && bgra[i * 4 + 3] == 255), "RGB read as BGRA");
        using var fromBgra = SharpImage.FromBgra(8, 6, bgra);
        Check(fromBgra.ToPixels().AsSpan().SequenceEqual(rgb), "BGRA read as RGB is the RGB it was made of");
        // To and from YUV 4:2:0 by libjpeg-turbo: a flat colour comes back within a step or two.
        var flat = Enumerable.Repeat(new byte[] { 200, 60, 30 }, 16 * 16).SelectMany(p => p).ToArray();
        using var solid = SharpImage.FromRgb(16, 16, flat);
        var planes = solid.ToPixels(SharpPixelFormat.Yuv420);
        using var back = SharpImage.FromPixels(16, 16, SharpPixelFormat.Yuv420, planes);
        var again = back.ToPixels(SharpPixelFormat.Rgb);
        Check(again.Select((b, i) => Math.Abs(b - flat[i])).Max() <= 3, $"RGB through YUV 4:2:0 and back: {again[0]},{again[1]},{again[2]}");
        var bgrBack = back.ToPixels(SharpPixelFormat.Bgr);
        Check(Math.Abs(bgrBack[0] - 30) <= 3 && Math.Abs(bgrBack[2] - 200) <= 3, "YUV read as BGR");
        // A JPEG and a blank picture read too.
        using var blank = SharpImage.Blank(4, 2, SharpColor.FromRgb(10, 20, 30));
        Check(blank.ToPixels(SharpPixelFormat.Bgra) is [30, 20, 10, 255, ..], "a blank picture reads as its colour");
        var jpeg = SharpImage.Load(solid.ToJpeg());
        var decoded = jpeg.ToPixels(SharpPixelFormat.Rgba);
        Check(decoded.Length == 16 * 16 * 4 && Math.Abs(decoded[0] - 200) <= 6 && decoded[3] == 255, $"a JPEG reads as its pixels: {decoded[0]},{decoded[1]},{decoded[2]},{decoded[3]}");
        Check(jpeg.ToPixels(SharpPixelFormat.Yuv420).Length == SharpImage.PixelsLength(16, 16, SharpPixelFormat.Yuv420), "a JPEG reads as YUV planes");
        // A destination too small is refused; a larger one is written from its start.
        Throws<ArgumentException>(() => image.CopyPixels(new byte[10]), "a destination too small");
        var large = new byte[200];
        image.CopyPixels(large.AsSpan(), SharpPixelFormat.Bgr);
        Check(large.AsSpan(0, 144).SequenceEqual(bgr) && large[144] == 0, "a larger destination is written from its start");
        Console.WriteLine("PASS video: pixels read in every layout: packed layouts exactly, YUV 4:2:0 through libjpeg-turbo, a JPEG and a blank picture too");
    }

    static void Drawn()
    {
        // The pixels read are the picture as it shows, its drawings drawn; a copy made before them reads as it came.
        using var frame = SharpImage.FromRgb(20, 20, Enumerable.Repeat((byte)100, 20 * 20 * 3).ToArray());
        using var before = frame.Copy();
        frame.DrawRect(0, 0, 20, 20, SharpColor.Red, 2);
        var drawn = frame.ToPixels();
        Check(drawn[0] > 200 && drawn[1] < 60 && drawn[(10 * 20 + 10) * 3] == 100, "the drawings are drawn on the pixels read");
        Check(before.ToPixels().All(b => b == 100), "a copy made before the drawings reads the frame as it came");
        var yuv = frame.ToPixels(SharpPixelFormat.Yuv420);
        Check(yuv.Length == SharpImage.PixelsLength(20, 20, SharpPixelFormat.Yuv420) && yuv[0] != yuv[10 * 20 + 10], "and in YUV 4:2:0 too");
        Console.WriteLine("PASS video: the pixels read have the drawings drawn on them, and a copy made before reads the frame as it came");
    }

    static void Refused()
    {
        Throws<ArgumentException>(() => SharpImage.FromRgb(0, 4, new byte[12]), "a side of 0");
        Throws<ArgumentException>(() => SharpImage.FromRgb(9000, 1, new byte[27000]), "a side over 8192");
        var tooLarge = Throws<ArgumentException>(() => SharpImage.Writer(8000, 8000, SharpPixelFormat.Rgb), "over 40 megapixels");
        Check(tooLarge.Message.Contains("40 megapixels"), tooLarge.Message);
        Check(Throws<ArgumentException>(() => SharpImage.FromRgb(4, 4, new byte[47]), "too few bytes").Message.Contains("fewer than the 48"), "too few bytes are refused in words");
        Check(Throws<ArgumentException>(() => SharpImage.FromRgba(4, 4, new byte[100], 12), "a stride less than a row").Message.Contains("fewer than a row's 16"), "a stride less than a row is refused");
        Throws<ArgumentException>(() => SharpImage.FromYuv420(4, 4, new byte[16], new byte[3], new byte[4]), "a U plane too small");
        Throws<ArgumentException>(() => SharpImage.FromPixels(4, 4, SharpPixelFormat.Yuv420, new byte[24], 8), "a stride for packed planes");
        Throws<ArgumentException>(() => SharpImage.FromPixels(4, 4, (SharpPixelFormat)99, new byte[64]), "a layout that is none");
        Console.WriteLine("PASS video: sizes past the limits, too few bytes, a stride less than a row and a layout that is none are refused");
    }

    static void Writer()
    {
        using var made = SharpImage.Create(8, 2, SharpPixelFormat.Bgr, pixels => { pixels.Fill(0); pixels[0] = 255; });
        Check(made.ToPixels() is [0, 0, 255, ..] && made.ToPixels().Skip(3).All(b => b == 0), "Create fills the picture's own pixels");
        // A writer's pixels become the picture's, no copy made.
        var writer = SharpImage.Writer(4, 2, SharpPixelFormat.Rgba);
        Check((writer.Width, writer.Height, writer.Format, writer.Stride, writer.Pixels.Length) == (4, 2, SharpPixelFormat.Rgba, 16, 32), "a writer's size, layout, stride and bytes");
        writer.Pixels.Span.Fill(77);
        var buffer = writer.Pixels;
        var image = writer.ToImage();
        Check(MemoryMarshal.TryGetArray<byte>(buffer, out var segment) && ReferenceEquals(segment.Array, Frame(image)!.Data), "the picture is the writer's buffer, not a copy of it");
        Check(image.ToPixels(SharpPixelFormat.Rgba).All(b => b == 77), "with the pixels written");
        Throws<ObjectDisposedException>(() => writer.ToImage(), "a second picture of one writer");
        Throws<ObjectDisposedException>(() => _ = writer.Pixels, "the pixels once the picture is made");
        writer.Dispose();
        Check(Frame(image)!.References == 1, "disposing the writer after leaves the picture whole");
        image.Dispose();
        // A writer disposed without a picture gives its buffer back; a 4K writer then takes it again, with no new buffer.
        var unused = SharpImage.Writer(3840, 2160, SharpPixelFormat.Yuv420);
        MemoryMarshal.TryGetArray<byte>(unused.Pixels, out var held);
        unused.Dispose();
        Check(PixelFrame.InPool(held.Array!), "a writer that made no picture gives its buffer back");
        var before = GC.GetAllocatedBytesForCurrentThread();
        var uhd = SharpImage.Writer(3840, 2160, SharpPixelFormat.Yuv420);
        uhd.Pixels.Span[..(3840 * 2160)].Fill(90);
        uhd.Pixels.Span[(3840 * 2160)..].Fill(128);
        using var written = uhd.ToImage();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(Math.Abs(Grey(written.ToJpeg()) - 90) <= 2, "a 4K frame written straight into its buffer encodes");
        Check(allocated < 4L * 1024 * 1024, $"a 4K writer takes its buffer from the pool: {allocated / 1048576.0:F1} MiB allocated");
        Console.WriteLine("PASS video: Create and a writer fill a pooled buffer that becomes the picture with no copy; a writer disposed unused gives it back, and a 4K one takes it again");
    }

    static int Grey(byte[] jpeg)
    {
        TurboJpeg.Header(jpeg, out var width, out var height);
        var rgb = new byte[width * height * 3];
        TurboJpeg.Decode(jpeg, rgb, width, height);
        return rgb[(height / 2 * width + width / 2) * 3];
    }

    static void Shared()
    {
        var image = SharpImage.FromRgb(32, 32, new byte[32 * 32 * 3]);
        var frame = Frame(image)!;
        var copy = image.Copy();
        Check(ReferenceEquals(Frame(copy)!.Data, frame.Data) && frame.References == 2, "a copy shares the pixels, holding a reference of its own");
        // A camera shows the frame without copying its pixels: the camera's picture holds one more reference to the same buffer.
        var camera = new SharpCamera("view");
        camera.Show(image);
        Check(frame.References == 3, $"Show keeps the frame by a reference, not a copy of its pixels: {frame.References}");
        image.Dispose();
        image.Dispose();
        copy.Dispose();
        Check(frame.References == 1 && !PixelFrame.InPool(frame.Data), "disposing gives back one reference, once each; the camera's keeps the buffer");
        Throws<InvalidOperationException>(() => image.ToPixels(), "the pixels of a picture disposed");
        var (jpeg, _) = camera.Frames.Picture("View", null, TimeSpan.FromSeconds(30), _ => "").Result;
        Check(jpeg is [0xFF, 0xD8, ..] && frame.References == 0 && PixelFrame.InPool(frame.Data), "the camera encodes its frame, then gives the buffer back");
        Console.WriteLine("PASS video: a copy and a camera's Show share a frame's pixels by reference; Dispose gives a reference back once, and the buffer goes round after the last");
    }

    static void Sources()
    {
        foreach (var good in new[] { "rtsp://admin:pw@door.local:8554/live", "RTSPS://door/x", "http://cam/video.mjpg?user=a&pwd=b", "https://cam:8443/x", "rtmp://live/app/key", "/media/clips/front door.mp4" })
            Check(FeedSource.Video(good, true, true).Text == good, "taken: " + good);
        foreach (var bad in new[] { "", "  ", "concat:/a.mp4|/b.mp4", "pipe:0", "file:/media/a.mp4", "data:video/mp4;base64,AAAA", "subfile,,0,10,:/etc/passwd", "testsrc", "clip.mp4", "./clip.mp4", "ftp://host/x", "http:///x", "http://cam/a b", " /media/a.mp4", "/media/a\n.mp4", "rtsp://", "udp://0.0.0.0:1234" })
            Throws<ArgumentException>(() => SharpVideo.Open(bad), "refused: " + bad);
        Check(VideoSources.Protocols("file") == "file" && VideoSources.Protocols("https") == "http,https,tcp,tls" && VideoSources.Protocols("rtsps") == "rtsp,rtp,udp,tcp,tls" && VideoSources.Protocols("rtmp") == "rtmp,tcp", "the protocols ffmpeg may open for each scheme");
        // Only a file loops and plays in real time; neither splits the feed of an address.
        Check(FeedSource.Video("/media/a.mp4", true, true) is { Loop: true, RealTime: true } && FeedSource.Video("rtsp://door/x", true, true) == FeedSource.Video("rtsp://door/x", false, false), "only a file loops and plays in real time");
        // The log never has an address's password, path or query.
        foreach (var (given, shown) in new[] { ("rtsp://admin:hunter2@door.local:8554/live?token=abc", "rtsp://door.local:8554"), ("HTTP://cam/x", "http://cam"), ("/media/a.mp4", "/media/a.mp4") })
            Check(SharpVideo.Open(given).ToString() == shown && FeedSource.Video(given, true, true).ToString() == shown, $"{given} is shown as {SharpVideo.Open(given)}");
        Check(SharpVideo.Open("camera.front_door").ToString() == "camera.front_door" && SharpVideo.Open(new HassSharp.Entities.Entity("camera.porch")).ToString() == "camera.porch", "a camera by its id, or its handle");
        Throws<ArgumentException>(() => SharpVideo.Open(new HassSharp.Entities.Entity("light.porch")), "a handle that is not a camera's");
        Throws<ArgumentException>(() => SharpVideo.Open("/media/a.mp4", new VideoOptions { MaxWidth = 8 }), "a width under 16");
        // What ffmpeg is given.
        var file = string.Join(' ', CameraFeed.Arguments("/media/a.mp4", (640, 0), "file", realTime: true, loop: true));
        Check(file == "-hide_banner -loglevel error -nostdin -protocol_whitelist file -re -stream_loop -1 -i /media/a.mp4 -an -vf scale=w='min(iw,640)':h=ih:force_original_aspect_ratio=decrease:out_range=full -f yuv4mpegpipe -pix_fmt yuv420p -", "a looped file in real time: " + file);
        var once = string.Join(' ', CameraFeed.Arguments("rtsp://door/x", null, "rtsp,rtp,udp,tcp,tls", single: true));
        Check(once == "-hide_banner -loglevel error -nostdin -protocol_whitelist rtsp,rtp,udp,tcp,tls -rtsp_transport tcp -i rtsp://door/x -an -frames:v 1 -vf scale=out_range=full -f yuv4mpegpipe -pix_fmt yuv420p -", "one frame of an RTSP address: " + once);
        Console.WriteLine("PASS video: http, https, rtsp, rtsps, rtmp addresses and absolute paths are taken, every other input refused; ffmpeg opens only the scheme's protocols; a file loops in real time; the log has no password");
    }

    static void Paths()
    {
        // media/ is allowed; outside/ is not. A link inside media/ that leads outside, and a .. out of it, are outside.
        var outside = Path.Combine(directory, "outside");
        Directory.CreateDirectory(outside);
        var inside = Path.Combine(allowed, "clip.mp4");
        var secret = Path.Combine(outside, "secret.mp4");
        File.WriteAllText(inside, "fake://fps=10");
        File.WriteAllText(secret, "fake://fps=10");
        File.CreateSymbolicLink(Path.Combine(allowed, "escape.mp4"), secret);
        Directory.CreateSymbolicLink(Path.Combine(allowed, "elsewhere"), outside);
        File.CreateSymbolicLink(Path.Combine(outside, "into.mp4"), inside);
        var real = VideoSources.RealPath(inside)!;
        string[] folders = [Path.Combine(directory, "media-link")];
        Check(VideoSources.Resolve(inside, folders) == (real, "file"), "a file in a folder allowed (named through a link) is read by its real path: " + real);
        Check(VideoSources.Resolve(Path.Combine(outside, "into.mp4"), folders).Input == real, "a link from outside to a file inside is the file inside");
        foreach (var refused in new[] { secret, Path.Combine(allowed, "escape.mp4"), Path.Combine(allowed, "elsewhere", "secret.mp4"), Path.Combine(allowed, "..", "outside", "secret.mp4") })
        {
            var error = Throws<InvalidOperationException>(() => VideoSources.Resolve(refused, folders), "outside the folders allowed: " + refused);
            Check(error.Message.Contains("allowlist_external_dirs"), "the refusal says how to allow it: " + error.Message);
        }
        // A .. after a link goes up from where the link leads: media/elsewhere/../media is outside's parent's media, the folder allowed.
        Check(VideoSources.Resolve(Path.Combine(allowed, "elsewhere", "..", "media", "clip.mp4"), folders).Input == real, "a .. after a link goes up from where it leads");
        Check(Throws<InvalidOperationException>(() => VideoSources.Resolve(Path.Combine(allowed, "missing.mp4"), folders), "a missing file").Message.Contains("does not exist"), "a missing file is refused");
        Check(Throws<InvalidOperationException>(() => VideoSources.Resolve(allowed, folders), "a folder").Message.Contains("a folder"), "a folder is refused");
        Throws<InvalidOperationException>(() => VideoSources.Resolve(inside, []), "no folder allowed");
        // A loop of links is no path.
        File.CreateSymbolicLink(Path.Combine(outside, "a"), Path.Combine(outside, "b"));
        File.CreateSymbolicLink(Path.Combine(outside, "b"), Path.Combine(outside, "a"));
        Check(VideoSources.RealPath(Path.Combine(outside, "a")) == null, "a loop of links resolves to none");
        Check(VideoSources.Resolve("http://cam/x?pwd=1", []) == ("http://cam/x?pwd=1", "http,https,tcp,tls"), "an address is read as it is, with its scheme's protocols");
        Console.WriteLine("PASS video: a file is read by its real path only inside a folder Home Assistant allows; a link or a .. that leads outside, a missing file, a folder and a loop of links are refused");
    }

    // A clip in the folder allowed: the fake ffmpeg reads its text as its stream source.
    static string Clip(string name, string config)
    {
        var path = Path.Combine(allowed, name);
        File.WriteAllText(path, config);
        return path;
    }

    static int Luma(SharpImage image) => Frame(image)!.Data[0];

    static async Task Next()
    {
        var args = Path.Combine(directory, "next.args");
        var clip = Clip("next.mp4", $"fake://fps=40,args={args}");
        await using var video = SharpVideo.Open(clip);
        var lumas = new List<int>();
        for (var i = 0; i < 5; i++)
        {
            using var frame = (await video.NextFrameAsync().WaitAsync(TimeSpan.FromSeconds(10)))!;
            lumas.Add(Luma(frame));
        }
        Check(lumas.Zip(lumas.Skip(1)).All(pair => pair.Second > pair.First), "each frame a reader gets is newer than the last: " + string.Join(",", lumas));
        // Another reader of the same file shares the feed and gets the newest at once.
        await using (var other = SharpVideo.Open(clip))
        {
            using var shared = (await other.NextFrameAsync())!;
            Check(Luma(shared) >= lumas[^1], "another reader gets the newest");
        }
        var feed = CameraFeeds.All.Single(f => f.Source.Text == clip);
        Check(feed.Started == 1, "one ffmpeg feeds both readers");
        Check(File.ReadAllText(args).Trim() == $"-hide_banner -loglevel error -nostdin -protocol_whitelist file -re -stream_loop -1 -i {VideoSources.RealPath(clip)} -an -vf scale=out_range=full -f yuv4mpegpipe -pix_fmt yuv420p -",
            "ffmpeg reads the file's real path with the file protocol alone, in real time, looped: " + File.ReadAllText(args));
        // A frame is the video's own YUV, and a sized reader is a feed of its own.
        await using var small = SharpVideo.Open(clip, new VideoOptions { MaxWidth = 32 });
        using var fitted = (await small.NextFrameAsync())!;
        Check((fitted.Width, fitted.Height) == (32, 24) && Frame(fitted)!.Format == SharpPixelFormat.Yuv420, $"a frame fits the size, YUV as ffmpeg gave it: {fitted.Width}x{fitted.Height}");
        Console.WriteLine("PASS video: a reader's frames are each newer than the last; readers of one file share one ffmpeg, which reads its real path with the file protocol, in real time and looped");
    }

    static async Task Looped()
    {
        // Three frames, looped: frames keep coming past the third.
        var clip = Clip("loop.mp4", "fake://fps=60,frames=3");
        await using var video = SharpVideo.Open(clip);
        var count = 0;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var frame in video.Frames(cancel.Token))
        {
            frame.Dispose();
            if (++count == 8) break;
        }
        Check(count == 8, "a looped file of three frames gives frames past its third");
        Console.WriteLine("PASS video: a looped file plays on past its end");
    }

    static async Task Once()
    {
        var clip = Clip("once.mp4", "fake://fps=20,frames=3");
        await using var video = SharpVideo.Open(clip, new VideoOptions { Loop = false });
        var lumas = new List<int>();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var frame in video.Frames(cancel.Token))
            using (frame) lumas.Add(Luma(frame));
        Check(lumas.Count is >= 1 and <= 3 && lumas[^1] == 22, "a file that does not loop gives its frames to its last, then ends: " + string.Join(",", lumas));
        var feed = CameraFeeds.All.Single(f => f.Source.Text == clip);
        Check(feed.Finished && feed.Started == 1, "its ffmpeg is not started again");
        Check(await video.NextFrameAsync() == null, "and gives no more");
        // A camera's frame of it keeps its last frame.
        HassCameras.MakingFrame.Value = "lens:once";
        try
        {
            using var last = await HassCameras.VideoFrameAsync(clip, loop: false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            using var again = await HassCameras.VideoFrameAsync(clip, loop: false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Check(Luma(last) == 22 && Luma(again) == 22, "a camera's frames of a file that ended are its last frame");
        }
        finally { HassCameras.MakingFrame.Value = null; }
        Console.WriteLine("PASS video: a file that does not loop ends with its last frame, which a camera keeps showing");
    }

    static async Task Closed()
    {
        var pids = Path.Combine(directory, "closed.pids");
        var clip = Clip("closed.mp4", $"fake://fps=30,pids={pids}");
        var video = SharpVideo.Open(clip);
        (await video.NextFrameAsync())!.Dispose();
        var pid = int.Parse(File.ReadAllLines(pids)[0]);
        Check(Alive(pid), "ffmpeg runs while the video is read");
        var feed = CameraFeeds.All.Single(f => f.Source.Text == clip);
        await video.DisposeAsync();
        await video.DisposeAsync();
        Check(feed.Closed, "disposing the last reader closes the feed at once");
        await EntityTestHome.Until(() => !Alive(pid), "its ffmpeg is killed");
        await ThrowsAsync<ObjectDisposedException>(() => video.NextFrameAsync(), "a frame of a reader disposed");
        // Unloading kills a feed whose reader is still open; the reader's next frame then says why there is none.
        File.Delete(pids);
        await using var open = SharpVideo.Open(clip);
        (await open.NextFrameAsync())!.Dispose();
        var running = int.Parse(File.ReadAllLines(pids)[0]);
        var waiting = open.NextFrameAsync();
        await CameraFeeds.CloseAllAsync();
        await EntityTestHome.Until(() => !Alive(running), "unloading kills the video's ffmpeg");
        try { (await waiting.WaitAsync(TimeSpan.FromSeconds(5)))?.Dispose(); } catch (InvalidOperationException) { }
        var unloading = await ThrowsAsync<InvalidOperationException>(() => open.NextFrameAsync(), "a frame while HassSharp unloads");
        Check(unloading.Message.Contains("unloading"), unloading.Message);
        CameraFeeds.Open();
        Console.WriteLine("PASS video: disposing the last reader kills ffmpeg at once; unloading kills it under an open reader, whose next frame says why there is none");
    }

    static async Task Outside()
    {
        var outside = Path.Combine(directory, "outside", "secret.mp4");
        await using var video = SharpVideo.Open(outside);
        var refused = await ThrowsAsync<InvalidOperationException>(() => video.NextFrameAsync().WaitAsync(TimeSpan.FromSeconds(10)), "a file outside the folders allowed");
        Check(refused.Message.Contains("allowlist_external_dirs") && CameraFeeds.All.Single(f => f.Source.Text == outside).Started == 0, "is refused before ffmpeg starts: " + refused.Message);
        var graph = await ThrowsAsync<InvalidOperationException>(() => HassCameras.VideoFrameAsync(outside, true, CancellationToken.None), "a Video frame of a file outside");
        Check(graph.Message.Contains("allowlist_external_dirs"), graph.Message);
        // Without Home Assistant's ffmpeg, no video is read.
        var ask = PyInterop.BeginVideoSource;
        PyInterop.BeginVideoSource = (_, fail) => fail("Home Assistant's ffmpeg integration is not set up");
        try
        {
            await using var none = SharpVideo.Open(Clip("none.mp4", "fake://fps=10"));
            var missing = await ThrowsAsync<InvalidOperationException>(() => none.NextFrameAsync().WaitAsync(TimeSpan.FromSeconds(10)), "without ffmpeg");
            Check(missing.Message.Contains("ffmpeg integration is not set up"), missing.Message);
        }
        finally { PyInterop.BeginVideoSource = ask; }
        Console.WriteLine("PASS video: a file outside the folders Home Assistant allows is refused before ffmpeg starts, in code and in a graph; without Home Assistant's ffmpeg nothing is read");
    }

    static async Task Graph()
    {
        var args = Path.Combine(directory, "graph.args");
        var clip = Clip("graph.mp4", $"fake://fps=40,args={args}");
        // No feed open and no camera's frame: one frame read by a short run.
        using (var grabbed = await HassCameras.VideoFrameAsync(clip, true, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)))
            Check(grabbed.IsFrame && CameraFeeds.All.All(f => f.Source.Text != clip), "elsewhere and without a feed, one frame is read and no feed starts");
        Check(File.ReadAllText(args).Contains("-frames:v 1") && !File.ReadAllText(args).Contains("-re"), "by a short run of one frame: " + File.ReadAllText(args));
        // In a camera's frame: live, each newer.
        var lumas = new List<int>();
        HassCameras.MakingFrame.Value = "lens:graph";
        try
        {
            for (var i = 0; i < 3; i++)
                using (var live = await HassCameras.VideoFrameAsync(clip, true, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)))
                    lumas.Add(Luma(live));
        }
        finally { HassCameras.MakingFrame.Value = null; }
        Check(lumas.Zip(lumas.Skip(1)).All(pair => pair.Second > pair.First), "in a camera's frame, each frame newer: " + string.Join(",", lumas));
        // Elsewhere with the feed open: its newest, no other run.
        var runs = File.ReadAllLines(args).Length;
        using (var newest = await HassCameras.VideoFrameAsync(clip, true, CancellationToken.None))
            Check(newest.IsFrame && File.ReadAllLines(args).Length == runs, "elsewhere with the feed open, its newest frame");
        // A Video frame node in a graph, through Draw rectangle into a camera's Image.
        await EntityGraphRuntimeTests.AVideoFrameShowsInACamera(clip);
        Console.WriteLine("PASS video: a Video frame reads one frame by a short run elsewhere, live frames in a camera's frame, and an open feed's newest elsewhere");
    }

    // A C# camera that pushes each frame of a video with a box drawn on it, through a whole manager.
    static async Task Push()
    {
        var clip = Clip("push.mp4", "fake://fps=30");
        var code = $$"""
            public class Boxed : SharpCamera
            {
                SharpVideo? video;
                protected override Task Start()
                {
                    video = SharpVideo.Open({{JsonSerializer.Serialize(clip)}}, new VideoOptions { MaxWidth = 32 });
                    // One call at a time, each waiting for the next frame, until the definition stops.
                    Every(TimeSpan.FromMilliseconds(1), async () =>
                    {
                        using var frame = await video.NextFrameAsync(Stopping);
                        if (frame != null) Show(frame.DrawRect(0, 0, 32, 24, SharpColor.Red, 2));
                    });
                    return Task.CompletedTask;
                }
                protected override async Task Stop()
                {
                    if (video != null) await video.DisposeAsync();
                }
            }
            """;
        await using var home = new EntityTestHome("video-push");
        var api = new HassSharpManager(home.Root);
        try
        {
            await api.Init();
            var descriptor = new NodeLibraryCompiler(Diagnostics).Preview([], [new CustomNodeSource("c", code, "csharp_entity")]).Snapshot.Custom["c"];
            Check(descriptor.Valid, "the push camera's code compiles: " + string.Join(" | ", descriptor.Diagnostics.Select(d => d.Message)));
            var node = EntityNodeTests.CSharpEntity("camera", "camera", new() { ["source"] = code, ["signature"] = descriptor.Signature!.DeepClone(), ["arguments"] = new JsonObject() });
            var definition = new JsonObject
            {
                ["name"] = "Pushed", ["settings"] = new JsonArray(),
                ["logic"] = new JsonObject { ["kind"] = "graph", ["graph"] = new JsonObject { ["version"] = 5, ["name"] = "Pushed", ["nodes"] = new JsonArray(node), ["edges"] = new JsonArray() } },
            };
            var answer = JsonNode.Parse(await api.EntityRequest(JsonSerializer.Serialize(new { operation = "save", id = "pushed", expectedRevision = 0, definition })))!;
            Check(answer["ok"]!.GetValue<bool>() && answer["result"]!["active"]!.GetValue<bool>(), "the push camera runs: " + answer.ToJsonString());
            // Home Assistant asks at its cadence (1/30 s): each answer is the newest frame pushed, with its box.
            byte[]? first = null;
            await EntityTestHome.Until(() => { try { first = api.CameraImage("pushed:camera").Result; return true; } catch { return false; } }, "the camera shows a pushed frame");
            var greys = new List<int>();
            for (var i = 0; i < 4; i++)
            {
                await Task.Delay(120);
                var jpeg = await api.CameraImage("pushed:camera");
                TurboJpeg.Header(jpeg, out var w, out var h);
                var rgb = new byte[w * h * 3];
                TurboJpeg.Decode(jpeg, rgb, w, h);
                Check((w, h) == (32, 24) && rgb[0] > 180 && rgb[1] < 90, $"a pushed frame has its box: {w}x{h} {rgb[0]},{rgb[1]},{rgb[2]}");
                greys.Add(rgb[(12 * 32 + 16) * 3]);
            }
            Check(greys.Zip(greys.Skip(1)).All(pair => pair.Second > pair.First), "each answer is a newer frame: " + string.Join(",", greys));
            Check(home.Pictures().Length == 0, "no snapshot is taken");
        }
        finally
        {
            await api.UnloadAsync();
        }
        Check(CameraFeeds.All.Length == 0, "unloading the manager closes the video's feed");
        Console.WriteLine("PASS video: a C# camera pushes each frame of a video with a box on it, and Home Assistant gets the newest at its cadence; unloading closes the feed");
    }

    static async Task Pull() => await EntityGraphRuntimeTests.ACustomNodesPixelsShowInACamera();
}
