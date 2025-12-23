using System.Reflection;

namespace HassSharp;

public readonly struct DependencyEntry
{
    public required UserScript Script { get; init; }
    public required string MethodName { get; init; }
}

class UserScriptManager
{
    readonly HashSet<UserScript> _userScripts = new(new UserScriptComparer());

    // This is iterator on the python side which calles async_track_state_change_event from hass on each key
    public Dictionary<string, List<DependencyEntry>> DependencyTracking { get; } = new();

    public void ClearUserScripts() => _userScripts.Clear();

    public void LoadUserScripts(Assembly[] assemblies)
    {
        foreach (var assembly in assemblies) LoadUserScripts(assembly);
    }

    public void LoadUserScripts(Assembly assembly)
    {
        var baseType = typeof(Automation);

        foreach (var automationClass in assembly.GetTypes()
                     .Where(t => baseType.IsAssignableFrom(t) && !t.IsAbstract))
        {
            var userScript = new UserScript(automationClass, this);
            _userScripts.Add(userScript);
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

    public ValueTask RunEntry(DependencyEntry entry) => entry.Script.RunMethod(entry.MethodName);

    public async Task InitializeUserScripts()
    {
        foreach (var userScript in _userScripts)
        {
            Logger.Info($"Running Class {userScript.ClassName}");

            foreach (var method in userScript.Methods)
            {
                Logger.Info($"Running Method {method.Name}");
                await userScript.RunMethod(method.Name);
                userScript.Initializing = false;
            }
        }
    }
}
