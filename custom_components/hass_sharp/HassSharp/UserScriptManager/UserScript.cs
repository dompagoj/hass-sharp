namespace HassSharp;

class UserScript : IAsyncDisposable
{
    public required UserScriptManager ScriptManager { get; init; }

    public required CompiledUserScript CompiledScript { get; init; }
    public required UserScriptClass[] Classes { get; set; }

    public Task WriteToDisk() => CompiledScript.WriteToDisk();
    public ValueTask WriteIfDirty() => CompiledScript.WriteIfDirty();

    internal Task Initialize() => Task.WhenAll(Classes.Select(c => c.Initialize()));

    public async ValueTask DisposeAsync()
    {
        await ScriptManager.SyncRunner.Clear(this);
        ScriptManager.DependencyTracking.RemoveScript(this);
    }
}
