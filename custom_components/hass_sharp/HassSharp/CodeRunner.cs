using System.Reflection;

namespace HassSharp;

public class CodeRunner
{
    readonly List<(Type, Automation)> _automationInstances = [];
    public Dictionary<string, List<string>> DependencyTracking { get; } = new();

    public void AddInstance(Automation instance, Type type)
    {
        _automationInstances.Add((type, instance));
    }

    string CombineClassAndMethod(string klass, string method) => $"{klass}::{method}";

    (string klass, string method) UnmixClassAndMethod(string combined)
    {
        var res = combined.Split("::");
        return (res[0], res[1]);
    }

    public void TrackEntityCall(string entityId, string klass, string method)
    {
        var methodName = CombineClassAndMethod(klass, method);

        if (!DependencyTracking.TryGetValue(entityId, out var methods))
        {
            methods = new List<string>();
            DependencyTracking[entityId] = methods;
        }

        if (!methods.Contains(methodName))
        {
            methods.Add(methodName);
        }
    }

    public void RunMethod(string classAndMethod)
    {
        var (klass, method) = UnmixClassAndMethod(classAndMethod);

        var foundIdx = _automationInstances.FindIndex(i => i.Item1.FullName == klass);
        if (foundIdx == -1)
        {
            Logger.Error($"Failed to find {classAndMethod}");
            return;
        }

        var (type, instance) = _automationInstances[foundIdx];

        var result = type.InvokeMember(method,
            BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            null,
            instance,
            null
        );

        if (result is Task task)
        {
            task.GetAwaiter().GetResult();
        }
    }

    public void RunAll()
    {
        foreach (var (type, instance) in _automationInstances)
        {
            Logger.Info($"Running Class {type.Name}");

            foreach (var method in type.GetMethods().Where(t => t.DeclaringType == type))
            {
                Logger.Info($"Running Method {method.Name}");
                try
                {
                    var result = method.Invoke(instance,
                        BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.DeclaredOnly,
                        null, null, null);

                    if (result is Task task)
                    {
                        task.GetAwaiter().GetResult();
                    }

                    instance.Initializing = false;
                }
                catch (Exception e)
                {
                    Logger.Error($"{e.Message} \n {e.StackTrace} \n {e.InnerException?.Message}");
                }
            }
        }
    }
}
