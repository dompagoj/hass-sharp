using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HassSharp;

/// <summary>Discovers source symbols only. Never emits or loads a user assembly.</summary>
/// <param name="serviceCalls">The typed service calls to compile against; the accepted ones when not given.</param>
public sealed class NodeLibraryCompiler(DiagnosticsProvider diagnostics, GeneratedServiceCalls? serviceCalls = null)
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
    /// <summary>Whether a parameter or returned value's name can be a pin's id, as both validators read pin ids.</summary>
    static bool PinId(string id) => VisualTypes.Matches(id, "^[A-Za-z_][A-Za-z0-9_]*$");
    /// <summary>What a pin's name may contain, said at the parameter or returned value whose name is not one.</summary>
    internal static string PinNameMessage(string what, string name) => $"Name this {what} with English letters, digits and _ only; {name} cannot name a pin";

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
        // The typed service calls as their built assembly: nothing of them is bound again here.
        var compilation = CSharpCompilation.Create("HassSharpNodeLibrary", syntaxTrees, FrameworkReferences().Add((serviceCalls ?? diagnostics.ServiceCalls()).Reference),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
        void Report(Location location, string message, int severity = 3)
        {
            var span = location.GetLineSpan();
            NodeLibraryDiagnostic At(string id) => new(id, Math.Max(1, span.StartLinePosition.Line + 1 - SourceLineOffset), span.StartLinePosition.Character + 1,
                Math.Max(1, span.EndLinePosition.Line + 1 - SourceLineOffset), span.EndLinePosition.Character + 1, message, severity);
            if (location.SourceTree != null && customTrees.TryGetValue(location.SourceTree, out var custom)) { customErrors[custom.NodeId].Add(At(custom.NodeId)); return; }
            // Not in any library's or node's code (the typed service calls, the global usings): no source's line.
            if (location.SourceTree != null && !trees.ContainsKey(location.SourceTree))
            {
                errors.Add(new("", 1, 1, 1, 1, (location.SourceTree.FilePath == DiagnosticsProvider.ServicesPath ? "In the typed service calls: " : "") + message, severity));
                return;
            }
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
        // A missing doc comment matters only on a library's [Node] method, whose summary is the node's description in the
        // editor: not on a type, a record's parameters and properties, or another member, which would only drown what
        // matters; and a parameter without its <param> only on such a method (a pin). A Custom C# node's code need not
        // document itself, wholly or in part: its card and its title say what the node is (an injected NodeContext or
        // CancellationToken is no pin to describe), and every automation using it would carry the warning.
        bool NodeMethod(ISymbol? symbol) => symbol is IMethodSymbol method && method.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, nodeAttribute));
        var models = new Dictionary<SyntaxTree, SemanticModel>();
        ISymbol? DeclaredAt(Location location)
        {
            if (location.SourceTree is not { } tree) return null;
            if (!models.TryGetValue(tree, out var model)) models[tree] = model = compilation.GetSemanticModel(tree);
            for (var node = tree.GetRoot(cancellationToken).FindToken(location.SourceSpan.Start).Parent; node != null; node = node.Parent)
                if (model.GetDeclaredSymbol(node, cancellationToken) is { } declared) return declared;
            return null;
        }
        foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken).Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning))
        {
            if (diagnostic.Id is "CS1591" or "CS1573" && diagnostic.Location.SourceTree is { } tree && customTrees.ContainsKey(tree)) continue;
            if (diagnostic.Id == "CS1591" && !NodeMethod(DeclaredAt(diagnostic.Location))) continue;
            if (diagnostic.Id == "CS1573" && !NodeMethod(DeclaredAt(diagnostic.Location)?.ContainingSymbol)) continue;
            Report(diagnostic.Location, diagnostic.GetMessage(), (int)diagnostic.Severity);
        }
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
        void Related(Dictionary<string, ITypeSymbol> into, IEnumerable<ITypeSymbol>? only = null)
        {
            foreach (var declared in (only ?? into.Values).ToArray())
            {
                // A supertype nested deeper than a pin may be is left out, as a member of such a type is: comparing it
                // for conversions would take as long as a pin of that depth.
                foreach (var contract in declared.AllInterfaces)
                    if (!Unsupported(contract) && !TooDeep(contract) && compilation.IsSymbolAccessibleWithin(contract, compilation.Assembly)) into.TryAdd(TypeIdentity(contract), contract);
                for (var baseType = declared.BaseType; baseType != null; baseType = baseType.BaseType)
                    if (!Unsupported(baseType) && !TooDeep(baseType) && compilation.IsSymbolAccessibleWithin(baseType, compilation.Assembly)) into.TryAdd(TypeIdentity(baseType), baseType);
            }
        }
        foreach (var (id, descriptor) in root["types"]!.AsObject())
        {
            var metadataName = VisualNodeCatalog.Text(descriptor, "clrType");
            if (compilation.GetTypeByMetadataName(metadataName) is { } symbol) { Register(typeSymbols, symbol); typeSymbols[id] = symbol; }
        }
        // The types of the static catalog, which every generation has and the editor knows before the server's catalog
        // arrives: a custom node lists every type it uses beyond them, so its pins validate against the static catalog too.
        var builtIn = new Dictionary<string, ITypeSymbol>(typeSymbols, StringComparer.Ordinal);
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
                if (elements.FirstOrDefault(e => !PinId(LowerFirst(e.Name))) is { } unnamed) { Report(unnamed.Locations.FirstOrDefault() ?? declaration.ReturnType.GetLocation(), PinNameMessage("value", unnamed.Name)); return null; }
                if (elements.FirstOrDefault(e => TooDeep(e.Type)) is { } deep) { Report(deep.Locations.FirstOrDefault() ?? declaration.ReturnType.GetLocation(), TooDeepMessage); return null; }
                for (var i = 0; i < elements.Length; i++)
                {
                    register(elements[i].Type);
                    outputs[ids[i]] = new JsonObject { ["type"] = TypeIdentity(elements[i].Type), ["label"] = InWords(elements[i].Name), ["description"] = i == 0 ? DocText(doc?.Element("returns")) : "" };
                }
                outputPorts = elements.Select((e, i) => new NodeOutputDescriptor(ids[i], Escape(e.Name))).ToImmutableArray();
            }
            else if (result != null)
            {
                if (TooDeep(result)) { Report(declaration.ReturnType.GetLocation(), TooDeepMessage); return null; }
                register(result);
                outputs["result"] = new JsonObject { ["type"] = TypeIdentity(result), ["label"] = PortLabel(method.GetReturnTypeAttributes()) ?? "Result", ["description"] = DocText(doc?.Element("returns")) };
                outputPorts = [new("result", null)];
            }
            var parameters = method.Parameters.Select(p => new NodeParameterDescriptor(p.Name, TypeIdentity(p.Type), TypeIdentity(p.Type),
                p.Type.ToDisplayString() == "System.Threading.CancellationToken", p.HasExplicitDefaultValue, p.HasExplicitDefaultValue ? p.ExplicitDefaultValue : null, p,
                SymbolEqualityComparer.Default.Equals(p.Type, nodeContext))).ToImmutableArray();
            // A pin is named by its parameter: C# takes letters of any language, a pin id only English ones.
            if (parameters.FirstOrDefault(p => !p.Injected && !PinId(p.Name)) is { } foreign) { Report(foreign.Symbol.Locations.FirstOrDefault() ?? declaration.Identifier.GetLocation(), PinNameMessage("input", foreign.Name)); return null; }
            if (parameters.FirstOrDefault(p => !p.Injected && TooDeep(p.Symbol.Type)) is { } nested) { Report(nested.Symbol.Locations.FirstOrDefault() ?? declaration.Identifier.GetLocation(), TooDeepMessage); return null; }
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
        // What a Select property node can read: the members of each class, record and struct declared in a node library,
        // a Custom C# node or HassSharp itself. A property's type joins the catalog as a pin's type does (with what it
        // relates to), and its own members are read in turn. The walk stops at generic types it meets through a property
        // (Box<T> with a Box<Box<T>> property would never end): such a type is listed, with no properties of its own. A
        // generic type a pin names, or one a pin's type relates to, has its properties.
        void Properties(Dictionary<string, ITypeSymbol> into, HashSet<string> named, Dictionary<string, ImmutableArray<(string Name, ITypeSymbol Type, ISymbol Member)>> found, IReadOnlyDictionary<string, ITypeSymbol>? known, bool related)
        {
            var pending = new Queue<string>(into.Keys);
            while (pending.TryDequeue(out var id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (found.ContainsKey(id) || PropertyOwner(into[id], frameworkAssembly) is not { } owner || Generic(owner) && !named.Contains(id)) continue;
                var members = Members(owner, compilation);
                found[id] = members;
                var before = into.Keys.ToHashSet(StringComparer.Ordinal);
                foreach (var member in members) Register(into, member.Type, known);
                var added = into.Where(pair => !before.Contains(pair.Key)).ToArray();
                if (related) Related(into, added.Select(pair => pair.Value));
                foreach (var key in into.Keys) if (!before.Contains(key)) pending.Enqueue(key);
            }
        }
        var pinned = new HashSet<string>(typeSymbols.Keys, StringComparer.Ordinal);
        Related(typeSymbols);
        pinned.UnionWith(typeSymbols.Keys);
        var properties = new Dictionary<string, ImmutableArray<(string Name, ITypeSymbol Type, ISymbol Member)>>(StringComparer.Ordinal);
        Properties(typeSymbols, pinned, properties, null, true);
        var types = root["types"]!.AsObject();
        foreach (var (id, symbol) in typeSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = types[id] as JsonObject ?? new JsonObject();
            if (!types.ContainsKey(id)) types[id] = type;
            // Only roots are CLR conversion targets; refinements are decided by the chain before Roslyn.
            DescribeType(type, symbol, typeSymbols.Where(pair => !refined(pair.Key)), compilation);
            if (properties.TryGetValue(id, out var members) && !members.IsEmpty) type["properties"] = PropertyList(members, cancellationToken);
        }
        VisualNodeCatalog.RefreshStructuralTypes(root);
        // Keep optional CLR defaults that cannot be represented faithfully in the JSON literal editor
        // in the backend descriptor only. Omitting their argument preserves C# default semantics.
        foreach (var method in methods)
            foreach (var (_, input) in root["nodes"]![method.Kind]!["inputs"]!.AsObject())
                if (input is JsonObject rule && rule["default"] is JsonObject operand && !VisualNodeCatalog.ValidLiteral(VisualNodeCatalog.Text(operand, "type"), operand["value"], operand.ContainsKey("value"), root)) rule.Remove("default");
        // A type declared in one Custom C# node's code is that node's own: another node's pins may not use it.
        bool OwnTypesArePrivate(MethodDeclarationSyntax declaration, SyntaxTree tree, IEnumerable<ITypeSymbol> used)
        {
            foreach (var type in used)
                if (type.OriginalDefinition.DeclaringSyntaxReferences.Select(r => r.SyntaxTree).FirstOrDefault(t => t != tree && customTrees.ContainsKey(t)) is { } other)
                {
                    Report(declaration.Identifier.GetLocation(), $"{type.ToDisplayString()} belongs to the Custom C# node {customTrees[other].NodeId}; declare the type in this code or in a node library to use it here");
                    return false;
                }
            return true;
        }
        var customs = new List<CustomNodeDescriptor>();
        foreach (var (tree, custom) in customTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var own = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            var ownProperties = new Dictionary<string, ImmutableArray<(string Name, ITypeSymbol Type, ISymbol Member)>>(StringComparer.Ordinal);
            // The types of the pins' properties are the node's as well (built-in ones aside), so another node's are refused too.
            bool WithProperties() { Properties(own, [.. own.Keys], ownProperties, builtIn, false); return true; }
            JsonObject? signature = null; NodeMethodDescriptor? descriptor = null;
            // Code that does not compile has no signature; its errors are the node's.
            if (!customErrors[custom.NodeId].Any(d => d.Severity == 3))
            {
                var found = NodeMethods(tree).ToArray();
                if (found.Length != 1)
                    Report(found.Length == 0 ? tree.GetRoot(cancellationToken).DescendantNodes().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.GetLocation() ?? tree.GetRoot(cancellationToken).GetLocation() : found[1].Declaration.Identifier.GetLocation(),
                        found.Length == 0 ? "Mark one method with [Node]: it becomes this node" : $"Mark only one method with [Node]; this code has {found.Length}");
                else if (Describe(found[0].Method, found[0].Declaration, found[0].Attribute, "custom", custom.NodeId, type => Register(own, type, builtIn)) is { } described
                    && WithProperties() && OwnTypesArePrivate(found[0].Declaration, tree, own.Values))
                {
                    // Each type the pins use beyond the built-in ones, with what it converts to and from: every type of this
                    // generation, its own other types, and every base type and interface it has (by id, so a pin of such a
                    // type in another Custom C# node matches without that node's code). A type another Custom C# node
                    // declares is refused above: it stays private to that node.
                    var generation = typeSymbols.Where(pair => !refined(pair.Key)).Concat(own.Where(pair => !typeSymbols.ContainsKey(pair.Key))).ToArray();
                    var entries = new JsonObject();
                    foreach (var (id, symbol) in own)
                    {
                        var entry = new JsonObject();
                        var supertypes = symbol.AllInterfaces.Cast<ITypeSymbol>().Concat(BaseTypes(symbol))
                            .Where(t => !Unsupported(t) && !TooDeep(t) && compilation.IsSymbolAccessibleWithin(t, compilation.Assembly))
                            .Select(t => KeyValuePair.Create(TypeIdentity(t), t)).Where(pair => !generation.Any(g => g.Key == pair.Key)).DistinctBy(pair => pair.Key);
                        DescribeType(entry, symbol, generation.Concat(supertypes), compilation);
                        entry["assignableFrom"] = new JsonArray(generation.Where(pair => pair.Key != id && VisualCatalogSnapshot.StandardConversion(compilation, pair.Value, symbol))
                            .Select(pair => (JsonNode?)JsonValue.Create(pair.Key)).ToArray());
                        if (ownProperties.TryGetValue(id, out var members) && !members.IsEmpty) entry["properties"] = PropertyList(members, cancellationToken);
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

    /// <summary>
    /// How many levels a pin's type may nest: the type itself, then each type argument, array element, tuple element and
    /// nullable wrapper inside it is one more (<c>int</c> is 1, <c>List&lt;int?&gt;</c> 3). Comparing types for what converts
    /// to what takes twice as long per level past this, and cannot be cancelled, so a deeper pin is refused first.
    /// </summary>
    internal const int PinDepth = 8;
    internal const string TooDeepMessage = "This type is nested too deeply to be a pin; use a simpler type";
    static bool TooDeep(ITypeSymbol type) => Nesting(type, new Dictionary<ITypeSymbol, int>(SymbolEqualityComparer.Default)) > PinDepth;
    /// <summary>
    /// The levels <paramref name="type"/> nests, or <see cref="PinDepth"/> + 1 once it is known to nest deeper: no call goes
    /// more than that many levels down, and each type met is measured once.
    /// </summary>
    static int Nesting(ITypeSymbol type, Dictionary<ITypeSymbol, int> known, int level = 1)
    {
        if (known.TryGetValue(type, out var height)) return height;
        if (level > PinDepth) return PinDepth + 1;
        var deepest = 0;
        foreach (var inner in Inner(type))
        {
            deepest = Math.Max(deepest, Nesting(inner, known, level + 1));
            if (level + deepest > PinDepth) return PinDepth + 1;
        }
        return known[type] = 1 + deepest;
    }
    /// <summary>The types one level inside <paramref name="type"/>: an array's element, a tuple's elements, and the type arguments of it and the types it is nested in.</summary>
    static IEnumerable<ITypeSymbol> Inner(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array) { yield return array.ElementType; yield break; }
        if (type is INamedTypeSymbol { IsTupleType: true } tuple) { foreach (var element in tuple.TupleElements) yield return element.Type; yield break; }
        for (var named = type as INamedTypeSymbol; named != null; named = named.ContainingType)
            foreach (var argument in named.TypeArguments) yield return argument;
    }
    static IEnumerable<ITypeSymbol> BaseTypes(ITypeSymbol type) { for (var b = type.BaseType; b != null; b = b.BaseType) yield return b; }

    /// <summary>
    /// The type whose members a Select property node reads from a value of <paramref name="type"/>: a class, record or
    /// struct declared in source (a node library or a Custom C# node) or in HassSharp itself, also when it may be empty
    /// (<c>Point?</c> reads Point's members); null for every other type (built-in scalars, enums, interfaces, JSON values
    /// and the base library's types).
    /// </summary>
    static INamedTypeSymbol? PropertyOwner(ITypeSymbol type, IAssemblySymbol hassSharp)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable) type = nullable.TypeArguments[0];
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct, IsStatic: false } named) return null;
        var origin = named.OriginalDefinition;
        return origin.Locations.Any(l => l.IsInSource) || SymbolEqualityComparer.Default.Equals(origin.ContainingAssembly, hassSharp) ? named : null;
    }
    static bool Generic(INamedTypeSymbol type) => type.IsGenericType || type.ContainingType is { } outer && Generic(outer);

    /// <summary>
    /// The public, readable instance properties and public instance fields of the type, inherited ones included: the base
    /// type's first, each type's in declaration order, a member declared again (<c>new</c>, <c>override</c>) where the
    /// base had it. No indexers, static or compiler-made members, and none whose type a pin could not have.
    /// </summary>
    static ImmutableArray<(string Name, ITypeSymbol Type, ISymbol Member)> Members(INamedTypeSymbol type, CSharpCompilation compilation)
    {
        var chain = new List<INamedTypeSymbol>();
        for (var current = type; current != null && current.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType); current = current.BaseType) chain.Add(current);
        chain.Reverse();
        var members = new List<(string Name, ITypeSymbol Type, ISymbol Member)>();
        foreach (var owner in chain)
            foreach (var member in owner.GetMembers())
            {
                ITypeSymbol? memberType = member switch
                {
                    IPropertySymbol { IsStatic: false, IsIndexer: false, DeclaredAccessibility: Accessibility.Public, GetMethod.DeclaredAccessibility: Accessibility.Public, RefKind: RefKind.None } property => property.Type,
                    IFieldSymbol { IsStatic: false, IsConst: false, DeclaredAccessibility: Accessibility.Public, IsFixedSizeBuffer: false } field => field.Type,
                    _ => null,
                };
                if (memberType == null || member.Name == "EqualityContract" && member.IsImplicitlyDeclared || Unsupported(memberType) || TooDeep(memberType) || !compilation.IsSymbolAccessibleWithin(memberType, compilation.Assembly)) continue;
                var index = members.FindIndex(m => m.Name == member.Name);
                if (index >= 0) members[index] = (member.Name, memberType, member); else members.Add((member.Name, memberType, member));
            }
        return [.. members];
    }

    /// <summary>
    /// The <c>properties</c> of a type entry: each member's C# name, type id and whether it may be empty, with what its
    /// documentation says (a record's positional property is described by the record's <c>param</c>).
    /// </summary>
    static JsonArray PropertyList(ImmutableArray<(string Name, ITypeSymbol Type, ISymbol Member)> members, CancellationToken cancellationToken) => new(members.Select(m =>
    {
        var entry = new JsonObject { ["name"] = m.Name, ["type"] = TypeIdentity(m.Type), ["nullable"] = Nullable(m.Type) };
        if (PropertyDescription(m.Member, cancellationToken) is { Length: > 0 } description) entry["description"] = description;
        return (JsonNode?)entry;
    }).ToArray());
    static string PropertyDescription(ISymbol member, CancellationToken cancellationToken)
    {
        static XElement? Parse(string? xml) { try { return string.IsNullOrWhiteSpace(xml) ? null : XElement.Parse(xml); } catch (System.Xml.XmlException) { return null; } }
        if (DocText(Parse(member.GetDocumentationCommentXml(cancellationToken: cancellationToken))?.Element("summary")) is { Length: > 0 } own) return own;
        return DocText(Parse(member.ContainingType.GetDocumentationCommentXml(cancellationToken: cancellationToken))?.Elements("param").FirstOrDefault(e => (string?)e.Attribute("name") == member.Name));
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
