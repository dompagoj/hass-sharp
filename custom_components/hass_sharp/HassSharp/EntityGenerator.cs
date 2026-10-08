using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace HassSharp;

class EntityGenerator
{
    public string[]? HassEntityIds { get; set; }

    public SyntaxTree GenerateEntities(string[] hassEntityIds)
    {
        HassEntityIds = hassEntityIds;
        var sb = new StringBuilder();
        sb.AppendLine("namespace HassSharp;");
        sb.AppendLine();
        sb.AppendLine("public static class Entities");
        sb.AppendLine("{");

        var domains = hassEntityIds
            .Select(id => id.Split('.'))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0]);

        foreach (var domainGroup in domains)
        {
            var domainName = Sanitize(domainGroup.Key);
            sb.AppendLine($"    public static class {domainName}");
            sb.AppendLine("    {");

            foreach (var entityIdParts in domainGroup)
            {
                var originalId = string.Join(".", entityIdParts);
                var propertyName = Sanitize(entityIdParts[1]);

                // If property name matches class name, append "Entity" to property
                if (propertyName == domainName)
                {
                    propertyName += "Entity";
                }

                var entityRefClass = GetEntityRefClassType(domainGroup.Key);

                sb.AppendLine(
                    $"        public static {entityRefClass} {propertyName} => new(\"{originalId}\");"
                );
            }

            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        var source = sb.ToString();

        return CSharpSyntaxTree.ParseText(source);

        // if (EntitiesDocumentId != null)
        // {
        //     var solution = _workspace.CurrentSolution.WithDocumentText(EntitiesDocumentId, SourceText.From(source));
        //     _workspace.TryApplyChanges(solution);
        // }
        // else
        // {
        //     EntitiesDocumentId = DocumentId.CreateNewId(_baseProject.Id);
        //     var solution = _workspace.CurrentSolution.AddDocument(EntitiesDocumentId, "Entities.g.cs", source);
        //     _workspace.TryApplyChanges(solution);
        // }
    }

    static string Sanitize(string name)
    {
        var parts = name.Split('_', '.', '-');
        var pascal = string.Join("", parts.Select(p =>
            p.Length > 0 ? char.ToUpper(p[0]) + p.Substring(1) : ""));

        if (pascal.Length > 0 && char.IsDigit(pascal[0]))
            pascal = "_" + pascal;

        return pascal;
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
