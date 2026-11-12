using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HassSharp;

/// <summary>Discovers source symbols only. Never emits or loads a user assembly.</summary>
public sealed class NodeLibraryCompiler(DiagnosticsProvider diagnostics)
{
    public const string WrapperPrefix = "#nullable enable\nnamespace NodeLib\n{\n";
    public const string WrapperSuffix = "\n}\n";
    public const int SourceLineOffset = 3;
    public static string WrapSource(string source) => WrapperPrefix + source + WrapperSuffix;
    static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier | SymbolDisplayMiscellaneousOptions.ExpandValueTuple);
    public static string TypeIdentity(ITypeSymbol type) => type.ToDisplayString(TypeFormat);
    static string Escape(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    /// <summary>Shared by library compilation and its language-service workspace.</summary>
    public static ImmutableArray<MetadataReference> FrameworkReferences() => References.Value;
    static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() => ProjectReferences.GetDefaultReferences()
        .Concat((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)))
        .DistinctBy(reference => reference.Display, StringComparer.Ordinal).ToImmutableArray());

    public NodeLibraryPreview Preview(IEnumerable<NodeLibrarySource> librarySources)
    {
        var sources = librarySources.ToImmutableArray();
        var errors = new List<NodeLibraryDiagnostic>();
        var trees = new Dictionary<SyntaxTree, NodeLibrarySource>();
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.Id) || sources.Count(s => s.Id == source.Id) > 1)
                errors.Add(new(source.Id, 1, 1, 1, 1, "Library source IDs must be nonempty and unique", 3));
            var tree = CSharpSyntaxTree.ParseText(WrapSource(source.Source), new CSharpParseOptions(LanguageVersion.Latest, documentationMode: DocumentationMode.Diagnose), path: source.Id);
            trees.Add(tree, source);
        }
        var syntaxTrees = trees.Keys.ToList();
        syntaxTrees.Add(CSharpSyntaxTree.ParseText(DiagnosticsProvider.GlobalUsings));
        if (diagnostics.HassEntitiesSyntaxTreeNullable() is { } entities) syntaxTrees.Add(entities);
        var compilation = CSharpCompilation.Create("HassSharpNodeLibrary", syntaxTrees, FrameworkReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
        void Report(Location location, string message, int severity = 3)
        {
            var span = location.GetLineSpan();
            var id = location.SourceTree != null && trees.TryGetValue(location.SourceTree, out var source) ? source.Id : "";
            errors.Add(new(id, Math.Max(1, span.StartLinePosition.Line + 1 - SourceLineOffset), span.StartLinePosition.Character + 1,
                Math.Max(1, span.EndLinePosition.Line + 1 - SourceLineOffset), span.EndLinePosition.Character + 1, message, severity));
        }
        foreach (var (tree, source) in trees)
        {
            var rootSyntax = (CompilationUnitSyntax)tree.GetRoot();
            if (rootSyntax.Members.Count != 1 || rootSyntax.Members[0] is not NamespaceDeclarationSyntax ns || ns.Name.ToString() != "NodeLib" || ns.CloseBraceToken.SpanStart != WrapperPrefix.Length + source.Source.Length + 1)
                Report(tree.GetRoot().GetLocation(), "Library source must stay inside the generated NodeLib namespace wrapper");
        }
        var frameworkAssembly = compilation.SourceModule.ReferencedAssemblySymbols.Single(a => a.Identity.Name == typeof(NodeAttribute).Assembly.GetName().Name);
        var nodeAttribute = frameworkAssembly.GetTypeByMetadataName("HassSharp.NodeAttribute");
        var portAttribute = frameworkAssembly.GetTypeByMetadataName("HassSharp.PortAttribute");
        string? PortLabel(ImmutableArray<AttributeData> attributes) => attributes.FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, portAttribute))?.ConstructorArguments.FirstOrDefault().Value as string;
        foreach (var diagnostic in compilation.GetDiagnostics().Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning))
            Report(diagnostic.Location, diagnostic.GetMessage(), (int)diagnostic.Severity);
        var root = VisualNodeCatalog.GetCatalog();
        var typeSymbols = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
        void Register(ITypeSymbol type)
        {
            var id = TypeIdentity(type);
            if (!typeSymbols.TryAdd(id, type)) return;
            if (type is IArrayTypeSymbol array) Register(array.ElementType);
            if (type is INamedTypeSymbol named)
            {
                foreach (var argument in named.TypeArguments) Register(argument);
            }
        }
        foreach (var (id, descriptor) in root["types"]!.AsObject())
        {
            var metadataName = VisualNodeCatalog.Text(descriptor, "clrType");
            if (compilation.GetTypeByMetadataName(metadataName) is { } symbol) { Register(symbol); typeSymbols[id] = symbol; }
        }
        var methods = new List<NodeMethodDescriptor>();
        foreach (var (tree, source) in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(declaration) is not IMethodSymbol method) continue;
                var attribute = method.GetAttributes().FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, nodeAttribute));
                if (attribute == null) continue;
                if (!ValidMethod(method)) { Report(declaration.Identifier.GetLocation(), "Node methods must be public static, closed, non-ref, non-pointer signatures; async void, abstract/virtual methods and ref-like types are not supported"); continue; }
                var title = attribute.ConstructorArguments.FirstOrDefault().Value as string;
                if (string.IsNullOrWhiteSpace(title)) { Report(declaration.Identifier.GetLocation(), "Node title must not be empty"); continue; }
                var returnType = method.ReturnType;
                var awaitResult = returnType is INamedTypeSymbol rt && rt.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks" && rt.Name is "Task" or "ValueTask";
                ITypeSymbol? result = method.ReturnsVoid ? null : awaitResult ? ((INamedTypeSymbol)returnType).TypeArguments.FirstOrDefault() : returnType;
                if (result != null) Register(result);
                var parameters = method.Parameters.Select(p => new NodeParameterDescriptor(p.Name, TypeIdentity(p.Type), TypeIdentity(p.Type),
                    p.Type.ToDisplayString() == "System.Threading.CancellationToken", p.HasExplicitDefaultValue, p.HasExplicitDefaultValue ? p.ExplicitDefaultValue : null, p)).ToImmutableArray();
                foreach (var parameter in parameters) if (!parameter.IsCancellationToken) Register(parameter.Symbol.Type);
                // Include signature types so overloads remain distinct; never include a generated assembly name or revision.
                var identity = TypeIdentity(method.ContainingType) + "." + method.Name + "(" + string.Join(",", parameters.Select(p => p.TypeId)) + ")";
                var kind = "library:" + Uri.EscapeDataString(source.Id) + ":" + Uri.EscapeDataString(identity);
                var doc = Documentation(method);
                var input = new JsonObject();
                foreach (var parameter in parameters.Where(p => !p.IsCancellationToken))
                {
                    var p = parameter.Symbol;
                    var optional = p.HasExplicitDefaultValue || Nullable(p.Type);
                    var rule = new JsonObject { ["types"] = new JsonArray(parameter.TypeId), ["required"] = !optional,
                        ["label"] = PortLabel(p.GetAttributes()) ?? p.Name, ["description"] = DocText(doc?.Elements("param").FirstOrDefault(e => (string?)e.Attribute("name") == p.Name)),
                        ["nullable"] = Nullable(p.Type), ["hasDefaultValue"] = p.HasExplicitDefaultValue };
                    if (p.HasExplicitDefaultValue && CanLiteral(p.Type) && (p.ExplicitDefaultValue is not double dv || double.IsFinite(dv)) && (p.ExplicitDefaultValue is not float fv || float.IsFinite(fv))) rule["default"] = new JsonObject { ["kind"] = "literal", ["type"] = parameter.TypeId, ["value"] = JsonSerializer.SerializeToNode(p.ExplicitDefaultValue) };
                    else if (Nullable(p.Type) && (!p.HasExplicitDefaultValue || p.ExplicitDefaultValue == null)) rule["default"] = new JsonObject { ["kind"] = "literal", ["type"] = parameter.TypeId, ["value"] = null };
                    input[p.Name] = rule;
                }
                root["nodes"]![kind] = new JsonObject {
                    ["label"] = title, ["category"] = attribute.NamedArguments.FirstOrDefault(a => a.Key == "Category").Value.Value as string ?? "Custom",
                    ["description"] = DocText(doc?.Element("summary")), ["minGraphVersion"] = 4, ["runtimeVersions"] = new JsonArray(4),
                    ["sourceId"] = source.Id, ["argumentStorage"] = "arguments", ["config"] = new JsonObject { ["arguments"] = new JsonObject { ["type"] = "object", ["default"] = new JsonObject(), ["description"] = "Typed method arguments" } },
                    ["inputs"] = input, ["outputs"] = result == null ? new JsonObject() : new JsonObject { ["result"] = new JsonObject { ["type"] = TypeIdentity(result), ["label"] = PortLabel(method.GetReturnTypeAttributes()) ?? "Result", ["description"] = DocText(doc?.Element("returns")) } },
                    ["execution"] = new JsonArray("next", "error") };
                if (methods.Any(m => m.Kind == kind)) { Report(declaration.Identifier.GetLocation(), "Duplicate node method signature"); continue; }
                methods.Add(new(kind, source.Id, TypeIdentity(method.ContainingType), Escape(method.Name), result == null ? null : TypeIdentity(result), awaitResult, parameters, method));
            }
        }
        // Declared signatures have finite structural type trees. Add their related types in
        // one separate pass, without recursively exploring related types' generic arguments.
        // Grow<T> : I<Grow<Grow<T>>> otherwise introduces endlessly deeper constructed types.
        // Every explicitly declared closed type was registered above, irrespective of depth.
        foreach (var declared in typeSymbols.Values.ToArray())
        {
            foreach (var contract in declared.AllInterfaces)
                if (!Unsupported(contract) && compilation.IsSymbolAccessibleWithin(contract, compilation.Assembly)) typeSymbols.TryAdd(TypeIdentity(contract), contract);
            for (var baseType = declared.BaseType; baseType != null; baseType = baseType.BaseType)
                if (!Unsupported(baseType) && compilation.IsSymbolAccessibleWithin(baseType, compilation.Assembly)) typeSymbols.TryAdd(TypeIdentity(baseType), baseType);
        }
        var types = root["types"]!.AsObject();
        foreach (var (id, symbol) in typeSymbols)
        {
            var type = types[id] as JsonObject ?? new JsonObject();
            if (!types.ContainsKey(id)) types[id] = type;
            type["label"] = symbol.ToDisplayString(); type["description"] ??= symbol.ToDisplayString();
            if (symbol is IArrayTypeSymbol arraySymbol) type["elementType"] = TypeIdentity(arraySymbol.ElementType);
            type["category"] = Category(symbol); type["nullable"] = Nullable(symbol); type["literalKind"] = LiteralKind(symbol);
            var scalar = symbol is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableScalar ? nullableScalar.TypeArguments[0] : symbol;
            if (scalar.SpecialType == SpecialType.System_Char) type["character"] = true;
            var bounds = NumericBounds(scalar.SpecialType);
            if (bounds is { } range) { type["min"] = range.Min; type["max"] = range.Max; }
            if (scalar.TypeKind == TypeKind.Enum) type["enumValues"] = new JsonArray(((INamedTypeSymbol)scalar).GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue && Convert.ToDecimal(f.ConstantValue) is >= -9007199254740991 and <= 9007199254740991).Select(f => JsonSerializer.SerializeToNode(f.ConstantValue)).ToArray());
            type["assignableTo"] = new JsonArray(typeSymbols.Where(pair => pair.Key != "entity_id" || id == "entity_id")
                .Where(pair => VisualCatalogSnapshot.StandardConversion(compilation, symbol, pair.Value)).Select(pair => (JsonNode?)JsonValue.Create(pair.Key)).ToArray());
        }
        // Keep optional CLR defaults that cannot be represented faithfully in the JSON literal editor
        // in the backend descriptor only. Omitting their argument preserves C# default semantics.
        foreach (var method in methods)
            foreach (var (_, input) in root["nodes"]![method.Kind]!["inputs"]!.AsObject())
                if (input is JsonObject rule && rule["default"] is JsonObject operand && !VisualNodeCatalog.ValidLiteral(VisualNodeCatalog.Text(operand, "type"), operand["value"], operand.ContainsKey("value"), root)) rule.Remove("default");
        var snapshot = new VisualCatalogSnapshot(root, sources, methods, typeSymbols.Select(p => new NodeTypeDescriptor(p.Key, TypeIdentity(p.Value), p.Value)), compilation, errors);
        return new(!errors.Any(e => e.Severity == 3), errors.ToImmutableArray(), snapshot);
    }

    // JSON numeric literals use the editor's IEEE-754 safe integer range for Int64/UInt64.
    // Wires retain the full CLR range and are not subject to literal-editor restrictions.
    static (double Min, double Max)? NumericBounds(SpecialType type) => type switch {
        SpecialType.System_SByte => (sbyte.MinValue, sbyte.MaxValue), SpecialType.System_Byte => (byte.MinValue, byte.MaxValue),
        SpecialType.System_Int16 => (short.MinValue, short.MaxValue), SpecialType.System_UInt16 => (ushort.MinValue, ushort.MaxValue),
        SpecialType.System_Int32 => (int.MinValue, int.MaxValue), SpecialType.System_UInt32 => (uint.MinValue, uint.MaxValue),
        SpecialType.System_Int64 => (-9007199254740991, 9007199254740991), SpecialType.System_UInt64 => (0, 9007199254740991),
        SpecialType.System_Single => (-float.MaxValue, float.MaxValue), SpecialType.System_Decimal => (-Math.BitDecrement((double)decimal.MaxValue), Math.BitDecrement((double)decimal.MaxValue)), _ => null };

    static bool Nullable(ITypeSymbol type) => type.NullableAnnotation == NullableAnnotation.Annotated || type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };
    static bool Unsupported(ITypeSymbol type) => type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer or TypeKind.TypeParameter or TypeKind.Error || type.IsRefLikeType ||
        type is IArrayTypeSymbol a && Unsupported(a.ElementType) || type is INamedTypeSymbol n && (n.IsUnboundGenericType || n.TypeArguments.Any(Unsupported) || n.ContainingType != null && Unsupported(n.ContainingType));
    static bool ValidMethod(IMethodSymbol method)
    {
        if (!method.IsStatic || method.IsAbstract || method.IsVirtual || method.DeclaredAccessibility != Accessibility.Public || method.IsGenericMethod || method.ReturnsByRef || method.ReturnsByRefReadonly || method.IsAsync && method.ReturnsVoid || Unsupported(method.ReturnType) || method.Parameters.Any(p => p.RefKind != RefKind.None || Unsupported(p.Type))) return false;
        for (var type = method.ContainingType; type != null; type = type.ContainingType)
            if (type.DeclaredAccessibility != Accessibility.Public || type.IsGenericType) return false;
        return true;
    }
    static XElement? Documentation(IMethodSymbol method)
    {
        try { return XElement.Parse(method.GetDocumentationCommentXml() ?? "<member/>"); } catch (System.Xml.XmlException) { return null; }
    }
    static string DocText(XElement? element) => element == null ? "" : string.Join(" ", element.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    static string Category(ITypeSymbol type) => LiteralKind(type) switch { "boolean" => "Boolean", "number" or "integer" => "Number", "string" or "enum" => "String", "array" => "Array", _ => type is IArrayTypeSymbol ? "Array" : "Object" };
    static bool CanLiteral(ITypeSymbol type) => LiteralKind(type) != null;
    internal static string? LiteralKind(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable) return LiteralKind(nullable.TypeArguments[0]);
        if (type.TypeKind == TypeKind.Enum) return "enum";
        return type.SpecialType switch { SpecialType.System_Boolean => "boolean", SpecialType.System_String or SpecialType.System_Char => "string",
            SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64 => "integer",
            SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal => "number",
            _ => type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString() switch { "System.Text.Json.Nodes.JsonObject" => "object", "System.Text.Json.Nodes.JsonArray" => "array", _ => null } };
    }
}
