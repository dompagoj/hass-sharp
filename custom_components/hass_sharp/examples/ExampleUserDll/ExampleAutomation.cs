namespace ExampleUserDll;
using HassSharp;

public class ExampleAutomation : Automation
{
    public override AutomationConfiguration Configure() => new(
        new AutomationAction(nameof(Run), Entities: ["input_button.test"]));

    public async Task Run()
    {
        CancellationToken.ThrowIfCancellationRequested();
        await Entity("input_number.test").As<HaInputNumber>().SetValue(25).RunAsync();
    }
}
