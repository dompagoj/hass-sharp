using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>
/// An Entity node declares one entity of its definition: its settings, without its run history and state values, are
/// the entity's description, and the catalog of its graph lists it after the listed entities of other keys, in place of
/// a listed one of its key. A graph of the wrong shape declares nothing.
/// </summary>
static class EntityNodeTests
{
    [RuntimeSuite("--entity-nodes", Order = 11)]
    public static Task Run()
    {
        var failures = new List<string>();
        // Every test runs, and each failure is reported under its name.
        foreach (var (name, test) in new (string, Action)[] {
            (nameof(NodesDeclareEntities), NodesDeclareEntities),
            (nameof(ANodeReplacesAListedEntityOfItsKey), ANodeReplacesAListedEntityOfItsKey),
            (nameof(AGraphOfTheWrongShapeDeclaresNothing), AGraphOfTheWrongShapeDeclaresNothing) })
        {
            try
            {
                test();
                Console.WriteLine($"PASS entity nodes: {name}");
            }
            catch (Exception e) { failures.Add($"{name}: {e.Message}"); }
        }
        if (failures.Count > 0) throw new Exception($"Entity nodes: {failures.Count} failing:\n" + string.Join("\n", failures));
        return Task.CompletedTask;
    }

    /// <summary>An Entity node of the key and type, named by its key, with these settings in place of its own.</summary>
    internal static JsonObject Hub(string id, string key, string domain, JsonObject? more = null)
    {
        var config = new JsonObject { ["key"] = key, ["domain"] = domain, ["name"] = key, ["options"] = new JsonObject(), ["history"] = new JsonObject(), ["arguments"] = new JsonObject() };
        foreach (var (name, value) in more ?? []) config[name] = value?.DeepClone();
        return new JsonObject { ["id"] = id, ["kind"] = "entity", ["label"] = "", ["config"] = config };
    }
    static JsonObject Graph(JsonArray? listed, params JsonObject[] nodes)
    {
        var definition = new JsonObject { ["settings"] = new JsonArray() };
        if (listed != null) definition["entities"] = listed;
        return new JsonObject { ["version"] = 5, ["name"] = "Nodes", ["nodes"] = new JsonArray(nodes), ["edges"] = new JsonArray(), ["definition"] = definition };
    }

    static void NodesDeclareEntities()
    {
        var graph = Graph(null, Hub("a", "door", "binary_sensor"), Hub("b", "power", "sensor", new() { ["options"] = new JsonObject { ["unit"] = "W" } }));
        var declared = VisualNodeCatalog.DeclaredEntities(graph, VisualNodeCatalog.Root).ToList();
        Check(string.Join(",", declared.Select(d => d.NodeId + "=" + d.Entity["key"])) == "a=door,b=power", "nodes declare entities in node order");
        Check(declared[1].Entity["options"]!["unit"]!.GetValue<string>() == "W" && declared[1].Entity["history"] == null && declared[1].Entity["arguments"] == null,
            "an entity's description is its node's settings without history and arguments");
        var merged = VisualNodeCatalog.WithGraphTypes(VisualNodeCatalog.Root, graph);
        Check(string.Join(",", VisualNodeCatalog.DefinitionItems(merged, "entities").Select(e => e["key"])) == "door,power", "the catalog of the graph lists them");
    }

    static void ANodeReplacesAListedEntityOfItsKey()
    {
        var listed = new JsonArray(new JsonObject { ["key"] = "old", ["domain"] = "switch" }, new JsonObject { ["key"] = "door", ["domain"] = "switch" });
        var merged = VisualNodeCatalog.WithGraphTypes(VisualNodeCatalog.Root, Graph(listed, Hub("a", "door", "binary_sensor")));
        var entities = VisualNodeCatalog.DefinitionItems(merged, "entities");
        Check(string.Join(",", entities.Select(e => e["key"] + ":" + e["domain"])) == "old:switch,door:binary_sensor", "listed first, a node's entity in place of the listed one of its key");
    }

    static void AGraphOfTheWrongShapeDeclaresNothing()
    {
        foreach (var graph in new JsonObject[] { new(), new() { ["nodes"] = 3, ["definition"] = new JsonObject() }, new() { ["nodes"] = new JsonArray(1, "x", new JsonObject { ["kind"] = "entity", ["config"] = 5 }), ["definition"] = new JsonObject() } })
            Check(VisualNodeCatalog.DeclaredEntities(graph, VisualNodeCatalog.Root).All(d => d.Entity.Count == 0), "no entity is read from a malformed graph");
    }
}
