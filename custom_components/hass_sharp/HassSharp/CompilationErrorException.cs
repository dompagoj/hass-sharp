namespace HassSharp;

public class CompilationErrorException(string[] errors) : Exception("Compilation exception")
{
    public string[] Errors => errors;
}
