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
    /// succeed, or end at a Stop step, fails the prepare with its error. When this revision replaces one that runs, each
    /// entity no State works out starts with the state and availability its namesake of the same domain has there
    /// (<see cref="Carry"/>), unless it refuses that state; the When started flow may still set another.
    /// </summary>
    async Task<SharpEntity[]> PrepareGraph(JsonArray described)
    {
        graphSettings = GraphSettings(definition.Settings);
        var all = described.OfType<JsonObject>().Select(entity => SharpDomains.Create(VisualNodeCatalog.Text(entity, "domain"), entity)).ToArray();
        CheckKeys(all);
        // Read before the lock: the running revision's lock is taken alone, never inside this one.
        var carried = Replaced();
        lock (gate)
        {
            graphEntities = all.ToDictionary(entity => entity.Key, StringComparer.Ordinal);
            // Each entity a State works out is published first by its State, once a run worked it out (see StartAsync).
            awaiting.UnionWith((definition.Roots?.StateEntities.Values ?? []).Where(graphEntities.ContainsKey));
            Carry(carried);
        }
        entities = all;
        if (definition.Roots?.Setup is not { } setup) return all;
        var run = FlowClass(setup).RunToEnd(setup, InvocationCause.Setup, TriggerPayload.Setup());
        if (!await Within(run, SetupTimeout))
            throw new TimeoutException($"{definition.Name} does not start: its When started flow did not finish within {Seconds(SetupTimeout)} s");
        var outcome = await run;
        // A flow that ends at a Stop step has done what it was to do.
        if (outcome.Status is not ("Succeeded" or "Stopped"))
            throw new InvalidOperationException(outcome.Status switch
            {
                "Failed" => outcome.Error ?? $"{definition.Name} does not start: its When started flow failed",
                "Cancelled" => $"{definition.Name} does not start: its When started flow was cancelled because {Because(outcome.Error)}",
                _ => $"{definition.Name} does not start: its When started flow did not run",
            });
        return all;
    }

    /// <summary>A graph's entity as it is now, in plain values: its state values by key and its availability.</summary>
    internal sealed record GraphEntityState(string Key, string Domain, bool Available, KeyValuePair<string, object?>[] Values);

    /// <summary>
    /// The state of each of a graph's entities as it is now, for the revision that replaces this one; none unless running
    /// and not yet replaced. Only plain values (true or false, a number, text or none), so that nothing of this revision's
    /// code is held by what reads them; a value of any other kind is left out, and so is its entity. Read under the lock,
    /// where no user code runs: a graph's entities are HassSharp's own. Never throws.
    /// </summary>
    internal GraphEntityState[] GraphStates()
    {
        try
        {
            lock (gate)
            {
                if (status != Running || Ignoring) return [];
                var states = new List<GraphEntityState>();
                foreach (var entity in graphEntities.Values)
                {
                    if (!SharpDomains.Graph.TryGetValue(entity.Domain, out var domain)) continue;
                    var values = domain.State.Select(state => KeyValuePair.Create(state.Key, state.Value.Read(entity))).ToArray();
                    if (values.All(value => value.Value is null or bool or double or string)) states.Add(new(entity.Key, entity.Domain, entity.Available, values));
                }
                return [.. states];
            }
        }
        catch (Exception) { return []; }
    }

    // What the revision of this definition that runs now shows, when this host replaces it: none for a first start, one
    // turned off, failed or stopping, or a class's. Never throws.
    GraphEntityState[] Replaced()
    {
        try { return Manager.Entities.Host(definition.Id) is { } running && running != this ? running.GraphStates() : []; }
        catch (Exception) { return []; }
    }

    // Under the gate, as the entities are created: each entity no State works out takes the state and availability of its
    // namesake of the same domain in the revision this one replaces, all or nothing, when it takes every value.
    void Carry(GraphEntityState[] carried)
    {
        foreach (var was in carried)
        {
            if (awaiting.Contains(was.Key) || !graphEntities.TryGetValue(was.Key, out var entity) || entity.Domain != was.Domain
                || !SharpDomains.Graph.TryGetValue(entity.Domain, out var domain)) continue;
            if (was.Values.Any(value => !domain.State.TryGetValue(value.Key, out var state) || state.Refuses(entity, value.Value) != null)) continue;
            // A graph's entity has no host of its own, so this publishes nothing by itself.
            foreach (var (key, value) in was.Values) domain.State[key].Keep(entity, value);
            entity.Available = was.Available;
        }
    }

    /// <summary>
    /// Runs the graph's When commanded flow for the entity's command and waits for it, no longer than
    /// <see cref="CommandTimeout"/> (the flow may wait on a command to this same entity, which waits behind it); a flow
    /// still running then is left to finish. The data is checked first, as a class's is (a value the entity does not take
    /// is refused, and no flow runs), and a value of it Home Assistant did not send is the entity's state value of the
    /// same name, when it has one (<see cref="Filled"/>). Completes when the flow succeeded or ended at a Stop step;
    /// otherwise throws, in words for the person who gave the command: the flow's own error when it failed (its first
    /// line; the run's record has it whole, and <see cref="FlowFailed"/> logs it).
    /// </summary>
    /// <exception cref="ArgumentException">The entity does not take the data (<see cref="SharpEntity.CheckData"/>).</exception>
    async Task RunFlow(DefinitionRoots roots, SharpEntity entity, string name, JsonElement data)
    {
        var who = entity.Name ?? definition.Name;
        var what = CommandLabel(entity.Domain, name);
        entity.CheckData(name, data);
        if (!roots.Commands.TryGetValue(entity.Key + ":" + name, out var method)) throw new InvalidOperationException($"{who} has no flow for {what}");
        var run = FlowClass(method).RunToEnd(method, InvocationCause.Command, TriggerPayload.Command(entity.Key, name, Filled(entity, name, data)));
        using (var timer = new CancellationTokenSource())
        {
            if (await Task.WhenAny(run, Task.Delay(CommandTimeout, timer.Token)) != run)
                throw new TimeoutException($"{who}: {what} did not finish within {Seconds(CommandTimeout)} s; it goes on in the background");
            timer.Cancel();
        }
        var outcome = await run;
        switch (outcome.Status)
        {
            // A flow that ends at a Stop step has done what it was to do.
            case "Succeeded" or "Stopped": return;
            case "Failed":
                var error = outcome.Error is { Length: > 0 } text ? text : $"{who}: {what} failed";
                throw new InvalidOperationException(error.Split('\n')[0].TrimEnd('\r'));
            case "Cancelled": throw new InvalidOperationException($"{who}: {what} did not finish: its flow was cancelled because {Because(outcome.Error)}");
            default: throw new InvalidOperationException($"{who}: {what} did not run");
        }
    }

    /// <summary>
    /// The command's data with each value its <c>entityDomains</c> row names that Home Assistant did not send (absent or
    /// null) taken from the entity's state value of the same name, when it has one: a light turned on without a brightness
    /// is turned on at the one it has. The data as it was when nothing is taken. Read under the lock, as states are kept.
    /// </summary>
    JsonElement Filled(SharpEntity entity, string name, JsonElement data)
    {
        if (!CommandValues.TryGetValue((entity.Domain, name), out var names) || !SharpDomains.Graph.TryGetValue(entity.Domain, out var domain)) return data;
        JsonObject? filled = null;
        lock (gate)
            foreach (var key in names)
                if (CommandData.Given(data, key) == null && domain.State.TryGetValue(key, out var state) && state.Read(entity) is { } value)
                    (filled ??= data.ValueKind == JsonValueKind.Object ? JsonNode.Parse(data.GetRawText())!.AsObject() : new JsonObject())[key] = JsonSerializer.SerializeToNode(value);
        return filled == null ? data : JsonSerializer.SerializeToElement(filled);
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
    public void SetState(string entityKey, IReadOnlyDictionary<string, object?> values, IReadOnlySet<string> hidden)
    {
        SharpEntity entity;
        lock (gate)
        {
            if (Ignoring) return;
            entity = GraphEntity(entityKey);
            var known = SharpDomains.Graph.TryGetValue(entity.Domain, out var domain) ? domain.State : new Dictionary<string, GraphState>();
            // Every value is checked before any is kept: a refused call changes nothing. A secret value is not quoted, nor
            // is a password text's, as its class words it.
            foreach (var (key, value) in values)
                if (!known.TryGetValue(key, out var state)) throw new InvalidOperationException($"{definition.Name}: a {entity.Domain} has no state {key}, so entity {entityKey} cannot take it");
                else if (state.Refuses(entity, value) is { } refused)
                    throw new InvalidOperationException(hidden.Contains(key)
                        ? $"The {key} of {Named(entityKey)} {refused}; the value is hidden because it comes from a secret setting"
                        : entity is SharpText { Mode: SharpTextMode.Password } ? $"The hidden value given for {Named(entityKey)} {refused}"
                        : $"The {key} {Shown(value)} of {Named(entityKey)} {refused}");
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
    /// take), leaves the entity unavailable. The first failure warns, naming the entity and why (the failing node and its
    /// step, when the visual runtime named them), and then logs the whole failure at Debug; a failure for the same reason
    /// again says nothing and costs nothing more than comparing the reason, one for another reason warns again; a success
    /// after a failure says, once, that the entity has a state again. A cancelled run says nothing. Ignored once the
    /// definition stops or is replaced. Never throws.
    /// </summary>
    /// <param name="recorded">The run record's error, for a failure the runner did not catch itself.</param>
    /// <param name="failure">What the run threw, when it failed.</param>
    internal void StateEnded(string method, string outcome, string? recorded, Exception? failure)
    {
        try
        {
            if (definition.Roots?.StateEntities.TryGetValue(method, out var key) != true) return;
            // Worked out before the lock: an exception's members are user code.
            string? reason = null;
            if (outcome == "Failed") try { reason = Reason(failure); } catch { }
            string? message = null;
            var detail = false;
            var level = PyLogLevel.Warn;
            SharpEntity? unavailable = null;
            lock (gate)
            {
                // A run of a revision stopping or replaced fails for that reason, which is no news about its entity.
                if (Ignoring) return;
                if (outcome == "Failed")
                {
                    var why = reason ?? (recorded is { Length: > 0 } text ? text : "its State failed");
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
                        detail = failure != null;
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
            if (message != null) Report(message, level);
            // The whole failure, once per reason and after its warning: ToString() is user code, and builds the stack.
            if (detail)
            {
                string whole;
                try { whole = failure!.ToString(); } catch { whole = failure!.GetType().FullName ?? "an exception"; }
                Report($"the State of {Named(key!)} failed: {whole}", PyLogLevel.Debug);
            }
        }
        catch { }
    }

    /// <summary>
    /// How a run of one of the graph's other flows failed, which the runner leaves to the host: a When commanded flow, the
    /// When started flow, or a trigger's flow. Logs one line naming the definition, the flow and why (as a State's
    /// warning says why), with no stack: a command's at warning, as whoever gave it is answered the error and Home
    /// Assistant logs it too; the When started flow's and a trigger flow's at error. Then the whole failure at Debug.
    /// Takes no lock and keeps nothing of the failure. Never throws.
    /// </summary>
    /// <param name="method">The flow's method.</param>
    /// <param name="cause">What started the run.</param>
    /// <param name="failure">What the run threw.</param>
    internal void FlowFailed(string method, InvocationCause cause, Exception failure)
    {
        try
        {
            // Worked out first: an exception's members are user code.
            string reason;
            try { reason = Reason(failure) ?? failure.GetType().Name; } catch { reason = "it failed"; }
            var level = PyLogLevel.Error;
            string flow;
            var roots = definition.Roots;
            if (roots?.Commands.FirstOrDefault(command => command.Value == method).Key is { } handled)
            {
                // Keyed <entity key>:<command>; an entity key has no colon.
                var colon = handled.IndexOf(':');
                var key = handled[..colon];
                var domain = definition.Entities?.OfType<JsonObject>().FirstOrDefault(e => VisualNodeCatalog.Text(e, "key") == key) is { } entity ? VisualNodeCatalog.Text(entity, "domain") : "";
                flow = $"{Named(key)}: {CommandLabel(domain, handled[(colon + 1)..])}";
                level = PyLogLevel.Warn;
            }
            else if (roots?.Setup == method || cause == InvocationCause.Setup) flow = "When started";
            else flow = script.CompiledScript.VisualTriggers?.GetValueOrDefault(method) is { } trigger ? trigger.Label ?? $"Trigger ({trigger.NodeId})" : method;
            Report($"{flow} failed: {reason}", level);
            // The whole failure after its line: ToString() is user code, and builds the stack.
            string whole;
            try { whole = failure.ToString(); } catch { whole = failure.GetType().FullName ?? "an exception"; }
            Report($"{flow} failed: {whole}", PyLogLevel.Debug);
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

    /// <summary>The names of the data values of each domain's commands that have some, as the catalog's <c>entityDomains</c> gives them.</summary>
    static readonly IReadOnlyDictionary<(string Domain, string Command), string[]> CommandValues =
        (VisualNodeCatalog.Root["entityDomains"] as JsonObject ?? []).SelectMany(domain => (domain.Value?["commands"] as JsonObject ?? [])
            .Where(command => command.Value?["data"] is JsonObject { Count: > 0 })
            .Select(command => KeyValuePair.Create((domain.Key, command.Key), ((JsonObject)command.Value!["data"]!).Select(value => value.Key).ToArray())))
        .ToDictionary();

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
