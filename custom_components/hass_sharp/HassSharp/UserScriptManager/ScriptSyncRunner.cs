using System.Collections.Concurrent;
using System.Reflection;

namespace HassSharp;

class ScriptSyncRunner
{
    readonly ConcurrentDictionary<MethodInfo, CancellationTokenSource> _cts = new();
    readonly ConcurrentDictionary<MethodInfo, SemaphoreSlim> _queues = new();

    public async Task Clear()
    {
        foreach (var token in _cts.Values)
        {
            await token.CancelAsync();
        }

        foreach (var semaphore in _queues.Values)
        {
            semaphore.Dispose();
        }

        _cts.Clear();
        _queues.Clear();
    }

    public async Task Clear(UserScript script)
    {
        foreach (var methodInfo in script.Classes.SelectMany(c => c.Methods))
        {
            if (_cts.TryRemove(methodInfo, out var token))
            {
                await token.CancelAsync();
                token.Dispose();
            }

            if (_queues.TryRemove(methodInfo, out var sem)) sem.Dispose();
        }
    }

    public async Task Run(UserScriptClass scriptClass, MethodInfo methodInfo, AutomationMode mode,
        Func<object?> callMethod)
    {
        var classMethodName = scriptClass.GetConcatedMethodNameWithClass(methodInfo.Name);
        switch (mode)
        {
            case AutomationMode.Single:
                if (_cts.TryGetValue(methodInfo, out _))
                {
                    Logger.Info(
                        $"Automation {classMethodName} is already running. Skipping new run.");
                    return;
                }

                break;

            case AutomationMode.Restart:
                if (_cts.TryRemove(methodInfo, out var oldCts))
                {
                    await oldCts.CancelAsync();
                    oldCts.Dispose();
                }

                break;

            case AutomationMode.Queued:
            case AutomationMode.Parallel:
            default:
                break;
        }

        var cts = new CancellationTokenSource();
        _cts[methodInfo] = cts;

        await Task.Run(async () =>
        {
            if (mode == AutomationMode.Queued)
            {
                var semaphore = _queues.GetOrAdd(methodInfo, _ => new SemaphoreSlim(1, 1));
                try
                {
                    await semaphore.WaitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            try
            {
                scriptClass.Instance._cancellationToken.Value = cts.Token;
                var result = callMethod();

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
                Logger.Error(
                    $"Error running automation {classMethodName}\n{ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                if (mode == AutomationMode.Queued)
                {
                    if (_queues.TryGetValue(methodInfo, out var semaphore))
                    {
                        semaphore.Release();
                    }
                }

                if (_cts.TryGetValue(methodInfo, out var currentCts) && currentCts == cts)
                {
                    _cts.TryRemove(methodInfo, out _);
                    cts.Dispose();
                }
            }
        }, cts.Token);
    }
}
