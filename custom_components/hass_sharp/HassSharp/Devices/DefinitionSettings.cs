using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>Sets the values a C# node is given for its class's <c>[Setting]</c> properties on the component made from it.</summary>
internal static class DefinitionSettings
{
    // The saved check of the settings can be stale once a library changes, so a setting that no longer fits fails here
    // rather than leaving the definition to run on defaults.
    // A required setting that has no value (a Setting wired to it whose definition setting has none) fails before any
    // value is set, so that no setter and no Setup() runs on a class that cannot start.
    /// <param name="name">What a failure starts with: the definition's name and the node's.</param>
    /// <param name="unset">The labels of the class's required settings that have no value (<see cref="DefinitionComponent.Unset"/>).</param>
    internal static void Apply(string name, IReadOnlyList<DefinitionSetting> settings, IReadOnlyList<string> unset, SharpComponent root)
    {
        if (unset.Count > 0) throw new InvalidOperationException($"{name}: the setting {unset[0]} has no value");
        var type = root.GetType();
        foreach (var setting in settings)
        {
            if (setting.Value == null) continue;
            var property = SettingProperty(type, setting.Key)
                ?? throw new InvalidOperationException($"{name}: {type.Name} has no setting {setting.Key}");
            if (!TryConvert(setting.Value, property.PropertyType, out var value))
                throw new InvalidOperationException($"{name}: the saved value of setting {setting.Key} does not fit its type {property.PropertyType.Name}");
            // The value is not quoted: it may be a secret.
            if (Range(property) is { } range && value is int or long or double && !DefinitionGenerator.Within(range, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)))
                throw new InvalidOperationException($"{name}: the saved value of setting {range.Label} is not {DefinitionGenerator.Phrase(range)}");
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

    // The range the property's [Setting] declares, as the node's signature gives it (an int's own bounds where it declares
    // one side only, or one beyond them); null when it declares none.
    static EntitySettingDescriptor? Range(PropertyInfo property)
    {
        var attribute = property.GetCustomAttribute<SettingAttribute>(false)!;
        if (double.IsNaN(attribute.Minimum) && double.IsNaN(attribute.Maximum)) return null;
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var whole = type == typeof(int) || type == typeof(long);
        var isInt = type == typeof(int);
        return new(property.Name, string.IsNullOrWhiteSpace(attribute.Label) ? property.Name : attribute.Label, null, whole ? "integer" : "number", attribute.Secret, [])
        {
            Minimum = double.IsNaN(attribute.Minimum) ? isInt ? int.MinValue : null : isInt ? Math.Max(attribute.Minimum, int.MinValue) : attribute.Minimum,
            Maximum = double.IsNaN(attribute.Maximum) ? isInt ? int.MaxValue : null : isInt ? Math.Min(attribute.Maximum, int.MaxValue) : attribute.Maximum,
        };
    }

    // An entity handle is made from its id by its class's (string) constructor, as EntityJsonConverter makes one: an id the
    // constructor refuses (no id, or one of another domain) does not fit.
    static bool TryConvert(JsonNode node, Type type, out object? value)
    {
        value = null;
        var target = Nullable.GetUnderlyingType(type) ?? type;
        using var document = JsonDocument.Parse(node.ToJsonString());
        var json = document.RootElement;
        switch (json.ValueKind)
        {
            case JsonValueKind.String when target == typeof(string): value = json.GetString(); return true;
            case JsonValueKind.String when typeof(Entities.Entity).IsAssignableFrom(target):
                try { value = Activator.CreateInstance(target, json.GetString()); return true; }
                catch (TargetInvocationException) { return false; }
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
