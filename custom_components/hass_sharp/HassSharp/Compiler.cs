using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace HassSharp;

using GetEntity = Func<string, HasEntityState?>;

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
    public static void Log(PyLogLevel level, string msg) => PyInterop.Log((int)level, msg);

    public static void Info(string msg) => Log(PyLogLevel.Info, msg);
    public static void Warn(string msg) => Log(PyLogLevel.Warn, msg);
    public static void Debug(string msg) => Log(PyLogLevel.Debug, msg);
    public static void Error(string msg) => Log(PyLogLevel.Error, msg);
}

public static class PyInterop
{
    public static Action<int, string> Log { get; set; } = null!;
    public static GetEntity Entity { get; set; } = null!;
    public static Action<string, string, string?> CallService { get; set; } = null!;
}

public static class CodeCompiler
{
    public static CodeRunner CompileFromFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
        }

        var sourcesTask = Directory.GetFiles(folderPath, "*.cs")
            .Select(p => File.ReadAllTextAsync(p));
        var sources = Task.WhenAll(sourcesTask).GetAwaiter().GetResult();

        return Compile(sources);
    }

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
        // 0️⃣ Prepend global usings to the source
        const string globalUsings = """
                                    global using System;
                                    global using System.Threading;
                                    global using System.Threading.Tasks;
                                    global using System.Collections.Generic;
                                    global using System.Linq;
                                    global using HassSharp;

                                    """;

        var fullSource = globalUsings + source;

        // Parse the C# source into a syntax tree
        var syntaxTree = CSharpSyntaxTree.ParseText(fullSource);

        // 1️⃣ Collect references from all loaded assemblies that have a file location
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .ToList();

        // 2️⃣ Explicitly add assemblies that are often missing but required
        references.Add(
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(System.Threading.Tasks.Task).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location));
        references.Add(
            MetadataReference.CreateFromFile(typeof(System.Net.Http.HttpClient).Assembly.Location));
        references.Add(
            MetadataReference.CreateFromFile(typeof(System.Net.Http.Json.HttpClientJsonExtensions).Assembly
                .Location));
        references.Add(
            MetadataReference.CreateFromFile(typeof(System.Uri).Assembly
                .Location));


        references.Add(
            MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly
                .Location));
        references.Add(
            MetadataReference.CreateFromFile(typeof(JsonSerializer).Assembly
                .Location));


        // 3️⃣ Define compilation options
        var compilationOptions = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release
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
