using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace HassSharp;

public class CodeCompiler
{
    const string CacheDllName = "hass_sharp_user_scripts.dll";
    const string CacheHashName = "hass_sharp_user_scripts.hash";


    public DiagnosticsProvider Diagnostics { get; } = new();

    public void SetHassPaths(string configurationFolderPath)
    {
        HassPath.HassConfiguration = configurationFolderPath;
    }


    public CodeRunner CompileFromUserScriptsFolder()
    {
        var userScriptsFolder = HassPath.UserScripts;
        if (!Directory.Exists(userScriptsFolder))
        {
            Directory.CreateDirectory(userScriptsFolder);
        }

        var filePaths = Directory.GetFiles(userScriptsFolder, "*.cs");

        var sourcesTask = filePaths.Select(path => File.ReadAllTextAsync(path));
        var sources = Task.WhenAll(sourcesTask).GetAwaiter().GetResult();

        // If there are no scripts, return an empty runner
        if (sources.Length == 0)
        {
            return new CodeRunner();
        }

        var hash = ComputeHash(filePaths, sources);

        var cacheDllPath = Path.Combine(userScriptsFolder, CacheDllName);
        var cacheHashPath = Path.Combine(userScriptsFolder, CacheHashName);

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

    public CodeRunner Compile(string[] sources)
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

    byte[] CompileToAssemblyBytes(string[] sources)
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

        syntaxTrees.Add(Diagnostics.HassEntitiesSyntaxTree());

        var references = ProjectReferences.GetDefaultReferences();

        var compilationOptions = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release
        );

        var compilation = CSharpCompilation.Create(
            assemblyName: "Automation_" + Guid.NewGuid(),
            syntaxTrees: syntaxTrees,
            references: references,
            options: compilationOptions
        );

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

    string ComputeHash(string[] filePaths, string[] sources)
    {
        using var sha = SHA256.Create();

        // // Include the entities source in the hash to force a recompile if they change
        // if (EntitiesTree != null)
        // {
        //     var entitiesBytes = Encoding.UTF8.GetBytes(EntitiesTree.ToString());
        //     sha.TransformBlock(entitiesBytes, 0, entitiesBytes.Length, null, 0);
        // }

        for (var i = 0; i < sources.Length; i++)
        {
            var pathBytes = Encoding.UTF8.GetBytes(filePaths[i]);
            sha.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);

            var contentBytes = Encoding.UTF8.GetBytes(sources[i]);
            sha.TransformBlock(contentBytes, 0, contentBytes.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }
}
