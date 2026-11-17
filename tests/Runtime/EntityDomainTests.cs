using System.Text.Json.Nodes;
using HassSharp;

/// <summary>
/// The catalog's <c>entityDomains</c> table, which an entity definition's graph is checked against: it describes the
/// typed entity classes exactly (their state values, commands and options), and only entity graphs embed it, so an
/// automation's generated source never changes with it.
/// </summary>
static class EntityDomainTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static string Keys(JsonNode? table) => string.Join(", ", (table as JsonObject ?? []).Select(p => p.Key));

    [RuntimeSuite("--entity-domains", Order = 11)]
    public static Task EntityDomainsMatchTheSharpClasses()
    {
        var domains = VisualNodeCatalog.Root["entityDomains"] as JsonObject;
        Check(domains != null, "the catalog has an entityDomains table");
        Check(Keys(domains) == string.Join(", ", SharpDomains.All.Select(d => d.Domain)), $"entityDomains lists the typed entity domains in order, has {Keys(domains)}");
        foreach (var (type, domain) in SharpDomains.All)
        {
            var entity = (SharpEntity)Activator.CreateInstance(type)!;
            var entry = domains![domain]!;
            Check(Keys(entry["state"]) == Keys(entity.Values()), $"{domain}: state keys {Keys(entry["state"])} are those of {type.Name}.Values(), {Keys(entity.Values())}");
            Check(Keys(entry["commands"]) == string.Join(", ", entity.Commands), $"{domain}: commands {Keys(entry["commands"])} are those of {type.Name}.Commands, {string.Join(", ", entity.Commands)}");
            Check(Keys(entry["options"]) == Keys(entity.DomainOptions()), $"{domain}: option keys {Keys(entry["options"])} are those of {type.Name}.DomainOptions(), {Keys(entity.DomainOptions())}");
            Check(entry["label"] is JsonValue label && label.TryGetValue<string>(out var text) && text.Length > 0, $"{domain} has a label");
            foreach (var (key, state) in entry["state"]!.AsObject())
                Check(state?["types"] is JsonArray { Count: > 0 } types && types.All(t => VisualTypes.Chain(VisualNodeCatalog.Root, t!.GetValue<string>()).Count > 0), $"{domain}.{key}: its types are catalog types");
        }
        Console.WriteLine($"PASS entity domains: {domains!.Count} domains match the state values, commands and options of their entity classes");
        return Task.CompletedTask;
    }

    [RuntimeSuite("--entity-domains-embedded", Order = 11)]
    public static Task EmbeddedCatalogLeavesEntityDomainsOutOfAutomations()
    {
        var automation = VisualGenerator.EmbeddedCatalog(VisualTests.Graph(), VisualCatalogSnapshot.BuiltIn);
        Check(!automation.ContainsKey("entityDomains") && !automation.ContainsKey("definition"), "an automation's embedded catalog has no entityDomains or definition: " + string.Join(", ", automation.Select(p => p.Key)));
        // An automation reading entities with the nodes it may use (Entity is, Current state) embeds no more than before.
        var reads = JsonNode.Parse("""{"version":5,"name":"Reads","nodes":[{"id":"t","kind":"trigger","config":{"type":"manual"}},{"id":"is","kind":"entity_is","config":{"entity":{"kind":"literal","type":"entity_id","value":"light.kitchen"},"state":{"kind":"literal","type":"string","value":"on"}}},{"id":"i","kind":"if","config":{"value":{"kind":"unset"}}}],"edges":[{"id":"t:i","source":"t","target":"i"},{"id":"is:i","kind":"data","source":"is","target":"i","sourcePort":"value","targetPort":"value"}]}""")!.AsObject();
        Check(!VisualGenerator.EmbeddedCatalog(reads, VisualCatalogSnapshot.BuiltIn).ContainsKey("entityDomains"), "an automation using Entity is embeds no entityDomains");
        var entity = JsonNode.Parse("""{"version":5,"name":"Entity","nodes":[{"id":"s","kind":"entity_state","config":{"entity":"door","is_on":{"kind":"literal","type":"boolean","value":true}}}],"edges":[],"definition":{"entities":[{"key":"door","domain":"binary_sensor"}],"settings":[]}}""")!.AsObject();
        var embedded = VisualGenerator.EmbeddedCatalog(entity, VisualCatalogSnapshot.BuiltIn);
        Check(embedded["entityDomains"] != null && JsonNode.DeepEquals(embedded["entityDomains"], VisualNodeCatalog.Root["entityDomains"]),"an entity graph's embedded catalog keeps entityDomains whole");
        // A definition that is not an object leaves the graph an automation: it generates what the graph without the key
        // does, but for the graph it embeds.
        var generator = new VisualGenerator(new DiagnosticsProvider());
        var plain = generator.Preview(reads, VisualCatalogSnapshot.BuiltIn);
        Check(plain.Valid, "the automation reading entities previews as valid");
        foreach (var value in new JsonNode?[] { null, JsonValue.Create("door") })
        {
            var stray = reads.DeepClone().AsObject(); stray["definition"] = value;
            var what = value == null ? "a null definition" : "a text definition";
            Check(!VisualGenerator.EmbeddedCatalog(stray, VisualCatalogSnapshot.BuiltIn).ContainsKey("entityDomains"), $"an automation with {what} embeds no entityDomains");
            var preview = generator.Preview(stray, VisualCatalogSnapshot.BuiltIn);
            Check(preview.Valid, $"an automation with {what} previews as valid: {string.Join("; ", preview.Diagnostics.Select(d => d.Message))}");
            Check(preview.EmbeddedCatalog == plain.EmbeddedCatalog, $"an automation with {what} embeds the catalog the graph without the key does");
            var a = preview.Source.Split('\n'); var b = plain.Source.Split('\n');
            var differing = Enumerable.Range(0, Math.Max(a.Length, b.Length)).Where(i => i >= a.Length || i >= b.Length || a[i] != b[i]).ToList();
            Check(a.Length == b.Length && differing.All(i => a[i].Contains("global::HassSharp.VisualRuntime(", StringComparison.Ordinal)),
                $"an automation with {what} generates the source of the graph without the key but for the graph it embeds; lines {string.Join(", ", differing)} differ");
        }
        Console.WriteLine("PASS embedded entity domains: automations leave entityDomains out, entity graphs keep it, a definition that is not an object changes nothing");
        return Task.CompletedTask;
    }

    /// <summary>
    /// The rules that allow one node of a key report every later one in id order, in both validators (the shared fixture
    /// compares diagnostics sorted, so it cannot hold the order): the editor's copy is pinned to the same order in
    /// <c>frontend/tests/visual-contract.test.ts</c>.
    /// </summary>
    [RuntimeSuite("--entity-graph-order", Order = 11)]
    public static Task LaterNodesAreReportedInIdOrder()
    {
        var graph = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "entity-graph-order.json")))!.AsObject();
        var got = VisualGraphValidator.Validate(graph["graph"]!.AsObject(), VisualCatalogSnapshot.BuiltIn)
            .Where(d => d.Code is "node.singleton" or "state.duplicate" or "command.duplicate").Select(d => $"{d.Code} {d.NodeId}").ToList();
        var want = graph["order"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Check(got.SequenceEqual(want), $"later nodes are reported in id order: expected [{string.Join(", ", want)}], got [{string.Join(", ", got)}]");
        Console.WriteLine($"PASS entity graph order: {want.Count} later nodes reported in id order");
        return Task.CompletedTask;
    }
}
