using System.Text.Json;
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
/// <param name="Port">The pin the flow starts at: <c>command:&lt;command&gt;</c>; <c>state</c> for its state and <c>frame</c> for its frames, which have no pin.</param>
/// <param name="Method">The flow's method (<see cref="VisualNodeCatalog.FlowMethod"/> of the node and <paramref name="Port"/>).</param>
/// <param name="Kind"><c>state</c>, <c>command</c> or <c>frame</c>.</param>
/// <param name="Key">The key of the node's entity.</param>
/// <param name="Command">The command of a <c>command</c> flow; null for the others.</param>
/// <param name="Label">The run's name: <c>{Name}: State</c>, <c>{Name}: {Command}</c> or <c>{Name}: When a frame is needed</c>.</param>
/// <param name="TrackHistory">Whether its runs are recorded, as the node's <c>history</c> switches say.</param>
internal sealed record HubFlow(string NodeId, string Port, string Method, string Kind, string Key, string? Command, string Label, bool TrackHistory);

/// <summary>
/// One C# entity or C# device node of a graph, as the host sets it up (<see cref="DefinitionGraph.Components"/>): what
/// its node says, and its pins resolved to values.
/// </summary>
/// <param name="NodeId">The node's id: <c>SharpNodes.Create</c> creates its class by it.</param>
/// <param name="Kind"><c>entity</c> for a C# entity, <c>device</c> for a C# device.</param>
/// <param name="Key">The node's <c>config.key</c>.</param>
/// <param name="Name">A C# device's <c>config.name</c>; null when it has none, and for a C# entity (its code names it).</param>
/// <param name="Via">A C# device's Part of; null for none, and for a C# entity.</param>
/// <param name="Device">The key of the Device node a C# entity is wired to; null when it stands alone, and for a C# device.</param>
/// <param name="Title">Its class's title, as its signature has it.</param>
/// <param name="Settings">Its pins resolved, in its class's order: a typed value as typed, a wired Setting as the
/// definition's saved value for its key; a pin that is unset, or whose value is none, is left out.</param>
internal sealed record DefinitionComponent(string NodeId, string Kind, string Key, string? Name, DefinitionVia? Via, string? Device, string Title, IReadOnlyList<DefinitionSetting> Settings)
{
    /// <summary>
    /// The labels of its class's required settings (the signature's <c>required</c>) that have no value in
    /// <see cref="Settings"/>, in its class's order, each its key where its label is blank; empty when none. The host
    /// refuses to set it up while one is listed.
    /// </summary>
    public IReadOnlyList<string> Unset { get; init; } = [];
    /// <summary>
    /// What its kind is called: its catalog entry's label (<c>C# entity</c>, <c>C# device</c>, or a library class's
    /// title); empty when its entry has none.
    /// </summary>
    public string KindLabel { get; init; } = "";
}

/// <summary>
/// Between a stored entity definition and the graph its nodes are checked, generated and run as: the stored graph never
/// has a <c>definition</c>; the document's settings are composed into one for the validator and the generator, and
/// taken out again before the graph is stored. Its entities are its Entity nodes, and its devices its Device nodes.
/// </summary>
internal static class DefinitionGraph
{
    /// <summary>The port of a camera's frame flow, which has no pin: its trace's <c>rootPort</c>.</summary>
    internal const string FrameFlow = "frame";
    /// <summary>What a frame flow's runs are named after the entity: <c>{Name}: When a frame is needed</c>.</summary>
    internal const string FrameLabel = "When a frame is needed";

    /// <summary>
    /// A copy of the definition's graph with <c>definition</c> = <c>{settings}</c>: each setting's key and type only. A
    /// setting's value, label and secret flag never reach the graph (nor the source it embeds in): a value may be secret,
    /// and changing one must not change the source. Its entities are the graph's Entity nodes, as
    /// <see cref="VisualNodeCatalog.WithGraphTypes"/> derives them, and an entity needs no name only when a Device node
    /// is wired to it. Any <c>definition</c> already in the graph is replaced. Without a graph, the definition alone.
    /// </summary>
    internal static JsonObject Compose(DefinitionBody body)
    {
        var graph = Decompose(body.Logic?.Graph ?? []);
        var definition = new JsonObject
        {
            ["settings"] = new JsonArray((body.Settings ?? []).Select(s => (JsonNode)new JsonObject { ["key"] = s?.Key, ["type"] = s?.Type }).ToArray()),
        };
        graph["definition"] = definition;
        return graph;
    }

    /// <summary>
    /// The entities of a composed graph (<see cref="Compose"/>), as a copy: the ones its Entity nodes declare
    /// (<see cref="VisualNodeCatalog.WithGraphTypes"/>), the first of each key, each with <c>device</c>: the key of the
    /// Device node it is on, or null. These are what the host builds as plain entities and what steps name: never a C#
    /// entity, which its class is (the host sets it up from <see cref="Components"/>), so that none is made twice.
    /// </summary>
    internal static JsonArray Entities(JsonObject composed, VisualCatalogSnapshot snapshot) =>
        new([.. VisualNodeCatalog.DefinitionItems(VisualNodeCatalog.WithGraphTypes(snapshot.Root, composed), "entities").Select(e => e.DeepClone())]);

    /// <summary>
    /// The entities a composed graph (<see cref="Compose"/>) declares, as its stored document and its answers list them,
    /// for reading only: its Entity nodes' (<see cref="Entities"/>), then each C# entity's
    /// (<see cref="VisualNodeCatalog.CSharpEntities"/>: <c>{key, domain, name: null, device, csharp: true}</c>), in node
    /// order, the first of each key, a key being text. What makes an entry a C# entity's is its node's kind alone: an
    /// Entity node's entry, a copy of its config, is listed without a <c>csharp</c> member its config may hold. The
    /// entities a C# device adds are not among them: its code adds them as it runs, and Home Assistant has them
    /// (<c>liveEntities</c>, <c>registeredEntities</c>). None past the catalog's limits.
    /// </summary>
    internal static JsonArray ListedEntities(JsonObject composed, VisualCatalogSnapshot snapshot)
    {
        var listed = Entities(composed, snapshot);
        foreach (var entity in listed) entity!.AsObject().Remove("csharp");
        if (VisualNodeCatalog.OverLimit(snapshot.Root, composed)) return listed;
        var keys = listed.Select(e => VisualNodeCatalog.Text(e, "key")).ToHashSet(StringComparer.Ordinal);
        foreach (var entity in VisualNodeCatalog.CSharpEntities(composed, snapshot.Root))
            if (entity["key"] is JsonValue value && value.TryGetValue<string>(out var key) && keys.Add(key)) listed.Add(entity);
        return listed;
    }

    /// <summary>
    /// The devices of a composed graph (<see cref="Compose"/>), as a copy: those its Device nodes declare, as
    /// <see cref="VisualNodeCatalog.WithGraphTypes"/> lists them (<see cref="VisualNodeCatalog.DerivedDevices"/>): each
    /// Device node's device members (<see cref="VisualNodeCatalog.DeclaredDevices"/>) in node order, then each C# device's,
    /// without a text left blank or a Part of of null; one without a key is left
    /// out, and two of one key are both listed. Empty for a graph that has none, or past the catalog's limits.
    /// </summary>
    internal static JsonArray Devices(JsonObject composed, JsonObject catalog) =>
        VisualNodeCatalog.WithGraphTypes(catalog, composed)["definition"]?["devices"] is JsonArray devices ? (JsonArray)devices.DeepClone() : [];

    /// <summary>
    /// The graph's C# entities and C# devices, for the host to set up, in node order: each well-formed node (the first of
    /// its id, as the validators read it) of a class node's kind (<see cref="VisualNodeCatalog.IsClassNode"/>) whose
    /// settings can be read (<see cref="VisualNodeCatalog.ClassSignature"/>). Each pin is resolved from what the graph and
    /// <paramref name="settings"/> say: the value wired to it when a Setting is (the definition's saved value for that
    /// Setting's key), else the value typed into it, never into a secret one (a secret is given by a Setting alone). An unset
    /// pin, a wire from anything else and a value of none (JSON null, a Setting not saved) leave the pin out: its property
    /// keeps what its class gives it; when its signature says it is <c>required</c>, its label is listed in
    /// <see cref="DefinitionComponent.Unset"/> instead. A typed value is the literal's <c>value</c>: an entity's is its id
    /// text. Nothing here goes into a source or an answer: values may be secret.
    /// </summary>
    /// <param name="graph">A composed graph (<see cref="Compose"/>), as previewed: its C# nodes' signatures are their code's.</param>
    /// <param name="settings">The definition's settings with their values; the first of each key counts.</param>
    internal static IReadOnlyList<DefinitionComponent> Components(JsonObject graph, VisualCatalogSnapshot snapshot, IReadOnlyList<DefinitionSetting> settings)
    {
        var nodes = VisualNodeCatalog.GraphNodes(graph);
        var wires = VisualNodeCatalog.ValueWires(graph, nodes);
        var saved = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var setting in settings) if (setting is { Key: { } key }) saved.TryAdd(key, setting.Value);
        static bool None(JsonNode? value) => value is null || value.GetValueKind() == System.Text.Json.JsonValueKind.Null;
        var components = new List<DefinitionComponent>();
        foreach (var raw in graph["nodes"] as JsonArray ?? [])
        {
            if (raw is not JsonObject node || !nodes.TryGetValue(VisualNodeCatalog.Text(node, "id"), out var first) || !ReferenceEquals(first, node)) continue;
            var spec = VisualNodeCatalog.Spec(node, snapshot);
            if (!VisualNodeCatalog.IsClassNode(spec) || VisualNodeCatalog.ClassSignature(node, spec) is not { } signature) continue;
            var id = VisualNodeCatalog.Text(node, "id");
            var config = (JsonObject)node["config"]!;
            var resolved = new List<DefinitionSetting>();
            var unset = new List<string>();
            foreach (var setting in signature["settings"]!.AsArray().OfType<JsonObject>())
            {
                var key = VisualNodeCatalog.Text(setting, "key");
                JsonNode? value = null;
                if (wires.Find(e => VisualNodeCatalog.Text(e, "target") == id && VisualNodeCatalog.Text(e, "targetPort") == key) is { } wire)
                {
                    var source = nodes[VisualNodeCatalog.Text(wire, "source")];
                    if (VisualNodeCatalog.Text(source, "kind") == "setting") value = saved.GetValueOrDefault(VisualNodeCatalog.Text(source["config"], "key"));
                }
                else if (setting["secret"] is not JsonValue secret || !secret.TryGetValue<bool>(out var hidden) || !hidden)
                    value = VisualNodeCatalog.InputValue(node, key, snapshot) is JsonObject typed && VisualNodeCatalog.Text(typed, "kind") == "literal" ? typed["value"] : null;
                if (!None(value)) resolved.Add(new(key, value!.DeepClone()));
                else if (setting["required"] is JsonValue required && required.TryGetValue<bool>(out var needed) && needed)
                    unset.Add(VisualNodeCatalog.Text(setting, "label") is var label && VisualGraphValidator.Words.Trimmed(label).Length > 0 ? label : key);
            }
            var title = VisualNodeCatalog.Text(signature, "title");
            if (VisualNodeCatalog.Declares(spec, "entity"))
            {
                var device = VisualNodeCatalog.DeviceOf(graph, id, snapshot.Root) is { } deviceNode ? VisualNodeCatalog.DeviceKey(nodes[deviceNode]["config"]) : null;
                components.Add(new(id, "entity", VisualNodeCatalog.Text(config, "key"), null, null, device, title, resolved) { Unset = unset, KindLabel = VisualNodeCatalog.Text(spec, "label") });
            }
            else
            {
                var name = VisualNodeCatalog.Text(config, "name") is var given && VisualGraphValidator.Words.Trimmed(given).Length > 0 ? given : null;
                DefinitionVia? via = null;
                try { via = config["via"]?.Deserialize<DefinitionVia>(VisualStore.Json); } catch (System.Text.Json.JsonException) { }
                components.Add(new(id, "device", VisualNodeCatalog.Text(config, "key"), name, via, null, title, resolved) { Unset = unset, KindLabel = VisualNodeCatalog.Text(spec, "label") });
            }
        }
        return components;
    }

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
        foreach (var node in roots.Where(n => VisualNodeCatalog.IsHub(VisualNodeCatalog.Spec(n, snapshot))))
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
    /// recorded unless <c>config.history.commands</c> is false; its frame, when a value wire goes into a picture it shows (a
    /// camera's Image, <see cref="VisualNodeCatalog.PictureInputs"/>), recorded when <c>config.history.frame</c> is true. None
    /// for a node of an unknown type, and for one that is no Entity node (<see cref="VisualNodeCatalog.IsHub"/>).
    /// </summary>
    internal static IReadOnlyList<HubFlow> HubFlows(JsonObject node, JsonObject graph, VisualCatalogSnapshot snapshot)
    {
        // Only an Entity node starts flows: a C# entity's code is its behaviour.
        if (!VisualNodeCatalog.IsHub(VisualNodeCatalog.Spec(node, snapshot))) return [];
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
        // Its Device pin and a picture it shows are no state values (VisualNodeCatalog.StateInputs): a wire into one gives the node no state flow.
        if (VisualNodeCatalog.StateInputs(node, snapshot).Any(input => wired.Contains(input.Key) || arguments?[input.Key] is JsonObject value && VisualNodeCatalog.Text(value, "kind") != "unset"))
            flows.Add(Flow("state", "state", null, "State", Recorded("state", false)));
        var started = edges.Where(e => VisualNodeCatalog.Text(e, "kind") != "data" && VisualNodeCatalog.Text(e, "source") == id).Select(e => VisualNodeCatalog.Text(e, "port", "next")).ToHashSet(StringComparer.Ordinal);
        foreach (var port in VisualNodeCatalog.ExecutionPorts(node, snapshot).Where(started.Contains))
            if (port.StartsWith("command:", StringComparison.Ordinal) && port["command:".Length..] is var command)
                flows.Add(Flow(port, "command", command, VisualNodeCatalog.Text(row?["commands"]?[command], "label", command), Recorded("commands", true)));
        // A camera makes its frame from what is wired into its Image: nothing wired, nothing to make.
        if (VisualNodeCatalog.PictureInputs(node, snapshot).Any(input => wired.Contains(input.Key)))
            flows.Add(Flow(FrameFlow, "frame", null, FrameLabel, Recorded("frame", false)));
        return flows;
    }
}
