using System.Text;

namespace HassSharp;

public static class EntityGenerator
{
    public static string Generate(string[] entityIds)
    {
        var sb = new StringBuilder();
        sb.AppendLine("namespace HassSharp;");
        sb.AppendLine();
        sb.AppendLine("public static class Entities");
        sb.AppendLine("{");

        var domains = entityIds
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
                var objectId = Sanitize(entityIdParts[1]);

                // If property name matches class name, append "Entity" to property
                if (objectId == domainName)
                {
                    objectId += "Entity";
                }

                sb.AppendLine($"        public static string {objectId} => \"{originalId}\";");
            }

            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        return sb.ToString();
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
}
