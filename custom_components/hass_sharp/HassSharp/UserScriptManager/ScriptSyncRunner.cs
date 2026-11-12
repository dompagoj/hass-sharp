using System.Reflection;

namespace HassSharp;

class ScriptSyncRunner
{
    sealed class ActiveRun(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
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
        foreach (var script in scriptArray)
        {
            script.CancelLifetime();
            foreach (var klass in script.Classes) klass.UnsubscribeSchedules();
        }

        using var scope = _gate.EnterScope();
        foreach (var key in scriptArray.SelectMany(s => s.Classes).SelectMany(c => c.Methods.Select(m => (c, m))))
        {
            _methods.Remove(key);
        }
    }

    public async Task Run(UserScriptClass scriptClass, MethodInfo methodInfo, AutomationMode mode, InvocationCause cause, string? runId = null, DateTime? plannedUtc = null, string? details = null)
    {
        var record = new InvocationRecord
        {
            RunId = runId ?? Guid.NewGuid().ToString("N"),
            AutomationId = scriptClass.Script.AutomationId, Revision = scriptClass.Script.Revision,
            Action = scriptClass.GetConcatedMethodNameWithClass(methodInfo.Name), Cause = cause.ToString(),
            PlannedUtc = plannedUtc,
            Details = details ?? (cause == InvocationCause.Entity ? UserScriptManager.CurrentTrigger.Value?.State.EntityId : null)
        };
        void Save() => scriptClass.Script.ScriptManager.History?.Save(record);
        // Fail closed before registering work if history is unavailable.
        Save();
        var registration = TryRegister(scriptClass, methodInfo, mode);
        if (registration == null)
        {
            record.Status = scriptClass.Script.LifetimeToken.IsCancellationRequested ? "Cancelled" : "Skipped";
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
                    await Execute(scriptClass, methodInfo, registration, cause);
                    record.Status = "Succeeded";
                }
                catch (OperationCanceledException) when (registration.Run.Token.IsCancellationRequested)
                { record.Status = "Cancelled"; record.Error = "Invocation cancelled"; }
                catch (Exception ex)
                { record.Status = "Failed"; record.Error = ex.GetBaseException().Message; }
                finally { record.DurationMs = timer.Elapsed.TotalMilliseconds; }
            });
        }
        catch (Exception ex)
        {
            record.Status = "Failed"; record.Error = ex.GetBaseException().Message;
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

    RunRegistration? TryRegister(UserScriptClass scriptClass, MethodInfo methodInfo, AutomationMode mode)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(scriptClass.Script.LifetimeToken);

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
            restartCancellation = runToRestart.CancellationTask ??=
                runToRestart.Cancellation.CancelAsync();
        }

        // Cancellation callbacks finishing does not mean user code has exited.
        // Keep the completion chain even when an intermediate restart is cancelled.
        var predecessor = mode is AutomationMode.Queued or AutomationMode.Restart ? state.Tail : null;
        var run = new ActiveRun(cancellation);
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
        RunRegistration registration, InvocationCause cause)
    {
        registration.Run.Token.ThrowIfCancellationRequested();
        scriptClass.Instance._cancellationToken.Value = registration.Run.Token;
        UserScriptManager.CurrentCause.Value = cause;
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
