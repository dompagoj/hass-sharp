public class ExampleAutomation : Automation
{
    public void OnButton()
    {
        var buttonState = Entity("input_button.test");

        if (Initializing) return;

        Logger.Info("Button was pressed!");
    }
}