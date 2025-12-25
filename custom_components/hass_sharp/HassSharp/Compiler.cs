using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace HassSharp;

public class CodeCompiler(
    DiagnosticsProvider _diagnosticsProvider)
{
    readonly UserScriptCacheProvider _cacheProvider = new();

    public Task<Assembly?> CompileFromUserScriptsFolder() => CompileFromFolder(HassPath.UserScripts);

    async Task<Assembly?> CompileFromFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
        }

        var filePaths = Directory.GetFiles(folderPath, "*.cs");

        var sources = await Task.WhenAll(filePaths.Select(path => File.ReadAllTextAsync(path)));

        // If there are no scripts, return an empty runner
        if (sources.Length == 0)
        {
            return null;
        }

        var userScriptsHash = _cacheProvider.ComputeHash(filePaths, sources);

        var cachedBytes = await _cacheProvider.GetScriptsCached(userScriptsHash);

        if (cachedBytes != null)
        {
            Logger.Info("No change detected in user scripts, using dll");
            return Assembly.Load(cachedBytes);
        }

        Logger.Info("User scripts change detected, recompiling");

        var assemblyBytes = CompileUserScriptToBytes(sources);
        await _cacheProvider.SetScriptsCache(userScriptsHash, assemblyBytes);
        return Assembly.Load(assemblyBytes);
    }

    byte[] CompileUserScriptToBytes(string[] sources)
    {
        SyntaxTree[] syntaxTrees =
        [
            ..sources.Select(source => CSharpSyntaxTree.ParseText(DiagnosticsProvider.GlobalUsings + source)),
            _diagnosticsProvider.HassEntitiesSyntaxTree(),
        ];

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

        if (result.Success) return ms.ToArray();

        var errors = string.Join("\n",
            result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
        );

        throw new Exception(errors);
    }

    byte[] CompileUserScriptToBytes(string source) => CompileUserScriptToBytes([source]);
}
