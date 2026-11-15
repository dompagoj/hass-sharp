using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HassSharp.Entities;

// The attributes and the converter are public for the generated classes; node code never names them, so the code
// editor's completion leaves them out (they are in a namespace every node's code imports).

/// <summary>The domain (<c>binary_sensor</c>) of the entities a handle class stands for.</summary>
[AttributeUsage(AttributeTargets.Class)]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class EntityDomainAttribute(string domain) : Attribute
{
    public string Domain { get; } = domain;
}

/// <summary>The text Home Assistant uses for an enum member, when it is not the member's name (<c>"on"</c>, <c>"heat_cool"</c>).</summary>
[AttributeUsage(AttributeTargets.Field)]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class EntityTextAttribute(string text) : Attribute
{
    public string Text { get; } = text;
    public string? Label { get; set; }
}

/// <summary>Enum members and their Home Assistant texts. It reflects on each call and keeps no cache: nothing is held by <see cref="Type"/>.</summary>
public static class EntityText
{
    /// <summary>The member's text; null when the member has no <see cref="EntityTextAttribute"/>.</summary>
    public static string? Of(Enum value) =>
        value.GetType().GetField(value.ToString(), BindingFlags.Public | BindingFlags.Static)?.GetCustomAttribute<EntityTextAttribute>()?.Text;

    /// <summary>The member with that text; null when none has it.</summary>
    public static object? Parse(Type enumType, string text)
    {
        foreach (var field in enumType.GetFields(BindingFlags.Public | BindingFlags.Static))
            if (field.GetCustomAttribute<EntityTextAttribute>()?.Text == text) return field.GetValue(null);
        return null;
    }

    /// <summary>Whether any member of the enum has <see cref="EntityTextAttribute"/>.</summary>
    public static bool Marked(Type enumType) =>
        enumType.GetFields(BindingFlags.Public | BindingFlags.Static).Any(f => f.IsDefined(typeof(EntityTextAttribute), false));
}

/// <summary>
/// A handle on one Home Assistant entity: it holds the id and reads the entity's current state each time a property is used.
/// A string converts to a handle where a node asks for one, and a handle converts to its id.
/// </summary>
[JsonConverter(typeof(EntityJsonConverter))]
public class Entity : IEquatable<Entity>
{
    // \z, not $: $ also matches before a final line break.
    static readonly Regex Id = new("^[a-z0-9_]+\\.[a-z0-9_]+\\z", RegexOptions.CultureInvariant);

    public Entity(string entityId)
    {
        if (entityId == null || !Id.IsMatch(entityId)) throw new ArgumentException($"'{entityId}' is not an entity id such as light.kitchen", nameof(entityId));
        EntityId = entityId;
    }

    /// <summary>The entity id, such as <c>light.kitchen</c>.</summary>
    public string EntityId { get; }
    /// <summary>The state as Home Assistant has it now (<c>on</c>, <c>21.5</c>, <c>unavailable</c>).</summary>
    public string RawState => Snapshot().State;
    /// <summary>The state as a number; null when it is not one.</summary>
    public double? NumericState => double.TryParse(RawState, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
    /// <summary>The attributes now, a JSON object.</summary>
    public JsonElement Attributes => Snapshot().Attributes;

    /// <summary>One attribute now; null when the entity has none by that name.</summary>
    public JsonElement? Attribute(string name) => Attributes.TryGetProperty(name, out var value) ? value : null;
    /// <summary>The whole state now.</summary>
    public EntityStateSnapshot Snapshot() => VisualRuntime.ReadEntity(EntityId);

    public override string ToString() => EntityId;
    public bool Equals(Entity? other) => other is not null && EntityId == other.EntityId;
    public override bool Equals(object? obj) => obj is Entity other && Equals(other);
    public override int GetHashCode() => EntityId.GetHashCode();
    [return: NotNullIfNotNull(nameof(entity))]
    public static implicit operator string?(Entity? entity) => entity?.EntityId;
}

/// <summary>Writes a handle as its id and reads one back through the (string) constructor of the type asked for.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class EntityJsonConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeof(Entity).IsAssignableFrom(typeToConvert);
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert))!;

    sealed class Converter<T> : JsonConverter<T> where T : Entity
    {
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            var id = reader.TokenType == JsonTokenType.String ? reader.GetString() : throw new JsonException("An entity is written as its id");
            try { return (T)Activator.CreateInstance(typeof(T), id)!; }
            catch (TargetInvocationException error) when (error.InnerException is ArgumentException) { throw new JsonException(error.InnerException.Message, error.InnerException); }
        }
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value.EntityId);
    }
}
