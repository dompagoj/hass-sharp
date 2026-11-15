using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;

/// <summary>
/// The entity schema: Home Assistant's flat entity_component translations normalised per domain, and the saved copy
/// of it that survives a restart (the same file class as the service schema).
/// </summary>
static class EntitySchemaTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name));
    static string[] Ids(JsonNode? list) => list!.AsArray().Select(s => s!["id"]!.GetValue<string>()).ToArray();

    static T Throws<T>(Action action, string what) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        catch (Exception error) { throw new Exception($"{what}: threw {error.GetType().Name} instead of {typeof(T).Name}: {error.Message}"); }
        throw new Exception($"{what}: did not throw {typeof(T).Name}");
    }

    [RuntimeSuite("--entity-schema", Order = 38)]
    public static void Run()
    {
        Normalising();
        Hostile();
        Persistence();
        Console.WriteLine("PASS entity schema: normalised domains, hostile keys dropped and the saved copy");
    }

    static void Normalising()
    {
        var schema = EntitySchemaCatalog.Parse(Fixture("ha-entity-components.json"));
        Check(Ids(schema.Items["binary_sensor"]!["states"]).SequenceEqual(["off", "on"]), "binary_sensor");
        Check(Ids(schema.Items["lock"]!["states"]).SequenceEqual(["jammed", "locked", "locking", "open", "opening", "unlocked", "unlocking"]), "lock");
        Check(Ids(schema.Items["light"]!["attributes"]!["color_mode"]!["values"]).Contains("hs"), "color modes");
        Check(schema.Items["light"]!["attributes"]!["brightness"]!["values"]!.AsArray().Count == 0, "brightness has no values");
        Check(Ids(schema.Items["weather"]!["states"]).Contains("clear-night"), "hyphen kept");
        Check(schema.Items["scene"]!["states"]!.AsArray().Count == 0, "a domain without metadata");
        Check(schema.Items["scene"]!["attributes"]!.AsObject().Count == 0, "a domain without metadata has no attributes");
        Check(schema.Count == schema.Items.Count && schema.Count >= 56, "count");
        Check(schema.Items.Select(p => p.Key).SequenceEqual(schema.Items.Select(p => p.Key).Order(StringComparer.Ordinal)), "domains in ordinal order");
        Check(EntitySchemaCatalog.Empty.Count == 0, "empty");

        // Device class "_" wins the label; otherwise the first class in ordinal order; dotted ids are taken whole;
        // a domain with keys but not listed still gets an item.
        var labels = EntitySchemaCatalog.Parse("""
            {"domains":["a"],"translations":{
              "component.a.entity_component.zeta.state.on":"Zeta on",
              "component.a.entity_component.beta.state.on":"Beta on",
              "component.a.entity_component.beta.state.dotted.id":"Dotted",
              "component.a.entity_component._.state.off":"Off",
              "component.a.entity_component.beta.state_attributes.mode.name":"Mode",
              "component.a.entity_component.zeta.state_attributes.mode.state.x":"X",
              "component.unlisted.entity_component._.state.on":"On"}}
            """);
        var a = labels.Items["a"]!;
        Check(Ids(a["states"]).SequenceEqual(["dotted.id", "off", "on"]), "union in ordinal order, dotted id whole");
        Check(a["states"]!.AsArray().Single(s => s!["id"]!.GetValue<string>() == "on")!["label"]!.GetValue<string>() == "Beta on", "first class by ordinal order");
        Check(a["attributes"]!["mode"]!["label"]!.GetValue<string>() == "Mode" && Ids(a["attributes"]!["mode"]!["values"]).SequenceEqual(["x"]), "attribute union");
        Check(labels.Items["unlisted"]!["states"]!.AsArray().Count == 1, "an unlisted domain with keys");
    }

    static void Hostile()
    {
        var hostile = EntitySchemaCatalog.Parse("""{"domains":["Bad Domain","ok"],"translations":{"component.ok.entity_component._.state.":"x","component.ok.other.a":"x","component.Bad.entity_component._.state.on":"x"}}""");
        Check(hostile.Count == 1 && hostile.Items["ok"]!["states"]!.AsArray().Count == 0, "hostile keys dropped");
        var long101 = new string('a', 101);
        var tooLong = EntitySchemaCatalog.Parse($$$"""{"domains":["ok"],"translations":{"component.ok.entity_component._.state.{{{long101}}}":"x","component.ok.entity_component._.state_attributes.{{{long101}}}.name":"x","component.ok.entity_component._.state_attributes..name":"x"}}""");
        Check(tooLong.Items["ok"]!["states"]!.AsArray().Count == 0 && tooLong.Items["ok"]!["attributes"]!.AsObject().Count == 0, "over-long and empty names dropped");
        Throws<JsonException>(() => EntitySchemaCatalog.Parse("[]"), "not an object");
        Throws<JsonException>(() => EntitySchemaCatalog.Parse("""{"translations":{}}"""), "missing domains");
        Throws<JsonException>(() => EntitySchemaCatalog.Parse("""{"domains":[]}"""), "missing translations");
    }

    static JsonObject Item(params string[] states)
    {
        var list = new JsonArray();
        foreach (var id in states) list.Add(new JsonObject { ["id"] = id, ["label"] = id });
        return new JsonObject { ["states"] = list, ["attributes"] = new JsonObject() };
    }

    static void Persistence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "entity-schema-" + Guid.NewGuid());
        var path = Path.Combine(directory, "entity_schema.json");
        var log = PyInterop.Log; var logged = new System.Collections.Concurrent.ConcurrentQueue<string>(); PyInterop.Log = (_, m) => logged.Enqueue(m);
        try
        {
            var a = new JsonObject { ["light"] = Item("on") };
            var b = new JsonObject { ["lock"] = Item("locked") };
            var first = new ServiceCallSchema(path, "entity");
            var proposed = first.Update(a, started: false)!;
            Check(proposed.Select(p => p.Key).SequenceEqual(["light"]), "first proposal");
            Check(first.Update(a, started: false) == null, "the same schema twice");
            Check(!File.Exists(path), "nothing saved before Accept");
            first.Accept(proposed);
            Check(File.Exists(path), "saved on Accept");

            var second = new ServiceCallSchema(path, "entity");
            Check(second.Update(b, started: false)!.Select(p => p.Key).SequenceEqual(["light", "lock"]), "union of the saved and the live");
            var third = new ServiceCallSchema(path, "entity");
            Check(third.Update(b, started: true)!.Select(p => p.Key).SequenceEqual(["lock"]), "started: only the live one");

            File.WriteAllText(path, "{not json");
            var corrupt = new ServiceCallSchema(path, "entity");
            Check(corrupt.Update(b, started: false)!.Select(p => p.Key).SequenceEqual(["lock"]), "a corrupt file is ignored");
            // The log lines name the entity classes, not typed calls.
            Check(logged.Count(l => l.StartsWith("No saved entity schema yet; the entity classes start from the live one", StringComparison.Ordinal)) == 1
                && logged.Count(l => l.StartsWith("Ignoring the saved entity schema ", StringComparison.Ordinal) && l.Contains("; the entity classes start from the live one: ", StringComparison.Ordinal)) == 1,
                "the log lines: " + string.Join(" | ", logged));
        }
        finally { PyInterop.Log = log; try { Directory.Delete(directory, true); } catch (DirectoryNotFoundException) { } }
    }
}
