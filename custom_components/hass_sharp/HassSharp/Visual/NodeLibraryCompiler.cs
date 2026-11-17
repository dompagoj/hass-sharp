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
    static bool InSystem(INamedTypeSymbol type) => type.ContainingNamespace is { Name: "System", ContainingNamespace.IsGlobalNamespace: true };
    static bool IsTask(ITypeSymbol type) => type is INamedTypeSymbol { Name: "Task", Arity: 0 } task && task.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks";
    /// <summary>
    /// A flow's shape: <c>Func&lt;Task&gt;</c> or <c>Func&lt;T, Task&gt;</c>, which the method awaits; <c>T</c> is what a
    /// call carries. Null for any other type.
    /// </summary>
    static (bool Flow, ITypeSymbol? Value)? FlowShape(ITypeSymbol type) => type is INamedTypeSymbol named && InSystem(named) ? named switch
    {
        { Name: "Func", Arity: 1 } when IsTask(named.TypeArguments[0]) => (true, null),
        { Name: "Func", Arity: 2 } when IsTask(named.TypeArguments[1]) => (true, named.TypeArguments[0]),
        _ => null
    } : null;
    /// <summary>A Func giving a Task: what a flow parameter looks like, even unmarked.</summary>
    static bool DelegateLike(ITypeSymbol type) => type is INamedTypeSymbol named && InSystem(named) && named.Name == "Func" && named.Arity > 0 && IsTask(named.TypeArguments[^1]);
    static bool IsAction(ITypeSymbol type) => type is INamedTypeSymbol { Name: "Action" } named && InSystem(named);
    static string LowerFirst(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
    /// <summary>Whether a parameter or returned value's name can be a pin's id, as both validators read pin ids.</summary>
    static bool PinId(string id) => VisualTypes.Matches(id, "^[A-Za-z_][A-Za-z0-9_]*$");
    /// <summary>What a pin's name may contain, said at the parameter or returned value whose name is not one.</summary>
    internal static string PinNameMessage(string what, string name) => $"Name this {what} with English letters, digits and _ only; {name} cannot name a pin";
    /// <summary>A warning at a Func&lt;…, Task&gt; parameter without FlowControl: it stays an input pin, which is rarely meant.</summary>
    internal const string FlowUnmarkedMessage = "This Func<…, Task> is an input pin; mark it [Port(\"Label\", FlowControl = true)] to make it a flow";
    internal const string FlowTypeMessage = "A flow (FlowControl = true) is a Func<Task> or Func<T, Task>, not nullable and without a default value";
    /// <summary>At an Action marked as a flow: a flow is awaited, as an Action would block its thread while the flow's steps wait.</summary>
    internal const string FlowActionMessage = "A flow is a Func<Task> or Func<T, Task> that the method awaits, not an Action: an Action would hold a thread for as long as the flow's steps wait (a Delay, Wait until)";
    /// <summary>At a method with flows that does not return a Task: it could only block on its flows or leave them running.</summary>
    internal const string FlowAwaitMessage = "A node with flows awaits them: make it async, return Task or Task<T>, and await each flow";
    internal const string FlowTupleMessage = "Name each value the flow carries, such as (string Light, double Index)";
    internal const string FlowDuplicateMessage = "Give each flow and each value it carries its own name, apart from the values the method returns";
    internal const string FlowPureMessage = "A value node (Pure = true) has no flows; remove Pure or the FlowControl parameters";
    /// <summary>A flow named as a port every step or loop already has.</summary>
    internal static string FlowNameMessage(string id) => $"Name this flow something other than {id}: next, error and condition are taken";
    /// <summary>At an entity type that may be empty where a value goes out (a result, a returned tuple element, a flow's value): an entity pin is always set.</summary>
    internal const string EntityResultMessage = "A node returns an entity, or nothing at all: remove the ? from this entity type";
    /// <summary>At a pin whose type holds an entity type within it: a wire carries one entity id.</summary>
    internal const string EntityNestedMessage = "An entity cannot be inside a list or another type on a pin; take or return one entity";

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
        // The typed service calls and the entity classes as their built assemblies: nothing of them is bound again here.
        var compilation = CSharpCompilation.Create("HassSharpNodeLibrary", syntaxTrees, FrameworkReferences().AddRange((serviceCalls ?? diagnostics.ServiceCalls()).References),
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
        var entityBase = frameworkAssembly.GetTypeByMetadataName("HassSharp.Entities.Entity");
        var entityDomain = frameworkAssembly.GetTypeByMetadataName("HassSharp.Entities.EntityDomainAttribute");
        // An entity handle on a pin is the entity id it wraps: entity_id for Entity, entity_id:<domain> for a domain's
        // generated class (the type's own [EntityDomain]); null for any other type. A ? does not change the id.
        string? EntityPin(ITypeSymbol type)
        {
            if (SymbolEqualityComparer.Default.Equals(type, entityBase)) return "entity_id";
            return type is INamedTypeSymbol named && named.ContainingAssembly?.Name.StartsWith(GeneratedEntities.AssemblyPrefix, StringComparison.Ordinal) == true
                && named.GetAttributes().FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, entityDomain))?.ConstructorArguments.FirstOrDefault().Value is string domain
                ? "entity_id:" + domain : null;
        }
        // The catalog id of a pin of this type.
        string PinType(ITypeSymbol type) => EntityPin(type) ?? TypeIdentity(type);
        // Whether an entity handle is inside the type (an array's element, a type argument, a tuple's element); the type is no deeper than a pin may be.
        bool EntityInside(ITypeSymbol type) => Inner(type).Any(inner => EntityPin(inner) != null || EntityInside(inner));
        // What is wrong with an entity handle in the type of a value going out (a result, a returned tuple element, a flow's
        // value), if anything: it may not be empty, and none may be inside the type.
        string? EntityOutputProblem(ITypeSymbol type) =>
            EntityPin(type) != null && Nullable(type) ? EntityResultMessage : EntityInside(type) ? EntityNestedMessage : null;
        AttributeData? Port(ImmutableArray<AttributeData> attributes) => attributes.FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, portAttribute));
        string? PortLabel(ImmutableArray<AttributeData> attributes) => Port(attributes)?.ConstructorArguments.FirstOrDefault().Value as string;
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
            // An entity handle's pin is an entity id type, which the catalog has: the class is never a type of its own.
            if (EntityPin(type) != null) return;
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
                // A class deriving from an entity handle brings neither the handle nor what the handle implements.
                var handle = BaseTypes(declared).FirstOrDefault(b => EntityPin(b) != null);
                foreach (var contract in declared.AllInterfaces)
                    if (!Unsupported(contract) && !TooDeep(contract) && handle?.AllInterfaces.Contains(contract, SymbolEqualityComparer.Default) != true && compilation.IsSymbolAccessibleWithin(contract, compilation.Assembly)) into.TryAdd(TypeIdentity(contract), contract);
                for (var baseType = declared.BaseType; baseType != null; baseType = baseType.BaseType)
                    if (!Unsupported(baseType) && !TooDeep(baseType) && EntityPin(baseType) == null && compilation.IsSymbolAccessibleWithin(baseType, compilation.Assembly)) into.TryAdd(TypeIdentity(baseType), baseType);
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
        (string Title, string Description, string Category, bool Pure, JsonObject Inputs, JsonObject Outputs, NodeMethodDescriptor Method, JsonObject? Flows)? Describe(
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
                if (elements.FirstOrDefault(e => EntityOutputProblem(e.Type) != null) is { } entity) { Report(entity.Locations.FirstOrDefault() ?? declaration.ReturnType.GetLocation(), EntityOutputProblem(entity.Type)!); return null; }
                for (var i = 0; i < elements.Length; i++)
                {
                    register(elements[i].Type);
                    outputs[ids[i]] = new JsonObject { ["type"] = PinType(elements[i].Type), ["label"] = InWords(elements[i].Name), ["description"] = i == 0 ? DocText(doc?.Element("returns")) : "" };
                }
                outputPorts = elements.Select((e, i) => new NodeOutputDescriptor(ids[i], Escape(e.Name))).ToImmutableArray();
            }
            else if (result != null)
            {
                if (TooDeep(result)) { Report(declaration.ReturnType.GetLocation(), TooDeepMessage); return null; }
                if (EntityOutputProblem(result) is { } entity) { Report(declaration.ReturnType.GetLocation(), entity); return null; }
                register(result);
                outputs["result"] = new JsonObject { ["type"] = PinType(result), ["label"] = PortLabel(method.GetReturnTypeAttributes()) ?? "Result", ["description"] = DocText(doc?.Element("returns")) };
                outputPorts = [new("result", null)];
            }
            // Flows: each [Port(..., FlowControl = true)] parameter is an execution port and a scope, its values outputs read there.
            var flowOutputs = new JsonObject(); var flowPorts = new List<string>(); var flowLabels = new JsonObject();
            var flows = new Dictionary<IParameterSymbol, NodeFlowDescriptor>(SymbolEqualityComparer.Default);
            foreach (var p in method.Parameters)
            {
                var location = p.Locations.FirstOrDefault() ?? declaration.Identifier.GetLocation();
                var marked = Port(p.GetAttributes())?.NamedArguments.Any(a => a.Key == "FlowControl" && a.Value.Value is true) == true;
                if (!marked) { if (DelegateLike(p.Type)) Report(location, FlowUnmarkedMessage, 2); continue; }
                if (pure) { Report(location, FlowPureMessage); return null; }
                if (IsAction(p.Type)) { Report(location, FlowActionMessage); return null; }
                if (FlowShape(p.Type) is not { } shape || p.Type.NullableAnnotation == NullableAnnotation.Annotated || p.HasExplicitDefaultValue) { Report(location, FlowTypeMessage); return null; }
                if (!PinId(p.Name)) { Report(location, PinNameMessage("flow", p.Name)); return null; }
                if (p.Name is "next" or "error" or "condition") { Report(location, FlowNameMessage(p.Name)); return null; }
                var label = PortLabel(p.GetAttributes()) ?? InWords(p.Name);
                var description = DocText(doc?.Elements("param").FirstOrDefault(e => (string?)e.Attribute("name") == p.Name));
                var values = ImmutableArray<NodeFlowValue>.Empty;
                if (shape.Value is INamedTypeSymbol { IsTupleType: true } carried)
                {
                    var elements = carried.TupleElements;
                    if (elements.Any(e => !e.IsExplicitlyNamedTupleElement)) { Report(location, FlowTupleMessage); return null; }
                    if (elements.FirstOrDefault(e => !PinId(LowerFirst(e.Name))) is { } unnamed) { Report(location, PinNameMessage("value", unnamed.Name)); return null; }
                    if (elements.Any(e => TooDeep(e.Type))) { Report(location, TooDeepMessage); return null; }
                    if (elements.Select(e => EntityOutputProblem(e.Type)).FirstOrDefault(m => m != null) is { } entity) { Report(location, entity); return null; }
                    values = elements.Select((e, i) => new NodeFlowValue(LowerFirst(e.Name), i)).ToImmutableArray();
                    for (var i = 0; i < elements.Length; i++)
                    {
                        if (flowOutputs.ContainsKey(values[i].Port)) { Report(location, FlowDuplicateMessage); return null; }
                        register(elements[i].Type);
                        flowOutputs[values[i].Port] = new JsonObject { ["type"] = PinType(elements[i].Type), ["label"] = InWords(elements[i].Name), ["description"] = i == 0 ? description : "", ["scope"] = p.Name };
                    }
                }
                else if (shape.Value != null)
                {
                    if (TooDeep(shape.Value)) { Report(location, TooDeepMessage); return null; }
                    if (EntityOutputProblem(shape.Value) is { } entity) { Report(location, entity); return null; }
                    if (flowOutputs.ContainsKey(p.Name)) { Report(location, FlowDuplicateMessage); return null; }
                    register(shape.Value);
                    values = [new(p.Name, null)];
                    flowOutputs[p.Name] = new JsonObject { ["type"] = PinType(shape.Value), ["label"] = label, ["description"] = description, ["scope"] = p.Name };
                }
                if (flowPorts.Contains(p.Name) || outputs.Any(o => flowOutputs.ContainsKey(o.Key))) { Report(location, FlowDuplicateMessage); return null; }
                flowPorts.Add(p.Name); flowLabels[p.Name] = label;
                flows[p] = new(p.Name, values);
            }
            // A method that cannot await its flows could only block on them or leave them running.
            if (flows.Count > 0 && !awaitResult) { Report(declaration.Identifier.GetLocation(), FlowAwaitMessage); return null; }
            JsonObject? flowSpec = null;
            if (flowPorts.Count > 0)
            {
                flowLabels["next"] = "Done";
                flowSpec = new JsonObject { ["execution"] = new JsonArray([.. flowPorts.Append("next").Append("error").Select(f => (JsonNode)f)]),
                    ["scopePorts"] = new JsonArray([.. flowPorts.Select(f => (JsonNode)f)]), ["executionLabels"] = flowLabels };
                // A flow's values come first, beside its pin; the values the method returns after them.
                foreach (var (key, value) in outputs.ToArray()) { outputs.Remove(key); flowOutputs[key] = value; }
                outputs = flowOutputs;
            }
            var parameters = method.Parameters.Select(p => new NodeParameterDescriptor(p.Name, PinType(p.Type), TypeIdentity(p.Type),
                p.Type.ToDisplayString() == "System.Threading.CancellationToken", p.HasExplicitDefaultValue, p.HasExplicitDefaultValue ? p.ExplicitDefaultValue : null, p,
                SymbolEqualityComparer.Default.Equals(p.Type, nodeContext), flows.GetValueOrDefault(p))).ToImmutableArray();
            // A pin is named by its parameter: C# takes letters of any language, a pin id only English ones.
            if (parameters.FirstOrDefault(p => !p.Injected && !PinId(p.Name)) is { } foreign) { Report(foreign.Symbol.Locations.FirstOrDefault() ?? declaration.Identifier.GetLocation(), PinNameMessage("input", foreign.Name)); return null; }
            if (parameters.FirstOrDefault(p => !p.Injected && TooDeep(p.Symbol.Type)) is { } nested) { Report(nested.Symbol.Locations.FirstOrDefault() ?? declaration.Identifier.GetLocation(), TooDeepMessage); return null; }
            if (parameters.FirstOrDefault(p => !p.Injected && EntityInside(p.Symbol.Type)) is { } holds) { Report(holds.Symbol.Locations.FirstOrDefault() ?? declaration.Identifier.GetLocation(), EntityNestedMessage); return null; }
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
                // An entity pin left unset is passed nothing (or its C# default): no null entity id is typed in.
                else if (Nullable(p.Type) && EntityPin(p.Type) == null && (!p.HasExplicitDefaultValue || p.ExplicitDefaultValue == null)) rule["default"] = new JsonObject { ["kind"] = "literal", ["type"] = parameter.TypeId, ["value"] = null };
                input[p.Name] = rule;
            }
            var descriptor = new NodeMethodDescriptor(kind, sourceId, TypeIdentity(method.ContainingType), Escape(method.Name), result == null ? null : PinType(result), awaitResult, parameters, method)
                { Outputs = outputPorts, Pure = pure };
            return (title, DocText(doc?.Element("summary")), attribute.NamedArguments.FirstOrDefault(a => a.Key == "Category").Value.Value as string ?? "Custom", pure, input, outputs, descriptor, flowSpec);
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
                if (described.Pure) spec["pure"] = true;
                else if (described.Flows is { } flowSpec) foreach (var (key, value) in flowSpec) spec[key] = value!.DeepClone();
                else spec["execution"] = new JsonArray("next", "error");
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
        // What Select property reads from an entity id: the members of Entity for entity_id, those of a domain's
        // generated class for entity_id:<domain>. Their types join the catalog (the classes never do); a type that joins
        // only for them, as each enum of the classes does, is left out of the editor's type lists.
        var handles = new List<(string Pin, INamedTypeSymbol Type, ImmutableArray<(string Name, ITypeSymbol Type, ISymbol Member)> Members)>();
        if (entityBase != null) handles.Add(("entity_id", entityBase, Members(entityBase, compilation)));
        if (compilation.SourceModule.ReferencedAssemblySymbols.FirstOrDefault(a => a.Identity.Name.StartsWith(GeneratedEntities.AssemblyPrefix, StringComparison.Ordinal)) is { } entities)
            foreach (var (pin, handle) in TypesIn(entities.GlobalNamespace).Select(t => (EntityPin(t), t)).Where(t => t.Item1 is not (null or "entity_id")).OrderBy(t => t.Item1, StringComparer.Ordinal))
                handles.Add((pin!, handle, Members(handle, compilation)));
        var listed = typeSymbols.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var (_, _, members) in handles) foreach (var member in members) Register(typeSymbols, member.Type);
        var types = root["types"]!.AsObject();
        // Only roots are CLR conversion targets; refinements are decided by the chain before Roslyn. Home Assistant has
        // many entity enums, and comparing each with every type would double the time a preview takes; but an enum
        // converts implicitly only to itself, its nullable form, System.Enum with what it derives from and implements, and
        // dynamic, and no other type converts to one but dynamic. Compared with those alone, it is listed as comparing
        // with every type would list it.
        var roots = typeSymbols.Where(pair => !refined(pair.Key)).ToArray();
        var others = roots.Where(pair => !TextEnum(Scalar(pair.Value))).ToArray();
        var enumType = compilation.GetSpecialType(SpecialType.System_Enum);
        var enumSupertypes = new HashSet<ITypeSymbol>(enumType.AllInterfaces.Cast<ITypeSymbol>().Concat(BaseTypes(enumType)).Append(enumType), SymbolEqualityComparer.Default);
        var aboveEnums = roots.Where(pair => pair.Value.TypeKind == TypeKind.Dynamic || enumSupertypes.Contains(pair.Value)).ToArray();
        var forms = roots.Where(pair => TextEnum(Scalar(pair.Value))).ToLookup(pair => Scalar(pair.Value), SymbolEqualityComparer.Default);
        IEnumerable<KeyValuePair<string, ITypeSymbol>> Targets(ITypeSymbol symbol) =>
            symbol.TypeKind == TypeKind.Dynamic ? roots : TextEnum(Scalar(symbol)) ? forms[Scalar(symbol)].Concat(aboveEnums) : others;
        foreach (var (id, symbol) in typeSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = types[id] as JsonObject ?? new JsonObject();
            if (!types.ContainsKey(id)) types[id] = type;
            DescribeType(type, symbol, Targets(symbol), compilation);
            if (!listed.Contains(id)) type["listed"] = false;
            if (properties.TryGetValue(id, out var members) && !members.IsEmpty) type["properties"] = PropertyList(members, PinType, cancellationToken);
        }
        var domains = new JsonObject();
        foreach (var (pin, handle, members) in handles)
        {
            if (pin == "entity_id") { if (types["entity_id"] is JsonObject entity) entity["properties"] = PropertyList(members, PinType, cancellationToken); }
            else domains[pin["entity_id:".Length..]] = new JsonObject { ["clrType"] = handle.ToDisplayString(), ["properties"] = PropertyList(members, PinType, cancellationToken) };
        }
        if (domains.Count > 0 && root["parametricTypes"]?["entity_id"] is JsonObject family) family["members"] = domains;
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
                        if (ownProperties.TryGetValue(id, out var members) && !members.IsEmpty) entry["properties"] = PropertyList(members, PinType, cancellationToken);
                        entries[id] = entry;
                    }
                    foreach (var (_, input) in described.Inputs)
                        if (input is JsonObject rule && rule["default"] is JsonObject operand && VisualNodeCatalog.Text(operand, "type") is var typeId &&
                            !VisualNodeCatalog.ValidLiteral(typeId, operand["value"], operand.ContainsKey("value"), types.ContainsKey(typeId) ? root : new JsonObject { ["types"] = new JsonObject { [typeId] = entries[typeId]?.DeepClone() } }))
                            rule.Remove("default");
                    signature = new JsonObject { ["title"] = described.Title, ["description"] = described.Description, ["pure"] = described.Pure,
                        ["inputs"] = described.Inputs, ["outputs"] = described.Outputs, ["types"] = entries };
                    if (described.Flows is { } flowSpec) foreach (var (key, value) in flowSpec) signature[key] = value!.DeepClone();
                    descriptor = described.Method;
                }
            }
            customs.Add(new(custom.NodeId, signature == null ? null : descriptor, signature, own.Select(p => new NodeTypeDescriptor(p.Key, TypeIdentity(p.Value), p.Value)).ToImmutableArray(), customErrors[custom.NodeId].ToImmutableArray()));
        }
        var entityTypes = EntityTypes(compilation, frameworkAssembly, trees, (location, message) => Report(location, message), cancellationToken);
        var snapshot = new VisualCatalogSnapshot(root, sources, methods, typeSymbols.Select(p => new NodeTypeDescriptor(p.Key, TypeIdentity(p.Value), p.Value)), compilation, errors, customs, entityTypes);
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
    /// <summary>The top-level types of a namespace and of each namespace inside it.</summary>
    static IEnumerable<INamedTypeSymbol> TypesIn(INamespaceSymbol space) => space.GetTypeMembers().Concat(space.GetNamespaceMembers().SelectMany(TypesIn));
    /// <summary>Whether the type is an enum of the entity classes, whose members stand for Home Assistant's texts (<c>EntityText</c>).</summary>
    static bool TextEnum(ITypeSymbol type) => type.TypeKind == TypeKind.Enum && type.ContainingAssembly?.Name.StartsWith(GeneratedEntities.AssemblyPrefix, StringComparison.Ordinal) == true;

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
    /// The <c>properties</c> of a type entry: each member's C# name, type id (<paramref name="typeId"/>: a pin's, so an
    /// entity handle is its entity id type) and whether it may be empty, with what its documentation says (a record's
    /// positional property is described by the record's <c>param</c>).
    /// </summary>
    static JsonArray PropertyList(ImmutableArray<(string Name, ITypeSymbol Type, ISymbol Member)> members, Func<ITypeSymbol, string> typeId, CancellationToken cancellationToken) => new(members.Select(m =>
    {
        var entry = new JsonObject { ["name"] = m.Name, ["type"] = typeId(m.Type), ["nullable"] = Nullable(m.Type) };
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
        var scalar = Scalar(symbol);
        if (scalar.SpecialType == SpecialType.System_Char) type["character"] = true;
        var bounds = NumericBounds(scalar.SpecialType);
        if (bounds is { } range) { type["min"] = range.Min; type["max"] = range.Max; }
        if (TextEnum(scalar))
        {
            // An entity enum's values are Home Assistant's texts, as saved graphs and traces hold them, with its labels; the
            // catch-all has no text and is no value to type in. Such a type is left out of the editor's type lists.
            var texts = ((INamedTypeSymbol)scalar).GetMembers().OfType<IFieldSymbol>()
                .Select(f => (Field: f, Text: f.GetAttributes().FirstOrDefault(a => a.AttributeClass is { Name: "EntityTextAttribute" } c && c.ContainingNamespace.ToDisplayString() == "HassSharp.Entities")))
                .Where(t => t.Text?.ConstructorArguments.FirstOrDefault().Value is string).ToArray();
            type["enumValues"] = new JsonArray(texts.Select(t => (JsonNode?)JsonValue.Create((string)t.Text!.ConstructorArguments[0].Value!)).ToArray());
            type["enumLabels"] = new JsonArray(texts.Select(t => (JsonNode?)JsonValue.Create(t.Text!.NamedArguments.FirstOrDefault(a => a.Key == "Label").Value.Value as string ?? InWords(t.Field.Name))).ToArray());
            type["listed"] = false;
        }
        else if (scalar.TypeKind == TypeKind.Enum)
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

    /// <summary>The type a <c>Nullable&lt;T&gt;</c> holds; any other type itself.</summary>
    static ITypeSymbol Scalar(ITypeSymbol type) => type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable ? nullable.TypeArguments[0] : type;
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
    static XElement? Documentation(ISymbol symbol, CancellationToken cancellationToken)
    {
        try { return XElement.Parse(symbol.GetDocumentationCommentXml(cancellationToken: cancellationToken) ?? "<member/>"); } catch (System.Xml.XmlException) { return null; }
    }

    /// <summary>At a <c>[Setting]</c> property of a type a setting cannot have.</summary>
    internal static string SettingTypeMessage(string type) => $"A setting is a string, bool, int, long, double or enum; {type} cannot be one";
    /// <summary>At a <c>[Setting]</c> property the settings form could not set.</summary>
    internal const string SettingSetterMessage = "A setting needs a public setter";

    /// <summary>
    /// The custom device and entity classes the libraries declare: each public, concrete, non-generic top-level class of a
    /// library source that derives <see cref="SharpDevice"/>, or a typed entity base (the domain), and has a public
    /// parameterless constructor. The <c>[Setting]</c> properties of every library class deriving
    /// <see cref="SharpComponent"/> are checked once, listed or not: one a setting cannot be is reported and left out.
    /// </summary>
    static List<EntityTypeDescriptor> EntityTypes(CSharpCompilation compilation, IAssemblySymbol hassSharp, IReadOnlyDictionary<SyntaxTree, NodeLibrarySource> trees,
        Action<Location, string> report, CancellationToken cancellationToken)
    {
        var component = hassSharp.GetTypeByMetadataName("HassSharp.SharpComponent");
        var device = hassSharp.GetTypeByMetadataName("HassSharp.SharpDevice");
        var entity = hassSharp.GetTypeByMetadataName("HassSharp.SharpEntity");
        var setting = hassSharp.GetTypeByMetadataName("HassSharp.SettingAttribute");
        bool Derives(ITypeSymbol type, INamedTypeSymbol? from) => BaseTypes(type).Any(b => SymbolEqualityComparer.Default.Equals(b, from));
        NodeLibrarySource? Source(INamedTypeSymbol type) => type.DeclaringSyntaxReferences.Select(r => trees.GetValueOrDefault(r.SyntaxTree)).FirstOrDefault(s => s != null);
        static IEnumerable<INamedTypeSymbol> All(INamespaceOrTypeSymbol space) => space.GetTypeMembers().SelectMany(t => All(t).Prepend(t))
            .Concat(space is INamespaceSymbol ns ? ns.GetNamespaceMembers().SelectMany(All) : Enumerable.Empty<INamedTypeSymbol>());
        var classes = All(compilation.Assembly.GlobalNamespace).Where(t => t.TypeKind == TypeKind.Class && Derives(t, component) && Source(t) != null).ToArray();
        // The settings each class declares itself, in declaration order.
        var own = new Dictionary<ITypeSymbol, List<EntitySettingDescriptor>>(SymbolEqualityComparer.Default);
        foreach (var type in classes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settings = own[type] = [];
            foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public || property.Type.TypeKind == TypeKind.Error) continue;
                if (property.GetAttributes().FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, setting)) is not { } attribute) continue;
                var location = property.Locations.FirstOrDefault() ?? Location.None;
                var settable = property.SetMethod is { DeclaredAccessibility: Accessibility.Public };
                var form = SettingType(property.Type);
                if (!settable) report(location, SettingSetterMessage);
                if (form == null) report(location, SettingTypeMessage(property.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                if (!settable || form is not { } shape) continue;
                var label = attribute.ConstructorArguments.FirstOrDefault().Value as string;
                var description = attribute.NamedArguments.FirstOrDefault(a => a.Key == "Description").Value.Value as string;
                if (string.IsNullOrWhiteSpace(description)) description = DocText(Documentation(property, cancellationToken)?.Element("summary"));
                settings.Add(new(property.Name, string.IsNullOrWhiteSpace(label) ? property.Name : label, string.IsNullOrWhiteSpace(description) ? null : description,
                    shape.Kind, attribute.NamedArguments.FirstOrDefault(a => a.Key == "Secret").Value.Value is true, shape.Options));
            }
        }
        var listed = new List<EntityTypeDescriptor>();
        foreach (var type in classes)
        {
            if (type.ContainingType != null || type.DeclaredAccessibility != Accessibility.Public || type.IsAbstract || type.IsGenericType
                || !type.InstanceConstructors.Any(c => c.Parameters.IsEmpty && c.DeclaredAccessibility == Accessibility.Public)) continue;
            // An entity's domain is its nearest typed base's; one deriving SharpEntity itself has none and cannot be created.
            var domain = BaseTypes(type).Where(b => SymbolEqualityComparer.Default.Equals(b.ContainingAssembly, hassSharp)).Select(b => SharpDomains.OfName(b.ToDisplayString())).FirstOrDefault(d => d != null);
            var kind = Derives(type, device) ? "device" : Derives(type, entity) && domain != null ? "entity" : null;
            if (kind == null) continue;
            // A base class's settings come first; one declared again in a derived class keeps its place.
            var settings = new List<EntitySettingDescriptor>();
            foreach (var owner in BaseTypes(type).Reverse().Append(type))
                foreach (var found in own.GetValueOrDefault(owner.OriginalDefinition) ?? [])
                {
                    var index = settings.FindIndex(s => s.Key == found.Key);
                    if (index >= 0) settings[index] = found; else settings.Add(found);
                }
            var summary = DocText(Documentation(type, cancellationToken)?.Element("summary"));
            listed.Add(new("library:" + Source(type)!.Id + ":" + type.ToDisplayString(), Source(type)!.Id, TypeIdentity(type), kind, kind == "device" ? null : domain,
                type.Name, summary.Length == 0 ? null : summary, [.. settings]));
        }
        return listed;
    }

    /// <summary>A setting's form type for a property of <paramref name="type"/>, with an enum's member names; null for a type a setting cannot have.</summary>
    static (string Kind, ImmutableArray<string> Options)? SettingType(ITypeSymbol type)
    {
        var scalar = Scalar(type);
        if (scalar.TypeKind == TypeKind.Enum) return ("enum", [.. scalar.GetMembers().OfType<IFieldSymbol>().Where(f => f.IsConst).Select(f => f.Name)]);
        string? kind = scalar.SpecialType switch
        {
            SpecialType.System_String => "string", SpecialType.System_Boolean => "boolean",
            SpecialType.System_Int32 or SpecialType.System_Int64 => "integer", SpecialType.System_Double => "number", _ => null,
        };
        return kind == null ? null : (kind, ImmutableArray<string>.Empty);
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
