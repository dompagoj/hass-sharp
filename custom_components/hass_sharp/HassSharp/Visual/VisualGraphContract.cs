using System.Text.Json.Nodes;
using static HassSharp.VisualNodeCatalog;

namespace HassSharp;

/// <summary>Immutable authoring helpers for the catalog graph format.</summary>
public static class VisualGraphContract
{
    public static JsonObject CreateNode(string kind, string id, VisualCatalogSnapshot? snapshot = null)
    {
        var spec = GetNode(kind, snapshot) ?? throw new ArgumentException("Unknown node kind", nameof(kind));
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("Node id is required", nameof(id));
        var config = new JsonObject();
        foreach (var group in new[] { "config", "inputs" }) foreach (var (key, value) in spec[group]!.AsObject())
            if (value is JsonObject rule && rule.TryGetPropertyValue("default", out var fallback)) {
                var storage = group == "inputs" && Text(spec, "argumentStorage") == "arguments"
                    ? config["arguments"] as JsonObject ?? (JsonObject)(config["arguments"] = new JsonObject()) : config;
                storage[key] = fallback?.DeepClone();
            }
        var node = new JsonObject { ["id"] = id, ["kind"] = kind, ["config"] = config };
        // A Custom C# node is titled by its method until someone names it.
        if (Text(spec, "dynamicInputs") != "custom") node["label"] = Text(spec, "label");
        return node;
    }
    /// <summary>A copy with a position for every node that lacks one. Nodes have no stored size (the editor measures
    /// them), so a <c>size</c> an older client still sends is dropped.</summary>
    public static JsonObject FillMissingLayout(JsonObject graph)
    {
        var next = (JsonObject)graph.DeepClone();
        if (next["nodes"] is not JsonArray nodes) throw new ArgumentException("Nodes array is required", nameof(graph));
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is not JsonObject node) throw new ArgumentException("Node must be an object", nameof(graph));
            node["position"] ??= new JsonObject { ["x"] = 60 + i % 4 * 300, ["y"] = 100 + i / 4 * 180 };
            node.Remove("size");
        }
        return next;
    }
}
