using System.Reflection;

namespace HassSharp;

public class UserScript
{
    public required UserScriptClass[] Classes { get; init; }
    public required string FilePath { get; init; }
    public required string FileName { get; init; }

    public required Assembly Assembly { get; init; }

    public string ScriptId() => FilePath;
    public string ScriptSlug() => FileName;
}
