using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace HassSharp;

// When adding methods to this class make sure to exclude them from the CodeRunner above or they will be run as an automation and fail
public abstract class Automation
{
    public string UserClassName { get; set; } = "Unknown";
    public bool Initializing { get; set; } = true;
    public CodeRunner Runner { get; internal set; } = null!;

    // Injected by Python
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    HasEntityState? EntityRaw(string entityId, string klass, string method)
    {
        Runner.TrackEntityCall(entityId, klass, method);

        return PyInterop.Entity(entityId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    HasEntityState? EntityRawUntracked(string entityId)
    {
        return PyInterop.Entity(entityId);
    }

    public EntityRef<T> Entity<T>(EntityRefWrapper<T> entityWrapper, [CallerMemberName] string? caller = null)
    {
        var raw = EntityRaw(entityWrapper.EntityId, UserClassName, caller!);

        if (raw == null) throw new($"Entity with id {entityWrapper.EntityId} not found");
        return new()
        {
            Automation = this,
            Raw = raw,
        };
    }

    public EntityRef<T> Entity<T>(string entityId, [CallerMemberName] string? caller = null)
    {
        var raw = EntityRaw(entityId, UserClassName, caller!);

        if (raw == null) throw new($"Entity with id {entityId} not found");
        return new()
        {
            Automation = this,
            Raw = raw,
        };
    }


    public EntityRef<string> Entity(string entityId, [CallerMemberName] string? caller = null)
    {
        var raw = EntityRaw(entityId, UserClassName, caller!);
        if (raw == null) throw new($"Entity with id {entityId} not found");
        return new()
        {
            Automation = this,
            Raw = raw,
        };
    }

    public EntityRef<T> EntityUntracked<T>(EntityRefWrapper<T> entityRefWrapper)
    {
        var raw = EntityRawUntracked(entityRefWrapper.EntityId);
        if (raw == null) throw new($"Entity with id {entityRefWrapper.EntityId} not found");
        return new()
        {
            Automation = this,
            Raw = raw,
        };
    }

    public EntityRef<string> EntityUntracked(string entityId)
    {
        var raw = EntityRawUntracked(entityId);
        if (raw == null) throw new($"Entity with id {entityId} not found");
        return new()
        {
            Automation = this,
            Raw = raw,
        };
    }

    public EntityRef<T> EntityUntracked<T>(string entityId)
    {
        var raw = EntityRawUntracked(entityId);
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
}
