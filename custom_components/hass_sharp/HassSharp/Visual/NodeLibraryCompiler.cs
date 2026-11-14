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

    /// <summary>
    /// The namespace a Custom C# node's code is placed in, inside <c>NodeLib</c> so library types are in scope: letters
    /// and digits of the node id are kept, every other character is written as <c>_</c> and four hex digits.
    /// </summary>
    public static string CustomNamespace(string nodeId)
    {
        var name = new System.Text.StringBuilder("Custom_");
        foreach (var c in nodeId) if (char.IsAsciiLetterOrDigit(c)) name.Append(c); else name.Append('_').Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
        return name.ToString();
    }
    public static string CustomWrapperPrefix(string nodeId) => "#nullable enable\nnamespace NodeLib." + CustomNamespace(nodeId) + "\n{\n";
    public static string WrapCustomSource(string nodeId, string source) => CustomWrapperPrefix(nodeId) + source + WrapperSuffix;
    /// <summary><c>roomTemperature</c> → <c>Room temperature</c>, <c>entity_id</c> → <c>Entity id</c>; a word in capitals stays as written.</summary>
    public static string InWords(string name)
    {
        var spaced = System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(name.Replace('_', ' '), "([a-z0-9])([A-Z])", "$1 $2"), "([A-Z]+)([A-Z][a-z])", "$1 $2");
        var words = spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Length > 1 && w.All(c => !char.IsLower(c)) ? w : w.ToLowerInvariant()).ToArray();
        var text = string.Join(' ', words);
        return text.Length == 0 ? name : char.ToUpperInvariant(text[0]) + text[1..];
    }
    static string LowerFirst(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    public NodeLibraryPreview Preview(IEnumerable<NodeLibrarySource> librarySources, CancellationToken cancellationToken = default) => Preview(librarySources, null, cancellationToken);
    /// <summary>
    /// Compiles the libraries, and with them each Custom C# node's code in its own namespace. Library nodes go into the
    /// catalog; a custom node's method, signature and types go into <see cref="VisualCatalogSnapshot.Custom"/> only, so
    /// the catalog (and its type entries) is the same with or without custom code.
    /// </summary>
    public NodeLibraryPreview Preview(IEnumerable<NodeLibrarySource> librarySources, IEnumerable<CustomNodeSource>? customSources, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sources = librarySources.ToImmutableArray();
        var errors = new List<NodeLibraryDiagnostic>();
        var trees = new Dictionary<SyntaxTree, NodeLibrarySource>();
        var parse = new CSharpParseOptions(LanguageVersion.Latest, documentationMode: DocumentationMode.Diagnose);
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.Id) || sources.Count(s => s.Id == source.Id) > 1)
                errors.Add(new(source.Id, 1, 1, 1, 1, "Library source IDs must be nonempty and unique", 3));
            var tree = CSharpSyntaxTree.ParseText(WrapSource(source.Source), parse, path: source.Id, cancellationToken: cancellationToken);
            trees.Add(tree, source);
        }
        var customTrees = new Dictionary<SyntaxTree, CustomNodeSource>();
        var customErrors = new Dictionary<string, List<NodeLibraryDiagnostic>>(StringComparer.Ordinal);
        foreach (var custom in customSources ?? [])
        {
            // A duplicate node id is the validator's to report; only its first node is compiled.
            if (!customErrors.TryAdd(custom.NodeId, [])) continue;
            customTrees.Add(CSharpSyntaxTree.ParseText(WrapCustomSource(custom.NodeId, custom.Source), parse, path: "$custom:" + custom.NodeId, cancellationToken: cancellationToken), custom);
        }
        var syntaxTrees = trees.Keys.Concat(customTrees.Keys).ToList();
        syntaxTrees.Add(CSharpSyntaxTree.ParseText(DiagnosticsProvider.GlobalUsings, cancellationToken: cancellationToken));
        syntaxTrees.Add(diagnostics.ServicesSyntaxTree());
        var compilation = CSharpCompilation.Create("HassSharpNodeLibrary", syntaxTrees, FrameworkReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
        void Report(Location location, string message, int severity = 3)
        {
            var span = location.GetLineSpan();
            NodeLibraryDiagnostic At(string id) => new(id, Math.Max(1, span.StartLinePosition.Line + 1 - SourceLineOffset), span.StartLinePosition.Character + 1,
                Math.Max(1, span.EndLinePosition.Line + 1 - SourceLineOffset), span.EndLinePosition.Character + 1, message, severity);
            if (location.SourceTree != null && customTrees.TryGetValue(location.SourceTree, out var custom)) { customErrors[custom.NodeId].Add(At(custom.NodeId)); return; }
            errors.Add(At(location.SourceTree != null && trees.TryGetValue(location.SourceTree, out var source) ? source.Id : ""));
        }
        foreach (var (tree, source) in trees)
        {
            var rootSyntax = (CompilationUnitSyntax)tree.GetRoot(cancellationToken);
            if (rootSyntax.Members.Count != 1 || rootSyntax.Members[0] is not NamespaceDeclarationSyntax ns || ns.Name.ToString() != "NodeLib" || ns.CloseBraceToken.SpanStart != WrapperPrefix.Length + source.Source.Length + 1)
                Report(tree.GetRoot(cancellationToken).GetLocation(), "Library source must stay inside the generated NodeLib namespace wrapper");
        }
        foreach (var (tree, custom) in customTrees)
        {
            var rootSyntax = (CompilationUnitSyntax)tree.GetRoot(cancellationToken);
            if (rootSyntax.Members.Count != 1 || rootSyntax.Members[0] is not NamespaceDeclarationSyntax ns || ns.Name.ToString() != "NodeLib." + CustomNamespace(custom.NodeId) || ns.CloseBraceToken.SpanStart != CustomWrapperPrefix(custom.NodeId).Length + custom.Source.Length + 1)
                Report(tree.GetRoot(cancellationToken).GetLocation(), "The code must stay inside the namespace it is placed in; check its braces");
        }
        var frameworkAssembly = compilation.SourceModule.ReferencedAssemblySymbols.Single(a => a.Identity.Name == typeof(NodeAttribute).Assembly.GetName().Name);
        var nodeAttribute = frameworkAssembly.GetTypeByMetadataName("HassSharp.NodeAttribute");
        var portAttribute = frameworkAssembly.GetTypeByMetadataName("HassSharp.PortAttribute");
        var nodeContext = frameworkAssembly.GetTypeByMetadataName("HassSharp.NodeContext");
        string? PortLabel(ImmutableArray<AttributeData> attributes) => attributes.FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, portAttribute))?.ConstructorArguments.FirstOrDefault().Value as string;
        // A Custom C# node's code need not document itself, wholly or in part (an injected NodeContext or
        // CancellationToken is no pin to describe): its card says what the node is.
        foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken).Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning))
            if (!(diagnostic.Id is "CS1591" or "CS1573" && diagnostic.Location.SourceTree is { } tree && customTrees.ContainsKey(tree)))
                Report(diagnostic.Location, diagnostic.GetMessage(), (int)diagnostic.Severity);
        var root = VisualNodeCatalog.GetCatalog();
        var graphVersion = root["graphVersion"]!.GetValue<int>();
        var typeSymbols = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
        void Register(Dictionary<string, ITypeSymbol> into, ITypeSymbol type, IReadOnlyDictionary<string, ITypeSymbol>? known = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = TypeIdentity(type);
            if (known?.ContainsKey(id) == true || !into.TryAdd(id, type)) return;
            if (type is IArrayTypeSymbol array) Register(into, array.ElementType, known);
            if (type is INamedTypeSymbol named)
            {
                foreach (var argument in named.TypeArguments) Register(into, argument, known);
            }
        }
        // Declared signatures have finite structural type trees. Add their related types in
        // one separate pass, without recursively exploring related types' generic arguments.
        // Grow<T> : I<Grow<Grow<T>>> otherwise introduces endlessly deeper constructed types.
        // Every explicitly declared closed type was registered above, irrespective of depth.
        void Related(Dictionary<string, ITypeSymbol> into)
        {
            foreach (var declared in into.Values.ToArray())
            {
                foreach (var contract in declared.AllInterfaces)
                    if (!Unsupported(contract) && compilation.IsSymbolAccessibleWithin(contract, compilation.Assembly)) into.TryAdd(TypeIdentity(contract), contract);
                for (var baseType = declared.BaseType; baseType != null; baseType = baseType.BaseType)
                    if (!Unsupported(baseType) && compilation.IsSymbolAccessibleWithin(baseType, compilation.Assembly)) into.TryAdd(TypeIdentity(baseType), baseType);
            }
        }
        foreach (var (id, descriptor) in root["types"]!.AsObject())
        {
            var metadataName = VisualNodeCatalog.Text(descriptor, "clrType");
            if (compilation.GetTypeByMetadataName(metadataName) is { } symbol) { Register(typeSymbols, symbol); typeSymbols[id] = symbol; }
        }
        // The types every generation has, whatever its libraries: a custom node lists only the types it uses beyond them.
        var builtIn = new Dictionary<string, ITypeSymbol>(typeSymbols, StringComparer.Ordinal);
        Related(builtIn);
        var refined = (string id) => root["types"]![id]?["refines"] != null;

        // The pins of one [Node] method, as the catalog describes a library node; null (reported) when it cannot be one.
        (string Title, string Description, string Category, bool Pure, JsonObject Inputs, JsonObject Outputs, NodeMethodDescriptor Method)? Describe(
            IMethodSymbol method, MethodDeclarationSyntax declaration, AttributeData attribute, string kind, string sourceId, Action<ITypeSymbol> register)
        {
            if (!ValidMethod(method)) { Report(declaration.Identifier.GetLocation(), "Node methods must be public static, closed, non-ref, non-pointer signatures; async void, abstract/virtual methods and ref-like types are not supported"); return null; }
            var title = attribute.ConstructorArguments.FirstOrDefault().Value as string;
            if (string.IsNullOrWhiteSpace(title)) { Report(declaration.Identifier.GetLocation(), "Node title must not be empty"); return null; }
            var pure = attribute.NamedArguments.FirstOrDefault(a => a.Key == "Pure").Value.Value is true;
            var returnType = method.ReturnType;
            var awaitResult = returnType is INamedTypeSymbol rt && rt.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks" && rt.Name is "Task" or "ValueTask";
            ITypeSymbol? result = method.ReturnsVoid ? null : awaitResult ? ((INamedTypeSymbol)returnType).TypeArguments.FirstOrDefault() : returnType;
            if (pure && result == null) { Report(declaration.Identifier.GetLocation(), "A value node (Pure = true) must return a value"); return null; }
            var doc = Documentation(method, cancellationToken);
            var outputs = new JsonObject();
            ImmutableArray<NodeOutputDescriptor> outputPorts = [];
            if (result is INamedTypeSymbol { IsTupleType: true } tuple)
            {
                var elements = tuple.TupleElements;
                if (elements.Any(e => !e.IsExplicitlyNamedTupleElement)) { Report(declaration.ReturnType.GetLocation(), "Name each value the method returns, such as (double Average, double Max)"); return null; }
                var ids = elements.Select(e => LowerFirst(e.Name)).ToArray();
                if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) { Report(declaration.ReturnType.GetLocation(), "Give each value the method returns its own name"); return null; }
                for (var i = 0; i < elements.Length; i++)
                {
                    register(elements[i].Type);
                    outputs[ids[i]] = new JsonObject { ["type"] = TypeIdentity(elements[i].Type), ["label"] = InWords(elements[i].Name), ["description"] = i == 0 ? DocText(doc?.Element("returns")) : "" };
                }
                outputPorts = elements.Select((e, i) => new NodeOutputDescriptor(ids[i], Escape(e.Name))).ToImmutableArray();
            }
            else if (result != null)
            {
                register(result);
                outputs["result"] = new JsonObject { ["type"] = TypeIdentity(result), ["label"] = PortLabel(method.GetReturnTypeAttributes()) ?? "Result", ["description"] = DocText(doc?.Element("returns")) };
                outputPorts = [new("result", null)];
            }
            var parameters = method.Parameters.Select(p => new NodeParameterDescriptor(p.Name, TypeIdentity(p.Type), TypeIdentity(p.Type),
                p.Type.ToDisplayString() == "System.Threading.CancellationToken", p.HasExplicitDefaultValue, p.HasExplicitDefaultValue ? p.ExplicitDefaultValue : null, p,
                SymbolEqualityComparer.Default.Equals(p.Type, nodeContext))).ToImmutableArray();
            var input = new JsonObject();
            foreach (var parameter in parameters.Where(p => !p.Injected))
            {
                var p = parameter.Symbol;
                register(p.Type);
                var optional = p.HasExplicitDefaultValue || Nullable(p.Type);
                var rule = new JsonObject { ["types"] = new JsonArray(parameter.TypeId), ["required"] = !optional,
                    ["label"] = PortLabel(p.GetAttributes()) ?? InWords(p.Name), ["description"] = DocText(doc?.Elements("param").FirstOrDefault(e => (string?)e.Attribute("name") == p.Name)),
                    ["nullable"] = Nullable(p.Type), ["hasDefaultValue"] = p.HasExplicitDefaultValue };
                if (p.HasExplicitDefaultValue && CanLiteral(p.Type) && (p.ExplicitDefaultValue is not double dv || double.IsFinite(dv)) && (p.ExplicitDefaultValue is not float fv || float.IsFinite(fv))) rule["default"] = new JsonObject { ["kind"] = "literal", ["type"] = parameter.TypeId, ["value"] = JsonSerializer.SerializeToNode(p.ExplicitDefaultValue) };
                else if (Nullable(p.Type) && (!p.HasExplicitDefaultValue || p.ExplicitDefaultValue == null)) rule["default"] = new JsonObject { ["kind"] = "literal", ["type"] = parameter.TypeId, ["value"] = null };
                input[p.Name] = rule;
            }
            var descriptor = new NodeMethodDescriptor(kind, sourceId, TypeIdentity(method.ContainingType), Escape(method.Name), result == null ? null : TypeIdentity(result), awaitResult, parameters, method)
                { Outputs = outputPorts, Pure = pure };
            return (title, DocText(doc?.Element("summary")), attribute.NamedArguments.FirstOrDefault(a => a.Key == "Category").Value.Value as string ?? "Custom", pure, input, outputs, descriptor);
        }
        IEnumerable<(MethodDeclarationSyntax Declaration, IMethodSymbol Method, AttributeData Attribute)> NodeMethods(SyntaxTree tree)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot(cancellationToken).DescendantNodes().OfType<MethodDeclarationSyntax>())
                if (model.GetDeclaredSymbol(declaration, cancellationToken) is IMethodSymbol method && method.GetAttributes().FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, nodeAttribute)) is { } attribute)
                    yield return (declaration, method, attribute);
        }
        var methods = new List<NodeMethodDescriptor>();
        foreach (var (tree, source) in trees)
        {
            var named = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (declaration, method, attribute) in NodeMethods(tree))
            {
                var identity = TypeIdentity(method.ContainingType) + "." + method.Name;
                if (!named.Add(identity)) { Report(declaration.Identifier.GetLocation(), "Give each node method its own name"); continue; }
                var kind = "library:" + Uri.EscapeDataString(source.Id) + ":" + Uri.EscapeDataString(identity);
                if (Describe(method, declaration, attribute, kind, source.Id, type => Register(typeSymbols, type)) is not { } described) continue;
                var spec = new JsonObject {
                    ["label"] = described.Title, ["category"] = described.Category, ["description"] = described.Description,
                    ["minGraphVersion"] = graphVersion, ["runtimeVersions"] = new JsonArray(graphVersion),
                    ["sourceId"] = source.Id, ["argumentStorage"] = "arguments", ["config"] = new JsonObject { ["arguments"] = new JsonObject { ["type"] = "object", ["default"] = new JsonObject(), ["description"] = "The values this step takes" } },
                    ["inputs"] = described.Inputs, ["outputs"] = described.Outputs };
                if (described.Pure) spec["pure"] = true; else spec["execution"] = new JsonArray("next", "error");
                root["nodes"]![kind] = spec;
                methods.Add(described.Method);
            }
        }
        Related(typeSymbols);
        var types = root["types"]!.AsObject();
        foreach (var (id, symbol) in typeSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = types[id] as JsonObject ?? new JsonObject();
            if (!types.ContainsKey(id)) types[id] = type;
            // Only roots are CLR conversion targets; refinements are decided by the chain before Roslyn.
            DescribeType(type, symbol, typeSymbols.Where(pair => !refined(pair.Key)), compilation);
        }
        VisualNodeCatalog.RefreshStructuralTypes(root);
        // Keep optional CLR defaults that cannot be represented faithfully in the JSON literal editor
        // in the backend descriptor only. Omitting their argument preserves C# default semantics.
        foreach (var method in methods)
            foreach (var (_, input) in root["nodes"]![method.Kind]!["inputs"]!.AsObject())
                if (input is JsonObject rule && rule["default"] is JsonObject operand && !VisualNodeCatalog.ValidLiteral(VisualNodeCatalog.Text(operand, "type"), operand["value"], operand.ContainsKey("value"), root)) rule.Remove("default");
        var customs = new List<CustomNodeDescriptor>();
        foreach (var (tree, custom) in customTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var own = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            JsonObject? signature = null; NodeMethodDescriptor? descriptor = null;
            // Code that does not compile has no signature; its errors are the node's.
            if (!customErrors[custom.NodeId].Any(d => d.Severity == 3))
            {
                var found = NodeMethods(tree).ToArray();
                if (found.Length != 1)
                    Report(found.Length == 0 ? tree.GetRoot(cancellationToken).DescendantNodes().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.GetLocation() ?? tree.GetRoot(cancellationToken).GetLocation() : found[1].Declaration.Identifier.GetLocation(),
                        found.Length == 0 ? "Mark one method with [Node]: it becomes this node" : $"Mark only one method with [Node]; this code has {found.Length}");
                else if (Describe(found[0].Method, found[0].Declaration, found[0].Attribute, "custom", custom.NodeId, type => Register(own, type, builtIn)) is { } described)
                {
                    // Its own types are compared with the built-in types and each other only, so a library change never changes them.
                    var entries = new JsonObject();
                    foreach (var (id, symbol) in own)
                    {
                        var entry = new JsonObject();
                        DescribeType(entry, symbol, builtIn.Where(pair => !refined(pair.Key)).Concat(own), compilation);
                        entries[id] = entry;
                    }
                    foreach (var (_, input) in described.Inputs)
                        if (input is JsonObject rule && rule["default"] is JsonObject operand && VisualNodeCatalog.Text(operand, "type") is var typeId &&
                            !VisualNodeCatalog.ValidLiteral(typeId, operand["value"], operand.ContainsKey("value"), types.ContainsKey(typeId) ? root : new JsonObject { ["types"] = new JsonObject { [typeId] = entries[typeId]?.DeepClone() } }))
                            rule.Remove("default");
                    signature = new JsonObject { ["title"] = described.Title, ["description"] = described.Description, ["pure"] = described.Pure,
                        ["inputs"] = described.Inputs, ["outputs"] = described.Outputs, ["types"] = entries };
                    descriptor = described.Method;
                }
            }
            customs.Add(new(custom.NodeId, signature == null ? null : descriptor, signature, own.Select(p => new NodeTypeDescriptor(p.Key, TypeIdentity(p.Value), p.Value)).ToImmutableArray(), customErrors[custom.NodeId].ToImmutableArray()));
        }
        var snapshot = new VisualCatalogSnapshot(root, sources, methods, typeSymbols.Select(p => new NodeTypeDescriptor(p.Key, TypeIdentity(p.Value), p.Value)), compilation, errors, customs);
        return new(!errors.Any(e => e.Severity == 3), errors.ToImmutableArray(), snapshot);
    }

    /// <summary>A type's catalog entry: its words, literal rules and the targets (of <paramref name="targets"/>) it converts to implicitly.</summary>
    static void DescribeType(JsonObject type, ITypeSymbol symbol, IEnumerable<KeyValuePair<string, ITypeSymbol>> targets, CSharpCompilation compilation)
    {
        type["label"] ??= symbol.ToDisplayString(); type["description"] ??= symbol.ToDisplayString();
        if (symbol is IArrayTypeSymbol arraySymbol) type["elementType"] = TypeIdentity(arraySymbol.ElementType);
        type["category"] = Category(symbol); type["nullable"] = Nullable(symbol); type["literalKind"] = LiteralKind(symbol);
        var scalar = symbol is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableScalar ? nullableScalar.TypeArguments[0] : symbol;
        if (scalar.SpecialType == SpecialType.System_Char) type["character"] = true;
        var bounds = NumericBounds(scalar.SpecialType);
        if (bounds is { } range) { type["min"] = range.Min; type["max"] = range.Max; }
        if (scalar.TypeKind == TypeKind.Enum)
        {
            var members = ((INamedTypeSymbol)scalar).GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue && Convert.ToDecimal(f.ConstantValue) is >= -9007199254740991 and <= 9007199254740991).ToArray();
            type["enumValues"] = new JsonArray(members.Select(f => JsonSerializer.SerializeToNode(f.ConstantValue)).ToArray());
            type["enumLabels"] = new JsonArray(members.Select(f => (JsonNode?)JsonValue.Create(InWords(f.Name))).ToArray());
        }
        type["assignableTo"] = new JsonArray(targets.Where(pair => VisualCatalogSnapshot.StandardConversion(compilation, symbol, pair.Value)).Select(pair => (JsonNode?)JsonValue.Create(pair.Key)).ToArray());
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
    static XElement? Documentation(IMethodSymbol method, CancellationToken cancellationToken)
    {
        try { return XElement.Parse(method.GetDocumentationCommentXml(cancellationToken: cancellationToken) ?? "<member/>"); } catch (System.Xml.XmlException) { return null; }
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
