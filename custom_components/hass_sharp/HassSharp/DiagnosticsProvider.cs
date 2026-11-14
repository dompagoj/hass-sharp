using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;

namespace HassSharp;

public class DiagnosticsProvider
{
    readonly EntityGenerator _entityGenerator = new();
    readonly AdhocWorkspace _workspace;
    readonly ProjectId _baseProjectId;

    readonly object _gate = new();
    DocumentId? _entitiesDocumentId;
    SyntaxTree? _entitiesSyntaxTree;

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
    }

    public SyntaxTree HassEntitiesSyntaxTree()
    {
        lock (_gate)
            return _entitiesSyntaxTree ?? throw new InvalidOperationException("Entities Tree not initialized, call generate");
    }

    public SyntaxTree? HassEntitiesSyntaxTreeNullable()
    {
        lock (_gate) return _entitiesSyntaxTree;
    }

    public string EntityReferenceExpression(string entityId)
    {
        lock (_gate)
        {
            var names = EntityGenerator.ResolveNames(_entityIds);
            if (!names.TryGetValue(entityId, out var name))
                throw new ArgumentException($"Unknown entity: {entityId}");
            return $"Entities.{name.Domain}.{name.Property}";
        }
    }

    string[] _entityIds = [];

    public void GenerateHassEntities(string[] entityIds)
    {
        lock (_gate)
        {
            var ids = entityIds.ToArray();
            var tree = _entityGenerator.GenerateEntities(ids);
            var solution = _workspace.CurrentSolution;
            if (_entitiesDocumentId is null)
            {
                _entitiesDocumentId = DocumentId.CreateNewId(_baseProjectId);
                solution = solution.AddDocument(_entitiesDocumentId, "Entities.g.cs", tree.GetText());
            }
            else
            {
                solution = solution.WithDocumentText(_entitiesDocumentId, tree.GetText());
            }
            if (!_workspace.TryApplyChanges(solution))
                throw new InvalidOperationException("Could not replace generated entities document.");
            _entityIds = ids;
            _entitiesSyntaxTree = tree;
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
