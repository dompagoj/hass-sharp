using System.Runtime.CompilerServices;
using System.Text.Json;

namespace HassSharp;

// When adding methods to this class make sure to exclude them from the CodeRunner above or they will be run as an automation and fail
public abstract class Automation
{
    public bool Initializing { get; set; } = true;
    public CodeRunner Runner { get; internal set; } = null!;

    // Injected by Python
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    HasEntityState? EntityRaw(string entityId, string caller)
    {
        if (!Runner.DependencyTracking.TryGetValue(entityId, out var methods))
        {
            methods = new List<string>();
            Runner.DependencyTracking[entityId] = methods;
        }

        if (!methods.Contains(caller))
        {
            methods.Add(caller);
        }

        return PyInterop.Entity(entityId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    HasEntityState? EntityRaw(string entityId)
    {
        return PyInterop.Entity(entityId);
    }

    public EntityRef<T> Entity<T>(string entityId, [CallerMemberName] string? caller = null)
    {
        var raw = EntityRaw(entityId, caller!);

        if (raw == null) throw new($"Entity with id {entityId} not found");
        return new()
        {
            Automation = this,
            Raw = raw,
        };
    }

    public EntityRef<string> Entity(string entityId, [CallerMemberName] string? caller = null)
    {
        var raw = EntityRaw(entityId, caller!);
        if (raw == null) throw new($"Entity with id {entityId} not found");
        return new()
        {
            Automation = this,
            Raw = raw,
        };
    }

    public EntityRef<T> EntityUntracked<T>(string entityId)
    {
        var raw = EntityRaw(entityId);
        if (raw == null) throw new($"Entity with id {entityId} not found");
        return new()
        {
            Automation = this,
            Raw = raw,
        };
    }

    public EntityRef<string> EntityUntracked(string entityId)
    {
        var raw = EntityRaw(entityId);
        if (raw == null) throw new($"Entity with id {entityId} not found");
        return new()
        {
            Automation = this,
            Raw = raw,
        };
    }


    internal void CallService(string domain, string service, object? data = null)
    {
        var json = data != null ? JsonSerializer.Serialize(data) : null;
        PyInterop.CallService(domain, service, json);
    }

    public void SetInputNumber(string entityId, int value)
    {
        CallService("input_number", "set_value", new
        {
            entity_id = entityId,
            value,
        });
    }
}
