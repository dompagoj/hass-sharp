using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace HassSharp;

public class CodeCompiler(
    DiagnosticsProvider _diagnosticsProvider)
{
    readonly UserScriptCacheProvider _cacheProvider = new();

    public Task<List<CompiledUserScript>> CompileFromUserScriptsFolder() => CompileFromFolder(HassPath.UserScripts);

    public async Task<CompiledUserScript> CompileSingleFile(string path, string source)
    {
        var hash = _cacheProvider.ComputeHash(path, source);
        var assemblyBytes = CompileSingleFileToBytes(source, path);
        await _cacheProvider.SetFileCache(path, hash, assemblyBytes);

        return new CompiledUserScript
        {
            Assembly = Assembly.Load(assemblyBytes),
            FilePath = path,
            FileName = Path.GetFileNameWithoutExtension(path)
        };
    }

    async Task<List<CompiledUserScript>> CompileFromFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
        }

        var filePaths = Directory.GetFiles(folderPath, "*.cs");

        var tasks = filePaths.Select(async path =>
        {
            var source = await File.ReadAllTextAsync(path);
            var hash = _cacheProvider.ComputeHash(path, source);
            var cachedBytes = await _cacheProvider.GetFileCache(path, hash);

            if (cachedBytes != null)
            {
                Logger.Info($"No change detected in {Path.GetFileName(path)}, using cached dll");
                return new CompiledUserScript
                {
                    Assembly = Assembly.Load(cachedBytes),
                    FilePath = path,
                    FileName = Path.GetFileNameWithoutExtension(path)
                };
            }

            Logger.Info($"Change detected in {Path.GetFileName(path)}, recompiling");
            var assemblyBytes = CompileSingleFileToBytes(source, path);
            await _cacheProvider.SetFileCache(path, hash, assemblyBytes);
            return new CompiledUserScript
            {
                Assembly = Assembly.Load(assemblyBytes),
                FilePath = path,
                FileName = Path.GetFileNameWithoutExtension(path)
            };
        });

        var assemblies = await Task.WhenAll(tasks);
        return [.. assemblies];
    }

    byte[] CompileSingleFileToBytes(string source, string path)
    {
        var tree = CSharpSyntaxTree.ParseText(DiagnosticsProvider.GlobalUsings + source, path: path);

        SyntaxTree[] syntaxTrees =
        [
            tree,
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
}
