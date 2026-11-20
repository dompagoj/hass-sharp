using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>
/// What the generated service classes (<c>Services.Light</c> and the others) call through: a step's
/// <see cref="NodeContext"/>, or the <see cref="ComponentServices"/> of a custom device or entity. Authors do not
/// implement it.
/// </summary>
public interface IServiceCaller
{
    /// <summary>Calls a Home Assistant service by its id and waits until Home Assistant has run it.</summary>
    /// <param name="service">The service, as <c>domain.service</c>, such as <c>light.turn_on</c>.</param>
    /// <param name="target">An entity id (<c>"light.kitchen"</c>), or an object with <c>entity_id</c>, <c>device_id</c>,
    /// <c>area_id</c>, <c>floor_id</c> or <c>label_id</c>; null for none.</param>
    /// <param name="data">The service's fields, such as <c>new JsonObject { ["brightness_pct"] = 40 }</c>; null for none.</param>
    /// <param name="returnResponse">Whether to ask for the service's response; only services that return one accept it.</param>
    /// <returns>The service's response when <paramref name="returnResponse"/> is true, else null.</returns>
    Task<JsonElement?> CallService(string service, JsonNode? target = null, JsonObject? data = null, bool returnResponse = false);
}

/// <summary>
/// The <c>Services</c> of a custom device or entity: this Home Assistant's services, one property per domain, with a
/// typed method for each service, such as <c>await Services.Light.TurnOn(Light, brightnessPct: 30);</c>. Each call is
/// the component's own <c>CallService</c>, so it works wherever that does, <c>Stop()</c> included.
/// </summary>
public sealed class ComponentServices : IServiceCaller
{
    readonly SharpComponent component;

    internal ComponentServices(SharpComponent component) => this.component = component;

    /// <summary>
    /// Calls a Home Assistant service by its id, as the component's own <c>CallService</c> does: it waits until Home
    /// Assistant has run it, also from <c>Stop()</c>. For a service id known only at run time; otherwise the domain's
    /// property calls it with typed fields.
    /// </summary>
    /// <param name="service">The service, as <c>domain.service</c>, such as <c>light.turn_on</c>.</param>
    /// <param name="target">An entity id (<c>"light.kitchen"</c>), or an object with <c>entity_id</c>, <c>device_id</c>,
    /// <c>area_id</c>, <c>floor_id</c> or <c>label_id</c>; null for none.</param>
    /// <param name="data">The service's fields, such as <c>new JsonObject { ["brightness_pct"] = 40 }</c>; null for none.</param>
    /// <param name="returnResponse">Whether to ask for the service's response; only services that return one accept it.</param>
    /// <returns>The service's response when <paramref name="returnResponse"/> is true, else null.</returns>
    public Task<JsonElement?> CallService(string service, JsonNode? target = null, JsonObject? data = null, bool returnResponse = false) =>
        component.CallServiceForServices(service, target, data, returnResponse);
}
