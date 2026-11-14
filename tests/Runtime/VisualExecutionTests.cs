using HassSharp;
using System.Text.Json;
using System.Text.Json.Nodes;

static class VisualExecutionTests
{
    internal static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    internal static JsonObject Lit(string type, object? value) => JsonSerializer.SerializeToNode(new {kind="literal",type,value})!.AsObject();
    internal static JsonObject N(string id,string kind, object? cfg=null) => new(){["id"]=id,["kind"]=kind,["config"]=cfg is JsonObject j ? j : JsonSerializer.SerializeToNode(cfg ?? new{})};
    internal static JsonObject E(string source,string target,string port="next") => new(){["id"]=Guid.NewGuid().ToString(),["source"]=source,["target"]=target,["port"]=port};
    internal static JsonObject D(string source,string target,string targetPort,string sourcePort="value") => new(){["id"]=Guid.NewGuid().ToString(),["kind"]="data",["source"]=source,["target"]=target,["sourcePort"]=sourcePort,["targetPort"]=targetPort};
    /// <summary>A manual trigger, then the nodes; the steps among them are chained in order, value nodes stay out of the run order.</summary>
    internal static JsonObject G(params JsonObject[] nodes)
    {
        var steps=new[]{"t"}.Concat(nodes.Where(n=>!VisualNodeCatalog.IsPure(VisualNodeCatalog.Root["nodes"]?[n["kind"]!.GetValue<string>()])).Select(n=>n["id"]!.GetValue<string>())).ToArray();
        return new(){["version"]=5,["name"]="Execution",["nodes"]=new JsonArray(new[]{N("t","trigger",new {type="manual"})}.Concat(nodes).ToArray<JsonNode>()),["edges"]=new JsonArray(steps.Zip(steps.Skip(1),(a,b)=>(JsonNode)E(a,b)).ToArray())};
    }
    static JsonObject Custom(string id,string code) => N(id,"custom",new {code});
    static JsonObject Log(string id,object value) => N(id,"log",new {value=Lit("string",value)});
    internal static readonly DiagnosticsProvider Diagnostics = new();
    internal static async Task Execute(JsonObject graph, VisualCatalogSnapshot? snapshot=null, bool cancel=false)
    {
        Diagnostics.GenerateHassEntities(["sensor.test"]);
        var preview = snapshot == null ? new VisualGenerator(Diagnostics).Preview(graph) : new VisualGenerator(Diagnostics).Preview(graph,snapshot);
        Check(preview.Valid, "v4 compiles: "+JsonSerializer.Serialize(preview.Diagnostics));
        var compiled=await new CodeCompiler(Diagnostics).CompileSingleFile(Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".cs"),preview.Source);
        try {
            var instance=(Automation)Activator.CreateInstance(compiled.Assembly.GetType("VisualAutomation")!)!;
            using var cts=new CancellationTokenSource();instance._cancellationToken.Value=cts.Token;
            var task=(Task)instance.GetType().GetMethod("Run_74")!.Invoke(instance,null)!;
            if(cancel) {cts.CancelAfter(30);try{await task.WaitAsync(TimeSpan.FromSeconds(3));throw new Exception("cancel ignored");}catch(OperationCanceledException){}}
            else await task.WaitAsync(TimeSpan.FromSeconds(10));
        } finally {compiled.Unload();}
    }
    [RuntimeSuite("--visual-execution", Order = 8)]
    public static async Task Run()
    {
        var previous=HassPath.HassConfiguration; HassPath.HassConfiguration=Path.Combine(Path.GetTempPath(),"visual-v4-"+Guid.NewGuid());
        var log=PyInterop.Log; var messages=new System.Collections.Concurrent.ConcurrentQueue<string>();PyInterop.Log=(_,m)=>messages.Enqueue(m);
        try {
            // Custom C# reads a value node's variable only after a step that reads the value node has run.
            static JsonObject Reads(JsonObject pure,string code) {
                var graph=G(pure,N("i","if",new{value=new{kind="unset"}}),Custom("c",code));
                graph["edges"]=new JsonArray(E("t","i"),E("i","c","true"),E("i","c","false"),D(pure["id"]!.GetValue<string>(),"i","value"));return graph;
            }
            var simple=Reads(N("b","and",new {left=Lit("boolean",true),right=Lit("boolean",false),output="answer"}),"if(values.Get<bool>(\"answer\",\"c\")) throw new Exception(); Logger.Info(\"executed\");");
            await Execute(simple); Check(messages.Contains("executed"),"compiled custom observes typed boolean");
            foreach(var kind in new[]{"and","or","xor"})foreach(var a in new[]{true,false})foreach(var b in new[]{true,false}) {
                var expected=kind=="and"?a&&b:kind=="or"?a||b:a^b;
                await Execute(Reads(N("b",kind,new {left=Lit("boolean",a),right=Lit("boolean",b),output="answer"}),$"if(values.Get<bool>(\"answer\",\"c\") != {expected.ToString().ToLowerInvariant()}) throw new Exception(\"logic\");"));
            }
            await Execute(Reads(N("n","not",new {value=Lit("boolean",false),output="yes"}),"if(!values.Get<bool>(\"yes\",\"c\")) throw new Exception();"));
            foreach(var choice in new[]{true,false}) {
                var branch=G(N("if","if",new{value=Lit("boolean",choice)}),Log("chosen","branch selected "+choice),Custom("wrong","throw new Exception(\"wrong Boolean If branch\");"));
                branch["edges"]=new JsonArray(E("t","if"),E("if","chosen",choice?"true":"false"),E("if","wrong",choice?"false":"true"));await Execute(branch);Check(messages.Contains("branch selected "+choice),"Boolean If selects the actual branch");
            }
            // A wire from a step that has not run fails the step reading it; it never runs that step.
            var missing=G(N("if","if",new {value=Lit("boolean",false)}),N("producer","read_entity",new{entity=Lit("entity_id","sensor.test")}),N("consumer","log",new{value=new{kind="unset"}}),Custom("error","if(!lastError.Message.Contains(\"consumer.value\") || !lastError.Message.Contains(\"producer.value\")) throw new Exception(lastError.Message); Logger.Info(\"missing caught\");"));
            missing["edges"]=new JsonArray(E("t","if"),E("if","producer","true"),E("if","consumer","false"),E("producer","consumer"),E("consumer","error","error"),D("producer","consumer","value")); await Execute(missing);Check(messages.Contains("missing caught"),"data edges do not execute producer");
            var loop=G(N("loop","repeat",new{count=Lit("number",3)}),Custom("body","Logger.Info(\"iteration\");"),Log("after","joined"));loop["edges"]=new JsonArray(E("t","loop"),E("loop","body","body"),E("loop","after"));await Execute(loop);Check(messages.Count(m=>m=="iteration")==3 && messages.Last()=="joined","repeat executes and joins");
            loop["nodes"]![1]!["config"]!["count"]=Lit("number",0);await Execute(loop);Check(messages.Count(m=>m=="iteration")==3,"zero repeat");
            loop["nodes"]![1]!["config"]!["count"]=Lit("number",1);loop["nodes"]![2]!["kind"]="delay";loop["nodes"]![2]!["config"]=new JsonObject{["seconds"]=Lit("number",60)};await Execute(loop,cancel:true);
            await Rich();Check(messages.Count(m=>m=="rich consumed")==3,"actual CLR consumers execute for entity chain and both typed array elements");
            await RichDefaultsAndNulls();
            await VisualStructuredTests.Run();
            await VisualDataTests.Run();
            var bad=(JsonObject)simple.DeepClone();bad["version"]=3;Check(!new VisualGenerator(Diagnostics).Preview(bad).Valid,"legacy graphs rejected");
            var root=Path.Combine(HassPath.HassConfiguration,"store");var store=new VisualStore(root,new VisualGenerator(Diagnostics));var saved=store.Save("v4",simple,0);var reread=store.Read("v4")!;Check(saved.Version==1 && saved.Source==reread.Source && JsonNode.DeepEquals(saved.Graph,reread.Graph),"v4 document/source roundtrip");
            bad=(JsonObject)simple.DeepClone();bad["nodes"]!.AsArray().Single(n=>n!["id"]!.GetValue<string>()=="c")!["config"]!["code"]="this is invalid;";Check(new VisualGenerator(Diagnostics).Preview(bad).Diagnostics.Any(d=>d.NodeId=="c"&&d.Severity==3),"custom compiler diagnostics mapped");
            Console.WriteLine("PASS v4 compiled typed execution, missing producers, loops, cancellation, rich calls, storage and diagnostics");
        } finally {PyInterop.Log=log;HassPath.HassConfiguration=previous;}
    }
    static async Task Rich()
    {
        const string source="""
        public sealed record Reading(string Name, long Value);
        public sealed class Box { public Reading Reading {get;set;} = new("",0); }
        public static class Nodes {
          [Node("Read")] public static Box Read(EntityStateSnapshot entity) => new() {Reading=new(entity.EntityId,entity.Attributes.GetProperty("temperature").GetInt64())};
          [Node("Unbox")] public static Reading Unbox(Box box) => box.Reading;
          [Node("Use")] public static void Use(Reading reading) {if(reading.Name!="sensor.test" || reading.Value!=9223372036854775807L)throw new Exception("CLR value lost");Logger.Info("rich consumed");}
          [Node("Array")] public static Reading[] Array() => [new("sensor.test",9223372036854775807L),new("sensor.test",9223372036854775807L)];
        }
        """;
        var library=new NodeLibraryCompiler(Diagnostics).Preview([new("rich",source,7)]);Check(library.Valid,"library compiles");var s=library.Snapshot;
        string K(string method)=>s.Methods.Values.Single(m=>m.MethodName==method).Kind;
        var graph=G(N("entity","read_entity",new{entity=Lit("entity_id","sensor.test")}),N("read",K("Read"),new{arguments=new{}}),N("unbox",K("Unbox"),new{arguments=new{}}),N("use",K("Use"),new{arguments=new{}}));
        graph["edges"]!.AsArray().Add(D("entity","read","entity"));graph["edges"]!.AsArray().Add(D("read","unbox","box","result"));graph["edges"]!.AsArray().Add(D("unbox","use","reading","result"));
        var old=PyInterop.Entity;PyInterop.Entity=id=>new(){EntityId=id,State="on",Attributes=new(){["temperature"]=long.MaxValue}};
        try {await Execute(graph,s);}finally{PyInterop.Entity=old;}
        var array=G(N("array",K("Array"),new{arguments=new{}}),N("loop","foreach",new{itemType="global::NodeLib.Reading",items=new{kind="unset"}}),N("use",K("Use"),new{arguments=new{}}));array["edges"]=new JsonArray(E("t","array"),E("array","loop"),E("loop","use","body"),D("array","loop","items","result"),D("loop","use","reading","item"));await Execute(array,s);
    }
    static async Task RichDefaultsAndNulls()
    {
        const string source="""
        public enum Mode { A, B }
        public static class Rich {
          [Node("Defaults")] public static async ValueTask<long> Defaults(double? nan=double.NaN,long maximum=long.MaxValue,string? text=null,Mode mode=Mode.B,CancellationToken cancellationToken=default) {
            if(!double.IsNaN(nan!.Value)||maximum!=long.MaxValue||text!=null||mode!=Mode.B||!cancellationToken.CanBeCanceled)throw new Exception("default or token lost");
            await Task.Delay(1,cancellationToken);return maximum;
          }
          [Node("Consume")] public static void Consume(long @value) {if(@value!=long.MaxValue)throw new Exception("wire lost integer range");}
          [Node("Dynamic")] public static dynamic Dynamic()=>"dynamic preserved";
          [Node("Dynamic use")] public static void DynamicUse(dynamic value) {if(value!="dynamic preserved")throw new Exception();}
          [Node("Bad")] public static string Bad()=>null!;
          [Node("Nullable")] public static string? Nullable()=>null;
          [Node("Null use")] public static void NullUse(string? text) {if(text!=null)throw new Exception();}
        }
        """;
        var lib=new NodeLibraryCompiler(Diagnostics).Preview([new("defaults",source,1)]);Check(lib.Valid,"defaults source valid");var snapshot=lib.Snapshot;string K(string name)=>snapshot.Methods.Values.Single(m=>m.Method.Name==name).Kind;
        var graph=G(N("defaults",K("Defaults"),new{arguments=new{}}),N("consume",K("Consume"),new{arguments=new{}}));graph["edges"]!.AsArray().Add(D("defaults","consume","value","result"));await Execute(graph,snapshot);
        var dynamicGraph=G(N("dynamic",K("Dynamic"),new{arguments=new{}}),N("use",K("DynamicUse"),new{arguments=new{}}));dynamicGraph["edges"]!.AsArray().Add(D("dynamic","use","value","result"));await Execute(dynamicGraph,snapshot);
        var nullable=G(N("null",K("Nullable"),new{arguments=new{}}),N("use",K("NullUse"),new{arguments=new{}}));nullable["edges"]!.AsArray().Add(D("null","use","text","result"));await Execute(nullable,snapshot);
        var bad=G(N("bad",K("Bad"),new{arguments=new{}}));try{await Execute(bad,snapshot);throw new Exception("non-null output accepted null");}catch(InvalidOperationException e){Check(e.Message.Contains("bad.result")&&e.Message.Contains("null"),"null output diagnostic precise");}
        // Preview must not execute static constructors, methods, or attach native triggers.
        var invalidLibrary=new NodeLibraryCompiler(Diagnostics).Preview([new("invalid","public static class Invalid {[Node(\"Valid\")]public static void Valid(){} [Node(\"Invalid\")]public static T Generic<T>(T value)=>value;}",1)]);
        Check(!invalidLibrary.Valid,"invalid library fixture rejected by discovery");var invalidGraph=G(N("valid",invalidLibrary.Snapshot.Methods.Keys.Single(),new{arguments=new{}}));Check(!new VisualGenerator(Diagnostics).Preview(invalidGraph,invalidLibrary.Snapshot).Valid,"partial snapshot from invalid preview must be rejected");
        var safeSource="public static class Safe {static Safe(){throw new Exception(\"preview executed\");}[Node(\"Safe\")]public static int Value()=>throw new Exception();}";
        var previewLib=new NodeLibraryCompiler(Diagnostics).Preview([new("preview",safeSource,1)]);var previewGraph=G(N("safe",previewLib.Snapshot.Methods.Keys.Single(),new{arguments=new{}}));Check(new VisualGenerator(Diagnostics).Preview(previewGraph,previewLib.Snapshot).Valid,"preview never executes user code");
    }

}
