using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>
/// The part of <see cref="DefinitionHost"/> only a definition built from nodes uses: its entities, created from the
/// document, its When started and When commanded flows, and what its graph's runs do to it (<see cref="IDefinitionRuntime"/>).
/// </summary>
internal sealed partial class DefinitionHost
{
    /// <summary>How long a command waits for the flow of a definition built from nodes that handles it; settable for tests.</summary>
    internal static TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

    // A graph's entities by key; empty for a class, and once stopped.
    Dictionary<string, SharpEntity> graphEntities = new(StringComparer.Ordinal);
    // The keys of a graph's entities a State works out and no run has given a state or availability yet: the host does
    // not publish them as it starts, so their first state is the one worked out, never an empty one before it.
    readonly HashSet<string> awaiting = new(StringComparer.Ordinal);
    // Why each failing State's entity is unavailable, by key, as last warned.
    readonly Dictionary<string, string> failing = new(StringComparer.Ordinal);
    // A graph's settings by key, as its Setting nodes read them; read from the definition as it prepares, before any run.
    volatile IReadOnlyDictionary<string, (object? Value, bool Secret)> graphSettings = new Dictionary<string, (object?, bool)>();

    /// <summary>
    /// A definition built from nodes: creates the entities its document describes, then runs its When started flow, if it
    /// has one, within <see cref="SetupTimeout"/>; the states that flow sets are the entities' first. A flow that does not
    /// succeed fails the prepare with its error.
    /// </summary>
    async Task<SharpEntity[]> PrepareGraph(JsonArray described)
    {
        graphSettings = GraphSettings(definition.Settings);
        var all = described.OfType<JsonObject>().Select(entity => SharpDomains.Create(VisualNodeCatalog.Text(entity, "domain"), entity)).ToArray();
        CheckKeys(all);
        lock (gate)
        {
            graphEntities = all.ToDictionary(entity => entity.Key, StringComparer.Ordinal);
            // Each entity a State works out is published first by its State, once a run worked it out (see StartAsync).
            awaiting.UnionWith((definition.Roots?.StateEntities.Values ?? []).Where(graphEntities.ContainsKey));
        }
        entities = all;
        if (definition.Roots?.Setup is not { } setup) return all;
        var run = FlowClass(setup).RunToEnd(setup, InvocationCause.Setup, TriggerPayload.Setup());
        if (!await Within(run, SetupTimeout))
            throw new TimeoutException($"{definition.Name} does not start: its When started flow did not finish within {Seconds(SetupTimeout)} s");
        var outcome = await run;
        if (outcome.Status != "Succeeded")
            throw new InvalidOperationException(outcome.Status switch
            {
                "Failed" => outcome.Error ?? $"{definition.Name} does not start: its When started flow failed",
                "Stopped" => $"{definition.Name} does not start: its When started flow stopped itself at a Stop step before it finished",
                "Cancelled" => $"{definition.Name} does not start: its When started flow was cancelled because {Because(outcome.Error)}",
                _ => $"{definition.Name} does not start: its When started flow did not run",
            });
        return all;
    }

    /// <summary>
    /// Runs the graph's When commanded flow for the entity's command and waits for it, no longer than
    /// <see cref="CommandTimeout"/> (the flow may wait on a command to this same entity, which waits behind it); a flow
    /// still running then is left to finish. Completes when the flow succeeded; otherwise throws, in words for the person
    /// who gave the command: the flow's own error when it failed (its first line; the run's log and record have it whole).
    /// </summary>
    async Task RunFlow(DefinitionRoots roots, SharpEntity entity, string name, JsonElement data)
    {
        var who = entity.Name ?? definition.Name;
        var what = CommandLabel(entity.Domain, name);
        if (!roots.Commands.TryGetValue(entity.Key + ":" + name, out var method)) throw new InvalidOperationException($"{who} has no flow for {what}");
        var run = FlowClass(method).RunToEnd(method, InvocationCause.Command, TriggerPayload.Command(entity.Key, name, data));
        using (var timer = new CancellationTokenSource())
        {
            if (await Task.WhenAny(run, Task.Delay(CommandTimeout, timer.Token)) != run)
                throw new TimeoutException($"{who}: {what} did not finish within {Seconds(CommandTimeout)} s; it goes on in the background");
            timer.Cancel();
        }
        var outcome = await run;
        switch (outcome.Status)
        {
            case "Succeeded": return;
            case "Failed":
                var error = outcome.Error is { Length: > 0 } text ? text : $"{who}: {what} failed";
                throw new InvalidOperationException(error.Split('\n')[0].TrimEnd('\r'));
            case "Stopped": throw new InvalidOperationException($"{who}: {what} did not finish: its flow stopped itself at a Stop step");
            case "Cancelled": throw new InvalidOperationException($"{who}: {what} did not finish: its flow was cancelled because {Because(outcome.Error)}");
            default: throw new InvalidOperationException($"{who}: {what} did not run");
        }
    }

    /// <summary>
    /// Why a flow of the definition was cancelled, in words for a definition: the runner's reasons are worded for an
    /// automation, and stay so for automations.
    /// </summary>
    static string Because(string? reason) => reason switch
    {
        CancelReasons.Reloaded => "the definition was reloaded",
        CancelReasons.TurnedOff => "the definition was turned off",
        CancelReasons.Stopping => "Home Assistant is stopping",
        CancelReasons.Restarted => "a newer run took its place",
        _ => "the definition stopped",
    };

    /// <summary>The script's class whose method <paramref name="method"/> is, the graph's generated class.</summary>
    UserScriptClass FlowClass(string method) => script.Classes.FirstOrDefault(klass => klass.Methods.Any(m => m.Name == method))
        ?? throw new InvalidOperationException($"{definition.Name}: the compiled graph has no flow {method}");

    /// <summary>What the editor calls a command of a domain, such as <c>Turn on</c>; the command itself when the catalog has no label.</summary>
    static string CommandLabel(string domain, string command) =>
        CommandLabels.TryGetValue(domain, out var known) && known.TryGetValue(command, out var label) ? label : command;

    // ---- A graph's runs act on the definition through its host ----

    // A run of a revision stopping, stopped or replaced (its script is invalidated as it is swapped out) changes nothing.
    bool Ignoring => stopped != null || script.Invalidated;

    /// <inheritdoc/>
    /// <remarks>
    /// Kept on the entity, and published once the definition runs: a state a run sets while the definition prepares or
    /// starts is the entity's first. Ignored once the definition stops or is replaced. Thread-safe; never calls into a run.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The definition has no such entity, its domain no such state, or the entity does not take a value (<see cref="GraphState.Refuses"/>).</exception>
    public void SetState(string entityKey, IReadOnlyDictionary<string, object?> values)
    {
        SharpEntity entity;
        lock (gate)
        {
            if (Ignoring) return;
            entity = GraphEntity(entityKey);
            var known = SharpDomains.Graph.TryGetValue(entity.Domain, out var domain) ? domain.State : new Dictionary<string, GraphState>();
            // Every value is checked before any is kept: a refused call changes nothing.
            foreach (var (key, value) in values)
                if (!known.TryGetValue(key, out var state)) throw new InvalidOperationException($"{definition.Name}: a {entity.Domain} has no state {key}, so entity {entityKey} cannot take it");
                else if (state.Refuses(entity, value) is { } refused) throw new InvalidOperationException($"The {key} {Shown(value)} does not fit {Named(entityKey)}, {refused}");
            foreach (var (key, value) in values) known[key].Keep(entity, value);
            awaiting.Remove(entityKey);
        }
        // Outside the lock: a publish that fails is logged, which calls into Python.
        Publish(entity);
    }

    /// <inheritdoc/>
    /// <remarks>Kept and published as <see cref="SetState"/> is; ignored once the definition stops or is replaced.</remarks>
    /// <exception cref="InvalidOperationException">The definition has no such entity.</exception>
    public void SetAvailable(string entityKey, bool available)
    {
        SharpEntity entity;
        lock (gate)
        {
            if (Ignoring) return;
            entity = GraphEntity(entityKey);
            // A graph's entity has no host of its own, so this publishes nothing by itself.
            entity.Available = available;
            awaiting.Remove(entityKey);
        }
        Publish(entity);
    }

    /// <inheritdoc/>
    public object? Setting(string key) =>
        graphSettings.TryGetValue(key, out var setting) ? setting.Value : throw new KeyNotFoundException($"{definition.Name} has no setting {key}");

    /// <inheritdoc/>
    public bool IsSecret(string key) => graphSettings.TryGetValue(key, out var setting) && setting.Secret;

    /// <summary>Whether <paramref name="method"/> is one of a graph's States, whose runs end in <see cref="StateEnded"/>.</summary>
    internal bool IsState(string method) => definition.Roots?.StateEntities.ContainsKey(method) == true;

    /// <summary>
    /// How a run of a State ended, which the runner does not log: Home Assistant's entities come and go as it starts, so a
    /// State may fail on every change for a while. A failed run, whatever failed (an input, or a value the entity does not
    /// take), leaves the entity unavailable, and its whole failure is logged at Debug. The first failure warns, naming the
    /// entity and why (the failing node and its step, when the visual runtime named them); a failure for the same reason
    /// again says nothing, one for another reason warns again; a success after a failure says, once, that the entity has a
    /// state again. A cancelled run says nothing. Ignored once the definition stops or is replaced. Never throws.
    /// </summary>
    /// <param name="recorded">The run record's error, for a failure the runner did not catch itself.</param>
    /// <param name="failure">What the run threw, when it failed.</param>
    internal void StateEnded(string method, string outcome, string? recorded, Exception? failure)
    {
        try
        {
            if (definition.Roots?.StateEntities.TryGetValue(method, out var key) != true) return;
            string? message = null;
            var level = PyLogLevel.Warn;
            SharpEntity? unavailable = null;
            lock (gate)
            {
                // A run of a revision stopping or replaced fails for that reason, which is no news about its entity.
                if (Ignoring) return;
                if (outcome == "Failed")
                {
                    var why = Reason(failure) ?? (recorded is { Length: > 0 } text ? text : "its State failed");
                    if (graphEntities.TryGetValue(key!, out var entity) && entity.Available)
                    {
                        // A graph's entity has no host of its own, so this publishes nothing by itself.
                        entity.Available = false;
                        unavailable = entity;
                    }
                    awaiting.Remove(key!);
                    if (!failing.TryGetValue(key!, out var last) || last != why)
                    {
                        failing[key!] = why;
                        message = $"{Named(key!)} is unavailable: {why}";
                    }
                }
                else if (outcome == "Succeeded" && failing.Remove(key!))
                {
                    message = $"{Named(key!)} has a state again";
                    level = PyLogLevel.Info;
                }
            }
            // Outside the lock: publishing and logging call into Python.
            if (unavailable != null) Publish(unavailable);
            if (failure != null) Report($"the State of {Named(key!)} failed: {failure}", PyLogLevel.Debug);
            if (message != null) Report(message, level);
        }
        catch { }
    }

    // What a State's failure says, as the person reads it: the message of what the run threw, past any wrapper of the
    // runner's, which names the failing node and the step that used it when the visual runtime wrapped it; then, when it
    // does not say it already, the message of the failure underneath (such as the text a number could not be read from).
    // Each its first line.
    static string? Reason(Exception? failure)
    {
        static string First(Exception error) => Text(error).Split('\n')[0].TrimEnd('\r');
        var shown = failure;
        while (shown is System.Reflection.TargetInvocationException or AggregateException && shown.InnerException != null) shown = shown.InnerException;
        if (shown == null || First(shown) is not { Length: > 0 } outer) return null;
        var cause = First(shown.GetBaseException());
        return cause.Length == 0 || outer.Contains(cause, StringComparison.Ordinal) ? outer : $"{outer} ({cause})";
    }

    // A value as a refusal names it.
    static string Shown(object? value) => value switch
    {
        null => "empty",
        string text => $"\"{text}\"",
        bool flag => flag ? "true" : "false",
        double number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => value.GetType().Name,
    };

    // An entity as a message names it: its name and key, or its key alone.
    string Named(string key) =>
        definition.Entities?.OfType<JsonObject>().FirstOrDefault(e => VisualNodeCatalog.Text(e, "key") == key) is { } described && VisualNodeCatalog.Text(described, "name") is { Length: > 0 } name
            ? $"{name} ({key})" : key;

    // Under the gate.
    SharpEntity GraphEntity(string key) => graphEntities.TryGetValue(key, out var entity) ? entity
        : throw new InvalidOperationException($"{definition.Name} has no entity {key}");

    /// <summary>The command labels of each entity domain, as the catalog's <c>entityDomains</c> gives them.</summary>
    static readonly IReadOnlyDictionary<string, Dictionary<string, string>> CommandLabels = ReadCommandLabels();

    static IReadOnlyDictionary<string, Dictionary<string, string>> ReadCommandLabels()
    {
        var domains = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (domain, spec) in VisualNodeCatalog.Root["entityDomains"] as JsonObject ?? [])
            domains[domain] = (spec?["commands"] as JsonObject ?? []).ToDictionary(command => command.Key, command => VisualNodeCatalog.Text(command.Value, "label", command.Key), StringComparer.Ordinal);
        return domains;
    }

    // A graph's settings as its Setting nodes read them: a text, true or false, or a number; null for no value, and for a
    // value of any other kind. Read once, so that the runs read them without a lock. Never throws.
    internal static IReadOnlyDictionary<string, (object? Value, bool Secret)> GraphSettings(IReadOnlyList<DefinitionSetting> definitionSettings)
    {
        var settings = new Dictionary<string, (object?, bool)>(StringComparer.Ordinal);
        foreach (var setting in definitionSettings) settings.TryAdd(setting.Key, (SettingValue(setting.Value), setting.Secret));
        return settings;
    }

    // Whatever backs the JSON value (a parsed element, or a number or text put in by code), as a run reads it.
    static object? SettingValue(JsonNode? node)
    {
        try
        {
            if (node is not JsonValue value) return null;
            var element = value.Deserialize<JsonElement>();
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when element.TryGetDouble(out var number) && double.IsFinite(number) => number,
                _ => null,
            };
        }
        catch (Exception) { return null; }
    }
}
