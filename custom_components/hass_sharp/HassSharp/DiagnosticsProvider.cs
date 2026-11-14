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
    SyntaxTree _servicesSyntaxTree = null!;
    string? _servicesSource;

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
    /// The typed service calls (<c>context.Services</c>) that every compilation and the language services include;
    /// until a schema is given, there are no domains.
    /// </summary>
    public SyntaxTree ServicesSyntaxTree()
    {
        lock (_gate) return _servicesSyntaxTree;
    }

    /// <summary>
    /// Replaces the typed service calls with those of <paramref name="services"/> (normalised, keyed by
    /// <c>domain.service</c>), as <see cref="UseServiceSchema(IReadOnlyCollection{KeyValuePair{string, System.Text.Json.Nodes.JsonObject}})"/>
    /// does. Compilations started later and the next language request see them; nothing compiled earlier changes.
    /// </summary>
    public void UseServiceSchema(ServiceSchemaCatalog services) =>
        UseServiceSchema(services.Items.Select(p => KeyValuePair.Create(p.Key, (System.Text.Json.Nodes.JsonObject)p.Value!)).ToArray());

    /// <summary>What a new schema did to the typed service calls.</summary>
    /// <param name="Accepted">False when no tree from it compiles: the previous tree stays.</param>
    /// <param name="Changed">Whether the tree every compilation gets was replaced.</param>
    /// <param name="Services">The services the tree now has: the schema's, less <paramref name="Excluded"/>.</param>
    /// <param name="Excluded">Services whose generated code did not compile, left out; the others are kept.</param>
    /// <param name="Errors">The compiler's errors, when there were any.</param>
    internal sealed record ServiceSchemaResult(bool Accepted, bool Changed, IReadOnlyList<KeyValuePair<string, System.Text.Json.Nodes.JsonObject>> Services,
        IReadOnlyList<string> Excluded, IReadOnlyList<string> Errors);

    /// <summary>How the typed service calls are generated; a test may break them on purpose.</summary>
    internal Func<IEnumerable<KeyValuePair<string, System.Text.Json.Nodes.JsonObject>>, ServiceGenerator.Generated> Generator { get; set; } = ServiceGenerator.GenerateWithSpans;

    /// <summary>
    /// Generates the typed service calls of <paramref name="services"/> and installs them if they compile on their own,
    /// without errors. A service whose own code does not compile is left out and the rest are tried again; when no tree
    /// compiles, the previous one stays (at first, the empty one). Schema text cannot reach beyond comments and string
    /// literals (<see cref="ServiceGenerator"/>), so this is a safety net: what one schema holds never stops node code
    /// that does not call that service from compiling. Generating and checking run outside the lock.
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
            lock (_gate) if (_servicesSource == generated.Source) return new(true, false, candidate, [.. excluded], errors);
            var tree = CSharpSyntaxTree.ParseText(generated.Source, new CSharpParseOptions(LanguageVersion.Latest), path: ServicesPath);
            var failures = CSharpCompilation.Create("HassSharpServiceCalls", [tree], NodeLibraryCompiler.FrameworkReferences(),
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable))
                .GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            if (failures.Length == 0)
            {
                lock (_gate) return new(true, Install(generated.Source, tree), candidate, [.. excluded], errors);
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
        return new(false, false, [], [.. excluded], errors);
    }

    // Under _gate.
    bool Install(string source, SyntaxTree tree)
    {
        if (_servicesSource == source) return false;
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
        (_servicesSyntaxTree, _servicesSource) = (tree, source);
        return true;
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
