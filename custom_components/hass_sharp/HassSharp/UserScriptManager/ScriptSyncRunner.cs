using System.Reflection;

namespace HassSharp;

/// <summary>The <c>error</c> of a cancelled run (C9), named by the place that cancels it.</summary>
static class CancelReasons
{
    public const string Restarted = "Restarted by a newer trigger";
    public const string Reloaded = "Automation reloaded";
    public const string Stopping = "Home Assistant stopping";
    public const string TurnedOff = "Automation turned off";
    public const string Unknown = "Invocation cancelled";
}

class ScriptSyncRunner
{
    sealed class ActiveRun(CancellationTokenSource cancellation, UserScript.RunScope scope)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        /// <summary>The script's runs when this one started; turning the automation off cancels them with its reason.</summary>
        public UserScript.RunScope Scope { get; } = scope;
        /// <summary>Set before this run alone is cancelled; a lifetime cancellation leaves it null.</summary>
        public volatile string? CancelReason;
        public Task? CancellationTask { get; set; }
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token => Cancellation.Token;
    }

    sealed class MethodState
    {
        public ActiveRun? CurrentRun { get; set; }
        public Task Tail { get; set; } = Task.CompletedTask;
        /// <summary>The runs registered and not yet ended, oldest first: what a Restart run cancels once it is recorded.</summary>
        public List<ActiveRun> Active { get; } = [];
    }

    sealed record RunRegistration(
        MethodState State,
        ActiveRun Run,
        bool Restart,
        Task? Predecessor);

    readonly Lock _gate = new();
    readonly Dictionary<(UserScriptClass, MethodInfo), MethodState> _methods = new();

    public void Clear(UserScript script) => Clear([script]);

    public void Clear(IEnumerable<UserScript> scripts)
    {
        var scriptArray = scripts.ToArray();
        List<Exception> errors = [];
        foreach (var script in scriptArray)
        {
            try { script.CancelLifetime(); } catch (Exception error) { errors.Add(error); }
            foreach (var klass in script.Classes)
                try { klass.UnsubscribeSchedules(); } catch (Exception error) { errors.Add(error); }
        }

        using var scope = _gate.EnterScope();
        foreach (var key in scriptArray.SelectMany(s => s.Classes).SelectMany(c => c.Methods.Select(m => (c, m))))
        {
            _methods.Remove(key);
        }
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    /// <summary>Runs the method and records the run; the script waits for it when it is unloaded (<see cref="UserScript.TrackRun"/>).</summary>
    /// <param name="recorded">Completed once the run's first record is written (faulted when it cannot be), for Run now,
    /// which answers with a run id the history already has. Triggers do not wait for it.</param>
    public Task Run(UserScriptClass scriptClass, MethodInfo methodInfo, AutomationMode mode, InvocationCause cause, System.Text.Json.JsonElement payload, string? runId = null, DateTime? plannedUtc = null, TaskCompletionSource? recorded = null)
    {
        var run = RunCore(scriptClass, methodInfo, mode, cause, payload, runId, plannedUtc, recorded);
        scriptClass.Script.TrackRun(run);
        return run;
    }

    async Task RunCore(UserScriptClass scriptClass, MethodInfo methodInfo, AutomationMode mode, InvocationCause cause, System.Text.Json.JsonElement payload, string? runId, DateTime? plannedUtc, TaskCompletionSource? recorded)
    {
        // Run now waits for recorded: whatever fails on the way (registering logs through Python), it must complete.
        try { await RunRecorded(scriptClass, methodInfo, mode, cause, payload, runId, plannedUtc, recorded); }
        catch (Exception error) { recorded?.TrySetException(error); throw; }
        finally { recorded?.TrySetResult(); }
    }

    async Task RunRecorded(UserScriptClass scriptClass, MethodInfo methodInfo, AutomationMode mode, InvocationCause cause, System.Text.Json.JsonElement payload, string? runId, DateTime? plannedUtc, TaskCompletionSource? recorded)
    {
        var compiled = scriptClass.Script.CompiledScript;
        var trigger = compiled.VisualTriggers?.GetValueOrDefault(methodInfo.Name);
        var record = new InvocationRecord
        {
            RunId = runId ?? Guid.NewGuid().ToString("N"),
            AutomationId = scriptClass.Script.AutomationId, Revision = scriptClass.Script.Revision,
            Action = scriptClass.GetConcatedMethodNameWithClass(methodInfo.Name), Cause = cause.ToString(),
            VisualId = compiled.VisualId, VisualRevision = compiled.VisualId == null ? null : compiled.VisualRevision,
            TriggerNodeId = trigger?.NodeId, TriggerLabel = trigger?.Label,
            Trigger = Summary(cause, payload), PlannedUtc = plannedUtc,
        };
        var history = scriptClass.Script.ScriptManager.History;
        var snapshot = compiled is { VisualId: { } visualId, VisualRevision: { } visualRevision, VisualGraph: { } graph, VisualCatalog: { } catalog }
            ? new GraphSnapshot(record.Revision, visualId, visualRevision, graph, catalog) : null;
        // Recording stays off without a store or for code automations (C10).
        var sink = snapshot != null && history != null ? new RunTraceSink(history, record.RunId) : null;
        // The revision's snapshot goes with the run's first record that is written (C8), even when that is its last.
        var firstWritten = false;
        void Save() { history?.Save(record, firstWritten ? null : snapshot); firstWritten = true; }
        // The trigger (under the activation gate) only registers the run; every history write comes after the hop below,
        // so slow storage holds back neither other triggers nor the editor. The snapshot lands with the revision's first run.
        var registration = TryRegister(scriptClass, methodInfo, mode);
        var cancelled = registration == null && scriptClass.Script.LifetimeToken.IsCancellationRequested;
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        if (registration == null)
        {
            record.Status = cancelled ? "Cancelled" : "Skipped";
            if (cancelled) record.Error = scriptClass.Script.CancelReason ?? CancelReasons.Unknown;
            record.EndUtc = DateTime.UtcNow;
            try { history?.Save(record, snapshot); }
            catch (Exception error) { recorded?.TrySetException(error); throw; }
            recorded?.TrySetResult();
            return;
        }

        var waited = false;
        try
        {
            // Fail closed: a run whose first record cannot be written fails before it executes, and restarts nothing.
            Save();
            recorded?.TrySetResult();
            if (registration.Restart && Restart(registration) is { } restartCancellation)
                await restartCancellation;

            waited = true;
            if (registration.Predecessor is not null) await registration.Predecessor;
            await Task.Run(async () =>
            {
                record.StartUtc = DateTime.UtcNow;
                record.Status = "Running";
                Save();
                var timer = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var context = new InvocationContext(record.RunId, cause, plannedUtc, payload) { TraceSink = sink };
                    await Execute(scriptClass, methodInfo, registration, context);
                    record.Status = context.Stopped ? "Stopped" : "Succeeded";
                }
                catch (OperationCanceledException) when (registration.Run.Token.IsCancellationRequested)
                {
                    record.Status = "Cancelled";
                    record.Error = registration.Run.CancelReason ?? registration.Run.Scope.Reason ?? scriptClass.Script.CancelReason ?? CancelReasons.Unknown;
                }
                catch (Exception ex)
                { record.Status = "Failed"; record.Error = ex.GetBaseException().Message; Logger.Error($"Invocation {record.RunId} ({record.Action}) failed: {ex}"); }
                finally { record.DurationMs = timer.Elapsed.TotalMilliseconds; }
            });
        }
        catch (Exception ex)
        {
            recorded?.TrySetException(ex);
            record.Status = "Failed"; record.Error = ex.GetBaseException().Message;
            Logger.Error($"Invocation {record.RunId} ({record.Action}) failed: {ex}");
            throw;
        }
        finally
        {
            recorded?.TrySetResult();
            record.EndUtc = DateTime.UtcNow;
            var cancellationTask = Unregister(scriptClass, methodInfo, registration);
            try
            {
                if (cancellationTask is not null) await cancellationTask;
            }
            finally
            {
                registration.Run.Cancellation.Dispose();
                // A run that ended before it waited for the runs before it (its first record failed) ends, for the runs
                // after it, when they have: Queued and Restart runs still never overlap.
                if (!waited && registration.Predecessor is { IsCompleted: false } predecessor)
                    _ = predecessor.ContinueWith(_ => registration.Run.Completed.TrySetResult(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                else registration.Run.Completed.TrySetResult();
                Save();
            }
        }
    }

    // Test seam: replaced to prove a failing summary cannot stop a run.
    internal static Func<InvocationCause, System.Text.Json.JsonElement, string> Summarize = TriggerSummary.Summarize;

    // A summary that cannot be built must never stop the run from being recorded and executed.
    static string Summary(InvocationCause cause, System.Text.Json.JsonElement payload)
    {
        try { return Summarize(cause, payload); }
        catch (Exception ex)
        {
            try { Logger.Error($"Cannot summarise the {cause} trigger payload: {ex.Message}"); } catch { }
            return TriggerSummary.KindOnly(cause);
        }
    }

    RunRegistration? TryRegister(UserScriptClass scriptClass, MethodInfo methodInfo, AutomationMode mode)
    {
        var runs = scriptClass.Script.Runs;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(scriptClass.Script.LifetimeToken, runs.Token);

        using var scope = _gate.EnterScope();
        if (cancellation.IsCancellationRequested)
        {
            cancellation.Dispose();
            return null;
        }

        if (!_methods.TryGetValue((scriptClass, methodInfo), out var state))
        {
            state = new();
            _methods.Add((scriptClass, methodInfo), state);
        }

        if (mode == AutomationMode.Single && state.CurrentRun != null)
        {
            cancellation.Dispose();
            Logger.Info(
                $"Automation {scriptClass.GetConcatedMethodNameWithClass(methodInfo.Name)} is already running. Skipping new run.");
            return null;
        }

        // Cancellation callbacks finishing does not mean user code has exited.
        // Keep the completion chain even when an intermediate restart is cancelled.
        var predecessor = mode is AutomationMode.Queued or AutomationMode.Restart ? state.Tail : null;
        var run = new ActiveRun(cancellation, runs);
        state.CurrentRun = run;
        state.Tail = run.Completed.Task;
        state.Active.Add(run);
        return new(state, run, mode == AutomationMode.Restart, predecessor);
    }

    /// <summary>
    /// A Restart run, once its first record is written, cancels every run of the method registered before it that has
    /// not ended (its own first record may have failed, and then it cancelled nothing); null when there is none.
    /// </summary>
    Task? Restart(RunRegistration registration)
    {
        using var scope = _gate.EnterScope();
        var cancellations = new List<Task>();
        foreach (var earlier in registration.State.Active.TakeWhile(r => !ReferenceEquals(r, registration.Run)))
        {
            earlier.CancelReason ??= CancelReasons.Restarted;
            cancellations.Add(earlier.CancellationTask ??= earlier.Cancellation.CancelAsync());
        }
        return cancellations.Count == 0 ? null : Task.WhenAll(cancellations);
    }

    Task? Unregister(UserScriptClass scriptClass, MethodInfo methodInfo, RunRegistration registration)
    {
        using var scope = _gate.EnterScope();
        // Off the list a Restart run cancels, before this run's cancellation source is disposed.
        registration.State.Active.Remove(registration.Run);
        if (_methods.TryGetValue((scriptClass, methodInfo), out var currentState) &&
            ReferenceEquals(currentState, registration.State) &&
            ReferenceEquals(currentState.CurrentRun, registration.Run))
        {
            currentState.CurrentRun = null;
        }

        return registration.Run.CancellationTask;
    }

    static async Task Execute(
        UserScriptClass scriptClass,
        MethodInfo methodInfo,
        RunRegistration registration, InvocationContext context)
    {
        registration.Run.Token.ThrowIfCancellationRequested();
        scriptClass.Instance._cancellationToken.Value = registration.Run.Token;
        InvocationContext.Current = context;
        object? result;
        try { result = methodInfo.Invoke(scriptClass.Instance, null); }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
        if (result is Task task) await task;
        if (result is ValueTask valueTask) await valueTask;
    }
}
