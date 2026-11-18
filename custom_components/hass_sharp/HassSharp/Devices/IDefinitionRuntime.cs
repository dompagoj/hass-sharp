namespace HassSharp;

/// <summary>
/// What a run of an entity graph can do to its definition, reached through the run in progress
/// (<see cref="InvocationContext.Definition"/>). The visual runtime calls it synchronously, on the run's own flow, from
/// the step or value node that needs it, and never after the run's task has completed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Concurrency.</b> Calls can arrive at the same time: from the parallel branches of one run, from runs of different
/// roots, and from two runs of one trigger whose mode is Parallel. Two runs of one State never overlap, nor do two of one
/// When commanded flow: a State restarts (a newer change cancels the run in progress, and the newer run starts only once
/// that one has ended) and a When commanded flow is queued. They arrive on thread-pool threads, never on Home
/// Assistant's or Python's. An implementation must be thread-safe. It may not call back into the run in progress nor
/// wait for a run of its definition from a call: the run is blocked in the call until it returns.
/// </para>
/// <para>
/// <b>A State's run</b> (<c>entity_state</c>):
/// <list type="number">
/// <item>The runtime works out the State's value nodes, which may call <see cref="Setting"/> and <see cref="IsSecret"/>,
/// then reads its inputs.</item>
/// <item>On success it checks the run's cancellation: a run already cancelled (superseded by a newer one, or stopped) ends
/// there and calls nothing more. Otherwise it calls <see cref="SetState"/>, then <see cref="SetAvailable"/> with true.
/// It does so on every run, even when the values did not change.</item>
/// <item>When working out the inputs fails, it calls <see cref="SetAvailable"/> with false and nothing else, and the run
/// fails with that error. When the failure is the run's own cancellation, it calls neither; an input that fails for
/// another reason while the run is being cancelled still calls <see cref="SetAvailable"/> with false.</item>
/// </list>
/// </para>
/// <para>
/// <b>A Set state or Set available step</b> reads its inputs, checks the run's cancellation in the same way, and then
/// calls <see cref="SetState"/> or <see cref="SetAvailable"/> once. Calls already made stay made when a later step fails;
/// nothing is undone.
/// </para>
/// <para>
/// <b>What remains to the host.</b> Only a run cancelled before its call is kept from calling. A cancellation that arrives
/// during the call itself, or right after the check, does not stop it; a newer run of the same State starts only after
/// such a call has returned, but a run of another root may call at any time. Ordering beyond that is the host's and the
/// runner's job. <see cref="SetAvailable"/> with false can also arrive while the definition is stopping, from a
/// run whose failure came before its cancellation.
/// </para>
/// <para>
/// <b>When a call throws</b>, the run fails, recorded as any failed run is:
/// <list type="bullet">
/// <item>A throwing <see cref="SetState"/>, from a State or a step, fails the run with that error. The runtime does not mark
/// the entity unavailable; for a State, the host may once the run has ended (<see cref="DefinitionHost"/> does).</item>
/// <item>A throwing <see cref="SetAvailable"/> fails the run with its error, with one exception. The call with false that
/// follows a State's failed inputs does not replace that failure: the run fails with the State's own error, and the
/// error of this call is logged.</item>
/// <item>A throwing <see cref="Setting"/> or <see cref="IsSecret"/> fails the node reading the setting, which fails the step
/// that uses it. For a State, that also marks the entity unavailable, as any failure of its inputs does.</item>
/// </list>
/// </para>
/// </remarks>
internal interface IDefinitionRuntime
{
    /// <summary>
    /// Sets the state of the definition's entity <paramref name="entityKey"/>. <paramref name="values"/> has one entry for
    /// each state key of the entity's domain (<c>entityDomains[domain].state</c>), in that order, each a plain value: a
    /// <see cref="bool"/> for true or false, a <see cref="double"/> for a number, a <see cref="string"/> for text, or null for
    /// an empty value (the state is then unknown, never refused). The keys in <paramref name="hidden"/> have secret
    /// values (a secret setting's, or worked out from one): a refusal names them without their value.
    /// </summary>
    void SetState(string entityKey, IReadOnlyDictionary<string, object?> values, IReadOnlySet<string> hidden);
    /// <summary>Marks the definition's entity <paramref name="entityKey"/> available or unavailable in Home Assistant.</summary>
    void SetAvailable(string entityKey, bool available);
    /// <summary>The value of the definition's setting <paramref name="key"/>: a string, bool or double; throws <see cref="KeyNotFoundException"/> for an unknown key.</summary>
    object? Setting(string key);
    /// <summary>
    /// Whether the setting <paramref name="key"/> is marked secret; false for an unknown key. A Setting node of a secret
    /// setting is traced as <c>***</c>, while its value still reaches the nodes that use it; so are the inputs it reaches
    /// and the values worked out from it (true or false aside), and a failure, the Log step and a service call's debug
    /// log do not quote them. Asked by a traced run as it writes such a value, and by any run, traced or not, once per
    /// Setting node, when a step that reads one fails, logs or calls a service. When a Setting node asks and the call
    /// throws, that Setting node fails. When the call throws while asked about a value that reached another step, the
    /// value is treated as secret.
    /// </summary>
    bool IsSecret(string key);
    /// <summary>
    /// The definition's downloads, which a Download file step makes (<see cref="SharpFiles.Download"/>): the time one
    /// receives during the When started flow does not count against its limit, and every one ends when the definition
    /// stops. Throws <see cref="ObjectDisposedException"/> once it has stopped.
    /// </summary>
    SharpFiles Files { get; }
}
