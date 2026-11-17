using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>Sets the values a definition stores for its <c>[Setting]</c> properties on the component it builds.</summary>
internal static class DefinitionSettings
{
    // The saved check of the settings can be stale once a library changes, so a setting that no longer fits fails here
    // rather than leaving the definition to run on defaults.
    /// <param name="name">The definition's name, which a failure starts with.</param>
    internal static void Apply(string name, IReadOnlyList<DefinitionSetting> settings, SharpComponent root)
    {
        var type = root.GetType();
        foreach (var setting in settings)
        {
            if (setting.Value == null) continue;
            var property = SettingProperty(type, setting.Key)
                ?? throw new InvalidOperationException($"{name}: {type.Name} has no setting {setting.Key}");
            if (!TryConvert(setting.Value, property.PropertyType, out var value))
                throw new InvalidOperationException($"{name}: the saved value of setting {setting.Key} does not fit its type {property.PropertyType.Name}");
            property.SetMethod!.Invoke(root, BindingFlags.DoNotWrapExceptions, null, [value], null);
        }
    }

    // The public settable [Setting] property of that name, the most derived one first.
    static PropertyInfo? SettingProperty(Type type, string key)
    {
        for (var current = type; current != null; current = current.BaseType)
            foreach (var property in current.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (property.Name == key && property.GetIndexParameters().Length == 0 && property.SetMethod is { IsPublic: true }
                    && property.IsDefined(typeof(SettingAttribute), false))
                    return property;
        return null;
    }

    static bool TryConvert(JsonNode node, Type type, out object? value)
    {
        value = null;
        var target = Nullable.GetUnderlyingType(type) ?? type;
        using var document = JsonDocument.Parse(node.ToJsonString());
        var json = document.RootElement;
        switch (json.ValueKind)
        {
            case JsonValueKind.String when target == typeof(string): value = json.GetString(); return true;
            case JsonValueKind.String when target.IsEnum && Enum.GetNames(target).Contains(json.GetString(), StringComparer.Ordinal):
                value = Enum.Parse(target, json.GetString()!); return true;
            case JsonValueKind.True or JsonValueKind.False when target == typeof(bool): value = json.GetBoolean(); return true;
            case JsonValueKind.Number when target == typeof(int) && json.TryGetInt32(out var whole): value = whole; return true;
            case JsonValueKind.Number when target == typeof(long) && json.TryGetInt64(out var large): value = large; return true;
            case JsonValueKind.Number when target == typeof(double) && json.TryGetDouble(out var number): value = number; return true;
            default: return false;
        }
    }
}
