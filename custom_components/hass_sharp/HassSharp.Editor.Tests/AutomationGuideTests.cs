using System.Text.RegularExpressions;
using Xunit;

namespace HassSharp.Editor.Tests;

public class AutomationGuideTests
{
    [Fact]
    public void Csharp_help_examples_compile_against_the_current_api()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "docs"))) root = root.Parent;
        Assert.NotNull(root);
        var guide = File.ReadAllText(Path.Combine(root.FullName, "docs", "writing-csharp-automations.md"));
        var snippets = Regex.Matches(guide, "```csharp\\r?\\n([\\s\\S]*?)```");
        Assert.NotEmpty(snippets);
        var provider = new DiagnosticsProvider();
        provider.GenerateHassEntities(["light.hall", "binary_sensor.hall_motion"]);
        foreach (Match snippet in snippets)
        {
            var errors = provider.GetDiagnostics(snippet.Groups[1].Value).Where(d => d.Severity == 3);
            Assert.True(!errors.Any(), string.Join("\n", errors.Select(d => d.Message)));
        }
    }
}
