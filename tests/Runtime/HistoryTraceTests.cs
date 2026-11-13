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
                // A cut that lands right after a high surrogate drops the whole pair.
                foreach (var length in new[] { 2, 3, 4 })
                {
                    var shortened = TriggerSummary.Shorten("a😀b😀c", length);
                    Check(shortened.EndsWith('…') && !shortened.Where((c, i) => char.IsHighSurrogate(c) && (i + 1 >= shortened.Length || !char.IsLowSurrogate(shortened[i + 1]))).Any(), $"Shorten({length}) left a lone high surrogate: {shortened}");
                }
                Check(TriggerSummary.Shorten("a😀b", 3) == "a…", "cut after the high surrogate kept it");
                return Task.CompletedTask;
            });

            await Test("trace sink stores at most once per interval, holds the latest and writes final at once", () =>
            {
                var store = new RunHistoryStore(Path.Combine(folder, "sink.db"));
                var run = new InvocationRecord { AutomationId = "a", Revision = "r", Action = "A::Run", Cause = "Manual" };
                store.Save(run);
                var time = new ManualTime();
                var sink = new RunTraceSink(store, run.RunId, TimeSpan.FromSeconds(1), time);
                sink.Write("{\"n\":1}", 1, false, false);
                Check(store.ReadTrace(run.RunId)?.Json == "{\"n\":1}", "first write not stored at once");
                time.Advance(TimeSpan.FromMilliseconds(400));
                sink.Write("{\"n\":2}", 2, false, false); sink.Write("{\"n\":3}", 3, false, false);
                Check(store.ReadTrace(run.RunId)?.Json == "{\"n\":1}", "write inside the interval stored at once");
                time.Advance(TimeSpan.FromMilliseconds(599));
                Check(store.ReadTrace(run.RunId)?.Json == "{\"n\":1}", "held write stored before the interval ended");
                time.Advance(TimeSpan.FromMilliseconds(1));
                Check(store.ReadTrace(run.RunId)?.Json == "{\"n\":3}" && time.Pending == 0, "the latest held write not stored when the interval ended");
                time.Advance(TimeSpan.FromMilliseconds(100));
                sink.Write("{\"n\":4}", 4, false, false);
                sink.Write("{\"n\":5}", 5, true, true);
                Check(store.ReadTrace(run.RunId) is { Json: "{\"n\":5}", EventCount: 5, Truncated: true } && time.Pending == 0, "final write not stored at once, or the held write survived it");
                sink.Write("{\"n\":6}", 6, false, false);
                time.Advance(TimeSpan.FromSeconds(5));
                Check(store.ReadTrace(run.RunId)?.Json == "{\"n\":5}", "a write after final was stored");
                return Task.CompletedTask;
            });

            await Test("concurrent sink writes: no exception and the final document wins", async () =>
            {
                var store = new RunHistoryStore(Path.Combine(folder, "sink-concurrent.db"));
                var run = new InvocationRecord { AutomationId = "a", Revision = "r", Action = "A::Run", Cause = "Manual" };
                store.Save(run);
                var time = new ManualTime();
                var sink = new RunTraceSink(store, run.RunId, TimeSpan.FromSeconds(1), time);
                using var start = new ManualResetEventSlim();
                var writers = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
                {
                    start.Wait();
                    for (var i = 0; i < 50; i++)
                    {
                        sink.Write($"{{\"t\":{t},\"i\":{i}}}", i, false, false);
                        if (i % 10 == 0) time.Advance(TimeSpan.FromMilliseconds(300));
                        if (t == 3 && i == 25) sink.Write("{\"final\":true}", 99, true, true);
                    }
                })).ToArray();
                start.Set();
                await Task.WhenAll(writers);
                time.Advance(TimeSpan.FromSeconds(5));
                Check(store.ReadTrace(run.RunId) is { Json: "{\"final\":true}", EventCount: 99, Truncated: true }, "final document did not win: " + store.ReadTrace(run.RunId)?.Json);
            });

            await Test("a sink write after its run was pruned stores nothing and does not throw", () =>
            {
                var store = new RunHistoryStore(Path.Combine(folder, "sink-pruned.db"), 1);
                var pruned = Completed("a", "r", received: new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc));
                store.Save(pruned);
                store.Save(Completed("a", "r", received: new DateTime(2026, 11, 12, 0, 1, 0, DateTimeKind.Utc)));
                Check(store.ReadRun(pruned.RunId) == null, "run not pruned");
                var time = new ManualTime();
                var sink = new RunTraceSink(store, pruned.RunId, TimeSpan.FromSeconds(1), time);
                sink.Write("{}", 0, false, false); sink.Write("{\"held\":1}", 1, false, false);
                time.Advance(TimeSpan.FromSeconds(2));
                sink.Write("{\"final\":1}", 1, false, true);
                Check(store.ReadTrace(pruned.RunId) == null && store.ReadRun(pruned.RunId) == null, "trace stored for a pruned run");
                return Task.CompletedTask;
            });

            await Test("traces are kept only for the newest traced runs", () =>
            {
                var store = new RunHistoryStore(Path.Combine(folder, "trace-cap.db"), 100, 2);
                var start = new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc);
                var runs = Enumerable.Range(0, 4).Select(i => Completed("a", "r", received: start.AddMinutes(i))).ToArray();
                foreach (var run in runs) { store.Save(run); Check(store.SaveTrace(run.RunId, "{\"n\":" + run.ReceivedUtc.Minute + "}", 0, false), "trace rejected"); }
                // Retention runs with the next save; an untraced newer run does not count against the cap.
                store.Save(Completed("a", "r", received: start.AddMinutes(10)));
                var traced = runs.Select(r => (store.ReadRun(r.RunId)!.HasTrace, store.ReadTrace(r.RunId) != null)).ToArray();
                Check(traced.SequenceEqual([(false, false), (false, false), (true, true), (true, true)]), "trace retention: " + string.Join(",", traced));
                Check(store.Read("a", 0, 100).Count == 5, "trace retention removed runs");
                Check(RunHistoryStore.TraceRetention == 1000 && RunHistoryStore.DefaultRetention == 10000, "retention constants");
                return Task.CompletedTask;
            });

            await Test("a trigger summary that throws falls back to its kind and the run still executes", async () =>
            {
                PyInterop.Entity = id => new() { EntityId = id, State = "fresh" };
                Probe.Mode = AutomationMode.Single;
                Probe.Release = new(TaskCreationOptions.RunContinuationsAsynchronously); Probe.Release.SetResult();
                Probe.Starts = System.Threading.Channels.Channel.CreateUnbounded<InvocationCause>();
                var history = new RunHistoryStore(Path.Combine(folder, "summary.db"));
                var manager = new UserScriptManager { History = history };
                try
                {
                    await manager.LoadUserScripts([new CompiledUserScript { FilePath = Path.Combine(folder, "summary.cs"), FileName = "summary", SourceCode = "summary", Assembly = typeof(Probe).Assembly, WrittenToDisk = true }]);
                    var script = manager.GetUserScripts().Single();
                    await script.Classes.Single().StartupCompletion;
                    ScriptSyncRunner.Summarize = (_, _) => throw new InvalidOperationException("summary failed");
                    try { await script.Classes.Single().RunMethod(nameof(Probe.Run), InvocationCause.Manual); }
                    finally { ScriptSyncRunner.Summarize = TriggerSummary.Summarize; }
                    var run = history.Read(script.AutomationId, 0, 10).First(r => r.Cause == "Manual");
                    Check(run.Status == "Succeeded" && run.Trigger == "{\"kind\":\"manual\"}", "fallback summary: " + JsonSerializer.Serialize(run));
                }
                finally { await manager.UnloadUserScriptsAsync(); }
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

            await Test("manager unload records reloaded or stopping, and a stop cancels runs with the stopping reason", async () =>
            {
                var root = Path.Combine(folder, "reasons");
                var previous = HassPath.HassConfiguration; HassPath.HassConfiguration = root;
                Directory.CreateDirectory(HassPath.UserScripts);
                File.WriteAllText(Path.Combine(HassPath.UserScripts, "hold.cs"), "public class Hold : Automation { public override AutomationConfiguration Configure() => new(new AutomationAction(nameof(Go), Startup: true)); public Task Go() => Task.Delay(-1, CancellationToken); }");
                var api = new HassSharpManager(root);
                try
                {
                    JsonNode[] Runs(string automation) => JsonNode.Parse(api.GetRunHistory(automation, 0, 100, null))!["runs"]!.AsArray().Select(r => r!).ToArray();
                    async Task<string> Cancelled(Func<Task> cancel, int count)
                    {
                        await api.Init([]);
                        var automation = api.GetScriptSummaries().Single(s => s.FileName == "hold").AutomationId!;
                        await Until(() => Runs(automation).Any(r => r["status"]!.GetValue<string>() == "Running"), "startup run is running");
                        await cancel();
                        await Until(() => Runs(automation).Count(r => r["status"]!.GetValue<string>() == "Cancelled") == count, "startup run cancelled");
                        return Runs(automation)[0]["error"]!.GetValue<string>();
                    }
                    Check(await Cancelled(() => api.UnloadAsync(false), 1) == "Automation reloaded", "an unload that is not a stop");
                    Check(await Cancelled(() => api.UnloadAsync(true), 2) == "Home Assistant stopping", "an unload while stopping");
                    Check(await Cancelled(() => { api.CancelRunsForShutdown(); return Task.CompletedTask; }, 3) == "Home Assistant stopping", "the stop event");
                    // After the stop, a new run is recorded as cancelled for the same reason, and an unload still works.
                    var automation = api.GetScriptSummaries().Single(s => s.FileName == "hold").AutomationId!;
                    var late = JsonNode.Parse(api.RunNow(automation, "Hold", "Go"))!["runId"]!.GetValue<string>();
                    var lateRun = JsonNode.Parse(api.GetRunTrace(late))!["run"]!;
                    Check(lateRun["status"]!.GetValue<string>() == "Cancelled" && lateRun["error"]!.GetValue<string>() == "Home Assistant stopping", "run after stop: " + lateRun.ToJsonString());
                    await api.UnloadAsync(true);
                    Check(api.GetScriptSummaries().All(s => !s.Active), "unload after stop left a script active");
                }
                finally { await api.UnloadAsync(); HassPath.HassConfiguration = previous; }
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

            await Test("visual runs record trigger, snapshot and trace; Stopped status; list omits source; snapshot identity; refreshed Init", async () =>
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
                        && run["trigger"]!.ToJsonString() == """{"kind":"manual"}""" && run["hasTrace"]!.GetValue<bool>(), "visual run fields: " + run.ToJsonString());
                    Check(History("Stopped")["runs"]!.AsArray().Count == 1 && History("Failed", "Succeeded")["runs"]!.AsArray().Count == 0, "status filter through the manager");

                    var trace = JsonNode.Parse(api.GetRunTrace(runId))!.AsObject();
                    // The recorder (Task 12) stored the run's trace: the stop node ended it.
                    Check(trace["version"]!.GetValue<int>() == 1 && !trace["live"]!.GetValue<bool>() && trace["run"]!["runId"]!.GetValue<string>() == runId
                        && trace["trace"]!["complete"]!.GetValue<bool>() && trace["trace"]!["events"]!.AsArray().Select(e => e!["status"]!.GetValue<string>()).SequenceEqual(["ok", "ok", "stopped"]), "run_trace of the recorded trace: " + trace.ToJsonString());
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

                    // Snapshot identity is the run's: a new revision with unchanged source, and an identical second
                    // document, share the first snapshot row but each shows its own id and revision.
                    async Task<JsonObject> RunAndTrace(string id)
                    {
                        var dispatched = (await Visual(new { operation = "run", id, triggerId = "t" }))["result"]!["runId"]!.GetValue<string>();
                        await Until(() => JsonNode.Parse(api.GetRunTrace(dispatched))!["run"]!["endUtc"] != null, "run of " + id + " completes");
                        return JsonNode.Parse(api.GetRunTrace(dispatched))!.AsObject();
                    }
                    var resaved = await Visual(new { operation = "save", id = "front-door", graph = read["graph"], expectedRevision = 1 });
                    Check(resaved["ok"]!.GetValue<bool>() && resaved["result"]!["active"]!.GetValue<bool>(), "re-save rejected: " + resaved.ToJsonString());
                    var copy = await Visual(new { operation = "save", id = "back-door", graph = read["graph"], expectedRevision = 0 });
                    Check(copy["ok"]!.GetValue<bool>() && copy["result"]!["active"]!.GetValue<bool>(), "copy rejected: " + copy.ToJsonString());
                    foreach (var (id, expected) in new[] { ("front-door", 2L), ("back-door", 1L) })
                    {
                        var viewed = await RunAndTrace(id);
                        Check(viewed["run"]!["revision"]!.GetValue<string>() == revision, id + " does not share the first snapshot row, so the test proves nothing");
                        Check(viewed["snapshot"]!["visualId"]!.GetValue<string>() == id && viewed["snapshot"]!["visualRevision"]!.GetValue<long>() == expected
                            && viewed["run"]!["visualId"]!.GetValue<string>() == id && viewed["run"]!["visualRevision"]!.GetValue<long>() == expected, "snapshot identity differs from the run: " + viewed.ToJsonString());
                    }

                    // A library written outside a library save changes the embedded catalog: Init stores the regenerated
                    // source at the same revision, the graph stays active, and its next run records the new snapshot.
                    Directory.CreateDirectory(Path.Combine(root, ".storage", "hass_sharp", "libraries"));
                    File.WriteAllText(Path.Combine(root, ".storage", "hass_sharp", "libraries", "helpers.json"),
                        JsonSerializer.Serialize(new NodeLibraryDocument(1, "helpers", "Helpers", 1, "public static class Helpers { [Node(\"Noop\")] public static void Noop() { } }"), VisualStore.Json));
                    await api.Init(["binary_sensor.motion", "light.hall"]);
                    var refreshed = (await Visual(new { operation = "read", id = "front-door" }))["result"]!;
                    Check(refreshed["currentRevisionActive"]!.GetValue<bool>() && refreshed["revision"]!.GetValue<long>() == 2, "graph not active at its revision after Init: " + refreshed.ToJsonString());
                    var after = await RunAndTrace("front-door");
                    Check(after["run"]!["revision"]!.GetValue<string>() != revision && after["snapshot"]!["revision"]!.GetValue<string>() == after["run"]!["revision"]!.GetValue<string>()
                        && after["snapshot"]!["catalog"]!["nodes"]!.AsObject().Any(p => p.Key.StartsWith("library:")) && after["snapshot"]!["visualRevision"]!.GetValue<long>() == 2, "run after the regenerated Init: " + after.ToJsonString());
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

/// <summary>A clock that moves only when told; due timers fire synchronously inside <see cref="Advance"/>.</summary>
sealed class ManualTime : TimeProvider
{
    readonly Lock gate = new();
    readonly List<ManualTimer> timers = [];
    DateTimeOffset now = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() { lock (gate) return now; }
    public int Pending { get { lock (gate) return timers.Count(t => !t.Disposed); } }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (gate) { var timer = new ManualTimer(this, callback, state, now + dueTime); timers.Add(timer); return timer; }
    }
    public void Advance(TimeSpan by)
    {
        ManualTimer[] due;
        lock (gate) { now += by; due = timers.Where(t => !t.Disposed && t.Due <= now).ToArray(); foreach (var t in due) timers.Remove(t); }
        foreach (var timer in due) timer.Fire();
    }
    internal void Remove(ManualTimer timer) { lock (gate) timers.Remove(timer); }

    internal sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state, DateTimeOffset due) : ITimer
    {
        public DateTimeOffset Due { get; private set; } = due;
        public bool Disposed { get; private set; }
        public void Fire() { if (!Disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) { Due = owner.GetUtcNow() + dueTime; return !Disposed; }
        public void Dispose() { Disposed = true; owner.Remove(this); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
