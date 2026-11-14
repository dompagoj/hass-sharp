using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>
/// What a node method can reach of the run it is part of. Declare a parameter of this type and the node gets one;
/// it is never a pin. Each step gets its own, made just before it runs.
/// </summary>
public sealed class NodeContext
{
    readonly VisualRuntime runtime;
    readonly VisualScope scope;
    readonly string node;

    internal NodeContext(VisualRuntime runtime, VisualScope scope, string node, string runId, JsonElement trigger)
    {
        this.runtime = runtime; this.scope = scope; this.node = node; RunId = runId; Trigger = trigger;
    }

    /// <summary>Cancelled when the run stops: a Stop step, a parallel branch that failed, or the automation being stopped or restarted.</summary>
    public CancellationToken CancellationToken => scope.CancellationToken;

    /// <summary>The id of this run, as the run history shows it; empty outside a recorded run.</summary>
    public string RunId { get; }

    /// <summary>
    /// What started the run, as the trigger's Data output gives it: for an entity change, its <c>entity_id</c> and
    /// <c>from_state</c> and <c>to_state</c>; for a Home Assistant trigger, its variables. Undefined for a run without one.
    /// </summary>
    public JsonElement Trigger { get; }

    /// <summary>
    /// The most recent failure on this path, when a step's error pin led here; null when no step has failed. The same
    /// failure the Error details step shows.
    /// </summary>
    public Exception? Error => scope.LastError;

    /// <summary>
    /// The current state of an entity, read as the Read entity step reads it: for the entity that started the run, its
    /// state when the trigger fired. Throws when the entity does not exist.
    /// </summary>
    /// <param name="entityId">The entity, such as <c>light.kitchen</c>.</param>
    public EntityStateSnapshot State(string entityId) => VisualRuntime.ReadEntity(entityId);

    /// <summary>
    /// Calls a Home Assistant service, as the Call service step does, and waits until Home Assistant has run it.
    /// </summary>
    /// <param name="service">The service, as <c>domain.service</c>, such as <c>light.turn_on</c>.</param>
    /// <param name="target">An entity id (<c>"light.kitchen"</c>), or an object with <c>entity_id</c>, <c>device_id</c>,
    /// <c>area_id</c>, <c>floor_id</c> or <c>label_id</c>; null for none.</param>
    /// <param name="data">The service's fields, such as <c>new JsonObject { ["brightness_pct"] = 40 }</c>; null for none.</param>
    /// <param name="returnResponse">Whether to ask for the service's response; only services that return one accept it.</param>
    /// <returns>The service's response when <paramref name="returnResponse"/> is true, else null.</returns>
    public Task<JsonElement?> CallService(string service, JsonNode? target = null, JsonObject? data = null, bool returnResponse = false) =>
        runtime.CallService(scope, node, service, target, data, returnResponse);

    /// <summary>Writes a message to the Home Assistant log, as the Log step does.</summary>
    /// <param name="message">The message.</param>
    /// <param name="level">How important it is; Info unless given.</param>
    public void Log(string message, PyLogLevel level = PyLogLevel.Info) => PyInterop.Log((int)level, message);
}
