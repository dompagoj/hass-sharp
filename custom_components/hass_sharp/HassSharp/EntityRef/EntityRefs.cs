using System.Diagnostics.Contracts;

namespace HassSharp;

public struct EntityRef<T>
{
    internal HasEntityState Raw { get; private set; }

    public string EntityId => Raw.EntityId;

    public double LastChanged => Raw.LastChanged;
    public double LastReported => Raw.LastReported;
    public string ObjectId => Raw.ObjectId;
    public string Domain => Raw.Domain;

    internal EntityRef(HasEntityState raw) => Raw = raw;

    public TValue? GetAttribute<TValue>(string key)
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
        return Raw.State != Previous.Value.Raw.State;
    }

    public bool AttributeChanged(string key)
    {
        if (Previous == null) return true;

        var hasNew = Raw.Attributes.TryGetValue(key, out var newValue);
        var hasOld = Previous.Value.Raw.Attributes.TryGetValue(key, out var oldValue);

        if (hasNew != hasOld) return true;
        if (!hasNew) return false;

        return newValue == oldValue;
    }

    public void Refresh()
    {
        Raw = PyInterop.Entity(EntityId) ?? Raw;
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

public sealed class HaMotionSensor(string entityId) : EntityRefWrapper<HaMotionSensor>(entityId);

public sealed class ShellyButton(string entityId) : EntityRefWrapper<ShellyButton>(entityId);

public static class EntityRefExtensions
{
    extension(EntityRef<HaUnknown> unknown)
    {
        public string Value => unknown.Raw.State;
        public EntityRef<T> As<T>() => new(unknown.Raw);
    }

    extension(EntityRef<HaInputNumber> num)
    {
        public float Value => float.Parse(num.Raw.State);

        [Pure]
        public ServiceCall SetValue(int value) => HassServices.InputNumber.SetValue(num.EntityId, value);
    }

    extension(EntityRef<HaSwitch> sw)
    {
        public bool IsOn() => sw.Raw.State == "on";
        public bool IsOff() => sw.Raw.State == "off";

        public ServiceCall TurnOn() => HassServices.Switch.TurnOn(sw.EntityId);
        public ServiceCall TurnOff() => HassServices.Switch.TurnOff(sw.EntityId);
        public ServiceCall Toggle() => HassServices.Switch.Toggle(sw.EntityId);
    }

    extension(EntityRef<HaBinarySensor> s)
    {
        public bool isState(string state) => s.Raw.State == state;
        public bool isOn() => s.Raw.State == "on";
        public bool isOff() => s.Raw.State == "off";

        public EntityRef<HaMotionSensor> asMotion() => new(s.Raw);
    }

    extension(EntityRef<HaMotionSensor> m)
    {
        public bool IsMotionDetected() => m.Raw.State == "on";
    }

    extension(EntityRef<ShellyButton> btn)
    {
        public string? Value => btn.GetAttribute<string>("event_type");

        bool IsState(string state) => btn.Value == state;

        public bool IsSinglePress() => btn.IsState("press");
        public bool IsDoublePress() => btn.IsState("double_press");
        public bool IsTriplePress() => btn.IsState("triple_press");
        public bool IsLongPress() => btn.IsState("long_press");
        public bool IsLongDoublePress() => btn.IsState("long_double_press");
        public bool IsLongTriplePress() => btn.IsState("long_triple_press");
        public bool IsHoldPress() => btn.IsState("hold_press");
    }

    extension(EntityRef<HaSun> sun)
    {
        public bool IsRising() => sun.GetAttribute<bool>("rising");

        public bool IsBelowHorizon() => sun.Raw.State == "below_horizon";
        public bool IsAboveHorizon() => sun.Raw.State == "above_horizon";
    }

    extension(EntityRef<HaMediaPlayer> p)
    {
        public ServiceCall TurnOn() => HassServices.MediaPlayer.TurnOn(p.EntityId);

        public ServiceCall TurnOff() => HassServices.MediaPlayer.TurnOff(p.EntityId);

        public ServiceCall Toggle() => HassServices.MediaPlayer.Toggle(p.EntityId);

        /// <summary>
        /// Will be 0 if the media player is off, call TurnOn first and then Refresh before calling this
        /// </summary>
        public double VolumeLevel => p.GetAttribute<double>("volume_level");

        public ServiceCall PlayLocal(string media, string? mediaContentType = null)
            => HassServices.MediaPlayer.MediaPlayLocal(p.EntityId, media, mediaContentType);

        public ServiceCall TextToSpeech(string speech, string? ttsEnttiyId = null)
            => HassServices.MediaPlayer.TextToSpeech(p.EntityId, speech, ttsEnttiyId);

        public ServiceCall SetVolumeLevel(double level)
            => HassServices.MediaPlayer.SetVolumeLevel(p.EntityId, level);
    }
}
