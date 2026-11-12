namespace HassSharp;

class UserScript
{
    internal bool Active { get; set; } = true;
    public string AutomationId { get; init; } = Guid.NewGuid().ToString("N");
    public string Revision => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(CompiledScript.SourceCode)));

    volatile CancellationTokenSource _lifetimeCancellation = new();

    public required UserScriptManager ScriptManager { get; init; }

    public required CompiledUserScript CompiledScript { get; init; }
    public required UserScriptClass[] Classes { get; set; }

    internal CancellationToken LifetimeToken => _lifetimeCancellation.Token;

    public Task WriteToDisk() => CompiledScript.WriteToDisk();
    public ValueTask WriteIfDirty() => CompiledScript.WriteIfDirty();

    internal Task ConfigureAndStart()
    {
        Prepare();
        Start();
        return Task.CompletedTask;
    }

    internal void Prepare()
    {
        foreach (var klass in Classes) klass.Configure();
        foreach (var klass in Classes) klass.Subscribe();
    }

    internal void Start()
    {
        foreach (var klass in Classes) klass.Start();
    }

    internal void CancelLifetime() => _lifetimeCancellation.Cancel();

    internal void RenewLifetime() => _lifetimeCancellation = new();

    public void FullClean()
    {
        ClearFromTracking();
        Unload();
        CompiledScript.DeleteFromDisk();
        ScriptManager.History?.Forget(CompiledScript.FilePath);
    }

    internal void ClearFromTracking()
    {
        ScriptManager.RemoveScriptFromList(this);
        ScriptManager.SyncRunner.Clear(this);
        ScriptManager.DependencyTracking.RemoveScript(this);
    }

    internal void Unload()
    {
        CompiledScript.Unload();
    }
}
