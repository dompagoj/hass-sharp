using HassSharp;
using System.Text.Json;
using System.Text.Json.Nodes;
using static VisualExecutionTests;

static class AuthoringAdapterTests
{
    static async Task<JsonNode> Request(HassSharpManager manager, object request) => JsonNode.Parse(await manager.VisualRequest(JsonSerializer.Serialize(request)))!;
    static async Task<JsonNode> EntityRequest(HassSharpManager manager, object request) => JsonNode.Parse(await manager.EntityRequest(JsonSerializer.Serialize(request)))!;
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
            // The entity classes are generated from Home Assistant's lists, which always have binary_sensor and light: the
            // examples compile against those of the captured lists (an example takes a BinarySensorEntity).
            var exampleDiagnostics=new DiagnosticsProvider();
            VisualExecutionTests.Check(exampleDiagnostics.UseEntitySchema([..EntitySchemaCatalog.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"ha-entity-components.json"))).Items.Select(p=>KeyValuePair.Create(p.Key,(JsonObject)p.Value!))]).Accepted,"the captured entity lists give classes");
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
            // An entity handle pin: only binary sensors reach it, so the same graph with a light trigger is refused.
            var motion=graphs.Single(g=>g["name"]!.GetValue<string>()=="Motion lights");
            var motionPin=motion["nodes"]!.AsArray().Single(n=>n!["id"]!.GetValue<string>()=="check")!["config"]!["signature"]!["inputs"]!["motionSensor"]!;
            VisualExecutionTests.Check(motionPin["types"]!.AsArray().Select(t=>t!.GetValue<string>()).SequenceEqual(["entity_id:binary_sensor"]),"the motion example's pin takes binary sensors only: "+motionPin.ToJsonString());
            var lightMotion=(JsonObject)motion.DeepClone(); lightMotion["nodes"]![0]!["config"]!["entity"]="light.hallway";
            VisualExecutionTests.Check(VisualGraphValidator.Validate(lightMotion,builtIn).Any(d=>d.Severity==3&&d.NodeId=="check"&&d.InputId=="motionSensor"),"a light's entity into the motion example's pin is refused");
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
            ExampleDefinitions(examples["definitions"]!.AsArray(),exampleDiagnostics,builtIn,services);
            await ExampleDefinitionsThroughTheApi(manager,examples["definitions"]!.AsArray());
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

    /// <summary>
    /// The entities each graph example lists, as a save stores them: its Entity nodes', then its C# entity nodes'
    /// (<c>DefinitionGraph.ListedEntities</c>; each member given must be there and match; device: the key of the Device node
    /// it is on, or null). A C# device's own entities are not listed: its code adds them as it runs.
    /// </summary>
    static readonly Dictionary<string,string> DerivedEntities=new()
    {
        ["Motion in either room"]="""[{"key":"any_motion","domain":"binary_sensor","name":"Any motion","deviceClass":"motion","device":null}]""",
        ["Plug over HTTP"]="""[{"key":"relay","domain":"switch","name":"Relay","deviceClass":"outlet","device":"plug"},{"key":"power","domain":"sensor","name":"Power","deviceClass":"power","options":{"unit":"W","stateClass":"measurement"},"device":"plug"}]""",
        ["Camera with a box"]="""[{"key":"boxed","domain":"camera","name":"Front door with a box","options":{"frame_interval":2},"device":null}]""",
        ["File at setup"]="""[{"key":"model","domain":"sensor","name":"Model file","entityCategory":"diagnostic","device":null}]""",
        ["Plug in C#"]="[]",
        ["Thermometer over HTTP"]="""[{"key":"thermometer","domain":"sensor","name":null,"device":null,"csharp":true}]""",
    };

    /// <summary>The devices each graph example lists, as a save stores them (<c>DefinitionGraph.Devices</c>), exactly: its Device nodes', then its C# device nodes'.</summary>
    static readonly Dictionary<string,string> DerivedDevices=new()
    {
        ["Motion in either room"]="[]",
        ["Plug over HTTP"]="""[{"key":"plug","name":"Plug over HTTP","model":"Smart plug","suggestedArea":"Living room"}]""",
        ["Camera with a box"]="[]",
        ["File at setup"]="[]",
        ["Plug in C#"]="""[{"key":"plug","name":"Smart plug","csharp":true}]""",
        ["Thermometer over HTTP"]="[]",
    };

    /// <summary>What each C# node of an example is given when the definition starts, by node id: each setting's key and value (<c>DefinitionGraph.Components</c>), a wired Setting's saved value included.</summary>
    static readonly Dictionary<string,string> ComponentSettings=new()
    {
        ["Plug in C#"]="""{"plug":{"Address":"http://192.168.1.50/rpc"}}""",
        ["Thermometer over HTTP"]="""{"thermometer":{"Address":"http://192.168.1.60/temperature","Interval":60}}""",
    };

    /// <summary>
    /// The example definitions <c>get_capabilities</c> gives for an entity document, in their order: each previews with no
    /// diagnostics at all, with and without the service schemas, lays out every step and carries no secret's value; each
    /// C# node's stored signature is the one its code gives; each lists the entities and devices a save stores, and gives
    /// its C# nodes their settings; together they use every kind an author is shown by them, a C# entity and a C# device
    /// among them. Each is a graph: a C# class is written in a node of it.
    /// </summary>
    static void ExampleDefinitions(JsonArray definitions,DiagnosticsProvider diagnostics,VisualCatalogSnapshot builtIn,ServiceSchemaCatalog services)
    {
        string[] titles=["Motion in either room","Plug over HTTP","Camera with a box","File at setup","Plug in C#","Thermometer over HTTP"];
        VisualExecutionTests.Check(definitions.Select(d=>d!["title"]!.GetValue<string>()).SequenceEqual(titles),"the example definitions are the six, in order: "+string.Join(", ",definitions.Select(d=>d!["title"])));
        var kinds=new HashSet<string>(StringComparer.Ordinal);
        foreach(var example in definitions)
        {
            var title=example!["title"]!.GetValue<string>();
            VisualExecutionTests.Check(!string.IsNullOrWhiteSpace(example["description"]?.GetValue<string>()),$"example definition {title} has a description");
            VisualExecutionTests.Check(example["library"]==null&&example["definition"]!["logic"]!["kind"]!.GetValue<string>()=="graph",$"example definition {title} is a graph, with no library of its own");
            var snapshot=builtIn;
            var body=example["definition"]!.Deserialize<DefinitionBody>(VisualStore.Json)!;
            var graph=body.Logic.Graph!;
            // Each C# node's stored signature is exactly the one deriving writes from its code, and deriving removes nothing.
            var rederived=new VisualGenerator(diagnostics).DeriveSignatures((JsonObject)graph.DeepClone(),snapshot,out var dropped);
            VisualExecutionTests.Check(dropped.Count==0,$"example definition {title}: deriving its signatures removes nothing: "+JsonSerializer.Serialize(dropped));
            foreach(var node in graph["nodes"]!.AsArray().Where(n=>n!["kind"]!.GetValue<string>() is "csharp_entity" or "csharp_device"))
            {
                var written=rederived["nodes"]!.AsArray().Single(n=>n!["id"]!.GetValue<string>()==node!["id"]!.GetValue<string>())!["config"]!["signature"];
                VisualExecutionTests.Check(JsonNode.DeepEquals(node!["config"]!["signature"],written),$"example definition {title}: the stored signature of {node["id"]} is its code's: "+written?.ToJsonString());
            }
            foreach(var (name,attached) in new[]{("without services",snapshot),("with services",snapshot.WithServices(services))})
            {
                var preview=new DefinitionGenerator(diagnostics).Preview(body,attached);
                VisualExecutionTests.Check(preview.Valid&&preview.Diagnostics.Count==0,$"example definition {title} validates and compiles {name} with no diagnostics: "+JsonSerializer.Serialize(preview.Diagnostics));
            }
            VisualExecutionTests.Check((body.Settings??[]).All(s=>!s.Secret||s.Value==null),$"example definition {title}: no secret setting carries a value");
            // A graph example lists no entities: its Entity nodes declare them, and the preview derives each as it is listed here.
            VisualExecutionTests.Check(example["definition"]!["entities"]==null,$"example definition {title} lists no entities: its Entity nodes declare them");
            // Nor does it send a device of its own: its devices are its Device nodes.
            VisualExecutionTests.Check(!example["definition"]!.AsObject().ContainsKey("device"),$"example definition {title} sends no device: its Device nodes are its devices");
            var composed=new DefinitionGenerator(diagnostics).Preview(body,snapshot).Graph!;
            var derived=DefinitionGraph.ListedEntities(composed,snapshot);
            var expected=JsonNode.Parse(DerivedEntities[title])!.AsArray();
            VisualExecutionTests.Check(derived.Count==expected.Count&&expected.Select((e,i)=>e!.AsObject().All(p=>derived[i]!.AsObject().ContainsKey(p.Key)&&JsonNode.DeepEquals(p.Value,derived[i]![p.Key]))).All(x=>x),
                $"example definition {title} derives its entities {expected.ToJsonString()}: "+derived.ToJsonString());
            var devices=DefinitionGraph.Devices(composed,snapshot.Root);
            VisualExecutionTests.Check(JsonNode.DeepEquals(devices,JsonNode.Parse(DerivedDevices[title])),$"example definition {title} derives its devices {DerivedDevices[title]}: "+devices.ToJsonString());
            var given=new JsonObject();
            foreach(var component in DefinitionGraph.Components(composed,snapshot,body.Settings??[]))
                given[component.NodeId]=new JsonObject(component.Settings.Select(s=>KeyValuePair.Create(s.Key,s.Value?.DeepClone())));
            VisualExecutionTests.Check(JsonNode.DeepEquals(given,JsonNode.Parse(ComponentSettings.GetValueOrDefault(title,"{}"))),$"example definition {title} gives its C# nodes their settings: "+given.ToJsonString());
            foreach(var node in graph["nodes"]!.AsArray())
            {
                VisualExecutionTests.Check(node!["position"] is JsonObject&&node["size"]==null,$"example definition {title}: step {node["id"]} has a position and no size");
                kinds.Add(node["kind"]!.GetValue<string>());
            }
        }
        string[] shown=["entity","device","csharp_entity","csharp_device","camera_snapshot","image_draw_rect","image_draw_text","show_frame","http_request","download_file","entity_setup","set_entity_state","entity_is","setting","set_entity_available"];
        VisualExecutionTests.Check(shown.All(kinds.Contains),"the example definitions use every kind they show: missing "+string.Join(", ",shown.Where(k=>!kinds.Contains(k))));
        Console.WriteLine("PASS the example definitions preview with no diagnostics, lay out their steps, carry no secret, hold their code's signatures, list what a save stores and use every kind they show");
    }

    /// <summary>
    /// Each example definition through the entity API, as MCP authoring sends it: with <c>deriveSignatures</c>, its C# nodes'
    /// signatures left out, it previews with no problem and the graph answered carries each C# node's stored signature; as
    /// stored, without deriving, it previews with no problem too.
    /// </summary>
    static async Task ExampleDefinitionsThroughTheApi(HassSharpManager manager,JsonArray definitions)
    {
        var withCode=0;
        foreach(var example in definitions)
        {
            var title=example!["title"]!.GetValue<string>();
            var stored=example["definition"]!.AsObject();
            var sent=(JsonObject)stored.DeepClone();
            var classNodes=sent["logic"]!["graph"]!["nodes"]!.AsArray().Where(n=>n!["kind"]!.GetValue<string>() is "csharp_entity" or "csharp_device").ToArray();
            foreach(var node in classNodes) node!["config"]!.AsObject().Remove("signature");
            withCode+=classNodes.Length>0?1:0;
            var derived=await EntityRequest(manager,new{operation="preview",definition=sent,deriveSignatures=true});
            VisualExecutionTests.Check(derived["ok"]!.GetValue<bool>()&&derived["result"]!["valid"]!.GetValue<bool>()&&derived["result"]!["diagnostics"]!.AsArray().Count==0,
                $"example definition {title}, its signatures left out, previews through the API with deriveSignatures and no problem: "+derived.ToJsonString());
            var answered=derived["result"]!["graph"]!["nodes"]!.AsArray();
            foreach(var node in stored["logic"]!["graph"]!["nodes"]!.AsArray().Where(n=>n!["kind"]!.GetValue<string>() is "csharp_entity" or "csharp_device"))
            {
                var signature=answered.Single(n=>n!["id"]!.GetValue<string>()==node!["id"]!.GetValue<string>())!["config"]!["signature"];
                VisualExecutionTests.Check(JsonNode.DeepEquals(node!["config"]!["signature"],signature),$"example definition {title}: the API derives the stored signature of {node["id"]}: "+signature?.ToJsonString());
            }
            var asStored=await EntityRequest(manager,new{operation="preview",definition=stored});
            VisualExecutionTests.Check(asStored["ok"]!.GetValue<bool>()&&asStored["result"]!["valid"]!.GetValue<bool>()&&asStored["result"]!["diagnostics"]!.AsArray().Count==0,
                $"example definition {title} previews through the API as stored, without deriving, with no problem: "+asStored.ToJsonString());
        }
        VisualExecutionTests.Check(withCode==2,"two example definitions hold C# nodes: "+withCode);
        Console.WriteLine("PASS the example definitions preview through the entity API with no problem, derived and as stored, and derive their stored signatures");
    }
}
