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
/// Tracing must not change what an automation does, so the writer walks only materialised values and runs no
/// automation code: JSON types, arrays, collections and dictionaries whose concrete type comes from the base
/// library, and the public fields and compiler-generated auto-properties of objects (read from their fields, so
/// no getter runs). A user-defined collection, a lazy sequence or anything else is written as its type name,
/// marked <c>trunc</c>. Properties, dictionary keys and ports with a secret-looking name are written as
/// <c>"***"</c>, and <c>token=</c> inside a string loses its value. The redaction is by name only.
/// </remarks>
static class TraceJson
{
    internal const int MaxDepth = 6, MaxItems = 50, MaxChars = 1000, MaxValueBytes = 4096;
    internal const string Redacted = "***";
    internal static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly HashSet<string> secrets = new(["password", "passwd", "passcode", "secret", "client_secret", "token", "access_token", "api_key", "apikey", "api-key", "authorization", "code", "pin"], StringComparer.OrdinalIgnoreCase);
    static readonly string[] secretSuffixes = ["_token", "_secret", "_password", "_passcode", "_key"];
    static readonly Regex tokenQuery = new("(token)=[^&\\s\"'#<>]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    // Weakly keyed: library and script types live in collectible assemblies.
    static readonly ConditionalWeakTable<Type, Shape> shapes = new();
    // The public key tokens of the .NET base library assemblies.
    static readonly HashSet<string> frameworkKeys = ["7cec85d7bea7798e", "b03f5f7f11d50a3a", "cc7b13ffcd2ddd51"];
    static readonly HashSet<string> collectionNamespaces = ["System.Collections", "System.Collections.Generic", "System.Collections.Concurrent", "System.Collections.Immutable", "System.Collections.Frozen", "System.Collections.Specialized"];

    enum Walk { Opaque, Items, Map, Members }

    /// <summary>
    /// How values of a type are written: not at all (its type name), as items, as a map, or as its readable members
    /// (public fields and auto-properties by field); <c>Partial</c> when it also has public properties that are not read.
    /// </summary>
    sealed record Shape(Walk Walk, (string Name, FieldInfo Field)[] Members, bool Partial);

    /// <summary>A property, dictionary key or port name whose value is never recorded.</summary>
    internal static bool Secret(string name)
    {
        if (name.StartsWith("field:", StringComparison.Ordinal)) name = name[6..];
        if (secrets.Contains(name)) return true;
        foreach (var suffix in secretSuffixes)
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

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

    static readonly Dictionary<Type, string> aliases = new()
    {
        [typeof(bool)] = "bool", [typeof(byte)] = "byte", [typeof(sbyte)] = "sbyte", [typeof(char)] = "char", [typeof(decimal)] = "decimal",
        [typeof(double)] = "double", [typeof(float)] = "float", [typeof(int)] = "int", [typeof(uint)] = "uint", [typeof(long)] = "long",
        [typeof(ulong)] = "ulong", [typeof(short)] = "short", [typeof(ushort)] = "ushort", [typeof(object)] = "object", [typeof(string)] = "string",
    };

    /// <summary>A C#-style name: <c>global::System.Collections.Generic.List&lt;int&gt;</c>, never assembly-qualified.</summary>
    internal static string TypeName(Type type)
    {
        if (aliases.TryGetValue(type, out var alias)) return alias;
        if (type.IsArray) return TypeName(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        if (type.IsGenericParameter) return type.Name;
        if (Nullable.GetUnderlyingType(type) is { } inner) return TypeName(inner) + "?";
        var name = type.Name;
        var tick = name.IndexOf('`');
        if (tick >= 0) name = name[..tick];
        if (type.IsGenericType) name += "<" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ">";
        var owner = type.DeclaringType is { } declaring && !type.IsGenericParameter
            ? (declaring.IsGenericTypeDefinition ? "global::" + (declaring.Namespace is { } ns0 ? ns0 + "." : "") + declaring.Name.Split('`')[0] : TypeName(declaring))
            : "global::" + type.Namespace;
        return owner == "global::" ? owner + name : owner + "." + name;
    }

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

    static readonly Shape opaque = new(Walk.Opaque, [], true);

    static Shape ShapeOf(Type type) => shapes.GetValue(type, t =>
    {
        // Python.NET objects are enumerable but must not be touched without the GIL.
        if (t.Namespace?.StartsWith("Python", StringComparison.Ordinal) == true) return opaque;
        if (t.IsArray) return new(Walk.Items, [], false);
        var framework = Framework(t.Assembly);
        // Only the base library's own collections are known to hold their items and to run no automation code
        // while enumerated. Wrappers over another collection (System.Collections.ObjectModel) are not among them.
        if (framework && t.Namespace is { } ns && collectionNamespaces.Contains(ns))
        {
            if (typeof(IDictionary).IsAssignableFrom(t)) return new(Walk.Map, [], false);
            if (typeof(ICollection).IsAssignableFrom(t) || t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() is var d && (d == typeof(ICollection<>) || d == typeof(IReadOnlyCollection<>))))
                return new(Walk.Items, [], false);
        }
        if (typeof(IEnumerable).IsAssignableFrom(t) || framework || t.Namespace?.StartsWith("System", StringComparison.Ordinal) == true || t.Namespace?.StartsWith("Microsoft", StringComparison.Ordinal) == true)
            return opaque;
        var members = new List<(string, FieldInfo)>(); var names = new HashSet<string>(StringComparer.Ordinal); var partial = false;
        void Add(string name, FieldInfo field) { if (names.Add(name)) members.Add((name, field)); else partial = true; }
        // Most derived first, so a hiding (new) member wins and the hidden one only marks the value partial.
        foreach (var property in t.GetProperties(BindingFlags.Instance | BindingFlags.Public).OrderBy(p => Distance(t, p.DeclaringType)))
        {
            if (!property.CanRead || property.GetMethod?.IsPublic != true || property.GetIndexParameters().Length > 0) continue;
            // An auto-property's value lives in its compiler-generated backing field; an anonymous type's in <Name>i__Field.
            var field = FieldIn(property.DeclaringType, $"<{property.Name}>k__BackingField") ?? FieldIn(property.DeclaringType, $"<{property.Name}>i__Field");
            if (field != null && (field.IsDefined(typeof(CompilerGeneratedAttribute)) || t.IsDefined(typeof(CompilerGeneratedAttribute)))) Add(property.Name, field);
            else partial = true;
        }
        foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public).OrderBy(f => Distance(t, f.DeclaringType))) Add(field.Name, field);
        return members.Count == 0 && partial ? opaque : new(Walk.Members, [.. members], partial);
    });

    static int Distance(Type type, Type? declaring)
    {
        var distance = 0;
        for (var current = type; current != null && current != declaring; current = current.BaseType) distance++;
        return distance;
    }

    static bool Framework(Assembly assembly)
    {
        if (assembly == typeof(object).Assembly) return true;
        if (assembly.IsDynamic || assembly.GetName() is not { Name: { } name } identity || !name.StartsWith("System", StringComparison.Ordinal)) return false;
        return identity.GetPublicKeyToken() is { Length: 8 } token && frameworkKeys.Contains(Convert.ToHexStringLower(token));
    }

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
            var shape = ShapeOf(type);
            var special = value is EntityStateSnapshot or Exception;
            if (depth >= MaxDepth && (special || shape.Walk != Walk.Opaque)) { cut = true; w.WriteNullValue(); return; }
            switch (value)
            {
                case EntityStateSnapshot entity:
                    Object(Pairs(("entity_id", entity.EntityId), ("state", entity.State), ("attributes", entity.Attributes),
                        ("last_changed", entity.LastChanged), ("last_updated", entity.LastUpdated), ("last_reported", entity.LastReported)), depth); return;
                case Exception error:
                    string message;
                    try { message = error.Message; } catch { message = ""; cut = true; }
                    Object(Pairs(("type", type.Name), ("message", message)), depth); return;
            }
            switch (shape.Walk)
            {
                case Walk.Map: Guarded(((IDictionary)value).GetEnumerator, items => Object(Entries(items), depth)); return;
                case Walk.Items: Guarded(((IEnumerable)value).GetEnumerator, items => Array(items, depth)); return;
                case Walk.Members: if (shape.Partial) cut = true; Object(Members(value, shape), depth); return;
                default: Opaque(type); return;
            }
        }

        void Guarded<T>(Func<T> open, Action<T> write)
        {
            T items;
            try { items = open(); } catch { cut = true; w.WriteNullValue(); return; }
            write(items);
        }

        void Dispose(object? items)
        {
            try { (items as IDisposable)?.Dispose(); } catch { cut = true; }
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
            // Cut means something other than a redaction is missing: text past the window, or past a cap.
            var over = longer || text.Length > 2 * MaxChars;
            var window = over ? text[..Math.Min(text.Length, 2 * MaxChars)] : text;
            if (window.Contains("token=", StringComparison.OrdinalIgnoreCase)) window = tokenQuery.Replace(window, "$1=" + Redacted);
            var body = Free - 1 - Least - 2;
            var result = Cut(window, MaxChars, body);
            if (over || result.Length < window.Length)
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
            w.WriteStartObject(); open++;
            HashSet<string>? names = null;
            try
            {
                for (var count = 0; ; count++)
                {
                    if (!Next(items)) break;
                    if (count == MaxItems) { cut = true; break; }
                    var key = items.Current.Key;
                    var name = Cut(key, MaxChars, long.MaxValue);
                    if (name.Length < key.Length) cut = true;
                    // Distinct keys can format alike (1 and "1", or a key cut short): later ones get their position.
                    if (!(names ??= new(StringComparer.Ordinal)).Add(name)) { name = Cut(name, MaxChars - 8, long.MaxValue) + "#" + count; names.Add(name); cut = true; }
                    if (!Fits(Length(name) + 1)) break;
                    w.WritePropertyName(name);
                    if (Secret(key)) String(Redacted); else Value(items.Current.Value, depth + 1);
                    if (full) break;
                }
            }
            finally { Dispose(items); }
            open--; w.WriteEndObject();
        }

        void Array(IEnumerator items, int depth)
        {
            w.WriteStartArray(); open++;
            try
            {
                for (var count = 0; ; count++)
                {
                    if (!Next(items)) break;
                    if (count == MaxItems) { cut = true; break; }
                    if (!Fits(0)) break;
                    object? item;
                    try { item = items.Current; } catch { cut = true; break; }
                    Value(item, depth + 1);
                    if (full) break;
                }
            }
            finally { Dispose(items); }
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

        // A base-library dictionary's entries. Keys are strings, or formatted by the base library (numbers, dates,
        // enums); any other key is named by its type and position, and the value is marked cut.
        IEnumerator<KeyValuePair<string, object?>> Entries(IDictionaryEnumerator entries)
        {
            try
            {
                for (var index = 0; entries.MoveNext(); index++)
                {
                    var entry = entries.Entry;
                    var key = entry.Key as string;
                    if (key == null && entry.Key is IFormattable f && (f.GetType().IsEnum || Framework(f.GetType().Assembly)))
                        key = f.ToString(null, CultureInfo.InvariantCulture);
                    if (key == null) { key = (entry.Key?.GetType().Name ?? "null") + "#" + index; cut = true; }
                    yield return new(key, entry.Value);
                }
            }
            finally { Dispose(entries); }
        }

        // Public fields and auto-properties, read from their fields: no getter runs.
        static IEnumerator<KeyValuePair<string, object?>> Members(object value, Shape shape)
        {
            foreach (var (name, field) in shape.Members) yield return new(name, field.GetValue(value));
        }
    }
}
