using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HassSharp;

/// <summary>Schema validation and code expressions for the typed version 3 nodes.</summary>
internal sealed class VisualNodeCompiler
{
    internal string StepName { get; }
    internal string StoppedName { get; }
    internal string DispatchName { get; }
    internal string ExecutedName { get; }
    internal string IterationName { get; }
    internal VisualNodeCompiler(IEnumerable<string> authoredCode)
    {
        // Parse interpolation expressions as syntax; the token lexer treats them
        // as string content. Include skipped tokens from standalone C# fragments.
        // ValueText also normalizes @name and Unicode-escaped identifiers.
        var identifiers = authoredCode.SelectMany(code => Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(code)
                .GetRoot().DescendantTokens(descendIntoTrivia: true))
            .Where(token => token.RawKind == (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.IdentifierToken)
            .Select(token => token.ValueText).ToHashSet(StringComparer.Ordinal);
        string Allocate(string stem)
        {
            var name = stem;
            for (var suffix = 1; !identifiers.Add(name); suffix++) name = stem + suffix;
            return name;
        }
        StepName = Allocate("__hassSharpStep");
        StoppedName = Allocate("__hassSharpStopped");
        DispatchName = Allocate("__hassSharpDispatch");
        ExecutedName = Allocate("__hassSharpExecuted");
        IterationName = Allocate("__hassSharpIteration");
    }
    readonly Dictionary<string, string> declarations = new(StringComparer.Ordinal);
    internal static readonly string[] Kinds = ["if", "and", "or", "xor", "not", "compare", "read_entity", "set_value", "stop", "log", "repeat", "wait_until"];
    static string Text(JsonNode cfg, string key) => cfg[key]?.GetValue<string>() ?? "";
    static string Q(string value) => JsonSerializer.Serialize(value);
    internal static string TypeName(string type) => type switch { "boolean" => "bool", "number" => "double", "string" => "string", _ => throw new ArgumentException("Result type must be boolean, number or string") };
    internal static string? OutputType(JsonNode node) => Text(node, "kind") switch
    {
        "and" or "or" or "xor" or "not" or "compare" => "boolean",
        "set_value" or "read_entity" => Text(node["config"]!, "type"), _ => null
    };
    internal void Declare(JsonNode node)
    {
        var type = OutputType(node); if (type == null) return; TypeName(type);
        var name = Text(node["config"]!, "output");
        if (!Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]{0,63}$")) throw new ArgumentException("Output name must start with a letter/underscore and contain at most 64 letters, numbers or underscores");
        if (declarations.TryGetValue(name, out var old) && old != type) throw new ArgumentException($"Value '{name}' has conflicting types {old} and {type}");
        declarations[name] = type;
    }
    internal (string Code, string Type) Operand(JsonNode? operand, string node, string? expected = null)
    {
        if (operand is not JsonObject) throw new ArgumentException("Choose a typed literal or a value reference");
        string code, type;
        if (Text(operand, "kind") == "variable")
        {
            var name = Text(operand, "name");
            if (!declarations.TryGetValue(name, out type!)) throw new ArgumentException($"Value '{name}' has no producer. Add Set value, Read entity or a logic node.");
            code = $"values.Get<{TypeName(type)}>({Q(name)}, {Q(node)})";
        }
        else if (Text(operand, "kind") == "literal")
        {
            type = Text(operand, "type"); TypeName(type);
            code = type switch
            {
                "boolean" => operand["value"] is JsonValue boolean && boolean.TryGetValue<bool>(out var b) ? b ? "true" : "false" : throw new ArgumentException("Boolean literal must be true or false"),
                "string" => operand["value"] is JsonValue text && text.TryGetValue<string>(out var str) ? Q(str) : throw new ArgumentException("String literal must contain text"),
                _ => operand["value"] is JsonValue number && number.TryGetValue<double>(out var n) ? Number(n) : throw new ArgumentException("Number literal is incomplete or invalid. Enter a finite number, for example -2.5")
            };
        }
        else throw new ArgumentException("Operand kind must be literal or variable");
        if (expected != null && type != expected) throw new ArgumentException($"Expected {expected} operand, got {type}. Use Set value to convert explicitly.");
        return (code, type);
    }
    static string Number(double n) => double.IsFinite(n) ? n.ToString("R", CultureInfo.InvariantCulture) + "d" : throw new ArgumentException("Numbers must be finite");
    internal static double Bounded(JsonNode cfg, string key, double min, double max)
    {
        if (!double.TryParse(Text(cfg, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n < min || n > max) throw new ArgumentException($"{key} must be between {min} and {max}");
        return n;
    }
    internal string Comparison(JsonNode cfg, string node, (string Code, string Type) left)
    {
        var right = Operand(cfg["right"], node, left.Type); var op = Text(cfg, "operator");
        if (op is not ("eq" or "ne" or "lt" or "le" or "gt" or "ge")) throw new ArgumentException("Choose a comparison operator");
        if (op is not ("eq" or "ne") && left.Type != "number") throw new ArgumentException("Ordering comparisons require number operands");
        return $"VisualValues.Compare({left.Code}, {right.Code}, {Q(op)})";
    }
    internal (string Code, string Type) Read(JsonNode cfg, DiagnosticsProvider diagnostics, bool fresh)
    {
        var entity = Text(cfg, "entity"); var reference = diagnostics.EntityReferenceExpression(entity);
        var type = Text(cfg, "type"); TypeName(type);
        var field = Text(cfg, "field"); var attribute = Text(cfg, "attribute");
        if (field is not ("state" or "attribute")) throw new ArgumentException("Choose state or attribute");
        if (field == "attribute" && string.IsNullOrWhiteSpace(attribute)) throw new ArgumentException("Choose an attribute name");
        return (fresh ? $"VisualValues.ReadFresh({reference}.EntityId, {Q(field)}, {Q(attribute)}, {Q(type)})" : $"VisualValues.Read(Entity({reference}), {Q(field)}, {Q(attribute)}, {Q(type)})", type);
    }
    internal IEnumerable<string> Emit(string kind, JsonNode cfg, string id, DiagnosticsProvider diagnostics, Func<string,int> next)
    {
        string Value(string key, string? type = null) => Operand(cfg[key], id, type).Code;
        string Set(string expression) => $"values.Set({Q(Text(cfg, "output"))}, {expression});";
        switch (kind)
        {
            case "if": yield return $"{StepName} = {Value("value", "boolean")} ? {next("true")} : {next("false")};"; break;
            case "and": case "or": case "xor":
                yield return Set($"{Value("left", "boolean")} {(kind == "and" ? "&" : kind == "or" ? "|" : "^")} {Value("right", "boolean")}"); break;
            case "not": yield return Set($"!{Value("value", "boolean")}"); break;
            case "compare": yield return Set(Comparison(cfg,id,Operand(cfg["left"],id))); break;
            case "read_entity": yield return Set(Read(cfg, diagnostics, false).Code); break;
            case "set_value": yield return Set($"VisualValues.ConvertValue({Value("value")}, {Q(Text(cfg,"type"))})"); break;
            case "stop": yield return $"{StoppedName} = true; return;"; break;
            case "log":
                var level = Text(cfg,"level"); if (level is not ("Debug" or "Info" or "Warn" or "Error")) throw new ArgumentException("Choose a log level");
                yield return $"Logger.Log(PyLogLevel.{level}, (string)VisualValues.ConvertValue({Value("value")}, \"string\"));"; break;
            case "repeat":
                var count = Bounded(cfg,"count",0,1000); if (count != Math.Truncate(count)) throw new ArgumentException("Repeat count must be a whole number");
                if (next("body") < 0) throw new ArgumentException("Repeat requires a Body connection");
                yield return $"for (var {IterationName} = 0; {IterationName} < {count}; {IterationName}++) {{ CancellationToken.ThrowIfCancellationRequested(); await {DispatchName}({next("body")}); if ({StoppedName}) return; }}"; break;
            case "wait_until":
                var timeout = Bounded(cfg,"timeout",0.01,86400); var interval = Bounded(cfg,"interval",0.01,3600);
                yield return $"await VisualValues.WaitUntil(() => {Comparison(cfg,id,Read(cfg,diagnostics,true))}, {Number(timeout)}, {Number(interval)}, CancellationToken);"; break;
        }
    }
}
