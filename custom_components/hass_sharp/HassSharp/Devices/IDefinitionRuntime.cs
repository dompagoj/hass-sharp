namespace HassSharp;

/// <summary>
/// What a run of an entity graph can do to its definition, reached through the run in progress
/// (<see cref="InvocationContext.Definition"/>). The visual runtime calls it synchronously, on the run's own flow, from
/// the step or value node that needs it, and never after the run's task has completed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Concurrency.</b> Calls can arrive at the same time: from the parallel branches of one run, and from overlapping
/// runs, including two runs of the same root (a newer change restarts a State, but the older run ends only once it
/// notices its cancellation). They arrive on thread-pool threads, never on Home Assistant's or Python's. An implementation
/// must be thread-safe. It may not call back into the run in progress nor wait for a run of its definition from a call:
/// the run is blocked in the call until it returns.
/// </para>
/// <para>
/// <b>A State's run</b> (<c>entity_state</c>):
/// <list type="number">
/// <item>The runtime works out the State's value nodes, which may call <see cref="Setting"/> (and, in a traced run only,
/// <see cref="IsSecret"/>), then reads its inputs.</item>
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
/// during the call itself, or right after the check, does not stop it, so an older run's call can still land after a newer
/// run's. Ordering beyond that check is the host's and the runner's job, for example by dropping a call from a run that is
/// no longer the latest. <see cref="SetAvailable"/> with false can also arrive while the definition is stopping, from a
/// run whose failure came before its cancellation.
/// </para>
/// <para>
/// <b>When a call throws</b>, the run fails, recorded as any failed run is:
/// <list type="bullet">
/// <item>A throwing <see cref="SetState"/>, from a State or a step, fails the run with that error. The entity is not marked unavailable.</item>
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
    /// an empty value (the state is then unknown, never refused).
    /// </summary>
    void SetState(string entityKey, IReadOnlyDictionary<string, object?> values);
    /// <summary>Marks the definition's entity <paramref name="entityKey"/> available or unavailable in Home Assistant.</summary>
    void SetAvailable(string entityKey, bool available);
    /// <summary>The value of the definition's setting <paramref name="key"/>: a string, bool or double; throws <see cref="KeyNotFoundException"/> for an unknown key.</summary>
    object? Setting(string key);
    /// <summary>
    /// Whether the setting <paramref name="key"/> is marked secret; false for an unknown key. Asked only by a run that is
    /// traced, as it writes a Setting node's output into the trace. A Setting node of a secret
    /// setting is traced as <c>***</c>, while its value still reaches the nodes that use it. The value is not followed
    /// further: a node that reads the secret value through a wire is traced with it in clear, which a later phase must
    /// handle.
    /// </summary>
    bool IsSecret(string key);
}
