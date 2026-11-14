using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Python.Runtime;

namespace HassSharp;

/// <summary>Value conversions and waiting shared by the visual runtime's built-in steps.</summary>
public static class VisualValues
{
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
