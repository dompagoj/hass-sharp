using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Python.Runtime;

namespace HassSharp;

/// <summary>Strict, invocation-local values for visual graphs. No absent value is replaced by a default.</summary>
public sealed class VisualValues
{
    readonly Dictionary<string, object> values = new(StringComparer.Ordinal);
    public void Set(string name, object value) => values[name] = value;
    public T Get<T>(string name, string node)
    {
        if (!values.TryGetValue(name, out var value)) throw new InvalidOperationException($"Node {node}: value '{name}' has not been assigned on this path.");
        if (value is not T typed) throw new InvalidOperationException($"Node {node}: value '{name}' requires {typeof(T).Name}, got {value.GetType().Name}.");
        return typed;
    }
    public static object ConvertValue(object? value, string type)
    {
        if (value is PyObject py)
        {
            // Python ints remain PyInt in Dictionary<string,object>. Invocation continuations
            // run on managed threads, so even type checks and conversion must hold the GIL.
            using var gil = Py.GIL();
            using var builtins = Py.Import("builtins");
            using var boolean = builtins.GetAttr("bool");
            using var integer = builtins.GetAttr("int");
            using var number = builtins.GetAttr("float");
            using var textType = builtins.GetAttr("str");
            value = py.IsInstance(boolean) ? py.As<bool>()
                : py.IsInstance(integer) || py.IsInstance(number) ? py.As<double>()
                : py.IsInstance(textType) ? py.As<string>()
                : throw new InvalidOperationException("Only boolean, number and string entity attributes are supported; null and collections cannot be converted.");
        }
        if (value is JsonElement json) value = json.ValueKind switch
        {
            JsonValueKind.String => json.GetString(), JsonValueKind.Number => json.GetDouble(),
            JsonValueKind.True => true, JsonValueKind.False => false, _ => null
        };
        if (value == null) throw new InvalidOperationException($"Cannot convert missing/null value to {type}.");
        var text = System.Convert.ToString(value, CultureInfo.InvariantCulture)!;
        return type switch
        {
            "string" when value is string or bool or byte or short or int or long or float or double or decimal => text,
            "boolean" when value is bool b => b,
            "boolean" when bool.TryParse(text, out var b) => b,
            "boolean" when text is "on" or "1" => true,
            "boolean" when text is "off" or "0" => false,
            "number" when value is not bool && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) => n,
            _ => throw new InvalidOperationException($"Cannot convert '{text}' to {type}. Boolean accepts true/false, on/off, 1/0; numbers must be finite invariant numbers.")
        };
    }
    public static object Read<T>(EntityRef<T> entity, string field, string attribute, string type)
    {
        var raw = entity.Raw;
        object? value = raw.State;
        if (field == "attribute" && (raw.Attributes == null || !raw.Attributes.TryGetValue(attribute, out value)))
            throw new InvalidOperationException($"Entity '{raw.EntityId}' has no attribute '{attribute}'.");
        return ConvertValue(value, type);
    }
    // Poll the live integration directly: trigger snapshots must never overwrite a fresh poll.
    public static object ReadFresh(string entity, string field, string attribute, string type)
    {
        var raw = PyInterop.Entity(entity) ?? throw new InvalidOperationException($"Entity '{entity}' is unavailable.");
        object? value = raw.State;
        if (field == "attribute" && (raw.Attributes == null || !raw.Attributes.TryGetValue(attribute, out value)))
            throw new InvalidOperationException($"Entity '{entity}' has no attribute '{attribute}'.");
        return ConvertValue(value, type);
    }
    public static bool Compare(object left, object right, string op) => op switch
    {
        "eq" => left.Equals(right), "ne" => !left.Equals(right),
        "lt" => (double)left < (double)right, "le" => (double)left <= (double)right,
        "gt" => (double)left > (double)right, "ge" => (double)left >= (double)right,
        _ => throw new InvalidOperationException("Unknown comparison")
    };
    public static async Task WaitUntil(Func<bool> predicate, double timeout, double interval, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        var initial = true;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!initial && clock.Elapsed.TotalSeconds >= timeout)
                throw new TimeoutException($"Wait until timed out after {timeout} seconds.");
            initial = false;
            var satisfied = predicate();
            cancellation.ThrowIfCancellationRequested();
            // A synchronous read may finish late; its result must not override timeout.
            var remaining = timeout - clock.Elapsed.TotalSeconds;
            if (remaining <= 0) throw new TimeoutException($"Wait until timed out after {timeout} seconds.");
            if (satisfied) return;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(interval, remaining)), cancellation);
        }
    }
}
