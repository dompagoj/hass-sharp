using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace HassSharp;

public sealed record NodeLibrarySource(string Id, string Source, long Revision);
public sealed record NodeLibraryDiagnostic(string SourceId, int StartLine, int StartColumn, int EndLine, int EndColumn, string Message, int Severity);
public sealed record NodeLibraryPreview(bool Valid, ImmutableArray<NodeLibraryDiagnostic> Diagnostics, VisualCatalogSnapshot Snapshot);
/// <param name="IsContext">A <see cref="NodeContext"/> parameter; like a <see cref="CancellationToken"/> one it is injected, never a pin.</param>
public sealed record NodeParameterDescriptor(string Name, string TypeId, string TypeExpression, bool IsCancellationToken, bool HasDefaultValue, object? DefaultValue, IParameterSymbol Symbol, bool IsContext = false)
{
    public bool Injected => IsCancellationToken || IsContext;
}
/// <summary>One output pin of a node method: the whole result (<paramref name="Element"/> null) or one named tuple element.</summary>
public sealed record NodeOutputDescriptor(string Port, string? Element);
public sealed record NodeMethodDescriptor(string Kind, string SourceId, string InvocationType, string MethodName, string? ResultType,
    bool AwaitResult, ImmutableArray<NodeParameterDescriptor> Parameters, IMethodSymbol Method)
{
    public ImmutableArray<NodeOutputDescriptor> Outputs { get; init; } = ResultType == null ? [] : [new("result", null)];
    public bool Pure { get; init; }
}
public sealed record NodeTypeDescriptor(string Id, string TypeExpression, ITypeSymbol Symbol);
/// <summary>The code of one Custom C# node, compiled together with the libraries in a namespace of its own.</summary>
public sealed record CustomNodeSource(string NodeId, string Source);
/// <summary>
/// What the compiler made of one Custom C# node: its method and the signature it gives (null when the code does
/// not compile or does not have exactly one <c>[Node]</c> method), the types only it uses, and diagnostics whose
/// lines and columns are in the node's own code.
/// </summary>
public sealed record CustomNodeDescriptor(string NodeId, NodeMethodDescriptor? Method, JsonObject? Signature, ImmutableArray<NodeTypeDescriptor> Types, ImmutableArray<NodeLibraryDiagnostic> Diagnostics)
{
    public bool Valid => Signature != null && !Diagnostics.Any(d => d.Severity == 3);
}

/// <summary>A coherent catalog/source generation. Mutable JSON is returned only as a defensive copy.</summary>
public sealed class VisualCatalogSnapshot
{
    readonly JsonObject catalog;
    internal JsonObject Root => catalog;
    internal CSharpCompilation? Compilation { get; }
    internal ImmutableArray<NodeLibraryDiagnostic> LibraryDiagnostics { get; }
    public JsonObject Catalog => (JsonObject)catalog.DeepClone();
    public ImmutableArray<NodeLibrarySource> Sources { get; }
    public ImmutableDictionary<string, NodeMethodDescriptor> Methods { get; }
    public ImmutableDictionary<string, NodeTypeDescriptor> Types { get; }
    /// <summary>The Custom C# nodes compiled with this generation, by node id; empty for a library-only preview.</summary>
    public ImmutableDictionary<string, CustomNodeDescriptor> Custom { get; private init; } = ImmutableDictionary<string, CustomNodeDescriptor>.Empty;
    /// <summary>Home Assistant service schemas for validation only; never part of <see cref="Root"/> (D2).</summary>
    public ServiceSchemaCatalog? Services { get; private init; }
    public static VisualCatalogSnapshot BuiltIn { get; } = new(VisualNodeCatalog.Root, [], [], [], null);

    internal VisualCatalogSnapshot(JsonObject catalog, IEnumerable<NodeLibrarySource> sources,
        IEnumerable<NodeMethodDescriptor> methods, IEnumerable<NodeTypeDescriptor> types, CSharpCompilation? compilation, IEnumerable<NodeLibraryDiagnostic>? libraryDiagnostics = null,
        IEnumerable<CustomNodeDescriptor>? custom = null)
    {
        Custom = (custom ?? []).ToImmutableDictionary(c => c.NodeId, StringComparer.Ordinal);
        this.catalog = (JsonObject)catalog.DeepClone();
        Sources = sources.ToImmutableArray();
        Methods = methods.ToImmutableDictionary(m => m.Kind, StringComparer.Ordinal);
        Types = types.ToImmutableDictionary(t => t.Id, StringComparer.Ordinal);
        Compilation = compilation;
        LibraryDiagnostics = libraryDiagnostics?.ToImmutableArray() ?? [];
    }

    // Shares the immutable catalog and compilation; only the attached services differ.
    VisualCatalogSnapshot(VisualCatalogSnapshot other)
    {
        catalog = other.catalog; Sources = other.Sources; Methods = other.Methods; Types = other.Types;
        Compilation = other.Compilation; LibraryDiagnostics = other.LibraryDiagnostics; Custom = other.Custom; Services = other.Services;
    }
    VisualCatalogSnapshot(VisualCatalogSnapshot other, JsonObject root) : this(other) { catalog = root; }

    public VisualCatalogSnapshot WithServices(ServiceSchemaCatalog? services) => new(this) { Services = services };
    /// <summary>
    /// The same generation over another catalog root, such as one with a graph's Custom C# types added. Types not in
    /// <see cref="Types"/> are compared by their JSON entries only, exactly as <c>contract.ts</c> compares them.
    /// </summary>
    internal VisualCatalogSnapshot WithRoot(JsonObject root) => new(this, root);

    // Refinement is decided first; Roslyn only ever compares two root types.
    public bool IsAssignable(string source, string target) => VisualTypes.IsAssignable(catalog, source, target, (root, to) =>
        Compilation != null && Types.TryGetValue(root, out var from) && Types.TryGetValue(to, out var symbol) ? StandardConversion(Compilation, from.Symbol, symbol.Symbol) : null);

    internal static bool StandardConversion(CSharpCompilation compilation, ITypeSymbol source, ITypeSymbol target)
    {
        var conversion = compilation.ClassifyConversion(source, target);
        return conversion.IsImplicit && !conversion.IsUserDefined && NullabilitySafe(source, target);
    }

    // Roslyn treats nullable reference mismatches as warnings. Graph ports treat them as errors,
    // including generic variance and inherited interface arguments.
    static bool NullabilitySafe(ITypeSymbol source, ITypeSymbol target)
    {
        if ((source.NullableAnnotation == NullableAnnotation.Annotated || source is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T }) && target.IsReferenceType && target.NullableAnnotation != NullableAnnotation.Annotated) return false;
        if (source is IArrayTypeSymbol sa && target is IArrayTypeSymbol ta) return NullabilitySafe(sa.ElementType, ta.ElementType);
        if (target is INamedTypeSymbol tn)
        {
            // Arrays also implement constructed generic interfaces. Their element annotations
            // must survive conversions to IEnumerable<T>, IReadOnlyList<T> and IList<T>.
            var candidates = source is INamedTypeSymbol sn
                ? new[] { sn }.Concat(sn.AllInterfaces).Concat(BaseTypes(sn))
                : source.AllInterfaces;
            var matching = candidates.FirstOrDefault(t => SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, tn.OriginalDefinition));
            if (matching != null)
                for (var i = 0; i < matching.TypeArguments.Length; i++)
                {
                    var from = matching.TypeArguments[i]; var to = tn.TypeArguments[i];
                    var variance = tn.OriginalDefinition.TypeParameters[i].Variance;
                    if (variance != VarianceKind.In && !NullabilitySafe(from, to)) return false;
                    if (variance != VarianceKind.Out && !NullabilitySafe(to, from)) return false;
                }
        }
        return true;
    }
    static IEnumerable<INamedTypeSymbol> BaseTypes(INamedTypeSymbol symbol)
    {
        for (var type = symbol.BaseType; type != null; type = type.BaseType) yield return type;
    }
}
