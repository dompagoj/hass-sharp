using System.Diagnostics;

namespace HassSharp;

/// <summary>
/// One camera's current frame, and how the requests for its picture share the frames made (decision 4 of the camera's
/// design). The current frame is set by <see cref="Show"/>. A camera that makes its frames when they are needed makes one
/// at a time: a request while one is being made shares it, and one within 0.8 of the camera's interval of the start of
/// the last one gets that one again, or within one second of it when it failed (<see cref="Share"/>). A frame that passed
/// its time limit and still runs is still the one being made: no other starts beside it, however long it hangs, and the
/// requests get its timed-out outcome until it has ended. The last frame whose JPEG was made is kept, with the time it
/// was last made or confirmed: when making one failed, or the current frame cannot be encoded, that good frame is the
/// answer for <see cref="GoodWithin"/> after that time; after that, and when there has never been one, the request fails
/// (<see cref="Picture"/>). Each frame is encoded once, on a pool thread, whoever asks. A new revision of the definition
/// takes over the current and the good frame of the camera it replaces (<see cref="Handed"/>, <see cref="TakeOver"/>),
/// and nothing else. Thread-safe.
/// </summary>
internal sealed class CameraFrames
{
    /// <summary>
    /// The shortest time the last good frame is still given while making one fails (Home Assistant asks again and again);
    /// a camera with a long interval keeps it for two of its intervals (<see cref="GoodWithin"/>). Settable for tests.
    /// </summary>
    internal static TimeSpan GoodFor { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>How long making a frame may take: Home Assistant gives up on a picture after 10 s. Settable for tests.</summary>
    internal static TimeSpan MakeLimit { get; set; } = TimeSpan.FromSeconds(8);
    /// <summary>How long one second of a camera's <see cref="SharpCamera.FrameInterval"/> lasts; settable for tests, as <see cref="SharpHttp.ConnectTimeout"/> is.</summary>
    internal static TimeSpan IntervalSecond { get; set; } = TimeSpan.FromSeconds(1);
    // Of the interval: a request this soon after the last frame was begun gets that frame again.
    const double Reuse = 0.8;

    /// <summary>How long the last good frame is given for a camera whose frames are <paramref name="interval"/> apart: <see cref="GoodFor"/>, or two intervals when that is longer.</summary>
    internal static TimeSpan GoodWithin(TimeSpan interval) => interval * 2 > GoodFor ? interval * 2 : GoodFor;

    /// <summary>How making a frame ended: worked (no failure), or failed with the reason, as the person reads it.</summary>
    internal sealed record Outcome(string? Failure = null, bool TimedOut = false)
    {
        public static readonly Outcome Worked = new();
    }

    /// <summary>A frame a new revision takes over: its picture, its JPEG when already made, and when it was last made or confirmed.</summary>
    internal sealed record Kept(SharpImage Image, byte[]? Jpeg, long Confirmed);

    /// <summary>What a camera hands the camera of the same key in the revision that replaces it: its current frame and its last good one.</summary>
    internal sealed record Handover(Kept? Current, Kept? Good);

    // A frame and its JPEG, encoded once on first request; with the time it was last made or confirmed (under the gate).
    sealed class Frame
    {
        readonly Lazy<Task<byte[]>> jpeg;

        public Frame(SharpImage image, long confirmed, byte[]? encoded = null)
        {
            Image = image;
            Confirmed = confirmed;
            jpeg = encoded != null ? new(Task.FromResult(encoded)) : new(() => Task.Run(image.ToJpeg), LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public SharpImage Image { get; }
        public long Confirmed;
        public Task<byte[]> Jpeg => jpeg.Value;
        // The JPEG once it was made, without making it.
        public byte[]? Made => jpeg.IsValueCreated && jpeg.Value.IsCompletedSuccessfully ? jpeg.Value.Result : null;
        public Kept Kept() => new(Image, Made, Confirmed);
    }

    /// <summary>The making of one frame: how it ended for the requests (<paramref name="Done"/>), and the code making it
    /// (<paramref name="Made"/>), which may run on after a timeout.</summary>
    internal sealed record Making(Task<Outcome> Done, Task Made);

    sealed record Attempt(long Started, Making Making);

    readonly Lock gate = new();
    Frame? current;
    // The last frame whose JPEG was made: the current one, or one before it.
    Frame? good;
    Attempt? last;
    long begun;

    /// <summary>Why making or encoding a frame last failed, as last logged (<see cref="DefinitionHost.CameraImage"/>).</summary>
    internal FailureLog Failures { get; } = new();

    /// <summary>How many frames were begun to be made; for tests.</summary>
    internal long Begun => Interlocked.Read(ref begun);

    /// <summary>Whether the last frame begun is still being made, or its code still runs past its limit; for tests.</summary>
    internal bool Busy
    {
        get { lock (gate) return last is { Making: var making } && (!making.Done.IsCompleted || !making.Made.IsCompleted); }
    }

    /// <summary>Makes the picture the current frame; the camera owns it from now on (the caller keeps none of it).</summary>
    public void Show(SharpImage image)
    {
        var frame = new Frame(image, Stopwatch.GetTimestamp());
        lock (gate) current = frame;
    }

    /// <summary>A frame was made of none: the current frame is still the picture, as good as if shown again now.</summary>
    public void Keep()
    {
        lock (gate)
            if (current != null) current.Confirmed = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// The making of a frame a request shares: the one in flight (also one past its time limit whose code still runs), or
    /// the last one when it was begun less than 0.8 of <paramref name="interval"/> ago (and less than one second ago,
    /// <see cref="IntervalSecond"/>, when it failed); otherwise the one <paramref name="begin"/> begins now, which must not
    /// block (it is called under the lock).
    /// </summary>
    public Task<Outcome> Share(TimeSpan interval, Func<Making> begin)
    {
        lock (gate)
        {
            var now = Stopwatch.GetTimestamp();
            if (last is { Making: var shared } && (!shared.Done.IsCompleted || !shared.Made.IsCompleted || Stopwatch.GetElapsedTime(last.Started, now) < Reused(shared.Done.Result, interval)))
                return shared.Done;
            var making = begin();
            Interlocked.Increment(ref begun);
            last = new(now, making);
            return making.Done;
        }
    }

    // How long the requests share an attempt that has ended: a failed one no longer than a second, so that a camera with a
    // long interval tries again soon.
    static TimeSpan Reused(Outcome outcome, TimeSpan interval)
    {
        var reused = interval * Reuse;
        return outcome.Failure != null && reused > IntervalSecond ? IntervalSecond : reused;
    }

    /// <summary>Whether <paramref name="done"/> is the outcome of the camera's last attempt, which no later one has replaced.</summary>
    public bool IsLast(Task<Outcome> done)
    {
        lock (gate) return last?.Making.Done == done;
    }

    /// <summary>
    /// The picture for a request, as JPEG, once the making it shared ended with <paramref name="outcome"/> (null for a
    /// camera that makes none, whose current frame is the answer however old): the current frame, unless making one
    /// failed and the frame was last made or confirmed more than <paramref name="goodFor"/> ago. When the current frame
    /// cannot be encoded, <paramref name="failed"/> is told why (and gives the reason as the request words it), and the
    /// last good frame is the answer if it was last made or confirmed within <paramref name="goodFor"/>. Encoding runs on
    /// a pool thread. With the picture, whether it is the current frame.
    /// </summary>
    /// <param name="called">The camera, as a message names it.</param>
    /// <exception cref="InvalidOperationException">There is no frame, making or encoding one failed and the last good one is too old, or there is none.</exception>
    /// <exception cref="TimeoutException">Making one took too long and the last good one is too old.</exception>
    public async Task<(byte[] Jpeg, bool Current)> Picture(string called, Outcome? outcome, TimeSpan goodFor, Func<Exception, string> failed)
    {
        Frame? frame;
        long since;
        lock (gate) (frame, since) = (current, current?.Confirmed ?? 0);
        // The good frame is the current one or one before it: when the current one is too old, so is it.
        if (outcome is { Failure: { } why } && (frame == null || Stopwatch.GetElapsedTime(since) > goodFor))
        {
            var message = $"{called} could not make a frame: {why}";
            throw outcome.TimedOut ? new TimeoutException(message) : new InvalidOperationException(message);
        }
        if (frame == null) throw new InvalidOperationException($"{called} has not shown a frame yet");
        try
        {
            var jpeg = await frame.Jpeg;
            lock (gate)
                // A frame encoded late, after a newer one was, is not the good one.
                if (good == null || good == frame || Newer(frame)) good = frame;
            return (jpeg, true);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            var reason = failed(failure);
            Frame? earlier;
            long confirmed;
            lock (gate) (earlier, confirmed) = (good, good?.Confirmed ?? 0);
            if (earlier != null && earlier != frame && Stopwatch.GetElapsedTime(confirmed) <= goodFor)
                return (await earlier.Jpeg, false);
            throw new InvalidOperationException($"{called} could not make a frame: {reason}");
        }
    }

    // Under the gate: whether the frame was shown after the good one (frames are shown one after the other, so the later one was confirmed later).
    bool Newer(Frame frame) => frame.Confirmed >= good!.Confirmed;

    /// <summary>What the camera of the same key in the revision replacing this one takes over: the current and the last good frame, with their times.</summary>
    public Handover Handed()
    {
        lock (gate) return new(current?.Kept(), good == null ? null : good == current ? null : good.Kept());
    }

    /// <summary>
    /// The camera of the revision this one replaced handed over its frames: they become this camera's, unless it has
    /// shown one of its own meanwhile (as the revision prepared). The frames are made anew here, of copies of the pictures,
    /// so nothing of the replaced revision is kept; none is fresh for <see cref="Share"/>, which only an attempt of this camera is.
    /// </summary>
    public void TakeOver(Handover handed)
    {
        if (handed.Current is not { } kept) return;
        // Copies (the bytes shared), as a picture is for one flow at a time: a request to the replaced revision may still
        // be encoding its own.
        var taken = new Frame(kept.Image.Copy(), kept.Confirmed, kept.Jpeg);
        // The current frame was the good one when its JPEG was made and no other is handed.
        var earlier = handed.Good is { } was ? new Frame(was.Image.Copy(), was.Confirmed, was.Jpeg) : kept.Jpeg != null ? taken : null;
        lock (gate)
        {
            if (current != null) return;
            (current, good) = (taken, earlier);
        }
    }
}
