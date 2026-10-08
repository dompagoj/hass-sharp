namespace ExampleUserDll;

using HassSharp;

public class ExampleAutomation : Automation
{
    public void UserCustomDllAutomation()
    {
        var button = Entity("input_button.test");

        InitGuard();

        var inputNumber = EntityUntracked("input_number.test").As<HaInputNumber>();

        Logger.Info("Setting number from user custom dll to 50");
        inputNumber.SetValue(25);
    }
}
