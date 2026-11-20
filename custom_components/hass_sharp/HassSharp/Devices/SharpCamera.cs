using System.Reflection;
using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>
/// A camera: a picture Home Assistant shows, such as another camera's picture with what was seen drawn on it. It has a
/// current frame, which <see cref="Show"/> sets whenever there is a new picture, from any thread. Or it says how a frame
/// is made when one is needed, by overriding <see cref="Frame"/> or, for a camera inside a device, by setting
/// <see cref="OnFrame"/>: then nothing runs while nobody watches. While someone watches, Home Assistant asks for a frame
/// every <see cref="FrameInterval"/> seconds; one frame is made at a time and at most one per interval, whoever asks.
/// When making one fails, the last good frame is shown for up to 30 seconds. The picture is a JPEG; its state in Home
/// Assistant stays <c>idle</c>.
/// </summary>
public class SharpCamera : SharpEntity
{
    const double ShortestInterval = 0.5, LongestInterval = 60;
    double frameInterval = 1;
    // Whether the type overrides Frame(), worked out on first use; per instance, so no static keeps a user's type.
    bool? overridden;

    /// <summary>A camera whose key is <c>main</c>.</summary>
    public SharpCamera() { }
    /// <summary>A camera with its own key, unique within its device.</summary>
    /// <param name="key">What identifies it within the device, such as <c>door</c>.</param>
    /// <param name="name">What Home Assistant calls it; null for the device's name alone.</param>
    public SharpCamera(string key, string? name = null) : base(key, name) { }

    /// <summary>
    /// How many seconds pass between the frames Home Assistant asks for while someone watches, from 0.5 to 60; 1 unless set.
    /// Requests that come sooner after a frame was begun share it.
    /// </summary>
    /// <exception cref="ArgumentException">The interval is outside 0.5 to 60 seconds.</exception>
    public double FrameInterval
    {
        get => frameInterval;
        init => frameInterval = double.IsFinite(value) && value is >= ShortestInterval and <= LongestInterval ? value
            : throw Refused($"a frame interval from {CommandData.Shown(ShortestInterval)} to {CommandData.Shown(LongestInterval)} seconds", CommandData.Shown(value));
    }

    /// <summary>
    /// How a frame is made when Home Assistant needs one, when <see cref="Frame"/> is not overridden: the picture to show,
    /// or null to keep the one shown. Pass <see cref="SharpComponent.Stopping"/> on to what it waits for.
    /// </summary>
    public Func<Task<SharpImage?>>? OnFrame { get; init; }

    /// <summary>
    /// Shows the picture: it is the camera's current frame from now on. A copy is kept, so the caller may go on drawing on
    /// its own picture. From any thread, also before the definition runs.
    /// </summary>
    /// <param name="image">The picture.</param>
    public void Show(SharpImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Frames.Show(image.Copy());
    }

    /// <summary>
    /// Makes a frame when Home Assistant needs one: the picture to show, or null to keep the one shown; runs
    /// <see cref="OnFrame"/> unless overridden. One runs at a time, at most once per <see cref="FrameInterval"/>, and has 8
    /// seconds: one that takes longer is left to finish, and what it gives is not shown. Pass
    /// <see cref="SharpComponent.Stopping"/> on to what it waits for. A camera that only shows pictures leaves this alone.
    /// </summary>
    protected virtual Task<SharpImage?> Frame() => OnFrame?.Invoke() ?? Task.FromResult<SharpImage?>(null);

    /// <summary>
    /// Takes a picture with one of Home Assistant's cameras, such as a doorbell's, as Home Assistant's own snapshot does: a
    /// JPEG, within 10 seconds. Draw on it and show it, or pass it on as it is. Not a camera of this definition itself,
    /// which would wait for itself. Also from <see cref="SharpComponent.Stop"/>, as
    /// <see cref="SharpComponent.CallService"/>: the wait ends only when stopping the definition stops waiting for its code.
    /// </summary>
    /// <param name="entityId">The camera, such as <c>camera.front_door</c>.</param>
    /// <exception cref="InvalidOperationException">Home Assistant gave no picture; the message says why.</exception>
    protected Task<SharpImage> CameraSnapshot(string entityId) =>
        HassCameras.SnapshotAsync(Host?.DefinitionId ?? "", entityId, Host?.Calls ?? CancellationToken.None);

    /// <summary>
    /// Takes a picture with one of Home Assistant's cameras, as <see cref="CameraSnapshot(string)"/> does, and stops waiting
    /// for it as soon as <paramref name="cancellation"/> is cancelled, such as <see cref="SharpComponent.Stopping"/> as the
    /// definition stops.
    /// </summary>
    /// <param name="entityId">The camera, such as <c>camera.front_door</c>.</param>
    /// <param name="cancellation">Ends the wait; the picture Home Assistant still sends is then dropped.</param>
    /// <exception cref="InvalidOperationException">Home Assistant gave no picture; the message says why.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellation"/> was cancelled before the picture came.</exception>
    protected async Task<SharpImage> CameraSnapshot(string entityId, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(Host?.Calls ?? CancellationToken.None, cancellation);
        return await HassCameras.SnapshotAsync(Host?.DefinitionId ?? "", entityId, linked.Token);
    }

    /// <summary>Whether the camera makes its frames when they are needed: it overrides <see cref="Frame"/>, or has <see cref="OnFrame"/>.</summary>
    internal bool MakesFrames => OnFrame != null || (overridden ??= GetType().GetMethod(nameof(Frame), BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)?.DeclaringType != typeof(SharpCamera));

    /// <summary>Runs <see cref="Frame"/>.</summary>
    internal Task<SharpImage?> MakeFrame() => Frame();

    /// <summary>The camera's current frame, and the frames being made.</summary>
    internal CameraFrames Frames { get; } = new();

    internal override string Domain => HassDomains.Camera;
    internal override JsonObject DomainOptions() => new() { ["frame_interval"] = FrameInterval };
}
