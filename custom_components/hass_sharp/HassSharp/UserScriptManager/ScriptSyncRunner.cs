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
    }

    sealed record RunRegistration(
        MethodState State,
        ActiveRun Run,
        Task? RestartCancellation,
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

    public async Task Run(UserScriptClass scriptClass, MethodInfo methodInfo, AutomationMode mode, InvocationCause cause, System.Text.Json.JsonElement payload, string? runId = null, DateTime? plannedUtc = null)
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
        void Save() => history?.Save(record);
        // Fail closed before registering work if history is unavailable. The snapshot lands with the revision's first run.
        history?.Save(record, snapshot);
        var registration = TryRegister(scriptClass, methodInfo, mode);
        if (registration == null)
        {
            var cancelled = scriptClass.Script.LifetimeToken.IsCancellationRequested;
            record.Status = cancelled ? "Cancelled" : "Skipped";
            if (cancelled) record.Error = scriptClass.Script.CancelReason ?? CancelReasons.Unknown;
            record.EndUtc = DateTime.UtcNow;
            Save();
            return;
        }

        try
        {
            if (registration.RestartCancellation is not null)
                await registration.RestartCancellation;

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
            record.Status = "Failed"; record.Error = ex.GetBaseException().Message;
            Logger.Error($"Invocation {record.RunId} ({record.Action}) failed: {ex}");
            throw;
        }
        finally
        {
            record.EndUtc = DateTime.UtcNow;
            var cancellationTask = Unregister(scriptClass, methodInfo, registration);
            try
            {
                if (cancellationTask is not null) await cancellationTask;
            }
            finally
            {
                registration.Run.Cancellation.Dispose();
                registration.Run.Completed.TrySetResult();
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

        Task? restartCancellation = null;
        if (mode == AutomationMode.Restart && state.CurrentRun is { } runToRestart)
        {
            runToRestart.CancelReason ??= CancelReasons.Restarted;
            restartCancellation = runToRestart.CancellationTask ??=
                runToRestart.Cancellation.CancelAsync();
        }

        // Cancellation callbacks finishing does not mean user code has exited.
        // Keep the completion chain even when an intermediate restart is cancelled.
        var predecessor = mode is AutomationMode.Queued or AutomationMode.Restart ? state.Tail : null;
        var run = new ActiveRun(cancellation, runs);
        state.CurrentRun = run;
        state.Tail = run.Completed.Task;
        return new(state, run, restartCancellation, predecessor);
    }

    Task? Unregister(UserScriptClass scriptClass, MethodInfo methodInfo, RunRegistration registration)
    {
        using var scope = _gate.EnterScope();
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
