using System.Text.Json.Nodes;
using static HassSharp.VisualNodeCatalog;

namespace HassSharp;

/// <summary>Immutable authoring helpers for the v4 graph format.</summary>
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
        return new() { ["id"] = id, ["kind"] = kind, ["label"] = Text(spec, "label"), ["config"] = config };
    }
    public static JsonObject FillMissingLayout(JsonObject graph)
    {
        var next = (JsonObject)graph.DeepClone();
        if (next["nodes"] is not JsonArray nodes) throw new ArgumentException("Nodes array is required", nameof(graph));
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is not JsonObject node) throw new ArgumentException("Node must be an object", nameof(graph));
            node["position"] ??= new JsonObject { ["x"] = 60 + i % 4 * 300, ["y"] = 100 + i / 4 * 180 };
            node["size"] ??= new JsonObject { ["width"] = 220, ["height"] = 112 };
        }
        return next;
    }
}
