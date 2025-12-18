namespace HassSharp;

public class EntityRef<T>
{
    internal Automation Automation { get; init; } = null!;

    public required HasEntityState Raw { get; init; }
    public string EntityId => Raw.EntityId;
}

public sealed class HaSwitch : EntityRef<bool>;

public sealed class HaInputNumber : EntityRef<int>;

public static class AutomationExt
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
    }
}
