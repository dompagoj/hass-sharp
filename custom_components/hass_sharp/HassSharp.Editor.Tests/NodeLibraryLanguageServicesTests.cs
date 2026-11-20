using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace HassSharp.Editor.Tests;

static class CompletionReply
{
    /// <summary>A completion reply's items (the list is <c>{ items, incomplete }</c>); any other reply as it is.</summary>
    public static JsonNode Items(this JsonNode node) => node is JsonObject reply && reply["items"] is JsonArray items ? items : node;
}

public class NodeLibraryLanguageServicesTests
{
    static async Task<JsonNode> Request(string operation, NodeLibrarySource[] sources, string id, int position=0)
    {
        var provider = new DiagnosticsProvider();
        var service = new NodeLibraryLanguageServices(provider);
        return JsonSerializer.SerializeToNode(await service.RequestAsync(operation,sources,id,position,TestContext.Current.CancellationToken), VisualStore.Json)!.Items();
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
    static async Task<JsonNode> Resolve(JsonNode item, string source, int position, bool custom, NodeLibrarySource[]? sources = null)
    {
        var service = new NodeLibraryLanguageServices(new DiagnosticsProvider());
        var key = new CompletionItemKey(item["displayText"]!.GetValue<string>(), item["displayTextPrefix"]!.GetValue<string>(),
            item["displayTextSuffix"]!.GetValue<string>(), item["inlineDescription"]?.GetValue<string>() ?? "");
        return JsonSerializer.SerializeToNode(custom
            ? await service.ResolveAsync(sources ?? [], new CustomNodeSource("check", source), position, key, TestContext.Current.CancellationToken)
            : await service.ResolveAsync([.. sources ?? [], new("main", source, 1)], "main", position, key, TestContext.Current.CancellationToken), VisualStore.Json)!;
    }
    static async Task<JsonNode> Custom(string operation, NodeLibrarySource[] sources, string source, int position=0)
    {
        var provider = new DiagnosticsProvider();
        var service = new NodeLibraryLanguageServices(provider);
        return JsonSerializer.SerializeToNode(await service.RequestAsync(operation,sources,new CustomNodeSource("check",source),position,TestContext.Current.CancellationToken), VisualStore.Json)!.Items();
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
    public async Task A_class_nodes_code_gets_completion_hover_and_definition_inside_its_class()
    {
        // A C# device's code is one class, in the namespace its node's code is placed in: what it derives from, the
        // libraries and its own members are in reach.
        const string helper="public static class Rooms { public static int Count(string area) => 1; }";
        const string source="public class Plug : SharpDevice\n{\n    [Setting(\"Host\")] public string Host { get; set; } = \"\";\n    public Plug() { Ad; Rooms.Count(Host); }\n}";
        NodeLibrarySource[] sources=[new("rooms",helper,1)];
        var service=new NodeLibraryLanguageServices(new DiagnosticsProvider());
        async Task<JsonNode> Class(string operation,int position)=>JsonSerializer.SerializeToNode(await service.RequestAsync(operation,sources,new CustomNodeSource("plug",source,"csharp_device"),position,TestContext.Current.CancellationToken),VisualStore.Json)!.Items();
        var position=source.IndexOf("{ Ad")+"{ Ad".Length;
        Assert.Contains((await Class("completion",position)).AsArray(),i=>i!["displayText"]!.GetValue<string>()=="Add");
        Assert.Contains("SharpDevice",(await Class("hover",source.IndexOf("SharpDevice")+1)).ToJsonString());
        var definition=await Class("definition",source.IndexOf("Count(Host)")+1);
        Assert.Equal("rooms",definition[0]!["id"]!.GetValue<string>());
        var own=await Class("definition",source.IndexOf("Host)")+1);
        Assert.Equal(NodeLibraryLanguageServices.CustomDocumentId,own[0]!["id"]!.GetValue<string>());
        Assert.Equal(source.IndexOf("Host {"),own[0]!["range"]!["start"]!.GetValue<int>());
    }
    [Fact]
    public async Task CompletionOffersSharpDeviceInLibraryCode()
    {
        // A library declares its custom devices and entities by deriving from the base classes, which completion offers there.
        const string source="public class X : Sharp";
        var names=(await Request("completion",[new("main",source,1)],"main",source.Length)).AsArray().Select(i=>i!["displayText"]!.GetValue<string>()).ToArray();
        Assert.Contains("SharpDevice",names); Assert.Contains("SharpSwitch",names); Assert.Contains("SharpSensor",names);
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
    public async Task Completion_offers_no_per_entity_tree()
    {
        const string source="public class Main { void Run() { Entit } }";
        var items=(await Request("completion",[new("main",source,1)],"main",source.IndexOf("Entit")+5)).AsArray();
        Assert.DoesNotContain(items,i=>i!["displayText"]!.GetValue<string>() is "Entities" or "EntityRef" or "HassServices");
        // The filter keeps the best matches: Entity (the handle class) now matches "Entit" better than the snapshot does, so it is asked for by more of its name.
        Assert.Contains(items,i=>i!["displayText"]!.GetValue<string>()=="Entity");
        const string snapshot="public class Main { void Run() { EntityStat } }";
        var states=(await Request("completion",[new("main",snapshot,1)],"main",snapshot.IndexOf("EntityStat")+10)).AsArray();
        Assert.Contains(states,i=>i!["displayText"]!.GetValue<string>()=="EntityStateSnapshot");
        // With Home Assistant's entity lists given, there is still no tree: a class per domain only.
        const string tree="public static class CustomNode\n{\n    [Node(\"Check\")]\n    public static void Run() { Entit }\n}";
        var withEntities=(await WithEntities("completion",tree,tree.IndexOf("Entit")+5)).AsArray();
        Assert.DoesNotContain(withEntities,i=>i!["displayText"]!.GetValue<string>() is "Entities" or "EntityRef" or "HassServices");
        Assert.Contains(withEntities,i=>i!["displayText"]!.GetValue<string>()=="Entity");
    }
    static string Fixture(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "tests", "fixtures", name))) return File.ReadAllText(Path.Combine(directory.FullName, "tests", "fixtures", name));
        throw new FileNotFoundException(name);
    }
    // A Custom C# node's code with the entity classes of the captured Home Assistant lists (ha-entity-components.json).
    static async Task<JsonNode> WithEntities(string operation, string source, int position)
    {
        var provider = new DiagnosticsProvider();
        var domains = EntitySchemaCatalog.Parse(Fixture("ha-entity-components.json")).Items.Select(p => KeyValuePair.Create(p.Key, (JsonObject)p.Value!)).ToArray();
        // UseEntitySchema is internal to HassSharp: called as the manager calls it when Home Assistant sends the lists.
        var result = typeof(DiagnosticsProvider).GetMethod("UseEntitySchema", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(provider, [domains])!;
        Assert.True((bool)result.GetType().GetProperty("Accepted")!.GetValue(result)!, "the captured entity lists give classes");
        return JsonSerializer.SerializeToNode(await new NodeLibraryLanguageServices(provider).RequestAsync(operation,[],new CustomNodeSource("motion",source),position,TestContext.Current.CancellationToken), VisualStore.Json)!.Items();
    }
    [Fact]
    public async Task Custom_node_code_completes_and_documents_the_entity_classes()
    {
        const string source="public static class CustomNode\n{\n    [Node(\"Motion\")]\n    public static bool Run(BinarySensorEntity motionSensor, Light) => motionSensor.State == BinarySensorState.On;\n}";
        var items=(await WithEntities("completion",source,source.IndexOf("Light)")+5)).AsArray();
        Assert.Contains(items,i=>i!["displayText"]!.GetValue<string>()=="LightEntity");
        var hover=(await WithEntities("hover",source,source.IndexOf("State.On")+"State.".Length+1))["text"]!.GetValue<string>();
        // Its summary: Home Assistant's label, and the text Home Assistant gives the state.
        Assert.Contains("BinarySensorState.On",hover);
        Assert.Contains("On (on).",hover);
        // A member of the class reads its summary.
        var state=(await WithEntities("hover",source,source.IndexOf("motionSensor.State")+"motionSensor.".Length+1))["text"]!.GetValue<string>();
        Assert.Contains("BinarySensorState BinarySensorEntity.State",state);
        Assert.Contains("Other",state);
    }
    [Fact]
    public async Task Completion_does_not_offer_the_entity_helpers_node_code_never_names()
    {
        // The JSON converter of the handles is public in a namespace every node's code imports: it is HassSharp's, and
        // typing "json" offers JsonSerializer (with its using line), not it.
        const string source="public static class CustomNode\n{\n    [Node(\"Check\")]\n    public static string Run(string text)\n    {\n        return json;\n    }\n}";
        var items=(await WithEntities("completion",source,source.IndexOf("json;")+4)).AsArray().Select(i=>i!["displayText"]!.GetValue<string>()).ToArray();
        Assert.Contains("JsonSerializer",items);
        Assert.DoesNotContain("EntityJsonConverter",items);
        const string attributes="public static class CustomNode\n{\n    [Node(\"Check\")]\n    public static void Run() { EntityD }\n}";
        Assert.DoesNotContain((await WithEntities("completion",attributes,attributes.IndexOf("EntityD")+7)).AsArray(),i=>i!["displayText"]!.GetValue<string>() is "EntityDomainAttribute" or "EntityDomain");
        var texts=attributes.Replace("EntityD","EntityTextA");
        Assert.DoesNotContain((await WithEntities("completion",texts,texts.IndexOf("EntityTextA")+11)).AsArray(),i=>i!["displayText"]!.GetValue<string>()=="EntityTextAttribute");
    }
    [Fact]
    public async Task Completion_offers_a_type_of_a_namespace_that_code_without_using_lines_does_not_import()
    {
        // A type from a namespace the code does not import comes with its using line. Code with no using lines got none of
        // these: Roslyn writes the line above the namespace the code is wrapped in, where the editor cannot put it, so the
        // item was left out and "return jsonseria" showed "No suggestions". The line goes at the top of the person's code.
        const string source="public static class CustomNode\n{\n    [Node(\"Check\")]\n    public static string Run(NodeContext context, string text)\n    {\n        return jsonseria;\n    }\n}";
        var position=source.IndexOf("jsonseria")+"jsonseria".Length;
        foreach(var custom in new[]{true,false})
        {
            var items=(custom?await Custom("completion",[],source,position):await Request("completion",[new("main",source,1)],"main",position)).AsArray();
            var item=Assert.Single(items,i=>i!["displayText"]!.GetValue<string>()=="JsonSerializer")!;
            // The list names the item only; its using line is worked out when the item is resolved (shown or inserted).
            Assert.True(item["resolve"]!.GetValue<bool>()); Assert.Empty(item["additionalTextEdits"]!.AsArray());
            var resolved=await Resolve(item,source,position,custom);
            Assert.Contains("JsonSerializer",resolved["documentation"]!.GetValue<string>());
            var line=Assert.Single(resolved["additionalTextEdits"]!.AsArray())!;
            Assert.Equal((0,0),(line["start"]!.GetValue<int>(),line["length"]!.GetValue<int>()));
            Assert.StartsWith("using System.Text.Json;\n",line["newText"]!.GetValue<string>());
            // Applied as the editor applies them (the name where it was typed, the line at the top), the code compiles.
            var name=item["textEdit"]!;
            Assert.Equal((source.IndexOf("jsonseria"),"jsonseria".Length,"JsonSerializer"),(name["start"]!.GetValue<int>(),name["length"]!.GetValue<int>(),name["newText"]!.GetValue<string>()));
            var completed=line["newText"]!.GetValue<string>()+source.Replace("jsonseria","JsonSerializer.Serialize(text)");
            var compiler=new NodeLibraryCompiler(new DiagnosticsProvider());
            var preview=custom?compiler.Preview([],[new CustomNodeSource("check",completed)],TestContext.Current.CancellationToken):compiler.Preview([new("main",completed,1)],TestContext.Current.CancellationToken);
            Assert.DoesNotContain(custom?preview.Snapshot.Custom["check"].Diagnostics:preview.Diagnostics,d=>d.Severity==3);
        }
    }
    [Fact]
    public async Task Completion_gives_the_whole_list_for_the_word_so_the_editor_filters_it_while_typing()
    {
        // Roslyn's own filter kept only its best matches for the word ("B" gave a dozen items), so the editor had to ask
        // again at every letter. The list is now everything the editor can match by the first letter, complete, and the
        // editor narrows it itself; a type of a namespace the code does not import comes with resolve, not its using line.
        var service=new NodeLibraryLanguageServices(new DiagnosticsProvider());
        async Task<JsonObject> Raw(string code,int position) =>
            JsonSerializer.SerializeToNode(await service.RequestAsync("completion",[],new CustomNodeSource("check",code),position,TestContext.Current.CancellationToken),VisualStore.Json)!.AsObject();
        const string code="public static class CustomNode\n{\n    [Node(\"Check\")]\n    public static void Run(NodeContext context) { var x = B; var y = J; var z = ; context. }\n}";
        var word=await Raw(code,code.IndexOf("= B;")+3);
        Assert.False(word["incomplete"]!.GetValue<bool>());
        var names=word["items"]!.AsArray().Select(i=>i!["displayText"]!.GetValue<string>()).ToHashSet();
        Assert.True(names.Count>100,$"the whole list for B, not Roslyn's best matches ({names.Count})");
        Assert.Contains("Boolean",names); Assert.Contains("BitConverter",names);
        Assert.Contains("DBNull",names); // B at a capital: the editor matches it by camel case
        Assert.DoesNotContain("Array",names); Assert.DoesNotContain("Object",names); // no B the editor could match first
        var imported=(await Raw(code,code.IndexOf("= J;")+3))["items"]!.AsArray();
        var serializer=Assert.Single(imported,i=>i!["displayText"]!.GetValue<string>()=="JsonSerializer"&&i["inlineDescription"]?.GetValue<string>()=="System.Text.Json")!;
        Assert.True(serializer["resolve"]!.GetValue<bool>());
        Assert.Equal("JsonSerializer",serializer["textEdit"]!["newText"]!.GetValue<string>());
        // At an empty word the types of namespaces the code does not import wait for the first letter, which asks again.
        var empty=await Raw(code,code.IndexOf("= ;")+2);
        Assert.True(empty["incomplete"]!.GetValue<bool>());
        Assert.DoesNotContain(empty["items"]!.AsArray(),i=>i!["resolve"]?.GetValue<bool>()==true);
        Assert.Contains(empty["items"]!.AsArray(),i=>i!["displayText"]!.GetValue<string>()=="context");
        // After a dot the members are the whole list.
        var members=await Raw(code,code.IndexOf("context. ")+8);
        Assert.False(members["incomplete"]!.GetValue<bool>());
        Assert.Contains(members["items"]!.AsArray(),i=>i!["displayText"]!.GetValue<string>()=="CancellationToken");
    }
    [Fact]
    public async Task A_using_line_added_beside_the_codes_own_is_not_indented_as_the_wrapping_namespace_would_have_it()
    {
        // The code compiles inside a namespace block, so Roslyn indented the new using line and the code's own one after it.
        const string source="using System.Threading.Tasks;\npublic static class CustomNode\n{\n    [Node(\"Check\")]\n    public static void Run() { var j = JsonSer; }\n}";
        var position=source.IndexOf("JsonSer;")+7;
        foreach(var custom in new[]{true,false})
        {
            var item=new JsonObject{["displayText"]="JsonSerializer",["displayTextPrefix"]="",["displayTextSuffix"]="",["inlineDescription"]="System.Text.Json"};
            var line=Assert.Single((await Resolve(item,source,position,custom))["additionalTextEdits"]!.AsArray())!;
            Assert.Equal((0,0,"using System.Text.Json;\n"),(line["start"]!.GetValue<int>(),line["length"]!.GetValue<int>(),line["newText"]!.GetValue<string>()));
        }
    }
    [Fact]
    public async Task Completion_lists_the_members_of_an_entity_state_enum()
    {
        // "BinarySensorState." showed "No suggestions": the members came (tagged EnumMember), and the editor took a tag it
        // did not know for a snippet, which it hides. Roslyn's tags reach the editor as they are.
        const string source="public static class CustomNode\n{\n    [Node(\"Motion\")]\n    public static bool Run(BinarySensorEntity binary) { var x = BinarySensorState.; return true; }\n}";
        var items=(await WithEntities("completion",source,source.IndexOf("State.;")+6)).AsArray();
        Assert.Equal(["Off","On","Other","Unavailable","Unknown"],items.Select(i=>i!["displayText"]!.GetValue<string>()).Order());
        Assert.All(items,i=>Assert.Equal("EnumMember",i!["tags"]![0]!.GetValue<string>()));
    }
    [Fact]
    public async Task Completion_offers_EntityText_and_EntityHandles()
    {
        // Node code gets an entity enum's Home Assistant text from EntityText.Of. EntityHandles is offered as well: it only
        // makes a handle from an id, so offering it is harmless.
        const string source="public static class CustomNode\n{\n    [Node(\"Check\")]\n    public static void Run() { EntityT }\n}";
        Assert.Contains((await WithEntities("completion",source,source.IndexOf("EntityT")+7)).AsArray(),i=>i!["displayText"]!.GetValue<string>()=="EntityText");
        var handles=source.Replace("EntityT","EntityH");
        Assert.Contains((await WithEntities("completion",handles,handles.IndexOf("EntityH")+7)).AsArray(),i=>i!["displayText"]!.GetValue<string>()=="EntityHandles");
    }
    static async Task<JsonNode> WithServices(string operation, string source, int position)
    {
        var provider = new DiagnosticsProvider();
        provider.UseServiceSchema(ServiceSchemaCatalog.Parse("""
            {"light":{"turn_on":{"name":"Turn on","description":"Turns on one or more lights.","target":{"entity":{"domain":"light"}},
              "fields":{"brightness_pct":{"name":"Brightness","description":"Percentage of full brightness.","selector":{"number":{"min":0,"max":100,"unit_of_measurement":"%"}}}}}}}
            """, _ => { }));
        return JsonSerializer.SerializeToNode(await new NodeLibraryLanguageServices(provider).RequestAsync(operation,[],new CustomNodeSource("dim",source),position,TestContext.Current.CancellationToken), VisualStore.Json)!.Items();
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
    public void Custom_code_with_a_pin_name_in_other_letters_shows_the_problem_where_it_is_written()
    {
        // The code editor shows what custom_preview answers: the code compiles, but a pin cannot be named širina, so
        // the problem is at that name and there is no signature to apply, rather than "No problems" and nothing applied.
        const string source="public static class CustomNode\n{\n    [Node(\"Area\")]\n    public static double Area(double širina, double visina) => širina * visina;\n}";
        var custom=new NodeLibraryCompiler(new DiagnosticsProvider()).Preview([],[new CustomNodeSource("area",source)],TestContext.Current.CancellationToken).Snapshot.Custom["area"];
        Assert.False(custom.Valid); Assert.Null(custom.Signature);
        var problem=Assert.Single(custom.Diagnostics);
        Assert.Equal("Name this input with English letters, digits and _ only; širina cannot name a pin",problem.Message);
        Assert.Equal((4,source.Split('\n')[3].IndexOf("širina")+1,3),(problem.StartLine,problem.StartColumn,problem.Severity));
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
                :await service.RequestAsync(operation,[new("main",code,1)],"main",position,TestContext.Current.CancellationToken),VisualStore.Json)!.Items();
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
        // Judged by the method, not its name: the library's own ToPython on another type does not bring Python.NET's back
        // on a NodeContext, and is offered where it applies; the same with Python.NET imported, and from another namespace.
        foreach(var usings in new[]{"","using Python.Runtime;\n","using System.Globalization;\n"})
        {
            var elsewhere=usings+"public static class Mine { public static string ToPython(this string text) => text; }\npublic static class Nodes { public static void Use(NodeContext context, string text) { context. } }";
            Assert.DoesNotContain(await Names(elsewhere,"context.",custom:false),i=>i.StartsWith("ToPython",StringComparison.Ordinal));
            var onText=elsewhere.Replace("{ context. }","{ text. }");
            Assert.Contains("ToPython",await Names(onText,"text.",custom:false));
        }
        // Where both apply, the item is the library's: its signature help shows the library's method only.
        const string both="using Python.Runtime;\npublic static class Mine { public static string ToPython(this NodeContext context, int depth) => \"\"; }\npublic static class Nodes { public static void Use(NodeContext context) { context.ToPython(); } }";
        Assert.Contains("ToPython",await Names(both.Replace("context.ToPython();","context."),"context.",custom:false));
        var bothSignature=(await Ask("signature",both,both.LastIndexOf("ToPython(")+"ToPython(".Length,custom:false)).ToJsonString();
        Assert.True(bothSignature.Contains("depth"),bothSignature); Assert.DoesNotContain("PyObject",bothSignature);
    }
}
