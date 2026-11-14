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

    /// <param name="path">The script's identity; nothing is read from or written to it.</param>
    public Task<CompiledUserScript> CompileSingleFile(string path, string source)
    {
        // Taken once: the assembly this script references is the one its load context can load.
        var services = _diagnosticsProvider.ServiceCalls();
        var assemblyBytes = CompileSingleFileToBytes(source, path, services);
        var (assembly, loadContext) = LoadAssembly(assemblyBytes, services);

        return Task.FromResult(new CompiledUserScript
        {
            Assembly = assembly,
            LoadContext = loadContext,
            FilePath = path,
            FileName = Path.GetFileNameWithoutExtension(path),
            SourceCode = source,
        });
    }

    static (Assembly, ScriptLoadContext) LoadAssembly(byte[] assemblyBytes, GeneratedServiceCalls services)
    {
        var loadContext = new ScriptLoadContext(services);

        using var stream = new MemoryStream(assemblyBytes, writable: false);
        try { return (loadContext.LoadFromStream(stream), loadContext); }
        catch { loadContext.Unload(); throw; }
    }

    byte[] CompileSingleFileToBytes(string source, string path, GeneratedServiceCalls services)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path: path);

        SyntaxTree[] syntaxTrees =
        [
            tree,
            CSharpSyntaxTree.ParseText(DiagnosticsProvider.GlobalUsings),
        ];

        var references = NodeLibraryCompiler.FrameworkReferences().Add(services.Reference);

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
