using System.Runtime.CompilerServices;

namespace HassSharp;

public enum AutomationMode
{
    Single,
    Restart,
    Queued,
    Parallel
}

[AttributeUsage(AttributeTargets.Method)]
public class ModeAttribute(AutomationMode mode) : Attribute
{
    public AutomationMode Mode { get; } = mode;
}

public abstract class Automation : UserScriptClassBase
{
    internal readonly AsyncLocal<CancellationToken> _cancellationToken = new();
    public CancellationToken CancellationToken => _cancellationToken.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    HasEntityState? GetEntityValueTracked(string entityId, string method)
    {
        UserScriptClass.Script.ScriptManager.TrackEntityCall(entityId, UserScriptClass, method);

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

    /// <summary>
    ///  Use to get the entityId of the entity which triggered a rerun of this automation, will be null on first run or if the trigger is time based or otherwise non entity related
    /// </summary>
    /// <returns>Entity Id of the entity which triggered this automation to rerun</returns>
    protected EntityRef<HaUnknown>? TriggeredBy()
    {
        if (UserScriptManager.CurrentTrigger.Value is null) return null;

        return new(UserScriptManager.CurrentTrigger.Value.NewState);
    }

    protected EntityRef<T> Entity<T>(EntityRefWrapper<T> entityWrapper, [CallerMemberName] string? caller = null)
        where T : EntityRefWrapper<T>
    {
        var raw = GetEntityValueTracked(entityWrapper.EntityId, caller!);

        if (raw == null) throw new($"Entity with id {entityWrapper.EntityId} not found");

        return new(raw);
    }

    protected EntityRef<HaUnknown> Entity(string entityId, [CallerMemberName] string? caller = null)
    {
        var raw = GetEntityValueTracked(entityId, caller!);
        if (raw == null) throw new($"Entity with id {entityId} not found");
        return new(raw);
    }

    protected EntityRef<T> EntityUntracked<T>(EntityRefWrapper<T> entityRefWrapper)
        where T : EntityRefWrapper<T>
    {
        var raw = GetEntityValueUntracked(entityRefWrapper.EntityId);
        if (raw == null) throw new($"Entity with id {entityRefWrapper.EntityId} not found");
        return new(raw);
    }

    protected EntityRef<HaUnknown> EntityUntracked(string entityId)
    {
        var raw = GetEntityValueUntracked(entityId);
        if (raw == null) throw new($"Entity with id {entityId} not found");

        return new(raw);
    }


    public static HassServices Services => HassServices.Instance;
}
