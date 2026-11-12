using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.QuickInfo;
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

    public DiagnosticModel[] GetDiagnostics(string source)
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
            .Where(d => d.Location.SourceTree == syntaxTree &&
                        d.Location.GetLineSpan().StartLinePosition.Line >= GlobalUsingsLineCount)
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
            .ToArray();
    }

    public async Task<IReadOnlyList<CompletionModel>> GetCompletions(string source, int position,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, source.Length);
        var fullSource = GlobalUsings + source;
        var adjustedPosition = GlobalUsings.Length + position;

        var userScriptDocument = CreateDocument("Script.cs", fullSource);

        var completionService = CompletionService.GetService(userScriptDocument);
        if (completionService == null)
        {
            return [];
        }

        var lastChar = position > 0 ? source[position - 1] : '\0';
        var trigger = lastChar == '.' ? CompletionTrigger.CreateInsertionTrigger('.') : CompletionTrigger.Invoke;

        var completions = await completionService.GetCompletionsAsync(userScriptDocument, adjustedPosition, trigger, cancellationToken: cancellationToken);

        if (completions is null) return [];
        var items = completions.ItemsList;
        var filterText = fullSource.Substring(completions.Span.Start, adjustedPosition - completions.Span.Start);
        if (!string.IsNullOrEmpty(filterText))
        {
            items = completionService.FilterItems(userScriptDocument, [.. items], filterText);
        }

        var result = new List<CompletionModel>();
        foreach (var item in items)
        {
            var change = await completionService.GetChangeAsync(userScriptDocument, item,
                cancellationToken: cancellationToken);
            var edits = change.TextChanges.IsDefaultOrEmpty ? [change.TextChange] : change.TextChanges;
            var primary = edits.FirstOrDefault(e => e.Span.Start <= adjustedPosition && e.Span.End >= adjustedPosition);
            // Do not expose edits to synthetic global usings as offsets into the user's file.
            if (primary.NewText is null || edits.Any(e => e.Span.Start < GlobalUsings.Length)) continue;
            result.Add(new CompletionModel
            {
                DisplayText = item.DisplayText,
                DisplayTextPrefix = item.DisplayTextPrefix,
                DisplayTextSuffix = item.DisplayTextSuffix,
                FilterText = item.FilterText,
                Tags = item.Tags.ToArray(),
                TextEdit = ToEdit(primary),
                AdditionalTextEdits = edits.Where(e => e != primary).Select(ToEdit).ToArray()
            });
        }
        return result;

        static CompletionTextEdit ToEdit(TextChange edit) => new()
        {
            Start = edit.Span.Start - GlobalUsings.Length,
            Length = edit.Span.Length,
            NewText = edit.NewText ?? ""
        };
    }

    public string? GetHover(string source, int position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, source.Length);
        var fullSource = GlobalUsings + source;
        var adjustedPosition = GlobalUsings.Length + position;

        var userScriptDocument = CreateDocument("Script.cs", fullSource);

        var quickInfoService = QuickInfoService.GetService(userScriptDocument);
        if (quickInfoService == null) return null;

        var quickInfo = quickInfoService.GetQuickInfoAsync(userScriptDocument, adjustedPosition).GetAwaiter()
            .GetResult();
        if (quickInfo == null) return null;

        return string.Join("\n", quickInfo.Sections.Select(s => s.Text));
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

    // Documents belong only to an immutable request snapshot, never the shared workspace.
    // Empty results, exceptions and overlapping requests therefore need no cleanup.
    Document CreateDocument(string name, string source)
    {
        lock (_gate)
            return _workspace.CurrentSolution.GetProject(_baseProjectId)!.AddDocument(name, SourceText.From(source));
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

    public async Task<string> FormatCode(string source)
    {
        var userScriptDocument = CreateDocument("Format.cs", source);
        var formattedDocument = await Formatter.FormatAsync(userScriptDocument);
        var formattedText = (await formattedDocument.GetTextAsync()).ToString();

        return formattedText;
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
