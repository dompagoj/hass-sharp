namespace HassSharp;

class InitializingException : Exception
{
    public InitializingException() : base("User Script Initializing")
    {
    }
}

public abstract class UserScriptBase
{
    internal UserScript UserScript { get; set; } = null!;
    public bool Initializing => UserScript.Initializing;

    protected void InitGuard()
    {
        if (Initializing) throw new InitializingException();
    }
}
