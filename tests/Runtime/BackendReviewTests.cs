using HassSharp;
using System.Text.Json;
using System.Text.Json.Nodes;

static class BackendReviewTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    [RuntimeSuite("--backend-review", Order = 1)]
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "backend-review-" + Guid.NewGuid());
        var previous = HassPath.HassConfiguration;
        var log = PyInterop.Log;
        var logs = new List<string>();
        PyInterop.Log = (_, message) => { lock (logs) logs.Add(message); };
        HassPath.HassConfiguration = root;
        var failures = new List<string>();
        async Task Test(string name, Func<Task> test)
        {
            try { await test(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures.Add(name + ": " + ex); }
        }
        try
        {
            var diagnostics = new DiagnosticsProvider(); diagnostics.GenerateHassEntities(["binary_sensor.motion", "light.hall"]);
            var generator = new VisualGenerator(diagnostics);
            var compiler = new CodeCompiler(diagnostics);
            foreach (var symbol in new[] { "Dispatch", "stopped", "values", "executed" })
            {
                await Test($"v4 {symbol} binding survives compiled execution", async () => {
                    var preview = generator.Preview(VisualTests.MemberBindingGraph(symbol));
                    Check(preview.Valid, "v4 member preview rejected: " + JsonSerializer.Serialize(preview.Diagnostics));
                    var compiled = await compiler.CompileSingleFile(Path.Combine(root, "binding-" + symbol + ".cs"), preview.Source);
                    try {
                        var instance = (Automation)Activator.CreateInstance(compiled.Assembly.GetType("VisualAutomation")!)!;
                        await ((Task)instance.GetType().GetMethod("Run_74")!.Invoke(instance, null)!).WaitAsync(TimeSpan.FromSeconds(2));
                        Check((bool)instance.GetType().GetField("Completed")!.GetValue(instance)!, "v4 continuation was skipped");
                    } finally { compiled.Unload(); }
                });
                await Test($"saved v4 {symbol} binding survives startup regeneration", async () => {
                    var graph = VisualTests.MemberBindingGraph(symbol);
                    var directory = Path.Combine(root, ".storage", "hass_sharp", "visual");
                    Directory.CreateDirectory(directory);
                    var id = "binding-" + symbol.ToLowerInvariant();
                    // A saved v4 document must load even if its old generated source is usable.
                    var source = $$"""
                    public class VisualAutomation : Automation {
                      {{graph["members"]!.GetValue<string>()}}
                      public override AutomationConfiguration Configure() => new(new AutomationAction("Run_74", Startup: true));
                      public async Task Run_74() { {{graph["nodes"]![1]!["config"]!["code"]!.GetValue<string>()}} {{graph["nodes"]![2]!["config"]!["code"]!.GetValue<string>()}} }
                    }
                    """;
                    var path = Path.Combine(directory, id + ".json");
                    var persisted = JsonSerializer.Serialize(new VisualDocument(1,id,1,graph,source,[]), VisualStore.Json);
                    File.WriteAllText(path,persisted);
                    var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var oldLog = PyInterop.Log;
                    PyInterop.Log = (level,message) => { oldLog(level,message); if (message == "binding-" + symbol + "-Startup") completed.TrySetResult(); };
                    var api = new HassSharpManager(root);
                    try {
                        await api.Init(["binary_sensor.motion", "light.hall"]);
                        Check(VisualTests.Loaded(api, id) != null, "saved v4 graph failed activation");
                        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
                        var stored = JsonNode.Parse(File.ReadAllText(path))!;
                        Check(stored["revision"]!.GetValue<long>() == 1 && stored["graph"]!.ToJsonString() == graph.ToJsonString(), "startup changed the saved revision or graph");
                        Check(stored["source"]!.GetValue<string>() == VisualTests.Loaded(api, id)!.CompiledScript.SourceCode, "startup did not store the regenerated source");
                    } finally { api.Unload(); PyInterop.Log = oldLog; File.Delete(path); }
                });
            }
            foreach (var symbol in new[] { "Dispatch", "stopped", "executed" })
                await Test($"v4 internal controls preserve authored {symbol} binding", async () => {
                    var graph = VisualTests.MemberBindingGraph(symbol); graph["version"] = 5;
                    var preview = generator.Preview(graph);
                    Check(preview.Valid, "v4 member preview rejected: " + JsonSerializer.Serialize(preview.Diagnostics));
                    var compiled = await compiler.CompileSingleFile(Path.Combine(root, "v4-binding-" + symbol + ".cs"), preview.Source);
                    try {
                        var instance = (Automation)Activator.CreateInstance(compiled.Assembly.GetType("VisualAutomation")!)!;
                        await ((Task)instance.GetType().GetMethod("Run_74")!.Invoke(instance, null)!).WaitAsync(TimeSpan.FromSeconds(2));
                        Check((bool)instance.GetType().GetField("Completed")!.GetValue(instance)!, "v4 continuation was skipped");
                    } finally { compiled.Unload(); }
                });
            foreach (var (symbol, internalName) in new[] { ("Dispatch", "@__hassSharpDispatch"), ("stopped", "__hassSharpStopped"), ("executed", "__hassSharpExecuted"), ("step", "__hassSharpStep") })
                await Test($"v4 controls avoid authored prefix collision for {symbol}", async () => {
                    var graph = VisualTests.MemberBindingGraph(symbol); graph["version"] = 5;
                    graph["members"] = graph["members"]!.GetValue<string>().Replace(symbol, internalName);
                    foreach (var node in graph["nodes"]!.AsArray().Skip(1))
                        node!["config"]!["code"] = node["config"]!["code"]!.GetValue<string>().Replace(symbol,internalName);
                    var preview = generator.Preview(graph);
                    Check(preview.Valid, "authored prefix member rejected: " + JsonSerializer.Serialize(preview.Diagnostics));
                    var compiled = await compiler.CompileSingleFile(Path.Combine(root, "v4-prefix-" + symbol + ".cs"), preview.Source);
                    try {
                        var instance = (Automation)Activator.CreateInstance(compiled.Assembly.GetType("VisualAutomation")!)!;
                        await ((Task)instance.GetType().GetMethod("Run_74")!.Invoke(instance, null)!).WaitAsync(TimeSpan.FromSeconds(2));
                        Check((bool)instance.GetType().GetField("Completed")!.GetValue(instance)!, "authored prefix member changed control flow");
                    } finally { compiled.Unload(); }
                });
            await Test("v4 internal names avoid declarations inside interpolated strings", async () => {
                var graph = VisualTests.MemberBindingGraph("step"); graph["version"] = 5;
                graph["members"] = "public bool Completed;";
                graph["nodes"]![1]!["config"]!["code"] = """Logger.Info($"interpolation:{int.TryParse("7", out var __hassSharpStep)} {__hassSharpStep}"); Completed = true;""";
                graph["nodes"]![2]!["config"]!["code"] = "if (!Completed) throw new Exception(\"interpolation node did not run\");";
                var preview = generator.Preview(graph);
                Check(preview.Valid, "interpolated declaration rejected: " + JsonSerializer.Serialize(preview.Diagnostics));
                var compiled = await compiler.CompileSingleFile(Path.Combine(root, "v4-interpolation-binding.cs"), preview.Source);
                try {
                    var instance = (Automation)Activator.CreateInstance(compiled.Assembly.GetType("VisualAutomation")!)!;
                    await ((Task)instance.GetType().GetMethod("Run_74")!.Invoke(instance, null)!).WaitAsync(TimeSpan.FromSeconds(2));
                    Check((bool)instance.GetType().GetField("Completed")!.GetValue(instance)!, "interpolation node was skipped");
                    Check(logs.Contains("interpolation:True 7"), "interpolated declaration did not produce its authored value");
                } finally { compiled.Unload(); }
            });
            await Test("delete inactive visual forgets identity", async () => {
                var api = new HassSharpManager(root); api.GenerateHassEntities(["binary_sensor.motion", "light.hall"]);
                var store = new VisualStore(Path.Combine(root,".storage","hass_sharp","visual"), generator);
                store.Save("orphan",VisualTests.Graph(),0);
                async Task<string> Identity() => JsonNode.Parse(await api.VisualRequest("{\"operation\":\"read\",\"id\":\"orphan\"}"))!["result"]!["automationId"]!.GetValue<string>();
                var first = await Identity();
                var deleted = JsonNode.Parse(await api.VisualRequest("{\"operation\":\"delete\",\"id\":\"orphan\",\"expectedRevision\":1}"))!;
                Check(deleted["ok"]!.GetValue<bool>(), "delete rejected");
                store.Save("orphan",VisualTests.Graph(),0);
                Check(first != await Identity(), "recreated inactive visual reused identity");
                store.Delete("orphan",1); api.Unload();
            });
            await Test("run now of an unknown or unloaded automation is refused as not active", async () => {
                var api = new HassSharpManager(root); api.GenerateHassEntities(["binary_sensor.motion", "light.hall"]);
                var store = new VisualStore(Path.Combine(root,".storage","hass_sharp","visual"), generator);
                store.Save("unloaded", VisualTests.Graph(), 0);
                try {
                    // Saved but never loaded (no Init): it has an identity and no running script.
                    var unloaded = JsonNode.Parse(await api.VisualRequest("{\"operation\":\"read\",\"id\":\"unloaded\"}"))!["result"]!["automationId"]!.GetValue<string>();
                    foreach (var automation in new[] { "no-such-automation", unloaded })
                    {
                        try { api.RunNow(automation, "VisualAutomation", "Run_74"); throw new Exception("run accepted for " + automation); }
                        catch (InvalidOperationException error) when (error.Message == "Automation is not active") { }
                    }
                } finally { api.Unload(); store.Delete("unloaded", 1); }
            });
            await Test("retention uses a global completed-run index", () => {
                var path = Path.Combine(root,"indexed.db");
                _ = new RunHistoryStore(path);
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path); connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "EXPLAIN QUERY PLAN SELECT RunId FROM Runs WHERE EndUtc IS NOT NULL ORDER BY ReceivedUtc DESC, RunId DESC LIMIT -1 OFFSET 10000";
                using var reader = command.ExecuteReader(); var plan = "";
                while(reader.Read()) plan += reader.GetString(3);
                Check(!plan.Contains("TEMP B-TREE") && plan.Contains("IX_Runs_Completed"), "retention sorts/scans without completed index: " + plan);
                return Task.CompletedTask;
            });
            await Test("preview has no scaffolding warnings", () => {
                var preview = generator.Preview(VisualTests.Graph());
                Check(preview.Valid && preview.Diagnostics.Count == 0, "unexpected diagnostics: " + JsonSerializer.Serialize(preview.Diagnostics));
                return Task.CompletedTask;
            });
            await Test("corrupt visual can be explicitly deleted", () => {
                var dir = Path.Combine(root,"corrupt-delete"); Directory.CreateDirectory(dir);
                var path = Path.Combine(dir,"hall.json"); File.WriteAllText(path,"{ truncated");
                var store = new VisualStore(dir,generator);
                try { store.Delete("hall", 5); throw new Exception("stale revision deleted corrupt file"); }
                catch (VisualConflictException) { }
                store.Delete("hall",0);
                Check(!File.Exists(path), "corrupt file not deleted");
                store.Save("hall",VisualTests.Graph(),0);
                try { store.Delete("hall",0); throw new Exception("zero revision deleted healthy document"); }
                catch (VisualConflictException) { }
                return Task.CompletedTask;
            });
            await Test("visual startup failure is visible in read and list", async () => {
                var dir = Path.Combine(root,".storage","hass_sharp","visual");
                var store = new VisualStore(dir,generator);
                var doc = store.Save("status", VisualTests.Graph(),0);
                doc.Graph["nodes"]![0]!["config"]!["entity"] = "sensor.removed";
                File.WriteAllText(Path.Combine(dir,"status.json"),JsonSerializer.Serialize(doc,VisualStore.Json));
                var api = new HassSharpManager(root);
                try {
                    await api.Init(["binary_sensor.motion", "light.hall"]);
                    foreach(var operation in new[] { "read", "list" }) {
                        var result = JsonNode.Parse(await api.VisualRequest(new JsonObject { ["operation"] = operation, ["id"] = "status" }.ToJsonString()))!["result"]!;
                        var item = operation == "read" ? result : result.AsArray().Single(x => x!["id"]!.GetValue<string>() == "status")!;
                        Check(item["active"]?.GetValue<bool>() == false && !string.IsNullOrWhiteSpace(item["activationError"]?.GetValue<string>()), "failure status absent from " + operation);
                    }
                } finally { api.Unload(); store.Delete("status",1); }
            });
            await Test("strict mode names", () => {
                foreach (var mode in new[] { "0", "Single, Restart", " Single " }) {
                    var graph = VisualTests.Graph(); graph["nodes"]![0]!["config"]!["mode"] = mode;
                    Check(!generator.Preview(graph).Valid, "accepted mode " + mode);
                }
                return Task.CompletedTask;
            });
            await Test("list skips corrupt and stray documents", () => {
                var dir = Path.Combine(root, "listing"); var store = new VisualStore(dir, generator);
                store.Save("healthy", VisualTests.Graph(), 0);
                foreach (var pair in new[] { ("broken.json", "{"), ("stray file.json", "{}"), ("null.json", "null"), ("empty.json", "{}") })
                    File.WriteAllText(Path.Combine(dir, pair.Item1), pair.Item2);
                Check(store.List().Single().Id == "healthy", "healthy document lost");
                Check(Directory.GetFiles(dir).Length == 5, "repair files removed");
                return Task.CompletedTask;
            });
            await Test("configuration failure isolated and subscriptions cleaned", async () => {
                var manager = new UserScriptManager();
                var bad = await compiler.CompileSingleFile(Path.Combine(root,"bad.cs"), """
                public class Bad : Automation {
                  public override AutomationConfiguration Configure() => new(new AutomationAction(nameof(Run), Entities: [new EntityRefWrapper<HaSensor>("sensor.partial")], Schedules: [AutomationSchedule.Once("bad", DateTimeOffset.UtcNow)]));
                  public Task Run() => Task.CompletedTask;
                }
                """);
                var good = await compiler.CompileSingleFile(Path.Combine(root,"good.cs"), "public class Good : Automation {}");
                var register = PyInterop.RegisterSchedule;
                PyInterop.RegisterSchedule = (_, _) => throw new InvalidOperationException("schedule registration failed");
                try {
                    await manager.LoadUserScripts([bad, good]);
                    Check(manager.GetUserScripts().Single().CompiledScript.FileName == "good", "healthy script not loaded");
                    Check(manager.DependencyTracking.GetEntries("sensor.partial").Count == 0, "failed tracking retained");
                } finally { PyInterop.RegisterSchedule = register; manager.UnloadUserScripts(); }
            });
            await Test("invocation logs type and stack", async () => {
                var compiled = await compiler.CompileSingleFile(Path.Combine(root,"throw.cs"), """
                public class Throwing : Automation {
                  public override AutomationConfiguration Configure() => new(new AutomationAction(nameof(Run)));
                  public Task Run() => throw new InvalidOperationException("review failure");
                }
                """);
                var manager = new UserScriptManager();
                try {
                    await manager.LoadUserScripts([compiled]);
                    await manager.GetUserScripts().Single().Classes.Single().RunMethod("Run");
                    Check(logs.Any(x => x.Contains("System.InvalidOperationException") && x.Contains("Throwing.Run")), "exception diagnostics missing");
                } finally { manager.UnloadUserScripts(); }
            });
            await Test("stale visual source refreshes on startup at the same revision", async () => {
                var directory = Path.Combine(root, ".storage", "hass_sharp", "visual");
                var store = new VisualStore(directory, generator);
                var saved = store.Save("v4", VisualTests.Graph(), 0);
                var v4 = saved with { Source = "deliberately stale generated source", SourceMap = [] };
                var path = Path.Combine(directory, "v4.json");
                File.WriteAllText(path, JsonSerializer.Serialize(v4, VisualStore.Json));
                var api = new HassSharpManager(root);
                try {
                    await api.Init(["binary_sensor.motion", "light.hall"]);
                    Check(VisualTests.Loaded(api, "v4") != null, "v4 generated string source failed to activate");
                    var running = VisualTests.Loaded(api, "v4")!.CompiledScript.SourceCode;
                    Check(running.Contains("Entities:[Entities.BinarySensor.Motion]"), "runtime did not compile regenerated source");
                    var read = JsonNode.Parse(await api.VisualRequest("{\"operation\":\"read\",\"id\":\"v4\"}"))!["result"]!;
                    Check(read["currentRevisionActive"]!.GetValue<bool>(), "regenerated revision is not current after startup");
                    Check(read["revision"]!.GetValue<long>() == saved.Revision, "startup changed the saved revision");
                    Check(read["source"]!.GetValue<string>() == running, "read still returns stale source");
                    Check(read["sourceMap"]!.AsArray().Count > 0 && read["catalogGeneration"]?.GetValue<string>() is { Length: > 0 }, "derived source map/catalog generation not refreshed");
                    Check(read["graph"]!.ToJsonString() == saved.Graph.ToJsonString(), "startup changed the saved graph");
                    var run = JsonNode.Parse(await api.VisualRequest("{\"operation\":\"run\",\"id\":\"v4\",\"triggerId\":\"t\"}"))!;
                    Check(run["ok"]!.GetValue<bool>() && run["result"]?["runId"] != null, "manual dispatch of refreshed revision rejected: " + run.ToJsonString());
                    var refreshed = File.ReadAllText(path);
                    await api.Init(["light.hall"]);
                    Check(VisualTests.Loaded(api, "v4") == null, "missing trigger still active");
                    var missing = JsonNode.Parse(await api.VisualRequest("{\"operation\":\"read\",\"id\":\"v4\"}"))!["result"]!;
                    Check(missing["activationError"]!.GetValue<string>().Contains("t: Unknown entity"), "missing entity omitted actionable graph node");
                    Check(File.ReadAllText(path) == refreshed, "failed startup compile rewrote the saved document");
                } finally { api.Unload(); store.Delete("v4", saved.Revision); }
            });
            await Test("visual that fails to compile on startup is left untouched", async () => {
                var directory = Path.Combine(root, ".storage", "hass_sharp", "visual");
                var store = new VisualStore(directory, generator);
                var saved = store.Save("uncompiled", VisualTests.Graph(), 0);
                saved.Graph["members"] = "this is not C#";
                var path = Path.Combine(directory, "uncompiled.json");
                File.WriteAllText(path, JsonSerializer.Serialize(saved with { Source = "stale" }, VisualStore.Json));
                var persisted = File.ReadAllText(path);
                var api = new HassSharpManager(root);
                try {
                    await api.Init(["binary_sensor.motion", "light.hall"]);
                    Check(VisualTests.Loaded(api, "uncompiled") == null, "invalid saved graph activated");
                    Check(File.ReadAllText(path) == persisted, "startup rewrote a document that failed to compile");
                } finally { api.Unload(); store.Delete("uncompiled", saved.Revision); }
            });
            await Test("refresh derived rewrites only changed documents at the expected revision", () => {
                var dir = Path.Combine(root, "refresh-derived"); var store = new VisualStore(dir, generator);
                var saved = store.Save("hall", VisualTests.Graph(), 0);
                var path = Path.Combine(dir, "hall.json"); var persisted = File.ReadAllText(path);
                Check(!store.RefreshDerived("hall", saved.Revision, saved.Source, saved.SourceMap, saved.CatalogGeneration), "unchanged derived data reported a rewrite");
                Check(!store.RefreshDerived("hall", saved.Revision + 1, "other", [], "generation"), "stale expected revision reported a rewrite");
                Check(!store.RefreshDerived("missing", 1, "other", [], "generation") && !File.Exists(Path.Combine(dir, "missing.json")), "missing document was created");
                Check(File.ReadAllText(path) == persisted, "no-op refresh touched the stored document");
                Check(store.RefreshDerived("hall", saved.Revision, "fresh source", [new("t", 1, 2)], "generation"), "changed derived data not written");
                var read = store.Read("hall")!;
                Check(read.Revision == saved.Revision && read.Source == "fresh source" && read.SourceMap.SequenceEqual([new VisualSourceMap("t", 1, 2)]) && read.CatalogGeneration == "generation", "derived data not stored at the same revision");
                Check(read.Graph.ToJsonString() == saved.Graph.ToJsonString(), "refresh changed the graph");
                Check(Directory.GetFiles(dir).Length == 1, "temporary refresh file left behind");
                return Task.CompletedTask;
            });
            await Test("startup isolates visual compilation errors", async () => {
                var dir = Path.Combine(root,".storage","hass_sharp","visual");
                var store = new VisualStore(dir, generator);
                var doc = store.Save("invalid", VisualTests.Graph(),0);
                doc.Graph["nodes"]![0]!["config"]!["entity"] = "sensor.removed";
                File.WriteAllText(Path.Combine(dir,"invalid.json"), JsonSerializer.Serialize(doc, VisualStore.Json));
                store.Save("healthy", VisualTests.Graph(),0);
                var api = new HassSharpManager(root);
                try {
                    await api.Init(["binary_sensor.motion", "light.hall"]);
                    Check(VisualTests.Loaded(api, "healthy") != null, "healthy runtime missing");
                    Check(VisualTests.Loaded(api, "invalid") == null, "invalid saved graph activated");
                    Check(!Directory.Exists(Path.Combine(HassPath.HassSharpIntegration, "user_scripts")), "startup created a user_scripts directory");
                } finally { api.Unload(); store.Delete("invalid", doc.Revision); store.Delete("healthy", 1); }
            });
            await Test("startup leaves hand-written files in user_scripts alone and loads none of them", async () => {
                var scripts = Path.Combine(HassPath.HassSharpIntegration, "user_scripts");
                Directory.CreateDirectory(scripts);
                var path = Path.Combine(scripts, "legacy.cs");
                byte[] source = System.Text.Encoding.UTF8.GetBytes("""
                    public class Legacy : Automation
                    {
                        public override AutomationConfiguration Configure() => new(new AutomationAction(nameof(Run), Startup: true));
                        public Task Run() { Logger.Info("LEGACY SCRIPT RAN"); return Task.CompletedTask; }
                    }
                    """);
                File.WriteAllBytes(path, source);
                var written = File.GetLastWriteTimeUtc(path);
                const string notice = "user_scripts is no longer used: automations are visual only. The files there were left as they are.";
                var api = new HassSharpManager(root);
                try {
                    lock (logs) logs.Clear();
                    await api.Init(["binary_sensor.motion", "light.hall"]);
                    await Task.Delay(100); // A startup action, had one loaded, would have logged by now.
                    string[] seen; lock (logs) seen = logs.ToArray();
                    Check(seen.Count(m => m == notice) == 1, "notice not logged exactly once: " + string.Join(" | ", seen));
                    Check(!seen.Any(m => m.Contains("LEGACY SCRIPT RAN")), "a user_scripts file was loaded and started");
                    var loaded = (UserScriptManager)typeof(HassSharpManager).GetField("_userScriptManager", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(api)!;
                    Check(loaded.GetUserScripts().Count == 0, "a user_scripts file was loaded");
                    Check(File.ReadAllBytes(path).SequenceEqual(source) && File.GetLastWriteTimeUtc(path) == written, "the user_scripts file was changed");
                    Check(Directory.GetFileSystemEntries(scripts).SequenceEqual([path]), "user_scripts gained entries: " + string.Join(", ", Directory.GetFileSystemEntries(scripts)));
                } finally { api.Unload(); }
                File.Delete(path);
                var quiet = new HassSharpManager(root);
                try {
                    lock (logs) logs.Clear();
                    await quiet.Init(["binary_sensor.motion", "light.hall"]);
                    lock (logs) Check(!logs.Contains(notice), "notice logged without a .cs file");
                } finally { quiet.Unload(); }
            });
        }
        finally { PyInterop.Log = log; HassPath.HassConfiguration = previous; Directory.Delete(root,true); }
        if (failures.Count != 0) throw new Exception(string.Join("\n", failures));
    }
}
