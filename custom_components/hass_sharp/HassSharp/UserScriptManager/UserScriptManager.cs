using System.Reflection;
using System.Text.Json;

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

    public async Task<UserScriptSourceDTO?> GetUserScriptSource(string scriptSlug)
    {
        var found = _userScripts.FirstOrDefault(s => s.ScriptSlug() == scriptSlug);
        if (found == null) return null;

        var contents = await File.ReadAllTextAsync(found.ScriptId());

        return new()
        {
            FileName = found.FileName,
            Source = contents,
        };
    }

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
            FileName = compiledScript.FileName,
            FilePath = compiledScript.FilePath,
            Assembly = compiledScript.Assembly,
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

    Task InitializeUserScript(UserScript userScript) =>
        Task.WhenAll(userScript.Classes.Select(InitializeUserScriptClass));

    Task InitializeUserScriptClass(UserScriptClass klass) => klass.Initialize();


    public async Task UpdateUserScript(CompiledUserScript compiled)
    {
        Logger.Info(
            $"New script path: {compiled.FilePath}, Existing scripts paths: {string.Join('\n', _userScripts.Select(s => s.FilePath))}");
        var foundIdx = _userScripts.FindIndex(s => s.FilePath == compiled.FilePath);
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
        var compiled = await compiler.CompileSingleFile(Path.Join(HassPath.UserScripts, scriptName), emptyScriptSource);
        var script = LoadUserScript(compiled);
        await InitializeUserScript(script);
    }
}
