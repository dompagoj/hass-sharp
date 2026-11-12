using HassSharp;
using System.Text.Json.Nodes;

static class VisualTests
{
    public static JsonObject Graph() => JsonNode.Parse("""
    {"version":1,"name":"Motion","nodes":[
      {"id":"t","kind":"trigger","config":{"entity":"binary_sensor.motion"}},
      {"id":"c","kind":"condition","config":{"entity":"binary_sensor.motion","state":"on"}},
      {"id":"s","kind":"service","config":{"service":"light.turn_on","entity":"light.hall"}}
    ],"edges":[{"id":"tc","source":"t","target":"c"},{"id":"cs","source":"c","target":"s"}]}
    """)!.AsObject();
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static async Task Run()
    {
        var diagnostics = new DiagnosticsProvider();
        diagnostics.GenerateHassEntities([]);
        var generator = new VisualGenerator(diagnostics);
        var graph = Graph();
        var preview = generator.Preview(graph);
        Check(preview.Valid, "simple graph must compile: " + System.Text.Json.JsonSerializer.Serialize(preview.Diagnostics));
        Check(preview.Source == generator.Preview(graph).Source, "deterministic generation");
        var bad = Graph(); bad["edges"]!.AsArray().Add(JsonNode.Parse("""{"id":"cycle","source":"s","target":"c"}"""));
        Check(!generator.Preview(bad).Valid, "cycle rejected");
        bad = Graph(); bad["nodes"]![2]!["kind"] = "custom"; bad["nodes"]![2]!["config"] = JsonNode.Parse("""{"code":"this is not C#;"}""");
        Check(generator.Preview(bad).Diagnostics.Any(d => d.NodeId == "s" && d.Severity == 3), "compiler errors map to node");
        var tooLong = Graph(); tooLong["nodes"]![2]!["kind"] = "delay";
        tooLong["nodes"]![2]!["config"] = JsonNode.Parse("""{"seconds":"5000000"}""");
        Check(!generator.Preview(tooLong).Valid, "delay beyond Task.Delay maximum rejected");
        var root = Path.Combine(Path.GetTempPath(), "visual-test-" + Guid.NewGuid());
        try
        {
            var store = new VisualStore(root, generator);
            var saved = store.Save("motion", graph, 0);
            Check(saved.Revision == 1 && saved.Source == preview.Source, "atomic graph/source pair");
            try { store.Save("motion", graph, 0); throw new Exception("conflict missing"); } catch (VisualConflictException) { }
            graph["name"] = "Renamed";
            var renamed = store.Save("motion", graph, 1);
            Check(renamed.Revision == 2 && new VisualStore(root, generator).Read("motion")!.Graph["name"]!.GetValue<string>() == "Renamed", "rename durable stable ID");
            try { store.Save("motion", bad, 2); throw new Exception("invalid save allowed"); } catch (VisualValidationException) { }
            Check(store.Read("motion")!.Revision == 2, "invalid save leaves old pair intact");
            var contenders = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                try { store.Save("motion", graph, 2); return true; }
                catch (VisualConflictException) { return false; }
            })));
            Check(contenders.Count(x => x) == 1, "only one stale concurrent writer wins");
            store.Delete("motion", 3);
            Check(store.Read("motion") == null, "delete");
        }
        finally { Directory.Delete(root, true); }
        var compiler = new CodeCompiler(diagnostics);
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "visual-fixtures-" + Guid.NewGuid());
        var previousPath = HassPath.HassConfiguration;
        HassPath.HassConfiguration = fixtureRoot;
        try
        {
            var calls = 0;
            PyInterop.Entity = id => new() { EntityId = id, State = "on" };
            PyInterop.CallService = (_, _, _, done, _) => { calls++; done?.Invoke(); };
            async Task Execute(JsonObject fixture)
            {
                var generated = generator.Preview(fixture);
                Check(generated.Valid, "fixture compiles: " + System.Text.Json.JsonSerializer.Serialize(generated.Diagnostics));
                var compiled = await compiler.CompileSingleFile(Path.Combine(fixtureRoot, "fixture.cs"), generated.Source, true);
                try
                {
                    var instance = (Automation)Activator.CreateInstance(compiled.Assembly.GetType("VisualAutomation")!)!;
                    await ((Task)instance.GetType().GetMethod("Run")!.Invoke(instance, null)!).WaitAsync(TimeSpan.FromSeconds(2));
                }
                finally { compiled.Unload(); }
            }
            await Execute(Graph()); Check(calls == 1, "awaited entity-condition-service fixture executes");
            var branch = Graph(); branch["nodes"]![1]!["config"]!["state"] = "off";
            await Execute(branch); Check(calls == 1, "false condition stops effect");
            branch["edges"]![1]!["port"] = "false";
            await Execute(branch); Check(calls == 2, "false branch executes");
            var response = Graph(); response["nodes"]![2]!["config"]!["response"] = "answer";
            response["nodes"]!.AsArray().Add(JsonNode.Parse("""{"id":"verify","kind":"custom","config":{"code":"if (responses[\"answer\"].GetProperty(\"ok\").GetBoolean() != true) throw new Exception();"}}"""));
            response["edges"]!.AsArray().Add(JsonNode.Parse("""{"id":"sv","source":"s","target":"verify"}"""));
            PyInterop.CallServiceWithResponse = (_, _, _, done, _) => done("{\"ok\":true}");
            await Execute(response);
            var failure = Graph();
            failure["nodes"]!.AsArray().Add(JsonNode.Parse("""{"id":"recover","kind":"custom","config":{"code":"if (lastError == null) throw new Exception();"}}"""));
            failure["edges"]!.AsArray().Add(JsonNode.Parse("""{"id":"se","source":"s","target":"recover","port":"error"}"""));
            PyInterop.CallService = (_, _, _, _, failed) => failed!("service failed");
            await Execute(failure);
            foreach (var mode in Enum.GetNames<AutomationMode>())
            {
                var delay = Graph(); delay["nodes"]![0]!["config"]!["mode"] = mode;
                delay["nodes"]![2]!["kind"] = "delay"; delay["nodes"]![2]!["config"] = JsonNode.Parse("""{"seconds":"0"}""");
                await Execute(delay);
            }
            var cancelGraph = Graph(); cancelGraph["nodes"]![2]!["kind"] = "delay";
            cancelGraph["nodes"]![2]!["config"] = JsonNode.Parse("""{"seconds":"60"}""");
            var cancelCompiled = await compiler.CompileSingleFile(Path.Combine(fixtureRoot, "cancel.cs"), generator.Preview(cancelGraph).Source, true);
            try
            {
                var instance = (Automation)Activator.CreateInstance(cancelCompiled.Assembly.GetType("VisualAutomation")!)!;
                using var cancellation = new CancellationTokenSource();
                instance._cancellationToken.Value = cancellation.Token;
                var running = (Task)instance.GetType().GetMethod("Run")!.Invoke(instance, null)!;
                cancellation.Cancel();
                try { await running.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("generated delay ignored cancellation"); }
                catch (OperationCanceledException) { }
            }
            finally { cancelCompiled.Unload(); }
            var api = new HassSharpManager(fixtureRoot);
            api.GenerateHassEntities([]);
            var originalSubscribe = PyInterop.SubscribeToEntityTracking;
            PyInterop.SubscribeToEntityTracking = _ => throw new InvalidOperationException("fixture registration failure");
            try
            {
                var request = new JsonObject { ["operation"] = "save", ["id"] = "broken", ["expectedRevision"] = 0, ["graph"] = Graph() };
                var reply = JsonNode.Parse(await api.VisualRequest(request.ToJsonString()))!;
                Check(reply["ok"]!.GetValue<bool>() && !reply["result"]!["active"]!.GetValue<bool>(), "persisted but activation failure explicit");
                Check(api.GetUserScript("visual_broken") == null, "failed initial activation must leave no runtime entry");
            }
            finally { PyInterop.SubscribeToEntityTracking = originalSubscribe; api.Unload(); }
            await api.Init([]);
            Check(api.GetUserScript("visual_broken") != null, "persisted visual source loads after restart");
            var edited = Graph(); edited["name"] = "Display rename";
            var saveReply = JsonNode.Parse(await api.VisualRequest(new JsonObject { ["operation"] = "save", ["id"] = "broken", ["expectedRevision"] = 1, ["graph"] = edited }.ToJsonString()))!;
            Check(saveReply["result"]!["active"]!.GetValue<bool>(), "saved update activates");
            var deleteReply = JsonNode.Parse(await api.VisualRequest("""{"operation":"delete","id":"broken","expectedRevision":2}"""))!;
            Check(deleteReply["ok"]!.GetValue<bool>() && api.GetUserScript("visual_broken") == null, "delete unloads visual runtime");
            await api.Init([]);
            Check(api.GetUserScript("visual_broken") == null, "delete survives reload");
            api.Unload();
            foreach (var configuration in new[] { """{"type":"daily","time":"07:30","timeZone":"Europe/Zagreb"}""", """{"type":"once","at":"2030-01-01T10:00:00Z"}""" })
            {
                var scheduled = Graph(); scheduled["nodes"]![0]!["config"] = JsonNode.Parse(configuration);
                scheduled["nodes"]![2]!["kind"] = "delay"; scheduled["nodes"]![2]!["config"] = JsonNode.Parse("""{"seconds":"0"}""");
                await Execute(scheduled);
            }
        }
        finally { HassPath.HassConfiguration = previousPath; Directory.Delete(fixtureRoot, true); }
        Console.WriteLine("PASS visual: generation, validation, mappings, persistence and conflicts");
    }
}
