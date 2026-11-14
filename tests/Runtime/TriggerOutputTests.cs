using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>Contracts D8/C6: one normalised payload for every cause and typed trigger outputs read from it.</summary>
static class TriggerOutputTests
{
    const string Snapshot = "global::HassSharp.EntityStateSnapshot";
    static readonly ConcurrentQueue<string> messages = new();

    [RuntimeSuite("--trigger-outputs", Order = 16)]
    public static async Task Run()
    {
        var previousPath = HassPath.HassConfiguration; HassPath.HassConfiguration = Path.Combine(Path.GetTempPath(), "trigger-outputs-" + Guid.NewGuid());
        var log = PyInterop.Log; PyInterop.Log = (_, message) => messages.Enqueue(message);
        try
        {
            Ports();
            Payloads();
            await Outputs();
            await Invocations();
            Console.WriteLine("PASS trigger outputs: typed ports per trigger, refinement, normalised payloads for every cause and absent outputs");
        }
        finally { PyInterop.Log = log; HassPath.HassConfiguration = previousPath; }
    }

    static string Describe(JsonObject trigger) => string.Join(",", VisualNodeCatalog.DataOutputs(N("t", "trigger", trigger.DeepClone())).Select(p => p.Key + "=" + p.Value!["type"]!.GetValue<string>()));
    static JsonObject Ha(object trigger) => new() { ["type"] = "ha", ["trigger"] = JsonSerializer.SerializeToNode(trigger) };

    static void Ports()
    {
        var entity = $"data=object,entity_id=entity_id:light,to_state={Snapshot},from_state={Snapshot}";
        foreach (var (trigger, expected) in new (JsonObject, string)[] {
            (new() { ["type"] = "manual" }, "data=object"),
            (new() { ["type"] = "startup" }, "data=object"),
            (new() { ["type"] = "daily", ["time"] = "07:30", ["timeZone"] = "UTC" }, "data=object,planned=string"),
            (new() { ["type"] = "once", ["at"] = "2030-01-01T10:00:00Z" }, "data=object,planned=string"),
            (new() { ["type"] = "entity", ["entity"] = "light.kitchen" }, entity),
            (new() { ["entity"] = "light.kitchen" }, entity),
            (new() { ["type"] = "entity" }, $"data=object,entity_id=entity_id,to_state={Snapshot},from_state={Snapshot}"),
            (new() { ["type"] = "entity", ["entity"] = "Light.Kitchen" }, $"data=object,entity_id=entity_id,to_state={Snapshot},from_state={Snapshot}"),
            (Ha(new { trigger = "state", entity_id = "light.kitchen" }), entity),
            (Ha(new { platform = "state", entity_id = "light.kitchen" }), entity),
            (Ha(new { trigger = "state", entity_id = new[] { "light.a", "light.b" } }), entity),
            (Ha(new { trigger = "state", entity_id = new[] { "light.a", "switch.b" } }), $"data=object,entity_id=entity_id,to_state={Snapshot},from_state={Snapshot}"),
            (Ha(new { trigger = "state", entity_id = Array.Empty<string>() }), $"data=object,entity_id=entity_id,to_state={Snapshot},from_state={Snapshot}"),
            (Ha(new { trigger = "numeric_state", entity_id = "sensor.temperature", above = 20 }), $"data=object,entity_id=entity_id:sensor,to_state={Snapshot},from_state={Snapshot}"),
            (Ha(new { trigger = "template", value_template = "{{ true }}" }), $"data=object,entity_id=entity_id,to_state={Snapshot},from_state={Snapshot}"),
            (Ha(new { trigger = "zone", entity_id = "person.ana", zone = "zone.home", @event = "enter" }), "data=object,entity_id=entity_id:person,zone=string,event=string"),
            (Ha(new { trigger = "event", event_type = "test" }), "data=object,event_type=string,event_data=object"),
            (Ha(new { trigger = "time", at = "07:00:00" }), "data=object,now=string"),
            (Ha(new { trigger = "time_pattern", minutes = "/5" }), "data=object,now=string"),
            (Ha(new { trigger = "sun", @event = "sunset" }), "data=object,event=string"),
            (Ha(new { trigger = "homeassistant", @event = "start" }), "data=object,event=string"),
            (Ha(new { trigger = "webhook", webhook_id = "hook" }), "data=object,json=object,query=object"),
            (Ha(new { trigger = "mqtt", topic = "a/b" }), "data=object,topic=string,payload=string"),
            (Ha(new { trigger = "device", device_id = "x" }), "data=object") })
            Check(Describe(trigger) == expected, $"trigger ports for {trigger.ToJsonString()}: {Describe(trigger)}");
        var ports = VisualNodeCatalog.DataOutputs(N("t", "trigger", new { type = "entity", entity = "light.kitchen" }));
        Check(ports["to_state"]!["pointer"]!.GetValue<string>() == "/to_state" && ports["data"]!["pointer"]!.GetValue<string>() == "", "trigger ports keep their payload pointers");
    }

    static HasEntityState State(string id, string state, double changed = 1_700_000_000) =>
        new() { EntityId = id, State = state, Attributes = new() { ["unit"] = "C", ["levels"] = new object[] { 1, "two" } }, LastChanged = changed, LastReported = changed + 5 };

    static void Payloads()
    {
        var current = State("sensor.test", "on"); current.OldState = State("sensor.test", "off", 1_600_000_000);
        var entity = TriggerPayload.Entity(current, current.OldState);
        Check(entity.GetProperty("platform").GetString() == "entity" && entity.GetProperty("entity_id").GetString() == "sensor.test", "entity payload identifies the entity");
        var to = entity.GetProperty("to_state");
        Check(to.GetProperty("state").GetString() == "on" && to.GetProperty("attributes").GetProperty("levels")[1].GetString() == "two"
            && to.GetProperty("last_changed").GetString() == "2023-11-14T22:13:20.0000000+00:00" && to.GetProperty("last_updated").ValueKind == JsonValueKind.Null, "entity states use the as_dict shape");
        var snapshot = EntityStateSnapshot.FromStateJson(entity.GetProperty("from_state"));
        Check(snapshot.EntityId == "sensor.test" && snapshot.State == "off" && snapshot.Attributes.GetProperty("unit").GetString() == "C"
            && snapshot.LastChanged == DateTimeOffset.UnixEpoch.AddSeconds(1_600_000_000) && snapshot.LastReported == snapshot.LastChanged.AddSeconds(5) && snapshot.LastUpdated == null, "FromStateJson reads an as_dict state");
        Check(TriggerPayload.Entity(current, null).GetProperty("from_state").ValueKind == JsonValueKind.Null, "missing old state is null");
        var native = EntityStateSnapshot.FromStateJson(JsonSerializer.SerializeToElement(new { entity_id = "light.kitchen", state = "on", attributes = new { brightness = 3 }, last_changed = "2026-11-13T10:00:00.123456+00:00", last_updated = "2026-11-13T10:00:01+00:00" }));
        Check(native.LastUpdated == DateTimeOffset.Parse("2026-11-13T10:00:01+00:00") && native.LastReported == native.LastUpdated && native.Attributes.GetProperty("brightness").GetInt32() == 3, "HA as_dict timestamps are parsed");
        try { EntityStateSnapshot.FromStateJson(JsonSerializer.SerializeToElement(new { entity_id = "light.kitchen" })); throw new Exception("incomplete state accepted"); } catch (ArgumentException) { }

        var schedule = TriggerPayload.Schedule("noon", new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc));
        Check(schedule.ToString() == """{"platform":"schedule","schedule_id":"noon","planned":"2026-11-13T12:00:00.0000000Z"}""", "schedule payload: " + schedule);
        Check(TriggerPayload.Manual().ToString() == """{"platform":"manual"}""" && TriggerPayload.Startup().ToString() == """{"platform":"startup"}""", "manual and startup payloads");
        // A real entity firing is byte-for-byte what it was before manual runs carried states.
        Check(entity.ToString() == """{"platform":"entity","entity_id":"sensor.test","from_state":{"entity_id":"sensor.test","state":"off","attributes":{"unit":"C","levels":[1,"two"]},"last_changed":"2020-09-13T12:26:40.0000000\u002B00:00","last_updated":null,"last_reported":"2020-09-13T12:26:45.0000000\u002B00:00"},"to_state":{"entity_id":"sensor.test","state":"on","attributes":{"unit":"C","levels":[1,"two"]},"last_changed":"2023-11-14T22:13:20.0000000\u002B00:00","last_updated":null,"last_reported":"2023-11-14T22:13:25.0000000\u002B00:00"}}""", "real entity payload changed: " + entity);
        ManualPayloads();
        var variables = JsonSerializer.SerializeToElement(new { trigger = new { platform = "event", @event = new { event_type = "test" } }, context = new { id = "c1" }, other = 1 });
        Check(TriggerPayload.Native(variables).ToString() == """{"platform":"event","event":{"event_type":"test"},"context":{"id":"c1"}}""", "native payload is the trigger dict plus context");
        Check(TriggerPayload.Native(JsonSerializer.SerializeToElement(new { other = 1 })).ToString() == "{}", "native payload without a trigger dict is empty");
    }

    // The current state a manual run reads: sensor.test is 21, sensor.blank has no state, everything else does not exist.
    static HasEntityState? Current(string id) => id switch { "sensor.test" => State("sensor.test", "21"), "sensor.blank" => new() { EntityId = id }, _ => null };
    static JsonElement ManualOf(JsonObject trigger) => TriggerPayload.Manual(trigger["type"]!.GetValue<string>(), trigger, Current);

    static void ManualPayloads()
    {
        JsonNode Expected(object fields)
        {
            var payload = JsonSerializer.SerializeToNode(fields)!.AsObject();
            foreach (var key in new[] { "from_state", "to_state" }) payload[key] = JsonNode.Parse(TriggerPayload.Entity(State("sensor.test", "21"), null).GetProperty("to_state").GetRawText());
            payload["manual"] = true;
            return payload;
        }
        void Same(JsonObject trigger, JsonNode expected) =>
            Check(JsonNode.DeepEquals(JsonNode.Parse(ManualOf(trigger).GetRawText()), expected), $"manual payload of {trigger.ToJsonString()}: {ManualOf(trigger)}");

        // An entity trigger carries the shape of a real firing, with the current state as both states.
        var entity = new JsonObject { ["type"] = "entity", ["entity"] = "sensor.test" };
        Same(entity, Expected(new { platform = "entity", entity_id = "sensor.test", from_state = 0, to_state = 0 }));
        var keys = string.Join(",", ManualOf(entity).EnumerateObject().Select(p => p.Name));
        Check(keys == "platform,entity_id,from_state,to_state,manual", "manual entity payload keys: " + keys);
        Check(EntityStateSnapshot.FromStateJson(ManualOf(entity).GetProperty("from_state")).State == "21", "manual states read as snapshots");

        // Native state (trigger or platform key, a list uses its first entity) and numeric_state with its bounds.
        Same(Ha(new { trigger = "state", entity_id = new[] { "sensor.test", "sensor.other" }, to = "on" }), Expected(new { platform = "state", entity_id = "sensor.test", from_state = 0, to_state = 0 }));
        Same(Ha(new { platform = "state", entity_id = "sensor.test" }), Expected(new { platform = "state", entity_id = "sensor.test", from_state = 0, to_state = 0 }));
        // The configured id and attribute are copied; for is not (it cannot be known without a real firing).
        Same(Ha(new { trigger = "state", entity_id = "sensor.test", id = "warm", attribute = "unit", @for = "00:05:00" }),
            Expected(new { platform = "state", entity_id = "sensor.test", from_state = 0, to_state = 0, id = "warm", attribute = "unit" }));
        Same(Ha(new { trigger = "numeric_state", entity_id = "sensor.test", id = "hot", attribute = "unit", above = 20 }),
            Expected(new { platform = "numeric_state", entity_id = "sensor.test", from_state = 0, to_state = 0, id = "hot", attribute = "unit", above = 20 }));
        Same(Ha(new { trigger = "numeric_state", entity_id = "sensor.test", above = 20, below = "input_number.limit" }),
            Expected(new { platform = "numeric_state", entity_id = "sensor.test", from_state = 0, to_state = 0, below = "input_number.limit", above = 20 }));
        Same(Ha(new { trigger = "numeric_state", entity_id = new[] { "sensor.test" }, below = 30.5 }), Expected(new { platform = "numeric_state", entity_id = "sensor.test", from_state = 0, to_state = 0, below = 30.5 }));

        // Everything else, and an entity that is not known or has no state, stays a plain manual run.
        foreach (var trigger in new JsonObject[] {
            new() { ["type"] = "entity", ["entity"] = "sensor.missing" },
            new() { ["type"] = "entity", ["entity"] = "sensor.blank" },
            new() { ["type"] = "entity" },
            new() { ["type"] = "daily", ["time"] = "07:30", ["timeZone"] = "UTC" },
            new() { ["type"] = "once", ["at"] = "2030-01-01T10:00:00Z" },
            new() { ["type"] = "manual" },
            new() { ["type"] = "startup" },
            Ha(new { trigger = "state", entity_id = "sensor.missing" }),
            Ha(new { trigger = "state", entity_id = Array.Empty<string>() }),
            Ha(new { trigger = "numeric_state", above = 1 }),
            Ha(new { trigger = "template", value_template = "{{ is_state('sensor.test', '21') }}", entity_id = "sensor.test" }),
            Ha(new { trigger = "zone", entity_id = "sensor.test", zone = "zone.home", @event = "enter" }),
            Ha(new { trigger = "time", at = "07:00:00" }),
            Ha(new { trigger = "time_pattern", minutes = "/5" }),
            Ha(new { trigger = "event", event_type = "test" }),
            Ha(new { trigger = "sun", @event = "sunset" }),
            new() { ["type"] = "ha" } })
            Check(ManualOf(trigger).ToString() == """{"platform":"manual"}""", $"manual payload of {trigger.ToJsonString()}: {ManualOf(trigger)}");

        // A state that cannot be read, or whose attributes cannot be normalised, falls back with a warning naming the entity.
        foreach (var (reader, what) in new (Func<string, HasEntityState?>, string)[] {
            (_ => throw new InvalidOperationException("bridge down"), "bridge down"),
            (id => new() { EntityId = id, State = "21", Attributes = new() { ["forecast"] = new Dictionary<object, object> { [1] = "x" } } }, "keys must be strings") })
            foreach (var trigger in new[] { new JsonObject { ["type"] = "entity", ["entity"] = "sensor.test" }, Ha(new { trigger = "state", entity_id = "sensor.test" }) })
            {
                while (messages.TryDequeue(out _)) { }
                var payload = TriggerPayload.Manual(trigger["type"]!.GetValue<string>(), trigger, reader);
                var warning = messages.ToArray();
                Check(payload.ToString() == """{"platform":"manual"}""" && warning.Length == 1 && warning[0].Contains("'sensor.test'") && warning[0].Contains("no trigger values") && warning[0].Contains(what),
                    $"unreadable state of {trigger.ToJsonString()}: {payload} | {string.Join("|", warning)}");
            }
        try { TriggerPayload.Manual("entity", new JsonObject { ["entity"] = "sensor.test" }, _ => throw new OperationCanceledException()); throw new Exception("cancellation swallowed"); }
        catch (OperationCanceledException) { }
    }

    static JsonObject Log(string id) => N(id, "log", new { value = new { kind = "unset" } });
    static JsonObject Field(string id, string field = "state", string attribute = "") => N(id, "entity_field", new { entity = new { kind = "unset" }, field, attribute, type = "string" });

    // Runs the graph with the given payload as the current invocation and returns what its log nodes wrote.
    static async Task<string[]> Logged(JsonObject graph, InvocationCause cause, JsonElement payload)
    {
        while (messages.TryDequeue(out _)) { }
        var prior = InvocationContext.Current;
        try { InvocationContext.Current = new("run", cause, null, payload); await Execute(graph); }
        finally { InvocationContext.Current = prior; }
        return messages.ToArray();
    }

    static JsonObject Wired(JsonObject trigger, JsonObject[] nodes, params (string Port, string Target, string TargetPort)[] wires)
    {
        var graph = G(nodes); graph["nodes"]![0]!["config"] = trigger;
        foreach (var (port, target, targetPort) in wires) graph["edges"]!.AsArray().Add(D("t", target, targetPort, port));
        return graph;
    }

    static async Task Outputs()
    {
        // Manual and startup runs: data is the whole payload.
        foreach (var (cause, payload, platform) in new[] { (InvocationCause.Manual, TriggerPayload.Manual(), "manual"), (InvocationCause.Startup, TriggerPayload.Startup(), "startup") })
        {
            var graph = Wired(new() { ["type"] = platform }, [N("p", "get_field", new { value = new { kind = "unset" }, pointer = "/platform", type = "string" }), Log("l")], ("data", "p", "value"));
            graph["edges"]!.AsArray().Add(D("p", "l", "value"));
            Check((await Logged(graph, cause, payload)).SequenceEqual([platform]), platform + " payload reaches the data output");
        }

        // Schedule: planned is the ISO-8601 UTC occurrence.
        var daily = Wired(new() { ["type"] = "daily", ["time"] = "12:00", ["timeZone"] = "UTC" }, [Log("l")], ("planned", "l", "value"));
        Check((await Logged(daily, InvocationCause.Schedule, TriggerPayload.Schedule("noon", new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc)))).SequenceEqual(["2026-11-13T12:00:00.0000000Z"]), "schedule payload reaches planned");

        // Entity: refined entity_id and both states, attributes included.
        var current = State("sensor.test", "on"); current.OldState = State("sensor.test", "off");
        var entity = Wired(new() { ["type"] = "entity", ["entity"] = "sensor.test" },
            [N("e", "constant", new { type = "entity_id:sensor", value = new { kind = "unset" } }), Log("le"), Field("to"), Log("lt"), Field("from"), Log("lf"), Field("unit", "attribute", "unit"), Log("lu")],
            ("entity_id", "e", "value"), ("to_state", "to", "entity"), ("from_state", "from", "entity"), ("to_state", "unit", "entity"));
        foreach (var (source, target) in new[] { ("e", "le"), ("to", "lt"), ("from", "lf"), ("unit", "lu") }) entity["edges"]!.AsArray().Add(D(source, target, "value"));
        Check((await Logged(entity, InvocationCause.Entity, TriggerPayload.Entity(current, current.OldState))).SequenceEqual(["sensor.test", "on", "off", "C"]), "entity payload reaches typed outputs: " + string.Join("|", messages));

        // A null from_state produces no output; its consumer's error names it.
        var absent = Wired(new() { ["type"] = "entity", ["entity"] = "sensor.test" },
            [Field("from"), Log("lf"), Code("c", "if(!lastError!.Message.Contains(\"trigger output t.from_state\"))throw lastError; Logger.Info(\"absent \"+lastError.Message);")], ("from_state", "from", "entity"));
        absent["edges"] = new JsonArray(E("t", "lf"), E("lf", "c", "error"), D("t", "from", "entity", "from_state"), D("from", "lf", "value"));
        var logged = await Logged(absent, InvocationCause.Entity, TriggerPayload.Entity(current, null));
        Check(logged.Length == 1 && logged[0].Contains("t.from_state") && logged[0].Contains("from.entity"), "absent from_state names the trigger output: " + string.Join("|", logged));

        // A manual run of an entity trigger, a native state trigger and a native numeric_state trigger: the typed
        // outputs resolve from the current state, and the bounds a numeric_state trigger was configured with are in data.
        foreach (var (trigger, expected) in new (JsonObject, string[])[] {
            (new() { ["type"] = "entity", ["entity"] = "sensor.test" }, ["sensor.test", "21", "21", "C", "entity"]),
            (Ha(new { trigger = "state", entity_id = "sensor.test" }), ["sensor.test", "21", "21", "C", "state"]),
            (Ha(new { trigger = "numeric_state", entity_id = new[] { "sensor.test" }, above = 20 }), ["sensor.test", "21", "21", "C", "numeric_state", "20"]) })
        {
            var reads = Wired(trigger,
                [N("e", "constant", new { type = "entity_id:sensor", value = new { kind = "unset" } }), Log("le"), Field("to"), Log("lt"), Field("from"), Log("lf"), Field("unit", "attribute", "unit"), Log("lu"),
                 N("p", "get_field", new { value = new { kind = "unset" }, pointer = "/platform", type = "string" }), Log("lp")],
                ("entity_id", "e", "value"), ("to_state", "to", "entity"), ("from_state", "from", "entity"), ("to_state", "unit", "entity"), ("data", "p", "value"));
            foreach (var (source, target) in new[] { ("e", "le"), ("to", "lt"), ("from", "lf"), ("unit", "lu"), ("p", "lp") }) reads["edges"]!.AsArray().Add(D(source, target, "value"));
            if (expected.Length == 6)
            {
                reads["nodes"]!.AsArray().Add(N("a", "get_field", new { value = new { kind = "unset" }, pointer = "/above", type = "number" }));
                reads["nodes"]!.AsArray().Add(Log("la"));
                reads["edges"]!.AsArray().Add(E("lp", "la"));
                reads["edges"]!.AsArray().Add(D("t", "a", "value", "data")); reads["edges"]!.AsArray().Add(D("a", "la", "value"));
            }
            var got = await Logged(reads, InvocationCause.Manual, ManualOf(trigger));
            Check(got.SequenceEqual(expected), $"manual run of {trigger.ToJsonString()} reaches typed outputs: " + string.Join("|", got));
        }

        // A manual run of an entity that no longer exists (compiled when it did) is a plain manual run: its consumer gets the named error.
        var gone = new JsonObject { ["type"] = "entity", ["entity"] = "sensor.test" };
        var fallback = TriggerPayload.Manual("entity", gone, _ => null);
        Check(fallback.ToString() == """{"platform":"manual"}""", "unknown entity falls back");
        var missing = Wired(gone,
            [Field("to"), Log("lt"), Code("c", "if(!lastError!.Message.Contains(\"trigger output t.to_state\"))throw lastError; Logger.Info(\"missing \"+lastError.Message);")]);
        missing["edges"] = new JsonArray(E("t", "lt"), E("lt", "c", "error"), D("t", "to", "entity", "to_state"), D("to", "lt", "value"));
        logged = await Logged(missing, InvocationCause.Manual, fallback);
        Check(logged.Length == 1 && logged[0].Contains("t.to_state") && logged[0].Contains("to.entity"), "unknown entity names the trigger output: " + string.Join("|", logged));

        // Without any payload even data is absent.
        var none = Wired(new() { ["type"] = "manual" }, [N("p", "get_field", new { value = new { kind = "unset" }, pointer = "", type = "object" }), Log("lp"), Code("c", "if(!lastError!.Message.Contains(\"trigger output t.data\"))throw lastError;")], ("data", "p", "value"));
        none["edges"] = new JsonArray(E("t", "lp"), E("lp", "c", "error"), D("t", "p", "value", "data"), D("p", "lp", "value"));
        await Execute(none);

        // Native: a state trigger's as_dict states and context, and an event trigger's event fields.
        var state = new { entity_id = "light.kitchen", state = "on", attributes = new { brightness = 3 }, last_changed = "2026-11-13T10:00:00+00:00", last_updated = "2026-11-13T10:00:00+00:00" };
        var variables = JsonSerializer.SerializeToElement(new { trigger = new { platform = "state", entity_id = "light.kitchen", from_state = (object?)null, to_state = state }, context = new { id = "ctx" } });
        var native = Wired(Ha(new { trigger = "state", entity_id = "light.kitchen" }),
            [N("e", "constant", new { type = "entity_id:light", value = new { kind = "unset" } }), Log("le"), Field("to"), Log("lt"), N("ctx", "get_field", new { value = new { kind = "unset" }, pointer = "/context/id", type = "string" }), Log("lc")],
            ("entity_id", "e", "value"), ("to_state", "to", "entity"), ("data", "ctx", "value"));
        foreach (var (source, target) in new[] { ("e", "le"), ("to", "lt"), ("ctx", "lc") }) native["edges"]!.AsArray().Add(D(source, target, "value"));
        Check((await Logged(native, InvocationCause.HomeAssistantTrigger, TriggerPayload.Native(variables))).SequenceEqual(["light.kitchen", "on", "ctx"]), "native state payload reaches typed outputs: " + string.Join("|", messages));
        var fired = JsonSerializer.SerializeToElement(new { trigger = new { platform = "event", @event = new { event_type = "test_event", data = new { value = 7 } } } });
        var events = Wired(Ha(new { trigger = "event", event_type = "test_event" }), [Log("lt"), N("v", "get_field", new { value = new { kind = "unset" }, pointer = "/value", type = "number" }), Log("lv")], ("event_type", "lt", "value"), ("event_data", "v", "value"));
        events["edges"]!.AsArray().Add(D("v", "lv", "value"));
        Check((await Logged(events, InvocationCause.HomeAssistantTrigger, TriggerPayload.Native(fired))).SequenceEqual(["test_event", "7"]), "native event payload reaches typed outputs: " + string.Join("|", messages));

        // A value of another type fails only its consumer: a webhook body that is an array is not an object.
        var hook = JsonSerializer.SerializeToElement(new { trigger = new { platform = "webhook", json = new[] { 1, 2 }, query = new { a = "b" } } });
        var webhook = Wired(Ha(new { trigger = "webhook", webhook_id = "hook" }),
            [N("q", "get_field", new { value = new { kind = "unset" }, pointer = "/a", type = "string" }), Log("lq"), N("j", "get_field", new { value = new { kind = "unset" }, pointer = "", type = "object" }), Log("lj"),
             Code("c", "if(!lastError!.Message.Contains(\"trigger output t.json cannot be read\"))throw lastError; Logger.Info(\"unreadable\");")]);
        webhook["edges"] = new JsonArray(E("t", "lq"), E("lq", "lj"), E("lj", "c", "error"), D("t", "q", "value", "query"), D("q", "lq", "value"), D("t", "j", "value", "json"), D("j", "lj", "value"));
        Check((await Logged(webhook, InvocationCause.HomeAssistantTrigger, TriggerPayload.Native(hook))).SequenceEqual(["b", "unreadable"]), "unreadable output fails only its consumer: " + string.Join("|", messages));
    }

    // Every cause, through the real runner: payloads are built by the caller and reach the graph.
    static async Task Invocations()
    {
        var diagnostics = new DiagnosticsProvider();
        var nodes = new JsonArray(); var edges = new JsonArray();
        void Root(string id, JsonObject trigger, string port, string? pointer = null)
        {
            nodes.Add(N(id, "trigger", trigger)); nodes.Add(N(id + "_log", "log", new { value = new { kind = "unset" } }));
            if (pointer == null) { edges.Add(E(id, id + "_log")); edges.Add(D(id, id + "_log", "value", port)); return; }
            nodes.Add(N(id + "_get", "get_field", new { value = new { kind = "unset" }, pointer, type = "string" }));
            edges.Add(E(id, id + "_log")); edges.Add(D(id, id + "_get", "value", port)); edges.Add(D(id + "_get", id + "_log", "value"));
        }
        Root("s", new() { ["type"] = "startup" }, "data", "/platform");
        Root("m", new() { ["type"] = "manual" }, "data", "/platform");
        Root("e", new() { ["type"] = "entity", ["entity"] = "sensor.test", ["manual"] = false }, "entity_id");
        Root("d", new() { ["type"] = "daily", ["time"] = "12:00", ["timeZone"] = "UTC", ["manual"] = false }, "planned");
        Root("h", new() { ["type"] = "ha", ["manual"] = false, ["trigger"] = new JsonObject { ["trigger"] = "event", ["event_type"] = "test_event" } }, "event_type");
        var graph = new JsonObject { ["version"] = 5, ["name"] = "Causes", ["nodes"] = nodes, ["edges"] = edges };
        var preview = new VisualGenerator(diagnostics).Preview(graph);
        Check(preview.Valid, "cause graph compiles: " + JsonSerializer.Serialize(preview.Diagnostics));
        var compiled = await new CodeCompiler(diagnostics).CompileSingleFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cs"), preview.Source);

        var (schedule, registerSchedule) = ((Func<string, Task>?)null, PyInterop.RegisterSchedule);
        var (native, beginNative, removeNative) = ((Func<string, Task>?)null, PyInterop.BeginRegisterHaTriggers, PyInterop.BeginRemoveHaTriggers);
        PyInterop.RegisterSchedule = (_, fire) => { schedule = fire; return () => { }; };
        PyInterop.BeginRegisterHaTriggers = (_, _, fire, success, _) => { native = fire; success("lease"); };
        PyInterop.BeginRemoveHaTriggers = (_, done, _) => done();
        var (subscribe, unsubscribe) = (PyInterop.SubscribeToEntityTracking, PyInterop.UnSubscribeFromEntityTracking);
        PyInterop.SubscribeToEntityTracking = _ => { }; PyInterop.UnSubscribeFromEntityTracking = _ => { };
        var manager = new UserScriptManager();
        try
        {
            while (messages.TryDequeue(out _)) { }
            var failures = new List<string>();
            await manager.LoadUserScripts([compiled], (_, error) => failures.Add(error.ToString()));
            Check(failures.Count == 0, "cause graph activates: " + string.Join("; ", failures));
            var script = manager.GetUserScripts().Single();
            await script.Classes.Single().StartupCompletion;
            Check(messages.Contains("startup"), "startup payload reaches the graph: " + string.Join("|", messages));
            var current = State("sensor.test", "on"); current.OldState = State("sensor.test", "off");
            await manager.RunEntries("sensor.test", new(current));
            Check(messages.Contains("sensor.test"), "entity payload reaches the graph: " + string.Join("|", messages));
            await schedule!("2026-11-13T12:00:00+00:00");
            Check(messages.Contains("2026-11-13T12:00:00.0000000Z"), "schedule payload reaches the graph: " + string.Join("|", messages));
            await native!("""{"trigger":{"platform":"event","event":{"event_type":"test_event","data":{}}},"context":{"id":"c"}}""");
            Check(messages.Contains("test_event"), "native payload reaches the graph: " + string.Join("|", messages));
            await script.Classes.Single().RunMethod(VisualNodeCatalog.EffectiveTriggerMethod(nodes.OfType<JsonObject>().Single(n => n["id"]!.GetValue<string>() == "m")));
            Check(messages.Contains("manual"), "manual payload reaches the graph: " + string.Join("|", messages));
            Check(!messages.Any(m => m.Contains("failed")), "no invocation failed: " + string.Join("|", messages));
        }
        finally
        {
            await manager.UnloadUserScriptsAsync();
            PyInterop.RegisterSchedule = registerSchedule; PyInterop.BeginRegisterHaTriggers = beginNative; PyInterop.BeginRemoveHaTriggers = removeNative;
            PyInterop.SubscribeToEntityTracking = subscribe; PyInterop.UnSubscribeFromEntityTracking = unsubscribe;
        }
    }
}
