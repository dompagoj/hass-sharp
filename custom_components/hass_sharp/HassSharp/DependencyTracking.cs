

namespace HassSharp;

readonly struct DependencyEntry
{
    public required UserScriptClass ScriptClass { get; init; }
    public required string MethodName { get; init; }
}

/// <summary>A callback a script follows an entity with, such as a custom entity's <c>Track</c>.</summary>
sealed class DelegateEntry(UserScript owner, Func<TriggerContext, Task> action)
{
    public UserScript Owner { get; } = owner;
    public Func<TriggerContext, Task> Action { get; } = action;
}

class DependencyTracking
{
    readonly Lock _gate = new();
    readonly Dictionary<EntityId, List<DependencyEntry>> _dependencyTracking = new();
    // An entity is subscribed while either list has entries for it.
    readonly Dictionary<EntityId, List<DelegateEntry>> _delegates = new();

    public void Clear()
    {
        lock (_gate)
        {
            _dependencyTracking.Clear();
            _delegates.Clear();
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
                if (!_delegates.ContainsKey(entityId)) PyInterop.SubscribeToEntityTracking(entityId);
            }
            if (!entries.Any(e => e.ScriptClass == scriptClass && e.MethodName == method))
                entries.Add(new() { ScriptClass = scriptClass, MethodName = method });
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> with each change of the entity while <paramref name="owner"/> is active, until the
    /// returned handle is disposed or the owner is removed; the first follower of an entity subscribes to it.
    /// </summary>
    public IDisposable TrackDelegate(string entityId, UserScript owner, Func<TriggerContext, Task> action)
    {
        var entry = new DelegateEntry(owner, action);
        lock (_gate)
        {
            if (!_delegates.TryGetValue(entityId, out var entries))
            {
                if (!_dependencyTracking.ContainsKey(entityId)) PyInterop.SubscribeToEntityTracking(entityId);
                _delegates.Add(entityId, entries = []);
            }
            entries.Add(entry);
        }
        return new Untrack(this, entityId, entry);
    }

    sealed class Untrack(DependencyTracking tracking, string entityId, DelegateEntry entry) : IDisposable
    {
        int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (tracking._gate)
            {
                if (!tracking._delegates.TryGetValue(entityId, out var entries) || !entries.Remove(entry) || entries.Count > 0) return;
                tracking._delegates.Remove(entityId);
                if (!tracking._dependencyTracking.ContainsKey(entityId)) tracking.Unsubscribe(entityId);
            }
        }
    }

    void Unsubscribe(string entityId)
    {
        Logger.Info($"Unsubscribing from entity tracking for entity_id: {entityId}:");
        PyInterop.UnSubscribeFromEntityTracking(entityId);
    }

    public void RemoveScript(UserScript script)
    {
        List<Exception> errors = [];
        lock (_gate)
        {
            var emptied = new List<EntityId>();
            foreach (var (entityId, entries) in _dependencyTracking.ToArray())
            {
                entries.RemoveAll(e => e.ScriptClass.Script == script);
                if (entries.Count == 0) { _dependencyTracking.Remove(entityId); emptied.Add(entityId); }
            }
            foreach (var (entityId, entries) in _delegates.ToArray())
            {
                if (entries.RemoveAll(e => e.Owner == script) == 0 || entries.Count > 0) continue;
                _delegates.Remove(entityId);
                emptied.Add(entityId);
            }
            // Unsubscribed once neither list follows the entity any more.
            foreach (var entityId in emptied.Distinct())
                if (!_dependencyTracking.ContainsKey(entityId) && !_delegates.ContainsKey(entityId))
                    try { Unsubscribe(entityId); }
                    catch (Exception error) { errors.Add(error); }
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

    public List<DelegateEntry> GetDelegates(string entityId)
    {
        lock (_gate) return _delegates.TryGetValue(entityId, out var entries) ? [..entries] : [];
    }
}
