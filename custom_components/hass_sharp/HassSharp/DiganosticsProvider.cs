using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Text;

namespace HassSharp;

public class DiagnosticsProvider
{
    public static SyntaxTree? EntitiesTree { get; set; }
    static DocumentId? EntitiesDocumentId;

    readonly AdhocWorkspace Workspace;
    readonly Project BaseProject;
    DocumentId? _scriptDocumentId;

    public DiagnosticsProvider()
    {
        var host = MefHostServices.Create(MefHostServices.DefaultAssemblies);
        Workspace = new AdhocWorkspace(host);

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

        BaseProject = Workspace.AddProject(projectInfo);

        // Add default references to the workspace project
        var references = ProjectReferences.GetDefaultReferences();
        Workspace.TryApplyChanges(Workspace.CurrentSolution.WithProjectMetadataReferences(BaseProject.Id, references));
    }

    public SyntaxTree HasEntitiesSyntaxTree()
    {
        if (EntitiesTree == null) throw new("Entities Tree not initialized, call generate");

        return EntitiesTree;
    }

    public List<DiagnosticModel> GetDiagnostics(string source)
    {
        const string globalUsings = """
                                    global using System;
                                    global using System.Threading;
                                    global using System.Threading.Tasks;
                                    global using System.Collections.Generic;
                                    global using System.Linq;
                                    global using HassSharp;

                                    """;
        var syntaxTree = CSharpSyntaxTree.ParseText(globalUsings + source);
        var references = ProjectReferences.GetDefaultReferences();

        var compilation = CSharpCompilation.Create(
            "Diagnostics_" + Guid.NewGuid(),
            [syntaxTree, HasEntitiesSyntaxTree()],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var prefixLineCount = globalUsings.Count(c => c == '\n');

        return compilation.GetDiagnostics()
            .Where(d => d.Location.GetLineSpan().StartLinePosition.Line >= prefixLineCount)
            .Select(d =>
            {
                var lineSpan = d.Location.GetLineSpan();
                // Roslyn lines are 0-based.
                var startLine = lineSpan.StartLinePosition.Line - prefixLineCount + 1;
                var endLine = lineSpan.EndLinePosition.Line - prefixLineCount + 1;

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
        const string globalUsings = """
                                    global using System;
                                    global using System.Threading;
                                    global using System.Threading.Tasks;
                                    global using System.Collections.Generic;
                                    global using System.Linq;
                                    global using HassSharp;

                                    """;
        var fullSource = globalUsings + source;
        var adjustedPosition = globalUsings.Length + position;

        Document document;
        lock (Workspace)
        {
            if (_scriptDocumentId == null)
            {
                document = Workspace.AddDocument(BaseProject.Id, "Script.cs", SourceText.From(fullSource));
                _scriptDocumentId = document.Id;
            }
            else
            {
                var solution =
                    Workspace.CurrentSolution.WithDocumentText(_scriptDocumentId, SourceText.From(fullSource));
                Workspace.TryApplyChanges(solution);
                document = Workspace.CurrentSolution.GetDocument(_scriptDocumentId)!;
            }
        }

        var completionService = CompletionService.GetService(document);
        if (completionService == null) return [];

        var filterText = source.Substring(0, position).Split(' ', '.', '(', '\n', '\r', '\t').LastOrDefault() ?? "";
        var lastChar = position > 0 ? source[position - 1] : '\0';
        var trigger = lastChar == '.' ? CompletionTrigger.CreateInsertionTrigger('.') : CompletionTrigger.Invoke;

        var completionsTask = completionService.GetCompletionsAsync(document, adjustedPosition, trigger);

        var completions = completionsTask.GetAwaiter().GetResult();

        var items = completions.ItemsList;
        if (!string.IsNullOrEmpty(filterText))
        {
            items = completionService.FilterItems(document, [.. items], filterText);
        }

        return items;
    }

    public void GenerateHassEntities(string[] entityIds) => GenerateImpl(entityIds);

    void GenerateImpl(string[] entityIds)
    {
        var sb = new StringBuilder();
        sb.AppendLine("namespace HassSharp;");
        sb.AppendLine();
        sb.AppendLine("public static class Entities");
        sb.AppendLine("{");

        var domains = entityIds
            .Select(id => id.Split('.'))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0]);

        foreach (var domainGroup in domains)
        {
            var domainName = Sanitize(domainGroup.Key);
            sb.AppendLine($"    public static class {domainName}");
            sb.AppendLine("    {");

            foreach (var entityIdParts in domainGroup)
            {
                var originalId = string.Join(".", entityIdParts);
                var objectId = Sanitize(entityIdParts[1]);

                // If property name matches class name, append "Entity" to property
                if (objectId == domainName)
                {
                    objectId += "Entity";
                }

                sb.AppendLine($"        public static string {objectId} => \"{originalId}\";");
            }

            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        var source = sb.ToString();

        EntitiesTree = CSharpSyntaxTree.ParseText(source);

        if (EntitiesDocumentId != null)
        {
            var solution = Workspace.CurrentSolution.WithDocumentText(EntitiesDocumentId, SourceText.From(source));
            Workspace.TryApplyChanges(solution);
        }
        else
        {
            EntitiesDocumentId = DocumentId.CreateNewId(BaseProject.Id);
            var solution = Workspace.CurrentSolution.AddDocument(EntitiesDocumentId, "Entities.g.cs", source);
            Workspace.TryApplyChanges(solution);
        }
    }

    static string Sanitize(string name)
    {
        var parts = name.Split('_', '.', '-');
        var pascal = string.Join("", parts.Select(p =>
            p.Length > 0 ? char.ToUpper(p[0]) + p.Substring(1) : ""));

        if (pascal.Length > 0 && char.IsDigit(pascal[0]))
            pascal = "_" + pascal;

        return pascal;
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
