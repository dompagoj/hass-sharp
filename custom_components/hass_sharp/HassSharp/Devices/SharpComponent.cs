using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>
/// What hosts a custom device or entity while its definition runs: it publishes state to Home Assistant, owns the
/// timers and entity tracks the component starts and the definition's HTTP client and downloads, and says when the
/// definition is stopping. A component without one (built but not yet attached, or in a test) publishes nothing.
/// </summary>
internal interface IComponentHost
{
    /// <summary>The definition's id.</summary>
    string DefinitionId { get; }
    /// <summary>Cancelled when the definition stops or is replaced.</summary>
    CancellationToken Stopping { get; }
    /// <summary>
    /// Ends what the host waits on for the component, such as a service call: cancelled only once stopping stops waiting
    /// for the component's code, so a call from <c>Stop()</c> still completes.
    /// </summary>
    CancellationToken Calls { get; }
    /// <summary>Sends the entity's current snapshot towards Home Assistant.</summary>
    void Publish(SharpEntity entity);
    /// <summary>Runs <paramref name="action"/> every <paramref name="interval"/> until disposed or stopped.</summary>
    IDisposable Every(TimeSpan interval, Func<Task> action, bool immediately);
    /// <summary>Runs <paramref name="action"/> each time the entity changes, until disposed or stopped.</summary>
    IDisposable Track(string entityId, Func<EntityChange, Task> action);
    /// <summary>Writes to the Home Assistant log on behalf of the definition.</summary>
    void Log(string message, PyLogLevel level);
    /// <summary>
    /// The definition's HTTP client, the same for its device and every entity: made on first use, disposed once the last
    /// <c>Stop()</c> has run, which cancels what is still in flight on it. Throws <see cref="ObjectDisposedException"/> after.
    /// </summary>
    HttpClient Http { get; }
    /// <summary>The definition's downloads (<see cref="SharpFiles"/>): ended once it stopped. Throws <see cref="ObjectDisposedException"/> after.</summary>
    SharpFiles Files { get; }
}

/// <summary>A change of an entity that <see cref="SharpComponent.Track(string, Func{EntityChange, Task})"/> follows.</summary>
/// <param name="EntityId">The entity that changed, such as <c>sensor.outdoor_temperature</c>.</param>
/// <param name="To">Its new state.</param>
/// <param name="From">Its state before the change; null when the entity has just appeared.</param>
public sealed record EntityChange(string EntityId, EntityStateSnapshot To, EntityStateSnapshot? From);

/// <summary>
/// Marks a property of a custom device or entity as a setting: an input of its C# node, which takes a value typed into
/// the node or a Setting, such as a host name or a token, applied before <see cref="SharpComponent.Setup"/> runs.
/// </summary>
/// <param name="label">What the node's input is called; the property name when null.</param>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingAttribute(string? label = null) : Attribute
{
    /// <summary>What the node's input is called; the property name when null.</summary>
    public string? Label { get; } = label;
    /// <summary>A line of help under the input's field.</summary>
    public string? Description { get; set; }
    /// <summary>Whether the value is a secret, such as a password or token: only a Setting gives it, and logs never show it.</summary>
    public bool Secret { get; set; }
    /// <summary>
    /// The smallest value an <c>int</c>, <c>long</c> or <c>double</c> setting takes, such as <c>1</c> for a poll interval in
    /// seconds; <see cref="double.NaN"/>, the default, for none (an <c>int</c> then still takes none below its own smallest).
    /// A save outside it is refused, and so is a saved value outside it when the definition starts.
    /// </summary>
    public double Minimum { get; set; } = double.NaN;
    /// <summary>The largest value an <c>int</c>, <c>long</c> or <c>double</c> setting takes; <see cref="double.NaN"/>, the default, for none. As <see cref="Minimum"/>.</summary>
    public double Maximum { get; set; } = double.NaN;
}

/// <summary>
/// The common base of custom devices (<see cref="SharpDevice"/>) and entities (<see cref="SharpEntity"/>): the life
/// cycle a definition runs it through, and what its code can reach of Home Assistant while it runs.
/// </summary>
public abstract class SharpComponent
{
    /// <summary>The host while the definition runs; null before it is attached.</summary>
    internal IComponentHost? Host { get; set; }

    /// <summary>
    /// Runs once before the entities are registered, with the settings applied: the place to check settings or fetch
    /// what the device needs. A failure leaves Home Assistant untouched. The constructors and <c>Setup()</c>s of a
    /// definition's C# nodes, with the <c>Setup()</c> of each entity a C# device adds, have 60 seconds together, not
    /// counting the time a download of <see cref="Files"/> receives; one that takes longer fails the definition, which is
    /// tried again when the libraries or Home Assistant's services change.
    /// </summary>
    protected internal virtual Task Setup() => Task.CompletedTask;

    /// <summary>
    /// Runs when the definition starts, after its entities are registered: the place to start timers and tracks. It
    /// must return: the entities stay unavailable until every <c>Start()</c> of the definition has, so poll in a timer
    /// started with <see cref="Every"/>, not in a loop here.
    /// </summary>
    protected internal virtual Task Start() => Task.CompletedTask;

    /// <summary>
    /// Runs when the definition stops or is replaced, after its timer callbacks, track callbacks and commands have
    /// ended; it may still call services. Timers and tracks started through this component stop on their own. Once the
    /// definition has started it runs on the device and every entity, also on one whose own <c>Start()</c> never ran
    /// because an earlier one threw, so it must not count on what <c>Start()</c> sets up. It does not run when Home
    /// Assistant shuts down: then only <see cref="Stopping"/> is cancelled.
    /// </summary>
    protected internal virtual Task Stop() => Task.CompletedTask;

    /// <summary>
    /// Cancelled when the definition stops or is replaced, and when Home Assistant shuts down; already cancelled when
    /// <see cref="Stop"/> runs. <see cref="CancellationToken.None"/> before it is attached.
    /// </summary>
    protected CancellationToken Stopping => Host?.Stopping ?? CancellationToken.None;

    /// <summary>
    /// Runs <paramref name="action"/> every <paramref name="interval"/> until the definition stops or the returned
    /// handle is disposed. Only once the component is attached, such as from <see cref="Start"/>. A run that throws is
    /// logged as an error once for each new reason (a device that does not answer is not logged on every tick), and a run
    /// that works after one that threw is logged once.
    /// </summary>
    /// <param name="interval">The time between runs.</param>
    /// <param name="action">What to run, such as polling the device.</param>
    /// <param name="immediately">Whether the first run is now rather than after one interval.</param>
    protected IDisposable Every(TimeSpan interval, Func<Task> action, bool immediately = true) =>
        Attached().Every(interval, action, immediately);

    /// <summary>
    /// Runs <paramref name="action"/> each time an entity changes, until the definition stops or the returned handle
    /// is disposed. Only once the component is attached, such as from <see cref="Start"/>.
    /// </summary>
    /// <param name="entityId">The entity to follow, such as <c>binary_sensor.door</c>.</param>
    /// <param name="action">What to run with the change.</param>
    protected IDisposable Track(string entityId, Func<EntityChange, Task> action) => Attached().Track(entityId, action);

    /// <summary>
    /// Runs <paramref name="action"/> each time the entity of the handle changes, as <see cref="Track(string, Func{EntityChange, Task})"/>
    /// does with its id, such as <c>Track(Light, change => ...)</c> for a <c>[Setting]</c> of an entity class.
    /// </summary>
    /// <param name="entity">The handle of the entity to follow.</param>
    /// <param name="action">What to run with the change.</param>
    protected IDisposable Track(Entities.Entity entity, Func<EntityChange, Task> action) =>
        Track((entity ?? throw new ArgumentNullException(nameof(entity))).EntityId, action);

    /// <summary>
    /// An HTTP client to reach the device with, such as <c>await Http.GetStringAsync(url, Stopping)</c>: the definition's
    /// own, the same for its device and every entity, with 30 seconds per request, at most 5 redirects, certificates
    /// always checked and <c>User-Agent: HassSharp</c>. Pass <see cref="Stopping"/> to each request, so that it ends when
    /// the definition stops. The definition disposes it after the last <see cref="Stop"/> (a request from there still
    /// works), which cancels any request still in flight; from then on it throws <see cref="ObjectDisposedException"/>.
    /// Only once the component is attached, from <see cref="Setup"/> on.
    /// </summary>
    protected HttpClient Http => Attached(nameof(Http)).Http;

    /// <summary>
    /// The files the definition depends on, downloaded once and checked against their checksum, such as
    /// <c>var path = await Files.Download(url, sha256, cancellationToken: Stopping)</c> in <see cref="Setup"/>. Only once
    /// the component is attached, from <see cref="Setup"/> on.
    /// </summary>
    protected SharpFiles Files => Attached(nameof(Files)).Files;

    /// <summary>The current state of an entity, read from Home Assistant now. Throws when the entity does not exist.</summary>
    /// <param name="entityId">The entity, such as <c>light.kitchen</c>.</param>
    protected EntityStateSnapshot State(string entityId) => VisualRuntime.ReadEntity(entityId, fresh: true);

    /// <summary>
    /// Calls a Home Assistant service by its id and waits until Home Assistant has run it, as the Call service step
    /// does. Also from <see cref="Stop"/>: the wait ends only when stopping the definition stops waiting for its code.
    /// </summary>
    /// <param name="service">The service, as <c>domain.service</c>, such as <c>light.turn_on</c>.</param>
    /// <param name="target">An entity id (<c>"light.kitchen"</c>), or an object with <c>entity_id</c>, <c>device_id</c>,
    /// <c>area_id</c>, <c>floor_id</c> or <c>label_id</c>; null for none.</param>
    /// <param name="data">The service's fields, such as <c>new JsonObject { ["brightness_pct"] = 40 }</c>; null for none.</param>
    /// <param name="returnResponse">Whether to ask for the service's response; only services that return one accept it.</param>
    /// <returns>The service's response when <paramref name="returnResponse"/> is true, else null.</returns>
    protected async Task<JsonElement?> CallService(string service, JsonNode? target = null, JsonObject? data = null, bool returnResponse = false)
    {
        var payload = data == null ? new JsonObject() : (JsonObject)data.DeepClone();
        var (domain, name) = HassServices.PrepareCall(service, payload, target, "The target");
        // Not Stopping, which is cancelled before Stop() runs: a call from Stop() must still complete.
        var calls = Host?.Calls ?? CancellationToken.None;
        if (returnResponse) return await HassServices.CallServiceAsync<JsonElement>(domain, name, payload).WaitAsync(calls);
        await HassServices.CallServiceAsync(domain, name, payload).WaitAsync(calls);
        return null;
    }

    /// <summary>Writes a message to the Home Assistant log.</summary>
    /// <param name="message">The message.</param>
    /// <param name="level">How important it is; Info unless given.</param>
    protected void Log(string message, PyLogLevel level = PyLogLevel.Info)
    {
        if (Host is { } host) host.Log(message, level);
        else PyInterop.Log((int)level, message);
    }

    IComponentHost Attached() =>
        Host ?? throw new InvalidOperationException($"{GetType().Name} is not running yet: start timers and tracks from Start, not from the constructor.");

    IComponentHost Attached(string what) =>
        Host ?? throw new InvalidOperationException($"{GetType().Name} is not running yet: use {what} from Setup or later, not from the constructor.");
}
