using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>
/// The Home Assistant entities a State (<c>entity_state</c>) of an entity definition's graph reads, so that it is
/// worked out again whenever one of them changes. A State has no flow: what it reads is in the graph, as the entity
/// chosen on each value node upstream of it, such as Has state (<c>entity_is</c>) and Current state
/// (<c>entity_current</c>).
/// </summary>
internal static class StateDependencies
{
    /// <summary>
    /// The distinct entity ids, in ordinal order, chosen (not connected) on the entity id inputs of every value node
    /// upstream of the node <paramref name="stateNodeId"/> through value wires
    /// (<see cref="VisualNodeCatalog.ValueUpstream(JsonObject, string, VisualCatalogSnapshot)"/>). An entity id input is
    /// one that takes nothing but entity ids (<c>entity_id</c>, or <c>entity_id:&lt;domain&gt;</c> for a node library's): an
    /// input open to every type, such as a comparison's, holds an entity id as a value rather than reading it. A chosen
    /// entity is a literal of non-empty text. A reader given its entity over a wire adds none (the entity it gets is read
    /// as it is, so the validators warn with <c>state.dynamicSource</c>), but an entity chosen further up that wire, such
    /// as an entity id Constant's, still does. The walk stops at a step, so neither what a step reads nor what is upstream
    /// of it is a dependency. Empty for an unknown node or a graph of the wrong shape; meaningful for a valid graph only.
    /// </summary>
    internal static IReadOnlyList<string> Of(JsonObject graph, string stateNodeId, VisualCatalogSnapshot snapshot)
    {
        var nodes = VisualNodeCatalog.GraphNodes(graph);
        var connected = VisualNodeCatalog.ValueWires(graph, nodes).Select(e => (VisualNodeCatalog.Text(e, "target"), VisualNodeCatalog.Text(e, "targetPort"))).ToHashSet();
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var node in VisualNodeCatalog.ValueUpstream(graph, stateNodeId, snapshot))
        {
            var id = VisualNodeCatalog.Text(node, "id");
            foreach (var (port, input) in VisualNodeCatalog.DataInputs(node, null, snapshot))
                if (!connected.Contains((id, port)) && TakesEntityIds(input) && VisualNodeCatalog.InputValue(node, port, snapshot) is JsonObject operand &&
                    VisualNodeCatalog.Text(operand, "kind") == "literal" && operand["value"] is JsonValue value && value.TryGetValue<string>(out var entity) && entity.Length > 0)
                    found.Add(entity);
        }
        return found.ToList();
    }
    static bool TakesEntityIds(JsonNode? input) => input?["types"] is JsonArray { Count: > 0 } types &&
        types.All(t => t is JsonValue v && v.TryGetValue<string>(out var type) && (type == "entity_id" || type.StartsWith("entity_id:", StringComparison.Ordinal)));
}
