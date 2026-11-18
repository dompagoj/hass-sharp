using HassSharp;
using System.Collections.Concurrent;
using System.Text.Json;

/// <summary>
/// A caller can wait for a run to end and learn how it ended (<see cref="UserScriptClass.RunToEnd"/>): its status and
/// error as its run record carries them, whether history is on for it or not. A failed, skipped or cancelled run is an
/// outcome, never an exception; Run/RunMethod callers still see none of it. Command and setup runs (the entity host's)
/// are new causes, stored and summarised by name. A Restart run cancels the older one before its own method starts.
/// </summary>
static class RunOutcomeTests
{
    const string BodyKey = "hass-sharp.run-outcome.body";
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // Every action hands its body to the test (Body), so the test decides how each run goes. Quiet, QuietOnce and Latest have
    // automatic history off, as an entity graph's state roots do; none but Manual can be run manually.
    const string Source = """
        public class Outcomes : Automation
        {
            Task Body(string name) => ((Func<string, Automation, Task>)AppDomain.CurrentDomain.GetData("hass-sharp.run-outcome.body")!)(name, this);
            public override AutomationConfiguration Configure() => new(
                new AutomationAction(nameof(Flow), AutomationMode.Parallel, Startup: false, Manual: false),
                new AutomationAction(nameof(Quiet), AutomationMode.Parallel, Startup: false, Manual: false),
                new AutomationAction(nameof(Once), AutomationMode.Single, Startup: false, Manual: false),
                new AutomationAction(nameof(QuietOnce), AutomationMode.Single, Startup: false, Manual: false),
                new AutomationAction(nameof(Queue), AutomationMode.Queued, Startup: false, Manual: false),
                new AutomationAction(nameof(Latest), AutomationMode.Restart, Startup: false, Manual: false),
                new AutomationAction(nameof(Manual), AutomationMode.Parallel));
            public Task Flow() => Body(nameof(Flow));
            public Task Quiet() => Body(nameof(Quiet));
            public Task Once() => Body(nameof(Once));
            public Task QuietOnce() => Body(nameof(QuietOnce));
            public Task Queue() => Body(nameof(Queue));
            public Task Latest() => Body(nameof(Latest));
            public Task Manual() => Body(nameof(Manual));
        }
        """;

    static Func<string, Automation, Task> Body { set => AppDomain.CurrentDomain.SetData(BodyKey, value); }

    sealed record Home(UserScriptManager Manager, RunHistoryStore Store, string StorePath, UserScript Script, UserScriptClass Class)
    {
        public InvocationRecord[] Rows() => Store.Read(Script.AutomationId, 0, 100).ToArray();
        public InvocationRecord[] Rows(string method) => Rows().Where(r => r.Action.EndsWith("::" + method, StringComparison.Ordinal)).ToArray();
    }

    static async Task<Home> Load(CodeCompiler compiler, string folder)
    {
        var compiled = await compiler.CompileSingleFile(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".cs"), Source);
        compiled.VisualTriggers = new Dictionary<string, VisualTrigger>
        {
            ["Quiet"] = new("quiet", null, TrackHistory: false), ["QuietOnce"] = new("quiet-once", null, TrackHistory: false),
            ["Latest"] = new("latest", null, TrackHistory: false),
        };
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".db");
        var store = new RunHistoryStore(path);
        var manager = new UserScriptManager { History = store };
        await manager.LoadUserScripts([compiled], (path, error) => throw new Exception("cannot load " + path, error));
        var script = manager.GetUserScripts().Single();
        return new(manager, store, path, script, script.Classes.Single());
    }

    static async Task Until(Func<bool> done, string what)
    {
        for (var started = DateTime.UtcNow; !done(); await Task.Delay(5))
            Check(DateTime.UtcNow - started < Wait, what);
    }

    static JsonElement Keyed(string key) => JsonSerializer.SerializeToElement(new { platform = "entity", entity = key });

    /// <summary>The task, or a failure that names what it waited for when it does not end within <see cref="Wait"/>.</summary>
    static async Task Within(Task task, string what)
    {
        try { await task.WaitAsync(Wait); }
        catch (TimeoutException) { throw new TimeoutException($"Timed out after {Wait.TotalSeconds:0} s waiting for {what}"); }
    }

    static async Task<T> Within<T>(Task<T> task, string what) { await Within((Task)task, what); return await task; }

    static string Show(RunOutcome outcome) => $"({outcome.Status}, {outcome.Error ?? "null"})";

    [RuntimeSuite("--run-outcome", Order = 37)]
    public static async Task Run()
    {
        var previous = HassPath.HassConfiguration;
        var folder = Path.Combine(Path.GetTempPath(), "run-outcome-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        var log = PyInterop.Log;
        var messages = new ConcurrentQueue<string>();
        PyInterop.Log = (_, message) => messages.Enqueue(message);
        HassPath.HassConfiguration = folder;
        var compiler = new CodeCompiler(new DiagnosticsProvider());
        var homes = new List<Home>();
        async Task<Home> Fresh() { var home = await Load(compiler, folder); homes.Add(home); return home; }
        try
        {
            var home = await Fresh();
            await RunToEndReportsSuccess(home);
            await RunToEndReportsTheErrorOfAFailedRun(home, messages);
            await RunToEndReportsASkippedSingleRun(home, "Once");
            await RunToEndReportsASkippedSingleRun(home, "QuietOnce");
            await RunToEndReportsAStoppedRun(home);
            await QueuedRunsFinishInOrder(home);
            await ExistingCallersStillDoNotSeeFailures(home, messages);
            await HistoryRecordsCarryTheNewCauses(home);
            await MisuseThrows(home);
            await RestartCancelsTheOlderRunBeforeTheNewerStarts(home);
            await RunToEndReportsCancellation(await Fresh());
            await ARunCancelledByItsOwnTokenEndsAlone(await Fresh());
            await StoppingResumesTheCallerOffTheCancellingThread(await Fresh());
        }
        finally
        {
            foreach (var home in homes) await home.Manager.UnloadUserScriptsAsync();
            AppDomain.CurrentDomain.SetData(BodyKey, null);
            PyInterop.Log = log;
            HassPath.HassConfiguration = previous;
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    static readonly JsonElement Data = JsonSerializer.SerializeToElement(new { brightness = 3 });

    static async Task RunToEndReportsSuccess(Home home)
    {
        var seen = new ConcurrentQueue<(string Name, InvocationCause Cause, string Payload)>();
        Body = (name, automation) => { seen.Enqueue((name, automation.Cause, automation.TriggerData.GetRawText())); return Task.CompletedTask; };
        var command = TriggerPayload.Command("power", "turn_on", Data);
        // Command and setup actions are declared Manual: false; they still run for their own causes.
        foreach (var (method, cause, payload) in new[] { ("Flow", InvocationCause.Command, command), ("Quiet", InvocationCause.Command, command), ("Flow", InvocationCause.Setup, TriggerPayload.Setup()), ("Quiet", InvocationCause.Setup, TriggerPayload.Setup()) })
        {
            var outcome = await Within(home.Class.RunToEnd(method, cause, payload), $"the {method} {cause} run to end");
            Check(outcome == new RunOutcome("Succeeded", null), $"{method} {cause}: {Show(outcome)}");
        }
        var runs = seen.ToArray();
        Check(runs.Select(r => r.Name).SequenceEqual(["Flow", "Quiet", "Flow", "Quiet"]), "the bodies that ran: " + string.Join(",", runs.Select(r => r.Name)));
        Check(runs.Take(2).All(r => r.Cause == InvocationCause.Command && r.Payload == command.GetRawText())
            && runs.Skip(2).All(r => r.Cause == InvocationCause.Setup && r.Payload == """{"platform":"setup"}"""), "the runs' causes and trigger data: " + string.Join("; ", runs));
        Check(command.GetRawText() == """{"platform":"command","entity":"power","command":"turn_on","data":{"brightness":3}}""", "command payload: " + command.GetRawText());
        Check(TriggerPayload.Command("power", "turn_off", null).GetRawText() == """{"platform":"command","entity":"power","command":"turn_off"}"""
            && TriggerPayload.Command("power", "turn_off", default(JsonElement)).GetRawText() == """{"platform":"command","entity":"power","command":"turn_off"}"""
            && TriggerPayload.Command("power", "turn_off", JsonSerializer.SerializeToElement<object?>(null)).GetRawText() == """{"platform":"command","entity":"power","command":"turn_off"}""", "a command without data: " + TriggerPayload.Command("power", "turn_off", null).GetRawText());
        Check(home.Rows("Flow").Length == 2 && home.Rows("Quiet").Length == 0, "history: Flow " + home.Rows("Flow").Length + ", Quiet " + home.Rows("Quiet").Length);
        Console.WriteLine("PASS RunToEndReportsSuccess: a command or setup run of a Manual: false action, history on or off, reports (Succeeded, null) and carries its cause and payload");
    }

    static async Task RunToEndReportsTheErrorOfAFailedRun(Home home, ConcurrentQueue<string> messages)
    {
        Body = async (_, _) => { await Task.Yield(); throw new InvalidOperationException("boom"); };
        var logged = messages.Count(m => m.Contains("failed: System.InvalidOperationException: boom"));
        foreach (var method in new[] { "Flow", "Quiet" })
        {
            var outcome = await Within(home.Class.RunToEnd(method, InvocationCause.Command, TriggerPayload.Command("power", "turn_on", null)), $"the failing {method} run to end");
            Check(outcome == new RunOutcome("Failed", "boom"), $"{method}: {Show(outcome)}");
        }
        // Synchronously, before the method returns a task, too.
        Body = (_, _) => throw new InvalidOperationException("at once");
        var direct = await Within(home.Class.RunToEnd("Quiet", InvocationCause.Setup, TriggerPayload.Setup()), "the synchronously failing run to end");
        Check(direct == new RunOutcome("Failed", "at once"), "a synchronous failure: " + Show(direct));
        Check(messages.Count(m => m.Contains("failed: System.InvalidOperationException: boom")) == logged + 2, "a failed run is logged as before, history on or off");
        var failed = home.Rows("Flow").First();
        Check(failed.Status == "Failed" && failed.Error == "boom", $"the record: {failed.Status} {failed.Error}");
        Check(home.Rows("Quiet").Length == 0, "a run with history off wrote a record");
        Console.WriteLine("PASS RunToEndReportsTheErrorOfAFailedRun: a failure is (Failed, its message), as its record carries it and logged as before, history on or off");
    }

    static async Task RunToEndReportsASkippedSingleRun(Home home, string method)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Body = async (_, _) => { entered.TrySetResult(); await release.Task; };
        var first = home.Class.RunToEnd(method, InvocationCause.Command, TriggerPayload.Command("power", "turn_on", null));
        await Within(entered.Task, "the first Single run to enter its method");
        var skipped = await Within(home.Class.RunToEnd(method, InvocationCause.Command, TriggerPayload.Command("power", "turn_off", null)), "the colliding Single run to be skipped");
        Check(skipped == new RunOutcome("Skipped", null), "the second Single run: " + Show(skipped));
        Check(!first.IsCompleted, "the running Single run ended when the second was skipped");
        release.SetResult();
        var ran = await Within(first, "the first Single run to end");
        Check(ran == new RunOutcome("Succeeded", null), "the first Single run: " + Show(ran));
        string[] expected = method == "QuietOnce" ? [] : ["Skipped", "Succeeded"];
        Check(home.Rows(method).Select(r => r.Status).Order().SequenceEqual(expected), method + " records: " + string.Join(",", home.Rows(method).Select(r => r.Status)));
        Console.WriteLine($"PASS RunToEndReportsASkippedSingleRun ({method}, history {(method == "QuietOnce" ? "off" : "on")}): a Single run that collides is (Skipped, null) at once; the one running reports its own end");
    }

    static async Task RunToEndReportsAStoppedRun(Home home)
    {
        // What a stop node does: the run ends early and is recorded as Stopped.
        Body = (_, _) => { InvocationContext.Current!.Stopped = true; return Task.CompletedTask; };
        foreach (var method in new[] { "Flow", "Quiet" })
        {
            var outcome = await Within(home.Class.RunToEnd(method, InvocationCause.Command, TriggerPayload.Command("power", "turn_on", null)), $"the stopping {method} run to end");
            Check(outcome == new RunOutcome("Stopped", null), $"{method}: {Show(outcome)}");
        }
        Check(home.Rows("Flow").First().Status == "Stopped", "the stopped run's record: " + home.Rows("Flow").First().Status);
        Console.WriteLine("PASS RunToEndReportsAStoppedRun: a run that stops itself is (Stopped, null), history on or off");
    }

    static async Task QueuedRunsFinishInOrder(Home home)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var steps = new ConcurrentQueue<string>();
        Body = async (_, automation) =>
        {
            var entity = automation.TriggerData.GetProperty("entity").GetString()!;
            steps.Enqueue("start " + entity);
            entered.TrySetResult();
            await release.Task;
            await Task.Delay(5);
            steps.Enqueue("end " + entity);
        };
        var keys = Enumerable.Range(0, 5).Select(i => "k" + i).ToArray();
        var first = home.Class.RunToEnd("Queue", InvocationCause.Command, TriggerPayload.Command(keys[0], "turn_on", null));
        await Within(entered.Task, "the head of the queue to enter its method");
        var rest = keys.Skip(1).Select(key => home.Class.RunToEnd("Queue", InvocationCause.Command, TriggerPayload.Command(key, "turn_on", null))).ToArray();
        await Task.Delay(50);
        Check(!first.IsCompleted && rest.All(r => !r.IsCompleted), "a queued run ended before the one before it");
        release.SetResult();
        var outcomes = await Within(Task.WhenAll(rest.Prepend(first)), "the queued runs to end");
        Check(outcomes.All(o => o == new RunOutcome("Succeeded", null)), "outcomes: " + string.Join(",", outcomes.Select(Show)));
        Check(steps.SequenceEqual(keys.SelectMany(key => new[] { "start " + key, "end " + key })), "queued runs overlapped or ran out of order: " + string.Join(",", steps));
        Console.WriteLine("PASS QueuedRunsFinishInOrder: queued runs each report their end, run one after another in arrival order");
    }

    static async Task ExistingCallersStillDoNotSeeFailures(Home home, ConcurrentQueue<string> messages)
    {
        Body = async (_, _) => { await Task.Yield(); throw new InvalidOperationException("boom"); };
        var logged = messages.Count(m => m.Contains("failed: System.InvalidOperationException: boom"));
        await Within(home.Class.RunMethod("Manual"), "RunMethod of Manual to return");
        await Within(home.Class.RunMethod("Flow", InvocationCause.Command, payload: TriggerPayload.Command("power", "turn_on", null)), "RunMethod of a command to return");
        await Within(home.Class.RunMethod("Quiet", InvocationCause.Setup, payload: TriggerPayload.Setup()), "RunMethod of a setup to return");
        Check(messages.Count(m => m.Contains("failed: System.InvalidOperationException: boom")) == logged + 3, "RunMethod's failures are not logged as before");
        var manual = home.Rows("Manual").Single();
        Check(manual is { Status: "Failed", Error: "boom", Cause: "Manual" }, $"the manual run's record: {manual.Status} {manual.Error} {manual.Cause}");
        // The refusal of a manual run of a Manual: false action is unchanged.
        try { _ = home.Class.RunMethod("Flow"); throw new Exception("RunMethod ran a Manual: false action manually"); }
        catch (InvalidOperationException error) { Check(error.Message == "Manual invocation is disabled", error.Message); }
        Console.WriteLine("PASS ExistingCallersStillDoNotSeeFailures: RunMethod still completes without the run's failure, which is logged and recorded");
    }

    static async Task HistoryRecordsCarryTheNewCauses(Home home)
    {
        Check(Enum.GetNames<InvocationCause>().SequenceEqual(["Startup", "Entity", "Manual", "Schedule", "HomeAssistantTrigger", "Command", "Setup", "Frame"]), "the causes: " + string.Join(",", Enum.GetNames<InvocationCause>()));
        Check((int)InvocationCause.Command == 5 && (int)InvocationCause.Setup == 6 && (int)InvocationCause.Frame == 7, "the new causes were not appended");
        Body = (_, _) => Task.CompletedTask;
        var before = home.Rows("Flow").Length;
        await Within(home.Class.RunToEnd("Flow", InvocationCause.Command, TriggerPayload.Command("power", "turn_on", Data)), "the command run to end");
        // RunToEnd completes after the run's last history write: the record is final as soon as it returns.
        var command = home.Rows("Flow").First();
        await Within(home.Class.RunToEnd("Flow", InvocationCause.Setup, TriggerPayload.Setup()), "the setup run to end");
        var setup = home.Rows("Flow").First();
        await Within(home.Class.RunToEnd("Flow", InvocationCause.Frame, TriggerPayload.Frame("front")), "the frame run to end");
        var frame = home.Rows("Flow").First();
        Check(home.Rows("Flow").Length == before + 3, "records written: " + (home.Rows("Flow").Length - before));
        Check(frame is { Cause: "Frame", Status: "Succeeded" } && frame.Trigger == """{"kind":"frame","entity":"front"}""", $"the frame record: {frame.Cause} {frame.Status} {frame.Trigger}");
        Check(command is { Cause: "Command", Status: "Succeeded", EndUtc: not null }, $"the command record: {command.Cause} {command.Status} {command.EndUtc}");
        Check(command.Trigger == """{"kind":"command","entity":"power","command":"turn_on"}""", "the command record's trigger: " + command.Trigger);
        Check(setup is { Cause: "Setup", Status: "Succeeded", EndUtc: not null } && setup.Trigger == """{"kind":"setup"}""", $"the setup record: {setup.Cause} {setup.Status} {setup.Trigger}");
        // Read back through a store opened again, as after a restart.
        var reopened = new RunHistoryStore(home.StorePath).Read(home.Script.AutomationId, 0, 2).Select(r => r.Cause).ToArray();
        Check(reopened.SequenceEqual(["Frame", "Setup"]), "causes read back: " + string.Join(",", reopened));
        Check(TriggerSummary.KindOnly(InvocationCause.Command) == """{"kind":"command"}""" && TriggerSummary.KindOnly(InvocationCause.Setup) == """{"kind":"setup"}""", "kind-only summaries");
        Check(TriggerSummary.Summarize(InvocationCause.Command, JsonSerializer.SerializeToElement(new { platform = "command" })) == """{"kind":"command"}""", "a command payload without its fields");
        Check(TriggerSummary.KindOnly(InvocationCause.Frame) == """{"kind":"frame"}""", "the frame kind-only summary");
        Console.WriteLine("PASS HistoryRecordsCarryTheNewCauses: Command, Setup and Frame are appended, stored and read back by name, with their trigger summary, final when RunToEnd returns");
    }

    static async Task MisuseThrows(Home home)
    {
        try { await home.Class.RunToEnd("Nope", InvocationCause.Command, TriggerPayload.Setup()); throw new Exception("an unknown method ran"); }
        catch (ArgumentException error) { Check(error.Message.Contains("Nope"), error.Message); }
        try { await home.Class.RunToEnd("Flow", InvocationCause.Manual, TriggerPayload.Manual()); throw new Exception("a Manual: false action ran manually"); }
        catch (InvalidOperationException error) { Check(error.Message == "Manual invocation is disabled", error.Message); }
        Console.WriteLine("PASS MisuseThrows: RunToEnd throws for a method the class does not declare, and refuses a manual run of a Manual: false action");
    }

    /// <summary>
    /// History off, as a state root: the older run is blocked inside its method on a signal that ignores cancellation.
    /// A newer run being accepted cancels it at once, and the newer method starts only after the older run has ended,
    /// so the older one (checking its token right before it publishes) can never publish after the newer one.
    /// </summary>
    static async Task RestartCancelsTheOlderRunBeforeTheNewerStarts(Home home)
    {
        var tokens = new ConcurrentDictionary<string, CancellationToken>();
        var entered = new ConcurrentDictionary<string, TaskCompletionSource>();
        var olderCancelledAtNewerEntry = new ConcurrentDictionary<string, bool>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var steps = new ConcurrentQueue<string>();
        TaskCompletionSource Entered(string key) => entered.GetOrAdd(key, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        Body = async (_, automation) =>
        {
            var key = automation.TriggerData.GetProperty("entity").GetString()!;
            tokens[key] = automation.CancellationToken;
            olderCancelledAtNewerEntry[key] = tokens.TryGetValue("a", out var older) && older.IsCancellationRequested;
            steps.Enqueue("start " + key);
            Entered(key).TrySetResult();
            if (key == "a") await release.Task;
            // The check a state root makes right before it publishes.
            automation.CancellationToken.ThrowIfCancellationRequested();
            steps.Enqueue("publish " + key);
        };
        var rowsBefore = home.Rows().Length;
        var a = home.Class.RunToEnd("Latest", InvocationCause.Entity, Keyed("a"));
        await Within(Entered("a").Task, "the older Restart run to enter its method");
        var b = home.Class.RunToEnd("Latest", InvocationCause.Entity, Keyed("b"));
        await Until(() => tokens["a"].IsCancellationRequested, "accepting a newer Restart run did not cancel the older one");
        var c = home.Class.RunToEnd("Latest", InvocationCause.Entity, Keyed("c"));
        await Task.Delay(100);
        Check(!Entered("b").Task.IsCompleted && !Entered("c").Task.IsCompleted, "a newer Restart run's method started while the older one was still in its method");
        release.SetResult();
        var outcomes = await Within(Task.WhenAll(a, b, c), "the three Restart runs to end");
        Check(outcomes[0] == new RunOutcome("Cancelled", CancelReasons.Restarted), "the older run: " + Show(outcomes[0]));
        Check(outcomes[1] == new RunOutcome("Cancelled", CancelReasons.Restarted), "the run between: " + Show(outcomes[1]));
        Check(outcomes[2] == new RunOutcome("Succeeded", null), "the newest run: " + Show(outcomes[2]));
        Check(olderCancelledAtNewerEntry.GetValueOrDefault("c"), "the older run's token was not cancelled when the newer method started");
        Check(steps.SequenceEqual(["start a", "start c", "publish c"]), "steps: " + string.Join(",", steps));
        Check(home.Rows().Length == rowsBefore, "a Restart run with history off wrote a record");
        Console.WriteLine("PASS RestartCancelsTheOlderRunBeforeTheNewerStarts: with history off, accepting a newer Restart run cancels the older run before the newer method starts (it starts once the older has ended); the older reports (Cancelled, Restarted by a newer trigger), the newest (Succeeded, null)");
    }

    /// <summary>Unloading while a run is going or queued behind another: every RunToEnd still completes, cancelled.</summary>
    static async Task RunToEndReportsCancellation(Home home)
    {
        var entered = new ConcurrentDictionary<string, TaskCompletionSource>();
        TaskCompletionSource Entered(string key) => entered.GetOrAdd(key, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        var bodies = new ConcurrentQueue<string>();
        Body = async (name, automation) =>
        {
            var key = name + " " + automation.TriggerData.GetProperty("entity").GetString();
            bodies.Enqueue(key);
            Entered(key).TrySetResult();
            await Task.Delay(Timeout.Infinite, automation.CancellationToken);
        };
        Task<RunOutcome> Start(string method, string key) => home.Class.RunToEnd(method, InvocationCause.Command, TriggerPayload.Command(key, "turn_on", null));
        var running = Start("Flow", "a");
        var quiet = Start("Quiet", "a");
        var head = Start("Queue", "a");
        await Within(Task.WhenAll(Entered("Flow a").Task, Entered("Quiet a").Task, Entered("Queue a").Task), "the runs to enter their methods");
        var queued = Start("Queue", "b");
        await Task.Delay(50);
        Check(!queued.IsCompleted && !Entered("Queue b").Task.IsCompleted, "the queued run did not wait");
        await Within(home.Manager.UnloadUserScriptsAsync(CancelReasons.Reloaded), "the unload to finish");
        var outcomes = await Within(Task.WhenAll(running, quiet, head, queued), "the cancelled runs to end");
        Check(outcomes.All(o => o == new RunOutcome("Cancelled", CancelReasons.Reloaded)), "outcomes: " + string.Join(",", outcomes.Select(Show)));
        Check(!bodies.Contains("Queue b"), "the queued run's method ran after the unload");
        // A run asked for once the script is gone completes too.
        var stale = await Within(Start("Flow", "late"), "the run asked for after the unload to end");
        Check(stale == new RunOutcome("Cancelled", CancelReasons.Reloaded), "after unload: " + Show(stale));
        Console.WriteLine("PASS RunToEndReportsCancellation: unloading completes every RunToEnd with (Cancelled, its reason): running, history off, queued behind another, and asked for after the unload");
    }

    /// <summary>
    /// A run given a token of its own (as a camera's frame flow is, for its time limit) ends Cancelled with the reason
    /// given when that token is cancelled, its record saying so; the other runs, also of the same method, go on, and a
    /// later run is not affected. A run given no token is never cancelled by one.
    /// </summary>
    static async Task ARunCancelledByItsOwnTokenEndsAlone(Home home)
    {
        var entered = new ConcurrentDictionary<string, TaskCompletionSource>();
        TaskCompletionSource Entered(string key) => entered.GetOrAdd(key, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Body = async (name, automation) =>
        {
            var key = name + " " + automation.TriggerData.GetProperty("entity").GetString();
            Entered(key).TrySetResult();
            await release.Task.WaitAsync(automation.CancellationToken);
        };
        using var limit = new CancellationTokenSource();
        var timed = home.Class.RunToEnd("Flow", InvocationCause.Frame, TriggerPayload.Frame("a"), limit.Token, "Took longer than 0.2 s");
        var other = home.Class.RunToEnd("Flow", InvocationCause.Frame, TriggerPayload.Frame("b"));
        await Within(Task.WhenAll(Entered("Flow a").Task, Entered("Flow b").Task), "both runs to enter their method");
        limit.Cancel();
        var outcome = await Within(timed, "the run cancelled by its token to end");
        Check(outcome == new RunOutcome("Cancelled", "Took longer than 0.2 s"), "the cancelled run: " + Show(outcome));
        var record = home.Rows("Flow").Single(r => r.Trigger == """{"kind":"frame","entity":"a"}""");
        Check(record is { Status: "Cancelled", Error: "Took longer than 0.2 s", Cause: "Frame" }, $"its record: {record.Status} {record.Error}");
        await Task.Delay(50);
        Check(!other.IsCompleted, "the other run of the method was cancelled too");
        release.TrySetResult();
        Check(await Within(other, "the other run to end") == new RunOutcome("Succeeded", null), "the other run did not succeed");
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = await Within(home.Class.RunToEnd("Flow", InvocationCause.Frame, TriggerPayload.Frame("c"), limit.Token, "late"), "a run given a cancelled token to end");
        Check(late == new RunOutcome("Cancelled", "late"), "a run given a token already cancelled: " + Show(late));
        Console.WriteLine("PASS ARunCancelledByItsOwnTokenEndsAlone: a run's own token cancels it alone, with the reason given, recorded; the other runs go on");
    }

    /// <summary>
    /// Home Assistant stopping (CancelRuns, called from Python) cancels runs synchronously, and a run blocked on its token
    /// ends inline on that thread. The RunToEnd caller must not resume there: it would call into Python re-entrantly
    /// inside the stop.
    /// </summary>
    static async Task StoppingResumesTheCallerOffTheCancellingThread(Home home)
    {
        var entered = new ConcurrentDictionary<string, TaskCompletionSource>();
        TaskCompletionSource Entered(string key) => entered.GetOrAdd(key, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        Body = async (name, automation) =>
        {
            Entered(name + " " + automation.TriggerData.GetProperty("entity").GetString()).TrySetResult();
            // Ends inline in the cancelling call, as user code waiting on its token through a registration does
            // (Task.Delay with a token resumes on the thread pool instead).
            var stopped = new TaskCompletionSource();
            using var registration = automation.CancellationToken.Register(() => stopped.TrySetCanceled(automation.CancellationToken));
            await stopped.Task;
        };
        async Task<(RunOutcome Outcome, Thread Resumed)> Awaiting(string method, string key)
        {
            var outcome = await home.Class.RunToEnd(method, InvocationCause.Command, TriggerPayload.Command(key, "turn_on", null));
            return (outcome, Thread.CurrentThread);
        }
        var runs = new[] { Awaiting("Flow", "a"), Awaiting("Quiet", "a"), Awaiting("Queue", "a") };
        await Within(Task.WhenAll(Entered("Flow a").Task, Entered("Quiet a").Task, Entered("Queue a").Task), "the runs to enter their methods");
        var queued = Awaiting("Queue", "b");
        Exception? failed = null;
        var canceller = new Thread(() => { try { home.Manager.CancelRuns(CancelReasons.Stopping); } catch (Exception error) { failed = error; } }) { Name = "home-assistant-stopping", IsBackground = true };
        canceller.Start();
        Check(canceller.Join(Wait), "CancelRuns did not return");
        Check(failed == null, "CancelRuns failed: " + failed);
        var ended = await Within(Task.WhenAll(runs.Append(queued)), "the runs cancelled by the stop to end");
        Check(ended.All(e => e.Outcome == new RunOutcome("Cancelled", CancelReasons.Stopping)), "outcomes: " + string.Join(",", ended.Select(e => Show(e.Outcome))));
        Check(ended.All(e => !ReferenceEquals(e.Resumed, canceller)), "a RunToEnd caller resumed on the thread that cancelled its run: " + string.Join(",", ended.Select(e => e.Resumed.Name ?? e.Resumed.ManagedThreadId.ToString())));
        Check(!entered.ContainsKey("Queue b"), "the queued run's method ran after the stop");
        Console.WriteLine("PASS StoppingResumesTheCallerOffTheCancellingThread: Home Assistant stopping completes every RunToEnd with (Cancelled, Home Assistant stopping), running, history off or queued, and none of their callers resumes on the thread that cancelled them");
    }
}
