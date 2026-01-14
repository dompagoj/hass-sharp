using System.Reflection;

namespace HassSharp;

class TriggerContext
{
    public required HasEntityState NewState { get; init; }

    public HasEntityState? OldState { get; init; }
}

class UserScriptManager
{
    public static readonly AsyncLocal<TriggerContext?> CurrentTrigger = new();

    readonly List<UserScript> _userScripts = new();
    public DependencyTracking DependencyTracking { get; } = new();
    public ScriptSyncRunner SyncRunner { get; } = new();

    public List<UserScript> GetUserScripts() => _userScripts;

    public UserScript? GetUserScript(string scriptSlug) =>
        _userScripts.FirstOrDefault(s => s.CompiledScript.Slug() == scriptSlug);

    public async Task UnloadUserScripts()
    {
        PyInterop.UnSubscribeAllFromEntityTracking();
        DependencyTracking.Clear();
        _userScripts.Clear();
        await SyncRunner.Clear();
    }

    public UserScript LoadUserScript(CompiledUserScript compiledScript)
    {
        var baseType = typeof(Automation);

        var userScript = new UserScript
        {
            CompiledScript = compiledScript,
            Classes = null!,
            ScriptManager = this,
        };
        var userScriptClasses = compiledScript.Assembly.GetTypes()
            .Where(t => baseType.IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => new UserScriptClass(t, userScript))
            .ToArray();

        userScript.Classes = userScriptClasses;
        _userScripts.Add(userScript);
        return userScript;
    }

    public Task LoadUserScripts(IEnumerable<CompiledUserScript> compiledScripts)
        => Task.WhenAll(compiledScripts.Select(s => LoadUserScript(s).Initialize()));


    public void TrackEntityCall(string entityId, UserScriptClass scriptClass, string method) =>
        DependencyTracking.TrackEntityCall(entityId, scriptClass, method);

    public Task RunEntries(string entityId, TriggerContext trigger)
    {
        var entries = DependencyTracking.GetEntries(entityId);
        return Task.WhenAll(entries.Select(async e => await RunEntry(e, trigger)));
    }

    public async Task RunEntry(DependencyEntry entry, TriggerContext trigger)
    {
        CurrentTrigger.Value = trigger;
        try
        {
            await entry.ScriptClass.RunMethod(entry.MethodName);
        }
        finally
        {
            CurrentTrigger.Value = null;
        }
    }

    public async Task UpdateUserScript(CompiledUserScript compiled)
    {
        var foundIdx = _userScripts.FindIndex(s => s.CompiledScript.Id() == compiled.Id());
        if (foundIdx == -1) throw new("Script not found");

        var found = _userScripts[foundIdx];
        _userScripts.RemoveAt(foundIdx);

        UserScript? loadedScript = null;

        try
        {
            await found.DisposeAsync();
            loadedScript = LoadUserScript(compiled);

            Logger.Info("Initializing newly loaded scripts");
            await loadedScript.Initialize();
            await loadedScript
                .WriteIfDirty(); // TODO maybe make this a single call? its easy to forget not to write to disk
        }
        catch (Exception ex) when (ex is not TargetInvocationException)
        {
            // Restore the original script
            Logger.Error($"Failed to update user script {ex.Message} {ex.InnerException?.Message}");
            if (loadedScript != null) _userScripts.Remove(loadedScript);
            await found.Initialize();
            await found.WriteToDisk();
            _userScripts.Add(found);
            throw;
        }

        DependencyTracking.Debug();
    }

    public async Task CreateEmptyScript(CodeCompiler compiler, string scriptName)
    {
        const string emptyScriptSource = """
                                         public class ReplaceThisNameAutomation : Automation
                                         {
                                         }
                                         """;
        var compiled =
            await compiler.CompileFromUserScriptFile(scriptName, emptyScriptSource, false);
        await LoadUserScript(compiled).Initialize();
    }
}
