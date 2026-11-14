namespace HassSharp;

class UserScript
{
    internal bool Active { get; set; } = true;
    volatile bool _enabled = true;
    /// <summary>
    /// Off: the script stays loaded, so a manual run still works, but its Home Assistant triggers are detached (never
    /// attached when it loads off), and its entity and schedule triggers and its startup run are dropped (a schedule
    /// occurrence that passes is skipped, not caught up).
    /// </summary>
    internal bool Enabled { get => _enabled; init => _enabled = value; }

    /// <summary>Held while Home Assistant triggers are attached or detached, so they are attached at most once.</summary>
    internal SemaphoreSlim NativeSwitch { get; } = new(1, 1);

    /// <summary>The runs started since the automation was last turned off; turning it off again cancels them.</summary>
    internal sealed class RunScope
    {
        readonly CancellationTokenSource _cancellation = new();
        public CancellationToken Token => _cancellation.Token;
        public volatile string? Reason;
        public void Cancel(string reason) { Reason ??= reason; _cancellation.Cancel(); }
    }
    volatile RunScope _runs = new();
    internal RunScope Runs => _runs;

    /// <summary>
    /// Turns the automation on or off without reloading it. The switch is applied first (under the activation gate, so a
    /// trigger sees either on or off); then runs in progress are cancelled and Home Assistant triggers detached or
    /// attached. Errors in that follow-up are logged: they do not undo or fail the switch.
    /// </summary>
    internal async Task SetEnabledAsync(bool enabled)
    {
        RunScope? cancelled = null;
        lock (ScriptManager.ActivationGate)
        {
            if (_enabled == enabled) return;
            _enabled = enabled;
            if (!enabled) { cancelled = _runs; _runs = new(); }
        }
        // A cancellation callback (user code can register one) may throw; every callback has still run.
        try { cancelled?.Cancel(CancelReasons.TurnedOff); }
        catch (Exception error) { Logger.Error($"Turning {CompiledScript.FileName} off: a cancellation callback failed: {error}"); }
        await NativeSwitch.WaitAsync();
        try
        {
            if (LifetimeToken.IsCancellationRequested) return;
            // The latest switch wins when calls overlap; attaching twice does nothing.
            var on = _enabled;
            foreach (var klass in Classes)
                try { if (on) await klass.AttachNativeAsync(); else await klass.DetachNativeAsync(); }
                catch (Exception error) { Logger.Error($"Turning {CompiledScript.FileName} {(on ? "on" : "off")}: Home Assistant triggers could not be {(on ? "attached" : "detached")}: {error}"); }
        }
        finally { NativeSwitch.Release(); }
    }
    internal bool Invalidated { get; private set; }
    internal string Generation { get; } = Guid.NewGuid().ToString("N");
    Task _preparation = Task.CompletedTask;
    Task? _cleanup;
    readonly object _cleanupGate = new();
    public string AutomationId { get; init; } = Guid.NewGuid().ToString("N");
    public string Revision => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(CompiledScript.SourceCode)));

    volatile CancellationTokenSource _lifetimeCancellation = new();

    public required UserScriptManager ScriptManager { get; init; }

    public required CompiledUserScript CompiledScript { get; init; }
    public required UserScriptClass[] Classes { get; set; }

    internal CancellationToken LifetimeToken => _lifetimeCancellation.Token;

    internal async Task ConfigureAndStart()
    {
        var epoch = ScriptManager.ActivationEpoch;
        Active = false;
        await PrepareAsync();
        ScriptManager.ActivatePreparedScript(this, epoch);
        Start();
    }

    internal Task PrepareAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _preparation = completion.Task;
        return PrepareCoreAsync(completion);
    }

    async Task PrepareCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            LifetimeToken.ThrowIfCancellationRequested();
            foreach (var klass in Classes) klass.Configure();
            foreach (var klass in Classes)
            {
                LifetimeToken.ThrowIfCancellationRequested();
                await klass.SubscribeAsync();
            }
        }
        finally { completion.TrySetResult(); }
    }

    internal void Start()
    {
        lock (ScriptManager.ActivationGate)
        {
            if (!Active || Invalidated || LifetimeToken.IsCancellationRequested) return;
            foreach (var klass in Classes) klass.Start();
        }
    }

    internal void Invalidate() { Active = false; Invalidated = true; }

    string? _cancelReason;
    /// <summary>Why the lifetime was cancelled (the first reason given wins); null when unknown.</summary>
    internal string? CancelReason => Volatile.Read(ref _cancelReason);

    internal void SetCancelReason(string reason) => Interlocked.CompareExchange(ref _cancelReason, reason, null);

    internal void CancelLifetime(string? reason = null)
    {
        if (reason != null) SetCancelReason(reason);
        _lifetimeCancellation.Cancel();
    }

    internal void RenewLifetime() { _cancelReason = null; _lifetimeCancellation = new(); }

    internal Task ClearFromTrackingAsync()
    {
        lock (_cleanupGate) return _cleanup ??= ClearCoreAsync();
    }

    async Task ClearCoreAsync()
    {
        ScriptManager.RemoveScriptFromList(this);
        List<Exception> errors = [];
        try { CancelLifetime(); } catch (Exception error) { errors.Add(error); }
        // Invalidated callbacks are already inert. Wait until preparation stops
        // publishing subscriptions before draining either schedule/native lists.
        try { await _preparation; } catch (Exception error) { errors.Add(error); }
        try { ScriptManager.SyncRunner.Clear(this); } catch (Exception error) { errors.Add(error); }
        try { ScriptManager.DependencyTracking.RemoveScript(this); } catch (Exception error) { errors.Add(error); }
        // An attach in progress (the automation being turned on) finishes first, so its leases are removed here too.
        await NativeSwitch.WaitAsync();
        try
        {
            var nativeCleanup = Task.WhenAll(Classes.Select(klass => klass.UnsubscribeNativeAsync()));
            try { await nativeCleanup; }
            catch (Exception error) { errors.Add(nativeCleanup.Exception ?? error); }
        }
        finally { NativeSwitch.Release(); }
        try { await DrainRunsAsync(); } catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    readonly HashSet<Task> _inFlight = [];

    /// <summary>
    /// How long unloading waits for the runs it cancelled to finish (user code may take a moment to stop, or ignore the
    /// cancellation); after that the script unloads without them, and says so.
    /// </summary>
    internal static TimeSpan RunDrainTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>A run of this script, until it ends: unloading waits for it, so that it records its end first.</summary>
    internal void TrackRun(Task run)
    {
        if (run.IsCompleted) return;
        lock (_inFlight) _inFlight.Add(run);
        run.ContinueWith(ended => { lock (_inFlight) _inFlight.Remove(ended); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Waits for the runs in progress (already cancelled) to end, so that nothing of this script, its run history and
    /// trace included, is written after unloading returns. A run's own failure was logged when it failed.
    /// </summary>
    async Task DrainRunsAsync()
    {
        Task[] runs;
        lock (_inFlight) runs = [.. _inFlight];
        if (await Drain(runs, RunDrainTimeout)) return;
        int left; lock (_inFlight) left = _inFlight.Count;
        Logger.Warn($"{CompiledScript.FileName}: {left} run(s) did not stop within {RunDrainTimeout.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s of being cancelled; unloading without waiting for them");
    }

    /// <summary>Whether the runs ended within the timeout; their failures are observed, never thrown.</summary>
    internal static async Task<bool> Drain(Task[] runs, TimeSpan timeout)
    {
        if (runs.Length == 0) return true;
        var all = Task.WhenAll(runs);
        _ = all.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return await Task.WhenAny(all, Task.Delay(timeout)) == all;
    }

    internal void Unload()
    {
        CompiledScript.Unload();
    }
}
