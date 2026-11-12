using HassSharp;
using System.Text.Json.Nodes;
using static VisualExecutionTests;

static class VisualStructuredTests
{
    static JsonObject C(string id,string code)=>N(id,"custom",new{code});
    static JsonObject V(string name)=>new(){["kind"]="variable",["name"]=name};
    static JsonObject Decl(string name, double value)=>new(){["name"]=name,["type"]="number",["initial"]=Lit("number",value)};
    public static async Task Run()
    {
        foreach(var kind in new[]{"while","until","repeat"}) {
            var cfg=new JsonObject{["maxIterations"]=3,["carry"]=new JsonArray(Decl("n",0)),["exports"]=new JsonArray(Decl("total",9))};if(kind=="repeat")cfg["count"]=Lit("number",3);
            var g=G(N("loop",kind,cfg),N("body","set_value",new{type="number",value=new{kind="unset"},output="next"}),C("inc","values.Set(\"next\", values.Get<double>(\"next\",\"inc\")+1);"),N("result","loop_result",new{values=new JsonObject{["carry:n"]=V("next"),["export:total"]=V("next")}}),N("out","set_value",new{type="number",value=new{kind="unset"},output="total"}),C("verify","if(values.Get<double>(\"total\",\"verify\")!=3)throw new Exception(\"export\");"));
            var edges=new JsonArray(E("t","loop"),E("loop","body","body"),E("body","inc"),E("inc","result"),E("loop","out"),E("out","verify"),D("loop","body","value","carry:n"),D("loop","out","value","export:total"));g["edges"]=edges;
            if(kind!="repeat") {g["nodes"]!.AsArray().Add(N("compare","compare",new{left=new{kind="unset"},right=Lit("number",3),@operator=kind=="while"?"lt":"ge"}));g["nodes"]!.AsArray().Add(N("condition","condition_result",new{value=new{kind="unset"}}));edges.Add(E("loop","compare","condition"));edges.Add(E("compare","condition"));edges.Add(D("loop","compare","left","carry:n"));edges.Add(D("compare","condition","value"));}
            await Execute(g);
            if(kind=="repeat") {cfg["count"]=Lit("number",0);g["nodes"]![6]!["config"]!["code"]="if(values.Get<double>(\"total\",\"verify\")!=9)throw new Exception(\"zero export\");";await Execute(g);}
        }
        var limit=G(N("w","while",new{maxIterations=2}),N("condition","condition_result",new{value=Lit("boolean",true)}),C("body","count++;"),C("error","if(count!=2 || !lastError.Message.Contains(\"maximum 2\"))throw new Exception(\"loop limit\");"));limit["members"]="int count;";limit["edges"]=new JsonArray(E("t","w"),E("w","condition","condition"),E("w","body","body"),E("w","error","error"));await Execute(limit);
        var nested=G(N("outer","repeat",new{count=Lit("number",2)}),N("inner","repeat",new{count=Lit("number",3)}),C("body","count++;"),C("after","if(count!=6)throw new Exception(\"nested\");"));nested["members"]="int count;";nested["edges"]=new JsonArray(E("t","outer"),E("outer","inner","body"),E("inner","body","body"),E("outer","after"));await Execute(nested);
        var stale=G(N("loop","repeat",new{count=Lit("number",2)}),N("if","if",new{value=Lit("boolean",true)}),N("producer","not",new{value=Lit("boolean",true)}),N("consumer","if",new{value=new{kind="unset"}}),C("error","if(!lastError.Message.Contains(\"producer.value\"))throw new Exception();"));
        // Custom library condition lets successive iterations take different paths.
        var src="public static class Test { static int n; [Node(\"first\")] public static bool First()=>++n==1; }";var lib=new NodeLibraryCompiler(Diagnostics).Preview([new("stale",src,1)]);stale["nodes"]![2]!["kind"]=lib.Snapshot.Methods.Keys.Single();stale["nodes"]![2]!["config"]=new JsonObject{["arguments"]=new JsonObject()};stale["nodes"]!.AsArray().Add(N("branch","if",new{value=new{kind="unset"}}));
        stale["edges"]=new JsonArray(E("t","loop"),E("loop","if","body"),E("if","branch"),D("if","branch","value","result"),E("branch","producer","true"),E("branch","consumer","false"),E("producer","consumer"),D("producer","consumer","value"),E("consumer","error","error"));await Execute(stale,lib.Snapshot);
        await Parallel();
        await Budget();
        Console.WriteLine("PASS v4 structured carry/export, zero iterations, while/until order, stale reset, limits, parallel cleanup and isolation");
    }
    static async Task Parallel()
    {
        var g=G(C("seed","values.Set(\"bag\",new List<int>{1});"),N("p","parallel",new{branches=new[]{"a","b"}}),C("a","values.Get<List<int>>(\"bag\",\"a\").Add(2); await Task.Delay(20,CancellationToken);"),C("b","await Task.Delay(40,CancellationToken);if(values.Get<List<int>>(\"bag\",\"b\").Count!=1)throw new Exception(\"alias\");"),C("join","if(values.Get<List<int>>(\"bag\",\"join\").Count!=1)throw new Exception(\"parent alias\");"));g["edges"]=new JsonArray(E("t","seed"),E("seed","p"),E("p","a","branch:a"),E("p","b","branch:b"),E("p","join"));await Execute(g);
        // Failure must cancel sibling, await finally, and enter parent recovery only after cleanup.
        g["members"]="volatile bool cleaned;";g["nodes"]![3]!["config"]!["code"]="await Task.Delay(20,CancellationToken);throw new InvalidOperationException(\"branch failure\");";g["nodes"]![4]!["config"]!["code"]="try {await Task.Delay(60000,CancellationToken);}finally{await Task.Delay(30);cleaned=true;}";g["nodes"]![5]!["config"]!["code"]="if(!cleaned || !lastError.Message.Contains(\"branch failure\"))throw new Exception(\"cleanup not awaited\");";g["edges"]![4]!["port"]="error";await Execute(g);
        // A throwing cancellation registration must not skip awaited sibling cleanup.
        g["nodes"]![4]!["config"]!["code"]="using var registration=CancellationToken.Register(()=>throw new Exception(\"callback fault\"));try {await Task.Delay(60000,CancellationToken);}finally{await Task.Delay(30);cleaned=true;}";await Execute(g);
        // Stop is invocation-wide, including joined/error routes.
        g["nodes"]![4]!["config"]!["code"]="try {await Task.Delay(60000,CancellationToken);}finally{await Task.Delay(30);cleaned=true;}";g["nodes"]![3]!["kind"]="stop";g["nodes"]![3]!["config"]=new JsonObject();g["nodes"]![5]!["config"]!["code"]="throw new Exception(\"stop reached recovery\");";await Execute(g);
        g["nodes"]![3]!["kind"]="delay";g["nodes"]![3]!["config"]=new JsonObject{["seconds"]=Lit("number",60)};await Execute(g,cancel:true);
        // Every boundary copy must complete before any branch work starts.
        g["nodes"]![1]!["config"]!["code"]="values.Set(\"bag\",new System.IO.MemoryStream());";g["nodes"]![3]!["kind"]="custom";g["nodes"]![3]!["config"]=new JsonObject{["code"]="cleaned=true;"};g["nodes"]![5]!["config"]!["code"]="if(cleaned || !lastError.Message.Contains(\"IVisualSnapshot\"))throw new Exception(\"unsafe copy\");";await Execute(g);
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
