using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HassSharp;

/// <summary>
/// The catalog type rule, mirrored by <c>frontend/src/visual/contract.ts</c>. Refinement is decided
/// here before any CLR conversion: a refined type is never reached from its base implicitly.
/// </summary>
public static class VisualTypes
{
    // Library ids contain colons (global::HassSharp.EntityStateSnapshot); only this exact shape is parametric.
    static readonly Regex ParametricId = new("^([a-z_]+):([a-z0-9_]+)$", RegexOptions.CultureInvariant);
    static string Text(JsonNode? node, string key) => VisualNodeCatalog.Text(node, key);
    static bool Match(string text, string pattern) => Matches(text, pattern);
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> JavaScriptPatterns = new(StringComparer.Ordinal);

    /// <summary>
    /// A catalog or service pattern tested the way JavaScript's <c>RegExp.test</c> does, so both validators agree.
    /// .NET's <c>$</c> also matches before a final newline, so each unescaped <c>$</c> outside a character class
    /// becomes <c>\z</c>, which is JavaScript's <c>$</c> without the m flag; and .NET's <c>\d</c> and <c>\w</c> also
    /// match non-ASCII digits and letters, so they become JavaScript's ASCII classes (<c>\D</c> and <c>\W</c> too,
    /// outside a character class). A pattern that cannot be compiled matches nothing.
    /// </summary>
    public static bool Matches(string text, string pattern) => TryMatches(text, pattern) ?? false;
    /// <summary>Whether the text matches, as <see cref="Matches"/>; null when the pattern cannot be compiled (or runs out of time).</summary>
    public static bool? TryMatches(string text, string pattern)
    {
        try { return Regex.IsMatch(text, JavaScriptPatterns.GetOrAdd(pattern, JavaScriptPattern), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
        catch (Exception error) when (error is ArgumentException or RegexMatchTimeoutException) { return null; }
    }
    static string JavaScriptPattern(string pattern)
    {
        var result = new System.Text.StringBuilder(pattern.Length + 4); var inClass = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length)
            {
                var escaped = pattern[++i];
                result.Append(escaped switch
                {
                    'd' => inClass ? "0-9" : "[0-9]", 'w' => inClass ? "a-zA-Z0-9_" : "[a-zA-Z0-9_]",
                    'D' when !inClass => "[^0-9]", 'W' when !inClass => "[^a-zA-Z0-9_]", _ => "\\" + escaped,
                });
                continue;
            }
            if (inClass) { if (c == ']') inClass = false; result.Append(c); continue; }
            if (c == '[') inClass = true;
            if (c == '$') result.Append(@"\z"); else result.Append(c);
        }
        return result.ToString();
    }

    /// <summary>An exact catalog type, or a type synthesized from <c>parametricTypes</c>; null when unknown.</summary>
    public static JsonObject? Resolve(JsonObject catalog, string id)
    {
        if (catalog["types"] is not JsonObject types) return null;
        if (types[id] is JsonObject exact) return exact;
        var match = ParametricId.Match(id);
        if (!match.Success) return null;
        var (name, param) = (match.Groups[1].Value, match.Groups[2].Value);
        if (catalog["parametricTypes"]?[name] is not JsonObject family || types[name] is not JsonObject root || !Match(param, Text(family, "param"))) return null;
        var label = Text(family, "label").Replace("{Param}", char.ToUpperInvariant(param[0]) + param[1..]).Replace("{param}", param);
        var resolved = new JsonObject { ["refines"] = name, ["pattern"] = Text(family, "valuePattern").Replace("{param}", param), ["label"] = label, ["description"] = label };
        foreach (var key in new[] { "clrType", "literalKind", "category", "nullable" }) if (root[key] is { } value) resolved[key] = value.DeepClone();
        return resolved;
    }

    /// <summary>The type, then each type it refines, ending at its root; empty when any link is unknown or cyclic.</summary>
    public static IReadOnlyList<string> Chain(JsonObject catalog, string id)
    {
        var chain = new List<string>();
        for (string? current = id; current != null;)
        {
            if (chain.Contains(current) || Resolve(catalog, current) is not { } spec) return [];
            chain.Add(current);
            current = spec["refines"] is JsonValue refines && refines.TryGetValue<string>(out var parent) ? parent : null;
        }
        return chain;
    }

    /// <summary>The CLR-registered root of a resolvable type, used for every runtime type lookup.</summary>
    public static string RootId(JsonObject catalog, string id) => Chain(catalog, id) is { Count: > 0 } chain ? chain[^1] : id;

    public static bool Refines(JsonObject catalog, string id, string ancestor) => Chain(catalog, id).Contains(ancestor);

    /// <param name="roots">Optional root-to-root conversion (Roslyn); otherwise the root's <c>assignableTo</c> decides.</param>
    public static bool IsAssignable(JsonObject catalog, string source, string target, Func<string, string, bool?>? roots = null)
    {
        var chain = Chain(catalog, source);
        if (chain.Count == 0 || Chain(catalog, target).Count == 0) return false;
        if (source == target) return true;
        if (Resolve(catalog, target)!.ContainsKey("refines")) return chain.Contains(target);
        var root = chain[^1];
        if (root == target) return true;
        return roots?.Invoke(root, target) ?? (catalog["types"]![root]?["assignableTo"] as JsonArray ?? []).Any(t => t is JsonValue v && v.TryGetValue<string>(out var id) && id == target);
    }

    /// <summary>Literal kind of the type itself, then pattern, numeric bounds and enumValues of every type in its chain.</summary>
    public static bool ValidLiteral(JsonObject catalog, string type, JsonNode? value, bool present)
    {
        var chain = Chain(catalog, type);
        if (chain.Count == 0 || !present) return false;
        var spec = Resolve(catalog, type)!;
        if (value == null) return spec["nullable"]?.GetValue<bool>() == true;
        var literal = Text(spec, "literalKind");
        var text = value is JsonValue sv && sv.TryGetValue<string>(out var s) ? s : null;
        double? number = literal is "number" or "integer" && value is JsonValue nv && nv.GetValueKind() == System.Text.Json.JsonValueKind.Number &&
            double.TryParse(nv.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
        var kind = literal switch
        {
            "boolean" => value is JsonValue bv && bv.TryGetValue<bool>(out _),
            "string" => text != null && (spec["character"]?.GetValue<bool>() != true || text.Length == 1),
            "object" => value is JsonObject,
            "array" => value is JsonArray,
            "enum" => (spec["enumValues"] as JsonArray ?? []).Any(v => JsonNode.DeepEquals(v, value)),
            "number" or "integer" => number is { } x && (literal != "integer" || Math.Truncate(x) == x),
            _ => false
        };
        return kind && chain.Select(id => Resolve(catalog, id)!).All(t =>
            (Text(t, "pattern") is not { Length: > 0 } pattern || text != null && Match(text, pattern)) &&
            (number is not { } x || (t["min"] == null || x >= t["min"]!.GetValue<double>()) && (t["max"] == null || x <= t["max"]!.GetValue<double>())) &&
            (t["enumValues"] is not JsonArray values || values.Any(v => JsonNode.DeepEquals(v, value))));
    }
}
