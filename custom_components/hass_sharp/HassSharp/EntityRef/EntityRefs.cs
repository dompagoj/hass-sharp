namespace HassSharp;

public class EntityRef<T>
{
    internal Automation Automation { get; init; } = null!;

    public required HasEntityState Raw { get; init; }
    public string EntityId => Raw.EntityId;

    public EntityRef<T>? Previous
    {
        get
        {
            if (Raw.OldState == null) return null;
            return new()
            {
                Automation = Automation,
                Raw = Raw.OldState
            };
        }
    }
}

public class EntityRefWrapper<T>
{
    public string EntityId { get; init; }

    public EntityRefWrapper(string entityId) => EntityId = entityId;
};

public sealed class HaSwitch(string entityId) : EntityRefWrapper<HaSwitch>(entityId);

public sealed class HaNumberEntity(string entityId) : EntityRefWrapper<int>(entityId);

public sealed class HaFloatEntity(string entityId) : EntityRefWrapper<float>(entityId);

public sealed class HaInputNumber(string entityId) : EntityRefWrapper<HaInputNumber>(entityId);

public sealed class HaSun(string entityId) : EntityRefWrapper<HaSun>(entityId);

public sealed class HaBinarySensor(string entityId) : EntityRefWrapper<HaBinarySensor>(entityId);

public sealed class HaSensor(string entityId) : EntityRefWrapper<HaSensor>(entityId);

public sealed class HaInputButton(string entityId) : EntityRefWrapper<HaInputButton>(entityId);

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
            eRef.Automation.CallService("input_number", "set_value", new
            {
                entity_id = eRef.EntityId,
                value,
            });
        }
    }

    extension(EntityRef<HaSwitch> eRef)
    {
        public void TurnOn()
        {
            eRef.Automation.CallService("switch", "turn_on", new
            {
                entity_id = eRef.EntityId,
            });
        }

        public void TurnOff()
        {
            eRef.Automation.CallService("switch", "turn_off", new
            {
                entity_id = eRef.EntityId,
            });
        }

        public void Toggle()
        {
            eRef.Automation.CallService("switch", "toggle", new
            {
                entity_id = eRef.EntityId,
            });
        }
    }

    extension(EntityRef<HaInputButton> eRef)
    {
        public void SinglePress()
        {
        }

        public void DoublePress()
        {
        }

        public void TriplePress()
        {
        }

        public void QuadPress()
        {
        }

        public void Hold()
        {
        }
    }

    extension(EntityRef<HaSun> eRef)
    {
        public bool IsRising() => (bool)eRef.Raw.Attributes["rising"];

        public bool IsBelowHorizon() => eRef.Raw.State == "below_horizon";
        public bool IsAboveHorizon() => eRef.Raw.State == "above_horizon";
    }
}
