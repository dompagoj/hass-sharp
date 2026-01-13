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
        { "binary_sensor", nameof(HaBinarySensor) },
        { "switch", nameof(HaSwitch) },
        { "light", nameof(HaLight) },
        { "input_number", nameof(HaInputNumber) },
        { "sensor", nameof(HaSensor) },
        { "input_button", nameof(HaInputButton) },
        { "media_player", nameof(HaMediaPlayer) },
        { "sun", nameof(HaSun) },
    };

    string GetEntityRefClassType(string hassDomain)
    {
        var entityRefWrapperClassName = nameof(EntityRefWrapper<>);
        if (HassDomainToEntityRefClass.TryGetValue(hassDomain, out var entityRefClass))
            return $"{entityRefWrapperClassName}<{entityRefClass}>";

        return $"{entityRefWrapperClassName}<{nameof(HaUnknown)}>";
    }
}