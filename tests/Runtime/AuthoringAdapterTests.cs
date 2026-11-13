using HassSharp;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static VisualExecutionTests;

static class AuthoringAdapterTests
{
    static async Task<JsonNode> Request(HassSharpManager manager, object request) => JsonNode.Parse(await manager.VisualRequest(JsonSerializer.Serialize(request)))!;
    [RuntimeSuite("--authoring-adapter", Order = 13)]
    public static async Task Run()
    {
        var previous=HassPath.HassConfiguration;
        var path=Path.Combine(Path.GetTempPath(),"authoring-adapter-"+Guid.NewGuid());
        var manager=new HassSharpManager(path); PyInterop.Log=(_,_)=>{};
        try
        {
            await manager.Init([]);
            var examples=JsonNode.Parse(await File.ReadAllTextAsync("custom_components/hass_sharp/authoring_examples.json"))!;
            var exampleDiagnostics=new DiagnosticsProvider(); exampleDiagnostics.GenerateHassEntities([]);
            // Every example must hold both at startup (no services) and in authoring preview/save, where the
            // service schemas are attached: the golden fixture is the normalised form of ha-service-descriptions.json.
            var services=ServiceSchemaCatalog.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"ha-service-descriptions.json")),_=>{});
            VisualExecutionTests.Check(JsonNode.DeepEquals(services.Section,JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"visual-services.json")))),"fixture services normalise to visual-services.json");
            var builtIn=new NodeLibraryCompiler(exampleDiagnostics).Preview([]).Snapshot;
            void Clean(JsonObject example,VisualCatalogSnapshot snapshot,string label) {
                foreach(var (name,attached) in new[]{("without services",snapshot),("with services",snapshot.WithServices(services))}) {
                    var preview=new VisualGenerator(exampleDiagnostics).Preview(example,attached);
                    VisualExecutionTests.Check(preview.Valid && preview.Diagnostics.Count==0,$"{label} validates and compiles {name} with no diagnostics: "+JsonSerializer.Serialize(preview.Diagnostics));
                }
            }
            var graphs=examples["graphs"]!.AsArray().Select(g=>g!.AsObject()).ToArray();
            foreach(var item in graphs) Clean(item,builtIn,"capability example "+item["name"]);
            VisualExecutionTests.Check(graphs.Any(g=>g["nodes"]!.AsArray().Any(n=>n!["kind"]!.GetValue<string>()=="service")),"capability examples include a service call");
            // The documented typing rule, against the examples themselves: with services attached, a plain entity_id
            // into light.turn_on's domain-filtered target is an error, as a literal and as a wire (Convert removed).
            JsonObject Example(string name)=>(JsonObject)graphs.Single(g=>g["name"]!.GetValue<string>()==name).DeepClone();
            bool Rejected(JsonObject mutated,string nodeId,string inputId)=>VisualGraphValidator.Validate(mutated,builtIn.WithServices(services)).Any(d=>d.Severity==3&&d.NodeId==nodeId&&d.InputId==inputId);
            var plainLiteral=Example("Sunset lights"); plainLiteral["nodes"]![1]!["config"]!["target"]!["type"]="entity_id";
            VisualExecutionTests.Check(Rejected(plainLiteral,"lights","target") && VisualGraphValidator.Validate(plainLiteral).All(d=>d.Severity<3),"plain entity_id literal into light.turn_on target is an error only once services are attached");
            var unknownField=Example("Sunset lights"); unknownField["nodes"]![1]!["config"]!["fields"]!["not_a_field"]=new JsonObject{["kind"]="literal",["type"]="number",["value"]=1};
            VisualExecutionTests.Check(Rejected(unknownField,"lights","field:not_a_field"),"service fields are checked against the attached schema");
            var plainWire=Example("Light named in an event");
            var edges=plainWire["edges"]!.AsArray(); edges.Remove(edges.Single(e=>e!["id"]!.GetValue<string>()=="light-target"));
            edges.Add(new JsonObject{["id"]="plain-target",["kind"]="data",["source"]="entity",["sourcePort"]="value",["target"]="on",["targetPort"]="target"});
            VisualExecutionTests.Check(Rejected(plainWire,"on","target"),"a wired plain entity_id into light.turn_on target needs Convert");
            var library=examples["library"]!;
            var candidate=new NodeLibraryCompiler(exampleDiagnostics).Preview([new(library["id"]!.GetValue<string>(),library["source"]!.GetValue<string>(),1)]);
            VisualExecutionTests.Check(candidate.Valid,"capability library source compiles");
            Clean(library["graph"]!.AsObject(),candidate.Snapshot,"complete read-entity/custom-library example");
            var ordinary=await new CodeCompiler(exampleDiagnostics).CompileSingleFile(Path.Combine(path,"example.cs"),examples["ordinaryCSharp"]!.GetValue<string>(),true); ordinary.Unload();
            var graph=G(N("l","log",new{value=Lit("string","safe")}));
            graph["nodes"]![0]!["position"]=new JsonObject{["x"]=12,["y"]=34};
            var save=await Request(manager,new{operation="save",id="safe",graph,expectedRevision=0});
            VisualExecutionTests.Check(save["result"]!["document"]!["graph"]!["nodes"]![0]!["position"]!["x"]!.GetValue<int>()==12,"saved layout preserved");
            VisualExecutionTests.Check(save["result"]!["document"]!["graph"]!["nodes"]![1]!["position"]!=null,"managed store supplies missing layout");
            VisualExecutionTests.Check(save["ok"]!.GetValue<bool>(),save.ToJsonString());
            var run=await Request(manager,new{operation="run",id="safe",triggerId="t"});
            VisualExecutionTests.Check(run["ok"]!.GetValue<bool>() && run["result"]?["runId"]!=null,"managed visual run resolves stable trigger and dispatches atomically");
            var automation=save["result"]!["document"]!["automationId"]!.GetValue<string>();
            var documentPath=Path.Combine(path,".storage","hass_sharp","visual","safe.json");
            var savedJson=await File.ReadAllTextAsync(documentPath);
            File.Delete(documentPath);
            bool missingRejected=false;
            try { manager.RunNow(automation,"VisualAutomation","Run_74"); }
            catch(InvalidOperationException) { missingRejected=true; }
            finally { await File.WriteAllTextAsync(documentPath,savedJson); }
            await Task.Delay(100); // Let a wrongly dispatched invocation finish before cleanup on RED.
            VisualExecutionTests.Check(missingRejected,"WS rejects active visual without saved document");
            // Hold the authoring gate: a run submitted while a replacement owns the gate must
            // observe the replacement's saved revision, including a failed activation.
            var gate=(SemaphoreSlim)typeof(HassSharpManager).GetField("_visualGate",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(manager)!;
            await gate.WaitAsync();
            graph["members"]="public VisualAutomation() { throw new Exception(\"blocked\"); }";
            var replacement=Request(manager,new{operation="save",id="safe",graph,expectedRevision=1});
            var racing=Request(manager,new{operation="run",id="safe",triggerId="t"});
            var ws=Task.Run(()=>manager.RunNow(automation,"VisualAutomation","Run_74"));
            VisualExecutionTests.Check(!racing.IsCompleted,"visual run waits for authoring gate");
            gate.Release(); await replacement;
            VisualExecutionTests.Check(!(await racing)["ok"]!.GetValue<bool>(),"failed replacement blocks stale visual run");
            try { await ws; throw new Exception("WS dispatched stale saved revision"); } catch(InvalidOperationException) { }
            await manager.UnloadAsync();
            var diagnostics=new DiagnosticsProvider(); diagnostics.GenerateHassEntities([]);
            var compiler=new CodeCompiler(diagnostics);
            var scriptPath=Path.Combine(HassPath.UserScripts,"cached.cs");
            Directory.CreateDirectory(HassPath.UserScripts);
            const string current="public class Current { public static int Value => 2; }";
            var stale=await compiler.CompileSingleFile(scriptPath,"public class Stale { }",true); stale.Unload();
            await File.WriteAllTextAsync(scriptPath,current);
            var oldHash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scriptPath+current+"\0"+diagnostics.HassEntitiesSyntaxTree())));
            var hashPath=Path.Combine(HassPath.UserScripts,".cache","cached.cs.hash");
            await File.WriteAllTextAsync(hashPath,oldHash);
            var loaded=await compiler.CompileFromUserScriptsFolder();
            VisualExecutionTests.Check(loaded.Single().Assembly.GetType("Current")!=null,"runtime build identity invalidates valid stale assembly under legacy cache key");
            var identity=loaded.Single().Assembly.ManifestModule.ModuleVersionId; loaded.ForEach(s=>s.Unload());
            loaded=await compiler.CompileFromUserScriptsFolder();
            VisualExecutionTests.Check(loaded.Single().Assembly.ManifestModule.ModuleVersionId==identity,"same build reuses compiled cache"); loaded.ForEach(s=>s.Unload());
            Console.WriteLine("PASS atomic visual run / stale activation rejection / runtime cache identity");
        }
        finally { await manager.UnloadAsync(); HassPath.HassConfiguration=previous; Directory.Delete(path,true); }
    }
}
