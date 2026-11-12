using HassSharp;
using System.Text.Json;
using System.Text.Json.Nodes;

static class BackendReviewTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
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
                await Test($"legacy {symbol} binding survives compiled execution", async () => {
                    var preview = generator.Preview(VisualTests.MemberBindingGraph(symbol));
                    Check(preview.Valid, "legacy member preview rejected: " + JsonSerializer.Serialize(preview.Diagnostics));
                    var compiled = await compiler.CompileSingleFile(Path.Combine(root, "binding-" + symbol + ".cs"), preview.Source, true);
                    try {
                        var instance = (Automation)Activator.CreateInstance(compiled.Assembly.GetType("VisualAutomation")!)!;
                        await ((Task)instance.GetType().GetMethod("Run_74")!.Invoke(instance, null)!).WaitAsync(TimeSpan.FromSeconds(2));
                        Check((bool)instance.GetType().GetField("Completed")!.GetValue(instance)!, "legacy continuation was skipped");
                    } finally { compiled.Unload(); }
                });
                await Test($"saved legacy {symbol} binding survives startup regeneration", async () => {
                    var graph = VisualTests.MemberBindingGraph(symbol);
                    var directory = Path.Combine(root, ".storage", "hass_sharp", "visual");
                    Directory.CreateDirectory(directory);
                    var id = "binding-" + symbol.ToLowerInvariant();
                    // A pre-v3 saved document must load even if its old generated source is usable.
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
                        Check(api.GetUserScript("visual_" + id) != null, "saved legacy graph failed activation");
                        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
                        Check(File.ReadAllText(path) == persisted, "startup rewrote the saved document");
                    } finally { api.Unload(); PyInterop.Log = oldLog; File.Delete(path); }
                });
            }
            foreach (var symbol in new[] { "Dispatch", "stopped", "executed" })
                await Test($"v3 internal controls preserve authored {symbol} binding", async () => {
                    var graph = VisualTests.MemberBindingGraph(symbol); graph["version"] = 3;
                    var preview = generator.Preview(graph);
                    Check(preview.Valid, "v3 member preview rejected: " + JsonSerializer.Serialize(preview.Diagnostics));
                    var compiled = await compiler.CompileSingleFile(Path.Combine(root, "v3-binding-" + symbol + ".cs"), preview.Source, true);
                    try {
                        var instance = (Automation)Activator.CreateInstance(compiled.Assembly.GetType("VisualAutomation")!)!;
                        await ((Task)instance.GetType().GetMethod("Run_74")!.Invoke(instance, null)!).WaitAsync(TimeSpan.FromSeconds(2));
                        Check((bool)instance.GetType().GetField("Completed")!.GetValue(instance)!, "v3 continuation was skipped");
                    } finally { compiled.Unload(); }
                });
            foreach (var (symbol, internalName) in new[] { ("Dispatch", "@__hassSharpDispatch"), ("stopped", "__hassSharpStopped"), ("executed", "__hassSharpExecuted"), ("step", "__hassSharpStep") })
                await Test($"v3 controls avoid authored prefix collision for {symbol}", async () => {
                    var graph = VisualTests.MemberBindingGraph(symbol); graph["version"] = 3;
                    graph["members"] = graph["members"]!.GetValue<string>().Replace(symbol, internalName);
                    foreach (var node in graph["nodes"]!.AsArray().Skip(1))
                        node!["config"]!["code"] = node["config"]!["code"]!.GetValue<string>().Replace(symbol,internalName);
                    var preview = generator.Preview(graph);
                    Check(preview.Valid, "authored prefix member rejected: " + JsonSerializer.Serialize(preview.Diagnostics));
                    var compiled = await compiler.CompileSingleFile(Path.Combine(root, "v3-prefix-" + symbol + ".cs"), preview.Source, true);
                    try {
                        var instance = (Automation)Activator.CreateInstance(compiled.Assembly.GetType("VisualAutomation")!)!;
                        await ((Task)instance.GetType().GetMethod("Run_74")!.Invoke(instance, null)!).WaitAsync(TimeSpan.FromSeconds(2));
                        Check((bool)instance.GetType().GetField("Completed")!.GetValue(instance)!, "authored prefix member changed control flow");
                    } finally { compiled.Unload(); }
                });
            await Test("v3 internal names avoid declarations inside interpolated strings", async () => {
                var graph = VisualTests.MemberBindingGraph("step"); graph["version"] = 3;
                graph["members"] = "public bool Completed;";
                graph["nodes"]![1]!["config"]!["code"] = """Logger.Info($"interpolation:{int.TryParse("7", out var __hassSharpStep)} {__hassSharpStep}"); Completed = true;""";
                graph["nodes"]![2]!["config"]!["code"] = "if (!Completed) throw new Exception(\"interpolation node did not run\");";
                var preview = generator.Preview(graph);
                Check(preview.Valid, "interpolated declaration rejected: " + JsonSerializer.Serialize(preview.Diagnostics));
                var compiled = await compiler.CompileSingleFile(Path.Combine(root, "v3-interpolation-binding.cs"), preview.Source, true);
                try {
                    var instance = (Automation)Activator.CreateInstance(compiled.Assembly.GetType("VisualAutomation")!)!;
                    await ((Task)instance.GetType().GetMethod("Run_74")!.Invoke(instance, null)!).WaitAsync(TimeSpan.FromSeconds(2));
                    Check((bool)instance.GetType().GetField("Completed")!.GetValue(instance)!, "interpolation node was skipped");
                    Check(logs.Contains("interpolation:True 7"), "interpolated declaration did not produce its authored value");
                } finally { compiled.Unload(); }
            });
            await Test("missing motion sensor gives actionable error", async () => {
                var compiled = await compiler.CompileSingleFile(Path.Combine(root,"motion.cs"), "public class Motion : HassSharp.HelperBaseAutomations.MotionLightAutomation {}", true);
                try {
                    var instance = (Automation)Activator.CreateInstance(compiled.Assembly.GetType("Motion")!)!;
                    try { instance.Configure(); throw new Exception("missing sensor accepted"); }
                    catch (InvalidOperationException ex) { Check(ex.Message.Contains("MotionSensor"), "missing property not named"); }
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
            await Test("damaged compile cache rebuilds", async () => {
                Directory.CreateDirectory(HassPath.UserScripts);
                var path = Path.Combine(HassPath.UserScripts, "cached.cs");
                File.WriteAllText(path, "public class Cached : Automation {}");
                foreach (var script in await compiler.CompileFromUserScriptsFolder()) script.Unload();
                File.WriteAllBytes(Path.Combine(HassPath.UserScripts,".cache","cached.cs.dll"), [1,2,3]);
                var rebuilt = await compiler.CompileFromUserScriptsFolder();
                try { Check(rebuilt.Any(x => x.FileName == "cached"), "valid source blocked by cache"); }
                finally { foreach (var script in rebuilt) script.Unload(); File.Delete(path); }
            });
            await Test("entity refresh invalidates compiled folder cache", async () => {
                Directory.CreateDirectory(HassPath.UserScripts);
                var path = Path.Combine(HassPath.UserScripts, "typed-cache.cs");
                File.WriteAllText(path, "public class TypedCache : Automation { public override AutomationConfiguration Configure() => new(new AutomationAction(\"Run\", Entities: [Entities.BinarySensor.Motion])); public Task Run() => Task.CompletedTask; }");
                foreach (var script in await compiler.CompileFromUserScriptsFolder()) script.Unload();
                diagnostics.GenerateHassEntities(["light.hall"]);
                var refreshed = await compiler.CompileFromUserScriptsFolder();
                try { Check(!refreshed.Any(x => x.FileName == "typed-cache"), "cached assembly retained removed entity symbol"); }
                finally { foreach (var script in refreshed) script.Unload(); File.Delete(path); diagnostics.GenerateHassEntities(["binary_sensor.motion", "light.hall"]); }
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
            await Test("failed startup code remains readable and deletable", async () => {
                Directory.CreateDirectory(HassPath.UserScripts);
                var path = Path.Combine(HassPath.UserScripts,"repair.cs");
                File.WriteAllText(path,"invalid code for repair");
                var api = new HassSharpManager(root);
                try {
                    await api.Init(["binary_sensor.motion", "light.hall"]);
                    var summary = api.GetScriptSummaries().SingleOrDefault(x => x.FileName == "repair");
                    Check(summary is { Active: false } && !string.IsNullOrEmpty(summary.ActivationError), "failed code not listed with error");
                    Check(api.GetUserScript("repair")?.Source == "invalid code for repair", "failed code cannot be opened");
                    await api.DeleteScript(path);
                    Check(!File.Exists(path) && api.GetUserScript("repair") == null, "failed code cannot be deleted");
                } finally { api.Unload(); File.Delete(path); }
            });
            await Test("failed startup code can be repaired and activated", async () => {
                Directory.CreateDirectory(HassPath.UserScripts);
                var path = Path.Combine(HassPath.UserScripts,"repair.cs");
                File.WriteAllText(path,"invalid code for repair");
                var manager = new UserScriptManager();
                var compiled = await compiler.CompileSingleFile(path,"public class Repaired : Automation {}",false);
                try {
                    var rejected = await compiler.CompileSingleFile(path, "public class Invalid : Automation { public override AutomationConfiguration Configure() => throw new InvalidOperationException(\"not ready\"); }", false);
                    try { await manager.UpdateUserScript(rejected); throw new Exception("invalid repair accepted"); }
                    catch (InvalidOperationException ex) when (ex.Message == "not ready") { }
                    Check(File.ReadAllText(path) == "invalid code for repair" && manager.GetUserScripts().Count == 0, "invalid repair damaged source or activated");
                    await manager.UpdateUserScript(compiled);
                    Check(manager.GetUserScripts().Single().CompiledScript.FileName == "repair", "repair did not activate");
                    Check(File.ReadAllText(path).Contains("Repaired"), "repair not persisted");
                } finally { manager.UnloadUserScripts(); File.Delete(path); }
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
                """, true);
                var good = await compiler.CompileSingleFile(Path.Combine(root,"good.cs"), "public class Good : Automation {}", true);
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
                """, true);
                var manager = new UserScriptManager();
                try {
                    await manager.LoadUserScripts([compiled]);
                    await manager.GetUserScripts().Single().Classes.Single().RunMethod("Run");
                    Check(logs.Any(x => x.Contains("System.InvalidOperationException") && x.Contains("Throwing.Run")), "exception diagnostics missing");
                } finally { manager.UnloadUserScripts(); }
            });
            await Test("legacy visual source regenerates on startup without changing saved revision", async () => {
                var directory = Path.Combine(root, ".storage", "hass_sharp", "visual");
                var store = new VisualStore(directory, generator);
                var saved = store.Save("legacy", VisualTests.Graph(), 0);
                var legacy = saved with { Source = saved.Source.Replace("Entities: [Entities.BinarySensor.Motion]", "Entities: [\"binary_sensor.motion\"]").Replace("Entity(Entities.BinarySensor.Motion)", "Entity(\"binary_sensor.motion\")") };
                var path = Path.Combine(directory, "legacy.json");
                File.WriteAllText(path, JsonSerializer.Serialize(legacy, VisualStore.Json));
                var persisted = File.ReadAllText(path);
                var api = new HassSharpManager(root);
                try {
                    await api.Init(["binary_sensor.motion", "light.hall"]);
                    Check(api.GetUserScript("visual_legacy") != null, "legacy generated string source failed to activate");
                    var read = JsonNode.Parse(await api.VisualRequest("{\"operation\":\"read\",\"id\":\"legacy\"}"))!["result"]!;
                    Check(api.GetUserScript("visual_legacy")!.Source.Contains("Entities: [Entities.BinarySensor.Motion]"), "runtime did not compile regenerated source");
                    Check(read["source"]!.GetValue<string>() == legacy.Source, "read changed last-saved source provenance");
                    Check(read["sourceMap"]!.ToJsonString() == JsonSerializer.SerializeToNode(legacy.SourceMap, VisualStore.Json)!.ToJsonString(), "read changed last-saved source map provenance");
                    Check(read["revision"]!.GetValue<long>() == saved.Revision && File.ReadAllText(path) == persisted, "startup rewrote saved revision/source");
                    await api.Init(["light.hall"]);
                    Check(api.GetUserScript("visual_legacy") == null, "missing trigger still active");
                    var missing = JsonNode.Parse(await api.VisualRequest("{\"operation\":\"read\",\"id\":\"legacy\"}"))!["result"]!;
                    Check(missing["activationError"]!.GetValue<string>().Contains("t: Unknown entity"), "missing entity omitted actionable graph node");
                } finally { api.Unload(); store.Delete("legacy", saved.Revision); }
            });
            await Test("startup isolates code and visual compilation errors", async () => {
                Directory.CreateDirectory(HassPath.UserScripts);
                File.WriteAllText(Path.Combine(HassPath.UserScripts,"broken.cs"), "invalid code");
                File.WriteAllText(Path.Combine(HassPath.UserScripts,"healthy.cs"), "public class Healthy : Automation {}");
                var dir = Path.Combine(root,".storage","hass_sharp","visual");
                var store = new VisualStore(dir, generator);
                var doc = store.Save("invalid", VisualTests.Graph(),0);
                doc.Graph["nodes"]![0]!["config"]!["entity"] = "sensor.removed";
                File.WriteAllText(Path.Combine(dir,"invalid.json"), JsonSerializer.Serialize(doc, VisualStore.Json));
                store.Save("healthy", VisualTests.Graph(),0);
                var api = new HassSharpManager(root);
                try {
                    await api.Init(["binary_sensor.motion", "light.hall"]);
                    Check(api.GetUserScript("healthy") != null && api.GetUserScript("visual_healthy") != null, "healthy runtime missing");
                    Check(api.GetUserScript("visual_invalid") == null, "invalid saved graph activated");
                    Check(File.Exists(Path.Combine(HassPath.UserScripts,"broken.cs")), "broken source deleted");
                    try { await api.DeleteScript(Path.Combine(dir,"visual_healthy.cs")); throw new Exception("code delete accepted visual path"); }
                    catch (ArgumentException) { }
                } finally { api.Unload(); }
            });
        }
        finally { PyInterop.Log = log; HassPath.HassConfiguration = previous; Directory.Delete(root,true); }
        if (failures.Count != 0) throw new Exception(string.Join("\n", failures));
    }
}
