namespace HassSharp;

/// <summary>
/// What a run of an entity graph can do to its definition. Reached through the run in progress
/// (<see cref="InvocationContext.Definition"/>); the visual runtime calls it on the run's own flow, synchronously, from the
/// step or value node that needs it, never once the run has ended.
/// </summary>
internal interface IDefinitionRuntime
{
    /// <summary>
    /// Sets the state of the definition's entity <paramref name="entityKey"/>. <paramref name="values"/> has one entry per
    /// state key of the entity's domain (<c>entityDomains[domain].state</c>), in that order, each a plain value: a
    /// <see cref="bool"/>, a <see cref="double"/>, a <see cref="string"/>, or null for an empty value (the state is then unknown).
    /// </summary>
    void SetState(string entityKey, IReadOnlyDictionary<string, object?> values);
    /// <summary>Marks the definition's entity <paramref name="entityKey"/> available or unavailable in Home Assistant.</summary>
    void SetAvailable(string entityKey, bool available);
    /// <summary>The value of the definition's setting <paramref name="key"/>: a string, bool or double; throws <see cref="KeyNotFoundException"/> for an unknown key.</summary>
    object? Setting(string key);
}
