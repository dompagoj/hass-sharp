using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Xunit;

namespace HassSharp.Editor.Tests;

public class DiagnosticsProviderTests
{
    [Fact]
    public void Configuration_accepts_mixed_typed_entities_and_rejects_strings()
    {
        var provider = new DiagnosticsProvider();
        provider.GenerateHassEntities(["binary_sensor.hall_motion", "light.lamp"]);
        const string source = "class Test : Automation { public override AutomationConfiguration Configure() => new(new AutomationAction(nameof(Run), Entities: [Entities.BinarySensor.HallMotion, Entities.Light.Lamp])); public async Task Run() { if (Entity(Entities.BinarySensor.HallMotion).isOn()) await Entity(Entities.Light.Lamp).TurnOn().RunAsync(); } }";
        Assert.DoesNotContain(provider.GetDiagnostics(source), d => d.Severity == 3);
        Assert.Contains(provider.GetDiagnostics(source.Replace("Entities.BinarySensor.HallMotion, Entities.Light.Lamp", "\"light.lamp\"")), d => d.Severity == 3);
    }

    [Fact]
    public void Generated_names_are_valid_unique_and_deterministic()
    {
        var provider = new DiagnosticsProvider();
        string[] ids = ["light.a_b", "light.a-b", "light.a b", "light.light", "entities.foo", "light.123", "light.!", "light.a_b"];
        provider.GenerateHassEntities(ids);
        var source = provider.HassEntitiesSyntaxTree().ToString();
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Names", [provider.HassEntitiesSyntaxTree()], ((Microsoft.CodeAnalysis.Project)Workspace(provider).CurrentSolution.Projects.Single()).MetadataReferences, new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(TestContext.Current.CancellationToken), d => d.Severity == DiagnosticSeverity.Error);
        provider.GenerateHassEntities(ids.Reverse().ToArray());
        Assert.Equal(source, provider.HassEntitiesSyntaxTree().ToString());
    }

    [Fact]
    public void Entity_names_keep_ownership_across_replacement_removal_and_restart()
    {
        var provider = new DiagnosticsProvider();
        foreach (var (canonical, alias) in new[] { ("light.a_b", "light.a-b"), ("custom_domain.one", "custom-domain.one"), ("sun.sun", "sun.sun_entity") })
        {
            provider.GenerateHassEntities([canonical]);
            var expression = provider.EntityReferenceExpression(canonical);
            provider.GenerateHassEntities([alias]);
            Assert.NotEqual(expression, provider.EntityReferenceExpression(alias));
            Assert.Contains(provider.GetDiagnostics($"class Test {{ object x = {expression}; }}"), d => d.Severity == 3);
            var aliasExpression = provider.EntityReferenceExpression(alias);
            provider.GenerateHassEntities([canonical, alias]);
            Assert.Equal(expression, provider.EntityReferenceExpression(canonical));
            Assert.Equal(aliasExpression, provider.EntityReferenceExpression(alias));
            provider.GenerateHassEntities([alias]);
            Assert.Equal(aliasExpression, provider.EntityReferenceExpression(alias));
            var restarted = new DiagnosticsProvider(); restarted.GenerateHassEntities([alias]);
            Assert.Equal(aliasExpression, restarted.EntityReferenceExpression(alias));
        }
        provider.GenerateHassEntities(["binary_sensor.hall_motion", "sun.sun"]);
        Assert.Equal("Entities.BinarySensor.HallMotion", provider.EntityReferenceExpression("binary_sensor.hall_motion"));
        Assert.Equal("Entities.Sun.SunEntity", provider.EntityReferenceExpression("sun.sun"));
    }

    static AdhocWorkspace Workspace(DiagnosticsProvider provider) =>
        (AdhocWorkspace)typeof(DiagnosticsProvider).GetField("_workspace", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(provider)!;

    [Fact]
    public async Task Entity_refresh_replaces_old_symbols_in_completions()
    {
        var provider = new DiagnosticsProvider();
        provider.GenerateHassEntities(["light.old_lamp"]);
        const string source = "class Test { void Run() { Entities.Light. } }";
        var position = source.IndexOf(". }") + 1;
        Assert.Contains(await provider.GetCompletions(source, position, TestContext.Current.CancellationToken), c => c.DisplayText == "OldLamp");
        Assert.DoesNotContain(provider.GetDiagnostics("class Test { object x = Entities.Light.OldLamp; }"), d => d.Severity == 3);
        provider.GenerateHassEntities(["light.new_lamp"]);
        Assert.Contains(provider.GetDiagnostics("class Test { object x = Entities.Light.OldLamp; }"), d => d.Severity == 3);
        Assert.DoesNotContain(provider.GetDiagnostics("class Test { object x = Entities.Light.NewLamp; }"), d => d.Severity == 3);
        var completions = await provider.GetCompletions(source, position, TestContext.Current.CancellationToken);
        Assert.Contains(completions, c => c.DisplayText == "NewLamp");
        Assert.DoesNotContain(completions, c => c.DisplayText == "OldLamp");
        Assert.Single(Workspace(provider).CurrentSolution.Projects.Single().Documents);
    }

    [Fact]
    public void Empty_hover_does_not_leave_temporary_documents()
    {
        var provider = new DiagnosticsProvider();
        provider.GenerateHassEntities([]);
        for (var i = 0; i < 3; i++) Assert.Null(provider.GetHover("\n\n", 1));
        Assert.Single(Workspace(provider).CurrentSolution.Projects.Single().Documents);
    }

    [Fact]
    public async Task Invalid_completion_position_does_not_leave_temporary_documents()
    {
        var provider = new DiagnosticsProvider();
        provider.GenerateHassEntities([]);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => provider.GetCompletions("", 10, TestContext.Current.CancellationToken));
        Assert.Single(Workspace(provider).CurrentSolution.Projects.Single().Documents);
    }
    [Fact]
    public async Task Completion_returns_Roslyn_insertion_and_source_relative_replacement_span()
    {
        var provider = new DiagnosticsProvider();
        provider.GenerateHassEntities([]);
        const string source = "class Test { void Run() { Lis } }";
        var position = source.IndexOf("Lis") + 3;
        var item = (await provider.GetCompletions(source, position, TestContext.Current.CancellationToken)).Single(c => c.DisplayText == "List");
        var json = JsonSerializer.SerializeToElement(item);
        Assert.True(json.TryGetProperty("TextEdit", out var edit), "Completion must expose the Roslyn text edit");
        Assert.Equal(source.IndexOf("Lis"), edit.GetProperty("Start").GetInt32());
        Assert.Equal(3, edit.GetProperty("Length").GetInt32());
        Assert.Equal("List", edit.GetProperty("NewText").GetString());
    }

    [Fact]
    public void Diagnostics_only_report_the_user_document()
    {
        var provider = new DiagnosticsProvider();
        provider.GenerateHassEntities(["light.one", "light.two", "light.bad name"]);
        Assert.Empty(provider.GetDiagnostics("class Test {}"));
    }

    [Fact]
    public async Task Completion_filters_the_identifier_after_punctuation()
    {
        var provider = new DiagnosticsProvider();
        provider.GenerateHassEntities(["light.lamp"]);
        const string source = "class Test { void Run() { var x=Ent } }";
        var position = source.IndexOf("Ent") + 3;
        Assert.Contains(await provider.GetCompletions(source, position, TestContext.Current.CancellationToken),
            c => c.DisplayText == "Entities");
    }

    [Fact]
    public async Task Namespace_completion_keeps_namespace_tag_and_source_edit()
    {
        var provider = new DiagnosticsProvider();
        provider.GenerateHassEntities([]);
        const string source = "using System.Tex;\nclass Test {}";
        var position = source.IndexOf("Tex") + 3;
        var item = Assert.Single(await provider.GetCompletions(source, position, TestContext.Current.CancellationToken), c => c.DisplayText == "Text");
        Assert.Contains("Namespace", item.Tags);
        Assert.Equal(source.IndexOf("Tex"), item.TextEdit.Start);
        Assert.Equal(3, item.TextEdit.Length);
        Assert.Equal("Text", item.TextEdit.NewText);
    }

    [Fact]
    public async Task Completion_replaces_the_entire_identifier_when_cursor_is_in_the_middle()
    {
        var provider = new DiagnosticsProvider();
        provider.GenerateHassEntities([]);
        const string source = "class Test { void Run() { ListWrong value; } }";
        var position = source.IndexOf("ListWrong") + 3;
        var item = Assert.Single(await provider.GetCompletions(source, position, TestContext.Current.CancellationToken), c => c.DisplayText == "List");
        Assert.Equal(source.IndexOf("ListWrong"), item.TextEdit.Start);
        Assert.Equal("ListWrong".Length, item.TextEdit.Length);
        Assert.Equal("List", item.TextEdit.NewText);
    }

}
