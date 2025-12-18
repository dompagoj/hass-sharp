public class ExampleAutomation : Automation
{
    public void OnButton()
    {
        var buttonState = Entity("input_button.test");

        Logger.Info("Wuhu reloading!!");

        if (Initializing) return;

        var inputNumber = EntityUntracked<HaInputNumber>("input_number.test");

        Logger.Info("Setting to 50");

        inputNumber.SetValue(50);
    }
}