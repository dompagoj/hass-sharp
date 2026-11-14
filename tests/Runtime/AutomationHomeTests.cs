using HassSharp;
using System.Text.Json;
using System.Text.Json.Nodes;

// The automations page (Task 24b): turning an automation on and off, and the home list with outlines and recent runs.
static class AutomationHomeTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static async Task Until(Func<bool> condition, string message)
    {
        for (var i = 0; i < 250; i++) { if (condition()) return; await Task.Delay(20); }
        throw new Exception("timed out: " + message);
    }
    static RunHistoryStore History(HassSharpManager api) =>
        (RunHistoryStore)typeof(HassSharpManager).GetField("_history", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(api)!;

    // One trigger of each kind the switch withholds (entity, schedule, startup) and a manual one whose run waits until cancelled.
    static JsonObject Switchable(string name = "Door") => VisualExecutionTests.Derived(JsonNode.Parse("""
    {"version":5,"name":"NAME","nodes":[
      {"id":"door","kind":"trigger","config":{"type":"entity","entity":"binary_sensor.door"}},
      {"id":"noon","kind":"trigger","config":{"type":"daily","time":"12:00","timeZone":"UTC"}},
      {"id":"boot","kind":"trigger","config":{"type":"startup"}},
      {"id":"hold","kind":"trigger","config":{"type":"manual"}},
      {"id":"l1","kind":"log","config":{"value":{"kind":"literal","type":"string","value":"door"}}},
      {"id":"l2","kind":"log","config":{"value":{"kind":"literal","type":"string","value":"noon"}}},
      {"id":"l3","kind":"log","config":{"value":{"kind":"literal","type":"string","value":"boot"}}},
      {"id":"wait","kind":"custom","config":HOLD}
    ],"edges":[{"id":"e1","source":"door","target":"l1"},{"id":"e2","source":"noon","target":"l2"},{"id":"e3","source":"boot","target":"l3"},{"id":"e4","source":"hold","target":"wait"}]}
    """.Replace("NAME", name).Replace("HOLD", VisualExecutionTests.Code("wait", "await Task.Delay(-1, CancellationToken);")["config"]!.ToJsonString()))!.AsObject());

    [RuntimeSuite("--automation-home", Order = 7)]
    public static async Task Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "automation-home-" + Guid.NewGuid());
        var previous = HassPath.HassConfiguration;
        var register = PyInterop.RegisterSchedule;
        var failures = new List<string>();
        async Task Test(string name, Func<Task> test)
        {
            try { await test(); Console.WriteLine("PASS automation home: " + name); }
            catch (Exception ex) { failures.Add(name + ": " + ex); }
        }
        try
        {
            var schedules = new List<Func<string, Task>>();
            PyInterop.RegisterSchedule = (_, fire) => { lock (schedules) schedules.Add(fire); return () => { }; };

            await Test("off withholds entity, schedule and startup runs, Run now still works, on again needs no recompile, off cancels runs; survives restart and save", async () =>
            {
                var root = Path.Combine(folder, "switch"); HassPath.HassConfiguration = root;
                string[] entities = ["binary_sensor.door"];
                var api = new HassSharpManager(root);
                try
                {
                    await api.Init(entities);
                    async Task<JsonNode> Visual(object request) => JsonNode.Parse(await api.VisualRequest(JsonSerializer.Serialize(request)))!;
                    var saved = await Visual(new { operation = "save", id = "door", graph = Switchable(), expectedRevision = 0 });
                    Check(saved["result"]?["active"]?.GetValue<bool>() == true && saved["result"]!["document"]!["enabled"]!.GetValue<bool>(), "a new automation is not on and active: " + saved.ToJsonString());
                    var automation = saved["result"]!["document"]!["automationId"]!.GetValue<string>();
                    JsonNode[] Runs() => JsonNode.Parse(api.GetRunHistory(automation, 0, 100, null))!["runs"]!.AsArray().Select(r => r!).ToArray();
                    int Count(string cause, string? status = null) => Runs().Count(r => r["cause"]!.GetValue<string>() == cause && (status == null || r["status"]!.GetValue<string>() == status));
                    Func<string, Task> Schedule() { lock (schedules) return schedules[^1]; }
                    Task Door() => api.OnTrackedEntityChange("binary_sensor.door", new() { EntityId = "binary_sensor.door", State = "on" }, new() { EntityId = "binary_sensor.door", State = "off" });
                    await Until(() => Count("Startup", "Succeeded") == 1, "the startup run of an automation that is on");

                    var off = await Visual(new { operation = "set_enabled", id = "door", enabled = false });
                    Check(off["ok"]!.GetValue<bool>() && off["result"]!.ToJsonString() == """{"id":"door","enabled":false}""", "set_enabled result: " + off.ToJsonString());
                    var read = (await Visual(new { operation = "read", id = "door" }))["result"]!;
                    Check(!read["enabled"]!.GetValue<bool>() && read["revision"]!.GetValue<long>() == 1 && JsonNode.DeepEquals(read["graph"], saved["result"]!["document"]!["graph"]) && read["currentRevisionActive"]!.GetValue<bool>(),
                        "turning off changed the revision or graph, or unloaded it: " + read.ToJsonString());
                    var loaded = VisualTests.Loaded(api, "door");
                    await Door();
                    await Schedule()("2026-11-12T12:00:00+00:00");
                    await Task.Delay(50);
                    Check(Count("Entity") == 0 && Count("Schedule") == 0, "an entity or schedule trigger ran while off: " + string.Join(", ", Runs().Select(r => r["cause"])));
                    var manual = await Visual(new { operation = "run", id = "door", triggerId = "door" });
                    Check(manual["ok"]!.GetValue<bool>(), "Run now refused while off: " + manual.ToJsonString());
                    await Until(() => Count("Manual", "Succeeded") == 1, "Run now while off");

                    await api.Init(entities);
                    await Task.Delay(100);
                    read = (await Visual(new { operation = "read", id = "door" }))["result"]!;
                    Check(!read["enabled"]!.GetValue<bool>() && Count("Startup") == 1, "off did not survive a restart, or the startup run ran while off");
                    var renamed = Switchable("Front door");
                    var resaved = await Visual(new { operation = "save", id = "door", graph = renamed, expectedRevision = 1 });
                    Check(resaved["result"]?["active"]?.GetValue<bool>() == true && !resaved["result"]!["document"]!["enabled"]!.GetValue<bool>(), "a save turned the automation on: " + resaved.ToJsonString());
                    await Task.Delay(100);
                    Check(Count("Startup") == 1 && !(await Visual(new { operation = "read", id = "door" }))["result"]!["enabled"]!.GetValue<bool>(), "a save while off ran the startup run or lost the state");
                    var listed = (await Visual(new { operation = "list" }))["result"]!.AsArray().Single()!.AsObject();
                    Check(!listed["enabled"]!.GetValue<bool>() && listed["graph"] != null && !listed.ContainsKey("savedUtc") && !listed.ContainsKey("recentRuns"), "list shape: " + listed.ToJsonString());

                    // A library save rebuilds every graph at a new revision: it keeps the switch too.
                    var library = JsonNode.Parse(await api.NodeLibraryRequest(JsonSerializer.Serialize(new
                        { operation = "save", id = "helpers", name = "Helpers", expectedRevision = 0, source = "public static class Helpers { [Node(\"Noop\")] public static void Noop() { } }" })))!;
                    Check(library["ok"]!.GetValue<bool>(), "library save: " + library.ToJsonString());
                    read = (await Visual(new { operation = "read", id = "door" }))["result"]!;
                    Check(read["revision"]!.GetValue<long>() == 3 && !read["enabled"]!.GetValue<bool>() && Count("Startup") == 1, "a library rebuild turned the automation on: " + read["enabled"]);

                    loaded = VisualTests.Loaded(api, "door");
                    var on = await Visual(new { operation = "set_enabled", id = "door", enabled = true });
                    Check(on["result"]!["enabled"]!.GetValue<bool>() && ReferenceEquals(loaded, VisualTests.Loaded(api, "door")), "turning on recompiled or replaced the script");
                    await Door();
                    await Schedule()("2026-11-13T12:00:00+00:00");
                    await Until(() => Count("Entity", "Succeeded") == 1 && Count("Schedule", "Succeeded") == 1, "triggers fire once on again");
                    Check(Count("Startup") == 1, "turning on ran the startup run");

                    var held = (await Visual(new { operation = "run", id = "door", triggerId = "hold" }))["result"]!["runId"]!.GetValue<string>();
                    await Until(() => Runs().Any(r => r["runId"]!.GetValue<string>() == held && r["status"]!.GetValue<string>() == "Running"), "the held run is running");
                    await Visual(new { operation = "set_enabled", id = "door", enabled = false });
                    await Until(() => Runs().Any(r => r["runId"]!.GetValue<string>() == held && r["status"]!.GetValue<string>() != "Running"), "turning off ends the held run");
                    var cancelled = Runs().Single(r => r["runId"]!.GetValue<string>() == held);
                    Check(cancelled["status"]!.GetValue<string>() == "Cancelled" && cancelled["error"]!.GetValue<string>() == "Automation turned off", "turned-off run: " + cancelled.ToJsonString());
                    // The runs started after it are not cancelled by that switch.
                    await Visual(new { operation = "run", id = "door", triggerId = "door" });
                    await Until(() => Count("Manual", "Succeeded") == 2, "Run now after turning off again");
                    Check(!(await Visual(new { operation = "set_enabled", id = "missing", enabled = true }))["ok"]!.GetValue<bool>(), "set_enabled of a missing automation");
                    Check((await Visual(new { operation = "set_enabled", id = "door" }))["error"]!["code"]!.GetValue<string>() == "invalid_request", "set_enabled without enabled");
                }
                finally { await api.UnloadAsync(); }
            });

            await Test("a document without enabled is on; turning it off keeps its revision, source and save time", () =>
            {
                var diagnostics = new DiagnosticsProvider(); diagnostics.GenerateHassEntities(["binary_sensor.motion", "light.hall"]);
                var dir = Path.Combine(folder, "default");
                var store = new VisualStore(dir, new VisualGenerator(diagnostics));
                var saved = store.Save("hall", VisualTests.Graph(), 0);
                Check(saved.Enabled && saved.SavedUtc is { } at && DateTime.UtcNow - at < TimeSpan.FromMinutes(1), "a new document is not on or has no save time");
                var path = Path.Combine(dir, "hall.json");
                var stored = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                stored.Remove("enabled"); stored.Remove("savedUtc");
                File.WriteAllText(path, stored.ToJsonString());
                var old = store.Read("hall")!;
                Check(old.Enabled && old.SavedUtc == null, "a document without the field is not on");
                var off = store.SetEnabled("hall", false);
                var reread = store.Read("hall")!;
                Check(!off.Enabled && !reread.Enabled && reread.Revision == old.Revision && reread.Source == old.Source && reread.SavedUtc == null && reread.Graph.ToJsonString() == old.Graph.ToJsonString(), "turning off changed more than the switch");
                Check(store.RefreshDerived("hall", 1, "fresh", [], "g") && !store.Read("hall")!.Enabled, "a startup refresh turned it on");
                var next = store.Save("hall", VisualTests.Graph(), 1);
                Check(!next.Enabled && next.SavedUtc != null && !store.Read("hall")!.Enabled, "a save turned it on");
                try { store.SetEnabled("missing", true); throw new Exception("missing document toggled"); } catch (FileNotFoundException) { }
                return Task.CompletedTask;
            });

            await Test("home list: outlines without graphs, recent runs for several automations from one store call, newest last, capped at 10; runs today; a not-active revision", async () =>
            {
                var root = Path.Combine(folder, "home"); HassPath.HassConfiguration = root;
                var diagnostics = new DiagnosticsProvider(); diagnostics.GenerateHassEntities(["binary_sensor.motion", "light.hall"]);
                var dir = Path.Combine(root, ".storage", "hass_sharp", "visual");
                var store = new VisualStore(dir, new VisualGenerator(diagnostics));
                foreach (var id in new[] { "alpha", "beta", "gamma", "broken" }) store.Save(id, VisualTests.Graph(), 0);
                var broken = store.Read("broken")!;
                broken.Graph["nodes"]![0]!["config"]!["entity"] = "sensor.removed";
                File.WriteAllText(Path.Combine(dir, "broken.json"), JsonSerializer.Serialize(broken, VisualStore.Json));
                var api = new HassSharpManager(root);
                try
                {
                    await api.Init(["binary_sensor.motion", "light.hall"]);
                    async Task<JsonObject[]> Home(object request) => JsonNode.Parse(await api.VisualRequest(JsonSerializer.Serialize(request)))!["result"]!.AsArray().Select(x => x!.AsObject()).ToArray();
                    var ids = (await Home(new { operation = "list" })).ToDictionary(d => d["id"]!.GetValue<string>(), d => d["automationId"]!.GetValue<string>());
                    var history = History(api);
                    var now = DateTime.UtcNow;
                    InvocationRecord Done(string automation, int hoursAgo, string status) => new()
                    {
                        AutomationId = automation, Revision = "r", Action = "VisualAutomation::Run_74", Cause = "Entity", Status = status,
                        ReceivedUtc = now.AddHours(-hoursAgo), StartUtc = now.AddHours(-hoursAgo), EndUtc = now.AddHours(-hoursAgo), DurationMs = 10 + hoursAgo,
                    };
                    for (var i = 0; i < 12; i++) history.Save(Done(ids["alpha"], i, i == 2 ? "Failed" : i == 4 ? "Interrupted" : "Succeeded"));
                    history.Save(Done(ids["alpha"], 30, "Failed"));
                    var running = new InvocationRecord { AutomationId = ids["beta"], Revision = "r", Action = "VisualAutomation::Run_74", Cause = "Manual", Status = "Running", ReceivedUtc = now, StartUtc = now };
                    history.Save(running);
                    history.Save(Done("deleted-automation", 0, "Failed"));

                    var today = now.AddHours(-5.5);
                    var recent = history.Recent([ids["alpha"], ids["beta"], ids["gamma"]], today, now.AddDays(-1));
                    Check(recent.Count == 2 && recent.ContainsKey(ids["alpha"]) && recent.ContainsKey(ids["beta"]), "one store call did not cover every automation with runs");
                    var alpha = recent[ids["alpha"]];
                    Check(alpha.Runs.Count == 10 && alpha.Runs[^1].DurationMs == 10 && alpha.Runs[0].DurationMs == 19, "not the newest 10, newest last: " + string.Join(",", alpha.Runs.Select(r => r.DurationMs)));
                    Check(alpha.RunsToday == 6 && alpha.FailedLastDay == 2 && alpha.LastRunUtc is { } last && Math.Abs((last - now).TotalSeconds) < 1 && last.Kind == DateTimeKind.Utc, $"counts: today {alpha.RunsToday}, failed {alpha.FailedLastDay}, last {alpha.LastRunUtc:O}");
                    Check(recent[ids["beta"]].Runs.Single() is { Status: "Running", DurationMs: null, StartUtc: not null }, "a run in progress");

                    var items = (await Home(new { operation = "list", view = "home", todayUtc = today.ToString("O") })).ToDictionary(d => d["id"]!.GetValue<string>());
                    foreach (var item in items.Values)
                        Check(!item.ContainsKey("graph") && !item.ContainsKey("source") && item["enabled"]!.GetValue<bool>() && item["savedUtc"] != null, "home item shape: " + item.ToJsonString());
                    var a = items["alpha"];
                    Check(a["recentRuns"]!.AsArray().Count == 10 && a["runsToday"]!.GetValue<int>() == 6 && a["failedLastDay"]!.GetValue<int>() == 2 && a["lastRunUtc"] != null && a["problemCount"] == null && a["currentRevisionActive"]!.GetValue<bool>(), "alpha: " + a.ToJsonString());
                    Check(a["recentRuns"]![9]!.ToJsonString().StartsWith("{\"runId\":") && a["recentRuns"]![9]!["status"]!.GetValue<string>() == "Succeeded" && a["recentRuns"]![9]!["durationMs"]!.GetValue<double>() == 10, "run shape: " + a["recentRuns"]![9]!.ToJsonString());
                    Check(items["beta"]["recentRuns"]![0]!["status"]!.GetValue<string>() == "Running", "beta");
                    var g = items["gamma"];
                    Check(g["recentRuns"]!.AsArray().Count == 0 && g["runsToday"]!.GetValue<int>() == 0 && g["lastRunUtc"] == null, "an automation that never ran: " + g.ToJsonString());
                    var b = items["broken"];
                    Check(!b["currentRevisionActive"]!.GetValue<bool>() && b["problemCount"]!.GetValue<int>() == 1 && b["activationError"]!.GetValue<string>().Contains("Unknown entity"), "not active: " + b.ToJsonString());
                    var outline = a["outline"]!;
                    Check(outline["name"]!.GetValue<string>() == "Motion" && outline["nodes"]!.AsArray().Count == 3 && outline["edges"]!.ToJsonString() == "[[0,1],[1,2]]", "outline: " + outline.ToJsonString());
                    Check(outline["nodes"]![0]!["id"]!.GetValue<string>() == "t" && outline["nodes"]![0]!["config"]!["entity"]!.GetValue<string>() == "binary_sensor.motion"
                        && outline["nodes"]![1]!.AsObject().Select(p => p.Key).Order().SequenceEqual(["kind", "x", "y"]), "outline nodes: " + outline.ToJsonString());
                    var bad = JsonNode.Parse(await api.VisualRequest("""{"operation":"list","view":"home","todayUtc":"not a time"}"""))!;
                    Check(bad["error"]!["code"]!.GetValue<string>() == "invalid_request", "an invalid todayUtc");
                }
                finally { await api.UnloadAsync(); }
            });

            await Test("recent runs read each automation from the runs index", () =>
            {
                var path = Path.Combine(folder, "plan.db");
                _ = new RunHistoryStore(path);
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path); connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "EXPLAIN QUERY PLAN " + RunHistoryStore.RecentSql;
                foreach (var (name, value) in new (string, object)[] { ("$ids", "[\"a\",\"b\"]"), ("$today", DateTime.UtcNow), ("$failed", DateTime.UtcNow), ("$limit", 10) }) command.Parameters.AddWithValue(name, value);
                using var reader = command.ExecuteReader(); var plan = "";
                while (reader.Read()) plan += reader.GetString(3) + "; ";
                Check(plan.Contains("IX_Runs_AutomationId_ReceivedUtc_RunId") && !plan.Contains("SCAN Runs"), "recent runs scan the table: " + plan);
                return Task.CompletedTask;
            });

            await Test("an outline is a fraction of the graph it is drawn from", () =>
            {
                // A realistic 30-node graph: services with a target and fields, conditions, delays and logs, with labels.
                var nodes = new JsonArray(); var edges = new JsonArray();
                string Id(int i) => $"{Guid.NewGuid()}";
                var ids = Enumerable.Range(0, 30).Select(Id).ToArray();
                JsonObject Lit(string type, JsonNode value) => new() { ["kind"] = "literal", ["type"] = type, ["value"] = value };
                for (var i = 0; i < 30; i++)
                {
                    JsonObject config = i == 0 ? new() { ["type"] = "entity", ["entity"] = "binary_sensor.hallway_motion", ["mode"] = "Restart", ["manual"] = true }
                        : (i % 4) switch
                        {
                            0 => new() { ["entity"] = Lit("entity_id", "binary_sensor.hallway_motion"), ["state"] = "on" },
                            1 => new() { ["service"] = "light.turn_on", ["target"] = Lit("target", new JsonObject { ["entity_id"] = new JsonArray("light.hallway_ceiling", "light.hallway_lamp") }), ["fields"] = new JsonObject { ["brightness_pct"] = Lit("number", 80), ["transition"] = Lit("number", 2) } },
                            2 => new() { ["seconds"] = Lit("number", 300) },
                            _ => new() { ["level"] = "Info", ["value"] = Lit("string", "Hallway lights changed by the motion automation") },
                        };
                    var kind = i == 0 ? "trigger" : (i % 4) switch { 0 => "condition", 1 => "service", 2 => "delay", _ => "log" };
                    nodes.Add(new JsonObject { ["id"] = ids[i], ["kind"] = kind, ["label"] = $"Step {i} of the hallway", ["config"] = config, ["position"] = new JsonObject { ["x"] = 40 + i * 400.5, ["y"] = 80 + i % 3 * 240 } });
                    if (i > 0) edges.Add(new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["source"] = ids[i - 1], ["target"] = ids[i], ["port"] = i % 4 == 1 ? "true" : "next" });
                }
                for (var i = 0; i < 6; i++) edges.Add(new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["kind"] = "data", ["source"] = ids[0], ["sourcePort"] = "entity_id", ["target"] = ids[4 * i + 4], ["targetPort"] = "entity" });
                var graph = new JsonObject { ["version"] = 5, ["name"] = "Hallway motion lights", ["nodes"] = nodes, ["edges"] = edges };
                var outline = HassSharpManager.Outline(graph);
                int graphBytes = graph.ToJsonString().Length, outlineBytes = outline.ToJsonString().Length;
                Check(outline["nodes"]!.AsArray().Count == 30 && outline["edges"]!.AsArray().Count == 29, "outline lost nodes or kept data edges");
                Check(outlineBytes * 3 < graphBytes, $"outline {outlineBytes} B is not much smaller than the graph {graphBytes} B");
                Console.WriteLine($"MEASURE automation home: a 30-node graph is {graphBytes} B, its outline {outlineBytes} B; 100 such automations: {graphBytes * 100 / 1024} KiB of graphs, {outlineBytes * 100 / 1024} KiB of outlines");
                return Task.CompletedTask;
            });
        }
        finally
        {
            PyInterop.RegisterSchedule = register;
            HassPath.HassConfiguration = previous;
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
        if (failures.Count != 0) throw new Exception(string.Join("\n", failures));
    }
}
