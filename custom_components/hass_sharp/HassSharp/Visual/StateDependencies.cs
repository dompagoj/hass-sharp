using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>
/// The Home Assistant entities the state of an Entity node of an entity definition's graph reads, so that it is
/// worked out again whenever one of them changes. Its state flow has no steps: what it reads is in the graph, as the entity
/// chosen on each value node upstream of it, such as Has state (<c>entity_is</c>) and Current state
/// (<c>entity_current</c>).
/// </summary>
internal static class StateDependencies
{
    /// <summary>
    /// The distinct entity ids, in ordinal order, chosen (not connected) on the entity id inputs of every value node
    /// upstream of the node <paramref name="stateNodeId"/> through value wires
    /// (<see cref="VisualNodeCatalog.ValueUpstream(JsonObject, string, VisualCatalogSnapshot)"/>). An entity id input is
    /// one that takes nothing but entity ids (<see cref="VisualNodeCatalog.TakesOnlyEntityIds"/>), on a built-in, node
    /// library or Custom C# value node alike; a chosen entity is a literal of non-empty text in it. An input that is
    /// connected adds none: the entity it is given is read as it is. The validators warn on such a reader with
    /// <c>state.dynamicSource</c>, unless the wire comes from a Constant of an entity id type
    /// (<see cref="VisualNodeCatalog.FollowedSource"/>), whose own chosen entity is counted here, or from an Entity
    /// (<c>ha_entity</c>), whose entity id, when it is one, is counted too. The walk stops at a step,
    /// so neither what a step reads nor what is upstream of it is a dependency. A structural wire (a Device wire) carries no
    /// value: it is not walked, and a structural input is no reader's. Empty for an unknown node or a graph of
    /// the wrong shape; meaningful for a valid graph only.
    /// </summary>
    internal static IReadOnlyList<string> Of(JsonObject graph, string stateNodeId, VisualCatalogSnapshot snapshot)
    {
        var nodes = VisualNodeCatalog.GraphNodes(graph);
        var connected = VisualNodeCatalog.ValueWires(graph, nodes).Select(e => (VisualNodeCatalog.Text(e, "target"), VisualNodeCatalog.Text(e, "targetPort"))).ToHashSet();
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var node in VisualNodeCatalog.ValueUpstream(graph, stateNodeId, snapshot))
        {
            var id = VisualNodeCatalog.Text(node, "id");
            // An Entity's entity is chosen on it, and is followed through its wires (VisualNodeCatalog.FollowedSource).
            if (VisualNodeCatalog.Text(node, "kind") == "ha_entity" && VisualNodeCatalog.Text(node["config"], "entity") is var picked && VisualNodeCatalog.EntityIdDomain(picked) != null)
                found.Add(picked);
            foreach (var (port, input) in VisualNodeCatalog.DataInputs(node, null, snapshot))
                if (!VisualNodeCatalog.Structural(input) && !connected.Contains((id, port)) && VisualNodeCatalog.TakesOnlyEntityIds(input) && VisualNodeCatalog.InputValue(node, port, snapshot) is JsonObject operand &&
                    VisualNodeCatalog.Text(operand, "kind") == "literal" && operand["value"] is JsonValue value && value.TryGetValue<string>(out var entity) && entity.Length > 0)
                    found.Add(entity);
        }
        return found.ToList();
    }
}
