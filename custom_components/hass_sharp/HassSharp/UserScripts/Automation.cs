using System.Runtime.CompilerServices;
using System.Text.Json;

namespace HassSharp;

public abstract class Automation : UserScriptBase
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    HasEntityState? GetEntityValueTracked(string entityId, string method)
    {
        UserScript.Runner.TrackEntityCall(entityId, UserScript, method);

        var state = PyInterop.Entity(entityId);

        var trigger = UserScriptManager.CurrentTrigger.Value;
        if (trigger != null && trigger.NewState.EntityId == entityId && state != null)
        {
            state.OldState = trigger.OldState;
        }

        return state;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    HasEntityState? GetEntityValueUntracked(string entityId)
    {
        var state = PyInterop.Entity(entityId);

        var trigger = UserScriptManager.CurrentTrigger.Value;
        if (trigger != null && trigger.NewState.EntityId == entityId && state != null)
        {
            state.OldState = trigger.OldState;
        }

        return state;
    }

    protected EntityRef<T> Entity<T>(EntityRefWrapper<T> entityWrapper, [CallerMemberName] string? caller = null)
    {
        var raw = GetEntityValueTracked(entityWrapper.EntityId, caller!);

        if (raw == null) throw new($"Entity with id {entityWrapper.EntityId} not found");
        return new()
        {
            Automation = this,
            Raw = raw,
        };
    }

    protected EntityRef<T> Entity<T>(string entityId, [CallerMemberName] string? caller = null)
    {
        var raw = GetEntityValueTracked(entityId, caller!);

        if (raw == null) throw new($"Entity with id {entityId} not found");
        return new()
        {
            Automation = this,
            Raw = raw,
        };
    }


    protected EntityRef<string> Entity(string entityId, [CallerMemberName] string? caller = null)
    {
        var raw = GetEntityValueTracked(entityId, caller!);
        if (raw == null) throw new($"Entity with id {entityId} not found");
        return new()
        {
            Automation = this,
            Raw = raw,
        };
    }

    protected EntityRef<T> EntityUntracked<T>(EntityRefWrapper<T> entityRefWrapper)
    {
        var raw = GetEntityValueUntracked(entityRefWrapper.EntityId);
        if (raw == null) throw new($"Entity with id {entityRefWrapper.EntityId} not found");
        return new()
        {
            Automation = this,
            Raw = raw,
        };
    }

    protected EntityRef<string> EntityUntracked(string entityId)
    {
        var raw = GetEntityValueUntracked(entityId);
        if (raw == null) throw new($"Entity with id {entityId} not found");
        return new()
        {
            Automation = this,
            Raw = raw,
        };
    }

    protected EntityRef<T> EntityUntracked<T>(string entityId)
    {
        var raw = GetEntityValueUntracked(entityId);
        if (raw == null) throw new($"Entity with id {entityId} not found");
        return new()
        {
            Automation = this,
            Raw = raw,
        };
    }


    public void CallService(string domain, string service, object? data = null)
    {
        var json = data != null ? JsonSerializer.Serialize(data) : null;
        PyInterop.CallService(domain, service, json);
    }
}
