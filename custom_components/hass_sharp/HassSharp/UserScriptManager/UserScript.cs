using System.Reflection;
using System.Text.Json.Serialization;

namespace HassSharp;

class UserScriptComparer : IEqualityComparer<UserScript>
{
    public bool Equals(UserScript? x, UserScript? y)
    {
        if (x == null || y == null) return false;

        return x.ClassType == y.ClassType;
    }

    public int GetHashCode(UserScript obj) => obj.ClassType.GetHashCode();
}

public class UserScript
{
    internal Type ClassType { get; set; }
    public string ClassName => ClassType.FullName ?? "Unknown";
    [JsonIgnore] public MethodInfo[] Methods { get; private set; }
    Automation Instance { get; set; }

    internal UserScriptManager Runner { get; }

    internal bool Initializing { get; set; } = true;

    internal UserScript(Type classType, UserScriptManager runner)
    {
        ClassType = classType;
        Methods = classType.GetMethods().Where(t => t.DeclaringType == classType).ToArray();
        Instance = (Automation)Activator.CreateInstance(classType)!;
        Runner = runner;

        Instance.UserScript = this;
    }

    internal ValueTask RunMethod(string method)
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

    internal ValueTask RunMethod(MethodInfo method)
    {
        var result = method.Invoke(
            Instance,
            BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            null,
            null,
            null
        );

        return result switch
        {
            ValueTask valueTask => valueTask,
            Task task => new(task),
            _ => ValueTask.CompletedTask
        };
    }

    internal async ValueTask RunAllMethods()
    {
        foreach (var method in Methods) await RunMethod(method);
    }

    string GetConcatedMethodNameWithClass(string methodName)
    {
        return CombineClassAndMethod(ClassName, methodName);
    }

    string CombineClassAndMethod(string klass, string method) => $"{klass}::{method}";
}
