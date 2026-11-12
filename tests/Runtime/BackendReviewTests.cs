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
            var diagnostics = new DiagnosticsProvider(); diagnostics.GenerateHassEntities([]);
            var generator = new VisualGenerator(diagnostics);
            var compiler = new CodeCompiler(diagnostics);
            await Test("missing motion sensor gives actionable error", async () => {
                var compiled = await compiler.CompileSingleFile(Path.Combine(root,"motion.cs"), "public class Motion : HassSharp.HelperBaseAutomations.MotionLightAutomation {}", true);
                try {
                    var instance = (Automation)Activator.CreateInstance(compiled.Assembly.GetType("Motion")!)!;
                    try { instance.Configure(); throw new Exception("missing sensor accepted"); }
                    catch (InvalidOperationException ex) { Check(ex.Message.Contains("MotionSensor"), "missing property not named"); }
                } finally { compiled.Unload(); }
            });
            await Test("delete inactive visual forgets identity", async () => {
                var api = new HassSharpManager(root); api.GenerateHassEntities([]);
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
                  public override AutomationConfiguration Configure() => new(new AutomationAction(nameof(Run), Entities: ["sensor.partial"], Schedules: [AutomationSchedule.Once("bad", DateTimeOffset.UtcNow)]));
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
            await Test("startup isolates code and visual compilation errors", async () => {
                Directory.CreateDirectory(HassPath.UserScripts);
                File.WriteAllText(Path.Combine(HassPath.UserScripts,"broken.cs"), "invalid code");
                File.WriteAllText(Path.Combine(HassPath.UserScripts,"healthy.cs"), "public class Healthy : Automation {}");
                var dir = Path.Combine(root,".storage","hass_sharp","visual");
                var store = new VisualStore(dir, generator);
                var doc = store.Save("invalid", VisualTests.Graph(),0);
                File.WriteAllText(Path.Combine(dir,"invalid.json"), JsonSerializer.Serialize(doc with { Source = "invalid C#" }, VisualStore.Json));
                store.Save("healthy", VisualTests.Graph(),0);
                var api = new HassSharpManager(root);
                try {
                    await api.Init([]);
                    Check(api.GetUserScript("healthy") != null && api.GetUserScript("visual_healthy") != null, "healthy runtime missing");
                    Check(File.Exists(Path.Combine(HassPath.UserScripts,"broken.cs")), "broken source deleted");
                    try { api.DeleteScript(Path.Combine(dir,"visual_healthy.cs")); throw new Exception("code delete accepted visual path"); }
                    catch (ArgumentException) { }
                } finally { api.Unload(); }
            });
        }
        finally { PyInterop.Log = log; HassPath.HassConfiguration = previous; Directory.Delete(root,true); }
        if (failures.Count != 0) throw new Exception(string.Join("\n", failures));
    }
}
