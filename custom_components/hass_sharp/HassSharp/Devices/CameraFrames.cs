using System.Diagnostics;

namespace HassSharp;

/// <summary>
/// One camera's current frame, and how the requests for its picture share the frames made (decision 4 of the camera's
/// design). The current frame is set by <see cref="Show"/>. A camera that makes its frames when they are needed makes one
/// at a time: a request while one is being made shares it, and one within 0.8 of the camera's interval of the start of
/// the last one gets that one again (<see cref="Share"/>). A frame that passed its time limit and still runs is still the
/// one being made: no other starts beside it, however long it hangs, and the requests get its timed-out outcome until it
/// has ended. When making one failed, the current frame is still the answer
/// for <see cref="GoodFor"/> after it was last made or confirmed; after that, and when there has never been one, the
/// request fails (<see cref="Picture"/>). Each frame is encoded once, on a pool thread, whoever asks. Thread-safe.
/// </summary>
internal sealed class CameraFrames
{
    /// <summary>How long the last good frame is still given while making one fails (Home Assistant asks again and again); settable for tests.</summary>
    internal static TimeSpan GoodFor { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>How long making a frame may take: Home Assistant gives up on a picture after 10 s. Settable for tests.</summary>
    internal static TimeSpan MakeLimit { get; set; } = TimeSpan.FromSeconds(8);
    /// <summary>How long one second of a camera's <see cref="SharpCamera.FrameInterval"/> lasts; settable for tests, as <see cref="SharpHttp.ConnectTimeout"/> is.</summary>
    internal static TimeSpan IntervalSecond { get; set; } = TimeSpan.FromSeconds(1);
    // Of the interval: a request this soon after the last frame was begun gets that frame again.
    const double Reuse = 0.8;

    /// <summary>How making a frame ended: worked (no failure), or failed with the reason, as the person reads it.</summary>
    internal sealed record Outcome(string? Failure = null, bool TimedOut = false)
    {
        public static readonly Outcome Worked = new();
    }

    // A frame and its JPEG, encoded once on first request.
    sealed class Frame(SharpImage image)
    {
        readonly Lazy<Task<byte[]>> jpeg = new(() => Task.Run(image.ToJpeg), LazyThreadSafetyMode.ExecutionAndPublication);
        public Task<byte[]> Jpeg => jpeg.Value;
    }

    /// <summary>The making of one frame: how it ended for the requests (<paramref name="Done"/>), and the code making it
    /// (<paramref name="Made"/>), which may run on after a timeout.</summary>
    internal sealed record Making(Task<Outcome> Done, Task Made);

    sealed record Attempt(long Started, Making Making);

    readonly object gate = new();
    Frame? current;
    // When the current frame was last shown, or kept by a frame made of none.
    long confirmed;
    Attempt? last;
    long begun;

    /// <summary>Why making or encoding a frame last failed, as last logged (<see cref="DefinitionHost.CameraImage"/>).</summary>
    internal FailureLog Failures { get; } = new();

    /// <summary>How many frames were begun to be made; for tests.</summary>
    internal long Begun => Interlocked.Read(ref begun);

    /// <summary>Makes the picture the current frame; the camera owns it from now on (the caller keeps none of it).</summary>
    public void Show(SharpImage image)
    {
        var frame = new Frame(image);
        lock (gate) (current, confirmed) = (frame, Stopwatch.GetTimestamp());
    }

    /// <summary>A frame was made of none: the current frame is still the picture, as good as if shown again now.</summary>
    public void Keep()
    {
        lock (gate) confirmed = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// The making of a frame a request shares: the one in flight (also one past its time limit whose code still runs), or
    /// the last one when it was begun less than 0.8 of <paramref name="interval"/> ago; otherwise the one
    /// <paramref name="begin"/> begins now, which must not block (it is called under the lock).
    /// </summary>
    public Task<Outcome> Share(TimeSpan interval, Func<Making> begin)
    {
        lock (gate)
        {
            var now = Stopwatch.GetTimestamp();
            if (last is { Making: var shared } && (!shared.Done.IsCompleted || !shared.Made.IsCompleted || Stopwatch.GetElapsedTime(last.Started, now) < interval * Reuse))
                return shared.Done;
            var making = begin();
            Interlocked.Increment(ref begun);
            last = new(now, making);
            return making.Done;
        }
    }

    /// <summary>
    /// The picture for a request, as JPEG, once the making it shared ended with <paramref name="outcome"/> (null for a
    /// camera that makes none): the current frame, unless making one failed and the frame was last made or confirmed more
    /// than <see cref="GoodFor"/> ago. A frame that cannot be encoded fails the request; encoding runs on a pool thread.
    /// </summary>
    /// <param name="called">The camera, as a message names it.</param>
    /// <exception cref="InvalidOperationException">There is no frame, or making one failed and the last good one is too old.</exception>
    /// <exception cref="TimeoutException">Making one took too long and the last good one is too old.</exception>
    public Task<byte[]> Picture(string called, Outcome? outcome)
    {
        Frame? frame;
        long since;
        lock (gate) (frame, since) = (current, confirmed);
        if (outcome is { Failure: { } why } && (frame == null || Stopwatch.GetElapsedTime(since) > GoodFor))
        {
            var message = $"{called} could not make a frame: {why}";
            throw outcome.TimedOut ? new TimeoutException(message) : new InvalidOperationException(message);
        }
        return frame?.Jpeg ?? throw new InvalidOperationException($"{called} has not shown a frame yet");
    }
}
