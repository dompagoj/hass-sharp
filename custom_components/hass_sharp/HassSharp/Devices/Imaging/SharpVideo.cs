using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace HassSharp;

/// <summary>
/// How <see cref="SharpVideo"/> reads a video: the most each frame may be, and how a file plays.
/// </summary>
public sealed record VideoOptions
{
    /// <summary>The widest a frame may be, 16 to 8192 pixels; 0 (the default) for any width. ffmpeg fits each frame, its aspect ratio kept, never larger than the video's own.</summary>
    public int MaxWidth { get; init; }
    /// <summary>The highest a frame may be, 16 to 8192 pixels; 0 (the default) for any height.</summary>
    public int MaxHeight { get; init; }
    /// <summary>Whether a file starts again from its beginning when it ends (the default); otherwise its frames end with its last. A stream's address and a camera play as they come.</summary>
    public bool Loop { get; init; } = true;
    /// <summary>
    /// Whether a file plays at its own frame rate (the default), as a stream would come; otherwise ffmpeg reads it as fast
    /// as it decodes it, and a reader gets the newest frame when it asks, skipping those it was too slow for. A stream's
    /// address and a camera play as they come.
    /// </summary>
    public bool RealTime { get; init; } = true;
}

/// <summary>
/// The raw frames of a video, for code that shows, inspects or draws on them: of a stream's address (http, https, rtsp,
/// rtsps or rtmp), of a file (its absolute path, in a folder Home Assistant allows: <c>allowlist_external_dirs</c> or
/// <c>media_dirs</c>), or of one of Home Assistant's cameras (its live feed, of its stream source). One ffmpeg reads each
/// video and size, however many readers and Video frame nodes read it: it starts with the first frame asked for, starts
/// again with a pause when it ends or fails, closes when the last reader is disposed or nobody asked for a frame for 10
/// seconds (and starts again with the next), and is killed when HassSharp unloads. Each reader gets frames newer than the
/// last it got (<see cref="NextFrameAsync"/>, <see cref="Frames"/>); a reader too slow for the video skips frames rather
/// than falling behind. A frame is a <see cref="SharpImage"/> of the video's own pixels (YUV 4:2:0), which a camera shows
/// without copying them (<see cref="SharpCamera.Show"/>) and which is encoded as a JPEG without converting them when
/// nothing is drawn on it. Each frame holds a reference of its own to its pooled buffer: dispose it once done, so that the
/// buffer goes round at once; one never disposed is left to the garbage collector, never read again by another frame.
/// Credentials in an address never reach the log. Thread-safe; frames are for one reader at a time, as a picture is.
/// </summary>
/// <example>
/// A camera that shows a box on each frame of a stream, as fast as it comes:
/// <code>
/// public class Boxed : SharpCamera
/// {
///     SharpVideo? video;
///
///     // Start must return: a timer reads the frames, one call at a time, each waiting for the next frame.
///     protected override Task Start()
///     {
///         video = SharpVideo.Open("rtsp://door.local:8554/live", new VideoOptions { MaxWidth = 1280 });
///         Every(TimeSpan.FromMilliseconds(1), async () =>
///         {
///             using var frame = await video.NextFrameAsync(Stopping);
///             if (frame != null) Show(frame.DrawRect(40, 40, 200, 120, SharpColor.Green, 4));
///         });
///         return Task.CompletedTask;
///     }
///
///     protected override async Task Stop()
///     {
///         if (video != null) await video.DisposeAsync();
///     }
/// }
/// </code>
/// </example>
public sealed partial class SharpVideo : IAsyncDisposable
{
    readonly FeedSource source;
    readonly (int Width, int Height)? size;
    readonly string reader = "video:" + Guid.NewGuid().ToString("N");
    int disposed;

    SharpVideo(FeedSource source, (int Width, int Height)? size)
    {
        this.source = source;
        this.size = size;
    }

    /// <summary>
    /// A reader of a video: a stream's address (<c>rtsp://door.local:8554/live</c>, http, https, rtsps or rtmp), a file's
    /// absolute path (<c>/media/clip.mp4</c>) in a folder Home Assistant allows, or a camera's entity id
    /// (<c>camera.front_door</c>), whose live feed it reads. Nothing starts until a frame is asked for, so opening never
    /// waits; a file outside the folders allowed, or missing, is refused when its first frame is.
    /// </summary>
    /// <param name="source">The address, path or camera.</param>
    /// <param name="options">The most a frame may be, and how a file plays; the video's own size, looped and in real time when not given.</param>
    /// <exception cref="ArgumentException">The source is none of those (another of ffmpeg's protocols, such as <c>concat:</c> or <c>pipe:</c>, or a relative path), or a side is neither 0 nor 16 to 8192.</exception>
    public static SharpVideo Open(string source, VideoOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new();
        var size = HassCameras.Size(options.MaxWidth, options.MaxHeight);
        if (CameraId().IsMatch(source)) return new(FeedSource.Camera(source), size);
        return new(FeedSource.Video(source, options.Loop, options.RealTime), size);
    }

    /// <summary>A reader of the live feed of one of Home Assistant's cameras, as <see cref="Open(string, VideoOptions?)"/> reads one by its entity id.</summary>
    /// <param name="camera">The camera, such as a <c>CameraEntity</c> setting.</param>
    /// <param name="options">The most a frame may be; the camera's own size when not given.</param>
    /// <exception cref="ArgumentException">The entity is not a camera, or a side is neither 0 nor 16 to 8192.</exception>
    public static SharpVideo Open(Entities.Entity camera, VideoOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (!CameraId().IsMatch(camera.EntityId)) throw new ArgumentException($"{camera.EntityId} is not a camera", nameof(camera));
        return Open(camera.EntityId, options);
    }

    /// <summary>
    /// The next frame, newer than the last this reader got, waited for: at once when the video has one newer, else when it
    /// comes. The video starts when it is not running, and is waited for while ffmpeg starts again. A camera whose live
    /// feed gives no frame (it has no stream source, Home Assistant's ffmpeg is not set up, or ffmpeg keeps failing) gives a
    /// picture Home Assistant takes instead, as a Camera frame does. Null once a file that does not loop has given its
    /// last frame. The caller holds the frame: dispose it once done (see <see cref="SharpVideo"/>).
    /// </summary>
    /// <param name="cancellation">Ends the wait, such as <see cref="SharpComponent.Stopping"/>.</param>
    /// <exception cref="InvalidOperationException">The video is refused (a file outside the folders allowed, or missing; Home Assistant's ffmpeg not set up), a camera gave no picture, or HassSharp is unloading; the message says why.</exception>
    /// <exception cref="ObjectDisposedException">The reader was disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellation"/> was cancelled.</exception>
    public async Task<SharpImage?> NextFrameAsync(CancellationToken cancellation = default)
    {
        while (true)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            cancellation.ThrowIfCancellationRequested();
            if (source.IsCamera && PyInterop.BeginCameraSource == null) return await HassCameras.SnapshotAsync("", source.Text, cancellation, size);
            var feed = CameraFeeds.Join(source, size, reader) ?? throw new InvalidOperationException($"{source} gives no frames: HassSharp is unloading");
            if (await feed.NextAsync(reader, cancellation, waitOutRest: !source.IsCamera) is { } frame) return frame;
            if (feed.Finished) return null;
            if (feed.Refusal is { } refused) throw new InvalidOperationException($"{source} gives no frames: {refused}");
            // A camera's feed that rests: a picture Home Assistant takes, as Camera frame takes one.
            if (source.IsCamera && !feed.Closed) return await HassCameras.SnapshotAsync("", source.Text, cancellation, size);
            // The feed closed (idle, or failed): the next is joined.
        }
    }

    /// <summary>
    /// Each frame as it comes, newer than the last (<see cref="NextFrameAsync"/>), until <paramref name="cancellation"/> is
    /// cancelled, or a file that does not loop has given its last. Dispose each frame once done with it.
    /// </summary>
    /// <param name="cancellation">Ends the frames, such as <see cref="SharpComponent.Stopping"/>; the loop then throws <see cref="OperationCanceledException"/>.</param>
    /// <exception cref="InvalidOperationException">As <see cref="NextFrameAsync"/>.</exception>
    public async IAsyncEnumerable<SharpImage> Frames([EnumeratorCancellation] CancellationToken cancellation = default)
    {
        while (await NextFrameAsync(cancellation) is { } frame) yield return frame;
    }

    /// <summary>Closes the reader: the video's ffmpeg is stopped when nobody else reads it. Frames already given stay readable. Never throws.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) CameraFeeds.Leave(source, size, reader);
        return ValueTask.CompletedTask;
    }

    /// <summary>The camera, or the video's address without its password, path or query, or its file.</summary>
    public override string ToString() => source.ToString();

    [GeneratedRegex(@"^camera\.[a-z0-9_]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex CameraId();
}
