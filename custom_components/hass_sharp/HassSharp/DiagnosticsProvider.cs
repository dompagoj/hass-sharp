using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Text;

namespace HassSharp;

public class DiagnosticsProvider
{
    readonly EntityGenerator _entityGenerator = new();
    readonly AdhocWorkspace _workspace;
    readonly Project _baseProject;

    SyntaxTree? _entitiesSyntaxTree;

    const string GlobalUsings = """
                                global using System;
                                global using System.Threading;
                                global using System.Threading.Tasks;
                                global using System.Collections.Generic;
                                global using System.Linq;
                                global using HassSharp;

                                """;

    static readonly int GlobalUsingsLineCount = GlobalUsings.Count(c => c == '\n');

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

        _baseProject = _workspace.AddProject(projectInfo);

        // Add default references to the workspace project
        var references = ProjectReferences.GetDefaultReferences();
        _workspace.TryApplyChanges(
            _workspace.CurrentSolution.WithProjectMetadataReferences(_baseProject.Id, references));
    }

    public SyntaxTree HassEntitiesSyntaxTree()
    {
        if (_entitiesSyntaxTree == null) throw new("Entities Tree not initialized, call generate");

        return _entitiesSyntaxTree;
    }

    public List<DiagnosticModel> GetDiagnostics(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(GlobalUsings + source);
        var references = ProjectReferences.GetDefaultReferences();

        var compilation = CSharpCompilation.Create(
            "Diagnostics_" + Guid.NewGuid(),
            [syntaxTree, HassEntitiesSyntaxTree()],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        return compilation.GetDiagnostics()
            .Where(d => d.Location.GetLineSpan().StartLinePosition.Line >= GlobalUsingsLineCount)
            .Select(d =>
            {
                var lineSpan = d.Location.GetLineSpan();
                // Roslyn lines are 0-based.
                var startLine = lineSpan.StartLinePosition.Line - GlobalUsingsLineCount + 1;
                var endLine = lineSpan.EndLinePosition.Line - GlobalUsingsLineCount + 1;

                return new DiagnosticModel
                {
                    StartLine = startLine,
                    StartColumn = lineSpan.StartLinePosition.Character + 1,
                    EndLine = endLine,
                    EndColumn = lineSpan.EndLinePosition.Character + 1,
                    Message = d.GetMessage(),
                    Severity = (int)d.Severity
                };
            })
            .ToList();
    }

    public IReadOnlyList<CompletionItem> GetCompletions(string source, int position)
    {
        var fullSource = GlobalUsings + source;
        var adjustedPosition = GlobalUsings.Length + position;

        var userScriptDocument = _workspace.AddDocument(_baseProject.Id, "Script.cs", SourceText.From(fullSource));

        var completionService = CompletionService.GetService(userScriptDocument);
        if (completionService == null) return [];

        var filterText = source.Substring(0, position).Split(' ', '.', '(', '\n', '\r', '\t').LastOrDefault() ?? "";
        var lastChar = position > 0 ? source[position - 1] : '\0';
        var trigger = lastChar == '.' ? CompletionTrigger.CreateInsertionTrigger('.') : CompletionTrigger.Invoke;

        var completionsTask = completionService.GetCompletionsAsync(userScriptDocument, adjustedPosition, trigger);

        var completions = completionsTask.GetAwaiter().GetResult();

        var items = completions.ItemsList;
        if (!string.IsNullOrEmpty(filterText))
        {
            items = completionService.FilterItems(userScriptDocument, [.. items], filterText);
        }

        return items;
    }

    public string? GetHover(string source, int position)
    {
        var fullSource = GlobalUsings + source;
        var adjustedPosition = GlobalUsings.Length + position;

        var userScriptDocument = _workspace.AddDocument(_baseProject.Id, "Script.cs", SourceText.From(fullSource));

        var quickInfoService = QuickInfoService.GetService(userScriptDocument);
        if (quickInfoService == null) return null;

        var quickInfo = quickInfoService.GetQuickInfoAsync(userScriptDocument, adjustedPosition).GetAwaiter()
            .GetResult();
        if (quickInfo == null) return null;

        return quickInfo.Sections.Select(s => s.Text).Aggregate((a, b) => a + "\n" + b);
    }

    public void GenerateHassEntities(string[] entityIds)
    {
        _entitiesSyntaxTree = _entityGenerator.GenerateEntities(entityIds);

        var entitiesDocumentId = DocumentId.CreateNewId(_baseProject.Id);
        var solution =
            _workspace.CurrentSolution.AddDocument(entitiesDocumentId, "Entities.g.cs", _entitiesSyntaxTree.GetText());
        _workspace.TryApplyChanges(solution);
    }
}

public class DiagnosticModel
{
    public int StartLine { get; set; }
    public int StartColumn { get; set; }
    public int EndLine { get; set; }
    public int EndColumn { get; set; }
    public string Message { get; set; } = null!;
    public int Severity { get; set; }
}
