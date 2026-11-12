namespace HassSharp;

class UserScript
{
    internal bool Active { get; set; } = true;
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

    public Task WriteToDisk() => CompiledScript.WriteToDisk();
    public ValueTask WriteIfDirty() => CompiledScript.WriteIfDirty();

    internal async Task ConfigureAndStart()
    {
        Active = false;
        await PrepareAsync();
        lock (ScriptManager.ActivationGate)
        {
            LifetimeToken.ThrowIfCancellationRequested();
            Active = true;
        }
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
        foreach (var klass in Classes) klass.Start();
    }

    internal void CancelLifetime() => _lifetimeCancellation.Cancel();

    internal void RenewLifetime() => _lifetimeCancellation = new();

    public async Task FullCleanAsync()
    {
        await ClearFromTrackingAsync();
        Unload();
        CompiledScript.DeleteFromDisk();
        ScriptManager.History?.Forget(CompiledScript.FilePath);
    }

    internal Task ClearFromTrackingAsync()
    {
        lock (_cleanupGate) return _cleanup ??= ClearCoreAsync();
    }

    async Task ClearCoreAsync()
    {
        ScriptManager.RemoveScriptFromList(this);
        CancelLifetime();
        List<Exception> errors = [];
        try { ScriptManager.SyncRunner.Clear(this); } catch (Exception error) { errors.Add(error); }
        ScriptManager.DependencyTracking.RemoveScript(this);
        await _preparation;
        // Preparation may have registered dependencies after the first removal.
        try { ScriptManager.SyncRunner.Clear(this); } catch (Exception error) { errors.Add(error); }
        ScriptManager.DependencyTracking.RemoveScript(this);
        try { await Task.WhenAll(Classes.Select(klass => klass.UnsubscribeNativeAsync())); }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    internal void Unload()
    {
        CompiledScript.Unload();
    }
}
