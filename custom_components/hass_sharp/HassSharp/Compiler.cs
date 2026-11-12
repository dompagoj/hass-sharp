using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace HassSharp;

public class CodeCompiler(
    DiagnosticsProvider _diagnosticsProvider)
{
    static readonly CSharpCompilationOptions _compileOptions = new(
        OutputKind.DynamicallyLinkedLibrary,
        optimizationLevel: OptimizationLevel.Release, allowUnsafe: true
    );

    readonly UserScriptCacheProvider _cacheProvider = new();

    public Task<List<CompiledUserScript>> CompileFromUserScriptsFolder(Action<string, Exception>? onFailure = null) => CompileFromFolder(HassPath.UserScripts, onFailure);

    public Task<CompiledUserScript> CompileFromUserScriptFile(string path, string source, bool writtenToDisk) =>
        CompileSingleFile(Path.Join(HassPath.UserScripts, path), source, writtenToDisk);

    public async Task<CompiledUserScript> CompileSingleFile(string path, string source, bool writtenToDisk)
    {
        var entities = _diagnosticsProvider.HassEntitiesSyntaxTree();
        var hash = _cacheProvider.ComputeHash(path, source + "\0" + entities.ToString());
        var assemblyBytes = CompileSingleFileToBytes(source, path, entities);
        await _cacheProvider.SetFileCache(path, hash, assemblyBytes);

        return new CompiledUserScript
        {
            Assembly = LoadAssembly(assemblyBytes),
            FilePath = path,
            FileName = Path.GetFileNameWithoutExtension(path),
            SourceCode = source,
            WrittenToDisk = writtenToDisk,
        };
    }

    async Task<List<CompiledUserScript>> CompileFromFolder(string folderPath, Action<string, Exception>? onFailure)
    {
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
        }

        var filePaths = Directory.GetFiles(folderPath, "*.cs");

        var tasks = filePaths.Select(async path =>
        {
            try
            {
                var source = await File.ReadAllTextAsync(path);
                var entities = _diagnosticsProvider.HassEntitiesSyntaxTree();
                var hash = _cacheProvider.ComputeHash(path, source + "\0" + entities.ToString());
                var cachedBytes = await _cacheProvider.GetFileCache(path, hash);

                if (cachedBytes != null)
                {
                    try
                    {
                        Logger.Info($"No change detected in {Path.GetFileName(path)}, using cached dll");
                        return new CompiledUserScript
                        {
                            Assembly = LoadAssembly(cachedBytes),
                            FilePath = path,
                            FileName = Path.GetFileNameWithoutExtension(path),
                            SourceCode = source,
                            WrittenToDisk = true,
                        };
                    }
                    catch (Exception ex) when (ex is BadImageFormatException or FileLoadException)
                    { Logger.Error($"Invalid compile cache for {path}; rebuilding: {ex}"); }
                }

                Logger.Info($"Change detected in {Path.GetFileName(path)}, recompiling");
                var assemblyBytes = CompileSingleFileToBytes(source, path, entities);
                await _cacheProvider.SetFileCache(path, hash, assemblyBytes);
                return new CompiledUserScript
                {
                    Assembly = LoadAssembly(assemblyBytes),
                    FilePath = path,
                    FileName = Path.GetFileNameWithoutExtension(path),
                    SourceCode = source,
                    WrittenToDisk = true,
                };
            }
            catch (Exception ex)
            {
                Logger.Error($"Cannot load script {path}: {ex}");
                if (ex is CompilationErrorException compilation) Logger.Error(string.Join("\n", compilation.Errors));
                onFailure?.Invoke(path, ex);
                return null;
            }
        });

        var assemblies = await Task.WhenAll(tasks);
        return assemblies.OfType<CompiledUserScript>().ToList();
    }

    static Assembly LoadAssembly(byte[] assemblyBytes)
    {
        var loadContext = new ScriptLoadContext();

        using var stream = new MemoryStream(assemblyBytes, writable: false);
        try { return loadContext.LoadFromStream(stream); }
        catch { loadContext.Unload(); throw; }
    }

    byte[] CompileSingleFileToBytes(string source, string path, SyntaxTree entities)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path: path);

        SyntaxTree[] syntaxTrees =
        [
            tree,
            CSharpSyntaxTree.ParseText(DiagnosticsProvider.GlobalUsings),
            entities,
        ];

        var references = NodeLibraryCompiler.FrameworkReferences();

        var compilation = CSharpCompilation.Create(
            assemblyName: "Automation_" + Guid.NewGuid(),
            syntaxTrees: syntaxTrees,
            references: references,
            options: _compileOptions
        );

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        if (result.Success) return ms.ToArray();

        var errors =
            result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToArray();

        throw new CompilationErrorException(errors);
    }
}
