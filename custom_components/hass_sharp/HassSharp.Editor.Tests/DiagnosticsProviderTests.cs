using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Xunit;

namespace HassSharp.Editor.Tests;

public class DiagnosticsProviderTests
{
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
        provider.GenerateHassEntities(["light.new_lamp"]);
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

}
