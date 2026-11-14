using System.Text;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace HassSharp;

/// <summary>Where a type shown by <c>type_definition</c> is declared: a node library, a Custom C# node's code, or an assembly.</summary>
public sealed record TypeDefinitionOrigin(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Library = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? File = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NodeId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Assembly = null);
/// <param name="Kind"><c>source</c>: the declaration as written; <c>metadata</c>: rebuilt from a compiled assembly.</param>
/// <param name="Line">The 1-based line the shown text starts on, in the library's or node's own code; null for metadata.</param>
/// <param name="Truncated">The text was cut at <see cref="TypeDefinitions.MaxText"/> characters.</param>
public sealed record TypeDefinition(string Name, string Kind, string Text, TypeDefinitionOrigin Origin,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Line,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Truncated = false);

/// <summary>
/// The C# definition of a type a pin or property carries, which the panel's C# type links show (Task 31): a type declared
/// in a node library or in a Custom C# node's code as written there, and a type of HassSharp rebuilt from its metadata.
/// C#'s own types (keywords and the <c>System</c> namespaces) have none. Semantic-model work only, as a preview is:
/// nothing is emitted or run, and no symbol outlives the request.
/// </summary>
internal static class TypeDefinitions
{
    /// <summary>The longest text answered; a longer definition is cut at a line and says so.</summary>
    public const int MaxText = 64_000;
    const string Cut = "\n// The rest is not shown: the definition is longer than 64,000 characters";

    /// <summary>A requested id as a person reads it: without <c>global::</c>, and cut at 200 characters.</summary>
    static string Named(string typeId)
    {
        var name = typeId.Replace("global::", "", StringComparison.Ordinal);
        return name.Length <= 200 ? name : name[..200] + "…";
    }

    /// <param name="snapshot">A compiled generation: the saved libraries, with the node's code when <paramref name="nodeId"/> is given.</param>
    /// <param name="typeId">A catalog type id, such as <c>global::NodeLib.Mode</c> or <c>global::HassSharp.EntityStateSnapshot</c>.</param>
    /// <param name="files">The libraries' file names, by source id.</param>
    /// <exception cref="FileNotFoundException">No such type.</exception>
    /// <exception cref="ArgumentException">A built-in type, or one with no definition HassSharp can show.</exception>
    public static TypeDefinition Find(VisualCatalogSnapshot snapshot, string typeId, string? nodeId, IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken)
    {
        var compilation = snapshot.Compilation ?? throw new InvalidOperationException("The libraries were not compiled");
        var hassSharp = compilation.SourceModule.ReferencedAssemblySymbols.Single(a => a.Identity.Name == typeof(NodeAttribute).Assembly.GetName().Name);
        var symbol = Lookup(snapshot, typeId, nodeId) ?? (typeId.EndsWith('?') ? Lookup(snapshot, typeId[..^1], nodeId) : Lookup(snapshot, typeId + "?", nodeId))
            ?? Declared(compilation, typeId.TrimEnd('?'), cancellationToken) ?? Metadata(hassSharp, typeId.TrimEnd('?'))
            ?? throw new FileNotFoundException($"There is no type {Named(typeId)}; it may have been renamed or removed");
        // A value that may be empty is of the type it wraps: Point? shows Point.
        if (symbol is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable && nullable.TypeArguments[0] is INamedTypeSymbol { SpecialType: SpecialType.None, TypeKind: not TypeKind.Error } wrapped && !InSystem(wrapped)) symbol = wrapped;
        // An array of a type that has a definition points to that type (an array of C#'s own types is C#'s own).
        if (symbol is IArrayTypeSymbol array)
        {
            ITypeSymbol items = array;
            for (var depth = 0; items is IArrayTypeSymbol inner && depth < 8; depth++) items = inner.ElementType;
            if (items is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } wrappedItems) items = wrappedItems.TypeArguments[0];
            if (items is INamedTypeSymbol { IsTupleType: false, SpecialType: SpecialType.None, TypeKind: not TypeKind.Error } element && !InSystem(element))
                throw new ArgumentException($"{Written(symbol)} is an array: ask for {Written(element)}, the type of its items");
        }
        if (symbol is not INamedTypeSymbol { IsTupleType: false, TypeKind: not TypeKind.Error } named || named.SpecialType != SpecialType.None || InSystem(named))
            throw new ArgumentException($"{(Written(symbol) is var written && typeId.EndsWith('?') && !written.EndsWith('?') ? written + "?" : written)} is a built-in C# type, so it has no definition to show");
        var definition = named.OriginalDefinition;
        var name = definition.ToDisplayString(NameFormat);
        var parts = definition.DeclaringSyntaxReferences.Where(r => Origin(r.SyntaxTree, files) != null)
            .OrderBy(r => compilation.SyntaxTrees.IndexOf(r.SyntaxTree)).ThenBy(r => r.Span.Start).ToArray();
        if (parts.Length > 0)
        {
            var (text, line) = SourceText(parts, cancellationToken);
            var (shown, truncated) = Capped(text);
            return new(name, "source", shown, Origin(parts[0].SyntaxTree, files)!, line, truncated);
        }
        var assembly = definition.ContainingAssembly;
        if (SymbolEqualityComparer.Default.Equals(assembly, hassSharp) || assembly?.Name.StartsWith(GeneratedServiceCalls.AssemblyPrefix, StringComparison.Ordinal) == true)
        {
            var (shown, truncated) = MetadataText(definition, cancellationToken);
            return new(name, "metadata", shown, new(Assembly: "HassSharp"), null, truncated);
        }
        throw new ArgumentException($"{Written(symbol)} has no definition to show: only types of node libraries, Custom C# nodes and HassSharp have one");
    }

    static ITypeSymbol? Lookup(VisualCatalogSnapshot snapshot, string id, string? nodeId) =>
        nodeId != null && snapshot.Custom.TryGetValue(nodeId, out var custom) && custom.Types.FirstOrDefault(t => t.Id == id) is { } own ? own.Symbol
            : snapshot.Types.TryGetValue(id, out var type) ? type.Symbol : null;

    /// <summary>
    /// A type declared in the compiled source (a library or the node's code) whose id is <paramref name="id"/>, for one no
    /// pin uses (a node's code that does not compile gives no pins). Each declared namespace and type is visited once.
    /// </summary>
    static INamedTypeSymbol? Declared(Compilation compilation, string id, CancellationToken cancellationToken)
    {
        var pending = new Stack<INamespaceOrTypeSymbol>([compilation.Assembly.GlobalNamespace]);
        while (pending.TryPop(out var container))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var member in container.GetMembers())
            {
                if (member is INamespaceSymbol space) pending.Push(space);
                else if (member is INamedTypeSymbol type)
                {
                    if (NodeLibraryCompiler.TypeIdentity(type) == id) return type;
                    pending.Push(type);
                }
            }
        }
        return null;
    }
    /// <summary>A non-generic, non-nested type of HassSharp by its id (<c>global::HassSharp.NodeLibrarySource</c>).</summary>
    static INamedTypeSymbol? Metadata(IAssemblySymbol hassSharp, string id)
    {
        var name = id.StartsWith("global::", StringComparison.Ordinal) ? id["global::".Length..] : id;
        return name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_') && hassSharp.GetTypeByMetadataName(name) is { DeclaredAccessibility: Accessibility.Public } type ? type : null;
    }

    static bool InSystem(INamedTypeSymbol type)
    {
        var space = type.ContainingNamespace;
        while (space is { ContainingNamespace.IsGlobalNamespace: false }) space = space.ContainingNamespace;
        return space?.Name == "System";
    }
    /// <summary>A type as C# code writes it: <c>string</c>, <c>List&lt;Person&gt;</c>, <c>int?</c>.</summary>
    static string Written(ITypeSymbol type) => type.ToDisplayString(TypeFormat);

    static TypeDefinitionOrigin? Origin(SyntaxTree tree, IReadOnlyDictionary<string, string> files)
    {
        const string custom = "$custom:";
        if (tree.FilePath.StartsWith(custom, StringComparison.Ordinal)) return new(NodeId: tree.FilePath[custom.Length..]);
        return files.TryGetValue(tree.FilePath, out var file) ? new(Library: tree.FilePath, File: file) : null;
    }

    /// <summary>
    /// The declaration of each part as written, from its doc comment (or its first attribute) to its closing brace, with
    /// the indentation the lines share taken off; parts are separated by an empty line. The line is the first part's.
    /// </summary>
    static (string Text, int Line) SourceText(IEnumerable<SyntaxReference> parts, CancellationToken cancellationToken)
    {
        var written = new List<string>(); int? first = null;
        foreach (var part in parts)
        {
            var node = part.GetSyntax(cancellationToken);
            var text = part.SyntaxTree.GetText(cancellationToken);
            var start = node.SpanStart;
            // The doc comment just above belongs to the declaration; another comment before it does not.
            foreach (var trivia in node.GetLeadingTrivia())
                if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)) { start = trivia.FullSpan.Start; break; }
            var line = text.Lines.GetLineFromPosition(start);
            if (string.IsNullOrWhiteSpace(text.ToString(TextSpan.FromBounds(line.Start, start)))) start = line.Start;
            first ??= line.LineNumber + 1 - NodeLibraryCompiler.SourceLineOffset;
            written.Add(Dedent(text.ToString(TextSpan.FromBounds(start, node.Span.End))));
            if (written.Sum(w => w.Length) > MaxText) break;
        }
        return (string.Join("\n\n", written), Math.Max(1, first ?? 1));
    }
    static string Dedent(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        string? shared = null;
        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var indent = line[..(line.Length - line.TrimStart(' ', '\t').Length)];
            if (shared == null) { shared = indent; continue; }
            var same = 0;
            while (same < shared.Length && same < indent.Length && shared[same] == indent[same]) same++;
            shared = shared[..same];
        }
        return string.Join("\n", lines.Select(l => string.IsNullOrWhiteSpace(l) ? "" : l[(shared?.Length ?? 0)..].TrimEnd()));
    }
    static (string Text, bool Truncated) Capped(string text)
    {
        if (text.Length <= MaxText) return (text, false);
        var end = text.LastIndexOf('\n', MaxText - Cut.Length);
        return (text[..Math.Max(0, end)] + Cut, true);
    }

    static readonly SymbolDisplayFormat TypeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
    static readonly SymbolDisplayFormat NameFormat = TypeFormat.WithGenericsOptions(SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance);
    /// <summary>The name a declaration gives itself: <c>Inner</c>, not <c>Outer.Inner</c>.</summary>
    static readonly SymbolDisplayFormat DeclaredNameFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);
    /// <summary>A member's signature: accessibility, modifiers, type, name, parameters with their defaults, and a property's accessors.</summary>
    static readonly SymbolDisplayFormat MemberFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints,
        memberOptions: SymbolDisplayMemberOptions.IncludeAccessibility | SymbolDisplayMemberOptions.IncludeModifiers | SymbolDisplayMemberOptions.IncludeType
            | SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeRef | SymbolDisplayMemberOptions.IncludeConstantValue,
        kindOptions: SymbolDisplayKindOptions.IncludeMemberKeyword,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeDefaultValue
            | SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeExtensionThis,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>
    /// A declaration rebuilt from metadata: the namespace, the type's kind, base type and interfaces (a record's primary
    /// constructor when its <c>Deconstruct</c> tells it), each public member's signature with no body, and the summary of
    /// each from HassSharp's documentation file when it is published; nested public types are listed by their declaration.
    /// </summary>
    static (string Text, bool Truncated) MetadataText(INamedTypeSymbol type, CancellationToken cancellationToken) => MetadataText(type, Summaries.Value, cancellationToken);
    /// <summary>The same, with the summaries given by documentation id (tests give their own).</summary>
    internal static (string Text, bool Truncated) MetadataText(INamedTypeSymbol type, IReadOnlyDictionary<string, string> summaries, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        if (!type.ContainingNamespace.IsGlobalNamespace) text.Append("namespace ").Append(type.ContainingNamespace.ToDisplayString()).Append(";\n\n");
        void Summary(StringBuilder into, ISymbol symbol, string indent)
        {
            if (symbol.GetDocumentationCommentId() is { } id && summaries.TryGetValue(id, out var summary)) into.Append(indent).Append("/// <summary>").Append(summary).Append("</summary>\n");
        }
        var primary = PrimaryConstructor(type);
        Summary(text, type, "");
        text.Append(Header(type, primary));
        if (type.TypeKind == TypeKind.Delegate) return Capped(text.ToString());
        var body = new StringBuilder();
        var nested = new List<INamedTypeSymbol>();
        // Fields (an enum's members), constructors, properties, events, methods, then operators, each in declaration order.
        static int Rank(ISymbol member) => member switch
        {
            IFieldSymbol => 0, IMethodSymbol { MethodKind: MethodKind.Constructor } => 1, IPropertySymbol => 2, IEventSymbol => 3,
            IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion } => 5, _ => 4,
        };
        foreach (var member in type.GetMembers().OrderBy(Rank))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (text.Length + body.Length > MaxText) break;
            // Public members only: an interface's too (one without a modifier is public; a private, protected or internal
            // one is not part of what the type offers).
            if (member.DeclaredAccessibility != Accessibility.Public || Generated(member)) continue;
            if (member is INamedTypeSymbol inner) { nested.Add(inner); continue; }
            string? line = member switch
            {
                IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum, HasConstantValue: true } value => $"{SyntaxFacts.GetKeywordKind(value.Name) switch { SyntaxKind.None => value.Name, _ => "@" + value.Name }} = {value.ConstantValue},",
                IFieldSymbol or IEventSymbol => Display(member) + ";",
                IPropertySymbol property when primary == null || !primary.Parameters.Any(p => p.Name == property.Name) => Display(property),
                IMethodSymbol { MethodKind: MethodKind.Constructor } constructor when !SymbolEqualityComparer.Default.Equals(constructor, primary) && !(type.IsValueType && constructor.Parameters.IsEmpty) => Display(constructor) + ";",
                IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.UserDefinedOperator or MethodKind.Conversion } => Display(member) + ";",
                _ => null,
            };
            if (line == null) continue;
            Summary(body, member, "    ");
            body.Append("    ").Append(line).Append('\n');
        }
        foreach (var inner in nested)
        {
            if (text.Length + body.Length > MaxText) break;
            body.Append("    ").Append(Header(inner, PrimaryConstructor(inner))).Append(" { … }\n");
        }
        // A record with nothing beyond its primary constructor is written as one: `record Name(...);`.
        if (body.Length == 0) text.Append(type.IsRecord ? ";" : " { }");
        else text.Append("\n{\n").Append(body).Append('}');
        return Capped(text.ToString());
    }
    /// <summary>
    /// A member's signature, with an enum default written as C# writes it (<c>PyLogLevel level = PyLogLevel.Info</c>,
    /// where the display alone gives <c>Info</c>).
    /// </summary>
    static string Display(ISymbol member)
    {
        var text = new StringBuilder(); var previous = "";
        foreach (var part in member.ToDisplayParts(MemberFormat))
        {
            if (part.Kind == SymbolDisplayPartKind.EnumMemberName && part.Symbol is IFieldSymbol { ContainingType: { TypeKind: TypeKind.Enum } owner } && previous != ".")
                text.Append(owner.ToDisplayString(TypeFormat)).Append('.');
            var written = part.ToString(); text.Append(written); previous = written;
        }
        return text.ToString();
    }
    /// <summary>What the compiler wrote for a record (equality, cloning, printing, Deconstruct) and a property's or event's accessors.</summary>
    static bool Generated(ISymbol member) =>
        member.IsImplicitlyDeclared || member is IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.StaticConstructor or MethodKind.Destructor }
        || !member.CanBeReferencedByName && member is not IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.UserDefinedOperator or MethodKind.Conversion }
        || member.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");
    /// <summary>A record's primary constructor, when its compiler-made <c>Deconstruct</c> has the same parameters.</summary>
    static IMethodSymbol? PrimaryConstructor(INamedTypeSymbol type)
    {
        if (!type.IsRecord) return null;
        var deconstruct = type.GetMembers("Deconstruct").OfType<IMethodSymbol>().FirstOrDefault(m => Generated(m) && m.Parameters.All(p => p.RefKind == RefKind.Out));
        if (deconstruct == null) return null;
        return type.InstanceConstructors.FirstOrDefault(c => c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Length == deconstruct.Parameters.Length
            && c.Parameters.Zip(deconstruct.Parameters).All(p => p.First.Name == p.Second.Name && SymbolEqualityComparer.IncludeNullability.Equals(p.First.Type, p.Second.Type)));
    }
    /// <summary><c>public sealed record Name(int A) : Base, IThing</c>, as a declaration begins.</summary>
    static string Header(INamedTypeSymbol type, IMethodSymbol? primary)
    {
        if (type.TypeKind == TypeKind.Delegate && type.DelegateInvokeMethod is { } invoke)
            return $"{AccessText(type)}delegate {invoke.ReturnType.ToDisplayString(TypeFormat)} {type.ToDisplayString(DeclaredNameFormat)}({string.Join(", ", invoke.Parameters.Select(Display))});";
        var modifiers = new List<string>();
        if (type.TypeKind == TypeKind.Class)
        {
            if (type.IsStatic) modifiers.Add("static");
            else if (type.IsAbstract) modifiers.Add("abstract");
            else if (type.IsSealed) modifiers.Add("sealed");
        }
        if (type.TypeKind == TypeKind.Struct)
        {
            if (type.IsReadOnly) modifiers.Add("readonly");
            if (type.IsRefLikeType) modifiers.Add("ref");
        }
        var kind = type.TypeKind switch
        {
            TypeKind.Struct => type.IsRecord ? "record struct" : "struct",
            TypeKind.Interface => "interface",
            TypeKind.Enum => "enum",
            _ => type.IsRecord ? "record" : "class",
        };
        var header = new StringBuilder(AccessText(type));
        foreach (var modifier in modifiers) header.Append(modifier).Append(' ');
        header.Append(kind).Append(' ').Append(type.ToDisplayString(DeclaredNameFormat));
        if (primary != null) header.Append('(').Append(string.Join(", ", primary.Parameters.Select(Display))).Append(')');
        var bases = new List<string>();
        if (type.TypeKind == TypeKind.Enum && type.EnumUnderlyingType is { SpecialType: not SpecialType.System_Int32 } underlying) bases.Add(Written(underlying));
        if (type.BaseType is { SpecialType: not (SpecialType.System_Object or SpecialType.System_ValueType or SpecialType.System_Enum) } baseType && type.TypeKind == TypeKind.Class) bases.Add(Written(baseType));
        // A record's IEquatable<itself> is the compiler's, never written in its declaration.
        bases.AddRange(type.Interfaces.Where(i => !(type.IsRecord && i.OriginalDefinition.ToDisplayString() == "System.IEquatable<T>" && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], type))).Select(Written));
        if (bases.Count > 0) header.Append(" : ").Append(string.Join(", ", bases));
        foreach (var parameter in type.TypeParameters.Where(p => p.ConstraintTypes.Length > 0 || p.HasReferenceTypeConstraint || p.HasValueTypeConstraint || p.HasUnmanagedTypeConstraint || p.HasNotNullConstraint || p.HasConstructorConstraint))
        {
            var constraints = new List<string>();
            if (parameter.HasReferenceTypeConstraint) constraints.Add("class");
            if (parameter.HasUnmanagedTypeConstraint) constraints.Add("unmanaged");
            else if (parameter.HasValueTypeConstraint) constraints.Add("struct");
            if (parameter.HasNotNullConstraint) constraints.Add("notnull");
            constraints.AddRange(parameter.ConstraintTypes.Select(Written));
            if (parameter.HasConstructorConstraint && !parameter.HasValueTypeConstraint) constraints.Add("new()");
            header.Append(" where ").Append(parameter.Name).Append(" : ").Append(string.Join(", ", constraints));
        }
        return header.ToString();
    }
    static string AccessText(ISymbol symbol) => symbol.DeclaredAccessibility switch
    {
        Microsoft.CodeAnalysis.Accessibility.Public => "public ",
        Microsoft.CodeAnalysis.Accessibility.Protected => "protected ",
        _ => "",
    };

    /// <summary>
    /// The one-line summary of each documented member of HassSharp, by documentation id, from the documentation file
    /// published beside the assembly (<c>HassSharp.xml</c>); empty without one. Read once: it holds HassSharp's own
    /// words only, no type or symbol.
    /// </summary>
    static readonly Lazy<IReadOnlyDictionary<string, string>> Summaries = new(() => ReadSummaries(() =>
        System.IO.File.OpenRead(Path.ChangeExtension(typeof(NodeAttribute).Assembly.Location, ".xml"))));
    /// <summary>The summaries of a documentation file <paramref name="open"/> gives.</summary>
    internal static IReadOnlyDictionary<string, string> ReadSummaries(Func<Stream> open)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var stream = open();
            foreach (var member in XDocument.Load(stream).Root?.Element("members")?.Elements("member") ?? [])
                if ((string?)member.Attribute("name") is { } name && member.Element("summary") is { } summary && Words(summary) is { Length: > 0 } words)
                    found[name] = words;
        }
        // Whatever goes wrong (no file, an odd location, a broken file), definitions are shown without summaries: never a
        // failure kept by the Lazy and thrown at every later request.
        catch (Exception) { found.Clear(); }
        return found;
    }
    /// <summary>
    /// A summary as one line of words: a <c>see</c> by its own words, or without any by the last part of what it names; a
    /// <c>paramref</c> by its name.
    /// </summary>
    internal static string Words(XElement summary)
    {
        var words = new StringBuilder();
        foreach (var node in summary.DescendantNodes())
        {
            // Text is words wherever it is, a see's own (<see cref="X">the entity state</see>) too.
            if (node is XText text) words.Append(text.Value);
            else if (node is XElement { Name.LocalName: "see" or "seealso" or "paramref" or "typeparamref" } element && !element.Nodes().Any())
            {
                var target = (string?)element.Attribute("cref") ?? (string?)element.Attribute("name") ?? (string?)element.Attribute("langword") ?? "";
                var bare = target.Contains(':') ? target[(target.IndexOf(':') + 1)..] : target;
                var parenthesis = bare.IndexOf('(');
                if (parenthesis >= 0) bare = bare[..parenthesis];
                words.Append(bare[(bare.LastIndexOf('.') + 1)..].Replace('{', '<').Replace('}', '>'));
            }
        }
        return string.Join(' ', words.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
