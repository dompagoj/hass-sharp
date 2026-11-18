namespace HassSharp;

/// <summary>
/// Pictures of Home Assistant's cameras, as <c>camera.async_get_image</c> gives them (a JPEG, within 10 seconds): the one way
/// a definition's code and a graph's step take one. Python refuses a picture of a camera of the asking definition itself,
/// which would wait for itself.
/// </summary>
static class HassCameras
{
    /// <summary>A picture of the camera, as it is now.</summary>
    /// <param name="definitionId">The id of the definition asking; empty for none.</param>
    /// <param name="entityId">The camera, such as <c>camera.front_door</c>.</param>
    /// <param name="cancellation">Ends the wait; the picture Home Assistant still sends is then dropped.</param>
    /// <exception cref="InvalidOperationException">Home Assistant gave no picture (its message says why), or pictures cannot be taken here.</exception>
    /// <exception cref="ArgumentException">What came is not a JPEG of at most 20 MiB.</exception>
    internal static async Task<SharpImage> SnapshotAsync(string definitionId, string entityId, CancellationToken cancellation)
    {
        var begin = PyInterop.BeginCameraSnapshot ?? throw new InvalidOperationException("Pictures from cameras are not available here");
        cancellation.ThrowIfCancellationRequested();
        // Python answers on its event loop: what follows runs elsewhere.
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        try { begin(definitionId, entityId, bytes => completion.TrySetResult(bytes), message => completion.TrySetException(StoppingAnswers.Contains(message) ? new Stopped(message) : new InvalidOperationException(message))); }
        catch (Exception error) { completion.TrySetException(error); }
        var picture = await completion.Task.WaitAsync(cancellation);
        if (picture is not { Length: > 0 }) throw new InvalidOperationException($"{entityId} gave no picture");
        return SharpImage.Load(picture);
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
