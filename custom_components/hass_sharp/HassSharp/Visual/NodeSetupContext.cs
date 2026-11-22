using System.Runtime.InteropServices;

namespace HassSharp;

/// <summary>
/// What a <see cref="NodeSetupAttribute"/> method reaches of the entity definition it sets up: the definition's files,
/// its log, the runtime it runs on, its stopping and the objects to dispose when it stops.
/// </summary>
public sealed class NodeSetupContext
{
    readonly Action<string, PyLogLevel> log;
    readonly Action<IDisposable> own;

    /// <param name="files">The definition's files.</param>
    /// <param name="log">Writes a line of the level to the definition's log.</param>
    /// <param name="stopping">Cancelled when the definition stops.</param>
    /// <param name="own">Keeps an object to dispose when the definition stops.</param>
    /// <param name="runtimeIdentifier">The runtime identifier to give; the process's when null.</param>
    internal NodeSetupContext(SharpFiles files, Action<string, PyLogLevel> log, CancellationToken stopping, Action<IDisposable> own, string? runtimeIdentifier = null)
    {
        Files = files;
        this.log = log;
        Stopping = stopping;
        this.own = own;
        RuntimeIdentifier = runtimeIdentifier ?? RuntimeInformation.RuntimeIdentifier;
    }

    /// <summary>The definition's files: <see cref="SharpFiles.Download"/> fetches a file once and keeps it by its checksum.</summary>
    public SharpFiles Files { get; }

    /// <summary>Writes a line to the definition's log, under its name.</summary>
    public void Log(string message, PyLogLevel level = PyLogLevel.Info) => log(message, level);

    /// <summary>The runtime identifier of the process, such as <c>linux-musl-arm64</c>: which native library to load.</summary>
    public string RuntimeIdentifier { get; }

    /// <summary>Cancelled when the definition stops, also while its setup still runs.</summary>
    public CancellationToken Stopping { get; }

    /// <summary>
    /// Disposes <paramref name="disposable"/> when the definition stops, last owned first, and returns it. Once the
    /// definition has stopped, disposes it at once and throws <see cref="ObjectDisposedException"/>.
    /// </summary>
    /// <remarks>
    /// Stopping waits at most 10 s in all for the start, the nodes and frames still running and the <c>Stop()</c>s, and
    /// disposes what is owned also when they have not finished by then, so <c>Dispose()</c> can run while one of them
    /// still uses the object. Make <c>Dispose()</c> wait for a call in progress and a later call fail, as
    /// hass-sharp-vision's detector does: each call and <c>Dispose()</c> take one lock, and a call after it throws
    /// <see cref="ObjectDisposedException"/>.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The definition has stopped: it keeps nothing more.</exception>
    public T Own<T>(T disposable) where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(disposable);
        own(disposable);
        return disposable;
    }
}
