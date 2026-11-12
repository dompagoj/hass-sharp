using HassSharp;
using System.Text.Json.Nodes;

static class VisualTests
{
    public static JsonObject MemberBindingGraph(string symbol)
    {
        var (members, code, verify) = symbol switch
        {
            "Dispatch" => ("private bool dispatched; private Task Dispatch() { dispatched = true; return Task.CompletedTask; }", "await Dispatch();", "if (!dispatched) throw new Exception(\"helper was not called\");"),
            "stopped" => ("private bool stopped;", "stopped = true;", "if (!stopped) throw new Exception(\"member was not set\");"),
            "values" => ("private string values = \"original\";", "Logger.Info(values); values = \"changed\";", "if (values != \"changed\") throw new Exception(\"member was not set\");"),
            "executed" => ("private int executed;", "executed = 10;", "if (executed != 10) throw new Exception(\"member was changed by control flow\");"),
            "step" => ("private int step;", "step = 10;", "if (step != 10) throw new Exception(\"member was changed by control flow\");"),
            _ => throw new ArgumentException(symbol)
        };
        return new JsonObject {
            ["version"] = 4, ["name"] = "Member binding", ["members"] = members + " public bool Completed;",
            ["nodes"] = new JsonArray(
                new JsonObject { ["id"]="t", ["kind"]="trigger", ["config"]=new JsonObject { ["type"]="startup" } },
                new JsonObject { ["id"]="c", ["kind"]="custom", ["config"]=new JsonObject { ["code"]=code } },
                new JsonObject { ["id"]="verify", ["kind"]="custom", ["config"]=new JsonObject { ["code"]=verify + " Completed = true; Logger.Info(\"binding-" + symbol + "-\" + Cause);" } }),
            ["edges"] = new JsonArray(JsonNode.Parse("""{"id":"tc","source":"t","target":"c"}"""),JsonNode.Parse("""{"id":"cv","source":"c","target":"verify"}"""))
        };
    }
    public static JsonObject Graph() => JsonNode.Parse("""
    {"version":4,"name":"Motion","nodes":[
      {"id":"t","kind":"trigger","config":{"type":"entity","entity":"binary_sensor.motion"}},
      {"id":"c","kind":"condition","config":{"entity":{"kind":"literal","type":"entity_id","value":"binary_sensor.motion"},"state":"on"}},
      {"id":"s","kind":"service","config":{"service":"light.turn_on","entity":"light.hall"}}
    ],"edges":[{"id":"tc","source":"t","target":"c"},{"id":"cs","source":"c","target":"s","port":"true"}]}
    """)!.AsObject();
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
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
            var contenders=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>{try{store.Save("motion",graph,2);return true;}catch(VisualConflictException){return false;}})));Check(contenders.Count(x=>x)==1,"one concurrent revision writer wins");store.Delete("motion",3);Check(store.Read("motion")==null,"delete persists");
            var api=new HassSharpManager(root);api.GenerateHassEntities(["binary_sensor.motion","light.hall"]);var original=PyInterop.SubscribeToEntityTracking;
            PyInterop.SubscribeToEntityTracking=_=>throw new InvalidOperationException("fixture registration failure");
            try{var reply=JsonNode.Parse(await api.VisualRequest(new JsonObject{["operation"]="save",["id"]="broken",["expectedRevision"]=0,["graph"]=Graph()}.ToJsonString()))!;Check(reply["ok"]!.GetValue<bool>()&&!reply["result"]!["active"]!.GetValue<bool>(),"persisted activation failure explicit");Check(api.GetUserScript("visual_broken")==null,"failed activation absent");}
            finally{PyInterop.SubscribeToEntityTracking=original;await api.UnloadAsync();}
            await api.Init(["binary_sensor.motion","light.hall"]);Check(api.GetUserScript("visual_broken")!=null,"saved v4 artifact reloads");
            var edited=Graph();edited["name"]="Display rename";var update=JsonNode.Parse(await api.VisualRequest(new JsonObject{["operation"]="save",["id"]="broken",["expectedRevision"]=1,["graph"]=edited}.ToJsonString()))!;Check(update["result"]!["active"]!.GetValue<bool>(),"update activates");
            var delete=JsonNode.Parse(await api.VisualRequest("""{"operation":"delete","id":"broken","expectedRevision":2}"""))!;Check(delete["ok"]!.GetValue<bool>()&&api.GetUserScript("visual_broken")==null,"delete unloads");await api.Init(["binary_sensor.motion","light.hall"]);Check(api.GetUserScript("visual_broken")==null,"delete survives reload");await api.UnloadAsync();
        }finally{HassPath.HassConfiguration=previous;Directory.Delete(root,true);}
        Console.WriteLine("PASS v4 visual persistence, revision conflicts, activation failures, reload and delete");
    }
}
