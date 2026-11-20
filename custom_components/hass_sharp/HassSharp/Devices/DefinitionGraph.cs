using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>
/// The method names of an entity graph's roots, as the generated class has them (<see cref="VisualNodeCatalog.EffectiveTriggerMethod"/>).
/// A trigger in the graph is not among them: it runs as an automation's does.
/// </summary>
/// <param name="Setup">The When started flow's method; null when the graph has none.</param>
/// <param name="Commands">Each command flow's method, keyed <c>&lt;entity key&gt;:&lt;command&gt;</c>.</param>
/// <param name="States">Each state flow's method, in node id order.</param>
internal sealed record DefinitionRoots(string? Setup, IReadOnlyDictionary<string, string> Commands, IReadOnlyList<string> States)
{
    /// <summary>The key of the entity each state flow's method works out the state of, by method.</summary>
    public IReadOnlyDictionary<string, string> StateEntities { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    /// <summary>Each frame flow's method, keyed by the key of its camera.</summary>
    public IReadOnlyDictionary<string, string> Frames { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>One flow an Entity node starts, as the generator gives it a method and the run history names its runs.</summary>
/// <param name="NodeId">The Entity node's id.</param>
/// <param name="Port">The pin the flow starts at: <c>command:&lt;command&gt;</c> or <c>frame</c>; <c>state</c> for its state, which has no pin.</param>
/// <param name="Method">The flow's method (<see cref="VisualNodeCatalog.FlowMethod"/> of the node and <paramref name="Port"/>).</param>
/// <param name="Kind"><c>state</c>, <c>command</c> or <c>frame</c>.</param>
/// <param name="Key">The key of the node's entity.</param>
/// <param name="Command">The command of a <c>command</c> flow; null for the others.</param>
/// <param name="Label">The run's name: <c>{Name}: State</c>, <c>{Name}: {Command}</c> or <c>{Name}: When a frame is needed</c>.</param>
/// <param name="TrackHistory">Whether its runs are recorded, as the node's <c>history</c> switches say.</param>
internal sealed record HubFlow(string NodeId, string Port, string Method, string Kind, string Key, string? Command, string Label, bool TrackHistory);

/// <summary>
/// Between a stored entity definition and the graph its nodes are checked, generated and run as: the stored graph never
/// has a <c>definition</c>; the document's settings, and whether it has a device, are composed into one for the
/// validator and the generator, and taken out again before the graph is stored. Its entities are its Entity nodes.
/// </summary>
internal static class DefinitionGraph
{
    /// <summary>
    /// A copy of the definition's graph with <c>definition</c> = <c>{settings}</c>: each setting's key and type only. A
    /// setting's value, label and secret flag never reach the graph (nor the source it embeds in): a value may be secret,
    /// and changing one must not change the source. A definition with a device has an empty <c>device</c> object too,
    /// which only says it has one (its entities then need no names). Its entities are the graph's Entity nodes, as
    /// <see cref="VisualNodeCatalog.WithGraphTypes"/> derives them. Any <c>definition</c> already in the graph is replaced.
    /// Without a graph, the definition alone.
    /// </summary>
    internal static JsonObject Compose(DefinitionBody body)
    {
        var graph = Decompose(body.Logic?.Graph ?? []);
        var definition = new JsonObject
        {
            ["settings"] = new JsonArray((body.Settings ?? []).Select(s => (JsonNode)new JsonObject { ["key"] = s?.Key, ["type"] = s?.Type }).ToArray()),
        };
        if (body.Device != null) definition["device"] = new JsonObject();
        graph["definition"] = definition;
        return graph;
    }

    /// <summary>
    /// The entities of a composed graph (<see cref="Compose"/>), as a copy: the ones its Entity nodes declare
    /// (<see cref="VisualNodeCatalog.WithGraphTypes"/>), the first of each key.
    /// </summary>
    internal static JsonArray Entities(JsonObject composed, VisualCatalogSnapshot snapshot) =>
        new([.. VisualNodeCatalog.DefinitionItems(VisualNodeCatalog.WithGraphTypes(snapshot.Root, composed), "entities").Select(e => e.DeepClone())]);

    /// <summary>A copy of the graph without its <c>definition</c>; every other key is kept.</summary>
    internal static JsonObject Decompose(JsonObject graph)
    {
        var copy = (JsonObject)graph.DeepClone();
        copy.Remove("definition");
        return copy;
    }

    /// <summary>
    /// The methods of the graph's When started step and of its Entity nodes' flows (<see cref="HubFlows"/>), named as the
    /// generator names them: a node's state flow works out its entity's state, each command flow is its entity's flow for
    /// that command, its frame flow its camera's.
    /// </summary>
    internal static DefinitionRoots Roots(JsonObject graph, VisualCatalogSnapshot snapshot)
    {
        var commands = new Dictionary<string, string>(StringComparer.Ordinal);
        var states = new List<string>();
        var stateEntities = new Dictionary<string, string>(StringComparer.Ordinal);
        var frames = new Dictionary<string, string>(StringComparer.Ordinal);
        var roots = (graph["nodes"] as JsonArray ?? []).OfType<JsonObject>().Where(n => VisualNodeCatalog.IsRoot(n, snapshot))
            .OrderBy(n => VisualNodeCatalog.Text(n, "id"), StringComparer.Ordinal).ToArray();
        foreach (var node in roots.Where(n => VisualNodeCatalog.Declares(VisualNodeCatalog.Spec(n, snapshot), "entity")))
            foreach (var flow in HubFlows(node, graph, snapshot))
                switch (flow.Kind)
                {
                    case "state": states.Add(flow.Method); stateEntities[flow.Method] = flow.Key; break;
                    case "command": commands.TryAdd(flow.Key + ":" + flow.Command, flow.Method); break;
                    case "frame": frames.TryAdd(flow.Key, flow.Method); break;
                }
        // A valid graph has one When started and one Entity node per key; the first in id order is kept otherwise.
        var setup = roots.Where(n => VisualNodeCatalog.Text(n, "kind") == "entity_setup").Select(VisualNodeCatalog.EffectiveTriggerMethod).FirstOrDefault();
        return new(setup, commands, states) { StateEntities = stateEntities, Frames = frames };
    }

    /// <summary>
    /// The flows of the Entity node <paramref name="node"/>, in this order: its state, when at least one of its state values
    /// is supplied (a value wire into it, or a value under <c>config.arguments</c> that is not unset; its Device pin is
    /// none), recorded when
    /// <c>config.history.state</c> is true; each command whose pin has a step order wire out of it, in its type's order,
    /// recorded unless <c>config.history.commands</c> is false; its frame, when that pin has one, recorded when
    /// <c>config.history.frame</c> is true. None for a node of an unknown type.
    /// </summary>
    internal static IReadOnlyList<HubFlow> HubFlows(JsonObject node, JsonObject graph, VisualCatalogSnapshot snapshot)
    {
        var id = VisualNodeCatalog.Text(node, "id");
        var config = node["config"] as JsonObject;
        var key = VisualNodeCatalog.Text(config, "key");
        var name = VisualGraphValidator.Words.EntityName(node, VisualNodeCatalog.Spec(node, snapshot));
        var row = VisualNodeCatalog.DeclaredDomain(node, snapshot.Root);
        var history = config?["history"] as JsonObject;
        bool Recorded(string flag, bool fallback) => history?[flag] is JsonValue value && value.TryGetValue<bool>(out var on) ? on : fallback;
        var edges = (graph["edges"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var flows = new List<HubFlow>();
        HubFlow Flow(string port, string kind, string? command, string what, bool track) =>
            new(id, port, VisualNodeCatalog.FlowMethod(node, port), kind, key, command, $"{name}: {what}", track);
        var wired = edges.Where(e => VisualNodeCatalog.Text(e, "kind") == "data" && VisualNodeCatalog.Text(e, "target") == id).Select(e => VisualNodeCatalog.Text(e, "targetPort")).ToHashSet(StringComparer.Ordinal);
        var arguments = config?["arguments"] as JsonObject;
        // Its Device pin is no state value (VisualNodeCatalog.StateInputs): a wire into it gives the node no state flow.
        if (VisualNodeCatalog.DataInputs(node, null, snapshot).Where(input => !VisualNodeCatalog.Structural(input.Value)).Any(input => wired.Contains(input.Key) || arguments?[input.Key] is JsonObject value && VisualNodeCatalog.Text(value, "kind") != "unset"))
            flows.Add(Flow("state", "state", null, "State", Recorded("state", false)));
        var started = edges.Where(e => VisualNodeCatalog.Text(e, "kind") != "data" && VisualNodeCatalog.Text(e, "source") == id).Select(e => VisualNodeCatalog.Text(e, "port", "next")).ToHashSet(StringComparer.Ordinal);
        foreach (var port in VisualNodeCatalog.ExecutionPorts(node, snapshot).Where(started.Contains))
            if (port.StartsWith("command:", StringComparison.Ordinal) && port["command:".Length..] is var command)
                flows.Add(Flow(port, "command", command, VisualNodeCatalog.Text(row?["commands"]?[command], "label", command), Recorded("commands", true)));
            else if (port == "frame")
                flows.Add(Flow(port, "frame", null, VisualNodeCatalog.Text(row?["frame"], "label", "When a frame is needed"), Recorded("frame", false)));
        return flows;
    }
}
