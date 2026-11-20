using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace HassSharp;

/// <summary>What a definition previews to: whether it can be saved, its problems, the source it generates and its class.</summary>
/// <param name="Valid">True when no diagnostic is an error.</param>
/// <param name="Diagnostics">Its problems, each with a <c>definition.</c> code and, for a setting, the setting's key as its input; for an Entity node's option, that node and <c>option:&lt;option&gt;</c> as its input; for a graph request that lists entities, <c>entities</c> as its input.</param>
/// <param name="Source">The generated source; empty when the definition is not valid.</param>
/// <param name="Type">The class it creates; null when the type is not one the libraries declare, and for graph logic.</param>
public sealed record DefinitionPreview(bool Valid, List<VisualDiagnostic> Diagnostics, string Source, EntityTypeDescriptor? Type)
{
    /// <summary>
    /// The sections of <see cref="Source"/>: for a class, one <c>$library:&lt;id&gt;</c> range per library; for a graph, the
    /// generator's (its nodes' and libraries'). Empty when it is not valid.
    /// </summary>
    public List<VisualSourceMap> SourceMap { get; init; } = [];
    /// <summary>
    /// For a graph, the graph as previewed, with its definition (its settings, and whether it has a device) composed into
    /// it (<see cref="DefinitionGraph.Compose"/>); its entities are its Entity nodes. Null for a class, or a document with problems.
    /// </summary>
    [JsonIgnore] public JsonObject? Graph { get; init; }
    /// <summary>For a valid graph, the pruned catalog its source embeds (the run snapshot's catalog); null otherwise.</summary>
    [JsonIgnore] public JsonObject? EmbeddedCatalog { get; init; }
}

/// <summary>
/// Checks an entity definition and generates its source. For a class: against the classes the node libraries declare,
/// a <c>SharpDefinition</c> class that creates it, with the libraries after it, as an automation embeds them. For a
/// graph: its document (its settings, and the entities its Entity nodes declare) first, then its graph with that
/// definition composed into it, generated as an automation's graph is.
/// </summary>
/// <param name="diagnostics">What a graph's Custom C# nodes compile with; a shared one of its own when null.</param>
/// <param name="serviceCalls">The typed service calls one request read once, for a graph's compile; the current ones when null.</param>
public sealed class DefinitionGenerator(DiagnosticsProvider? diagnostics = null, GeneratedServiceCalls? serviceCalls = null)
{
    // Only a caller without one, such as a test, previews a graph with this.
    static readonly Lazy<DiagnosticsProvider> Fallback = new(() => new DiagnosticsProvider());

    /// <summary>
    /// Every problem of the definition, in a fixed order; a definition whose class is unknown, or whose libraries have
    /// errors, gets no setting problems. A setting it does not mention keeps the class default, as a null value does.
    /// </summary>
    public DefinitionPreview Preview(DefinitionBody body, VisualCatalogSnapshot snapshot)
    {
        var errors = new List<VisualDiagnostic>();
        void Error(string code, string message, string? input = null, string? port = null, string? node = null) => errors.Add(new(node, message, 3, InputId: input, PortId: port, Code: code));

        if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 120) Error("definition.name", "Give the definition a name of up to 120 characters");
        var kind = body.Logic?.Kind;
        if (kind is not ("class" or "graph")) Error("definition.logic", "Logic is a graph or a class");
        // For a class an empty array is the same as none: a client may always send the field. A graph's entities are its
        // Entity nodes: a request that lists them at all, even as an empty list, is told so, with nothing else of it
        // checked (its name aside), since what else is wrong is likely the list form's.
        if (kind == "class" && body.Entities is { Count: > 0 }) Error("definition.entitiesNotAllowed", "A class definition's entities come from its code");
        if (kind == "graph" && body.Entities != null)
        {
            Error("definition.entitiesNotAllowed", "Entities are Entity nodes in the graph now: leave entities out and add one Entity node for each", "entities");
            return new(false, errors, "", null);
        }
        if (kind == "graph") DefinitionDocument.Check(body, snapshot.Root, Error);

        EntityTypeDescriptor? type = null;
        if (snapshot.LibraryDiagnostics.Any(d => d.Severity == 3)) Error("definition.library", "The node libraries have errors; fix them first");
        else if (kind == "class")
        {
            var name = body.Logic!.Type;
            if (string.IsNullOrWhiteSpace(name)) Error("definition.unknownType", "No entity class was given: choose the class the definition creates");
            else if (!snapshot.EntityTypes.TryGetValue(name, out type)) Error("definition.unknownType", $"No entity class {name} in the node libraries");
            else Settings(type, body.Settings ?? [], Error);
        }

        // The document's problems come first: its graph is not checked against a definition that would only repeat them.
        // A problem on an Entity node is the graph's, reported with the rest of the graph's, and leaves it with no source.
        if (errors.Any(e => e.NodeId == null)) return new(false, errors, "", type);
        if (kind != "graph") return Generate(type!, snapshot);
        var preview = GenerateGraph(body, snapshot);
        return errors.Count == 0 ? preview : preview with { Valid = false, Diagnostics = [.. errors, .. preview.Diagnostics], Source = "", SourceMap = [], EmbeddedCatalog = null };
    }

    /// <summary>
    /// The graph with the document's definition composed into it, previewed as an automation's graph is: its problems
    /// and warnings each on its node, and when valid its source, source map and embedded catalog.
    /// </summary>
    DefinitionPreview GenerateGraph(DefinitionBody body, VisualCatalogSnapshot snapshot)
    {
        var graph = DefinitionGraph.Compose(body);
        var preview = new VisualGenerator(diagnostics ?? Fallback.Value, serviceCalls).Preview(graph, snapshot);
        return new(preview.Valid, preview.Diagnostics, preview.Valid ? preview.Source : "", null)
        {
            SourceMap = preview.Valid ? preview.SourceMap : [],
            Graph = preview.Graph ?? graph,
            EmbeddedCatalog = preview.Valid && preview.EmbeddedCatalog != null ? JsonNode.Parse(preview.EmbeddedCatalog)!.AsObject() : null,
        };
    }

    static void Settings(EntityTypeDescriptor type, List<DefinitionSetting> given, DefinitionProblem error)
    {
        var settings = given.Select(s => (Key: s?.Key ?? "", s?.Value)).ToArray();
        var known = type.Settings.ToDictionary(s => s.Key, StringComparer.Ordinal);
        foreach (var key in settings.Select(s => s.Key).Distinct(StringComparer.Ordinal).Where(k => !known.ContainsKey(k)))
            error("definition.unknownSetting", $"{type.Title} has no setting {key}", key);
        foreach (var (key, value) in settings)
            if (known.TryGetValue(key, out var setting) && !Fits(setting, value))
                error("definition.settingType", $"{setting.Label} takes {Phrase(setting)}", key);
        foreach (var key in settings.GroupBy(s => s.Key, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key))
            error("definition.duplicateSetting", $"{key} is set twice", key);
    }

    // JSON null leaves the class default, so it fits every setting. A whole number is one a long holds, written without a
    // fraction; it and a number are within the setting's range when it has one (its [Setting]'s, or an int's).
    static bool Fits(EntitySettingDescriptor setting, JsonNode? value)
    {
        if (value == null || value.GetValueKind() == JsonValueKind.Null) return true;
        var kind = value.GetValueKind();
        return setting.Type switch
        {
            "integer" => kind == JsonValueKind.Number && long.TryParse(value.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole) && Within(setting, whole),
            "number" => DefinitionDocument.Fits("number", value) && Within(setting, double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture)),
            "enum" => kind == JsonValueKind.String && setting.Options.Contains(value.GetValue<string>(), StringComparer.Ordinal),
            // Text, and true or false, fit as a graph setting's do.
            var type => DefinitionDocument.Fits(type, value),
        };
    }

    /// <summary>Whether a number is within the setting's range: no smaller than its minimum and no larger than its maximum, each when it has one.</summary>
    internal static bool Within(EntitySettingDescriptor setting, double value) => !(value < setting.Minimum) && !(value > setting.Maximum);

    /// <summary>
    /// What a setting takes, in words after "takes": its kind and, for a number, its range, such as <c>a whole number from 1
    /// to 3600</c> or <c>a number of at least 0.5</c>; an int's range declared on one side only, such as <c>a whole number of
    /// at least 1</c> (the other side is the int's own, which goes without saying).
    /// </summary>
    internal static string Phrase(EntitySettingDescriptor setting)
    {
        static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);
        var what = setting.Type switch
        {
            "integer" => "a whole number",
            "enum" => "one of " + string.Join(", ", setting.Options),
            var type => DefinitionDocument.Phrase(type),
        };
        if (setting.Type is not ("integer" or "number")) return what;
        // An int's own bound is no bound the setting declared: a range given on one side says that side only.
        var (low, high) = (setting.Minimum, setting.Maximum);
        if (setting.Type == "integer" && low == int.MinValue && high != int.MaxValue) low = null;
        if (setting.Type == "integer" && high == int.MaxValue && low != null && low != int.MinValue) high = null;
        return (low, high) switch
        {
            ({ } minimum, { } maximum) => $"{what} from {Number(minimum)} to {Number(maximum)}",
            ({ } minimum, null) => $"{what} of at least {Number(minimum)}",
            (null, { } maximum) => $"{what} of at most {Number(maximum)}",
            _ => what,
        };
    }

    // The library sections are written exactly as VisualGenerator writes an automation's.
    static DefinitionPreview Generate(EntityTypeDescriptor type, VisualCatalogSnapshot snapshot)
    {
        var lines = new List<string>(); var maps = new List<VisualSourceMap>();
        void Add(string source) => lines.AddRange(source.Replace("\r\n", "\n").Split('\n'));
        Add("// HassSharp entity definition");
        Add($"public static class SharpDefinition {{ public static object Create() => new {type.TypeExpression}(); }}");
        foreach (var library in snapshot.Sources)
        {
            Add($"// NodeLibrarySource {JsonSerializer.Serialize(library.Id)} revision {library.Revision.ToString(CultureInfo.InvariantCulture)}");
            var start = lines.Count + 1;
            Add(NodeLibraryCompiler.WrapSource(library.Source));
            maps.Add(new("$library:" + library.Id, start, lines.Count));
        }
        return new(true, [], string.Join('\n', lines) + "\n", type) { SourceMap = maps };
    }
}
