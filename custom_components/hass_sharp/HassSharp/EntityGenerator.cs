using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace HassSharp;

class EntityGenerator
{
    public SyntaxTree GenerateEntities(string[] hassEntityIds)
    {
        var names = ResolveNames(hassEntityIds);
        var sb = new StringBuilder("namespace HassSharp;\npublic static class Entities\n{\n");
        foreach (var group in names.GroupBy(x => x.Value.Domain))
        {
            sb.AppendLine($"    public static class {group.Key}\n    {{");
            foreach (var item in group)
                sb.AppendLine($"        public static {GetEntityRefClassType(item.Key.Split('.')[0])} {item.Value.Property} => new({Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(item.Key, true)});");
            sb.AppendLine("    }");
        }
        sb.AppendLine("}");
        return CSharpSyntaxTree.ParseText(sb.ToString());
    }

    internal static Dictionary<string, (string Domain, string Property)> ResolveNames(IEnumerable<string> entityIds)
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        var groups = entityIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Where(id => id.Split('.') is [var domain, var name] && domain.Length > 0 && name.Length > 0)
            .GroupBy(id => id.Split('.')[0]);
        foreach (var group in groups)
        {
            var domainBase = Sanitize(group.Key);
            var domain = OwnedName(group.Key, domainBase, group.Key == CanonicalIdentity(domainBase) && domainBase != "Entities");
            foreach (var id in group)
            {
                var objectId = id.Split('.')[1];
                var propertyBase = Sanitize(objectId);
                var canonical = objectId == CanonicalIdentity(propertyBase);
                if (propertyBase == domain) propertyBase += "Entity";
                else if (propertyBase == domain + "Entity") canonical = false;
                result[id] = (domain, OwnedName(id, propertyBase, canonical));
            }
        }
        return result;
    }

    // Plain names belong to exactly one canonical snake_case identity, independent
    // of the live catalog. Aliases always retain their full identity in the suffix.
    static string OwnedName(string identity, string name, bool canonical) => canonical
        ? name : name + "_" + Convert.ToHexString(Encoding.UTF8.GetBytes(identity));

    static string CanonicalIdentity(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.TrimStart('_'))
        {
            if (char.IsUpper(c) && sb.Length > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    static string Sanitize(string name)
    {
        var sb = new StringBuilder();
        var upper = true;
        foreach (var c in name)
        {
            if (!SyntaxFacts.IsIdentifierPartCharacter(c) || c == '_') { upper = true; continue; }
            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        if (sb.Length == 0) sb.Append("Entity");
        if (!SyntaxFacts.IsIdentifierStartCharacter(sb[0])) sb.Insert(0, '_');
        var value = sb.ToString();
        return SyntaxFacts.GetKeywordKind(value) != SyntaxKind.None ? "_" + value : value;
    }

    static readonly Dictionary<string, string> HassDomainToEntityRefClass = new()
    {
        { "ai_task", nameof(HaAiTask) },
        { "air_quality", nameof(HaAirQuality) },
        { "alarm_control_panel", nameof(HaAlarmControlPanel) },
        { "alert", nameof(HaAlert) },
        { "assist_satellite", nameof(HaAssistSatellite) },
        { "automation", nameof(HaAutomation) },
        { "binary_sensor", nameof(HaBinarySensor) },
        { "button", nameof(HaButton) },
        { "calendar", nameof(HaCalendar) },
        { "camera", nameof(HaCamera) },
        { "climate", nameof(HaClimate) },
        { "conversation", nameof(HaConversation) },
        { "counter", nameof(HaCounter) },
        { "cover", nameof(HaCover) },
        { "date", nameof(HaDate) },
        { "datetime", nameof(HaDateTime) },
        { "device_tracker", nameof(HaDeviceTracker) },
        { "event", nameof(HaEvent) },
        { "fan", nameof(HaFan) },
        { "group", nameof(HaGroup) },
        { "geo_location", nameof(HaGeoLocation) },
        { "humidifier", nameof(HaHumidifier) },
        { "image", nameof(HaImage) },
        { "image_processing", nameof(HaImageProcessing) },
        { "input_boolean", nameof(HaInputBoolean) },
        { "input_button", nameof(HaInputButton) },
        { "input_datetime", nameof(HaInputDateTime) },
        { "input_number", nameof(HaInputNumber) },
        { "input_select", nameof(HaInputSelect) },
        { "input_text", nameof(HaInputText) },
        { "lawn_mower", nameof(HaLawnMower) },
        { "light", nameof(HaLight) },
        { "lock", nameof(HaLock) },
        { "media_player", nameof(HaMediaPlayer) },
        { "notify", nameof(HaNotify) },
        { "number", nameof(HaNumber) },
        { "person", nameof(HaPerson) },
        { "plant", nameof(HaPlant) },
        { "remote", nameof(HaRemote) },
        { "scene", nameof(HaScene) },
        { "schedule", nameof(HaSchedule) },
        { "script", nameof(HaScript) },
        { "select", nameof(HaSelect) },
        { "sensor", nameof(HaSensor) },
        { "siren", nameof(HaSiren) },
        { "stt", nameof(HaStt) },
        { "sun", nameof(HaSun) },
        { "switch", nameof(HaSwitch) },
        { "tag", nameof(HaTag) },
        { "text", nameof(HaText) },
        { "time", nameof(HaTime) },
        { "timer", nameof(HaTimer) },
        { "todo", nameof(HaTodo) },
        { "tts", nameof(HaTts) },
        { "update", nameof(HaUpdate) },
        { "vacuum", nameof(HaVacuum) },
        { "valve", nameof(HaValve) },
        { "wake_word", nameof(HaWakeWord) },
        { "water_heater", nameof(HaWaterHeater) },
        { "weather", nameof(HaWeather) },
        { "zone", nameof(HaZone) },
    };

    string GetEntityRefClassType(string hassDomain)
    {
        var entityRefWrapperClassName = nameof(EntityRefWrapper<>);
        if (HassDomainToEntityRefClass.TryGetValue(hassDomain, out var entityRefClass))
            return $"{entityRefWrapperClassName}<{entityRefClass}>";

        return $"{entityRefWrapperClassName}<{nameof(HaUnknown)}>";
    }
}
