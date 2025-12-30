namespace HassSharp;

public class EntityRef<T>
{
    // internal Automation Automation { get; init; } = null!;

    public required HasEntityState Raw { get; init; }
    public string EntityId => Raw.EntityId;

    public EntityRef<T>? Previous
    {
        get
        {
            if (Raw.OldState == null) return null;
            return new()
            {
                // Automation = Automation,
                Raw = Raw.OldState
            };
        }
    }
}

public class EntityRefWrapper<T>
    where T : EntityRefWrapper<T>
{
    public string EntityId { get; init; }

    protected EntityRefWrapper(string entityId) => EntityId = entityId;
};

public class HaSwitch(string entityId) : EntityRefWrapper<HaSwitch>(entityId);

public sealed class HaLight(string entityId) : HaSwitch(entityId);

public sealed class HaInputNumber(string entityId) : EntityRefWrapper<HaInputNumber>(entityId);

public sealed class HaSun(string entityId) : EntityRefWrapper<HaSun>(entityId);

public sealed class HaBinarySensor(string entityId) : EntityRefWrapper<HaBinarySensor>(entityId);

public sealed class HaSensor(string entityId) : EntityRefWrapper<HaSensor>(entityId);

public sealed class HaInputButton(string entityId) : EntityRefWrapper<HaInputButton>(entityId);

public sealed class ShellyButton(string entityId) : EntityRefWrapper<ShellyButton>(entityId);

public static class EntityRefExtensions
{
    extension(EntityRef<int> entityRef)
    {
        public int Value
        {
            get
            {
                var success = int.TryParse(entityRef.Raw.State, out var result);
                if (success) return result;

                return (int)Math.Floor(float.Parse(entityRef.Raw.State));
            }
        }
    }

    extension(EntityRef<string> entityRef)
    {
        public string Value => entityRef.Raw.State;
    }

    extension(EntityRef<float> entityRef)
    {
        public float Value => float.Parse(entityRef.Raw.State);
    }

    extension(EntityRef<HaInputNumber> eRef)
    {
        public float Value => float.Parse(eRef.Raw.State);

        public void SetValue(int value)
        {
            HassServices.CallService("input_number", "set_value", new
            {
                entity_id = eRef.EntityId,
                value,
            });
        }
    }

    extension(EntityRef<HaSwitch> eRef)
    {
        public bool IsOn() => eRef.Raw.State == "on";
        public bool IsOff() => eRef.Raw.State == "off";

        public void TurnOn()
        {
            if (eRef.IsOn()) return;
            HassServices.CallService("switch", "turn_on", new
            {
                entity_id = eRef.EntityId,
            });
        }

        public void TurnOff()
        {
            if (eRef.IsOff()) return;
            HassServices.CallService("switch", "turn_off", new
            {
                entity_id = eRef.EntityId,
            });
        }

        public void Toggle()
        {
            HassServices.CallService("switch", "toggle", new
            {
                entity_id = eRef.EntityId,
            });
        }
    }

    extension(EntityRef<ShellyButton> eRef)
    {
        bool GetState(string state) => (string)eRef.Raw.Attributes["event_type"] == state;

        public bool IsSinglePress() => GetState(eRef, "press");
        public bool IsDoublePress() => GetState(eRef, "double_press");
        public bool IsTriplePress() => GetState(eRef, "tripple_press");
        public bool IsIsLongPress() => GetState(eRef, "long_press");
        public bool IsIsLongDoublePress() => GetState(eRef, "long_double_press");
        public bool IsIsLongTriplePress() => GetState(eRef, "long_triple_press");
        public bool IsHoldPress() => GetState(eRef, "hold_press");
    }

    extension(EntityRef<HaSun> eRef)
    {
        public bool IsRising() => (bool)eRef.Raw.Attributes["rising"];

        public bool IsBelowHorizon() => eRef.Raw.State == "below_horizon";
        public bool IsAboveHorizon() => eRef.Raw.State == "above_horizon";
    }
}
