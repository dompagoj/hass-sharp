namespace HassSharp;

/// <summary>
/// A custom Home Assistant device: one definition with several entities that share its logic and its settings, such
/// as a smart plug with a relay and a power reading. Add the entities in the constructor with <see cref="Add{T}"/>.
/// </summary>
public abstract class SharpDevice : SharpComponent
{
    readonly List<SharpEntity> entities = [];

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

    /// <summary>Adds an entity to the device and returns it, so it can be kept in a field.</summary>
    /// <param name="entity">The entity; its key must differ from every other entity's of the device.</param>
    /// <exception cref="ArgumentException">The device already has an entity with the same key.</exception>
    protected T Add<T>(T entity) where T : SharpEntity
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entities.Any(existing => existing.Key == entity.Key))
            throw new ArgumentException($"{GetType().Name} already has an entity with key '{entity.Key}'.", nameof(entity));
        entities.Add(entity);
        return entity;
    }
}
