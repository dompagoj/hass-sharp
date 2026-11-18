using System.Net;

namespace HassSharp;

/// <summary>
/// The one HTTP connection pool of the process, and the clients made over it: each entity definition gets its own client
/// (<see cref="SharpComponent.Http"/>), which its host disposes once it stopped, while the connections stay pooled here.
/// The handler holds nothing of a definition's code (no callback, no delegate of one), so it never keeps a definition's
/// load context alive; certificates are always checked.
/// </summary>
internal static class SharpHttp
{
    /// <summary>
    /// What every client sends through: at most 5 redirects, every decompression, 10 s to connect, and connections renewed
    /// every 2 minutes, so a device that changes its address is found again.
    /// </summary>
    internal static SocketsHttpHandler Handler { get; } = new()
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };

    /// <summary>A new client over <see cref="Handler"/>, which disposing it leaves open: 30 s per request, <c>User-Agent: HassSharp</c>.</summary>
    internal static HttpClient Create()
    {
        var client = new HttpClient(Handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HassSharp");
        return client;
    }
}
