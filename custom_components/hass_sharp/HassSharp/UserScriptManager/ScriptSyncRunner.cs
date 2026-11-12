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
        foreach (var script in scriptArray) script.CancelLifetime();

        using var scope = _gate.EnterScope();
        foreach (var key in scriptArray.SelectMany(s => s.Classes).SelectMany(c => c.Methods.Select(m => (c, m))))
        {
            _methods.Remove(key);
        }
    }

    public async Task Run(UserScriptClass scriptClass, MethodInfo methodInfo, AutomationMode mode, InvocationCause cause)
    {
        var registration = TryRegister(scriptClass, methodInfo, mode);
        if (registration == null) return;

        try
        {
            if (registration.RestartCancellation is not null)
                await registration.RestartCancellation;

            if (registration.Predecessor is not null) await registration.Predecessor;
            await Task.Run(() => Execute(scriptClass, methodInfo, registration, cause));
        }
        finally
        {
            var cancellationTask = Unregister(scriptClass, methodInfo, registration);
            try
            {
                if (cancellationTask is not null) await cancellationTask;
            }
            finally
            {
                registration.Run.Cancellation.Dispose();
                registration.Run.Completed.TrySetResult();
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

        var predecessor = mode == AutomationMode.Queued ? state.Tail : null;
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
        try
        {
            registration.Run.Token.ThrowIfCancellationRequested();
            scriptClass.Instance._cancellationToken.Value = registration.Run.Token;

            UserScriptManager.CurrentCause.Value = cause;

            var result = methodInfo.Invoke(
                scriptClass.Instance,
                BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                null,
                null,
                null
            );

            if (result is Task task) await task;
            if (result is ValueTask valueTask) await valueTask;
        }
        catch (OperationCanceledException)
        {
            // Task was canceled, ignore
        }
        catch (Exception ex)
        {
            var methodName = scriptClass.GetConcatedMethodNameWithClass(methodInfo.Name);
            Logger.Error(
                $"Error running automation {methodName} | {ex.Message} | {ex.InnerException?.Message} | {ex.StackTrace}");
        }
    }
}
