using HassSharp;
using System.Text.Json.Nodes;

static class VisualTests
{
    /// <summary>
    /// A startup graph whose two Custom C# nodes both declare a member named <paramref name="symbol"/> in a class named
    /// CustomNode: each node's code is its own, so neither the other node nor the generated automation can meet it.
    /// The second logs <c>binding-{symbol}-Startup</c>.
    /// </summary>
    public static JsonObject IdentifierGraph(string symbol)
    {
        static string Source(string symbol, string body) =>
            "public static class CustomNode\n{\n    static int " + symbol + ";\n    [Node(\"Use " + symbol + "\")]\n    public static async Task Run(NodeContext context)\n    {\n        " + body + "\n        await Task.Yield();\n    }\n}";
        var graph = new JsonObject {
            ["version"] = 5, ["name"] = "Identifier binding",
            ["nodes"] = new JsonArray(
                new JsonObject { ["id"]="t", ["kind"]="trigger", ["config"]=new JsonObject { ["type"]="startup" } },
                new JsonObject { ["id"]="c", ["kind"]="custom", ["config"]=new JsonObject { ["source"]=Source(symbol, symbol + " = 1;"), ["arguments"]=new JsonObject() } },
                new JsonObject { ["id"]="verify", ["kind"]="custom", ["config"]=new JsonObject { ["source"]=Source(symbol, symbol + " = 10; if (" + symbol + " != 10) throw new Exception(\"member was changed\"); Logger.Info(\"binding-" + symbol + "-Startup\");"), ["arguments"]=new JsonObject() } }),
            ["edges"] = new JsonArray(JsonNode.Parse("""{"id":"tc","source":"t","target":"c"}"""),JsonNode.Parse("""{"id":"cv","source":"c","target":"verify"}"""))
        };
        return VisualExecutionTests.Derived(graph);
    }
    public static JsonObject Graph() => JsonNode.Parse("""
    {"version":5,"name":"Motion","nodes":[
      {"id":"t","kind":"trigger","config":{"type":"entity","entity":"binary_sensor.motion"}},
      {"id":"c","kind":"condition","config":{"entity":{"kind":"literal","type":"entity_id","value":"binary_sensor.motion"},"state":"on"}},
      {"id":"s","kind":"service","config":{"service":"light.turn_on","target":{"kind":"literal","type":"target","value":{"entity_id":"light.hall"}}}}
    ],"edges":[{"id":"tc","source":"t","target":"c"},{"id":"cs","source":"c","target":"s","port":"true"}]}
    """)!.AsObject();
    /// <summary>The runtime the manager has loaded for saved document <paramref name="id"/>, or null.</summary>
    internal static UserScript? Loaded(HassSharpManager api, string id) =>
        ((UserScriptManager)typeof(HassSharpManager).GetField("_userScriptManager", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(api)!)
            .GetUserScripts().FirstOrDefault(s => s.CompiledScript.FileName == "visual_" + id);
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    [RuntimeSuite("--visual", Order = 6)]
    public static async Task Run()
    {
        var diagnostics=new DiagnosticsProvider();diagnostics.GenerateHassEntities(["binary_sensor.motion","light.hall"]);
        var generator=new VisualGenerator(diagnostics);var graph=Graph();var preview=generator.Preview(graph);
        Check(preview.Valid,"v4 visual compiles");Check(preview.Source==generator.Preview(graph).Source,"deterministic generated source");
        var root=Path.Combine(Path.GetTempPath(),"visual-store-"+Guid.NewGuid());var previous=HassPath.HassConfiguration;HassPath.HassConfiguration=root;
        try {
            var store=new VisualStore(Path.Combine(root,"store"),generator);var saved=store.Save("motion",graph,0);Check(saved.Version==1&&saved.Revision==1,"document envelope/revision");
            Check(saved.Graph["nodes"]![0]!["position"]!=null,"layout populated at save boundary");
            try{store.Save("motion",graph,0);throw new Exception("conflict missing");}catch(VisualConflictException){}
            graph["name"]="Renamed";store.Save("motion",graph,1);Check(store.Read("motion")!.Graph["name"]!.GetValue<string>()=="Renamed","durable rename");
            var invalid=(JsonObject)graph.DeepClone();invalid["version"]=3;try{store.Save("motion",invalid,2);throw new Exception("invalid save accepted");}catch(VisualValidationException){}
            Check(store.Read("motion")!.Revision==2,"invalid write leaves old artifact");
            // Steps wired by value but not into the flow: the wire itself is not a diagnostic, but each step is
            // node.unreachable, an error, so neither Save nor the Preview gate Manager.Init uses accepts the graph.
            var loose=(JsonObject)graph.DeepClone();
            loose["nodes"]!.AsArray().Add(JsonNode.Parse("""{"id":"a","kind":"read_entity","config":{"entity":{"kind":"literal","type":"entity_id","value":"light.hall"}}}"""));
            loose["nodes"]!.AsArray().Add(JsonNode.Parse("""{"id":"x","kind":"log","config":{"value":{"kind":"unset"}}}"""));
            loose["edges"]!.AsArray().Add(JsonNode.Parse("""{"id":"ax","kind":"data","source":"a","target":"x","sourcePort":"value","targetPort":"value"}"""));
            var loosePreview=generator.Preview(loose);
            Check(!loosePreview.Valid&&loosePreview.Diagnostics.Select(d=>d.Code+" "+d.NodeId+": "+d.Message).OrderBy(s=>s,StringComparer.Ordinal).SequenceEqual([
                "node.unreachable a: Read entity is not connected into the flow; connect a step to its execution input",
                "node.unreachable x: Log is not connected into the flow; connect a step to its execution input"]),"loose steps are only unreachable: "+System.Text.Json.JsonSerializer.Serialize(loosePreview.Diagnostics));
            try{store.Save("motion",loose,2);throw new Exception("graph with a step outside the flow saved");}catch(VisualValidationException e) when (e.Preview.Diagnostics.Any(d=>d.Code=="node.unreachable")){}
            Check(store.Read("motion")!.Revision==2,"refused loose write leaves old artifact");
            var contenders=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>{try{store.Save("motion",graph,2);return true;}catch(VisualConflictException){return false;}})));Check(contenders.Count(x=>x)==1,"one concurrent revision writer wins");store.Delete("motion",3);Check(store.Read("motion")==null,"delete persists");
            var api=new HassSharpManager(root);api.GenerateHassEntities(["binary_sensor.motion","light.hall"]);var original=PyInterop.SubscribeToEntityTracking;
            PyInterop.SubscribeToEntityTracking=_=>throw new InvalidOperationException("fixture registration failure");
            try{var reply=JsonNode.Parse(await api.VisualRequest(new JsonObject{["operation"]="save",["id"]="broken",["expectedRevision"]=0,["graph"]=Graph()}.ToJsonString()))!;Check(reply["ok"]!.GetValue<bool>()&&!reply["result"]!["active"]!.GetValue<bool>(),"persisted activation failure explicit");Check(Loaded(api,"broken")==null,"failed activation absent");}
            finally{PyInterop.SubscribeToEntityTracking=original;await api.UnloadAsync();}
            await api.Init(["binary_sensor.motion","light.hall"]);Check(Loaded(api,"broken")!=null,"saved v4 artifact reloads");
            var edited=Graph();edited["name"]="Display rename";var update=JsonNode.Parse(await api.VisualRequest(new JsonObject{["operation"]="save",["id"]="broken",["expectedRevision"]=1,["graph"]=edited}.ToJsonString()))!;Check(update["result"]!["active"]!.GetValue<bool>(),"update activates");
            var delete=JsonNode.Parse(await api.VisualRequest("""{"operation":"delete","id":"broken","expectedRevision":2}"""))!;Check(delete["ok"]!.GetValue<bool>()&&Loaded(api,"broken")==null,"delete unloads");await api.Init(["binary_sensor.motion","light.hall"]);Check(Loaded(api,"broken")==null,"delete survives reload");await api.UnloadAsync();
        }finally{HassPath.HassConfiguration=previous;Directory.Delete(root,true);}
        Console.WriteLine("PASS v4 visual persistence, revision conflicts, activation failures, reload and delete");
    }
}
