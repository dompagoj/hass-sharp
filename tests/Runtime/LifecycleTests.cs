using HassSharp;
using System.Text.Json;
using System.Text.Json.Nodes;
using static VisualExecutionTests;

/// <summary>
/// The edges of an automation's life: Run now while it stops or after it is gone is refused plainly; an entity change
/// while Home Assistant stops starts no run; a cleanup failure of a replaced revision leaves the new one running,
/// started and reported active, and one of a deleted automation does not fail the delete.
/// </summary>
static class LifecycleTests
{
    [RuntimeSuite("--lifecycle", Order = 36)]
    public static async Task Run()
    {
        var previous = HassPath.HassConfiguration;
        var root = Path.Combine(Path.GetTempPath(), "lifecycle-" + Guid.NewGuid());
        var log = PyInterop.Log; var entity = PyInterop.Entity; var subscribe = PyInterop.SubscribeToEntityTracking;
        var register = PyInterop.BeginRegisterHaTriggers; var remove = PyInterop.BeginRemoveHaTriggers;
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        PyInterop.Log = (_, message) => messages.Enqueue(message);
        PyInterop.Entity = id => new() { EntityId = id, State = "on" };
        PyInterop.SubscribeToEntityTracking = _ => { };
        var leases = 0;
        PyInterop.BeginRegisterHaTriggers = (_, _, _, success, _) => success("lease-" + Interlocked.Increment(ref leases));
        PyInterop.BeginRemoveHaTriggers = (_, done, _) => done();
        var manager = new HassSharpManager(root);
        try
        {
            HassPath.HassConfiguration = root;
            await manager.Init();
            async Task<JsonNode> Visual(object request) => JsonNode.Parse(await manager.VisualRequest(JsonSerializer.Serialize(request)))!;
            JsonObject Graph(string trigger, string said)
            {
                var graph = G(Code("say", $"Logger.Info(\"{said}\");"));
                graph["nodes"]![0]!["config"] = trigger == "ha"
                    ? new JsonObject { ["type"] = "ha", ["startup"] = true, ["trigger"] = new JsonObject { ["trigger"] = "state", ["entity_id"] = "light.hall" } }
                    : new JsonObject { ["type"] = "entity", ["entity"] = "binary_sensor.door" };
                return Derived(graph);
            }
            async Task Until(Func<bool> done, string what)
            {
                for (var started = DateTime.UtcNow; !done(); await Task.Delay(10))
                    Check(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), what);
            }

            // The saved libraries compile once per library generation and typed service calls, not on every request.
            object Compiled() { var saved = typeof(HassSharpManager).GetMethod("SavedLibraries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(manager, null)!; return saved.GetType().GetField("Item3")!.GetValue(saved)!; }
            var once = Compiled();
            await Visual(new { operation = "list" });
            Check(ReferenceEquals(once, Compiled()), "the saved libraries were compiled again for an unchanged generation");
            var library = JsonNode.Parse(await manager.NodeLibraryRequest(JsonSerializer.Serialize(new { operation = "save", id = "extra", name = "Extra.cs", expectedRevision = 0, source = "public static class Extra { [Node(\"Echo\")] public static string Echo(string text) => text; }" })))!;
            Check(library["ok"]?.GetValue<bool>() == true, "library save: " + library.ToJsonString());
            Check(!ReferenceEquals(once, Compiled()), "a library save did not compile the libraries again");

            // A cleanup failure of the replaced revision (its Home Assistant trigger cannot be removed): the save answers
            // active, the new revision has started, Run now works; a delete whose cleanup fails still succeeds.
            var first = await Visual(new { operation = "save", id = "native", graph = Graph("ha", "first"), expectedRevision = 0 });
            Check(first["result"]?["active"]?.GetValue<bool>() == true, "the native automation is active: " + first.ToJsonString());
            await Until(() => messages.Contains("first"), "the first revision's startup run");
            PyInterop.BeginRemoveHaTriggers = (_, _, error) => error("remove failed");
            var second = await Visual(new { operation = "save", id = "native", graph = Graph("ha", "second"), expectedRevision = 1 });
            Check(second["ok"]?.GetValue<bool>() == true && second["result"]?["active"]?.GetValue<bool>() == true && second["result"]?["activationError"] == null,
                "a cleanup failure of the replaced revision made the save fail or report the new one inactive: " + second.ToJsonString());
            await Until(() => messages.Contains("second"), "the replacement's startup run did not happen");
            var now = await Visual(new { operation = "run", id = "native", triggerId = "t" });
            Check(now["ok"]?.GetValue<bool>() == true, "Run now of the replacement: " + now.ToJsonString());
            Check(messages.Any(m => m.Contains("cleaning up the replaced revision failed") && m.Contains("remove failed")), "the cleanup failure was not logged");
            var deleted = await Visual(new { operation = "delete", id = "native", expectedRevision = 2 });
            Check(deleted["ok"]?.GetValue<bool>() == true, "a delete whose cleanup failed: " + deleted.ToJsonString());
            Check(messages.Any(m => m.Contains("cleaning up the deleted automation failed")), "the delete's cleanup failure was not logged");
            PyInterop.BeginRemoveHaTriggers = (_, done, _) => done();

            // While Home Assistant stops, the runs are cancelled: an entity change starts none, and Run now is refused.
            var door = await Visual(new { operation = "save", id = "door", graph = Graph("entity", "door"), expectedRevision = 0 });
            var automation = door["result"]!["document"]!["automationId"]!.GetValue<string>();
            var scripts = (UserScriptManager)typeof(HassSharpManager).GetField("_userScriptManager", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(manager)!;
            int Runs() => JsonNode.Parse(manager.GetRunHistory(automation, 0, 20, null))!["runs"]!.AsArray().Count;
            manager.CancelRunsForShutdown();
            await manager.OnTrackedEntityChange("binary_sensor.door", new() { EntityId = "binary_sensor.door", State = "on" }, new() { EntityId = "binary_sensor.door", State = "off" });
            Check(Runs() == 0 && !messages.Contains("door"), "an entity change while stopping recorded or ran a run");
            void Refused(string when)
            {
                try { scripts.DispatchManual(automation, "VisualAutomation", VisualNodeCatalog.EffectiveTriggerMethod(Graph("entity", "door")["nodes"]![0]!.AsObject())); }
                catch (InvalidOperationException error) { Check(error.Message == UserScriptManager.NotRunning, $"Run now {when}: " + error.Message); return; }
                throw new Exception($"Run now {when} was not refused");
            }
            Refused("while stopping");
            await manager.UnloadAsync();
            Refused("after unload");
            Check(Runs() == 0, "Run now while stopping or after unload recorded a run");
            Console.WriteLine("PASS lifecycle: the saved libraries compile once per generation, Run now while stopping or after unload is refused, an entity change while stopping starts no run, a replaced revision's cleanup failure leaves the new one started and active, a deleted automation's cleanup failure does not fail the delete");
        }
        finally
        {
            await manager.UnloadAsync();
            PyInterop.Log = log; PyInterop.Entity = entity; PyInterop.SubscribeToEntityTracking = subscribe;
            PyInterop.BeginRegisterHaTriggers = register; PyInterop.BeginRemoveHaTriggers = remove;
            HassPath.HassConfiguration = previous;
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
