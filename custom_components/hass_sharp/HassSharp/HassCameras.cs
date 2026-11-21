namespace HassSharp;

/// <summary>
/// Pictures of Home Assistant's cameras: the one way a definition's code and a graph's step take one. How a picture is
/// taken follows from what asks, with no setting:
/// <list type="number">
/// <item><b>Live</b>, while a frame of a definition's camera is made (<see cref="MakingFrame"/>, which
/// <see cref="DefinitionHost"/> sets for a graph's frame flow and a C# camera's <c>Frame()</c> and <c>OnFrame</c>): the
/// next raw frame of the camera's live feed (<see cref="CameraFeeds"/>) newer than the last one that camera got, waited for
/// when it has had the newest; the feed starts when it is not running.</item>
/// <item>Otherwise, while a live feed of the camera of that size gives frames, its newest one, encoded as a JPEG when one
/// is needed.</item>
/// <item>Otherwise, and when the live feed gives no frame (the camera has no stream source, Home Assistant's ffmpeg is not
/// set up, or ffmpeg keeps failing), a <b>snapshot</b>: <c>camera.async_get_image</c>, a JPEG within 10 seconds, passed on
/// as it is; so a camera's frames made of a camera without a feed are pictures taken one after the other.</item>
/// </list>
/// Python refuses a snapshot of a camera of the asking definition itself, which would wait for itself (a HassSharp camera
/// has no stream source, so it never has a live feed).
/// </summary>
static class HassCameras
{
    /// <summary>
    /// While a frame of a definition's camera is made: that camera's unique id, so that each camera gets the frames of a
    /// live feed in turn, none twice. Set by <see cref="DefinitionHost"/> for the code making the frame, and flows to what it awaits.
    /// </summary>
    internal static readonly AsyncLocal<string?> MakingFrame = new();

    /// <summary>A picture of the camera, as it is now: a frame of its live feed, or a snapshot (see <see cref="HassCameras"/>).</summary>
    /// <param name="definitionId">The id of the definition asking; empty for none.</param>
    /// <param name="entityId">The camera, such as <c>camera.front_door</c>.</param>
    /// <param name="cancellation">Ends the wait; the picture Home Assistant still sends is then dropped.</param>
    /// <param name="size">
    /// At most this size (<see cref="Size"/>), its aspect ratio kept and never larger than the camera's own; the camera's
    /// own when none. A side of 0 has no limit. ffmpeg scales a live feed's frames to fit; a snapshot is asked of Home
    /// Assistant at that size when both sides are given, and then made to fit here (<see cref="SharpImage.Fitted"/>), as
    /// Home Assistant scales by eighths to at least the size it is asked for.
    /// </param>
    /// <exception cref="InvalidOperationException">Home Assistant gave no picture (its message says why), or pictures cannot be taken here.</exception>
    /// <exception cref="ArgumentException">What came is not a JPEG of at most 20 MiB.</exception>
    internal static async Task<SharpImage> SnapshotAsync(string definitionId, string entityId, CancellationToken cancellation, (int Width, int Height)? size = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (MakingFrame.Value is { } camera)
        {
            if (await CameraFeeds.NextAsync(camera, entityId, size, cancellation) is { } live) return live;
        }
        else if (CameraFeeds.Newest(entityId, size) is { } newest) return newest;
        return await Snapshot(definitionId, entityId, size, cancellation);
    }

    /// <summary>
    /// A frame of a video, by its address or a file's path, as a Video frame node takes one, by the same rule as a camera's:
    /// <b>live</b> while a frame of a definition's camera is made (the next frame of the video's feed newer than the last
    /// that camera got, or a file's last once it ended without looping); otherwise the newest frame of an open feed of the
    /// video; otherwise, and while the feed gives no frame, one frame read by a short run of ffmpeg
    /// (<see cref="CameraFeed.GrabAsync"/>). A file plays in real time, and with <paramref name="loop"/> starts again when it ends.
    /// </summary>
    /// <param name="source">An http, https, rtsp, rtsps or rtmp address, or a file's absolute path in a folder Home Assistant allows.</param>
    /// <param name="loop">Whether a file starts again from its beginning when it ends.</param>
    /// <param name="cancellation">Ends the wait.</param>
    /// <param name="size">At most this size (<see cref="Size"/>), as ffmpeg fits it; the video's own when none.</param>
    /// <exception cref="ArgumentException">The source is not one a video is read from.</exception>
    /// <exception cref="InvalidOperationException">The video is refused, or gave no frame; the message says why.</exception>
    internal static async Task<SharpImage> VideoFrameAsync(string source, bool loop, CancellationToken cancellation, (int Width, int Height)? size = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var video = FeedSource.Video(source, loop, realTime: true);
        if (MakingFrame.Value is { } camera)
        {
            if (CameraFeeds.Join(video, size) is { } feed)
            {
                if (await feed.NextAsync(camera, cancellation) is { } live) return live;
                if (feed.Finished && feed.Newest() is { } last) return last;
            }
        }
        else if (CameraFeeds.Newest(video, size) is { } newest) return newest;
        return await CameraFeed.GrabAsync(video, size, cancellation);
    }

    /// <summary>The fewest and most pixels a side of a picture may be limited to.</summary>
    internal const int SmallestSide = 16, LargestSide = 8192;

    /// <summary>
    /// The size a picture is to fit in: at most <paramref name="maxWidth"/> wide and <paramref name="maxHeight"/> high,
    /// each 16 to 8192 pixels or 0 for no limit; null, the camera's own size, when neither side is limited.
    /// </summary>
    /// <exception cref="ArgumentException">A side is neither 0 nor 16 to 8192.</exception>
    internal static (int Width, int Height)? Size(int maxWidth, int maxHeight)
    {
        if (maxWidth is not 0 and (< SmallestSide or > LargestSide)) throw new ArgumentException($"A picture's largest width is {SmallestSide} to {LargestSide} pixels, or 0 for any width, not {maxWidth}", nameof(maxWidth));
        if (maxHeight is not 0 and (< SmallestSide or > LargestSide)) throw new ArgumentException($"A picture's largest height is {SmallestSide} to {LargestSide} pixels, or 0 for any height, not {maxHeight}", nameof(maxHeight));
        return maxWidth == 0 && maxHeight == 0 ? null : (maxWidth, maxHeight);
    }

    // A picture Home Assistant takes, through Python.
    static async Task<SharpImage> Snapshot(string definitionId, string entityId, (int Width, int Height)? size, CancellationToken cancellation)
    {
        var begin = PyInterop.BeginCameraSnapshot ?? throw new InvalidOperationException("Pictures from cameras are not available here");
        // Without the scaled call, the camera's own size; Home Assistant scales only to both sides.
        var scaled = size is { Width: > 0, Height: > 0 } ? PyInterop.BeginCameraSnapshotScaled : null;
        cancellation.ThrowIfCancellationRequested();
        // Python answers on its event loop: what follows runs elsewhere.
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Picture(byte[] bytes) => completion.TrySetResult(bytes);
        void Refused(string message) => completion.TrySetException(StoppingAnswers.Contains(message) ? new Stopped(message) : new InvalidOperationException(message));
        try
        {
            if (scaled != null && size is { } fit) scaled(definitionId, entityId, fit.Width, fit.Height, Picture, Refused);
            else begin(definitionId, entityId, Picture, Refused);
        }
        catch (Exception error) { completion.TrySetException(error); }
        var picture = await completion.Task.WaitAsync(cancellation);
        if (picture is not { Length: > 0 }) throw new InvalidOperationException($"{entityId} gave no picture");
        var image = SharpImage.Load(picture);
        // Made to fit on a pool thread, never on Home Assistant's event loop: the answer's continuations run asynchronously.
        return size is { } most ? image.Fitted(most.Width, most.Height) : image;
    }

    // What Python answers when Home Assistant or HassSharp stops while a picture is taken (entity_bridge.py's
    // SNAPSHOT_STOPPED and SNAPSHOT_CLOSED): no failure of the camera asking, which is stopping too.
    static readonly string[] StoppingAnswers = ["Home Assistant is stopping, so no picture was taken", "HassSharp is stopping, so no picture was taken"];

    /// <summary>No picture was taken because Home Assistant or HassSharp is stopping; the code asking sees an <see cref="InvalidOperationException"/>.</summary>
    internal sealed class Stopped(string message) : InvalidOperationException(message);

    /// <summary>The failure, or one it wraps, that says a picture was not taken because Home Assistant or HassSharp is stopping; null when it is none.</summary>
    internal static Stopped? Stopping(Exception? failure)
    {
        for (; failure != null; failure = failure.InnerException)
            if (failure is Stopped stopped) return stopped;
        return null;
    }
}
