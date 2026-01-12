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

    public List<UserScript> GetUserScripts() => _userScripts;

    public UserScript? GetUserScript(string scriptSlug) =>
        _userScripts.FirstOrDefault(s => s.CompiledScript.Slug() == scriptSlug);

    public void UnloadUserScripts()
    {
        PyInterop.UnSubscribeAllFromEntityTracking();
        DependencyTracking.Clear();
        _userScripts.Clear();
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

    public void LoadUserScripts(IEnumerable<CompiledUserScript> compiledScripts)
    {
        foreach (var compiled in compiledScripts) LoadUserScript(compiled);
    }


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

    public Task InitializeUserScripts() => Task.WhenAll(_userScripts.Select(InitializeUserScript));

    async Task InitializeUserScript(UserScript userScript)
    {
        await Task.WhenAll(userScript.Classes.Select(InitializeUserScriptClass));
        await userScript.WriteIfDirty();
    }

    Task InitializeUserScriptClass(UserScriptClass klass) => klass.Initialize();

    public async Task UpdateUserScript(CompiledUserScript compiled)
    {
        var foundIdx = _userScripts.FindIndex(s => s.CompiledScript.Id() == compiled.Id());
        if (foundIdx == -1) throw new("Script not found");

        var found = _userScripts[foundIdx];
        _userScripts.RemoveAt(foundIdx);

        UserScript? loadedScript = null;

        try
        {
            DependencyTracking.RemoveScript(found);
            loadedScript = LoadUserScript(compiled);

            Logger.Info("Initializing newly loaded scripts");
            await InitializeUserScript(loadedScript);
        }
        catch (Exception ex) when (ex is not TargetInvocationException)
        {
            // Restore the original script
            Logger.Error($"Failed to update user script {ex.Message} {ex.InnerException?.Message}");
            if (loadedScript != null) _userScripts.Remove(loadedScript);
            await InitializeUserScript(found);
            _userScripts.Add(found);
            throw;
        }

        // TODO: Remove
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
        await InitializeUserScript(LoadUserScript(compiled));
    }
}
