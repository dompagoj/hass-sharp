using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>
/// An Entity node declares one entity of its definition: its settings, without its run history and state values, are
/// the entity's description, and the catalog of its graph lists the entities its nodes declare and no other (a list in
/// the graph's definition is ignored). A graph of the wrong shape declares nothing.
/// </summary>
static class EntityNodeTests
{
    [RuntimeSuite("--entity-nodes", Order = 11)]
    public static Task Run()
    {
        var failures = new List<string>();
        // Every test runs, and each failure is reported under its name.
        foreach (var (name, test) in new (string, Action)[] {
            (nameof(NodesDeclareEntities), NodesDeclareEntities),
            (nameof(AListOnTheGraphIsIgnored), AListOnTheGraphIsIgnored),
            (nameof(AGraphOfTheWrongShapeDeclaresNothing), AGraphOfTheWrongShapeDeclaresNothing),
            (nameof(TheNodeHasItsTypesPins), TheNodeHasItsTypesPins),
            (nameof(ACommandEntryThatIsNotAnObjectHasNoValues), ACommandEntryThatIsNotAnObjectHasNoValues),
            (nameof(FlowsAreNamedByNodeAndPort), FlowsAreNamedByNodeAndPort),
            (nameof(ANodeIsNamedByItsEntity), ANodeIsNamedByItsEntity) })
        {
            try
            {
                test();
                Console.WriteLine($"PASS entity nodes: {name}");
            }
            catch (Exception e) { failures.Add($"{name}: {e.Message}"); }
        }
        if (failures.Count > 0) throw new Exception($"Entity nodes: {failures.Count} failing:\n" + string.Join("\n", failures));
        return Task.CompletedTask;
    }

    static JsonObject Graph(JsonArray? listed, params JsonObject[] nodes)
    {
        var definition = new JsonObject { ["settings"] = new JsonArray() };
        if (listed != null) definition["entities"] = listed;
        return new JsonObject { ["version"] = 5, ["name"] = "Nodes", ["nodes"] = new JsonArray(nodes), ["edges"] = new JsonArray(), ["definition"] = definition };
    }

    static void NodesDeclareEntities()
    {
        var graph = Graph(null, Hub("a", "door", "binary_sensor"), Hub("b", "power", "sensor", new() { ["options"] = new JsonObject { ["unit"] = "W" } }));
        var declared = VisualNodeCatalog.DeclaredEntities(graph, VisualNodeCatalog.Root).ToList();
        Check(string.Join(",", declared.Select(d => d.NodeId + "=" + d.Entity["key"])) == "a=door,b=power", "nodes declare entities in node order");
        Check(declared[1].Entity["options"]!["unit"]!.GetValue<string>() == "W" && declared[1].Entity["history"] == null && declared[1].Entity["arguments"] == null,
            "an entity's description is its node's settings without history and arguments");
        var merged = VisualNodeCatalog.WithGraphTypes(VisualNodeCatalog.Root, graph);
        Check(string.Join(",", VisualNodeCatalog.DefinitionItems(merged, "entities").Select(e => e["key"])) == "door,power", "the catalog of the graph lists them");
    }

    static void AListOnTheGraphIsIgnored()
    {
        var listed = new JsonArray(new JsonObject { ["key"] = "old", ["domain"] = "switch" }, new JsonObject { ["key"] = "door", ["domain"] = "switch" });
        var merged = VisualNodeCatalog.WithGraphTypes(VisualNodeCatalog.Root, Graph(listed, Hub("a", "door", "binary_sensor")));
        var entities = VisualNodeCatalog.DefinitionItems(merged, "entities");
        Check(string.Join(",", entities.Select(e => e["key"] + ":" + e["domain"])) == "door:binary_sensor", "only the node's entity: " + string.Join(",", entities.Select(e => e["key"] + ":" + e["domain"])));
        var none = VisualNodeCatalog.WithGraphTypes(VisualNodeCatalog.Root, Graph(listed.DeepClone().AsArray()));
        Check(VisualNodeCatalog.DefinitionItems(none, "entities").Count == 0, "a graph of no Entity node has no entity, whatever its definition lists");
    }

    static void AGraphOfTheWrongShapeDeclaresNothing()
    {
        foreach (var graph in new JsonObject[] { new(), new() { ["nodes"] = 3, ["definition"] = new JsonObject() }, new() { ["nodes"] = new JsonArray(1, "x", new JsonObject { ["kind"] = "entity", ["config"] = 5 }), ["definition"] = new JsonObject() } })
            Check(VisualNodeCatalog.DeclaredEntities(graph, VisualNodeCatalog.Root).All(d => d.Entity.Count == 0), "no entity is read from a malformed graph");
    }

    static void TheNodeHasItsTypesPins()
    {
        var light = Hub("l", "lamp", "light");
        Check(string.Join(",", VisualNodeCatalog.DataInputs(light).Select(p => p.Key)) == "is_on,brightness", "a light's state values are its inputs");
        Check(VisualNodeCatalog.DataInputs(light).All(p => p.Value?["required"]?.GetValue<bool>() == false), "every state input is optional as a pin");
        Check(string.Join(",", VisualNodeCatalog.ExecutionPorts(light)) == "command:turn_on,command:turn_off", "one execution pin per command");
        var outputs = VisualNodeCatalog.DataOutputs(light);
        Check(string.Join(",", outputs.Select(p => p.Key)) == "command:turn_on:brightness", "a command's values are outputs");
        Check(outputs["command:turn_on:brightness"]!["pointer"]!.GetValue<string>() == "/data/brightness" && outputs["command:turn_on:brightness"]!["group"]!.GetValue<string>() == "command:turn_on", "an output points into its command's data and is grouped under it");
        Check(string.Join(",", VisualNodeCatalog.ExecutionPorts(Hub("c", "cam", "camera"))) == "frame", "a camera has the frame pin");
        Check(VisualNodeCatalog.ExecutionPorts(Hub("s", "t", "sensor")).Length == 0 && VisualNodeCatalog.DataOutputs(Hub("s", "t", "sensor")).Count == 0, "a sensor has inputs only");
        var unknown = Hub("u", "u", "nothing");
        Check(VisualNodeCatalog.DataInputs(unknown).Count == 0 && VisualNodeCatalog.ExecutionPorts(unknown).Length == 0, "an unknown type has no pins");
        Check(VisualNodeCatalog.DataInputs(Hub("d", "", "light")).Count == 2, "the pins do not wait for a key");
    }

    /// <summary>A command entry of a catalog that is not an object gives its Entity node no values, as in TypeScript, rather than failing.</summary>
    static void ACommandEntryThatIsNotAnObjectHasNoValues()
    {
        var catalog = (JsonObject)VisualNodeCatalog.Root.DeepClone();
        catalog["entityDomains"]!["light"]!["commands"]!["turn_on"] = 5;
        Check(VisualNodeCatalog.DataOutputs(Hub("l", "lamp", "light"), catalog).Count == 0, "a command entry that is not an object has no values");
    }

    /// <summary>
    /// A declaring node is named by its entity in the validator's messages and in its runs' names: its name, else its
    /// key, each trimmed, else its kind's label; never by a label of its own.
    /// </summary>
    static void ANodeIsNamedByItsEntity()
    {
        var snapshot = VisualCatalogSnapshot.BuiltIn;
        string Named(JsonObject node) => VisualGraphValidator.Words.StepName(node, VisualNodeCatalog.Spec(node, snapshot));
        Check(Named(Hub("l", "lamp", "light", new() { ["name"] = "  Lamp " })) == "Lamp", "by its name, trimmed");
        Check(Named(Hub("l", " lamp ", "light", new() { ["name"] = " " })) == "lamp", "else by its key");
        var bare = Hub("l", "", "light", new() { ["name"] = "" });
        bare["label"] = "Something else";
        Check(Named(bare) == "Entity", "else by its kind's label, not its own: " + Named(bare));
        var hub = Hub("l", "lamp", "light", new() { ["name"] = "  Lamp ", ["arguments"] = new JsonObject { ["is_on"] = new JsonObject { ["kind"] = "literal", ["type"] = "boolean", ["value"] = true } } });
        var flows = DefinitionGraph.HubFlows(hub, Graph(null, hub), snapshot);
        Check(string.Join(",", flows.Select(f => f.Label)) == "Lamp: State", "its runs are named the same: " + string.Join(",", flows.Select(f => f.Label)));
    }

    /// <summary>One Entity node starts several flows, so a flow's method is named by the node and its port.</summary>
    static void FlowsAreNamedByNodeAndPort()
    {
        var hub = Hub("l", "lamp", "light");
        Check(VisualNodeCatalog.FlowMethod(hub, "state") == "Run_6C_7374617465", "a flow's method is the node's and the port's, in hex: " + VisualNodeCatalog.FlowMethod(hub, "state"));
        Check(VisualNodeCatalog.FlowMethod(hub, "command:turn_on") != VisualNodeCatalog.FlowMethod(hub, "command:turn_off"), "each port has its own");
        Check(VisualNodeCatalog.FlowMethod(hub, "state").StartsWith(VisualNodeCatalog.EffectiveTriggerMethod(hub) + "_", StringComparison.Ordinal), "a flow's method starts with its node's");
    }
}
