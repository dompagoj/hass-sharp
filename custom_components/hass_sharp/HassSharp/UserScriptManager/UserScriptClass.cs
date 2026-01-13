using System.Reflection;

namespace HassSharp;

class UserScriptClass
{
    internal UserScript Script { get; }
    internal Type ClassType { get; }
    public string ClassName => ClassType.FullName ?? "Unknown";
    public MethodInfo[] Methods { get; }
    internal Automation Instance { get; }


    internal bool Initializing { get; set; } = true;

    internal UserScriptClass(Type classType, UserScript script)
    {
        ClassType = classType;
        Script = script;
        Methods = classType.GetMethods().Where(t => t.DeclaringType == classType).ToArray();
        Instance = (Automation)Activator.CreateInstance(classType)!;
        Instance.UserScriptClass = this;
    }

    internal async Task Initialize()
    {
        Initializing = true;
        Logger.Info(
            $"Initializing Class {ClassName} with methods: {string.Join('\n', Methods.Select(m => m.Name))}");
        await RunAllMethods();
        Initializing = false;
    }

    internal Task RunMethod(string method)
    {
        var found = Methods.FirstOrDefault(m => m.Name == method);

        if (found == null)
        {
            var ex = new Exception("Method not found");
            Logger.Error($"Method not found {GetConcatedMethodNameWithClass(method)} \n {ex.StackTrace}");
            throw ex;
        }

        return RunMethod(found);
    }

    internal async Task RunMethod(MethodInfo method)
    {
        var mode = method.GetCustomAttribute<ModeAttribute>()?.Mode ?? AutomationMode.Single;

        await Script.ScriptManager.SyncRunner.Run(
            this,
            method,
            mode,
            () => method.Invoke(
                Instance,
                BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                null,
                null,
                null
            )
        );
    }

    internal async Task RunAllMethods()
    {
        foreach (var method in Methods) await RunMethod(method);
    }

    internal string GetConcatedMethodNameWithClass(string methodName)
    {
        return CombineClassAndMethod(ClassName, methodName);
    }

    internal string CombineClassAndMethod(string klass, string method) => $"{klass}::{method}";
}
