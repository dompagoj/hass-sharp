namespace ExampleUserDll;
using HassSharp;

public class ExampleAutomation : Automation
{
    public override AutomationConfiguration Configure() => new(
        new AutomationAction(nameof(Run), Entities: [ExampleEntities.TestButton]));

    public async Task Run()
    {
        CancellationToken.ThrowIfCancellationRequested();
        await Entity(ExampleEntities.TestNumber).SetValue(25).RunAsync();
    }
}

// External DLL builds supply a typed catalog; the in-app editor generates Entities automatically.
static class ExampleEntities
{
    public static EntityRefWrapper<HaInputButton> TestButton => new("input_button.test");
    public static EntityRefWrapper<HaInputNumber> TestNumber => new("input_number.test");
}
