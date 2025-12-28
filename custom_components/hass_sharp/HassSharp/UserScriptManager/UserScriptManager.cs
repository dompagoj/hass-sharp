using System.Runtime.InteropServices;

namespace HassSharp;

using EntityId = string;

class TriggerContext
{
    public required HasEntityState NewState { get; init; }

    public HasEntityState? OldState { get; init; }
}

public readonly struct DependencyEntry
{
    public required UserScriptClass ScriptClass { get; init; }
    public required string MethodName { get; init; }
}

class UserScriptManager
{
    public static readonly AsyncLocal<TriggerContext?> CurrentTrigger = new();

    readonly List<UserScript> _userScripts = new();

    // This is iterator on the python side which calles async_track_state_change_event from hass on each key
    public Dictionary<EntityId, List<DependencyEntry>> DependencyTracking { get; } = new();

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


    public void ClearUserScripts()
    {
        _userScripts.Clear();
        DependencyTracking.Clear();
    }

    public UserScript LoadUserScript(CompiledUserScript compiledScript)
    {
        var baseType = typeof(Automation);

        var userScriptClasses = compiledScript.Assembly.GetTypes()
            .Where(t => baseType.IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => new UserScriptClass(t, this))
            .ToArray();

        var userScript = new UserScript
        {
            FileName = compiledScript.FileName,
            FilePath = compiledScript.FilePath,
            Assembly = compiledScript.Assembly,
            Classes = userScriptClasses,
        };
        _userScripts.Add(userScript);
        return userScript;
    }

    public void LoadUserScripts(IEnumerable<CompiledUserScript> compiledScripts)
    {
        foreach (var compiled in compiledScripts) LoadUserScript(compiled);
    }


    public void TrackEntityCall(string entityId, UserScriptClass scriptClass, string method)
    {
        Dictionary<string, int> test = new();

        ref var res = ref CollectionsMarshal.GetValueRefOrAddDefault(DependencyTracking, entityId, out _);
        if (res != null)
        {
            if (!res.Exists(e => e.ScriptClass == scriptClass && e.MethodName == method))
            {
                res.Add(new()
                {
                    MethodName = method,
                    ScriptClass = scriptClass
                });
            }
        }
        else
        {
            res =
            [
                new()
                {
                    MethodName = method,
                    ScriptClass = scriptClass,
                }
            ];
        }

        if (!DependencyTracking.TryGetValue(entityId, out var entries))
        {
            entries =
            [
                new()
                {
                    MethodName = method,
                    ScriptClass = scriptClass,
                }
            ];
            DependencyTracking[entityId] = entries;
        }
        else
        {
            if (!entries.Exists(e => e.ScriptClass == scriptClass && e.MethodName == method))
            {
                entries.Add(new()
                {
                    MethodName = method,
                    ScriptClass = scriptClass
                });
            }
        }
    }

    public Task RunEntries(List<DependencyEntry> entries, TriggerContext trigger) =>
        Task.WhenAll(entries.Select(async e => await RunEntry(e, trigger)));

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

        var found = _userScripts[foundIdx]; // TODO: Insert the old script back if something fails
        _userScripts.RemoveAt(foundIdx);

        foreach (var (entityId, scriptClasses) in DependencyTracking)
        {
            scriptClasses.RemoveAll(c => found.Classes.Contains(c.ScriptClass));
            if (scriptClasses.Count == 0)
            {
                DependencyTracking.Remove(entityId);
                // TODO: Stop tracking on the python side
            }
        }

        var loadedScript = LoadUserScript(compiled);

        Logger.Info("Initializing newly loaded scripts");
        await InitializeUserScript(loadedScript);
    }
}
