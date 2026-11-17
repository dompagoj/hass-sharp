using System.Text.Json.Nodes;
using HassSharp;

/// <summary>
/// What the entity suites run a whole manager in: a temporary configuration directory, and recording fakes in place of
/// Python's four entity delegates (sync, push, remove, prune) and its log. Disposing puts back what it replaced and
/// deletes the directory; unload the managers first.
/// </summary>
sealed class EntityTestHome : IAsyncDisposable
{
    readonly List<string> synced = [], pushed = [], removed = [], pruned = [];
    readonly List<(int Level, string Message)> logged = [];
    readonly string previousPath = HassPath.HassConfiguration;
    readonly Action<string, Action<string>, Action<string>> sync = PyInterop.BeginSyncEntities;
    readonly Action<string> push = PyInterop.PushEntityStates;
    readonly Action<string, Action, Action<string>> remove = PyInterop.BeginRemoveEntities, prune = PyInterop.BeginPruneEntities;
    readonly Action<int, string> log = PyInterop.Log;
    readonly TimeSpan removalTimeout = HassSharpManager.RemovalTimeout, setupTimeout = DefinitionHost.SetupTimeout, syncTimeout = DefinitionHost.SyncTimeout;
    volatile bool failRemoval, ignoreRemoval, failPrune, ignorePrune;

    /// <summary>A new directory under the temporary folder, named after the suite; the fakes are installed from now on.</summary>
    public EntityTestHome(string name)
    {
        Root = Path.Combine(Path.GetTempPath(), name + "-" + Guid.NewGuid());
        PyInterop.BeginSyncEntities = (json, ok, _) => { lock (synced) synced.Add(json); ok(Answer(json)); };
        PyInterop.PushEntityStates = batch => { lock (pushed) pushed.Add(batch); };
        PyInterop.BeginRemoveEntities = (id, ok, fail) =>
        {
            lock (removed) removed.Add(id);
            if (ignoreRemoval) return;
            if (failRemoval) fail("the registry is busy"); else ok();
        };
        PyInterop.BeginPruneEntities = (ids, ok, fail) =>
        {
            lock (pruned) pruned.Add(ids);
            if (ignorePrune) return;
            if (failPrune) fail("the registry is locked"); else ok();
        };
        PyInterop.Log = (level, message) => { lock (logged) logged.Add((level, message)); };
        HassPath.HassConfiguration = Root;
    }

    /// <summary>The configuration directory a manager is made over.</summary>
    public string Root { get; }
    /// <summary>A removal answers with an error, as Python's might.</summary>
    public bool FailRemoval { get => failRemoval; set => failRemoval = value; }
    /// <summary>A removal is never answered.</summary>
    public bool IgnoreRemoval { get => ignoreRemoval; set => ignoreRemoval = value; }
    /// <summary>A prune answers with an error.</summary>
    public bool FailPrune { get => failPrune; set => failPrune = value; }
    /// <summary>A prune is never answered.</summary>
    public bool IgnorePrune { get => ignorePrune; set => ignorePrune = value; }

    /// <summary>Every sync of the definition sent so far, as JSON.</summary>
    public string[] Syncs(string definitionId)
    {
        lock (synced) return synced.Where(json => JsonNode.Parse(json)!["definitionId"]!.GetValue<string>() == definitionId).ToArray();
    }
    /// <summary>Every batch of states pushed so far, as JSON arrays.</summary>
    public string[] Pushes() { lock (pushed) return [.. pushed]; }
    /// <summary>The definition ids whose entities were asked to be removed.</summary>
    public string[] Removals() { lock (removed) return [.. removed]; }
    /// <summary>The JSON arrays of definition ids each prune kept.</summary>
    public string[] Prunes() { lock (pruned) return [.. pruned]; }
    /// <summary>Every log line so far.</summary>
    public string[] Logs() { lock (logged) return logged.Select(line => line.Message).ToArray(); }
    /// <summary>Every log line so far at the given level.</summary>
    public string[] Logs(PyLogLevel level) { lock (logged) return logged.Where(line => line.Level == (int)level).Select(line => line.Message).ToArray(); }

    /// <summary>Every snapshot pushed in the batches from the given one on, in order.</summary>
    public JsonObject[] Snapshots(int fromBatch = 0) =>
        Pushes().Skip(fromBatch).SelectMany(batch => JsonNode.Parse(batch)!.AsArray()).Select(node => node!.AsObject()).ToArray();

    /// <summary>What every <c>NodeLib.FakePlug</c> instance's handlers recorded, from every compiled assembly still loaded.</summary>
    public static string[] Commands() => AppDomain.CurrentDomain.GetAssemblies()
        .Select(assembly => assembly.GetType("NodeLib.FakePlug", false)).OfType<Type>()
        .SelectMany(type => (IEnumerable<string>)type.GetField("Calls")!.GetValue(null)!).ToArray();

    /// <summary>
    /// Empties what every <c>NodeLib.FakePlug</c> still loaded recorded, so a count after it holds only what ran since,
    /// whether or not an unloaded assembly has been collected meanwhile (a collected one takes its calls with it).
    /// </summary>
    public static void ClearCommands()
    {
        foreach (var type in AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("NodeLib.FakePlug", false)).OfType<Type>())
            ((System.Collections.Concurrent.ConcurrentQueue<string>)type.GetField("Calls")!.GetValue(null)!).Clear();
    }

    // Python's answer to a sync: an entity id for each unique id, as "<domain>.<definition>_<key>".
    static string Answer(string json)
    {
        var definition = JsonNode.Parse(json)!;
        var entities = new JsonObject();
        foreach (var entity in definition["entities"]!.AsArray())
            entities[entity!["uniqueId"]!.GetValue<string>()] = new JsonObject { ["entityId"] = $"{entity["domain"]}.{definition["definitionId"]}_{entity["key"]}" };
        return new JsonObject { ["entities"] = entities }.ToJsonString();
    }

    /// <summary>Waits up to five seconds for the condition, then fails naming what it waited for.</summary>
    public static async Task Until(Func<bool> condition, string what)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed >= TimeSpan.FromSeconds(5)) throw new Exception("timed out waiting: " + what);
            await Task.Delay(5);
        }
    }

    /// <summary>Puts back the delegates, the log, the configuration path and the timeouts, and deletes the directory.</summary>
    public async ValueTask DisposeAsync()
    {
        PyInterop.BeginSyncEntities = sync;
        PyInterop.PushEntityStates = push;
        PyInterop.BeginRemoveEntities = remove;
        PyInterop.BeginPruneEntities = prune;
        PyInterop.Log = log;
        HassPath.HassConfiguration = previousPath;
        HassSharpManager.RemovalTimeout = removalTimeout;
        DefinitionHost.SetupTimeout = setupTimeout;
        DefinitionHost.SyncTimeout = syncTimeout;
        if (Directory.Exists(Root)) await ServiceCallTests.DeleteWhenQuiet(Root);
    }
}
