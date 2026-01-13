namespace HassSharp;

public class EntityRef<T>
{
    internal HasEntityState Raw { get; init; }

    public string EntityId => Raw.EntityId;

    public double LastChanged => Raw.LastChanged;
    public double LastReported => Raw.LastReported;
    public string ObjectId => Raw.ObjectId;
    public string Domain => Raw.Domain;

    internal EntityRef(HasEntityState raw) => Raw = raw;

    internal TValue? GetAttribute<TValue>(string key)
    {
        var found = Raw.Attributes.TryGetValue(key, out var value);
        if (!found) return default;

        return (TValue?)value;
    }

    public EntityRef<T>? Previous
    {
        get
        {
            if (Raw.OldState == null) return null;
            return new(Raw.OldState);
        }
    }

    public bool StateChanged()
    {
        if (Previous == null) return true;
        return Raw.State != Previous.Raw.State;
    }

    public bool AttributeChanged(string key)
    {
        if (Previous == null) return true;

        var hasNew = Raw.Attributes.TryGetValue(key, out var newValue);
        var hasOld = Previous.Raw.Attributes.TryGetValue(key, out var oldValue);

        if (hasNew != hasOld) return true;
        if (!hasNew) return false;

        return newValue == oldValue;
    }
}

public class EntityRefWrapper<T>
    where T : EntityRefWrapper<T>
{
    public string EntityId { get; init; }

    public EntityRefWrapper(string entityId) => EntityId = entityId;
};

public class HaSwitch(string entityId) : EntityRefWrapper<HaSwitch>(entityId);

public sealed class HaLight(string entityId) : EntityRefWrapper<HaLight>(entityId);

public sealed class HaInputNumber(string entityId) : EntityRefWrapper<HaInputNumber>(entityId);

public sealed class HaSun(string entityId) : EntityRefWrapper<HaSun>(entityId);

public sealed class HaBinarySensor(string entityId) : EntityRefWrapper<HaBinarySensor>(entityId);

public sealed class HaSensor(string entityId) : EntityRefWrapper<HaSensor>(entityId);

public sealed class HaInputButton(string entityId) : EntityRefWrapper<HaInputButton>(entityId);

public sealed class HaMediaPlayer(string entityId) : EntityRefWrapper<HaMediaPlayer>(entityId);

public sealed class HaUnknown(string entityId) : EntityRefWrapper<HaUnknown>(entityId);

public sealed class ShellyButton(string entityId) : EntityRefWrapper<ShellyButton>(entityId);

public static class EntityRefExtensions
{
    extension(EntityRef<HaUnknown> unknown)
    {
        public string Value => unknown.Raw.State;
        public EntityRef<T> As<T>() => (unknown as EntityRef<T>)!;
    }

    extension(EntityRef<HaInputNumber> num)
    {
        public float Value => float.Parse(num.Raw.State);

        public void SetValue(int value)
        {
            HassServices.CallService("input_number", "set_value", new
            {
                entity_id = num.EntityId,
                value,
            });
        }
    }

    extension(EntityRef<HaSwitch> sw)
    {
        public bool IsOn() => sw.Raw.State == "on";
        public bool IsOff() => sw.Raw.State == "off";

        public void TurnOn()
        {
            if (sw.IsOn()) return;
            HassServices.CallService("switch", "turn_on", new
            {
                entity_id = sw.EntityId,
            });
        }

        public void TurnOff()
        {
            if (sw.IsOff()) return;
            HassServices.CallService("switch", "turn_off", new
            {
                entity_id = sw.EntityId,
            });
        }

        public void Toggle()
        {
            HassServices.CallService("switch", "toggle", new
            {
                entity_id = sw.EntityId,
            });
        }
    }

    extension(EntityRef<ShellyButton> btn)
    {
        public string? Value => btn.GetAttribute<string>("event_type");

        bool IsState(string state) => btn.Value == state;

        public bool IsSinglePress() => IsState(btn, "press");
        public bool IsDoublePress() => IsState(btn, "double_press");
        public bool IsTriplePress() => IsState(btn, "triple_press");
        public bool IsLongPress() => IsState(btn, "long_press");
        public bool IsLongDoublePress() => IsState(btn, "long_double_press");
        public bool IsLongTriplePress() => IsState(btn, "long_triple_press");
        public bool IsHoldPress() => IsState(btn, "hold_press");
    }

    extension(EntityRef<HaSun> sun)
    {
        public bool IsRising() => sun.GetAttribute<bool>("rising");

        public bool IsBelowHorizon() => sun.Raw.State == "below_horizon";
        public bool IsAboveHorizon() => sun.Raw.State == "above_horizon";
    }

    extension(EntityRef<HaMediaPlayer> p)
    {
        public void PlayMedia(string media, string? mediaContentType = null)
        {
            HassServices.CallService("media_player", "play_media", new
            {
                target = new
                {
                    entity_id = p.EntityId,
                },
                data = new
                {
                    media = new
                    {
                        media_content_id = $"media-source://local/{media}",
                        media_content_type = mediaContentType ?? "audio/mpeg",
                        metadata = new
                        {
                            title = media,
                            media_class = "music"
                        }
                    }
                }
            });
        }
    }
}
