using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;

namespace HassSharp;

public class DiagnosticsProvider
{
    readonly AdhocWorkspace _workspace;
    readonly ProjectId _baseProjectId;

    readonly object _gate = new();
    DocumentId? _servicesDocumentId;
    GeneratedServiceCalls _serviceCalls = null!;

    public const string GlobalUsings = """
                                       global using System;
                                       global using System.Threading;
                                       global using System.Threading.Tasks;
                                       global using System.Collections.Generic;
                                       global using System.Linq;
                                       global using HassSharp;

                                       """;

    public DiagnosticsProvider()
    {
        var host = MefHostServices.Create(MefHostServices.DefaultAssemblies);
        _workspace = new AdhocWorkspace(host);

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

        _baseProjectId = _workspace.AddProject(projectInfo).Id;

        // Add default references to the workspace project
        var references = ProjectReferences.GetDefaultReferences();
        _workspace.TryApplyChanges(
            _workspace.CurrentSolution.WithProjectMetadataReferences(_baseProjectId, references));
        if (!UseServiceSchema(Array.Empty<KeyValuePair<string, System.Text.Json.Nodes.JsonObject>>()).Accepted)
            throw new InvalidOperationException("The empty typed service calls do not compile.");
    }

    /// <summary>
    /// The accepted typed service calls (<c>context.Services</c>): their source, and the assembly built from it once,
    /// which every compilation references. Swapped whole under the lock: a compilation takes it once and keeps it, so it
    /// never references an assembly it was not given. Until a schema is given, there are no domains.
    /// </summary>
    public GeneratedServiceCalls ServiceCalls()
    {
        lock (_gate) return _serviceCalls;
    }

    /// <summary>
    /// The source of the accepted typed service calls, as the language services' workspace has it (completion, hover,
    /// signatures and definitions read it there); compilations reference <see cref="ServiceCalls"/>' assembly instead.
    /// </summary>
    public SyntaxTree ServicesSyntaxTree() => ServiceCalls().Tree;

    /// <summary>
    /// Replaces the typed service calls with those of <paramref name="services"/> (normalised, keyed by
    /// <c>domain.service</c>), as <see cref="UseServiceSchema(IReadOnlyCollection{KeyValuePair{string, System.Text.Json.Nodes.JsonObject}})"/>
    /// does. Compilations started later and the next language request see them; nothing compiled earlier changes.
    /// </summary>
    public void UseServiceSchema(ServiceSchemaCatalog services) =>
        UseServiceSchema(services.Items.Select(p => KeyValuePair.Create(p.Key, (System.Text.Json.Nodes.JsonObject)p.Value!)).ToArray());

    /// <summary>What a new schema did to the typed service calls.</summary>
    /// <param name="Accepted">False when no tree from it compiles: the previous tree stays.</param>
    /// <param name="Changed">Whether the assembly every compilation gets was replaced.</param>
    /// <param name="Grew">Whether that replacement added a service or changed one (rather than only dropping some).</param>
    /// <param name="Services">The services the tree now has: the schema's, less <paramref name="Excluded"/>.</param>
    /// <param name="Excluded">Services whose generated code did not compile, left out; the others are kept.</param>
    /// <param name="Errors">The compiler's errors, when there were any.</param>
    internal sealed record ServiceSchemaResult(bool Accepted, bool Changed, bool Grew, IReadOnlyList<KeyValuePair<string, System.Text.Json.Nodes.JsonObject>> Services,
        IReadOnlyList<string> Excluded, IReadOnlyList<string> Errors);

    /// <summary>How the typed service calls are generated; a test may break them on purpose.</summary>
    internal Func<IEnumerable<KeyValuePair<string, System.Text.Json.Nodes.JsonObject>>, ServiceGenerator.Generated> Generator { get; set; } = ServiceGenerator.GenerateWithSpans;

    /// <summary>
    /// Generates the typed service calls of <paramref name="services"/> and, if they compile on their own without errors,
    /// builds them once into an assembly and installs it. A service whose own code does not compile is left out and the
    /// rest are tried again; when no tree compiles, the previous assembly stays (at first, the empty one). Schema text
    /// cannot reach beyond comments and string literals (<see cref="ServiceGenerator"/>), so this is a safety net: what
    /// one schema holds never stops node code that does not call that service from compiling. Generating, building and
    /// checking run outside the lock; only the swap takes it.
    /// </summary>
    internal ServiceSchemaResult UseServiceSchema(IReadOnlyCollection<KeyValuePair<string, System.Text.Json.Nodes.JsonObject>> services)
    {
        var candidate = services.ToList();
        var excluded = new SortedSet<string>(StringComparer.Ordinal);
        var errors = new List<string>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ServiceGenerator.Generated generated;
            try { generated = Generator(candidate); }
            catch (InvalidOperationException ex) { errors.Add(ex.Message); break; }
            lock (_gate) if (_serviceCalls?.Source == generated.Source) return new(true, false, false, candidate, [.. excluded], errors);
            var built = GeneratedServiceCalls.Build(generated.Source, candidate, out var failures);
            if (built != null)
            {
                lock (_gate) { var (changed, grew) = Install(built); return new(true, changed, grew, candidate, [.. excluded], errors); }
            }
            errors.AddRange(failures.Take(10).Select(d => d.ToString()));
            var ids = candidate.Select(p => p.Key).ToArray();
            var lines = failures.Select(d => d.Location.GetLineSpan().StartLinePosition.Line + 1).ToArray();
            var broken = lines.Select(line => generated.ServicesAt(line, ids).ToArray()).ToArray();
            // An error outside every service's code is the generator's: no subset would compile.
            if (broken.Any(b => b.Length == 0)) break;
            excluded.UnionWith(broken.SelectMany(b => b));
            candidate = candidate.Where(p => !excluded.Contains(p.Key)).ToList();
        }
        return new(false, false, false, [], [.. excluded], errors);
    }

    // Under _gate.
    (bool Changed, bool Grew) Install(GeneratedServiceCalls built)
    {
        if (_serviceCalls?.Source == built.Source) return (false, false);
        var tree = built.Tree;
        var solution = _workspace.CurrentSolution;
        if (_servicesDocumentId is null)
        {
            _servicesDocumentId = DocumentId.CreateNewId(_baseProjectId);
            solution = solution.AddDocument(_servicesDocumentId, ServicesPath, tree.GetText());
        }
        else
        {
            solution = solution.WithDocumentText(_servicesDocumentId, tree.GetText());
        }
        if (!_workspace.TryApplyChanges(solution))
            throw new InvalidOperationException("Could not replace the generated services document.");
        var grew = _serviceCalls == null || built.Schemas.Any(p => !_serviceCalls.Schemas.TryGetValue(p.Key, out var before) || before != p.Value);
        _serviceCalls = built;
        return (true, grew);
    }

    /// <summary>The path of the services tree: a diagnostic in it is told apart from one in node code by its tree.</summary>
    public const string ServicesPath = "Services.g.cs";

    internal Project CreateLibraryProject()
    {
        lock (_gate)
            return _workspace.CurrentSolution.GetProject(_baseProjectId)!
                .WithMetadataReferences(NodeLibraryCompiler.FrameworkReferences())
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable))
                .AddDocument("GlobalUsings.g.cs", SourceText.From(GlobalUsings)).Project;
    }
}

/// <summary>
/// One accepted set of typed service calls: the generated source and its syntax tree, and the assembly built from it
/// once. The assembly's name is unique per source (<c>HassSharpServiceCalls_</c> and a hash of it), so code compiled
/// against an older set keeps finding the one it was compiled against: its load context loads <see cref="Image"/> by
/// that name (<see cref="ScriptLoadContext"/>).
/// </summary>
public sealed class GeneratedServiceCalls
{
    public const string AssemblyPrefix = "HassSharpServiceCalls_";
    public string Source { get; }
    /// <summary>The source parsed, as the language services' workspace has it.</summary>
    public SyntaxTree Tree { get; }
    public string AssemblyName { get; }
    /// <summary>The built assembly, loaded from these bytes into each automation's load context that uses it.</summary>
    public byte[] Image { get; }
    /// <summary>What every compilation references in place of the source.</summary>
    public MetadataReference Reference { get; }
    /// <summary>Each service's normalised schema as JSON, to tell an added or changed service from a dropped one.</summary>
    internal IReadOnlyDictionary<string, string> Schemas { get; }

    GeneratedServiceCalls(string source, SyntaxTree tree, string name, byte[] image, IReadOnlyDictionary<string, string> schemas)
    {
        (Source, Tree, AssemblyName, Image, Schemas) = (source, tree, name, image, schemas);
        Reference = MetadataReference.CreateFromImage(System.Collections.Immutable.ImmutableArray.Create(image), filePath: name + ".dll");
    }

    /// <summary>Parses and builds <paramref name="source"/> on its own; null, with the errors, when it does not compile.</summary>
    internal static GeneratedServiceCalls? Build(string source, IEnumerable<KeyValuePair<string, System.Text.Json.Nodes.JsonObject>> services, out Diagnostic[] errors)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path: DiagnosticsProvider.ServicesPath);
        var name = AssemblyPrefix + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)))[..16].ToLowerInvariant();
        var compilation = CSharpCompilation.Create(name, [tree], NodeLibraryCompiler.FrameworkReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable));
        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (!result.Success) { if (errors.Length == 0) errors = result.Diagnostics.ToArray(); return null; }
        return new(source, tree, name, image.ToArray(), services.ToDictionary(p => p.Key, p => p.Value.ToJsonString(), StringComparer.Ordinal));
    }
}

public class CompletionModel
{
    public string DisplayText { get; init; } = "";
    public string DisplayTextPrefix { get; init; } = "";
    public string DisplayTextSuffix { get; init; } = "";
    public string FilterText { get; init; } = "";
    public string[] Tags { get; init; } = [];
    public CompletionTextEdit TextEdit { get; init; } = new();
    public CompletionTextEdit[] AdditionalTextEdits { get; init; } = [];
}

public class CompletionTextEdit
{
    public int Start { get; init; }
    public int Length { get; init; }
    public string NewText { get; init; } = "";
}
