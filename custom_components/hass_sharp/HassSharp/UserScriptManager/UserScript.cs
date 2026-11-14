namespace HassSharp;

class UserScript
{
    internal bool Active { get; set; } = true;
    volatile bool _enabled = true;
    /// <summary>
    /// Off: the script stays loaded and subscribed, so a manual run still works, but its entity, Home Assistant and
    /// schedule triggers and its startup run are dropped (a schedule occurrence that passes is skipped, not caught up).
    /// </summary>
    internal bool Enabled { get => _enabled; init => _enabled = value; }

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

    /// <summary>Turns the automation on or off without reloading it; turning it off cancels its runs in progress.</summary>
    internal void SetEnabled(bool enabled)
    {
        RunScope cancelled;
        lock (ScriptManager.ActivationGate)
        {
            if (_enabled == enabled) return;
            _enabled = enabled;
            if (enabled) return;
            cancelled = _runs;
            _runs = new();
        }
        cancelled.Cancel(CancelReasons.TurnedOff);
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
        var nativeCleanup = Task.WhenAll(Classes.Select(klass => klass.UnsubscribeNativeAsync()));
        try { await nativeCleanup; }
        catch (Exception error) { errors.Add(nativeCleanup.Exception ?? error); }
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    internal void Unload()
    {
        CompiledScript.Unload();
    }
}
