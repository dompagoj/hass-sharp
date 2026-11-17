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
        // An automation reading entities with the nodes it may use (Is on, Current state) embeds no more than before.
        var reads = JsonNode.Parse("""{"version":5,"name":"Reads","nodes":[{"id":"t","kind":"trigger","config":{"type":"manual"}},{"id":"is","kind":"entity_is","config":{"entity":{"kind":"literal","type":"entity_id","value":"light.kitchen"},"state":{"kind":"literal","type":"string","value":"on"}}},{"id":"i","kind":"if","config":{"value":{"kind":"unset"}}}],"edges":[{"id":"t:i","source":"t","target":"i"},{"id":"is:i","kind":"data","source":"is","target":"i","sourcePort":"value","targetPort":"value"}]}""")!.AsObject();
        Check(!VisualGenerator.EmbeddedCatalog(reads, VisualCatalogSnapshot.BuiltIn).ContainsKey("entityDomains"), "an automation using Is on embeds no entityDomains");
        var entity = JsonNode.Parse("""{"version":5,"name":"Entity","nodes":[{"id":"s","kind":"entity_state","config":{"entity":"door","is_on":{"kind":"literal","type":"boolean","value":true}}}],"edges":[],"definition":{"entities":[{"key":"door","domain":"binary_sensor"}],"settings":[]}}""")!.AsObject();
        var embedded = VisualGenerator.EmbeddedCatalog(entity, VisualCatalogSnapshot.BuiltIn);
        Check(embedded["entityDomains"] != null && JsonNode.DeepEquals(embedded["entityDomains"], VisualNodeCatalog.Root["entityDomains"]),"an entity graph's embedded catalog keeps entityDomains whole");
        Console.WriteLine("PASS embedded entity domains: automations leave entityDomains out, entity graphs keep it");
        return Task.CompletedTask;
    }
}
