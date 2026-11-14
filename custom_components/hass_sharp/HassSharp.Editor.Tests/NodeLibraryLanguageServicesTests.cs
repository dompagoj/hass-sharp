using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace HassSharp.Editor.Tests;

public class NodeLibraryLanguageServicesTests
{
    static async Task<JsonNode> Request(string operation, NodeLibrarySource[] sources, string id, int position=0)
    {
        var provider = new DiagnosticsProvider();
        var service = new NodeLibraryLanguageServices(provider);
        return JsonSerializer.SerializeToNode(await service.RequestAsync(operation,sources,id,position,TestContext.Current.CancellationToken), VisualStore.Json)!;
    }
    [Fact]
    public async Task Completion_uses_typed_entity_members_and_unwrapped_utf16_edits()
    {
        const string source="// 😀\npublic static class Nodes { public static void Read(EntityStateSnapshot state) { state.Sta } }";
        var position=source.IndexOf("state.Sta")+"state.Sta".Length;
        var result=await Request("completion",[new("main",source,1)],"main",position);
        var item=Assert.Single(result.AsArray(),i=>i!["displayText"]!.GetValue<string>()=="State")!;
        Assert.Equal(position-3,item["textEdit"]!["start"]!.GetValue<int>());
        Assert.Equal(3,item["textEdit"]!["length"]!.GetValue<int>());
    }
    [Fact]
    public async Task Cross_file_completion_hover_definition_and_nullable_generic_signature()
    {
        const string helper="public record Reading(string? Label); public static class Factory { public static List<Reading?> Make(string? label, int count=1) => []; }";
        const string source="public class Main { void Run() { var x = Factory.Make(null); var y = x[0]; y.La } }";
        NodeLibrarySource[] sources=[new("helper",helper,1),new("main",source,1)];
        Assert.Contains((await Request("completion",sources,"main",source.IndexOf("y.La")+4)).AsArray(),i=>i!["displayText"]!.GetValue<string>()=="Label");
        var hover=await Request("hover",sources,"main",source.IndexOf("Make")+1);
        Assert.Contains("Make",hover.ToJsonString());
        var definition=await Request("definition",sources,"main",source.IndexOf("Make")+1);
        Assert.Equal("helper",definition[0]!["id"]!.GetValue<string>());
        Assert.Equal(helper.IndexOf("Make"),definition[0]!["range"]!["start"]!.GetValue<int>());
        var signature=await Request("signature",sources,"main",source.IndexOf("null")+2);
        Assert.Contains("string?",signature.ToJsonString()); Assert.Contains("Reading",signature.ToJsonString());
    }
    static async Task<JsonNode> Custom(string operation, NodeLibrarySource[] sources, string source, int position=0)
    {
        var provider = new DiagnosticsProvider();
        var service = new NodeLibraryLanguageServices(provider);
        return JsonSerializer.SerializeToNode(await service.RequestAsync(operation,sources,new CustomNodeSource("check",source),position,TestContext.Current.CancellationToken), VisualStore.Json)!;
    }
    [Fact]
    public async Task Custom_node_code_gets_completion_hover_definition_and_format_beside_the_libraries_with_its_usings()
    {
        // using lines at the top of a Custom C# node's code sit inside the NodeLib.Custom_… namespace the code is placed in
        // (C# allows using directives at the start of a namespace); NodeContext and the libraries are in reach.
        const string helper="public static class Rooms { public static int Count(string area) => 1; }";
        const string source="using System.Globalization;\npublic static class CustomNode\n{\n    [Node(\"Check\")]\n    public static int Run(NodeContext context, string area) { context.Cancel; return Rooms.Count(area); }\n}";
        NodeLibrarySource[] sources=[new("rooms",helper,1)];
        var position=source.IndexOf("context.Cancel")+"context.Cancel".Length;
        var item=Assert.Single((await Custom("completion",sources,source,position)).AsArray(),i=>i!["displayText"]!.GetValue<string>()=="CancellationToken")!;
        Assert.Equal(position-6,item["textEdit"]!["start"]!.GetValue<int>());
        Assert.Contains("Count",(await Custom("hover",sources,source,source.IndexOf("Count(area)")+1)).ToJsonString());
        var definition=await Custom("definition",sources,source,source.IndexOf("Count(area)")+1);
        Assert.Equal("rooms",definition[0]!["id"]!.GetValue<string>());
        Assert.Equal(helper.IndexOf("Count"),definition[0]!["range"]!["start"]!.GetValue<int>());
        var own=await Custom("definition",sources,source,source.IndexOf("area)")+1);
        Assert.Equal(NodeLibraryLanguageServices.CustomDocumentId,own[0]!["id"]!.GetValue<string>());
        Assert.Equal(source.IndexOf("area"),own[0]!["range"]!["start"]!.GetValue<int>());
        // Signature help on a library method called from the code.
        var signature=await Custom("signature",sources,source,source.IndexOf("Count(area)")+"Count(".Length);
        Assert.Contains("area",signature.ToJsonString()); Assert.Equal(0,signature["activeParameter"]!.GetValue<int>());
        Assert.Contains("Count",signature["signatures"]![0]!["label"]!.GetValue<string>());
        var formatted=(await Custom("format",sources,"public static class CustomNode{[Node(\"Check\")]public static int Run(int x)=>x;}")).GetValue<string>();
        Assert.DoesNotContain("namespace",formatted); Assert.StartsWith("public static class CustomNode",formatted);
    }
    [Fact]
    public async Task Concurrent_unsaved_snapshots_are_isolated_and_include_generated_symbols()
    {
        const string source="public class Main { void Run() { Other. } }";
        var position=source.IndexOf("Other.")+6;
        var tasks=new[]{"First", "Second"}.Select(name=>Request("completion",[new("main",source,1),new("other",$"public static class Other {{ public static int {name}; }}",1)],"main",position)).ToArray();
        var results=await Task.WhenAll(tasks);
        Assert.Contains("First",results[0].ToJsonString()); Assert.DoesNotContain("Second",results[0].ToJsonString());
        Assert.Contains("Second",results[1].ToJsonString()); Assert.DoesNotContain("First",results[1].ToJsonString());
    }
    [Fact]
    public async Task Completion_no_longer_offers_the_typed_entities()
    {
        const string source="public class Main { void Run() { Entit } }";
        var items=(await Request("completion",[new("main",source,1)],"main",source.IndexOf("Entit")+5)).AsArray();
        Assert.DoesNotContain(items,i=>i!["displayText"]!.GetValue<string>() is "Entities" or "EntityRef" or "HassServices");
        Assert.Contains(items,i=>i!["displayText"]!.GetValue<string>()=="EntityStateSnapshot");
    }
    static async Task<JsonNode> WithServices(string operation, string source, int position)
    {
        var provider = new DiagnosticsProvider();
        provider.UseServiceSchema(ServiceSchemaCatalog.Parse("""
            {"light":{"turn_on":{"name":"Turn on","description":"Turns on one or more lights.","target":{"entity":{"domain":"light"}},
              "fields":{"brightness_pct":{"name":"Brightness","description":"Percentage of full brightness.","selector":{"number":{"min":0,"max":100,"unit_of_measurement":"%"}}}}}}}
            """, _ => { }));
        return JsonSerializer.SerializeToNode(await new NodeLibraryLanguageServices(provider).RequestAsync(operation,[],new CustomNodeSource("dim",source),position,TestContext.Current.CancellationToken), VisualStore.Json)!;
    }
    [Fact]
    public async Task Custom_node_code_completes_and_documents_the_generated_service_calls()
    {
        const string source="public static class CustomNode\n{\n    [Node(\"Dim\")]\n    public static async Task Dim(NodeContext context, string light) { await context.Services.Light.TurnOn(light, brightnessPct: 30); }\n}";
        Assert.Contains((await WithServices("completion",source,source.IndexOf("Services")+3)).AsArray(),i=>i!["displayText"]!.GetValue<string>()=="Services");
        Assert.Contains((await WithServices("completion",source,source.IndexOf("Light.")+6)).AsArray(),i=>i!["displayText"]!.GetValue<string>()=="TurnOn");
        Assert.Contains((await WithServices("completion",source,source.IndexOf("Services.")+9)).AsArray(),i=>i!["displayText"]!.GetValue<string>()=="Light");
        var hover=(await WithServices("hover",source,source.IndexOf("TurnOn")+1)).ToJsonString();
        Assert.Contains("TurnOn",hover); Assert.Contains("Turn on (light.turn_on). Turns on one or more lights.",hover);
        var signature=(await WithServices("signature",source,source.IndexOf("brightnessPct")+1)).ToJsonString();
        Assert.Contains("brightnessPct",signature); Assert.Contains("From 0 to 100 %",signature);
    }
    [Fact]
    public async Task Named_arguments_select_the_declared_parameter_in_reordered_calls()
    {
        const string source="public static class Factory { public static string Make(string? label, int count=1) => label ?? \"\"; public static void Run() { Make(count: 1, label: null); } }";
        NodeLibrarySource[] sources=[new("main",source,1)];
        var count=await Request("signature",sources,"main",source.IndexOf("count: 1")+8);
        Assert.Equal(1,count["activeParameter"]!.GetValue<int>());
        var label=await Request("signature",sources,"main",source.IndexOf("label: null")+10);
        Assert.Equal(0,label["activeParameter"]!.GetValue<int>());
        var positional=source.Replace("count: 1, label: null","null, 1");
        var fallback=await Request("signature",[new("main",positional,1)],"main",positional.IndexOf("null, 1")+7);
        Assert.Equal(1,fallback["activeParameter"]!.GetValue<int>());
    }
    [Fact]
    public void Preview_cancellation_prevents_compiling_an_unsaved_workspace()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new NodeLibraryCompiler(new DiagnosticsProvider())
            .Preview([new("main", "public class Main {}", 1)], cancellation.Token));
    }

    [Fact]
    public async Task Format_keeps_top_level_code_at_the_left_margin()
    {
        // The source is wrapped in a namespace to compile; Format must not indent the person's code for it.
        const string source="using System;\npublic static class Nodes\n{\n[Node(\"Echo\")]\npublic static string Echo(string text) => text;\n}";
        const string expected="using System;\npublic static class Nodes\n{\n    [Node(\"Echo\")]\n    public static string Echo(string text) => text;\n}";
        Assert.Equal(expected,(await Request("format",[new("main",source,1)],"main")).GetValue<string>());
        Assert.Equal(expected,(await Custom("format",[],source)).GetValue<string>());
    }
    [Fact]
    public async Task Malformed_source_formats_without_wrapper_and_cancellation_reaches_roslyn()
    {
        const string source="public class C{public void F(){ int x=; }}";
        var format=await Request("format",[new("main",source,1)],"main");
        Assert.DoesNotContain("namespace NodeLib",format.ToJsonString()); Assert.Contains("class C",format.ToJsonString());
        using var cts=new CancellationTokenSource();cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new NodeLibraryLanguageServices(new DiagnosticsProvider()).RequestAsync("completion",[new("main",source,1)],"main",0,cts.Token));
    }
    [Fact]
    public async Task Python_net_extension_methods_are_not_offered_on_node_code_or_libraries()
    {
        // Python.NET extends every object with ToPython and ToPythonAs<T> (namespace Python.Runtime): neither belongs in
        // node code. Completion offers them from that namespace (adding its using line) where code has using lines of its
        // own, and as members once the code imports it.
        var service=new NodeLibraryLanguageServices(new DiagnosticsProvider());
        async Task<JsonNode> Ask(string operation,string code,int position,bool custom=true) =>
            JsonSerializer.SerializeToNode(custom?await service.RequestAsync(operation,[],new CustomNodeSource("check",code),position,TestContext.Current.CancellationToken)
                :await service.RequestAsync(operation,[new("main",code,1)],"main",position,TestContext.Current.CancellationToken),VisualStore.Json)!;
        async Task<string[]> Names(string code,string after,bool custom=true) =>
            (await Ask("completion",code,code.IndexOf(after)+after.Length,custom)).AsArray().Select(i=>i!["displayText"]!.GetValue<string>()).ToArray();
        foreach(var usings in new[]{"","using System.Globalization;\n","using Python.Runtime;\n"})
        {
            var custom=usings+"public static class CustomNode\n{\n    [Node(\"Check\")]\n    public static void Run(NodeContext context) { context.; }\n}";
            var items=await Names(custom,"context.");
            Assert.Contains("CancellationToken",items); Assert.Contains("ToString",items);
            Assert.DoesNotContain(items,i=>i.StartsWith("ToPython",StringComparison.Ordinal));
            Assert.DoesNotContain(await Names(custom.Replace("context.;","context.ToPy;"),"context.ToPy"),i=>i.StartsWith("ToPython",StringComparison.Ordinal));
            var library=usings+"public static class Nodes { public static void Read(EntityStateSnapshot state) { state. } }";
            var libraryItems=await Names(library,"state.",custom:false);
            Assert.Contains("State",libraryItems); Assert.DoesNotContain(libraryItems,i=>i.StartsWith("ToPython",StringComparison.Ordinal));
        }
        // Written out with its namespace imported, signature help and hover do not describe them either.
        const string called="using Python.Runtime;\npublic static class CustomNode\n{\n    [Node(\"Check\")]\n    public static void Run(NodeContext context) { context.ToPython(); }\n}";
        var signature=(await Ask("signature",called,called.IndexOf("ToPython(")+"ToPython(".Length)).ToJsonString();
        Assert.DoesNotContain("ToPython",signature); Assert.DoesNotContain("PyObject",signature);
        var hover=(await Ask("hover",called,called.IndexOf("ToPython")+2)).ToJsonString();
        Assert.DoesNotContain("PyObject",hover); Assert.DoesNotContain("ToPython",hover);
        // An extension method of that name that a library declares is still offered.
        const string own="public static class Mine { public static string ToPython(this NodeContext context) => \"\"; }\npublic static class Nodes { public static void Use(NodeContext context) { context. } }";
        Assert.Contains("ToPython",await Names(own,"context.",custom:false));
    }
}
