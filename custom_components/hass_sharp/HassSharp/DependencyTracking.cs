using System.Runtime.InteropServices;

namespace HassSharp;

readonly struct DependencyEntry
{
    public required UserScriptClass ScriptClass { get; init; }
    public required string MethodName { get; init; }
}

class DependencyTracking
{
    readonly Dictionary<EntityId, List<DependencyEntry>> _dependencyTracking = new();

    public void Clear()
    {
        _dependencyTracking.Clear();
        PyInterop.UnSubscribeAllFromEntityTracking();
    }

    public void TrackEntityCall(string entityId, UserScriptClass scriptClass, string method)
    {
        ref var res = ref CollectionsMarshal.GetValueRefOrAddDefault(_dependencyTracking, entityId, out _);
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
            PyInterop.SubscribeToEntityTracking(entityId);
        }
    }

    public void RemoveScript(UserScript script)
    {
        foreach (var (entityId, entries) in _dependencyTracking)
        {
            entries.RemoveAll(e => e.ScriptClass.Script == script);
            if (entries.Count == 0)
            {
                _dependencyTracking.Remove(entityId);
                Logger.Info($"Unsubscribing from entity tracking for entity_id: {entityId}:");
                PyInterop.UnSubscribeFromEntityTracking(entityId);
            }
        }
    }

    public void Debug()
    {
        var str = _dependencyTracking.Select(tuple =>
        {
            var (entityId, entries) = tuple;
            return $"""
                    {entityId}: 
                            {string.Join(',', entries.Select(e => $"{e.ScriptClass.ClassName}:{e.MethodName}"))}
                    """;
        });
        Logger.Info($"""

                     Currently tracking:
                         {string.Join('\n', str)}
                     """);
    }

    public List<DependencyEntry> GetEntries(string entityId) => _dependencyTracking[entityId];
}
