using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Text;

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
    const string CacheDllName = "hass_sharp_user_scripts.dll";
    const string CacheHashName = "hass_sharp_user_scripts.hash";

    static readonly AdhocWorkspace Workspace;
    static readonly Project BaseProject;
    static SyntaxTree EntitiesTree = null!;

    static CodeCompiler()
    {
        var host = MefHostServices.Create(MefHostServices.DefaultAssemblies);
        Workspace = new AdhocWorkspace(host);

        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "AutomationProject",
            "AutomationProject",
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest)
        );

        BaseProject = Workspace.AddProject(projectInfo);

        // Add default references to the workspace project
        var references = GetDefaultReferences();
        Workspace.TryApplyChanges(Workspace.CurrentSolution.WithProjectMetadataReferences(BaseProject.Id, references));
    }

    public static void InitializeEntities(string[] entityIds)
    {
        Logger.Info("Generating entities...");
        var source = EntityGenerator.Generate(entityIds);
        Logger.Info($"Generated Enttity class: \n {source}");
        EntitiesTree = CSharpSyntaxTree.ParseText(source);

        // Update the workspace with the generated entities
        var documentId = DocumentId.CreateNewId(BaseProject.Id);
        var solution = Workspace.CurrentSolution.AddDocument(documentId, "Entities.g.cs", source);
        Workspace.TryApplyChanges(solution);
    }

    public class DiagnosticModel
    {
        public int StartLine { get; set; }
        public int StartColumn { get; set; }
        public int EndLine { get; set; }
        public int EndColumn { get; set; }
        public string Message { get; set; } = null!;
        public int Severity { get; set; }
    }

    public static List<DiagnosticModel> GetDiagnostics(string source)
    {
        const string globalUsings = """
                                    global using System;
                                    global using System.Threading;
                                    global using System.Threading.Tasks;
                                    global using System.Collections.Generic;
                                    global using System.Linq;
                                    global using HassSharp;

                                    """;
        var syntaxTree = CSharpSyntaxTree.ParseText(globalUsings + source);
        var references = GetDefaultReferences();

        var compilation = CSharpCompilation.Create(
            "Diagnostics_" + Guid.NewGuid(),
            [syntaxTree, EntitiesTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var prefixLineCount = globalUsings.Count(c => c == '\n');

        return compilation.GetDiagnostics()
            .Where(d => d.Location.GetLineSpan().StartLinePosition.Line >= prefixLineCount)
            .Select(d =>
            {
                var lineSpan = d.Location.GetLineSpan();
                // Roslyn lines are 0-based.
                var startLine = lineSpan.StartLinePosition.Line - prefixLineCount + 1;
                var endLine = lineSpan.EndLinePosition.Line - prefixLineCount + 1;

                return new DiagnosticModel
                {
                    StartLine = startLine,
                    StartColumn = lineSpan.StartLinePosition.Character + 1,
                    EndLine = endLine,
                    EndColumn = lineSpan.EndLinePosition.Character + 1,
                    Message = d.GetMessage(),
                    Severity = (int)d.Severity
                };
            })
            .ToList();
    }

    public static string GetCompletions(string source, int position)
    {
        const string globalUsings = """
                                    global using System;
                                    global using System.Threading;
                                    global using System.Threading.Tasks;
                                    global using System.Collections.Generic;
                                    global using System.Linq;
                                    global using HassSharp;

                                    """;
        var fullSource = globalUsings + source;
        var adjustedPosition = globalUsings.Length + position;

        var document = Workspace.AddDocument(BaseProject.Id, "Script.cs", SourceText.From(fullSource));
        var completionService = CompletionService.GetService(document);
        if (completionService == null) return string.Empty;

        var completionsTask = completionService.GetCompletionsAsync(document, adjustedPosition);

        var completions = completionsTask.GetAwaiter().GetResult();

        Workspace.TryApplyChanges(document.Project.Solution.RemoveDocument(document.Id));

        return JsonSerializer.Serialize(completions.ItemsList.Take(50), JsonSerializerOptions.Web);
    }

    static List<MetadataReference> GetDefaultReferences()
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();

        references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(JsonSerializer).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(Task).Assembly.Location));
        return references;
    }

    public static CodeRunner CompileFromFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
        }

        var filePaths = Directory.GetFiles(folderPath, "*.cs");

        var sourcesTask = filePaths.Select(path => File.ReadAllTextAsync(path));
        var sources = Task.WhenAll(sourcesTask).GetAwaiter().GetResult();

        // If there are no scripts, return an empty runner
        if (sources.Length == 0)
        {
            return new CodeRunner();
        }

        var hash = ComputeHash(filePaths, sources);

        var cacheDllPath = Path.Combine(folderPath, CacheDllName);
        var cacheHashPath = Path.Combine(folderPath, CacheHashName);

        if (File.Exists(cacheDllPath) && File.Exists(cacheHashPath))
        {
            var existingHash = File.ReadAllText(cacheHashPath);
            if (existingHash == hash)
            {
                var cachedBytes = File.ReadAllBytes(cacheDllPath);
                Logger.Info("Detected no changes from user scripts, using cached DLL");
                return BuildRunnerFromAssemblyBytes(cachedBytes);
            }
        }

        Logger.Info("Compiling user scripts...");

        var assemblyBytes = CompileToAssemblyBytes(sources);
        File.WriteAllBytes(cacheDllPath, assemblyBytes);
        File.WriteAllText(cacheHashPath, hash);

        return BuildRunnerFromAssemblyBytes(assemblyBytes);
    }

    public static CodeRunner Compile(string[] sources)
    {
        if (sources.Length == 0)
        {
            return new CodeRunner();
        }

        var assemblyBytes = CompileToAssemblyBytes(sources);
        return BuildRunnerFromAssemblyBytes(assemblyBytes);
    }

    static CodeRunner BuildRunnerFromAssemblyBytes(byte[] assemblyBytes)
    {
        var assembly = Assembly.Load(assemblyBytes);
        var baseType = typeof(Automation);

        var codeRunner = new CodeRunner();

        foreach (var automationClass in assembly.GetTypes()
                     .Where(t => baseType.IsAssignableFrom(t) && !t.IsAbstract))
        {
            var instance = (Automation)Activator.CreateInstance(automationClass)!;
            instance.Runner = codeRunner;
            if (automationClass.FullName != null) instance.UserClassName = automationClass.FullName;
            codeRunner.AddInstance(instance, automationClass);
        }

        return codeRunner;
    }

    static byte[] CompileToAssemblyBytes(string[] sources)
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

        var syntaxTrees = sources
            .Select(source => CSharpSyntaxTree.ParseText(globalUsings + source))
            .ToList();

        syntaxTrees.Add(EntitiesTree);

        // 1️⃣ Collect references from all loaded assemblies that have a file location
        var references = GetDefaultReferences();

        // 3️⃣ Define compilation options
        var compilationOptions = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release
        );

        // 4️⃣ Create the compilation
        var compilation = CSharpCompilation.Create(
            assemblyName: "Automation_" + Guid.NewGuid(),
            syntaxTrees: syntaxTrees,
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

    static string ComputeHash(string[] filePaths, string[] sources)
    {
        using var sha = SHA256.Create();

        // Include the entities source in the hash to force a recompile if they change
        if (EntitiesTree != null)
        {
            var entitiesBytes = Encoding.UTF8.GetBytes(EntitiesTree.ToString());
            sha.TransformBlock(entitiesBytes, 0, entitiesBytes.Length, null, 0);
        }

        for (var i = 0; i < sources.Length; i++)
        {
            var pathBytes = Encoding.UTF8.GetBytes(filePaths[i]);
            sha.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);

            var contentBytes = Encoding.UTF8.GetBytes(sources[i]);
            sha.TransformBlock(contentBytes, 0, contentBytes.Length, null, 0);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }
}
