using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HassSharp;

/// <summary>
/// The files an entity definition depends on, such as a model or a firmware table, downloaded once and kept under
/// <c>.storage/hass_sharp/files/</c> by their SHA-256 checksum: a file is only ever at its path once its checksum matched.
/// Reached through <see cref="SharpComponent.Files"/>; its downloads end when the definition stops.
/// </summary>
public sealed class SharpFiles
{
    /// <summary>How long a download may receive nothing, waiting for the server's answer or for more of the file; settable for tests.</summary>
    internal static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>The largest file a download takes; settable for tests.</summary>
    internal static long SizeLimit { get; set; } = 2L * 1024 * 1024 * 1024;

    // What a download writes before its checksum matched, in the checksum's directory; a name never starts with a dot.
    const string Partial = ".partial";
    // One download of each checksum's directory at a time, across the process, with how many wait for it or hold it.
    static readonly Dictionary<string, (SemaphoreSlim Gate, int Users)> downloading = new(StringComparer.Ordinal);

    readonly string root;
    readonly Action<string> log;
    readonly Func<IDisposable> receiving;
    // Cancelled when the definition stops: every download in progress ends, and none starts after.
    readonly CancellationTokenSource closing = new();

    /// <param name="root">The directory the checksums' directories are in.</param>
    /// <param name="log">Writes an info line to the definition's log.</param>
    /// <param name="receiving">Marks the time a download receives, which the setup limit does not count, until disposed.</param>
    internal SharpFiles(string root, Action<string> log, Func<IDisposable> receiving)
    {
        this.root = root;
        this.log = log;
        this.receiving = receiving;
    }

    /// <summary>
    /// Downloads a file once and returns its path, <c>.storage/hass_sharp/files/&lt;sha256&gt;/&lt;name&gt;</c>: a file already
    /// there is returned at once, without a request. Otherwise it is written to a temporary file beside it, hashed as it
    /// is written, and put in place only when its checksum matches; a download that fails leaves nothing behind. While
    /// one download of a checksum runs, any other of it, from any definition, waits for it and then finds the file. The
    /// time a download receives does not count against the 60 seconds <c>Setup()</c> has; one that receives nothing for
    /// 60 seconds fails. Pass <see cref="SharpComponent.Stopping"/> as <paramref name="cancellationToken"/>; a download
    /// still in progress when the definition stops is cancelled anyway.
    /// </summary>
    /// <param name="url">Where the file is, an <c>http</c> or <c>https</c> address.</param>
    /// <param name="sha256">The SHA-256 checksum of the file as the server sends it (nothing is decompressed, also when the
    /// server labels it <c>Content-Encoding: gzip</c>), as <c>sha256sum</c> gives it for a file fetched with
    /// <c>curl -O</c>: 64 hexadecimal characters, in either case.</param>
    /// <param name="name">What the file is called on disk, a plain file name; the address's last path segment when null, else <c>file</c>.</param>
    /// <param name="cancellationToken">Ends the download.</param>
    /// <returns>The file's path.</returns>
    /// <exception cref="ArgumentException">The address, the checksum or the name is not one; nothing was requested. Its message is a plain sentence, naming no parameter.</exception>
    /// <exception cref="InvalidDataException">The file does not match its checksum, or is larger than 2 GiB.</exception>
    /// <exception cref="HttpRequestException">The server answered with an error status, or the download could not be made or broke off.</exception>
    /// <exception cref="TimeoutException">The server sent nothing for 60 seconds.</exception>
    /// <exception cref="ObjectDisposedException">The definition has stopped.</exception>
    public async Task<string> Download(string url, string sha256, string? name = null, CancellationToken cancellationToken = default)
    {
        if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("The address of a download must be an http or https URL");
        if (sha256 is not { Length: 64 } || !sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("The checksum of a download must be its SHA-256: 64 hexadecimal characters");
        if (name != null && !Plain(name))
            throw new ArgumentException($"'{name}' is not a plain file name: a download's name has no directory, does not start with a dot, has no control characters and takes at most 255 bytes in UTF-8");
        if (closing.IsCancellationRequested) throw new ObjectDisposedException(nameof(SharpFiles), "The definition has stopped: it downloads nothing more");
        var checksum = sha256.ToLowerInvariant();
        // A name the address gives is never logged: the path may carry a token, as the query may.
        var shown = name ?? "a file";
        name ??= DefaultName(uri);
        var directory = Path.GetFullPath(Path.Combine(root, checksum));
        var path = Path.Combine(directory, name);
        using var ending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closing.Token);
        Directory.CreateDirectory(directory);
        using (await Exclusive(directory, ending.Token))
        {
            if (File.Exists(path)) return path;
            RemoveLeftovers(directory);
            await Fetch(uri, checksum, shown, directory, path, ending.Token);
            return path;
        }
    }

    /// <summary>Ends every download in progress and refuses any later one; the definition has stopped.</summary>
    internal void Close() => closing.Cancel();

    // Logs the download as <paramref name="shown"/>: its name when the definition gave one, else "a file".
    async Task Fetch(Uri uri, string checksum, string shown, string directory, string path, CancellationToken ending)
    {
        // Only the host is ever named: the address may carry a token in its path or query.
        var host = uri.Host;
        var temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + Partial);
        var clock = Stopwatch.StartNew();
        long received = 0;
        var done = false;
        log($"Downloading {shown} from {host}");
        try
        {
            // Bounded by the stall limit, not by a whole-request timeout: a large file takes as long as it takes.
            // One that decompresses nothing: the checksum is of the bytes the server sends.
            using var client = SharpHttp.CreateForDownloads();
            client.Timeout = Timeout.InfiniteTimeSpan;
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ending);
            stall.CancelAfter(StallTimeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await Network(() => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token));
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode} from {host}", null, response.StatusCode);
                if (response.Content.Headers.ContentLength > SizeLimit) throw TooLarge(host);
                // From the answer on the download receives; the wait for it counts against the setup limit.
                using (receiving())
                {
                    await using var body = await Network(() => response.Content.ReadAsStreamAsync(stall.Token));
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    await using (var file = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous }))
                    {
                        var buffer = new byte[81920];
                        int read;
                        while ((read = await Network(() => body.ReadAsync(buffer, stall.Token).AsTask())) > 0)
                        {
                            stall.CancelAfter(StallTimeout);
                            received += read;
                            if (received > SizeLimit) throw TooLarge(host);
                            hash.AppendData(buffer, 0, read);
                            await file.WriteAsync(buffer.AsMemory(0, read), stall.Token);
                        }
                        // On disk before it is renamed into place, so a crash never leaves a short file at the path.
                        await file.FlushAsync(stall.Token);
                        file.Flush(flushToDisk: true);
                    }
                    if (!Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(checksum, StringComparison.Ordinal))
                        throw new InvalidDataException($"The file from {host} does not match its checksum");
                }
            }
            catch (OperationCanceledException) when (stall.IsCancellationRequested && !ending.IsCancellationRequested)
            {
                throw new TimeoutException($"The file from {host} sent nothing for {Seconds(StallTimeout)} s");
            }
            File.Move(temporary, path, overwrite: true);
            done = true;
            log($"Downloaded {shown} from {host}: {Size(received)} in {Seconds(clock.Elapsed)} s");
        }
        finally
        {
            if (!done)
            {
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                log($"Downloading {shown} from {host} failed after {Size(received)} in {Seconds(clock.Elapsed)} s");
            }
        }

        // A failure of the connection (refused, reset, ended early, no such host) with a message of its own, which names
        // no address.
        async Task<T> Network<T>(Func<Task<T>> receive)
        {
            try { return await receive(); }
            catch (Exception failure) when (failure is HttpRequestException or IOException)
            {
                throw new HttpRequestException($"The file from {host} could not be downloaded: {failure.GetBaseException().Message}", failure);
            }
        }
    }

    InvalidDataException TooLarge(string host) => new($"The file from {host} is larger than {Size(SizeLimit)}");

    // What a download that did not finish left in the checksum's directory, as when the process ended during it.
    static void RemoveLeftovers(string directory)
    {
        foreach (var leftover in Directory.EnumerateFiles(directory, "." + "*" + Partial))
            try { File.Delete(leftover); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // Waits for the checksum's directory to be free of other downloads; the wait counts as receiving, since the one it
    // waits for receives for it.
    async Task<IDisposable> Exclusive(string directory, CancellationToken token)
    {
        SemaphoreSlim gate;
        lock (downloading)
        {
            var (found, users) = downloading.TryGetValue(directory, out var entry) ? entry : (new SemaphoreSlim(1, 1), 0);
            downloading[directory] = (found, users + 1);
            gate = found;
        }
        try
        {
            var entered = gate.WaitAsync(token);
            if (entered.IsCompleted) await entered;
            else using (receiving()) await entered;
        }
        catch
        {
            Leave(directory, entered: false);
            throw;
        }
        return new Release(directory);
    }

    static void Leave(string directory, bool entered)
    {
        lock (downloading)
        {
            var (gate, users) = downloading[directory];
            if (entered) gate.Release();
            if (users == 1) downloading.Remove(directory);
            else downloading[directory] = (gate, users - 1);
        }
    }

    sealed class Release(string directory) : IDisposable
    {
        int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) Leave(directory, entered: true); }
    }

    // A file name without a directory: not empty, no separator, no control character, not hidden (so never a
    // temporary file's), no surrounding spaces, and at most the 255 bytes a Linux file name takes.
    static bool Plain(string name) =>
        name.Length > 0 && Encoding.UTF8.GetByteCount(name) <= 255 && name[0] != '.' && name.Trim() == name
        && name.IndexOfAny(['/', '\\', ':']) < 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.Any(char.IsControl);

    // The address's last path segment, as a name; "file" when it has none that is one.
    static string DefaultName(Uri uri)
    {
        var last = uri.AbsolutePath.Split('/')[^1];
        string segment;
        try { segment = Uri.UnescapeDataString(last); } catch (UriFormatException) { return "file"; }
        return Plain(segment) ? segment : "file";
    }

    static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);

    // A size as a log line or a message says it: in bytes, KiB, MiB or GiB.
    static string Size(long bytes)
    {
        if (bytes < 1024) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        string[] units = ["KiB", "MiB", "GiB"];
        var value = bytes / 1024.0;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value.ToString("0.#", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}
