using HassSharp;
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
            await manager.Init();
            var examples=JsonNode.Parse(await File.ReadAllTextAsync("custom_components/hass_sharp/authoring_examples.json"))!;
            var exampleDiagnostics=new DiagnosticsProvider();
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
            // An example's Custom C# node carries its source and arguments only, as MCP authoring sends it: preview and
            // save work its pins out from the code (deriveSignatures), which removes nothing from it.
            JsonObject Derives(JsonObject example,VisualCatalogSnapshot snapshot,string label) {
                foreach(var node in example["nodes"]!.AsArray().Where(n=>n!["kind"]!.GetValue<string>()=="custom"))
                    VisualExecutionTests.Check(node!["config"]!.AsObject().Select(p=>p.Key).Order(StringComparer.Ordinal).SequenceEqual(["arguments","source"]),$"{label}: a Custom C# node carries source and arguments only");
                var derived=new VisualGenerator(exampleDiagnostics).DeriveSignatures((JsonObject)example.DeepClone(),snapshot,out var removed);
                VisualExecutionTests.Check(removed.Count==0,$"{label}: deriving its pins removes nothing: "+JsonSerializer.Serialize(removed));
                return derived;
            }
            var graphs=examples["graphs"]!.AsArray().Select(g=>Derives(g!.AsObject(),builtIn,"capability example "+g["name"])).ToArray();
            foreach(var item in graphs) Clean(item,builtIn,"capability example "+item["name"]);
            VisualExecutionTests.Check(graphs.Any(g=>g["nodes"]!.AsArray().Any(n=>n!["kind"]!.GetValue<string>()=="custom"&&n["config"]!["source"]!.GetValue<string>().Contains("[Node(")&&n["config"]!["signature"]!["inputs"]!.AsObject().Count>0)),"capability examples include a Custom C# node with pins");
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
            var pureKinds=library["graph"]!["nodes"]!.AsArray().Select(n=>n!["kind"]!.GetValue<string>()).Where(k=>candidate.Snapshot.Root["nodes"]?[k]?["pure"]?.GetValue<bool>()==true).ToArray();
            VisualExecutionTests.Check(pureKinds.Length==1&&pureKinds[0].StartsWith("library:example:",StringComparison.Ordinal),"the library example uses a pure library method: "+string.Join(", ",pureKinds));
            // service_ports: the ports MCP shows for a service are the validator's own (DataInputs/DataOutputs).
            manager.SetServiceDescriptions(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"ha-service-descriptions.json")));
            async Task<JsonNode> Ports(string service)=>JsonNode.Parse(await manager.NodeLibraryRequest(JsonSerializer.Serialize(new{operation="service_ports",service})))!;
            static string[] Types(JsonNode? port)=>port!["types"]!.AsArray().Select(t=>t!.GetValue<string>()).ToArray();
            var light=await Ports("light.turn_on");
            VisualExecutionTests.Check(light["ok"]!.GetValue<bool>(),light.ToJsonString());
            var lightResult=light["result"]!; var lightInputs=lightResult["inputs"]!.AsObject();
            var lightFields=services.Section["items"]!["light.turn_on"]!["fields"]!.AsObject();
            VisualExecutionTests.Check(lightResult["known"]!.GetValue<bool>() && lightResult["response"]!.GetValue<string>()=="none" && lightResult["hasTarget"]!.GetValue<bool>()
                && Types(lightInputs["target"]).SequenceEqual(["target","entity_id:light"])
                && lightInputs["field:brightness_pct"]!["max"]!.GetValue<double>()==100 && Types(lightInputs["field:brightness_pct"]).SequenceEqual(["number"])
                && lightInputs.Count==lightFields.Count+1 && lightResult["outputs"]!.AsObject().Count==0,"service_ports light.turn_on: refined target, typed fields, no response: "+light.ToJsonString());
            var fieldOrder=lightInputs.Select(p=>p.Key).Where(k=>k.StartsWith("field:")).Select(k=>lightFields[k[6..]]!["order"]!.GetValue<int>()).ToArray();
            VisualExecutionTests.Check(fieldOrder.SequenceEqual(fieldOrder.Order()),"service_ports fields follow Home Assistant's order");
            var forecast=(await Ports("weather.get_forecasts"))["result"]!;
            VisualExecutionTests.Check(forecast["response"]!.GetValue<string>()=="only" && forecast["outputs"]!["response"]!["type"]!.GetValue<string>()=="object"
                && Types(forecast["inputs"]!["target"]).SequenceEqual(["target","entity_id:weather"]),"service_ports weather.get_forecasts returns a response: "+forecast.ToJsonString());
            var notify=(await Ports("notify.notify"))["result"]!;
            VisualExecutionTests.Check(notify["known"]!.GetValue<bool>() && !notify["hasTarget"]!.GetValue<bool>() && notify["inputs"]!["field:target"]!=null
                && notify["inputs"]!["field:message"]!["required"]!.GetValue<bool>(),"service_ports notify.notify: no target, its field named target is its own port: "+notify.ToJsonString());
            var unknown=(await Ports("light.not_a_service"))["result"]!;
            VisualExecutionTests.Check(!unknown["known"]!.GetValue<bool>() && unknown["response"]==null && unknown["inputs"]!.AsObject().Select(p=>p.Key).SequenceEqual(["target"])
                && Types(unknown["inputs"]!["target"]).SequenceEqual(["target","entity_id"]) && unknown["outputs"]!.AsObject().Count==0,"service_ports unknown service keeps only the permissive target: "+unknown.ToJsonString());
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
            // Code that runs as the automation loads (a module initializer) makes its activation fail.
            graph["nodes"]!.AsArray().Add(N("block","custom",new JsonObject{["source"]="""
                public static class Blocker
                {
                    [System.Runtime.CompilerServices.ModuleInitializer]
                    internal static void Load() => throw new Exception("blocked");
                    [Node("Block")]
                    public static void Run() { }
                }
                """,["arguments"]=new JsonObject()}));
            graph["edges"]!.AsArray().Add(E("l","block"));
            var replacement=Request(manager,new{operation="save",id="safe",graph,expectedRevision=1,deriveSignatures=true});
            var racing=Request(manager,new{operation="run",id="safe",triggerId="t"});
            var ws=Task.Run(()=>manager.RunNow(automation,"VisualAutomation","Run_74"));
            VisualExecutionTests.Check(!racing.IsCompleted,"visual run waits for authoring gate");
            gate.Release(); await replacement;
            VisualExecutionTests.Check(!(await racing)["ok"]!.GetValue<bool>(),"failed replacement blocks stale visual run");
            try { await ws; throw new Exception("WS dispatched stale saved revision"); } catch(InvalidOperationException) { }
            await manager.UnloadAsync();
            Console.WriteLine("PASS atomic visual run / stale activation rejection");
        }
        finally { await manager.UnloadAsync(); HassPath.HassConfiguration=previous; Directory.Delete(path,true); }
    }
}
