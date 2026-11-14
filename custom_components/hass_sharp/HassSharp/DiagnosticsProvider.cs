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
        UseServiceSchema([]);
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
    /// <c>domain.service</c>). Compilations started later and the next language request see them; nothing compiled
    /// earlier changes.
    /// </summary>
    public void UseServiceSchema(ServiceSchemaCatalog services) =>
        UseServiceSchema(services.Items.Select(p => KeyValuePair.Create(p.Key, (System.Text.Json.Nodes.JsonObject)p.Value!)));

    internal void UseServiceSchema(IEnumerable<KeyValuePair<string, System.Text.Json.Nodes.JsonObject>> services)
    {
        var source = ServiceGenerator.Generate(services);
        lock (_gate)
        {
            if (_servicesSyntaxTree?.ToString() == source) return;
            var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path: "Services.g.cs");
            var solution = _workspace.CurrentSolution;
            if (_servicesDocumentId is null)
            {
                _servicesDocumentId = DocumentId.CreateNewId(_baseProjectId);
                solution = solution.AddDocument(_servicesDocumentId, "Services.g.cs", tree.GetText());
            }
            else
            {
                solution = solution.WithDocumentText(_servicesDocumentId, tree.GetText());
            }
            if (!_workspace.TryApplyChanges(solution))
                throw new InvalidOperationException("Could not replace the generated services document.");
            _servicesSyntaxTree = tree;
        }
    }

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
