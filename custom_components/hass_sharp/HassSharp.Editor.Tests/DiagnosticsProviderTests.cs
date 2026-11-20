using System.Reflection;
using Microsoft.CodeAnalysis;
using Xunit;

namespace HassSharp.Editor.Tests;

public class DiagnosticsProviderTests
{
    static string Fixture(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "tests", "fixtures", name))) return File.ReadAllText(Path.Combine(directory.FullName, "tests", "fixtures", name));
        throw new FileNotFoundException(name);
    }

    [Fact]
    public void Generated_service_calls_compile_without_diagnostics_and_are_deterministic()
    {
        var provider = new DiagnosticsProvider();
        Assert.Empty(Diagnostics(provider, "class Empty { }"));
        provider.UseServiceSchema(ServiceSchemaCatalog.Parse(Fixture("ha-service-descriptions.json"), _ => { }));
        var source = provider.ServicesSyntaxTree().ToString();
        Assert.Contains("public global::HassSharp.ServiceCalls.LightServices Light", source);
        // A C# entity's or device's Services has each domain too, and the generated classes hold the caller interface.
        Assert.Contains("extension(global::HassSharp.ComponentServices services)", source);
        Assert.Contains("public global::HassSharp.ServiceCalls.LightServices Light => new(services);", source);
        Assert.Contains("internal LightServices(global::HassSharp.IServiceCaller context) => __context = context;", source);
        Assert.Empty(Diagnostics(provider, "class Empty { }"));
        var again = new DiagnosticsProvider();
        again.UseServiceSchema(ServiceSchemaCatalog.Parse(Fixture("ha-service-descriptions.json"), _ => { }));
        Assert.Equal(source, again.ServicesSyntaxTree().ToString());
    }

    [Fact]
    public void There_is_no_per_entity_tree()
    {
        var provider = new DiagnosticsProvider();
        Assert.Contains(Diagnostics(provider, "class Test { object x = Entities.Light.Kitchen; }"), d => d.Id == "CS0103" && d.GetMessage().Contains("Entities"));
        Assert.Contains(Diagnostics(provider, "class Test { object x = new HaLight(\"light.kitchen\"); }"), d => d.Id == "CS0246");
    }

    // The same inputs a compiled automation gets: global usings, the generated service calls and the workspace references.
    static Diagnostic[] Diagnostics(DiagnosticsProvider provider, string source) =>
        Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Check",
                [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(DiagnosticsProvider.GlobalUsings + source), provider.ServicesSyntaxTree()],
                Workspace(provider).CurrentSolution.Projects.Single().MetadataReferences,
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable))
            .GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning).ToArray();

    static AdhocWorkspace Workspace(DiagnosticsProvider provider) =>
        (AdhocWorkspace)typeof(DiagnosticsProvider).GetField("_workspace", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(provider)!;
}
