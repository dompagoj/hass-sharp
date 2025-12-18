namespace ExampleUserDll;

using HassSharp;

public class ExampleAutomation : Automation
{
    public void UserCustomDllAutomation()
    {
        var button = Entity("input_button.test");

        if (Initializing) return;

        var inputNumber = EntityUntracked<HaInputNumber>("input_number.test");

        Logger.Info("Setting number from user custom dll to 50");
        inputNumber.SetValue(25);
    }
}
