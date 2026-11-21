using System.Text.Json;
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
            (nameof(ADevicesProblemsAreOnItsNode), ADevicesProblemsAreOnItsNode),
            (nameof(ACSharpNodeIsNoHub), ACSharpNodeIsNoHub),
            (nameof(NothingDerivedCarriesCode), NothingDerivedCarriesCode),
            (nameof(EveryDeclaringKindIsDerivedOnPurpose), EveryDeclaringKindIsDerivedOnPurpose),
            (nameof(TheHostGetsACSharpDeviceAsAComponent), TheHostGetsACSharpDeviceAsAComponent),
            (nameof(ADeviceIsADeviceByItsKind), ADeviceIsADeviceByItsKind),
            (nameof(TheRuntimeNeverWalksACSharpNode), TheRuntimeNeverWalksACSharpNode),
            (nameof(ASettingTakesATypedValueOrASettingNode), ASettingTakesATypedValueOrASettingNode) })
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
        var camera = Hub("c", "cam", "camera");
        Check(VisualNodeCatalog.ExecutionPorts(camera).Length == 0 && string.Join(",", VisualNodeCatalog.DataInputs(camera).Select(p => p.Key)) == "device,image"
            && !VisualNodeCatalog.StateInputs(camera, VisualNodeCatalog.Root).Any() && string.Join(",", VisualNodeCatalog.PictureInputs(camera, VisualCatalogSnapshot.BuiltIn).Select(p => p.Key)) == "image",
            "a camera has no execution pin, and its Image input is its picture, not a state value");
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

    /// <summary>
    /// State, When commanded, When a frame is needed and Show frame are not kinds any more, and their problems are gone
    /// with them; the kind When a frame is needed had, camera_frame, is now Camera frame, a value.
    /// </summary>
    static void TheOldStepsAreNotKinds()
    {
        foreach (var kind in new[] { "entity_state", "entity_command", "show_frame" })
            Check(VisualNodeCatalog.GetNode(kind) == null, kind + " is not in the catalog");
        Check(VisualNodeCatalog.GetNode("camera_frame") is { } frame && frame["pure"]?.GetValue<bool>() == true && frame["label"]?.GetValue<string>() == "Camera frame" && frame["execution"] == null,
            "camera_frame is Camera frame, a value with no execution pins");
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
        var group = catalog["nodes"]!.AsObject().Where(n => VisualNodeCatalog.Text(n.Value, "category") == "Entity").Select(n => n.Key).Take(5);
        Check(string.Join(",", group) == "entity,device,csharp_entity,csharp_device,entity_setup", "the catalog's Entity group begins Entity, Device, C# entity, C# device, When started: " + string.Join(",", group));
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

        // The definition's devices, and each entity's device, as a stored document and the graph's catalog list them.
        var derived = DefinitionGraph.Devices(graph, catalog);
        Check(JsonNode.DeepEquals(derived, JsonNode.Parse("""[{"key":"plug","name":"Hall plug","model":"P1"},{"key":"hub","name":"Hub","via":{"deviceId":"zigbee"}}]""")),
            "DefinitionGraph.Devices lists each Device node's config in node order: " + derived.ToJsonString());
        Check(!ReferenceEquals(derived[0], graph["nodes"]![0]!["config"]), "as a copy");
        var lifted = VisualNodeCatalog.WithGraphTypes(catalog, graph)["definition"]!;
        Check(JsonNode.DeepEquals(lifted["devices"], derived), "the graph's catalog lists the same devices: " + lifted["devices"]?.ToJsonString());
        var entities = DefinitionGraph.Entities(graph, VisualCatalogSnapshot.BuiltIn);
        string Devices(JsonArray list) => string.Join(",", list.Select(e => e!["key"] + "=" + (e.AsObject().TryGetPropertyValue("device", out var device) ? device?.ToJsonString() ?? "null" : "absent")));
        Check(Devices(entities) == "relay=\"plug\",power=\"plug\",bare=null,stray=null", "each entity names the key of its device, or null: " + Devices(entities));
        Check(Devices(lifted["entities"]!.AsArray()) == Devices(entities), "the graph's catalog alike: " + Devices(lifted["entities"]!.AsArray()));

        // A text left blank and Part of nothing are left out; a device with no usable key is no device, and an entity on it is on none.
        var odd = Graph(null,
            N("a", "device", new { key = "desk", name = "Desk", manufacturer = " \t ", model = "", via = (object?)null }),
            N("b", "device", new { key = "  ", name = "Blank" }), N("c", "device", new { key = 5, name = "Number" }), N("d", "device", new { name = "None" }),
            N("e", "device", new { key = "desk", name = "Second desk", via = new { device = "desk" } }),
            Hub("on_blank", "on_blank", "switch"), Hub("on_number", "on_number", "switch"), Hub("on_second", "on_second", "switch"));
        odd["edges"] = new JsonArray(D("b", "on_blank", "device", "entities"), D("c", "on_number", "device", "entities"), D("e", "on_second", "device", "entities"));
        Check(JsonNode.DeepEquals(DefinitionGraph.Devices(odd, catalog), JsonNode.Parse("""[{"key":"desk","name":"Desk"},{"key":"desk","name":"Second desk","via":{"device":"desk"}}]""")),
            "blank texts and a null Part of are left out, a device without a usable key too, and two of one key are both listed: " + DefinitionGraph.Devices(odd, catalog).ToJsonString());
        Check(Devices(DefinitionGraph.Entities(odd, VisualCatalogSnapshot.BuiltIn)) == "on_blank=null,on_number=null,on_second=\"desk\"",
            "an entity on a device without a usable key is on none: " + Devices(DefinitionGraph.Entities(odd, VisualCatalogSnapshot.BuiltIn)));
        Check(DefinitionGraph.Devices(new JsonObject { ["nodes"] = 3, ["definition"] = new JsonObject() }, catalog).Count == 0, "a graph of the wrong shape has no devices");

        // A listed device is what a definition's device is read as: its key and name, and Part of another device of this definition.
        var plug = derived[0].Deserialize<DefinitionDevice>(VisualStore.Json)!;
        var hub = derived[1].Deserialize<DefinitionDevice>(VisualStore.Json)!;
        var second = DefinitionGraph.Devices(odd, catalog)[1].Deserialize<DefinitionDevice>(VisualStore.Json)!;
        Check(plug is { Key: "plug", Name: "Hall plug", Model: "P1", Via: null } && hub is { Key: "hub", Via: { DeviceId: "zigbee", Device: null } }
            && second is { Key: "desk", Via: { Device: "desk", DeviceId: null } }, "a listed device reads as a DefinitionDevice with its key, name and Part of");
        // A device with no key, name or Part of another device, as a request may give one, writes none of them.
        Check(JsonSerializer.Serialize(new DefinitionDevice("Shelly", null, null, null, null, null, new DefinitionVia("hub")), VisualStore.Json)
            == """{"manufacturer":"Shelly","model":null,"swVersion":null,"hwVersion":null,"configurationUrl":null,"suggestedArea":null,"via":{"deviceId":"hub"}}""",
            "a device without a key, name or device of this definition writes none of them");
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

    /// <summary>A node of <paramref name="kind"/> as the editor drops one: its kind's defaults (its template's code and settings), then <paramref name="config"/>; no label.</summary>
    internal static JsonObject Templated(string id, string kind, JsonObject config)
    {
        var node = VisualGraphContract.CreateNode(kind, id);
        node.Remove("label");
        foreach (var (name, value) in config) node["config"]![name] = value?.DeepClone();
        return node;
    }
    /// <summary>A C# entity node of the key, its code and settings its kind's template's (a Thermometer, a sensor with an Address setting).</summary>
    internal static JsonObject CSharpEntity(string id, string key, JsonObject? more = null)
    {
        var config = new JsonObject { ["key"] = key };
        foreach (var (name, value) in more ?? []) config[name] = value?.DeepClone();
        return Templated(id, "csharp_entity", config);
    }
    /// <summary>A C# device node of the key and name, its code and settings its kind's template's (a Plug with an Address setting and a relay).</summary>
    internal static JsonObject CSharpDevice(string id, string key, string name, JsonObject? more = null)
    {
        var config = new JsonObject { ["key"] = key, ["name"] = name };
        foreach (var (name2, value) in more ?? []) config[name2] = value?.DeepClone();
        return Templated(id, "csharp_device", config);
    }

    /// <summary>
    /// A C# entity and a C# device are declarations with code: no rule that is an Entity node's alone (a hub's) takes
    /// them for one. The flows and roots of a graph with a hub and both are the hub's; its entities are the hub's; the C#
    /// entity's pins are its Device pin and then its settings; and the C# device is among the graph's devices.
    /// </summary>
    static void ACSharpNodeIsNoHub()
    {
        var snapshot = VisualCatalogSnapshot.BuiltIn;
        var address = new JsonObject { ["Address"] = Lit("string", "http://192.168.1.60/temperature") };
        var lamp = Hub("lamp", "lamp", "light", values: new JsonObject { ["is_on"] = Lit("boolean", true) });
        var thermo = CSharpEntity("thermo", "thermometer", new() { ["arguments"] = address.DeepClone() });
        var plug = CSharpDevice("plug", "plug", "Plug", new() { ["arguments"] = address.DeepClone(), ["via"] = null });
        var graph = Graph(null, lamp, thermo, plug);
        graph["edges"] = new JsonArray(Flow("lamp", "command:turn_on", "log"));
        graph["nodes"]!.AsArray().Add(N("log", "log", new { value = Lit("string", "On") }));

        Check(VisualNodeCatalog.IsHub(VisualNodeCatalog.GetNode("entity")) && !VisualNodeCatalog.IsHub(VisualNodeCatalog.GetNode("csharp_entity"))
            && !VisualNodeCatalog.IsHub(VisualNodeCatalog.GetNode("csharp_device")) && !VisualNodeCatalog.IsHub(VisualNodeCatalog.GetNode("device")), "only the Entity node is a hub");
        Check(VisualNodeCatalog.IsClassNode(VisualNodeCatalog.GetNode("csharp_entity")) && VisualNodeCatalog.IsClassNode(VisualNodeCatalog.GetNode("csharp_device"))
            && !VisualNodeCatalog.IsClassNode(VisualNodeCatalog.GetNode("custom")) && !VisualNodeCatalog.IsClassNode(VisualNodeCatalog.GetNode("entity")), "the two kinds are class nodes, a Custom C# step is not");
        Check(VisualNodeCatalog.HasCode(VisualNodeCatalog.GetNode("custom")) && VisualNodeCatalog.HasCode(VisualNodeCatalog.GetNode("csharp_entity"))
            && VisualNodeCatalog.HasCode(VisualNodeCatalog.GetNode("csharp_device")) && !VisualNodeCatalog.HasCode(VisualNodeCatalog.GetNode("device")), "each of the three holds code");
        foreach (var node in new[] { thermo, plug })
        {
            var spec = VisualNodeCatalog.Spec(node);
            Check(VisualNodeCatalog.Declaration(spec) && !VisualNodeCatalog.IsRoot(node, snapshot) && !VisualNodeCatalog.IsPure(node, spec), VisualNodeCatalog.Text(node, "id") + " is a declaration, no root and no value");
            Check(VisualNodeCatalog.ExecutionPorts(node).Length == 0 && VisualNodeCatalog.DataOutputs(node).Count == 0, VisualNodeCatalog.Text(node, "id") + " has no execution pins and no outputs");
            // Even with a value given to a setting, it starts no flow: it is no hub.
            Check(DefinitionGraph.HubFlows(node, graph, snapshot).Count == 0, VisualNodeCatalog.Text(node, "id") + " has no flows of a hub");
            Check(VisualNodeCatalog.CustomSignature(node, spec) == null, VisualNodeCatalog.Text(node, "id") + " has no Custom C# signature");
        }

        var roots = DefinitionGraph.Roots(graph, snapshot);
        Check(roots.Setup == null && roots.States.SequenceEqual([VisualNodeCatalog.FlowMethod(lamp, "state")]) && string.Join(",", roots.StateEntities.Values) == "lamp"
            && string.Join(",", roots.Commands.Keys) == "lamp:turn_on" && roots.Frames.Count == 0, "the roots are the hub's flows alone: " + string.Join(",", roots.States.Concat(roots.Commands.Keys)));
        Check(string.Join(",", DefinitionGraph.Entities(graph, snapshot).Select(e => e!["key"])) == "lamp", "the graph's entities are the hub's alone: " + DefinitionGraph.Entities(graph, snapshot).ToJsonString());
        Check(string.Join(",", VisualNodeCatalog.DeclaredEntities(graph, VisualNodeCatalog.Root).Select(d => d.NodeId)) == "lamp", "a C# entity is in no derived entity list yet");

        var inputs = VisualNodeCatalog.DataInputs(thermo);
        Check(string.Join(",", inputs.Select(p => p.Key)) == "device,Address", "the C# entity's pins are Device, then its settings: " + string.Join(",", inputs.Select(p => p.Key)));
        Check(VisualNodeCatalog.Structural(inputs["device"]) && JsonNode.DeepEquals(inputs["device"], VisualNodeCatalog.GetNode("entity")!["inputs"]!["device"]), "its Device pin is the Entity node's");
        Check(JsonNode.DeepEquals(inputs["Address"], JsonNode.Parse("""{"types":["string"],"required":false,"label":"Address","description":"Where the thermometer answers with the temperature in degrees Celsius, such as http://192.168.1.60/temperature.","setting":{"type":"string","secret":false}}""")),
            "a setting pin is optional, of its setting's pin type, with the setting itself: " + inputs["Address"]!.ToJsonString());
        Check(string.Join(",", VisualNodeCatalog.DataInputs(plug).Select(p => p.Key)) == "Address", "the C# device's pins are its settings alone");
        // Each setting type takes the catalog type of its pin, and keeps its options and range for the editor.
        var typed = CSharpEntity("typed", "typed", new()
        {
            ["signature"] = JsonNode.Parse("""
                {"title":"Typed","description":"","domain":"sensor","settings":[
                {"key":"Mode","label":"Mode","description":"","type":"enum","secret":false,"options":["Fast","Slow"]},
                {"key":"Count","label":"Count","description":"","type":"integer","secret":false,"minimum":1,"maximum":10},
                {"key":"Level","label":"Level","description":"","type":"number","secret":false},
                {"key":"On","label":"On","description":"","type":"boolean","secret":false},
                {"key":"Token","label":"Token","description":"","type":"string","secret":true}]}
                """),
        });
        Check(string.Join(",", VisualNodeCatalog.DataInputs(typed).Skip(1).Select(p => p.Key + ":" + p.Value!["types"]![0])) == "Mode:string,Count:number,Level:number,On:boolean,Token:string",
            "enum and string take text, integer and number a number, boolean true or false: " + VisualNodeCatalog.DataInputs(typed).ToJsonString());
        Check(JsonNode.DeepEquals(VisualNodeCatalog.DataInputs(typed)["Mode"]!["setting"], JsonNode.Parse("""{"type":"enum","secret":false,"options":["Fast","Slow"]}"""))
            && JsonNode.DeepEquals(VisualNodeCatalog.DataInputs(typed)["Count"]!["setting"], JsonNode.Parse("""{"type":"integer","secret":false,"minimum":1,"maximum":10}"""))
            && VisualNodeCatalog.DataInputs(typed)["Token"]!["setting"]!["secret"]!.GetValue<bool>(), "a pin carries its setting's options, range and secret flag");
        // A signature that cannot be read gives the static pins alone.
        var unreadable = CSharpEntity("bad", "bad", new() { ["signature"] = new JsonObject { ["title"] = 5 } });
        Check(string.Join(",", VisualNodeCatalog.DataInputs(unreadable).Select(p => p.Key)) == "device" && VisualNodeCatalog.ClassSignature(unreadable, VisualNodeCatalog.Spec(unreadable)) == null,
            "a signature that is not well formed gives no setting pins");
        foreach (var (signature, why) in new (string, string)[] {
            ("""{"title":"T","description":"","settings":[]}""", "no domain"),
            ("""{"title":"T","description":"","domain":5,"settings":[]}""", "a domain that is not text or null"),
            ("""{"title":"T","description":"","domain":null,"settings":{}}""", "settings that are not a list"),
            ("""{"title":"T","description":"","domain":null,"settings":[{"key":"a b","label":"A","description":"","type":"string","secret":false}]}""", "a key that is no pin id"),
            ("""{"title":"T","description":"","domain":null,"settings":[{"key":"A","label":"A","description":"","type":"string","secret":false},{"key":"A","label":"A","description":"","type":"string","secret":false}]}""", "a key twice"),
            ("""{"title":"T","description":"","domain":null,"settings":[{"key":"A","label":"A","description":"","type":"date","secret":false}]}""", "a type a setting cannot have"),
            ("""{"title":"T","description":"","domain":null,"settings":[{"key":"A","label":"A","description":"","type":"string"}]}""", "no secret flag"),
            ("""{"title":"T","description":"","domain":null,"settings":[{"key":"A","label":"A","description":"","type":"enum","secret":false,"options":[1]}]}""", "options that are not texts"),
            ("""{"title":"T","description":"","domain":null,"settings":[{"key":"A","label":"A","description":"","type":"integer","secret":false,"minimum":"1"}]}""", "a minimum that is not a number") })
            Check(!VisualNodeCatalog.WellFormedClassSignature(JsonNode.Parse(signature)), "not well formed: " + why);
        Check(VisualNodeCatalog.WellFormedClassSignature(VisualNodeCatalog.GetNode("csharp_entity")!["config"]!["signature"]!["default"])
            && VisualNodeCatalog.WellFormedClassSignature(VisualNodeCatalog.GetNode("csharp_device")!["config"]!["signature"]!["default"]), "each template's signature is well formed");
        // A C# entity's setting called device would take its Device pin's place: such a signature is not read.
        var clash = CSharpEntity("clash", "clash", new() { ["signature"] = JsonNode.Parse("""{"title":"T","description":"","domain":"sensor","settings":[{"key":"device","label":"Device","description":"","type":"string","secret":false}]}""") });
        Check(VisualNodeCatalog.ClassSignature(clash, VisualNodeCatalog.Spec(clash)) == null && VisualNodeCatalog.Structural(VisualNodeCatalog.DataInputs(clash)["device"]), "a setting named as a static pin is not read");

        var devices = VisualNodeCatalog.DeclaredDevices(graph, VisualNodeCatalog.Root).ToList();
        Check(devices.Count == 1 && devices[0].NodeId == "plug" && JsonNode.DeepEquals(devices[0].Device, JsonNode.Parse("""{"key":"plug","name":"Plug","via":null,"csharp":true}""")),
            "the graph's devices list the C# device, by its key, name and Part of as its config has them: " + string.Join(",", devices.Select(d => d.Device.ToJsonString())));
        var bare = Graph(null, CSharpDevice("bare", "bare", "Bare"));
        bare["nodes"]![0]!["config"]!.AsObject().Remove("name");
        Check(JsonNode.DeepEquals(VisualNodeCatalog.DeclaredDevices(bare, VisualNodeCatalog.Root).Single().Device, JsonNode.Parse("""{"key":"bare","csharp":true}""")),
            "what its config does not have, it does not list");
        Check(VisualNodeCatalog.DeviceOf(graph, "thermo", VisualNodeCatalog.Root) == null, "a C# entity without a wire is on no device");
    }

    /// <summary>
    /// Nothing a graph derives carries a C# node's code, settings or their values: its devices (a C# device by its key,
    /// name and Part of, marked <c>csharp</c>, after the Device nodes), its entities and the definition its catalog carries,
    /// which the generated source embeds.
    /// </summary>
    static void NothingDerivedCarriesCode()
    {
        var address = new JsonObject { ["Address"] = Lit("string", "http://192.168.1.50") };
        var graph = Graph(null,
            CSharpDevice("plug", "plug", " Plug ", new() { ["via"] = new JsonObject { ["device"] = "hall" }, ["arguments"] = address.DeepClone() }),
            N("hall", "device", new { key = "hall", name = "Hall plug" }), Hub("relay", "relay", "switch"),
            CSharpEntity("thermo", "thermometer", new() { ["arguments"] = address.DeepClone() }),
            CSharpDevice("desk", "desk", " ", new() { ["via"] = null }));
        graph["edges"] = new JsonArray(D("hall", "relay", "device", "entities"), D("hall", "thermo", "device", "entities"));
        var catalog = VisualNodeCatalog.Root;
        var devices = DefinitionGraph.Devices(graph, catalog);
        Check(JsonNode.DeepEquals(devices, JsonNode.Parse("""[{"key":"hall","name":"Hall plug"},{"key":"plug","name":" Plug ","via":{"device":"hall"},"csharp":true},{"key":"desk","csharp":true}]""")),
            "a C# device is its key, name and Part of, blank texts and a null Part of left out, after the Device nodes: " + devices.ToJsonString());
        var lifted = VisualNodeCatalog.WithGraphTypes(catalog, graph)["definition"]!;
        Check(JsonNode.DeepEquals(lifted["devices"], devices), "the graph's catalog lists the same devices: " + lifted["devices"]!.ToJsonString());
        var entities = DefinitionGraph.Entities(graph, VisualCatalogSnapshot.BuiltIn);
        Check(string.Join(",", entities.Select(e => e!["key"] + "=" + e["device"])) == "relay=hall", "the entities are the hub's: " + entities.ToJsonString());
        Check(VisualNodeCatalog.DeviceOf(graph, "thermo", catalog) == "hall", "a C# entity is on the Device node wired to it");
        foreach (var (what, value) in new (string, JsonNode)[] { ("devices", devices), ("entities", entities), ("the graph's definition", lifted),
            ("the declared devices", new JsonArray([.. VisualNodeCatalog.DeclaredDevices(graph, catalog).Select(d => (JsonNode)d.Device)])) })
            foreach (var member in new[] { "source", "signature", "arguments" })
                Check(!value.ToJsonString().Contains($"\"{member}\"", StringComparison.Ordinal), $"{what} carry no {member}: {value.ToJsonString()}");
        // The generated source embeds the catalog's definition: it holds none of the code either.
        var embedded = VisualGenerator.EmbeddedCatalog(graph, VisualCatalogSnapshot.BuiltIn);
        Check(!embedded["definition"]!.ToJsonString().Contains("Thermometer", StringComparison.Ordinal) && !embedded["definition"]!.ToJsonString().Contains("SharpDevice", StringComparison.Ordinal),
            "the embedded definition has no code: " + embedded["definition"]!.ToJsonString());
    }

    /// <summary>
    /// Every kind that declares an entity or a device is one the derived lists know, and say on purpose what they make of
    /// it; a declaring kind they do not know is refused, never copied as it is.
    /// </summary>
    static void EveryDeclaringKindIsDerivedOnPurpose()
    {
        var declaring = VisualNodeCatalog.Root["nodes"]!.AsObject().Where(n => VisualNodeCatalog.Text(n.Value, "declares") != "").Select(n => n.Key).ToList();
        Check(string.Join(",", declaring) == "entity,device,csharp_entity,csharp_device", "the declaring kinds: " + string.Join(",", declaring));
        var graph = Graph(null, [.. declaring.Select(kind => Templated(kind, kind, new() { ["key"] = kind }))]);
        Check(VisualNodeCatalog.WithGraphTypes(VisualNodeCatalog.Root, graph)["definition"] is JsonObject, "a graph of one node of each derives its lists");
        var patched = (JsonObject)VisualNodeCatalog.Root.DeepClone();
        patched["nodes"]!["other"] = new JsonObject { ["label"] = "Other", ["declares"] = "device", ["config"] = new JsonObject(), ["inputs"] = new JsonObject(), ["outputs"] = new JsonObject() };
        foreach (var what in new[] { "entity", "device" })
        {
            patched["nodes"]!["other"]!["declares"] = what;
            var other = Graph(null, N("o", "other", new { key = "o" }));
            try { VisualNodeCatalog.WithGraphTypes(patched, other); throw new Exception($"a kind that declares {what} the lists do not know was derived"); }
            catch (InvalidOperationException) { }
        }
    }

    /// <summary>
    /// A C# device reaches the host as one of the graph's components, which its own code sets up, never as a plain device:
    /// the devices of a compiled graph definition are its Device nodes', and a C# device's config (even a member a device
    /// has, such as a manufacturer) makes none; the component carries its node's key and name, and no such member.
    /// </summary>
    static void TheHostGetsACSharpDeviceAsAComponent()
    {
        var graph = Graph(null, N("hall", "device", new { key = "hall", name = "Hall plug", model = "P1" }), Hub("relay", "relay", "switch"),
            CSharpDevice("plug", "plug", "Plug", new() { ["manufacturer"] = "Not mine" }));
        graph["edges"] = new JsonArray(D("hall", "relay", "device", "entities"));
        var sent = HassSharpManager.GraphDevices(graph, VisualCatalogSnapshot.BuiltIn);
        Check(sent.Count == 1 && sent[0] is { Key: "hall", Name: "Hall plug", Model: "P1" }, "the host is handed the Device node alone as a device: " + JsonSerializer.Serialize(sent, VisualStore.Json));
        var components = DefinitionGraph.Components(graph, VisualCatalogSnapshot.BuiltIn, []);
        Check(components.Count == 1 && components[0] is { NodeId: "plug", Kind: "device", Key: "plug", Name: "Plug", Via: null, Device: null },
            "and the C# device as a component: " + string.Join(" | ", components));
        Check(DefinitionGraph.Devices(graph, VisualNodeCatalog.Root).Count == 2, "while the stored list has both");
    }

    /// <summary>
    /// A Device node's device is the members a device has, and what makes a device a C# device is its node's kind: a
    /// Device node whose config carries <c>csharp</c> (an MCP save, or a copy of a read answer's devices) and a member no
    /// device has derives neither, is listed among the Device nodes, and is handed to the host. A config of every member
    /// a device has derives all of them, as before.
    /// </summary>
    static void ADeviceIsADeviceByItsKind()
    {
        var full = new JsonObject
        {
            ["key"] = "hall", ["name"] = "Hall plug", ["manufacturer"] = "Acme", ["model"] = "P1", ["swVersion"] = "1.2", ["hwVersion"] = "A",
            ["configurationUrl"] = "http://192.168.1.50", ["suggestedArea"] = "Hall", ["via"] = new JsonObject { ["device"] = "hub" },
        };
        Check(string.Join(",", full.Select(m => m.Key)) == string.Join(",", VisualNodeCatalog.GetNode("device")!["config"]!.AsObject().Select(m => m.Key)), "the config has every member the Device kind has");
        var stray = N("stray", "device", new JsonObject { ["key"] = "stray", ["name"] = "Stray", ["csharp"] = true, ["colour"] = "red" });
        var graph = Graph(null, CSharpDevice("plug", "plug", "Plug"), stray, N("hall", "device", full.DeepClone()), N("hub", "device", new { key = "hub", name = "Hub" }));
        var declared = VisualNodeCatalog.DeclaredDevices(graph, VisualNodeCatalog.Root).ToDictionary(d => d.NodeId, d => d.Device);
        Check(JsonNode.DeepEquals(declared["stray"], JsonNode.Parse("""{"key":"stray","name":"Stray"}""")), "a Device node's config passes on no member a device does not have: " + declared["stray"].ToJsonString());
        Check(JsonNode.DeepEquals(declared["hall"], full), "a Device node of every member passes on all of them: " + declared["hall"].ToJsonString());
        Check(!VisualNodeCatalog.IsCSharpDevice(declared["stray"]) && VisualNodeCatalog.IsCSharpDevice(declared["plug"]), "only the C# device kind is a C# device");
        var devices = DefinitionGraph.Devices(graph, VisualNodeCatalog.Root);
        Check(string.Join(",", devices.Select(d => d!["key"])) == "stray,hall,hub,plug", "the Device nodes in node order, then the C# device: " + devices.ToJsonString());
        Check(JsonNode.DeepEquals(devices[1], full), "the listed device of every member is its config: " + devices[1]!.ToJsonString());
        var sent = HassSharpManager.GraphDevices(graph, VisualCatalogSnapshot.BuiltIn);
        Check(string.Join(",", sent.Select(d => d.Key)) == "stray,hall,hub", "the host is handed every Device node, the one whose config says csharp too: " + JsonSerializer.Serialize(sent, VisualStore.Json));
    }

    /// <summary>The definition a run of <see cref="TheRuntimeNeverWalksACSharpNode"/> reaches: settings by key, none secret; it is handed nothing else.</summary>
    sealed class SettingsOnly(Dictionary<string, object?> settings) : IDefinitionRuntime
    {
        public string DefinitionId => "settings";
        public void SetState(string entityKey, IReadOnlyDictionary<string, object?> values, IReadOnlySet<string> hidden) => throw new InvalidOperationException("No state is set here");
        public void SetAvailable(string entityKey, bool available) => throw new InvalidOperationException("No entity is marked here");
        public object? Setting(string key) => settings[key];
        public bool IsSecret(string key) => false;
        public SharpFiles Files => throw new InvalidOperationException("This definition downloads nothing");
        public void ShowFrame(string entityKey, SharpImage image) => throw new InvalidOperationException("No frame is shown here");
    }
    /// <summary>The trace documents a run writes; the last one is its final trace.</summary>
    sealed class Traces : IRunTraceSink
    {
        readonly List<string> writes = [];
        public void Write(string json, int eventCount, bool truncated, bool final, bool immediate = false) { lock (writes) writes.Add(json); }
        public JsonObject Final { get { lock (writes) return JsonNode.Parse(writes[^1])!.AsObject(); } }
    }

    /// <summary>
    /// The runtime never walks a C# node, nor works out a wire into one: a run whose step reads a Setting that is also
    /// wired into a C# entity's and a C# device's setting pins runs that step alone, and its trace has neither C# node; a
    /// run started at one is refused in its kind's words.
    /// </summary>
    static void TheRuntimeNeverWalksACSharpNode()
    {
        var graph = Graph(null, N("setup", "entity_setup"), N("address", "setting", new { key = "address" }), N("say", "log", new { value = new { kind = "unset" } }),
            CSharpEntity("thermo", "hall_temperature"), CSharpDevice("plug", "plug", "Plug"), N("hall", "device", new { key = "hall", name = "Hall" }));
        graph["definition"]!["settings"] = new JsonArray(new JsonObject { ["key"] = "address", ["type"] = "string" });
        graph["edges"] = new JsonArray(E("setup", "say"), D("address", "say", "value"), D("address", "thermo", "Address"), D("address", "plug", "Address"), D("hall", "thermo", "device", "entities"));
        // A run is one: each is started over a runtime of its own.
        VisualRuntime Runtime() => new(graph.ToJsonString(), VisualNodeCatalog.Root.ToJsonString(), [], TriggerPayload.Manual(), CancellationToken.None,
            (_, _, _) => throw new InvalidOperationException("Unknown compiled node"));
        var log = PyInterop.Log; var prior = InvocationContext.Current; var said = new List<string>(); var traces = new Traces();
        try
        {
            PyInterop.Log = (_, message) => { lock (said) said.Add(message); };
            InvocationContext.Current = new("run-" + Guid.NewGuid().ToString("N"), InvocationCause.Entity, null, TriggerPayload.Manual())
                { Definition = new SettingsOnly(new() { ["address"] = "http://192.168.1.60" }), TraceSink = traces };
            Runtime().RunAsync("setup").GetAwaiter().GetResult();
        }
        finally { PyInterop.Log = log; InvocationContext.Current = prior; }
        Check(said.SequenceEqual(["http://192.168.1.60"]), "the step reads the Setting the C# nodes are wired to: " + string.Join(" | ", said));
        var traced = traces.Final["events"]!.AsArray().Select(e => VisualNodeCatalog.Text(e, "node")).ToList();
        Check(traced.Contains("say") && !traced.Contains("thermo") && !traced.Contains("plug"), "the trace has the step and neither C# node: " + string.Join(",", traced));
        foreach (var (id, words) in new[] { ("thermo", "Thermometer is an entity and is not part of the run order"), ("plug", "Plug is a device and is not part of the run order") })
        {
            try { Runtime().RunAsync(id).GetAwaiter().GetResult(); throw new Exception($"a run started at {id} ran"); }
            catch (InvalidOperationException refused) { Check(refused.Message == words, $"a run started at {id} is refused in words: {refused.Message}"); }
        }
    }

    /// <summary>
    /// A setting is set before anything runs: its pin takes a typed value or a wire from a Setting node, never a step's
    /// output or a value made from a Setting (<c>scope.settingSource</c>, on the pin); and a secret takes only the wire
    /// (<c>setting.secretLiteral</c>, in words that do not repeat what was typed). A secret left unset is no problem.
    /// </summary>
    static void ASettingTakesATypedValueOrASettingNode()
    {
        var template = VisualNodeCatalog.GetNode("csharp_device")!["config"]!["signature"]!["default"]!.DeepClone();
        template["settings"]!.AsArray().Add(JsonNode.Parse("""{"key":"Token","label":"Token","description":"The token the plug asks for.","type":"string","secret":true}"""));
        JsonObject Of(JsonObject? arguments, params JsonObject[] edges)
        {
            var graph = Graph(null, CSharpDevice("plug", "plug", "Plug", new() { ["signature"] = template.DeepClone(), ["arguments"] = arguments ?? [] }), CSharpEntity("thermo", "thermometer"),
                N("address", "setting", new { key = "address" }), N("text", "convert", new JsonObject { ["type"] = "string", ["value"] = new JsonObject { ["kind"] = "unset" } }),
                N("start", "entity_setup"), N("http", "http_request", new JsonObject { ["method"] = "GET", ["responseType"] = "string", ["url"] = Lit("string", "http://192.168.1.50/address") }));
            graph["definition"]!["settings"] = new JsonArray(new JsonObject { ["key"] = "address", ["type"] = "string" });
            graph["edges"] = new JsonArray([E("start", "http"), D("address", "text", "value"), .. edges]);
            return graph;
        }
        // The Convert step is not used unless it is wired on: that warning is not a setting's.
        List<VisualDiagnostic> Problems(JsonObject graph) => VisualGraphValidator.Validate(graph, VisualCatalogSnapshot.BuiltIn).Where(d => d.NodeId != "text" || d.Code != "pure.unused").ToList();
        string Of1(VisualDiagnostic d) => $"{d.Code}@{d.NodeId}:{d.InputId}:{d.PortId}:{d.Severity}:{d.Message}";
        Check(Problems(Of(null)).Count == 0, "the graph has no problem: " + string.Join(" | ", Problems(Of(null)).Select(Of1)));
        Check(VisualGraphValidator.Codes.Contains("scope.settingSource") && VisualGraphValidator.Codes.Contains("setting.secretLiteral"), "both codes are declared");
        foreach (var (source, port) in new[] { ("http", "body"), ("text", "value") })
            foreach (var target in new[] { "plug", "thermo" })
            {
                var found = Problems(Of(null, D(source, target, "Address", port)));
                Check(string.Join(" | ", found.Select(Of1)) == $"scope.settingSource@{target}:Address:Address:3:Address takes a typed value or a Setting: it is set before anything runs",
                    $"{source}.{port} into {target}.Address is refused on the pin: " + string.Join(" | ", found.Select(Of1)));
            }
        var wired = Problems(Of(null, D("address", "plug", "Address"), D("address", "thermo", "Address"), D("address", "plug", "Token")));
        Check(wired.Count == 0, "a Setting is taken by a setting, a secret one too: " + string.Join(" | ", wired.Select(Of1)));
        var typed = Problems(Of(new JsonObject { ["Address"] = Lit("string", "http://192.168.1.50"), ["Token"] = Lit("string", "s3cr3t-token") }));
        Check(string.Join(" | ", typed.Select(Of1)) == "setting.secretLiteral@plug:Token::3:Token is a secret: connect a Setting instead of typing it here",
            "a typed value is taken, a typed secret is not: " + string.Join(" | ", typed.Select(Of1)));
        Check(!typed.Any(d => d.Message.Contains("s3cr3t", StringComparison.Ordinal)), "the secret is not repeated");
        Check(Problems(Of(new JsonObject { ["Token"] = new JsonObject { ["kind"] = "unset" } })).Count == 0, "a secret left unset is no problem");
        // A typed secret is refused under a wire too (a document saved by hand or over MCP), and whatever its claimed type, the
        // secret sentence is all that is said of it: no message quotes it.
        foreach (var (operand, edges, why) in new (JsonObject, JsonObject[], string)[] {
            (Lit("string", "s3cr3t-token"), [D("address", "plug", "Token")], "under a wire from a Setting"),
            (Lit("number", 1234), [], "of another type"),
            (Lit("global::HassSharp.Entities.BinarySensorState", "s3cr3t-token"), [], "of an enum type") })
        {
            var told = Problems(Of(new JsonObject { ["Token"] = operand }, edges));
            Check(string.Join(" | ", told.Select(Of1)) == "setting.secretLiteral@plug:Token::3:Token is a secret: connect a Setting instead of typing it here",
                $"a typed secret {why} is told only that it is one: " + string.Join(" | ", told.Select(Of1)));
            Check(!told.Any(d => d.Message.Contains("s3cr3t", StringComparison.Ordinal) || d.Message.Contains("1234", StringComparison.Ordinal)), $"a typed secret {why} is not quoted");
        }
        var other = Problems(Of(new JsonObject { ["Address"] = new JsonObject { ["kind"] = "expression", ["code"] = "Address" } }));
        Check(string.Join(" | ", other.Select(Of1)) == "@plug:Address::3:Address has an invalid value", "a value of another kind is what any input says of it: " + string.Join(" | ", other.Select(Of1)));
    }
}
