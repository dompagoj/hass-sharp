using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>
/// The method names of an entity graph's roots, as the generated class has them (<see cref="VisualNodeCatalog.EffectiveTriggerMethod"/>).
/// A trigger in the graph is not among them: it runs as an automation's does.
/// </summary>
/// <param name="Setup">The When started flow's method; null when the graph has none.</param>
/// <param name="Commands">Each When commanded flow's method, keyed <c>&lt;entity key&gt;:&lt;command&gt;</c>.</param>
/// <param name="States">Each State's method, in node id order.</param>
internal sealed record DefinitionRoots(string? Setup, IReadOnlyDictionary<string, string> Commands, IReadOnlyList<string> States);

/// <summary>
/// Between a stored entity definition and the graph its nodes are checked, generated and run as: the stored graph never
/// has a <c>definition</c>; the document's entities and settings are composed into one for the validator and the
/// generator, and taken out again before the graph is stored.
/// </summary>
internal static class DefinitionGraph
{
    /// <summary>
    /// A copy of the definition's graph with <c>definition</c> = <c>{entities, settings}</c>: the document's entities as
    /// it gives them, in its order, and each setting's key and type only. A setting's value, label and secret flag never
    /// reach the graph (nor the source it embeds in): a value may be secret, and changing one must not change the
    /// source. Any <c>definition</c> already in the graph is replaced. Without a graph, the definition alone.
    /// </summary>
    internal static JsonObject Compose(DefinitionBody body)
    {
        var graph = Decompose(body.Logic?.Graph ?? []);
        graph["definition"] = new JsonObject
        {
            ["entities"] = body.Entities?.DeepClone() ?? new JsonArray(),
            ["settings"] = new JsonArray((body.Settings ?? []).Select(s => (JsonNode)new JsonObject { ["key"] = s?.Key, ["type"] = s?.Type }).ToArray()),
        };
        return graph;
    }

    /// <summary>A copy of the graph without its <c>definition</c>; every other key is kept.</summary>
    internal static JsonObject Decompose(JsonObject graph)
    {
        var copy = (JsonObject)graph.DeepClone();
        copy.Remove("definition");
        return copy;
    }

    /// <summary>The methods of the graph's When started, When commanded and State roots, named as the generator names them.</summary>
    internal static DefinitionRoots Roots(JsonObject graph, VisualCatalogSnapshot snapshot)
    {
        string? setup = null;
        var commands = new Dictionary<string, string>(StringComparer.Ordinal);
        var states = new List<string>();
        var roots = (graph["nodes"] as JsonArray ?? []).OfType<JsonObject>().Where(n => VisualNodeCatalog.IsRoot(n, snapshot))
            .OrderBy(n => VisualNodeCatalog.Text(n, "id"), StringComparer.Ordinal);
        // A valid graph has one When started and one flow per command; the first in id order is kept otherwise.
        foreach (var node in roots)
            switch (VisualNodeCatalog.Text(node, "kind"))
            {
                case "entity_setup": setup ??= VisualNodeCatalog.EffectiveTriggerMethod(node); break;
                case "entity_command":
                    commands.TryAdd(VisualNodeCatalog.Text(node["config"], "entity") + ":" + VisualNodeCatalog.Text(node["config"], "command"), VisualNodeCatalog.EffectiveTriggerMethod(node));
                    break;
                case "entity_state": states.Add(VisualNodeCatalog.EffectiveTriggerMethod(node)); break;
            }
        return new(setup, commands, states);
    }
}
