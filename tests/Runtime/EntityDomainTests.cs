using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;

/// <summary>
/// The catalog's <c>entityDomains</c> table, which an entity definition's graph is checked against: it describes the
/// typed entity classes exactly (their state values, commands and options) and the domains a graph creates
/// (<see cref="SharpDomains.Graph"/>), and only entity graphs embed it, so an automation's generated source never changes
/// with it. A state value marked <c>"required": false</c> is one the class may leave unknown, and each value a command's
/// <c>data</c> lists is one the class reads, refused when it is required and not given.
/// </summary>
static class EntityDomainTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static string Keys(JsonNode? table) => string.Join(", ", (table as JsonObject ?? []).Select(p => p.Key));

    static readonly NullabilityInfoContext Nullability = new();
    static bool Optional(JsonNode? spec) => spec?["required"] is JsonValue flag && flag.GetValueKind() == JsonValueKind.False;
    static string Pascal(string key) => string.Concat(key.Split('_').Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    static bool Nullable(NullabilityInfo info) => info.ReadState == NullabilityState.Nullable;

    // What a command does with this data on a class with no handler: the refusal of a value, else none (it got past
    // reading the data to the missing handler).
    static async Task<ArgumentException?> Refusal(SharpEntity entity, string command, string data)
    {
        try { await entity.Command(command, JsonDocument.Parse(data).RootElement.Clone()); }
        catch (ArgumentException refused) { return refused; }
        catch (NotSupportedException) { return null; }
        throw new Exception($"{entity.GetType().Name}.{command} with {data} ran without a handler");
    }

    [RuntimeSuite("--entity-domains", Order = 11)]
    public static async Task EntityDomainsMatchTheSharpClasses()
    {
        var domains = VisualNodeCatalog.Root["entityDomains"] as JsonObject;
        Check(domains != null, "the catalog has an entityDomains table");
        Check(Keys(domains) == string.Join(", ", SharpDomains.All.Select(d => d.Domain)), $"entityDomains lists the typed entity domains in order, has {Keys(domains)}");
        foreach (var (type, domain) in SharpDomains.All)
        {
            var entity = (SharpEntity)Activator.CreateInstance(type)!;
            var entry = domains![domain]!;
            // Commands are probed on the graph row's entity with every flag option on (a light that dims, a cover with a
            // position), so that no flag refuses the data before the class reads it.
            var flags = new JsonObject();
            foreach (var (option, rule) in entry["options"]!.AsObject()) if (rule?["type"]?.GetValue<string>() == "boolean") flags[option] = true;
            var probe = SharpDomains.Create(domain, new JsonObject { ["key"] = "main", ["options"] = flags });
            Check(Keys(entry["state"]) == Keys(entity.Values()), $"{domain}: state keys {Keys(entry["state"])} are those of {type.Name}.Values(), {Keys(entity.Values())}");
            Check(Keys(entry["commands"]) == string.Join(", ", entity.Commands), $"{domain}: commands {Keys(entry["commands"])} are those of {type.Name}.Commands, {string.Join(", ", entity.Commands)}");
            Check(Keys(entry["options"]) == Keys(entity.DomainOptions()), $"{domain}: option keys {Keys(entry["options"])} are those of {type.Name}.DomainOptions(), {Keys(entity.DomainOptions())}");
            Check(entry["label"] is JsonValue label && label.TryGetValue<string>(out var text) && text.Length > 0, $"{domain} has a label");
            foreach (var (key, state) in entry["state"]!.AsObject())
            {
                Check(state?["types"] is JsonArray { Count: > 0 } types && types.All(t => VisualTypes.Chain(VisualNodeCatalog.Root, t!.GetValue<string>()).Count > 0), $"{domain}.{key}: its types are catalog types");
                // Each state value is a public property the class's code sets; one a graph's State may leave out is one the class may leave unknown.
                var property = type.GetProperty(Pascal(key), BindingFlags.Public | BindingFlags.Instance);
                Check(property is { CanRead: true, CanWrite: true }, $"{domain}.{key}: {type.Name} has a public property {Pascal(key)} to set");
                if (Optional(state))
                    Check(Nullable(Nullability.Create(property!)) && entity.Values()[key] == null, $"{domain}.{key}: a state value that is not required is one {type.Name} may leave unknown, a nullable {Pascal(key)} null until set");
            }
            foreach (var (command, spec) in entry["commands"]!.AsObject())
            {
                Check(spec?["label"] is JsonValue commandLabel && commandLabel.TryGetValue<string>(out var words) && words.Length > 0, $"{domain}.{command} has a label");
                var data = spec?["data"] as JsonObject ?? [];
                foreach (var (name, value) in data)
                {
                    Check(value?["types"] is JsonArray { Count: > 0 } types && types.All(t => VisualTypes.Chain(VisualNodeCatalog.Root, t!.GetValue<string>()).Count > 0) && value["label"] is JsonValue, $"{domain}.{command}.{name}: a label and catalog types");
                    // The class reads the value by this name: one of the wrong type is refused quoting it (not as missing), and
                    // one not given is refused, naming it, unless it is optional.
                    var wrong = ((JsonArray)value!["types"]!).Any(t => t!.GetValue<string>() == "number") ? "\"x\"" : "5";
                    var refused = await Refusal(probe, command, $$"""{"{{name}}":{{wrong}}}""");
                    Check(refused != null && refused.Message.EndsWith(", not " + wrong, StringComparison.Ordinal), $"{domain}.{command}: {type.Name} reads {name}, refusing {wrong}: {refused?.Message}");
                    var missing = await Refusal(probe, command, "{}");
                    Check(Optional(value) ? missing == null : missing != null && missing.Message.Contains($" {name}: it takes ", StringComparison.Ordinal),
                        $"{domain}.{command}: {type.Name} {(Optional(value) ? "runs without" : "refuses a command without")} {name}: {missing?.Message}");
                }
                // The delegate that handles it takes the data in order, a value that may be left out as a nullable one.
                if (data.Count > 0)
                {
                    var handler = type.GetProperty("On" + Pascal(command), BindingFlags.Public | BindingFlags.Instance);
                    var arguments = handler?.PropertyType.IsGenericType == true ? handler.PropertyType.GetGenericArguments() : [];
                    Check(handler != null && arguments.Length == data.Count + 1 && arguments[^1] == typeof(Task), $"{domain}.{command}: {type.Name}.On{Pascal(command)} is a Func of its {data.Count} data values to a Task");
                    var nullable = Nullability.Create(handler!).GenericTypeArguments;
                    foreach (var (value, index) in data.Select((d, i) => (d.Value, i)))
                        Check(Nullable(nullable[index]) == Optional(value), $"{domain}.{command}: the delegate's value {index + 1} is {(Optional(value) ? "" : "not ")}nullable, as its data value is {(Optional(value) ? "not " : "")}required");
                }
            }
            foreach (var (option, spec) in entry["options"]!.AsObject())
                Check(spec?["type"] is JsonValue optionType && optionType.GetValue<string>() is "string" or "number" or "boolean" or "array" && spec["label"] is JsonValue, $"{domain}: option {option} has a type the document checks read and a label");
        }
        // The domains a graph defines are the catalog's, each with the catalog's state keys in its order.
        Check(string.Join(", ", SharpDomains.Graph.Keys.Order(StringComparer.Ordinal)) == string.Join(", ", domains!.Select(p => p.Key).Order(StringComparer.Ordinal)),
            $"the domains a graph creates are entityDomains': {string.Join(", ", SharpDomains.Graph.Keys)}, {Keys(domains)}");
        foreach (var (domain, graph) in SharpDomains.Graph)
            Check(string.Join(", ", graph.State.Keys) == Keys(domains![domain]?["state"]), $"{domain}: the graph's state keys {string.Join(", ", graph.State.Keys)} are entityDomains', {Keys(domains![domain]?["state"])}");
        Console.WriteLine($"PASS entity domains: {domains!.Count} domains match the state values, commands with their data, and options of their entity classes and the domains a graph creates");
    }

    [RuntimeSuite("--entity-domains-embedded", Order = 11)]
    public static Task EmbeddedCatalogLeavesEntityDomainsOutOfAutomations()
    {
        var automation = VisualGenerator.EmbeddedCatalog(VisualTests.Graph(), VisualCatalogSnapshot.BuiltIn);
        Check(!automation.ContainsKey("entityDomains") && !automation.ContainsKey("definition"), "an automation's embedded catalog has no entityDomains or definition: " + string.Join(", ", automation.Select(p => p.Key)));
        // An automation reading entities with the nodes it may use (Has state, Current state) embeds no more than before.
        var reads = JsonNode.Parse("""{"version":5,"name":"Reads","nodes":[{"id":"t","kind":"trigger","config":{"type":"manual"}},{"id":"is","kind":"entity_is","config":{"entity":{"kind":"literal","type":"entity_id","value":"light.kitchen"},"state":{"kind":"literal","type":"string","value":"on"}}},{"id":"i","kind":"if","config":{"value":{"kind":"unset"}}}],"edges":[{"id":"t:i","source":"t","target":"i"},{"id":"is:i","kind":"data","source":"is","target":"i","sourcePort":"value","targetPort":"value"}]}""")!.AsObject();
        Check(!VisualGenerator.EmbeddedCatalog(reads, VisualCatalogSnapshot.BuiltIn).ContainsKey("entityDomains"), "an automation using Has state embeds no entityDomains");
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
