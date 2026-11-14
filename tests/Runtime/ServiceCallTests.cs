using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using static VisualExecutionTests;

/// <summary>
/// Task 25b: the typed entities and service helpers are gone; entity triggers use a plain entity reference; node code calls
/// services through <c>context.Services</c>, generated from the live schema united with the last persisted one.
/// </summary>
static class ServiceCallTests
{
    static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name));
    static ServiceSchemaCatalog Schema(string json) => ServiceSchemaCatalog.Parse(json, _ => { });
    static string Source(ServiceSchemaCatalog schema) => ServiceGenerator.Generate(schema.Items.Select(p => KeyValuePair.Create(p.Key, (JsonObject)p.Value!)));
    static DiagnosticsProvider Provider(ServiceSchemaCatalog schema) { var provider = new DiagnosticsProvider(); provider.UseServiceSchema(schema); return provider; }

    const string Script = """
        {"script":{"good_night":{"name":"Good night","description":"Turns everything off.","fields":{
          "delay_minutes":{"name":"Delay","description":"How long to wait first.","required":true,"selector":{"number":{"min":0,"max":60,"unit_of_measurement":"min"}}},
          "rooms":{"name":"Rooms","selector":{"area":{"multiple":true}}},
          "announce":{"name":"Announce","selector":{"boolean":{}}},
          "wait":{"name":"Wait","selector":{"duration":{}}}}}}}
        """;
    static readonly string Calls = """
        {"light":{"turn_on":{"name":"Turn on","target":{"entity":{"domain":"light"}},"fields":{
            "brightness_pct":{"name":"Brightness","selector":{"number":{"min":0,"max":100,"unit_of_measurement":"%"}}},
            "transition":{"name":"Transition","selector":{"number":{"min":0,"max":300,"unit_of_measurement":"seconds"}}},
            "flash":{"name":"Flash","selector":{"select":{"options":["long","short"]}}}}}},
         "weather":{"get_forecasts":{"name":"Get forecasts","target":{"entity":{"domain":"weather"}},"response":{"optional":false},"fields":{
            "type":{"name":"Type","required":true,"selector":{"select":{"options":["daily","hourly","twice_daily"]}}}}}},
         "todo":{"get_items":{"target":{"entity":{"domain":"todo"}},"response":{"optional":true},"fields":{}}}}
        """[..^1] + "," + Script[1..];

    [RuntimeSuite("--service-calls", Order = 22)]
    public static async Task Run()
    {
        var previousPath = HassPath.HassConfiguration; HassPath.HassConfiguration = Path.Combine(Path.GetTempPath(), "service-calls-" + Guid.NewGuid());
        var (call, respond, log, entity) = (PyInterop.CallService, PyInterop.CallServiceWithResponse, PyInterop.Log, PyInterop.Entity);
        try
        {
            FixtureCompiles();
            Naming();
            Targets();
            await Calling();
            await Cancelled();
            Removed();
            await Startup();
            await EntityTrigger();
            await HostileText();
            await Rejected();
            Attribution();
            await UnknownEntities();
            await Recovers();
            await LibraryGeneration();
        }
        finally { (PyInterop.CallService, PyInterop.CallServiceWithResponse, PyInterop.Log, PyInterop.Entity) = (call, respond, log, entity); HassPath.HassConfiguration = previousPath; }
    }

    static Diagnostic[] Compile(DiagnosticsProvider provider, string source = "class Empty { }") =>
        CSharpCompilation.Create("Check", [CSharpSyntaxTree.ParseText(DiagnosticsProvider.GlobalUsings + source, new CSharpParseOptions(LanguageVersion.Latest)), provider.ServicesSyntaxTree()],
                NodeLibraryCompiler.FrameworkReferences(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable))
            .GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning).ToArray();

    // The services tree alone, its doc comments checked: a warning is malformed XML (or a doc on the wrong thing).
    static Diagnostic[] Documented(string source) =>
        CSharpCompilation.Create("Documented", [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest, documentationMode: DocumentationMode.Diagnose))],
                NodeLibraryCompiler.FrameworkReferences(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable))
            .GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning && d.Id != "CS1591").ToArray();

    // The services fixture (every selector, sections, odd texts, the three response kinds) compiles without a diagnostic, byte for byte the same each time.
    static void FixtureCompiles()
    {
        var fixture = Fixture("ha-service-descriptions.json");
        var provider = Provider(Schema(fixture));
        var diagnostics = Compile(provider);
        Check(diagnostics.Length == 0, "the generated service calls have diagnostics: " + string.Join("\n", diagnostics.Select(d => d.ToString())));
        var source = provider.ServicesSyntaxTree().ToString();
        // Key order of the input does not matter: the reversed fixture gives the same source.
        var reversed = new JsonObject(JsonNode.Parse(fixture)!.AsObject().Reverse().Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
        Check(Source(Schema(fixture)) == source && Source(Schema(reversed.ToJsonString())) == source, "the generated source is not byte-stable");
        var tree = provider.ServicesSyntaxTree(); provider.UseServiceSchema(Schema(fixture));
        Check(ReferenceEquals(tree, provider.ServicesSyntaxTree()), "an unchanged schema replaced the services tree");
        var documented = Documented(source);
        Check(documented.Length == 0, "the fixture's doc comments are not well-formed XML: " + string.Join("\n", documented.Select(d => d.ToString())));
        foreach (var expected in new[] {
            "public global::HassSharp.ServiceCalls.LightServices Light => new global::HassSharp.ServiceCalls.LightServices(__context);",
            "public global::System.Threading.Tasks.Task TurnOn(global::HassSharp.ServiceTarget? target, double? transition = null, int[]? rgbColor = null, double? colorTempKelvin = null, double? brightnessPct = null,",
            "public async global::System.Threading.Tasks.Task<global::System.Text.Json.JsonElement> GetForecasts(global::HassSharp.ServiceTarget? target, string type)",
            // Optional response: a plain method and one that gives the response.
            "public global::System.Threading.Tasks.Task RespondOptionally()", "public async global::System.Threading.Tasks.Task<global::System.Text.Json.JsonElement> RespondOptionallyWithResponse()",
            // Selector mapping: a single entity is a string, several types are object, a list of areas is an array, a target field is a ServiceTarget.
            "string? light = null", "object? lights = null", "global::HassSharp.ServiceTarget? targets = null", "object? labels = null", "object? wait = null", "object[]? pair = null",
        })
            Check(source.Contains(expected), "the generated source lacks: " + expected);
        Console.WriteLine("PASS service calls: the fixture's typed calls compile without diagnostics and are byte-stable");
    }

    // Names: PascalCase domains and services, camelCase fields, made valid and told apart deterministically; ids are sent as they are.
    static void Naming()
    {
        var schema = Schema("""
            {"media_player":{"play":{"fields":{}}},
             "media__player":{"play":{"fields":{}}},
             "services":{"reload":{"fields":{}}},
             "3d":{"print":{"fields":{}}},
             "light":{"turn_on":{"response":{"optional":true},"target":{},"fields":{
                 "class":{"selector":{"text":{}}},"1st":{"selector":{"text":{}}},"await":{"selector":{"text":{}}},"target":{"selector":{"text":{}}},
                 "brightness-pct":{"selector":{"number":{}}},"brightness_pct":{"selector":{"number":{}}},"rgb_color":{"selector":{"color_rgb":{}}},
                 "rooms":{"selector":{"text":{"multiple":true}}},"wait":{"selector":{"duration":{}}}}},
               "turn_on_with_response":{"fields":{}},"light_services":{"fields":{}},"to_string":{"fields":{}},"get_type":{"fields":{}}}}
            """);
        var source = Source(schema);
        var provider = Provider(schema);
        var diagnostics = Compile(provider);
        Check(diagnostics.Length == 0, "the naming schema does not compile cleanly: " + string.Join("\n", diagnostics.Select(d => d.ToString())));
        foreach (var expected in new[] {
            // Domains: the first id of a name in ordinal order keeps it ("media__player" < "media_player"); Services is the root's own name.
            "MediaPlayerServices MediaPlayer =>", "MediaPlayer2Services MediaPlayer2 =>", "Services2Services Services2 =>", "_3dServices _3d =>",
            // Services: the class's own name and object's members are taken; an optional response's second method gets the next free name.
            "Task TurnOn(", "Task<global::System.Text.Json.JsonElement> TurnOnWithResponse2(", "Task TurnOnWithResponse()", "Task LightServices2()", "Task ToString2()", "Task GetType2()",
            // Fields: keywords escaped, a leading digit prefixed, the target's own name taken, collisions numbered in ordinal order of the ids.
            "string? @class = null", "string? _1st = null", "string? @await = null", "string? target2 = null", "double? brightnessPct = null", "double? brightnessPct2 = null",
            "int[]? rgbColor = null", "string[]? rooms = null", "object? wait = null",
            "__data[\"brightness-pct\"]", "__data[\"brightness_pct\"]", "__data[\"class\"]", "__data[\"1st\"]", "__data[\"target\"]",
            "<param name=\"class\">", "__context.CallService(\"media__player.play\", null, __data)", "__context.CallService(\"3d.print\", null, __data)",
        })
            Check(source.Contains(expected), "the naming rules: the source lacks " + expected + "\n" + source);
        Check(ServiceGenerator.Words("brightness_pct", pascal: false) == "brightnessPct" && ServiceGenerator.Words("brightness_pct", pascal: true) == "BrightnessPct"
            && ServiceGenerator.Words("a​b", pascal: true) == "AB" && ServiceGenerator.Words("!", pascal: false) == "_" && ServiceGenerator.Words("9lives", pascal: true) == "_9lives", "word rules");
        Console.WriteLine("PASS service calls: naming, escapes and collision suffixes are deterministic, and the original ids are sent");
    }

    static void Targets()
    {
        static string Json(ServiceTarget target) => target.ToJson().ToJsonString();
        ServiceTarget one = "light.kitchen", many = new[] { "light.a", "light.b" }, list = new List<string> { "light.c" };
        Check(Json(one) == """{"entity_id":"light.kitchen"}""" && Json(many) == """{"entity_id":["light.a","light.b"]}""" && Json(list) == """{"entity_id":["light.c"]}""", "entity conversions");
        Check(Json(ServiceTarget.Areas("kitchen")) == """{"area_id":["kitchen"]}""" && Json(ServiceTarget.Devices("d1")) == """{"device_id":["d1"]}"""
            && Json(ServiceTarget.Floors("ground")) == """{"floor_id":["ground"]}""" && Json(ServiceTarget.Labels("night", "away")) == """{"label_id":["night","away"]}"""
            && Json(ServiceTarget.Entities("light.a")) == """{"entity_id":["light.a"]}""", "named targets");
        Check(Json(new ServiceTarget { AreaIds = ["kitchen"], LabelIds = ["night"] }) == """{"area_id":["kitchen"],"label_id":["night"]}""" && Json(new ServiceTarget()) == "{}", "combined targets");
        ServiceTarget expression = ["light.d", "light.e"];
        Check(Json(expression) == """{"entity_id":["light.d","light.e"]}""", "a collection expression of entity ids");
        ServiceTarget? none = (string?)null;
        Check(none == null, "a null entity id is no target");
        Console.WriteLine("PASS service calls: targets from an entity id, arrays and lists, and areas, devices, floors and labels");
    }

    static readonly List<(string Service, JsonObject Payload, bool Response)> Made = [];
    static void Double(string response = """{"weather.home":{"forecast":[{"temperature":3}]}}""")
    {
        Made.Clear();
        PyInterop.Log = (_, _) => { };
        PyInterop.CallService = (domain, name, json, done, _) => { lock (Made) Made.Add((domain + "." + name, JsonNode.Parse(json!)!.AsObject(), false)); done?.Invoke(); };
        PyInterop.CallServiceWithResponse = (domain, name, json, done, _) => { lock (Made) Made.Add((domain + "." + name, JsonNode.Parse(json!)!.AsObject(), true)); done(response); };
    }

    static string Node(string body) => "public static class CustomNode\n{\n    [Node(\"Call\")]\n    public static async Task Call(NodeContext context, string light)\n    {\n        " + body + "\n    }\n}";
    static JsonObject CustomGraph(string body) =>
        G(N("c", "custom", new JsonObject { ["source"] = Node(body), ["arguments"] = new JsonObject { ["light"] = Lit("string", "light.kitchen") } }));

    // A Custom C# node runs generated calls through NodeContext.CallService, against the suite's service double.
    static async Task Run(DiagnosticsProvider diagnostics, string body, bool cancel = false)
    {
        var snapshot = new NodeLibraryCompiler(diagnostics).Preview([]).Snapshot;
        var graph = new VisualGenerator(diagnostics).DeriveSignatures(CustomGraph(body), snapshot);
        var preview = new VisualGenerator(diagnostics).Preview(graph, snapshot);
        Check(preview.Valid, "the node does not compile: " + JsonSerializer.Serialize(preview.Diagnostics));
        var compiled = await new CodeCompiler(diagnostics).CompileSingleFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cs"), preview.Source);
        try
        {
            var instance = (Automation)Activator.CreateInstance(compiled.Assembly.GetType("VisualAutomation")!)!;
            using var cts = new CancellationTokenSource(); instance._cancellationToken.Value = cts.Token;
            var task = (Task)instance.GetType().GetMethod("Run_74")!.Invoke(instance, null)!;
            if (cancel) { cts.CancelAfter(50); try { await task.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("the call ignored the run's cancellation"); } catch (OperationCanceledException) { } }
            else await task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { compiled.Unload(); }
    }

    static void Expect(int index, string service, string payload, bool response, string what)
    {
        var made = Made[index];
        Check(made.Service == service && made.Response == response && JsonNode.DeepEquals(made.Payload, JsonNode.Parse(payload)), $"{what}: got {made.Service} {made.Payload.ToJsonString()} response={made.Response}");
    }

    static async Task Calling()
    {
        var diagnostics = Provider(Schema(Calls));
        Double();
        await Run(diagnostics, """
            await context.Services.Light.TurnOn(light, brightnessPct: 30, transition: 2);
            await context.Services.Light.TurnOn(light);
            await context.Services.Light.TurnOn(new[] { "light.a", "light.b" }, flash: "short");
            await context.Services.Light.TurnOn(ServiceTarget.Areas("kitchen"));
            await context.Services.Light.TurnOn(null);
            var forecast = await context.Services.Weather.GetForecasts("weather.home", type: "daily");
            if (forecast.GetProperty("weather.home").GetProperty("forecast")[0].GetProperty("temperature").GetInt32() != 3) throw new Exception("response lost");
            await context.Services.Todo.GetItems("todo.shopping");
            var items = await context.Services.Todo.GetItemsWithResponse("todo.shopping");
            await context.Services.Script.GoodNight(delayMinutes: 5);
            await context.Services.Script.GoodNight(5, rooms: new[] { "kitchen", "hall" }, announce: false);
            await context.CallService("light.turn_off", "light.kitchen");
            await context.Services.Light.TurnOn(["light.a", "light.b"]);
            await context.Services.Script.GoodNight(1, rooms: DayOfWeek.Monday, wait: new { Minutes = 5, HalfSeconds = 1 });
            """);
        Check(Made.Count == 13, "calls made: " + Made.Count);
        // Only the arguments given are sent; the target is flattened into the call as the Call service step does it.
        Expect(0, "light.turn_on", """{"entity_id":"light.kitchen","brightness_pct":30,"transition":2}""", false, "a target and two optional fields");
        Expect(1, "light.turn_on", """{"entity_id":"light.kitchen"}""", false, "no optional field given");
        Expect(2, "light.turn_on", """{"entity_id":["light.a","light.b"],"flash":"short"}""", false, "an array target");
        Expect(3, "light.turn_on", """{"area_id":["kitchen"]}""", false, "an area target");
        Expect(4, "light.turn_on", "{}", false, "no target");
        // Response only, optional (both methods), a script with fields, and the run-time id form.
        Expect(5, "weather.get_forecasts", """{"entity_id":"weather.home","type":"daily"}""", true, "a required field and a response");
        Expect(6, "todo.get_items", """{"entity_id":"todo.shopping"}""", false, "an optional response, not asked for");
        Expect(7, "todo.get_items", """{"entity_id":"todo.shopping"}""", true, "an optional response, asked for");
        Expect(8, "script.good_night", """{"delay_minutes":5}""", false, "a script with a required field");
        Expect(9, "script.good_night", """{"delay_minutes":5,"rooms":["kitchen","hall"],"announce":false}""", false, "a script with every field");
        Expect(10, "light.turn_off", """{"entity_id":"light.kitchen"}""", false, "CallService by id");
        Expect(11, "light.turn_on", """{"entity_id":["light.a","light.b"]}""", false, "a collection expression as the target");
        // Values go out as every service call sends them: snake_case member names, enums by name.
        Expect(12, "script.good_night", """{"delay_minutes":1,"rooms":"monday","wait":{"minutes":5,"half_seconds":1}}""", false, "an enum and an anonymous object");
        var source = diagnostics.ServicesSyntaxTree().ToString();
        Check(source.Contains("public global::System.Threading.Tasks.Task GoodNight(double delayMinutes, object? rooms = null, bool? announce = null, object? wait = null)"), "a script's fields: required first, then optional");
        Check(source.Contains("/// Delay (<c>delay_minutes</c>). How long to wait first. From 0 to 60 min.") && source.Contains("/// Type (<c>type</c>). One of <c>daily</c>, <c>hourly</c>, <c>twice_daily</c>."), "parameter docs with ranges, units and allowed values");
        Console.WriteLine("PASS service calls: a node calls typed services (targets, optional and required fields, three response kinds, a script) through the context");
    }

    static async Task Cancelled()
    {
        var diagnostics = Provider(Schema(Calls));
        Double();
        var pending = new TaskCompletionSource();
        PyInterop.CallService = (domain, name, json, done, _) => { lock (Made) Made.Add((domain + "." + name, JsonNode.Parse(json!)!.AsObject(), false)); pending.Task.ContinueWith(_ => done?.Invoke()); };
        await Run(diagnostics, "await context.Services.Light.TurnOn(light);", cancel: true);
        Check(Made.Single().Service == "light.turn_on", "the cancelled call was not made");
        pending.SetResult();
        Console.WriteLine("PASS service calls: a typed call is cancelled by the context's token");
    }

    // Code that used the typed entities or the old helpers no longer compiles: the ordinary compiler error, on the node.
    static void Removed()
    {
        var diagnostics = Provider(Schema(Calls));
        foreach (var (body, message) in new[] {
            ("var kitchen = Entities.Light.Kitchen; await Task.CompletedTask;", "The name 'Entities' does not exist"),
            ("await HassServices.Light.TurnOn(light).RunAsync();", "'HassServices' is inaccessible"),
            ("await new ServiceCall(\"light\", \"turn_on\", null).RunAsync();", "'ServiceCall' could not be found"),
            ("await context.Services.Light.Toggle(light);", "does not contain a definition for 'Toggle'"),
        })
        {
            var snapshot = new NodeLibraryCompiler(diagnostics).Preview([], [new CustomNodeSource("c", Node(body))]).Snapshot;
            var errors = snapshot.Custom["c"].Diagnostics;
            Check(errors.Any(d => d.Severity == 3 && d.Message.Contains(message) && d.StartLine == 6), $"{body}: " + string.Join(" | ", errors.Select(d => $"{d.StartLine}:{d.StartColumn} {d.Message}")));
            var preview = new VisualGenerator(diagnostics).Preview(CustomGraph(body), new NodeLibraryCompiler(diagnostics).Preview([]).Snapshot);
            Check(!preview.Valid && preview.Diagnostics.Any(d => d.NodeId == "c" && d.InputId == "source" && d.Message.Contains(message)), $"{body} in a graph: " + JsonSerializer.Serialize(preview.Diagnostics));
        }
        Console.WriteLine("PASS service calls: Entities, HassServices, ServiceCall and a missing service method fail to compile on the node");
    }

    static async Task Startup()
    {
        Double();
        var root = Path.Combine(Path.GetTempPath(), "service-calls-startup-" + Guid.NewGuid());
        var file = Path.Combine(root, ".storage", "hass_sharp", "service_schema.json");
        var logs = new List<string>();
        PyInterop.Log = (_, message) => { lock (logs) logs.Add(message); };
        const string Body = "await context.Services.Light.TurnOn(light, brightnessPct: 30);";
        async Task<JsonNode> Visual(HassSharpManager api, object request) => JsonNode.Parse(await api.VisualRequest(JsonSerializer.Serialize(request)))!;
        async Task<JsonNode> Preview(HassSharpManager api) => JsonNode.Parse(await api.NodeLibraryRequest(JsonSerializer.Serialize(new { operation = "custom_preview", nodeId = "c", source = Node(Body) })))!["result"]!;
        var first = new HassSharpManager(root);
        try
        {
            // Nothing persisted: logged once, never a failure.
            await first.Init();
            Check(logs.Count(l => l.Contains("No saved service schema yet")) == 1, "a missing schema file is not logged once: " + string.Join(" | ", logs));
            first.SetServiceDescriptions(Calls);
            var saved = await Visual(first, new { operation = "save", id = "dim", graph = CustomGraph(Body), expectedRevision = 0, deriveSignatures = true });
            Check(saved["ok"]!.GetValue<bool>() && saved["result"]!["active"]!.GetValue<bool>(), "a node calling a live service is not saved and active: " + saved.ToJsonString());
            var persisted = JsonNode.Parse(File.ReadAllText(file))!;
            Check(persisted["version"]!.GetValue<int>() == 1 && persisted["items"]!["light.turn_on"] != null && persisted["items"]!["script.good_night"] != null, "the schema was not persisted");
        }
        finally { await first.UnloadAsync(); }

        logs.Clear();
        var api = new HassSharpManager(root);
        try
        {
            // Restart: Init has no live service yet, and the persisted schema keeps the node compiling.
            await api.Init();
            Check(VisualTests.Loaded(api, "dim") != null, "Init with an empty live schema and a persisted one did not load the node that calls Light.TurnOn: " + string.Join(" | ", logs));
            Check(!logs.Any(l => l.Contains("service schema")), "a readable schema file was reported: " + string.Join(" | ", logs));
            // A push while Home Assistant is starting that lacks light keeps it (the union); the live definition of a service wins.
            api.SetServiceDescriptions("""{"light":{"turn_off":{"fields":{}}},"weather":{"get_forecasts":{"target":{},"response":{"optional":false},"fields":{"kind":{"selector":{"text":{}}}}}}}""");
            var preview = await Preview(api);
            Check(preview["valid"]!.GetValue<bool>(), "a push before Home Assistant has started dropped a service: " + preview.ToJsonString());
            var source = ((DiagnosticsProvider)typeof(HassSharpManager).GetField("_diagnosticsProvider", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(api)!).ServicesSyntaxTree().ToString();
            Check(source.Contains("Task TurnOff(") && source.Contains("Task TurnOn(") && source.Contains("GetForecasts(global::HassSharp.ServiceTarget? target, string? kind = null)") && !source.Contains("string type"), "the union, with the live definition winning");

            // Once Home Assistant has started, a push that lacks light.turn_on drops it: the node then fails to compile, on the node.
            api.SetServiceDescriptions("""{"light":{"turn_off":{"fields":{}}}}""", started: true);
            preview = await Preview(api);
            Check(!preview["valid"]!.GetValue<bool>() && preview["diagnostics"]!.AsArray().Any(d => d!["message"]!.GetValue<string>().Contains("does not contain a definition for 'TurnOn'")), "a dropped service still compiles: " + preview.ToJsonString());
            var persisted = JsonNode.Parse(File.ReadAllText(file))!["items"]!.AsObject();
            Check(persisted.Select(p => p.Key).SequenceEqual(["light.turn_off"]), "the persisted schema still has dropped services: " + string.Join(",", persisted.Select(p => p.Key)));
            var resave = await Visual(api, new { operation = "preview", graph = CustomGraph(Body), deriveSignatures = true });
            Check(resave["result"]!["valid"]?.GetValue<bool>() == false && resave["result"]!["diagnostics"]!.AsArray().Any(d => d!["nodeId"]?.GetValue<string>() == "c" && d["inputId"]?.GetValue<string>() == "source"), "the graph preview does not fail on the node: " + resave.ToJsonString());

            // The running automation is not stopped: its compiled copy calls the service by id.
            var loaded = VisualTests.Loaded(api, "dim");
            Check(loaded != null, "a schema change stopped a running automation");
            var run = await Visual(api, new { operation = "run", id = "dim", triggerId = "t" });
            Check(run["ok"]!.GetValue<bool>(), "Run now refused after the schema change: " + run.ToJsonString());
            for (var i = 0; i < 200 && !Made.Any(); i++) await Task.Delay(10);
            Expect(0, "light.turn_on", """{"entity_id":"light.kitchen","brightness_pct":30}""", false, "the running automation's call after its service was dropped");
            Check(ReferenceEquals(loaded, VisualTests.Loaded(api, "dim")), "the schema change replaced the running automation");
        }
        finally { await api.UnloadAsync(); }

        // An unreadable file means nothing persisted: logged once, and Init still loads what compiles.
        logs.Clear();
        File.WriteAllText(file, "{ not json");
        var broken = new HassSharpManager(root);
        try
        {
            await broken.Init();
            Check(logs.Count(l => l.Contains("Ignoring the saved service schema")) == 1, "an unreadable schema file is not logged once: " + string.Join(" | ", logs));
            Check(VisualTests.Loaded(broken, "dim") == null && (await Visual(broken, new { operation = "read", id = "dim" }))["result"]!["activationError"]!.GetValue<string>().Contains("does not contain a definition for 'Light'"),
                "without a schema, the node that calls Light.TurnOn should fail at startup with the compiler's error");
            broken.SetServiceDescriptions(Calls);
            Check(JsonNode.Parse(File.ReadAllText(file))!["items"]!["light.turn_on"] != null, "a later push does not replace an unreadable file");
        }
        finally { await broken.UnloadAsync(); Directory.Delete(root, true); }
        Console.WriteLine("PASS service calls: startup unites the persisted and live schemas, drops a service only after Home Assistant has started, never stops a running automation");
    }

    // An entity trigger through the plain reference: the subscription, the run, its payload and label, and a manual run.
    static async Task EntityTrigger()
    {
        Double();
        var root = Path.Combine(Path.GetTempPath(), "service-calls-entity-" + Guid.NewGuid());
        var subscribe = PyInterop.SubscribeToEntityTracking; var subscribed = new List<string>();
        PyInterop.SubscribeToEntityTracking = id => { lock (subscribed) subscribed.Add(id); };
        PyInterop.Entity = id => id == "binary_sensor.door" ? new() { EntityId = id, State = "on", LastChanged = 1_700_000_000, LastReported = 1_700_000_000 } : null;
        var api = new HassSharpManager(root);
        try
        {
            await api.Init();
            var graph = JsonNode.Parse("""
                {"version":5,"name":"Door","nodes":[
                  {"id":"t","kind":"trigger","label":"Front door","config":{"type":"entity","entity":"binary_sensor.door"}},
                  {"id":"l","kind":"log","config":{"value":{"kind":"unset"}}}
                ],"edges":[{"id":"tl","source":"t","target":"l"},{"id":"d","kind":"data","source":"t","sourcePort":"entity_id","target":"l","targetPort":"value"}]}
                """)!;
            var saved = JsonNode.Parse(await api.VisualRequest(JsonSerializer.Serialize(new { operation = "save", id = "door", graph, expectedRevision = 0 })))!;
            Check(saved["result"]?["active"]?.GetValue<bool>() == true, "the entity trigger is not active: " + saved.ToJsonString());
            Check(saved["result"]!["document"]!["source"]!.GetValue<string>().Contains("Entities:[new global::HassSharp.EntityReference(\"binary_sensor.door\")]"), "the generator does not emit the plain reference");
            Check(subscribed.Contains("binary_sensor.door"), "no subscription for the entity: " + string.Join(",", subscribed));
            var automation = saved["result"]!["document"]!["automationId"]!.GetValue<string>();
            JsonNode[] Runs() => JsonNode.Parse(api.GetRunHistory(automation, 0, 10, null))!["runs"]!.AsArray().Select(r => r!).ToArray();
            await api.OnTrackedEntityChange("binary_sensor.door", new() { EntityId = "binary_sensor.door", State = "on" }, new() { EntityId = "binary_sensor.door", State = "off" });
            for (var i = 0; i < 300 && !Runs().Any(r => r["endUtc"] != null); i++) await Task.Delay(10);
            var run = Runs().Single();
            Check(run["status"]!.GetValue<string>() == "Succeeded" && run["cause"]!.GetValue<string>() == "Entity" && run["actionLabel"]!.GetValue<string>() == "Front door" && run["triggerNodeId"]!.GetValue<string>() == "t", "the entity run: " + run.ToJsonString());
            var trace = JsonNode.Parse(api.GetRunTrace(run["runId"]!.GetValue<string>()))!["trace"]!["trigger"]!;
            Check(trace["platform"]!.GetValue<string>() == "entity" && trace["entity_id"]!.GetValue<string>() == "binary_sensor.door" && trace["to_state"]!["state"]!.GetValue<string>() == "on" && trace["from_state"]!["state"]!.GetValue<string>() == "off", "the entity payload: " + trace.ToJsonString());
            var manual = JsonNode.Parse(await api.VisualRequest("""{"operation":"run","id":"door","triggerId":"t"}"""))!["result"]!["runId"]!.GetValue<string>();
            for (var i = 0; i < 300 && JsonNode.Parse(api.GetRunTrace(manual))!["run"]!["endUtc"] == null; i++) await Task.Delay(10);
            var manualRun = JsonNode.Parse(api.GetRunTrace(manual))!;
            Check(manualRun["run"]!["cause"]!.GetValue<string>() == "Manual" && manualRun["run"]!["trigger"]!.ToJsonString() == """{"kind":"manual","entityId":"binary_sensor.door"}"""
                && manualRun["trace"]!["trigger"]!["manual"]!.GetValue<bool>() && manualRun["trace"]!["trigger"]!["to_state"]!["state"]!.GetValue<string>() == "on", "the manual run payload: " + manualRun.ToJsonString());
        }
        finally { await api.UnloadAsync(); PyInterop.SubscribeToEntityTracking = subscribe; Directory.Delete(root, true); }
        Console.WriteLine("PASS service calls: an entity trigger through the plain reference subscribes, runs with its payload and label, and runs manually");
    }

    static DiagnosticsProvider ProviderOf(HassSharpManager api) =>
        (DiagnosticsProvider)typeof(HassSharpManager).GetField("_diagnosticsProvider", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(api)!;

    // Schema text is untrusted: line breaks (\n, \r, U+0085, U+2028, U+2029), control characters and markup in a key, a
    // name, a unit, an option and a target domain stay inside comments and literals; the ids are sent as they are.
    static async Task HostileText()
    {
        const string Key = "key\nclass InjectedKey {}\n//";
        var schema = Schema("""
            {"evil":{"run":{"name":"Run\nclass InjectedName {}","description":"a\u2028class InjectedDescription {} b\u0000</summary>",
               "target":{"entity":{"domain":"light\npublic class InjectedDomain {}"}},"fields":{
               "key\nclass InjectedKey {}\n//":{"name":"Key\u2029class InjectedLabel {}","selector":{"text":{}}},
               "level":{"name":"Level\u0085class InjectedLevel {}","selector":{"number":{"min":0,"max":5,"unit_of_measurement":"%\r\nclass InjectedUnit {}"}}},
               "mode":{"selector":{"select":{"options":["a\u2029class InjectedOption {}","b\u0007<c>"]}}}}}}}
            """);
        var provider = Provider(schema);
        var source = provider.ServicesSyntaxTree().ToString();
        var diagnostics = Compile(provider);
        Check(diagnostics.Length == 0 && Documented(source).Length == 0, "hostile schema text broke the generated calls: " + string.Join("\n", diagnostics.Concat(Documented(source)).Select(d => d.ToString())));
        var compilation = CSharpCompilation.Create("Hostile", [provider.ServicesSyntaxTree()], NodeLibraryCompiler.FrameworkReferences());
        foreach (var injected in new[] { "InjectedKey", "InjectedName", "InjectedDescription", "InjectedDomain", "InjectedLabel", "InjectedLevel", "InjectedUnit", "InjectedOption" })
            Check(!compilation.GetSymbolsWithName(injected).Any(), "schema text became code: " + injected);
        Check(source.Contains("public global::System.Threading.Tasks.Task Run(global::HassSharp.ServiceTarget? target, string? keyClassInjectedKey = null, double? level = null, string? mode = null)"), "the hostile service's method:\n" + source);
        foreach (var line in source.Split('\n').Where(l => l.TrimStart().StartsWith("///", StringComparison.Ordinal)))
            Check(!line.Any(c => char.IsControl(c) || c is '\u0085' or '\u2028' or '\u2029'), "a doc line keeps a control or line break character: " + line);
        Check(source.Contains("/// Run class InjectedName {} (<c>evil.run</c>). a class InjectedDescription {} b &lt;/summary&gt;") && source.Contains("From 0 to 5 % class InjectedUnit {}."), "the hostile texts as one line each:\n" + source);
        // What XML cannot hold at all: a lone surrogate and U+FFFE become U+FFFD; a pair is kept.
        Check(ServiceGenerator.Xml("a\ud800b\uFFFEc\ud83d\ude00 \r\n\u2028 d") == "a\uFFFDb\uFFFDc\ud83d\ude00 d", "XML-invalid characters: " + ServiceGenerator.Xml("a\ud800b\uFFFEc\ud83d\ude00 \r\n\u2028 d"));
        Double();
        await Run(provider, "await context.Services.Evil.Run(\"light.a\", keyClassInjectedKey: \"v\", mode: \"b\");");
        Check(Made.Single().Payload[Key]?.GetValue<string>() == "v", "the field is not sent under its own key: " + Made.Single().Payload.ToJsonString());
        Console.WriteLine("PASS service calls: line breaks, control characters and markup in schema text stay in doc comments and literals; the doc XML is well-formed");
    }

    // A tree that does not compile is never installed: a service whose own code is broken is left out (the rest kept);
    // a tree broken outside every service keeps the previous one. Neither is persisted; each is logged once.
    static async Task Rejected()
    {
        Double();
        var logs = new List<string>();
        PyInterop.Log = (_, message) => { lock (logs) logs.Add(message); };
        var root = Path.Combine(Path.GetTempPath(), "service-calls-rejected-" + Guid.NewGuid());
        var file = Path.Combine(root, ".storage", "hass_sharp", "service_schema.json");
        const string Body = "await context.Services.Light.TurnOn(light, brightnessPct: 30);";
        var api = new HassSharpManager(root);
        try
        {
            await api.Init();
            var provider = ProviderOf(api);
            // One service's code broken, as unescaped text would break it.
            provider.Generator = items =>
            {
                var generated = ServiceGenerator.GenerateWithSpans(items);
                return generated with { Source = generated.Source.Replace("__context.CallService(\"todo.get_items\"", "__context.NoSuchCall(\"todo.get_items\"") };
            };
            api.SetServiceDescriptions(Calls, started: true);
            var source = provider.ServicesSyntaxTree().ToString();
            Check(source.Contains("Task TurnOn(") && source.Contains("GetForecasts(") && !source.Contains("todo.get_items"), "a service whose code does not compile was not left out alone:\n" + source);
            var persisted = JsonNode.Parse(File.ReadAllText(file))!["items"]!.AsObject();
            Check(persisted.ContainsKey("light.turn_on") && !persisted.ContainsKey("todo.get_items"), "the left-out service was persisted: " + string.Join(",", persisted.Select(p => p.Key)));
            api.SetServiceDescriptions(Calls, started: true);
            Check(logs.Count(l => l.Contains("todo.get_items do not compile and were left out")) == 1, "the left-out service is not logged once: " + string.Join(" | ", logs));
            var saved = JsonNode.Parse(await api.VisualRequest(JsonSerializer.Serialize(new { operation = "save", id = "dim", graph = CustomGraph(Body), expectedRevision = 0, deriveSignatures = true })))!;
            Check(saved["result"]?["active"]?.GetValue<bool>() == true, "a node calling a kept service is not active: " + saved.ToJsonString());

            // Broken outside every service: the previous tree stays, the schema is not persisted, node code compiles.
            var before = provider.ServicesSyntaxTree();
            var file1 = File.ReadAllText(file);
            provider.Generator = items => { var generated = ServiceGenerator.GenerateWithSpans(items); return generated with { Source = generated.Source.Replace("#nullable enable", "#nullable enable\nclass Broken { int x = \"no\"; }") }; };
            var more = Calls[..^1] + ""","extra":{"thing":{"fields":{}}}}""";
            api.SetServiceDescriptions(more, started: true);
            api.SetServiceDescriptions(more, started: true);
            Check(ReferenceEquals(before, provider.ServicesSyntaxTree()), "a tree that does not compile replaced the previous one");
            Check(File.ReadAllText(file) == file1, "a rejected schema was persisted");
            Check(logs.Count(l => l.Contains("do not compile; node code keeps the previous ones")) == 1, "a rejected schema is not logged once: " + string.Join(" | ", logs));
            var preview = JsonNode.Parse(await api.VisualRequest(JsonSerializer.Serialize(new { operation = "preview", graph = CustomGraph(Body), deriveSignatures = true })))!;
            Check(preview["result"]!["valid"]!.GetValue<bool>(), "node code stopped compiling after a rejected schema: " + preview.ToJsonString());
            await api.UnloadAsync();
            await api.Init();
            Check(VisualTests.Loaded(api, "dim") != null, "after a rejected schema and a restart, the automation does not load");

            // Fixed: the next schema is accepted (one schema is checked once: the same schema again is not retried).
            provider.Generator = ServiceGenerator.GenerateWithSpans;
            api.SetServiceDescriptions(more, started: true);
            Check(ReferenceEquals(before, provider.ServicesSyntaxTree()), "a rejected schema was checked again");
            api.SetServiceDescriptions(more[..^1] + ""","more":{"thing":{"fields":{}}}}""", started: true);
            Check(provider.ServicesSyntaxTree().ToString().Contains("Task Thing(") && JsonNode.Parse(File.ReadAllText(file))!["items"]!["extra.thing"] != null, "a good schema after a rejected one was not used");
        }
        finally { await api.UnloadAsync(); Directory.Delete(root, true); }
        Console.WriteLine("PASS service calls: a tree that does not compile is never installed or persisted: a broken service is left out, a broken tree keeps the previous one, each logged once");
    }

    // A diagnostic in the services tree is the graph's, never the node whose section has the same line number.
    static void Attribution()
    {
        var provider = new DiagnosticsProvider();
        // Compiles alone; together with an automation the second VisualAutomation is an error, in the services tree.
        provider.Generator = items =>
        {
            var generated = ServiceGenerator.GenerateWithSpans(items);
            return generated with { Source = generated.Source.Replace("#nullable enable\n", "#nullable enable\npublic class VisualAutomation { }\n") };
        };
        provider.UseServiceSchema(Schema(Calls));
        Check(provider.ServicesSyntaxTree().ToString().Contains("public class VisualAutomation { }"), "the test tree was not installed");
        var preview = new VisualGenerator(provider).Preview(G(N("l", "log", new JsonObject { ["value"] = Lit("string", "x") })));
        var duplicate = preview.Diagnostics.Where(d => d.Message.Contains("VisualAutomation")).ToArray();
        Check(!preview.Valid && duplicate.Any(d => d.NodeId == null && d.Message.StartsWith("In the typed service calls: ", StringComparison.Ordinal) && d.StartLine == null), "an error in the services tree is not the graph's: " + JsonSerializer.Serialize(preview.Diagnostics));
        Check(!preview.Diagnostics.Any(d => d.NodeId != null && d.Message.StartsWith("In the typed service calls", StringComparison.Ordinal)), "an error in the services tree landed on a node: " + JsonSerializer.Serialize(preview.Diagnostics));
        var library = new NodeLibraryCompiler(provider).Preview([new NodeLibrarySource("lib", "public static class Lib { [Node(\"X\")] public static int X() => 1; }", 1)]);
        Check(library.Diagnostics.All(d => d.SourceId != "lib" || !d.Message.Contains("typed service calls")), "a services tree diagnostic was put on a library: " + JsonSerializer.Serialize(library.Diagnostics));
        Console.WriteLine("PASS service calls: diagnostics are attributed by syntax tree: one in the typed service calls is never a node's");
    }

    // An entity id Home Assistant has no state for is a warning on preview and save (websocket and MCP), never an error,
    // and not raised at Init or when the lookup is unavailable.
    static async Task UnknownEntities()
    {
        Double();
        var root = Path.Combine(Path.GetTempPath(), "service-calls-entities-" + Guid.NewGuid());
        var subscribe = PyInterop.SubscribeToEntityTracking; PyInterop.SubscribeToEntityTracking = _ => { };
        PyInterop.Entity = id => id is "binary_sensor.door" or "sensor.known" ? new() { EntityId = id, State = "on" } : null;
        var graph = JsonNode.Parse("""
            {"version":5,"name":"Typos","nodes":[
              {"id":"t","kind":"trigger","config":{"type":"entity","entity":"binary_sensor.dor"}},
              {"id":"known","kind":"read_entity","config":{"entity":{"kind":"literal","type":"entity_id","value":"sensor.known"}}},
              {"id":"missing","kind":"read_entity","config":{"entity":{"kind":"literal","type":"entity_id","value":"sensor.missing"}}},
              {"id":"svc","kind":"service","config":{"service":"light.turn_on","fields":{},"target":{"kind":"literal","type":"target","value":{"entity_id":["light.gone","all"]}}}}
            ],"edges":[{"id":"a","source":"t","target":"known"},{"id":"b","source":"known","target":"missing"},{"id":"c","source":"missing","target":"svc"}]}
            """)!;
        static string[] Unknown(JsonNode? diagnostics) => (diagnostics as JsonArray ?? []).Where(d => d?["code"]?.GetValue<string>() == "entity.unknown")
            .Select(d => $"{d!["nodeId"]}/{d["inputId"]}/{d["severity"]}: {d["message"]}").Order(StringComparer.Ordinal).ToArray();
        string[] expected = [
            "missing/entity/2: Home Assistant has no entity sensor.missing. Check the spelling: this step finds no such entity when it runs.",
            "svc/target/2: Home Assistant has no entity light.gone. Check the spelling: this step finds no such entity when it runs.",
            "t/entity/2: Home Assistant has no entity binary_sensor.dor. Check the spelling: a trigger on it never fires.",
        ];
        var api = new HassSharpManager(root);
        try
        {
            await api.Init();
            async Task<JsonNode> Visual(object request) => JsonNode.Parse(await api.VisualRequest(JsonSerializer.Serialize(request)))!;
            // The editor's preview and MCP's (which derives Custom C# signatures first) alike.
            foreach (var derive in new[] { false, true })
            {
                var preview = await Visual(new { operation = "preview", graph, deriveSignatures = derive });
                Check(preview["result"]!["valid"]!.GetValue<bool>() && Unknown(preview["result"]!["diagnostics"]).SequenceEqual(expected), $"preview (derive {derive}): " + preview.ToJsonString());
            }
            var saved = await Visual(new { operation = "save", id = "typos", graph, expectedRevision = 0, deriveSignatures = true });
            Check(saved["ok"]!.GetValue<bool>() && saved["result"]!["active"]!.GetValue<bool>() && Unknown(saved["result"]!["diagnostics"]).SequenceEqual(expected), "save: " + saved.ToJsonString());
            // Spelled right, no warning.
            graph["nodes"]![0]!["config"]!["entity"] = "binary_sensor.door";
            var fixedPreview = await Visual(new { operation = "preview", graph });
            Check(!Unknown(fixedPreview["result"]!["diagnostics"]).Any(w => w.StartsWith("t/", StringComparison.Ordinal)), "a known trigger entity is warned about: " + fixedPreview.ToJsonString());
            // The lookup failing (Home Assistant not ready): no warning at all.
            PyInterop.Entity = _ => throw new InvalidOperationException("not ready");
            var unavailable = await Visual(new { operation = "preview", graph });
            Check(unavailable["result"]!["valid"]!.GetValue<bool>() && Unknown(unavailable["result"]!["diagnostics"]).Length == 0, "warnings while the lookup is unavailable: " + unavailable.ToJsonString());
            // Init never asks: the automation loads, with no activation error.
            var asked = 0;
            PyInterop.Entity = _ => { Interlocked.Increment(ref asked); return null; };
            await api.UnloadAsync();
            await api.Init();
            Check(VisualTests.Loaded(api, "typos") != null && (await Visual(new { operation = "read", id = "typos" }))["result"]!["activationError"] == null && asked == 0, $"Init: asked {asked}");
        }
        finally { await api.UnloadAsync(); PyInterop.SubscribeToEntityTracking = subscribe; Directory.Delete(root, true); }
        Console.WriteLine("PASS service calls: an entity Home Assistant does not have is a warning on preview and save (and MCP preview), never an error, not at Init or without a lookup");
    }

    // An automation that did not load because its service was dropped loads when a push brings the service back.
    static async Task Recovers()
    {
        Double();
        var root = Path.Combine(Path.GetTempPath(), "service-calls-recovers-" + Guid.NewGuid());
        var subscribe = PyInterop.SubscribeToEntityTracking; PyInterop.SubscribeToEntityTracking = _ => { };
        PyInterop.Entity = id => new() { EntityId = id, State = "on" };
        var graph = CustomGraph("await context.Services.Light.TurnOn(light, brightnessPct: 30);");
        graph["nodes"]![0]!["config"] = new JsonObject { ["type"] = "entity", ["entity"] = "binary_sensor.door" };
        var first = new HassSharpManager(root);
        try
        {
            await first.Init();
            first.SetServiceDescriptions(Calls, started: true);
            var saved = JsonNode.Parse(await first.VisualRequest(JsonSerializer.Serialize(new { operation = "save", id = "dim", graph, expectedRevision = 0, deriveSignatures = true })))!;
            Check(saved["result"]?["active"]?.GetValue<bool>() == true, "not active: " + saved.ToJsonString());
            first.SetServiceDescriptions("""{"light":{"turn_off":{"fields":{}}}}""", started: true);
        }
        finally { await first.UnloadAsync(); }
        var api = new HassSharpManager(root);
        try
        {
            await api.Init();
            Check(VisualTests.Loaded(api, "dim") == null, "an automation calling a dropped service loaded");
            api.SetServiceDescriptions("""{"light":{"turn_off":{"fields":{}}}}""", started: true);
            await api.RetryFailedLoads();
            Check(VisualTests.Loaded(api, "dim") == null, "an unchanged schema loaded it");
            api.SetServiceDescriptions(Calls, started: true);
            await api.RetryFailedLoads();
            Check(VisualTests.Loaded(api, "dim") != null && (JsonNode.Parse(await api.VisualRequest("""{"operation":"read","id":"dim"}"""))!["result"]!["activationError"]) == null,
                "the automation did not load once its service came back");
            await api.OnTrackedEntityChange("binary_sensor.door", new() { EntityId = "binary_sensor.door", State = "on" }, new() { EntityId = "binary_sensor.door", State = "off" });
            for (var i = 0; i < 300 && !Made.Any(); i++) await Task.Delay(10);
            Expect(0, "light.turn_on", """{"entity_id":"light.kitchen","brightness_pct":30}""", false, "the trigger of the recovered automation");
        }
        finally { await api.UnloadAsync(); PyInterop.SubscribeToEntityTracking = subscribe; Directory.Delete(root, true); }
        Console.WriteLine("PASS service calls: an automation that failed to load for a missing service loads when a push brings it back, and its trigger fires");
    }

    // The saved-library generation: unchanged by pushes while the catalog cannot change with them; changed when the
    // catalog has a type the typed calls declare.
    static async Task LibraryGeneration()
    {
        Double();
        var root = Path.Combine(Path.GetTempPath(), "service-calls-generation-" + Guid.NewGuid());
        var api = new HassSharpManager(root);
        async Task<JsonNode> Library(object request) => JsonNode.Parse(await api.NodeLibraryRequest(JsonSerializer.Serialize(request)))!;
        try
        {
            await api.Init();
            api.SetServiceDescriptions(Calls, started: true);
            // An automation that calls a dropped service does not block a library save: it is reported as invalid.
            var automation = JsonNode.Parse(await api.VisualRequest(JsonSerializer.Serialize(new { operation = "save", id = "dim", graph = CustomGraph("await context.Services.Light.TurnOn(light);"), expectedRevision = 0, deriveSignatures = true })))!;
            Check(automation["result"]?["active"]?.GetValue<bool>() == true, "automation: " + automation.ToJsonString());
            api.SetServiceDescriptions("""{"light":{"turn_off":{"fields":{}}}}""", started: true);
            const string Other = "public static class Other { [Node(\"Other\")] public static int One() => 1; }";
            var other = await Library(new { operation = "save", id = "other", name = "Other", expectedRevision = 0, source = Other });
            Check(other["ok"]!.GetValue<bool>() && other["result"]!["invalid"]!.AsArray().Any(i => i!["id"]!.GetValue<string>() == "dim"), "an automation calling a dropped service blocked a library save: " + other.ToJsonString());
            api.SetServiceDescriptions(Calls, started: true);
            var save = await Library(new { operation = "save", id = "calls", name = "Calls", expectedRevision = 0, source = """
                public static class Dim
                {
                    /// <summary>Dims a light.</summary>
                    [Node("Dim")]
                    public static async Task Run(NodeContext context, string light) => await context.Services.Light.TurnOn(light, brightnessPct: 30);
                }
                """ });
            Check(save["ok"]!.GetValue<bool>(), "library save: " + save.ToJsonString());
            var before = (await Library(new { operation = "catalog" }))["result"]!;
            api.SetServiceDescriptions("""{"light":{"turn_off":{"fields":{}}}}""", started: true);
            var dropped = (await Library(new { operation = "catalog" }))["result"]!;
            Check(!dropped["valid"]!.GetValue<bool>(), "a library calling a dropped service is still valid");
            // A library that calls it does block every library save until it no longer does, or the service is back.
            var blocked = await Library(new { operation = "save", id = "other", name = "Other", expectedRevision = 1, source = Other + " " });
            Check(!blocked["ok"]!.GetValue<bool>() && blocked["error"]!["code"]!.GetValue<string>() == "validation", "a library calling a dropped service did not block a library save: " + blocked.ToJsonString());
            api.SetServiceDescriptions(Calls, started: true);
            var back = (await Library(new { operation = "catalog" }))["result"]!;
            Check(back["valid"]!.GetValue<bool>() && back["generation"]!.GetValue<string>() == before["generation"]!.GetValue<string>() && dropped["generation"]!.GetValue<string>() == before["generation"]!.GetValue<string>()
                && JsonNode.DeepEquals(back["catalog"], before["catalog"]), "the catalog changed under an unchanged generation");

            // A pin of a generated type: the catalog follows the typed calls, and so does the generation.
            var typed = await Library(new { operation = "save", id = "typed", name = "Typed", expectedRevision = 0, source = """
                public static class Pick
                {
                    [Node("Lights")]
                    public static HassSharp.ServiceCalls.LightServices Lights(NodeContext context) => context.Services.Light;
                }
                """ });
            Check(typed["ok"]!.GetValue<bool>(), "library with a generated pin type: " + typed.ToJsonString());
            var withType = (await Library(new { operation = "catalog" }))["result"]!["generation"]!.GetValue<string>();
            var listed = (await Library(new { operation = "list" }))["result"]!["generation"]!.GetValue<string>();
            Check(withType == listed, "catalog and list generations differ");
            var toggled = JsonNode.Parse(Calls)!.AsObject(); toggled["light"]!["toggle"] = new JsonObject { ["fields"] = new JsonObject() };
            api.SetServiceDescriptions(toggled.ToJsonString(), started: true);
            var changed = (await Library(new { operation = "catalog" }))["result"]!["generation"]!.GetValue<string>();
            Check(changed != withType, "a catalog with a generated type kept its generation when the typed calls changed");
        }
        finally { await api.UnloadAsync(); Directory.Delete(root, true); }
        Console.WriteLine("PASS service calls: the saved-library generation stays across pushes while the catalog cannot change, and follows the typed calls when the catalog has a generated type; a library that calls a dropped service blocks library saves, an automation does not");
    }

    // The generated source of three services, for reading (not run by default).
    [RuntimeSuite("--print-service-calls", RunByDefault = false)]
    public static void Print(string? fixture) => Console.Write(Source(Schema(fixture == null ? Calls : Fixture(fixture))));
}
