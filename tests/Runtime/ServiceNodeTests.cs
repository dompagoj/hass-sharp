using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>The service-aware Call service node (contract C5): runtime payload and response, and where service schemas apply.</summary>
public static class ServiceNodeTests
{
    static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name));
    static JsonObject Unset() => new() { ["kind"] = "unset" };
    static JsonObject Service(string id, string service, JsonObject? fields = null, JsonObject? target = null, bool returnResponse = false)
    {
        var config = new JsonObject { ["service"] = service, ["fields"] = fields ?? [], ["returnResponse"] = returnResponse };
        if (target != null) config["target"] = target;
        return N(id, "service", config);
    }

    [RuntimeSuite("--service-node", Order = 18)]
    public static async Task Run()
    {
        await Payloads();
        await Attachment();
        Defaults();
    }

    // A service field's default (C4, a plain value) reaches its input spec as an operand, the shape catalog inputs use.
    static void Defaults()
    {
        var snapshot = VisualCatalogSnapshot.BuiltIn.WithServices(ServiceSchemaCatalog.Parse(Fixture("ha-service-descriptions.json"), _ => { }));
        var node = Service("s", "hass_sharp_test.sectioned", new JsonObject { ["level"] = Unset(), ["speed"] = Unset() });
        var inputs = VisualNodeCatalog.DataInputs(node, null, snapshot);
        Check(JsonNode.DeepEquals(inputs["field:level"]?["default"], JsonNode.Parse("""{"kind":"literal","type":"number","value":3}""")), "a service field default is an operand: " + inputs.ToJsonString());
        Check(inputs["field:speed"]!.AsObject().ContainsKey("default") == false, "a field without a default has none: " + inputs["field:speed"]!.ToJsonString());
        Console.WriteLine("PASS service node field defaults");
    }

    static async Task Payloads()
    {
        var previousCall = PyInterop.CallService; var previousResponse = PyInterop.CallServiceWithResponse; var previousLog = PyInterop.Log;
        var previousPath = HassPath.HassConfiguration; HassPath.HassConfiguration = Path.Combine(Path.GetTempPath(), "service-payload-" + Guid.NewGuid());
        var calls = new List<(string Service, JsonObject Payload, bool Response)>();
        PyInterop.Log = (_, _) => { };
        PyInterop.CallService = (domain, name, json, done, _) => { calls.Add((domain + "." + name, JsonNode.Parse(json!)!.AsObject(), false)); done?.Invoke(); };
        PyInterop.CallServiceWithResponse = (domain, name, json, done, _) => { calls.Add((domain + "." + name, JsonNode.Parse(json!)!.AsObject(), true)); done("""{"forecast":{"entity":"light.kitchen","brightness":42}}"""); };
        void Expect(string service, string payload, bool response, string message)
        {
            Check(calls.Count == 1, $"{message}: expected one call, got {calls.Count}");
            var call = calls.Single(); calls.Clear();
            Check(call.Service == service && call.Response == response && JsonNode.DeepEquals(call.Payload, JsonNode.Parse(payload)),
                $"{message}: got {call.Service} {call.Payload.ToJsonString()} response={call.Response}");
        }
        try
        {
            // A literal field plus a string target; the unset optional field is omitted.
            await Execute(G(Service("s", "light.turn_on", new JsonObject { ["brightness_pct"] = Lit("number", 40), ["transition"] = Unset() }, Lit("entity_id", "light.hall"))));
            Expect("light.turn_on", """{"brightness_pct":40,"entity_id":"light.hall"}""", false, "literal field and string target");

            // An object target is flattened into the payload; no target means no target keys.
            await Execute(G(Service("s", "light.turn_on", target: Lit("target", new { entity_id = new[] { "light.a", "light.b" }, area_id = "kitchen" }))));
            Expect("light.turn_on", """{"entity_id":["light.a","light.b"],"area_id":"kitchen"}""", false, "object target flattened");
            await Execute(G(Service("s", "light.turn_on", target: Unset())));
            Expect("light.turn_on", "{}", false, "unset target and no fields");

            // A field named target travels as a field and never as the target input.
            await Execute(G(Service("s", "notify.notify", new JsonObject { ["message"] = Lit("string", "Door open"), ["target"] = Lit("string", "phone") })));
            Expect("notify.notify", """{"message":"Door open","target":"phone"}""", false, "field named target");

            // returnResponse requests the response; the response output and wired fields/target drive the next call.
            var graph = G(Service("first", "weather.get_forecasts", new JsonObject { ["type"] = Lit("string", "daily") }, returnResponse: true),
                N("entity", "get_field", new { value = Unset(), pointer = "/forecast/entity", type = "entity_id" }),
                N("level", "get_field", new { value = Unset(), pointer = "/forecast/brightness", type = "number" }),
                Service("second", "light.turn_on", new JsonObject { ["brightness"] = Unset() }, Unset()));
            var edges = graph["edges"]!.AsArray();
            edges.Add(D("first", "entity", "value", "response")); edges.Add(D("first", "level", "value", "response"));
            edges.Add(D("entity", "second", "target")); edges.Add(D("level", "second", "field:brightness"));
            await Execute(graph);
            Check(calls.Count == 2 && calls[0] is { Service: "weather.get_forecasts", Response: true } && JsonNode.DeepEquals(calls[0].Payload, JsonNode.Parse("""{"type":"daily"}""")), "returnResponse requests a response");
            calls.RemoveAt(0);
            Expect("light.turn_on", """{"brightness":42,"entity_id":"light.kitchen"}""", false, "wired field and target from the response");

            // Without returnResponse the response port does not exist, so it cannot be wired.
            var unwired = (JsonObject)graph.DeepClone(); unwired["nodes"]![1]!["config"]!["returnResponse"] = false;
            var preview = new VisualGenerator(Diagnostics).Preview(unwired);
            Check(!preview.Valid && preview.Diagnostics.Any(d => d.NodeId == "first" && d.Message == "Unknown data output port"), "response output exists only with returnResponse: " + JsonSerializer.Serialize(preview.Diagnostics));
            // A target object holds only the five target keys: one that carries anything else (here brightness, which a
            // field also sets) fails the call with the validators' sentence, never a silent overwrite. A node with a set
            // target cannot have a field named like a target key, so the remaining collision is caught before run time.
            var twice = G(N("value", "constant", new { type = "target", value = Lit("target", new { entity_id = "light.a", brightness = 5 }) }),
                Service("s", "light.turn_on", new JsonObject { ["brightness"] = Lit("number", 1) }, Unset()));
            twice["edges"]!.AsArray().Add(D("value", "s", "target"));
            var failure = await Record(() => Execute(twice));
            Check(failure?.Message == VisualGraphValidator.TargetKeyMessage("brightness") && failure.Message == "Target can only contain entity_id, device_id, area_id, floor_id and label_id; found brightness" && calls.Count == 0,
                "a target object key that is not a target key fails the call: " + failure + $" after {calls.Count} call(s)");
            calls.Clear();
            Console.WriteLine("PASS service node payloads: literal and wired fields, unset fields omitted, string and object targets, field named target, returnResponse, target object keys");
        }
        finally
        {
            PyInterop.CallService = previousCall; PyInterop.CallServiceWithResponse = previousResponse; PyInterop.Log = previousLog;
            try { Directory.Delete(HassPath.HassConfiguration, true); } catch { }
            HassPath.HassConfiguration = previousPath;
        }
    }

    static async Task<Exception?> Record(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : error; }
    }

    // A stored graph using a field the pushed service lacks still loads at Init and survives a library save
    // rebuild (both validate without services), while authoring preview of the same graph reports the field.
    static async Task Attachment()
    {
        var previous = HassPath.HassConfiguration; var log = PyInterop.Log; PyInterop.Log = (_, _) => { };
        var path = Path.Combine(Path.GetTempPath(), "service-node-" + Guid.NewGuid());
        var graph = JsonNode.Parse("""
        {"version":5,"name":"Stale field","nodes":[
          {"id":"t","kind":"trigger","config":{"type":"manual"}},
          {"id":"s","kind":"service","config":{"service":"light.turn_on","fields":{"glow":{"kind":"literal","type":"number","value":3}},"returnResponse":false,
            "target":{"kind":"literal","type":"target","value":{"entity_id":"light.hall"}}}}
        ],"edges":[{"id":"ts","source":"t","target":"s"}]}
        """)!.AsObject();
        static async Task<JsonNode> Visual(HassSharpManager manager, object request) => JsonNode.Parse(await manager.VisualRequest(JsonSerializer.Serialize(request)))!;
        static bool Active(JsonNode document) => document["result"]?["currentRevisionActive"]?.GetValue<bool>() == true;
        try
        {
            // Saved before any service schemas were pushed: the unknown service is only a warning.
            var author = new HassSharpManager(path);
            try
            {
                await author.Init();
                var saved = await Visual(author, new { operation = "save", id = "stale", graph, expectedRevision = 0 });
                Check(saved["ok"]!.GetValue<bool>() && saved["result"]!["active"]!.GetValue<bool>(), "graph saves before services are pushed: " + saved.ToJsonString());
            }
            finally { await author.UnloadAsync(); }

            var manager = new HassSharpManager(path);
            try
            {
                manager.SetServiceDescriptions(Fixture("ha-service-descriptions.json"));
                await manager.Init();
                Check(Active(await Visual(manager, new { operation = "read", id = "stale" })), "Init loads and activates the stored graph although light.turn_on has no field glow");

                var preview = await Visual(manager, new { operation = "preview", graph });
                Check(preview["ok"]!.GetValue<bool>() && !preview["result"]!["valid"]!.GetValue<bool>()
                    && preview["result"]!["diagnostics"]!.AsArray().Any(d => d!["message"]!.GetValue<string>() == "light.turn_on has no field glow" && d["inputId"]?.GetValue<string>() == "field:glow"),
                    "authoring preview reports the unknown field: " + preview.ToJsonString());

                var library = JsonNode.Parse(await manager.NodeLibraryRequest(JsonSerializer.Serialize(new
                {
                    operation = "save", id = "helpers", name = "Helpers", expectedRevision = 0,
                    source = "public static class Helpers { [Node(\"Noop\")] public static void Noop() { } }"
                })))!;
                Check(library["ok"]!.GetValue<bool>() && library["result"]!["automations"]!.AsArray().Count == 1
                    && library["result"]!["automations"]![0]!["active"]!.GetValue<bool>(), "a library save rebuilds and reactivates the stored graph: " + library.ToJsonString());
                var reread = await Visual(manager, new { operation = "read", id = "stale" });
                Check(Active(reread) && reread["result"]!["revision"]!.GetValue<long>() == 2, "the rebuilt graph is the active revision: " + reread.ToJsonString());
            }
            finally { await manager.UnloadAsync(); }
            Console.WriteLine("PASS service node schema attachment: Init and library rebuild ignore services, authoring preview applies them");
        }
        finally { PyInterop.Log = log; HassPath.HassConfiguration = previous; try { Directory.Delete(path, true); } catch { } }
    }
}
