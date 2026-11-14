using HassSharp;
using System.Text.Json.Nodes;
using static VisualExecutionTests;

static class VisualStructuredTests
{
    static JsonObject C(string id,string code,string parameters="",string? returns=null)=>Code(id,code,parameters,returns);
    static JsonObject Decl(string name, double value)=>new(){["name"]=name,["type"]="number",["initial"]=Lit("number",value)};
    static JsonObject Unset()=>new(){["kind"]="unset"};
    public static async Task Run()
    {
        foreach(var kind in new[]{"while","until","repeat"}) {
            // The next carried value and the export come from a step's output by wire.
            var cfg=new JsonObject{["maxIterations"]=3,["carry"]=new JsonArray(Decl("n",0)),["exports"]=new JsonArray(Decl("total",9))};if(kind=="repeat")cfg["count"]=Lit("number",3);
            var g=G(N("loop",kind,cfg),C("inc","return n+1;","double n","double"),N("result","loop_result",new{values=new JsonObject{["carry:n"]=Unset(),["export:total"]=Unset()}}),C("verify","if(total!=3)throw new Exception(\"export\");","double total"));
            var edges=new JsonArray(E("t","loop"),E("loop","inc","body"),E("inc","result"),E("loop","verify"),D("loop","inc","n","carry:n"),D("inc","result","carry:n","result"),D("inc","result","export:total","result"),D("loop","verify","total","export:total"));g["edges"]=edges;
            if(kind!="repeat") {g["nodes"]!.AsArray().Add(N("compare","compare",new{left=Unset(),right=Lit("number",3),@operator=kind=="while"?"lt":"ge"}));g["nodes"]!.AsArray().Add(N("condition","condition_result",new{value=Unset()}));edges.Add(E("loop","condition","condition"));edges.Add(D("loop","compare","left","carry:n"));edges.Add(D("compare","condition","value"));}
            await Execute(g);
            if(kind=="repeat") {cfg["count"]=Lit("number",0);SetCode(g,"verify","if(total!=9)throw new Exception(\"zero export\");","double total");await Execute(g);}
        }
        var limit=G(N("w","while",new{maxIterations=2}),N("condition","condition_result",new{value=Lit("boolean",true)}),C("body","Shared.count++;"),C("error","if(Shared.count!=2 || !lastError.Message.Contains(\"maximum 2\"))throw new Exception(\"loop limit\");"));limit["edges"]=new JsonArray(E("t","w"),E("w","condition","condition"),E("w","body","body"),E("w","error","error"));await Execute(limit,Shared("public static int count;"));
        var nested=G(N("outer","repeat",new{count=Lit("number",2)}),N("inner","repeat",new{count=Lit("number",3)}),C("body","Shared.count++;"),C("after","if(Shared.count!=6)throw new Exception(\"nested\");"));nested["edges"]=new JsonArray(E("t","outer"),E("outer","inner","body"),E("inner","body","body"),E("outer","after"));await Execute(nested,Shared("public static int count;"));
        var stale=G(N("loop","repeat",new{count=Lit("number",2)}),N("if","if",new{value=Lit("boolean",true)}),N("producer","not",new{value=Lit("boolean",true)}),N("consumer","if",new{value=new{kind="unset"}}),C("error","if(!lastError.Message.Contains(\"producer.result\"))throw new Exception();"));
        // Custom library condition lets successive iterations take different paths; the producer is a library step, not a value node.
        var src="public static class Test { static int n; [Node(\"first\")] public static bool First()=>++n==1; [Node(\"produce\")] public static bool Produce()=>true; }";var lib=new NodeLibraryCompiler(Diagnostics).Preview([new("stale",src,1)]);
        string Kind(string method)=>lib.Snapshot.Methods.Values.Single(m=>m.MethodName==method).Kind;
        stale["nodes"]![2]!["kind"]=Kind("First");stale["nodes"]![2]!["config"]=new JsonObject{["arguments"]=new JsonObject()};stale["nodes"]![3]!["kind"]=Kind("Produce");stale["nodes"]![3]!["config"]=new JsonObject{["arguments"]=new JsonObject()};stale["nodes"]!.AsArray().Add(N("branch","if",new{value=new{kind="unset"}}));
        stale["edges"]=new JsonArray(E("t","loop"),E("loop","if","body"),E("if","branch"),D("if","branch","value","result"),E("branch","producer","true"),E("branch","consumer","false"),E("producer","consumer"),D("producer","consumer","value","result"),E("consumer","error","error"));await Execute(stale,lib.Snapshot);
        await Parallel();
        await Budget();
        Console.WriteLine("PASS v4 structured carry/export, zero iterations, while/until order, stale reset, limits, parallel cleanup and isolation");
    }
    static async Task Parallel()
    {
        // A value reaches each branch as its own copy: a branch changing it changes it for no one else.
        var g=G(C("seed","return new List<int>{1};","","List<int>"),N("p","parallel",new{branches=new[]{"a","b"}}),C("a","bag.Add(2); await Task.Delay(20,CancellationToken);","List<int> bag"),C("b","await Task.Delay(40,CancellationToken);if(bag.Count!=1)throw new Exception(\"alias\");","List<int> bag"),C("join","if(bag.Count!=1)throw new Exception(\"parent alias\");","List<int> bag"));
        g["edges"]=new JsonArray(E("t","seed"),E("seed","p"),E("p","a","branch:a"),E("p","b","branch:b"),E("p","join"),D("seed","a","bag","result"),D("seed","b","bag","result"),D("seed","join","bag","result"));await Execute(g);
        // Failure must cancel sibling, await finally, and enter parent recovery only after cleanup.
        var state=Shared("public static volatile bool cleaned;");
        SetCode(g,"a","await Task.Delay(20,CancellationToken);throw new InvalidOperationException(\"branch failure\");");SetCode(g,"b","try {await Task.Delay(60000,CancellationToken);}finally{await Task.Delay(30);Shared.cleaned=true;}");SetCode(g,"join","if(!Shared.cleaned || !lastError.Message.Contains(\"branch failure\"))throw new Exception(\"cleanup not awaited\");");g["edges"]![4]!["port"]="error";await Execute(g,state);
        // A throwing cancellation registration must not skip awaited sibling cleanup.
        SetCode(g,"b","using var registration=CancellationToken.Register(()=>throw new Exception(\"callback fault\"));try {await Task.Delay(60000,CancellationToken);}finally{await Task.Delay(30);Shared.cleaned=true;}");await Execute(g,state);
        // Stop is invocation-wide, including joined/error routes.
        SetCode(g,"b","try {await Task.Delay(60000,CancellationToken);}finally{await Task.Delay(30);Shared.cleaned=true;}");g["nodes"]![3]!["kind"]="stop";g["nodes"]![3]!["config"]=new JsonObject();foreach(var wire in g["edges"]!.AsArray().Where(e=>e!["kind"]?.GetValue<string>()=="data"&&e["target"]!.GetValue<string>()=="a").ToArray())g["edges"]!.AsArray().Remove(wire);SetCode(g,"join","throw new Exception(\"stop reached recovery\");");await Execute(g,state);
        g["nodes"]![3]!["kind"]="delay";g["nodes"]![3]!["config"]=new JsonObject{["seconds"]=Lit("number",60)};await Execute(g,state,cancel:true);
        // Every boundary copy must complete before any branch work starts.
        SetCode(g,"seed","return new System.IO.MemoryStream();","","System.IO.MemoryStream");g["nodes"]![3]!["kind"]="custom";g["nodes"]![3]!["config"]=Code("a","Shared.cleaned=true;")["config"]!.DeepClone();
        SetCode(g,"join","if(Shared.cleaned || !lastError.Message.Contains(\"IVisualSnapshot\"))throw new Exception(\"unsafe copy\");");await Execute(g,state);
        // Stop after a sibling starts must drain it; cleanup callback faults are reported.
        var stop=G(N("p","parallel",new{branches=new[]{"work","stop"}}),C("work","using var registration=CancellationToken.Register(()=>throw new Exception(\"stop callback fault\"));try{await Task.Delay(60000,CancellationToken);}finally{await Task.Delay(20);Logger.Info(\"stop cleanup observed\");}"),N("delay","delay",new{seconds=Lit("number",.02)}),N("stop","stop"),C("join","throw new Exception(\"stop joined\");"));
        stop["edges"]=new JsonArray(E("t","p"),E("p","work","branch:work"),E("p","delay","branch:stop"),E("delay","stop"),E("p","join"));
        var oldLog=PyInterop.Log;var cleaned=false;PyInterop.Log=(level,message)=>{oldLog(level,message);if(message=="stop cleanup observed")cleaned=true;};
        try{try{await Execute(stop);throw new Exception("stop cleanup fault silently lost");}catch(AggregateException e){VisualExecutionTests.Check(cleaned&&e.ToString().Contains("stop callback fault"),"Stop drains cleanup before reporting callback fault");}}finally{PyInterop.Log=oldLog;}
        var copier=new VisualSnapshot();var cycle=new Copyable();cycle.Next=cycle;cycle.Values.Add(1);var copy=(Copyable)copier.Copy(cycle)!;VisualExecutionTests.Check(!ReferenceEquals(copy,cycle)&&ReferenceEquals(copy,copy.Next),"cyclic copy retains internal identity");copy.Values.Add(2);VisualExecutionTests.Check(cycle.Values.Count==1,"class collections deep copied");
        var originalError=new InvalidOperationException("outer",new ArgumentException("inner"));originalError.Data["items"]=new List<int>{1};var copiedError=(InvalidOperationException)copier.Copy(originalError)!;VisualExecutionTests.Check(copiedError.InnerException?.Message=="inner"&&!ReferenceEquals(copiedError.InnerException,originalError.InnerException),"error inner state copied");((List<int>)copiedError.Data["items"]!).Add(2);VisualExecutionTests.Check(((List<int>)originalError.Data["items"]!).Count==1,"error data isolated");
        var special=new CustomCopy();var specialCopy=(CustomCopy)copier.Copy(special)!;VisualExecutionTests.Check(specialCopy!=special && specialCopy.Value==7,"explicit custom snapshot used");
    }
    static async Task Budget() {
        var g=G(N("outer","repeat",new{count=Lit("number",1000)}),N("inner","repeat",new{count=Lit("number",1000)}),C("body",""),C("error","if(!lastError.Message.Contains(\"100000\"))throw new Exception(\"wrong budget\");"));g["edges"]=new JsonArray(E("t","outer"),E("outer","inner","body"),E("inner","body","body"),E("outer","error","error"));
        try {await Execute(g);throw new Exception("global budget was not enforced");}catch(InvalidOperationException e){VisualExecutionTests.Check(e.Message.Contains("100000"),"budget failure names limit");}
        var parallel=G(N("p","parallel",new{branches=new[]{"a","b"}}),N("oa","repeat",new{count=Lit("number",300)}),N("ia","repeat",new{count=Lit("number",200)}),C("a","await Task.Yield();"),N("ob","repeat",new{count=Lit("number",300)}),N("ib","repeat",new{count=Lit("number",200)}),C("b","await Task.Yield();"));
        parallel["edges"]=new JsonArray(E("t","p"),E("p","oa","branch:a"),E("oa","ia","body"),E("ia","a","body"),E("p","ob","branch:b"),E("ob","ib","body"),E("ib","b","body"));
        try{await Execute(parallel);throw new Exception("parallel branches received separate budgets");}catch(Exception e)when(e is InvalidOperationException or AggregateException){VisualExecutionTests.Check(e.ToString().Contains("100000"),"shared thread-safe parallel budget");}
    }
    sealed class Copyable {public Copyable? Next; public List<int> Values=[];}
    sealed class CustomCopy:IVisualSnapshot {public int Value=7;public object CreateVisualSnapshot()=>new CustomCopy{Value=Value};}
}
