using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>What a definition previews to: whether it can be saved, its problems, the source it generates and its class.</summary>
/// <param name="Valid">True when no diagnostic is an error.</param>
/// <param name="Diagnostics">Its problems, each with a <c>definition.</c> code and, for a setting, the setting's key as its input.</param>
/// <param name="Source">The generated source; empty when the definition is not valid.</param>
/// <param name="Type">The class it creates; null when the type is not one the libraries declare.</param>
public sealed record DefinitionPreview(bool Valid, List<VisualDiagnostic> Diagnostics, string Source, EntityTypeDescriptor? Type)
{
    /// <summary>The library sections of <see cref="Source"/>, one <c>$library:&lt;id&gt;</c> range each; empty when it is not valid.</summary>
    public List<VisualSourceMap> SourceMap { get; init; } = [];
}

/// <summary>
/// Checks an entity definition against the classes the node libraries declare, and generates the source that creates
/// its class: a <c>SharpDefinition</c> class with the libraries after it, as an automation embeds them.
/// </summary>
public sealed class DefinitionGenerator
{
    /// <summary>
    /// Every problem of the definition, in a fixed order; a definition whose class is unknown, or whose libraries have
    /// errors, gets no setting problems. A setting it does not mention keeps the class default, as a null value does.
    /// </summary>
    public DefinitionPreview Preview(DefinitionBody body, VisualCatalogSnapshot snapshot)
    {
        var errors = new List<VisualDiagnostic>();
        void Error(string code, string message, string? input = null) => errors.Add(new(null, message, 3, InputId: input, Code: code));

        if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 120) Error("definition.name", "Give the definition a name of up to 120 characters");
        var kind = body.Logic?.Kind;
        if (kind is not ("class" or "graph")) Error("definition.logic", "Logic is a graph or a class");
        if (kind == "graph") Error("definition.graphUnsupported", "Graph definitions are not available yet");
        // An empty array is the same as none: a client may always send the field.
        if (kind == "class" && body.Entities is { Count: > 0 }) Error("definition.entitiesNotAllowed", "A class definition's entities come from its code");

        EntityTypeDescriptor? type = null;
        if (snapshot.LibraryDiagnostics.Any(d => d.Severity == 3)) Error("definition.library", "The node libraries have errors; fix them first");
        else if (kind == "class")
        {
            var name = body.Logic!.Type ?? "";
            if (!snapshot.EntityTypes.TryGetValue(name, out type)) Error("definition.unknownType", $"No entity class {name} in the node libraries");
            else Settings(type, body.Settings ?? [], Error);
        }

        if (errors.Count > 0) return new(false, errors, "", type);
        return Generate(type!, snapshot);
    }

    static void Settings(EntityTypeDescriptor type, List<DefinitionSetting> given, Action<string, string, string?> error)
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

    // JSON null leaves the class default, so it fits every setting. A whole number is one a long holds, written without a fraction.
    static bool Fits(EntitySettingDescriptor setting, JsonNode? value)
    {
        if (value == null || value.GetValueKind() == JsonValueKind.Null) return true;
        var kind = value.GetValueKind();
        return setting.Type switch
        {
            "string" => kind == JsonValueKind.String,
            "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
            "integer" => kind == JsonValueKind.Number && long.TryParse(value.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _),
            "number" => kind == JsonValueKind.Number && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number),
            "enum" => kind == JsonValueKind.String && setting.Options.Contains(value.GetValue<string>(), StringComparer.Ordinal),
            _ => false,
        };
    }

    static string Phrase(EntitySettingDescriptor setting) => setting.Type switch
    {
        "string" => "a text",
        "boolean" => "true or false",
        "integer" => "a whole number",
        "number" => "a number",
        "enum" => "one of " + string.Join(", ", setting.Options),
        _ => setting.Type,
    };

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
