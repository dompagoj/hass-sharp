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
            (nameof(ANameOfSpacesIsNoName), ANameOfSpacesIsNoName),
            (nameof(AGraphOfTheWrongShapeDeclaresNothing), AGraphOfTheWrongShapeDeclaresNothing),
            (nameof(TheNodeHasItsTypesPins), TheNodeHasItsTypesPins),
            (nameof(ACommandEntryThatIsNotAnObjectHasNoValues), ACommandEntryThatIsNotAnObjectHasNoValues),
            (nameof(FlowsAreNamedByNodeAndPort), FlowsAreNamedByNodeAndPort),
            (nameof(ANodeIsNamedByItsEntity), ANodeIsNamedByItsEntity),
            (nameof(TheOldStepsAreNotKinds), TheOldStepsAreNotKinds),
            (nameof(ADeviceNodeIsADeclaration), ADeviceNodeIsADeclaration),
            (nameof(DevicesAreDerivedFromTheirNodes), DevicesAreDerivedFromTheirNodes),
            (nameof(ADevicesProblemsAreOnItsNode), ADevicesProblemsAreOnItsNode) })
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

    /// <summary>
    /// A name that is blank once trimmed is no name: the entity it declares has none, so Home Assistant, the messages and
    /// the run names see none, and its runs are named by its key. A name with text keeps its spaces in the description.
    /// </summary>
    static void ANameOfSpacesIsNoName()
    {
        var blank = Hub("p", "power", "switch", new() { ["name"] = " \t ", ["arguments"] = new JsonObject { ["is_on"] = new JsonObject { ["kind"] = "literal", ["type"] = "boolean", ["value"] = true } } });
        var described = VisualNodeCatalog.EntityDescription(blank);
        Check(!described.ContainsKey("name") && described["key"]!.GetValue<string>() == "power", "a blank name is left out: " + described.ToJsonString());
        var graph = Graph(null, blank);
        Check(VisualNodeCatalog.DefinitionItems(VisualNodeCatalog.WithGraphTypes(VisualNodeCatalog.Root, graph), "entities").Single()["name"] == null, "the graph's entity has none");
        var flows = DefinitionGraph.HubFlows(blank, graph, VisualCatalogSnapshot.BuiltIn);
        Check(flows.Count > 0 && flows.All(f => f.Label.StartsWith("power: ", StringComparison.Ordinal)), "its runs are named by its key: " + string.Join(",", flows.Select(f => f.Label)));
        Check(VisualNodeCatalog.EntityDescription(Hub("p", "power", "switch", new() { ["name"] = " Power " }))["name"]!.GetValue<string>() == " Power ", "a name with text is kept as given");
        // Only a text is trimmed: a name of another type is the validator's to refuse.
        Check(VisualNodeCatalog.EntityDescription(Hub("p", "power", "switch", new() { ["name"] = 5 }))["name"]!.GetValue<int>() == 5, "a name that is not text is kept");
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
        Check(string.Join(",", VisualNodeCatalog.DataInputs(light).Select(p => p.Key)) == "device,is_on,brightness", "a light's inputs are its Device pin and its state values");
        Check(string.Join(",", VisualNodeCatalog.StateInputs(light, VisualNodeCatalog.Root).Select(p => p.Key)) == "is_on,brightness", "a light's state values are its inputs but Device");
        Check(VisualNodeCatalog.DataInputs(light).All(p => p.Value?["required"]?.GetValue<bool>() == false), "every state input is optional as a pin");
        Check(string.Join(",", VisualNodeCatalog.ExecutionPorts(light)) == "command:turn_on,command:turn_off", "one execution pin per command");
        var outputs = VisualNodeCatalog.DataOutputs(light);
        Check(string.Join(",", outputs.Select(p => p.Key)) == "command:turn_on:brightness", "a command's values are outputs");
        Check(outputs["command:turn_on:brightness"]!["pointer"]!.GetValue<string>() == "/data/brightness" && outputs["command:turn_on:brightness"]!["group"]!.GetValue<string>() == "command:turn_on", "an output points into its command's data and is grouped under it");
        Check(string.Join(",", VisualNodeCatalog.ExecutionPorts(Hub("c", "cam", "camera"))) == "frame", "a camera has the frame pin");
        Check(VisualNodeCatalog.ExecutionPorts(Hub("s", "t", "sensor")).Length == 0 && VisualNodeCatalog.DataOutputs(Hub("s", "t", "sensor")).Count == 0, "a sensor has inputs only");
        var unknown = Hub("u", "u", "nothing");
        Check(string.Join(",", VisualNodeCatalog.DataInputs(unknown).Select(p => p.Key)) == "device" && !VisualNodeCatalog.StateInputs(unknown, VisualNodeCatalog.Root).Any() && VisualNodeCatalog.ExecutionPorts(unknown).Length == 0, "an unknown type has no pins but Device");
        Check(VisualNodeCatalog.StateInputs(Hub("d", "", "light"), VisualNodeCatalog.Root).Count() == 2, "the pins do not wait for a key");
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

    /// <summary>State, When commanded and When a frame is needed are not kinds any more, and their problems are gone with them.</summary>
    static void TheOldStepsAreNotKinds()
    {
        foreach (var kind in new[] { "entity_state", "entity_command", "camera_frame" })
            Check(VisualNodeCatalog.GetNode(kind) == null, kind + " is not in the catalog");
        foreach (var code in new[] { "state.duplicate", "command.duplicate", "camera.duplicate" })
            Check(!VisualGraphValidator.Codes.Contains(code), code + " is not a problem any more");
    }

    /// <summary>
    /// A Device node declares a device and is neither a step nor a value; its wire is of a structural type, which no input
    /// open to every type lists. An Entity node's Device input is one of its inputs but not one of its state values.
    /// </summary>
    static void ADeviceNodeIsADeclaration()
    {
        var catalog = VisualNodeCatalog.Root;
        var device = VisualNodeCatalog.GetNode("device")!;
        Check(VisualNodeCatalog.Declaration(device) && VisualNodeCatalog.Declares(device, "device"), "the Device kind declares a device");
        Check(!VisualNodeCatalog.Declaration(VisualNodeCatalog.GetNode("entity")) && !VisualNodeCatalog.Declaration(VisualNodeCatalog.GetNode("log")), "an Entity node and a step are no declaration");
        Check(VisualNodeCatalog.Structural(catalog["types"]!["device"]), "the device type is structural");
        Check(!VisualNodeCatalog.Structural(catalog["types"]!["image"]) && !VisualNodeCatalog.Structural(null), "another type is not");
        Check(VisualNodeCatalog.Structural(VisualNodeCatalog.GetNode("entity")!["inputs"]!["device"]), "the Entity node's Device input is structural");
        var node = N("plug", "device", new { key = "plug", name = "Hall plug" });
        Check(VisualNodeCatalog.ExecutionPorts(node).Length == 0, "a Device node has no execution pins");
        Check(string.Join(",", VisualNodeCatalog.DataOutputs(node).Select(p => p.Key + ":" + p.Value!["type"])) == "entities:device", "its one output is Entities, of type device");
        Check(VisualGraphValidator.Words.StepName(node, device) == "Hall plug", "it is named by its name: " + VisualGraphValidator.Words.StepName(node, device));
        // No input open to every type lists the device type: a Log's message, an object's field, a list's item.
        static string[] Types(JsonObject inputs, string port) => inputs[port]!["types"]!.AsArray().Select(t => t!.GetValue<string>()).ToArray();
        var message = Types(VisualNodeCatalog.DataInputs(N("l", "log")), "value");
        Check(message.Contains("image") && !message.Contains("device"), "an input of any type lists every type but device: " + string.Join(",", message));
        Check(!Types(VisualNodeCatalog.DataInputs(N("o", "object_create", new { fields = new { room = new { kind = "unset" } } })), "field:room").Contains("device"), "an object's field does not take a device");
        Check(!Types(VisualNodeCatalog.DataInputs(N("a", "array_create", new { items = new[] { new { kind = "unset" } } })), "item:0").Contains("device"), "a list's item does not take a device");
        Check(!Types(VisualNodeCatalog.DataInputs(N("s", "service", new { service = "light.turn_on", fields = new { anything = new { kind = "unset" } } })), "field:anything").Contains("device"), "an unknown service field does not take a device");
        var relay = Hub("r", "relay", "switch");
        Check(string.Join(",", VisualNodeCatalog.StateInputs(relay, catalog).Select(p => p.Key)) == "is_on", "a switch's state values are On alone");
        Check(string.Join(",", VisualNodeCatalog.DataInputs(relay).Select(p => p.Key)) == "device,is_on", "its inputs also have Device");
        var group = catalog["nodes"]!.AsObject().Where(n => VisualNodeCatalog.Text(n.Value, "category") == "Entity").Select(n => n.Key).Take(3);
        Check(string.Join(",", group) == "entity,device,entity_setup", "the catalog's Entity group begins Entity, Device, When started: " + string.Join(",", group));
    }

    /// <summary>
    /// A definition's devices are its Device nodes, in node order, each its node's config; an entity is on the Device node
    /// wired into its Device pin, and on none when nothing or something else is wired there.
    /// </summary>
    static void DevicesAreDerivedFromTheirNodes()
    {
        var graph = Graph(null,
            N("plug", "device", new { key = "plug", name = "Hall plug", model = "P1" }), Hub("relay", "relay", "switch"), Hub("power", "power", "sensor"),
            N("hub", "device", new { key = "hub", name = "Hub", via = new { deviceId = "zigbee" } }), Hub("bare", "bare", "switch"),
            Hub("stray", "stray", "binary_sensor"), N("c", "constant", new { type = "string", value = new { kind = "literal", type = "string", value = "plug" } }));
        graph["edges"] = new JsonArray(D("plug", "relay", "device", "entities"), D("plug", "power", "device", "entities"), D("c", "stray", "device"));
        var catalog = VisualNodeCatalog.Root;
        var devices = VisualNodeCatalog.DeclaredDevices(graph, catalog).ToList();
        Check(string.Join(",", devices.Select(d => d.NodeId + "=" + d.Device["key"])) == "plug=plug,hub=hub", "the Device nodes in node order: " + string.Join(",", devices.Select(d => d.NodeId)));
        Check(devices[0].Device["model"]!.GetValue<string>() == "P1" && devices[1].Device["via"]!["deviceId"]!.GetValue<string>() == "zigbee", "each device is its node's config");
        Check(!ReferenceEquals(devices[0].Device, graph["nodes"]![0]!["config"]), "a copy of it, not the node's own");
        Check(VisualNodeCatalog.DeviceOf(graph, "relay", catalog) == "plug" && VisualNodeCatalog.DeviceOf(graph, "power", catalog) == "plug", "a wired entity is on its Device node");
        Check(VisualNodeCatalog.DeviceOf(graph, "bare", catalog) == null, "an entity without the wire is on none");
        Check(VisualNodeCatalog.DeviceOf(graph, "stray", catalog) == null, "nor is one whose Device pin is wired from a node that is not a Device node");
        Check(VisualNodeCatalog.DeviceOf(graph, "missing", catalog) == null && VisualNodeCatalog.DeviceOf(graph, "plug", catalog) == null, "an id that is no Entity node is on none");
        Check(!VisualNodeCatalog.DeclaredDevices(new JsonObject { ["nodes"] = 3 }, catalog).Any(), "a graph of the wrong shape declares no device");
    }

    /// <summary>
    /// A Device node is named by its name, else its key, else Device; its problems are on it, each on the setting it is
    /// about, and a device with no entities is a warning on the node as a whole. A device of a circle of Part of has it on each.
    /// </summary>
    static void ADevicesProblemsAreOnItsNode()
    {
        var spec = VisualNodeCatalog.GetNode("device");
        Check(VisualGraphValidator.Words.StepName(N("d", "device", new { key = " desk ", name = " " }), spec) == "desk", "a device without a name is named by its key, trimmed");
        Check(VisualGraphValidator.Words.StepName(N("d", "device", new { key = "", name = "" }), spec) == "Device", "else Device");
        var graph = Graph(null,
            N("plug", "device", new { key = "plug", name = "Hall plug", via = new { device = "hub" } }), N("hub", "device", new { key = "hub", name = "Hub", via = new { device = "plug" } }),
            N("desk", "device", new { key = "desk", name = "Desk" }), Hub("relay", "relay", "switch"), Hub("lamp", "lamp", "switch"));
        graph["edges"] = new JsonArray(D("plug", "relay", "device", "entities"), D("hub", "lamp", "device", "entities"));
        var found = VisualGraphValidator.Validate(graph, VisualCatalogSnapshot.BuiltIn).Where(d => d.Code is "device.viaCycle" or "device.empty").ToList();
        string Of(VisualDiagnostic d) => $"{d.Code}@{d.NodeId}:{d.InputId}:{d.PortId}:{d.Severity}";
        Check(string.Join(",", found.Select(Of)) == "device.viaCycle@plug:via::3,device.viaCycle@hub:via::3,device.empty@desk:::2",
            "each device of the circle on its Part of, an empty one on the node as a warning: " + string.Join(",", found.Select(Of)));
    }
}
