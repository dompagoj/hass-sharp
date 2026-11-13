using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HassSharp;

/// <summary>
/// Writes runtime values as trace JSON within the C10 caps, counting bytes while it writes: it never serialises
/// more of a value than fits, so a huge value costs no more than its first few kilobytes.
/// </summary>
/// <remarks>
/// Tracing must not change what an automation does, so the writer walks only materialised values: JSON types,
/// arrays, collections, dictionaries and the compiler-generated auto-properties of objects (read from their
/// backing fields, so no getter runs). A lazy sequence or anything else is written as its type name, marked
/// <c>trunc</c>. Properties, dictionary keys and ports with a secret-looking name are written as <c>"***"</c>, and
/// <c>token=</c> inside a string loses its value. The redaction is by name only.
/// </remarks>
static class TraceJson
{
    internal const int MaxDepth = 6, MaxItems = 50, MaxChars = 1000, MaxValueBytes = 4096;
    internal const string Redacted = "***";
    internal static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly HashSet<string> secrets = new(["password", "passwd", "secret", "token", "access_token", "api_key", "apikey", "authorization", "code", "pin"], StringComparer.OrdinalIgnoreCase);
    static readonly Regex tokenQuery = new("(token)=[^&\\s\"'#<>]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    // Weakly keyed: library and script types live in collectible assemblies.
    static readonly ConditionalWeakTable<Type, Shape> shapes = new();

    /// <summary>The auto-properties of a type, by backing field, and whether it has other public properties.</summary>
    sealed record Shape((string Name, FieldInfo Field)[] Members, bool Partial);

    /// <summary>A property, dictionary key or port name whose value is never recorded.</summary>
    internal static bool Secret(string name) => secrets.Contains(name.StartsWith("field:", StringComparison.Ordinal) ? name[6..] : name);

    /// <summary>An upper bound of the UTF-8 bytes one UTF-16 unit takes inside a JSON string.</summary>
    static int Bytes(char c) => c is >= ' ' and < (char)0x7f and not ('"' or '\\' or '<' or '>' or '&' or '\'' or '+' or '`') ? 1 : 6;

    /// <summary>An upper bound of the serialised length of <paramref name="text"/>, quotes included.</summary>
    internal static long Length(string text)
    {
        long total = 2;
        foreach (var c in text) total += Bytes(c);
        return total;
    }

    /// <summary>The longest prefix of at most <paramref name="chars"/> units whose serialised body fits <paramref name="bytes"/>, never splitting a surrogate pair.</summary>
    internal static string Cut(string text, int chars, long bytes)
    {
        long used = 0; var i = 0;
        while (i < text.Length && i < chars)
        {
            var pair = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
            var width = pair ? 2 : 1;
            var cost = pair ? 12 : Bytes(text[i]);
            if (i + width > chars || used + cost > bytes) break;
            used += cost; i += width;
        }
        return Clean(i == text.Length ? text : text[..i]);
    }

    // A lone surrogate is not valid UTF-16; the writer would reject it.
    static string Clean(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { i++; continue; }
            if (char.IsSurrogate(c))
            {
                var chars = text.ToCharArray();
                for (var j = i; j < chars.Length; j++)
                {
                    if (char.IsHighSurrogate(chars[j]) && j + 1 < chars.Length && char.IsLowSurrogate(chars[j + 1])) { j++; continue; }
                    if (char.IsSurrogate(chars[j])) chars[j] = '�';
                }
                return new string(chars);
            }
        }
        return text;
    }

    /// <summary>The type shown for a value whose port declares none.</summary>
    internal static string Kind(object? value) => value switch
    {
        null => "null",
        bool => "boolean",
        string or char => "string",
        JsonElement e => e.ValueKind switch { JsonValueKind.Object => "object", JsonValueKind.Array => "array", JsonValueKind.String => "string", JsonValueKind.Number => "number", JsonValueKind.True or JsonValueKind.False => "boolean", _ => "null" },
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v => v.GetValueKind() switch { JsonValueKind.String => "string", JsonValueKind.Number => "number", JsonValueKind.True or JsonValueKind.False => "boolean", _ => "null" },
        _ when Number(value) => "number",
        _ => TypeName(value.GetType()),
    };

    static string TypeName(Type type) => "global::" + (type.FullName ?? type.Name).Replace('+', '.');

    static bool Number(object value) => !value.GetType().IsEnum && Type.GetTypeCode(value.GetType()) is >= TypeCode.SByte and <= TypeCode.Decimal;

    /// <summary>
    /// One port: <c>{type, v, trunc?, redacted?, src?}</c>, and the same without <c>v</c> for when its value does not
    /// fit. A port with a secret name records <c>"***"</c>.
    /// </summary>
    internal static (byte[] Full, byte[] Bare) Entry(string port, string type, object? value, (string Node, string Port)? source)
    {
        var secret = Secret(port);
        byte[] Write(bool full)
        {
            var buffer = new ArrayBufferWriter<byte>(64);
            using (var w = new Utf8JsonWriter(buffer, Options))
            {
                w.WriteStartObject();
                w.WriteString("type", type);
                var cut = !full;
                if (full && secret) w.WriteString("v", Redacted);
                else if (full) { w.WritePropertyName("v"); cut = new Writer(w, MaxValueBytes).Root(value); }
                if (cut) w.WriteBoolean("trunc", true);
                if (secret) w.WriteBoolean("redacted", true);
                if (source is { } src) { w.WriteStartObject("src"); w.WriteString("node", src.Node); w.WriteString("port", src.Port); w.WriteEndObject(); }
                w.WriteEndObject();
            }
            return buffer.WrittenSpan.ToArray();
        }
        return (Write(true), Write(false));
    }

    /// <summary>The trigger payload as a JSON object within 16 KiB; true when it was cut.</summary>
    internal static (byte[] Json, bool Truncated) Trigger(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return ("{}"u8.ToArray(), payload.ValueKind != JsonValueKind.Undefined);
        return Value(payload, VisualTrace.MaxTriggerBytes);
    }

    /// <summary>One value within <paramref name="bytes"/>; true when it was cut.</summary>
    internal static (byte[] Json, bool Truncated) Value(object? value, int bytes)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        bool cut;
        using (var w = new Utf8JsonWriter(buffer, Options)) cut = new Writer(w, bytes).Root(value);
        return (buffer.WrittenSpan.ToArray(), cut);
    }

    static Shape ShapeOf(Type type) => shapes.GetValue(type, t =>
    {
        var members = new List<(string, FieldInfo)>(); var partial = false;
        foreach (var property in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || property.GetMethod?.IsPublic != true || property.GetIndexParameters().Length > 0) continue;
            // An auto-property's value lives in its compiler-generated backing field.
            var field = FieldIn(t, $"<{property.Name}>k__BackingField");
            if (field != null && field.IsDefined(typeof(CompilerGeneratedAttribute))) members.Add((property.Name, field));
            else partial = true;
        }
        return new([.. members], partial);
    });

    static FieldInfo? FieldIn(Type? type, string name)
    {
        for (; type != null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } field) return field;
        return null;
    }

    /// <summary>
    /// Writes exactly one JSON value into <c>w</c> within <c>max</c> bytes. Before each token it checks the bytes
    /// still free, keeping one closing byte per open container and room for a separator and a <c>null</c>,
    /// so a value cut anywhere is still valid JSON. The few calls that can reach other code (a collection's
    /// enumerator, an exception message) are guarded: a failure only shortens the value.
    /// </summary>
    sealed class Writer(Utf8JsonWriter w, int max)
    {
        const int Least = 6;
        readonly long start = w.BytesCommitted + w.BytesPending;
        int open;
        bool full, cut;

        long Free => max - (w.BytesCommitted + w.BytesPending - start) - open;
        bool Fits(long bytes) { if (!full && bytes + 1 + Least <= Free) return true; full = cut = true; return false; }

        public bool Root(object? value) { Value(value, 0); return cut; }

        // Something that is not walked: its type name, marked as cut.
        void Opaque(Type type) { cut = true; String(TypeName(type)); }

        void Value(object? value, int depth)
        {
            if (full) { w.WriteNullValue(); return; }
            switch (value)
            {
                case null: w.WriteNullValue(); return;
                case bool b: w.WriteBooleanValue(b); return;
                case string s: String(s); return;
                case char c: String(c.ToString()); return;
                case JsonElement e: Element(e, depth); return;
                case JsonNode n: Node(n, depth); return;
                case double d: Double(d); return;
                case float f: Double(f); return;
                case decimal m: if (Fits(32)) w.WriteNumberValue(m); else w.WriteNullValue(); return;
                case Enum: Value(Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType()), CultureInfo.InvariantCulture), depth); return;
                case ulong u: if (Fits(20)) w.WriteNumberValue(u); else w.WriteNullValue(); return;
                case sbyte or byte or short or ushort or int or uint or long: if (Fits(20)) w.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture)); else w.WriteNullValue(); return;
                case DateTime time: String(time.ToString("O", CultureInfo.InvariantCulture)); return;
                case DateTimeOffset time: String(time.ToString("O", CultureInfo.InvariantCulture)); return;
                case DateOnly date: String(date.ToString("O", CultureInfo.InvariantCulture)); return;
                case TimeOnly time: String(time.ToString("O", CultureInfo.InvariantCulture)); return;
                case TimeSpan span: String(span.ToString("c", CultureInfo.InvariantCulture)); return;
                case Guid guid: String(guid.ToString()); return;
            }
            var type = value.GetType();
            // Python.NET containers are enumerable but must not be touched without the GIL.
            if (type.Namespace?.StartsWith("Python", StringComparison.Ordinal) == true) { Opaque(type); return; }
            if (depth >= MaxDepth && IsContainer(value)) { cut = true; w.WriteNullValue(); return; }
            switch (value)
            {
                case EntityStateSnapshot entity:
                    Object(Pairs(("entity_id", entity.EntityId), ("state", entity.State), ("attributes", entity.Attributes),
                        ("last_changed", entity.LastChanged), ("last_updated", entity.LastUpdated), ("last_reported", entity.LastReported)), depth); return;
                case Exception error:
                    string message;
                    try { message = error.Message; } catch { message = ""; cut = true; }
                    Object(Pairs(("type", type.Name), ("message", message)), depth); return;
                case IDictionary map: Guarded(map.GetEnumerator, items => Object(Entries(items), depth)); return;
                case System.Array or ICollection: Guarded(((IEnumerable)value).GetEnumerator, items => Array(items, depth)); return;
            }
            if (Materialised(type)) { Guarded(((IEnumerable)value).GetEnumerator, items => Array(items, depth)); return; }
            if (value is IEnumerable || type.Assembly == typeof(object).Assembly || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true || type.Namespace?.StartsWith("Microsoft", StringComparison.Ordinal) == true)
            { Opaque(type); return; }
            var shape = ShapeOf(type);
            if (shape.Members.Length == 0 && shape.Partial) { Opaque(type); return; }
            if (shape.Partial) cut = true;
            Object(Members(value, shape), depth);
        }

        static bool IsContainer(object value) => value is JsonObject or JsonArray or EntityStateSnapshot or Exception or IEnumerable
            || value is JsonElement { ValueKind: JsonValueKind.Object or JsonValueKind.Array }
            || value.GetType() is { IsPrimitive: false, IsEnum: false } t && t.Namespace?.StartsWith("System", StringComparison.Ordinal) != true;

        // A generic ICollection<T> or IReadOnlyCollection<T> holds its items; any other sequence may be lazy.
        static bool Materialised(Type type) => type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() is var d && (d == typeof(ICollection<>) || d == typeof(IReadOnlyCollection<>)));

        void Guarded<T>(Func<T> open, Action<T> write)
        {
            T items;
            try { items = open(); } catch { cut = true; w.WriteNullValue(); return; }
            write(items);
        }

        void Double(double d)
        {
            if (!double.IsFinite(d)) { String(d.ToString(CultureInfo.InvariantCulture)); return; }
            if (Fits(24)) w.WriteNumberValue(d); else w.WriteNullValue();
        }

        // A string of at most MaxChars units, with token= values removed. Only a window slightly longer than what
        // can be kept is redacted, so a token value crossing the cut is never kept in part.
        void String(string text, bool longer = false)
        {
            if (!Fits(2)) { w.WriteNullValue(); return; }
            var window = text.Length > 2 * MaxChars ? text[..(2 * MaxChars)] : text;
            if (window.Contains("token=", StringComparison.OrdinalIgnoreCase)) window = tokenQuery.Replace(window, "$1=" + Redacted);
            var body = Free - 1 - Least - 2;
            var result = Cut(window, MaxChars, body);
            if (longer || result.Length < text.Length)
            {
                cut = true;
                // Cut by bytes rather than the character cap: nothing else fits either.
                if (result.Length < Math.Min(window.Length, MaxChars) - 1) full = true;
            }
            w.WriteStringValue(result);
        }

        // A raw JSON string token: decode only a prefix of its escaped UTF-8 body, never the whole string.
        void RawString(ReadOnlySpan<byte> raw)
        {
            if (!Fits(2)) { w.WriteNullValue(); return; }
            var inner = raw[1..^1];
            int i = 0, chars = 0;
            while (i < inner.Length)
            {
                int length, units = 1;
                var b = inner[i];
                if (b == '\\')
                {
                    length = inner[i + 1] == 'u' ? 6 : 2;
                    if (length == 6 && Hex(inner.Slice(i + 2, 4)) is >= 0xD800 and <= 0xDBFF && i + 12 <= inner.Length && inner[i + 6] == '\\' && inner[i + 7] == 'u') { length = 12; units = 2; }
                }
                else if (b < 0x80) length = 1;
                else if ((b & 0xE0) == 0xC0) length = 2;
                else if ((b & 0xF0) == 0xE0) length = 3;
                else { length = 4; units = 2; }
                if (chars + units > 2 * MaxChars) break;
                i += length; chars += units;
            }
            var token = new byte[i + 2];
            token[0] = token[^1] = (byte)'"';
            inner[..i].CopyTo(token.AsSpan(1));
            string text;
            try { var reader = new Utf8JsonReader(token); reader.Read(); text = reader.GetString() ?? ""; }
            catch (Exception error) when (error is InvalidOperationException or JsonException) { cut = true; w.WriteNullValue(); return; }
            String(text, i < inner.Length);
        }

        static int Hex(ReadOnlySpan<byte> digits) =>
            int.TryParse(Encoding.ASCII.GetString(digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value) ? value : -1;

        void Element(JsonElement e, int depth)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object: if (depth >= MaxDepth) { cut = true; w.WriteNullValue(); } else Object(Properties(e), depth); return;
                case JsonValueKind.Array: if (depth >= MaxDepth) { cut = true; w.WriteNullValue(); } else Array(Items(e), depth); return;
                case JsonValueKind.String: RawString(JsonMarshal.GetRawUtf8Value(e)); return;
                case JsonValueKind.Number:
                    var raw = JsonMarshal.GetRawUtf8Value(e);
                    if (Fits(raw.Length)) w.WriteRawValue(raw, true); else w.WriteNullValue();
                    return;
                case JsonValueKind.True: w.WriteBooleanValue(true); return;
                case JsonValueKind.False: w.WriteBooleanValue(false); return;
                default: w.WriteNullValue(); return;
            }
        }

        void Node(JsonNode node, int depth)
        {
            switch (node)
            {
                case JsonObject o: if (depth >= MaxDepth) { cut = true; w.WriteNullValue(); } else Object(o.Select(p => new KeyValuePair<string, object?>(p.Key, p.Value)).GetEnumerator(), depth); return;
                case JsonArray a: if (depth >= MaxDepth) { cut = true; w.WriteNullValue(); } else Array(a.GetEnumerator(), depth); return;
                case JsonValue v when v.TryGetValue<JsonElement>(out var e): Element(e, depth); return;
                case JsonValue v when v.TryGetValue<string>(out var s): String(s); return;
                // The wrapped CLR value (a number, a date, …) is written like any other value.
                case JsonValue v when v.TryGetValue<object>(out var wrapped) && wrapped is not JsonNode: Value(wrapped, depth); return;
                case JsonValue v:
                    var kind = v.GetValueKind();
                    if (kind is JsonValueKind.True or JsonValueKind.False) { w.WriteBooleanValue(kind == JsonValueKind.True); return; }
                    string text;
                    try { text = v.ToJsonString(); } catch { cut = true; w.WriteNullValue(); return; }
                    if (kind == JsonValueKind.String) { String(JsonSerializer.Deserialize<string>(text) ?? ""); return; }
                    if (Fits(text.Length)) w.WriteRawValue(text, true); else w.WriteNullValue();
                    return;
            }
        }

        void Object(IEnumerator<KeyValuePair<string, object?>> items, int depth)
        {
            using var _ = items;
            w.WriteStartObject(); open++;
            for (var count = 0; ; count++)
            {
                if (!Next(items)) break;
                if (count == MaxItems) { cut = true; break; }
                var key = items.Current.Key;
                var name = Cut(key, MaxChars, long.MaxValue);
                if (name.Length < key.Length) cut = true;
                if (!Fits(Length(name) + 1)) break;
                w.WritePropertyName(name);
                if (Secret(key)) String(Redacted); else Value(items.Current.Value, depth + 1);
                if (full) break;
            }
            open--; w.WriteEndObject();
        }

        void Array(IEnumerator items, int depth)
        {
            using var _ = items as IDisposable;
            w.WriteStartArray(); open++;
            for (var count = 0; ; count++)
            {
                if (!Next(items)) break;
                if (count == MaxItems) { cut = true; break; }
                if (!Fits(0)) break;
                Value(items.Current, depth + 1);
                if (full) break;
            }
            open--; w.WriteEndArray();
        }

        bool Next(IEnumerator items)
        {
            try { return items.MoveNext(); }
            catch { cut = true; return false; }
        }

        static IEnumerator<KeyValuePair<string, object?>> Pairs(params (string Key, object? Value)[] pairs) =>
            pairs.Select(p => new KeyValuePair<string, object?>(p.Key, p.Value)).GetEnumerator();

        static IEnumerator<KeyValuePair<string, object?>> Properties(JsonElement e)
        {
            foreach (var property in e.EnumerateObject()) yield return new(property.Name, property.Value);
        }

        static IEnumerator Items(JsonElement e)
        {
            foreach (var item in e.EnumerateArray()) yield return item;
        }

        static IEnumerator<KeyValuePair<string, object?>> Entries(IDictionaryEnumerator entries)
        {
            using var _ = entries as IDisposable;
            while (entries.MoveNext())
            {
                var entry = entries.Entry;
                // Only the base library's formatting is called; any other key is named by its type.
                var key = entry.Key as string
                    ?? (entry.Key is IFormattable f && (f.GetType().IsEnum || f.GetType().Assembly == typeof(object).Assembly)
                        ? f.ToString(null, CultureInfo.InvariantCulture) : entry.Key?.GetType().Name);
                yield return new(key ?? "", entry.Value);
            }
        }

        // Auto-properties, read from their backing fields: no getter runs.
        static IEnumerator<KeyValuePair<string, object?>> Members(object value, Shape shape)
        {
            foreach (var (name, field) in shape.Members) yield return new(name, field.GetValue(value));
        }
    }
}
