using System.Reflection;

namespace HassSharp;

class UserScript
{
    public required UserScriptManager ScriptManager { get; init; }
    public required UserScriptClass[] Classes { get; set; }
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required string SourceCode { get; init; }

    public required Assembly Assembly { get; init; }

    public List<string> UsedEntityIds { get; init; } = new();

    public string ScriptId() => FilePath;
    public string ScriptSlug() => FileName;

    internal Task RunMethod(UserScriptClass @class, string method) => @class.RunMethod(method);
}
