namespace HassSharp;

/// <summary>
/// A custom Home Assistant device: one definition with several entities that share its logic and its settings, such
/// as a smart plug with a relay and a power reading. Add the entities in the constructor with <see cref="Add{T}"/>.
/// The device metadata (<see cref="Manufacturer"/>, <see cref="Model"/> and the rest) is read once, after
/// <see cref="SharpComponent.Setup"/>: set it there at the latest, as changing it later has no effect.
/// </summary>
public abstract class SharpDevice : SharpComponent
{
    readonly List<SharpEntity> entities = [];

    /// <summary>Set by the host once the entities are known: from then on <see cref="Add{T}"/> refuses.</summary>
    internal bool Sealed { get; set; }
    /// <summary>What the host does with an entity added while it prepares the definition (it restores its state); null otherwise.</summary>
    internal Action<SharpEntity>? Added { get; set; }

    /// <summary>Who made the device, as the device page shows it; null for none.</summary>
    public string? Manufacturer { get; set; }
    /// <summary>The device's model; null for none.</summary>
    public string? Model { get; set; }
    /// <summary>The version of the device's software; null for none.</summary>
    public string? SwVersion { get; set; }
    /// <summary>The version of the device's hardware; null for none.</summary>
    public string? HwVersion { get; set; }
    /// <summary>The address of the device's own configuration page; null for none.</summary>
    public string? ConfigurationUrl { get; set; }
    /// <summary>The area Home Assistant suggests for the device when it first registers it, such as <c>Kitchen</c>; null for none.</summary>
    public string? SuggestedArea { get; set; }

    /// <summary>The device's entities, in the order they were added.</summary>
    public IReadOnlyList<SharpEntity> Entities => entities;

    /// <summary>
    /// Adds an entity to the device and returns it, so it can be kept in a field. Only in the constructor or in
    /// <see cref="SharpComponent.Setup"/>: once the entities are registered, such as from <c>Start()</c> or a timer, it throws.
    /// </summary>
    /// <param name="entity">The entity; its key must differ from every other entity's of the device.</param>
    /// <exception cref="ArgumentException">The device already has an entity with the same key.</exception>
    /// <exception cref="InvalidOperationException">The device's entities are already registered.</exception>
    protected T Add<T>(T entity) where T : SharpEntity
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (Sealed)
            throw new InvalidOperationException($"{GetType().Name} cannot add entity '{entity.Key}' now: entities are added in the constructor or in Setup(), before they are registered with Home Assistant.");
        if (entities.Any(existing => existing.Key == entity.Key))
            throw new ArgumentException($"{GetType().Name} already has an entity with key '{entity.Key}'.", nameof(entity));
        entities.Add(entity);
        Added?.Invoke(entity);
        return entity;
    }
}
