using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace HassSharp;

using GetEntity = Func<string, string, HasEntityState?>;

public class HasEntityState
{
    public string EntityId { get; set; } = null!;
    public string Domain { get; set; } = null!;
    public string ObjectId { get; set; } = null!;
    public string State { get; set; } = null!;
    public Dictionary<string, object> Attributes { get; set; } = null!;
    public float LastChanged { get; set; }
    public float LastReported { get; set; }
}

public enum PyLogLevel
{
    Info = 20,
    Error = 40,
    Warn = 30,
    Debug = 10,
    Critical = 50,
}

public static class Logger
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Log(PyLogLevel level, string msg) => PyInterp.Log((int)level, msg);

    public static void Info(string msg) => Log(PyLogLevel.Info, msg);
    public static void Warn(string msg) => Log(PyLogLevel.Warn, msg);
    public static void Debug(string msg) => Log(PyLogLevel.Debug, msg);
    public static void Error(string msg) => Log(PyLogLevel.Error, msg);
}

public static class PyInterp
{
    public static Action<int, string> Log { get; set; } = null!;
    public static GetEntity Entity { get; set; } = null!;
}

public class CodeRunner
{
    readonly List<(Type, Automation)> _automationInstances = [];
    public Dictionary<string, List<string>> DependencyTracking { get; } = new();

    public void AddInstance(Automation instance, Type type)
    {
        _automationInstances.Add((type, instance));
    }

    public void RunMethod(string methodName)
    {
        var (type, instance) = _automationInstances.First();

        type.InvokeMember(methodName,
            BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            null,
            instance,
            null
        );
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
                    method.Invoke(instance, BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.DeclaredOnly,
                        null, null, null);

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

public static class CodeCompiler
{
    public static CodeRunner Compile(string[] sources)
    {
        var assemblies = sources.Select(CompilePriv);
        var baseType = typeof(Automation);

        var codeRunner = new CodeRunner();

        foreach (var assemblyBytes in assemblies)
        {
            var assembly = Assembly.Load(assemblyBytes);

            foreach (var automationClass in assembly.GetTypes()
                         .Where(t => baseType.IsAssignableFrom(t) && !t.IsAbstract))
            {
                var instance = (Automation)Activator.CreateInstance(automationClass)!;
                instance.Runner = codeRunner;
                codeRunner.AddInstance(instance, automationClass);
            }
        }

        return codeRunner;
    }

    static byte[] CompilePriv(string source)
    {
        // Parse the C# source into a syntax tree
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        // 1️⃣ Collect references from all loaded assemblies that have a file location
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .ToList();

        // 2️⃣ Explicitly add assemblies that are often missing but required
        references.Add(
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location)); // mscorlib / System.Private.CoreLib
        references.Add(MetadataReference.CreateFromFile(typeof(System.Threading.Tasks.Task).Assembly.Location)); // Task
        references.Add(MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location)); // System.Linq
        references.Add(
            MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly
                .Location)); // Collections


        // 3️⃣ Define compilation options with global usings
        var compilationOptions = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release,
            usings:
            [
                "System",
                "System.Threading",
                "System.Threading.Tasks",
                "System.Collections.Generic",
                "System.Linq",
                "HassSharp" // Your DSL namespace
            ]
        );

        // 4️⃣ Create the compilation
        var compilation = CSharpCompilation.Create(
            assemblyName: "Automation_" + Guid.NewGuid(),
            syntaxTrees: [syntaxTree],
            references: references,
            options: compilationOptions
        );

        // 5️⃣ Emit the assembly to a memory stream
        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        // 6️⃣ Handle compilation errors
        if (!result.Success)
        {
            var errors = string.Join("\n",
                result.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d.ToString())
            );

            throw new Exception(errors);
        }

        // 7️⃣ Return the compiled assembly bytes
        return ms.ToArray();
    }
}

public class EntityRef<T>
{
    public required HasEntityState Raw { get; init; }
    public string EntityId => Raw.EntityId;
}

// When adding methods to this class make sure to exclude them from the CodeRunner above or they will be run as an automation and fail
public abstract class Automation
{
    public bool Initializing { get; set; } = true;
    public CodeRunner Runner { get; internal set; } = null!;

    // Injected by Python
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public HasEntityState? EntityRaw(string entityId, string caller)
    {
        if (!Runner.DependencyTracking.TryGetValue(entityId, out var methods))
        {
            methods = new List<string>();
            Runner.DependencyTracking[entityId] = methods;
        }

        if (!methods.Contains(caller))
        {
            methods.Add(caller);
        }

        return PyInterp.Entity(entityId, caller);
    }

    public EntityRef<T> Entity<T>(string entityId, [CallerMemberName] string? caller = null)
    {
        var raw = EntityRaw(entityId, caller!);

        if (raw == null) throw new($"Entity with id {entityId} not found");
        return new()
        {
            Raw = raw,
        };
    }

    public EntityRef<string> Entity(string entityId, [CallerMemberName] string? caller = null)
    {
        var raw = EntityRaw(entityId, caller!);
        if (raw == null) throw new($"Entity with id {entityId} not found");
        return new()
        {
            Raw = raw,
        };
    }
}

public static class AutomationExt
{
    extension(EntityRef<int> entityRef)
    {
        public int Value
        {
            get
            {
                var success = int.TryParse(entityRef.Raw.State, out var result);
                if (success) return result;

                return (int)Math.Floor(float.Parse(entityRef.Raw.State));
            }
        }
    }

    extension(EntityRef<string> entityRef)
    {
        public string Value => entityRef.Raw.State;
    }

    extension(EntityRef<float> entityRef)
    {
        public float Value => float.Parse(entityRef.Raw.State);
    }
}
