using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>
/// What hosts a custom device or entity while its definition runs: it publishes state to Home Assistant, owns the
/// timers and entity tracks the component starts, and says when the definition is stopping. A component without one
/// (built but not yet attached, or in a test) publishes nothing.
/// </summary>
internal interface IComponentHost
{
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
}

/// <summary>A change of an entity that <see cref="SharpComponent.Track"/> follows.</summary>
/// <param name="EntityId">The entity that changed, such as <c>sensor.outdoor_temperature</c>.</param>
/// <param name="To">Its new state.</param>
/// <param name="From">Its state before the change; null when the entity has just appeared.</param>
public sealed record EntityChange(string EntityId, EntityStateSnapshot To, EntityStateSnapshot? From);

/// <summary>
/// Marks a property of a custom device or entity as a setting: a value the user fills in for each definition, such as
/// a host name or a token, applied before <see cref="SharpComponent.Setup"/> runs.
/// </summary>
/// <param name="label">What the settings form calls it; the property name when null.</param>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingAttribute(string? label = null) : Attribute
{
    /// <summary>What the settings form calls it; the property name when null.</summary>
    public string? Label { get; } = label;
    /// <summary>A line of help under the field in the settings form.</summary>
    public string? Description { get; set; }
    /// <summary>Whether the value is a secret, such as a password or token: the form hides it and logs never show it.</summary>
    public bool Secret { get; set; }
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
    /// what the device needs. A failure leaves Home Assistant untouched. The <c>Setup()</c>s of a definition's device
    /// and entities have 60 seconds together; one that takes longer fails the definition, which is tried again when
    /// the libraries or Home Assistant's services change.
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
    /// handle is disposed. Only once the component is attached, such as from <see cref="Start"/>.
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
}
