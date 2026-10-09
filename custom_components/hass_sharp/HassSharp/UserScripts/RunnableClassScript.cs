namespace HassSharp;

public abstract class RunnableClassScript : Automation
{
    public virtual Task Initialize()
    {
        return Task.CompletedTask;
    }

    public abstract Task Run();
}
