using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Text;

namespace HassSharp;

public sealed record LibrarySourceRange(int Start, int Length, int StartLine, int StartColumn, int EndLine, int EndColumn);
public sealed record LibraryDefinition(string Id, LibrarySourceRange Range);
public sealed record LibraryHover(string Text, LibrarySourceRange Range);
public sealed record LibrarySignature(string Label, string Documentation, string[] Parameters);
public sealed record LibrarySignatureHelp(LibrarySignature[] Signatures, int ActiveSignature, int ActiveParameter);

/// <summary>Request-local immutable solutions. No unsaved document is ever added to the shared workspace.</summary>
public sealed class NodeLibraryLanguageServices(DiagnosticsProvider diagnostics)
{
    /// <summary>The id a Custom C# node's code has among the documents, in a definition result.</summary>
    public const string CustomDocumentId = "$custom";
    public Task<object> RequestAsync(string operation, IEnumerable<NodeLibrarySource> sources, string id, int position,
        CancellationToken cancellationToken = default) => RequestAsync(operation, sources, id, null, position, cancellationToken);
    /// <summary>The same services for a Custom C# node's unsaved code, beside the libraries, in the namespace it compiles in.</summary>
    public Task<object> RequestAsync(string operation, IEnumerable<NodeLibrarySource> sources, CustomNodeSource custom, int position,
        CancellationToken cancellationToken = default) => RequestAsync(operation, sources, CustomDocumentId, custom, position, cancellationToken);
    async Task<object> RequestAsync(string operation, IEnumerable<NodeLibrarySource> sources, string id, CustomNodeSource? custom, int position,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = sources.Where(s => custom == null || s.Id != CustomDocumentId).ToDictionary(s => s.Id, s => (Source: s.Source, Prefix: NodeLibraryCompiler.WrapperPrefix), StringComparer.Ordinal);
        if (custom != null) snapshot[CustomDocumentId] = (custom.Source, NodeLibraryCompiler.CustomWrapperPrefix(custom.NodeId));
        var source = snapshot.TryGetValue(id, out var current) ? current.Source : throw new ArgumentException("Unknown selected library");
        var offset = current.Prefix.Length;
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, source.Length);
        var project = diagnostics.CreateLibraryProject();
        var documents = new Dictionary<string, DocumentId>();
        foreach (var (itemId, item) in snapshot)
        {
            var added = project.AddDocument(itemId, SourceText.From(item.Prefix + item.Source + NodeLibraryCompiler.WrapperSuffix), filePath: itemId);
            documents.Add(itemId, added.Id); project = added.Project;
        }
        var document = project.GetDocument(documents[id])!;
        var adjusted = position + offset;
        bool InSource(TextSpan span, string text) => span.Start >= offset && span.End <= offset + text.Length;
        CompletionTextEdit Edit(TextChange change) => new() { Start = change.Span.Start - offset, Length = change.Span.Length, NewText = change.NewText ?? "" };
        LibrarySourceRange Range(string text, TextSpan wrapped) => SourceRange(text, wrapped, offset);
        switch (operation)
        {
            case "completion":
            {
                var service = CompletionService.GetService(document);
                if (service == null) return Array.Empty<CompletionModel>();
                var trigger = position > 0 && source[position - 1] == '.' ? CompletionTrigger.CreateInsertionTrigger('.') : CompletionTrigger.Invoke;
                var list = await service.GetCompletionsAsync(document, adjusted, trigger, cancellationToken: cancellationToken);
                if (list == null) return Array.Empty<CompletionModel>();
                var items = list.ItemsList;
                var full = current.Prefix + source + NodeLibraryCompiler.WrapperSuffix;
                var filter = full.Substring(list.Span.Start, Math.Max(0, adjusted-list.Span.Start));
                if (filter.Length > 0) items = service.FilterItems(document, [.. items], filter);
                var results = new List<CompletionModel>();
                var compilation = await document.Project.GetCompilationAsync(cancellationToken);
                foreach (var item in items)
                {
                    if (IsPythonNetItem(item, compilation)) continue;
                    var change = await service.GetChangeAsync(document,item,cancellationToken: cancellationToken);
                    var edits = change.TextChanges.IsDefaultOrEmpty ? [change.TextChange] : change.TextChanges;
                    var primary = edits.FirstOrDefault(e => e.Span.Start <= adjusted && e.Span.End >= adjusted);
                    if (primary.NewText == null || edits.Any(e => e.Span.Start < offset || e.Span.End > offset+source.Length)) continue;
                    results.Add(new() { DisplayText=item.DisplayText, DisplayTextPrefix=item.DisplayTextPrefix,DisplayTextSuffix=item.DisplayTextSuffix,
                        FilterText=item.FilterText,Tags=item.Tags.ToArray(),TextEdit=Edit(primary),AdditionalTextEdits=edits.Where(e=>e!=primary).Select(Edit).ToArray() });
                }
                return results;
            }
            case "hover":
            {
                var service = QuickInfoService.GetService(document);
                var info = service == null ? null : await service.GetQuickInfoAsync(document,adjusted,cancellationToken);
                if (info == null || !InSource(info.Span,source)) return new { };
                if (IsPythonNet(await SymbolFinder.FindSymbolAtPositionAsync(document,adjusted,cancellationToken))) return new { };
                return new LibraryHover(string.Join("\n",info.Sections.Select(s=>s.Text)),Range(source,info.Span));
            }
            case "definition":
            {
                var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document,adjusted,cancellationToken);
                if (symbol == null) return Array.Empty<LibraryDefinition>();
                var definitions = new List<LibraryDefinition>();
                foreach (var location in symbol.OriginalDefinition.Locations.Where(l=>l.IsInSource))
                {
                    var targetId=location.SourceTree?.FilePath;
                    if (targetId != null && snapshot.TryGetValue(targetId,out var target) && location.SourceSpan.Start>=target.Prefix.Length && location.SourceSpan.End<=target.Prefix.Length+target.Source.Length)
                        definitions.Add(new(targetId,SourceRange(target.Source,location.SourceSpan,target.Prefix.Length)));
                }
                return definitions;
            }
            case "signature":
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var model = await document.GetSemanticModelAsync(cancellationToken);
                var token = root!.FindToken(Math.Max(0,adjusted-1));
                var list = token.Parent?.AncestorsAndSelf().OfType<BaseArgumentListSyntax>()
                    .FirstOrDefault(a=>a.SpanStart < adjusted && adjusted <= a.Span.End);
                if (list?.Parent is not {} expression) return new LibrarySignatureHelp([],0,0);
                var info = model!.GetSymbolInfo(expression,cancellationToken);
                var methods = new List<IMethodSymbol>();
                if (info.Symbol is IMethodSymbol selected) methods.Add(selected);
                methods.AddRange(info.CandidateSymbols.OfType<IMethodSymbol>());
                if (expression is InvocationExpressionSyntax invocation)
                    methods.AddRange(model!.GetMemberGroup(invocation.Expression,cancellationToken).OfType<IMethodSymbol>());
                else if (expression is ObjectCreationExpressionSyntax creation && model!.GetTypeInfo(creation,cancellationToken).Type is INamedTypeSymbol type)
                    methods.AddRange(type.InstanceConstructors.Where(m=>m.DeclaredAccessibility==Accessibility.Public));
                var candidates = methods.Where(m=>!IsPythonNet(m)).Distinct<IMethodSymbol>(SymbolEqualityComparer.Default).ToArray();
                var signatures = candidates.Select(m=>new LibrarySignature(
                    m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),m.GetDocumentationCommentXml(cancellationToken:cancellationToken) ?? "",
                    m.Parameters.Select(p=>p.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)).ToArray())).ToArray();
                var activeParameter = list.Arguments.GetSeparators().Count(s=>s.SpanStart<adjusted);
                var argument = list.Arguments.ElementAtOrDefault(activeParameter);
                if (argument?.NameColon is {} named && candidates.FirstOrDefault() is {} signature)
                {
                    var parameter = signature.Parameters.FirstOrDefault(p=>p.Name==named.Name.Identifier.ValueText);
                    if (parameter != null) activeParameter = parameter.Ordinal;
                }
                return new LibrarySignatureHelp(signatures,0,activeParameter);
            }
            case "format":
            {
                // Formatting is syntactic: format the source on its own, so the namespace it is wrapped in to compile
                // does not indent every line of it by one level.
                var formatted = await Formatter.FormatAsync(document.WithText(SourceText.From(source)),cancellationToken:cancellationToken);
                return (await formatted.GetTextAsync(cancellationToken)).ToString();
            }
            default: throw new ArgumentException("Unknown language operation");
        }
    }
    // Python.NET is in the process, so among the references, and extends every object with ToPython and ToPythonAs<T>
    // (more with its codecs). They mean nothing in node code: completion, signature help and hover leave them out. The
    // assembly stays referenced; only these are not offered.
    const string PythonNetAssembly = "Python.Runtime";
    static readonly HashSet<string> PythonNetExtensions = typeof(Python.Runtime.PyObject).Assembly.GetExportedTypes()
        .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
        .Where(m => m.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false)).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
    internal static bool IsPythonNet(ISymbol? symbol) => symbol is IMethodSymbol method && (method.ReducedFrom ?? method) is var definition
        && definition.IsExtensionMethod && definition.ContainingAssembly?.Name == PythonNetAssembly;
    // A completion item names its symbol only by name: an extension method of Python.NET's name is one of them, unless the
    // code or a library declares an extension method by that name itself.
    static bool IsPythonNetItem(CompletionItem item, Compilation? compilation)
    {
        if (!item.Tags.Contains(Microsoft.CodeAnalysis.Tags.WellKnownTags.ExtensionMethod)) return false;
        var name = item.Properties.TryGetValue("SymbolName", out var symbolName) ? symbolName : item.DisplayText;
        if (!PythonNetExtensions.Contains(name)) return false;
        return compilation == null || !compilation.GetSymbolsWithName(name, SymbolFilter.Member).OfType<IMethodSymbol>().Any(m => m.IsExtensionMethod);
    }
    static LibrarySourceRange SourceRange(string source,TextSpan wrapped,int offset)
    {
        var span=new TextSpan(wrapped.Start-offset,wrapped.Length);
        var lines=SourceText.From(source).Lines.GetLinePositionSpan(span);
        return new(span.Start,span.Length,lines.Start.Line+1,lines.Start.Character+1,lines.End.Line+1,lines.End.Character+1);
    }
}
