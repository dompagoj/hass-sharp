using HassSharp;
using System.Text.Json;
using System.Text.Json.Nodes;

// Run history v2 (contracts C8, C9): migration, snapshots, traces, trigger summaries, cancel reasons and run_trace.
static class HistoryTraceTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name));

    static async Task Until(Func<bool> condition, string message)
    {
        for (var i = 0; i < 200; i++) { if (condition()) return; await Task.Delay(20); }
        throw new Exception("timed out: " + message);
    }

    static InvocationRecord Completed(string automation, string revision, string status = "Succeeded", DateTime? received = null) => new()
    {
        AutomationId = automation, Revision = revision, Action = "VisualAutomation::Run_74", Cause = "Manual",
        Status = status, ReceivedUtc = received ?? DateTime.UtcNow, EndUtc = DateTime.UtcNow,
    };

    public static JsonObject StopGraph() => JsonNode.Parse("""
    {"version":5,"name":"Stopper","nodes":[
      {"id":"t","kind":"trigger","label":"Front door opens","config":{"type":"manual"}},
      {"id":"l","kind":"log","config":{"value":{"kind":"literal","type":"string","value":"history-trace-stop"}}},
      {"id":"x","kind":"stop","config":{}}
    ],"edges":[{"id":"tl","source":"t","target":"l"},{"id":"lx","source":"l","target":"x"}]}
    """)!.AsObject();

    [RuntimeSuite("--history-trace", Order = 4)]
    public static async Task Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "history-trace-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        var failures = new List<string>();
        async Task Test(string name, Func<Task> test)
        {
            try { await test(); Console.WriteLine("PASS history trace: " + name); }
            catch (Exception ex) { failures.Add(name + ": " + ex); }
        }
        try
        {
            await Test("a version-3 database migrates and keeps its runs", () =>
            {
                var path = Path.Combine(folder, "v3.db");
                _ = new RunHistoryStore(Path.Combine(folder, "native.db"));
                // The schema the three earlier migrations produce, recorded as applied, with one run in it.
                using (var setup = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path + ";Pooling=False"))
                {
                    setup.Open();
                    using var create = setup.CreateCommand();
                    create.CommandText = """
                        CREATE TABLE "__EFMigrationsHistory" ("MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY, "ProductVersion" TEXT NOT NULL);
                        INSERT INTO "__EFMigrationsHistory" VALUES ('202611120001_InitialHistory', '10.0.0'), ('202611120002_ScheduleOccurrences', '10.0.0'), ('202611120003_CompletedRetentionIndex', '10.0.0');
                        CREATE TABLE "Identities" ("Path" TEXT NOT NULL PRIMARY KEY, "Id" TEXT NOT NULL);
                        CREATE TABLE "Runs" (
                          "RunId" TEXT NOT NULL PRIMARY KEY, "AutomationId" TEXT NOT NULL, "Revision" TEXT NOT NULL,
                          "Action" TEXT NOT NULL, "Cause" TEXT NOT NULL, "Details" TEXT NULL,
                          "PlannedUtc" TEXT NULL, "ReceivedUtc" TEXT NOT NULL, "StartUtc" TEXT NULL, "EndUtc" TEXT NULL,
                          "DurationMs" REAL NULL, "Status" TEXT NOT NULL, "Error" TEXT NULL);
                        CREATE INDEX "IX_Runs_AutomationId_ReceivedUtc_RunId" ON "Runs" ("AutomationId", "ReceivedUtc", "RunId");
                        CREATE TABLE ScheduleOccurrences (Id TEXT NOT NULL PRIMARY KEY);
                        CREATE INDEX IX_Runs_Completed ON Runs (ReceivedUtc, RunId) WHERE EndUtc IS NOT NULL;
                        INSERT INTO Runs (RunId, AutomationId, Revision, Action, Cause, Details, PlannedUtc, ReceivedUtc, StartUtc, EndUtc, DurationMs, Status, Error)
                        VALUES ('old', 'automation', 'r1', 'Home::Run', 'Manual', '{"legacy":true}', NULL, '2026-11-12 01:00:00', '2026-11-12 01:00:01', '2026-11-12 01:00:02', 1000.0, 'Failed', 'boom');
                        """;
                    create.ExecuteNonQuery();
                }
                var store = new RunHistoryStore(path);
                var run = store.Read("automation", 0, 10).Single();
                Check(run is { RunId: "old", Status: "Failed", Error: "boom", Action: "Home::Run", HasTrace: false, Trigger: null, VisualId: null, DurationMs: 1000.0 }, "legacy run lost: " + JsonSerializer.Serialize(run));
                Check(run.ReceivedUtc == new DateTime(2026, 11, 12, 1, 0, 0, DateTimeKind.Utc), "legacy timestamp changed");
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path); connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT (SELECT count(*) FROM __EFMigrationsHistory), (SELECT count(*) FROM pragma_table_info('Runs') WHERE name = 'Details'), (SELECT count(*) FROM sqlite_master WHERE name IN ('RunTraces', 'GraphSnapshots'))";
                using var reader = command.ExecuteReader(); reader.Read();
                Check(reader.GetInt32(0) == 4 && reader.GetInt32(1) == 0 && reader.GetInt32(2) == 2, "migration 202611130004_RunTracing not applied as specified");
                return Task.CompletedTask;
            });

            await Test("retention removes pruned traces and unreferenced snapshots", () =>
            {
                var store = new RunHistoryStore(Path.Combine(folder, "retention.db"), 2);
                GraphSnapshot Snapshot(string revision) => new(revision, "hall", 3, "{\"nodes\":[]}", "{\"nodes\":{}}");
                var start = new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc);
                var first = Completed("a", "R1", received: start);
                store.Save(first, Snapshot("R1"));
                Check(store.SaveTrace(first.RunId, "{\"format\":1}", 1, false), "trace of a known run rejected");
                Check(!store.SaveTrace("unknown", "{}", 0, false), "trace of an unknown run stored");
                Check(store.ReadRun(first.RunId)!.HasTrace && store.ReadTrace(first.RunId)!.Json == "{\"format\":1}", "trace not stored");
                first.Status = "Failed"; store.Save(first);
                Check(store.ReadRun(first.RunId)!.HasTrace, "saving the record cleared hasTrace");
                Check(store.SaveTrace(first.RunId, "{\"format\":1,\"complete\":true}", 2, true) && store.ReadTrace(first.RunId) is { EventCount: 2, Truncated: true, Format: 1 }, "trace not replaced");
                store.Save(Completed("a", "R1", received: start.AddMinutes(1)), Snapshot("R1"));
                store.Save(Completed("a", "R2", received: start.AddMinutes(2)), Snapshot("R2"));
                Check(store.ReadRun(first.RunId) == null && store.ReadTrace(first.RunId) == null, "pruned run kept its trace");
                Check(store.ReadSnapshot("R1") is { VisualId: "hall", VisualRevision: 3 }, "snapshot removed while a run still references it");
                store.Save(Completed("a", "R2", received: start.AddMinutes(3)));
                Check(store.ReadSnapshot("R1") == null && store.ReadSnapshot("R2") != null, "unreferenced snapshot kept or referenced one removed");
                // The known-snapshot set was cleared: the revision's next run stores it again.
                store.Save(Completed("a", "R1", received: start.AddMinutes(4)), Snapshot("R1"));
                Check(store.ReadSnapshot("R1") != null, "snapshot not re-inserted after retention removed it");
                return Task.CompletedTask;
            });

            await Test("status filter", () =>
            {
                var store = new RunHistoryStore(Path.Combine(folder, "filter.db"));
                foreach (var status in new[] { "Succeeded", "Failed", "Stopped", "Failed", "Cancelled" }) store.Save(Completed("a", "r", status));
                store.Save(Completed("other", "r", "Failed"));
                Check(store.Read("a", 0, 100, ["Failed"]).Count == 2 && store.Read("a", 0, 100, ["Failed", "Stopped"]).Count == 3, "status filter");
                Check(store.Read("a", 0, 100, []).Count == 5 && store.Read("a", 0, 100, null).Count == 5, "empty filter is not every run");
                Check(store.Read("a", 1, 1, ["Failed"]).Single().Status == "Failed", "filter pages after filtering");
                return Task.CompletedTask;
            });

            await Test("trigger summaries for every cause", () =>
            {
                JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();
                var state = Element("""
                    {"trigger":{"id":"0","idx":"0","alias":null,"platform":"state","entity_id":"climate.outside_bathroom_vtrv",
                      "from_state":{"entity_id":"climate.outside_bathroom_vtrv","state":"off","attributes":{}},
                      "to_state":{"entity_id":"climate.outside_bathroom_vtrv","state":"heat","attributes":{}},
                      "for":null,"attribute":null,"description":"state of climate.outside_bathroom_vtrv"},"context":{"id":"c"}}
                    """);
                Check(TriggerSummary.Summarize(InvocationCause.HomeAssistantTrigger, TriggerPayload.Native(state)) ==
                    """{"kind":"ha","platform":"state","id":"0","description":"state of climate.outside_bathroom_vtrv","entityId":"climate.outside_bathroom_vtrv","from":"off","to":"heat"}""", "ha state summary");
                var native = TriggerPayload.Native(Element("""{"trigger":{"id":"0","platform":"event","event":{"event_type":"doorbell_pressed","data":{}},"description":"event 'doorbell_pressed'"}}"""));
                Check(TriggerSummary.Summarize(InvocationCause.HomeAssistantTrigger, native) ==
                    """{"kind":"ha","platform":"event","id":"0","description":"event 'doorbell_pressed'","eventType":"doorbell_pressed"}""", "ha event summary");
                var entity = TriggerPayload.Entity(new() { EntityId = "binary_sensor.hall_motion", State = "on" }, new() { EntityId = "binary_sensor.hall_motion", State = "off" });
                Check(TriggerSummary.Summarize(InvocationCause.Entity, entity) == """{"kind":"entity","entityId":"binary_sensor.hall_motion","from":"off","to":"on"}""", "entity summary");
                Check(TriggerSummary.Summarize(InvocationCause.Schedule, TriggerPayload.Schedule("daily-0", new DateTime(2026, 11, 12, 6, 30, 0, DateTimeKind.Utc))) ==
                    """{"kind":"schedule","scheduleId":"daily-0","planned":"2026-11-12T06:30:00.0000000Z"}""", "schedule summary");
                Check(TriggerSummary.Summarize(InvocationCause.Manual, TriggerPayload.Manual()) == """{"kind":"manual"}"""
                    && TriggerSummary.Summarize(InvocationCause.Startup, TriggerPayload.Startup()) == """{"kind":"startup"}""", "manual/startup summary");
                // A huge native payload, with escapes and astral characters in every summarised field.
                string Huge(char c) => "\"\\\n😀" + new string(c, 20000);
                var huge = new JsonObject { ["trigger"] = new JsonObject {
                    ["platform"] = Huge('p'), ["id"] = Huge('i'), ["description"] = Huge('d'), ["entity_id"] = Huge('e'),
                    ["from_state"] = new JsonObject { ["state"] = Huge('f') }, ["to_state"] = new JsonObject { ["state"] = Huge('t'), ["attributes"] = new JsonObject { ["blob"] = new string('x', 100000) } },
                    ["event"] = new JsonObject { ["event_type"] = Huge('v') } } };
                var summary = TriggerSummary.Summarize(InvocationCause.HomeAssistantTrigger, TriggerPayload.Native(Element(huge.ToJsonString())));
                var parsed = JsonNode.Parse(summary)!.AsObject();
                Check(summary.Length <= 2048 && parsed["kind"]!.GetValue<string>() == "ha" && parsed.Count == 8 && parsed["description"]!.GetValue<string>().StartsWith("\"\\\n😀d"), "huge summary is not valid JSON within 2048 chars: " + summary.Length);
                Check(!JsonSerializer.Serialize(parsed).Contains("\\uD83D\"") , "summary split a surrogate pair");
                return Task.CompletedTask;
            });

            await Test("trace sink stores at most once per interval, holds the latest and writes final at once", async () =>
            {
                var store = new RunHistoryStore(Path.Combine(folder, "sink.db"));
                var run = new InvocationRecord { AutomationId = "a", Revision = "r", Action = "A::Run", Cause = "Manual" };
                store.Save(run);
                var sink = new RunTraceSink(store, run.RunId, TimeSpan.FromMilliseconds(300), TimeProvider.System);
                sink.Write("{\"n\":1}", 1, false, false);
                Check(store.ReadTrace(run.RunId)?.Json == "{\"n\":1}", "first write not stored at once");
                sink.Write("{\"n\":2}", 2, false, false); sink.Write("{\"n\":3}", 3, false, false);
                Check(store.ReadTrace(run.RunId)?.Json == "{\"n\":1}", "write inside the interval stored at once");
                await Until(() => store.ReadTrace(run.RunId)?.Json == "{\"n\":3}", "held write stored when the interval ends");
                sink.Write("{\"n\":4}", 4, false, false);
                sink.Write("{\"n\":5}", 5, true, true);
                Check(store.ReadTrace(run.RunId) is { Json: "{\"n\":5}", EventCount: 5, Truncated: true }, "final write not stored at once");
                sink.Write("{\"n\":6}", 6, false, false);
                await Task.Delay(400);
                Check(store.ReadTrace(run.RunId)?.Json == "{\"n\":5}", "write after final or a held write after final was stored");
            });

            await Test("cancel reasons for restart, reload, stop and unknown", async () =>
            {
                PyInterop.Entity = id => new() { EntityId = id, State = "fresh" };
                Probe.Mode = AutomationMode.Restart;
                Probe.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Probe.Starts = System.Threading.Channels.Channel.CreateUnbounded<InvocationCause>();
                var history = new RunHistoryStore(Path.Combine(folder, "cancel.db"));
                var manager = new UserScriptManager { History = history };
                var compiled = new CompiledUserScript { FilePath = Path.Combine(folder, "cancel.cs"), FileName = "cancel", SourceCode = "cancel", Assembly = typeof(Probe).Assembly, WrittenToDisk = true };
                try
                {
                    await manager.LoadUserScripts([compiled]);
                    var id = manager.GetUserScripts().Single().AutomationId;
                    InvocationRecord Run(string cause, string status) => history.Read(id, 0, 100).SingleOrDefault(r => r.Cause == cause && r.Status == status)!;
                    await Probe.Starts.Reader.ReadAsync();
                    var manual = manager.DispatchManual(id, nameof(Probe), nameof(Probe.Run));
                    await Until(() => Run("Startup", "Cancelled") != null, "restart cancels the startup run");
                    Check(Run("Startup", "Cancelled").Error == "Restarted by a newer trigger", "restart reason: " + Run("Startup", "Cancelled").Error);
                    await Probe.Starts.Reader.ReadAsync();
                    await manager.UpdateUserScript(new CompiledUserScript { FilePath = compiled.FilePath, FileName = "cancel", SourceCode = "cancel 2", Assembly = typeof(Probe).Assembly, WrittenToDisk = true });
                    await Until(() => history.ReadRun(manual)?.Status == "Cancelled", "reload cancels the manual run");
                    Check(history.ReadRun(manual)!.Error == "Automation reloaded", "reload reason: " + history.ReadRun(manual)!.Error);
                    await Probe.Starts.Reader.ReadAsync();
                    await manager.UnloadUserScriptsAsync(CancelReasons.Stopping);
                    await Until(() => history.Read(id, 0, 100).Count(r => r.Cause == "Startup" && r.Status == "Cancelled") == 2, "stopping cancels the new startup run");
                    Check(history.Read(id, 0, 100).First(r => r.Cause == "Startup").Error == "Home Assistant stopping", "stopping reason");
                    await manager.LoadUserScripts([new CompiledUserScript { FilePath = compiled.FilePath, FileName = "cancel", SourceCode = "cancel 3", Assembly = typeof(Probe).Assembly, WrittenToDisk = true }]);
                    await Probe.Starts.Reader.ReadAsync();
                    await manager.UnloadUserScriptsAsync();
                    await Until(() => history.Read(id, 0, 100).Count(r => r.Cause == "Startup" && r.Status == "Cancelled") == 3, "an unnamed unload cancels the startup run");
                    Check(history.Read(id, 0, 100).First(r => r.Cause == "Startup").Error == "Invocation cancelled", "unknown reason");
                    Check(history.Read(id, 0, 100).All(r => !r.HasTrace && r.VisualId == null && history.ReadSnapshot(r.Revision) == null), "a code automation run has a trace or snapshot");
                }
                finally { await manager.UnloadUserScriptsAsync(); Probe.Release.TrySetResult(); Probe.Mode = AutomationMode.Single; }
            });

            await Test("embedded catalog keeps only used built-in kinds, every library kind and their types", () =>
            {
                var diagnostics = new DiagnosticsProvider(); diagnostics.GenerateHassEntities(["binary_sensor.motion", "light.hall"]);
                var libraries = new NodeLibraryCompiler(diagnostics).Preview([new("helpers", "public static class Helpers { public sealed class Box { public int Size { get; set; } } [Node(\"Make box\")] public static Box Make(int size) => new() { Size = size }; }", 1)]);
                Check(libraries.Valid, "library rejected: " + JsonSerializer.Serialize(libraries.Diagnostics));
                var catalog = VisualGenerator.EmbeddedCatalog(StopGraph(), libraries.Snapshot);
                var kinds = catalog["nodes"]!.AsObject().Select(p => p.Key).ToArray();
                var library = kinds.Where(k => k.StartsWith("library:")).ToArray();
                Check(library.Length == 1 && kinds.Except(library).Order().SequenceEqual(["log", "stop", "trigger"]), "kinds: " + string.Join(",", kinds));
                var types = catalog["types"]!.AsObject();
                var result = catalog["nodes"]![library[0]]!["outputs"]!["result"]!["type"]!.GetValue<string>();
                Check(types.ContainsKey(result) && types.ContainsKey("global::HassSharp.EntityStateSnapshot") && types.ContainsKey("entity_id") && !types.ContainsKey("target"), "types: " + string.Join(",", types.Select(t => t.Key)));
                Check(catalog["parametricTypes"] != null && catalog["triggerOutputs"] != null && catalog["limits"] != null && !catalog.ContainsKey("services"), "indirect catalog sections dropped");
                var noTrigger = VisualGenerator.EmbeddedCatalog(JsonNode.Parse("""{"version":5,"nodes":[{"id":"x","kind":"stop","config":{}}],"edges":[]}""")!.AsObject(), libraries.Snapshot);
                Check(!noTrigger.ContainsKey("triggerOutputs"), "triggerOutputs kept without a trigger");
                var preview = new VisualGenerator(diagnostics).Preview(StopGraph(), libraries.Snapshot);
                Check(preview.Valid && preview.EmbeddedCatalog == catalog.ToJsonString() && preview.Source.Contains(JsonSerializer.Serialize(catalog.ToJsonString())), "generated class does not embed the pruned catalog");
                Check(!JsonSerializer.Serialize(preview).Contains("embeddedCatalog", StringComparison.OrdinalIgnoreCase), "preview serialises the embedded catalog");
                return Task.CompletedTask;
            });

            await Test("visual runs record trigger, snapshot and trace; Stopped status; list omits source", async () =>
            {
                var root = Path.Combine(folder, "manager");
                var previous = HassPath.HassConfiguration; HassPath.HassConfiguration = root;
                var api = new HassSharpManager(root);
                try
                {
                    api.SetServiceDescriptions(Fixture("ha-service-descriptions.json"));
                    await api.Init(["binary_sensor.motion", "light.hall"]);
                    async Task<JsonNode> Visual(object request) => JsonNode.Parse(await api.VisualRequest(JsonSerializer.Serialize(request)))!;
                    var saved = await Visual(new { operation = "save", id = "front-door", graph = StopGraph(), expectedRevision = 0 });
                    Check(saved["ok"]!.GetValue<bool>() && saved["result"]!["active"]!.GetValue<bool>(), "save rejected: " + saved.ToJsonString());
                    var automation = saved["result"]!["document"]!["automationId"]!.GetValue<string>();
                    var listed = (await Visual(new { operation = "list" }))["result"]!.AsArray().Single()!.AsObject();
                    Check(!listed.ContainsKey("source") && !listed.ContainsKey("sourceMap") && listed["graph"] != null && listed["currentRevisionActive"]!.GetValue<bool>(), "list shape: " + listed.ToJsonString());
                    var read = (await Visual(new { operation = "read", id = "front-door" }))["result"]!.AsObject();
                    Check(read.ContainsKey("source") && read.ContainsKey("sourceMap"), "read lost source");

                    var runId = (await Visual(new { operation = "run", id = "front-door", triggerId = "t" }))["result"]!["runId"]!.GetValue<string>();
                    JsonNode History(params string[] status) => JsonNode.Parse(api.GetRunHistory(automation, 0, 20, status))!;
                    await Until(() => History()["runs"]!.AsArray().Any(r => r!["runId"]!.GetValue<string>() == runId && r["endUtc"] != null), "visual run completes");
                    var page = History();
                    var run = page["runs"]!.AsArray().Single()!.AsObject();
                    Check(page["version"]!.GetValue<int>() == 2, "history version");
                    string[] fields = ["runId", "automationId", "revision", "action", "actionLabel", "triggerNodeId", "visualId", "visualRevision", "cause", "trigger", "plannedUtc", "receivedUtc", "startUtc", "endUtc", "durationMs", "status", "error", "hasTrace"];
                    Check(run.Select(p => p.Key).SequenceEqual(fields), "run fields: " + string.Join(",", run.Select(p => p.Key)));
                    Check(run["status"]!.GetValue<string>() == "Stopped" && run["error"] == null, "stop node did not record Stopped: " + run.ToJsonString());
                    Check(run["actionLabel"]!.GetValue<string>() == "Front door opens" && run["triggerNodeId"]!.GetValue<string>() == "t"
                        && run["visualId"]!.GetValue<string>() == "front-door" && run["visualRevision"]!.GetValue<long>() == 1
                        && run["trigger"]!.ToJsonString() == """{"kind":"manual"}""" && !run["hasTrace"]!.GetValue<bool>(), "visual run fields: " + run.ToJsonString());
                    Check(History("Stopped")["runs"]!.AsArray().Count == 1 && History("Failed", "Succeeded")["runs"]!.AsArray().Count == 0, "status filter through the manager");

                    var trace = JsonNode.Parse(api.GetRunTrace(runId))!.AsObject();
                    Check(trace["version"]!.GetValue<int>() == 1 && !trace["live"]!.GetValue<bool>() && trace["trace"] == null && trace["run"]!["runId"]!.GetValue<string>() == runId, "run_trace before a trace: " + trace.ToJsonString());
                    var snapshot = trace["snapshot"]!.AsObject();
                    var revision = run["revision"]!.GetValue<string>();
                    Check(snapshot["revision"]!.GetValue<string>() == revision && snapshot["visualId"]!.GetValue<string>() == "front-door" && snapshot["visualRevision"]!.GetValue<long>() == 1, "snapshot identity");
                    Check(JsonNode.DeepEquals(snapshot["graph"], read["graph"]), "snapshot graph differs from the saved graph");
                    var running = api.GetUserScript("visual_front-door")!.Source;
                    Check(running.Contains(JsonSerializer.Serialize(snapshot["catalog"]!.ToJsonString())) && !snapshot["catalog"]!.AsObject().ContainsKey("services")
                        && snapshot["catalog"]!["nodes"]!.AsObject().Select(p => p.Key).Order().SequenceEqual(["log", "stop", "trigger"]), "snapshot catalog is not the pruned embedded catalog");

                    var traceJson = """{"format":1,"runId":"RUN","rootNodeId":"t","startedUtc":"2026-11-13T00:00:00Z","complete":true,"truncated":false,"dropped":0,"trigger":{"platform":"manual"},"events":[],"summary":{}}""".Replace("RUN", runId);
                    new RunTraceSink(new RunHistoryStore(Path.Combine(root, ".storage", "hass_sharp", "runs.db")), runId).Write(traceJson, 0, false, true);
                    trace = JsonNode.Parse(api.GetRunTrace(runId))!.AsObject();
                    Check(JsonNode.DeepEquals(trace["trace"], JsonNode.Parse(traceJson)) && trace["run"]!["hasTrace"]!.GetValue<bool>() && History()["runs"]![0]!["hasTrace"]!.GetValue<bool>(), "stored trace not returned: " + trace.ToJsonString());
                    Check(api.GetRunTrace("missing") == "null", "unknown run");

                    // Init after the generated source changed: the refreshed revision is active and its runs still snapshot.
                    await api.Init(["binary_sensor.motion", "light.hall"]);
                    Check((await Visual(new { operation = "read", id = "front-door" }))["result"]!["currentRevisionActive"]!.GetValue<bool>(), "graph not active after Init");
                }
                finally { await api.UnloadAsync(); HassPath.HassConfiguration = previous; }
            });

            await Test("a code automation run has no trace and no snapshot in run_trace", async () =>
            {
                var root = Path.Combine(folder, "code");
                var previous = HassPath.HassConfiguration; HassPath.HassConfiguration = root;
                Directory.CreateDirectory(HassPath.UserScripts);
                File.WriteAllText(Path.Combine(HassPath.UserScripts, "plain.cs"), "public class Plain : Automation { public override AutomationConfiguration Configure() => new(new AutomationAction(nameof(Go))); public Task Go() => Task.CompletedTask; }");
                var api = new HassSharpManager(root);
                try
                {
                    await api.Init([]);
                    var summary = api.GetScriptSummaries().Single(s => s.FileName == "plain");
                    var runId = JsonNode.Parse(api.RunNow(summary.AutomationId!, "Plain", "Go"))!["runId"]!.GetValue<string>();
                    await Until(() => JsonNode.Parse(api.GetRunTrace(runId))!["run"]!["status"]!.GetValue<string>() == "Succeeded", "code run completes");
                    var trace = JsonNode.Parse(api.GetRunTrace(runId))!;
                    Check(trace["snapshot"] == null && trace["trace"] == null && !trace["run"]!["hasTrace"]!.GetValue<bool>() && trace["run"]!["visualId"] == null
                        && trace["run"]!["actionLabel"]!.GetValue<string>() == "Go" && trace["run"]!["trigger"]!.ToJsonString() == """{"kind":"manual"}""", "code run: " + trace.ToJsonString());
                }
                finally { await api.UnloadAsync(); HassPath.HassConfiguration = previous; }
            });
        }
        finally { Directory.Delete(folder, true); }
        if (failures.Count != 0) throw new Exception(string.Join("\n", failures));
    }
}
