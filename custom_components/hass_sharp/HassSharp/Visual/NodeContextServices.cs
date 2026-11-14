using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>
/// What a service acts on, the first argument of every <c>context.Services</c> method of a service that takes a target.
/// An entity id converts to it (<c>"light.kitchen"</c>), and so do an array, a list and a collection expression of them
/// (<c>["light.a", "light.b"]</c>); <see cref="Areas"/>,
/// <see cref="Devices"/>, <see cref="Floors"/> and <see cref="Labels"/> give the others, and the properties combine
/// them: <c>new ServiceTarget { AreaIds = ["kitchen"], LabelIds = ["night"] }</c>.
/// </summary>
[CollectionBuilder(typeof(ServiceTarget), nameof(Create))]
public sealed class ServiceTarget
{
    readonly string? entity;

    public ServiceTarget() { }
    ServiceTarget(string entityId) => entity = entityId;

    /// <summary>The entities, such as <c>light.kitchen</c>.</summary>
    public IReadOnlyList<string>? EntityIds { get; init; }
    /// <summary>The device ids.</summary>
    public IReadOnlyList<string>? DeviceIds { get; init; }
    /// <summary>The area ids, such as <c>kitchen</c>.</summary>
    public IReadOnlyList<string>? AreaIds { get; init; }
    /// <summary>The floor ids.</summary>
    public IReadOnlyList<string>? FloorIds { get; init; }
    /// <summary>The label ids.</summary>
    public IReadOnlyList<string>? LabelIds { get; init; }

    /// <summary>One entity, such as <c>"light.kitchen"</c>.</summary>
    [return: NotNullIfNotNull(nameof(entityId))]
    public static implicit operator ServiceTarget?(string? entityId) => entityId == null ? null : new(entityId);
    /// <summary>Several entities.</summary>
    [return: NotNullIfNotNull(nameof(entityIds))]
    public static implicit operator ServiceTarget?(string[]? entityIds) => entityIds == null ? null : Entities(entityIds);
    /// <summary>Several entities.</summary>
    [return: NotNullIfNotNull(nameof(entityIds))]
    public static implicit operator ServiceTarget?(List<string>? entityIds) => entityIds == null ? null : Entities([.. entityIds]);

    /// <summary>These entities.</summary>
    public static ServiceTarget Entities(params string[] entityIds) => new() { EntityIds = [.. entityIds] };
    /// <summary>These entities: what a collection expression such as <c>["light.a", "light.b"]</c> gives.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static ServiceTarget Create(ReadOnlySpan<string> entityIds) => new() { EntityIds = [.. entityIds] };
    /// <summary>The entities given as a list (a collection expression needs a target to have them).</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public IEnumerator<string> GetEnumerator()
    {
        IEnumerable<string> ids = entity != null ? [entity] : EntityIds ?? [];
        return ids.GetEnumerator();
    }
    /// <summary>Everything in these areas, by area id.</summary>
    public static ServiceTarget Areas(params string[] areaIds) => new() { AreaIds = [.. areaIds] };
    /// <summary>These devices, by device id.</summary>
    public static ServiceTarget Devices(params string[] deviceIds) => new() { DeviceIds = [.. deviceIds] };
    /// <summary>Everything on these floors, by floor id.</summary>
    public static ServiceTarget Floors(params string[] floorIds) => new() { FloorIds = [.. floorIds] };
    /// <summary>Everything with these labels, by label id.</summary>
    public static ServiceTarget Labels(params string[] labelIds) => new() { LabelIds = [.. labelIds] };

    /// <summary>
    /// The target as Home Assistant reads it: <c>entity_id</c>, <c>device_id</c>, <c>area_id</c>, <c>floor_id</c> and
    /// <c>label_id</c>, each only when given. One entity given as a string stays a string.
    /// </summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject();
        void Add(string key, IReadOnlyList<string>? ids) { if (ids != null) json[key] = new JsonArray([.. ids.Select(id => (JsonNode?)JsonValue.Create(id))]); }
        if (entity != null) json["entity_id"] = entity;
        else Add("entity_id", EntityIds);
        Add("device_id", DeviceIds); Add("area_id", AreaIds); Add("floor_id", FloorIds); Add("label_id", LabelIds);
        return json;
    }
}

/// <summary>How the typed service calls send a field's value: as every service call does, with snake_case member names
/// (<c>new { Minutes = 5 }</c> is sent as <c>{"minutes": 5}</c>) and enums by name.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ServiceData
{
    /// <summary>The value as Home Assistant receives it.</summary>
    public static JsonNode? Value<T>(T value) => JsonSerializer.SerializeToNode(value, HassServices.JsonOptions);
}
