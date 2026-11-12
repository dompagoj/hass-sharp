namespace HassSharp;

class UserScript
{
    volatile CancellationTokenSource _lifetimeCancellation = new();

    public required UserScriptManager ScriptManager { get; init; }

    public required CompiledUserScript CompiledScript { get; init; }
    public required UserScriptClass[] Classes { get; set; }

    internal CancellationToken LifetimeToken => _lifetimeCancellation.Token;

    public Task WriteToDisk() => CompiledScript.WriteToDisk();
    public ValueTask WriteIfDirty() => CompiledScript.WriteIfDirty();

    internal Task ConfigureAndStart()
    {
        foreach (var klass in Classes) klass.Configure();
        foreach (var klass in Classes) klass.Subscribe();
        foreach (var klass in Classes) klass.Start();
        return Task.CompletedTask;
    }

    internal void CancelLifetime() => _lifetimeCancellation.Cancel();

    internal void RenewLifetime() => _lifetimeCancellation = new();

    public void FullClean()
    {
        ClearFromTracking();
        Unload();
        CompiledScript.DeleteFromDisk();
    }

    internal void ClearFromTracking()
    {
        ScriptManager.SyncRunner.Clear(this);
        ScriptManager.DependencyTracking.RemoveScript(this);
        ScriptManager.RemoveScriptFromList(this);
    }

    internal void Unload()
    {
        CompiledScript.Unload();
    }
}
