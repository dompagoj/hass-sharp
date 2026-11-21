using System.Diagnostics;
using System.Text.RegularExpressions;

namespace HassSharp;

/// <summary>
/// The live feeds of Home Assistant's cameras and of videos: the raw frames of each camera's stream, or of a video's
/// address or file (<see cref="FeedSource"/>), as one ffmpeg reads them, for the definitions' cameras whose frames are
/// made of them (<see cref="HassCameras.SnapshotAsync"/>, <see cref="HassCameras.VideoFrameAsync"/>) and for code that
/// reads them (<see cref="SharpVideo"/>). A picture Home Assistant
/// takes of an ffmpeg camera starts ffmpeg, connects and waits for a keyframe each time, a second or two; a feed reads the
/// stream once for as long as frames are asked of it. There is one feed for each camera or video and size asked for. It starts
/// when its first frame is asked for, and closes when no frame was asked of it for <see cref="IdleAfter"/>, and all of
/// them close when HassSharp unloads or Home Assistant stops (<see cref="CloseAllAsync"/>), which kills their ffmpeg. They
/// live in HassSharp's own assembly, so none holds anything of a definition's code. Thread-safe.
/// </summary>
internal static class CameraFeeds
{
    /// <summary>How long a feed lives after a frame was last asked of it. Settable for tests, as are the times below.</summary>
    internal static TimeSpan IdleAfter { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>How long ffmpeg has for its first frame; then it is stopped, and its try gave none.</summary>
    internal static TimeSpan FirstFrameLimit { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>How long ffmpeg may give no frame after one: then it is stopped and started again. Within a frame's time limit (<see cref="CameraFrames.MakeLimit"/>) with a picture taken after it.</summary>
    internal static TimeSpan StallLimit { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>The pause before ffmpeg starts again: after a run that gave frames, and after each try in a row that gave none.</summary>
    internal static TimeSpan[] RestartDelays { get; set; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];
    /// <summary>How many tries in a row may give no frame; then the feed rests for <see cref="RestAfter"/>.</summary>
    internal static int Tries { get; set; } = 3;
    /// <summary>How long a feed rests, its pictures taken one by one, when the camera has no stream source or ffmpeg failed <see cref="Tries"/> times in a row.</summary>
    internal static TimeSpan RestAfter { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>How long Home Assistant has to name the stream source.</summary>
    internal static TimeSpan SourceLimit { get; set; } = TimeSpan.FromSeconds(10);
    // How long closing waits for each ffmpeg it killed to be reaped.
    static readonly TimeSpan ReapLimit = TimeSpan.FromSeconds(5);

    static readonly Lock gate = new();
    static readonly Dictionary<(FeedSource Source, (int Width, int Height)? Size), CameraFeed> feeds = [];
    static bool closed;
    // Across every feed, so that the frames of a feed that started again are newer than any of the one before.
    static long sequence;

    internal static long NextSequence() => Interlocked.Increment(ref sequence);

    /// <summary>
    /// The next frame of the camera's feed of that size newer than the last one <paramref name="asker"/> got of it, waited
    /// for when it has had the newest; the feed starts when there is none. Null when the feed gives no frame now: it has
    /// no stream source, ffmpeg failed and the feed waits to start it again, it closed, HassSharp is unloading, or
    /// Home Assistant gave no way to ask (<see cref="PyInterop.BeginCameraSource"/>). The caller holds the image's
    /// reference to the frame.
    /// </summary>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    internal static async Task<SharpImage?> NextAsync(string asker, string entityId, (int Width, int Height)? size, CancellationToken cancellation)
    {
        if (PyInterop.BeginCameraSource == null) return null;
        return Join(FeedSource.Camera(entityId), size) is { } feed ? await feed.NextAsync(asker, cancellation) : null;
    }

    /// <summary>
    /// The feed of the source and size, started when there is none; null while feeds are closed (HassSharp unloading).
    /// A <paramref name="reader"/> (a <see cref="SharpVideo"/>'s) is counted among the feed's readers until it leaves.
    /// </summary>
    internal static CameraFeed? Join(FeedSource source, (int Width, int Height)? size, string? reader = null)
    {
        lock (gate)
        {
            if (closed) return null;
            if (!feeds.TryGetValue((source, size), out var feed))
            {
                feeds[(source, size)] = feed = new(source, size);
                feed.Start();
            }
            if (reader != null) feed.Join(reader);
            return feed;
        }
    }

    /// <summary>A reader leaves the feed of the source and size, which closes at once when no one else reads it.</summary>
    internal static void Leave(FeedSource source, (int Width, int Height)? size, string reader)
    {
        CameraFeed? feed;
        lock (gate) feeds.TryGetValue((source, size), out feed);
        if (feed?.Leave(reader) == true)
        {
            Logger.Debug($"The live feed of {feed.Source} closes: its last reader closed it");
            Retire(feed);
        }
    }

    /// <summary>The newest frame of the camera's open feed of that size while it gives frames, without waiting and without keeping the feed open; null otherwise.</summary>
    internal static SharpImage? Newest(string entityId, (int Width, int Height)? size) => Newest(FeedSource.Camera(entityId), size);

    /// <summary>The newest frame of the open feed of the source and size while it gives frames, or its last once a file has ended, without waiting and without keeping the feed open; null otherwise.</summary>
    internal static SharpImage? Newest(FeedSource source, (int Width, int Height)? size)
    {
        CameraFeed? feed;
        lock (gate) feeds.TryGetValue((source, size), out feed);
        return feed?.Newest();
    }

    /// <summary>Closes the feed and forgets it: idle, or failed.</summary>
    internal static void Retire(CameraFeed feed)
    {
        lock (gate)
            if (feeds.TryGetValue(feed.Key, out var found) && found == feed) feeds.Remove(feed.Key);
        feed.Close();
    }

    /// <summary>
    /// Closes every feed, killing its ffmpeg, and waits until each has been reaped (at most 5 s each); no feed starts until
    /// <see cref="Open"/>, so a frame asked for meanwhile is a picture taken. Empties the pool of frame buffers. Never throws.
    /// </summary>
    internal static async Task CloseAllAsync()
    {
        CameraFeed[] all;
        lock (gate)
        {
            closed = true;
            all = [.. feeds.Values];
            feeds.Clear();
        }
        foreach (var feed in all) feed.Close();
        try { await Task.WhenAll(all.Select(feed => feed.Ended)).WaitAsync(ReapLimit); }
        catch (Exception) { }
        PixelFrame.Drain();
    }

    /// <summary>Feeds may start again: HassSharp is set up.</summary>
    internal static void Open()
    {
        lock (gate) closed = false;
    }

    /// <summary>The open feeds; for tests.</summary>
    internal static CameraFeed[] All { get { lock (gate) return [.. feeds.Values]; } }
}

/// <summary>
/// One camera's or video's live feed, of one size (none: its own): one ffmpeg at a time reads the camera's stream source as
/// Home Assistant names it (<see cref="PyInterop.BeginCameraSource"/>), or the video as <see cref="VideoSources"/> resolves
/// it (with Home Assistant's ffmpeg and allowed folders, <see cref="PyInterop.BeginVideoSource"/>), and writes raw YUV
/// 4:2:0 frames, scaled to fit the size, which <see cref="Y4mReader"/> reads; the newest is kept, with a sequence number, and whoever waits for a newer one
/// is woken. When ffmpeg ends, gives nothing for a while, or cannot start, it starts again after a pause that grows with
/// each try in a row that gave no frame; after <see cref="CameraFeeds.Tries"/> of them, and when the camera has no stream
/// source or Home Assistant's ffmpeg is not set up, the feed rests for <see cref="CameraFeeds.RestAfter"/>, as it does for a
/// video that is refused (<see cref="Refusal"/>). A file that does not loop ends: its last frame stays (<see cref="Finished"/>). While it waits
/// or rests it gives no frame, and the camera's pictures are taken one by one instead. ffmpeg's messages go to the debug
/// log, each once a run, with the stream's address and any password left out; why the feed gives no frames is logged once
/// for each new reason, and once when it gives them again. Closing kills ffmpeg's process tree, which the feed's task
/// then reaps. Thread-safe.
/// </summary>
internal sealed partial class CameraFeed
{
    enum Phase { Starting, Streaming, Resting, Finished, Closed }

    // At most this many of ffmpeg's messages a run go to the debug log, each once, and this much of each.
    const int MessagesLogged = 50, MessageLength = 300;

    readonly Lock gate = new();
    readonly CancellationTokenSource closing = new();
    // The sequence number of the last frame each asker got.
    readonly Dictionary<string, long> delivered = new(StringComparer.Ordinal);
    // The SharpVideos reading the feed, under the gate.
    readonly HashSet<string> readers = new(StringComparer.Ordinal);
    readonly FailureLog failures = new();
    // Under the gate: the newest frame (the feed's own reference) and its number, what the feed does, the task waiters
    // wait on (completed and replaced on each change), how many wait, when a frame was last asked for, and the ffmpeg running.
    PixelFrame? newest;
    long sequence;
    Phase phase = Phase.Starting;
    TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int waiting;
    long asked = Stopwatch.GetTimestamp();
    Process? process;
    string? resting;
    bool refused;
    Timer? idle;
    Task ended = Task.CompletedTask;
    int started;

    internal CameraFeed(FeedSource source, (int Width, int Height)? size)
    {
        Source = source;
        Size = size;
    }

    internal FeedSource Source { get; }
    /// <summary>The camera's entity id; null for a video.</summary>
    internal string? EntityId => Source.IsCamera ? Source.Text : null;
    internal (int Width, int Height)? Size { get; }
    internal (FeedSource, (int Width, int Height)?) Key => (Source, Size);
    /// <summary>Completes when the feed's task has ended, its last ffmpeg reaped.</summary>
    internal Task Ended => ended;
    /// <summary>How many times ffmpeg was started; for tests.</summary>
    internal int Started => Volatile.Read(ref started);
    /// <summary>Why the feed rests or waits to start ffmpeg again; null while it starts or gives frames. For tests.</summary>
    internal string? Resting { get { lock (gate) return phase == Phase.Resting ? resting : null; } }
    /// <summary>Whether it closed; for tests.</summary>
    internal bool Closed { get { lock (gate) return phase == Phase.Closed; } }
    /// <summary>Why a video is refused while the feed rests for it (a file outside the folders allowed, or missing; no ffmpeg); null otherwise.</summary>
    internal string? Refusal { get { lock (gate) return phase == Phase.Resting && refused ? resting : null; } }
    /// <summary>Whether a file that does not loop has ended: no frame comes after its last, which stays.</summary>
    internal bool Finished { get { lock (gate) return phase == Phase.Finished; } }

    /// <summary>Counts a reader (a <see cref="SharpVideo"/>), under CameraFeeds' lock.</summary>
    internal void Join(string reader)
    {
        lock (gate) readers.Add(reader);
    }

    /// <summary>A reader leaves; whether the feed is to close, as no other reader or asker has had a frame of it.</summary>
    internal bool Leave(string reader)
    {
        lock (gate)
        {
            if (!readers.Remove(reader)) return false;
            delivered.Remove(reader);
            return readers.Count == 0 && delivered.Count == 0 && waiting == 0 && phase != Phase.Closed;
        }
    }

    internal void Start()
    {
        ended = Task.Run(RunAsync);
        var tick = CameraFeeds.IdleAfter / 4;
        tick = tick < TimeSpan.FromMilliseconds(10) ? TimeSpan.FromMilliseconds(10) : tick > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : tick;
        idle = new Timer(_ => CheckIdle(), null, tick, tick);
    }

    void CheckIdle()
    {
        lock (gate)
            if (phase == Phase.Closed || waiting > 0 || Stopwatch.GetElapsedTime(asked) < CameraFeeds.IdleAfter) return;
        Logger.Debug($"The live feed of {Source} closes: no frame was asked of it for {Seconds(CameraFeeds.IdleAfter)} s");
        CameraFeeds.Retire(this);
    }

    /// <summary>
    /// The next frame newer than the last one <paramref name="asker"/> got, waited for; null when the feed gives none now
    /// (see <see cref="CameraFeeds.NextAsync"/>), and once a file that ended (<see cref="Finished"/>) has given the asker its
    /// last frame. With <paramref name="waitOutRest"/>, a feed that waits to start ffmpeg again is waited for too, unless it
    /// rests because the video is refused (<see cref="Refusal"/>).
    /// </summary>
    internal async Task<SharpImage?> NextAsync(string asker, CancellationToken cancellation, bool waitOutRest = false)
    {
        while (true)
        {
            Task wait;
            lock (gate)
            {
                asked = Stopwatch.GetTimestamp();
                if (phase == Phase.Closed || phase == Phase.Resting && (!waitOutRest || refused)) return null;
                // The feed holds the newest under the gate, so a reference to it can be taken.
                if (phase is Phase.Streaming or Phase.Finished && newest != null && sequence > delivered.GetValueOrDefault(asker) && newest.TryAddReference())
                {
                    delivered[asker] = sequence;
                    return SharpImage.Of(newest);
                }
                if (phase == Phase.Finished) return null;
                wait = changed.Task;
                waiting++;
            }
            try { await wait.WaitAsync(cancellation); }
            finally
            {
                lock (gate)
                {
                    waiting--;
                    asked = Stopwatch.GetTimestamp();
                }
            }
        }
    }

    /// <summary>The newest frame while the feed gives frames, or a file's last once it ended; null otherwise.</summary>
    internal SharpImage? Newest()
    {
        lock (gate) return phase is Phase.Streaming or Phase.Finished && newest != null && newest.TryAddReference() ? SharpImage.Of(newest) : null;
    }

    /// <summary>Closes the feed: its ffmpeg is killed, its newest frame given back, and whoever waits gets none. Never throws.</summary>
    internal void Close()
    {
        Process? running;
        PixelFrame? last;
        lock (gate)
        {
            if (phase == Phase.Closed) return;
            phase = Phase.Closed;
            (running, last, newest) = (process, newest, null);
            delivered.Clear();
            Changed();
        }
        idle?.Dispose();
        closing.Cancel();
        Kill(running);
        last?.Release();
    }

    // Under the gate: wakes whoever waits.
    void Changed()
    {
        var was = changed;
        changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        was.TrySetResult();
    }

    void Publish(PixelFrame frame)
    {
        PixelFrame? old;
        lock (gate)
        {
            if (phase == Phase.Closed) old = frame;
            else
            {
                (old, newest, sequence, phase) = (newest, frame, CameraFeeds.NextSequence(), Phase.Streaming);
                Changed();
            }
        }
        old?.Release();
    }

    // Gives no frames until the feed starts again, for the reason given; the newest is given back, as it is no news.
    void Rest(string reason, bool refusal = false)
    {
        PixelFrame? old;
        lock (gate)
        {
            if (phase == Phase.Closed) return;
            (old, newest, phase, resting, refused) = (newest, null, Phase.Resting, reason, refusal);
            Changed();
        }
        old?.Release();
    }

    // A file that does not loop has ended: its last frame stays, and whoever waits is woken to take it.
    void Finish()
    {
        lock (gate)
        {
            if (phase == Phase.Closed) return;
            phase = Phase.Finished;
            Changed();
        }
    }

    async Task RunAsync()
    {
        var token = closing.Token;
        var tries = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                lock (gate)
                    if (phase == Phase.Resting) phase = Phase.Starting;
                var (source, missing, refusal) = Source.IsCamera ? await SourceAsync(token) : await VideoAsync(Source, token);
                if (source is not { } found)
                {
                    // Most cameras have none, HassSharp's own among them: not worth more than a debug line. A video refused is
                    // a mistake in a graph or in code: a warning, once for each reason.
                    if (failures.Fails(missing!))
                    {
                        if (refusal) Logger.Warn($"The live feed of {Source} gives no frames: {missing}");
                        else Logger.Debug($"{Source} gives no live feed: {missing}; its pictures are taken one by one");
                    }
                    Rest(missing!, refusal);
                    await Task.Delay(CameraFeeds.RestAfter, token);
                    continue;
                }
                var (gave, ended, reason) = await StreamAsync(found, token);
                token.ThrowIfCancellationRequested();
                // A file that does not loop has played to its end: its last frame stays, and ffmpeg is not started again.
                if (gave && ended && Source.IsFile && !Source.Loop)
                {
                    Logger.Debug($"The live feed of {Source} ended with the file's last frame");
                    Finish();
                    await Task.Delay(Timeout.Infinite, token);
                }
                tries = gave ? 0 : tries + 1;
                var delays = CameraFeeds.RestartDelays;
                var pause = tries >= CameraFeeds.Tries ? CameraFeeds.RestAfter : delays[Math.Min(tries, delays.Length - 1)];
                // A stream that ends after giving frames is restarted as a matter of course; one that gives none is logged.
                if (!gave && failures.Fails(reason))
                    Logger.Warn($"The live feed of {Source} gives no frames: {reason}; {(Source.IsCamera ? "its pictures are taken one by one, and " : "")}ffmpeg starts again in {Seconds(pause)} s");
                else Logger.Debug($"The live feed of {Source} starts ffmpeg again in {Seconds(pause)} s: {reason}");
                Rest(reason);
                await Task.Delay(pause, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception failure)
        {
            try { Logger.Error($"The live feed of {Source} failed: {failure}"); } catch { }
            CameraFeeds.Retire(this);
        }
    }

    /// <summary>What ffmpeg reads and runs with: its input, Home Assistant's ffmpeg, the protocols it may open (none: any), and whether it plays a file in real time and loops it.</summary>
    internal sealed record Input(string Url, string Binary, string? Protocols = null, bool RealTime = false, bool Loop = false);

    // The stream source and ffmpeg Home Assistant names; else why there is none.
    async Task<(Input? Source, string? Missing, bool Refused)> SourceAsync(CancellationToken token)
    {
        var begin = PyInterop.BeginCameraSource;
        if (begin == null) return (null, "Home Assistant names no stream sources here", false);
        var answer = new TaskCompletionSource<(string? Url, string? Binary)>(TaskCreationOptions.RunContinuationsAsynchronously);
        try { begin(Source.Text, (url, binary) => answer.TrySetResult((url, binary)), message => answer.TrySetException(new InvalidOperationException(message))); }
        catch (Exception error) { answer.TrySetException(error); }
        try
        {
            var (url, binary) = await answer.Task.WaitAsync(CameraFeeds.SourceLimit, token);
            if (string.IsNullOrEmpty(url)) return (null, "it has no stream source", false);
            if (string.IsNullOrEmpty(binary)) return (null, "Home Assistant's ffmpeg is not set up", false);
            return (new(url, binary), null, false);
        }
        catch (TimeoutException) { return (null, $"Home Assistant did not name its stream source within {Seconds(CameraFeeds.SourceLimit)} s", false); }
        catch (Exception error) when (error is not OperationCanceledException) { return (null, error.Message, false); }
    }

    /// <summary>
    /// What ffmpeg reads for a video, and with which ffmpeg: Home Assistant's, and its folders allowed, which a file's path
    /// must be in (<see cref="VideoSources.Resolve"/>); else why it is refused (true), or why Home Assistant did not answer.
    /// </summary>
    internal static async Task<(Input? Source, string? Missing, bool Refused)> VideoAsync(FeedSource video, CancellationToken token)
    {
        var begin = PyInterop.BeginVideoSource;
        if (begin == null) return (null, "videos cannot be read here: Home Assistant names no ffmpeg", true);
        var answer = new TaskCompletionSource<(string? Binary, string? Folders)>(TaskCreationOptions.RunContinuationsAsynchronously);
        try { begin((binary, folders) => answer.TrySetResult((binary, folders)), message => answer.TrySetException(new InvalidOperationException(message))); }
        catch (Exception error) { answer.TrySetException(error); }
        string? binary, folders;
        try { (binary, folders) = await answer.Task.WaitAsync(CameraFeeds.SourceLimit, token); }
        catch (TimeoutException) { return (null, $"Home Assistant did not name its ffmpeg within {Seconds(CameraFeeds.SourceLimit)} s", false); }
        catch (Exception error) when (error is not OperationCanceledException) { return (null, error.Message, true); }
        if (string.IsNullOrEmpty(binary)) return (null, "Home Assistant's ffmpeg integration is not set up", true);
        try
        {
            var (url, protocols) = VideoSources.Resolve(video.Text, VideoSources.Folders(folders));
            return (new(url, binary, protocols, video.IsFile && video.RealTime, video.IsFile && video.Loop), null, false);
        }
        catch (InvalidOperationException refused) { return (null, refused.Message, true); }
    }

    /// <summary>
    /// What ffmpeg is given to write the stream's frames to its output, raw, of full range, scaled to fit the size when
    /// there is one: at most its width and height (a side of 0 has no limit), the aspect ratio kept, never scaled up.
    /// With <paramref name="protocols"/>, ffmpeg opens no other protocol; with <paramref name="realTime"/>, it reads a file
    /// at its own frame rate (<c>-re</c>), with <paramref name="loop"/> again from its start whenever it ends; and with
    /// <paramref name="single"/>, it writes one frame and ends.
    /// </summary>
    internal static List<string> Arguments(string url, (int Width, int Height)? size, string? protocols = null, bool realTime = false, bool loop = false, bool single = false)
    {
        List<string> arguments = ["-hide_banner", "-loglevel", "error", "-nostdin"];
        if (protocols != null) arguments.AddRange(["-protocol_whitelist", protocols]);
        // Over TCP an RTSP stream loses no packets, and so no parts of frames.
        if (url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase)) arguments.AddRange(["-rtsp_transport", "tcp"]);
        if (realTime) arguments.Add("-re");
        if (loop) arguments.AddRange(["-stream_loop", "-1"]);
        // The box is the size, or the frame's own side where it is smaller or not limited; the frame is fitted in it.
        static string Side(string own, int most) => most > 0 ? $"'min({own},{most})'" : own;
        var scale = size is { } fit ? $"scale=w={Side("iw", fit.Width)}:h={Side("ih", fit.Height)}:force_original_aspect_ratio=decrease:out_range=full" : "scale=out_range=full";
        arguments.AddRange(["-i", url, "-an"]);
        if (single) arguments.AddRange(["-frames:v", "1"]);
        arguments.AddRange(["-vf", scale, "-f", "yuv4mpegpipe", "-pix_fmt", "yuv420p", "-"]);
        return arguments;
    }

    // Runs ffmpeg until it ends, stalls or the feed closes, publishing its frames: whether it gave any, and why it ended.
    // Whether it ended on its own, of the end of its input, rather than failing, stalling or being stopped.
    async Task<(bool Gave, bool Ended, string Reason)> StreamAsync(Input input, CancellationToken token)
    {
        var (url, binary) = (input.Url, input.Binary);
        Process ffmpeg;
        try { ffmpeg = Run(binary, Arguments(url, Size, input.Protocols, input.RealTime, input.Loop)); }
        catch (Exception error) { return (false, false, $"ffmpeg ({binary}) could not start: {error.Message}"); }
        Interlocked.Increment(ref started);
        lock (gate)
        {
            if (phase == Phase.Closed) token = new(canceled: true);
            else process = ffmpeg;
        }
        bool gave = false, ended = false;
        string reason;
        var messages = Messages(ffmpeg, url, Source.ToString());
        try
        {
            try { ffmpeg.StandardInput.Close(); } catch (Exception) { }
            token.ThrowIfCancellationRequested();
            var reader = new Y4mReader(ffmpeg.StandardOutput.BaseStream);
            while (true)
            {
                var limit = gave ? CameraFeeds.StallLimit : CameraFeeds.FirstFrameLimit;
                using var timer = CancellationTokenSource.CreateLinkedTokenSource(token);
                // A read that does not see the cancellation ends as the process does.
                using var stop = timer.Token.Register(() => Kill(ffmpeg));
                timer.CancelAfter(limit);
                PixelFrame? frame;
                try { frame = await reader.NextAsync(timer.Token); }
                // Past the limit the read is cancelled, or fails as the process is killed under it: no frame came.
                catch (Exception) when (timer.IsCancellationRequested && !token.IsCancellationRequested) { frame = null; }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    reason = $"its frames could not be read: {error.Message}";
                    break;
                }
                token.ThrowIfCancellationRequested();
                if (frame == null)
                {
                    reason = timer.IsCancellationRequested ? $"ffmpeg gave no frame for {Seconds(limit)} s" : "ffmpeg ended";
                    // Its output ended: it ended of the end of its input when it exits without a failure soon.
                    if (!timer.IsCancellationRequested)
                    {
                        try { await ffmpeg.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(2), token); } catch (TimeoutException) { }
                        ended = ffmpeg.HasExited && ffmpeg.ExitCode == 0;
                    }
                    break;
                }
                if (!gave && failures.Works()) Logger.Info($"The live feed of {Source} gives frames again");
                gave = true;
                Publish(frame);
            }
        }
        finally
        {
            lock (gate)
                if (process == ffmpeg) process = null;
            await EndAsync(ffmpeg);
        }
        // What ffmpeg said last says why it ended.
        if (await messages.WaitAsync(TimeSpan.FromSeconds(1)).ContinueWith(read => read.IsCompletedSuccessfully ? read.Result : null, TaskScheduler.Default) is { } said)
            reason += ": " + said;
        return (gave, ended, reason);
    }

    /// <summary>How long a short run of ffmpeg has for the one frame of a video it is to read (<see cref="GrabAsync"/>). Settable for tests.</summary>
    internal static TimeSpan GrabLimit { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// One frame of a video, read by a short run of ffmpeg (<c>-frames:v 1</c>, neither in real time nor looped), as a
    /// Video frame node takes one where no feed of it is open; ffmpeg is killed once it gave it, after
    /// <see cref="GrabLimit"/>, or when <paramref name="cancellation"/> is cancelled. The caller holds the frame's reference.
    /// </summary>
    /// <exception cref="InvalidOperationException">The video is refused, or gave no frame; the message says why, without its address's password.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellation"/> was cancelled.</exception>
    internal static async Task<SharpImage> GrabAsync(FeedSource video, (int Width, int Height)? size, CancellationToken cancellation)
    {
        var (input, missing, _) = await VideoAsync(video, cancellation);
        if (input == null) throw new InvalidOperationException($"The video {video} gives no frame: {missing}");
        Process ffmpeg;
        try { ffmpeg = Run(input.Binary, Arguments(input.Url, size, input.Protocols, single: true)); }
        catch (Exception error) { throw new InvalidOperationException($"The video {video} gives no frame: ffmpeg ({input.Binary}) could not start: {error.Message}"); }
        var messages = Messages(ffmpeg, input.Url, null);
        PixelFrame? frame = null;
        var late = false;
        try
        {
            try { ffmpeg.StandardInput.Close(); } catch (Exception) { }
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            using var stop = timer.Token.Register(() => Kill(ffmpeg));
            timer.CancelAfter(GrabLimit);
            try { frame = await new Y4mReader(ffmpeg.StandardOutput.BaseStream).NextAsync(timer.Token); }
            catch (Exception) when (timer.IsCancellationRequested && !cancellation.IsCancellationRequested) { late = true; }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw new InvalidOperationException($"The video {video} gives no frame: its frames could not be read: {error.Message}");
            }
            cancellation.ThrowIfCancellationRequested();
        }
        finally
        {
            await EndAsync(ffmpeg);
        }
        if (frame != null) return SharpImage.Of(frame);
        var said = await messages.WaitAsync(TimeSpan.FromSeconds(1)).ContinueWith(read => read.IsCompletedSuccessfully ? read.Result : null, TaskScheduler.Default);
        throw new InvalidOperationException($"The video {video} gives no frame: " + (late ? $"ffmpeg gave none within {Seconds(GrabLimit)} s" : "ffmpeg ended") + (said != null ? ": " + said : ""));
    }

    static Process Run(string binary, List<string> arguments)
    {
        var start = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("it did not start");
    }

    // Reads ffmpeg's messages until it ends, into the debug log each once (for the feed of that name; none for no name); the last one.
    static async Task<string?> Messages(Process ffmpeg, string url, string? feed)
    {
        string? last = null;
        var logged = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            while (await ffmpeg.StandardError.ReadLineAsync() is { } line)
            {
                var message = Redacted(line, url).Trim();
                if (message.Length == 0) continue;
                if (message.Length > MessageLength) message = message[..MessageLength];
                last = message;
                if (feed != null && logged.Count < MessagesLogged && logged.Add(message)) Logger.Debug($"ffmpeg of the live feed of {feed}: {message}");
            }
        }
        catch (Exception) { }
        return last;
    }

    /// <summary>A message of ffmpeg's without the stream's address, which may hold a password, nor any other address's password.</summary>
    internal static string Redacted(string message, string url) =>
        Credentials().Replace(url.Length > 0 ? message.Replace(url, "<stream source>", StringComparison.Ordinal) : message, "://***@");

    [GeneratedRegex(@"://[^/@\s]+@")]
    private static partial Regex Credentials();

    // Kills ffmpeg and what it started; never throws (it may have ended, or been disposed).
    static void Kill(Process? ffmpeg)
    {
        if (ffmpeg == null) return;
        try { ffmpeg.Kill(entireProcessTree: true); }
        catch (Exception) { }
    }

    // Kills ffmpeg, waits until it is reaped (at most 5 s), and lets go of it.
    static async Task EndAsync(Process ffmpeg)
    {
        Kill(ffmpeg);
        try { await ffmpeg.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) { }
        ffmpeg.Dispose();
    }

    static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
