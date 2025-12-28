namespace HassSharp;

class InitializingException : Exception
{
    public InitializingException() : base("User Script Initializing")
    {
    }
}

public abstract class UserScriptClassBase
{
    internal UserScriptClass UserScriptClass { get; set; } = null!;
    public bool Initializing => UserScriptClass.Initializing;

    protected void InitGuard()
    {
        if (Initializing) throw new InitializingException();
    }
}
