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
        var assemblyBytes = CompileSingleFileToBytes(source, path, _diagnosticsProvider.HassEntitiesSyntaxTree());

        return Task.FromResult(new CompiledUserScript
        {
            Assembly = LoadAssembly(assemblyBytes),
            FilePath = path,
            FileName = Path.GetFileNameWithoutExtension(path),
            SourceCode = source,
        });
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
