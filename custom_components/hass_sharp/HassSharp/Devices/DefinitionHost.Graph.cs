using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>
/// The part of <see cref="DefinitionHost"/> that prepares a definition from its graph and runs what the graph does: the
/// entities its Entity nodes describe, the classes of its C# entity and C# device nodes, the states a revision takes over
/// from the one it replaces, its When started flow, its Entity nodes' flows, and what its graph's runs do to it
/// (<see cref="IDefinitionRuntime"/>).
/// </summary>
internal sealed partial class DefinitionHost
{
    /// <summary>How long a command waits for the Entity node's flow that handles it; settable for tests.</summary>
    internal static TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>How long a run of a state flow may take before the host warns that it has not finished; settable for tests.</summary>
    internal static TimeSpan SlowStateWarning { get; set; } = TimeSpan.FromSeconds(30);

    // A graph's entities by key; empty once stopped.
    Dictionary<string, SharpEntity> graphEntities = new(StringComparer.Ordinal);
    // The keys of a graph's entities a state flow works out and no run has given a state or availability yet: the host does
    // not publish them as it starts, so their first state is the one worked out, never an empty one before it.
    readonly HashSet<string> awaiting = new(StringComparer.Ordinal);
    // Why each failing state flow's entity is unavailable, by key, as last warned.
    readonly Dictionary<string, string> failing = new(StringComparer.Ordinal);
    // Why each trigger flow last failed, by method, as last logged.
    readonly Dictionary<string, FailureLog> failingFlows = new(StringComparer.Ordinal);
    // A graph's frame flows by the key of their camera: each runs before a request for that camera's picture is answered.
    readonly Dictionary<string, Func<Task>> frameFlows = new(StringComparer.Ordinal);
    // Why each frame flow's run last failed, by method, as FlowFailed worded it, until RunFrame takes it for its camera.
    readonly Dictionary<string, (string Reason, Exception Failure)> frameFailures = new(StringComparer.Ordinal);
    // A graph's settings by key, as its Setting nodes read them; read from the definition as it prepares, before any run.
    volatile IReadOnlyDictionary<string, (object? Value, bool Secret)> graphSettings = new Dictionary<string, (object?, bool)>();
    // The keys of a graph's entities a state flow works out.
    readonly HashSet<string> stateKeys = new(definition.Roots?.StateEntities.Values ?? [], StringComparer.Ordinal);
    // The revision preparing to replace this running one, which gets what changes meanwhile when this one stops.
    DefinitionHost? successor;
    // The revision this one prepares to replace, until it has stopped; read and written under this host's gate only.
    DefinitionHost? predecessor;
    // What that revision showed as this one was created, and the state values this one's runs set since: what changes
    // there meanwhile reaches this one unless it set that value itself (see Inherit).
    GraphEntityState[] handed = [];
    readonly HashSet<(string Entity, string State)> own = [];

    /// <summary>
    /// Prepares the definition: creates the entities its Entity nodes describe, then runs the <c>[NodeSetup]</c> of each
    /// node library class its graph uses (<see cref="RunNodeSetups"/>), then sets up its C# nodes' classes
    /// (<see cref="SetUpComponents"/>), both on one clock within <see cref="SetupTimeout"/>, and checks that every entity
    /// has a unique id of its own, then runs its When started flow, if it has one, within <see cref="SetupTimeout"/>; the
    /// states that flow sets are the entities' first. A flow that does not succeed, or end at a Stop step, fails the
    /// prepare with its error. When this revision replaces one that runs, each entity no state flow works out starts with
    /// the state, and availability, its namesake of the same domain has there (<see cref="Carry"/>), less each value it
    /// refuses; one that gets nothing from there takes the state values it last showed (<see cref="EntityStateStore"/>),
    /// less each it refuses. The When started flow may still set another. Each frame flow is what a request for its
    /// camera's picture runs first (<see cref="RunFrame"/>).
    /// </summary>
    async Task<SharpEntity[]> PrepareGraph()
    {
        graphSettings = GraphSettings(definition.Settings);
        var hubs = definition.Entities.OfType<JsonObject>().Select(entity => SharpDomains.Create(VisualNodeCatalog.Text(entity, "domain"), entity)).ToArray();
        CheckKeys(hubs);
        var all = hubs;
        if (definition.NodeSetups.Count > 0 || definition.Components.Count > 0)
        {
            try
            {
                await RunNodeSetups();
                if (definition.Components.Count > 0) all = [.. hubs, .. await SetUpComponents()];
            }
            // Also after a step that failed or ran out of time: a download it left running no longer stops a clock.
            finally { setupClock = null; }
            // One unique id each, over the Entity nodes' entities and the C# nodes' together.
            if (definition.Components.Count > 0) CheckKeys(all);
        }
        // Read before the lock: the running revision's lock is taken alone, never inside this one.
        var carried = Replaced();
        var stored = Manager.States is { } states
            ? hubs.Where(Restorable).Select(entity => (Entity: entity, Found: states.TryGet(UniqueId(entity), out var values), Values: values)).Where(s => s.Found).ToArray()
            : [];
        List<string> dropped = [];
        lock (gate)
        {
            graphEntities = hubs.ToDictionary(entity => entity.Key, StringComparer.Ordinal);
            // Each entity a state flow works out is published first by its state flow, once a run worked it out (see StartAsync).
            awaiting.UnionWith(stateKeys.Where(graphEntities.ContainsKey));
            handed = carried;
            var taken = Carry(carried, dropped);
            // An entity whose key the running revision gives another domain is another entity, which has no last state here.
            taken.UnionWith(carried.Where(was => graphEntities.TryGetValue(was.Key, out var entity) && entity.Domain != was.Domain).Select(was => was.Key));
            foreach (var (entity, _, values) in stored)
                if (!taken.Contains(entity.Key))
                    // A value stored as none restores nothing.
                    Take(entity, values.Where(value => value.Value != null).Select(value => KeyValuePair.Create(value.Key, Plain(value.Value))), "its last state", dropped);
        }
        // Outside the lock: logging calls into Python.
        foreach (var line in dropped) Report(line, PyLogLevel.Debug);
        lifecycle = [(null, hubs), .. parts.Select(part => (part.Root as SharpDevice, part.Entities))];
        entities = all;
        foreach (var (key, method) in definition.Roots?.Frames ?? new Dictionary<string, string>())
            FrameNeeded(key, () => RunFrame(method));
        if (definition.Roots?.Setup is not { } setup) return all;
        var clock = setupClock = new SetupClock();
        setupFailure = null;
        bool ended;
        Task<RunOutcome> run;
        try
        {
            run = FlowClass(setup).RunToEnd(setup, InvocationCause.Setup, TriggerPayload.Setup());
            ended = await Within(run, clock);
        }
        finally { setupClock = null; }
        if (!ended)
            throw new TimeoutException($"{definition.Name} does not start: its When started flow did not finish within {Seconds(SetupTimeout)} s");
        var outcome = await run;
        // A flow that ends at a Stop step has done what it was to do. One that failed says why as FlowFailed worded it: the
        // run's own error is the innermost failure alone, as an automation's run says it.
        if (outcome.Status is not ("Succeeded" or "Stopped"))
            throw new InvalidOperationException(outcome.Status switch
            {
                "Failed" => setupFailure ?? outcome.Error ?? $"{definition.Name} does not start: its When started flow failed",
                "Cancelled" => $"{definition.Name} does not start: its When started flow was cancelled because {Because(outcome.Error)}",
                _ => $"{definition.Name} does not start: its When started flow did not run",
            });
        return all;
    }

    /// <summary>
    /// Runs the <c>[NodeSetup]</c> of each node library class the graph uses, in order, each once, on the clock its C#
    /// nodes' set-up shares (<see cref="RunSetUp"/>; the caller ends it). Each gets the definition's files, log and
    /// stopping, and keeps what it owns until the definition stops (<see cref="Own"/>). One that throws fails the start
    /// with <c>{definition}: {class}: {message}</c>, one past the limit with
    /// <c>{definition}: {class}: its [NodeSetup] did not finish within {n} s</c>; what it owned so far is disposed when
    /// the failed host is stopped.
    /// </summary>
    async Task RunNodeSetups()
    {
        // The context is made within the step, so that a failure to make it (no directory for downloads) names the class too.
        foreach (var setup in definition.NodeSetups)
            await RunSetUp(() => setup.Run(new NodeSetupContext(Files, Log, Stopping, Own)), $"{definition.Name}: {setup.Class}", "its [NodeSetup]");
    }

    /// <summary>One C# node of a graph as its host set it up: what its node says, the class it made, and that class's entities.</summary>
    sealed record Part(DefinitionComponent Node, SharpComponent Root, SharpEntity[] Entities);

    // A graph's C# nodes as they were set up, in node order; none once stopped.
    volatile Part[] parts = [];
    // The node of each class a graph's C# node made, by the class (a C# entity's entity, a C# device), from the moment it
    // is made: the unique ids of a C# device's entities, restored before its Setup(), depend on it.
    readonly System.Collections.Concurrent.ConcurrentDictionary<SharpComponent, DefinitionComponent> nodes = new(ReferenceEqualityComparer.Instance);

    /// <summary>The C# node the entity is of: a C# entity's own, or the C# device's that added it; null for any other.</summary>
    DefinitionComponent? Node(SharpEntity entity) =>
        nodes.TryGetValue(entity, out var own) ? own : entity.AddedTo is { } device && nodes.TryGetValue(device, out var added) ? added : null;

    /// <summary>The C# node whose data the component keeps: a C# entity's own, a C# device's, or that of the C# device that added the entity; null for any other.</summary>
    DefinitionComponent? NodeOf(SharpComponent component) =>
        component is SharpEntity entity ? Node(entity) : nodes.TryGetValue(component, out var node) ? node : null;

    /// <summary>Whether the host made the entity from one of the graph's Entity nodes, whose flows are its behaviour; a C# node's class is its own.</summary>
    bool IsHub(SharpEntity entity) => Node(entity) == null;

    /// <summary>
    /// The graph's C# devices as Home Assistant gets them, in node order: each its node's key, name and Part of, and what
    /// its class's device page shows, read after its <c>Setup()</c>, null where it sets none.
    /// </summary>
    IEnumerable<DefinitionDevice> ClassDevices() => parts.Where(part => part.Root is SharpDevice).Select(part =>
    {
        var made = (SharpDevice)part.Root;
        return new DefinitionDevice(made.Manufacturer, made.Model, made.SwVersion, made.HwVersion, made.ConfigurationUrl, made.SuggestedArea, part.Node.Via)
            { Key = part.Node.Key, Name = part.Node.Name };
    });

    /// <summary>
    /// Sets up the graph's C# entity and C# device nodes, in node order, each with the same steps
    /// (<see cref="SetUpComponent"/>): creates its class (<see cref="CompiledDefinition.Create"/>), applies its settings,
    /// gives a C# entity its node's key and, when its code names it none, its class's title if it stands alone (none on a
    /// device, whose name Home Assistant then shows), restores, sets up and seals. Every constructor and <c>Setup()</c> of
    /// them runs within <see cref="SetupTimeout"/> on one clock, which the node setups before them share (the caller ends
    /// it). A failure fails the definition's start, naming the node: nothing of it is registered.
    /// </summary>
    /// <returns>Their entities, in node order.</returns>
    async Task<SharpEntity[]> SetUpComponents()
    {
        List<Part> made = [];
        foreach (var node in definition.Components)
        {
            var who = $"{definition.Name}: {Called(node)}";
            var root = await CreateNode(node, who);
            nodes[root] = node;
            AddPage(node);
            var standalone = node.Device == null;
            var all = await SetUpComponent(root, who, node.Settings, node.Unset, entity => entity.Adopt(node.Key, standalone ? node.Title : null));
            made.Add(new(node, root, all));
        }
        parts = [.. made];
        return [.. made.SelectMany(part => part.Entities)];
    }

    /// <summary>A C# node as the host's messages name it: a C# entity its class's title, else its key; a C# device its name, else its key; else its kind.</summary>
    static string Called(DefinitionComponent node) =>
        VisualGraphValidator.Words.Trimmed(node.Kind == "entity" ? node.Title : node.Name ?? "") is { Length: > 0 } name ? name
        : VisualGraphValidator.Words.Trimmed(node.Key) is { Length: > 0 } key ? key
        : node.Kind == "entity" ? "C# entity" : "C# device";

    /// <summary>
    /// The class of the C# node, as its definition's <see cref="CompiledDefinition.Create"/> (the compiled graph's
    /// <c>SharpNodes.Create</c>) makes it: a C# entity's an entity, a C# device's a device. Made on a thread of its own,
    /// within <see cref="SetupTimeout"/> on the clock the C# nodes' set-up shares (the first makes it), as a <c>Setup()</c>
    /// is: a constructor, or a static one, that never returns fails the start (<c>{who} could not be created: its
    /// constructor did not finish within {n} s</c>) and is left to run on in the background, as such a <c>Setup()</c> is.
    /// What its constructor throws, or none made, is <c>{who} could not be created: {reason}</c>.
    /// </summary>
    async Task<SharpComponent> CreateNode(DefinitionComponent node, string who)
    {
        var create = definition.Create;
        var clock = setupClock ??= new SetupClock();
        var creating = Task.Factory.StartNew(() => create?.Invoke(node.NodeId),
            CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        bool ended;
        try { ended = await Within(creating, clock); }
        // Its message is user code, read once here. A stop of the definition meanwhile is no failure of the class.
        catch (Exception failure) when (!(failure is OperationCanceledException && Stopping.IsCancellationRequested))
        { throw new InvalidOperationException($"{who} could not be created: {Text(failure)}", failure); }
        if (!ended) throw new TimeoutException($"{who} could not be created: its constructor did not finish within {Seconds(SetupTimeout)} s");
        var made = await creating;
        return (node.Kind, made) switch
        {
            ("entity", SharpEntity entity) => entity,
            ("device", SharpDevice device) => device,
            (_, null) => throw new InvalidOperationException($"{who} could not be created: the compiled definition has no class for its node"),
            _ => throw new InvalidOperationException($"{who} could not be created: {made.GetType().Name} is not {(node.Kind == "entity" ? "an entity" : "a device")}"),
        };
    }

    /// <summary>
    /// A graph's entity as it is now, in plain values: its state values by key and its availability, and whether a state flow
    /// of its revision works it out (its availability is then that state flow's, which the next revision does not take).
    /// </summary>
    internal sealed record GraphEntityState(string Key, string Domain, bool Available, KeyValuePair<string, object?>[] Values, bool Worked = false);

    /// <summary>
    /// The state of each of a graph's entities as it is now, for <paramref name="next"/>, the revision that replaces this
    /// one, which from now on also gets what changes here until this one stops (<see cref="Inherit"/>); none unless
    /// running and not yet replaced. Never throws.
    /// </summary>
    internal GraphEntityState[] HandOver(DefinitionHost next)
    {
        try
        {
            lock (gate)
            {
                if (status != Running || Ignoring) return [];
                successor = next;
                return CurrentStates();
            }
        }
        catch (Exception) { return []; }
    }

    // Under the gate: each of a graph's entities as it is now. Only plain values (true or false, a number, text or none),
    // so that nothing of this revision's code is held by what reads them; a value of any other kind is left out, and so
    // is its entity. No user code runs here: a graph's entities are HassSharp's own.
    GraphEntityState[] CurrentStates()
    {
        var states = new List<GraphEntityState>();
        foreach (var entity in graphEntities.Values)
        {
            if (!SharpDomains.Graph.TryGetValue(entity.Domain, out var domain)) continue;
            var values = domain.State.Select(state => KeyValuePair.Create(state.Key, state.Value.Read(entity))).ToArray();
            if (values.All(value => value.Value is null or bool or double or string))
                states.Add(new(entity.Key, entity.Domain, entity.Available, values, stateKeys.Contains(entity.Key)));
        }
        return [.. states];
    }

    // What the revision of this definition that runs now shows, when this host replaces it: none for a first start, one
    // turned off, failed or stopping. Never throws.
    GraphEntityState[] Replaced()
    {
        try
        {
            if (Manager.Entities.Host(definition.Id) is not { } running || running == this) return [];
            var handed = running.HandOver(this);
            lock (gate) predecessor = running;
            return handed;
        }
        catch (Exception) { return []; }
    }

    /// <summary>
    /// The revision this one was to replace stops, with <paramref name="final"/> its entities' last states: each value that
    /// changed there since <see cref="HandOver"/> reaches the entity here that took that revision's state
    /// (<see cref="Carry"/>), unless a run here has set that value itself; a value the entity here refuses is left out.
    /// Each camera here whose unique id was a camera's there takes over its current and last good frame
    /// (<see cref="CameraFrames.TakeOver"/>), so that a picture being watched stays; nothing else of it. Nothing once this
    /// one stops. Never throws.
    /// </summary>
    /// <param name="cameras">The frames of the replaced revision's cameras, by unique id.</param>
    internal void Inherit(DefinitionHost from, GraphEntityState[] final, (string UniqueId, CameraFrames.Handover Frames)[] cameras)
    {
        List<string> dropped = [];
        var live = false;
        try
        {
            lock (gate)
            {
                if (predecessor == from) predecessor = null;
                if (stopped != null) return;
                live = true;
                foreach (var was in final)
                {
                    var first = handed.FirstOrDefault(state => state.Key == was.Key && state.Domain == was.Domain);
                    var changed = was.Values.Where(value => !own.Contains((was.Key, value.Key))
                        && !(first?.Values.Any(known => known.Key == value.Key && Equals(known.Value, value.Value)) ?? false)).ToArray();
                    if (changed.Length > 0 && Heir(was) is { } entity) Take(entity, changed, "the state the replaced revision set", dropped);
                }
            }
        }
        catch (Exception) { }
        // Outside the lock: each camera takes its own.
        if (live)
            foreach (var (id, frames) in cameras)
                try { if (entities.FirstOrDefault(entity => UniqueId(entity) == id) is SharpCamera camera) camera.Frames.TakeOver(frames); }
                catch (Exception) { }
        foreach (var line in dropped) Report(line, PyLogLevel.Debug);
    }

    // Lets go of the revision this one was to replace, which no longer hands anything over to it. Never under the gate.
    void ReleasePredecessor()
    {
        DefinitionHost? before;
        lock (gate) (before, predecessor) = (predecessor, null);
        if (before == null) return;
        try { lock (before.gate) if (before.successor == this) before.successor = null; }
        catch (Exception) { }
    }

    // Under the gate: this revision's entity that takes the state of the replaced revision's: same key and domain, and no
    // state flow works it out here.
    SharpEntity? Heir(GraphEntityState was) =>
        !stateKeys.Contains(was.Key) && graphEntities.TryGetValue(was.Key, out var entity) && entity.Domain == was.Domain && SharpDomains.Graph.ContainsKey(entity.Domain) ? entity : null;

    // Under the gate, as the entities are created: each entity no state flow works out takes the state, value by value, and the
    // availability of its namesake of the same domain in the revision this one replaces; a value it refuses is left out
    // (see Take). Not the availability a state flow of that revision worked out: none works it out here. The keys of the
    // entities that took a value.
    HashSet<string> Carry(GraphEntityState[] carried, List<string> dropped)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var was in carried)
        {
            if (Heir(was) is not { } entity) continue;
            if (Take(entity, was.Values, "the state carried over from the running revision", dropped) > 0) taken.Add(entity.Key);
            // A graph's entity has no host of its own, so this publishes nothing by itself.
            if (!was.Worked) entity.Available = was.Available;
        }
        return taken;
    }

    // Under the gate: the entity takes each value its domain has a state for and takes (GraphState.Refuses), as a run's
    // would be; each other is left out, with why in dropped, for the debug log. How many it took.
    int Take(SharpEntity entity, IEnumerable<KeyValuePair<string, object?>> values, string from, List<string> dropped)
    {
        if (!SharpDomains.Graph.TryGetValue(entity.Domain, out var domain)) return 0;
        var count = 0;
        foreach (var (key, value) in values)
        {
            if (!domain.State.TryGetValue(key, out var state)) dropped.Add($"{Named(entity.Key)} has no state {key}; it is left out of {from}");
            else if (state.Refuses(entity, value) is { } refused) dropped.Add($"{Refusal(entity, key, value, refused, hidden: false)}; it is left out of {from}");
            else
            {
                // A graph's entity has no host of its own, so this publishes nothing by itself.
                state.Keep(entity, value);
                count++;
            }
        }
        return count;
    }

    // A stored state value as a run would give it: true or false, a number, text, or none; any other JSON as it is, which
    // every state refuses.
    // (Each arm is an object: JsonNode converts from a bool, a text and a number, and would otherwise be the type of all.)
    static object? Plain(JsonNode? node) => node is JsonValue value ? value.GetValueKind() switch
    {
        JsonValueKind.True => (object)true,
        JsonValueKind.False => (object)false,
        JsonValueKind.String => (object)value.GetValue<string>(),
        JsonValueKind.Number when value.TryGetValue<double>(out var number) && double.IsFinite(number) => (object)number,
        _ => (object)node,
    } : node;

    /// <summary>
    /// Runs the graph's command flow for the entity's command and waits for it, no longer than
    /// <see cref="CommandTimeout"/> (the flow may wait on a command to this same entity, which waits behind it); a flow
    /// still running then is left to finish. The data is checked first, as a C# node's is (a value the entity does not take
    /// is refused, and no flow runs), and a value of it Home Assistant did not send is the entity's state value of the
    /// same name when it has one, else its row's default when the entity takes that (<see cref="Filled"/>). Completes
    /// when the flow succeeded or ended at a Stop step; otherwise throws, in words for the person who gave the command: the
    /// flow's own error when it failed (its first line; the run's record has it whole, and <see cref="FlowFailed"/> logs it).
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
    /// Runs the graph's frame flow <paramref name="method"/> to its end, for a request for its camera's
    /// picture (<see cref="FrameNeeded"/>, under the rule of <see cref="CameraFrames"/>): it works out the camera's Image and
    /// shows it through <see cref="ShowFrame"/>. A run still going at <see cref="CameraFrames.MakeLimit"/> is cancelled, so that a flow
    /// that hangs (a camera that never answers) does not keep its camera from making the next frame; its record says why.
    /// Completes when the flow succeeded or ended at a Stop step; otherwise throws, for the camera to log once per reason:
    /// the flow's failure as <see cref="FlowFailed"/> words it, wrapping what the run threw; a
    /// <see cref="TimeoutException"/> past the limit; an <see cref="OperationCanceledException"/> when the definition stops,
    /// or its script's lifetime ends.
    /// </summary>
    async Task RunFrame(string method)
    {
        var late = $"it took longer than {Seconds(CameraFrames.MakeLimit)} s";
        using var limit = new CancellationTokenSource(CameraFrames.MakeLimit);
        var outcome = await FlowClass(method).RunToEnd(method, InvocationCause.Frame, TriggerPayload.Frame(FrameCamera(method)), limit.Token, $"Took longer than {Seconds(CameraFrames.MakeLimit)} s");
        (string Reason, Exception Failure) failed;
        lock (gate) frameFailures.Remove(method, out failed);
        switch (outcome.Status)
        {
            case "Succeeded" or "Stopped": return;
            case "Failed" when failed.Failure != null: throw new InvalidOperationException(failed.Reason, failed.Failure);
            case "Failed": throw new InvalidOperationException(outcome.Error is { Length: > 0 } error ? error.Split('\n')[0].TrimEnd('\r') : "its flow failed");
            // The script's lifetime ends before the host's Stopping is cancelled from it (Home Assistant stopping): a stop too.
            case "Cancelled" when Stopping.IsCancellationRequested || script.LifetimeToken.IsCancellationRequested: throw new OperationCanceledException(Stopping);
            case "Cancelled" when limit.IsCancellationRequested: throw new TimeoutException(late);
            case "Cancelled": throw new OperationCanceledException($"its flow was cancelled because {Because(outcome.Error)}");
            default: throw new InvalidOperationException("its flow did not run");
        }
    }

    // The key of the camera a frame flow makes the frames of.
    string FrameCamera(string method) => definition.Roots?.Frames.FirstOrDefault(frame => frame.Value == method).Key ?? "";

    /// <summary>
    /// The command's data with each value its <c>entityDomains</c> row names that Home Assistant did not send (absent or
    /// null) taken from the entity's state value of the same name, when it has one; else from the row's <c>default</c>,
    /// when the entity takes that value as it takes the command's data (<see cref="SharpEntity.CheckData"/>). A light
    /// turned on without a brightness is turned on at the one it has, or at full; one that cannot be dimmed gets none. A
    /// value still not given stays absent, and a step reading it fails in words (see <c>VisualRuntime</c>). The data as
    /// it was when nothing is taken. Read under the lock, as states are kept.
    /// </summary>
    JsonElement Filled(SharpEntity entity, string name, JsonElement data)
    {
        // RunFlow checks the data, then fills it, then runs the flow, and that order must hold: CheckData has already
        // refused a value the command needs that is not given, so only an optional value is ever filled here, and the data
        // filled in is data the entity takes.
        if (!CommandValues.TryGetValue((entity.Domain, name), out var values)) return data;
        var state = SharpDomains.Graph.TryGetValue(entity.Domain, out var domain) ? domain.State : new Dictionary<string, GraphState>();
        JsonObject? filled = null;
        JsonObject Copy() => data.ValueKind == JsonValueKind.Object ? JsonNode.Parse(data.GetRawText())!.AsObject() : new JsonObject();
        lock (gate)
            foreach (var (key, fallback) in values)
            {
                if (CommandData.Given(data, key) != null) continue;
                var value = state.TryGetValue(key, out var kept) && kept.Read(entity) is { } current ? JsonSerializer.SerializeToNode(current) : null;
                if (value == null && fallback != null)
                {
                    var tried = filled?.DeepClone().AsObject() ?? Copy();
                    tried[key] = fallback.DeepClone();
                    try { entity.CheckData(name, JsonSerializer.SerializeToElement(tried)); value = fallback.DeepClone(); }
                    catch (ArgumentException) { }
                }
                if (value != null) (filled ??= Copy())[key] = value;
            }
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
                    throw new InvalidOperationException(Refusal(entity, key, value, refused, hidden.Contains(key)));
            foreach (var (key, value) in values)
            {
                known[key].Keep(entity, value);
                own.Add((entityKey, key));
            }
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

    /// <summary>
    /// Sets the current frame of the graph's camera <paramref name="entityKey"/>: a copy of <paramref name="image"/> is kept,
    /// so the run may go on drawing on its own (<see cref="SharpCamera.Show"/>). From any run, while the definition prepares
    /// too; ignored once the definition stops or is replaced. Thread-safe; never calls into a run.
    /// </summary>
    /// <exception cref="InvalidOperationException">The definition has no such entity, or it is not a camera.</exception>
    public void SetFrame(string entityKey, SharpImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        SharpEntity entity;
        lock (gate)
        {
            if (Ignoring) return;
            entity = GraphEntity(entityKey);
        }
        // Outside the lock: the camera takes its own, and calls into this host under it.
        if (entity is not SharpCamera camera) throw new InvalidOperationException($"{definition.Name}: entity {entityKey} is not a camera, so it shows no frame");
        camera.Show(image);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <see cref="SetFrame"/>, from the camera's frame flow.
    /// Ignored once the definition stops or is replaced.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The definition has no such entity, or it is not a camera.</exception>
    public void ShowFrame(string entityKey, SharpImage image) => SetFrame(entityKey, image);

    /// <summary>
    /// Has a request for the picture of the graph's camera <paramref name="entityKey"/> run <paramref name="flow"/> first, as
    /// a C# node's <c>Frame()</c> runs (<see cref="CameraImage"/>): one at a time, at most once per the camera's interval and
    /// shared by the requests meanwhile, within <see cref="CameraFrames.MakeLimit"/>, cancelled through
    /// <see cref="Stopping"/> and waited for by stopping. The flow shows its frame with <see cref="SetFrame"/>; one that shows
    /// none keeps the frame shown, and one that throws fails the request as a C# node's would (the last good frame is still
    /// given for a while). Set as the graph prepares; forgotten once the definition stops.
    /// </summary>
    internal void FrameNeeded(string entityKey, Func<Task> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        lock (gate) frameFlows[entityKey] = flow;
    }

    /// <inheritdoc/>
    public object? Setting(string key) =>
        graphSettings.TryGetValue(key, out var setting) ? setting.Value : throw new KeyNotFoundException($"{definition.Name} has no setting {key}");

    /// <inheritdoc/>
    public bool IsSecret(string key) => graphSettings.TryGetValue(key, out var setting) && setting.Secret;

    /// <summary>Whether <paramref name="method"/> is one of a graph's state flows, whose runs end in <see cref="StateEnded"/>.</summary>
    internal bool IsState(string method) => definition.Roots?.StateEntities.ContainsKey(method) == true;

    /// <summary>
    /// How a run of a state flow ended, which the runner does not log: Home Assistant's entities come and go as it starts, so a
    /// state flow may fail on every change for a while. A failed run, whatever failed (an input, or a value the entity does not
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
    /// How a run of one of the graph's other flows failed, which the runner leaves to the host: a command flow, the
    /// When started flow, or a trigger's flow. Logs one line naming the definition, the flow and why (as a state flow's
    /// warning says why), with no stack: a command's at warning, as whoever gave it is answered the error and Home
    /// Assistant logs it too, each time, as someone asked for each; the When started flow's not at all, as the definition
    /// then cannot start, which its manager warns of once per reason in the same words. A trigger flow's
    /// at error once per reason, as a state flow's warning is: a poll of a device that does not answer fails on every tick,
    /// and only a failure for another reason logs again, until a run works (<see cref="FlowSucceeded"/>). Then, for every
    /// failure, the whole of it at Debug. Keeps the reason of a trigger flow's failure, and of the When started flow's for its start's error. A
    /// frame flow's is not logged here: its reason and failure are kept for <see cref="RunFrame"/>, which
    /// fails its camera's frame with them, and the camera logs that once per reason. Never throws.
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
            var (flow, kind) = Flow(method, cause);
            // A failing frame flow fails its camera's frame, which the camera logs once per reason, as a C# node's Frame().
            if (kind == FlowKind.Frame)
            {
                lock (gate) frameFailures[method] = (reason, failure);
                return;
            }
            // A failing When started flow fails the definition's start, whose error and warning say why, in these words.
            var log = kind != FlowKind.Setup;
            if (!log) setupFailure = reason;
            if (kind == FlowKind.Trigger) log = Failures(method).Fails(reason);
            if (log) Report($"{flow} failed: {reason}", kind == FlowKind.Command ? PyLogLevel.Warn : PyLogLevel.Error);
            // The whole failure after its line: ToString() is user code, and builds the stack.
            string whole;
            try { whole = failure.ToString(); } catch { whole = failure.GetType().FullName ?? "an exception"; }
            Report($"{flow} failed: {whole}", PyLogLevel.Debug);
        }
        catch { }
    }

    /// <summary>A run of a graph's flow other than a state flow worked: a trigger flow that failed before says, once, that it works again. Never throws.</summary>
    /// <param name="method">The flow's method.</param>
    internal void FlowSucceeded(string method)
    {
        try
        {
            FailureLog? failures;
            // Only a flow that failed has one.
            lock (gate) failingFlows.TryGetValue(method, out failures);
            if (failures?.Works() == true) Report($"{Flow(method, InvocationCause.Entity).Name} works again", PyLogLevel.Info);
        }
        catch { }
    }

    // What a trigger flow last logged of its failures.
    FailureLog Failures(string method)
    {
        lock (gate)
        {
            if (!failingFlows.TryGetValue(method, out var log)) failingFlows[method] = log = new();
            return log;
        }
    }

    enum FlowKind { Command, Setup, Trigger, Frame }

    // A flow as its log lines name it: an entity's command, When started, a camera's frame flow, or a trigger by its label
    // (else its node).
    (string Name, FlowKind Kind) Flow(string method, InvocationCause cause)
    {
        var roots = definition.Roots;
        if (roots?.Frames.FirstOrDefault(frame => frame.Value == method).Key is { } camera) return ($"{Named(camera)}: {DefinitionGraph.FrameLabel}", FlowKind.Frame);
        if (roots?.Commands.FirstOrDefault(command => command.Value == method).Key is { } handled)
        {
            // Keyed <entity key>:<command>; an entity key has no colon.
            var colon = handled.IndexOf(':');
            var key = handled[..colon];
            var domain = definition.Entities.OfType<JsonObject>().FirstOrDefault(e => VisualNodeCatalog.Text(e, "key") == key) is { } entity ? VisualNodeCatalog.Text(entity, "domain") : "";
            return ($"{Named(key)}: {CommandLabel(domain, handled[(colon + 1)..])}", FlowKind.Command);
        }
        if (roots?.Setup == method || cause == InvocationCause.Setup) return ("When started", FlowKind.Setup);
        return (script.CompiledScript.VisualTriggers?.GetValueOrDefault(method) is { } trigger ? trigger.Label ?? $"Trigger ({trigger.NodeId})" : method, FlowKind.Trigger);
    }

    /// <summary>
    /// Watches a run of the graph's state flow <paramref name="method"/> until the returned handle is disposed, as it ends: one
    /// that has not ended after <see cref="SlowStateWarning"/> warns once, naming the entity (a state flow waits on what it
    /// reads, which should not take that long); when it then ends, Debug says after how long. Nothing for another method,
    /// or once the definition stops. Never throws.
    /// </summary>
    internal IDisposable WatchState(string method)
    {
        try
        {
            if (definition.Roots?.StateEntities.TryGetValue(method, out var key) != true) return Nothing;
            return new SlowState(this, key!);
        }
        catch { return Nothing; }
    }

    // A state flow's run being watched: 0 while it runs in time, 1 once warned of, 2 once ended.
    sealed class SlowState : IDisposable
    {
        readonly DefinitionHost host;
        readonly string key;
        readonly CancellationTokenSource ended;
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        int state;

        public SlowState(DefinitionHost host, string key)
        {
            (this.host, this.key) = (host, key);
            ended = CancellationTokenSource.CreateLinkedTokenSource(host.Stopping);
            _ = Watch();
        }

        async Task Watch()
        {
            try { await Task.Delay(SlowStateWarning, ended.Token); }
            catch (OperationCanceledException) { return; }
            if (host.Ignoring || Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
            host.Report($"the State of {host.Named(key)} has not finished after {Seconds(SlowStateWarning)} s", PyLogLevel.Warn);
        }

        public void Dispose()
        {
            var was = Interlocked.Exchange(ref state, 2);
            if (was == 2) return;
            try { ended.Cancel(); } catch { }
            ended.Dispose();
            if (was == 1) host.Report($"the State of {host.Named(key)} finished after {clock.Elapsed.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s", PyLogLevel.Debug);
        }
    }

    /// <summary>
    /// What a failure says, as the person reads it (a state flow's, a flow's, a timer's, and why a definition cannot start):
    /// the message of what was thrown, past any wrapper of the runner's or of reflection's, which names the failing node
    /// and the step that used it when the visual runtime wrapped it; then, when it does not say it already, the message
    /// of the failure underneath (such as the text a number could not be read from). Each its first line; null when the
    /// failure says nothing. Never throws for a message that does (user code).
    /// </summary>
    internal static string? Reason(Exception? failure)
    {
        static string First(Exception error) => Text(error).Split('\n')[0].TrimEnd('\r');
        var shown = failure;
        while (shown is System.Reflection.TargetInvocationException or AggregateException && shown.InnerException != null) shown = shown.InnerException;
        if (shown == null || First(shown) is not { Length: > 0 } outer) return null;
        var cause = First(shown.GetBaseException());
        return cause.Length == 0 || outer.Contains(cause, StringComparison.Ordinal) ? outer : $"{outer} ({cause})";
    }

    // Why the entity does not take a value, naming the entity and the value: a secret value is not quoted, nor is a
    // password text's, as its class words it.
    string Refusal(SharpEntity entity, string key, object? value, string refused, bool hidden) =>
        hidden ? $"The {key} of {Named(entity.Key)} {refused}; the value is hidden because it comes from a secret setting"
        : entity is SharpText { Mode: SharpTextMode.Password } ? $"The hidden value given for {Named(entity.Key)} {refused}"
        : $"The {key} {Shown(value)} of {Named(entity.Key)} {refused}";

    // A value as a refusal names it.
    static string Shown(object? value) => value switch
    {
        null => "empty",
        string text => $"\"{text}\"",
        bool flag => flag ? "true" : "false",
        double number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => value.GetType().Name,
    };

    // An entity as a message names it: its name (trimmed, as run names have it) and key, or its key alone.
    string Named(string key) =>
        definition.Entities.OfType<JsonObject>().FirstOrDefault(e => VisualNodeCatalog.Text(e, "key") == key) is { } described
            && VisualGraphValidator.Words.Trimmed(VisualNodeCatalog.Text(described, "name") ?? "") is { Length: > 0 } name
            ? $"{name} ({key})" : key;

    // Under the gate.
    SharpEntity GraphEntity(string key) => graphEntities.TryGetValue(key, out var entity) ? entity
        : throw new InvalidOperationException($"{definition.Name} has no entity {key}");

    /// <summary>The command labels of each entity domain, as the catalog's <c>entityDomains</c> gives them.</summary>
    static readonly IReadOnlyDictionary<string, Dictionary<string, string>> CommandLabels = ReadCommandLabels();

    /// <summary>
    /// The data values of each domain's commands that have some, each with its <c>default</c> (null for none), as the
    /// catalog's <c>entityDomains</c> gives them.
    /// </summary>
    static readonly IReadOnlyDictionary<(string Domain, string Command), (string Name, JsonNode? Default)[]> CommandValues =
        (VisualNodeCatalog.Root["entityDomains"] as JsonObject ?? []).SelectMany(domain => (domain.Value?["commands"] as JsonObject ?? [])
            .Where(command => command.Value?["data"] is JsonObject { Count: > 0 })
            .Select(command => KeyValuePair.Create((domain.Key, command.Key),
                ((JsonObject)command.Value!["data"]!).Select(value => (value.Key, value.Value?["default"]?.DeepClone())).ToArray())))
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
