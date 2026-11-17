using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;

/// <summary>
/// An automation's revision is the SHA-256 of its generated source, and startup regenerates and stores the source of
/// every saved automation. So whatever the generator emits for an existing graph, including the catalog sections the
/// source embeds, is part of every user's automations: a change gives each of them a new revision on upgrade.
/// tests/fixtures/generated-source-pins.json holds, for each valid automation case of visual-graph-validation.json
/// (no <c>definition</c>, previewed valid with the case's snapshot as the contract suite builds it) and for
/// <see cref="VisualTests.Graph"/>, the SHA-256 of its generated source.
/// </summary>
public static class GeneratedSourcePinTests
{
    const string GraphKey = "VisualTests.Graph";

    [RuntimeSuite("--generated-source-pins", Order = 11)]
    public static Task Run()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (current, skipped) = Current();
        var pins = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "generated-source-pins.json")))!.AsObject()
            .ToDictionary(p => p.Key, p => p.Value!.GetValue<string>(), StringComparer.Ordinal);
        var cases = Cases().Select(c => c["name"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var (name, pinned) in pins.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (name != GraphKey && !cases.Contains(name))
                problems.Add($"pinned case \"{name}\" no longer exists in visual-graph-validation.json: it stands for automations people have saved, so keep the case and its graph");
            else if (!current.TryGetValue(name, out var hash))
                problems.Add($"pinned case \"{name}\" no longer generates source as a valid automation: an existing automation's revision would change (its source would no longer build)");
            else if (hash != pinned)
                problems.Add($"generated source of \"{name}\" changed (pinned {pinned}, now {hash}): an existing automation's revision would change on upgrade; keep the generated source of existing graphs byte for byte");
        }
        foreach (var name in current.Keys.Where(k => !pins.ContainsKey(k)).Order(StringComparer.Ordinal))
            problems.Add($"valid automation case \"{name}\" has no pin: add it with --describe-generated-source-pins only for a new case");
        if (problems.Count > 0) throw new Exception($"Generated source pins: {problems.Count} problem(s):\n" + string.Join("\n", problems));
        Console.WriteLine($"PASS generated source pins: {current.Count} graphs pinned ({current.Count - 1} fixture cases and {GraphKey}); {skipped.Count} valid cases skipped as they do not preview as valid; {watch.ElapsedMilliseconds} ms");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Not a test: prints the complete content of generated-source-pins.json for the current code. Only a new case's
    /// line belongs in the file; a changed hash is a changed revision for existing automations.
    /// <c>dotnet run --project tests/Runtime -- --describe-generated-source-pins</c>
    /// </summary>
    [RuntimeSuite("--describe-generated-source-pins", RunByDefault = false)]
    public static Task Describe()
    {
        var (current, _) = Current();
        var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        // Sorted by key, one entry per line, so adding a case is a one-line diff.
        Console.Write("{\n" + string.Join(",\n", current.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => "  " + JsonSerializer.Serialize(p.Key, options) + ": " + JsonSerializer.Serialize(p.Value, options))) + "\n}\n");
        return Task.CompletedTask;
    }

    static IEnumerable<JsonObject> Cases() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "visual-graph-validation.json")))!.AsArray().Select(c => c!.AsObject());

    /// <summary>The hash of each pinnable graph's generated source, and the valid automation cases that do not preview as valid.</summary>
    static (Dictionary<string, string> Hashes, List<string> Skipped) Current()
    {
        var generator = new VisualGenerator(new DiagnosticsProvider());
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var skipped = new List<string>();
        foreach (var fixture in Cases())
        {
            var name = fixture["name"]!.GetValue<string>();
            var graph = fixture["graph"] as JsonObject;
            if (fixture["valid"]?.GetValue<bool>() != true || graph == null || graph["definition"] is JsonObject) continue;
            // The case's snapshot exactly as the contract suite builds it.
            var snapshot = fixture["catalogPatch"] is JsonObject patch ? VisualGraphContractTests.Patched(patch) : VisualCatalogSnapshot.BuiltIn;
            if (fixture["services"]?.GetValue<bool>() == true) snapshot = snapshot.WithServices(VisualGraphContractTests.Services(name));
            var preview = generator.Preview(graph, snapshot);
            if (preview.Valid && preview.Source.Length > 0) hashes[name] = Hash(preview.Source);
            else skipped.Add(name);
        }
        var motion = generator.Preview(VisualTests.Graph(), VisualCatalogSnapshot.BuiltIn);
        if (!motion.Valid || motion.Source.Length == 0) throw new Exception($"Generated source pins: {GraphKey} does not preview as valid: {string.Join("; ", motion.Diagnostics)}");
        hashes[GraphKey] = Hash(motion.Source);
        if (hashes.Count < 50) throw new Exception($"Generated source pins: only {hashes.Count} graphs preview as valid, too few to pin");
        return (hashes, skipped);
    }

    // The source exactly as generated, as UTF-8 without a byte order mark.
    static string Hash(string source) => Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false).GetBytes(source)));
}
