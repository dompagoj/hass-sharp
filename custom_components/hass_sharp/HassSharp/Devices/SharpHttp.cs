using System.Net;

namespace HassSharp;

/// <summary>
/// The HTTP connection pools of the process, and the clients made over them: each entity definition gets its own client
/// (<see cref="SharpComponent.Http"/>), which its host disposes once it stopped, while the connections stay pooled here;
/// downloads (<see cref="SharpFiles"/>) have a pool of their own that decompresses nothing. The handlers hold nothing of a
/// definition's code (no callback, no delegate of one), so they never keep a definition's load context alive;
/// certificates are always checked.
/// </summary>
internal static class SharpHttp
{
    /// <summary>
    /// What every client sends through: at most 5 redirects, every decompression, 10 s to connect, and connections renewed
    /// every 2 minutes, so a device that changes its address is found again.
    /// </summary>
    internal static SocketsHttpHandler Handler { get; } = Pool(DecompressionMethods.All);

    /// <summary>
    /// What downloads send through: as <see cref="Handler"/>, but decompressing nothing, so that a file's checksum is of
    /// the bytes the server sends (a stored <c>.gz</c> it labels <c>Content-Encoding: gzip</c> stays as it is).
    /// </summary>
    internal static SocketsHttpHandler DownloadHandler { get; } = Pool(DecompressionMethods.None);

    static SocketsHttpHandler Pool(DecompressionMethods decompression) => new()
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        AutomaticDecompression = decompression,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };

    /// <summary>A new client over <see cref="Handler"/>, which disposing it leaves open: 30 s per request, <c>User-Agent: HassSharp</c>.</summary>
    internal static HttpClient Create() => Client(Handler);

    /// <summary>A new client over <see cref="DownloadHandler"/>, as <see cref="Create"/> makes one.</summary>
    internal static HttpClient CreateForDownloads() => Client(DownloadHandler);

    static HttpClient Client(SocketsHttpHandler handler)
    {
        var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HassSharp");
        return client;
    }
}
