using System.Text.Json;

namespace HassSharp;

/// <summary>
/// What a feed reads (<see cref="CameraFeed"/>): one of Home Assistant's cameras, by its entity id, whose stream source
/// Home Assistant names; or a video, by its address or a file's path, as a Video frame node or <see cref="SharpVideo"/>
/// gives it. <paramref name="Loop"/> and <paramref name="RealTime"/> apply to a file only (ffmpeg's <c>-stream_loop -1</c>
/// and <c>-re</c>), and are false for anything else, so that a feed's key does not split on them.
/// </summary>
internal sealed record FeedSource(string Text, bool IsCamera, bool Loop = false, bool RealTime = false)
{
    /// <summary>A Home Assistant camera, by its entity id.</summary>
    internal static FeedSource Camera(string entityId) => new(entityId, true);

    /// <summary>A video, checked as <see cref="VideoSources.Check"/> checks it; whether a file loops and plays in real time.</summary>
    /// <exception cref="ArgumentException">It is not a video's address or a file's absolute path.</exception>
    internal static FeedSource Video(string source, bool loop, bool realTime) =>
        VideoSources.Check(source) ? new(source, false, loop, realTime) : new(source, false);

    /// <summary>Whether it is a file's path.</summary>
    internal bool IsFile => !IsCamera && Text.StartsWith('/');

    /// <summary>How the log names it: a camera's entity id; a video's address without its user, password, path or query (which may hold a token), or a file's path.</summary>
    public override string ToString() => IsCamera ? Text : VideoSources.Shown(Text);
}

/// <summary>
/// The videos Video frame nodes and <see cref="SharpVideo"/> read, as ffmpeg is given them. Only these reach ffmpeg: an
/// http, https, rtsp, rtsps or rtmp address with a host and no spaces, read with ffmpeg's protocols for that scheme alone
/// (<c>-protocol_whitelist</c>); and a file's absolute path, read with the file protocol alone, once its symbolic links and
/// <c>..</c> are resolved, and only inside a folder Home Assistant allows (<c>allowlist_external_dirs</c> and
/// <c>media_dirs</c>, which Python gives, <see cref="PyInterop.BeginVideoSource"/>). The resolved path is what ffmpeg
/// reads, so a link changed after the check changes nothing. None of ffmpeg's other inputs is taken (<c>concat:</c>,
/// <c>pipe:</c>, <c>file:</c>, <c>data:</c>, <c>subfile</c>, a device, lavfi, a relative path), as the graph validators
/// refuse them too (<see cref="VisualGraphValidator.Words.VideoSourcePattern"/>, mirrored in the editor).
/// </summary>
internal static class VideoSources
{
    // At most this many symbolic links are followed in one path, as Linux follows at most 40.
    const int MostLinks = 40;

    /// <summary>Whether the source is a file's path (true) or an address (false).</summary>
    /// <exception cref="ArgumentException">It is neither, in the validators' words.</exception>
    internal static bool Check(string source)
    {
        if (source == null || VisualGraphValidator.Words.Trimmed(source).Length == 0) throw new ArgumentException("A video needs its source: an address such as rtsp://camera.local/stream, or a file's absolute path such as /media/clip.mp4", nameof(source));
        if (!VisualTypes.Matches(source, VisualGraphValidator.Words.VideoSourcePattern)) throw new ArgumentException(VisualGraphValidator.Words.VideoSource("A video's source"), nameof(source));
        return source.StartsWith('/');
    }

    /// <summary>The scheme of an address, lower case; <c>file</c> for a path.</summary>
    internal static string Scheme(string source) => source.StartsWith('/') ? "file" : source[..source.IndexOf("://", StringComparison.Ordinal)].ToLowerInvariant();

    /// <summary>The protocols ffmpeg may open for a source of that scheme, and no other.</summary>
    internal static string Protocols(string scheme) => scheme switch
    {
        "file" => "file",
        "http" or "https" => "http,https,tcp,tls",
        "rtsp" or "rtsps" => "rtsp,rtp,udp,tcp,tls",
        "rtmp" => "rtmp,tcp",
        _ => throw new ArgumentException($"{scheme} is not a scheme a video is read with"),
    };

    /// <summary>An address without its user and password, path and query; a file's path as it is.</summary>
    internal static string Shown(string source)
    {
        if (source.StartsWith('/')) return source;
        var separator = source.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0) return "<video>";
        var rest = source[(separator + 3)..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        var authority = end < 0 ? rest : rest[..end];
        var at = authority.LastIndexOf('@');
        return source[..separator].ToLowerInvariant() + "://" + (at < 0 ? authority : authority[(at + 1)..]);
    }

    /// <summary>
    /// What ffmpeg reads for a video and the protocols it may open: an address as it is; a file's path resolved (its links
    /// and <c>..</c>), once it is a file inside one of <paramref name="allowed"/> (each resolved too).
    /// </summary>
    /// <exception cref="InvalidOperationException">The file does not exist, is a folder, or is in no folder allowed; the message, of no subject (it follows the video's name), says how to allow it.</exception>
    internal static (string Input, string Protocols) Resolve(string source, IEnumerable<string> allowed)
    {
        var scheme = Scheme(source);
        if (scheme != "file") return (source, Protocols(scheme));
        var real = RealPath(source) ?? throw new InvalidOperationException("the file does not exist");
        if (Directory.Exists(real)) throw new InvalidOperationException("it is a folder, not a file");
        foreach (var folder in allowed)
            if (folder.StartsWith('/') && RealPath(folder) is { } inside && Within(real, inside)) return (real, Protocols(scheme));
        throw new InvalidOperationException("the file is in no folder Home Assistant allows: add its folder to allowlist_external_dirs (or media_dirs) in configuration.yaml");
    }

    /// <summary>The folders a JSON array of Python's names; none for anything else.</summary>
    internal static string[] Folders(string? json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json ?? "[]")?.Where(f => !string.IsNullOrEmpty(f)).ToArray() ?? []; }
        catch (JsonException) { return []; }
    }

    static bool Within(string path, string folder) =>
        folder == "/" || path == folder || path.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal);

    /// <summary>
    /// An absolute path with each symbolic link followed and each <c>.</c> and <c>..</c> gone, as <c>realpath(3)</c> makes it:
    /// a <c>..</c> after a link goes up from where the link leads. Null when a part does not exist, or links loop.
    /// </summary>
    internal static string? RealPath(string path)
    {
        if (!path.StartsWith('/')) return null;
        var pending = new Stack<string>(path.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse());
        var current = "/";
        var links = 0;
        while (pending.TryPop(out var part))
        {
            if (part == ".") continue;
            if (part == "..") { current = Path.GetDirectoryName(current) ?? "/"; continue; }
            var next = current == "/" ? "/" + part : current + "/" + part;
            string? target;
            try { target = new FileInfo(next).LinkTarget; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
            if (target == null)
            {
                if (!File.Exists(next) && !Directory.Exists(next)) return null;
                current = next;
                continue;
            }
            if (++links > MostLinks) return null;
            foreach (var piece in target.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse()) pending.Push(piece);
            if (target.StartsWith('/')) current = "/";
        }
        return current;
    }
}
