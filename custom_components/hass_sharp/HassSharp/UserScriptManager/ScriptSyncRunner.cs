using System.Reflection;

namespace HassSharp;

class ScriptSyncRunner
{
    sealed class ActiveRun(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task? CancellationTask { get; set; }
        public CancellationToken Token => Cancellation.Token;
    }

    sealed class MethodState
    {
        public ActiveRun? CurrentRun { get; set; }
        public SemaphoreSlim? Queue { get; set; }
    }

    sealed record RunRegistration(
        MethodState State,
        ActiveRun Run,
        Task? RestartCancellation,
        SemaphoreSlim? Queue);

    readonly Lock _gate = new();
    readonly Dictionary<MethodInfo, MethodState> _methods = new();

    public void Clear(UserScript script) => Clear([script]);

    public void Clear(IEnumerable<UserScript> scripts)
    {
        var scriptArray = scripts.ToArray();
        foreach (var script in scriptArray) script.CancelLifetime();

        using var scope = _gate.EnterScope();
        foreach (var method in scriptArray.SelectMany(s => s.Classes).SelectMany(c => c.Methods))
        {
            _methods.Remove(method);
        }
    }

    public async Task Run(UserScriptClass scriptClass, MethodInfo methodInfo, AutomationMode mode)
    {
        var registration = TryRegister(scriptClass, methodInfo, mode);
        if (registration == null) return;

        try
        {
            if (registration.RestartCancellation is not null)
                await registration.RestartCancellation;

            await Task.Run(() => Execute(scriptClass, methodInfo, registration));
        }
        finally
        {
            var cancellationTask = Unregister(methodInfo, registration);
            try
            {
                if (cancellationTask is not null) await cancellationTask;
            }
            finally
            {
                registration.Run.Cancellation.Dispose();
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

        if (!_methods.TryGetValue(methodInfo, out var state))
        {
            state = new();
            _methods.Add(methodInfo, state);
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

        var queue = mode == AutomationMode.Queued ? state.Queue ??= new(1, 1) : null;
        var run = new ActiveRun(cancellation);
        state.CurrentRun = run;
        return new(state, run, restartCancellation, queue);
    }

    Task? Unregister(MethodInfo methodInfo, RunRegistration registration)
    {
        using var scope = _gate.EnterScope();
        if (_methods.TryGetValue(methodInfo, out var currentState) &&
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
        RunRegistration registration)
    {
        var acquiredQueue = false;
        try
        {
            if (registration.Queue != null)
            {
                await registration.Queue.WaitAsync(registration.Run.Token);
                acquiredQueue = true;
            }

            registration.Run.Token.ThrowIfCancellationRequested();
            scriptClass.Instance._cancellationToken.Value = registration.Run.Token;

            if (scriptClass.Instance is RunnableClassScript runnable)
            {
                await runnable.Run();
                return;
            }

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
        catch (HassSharpInitializingException)
        {
            // ignore InitGuard(); calls
        }
        catch (TargetInvocationException ex) when (ex.InnerException is HassSharpInitializingException)
        {
            // ignore InitGuard(); calls
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
        finally
        {
            if (acquiredQueue) registration.Queue!.Release();
        }
    }
}
