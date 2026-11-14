using System.Reflection;
using Microsoft.CodeAnalysis;
using Xunit;

namespace HassSharp.Editor.Tests;

public class DiagnosticsProviderTests
{
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
            Assert.False(Compiles(provider, $"class Test {{ object x = {expression}; }}"));
            var aliasExpression = provider.EntityReferenceExpression(alias);
            Assert.True(Compiles(provider, $"class Test {{ object x = {aliasExpression}; }}"));
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

    // The same inputs a compiled automation gets: global usings, the generated entities and the workspace references.
    static bool Compiles(DiagnosticsProvider provider, string source) =>
        !Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Check",
                [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(DiagnosticsProvider.GlobalUsings + source), provider.HassEntitiesSyntaxTree()],
                Workspace(provider).CurrentSolution.Projects.Single().MetadataReferences,
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error);

    static AdhocWorkspace Workspace(DiagnosticsProvider provider) =>
        (AdhocWorkspace)typeof(DiagnosticsProvider).GetField("_workspace", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(provider)!;
}
