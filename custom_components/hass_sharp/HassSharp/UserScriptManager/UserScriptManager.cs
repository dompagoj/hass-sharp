namespace HassSharp;

using EntityId = string;

public readonly struct DependencyEntry
{
    public required UserScript Script { get; init; }
    public required string MethodName { get; init; }
}

class UserScriptManager
{
    readonly HashSet<UserScript> _userScripts = new(new UserScriptComparer());

    // This is iterator on the python side which calles async_track_state_change_event from hass on each key
    public Dictionary<EntityId, List<DependencyEntry>> DependencyTracking { get; } = new();

    public HashSet<UserScript> GetUserScripts() => _userScripts;

    public async Task<UserScriptSourceDTO?> GetUserScriptSource(string fileName)
    {
        var found = _userScripts.FirstOrDefault(s => s.CompiledUserScript.FileName == fileName);
        if (found == null) return null;

        var contents = await File.ReadAllTextAsync(found.CompiledUserScript.FilePath);

        return new()
        {
            FileName = found.CompiledUserScript.FileName,
            Source = contents,
        };
    }


    public void ClearUserScripts()
    {
        _userScripts.Clear();
        DependencyTracking.Clear();
    }

    public void LoadUserScripts(List<CompiledUserScript> compiledScripts)
    {
        foreach (var compiled in compiledScripts)
        {
            var baseType = typeof(Automation);

            foreach (var automationClass in compiled.Assembly.GetTypes()
                         .Where(t => baseType.IsAssignableFrom(t) && !t.IsAbstract))
            {
                var userScript = new UserScript(automationClass, this)
                {
                    CompiledUserScript = compiled,
                };
                _userScripts.Add(userScript);
            }
        }
    }


    public void TrackEntityCall(string entityId, UserScript script, string method)
    {
        if (!DependencyTracking.TryGetValue(entityId, out var entries))
        {
            entries =
            [
                new()
                {
                    MethodName = method,
                    Script = script,
                }
            ];
            DependencyTracking[entityId] = entries;
        }
        else
        {
            if (!entries.Exists(e => e.Script == script && e.MethodName == method))
            {
                entries.Add(new()
                {
                    MethodName = method,
                    Script = script
                });
            }
        }
    }

    public Task RunEntries(List<DependencyEntry> entries) =>
        Task.WhenAll(entries.Select(async e => await RunEntry(e)));

    public Task RunEntry(DependencyEntry entry) => entry.Script.RunMethod(entry.MethodName);

    public Task InitializeUserScripts() => Task.WhenAll(_userScripts.Select(InitializeUserScript));

    async Task InitializeUserScript(UserScript userScript)
    {
        userScript.Initializing = true;
        Logger.Info($"Running Class {userScript.ClassName}");

        foreach (var method in userScript.Methods)
        {
            Logger.Info($"Running Method {method.Name}");
            await userScript.RunMethod(method);
        }

        userScript.Initializing = false;
    }

    public async Task UpdateUserScript(CompiledUserScript compiled)
    {
        // var existing = _compiledScripts.FirstOrDefault(c => c.FilePath == compiled.FilePath);
        // if (existing != null)
        // {
        //     foreach (var script in existing.Scripts)
        //     {
        //         _userScripts.Remove(script);
        //
        //         // Remove from dependency tracking
        //         foreach (var entityId in DependencyTracking.Keys.ToList())
        //         {
        //             DependencyTracking[entityId].RemoveAll(e => e.Script == script);
        //             if (DependencyTracking[entityId].Count == 0)
        //             {
        //                 // TODO: tell python we can unsub from tracking changes of this entity
        //                 DependencyTracking.Remove(entityId);
        //             }
        //         }
        //     }
        //
        //     _compiledScripts.Remove(existing);
        // }
        //
        // LoadUserScripts([compiled]);
        //
        // foreach (var script in compiled.Scripts)
        // {
        //     await InitializeUserScript(script);
        // }
    }
}
