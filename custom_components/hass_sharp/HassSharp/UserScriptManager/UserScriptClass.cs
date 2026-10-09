using System.Reflection;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HassSharp;

class UserScriptClass
{
    internal UserScript Script { get; }
    Type ClassType { get; }
    public string ClassName => ClassType.FullName ?? "Unknown";
    public MethodInfo[] Methods { get; }
    internal Automation Instance { get; }

    internal bool Initializing { get; set; } = true;

    internal UserScriptClass(Type classType, UserScript script)
    {
        var instance = (Automation)Activator.CreateInstance(classType)!;
        MethodInfo[] methods;
        if (classType.IsSubclassOf(typeof(RunnableClassScript)))
        {
            Logger.Debug($"Got runnable! ${classType.Name}");
            // ReSharper disable once EntityNameCapturedOnly.Local
            var runMethod = nameof(RunnableClassScript.Run);
            var runMethodInfo = classType.GetMethod(runMethod);
            if (runMethodInfo is null) throw new Exception($"RunnableClassScript doesnt have a {runMethodInfo} method");
            methods = [runMethodInfo];
        }
        else
        {
            methods = classType.GetMethods().Where(t => t.DeclaringType == classType).ToArray();
        }

        ClassType = classType;
        Script = script;
        Methods = methods;
        Instance = instance;
        Instance.UserScriptClass = this;
    }

    internal async Task Initialize()
    {
        Initializing = true;
        Logger.Info(
            $"Initializing Class {ClassName} with methods: {string.Join('\n', Methods.Select(m => m.Name))}");

        if (Instance is RunnableClassScript script)
        {
            await script.Initialize();
        }
        else
        {
            await RunAllMethods();
        }

        Initializing = false;
    }

    internal Task RunMethod(string method)
    {
        if (Instance is RunnableClassScript)
            method = nameof(RunnableClassScript.Run);

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
            mode
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
