using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HassSharp;

public sealed record VisualDiagnostic(string? NodeId, string Message, int Severity = 3, int? StartLine = null, int? StartColumn = null);
public sealed record VisualSourceMap(string NodeId, int StartLine, int EndLine);
public sealed record VisualPreview(bool Valid, string Source, List<VisualSourceMap> SourceMap, List<VisualDiagnostic> Diagnostics);

/// <summary>Versions 1 and 2 are domain graphs, never a serialized canvas/library object.</summary>
public sealed class VisualGenerator(DiagnosticsProvider diagnostics)
{
    static string Text(JsonNode? node, string key, string fallback = "") => node?[key]?.GetValue<string>() ?? fallback;
    static string Quote(string value) => JsonSerializer.Serialize(value);
    static string Required(JsonNode node, string key)
    {
        var value = Text(node, key);
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{key} is required");
        return value;
    }
    public VisualPreview Preview(JsonObject graph)
    {
        var lines = new List<string>(); var maps = new List<VisualSourceMap>(); var errors = new List<VisualDiagnostic>();
        string? current = null;
        try
        {
            var version = graph["version"]?.GetValue<int>();
            if (version is not (1 or 2)) throw new ArgumentException("Unsupported graph version");
            Required(graph, "name");
            var nodes = graph["nodes"]!.AsArray(); var edges = graph["edges"]!.AsArray();
            if (nodes.Count is < 1 or > 256 || edges.Count > 512) throw new ArgumentException("Graph limit: 1–256 nodes, at most 512 edges");
            var byId = nodes.ToDictionary(n => Required(n!, "id"), n => n!);
            var outgoing = byId.Keys.ToDictionary(id => id, _ => new List<(string Target, string Port)>());
            foreach (var edge in edges)
            {
                var from = Required(edge!, "source"); var target = Required(edge!, "target");
                if (!byId.ContainsKey(from) || !byId.ContainsKey(target)) throw new ArgumentException("Edge endpoint missing");
                var port = Text(edge, "port", "next");
                if (outgoing[from].Any(e => e.Port == port)) throw new ArgumentException("Only one outgoing edge per port is supported");
                outgoing[from].Add((target, port));
            }
            var roots = byId.Values.Where(n => Text(n, "kind") == "trigger").OrderBy(n => Text(n, "id"), StringComparer.Ordinal).ToArray();
            if (roots.Length == 0 || (version == 1 && roots.Length != 1)) throw new ArgumentException("Version 1 requires one trigger; version 2 requires at least one");
            var visiting = new HashSet<string>(); var visited = new HashSet<string>();
            void Visit(string id)
            {
                if (!visiting.Add(id)) throw new ArgumentException("Cycles are not supported");
                if (!visited.Contains(id)) foreach (var edge in outgoing[id]) Visit(edge.Target);
                visiting.Remove(id); visited.Add(id);
            }
            foreach (var root in roots) Visit(Text(root, "id"));
            if (visited.Count != byId.Count) throw new ArgumentException("Every node must be reachable from the trigger");
            if (outgoing.Values.Any(es => es.Any(e => roots.Any(r => Text(r, "id") == e.Target)))) throw new ArgumentException("Trigger cannot have incoming edges");
            var ids = byId.Keys.Order(StringComparer.Ordinal).ToArray();
            var index = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
            void Section(string key)
            {
                current = "$" + key;
                var code = Text(graph, key);
                if (version == 1 && code != "") throw new ArgumentException("C# sections require version 2");
                if (code == "") return;
                var first = lines.Count + 1;
                lines.AddRange(code.Replace("\r\n", "\n").Split('\n'));
                maps.Add(new(current, first, lines.Count));
            }
            Section("imports");
            lines.Add("public class VisualAutomation : Automation"); lines.Add("{");
            Section("members");
            string Method(JsonNode root) => version == 1 ? "Run" : "Run_" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(Text(root, "id")));
            lines.Add("public override AutomationConfiguration Configure() => new(");
            var rootNumber = 0;
            foreach (var root in roots)
            {
                var rootId = Text(root, "id");
                var config = root["config"] ?? new JsonObject(); current = rootId;
                var mode = Text(config, "mode", "Single");
                if (!Enum.GetNames<AutomationMode>().Contains(mode, StringComparer.Ordinal)) throw new ArgumentException("Unknown concurrency mode");
                var triggerType = Text(config, "type", "entity");
                var entities = "[]"; var schedules = "[]";
                if (triggerType == "entity") entities = $"[{Quote(Required(config, "entity"))}]";
                else if (triggerType == "daily")
                {
                    var time = TimeOnly.ParseExact(Required(config, "time"), "HH:mm", CultureInfo.InvariantCulture);
                    TimeZoneInfo.FindSystemTimeZoneById(Required(config, "timeZone"));
                    schedules = $"[AutomationSchedule.Daily({Quote(rootId)}, new TimeOnly({time.Hour}, {time.Minute}), {Quote(Text(config, "timeZone"))})]";
                }
                else if (triggerType == "once")
                {
                    var at = Required(config, "at");
                    if (!Regex.IsMatch(at, @"(Z|[+-]\d{2}:\d{2})$")) throw new ArgumentException("at requires an explicit UTC offset");
                    var instant = DateTimeOffset.Parse(at, CultureInfo.InvariantCulture);
                    schedules = $"[AutomationSchedule.Once({Quote(rootId)}, DateTimeOffset.Parse({Quote(instant.ToString("O"))}))]";
                }
                else if (triggerType != "manual" && triggerType != "startup") throw new ArgumentException("Unknown trigger type");
                if (version == 2)
                {
                    entities = Text(config, "entitiesExpression", entities);
                    schedules = Text(config, "schedulesExpression", schedules);
                }
                var startup = triggerType == "startup";
                var manual = true;
                if (version == 2)
                {
                    startup = config["startup"]?.GetValue<bool>() ?? startup;
                    manual = config["manual"]?.GetValue<bool>() ?? manual;
                }
                var start = lines.Count + 1;
                lines.AddRange(($"new AutomationAction(nameof({Method(root)}), AutomationMode.{mode}, Startup: {(startup ? "true" : "false")}, Manual: {(manual ? "true" : "false")}, Entities: {entities}, Schedules: {schedules}){(++rootNumber < roots.Length ? "," : "")}").Replace("\r\n", "\n").Split('\n'));
                maps.Add(new(rootId, start, lines.Count));
            }
            lines.Add(");");
            foreach (var root in roots)
            {
                var first = lines.Count + 1;
                lines.Add($"public Task {Method(root)}() => ExecuteGraph({index[Text(root, "id")]});");
                maps.Add(new(Text(root, "id"), first, lines.Count));
            }
            lines.Add("private async Task ExecuteGraph(int step)"); lines.Add("{");
            lines.Add("var responses = new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>();");
            lines.Add("Exception? lastError = null;");
            lines.Add("while (step >= 0)"); lines.Add("{");
            lines.Add("CancellationToken.ThrowIfCancellationRequested();"); lines.Add("switch (step)"); lines.Add("{");
            foreach (var id in ids)
            {
                current = id; var node = byId[id]; var kind = Text(node, "kind"); var cfg = node["config"] ?? new JsonObject();
                var allowed = kind == "condition" ? new[] { "next", "true", "false", "error" } : new[] { "next", "error" };
                if (outgoing[id].Any(e => !allowed.Contains(e.Port))) throw new ArgumentException("Unsupported edge port");
                if (kind == "condition" && outgoing[id].Any(e => e.Port == "next") && outgoing[id].Any(e => e.Port == "true")) throw new ArgumentException("Use next or true, not both");
                int Next(string port) => outgoing[id].Where(e => e.Port == port).Select(e => index[e.Target]).DefaultIfEmpty(-1).Single();
                var start = lines.Count + 1; lines.Add($"case {index[id]}:"); lines.Add("{"); lines.Add("try"); lines.Add("{");
                switch (kind)
                {
                    case "trigger": break;
                    case "condition":
                        var yes = Next("true"); if (yes == -1) yes = Next("next");
                        lines.Add($"step = Entity({Quote(Required(cfg, "entity"))}).RawState == {Quote(Required(cfg, "state"))} ? {yes} : {Next("false")};"); break;
                    case "delay":
                        var seconds = double.Parse(Required(cfg, "seconds"), CultureInfo.InvariantCulture);
                        if (!double.IsFinite(seconds) || seconds < 0 || seconds > (uint.MaxValue - 1) / 1000.0) throw new ArgumentException("Invalid delay seconds");
                        lines.Add($"await Task.Delay(TimeSpan.FromSeconds({seconds.ToString("R", CultureInfo.InvariantCulture)}), CancellationToken);"); break;
                    case "service":
                        var service = Required(cfg, "service").Split('.');
                        if (service.Length != 2 || service.Any(s => !Regex.IsMatch(s, "^[a-z0-9_]+$"))) throw new ArgumentException("service must be domain.service");
                        var data = Text(cfg, "data", "{}"); var payload = JsonNode.Parse(data) as JsonObject ?? throw new ArgumentException("data must be a JSON object");
                        if (Text(cfg, "entity") != "") payload["entity_id"] = Text(cfg, "entity");
                        var args = $"{Quote(service[0])}, {Quote(service[1])}, System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>({Quote(payload.ToJsonString())})";
                        if (Text(cfg, "response") != "") lines.Add($"responses[{Quote(Text(cfg, "response"))}] = await new ServiceCall<System.Text.Json.JsonElement>({args}).RunAsync();");
                        else lines.Add($"await new ServiceCall({args}).RunAsync();");
                        break;
                    case "custom":
                        var code = Text(cfg, "code");
                        if (code.Split('\n').Any(l => l.TrimStart().StartsWith('#'))) throw new ArgumentException("Preprocessor directives are not supported in custom sections");
                        lines.AddRange(code.Replace("\r\n", "\n").Split('\n')); break;
                    default: throw new ArgumentException("Unknown node kind");
                }
                if (kind != "condition") lines.Add($"step = {Next("next")};");
                lines.Add("}");
                lines.Add("catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested) { throw; }");
                if (Next("error") >= 0) lines.Add($"catch (Exception error) {{ lastError = error; step = {Next("error")}; }}");
                lines.Add("break;"); lines.Add("}"); maps.Add(new(id, start, lines.Count));
            }
            lines.Add("default: throw new InvalidOperationException();"); lines.Add("}"); lines.Add("}"); lines.Add("}"); lines.Add("}");
            var source = string.Join('\n', lines) + "\n";
            foreach (var d in diagnostics.GetDiagnostics(source))
                errors.Add(new(maps.FirstOrDefault(m => d.StartLine >= m.StartLine && d.StartLine <= m.EndLine)?.NodeId, d.Message, d.Severity, d.StartLine, d.StartColumn));
            return new(!errors.Any(e => e.Severity == 3), source, maps, errors);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException or JsonException or KeyNotFoundException or NullReferenceException or TimeZoneNotFoundException or OverflowException)
        {
            errors.Add(new(current, e.Message)); return new(false, "", [], errors);
        }
    }
}
