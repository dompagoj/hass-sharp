namespace HassSharp;

class UserScript
{
    public required UserScriptManager ScriptManager { get; init; }

    public required CompiledUserScript CompiledScript { get; init; }
    public required UserScriptClass[] Classes { get; set; }

    public Task WriteToDisk() => CompiledScript.WriteToDisk();
    public ValueTask WriteIfDirty() => CompiledScript.WriteIfDirty();
}