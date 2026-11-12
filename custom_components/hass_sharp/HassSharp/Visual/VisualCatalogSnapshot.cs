using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace HassSharp;

public sealed record NodeLibrarySource(string Id, string Source, long Revision);
public sealed record NodeLibraryDiagnostic(string SourceId, int StartLine, int StartColumn, int EndLine, int EndColumn, string Message, int Severity);
public sealed record NodeLibraryPreview(bool Valid, ImmutableArray<NodeLibraryDiagnostic> Diagnostics, VisualCatalogSnapshot Snapshot);
public sealed record NodeParameterDescriptor(string Name, string TypeId, string TypeExpression, bool IsCancellationToken, bool HasDefaultValue, object? DefaultValue, IParameterSymbol Symbol);
public sealed record NodeMethodDescriptor(string Kind, string SourceId, string InvocationType, string MethodName, string? ResultType,
    bool AwaitResult, ImmutableArray<NodeParameterDescriptor> Parameters, IMethodSymbol Method);
public sealed record NodeTypeDescriptor(string Id, string TypeExpression, ITypeSymbol Symbol);

/// <summary>A coherent catalog/source generation. Mutable JSON is returned only as a defensive copy.</summary>
public sealed class VisualCatalogSnapshot
{
    readonly JsonObject catalog;
    internal JsonObject Root => catalog;
    internal CSharpCompilation? Compilation { get; }
    public JsonObject Catalog => (JsonObject)catalog.DeepClone();
    public ImmutableArray<NodeLibrarySource> Sources { get; }
    public ImmutableDictionary<string, NodeMethodDescriptor> Methods { get; }
    public ImmutableDictionary<string, NodeTypeDescriptor> Types { get; }
    public static VisualCatalogSnapshot BuiltIn { get; } = new(VisualNodeCatalog.Root, [], [], [], null);

    internal VisualCatalogSnapshot(JsonObject catalog, IEnumerable<NodeLibrarySource> sources,
        IEnumerable<NodeMethodDescriptor> methods, IEnumerable<NodeTypeDescriptor> types, CSharpCompilation? compilation)
    {
        this.catalog = (JsonObject)catalog.DeepClone();
        Sources = sources.ToImmutableArray();
        Methods = methods.ToImmutableDictionary(m => m.Kind, StringComparer.Ordinal);
        Types = types.ToImmutableDictionary(t => t.Id, StringComparer.Ordinal);
        Compilation = compilation;
    }

    public bool IsAssignable(string source, string target)
    {
        if (catalog["types"] is not JsonObject known || !known.ContainsKey(source) || !known.ContainsKey(target)) return false;
        // entity_id is a refinement: ordinary strings never implicitly acquire it.
        if (target == "entity_id") return source == target;
        if (Compilation != null && Types.TryGetValue(source, out var from) && Types.TryGetValue(target, out var to))
            return StandardConversion(Compilation, from.Symbol, to.Symbol);
        return source == target || (known[source]?["assignableTo"] as JsonArray ?? []).Any(t => t?.GetValue<string>() == target);
    }

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
