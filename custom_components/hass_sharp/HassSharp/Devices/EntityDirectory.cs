using System.Diagnostics.CodeAnalysis;

namespace HassSharp;

/// <summary>An entity of a running definition, with the entity id Home Assistant gave it.</summary>
/// <param name="UniqueId"><c>&lt;definition id&gt;:&lt;key&gt;</c>.</param>
/// <param name="Key">The entity's key within its definition.</param>
/// <param name="Domain">Its Home Assistant domain, such as <c>switch</c>.</param>
/// <param name="Name">What Home Assistant calls it; null for the device's name alone.</param>
/// <param name="EntityId">Its entity id, such as <c>switch.plug_relay</c>; null when Home Assistant did not say.</param>
public sealed record LiveEntity(string UniqueId, string Key, string Domain, string? Name, string? EntityId);

/// <summary>The started definitions by id, so a command from Home Assistant finds its entity by unique id.</summary>
internal sealed class EntityDirectory
{
    readonly object gate = new();
    readonly Dictionary<string, DefinitionHost> hosts = new(StringComparer.Ordinal);

    /// <summary>The started definition and entity with this unique id, if there is one.</summary>
    public bool TryGet(string uniqueId, [MaybeNullWhen(false)] out DefinitionHost host, [MaybeNullWhen(false)] out SharpEntity entity)
    {
        // A definition id never holds a colon, so the first one ends it.
        var colon = uniqueId.IndexOf(':');
        host = colon < 0 ? null : Host(uniqueId[..colon]);
        entity = host?.Entity(uniqueId);
        if (host != null && entity != null) return true;
        host = null;
        return false;
    }

    /// <summary>The started host of the definition; null when none is.</summary>
    public DefinitionHost? Host(string definitionId) { lock (gate) return hosts.GetValueOrDefault(definitionId); }

    internal void Register(DefinitionHost host) { lock (gate) hosts[host.DefinitionId] = host; }

    /// <summary>Removes the host, unless another revision of its definition has replaced it.</summary>
    internal void Leave(DefinitionHost host)
    {
        lock (gate)
            if (hosts.TryGetValue(host.DefinitionId, out var current) && current == host) hosts.Remove(host.DefinitionId);
    }
}
