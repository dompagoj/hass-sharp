

namespace HassSharp;

readonly struct DependencyEntry
{
    public required UserScriptClass ScriptClass { get; init; }
    public required string MethodName { get; init; }
}

class DependencyTracking
{
    readonly object _gate = new();
    readonly Dictionary<EntityId, List<DependencyEntry>> _dependencyTracking = new();

    public void Clear()
    {
        lock (_gate)
        {
            _dependencyTracking.Clear();
            PyInterop.UnSubscribeAllFromEntityTracking();
        }
    }

    public void TrackEntityCall(string entityId, UserScriptClass scriptClass, string method)
    {
        lock (_gate)
        {
            if (!_dependencyTracking.TryGetValue(entityId, out var entries))
            {
                entries = [];
                _dependencyTracking.Add(entityId, entries);
                PyInterop.SubscribeToEntityTracking(entityId);
            }
            if (!entries.Any(e => e.ScriptClass == scriptClass && e.MethodName == method))
                entries.Add(new() { ScriptClass = scriptClass, MethodName = method });
        }
    }

    public void RemoveScript(UserScript script)
    {
        List<Exception> errors = [];
        lock (_gate)
        foreach (var (entityId, entries) in _dependencyTracking.ToArray())
        {
            entries.RemoveAll(e => e.ScriptClass.Script == script);
            if (entries.Count == 0)
            {
                _dependencyTracking.Remove(entityId);
                Logger.Info($"Unsubscribing from entity tracking for entity_id: {entityId}:");
                try { PyInterop.UnSubscribeFromEntityTracking(entityId); }
                catch (Exception error) { errors.Add(error); }
            }
        }
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    public void Debug()
    {
        if (Logger.Level > PyLogLevel.Debug) return;

        var str = _dependencyTracking.Select(tuple =>
        {
            var (entityId, entries) = tuple;
            return $"""
                    {entityId}: 
                            {string.Join(',', entries.Select(e => $"{e.ScriptClass.ClassName}:{e.MethodName}"))}
                    """;
        });
        Logger.Debug($"Currently tracking: {string.Join(',', str)}");
    }

    public List<DependencyEntry> GetEntries(string entityId)
    {
        lock (_gate) return _dependencyTracking.TryGetValue(entityId, out var entries) ? [..entries] : [];
    }
}
