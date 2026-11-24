using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace HassSharp;

/// <summary>What a definition previews to: whether it can be saved, its problems and the source it generates.</summary>
/// <param name="Valid">True when no diagnostic is an error.</param>
/// <param name="Diagnostics">Its problems, each with a <c>definition.</c> code and, for a setting, the setting's key as its input; for an Entity node's option, that node and <c>option:&lt;option&gt;</c> as its input; for a request that lists entities, <c>entities</c> as its input, and for one that gives a device, <c>device</c>.</param>
/// <param name="Source">The generated source; empty when the definition is not valid.</param>
public sealed record DefinitionPreview(bool Valid, List<VisualDiagnostic> Diagnostics, string Source)
{
    /// <summary>The sections of <see cref="Source"/>, the generator's (its nodes' and libraries'). Empty when it is not valid.</summary>
    public List<VisualSourceMap> SourceMap { get; init; } = [];
    /// <summary>
    /// The graph as previewed, with its definition (its settings) composed into it (<see cref="DefinitionGraph.Compose"/>);
    /// its entities are its Entity nodes and its devices its Device nodes. Null for a document with problems.
    /// </summary>
    [JsonIgnore] public JsonObject? Graph { get; init; }
    /// <summary>For a valid graph, the pruned catalog its source embeds (the run snapshot's catalog); null otherwise.</summary>
    [JsonIgnore] public JsonObject? EmbeddedCatalog { get; init; }
}

/// <summary>
/// Checks an entity definition and generates its source: its document (its settings, and the entities its Entity nodes
/// declare) first, then its graph with that definition composed into it, generated as an automation's graph is.
/// </summary>
/// <param name="diagnostics">What a graph's Custom C# nodes compile with; a shared one of its own when null.</param>
/// <param name="serviceCalls">The typed service calls one request read once, for a graph's compile; the current ones when null.</param>
public sealed class DefinitionGenerator(DiagnosticsProvider? diagnostics = null, GeneratedServiceCalls? serviceCalls = null)
{
    // Only a caller without one, such as a test, previews a graph with this.
    static readonly Lazy<DiagnosticsProvider> Fallback = new(() => new DiagnosticsProvider());

    /// <summary>
    /// Every problem of the definition, in a fixed order. A definition is built from nodes: logic of any other kind, or
    /// none, is told so, beside its name's problem, with nothing else of it checked.
    /// </summary>
    /// <param name="settingsFromCode">
    /// For a library commit's rebuild, where nobody edited the definition: each C# entity's and C# device's settings are
    /// what its code now gives, as <see cref="VisualGenerator.Preview(JsonObject, VisualCatalogSnapshot, IReadOnlyList{DefinitionSetting}?, bool)"/>
    /// takes them; otherwise a node whose stored settings differ from its code's is out of date (<c>csharp.signature</c>).
    /// </param>
    public DefinitionPreview Preview(DefinitionBody body, VisualCatalogSnapshot snapshot, bool settingsFromCode = false)
    {
        var errors = new List<VisualDiagnostic>();
        void Error(string code, string message, string? input = null, string? port = null, string? node = null) => errors.Add(new(node, message, 3, InputId: input, PortId: port, Code: code));

        if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 120) Error("definition.name", "Give the definition a name of up to 120 characters");
        if (body.Logic?.Kind != "graph")
        {
            Error("definition.logic", "A definition is built from nodes: logic is {kind: \"graph\", graph}. Write C# in a C# entity or C# device node");
            return new(false, errors, "");
        }
        // A graph's entities are its Entity nodes: a request that lists them at all, even as an empty list, is told so,
        // with nothing else of it checked (its name aside), since what else is wrong is likely the list form's. Its
        // devices are its Device nodes: a request that gives one of its own, even an empty one, is told so alike (null is
        // none); one that lists entities as well is told of those alone.
        if (body.Entities != null)
        {
            Error("definition.entitiesNotAllowed", "Entities are Entity nodes in the graph now: leave entities out and add one Entity node for each", "entities");
            return new(false, errors, "");
        }
        if (body.Device != null)
        {
            Error("definition.deviceNotAllowed", "Devices are Device nodes in the graph now: leave device out, add a Device node and connect its Entities pin to each entity's Device pin", "device");
            return new(false, errors, "");
        }
        DefinitionDocument.Check(body, snapshot.Root, Error);
        if (snapshot.LibraryDiagnostics.Any(d => d.Severity == 3)) Error("definition.library", "The node libraries have errors; fix them first");

        // The document's problems come first: its graph is not checked against a definition that would only repeat them.
        // A problem on an Entity node is the graph's, reported with the rest of the graph's, and leaves it with no source.
        if (errors.Any(e => e.NodeId == null)) return new(false, errors, "");
        var preview = GenerateGraph(body, snapshot, settingsFromCode);
        return errors.Count == 0 ? preview : preview with { Valid = false, Diagnostics = [.. errors, .. preview.Diagnostics], Source = "", SourceMap = [], EmbeddedCatalog = null };
    }

    /// <summary>
    /// The graph with the document's definition composed into it, previewed as an automation's graph is: its problems
    /// and warnings each on its node, and when valid its source, source map and embedded catalog.
    /// </summary>
    DefinitionPreview GenerateGraph(DefinitionBody body, VisualCatalogSnapshot snapshot, bool settingsFromCode)
    {
        var graph = DefinitionGraph.Compose(body);
        // The settings with their values, which the graph does not carry: a C# node's pin wired to a Setting is checked with its value.
        var preview = new VisualGenerator(diagnostics ?? Fallback.Value, serviceCalls).Preview(graph, snapshot, body.Settings ?? [], settingsFromCode);
        return new(preview.Valid, preview.Diagnostics, preview.Valid ? preview.Source : "")
        {
            SourceMap = preview.Valid ? preview.SourceMap : [],
            Graph = preview.Graph ?? graph,
            EmbeddedCatalog = preview.Valid && preview.EmbeddedCatalog != null ? JsonNode.Parse(preview.EmbeddedCatalog)!.AsObject() : null,
        };
    }

    /// <summary>
    /// Whether a value is one the setting takes. JSON null leaves the property's default, so it fits every setting. A whole
    /// number is one a long holds, written without a fraction; it and a number are within the setting's range when it has
    /// one (its [Setting]'s, or an int's). An enum takes the text of one of its options. An entity takes a text that is an
    /// entity id (<c>light.kitchen</c>), and one of its domain (starting <c>light.</c>) when it has one.
    /// </summary>
    internal static bool Fits(EntitySettingDescriptor setting, JsonNode? value)
    {
        if (value == null || value.GetValueKind() == JsonValueKind.Null) return true;
        var kind = value.GetValueKind();
        return setting.Type switch
        {
            "integer" => kind == JsonValueKind.Number && long.TryParse(value.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole) && Within(setting, whole),
            "number" => DefinitionDocument.Fits("number", value) && Within(setting, double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture)),
            "enum" => kind == JsonValueKind.String && setting.Options.Contains(value.GetValue<string>(), StringComparer.Ordinal),
            "entity" => kind == JsonValueKind.String && value.GetValue<string>() is var id && EntityId.IsMatch(id) && (setting.Domain == null || id.StartsWith(setting.Domain + ".", StringComparison.Ordinal)),
            // Text, and true or false, fit as a graph setting's do.
            var type => DefinitionDocument.Fits(type, value),
        };
    }
    /// <summary>The shape of an entity id, as an entity handle checks it (<see cref="Entities.Entity"/>): <c>\z</c>, not <c>$</c>, so no final line break.</summary>
    static readonly System.Text.RegularExpressions.Regex EntityId = new("^[a-z0-9_]+\\.[a-z0-9_]+\\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The setting a C# entity's or C# device's signature lists (well formed: <see cref="VisualNodeCatalog.WellFormedClassSignature"/>),
    /// as the compiler describes a <c>[Setting]</c> of the node's class, so that its values are checked and phrased alike: its options when
    /// it lists them, its range when it has one, an entity's domain when it has one, and whether it is required.
    /// </summary>
    internal static EntitySettingDescriptor Described(JsonObject setting)
    {
        static double? Bound(JsonNode? value) => value is JsonValue number && number.GetValueKind() == JsonValueKind.Number ? number.GetValue<double>() : null;
        var description = VisualNodeCatalog.Text(setting, "description");
        return new(VisualNodeCatalog.Text(setting, "key"), VisualNodeCatalog.Text(setting, "label"), description == "" ? null : description, VisualNodeCatalog.Text(setting, "type"),
            setting["secret"] is JsonValue secret && secret.TryGetValue<bool>(out var hidden) && hidden,
            [.. (setting["options"] as JsonArray ?? []).Select(o => o is JsonValue v && v.TryGetValue<string>(out var option) ? option : "")])
        {
            Minimum = Bound(setting["minimum"]), Maximum = Bound(setting["maximum"]),
            Domain = setting["domain"] is JsonValue d && d.TryGetValue<string>(out var domain) ? domain : null,
            Required = setting["required"] is JsonValue r && r.TryGetValue<bool>(out var required) && required,
        };
    }

    /// <summary>Whether a number is within the setting's range: no smaller than its minimum and no larger than its maximum, each when it has one.</summary>
    internal static bool Within(EntitySettingDescriptor setting, double value) => !(value < setting.Minimum) && !(value > setting.Maximum);

    /// <summary>
    /// What a setting takes, in words after "takes": its kind and, for a number, its range, such as <c>a whole number from 1
    /// to 3600</c> or <c>a number of at least 0.5</c>; an int's range declared on one side only, such as <c>a whole number of
    /// at least 1</c> (the other side is the int's own, which goes without saying), and an int's own range none: <c>a whole
    /// number</c>. An entity's is <c>an entity id</c>, and <c>an entity id that starts with light.</c> with a domain.
    /// </summary>
    internal static string Phrase(EntitySettingDescriptor setting)
    {
        static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);
        var what = setting.Type switch
        {
            "integer" => "a whole number",
            "enum" => "one of " + string.Join(", ", setting.Options),
            "entity" => setting.Domain is { } domain ? $"an entity id that starts with {domain}." : "an entity id",
            var type => DefinitionDocument.Phrase(type),
        };
        if (setting.Type is not ("integer" or "number")) return what;
        // An int's own bound is no bound the setting declared: a range given on one side says that side only, and an
        // int's own range is no range at all.
        var (low, high) = (setting.Minimum, setting.Maximum);
        if (setting.Type == "integer" && low == int.MinValue && high == int.MaxValue) (low, high) = (null, null);
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
}
