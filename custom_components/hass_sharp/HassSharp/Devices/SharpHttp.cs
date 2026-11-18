using System.Net;

namespace HassSharp;

/// <summary>
/// The HTTP connection pools of the process, and the clients made over them: each entity definition gets its own client
/// (<see cref="SharpComponent.Http"/>), which its host disposes once it stopped, while the connections stay pooled here;
/// downloads (<see cref="SharpFiles"/>) have a pool of their own that decompresses nothing, and HTTP request steps one
/// that follows no redirect. The handlers hold nothing of a
/// definition's code (no callback, no delegate of one), so they never keep a definition's load context alive;
/// certificates are always checked.
/// </summary>
internal static class SharpHttp
{
    static TimeSpan connectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a connection may take to open, for every pool: 10 s. Settable for tests, which builds the pools anew (a
    /// pool in use takes no new setting); clients made before keep the pool they were made over.
    /// </summary>
    internal static TimeSpan ConnectTimeout
    {
        get => connectTimeout;
        set { connectTimeout = value; Build(); }
    }

    /// <summary>
    /// What every client sends through: at most 5 redirects, every decompression, <see cref="ConnectTimeout"/> to connect,
    /// connections renewed every 2 minutes, so a device that changes its address is found again, and no cookies kept.
    /// </summary>
    internal static SocketsHttpHandler Handler { get; private set; } = null!;

    /// <summary>
    /// What downloads send through: as <see cref="Handler"/>, but decompressing nothing, so that a file's checksum is of
    /// the bytes the server sends (a stored <c>.gz</c> it labels <c>Content-Encoding: gzip</c> stays as it is).
    /// </summary>
    internal static SocketsHttpHandler DownloadHandler { get; private set; } = null!;

    /// <summary>
    /// What an HTTP request step sends through: as <see cref="Handler"/>, but following no redirect, which the step does
    /// itself (.NET would hand its headers to another host).
    /// </summary>
    internal static SocketsHttpHandler RequestHandler { get; private set; } = null!;

    static SharpHttp() => Build();

    static void Build()
    {
        Handler = Pool(DecompressionMethods.All, redirects: true);
        DownloadHandler = Pool(DecompressionMethods.None, redirects: true);
        RequestHandler = Pool(DecompressionMethods.All, redirects: false);
    }

    static SocketsHttpHandler Pool(DecompressionMethods decompression, bool redirects) => new()
    {
        AllowAutoRedirect = redirects,
        MaxAutomaticRedirections = 5,
        AutomaticDecompression = decompression,
        ConnectTimeout = connectTimeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        // One jar for the whole process would hand a cookie one definition's device or account set to every other
        // definition's requests to that host, and keep it past the definition: nothing keeps cookies, and a definition
        // that needs a session sends the Cookie header itself.
        UseCookies = false,
    };

    /// <summary>A new client over <see cref="Handler"/>, which disposing it leaves open: 30 s per request, <c>User-Agent: HassSharp</c>.</summary>
    internal static HttpClient Create() => Client(Handler);

    /// <summary>A new client over <see cref="DownloadHandler"/>, as <see cref="Create"/> makes one.</summary>
    internal static HttpClient CreateForDownloads() => Client(DownloadHandler);

    /// <summary>A new client over <see cref="RequestHandler"/>, as <see cref="Create"/> makes one.</summary>
    internal static HttpClient CreateForRequests() => Client(RequestHandler);

    static HttpClient Client(SocketsHttpHandler handler)
    {
        var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HassSharp");
        return client;
    }
}
