using HassSharp;
using System.Text.Json;
using System.Text.Json.Nodes;
using static VisualExecutionTests;

static class VisualDataTests
{
    static JsonObject C(string id,string code,string parameters="")=>Code(id,code,parameters);
    public static async Task Run()
    {
        var previous=PyInterop.Entity;var service=PyInterop.CallService;var response=PyInterop.CallServiceWithResponse;
        try {
            PyInterop.Entity=id=>new(){EntityId=id,State="21",Attributes=new(){["nested"]=new Dictionary<string,object>{{"list",new object[]{1,"two",true}}}}};
            var g=G(N("entity","read_entity",new{entity=Lit("entity_id","sensor.test")}),N("field","entity_field",new{entity=new{kind="unset"},field="state",type="number"}),C("verify","if(n!=21)throw new Exception();","double n"));g["edges"]!.AsArray().Add(D("entity","field","entity"));g["edges"]!.AsArray().Add(D("field","verify","n"));await Execute(g);
            var priorEntityTrigger=UserScriptManager.CurrentTrigger.Value;
            try { UserScriptManager.CurrentTrigger.Value=new(new(){EntityId="sensor.test",State="33"});SetCode(g,"verify","if(n!=33)throw new Exception();","double n");await Execute(g); }
            finally {UserScriptManager.CurrentTrigger.Value=priorEntityTrigger;}

            var obj=G(N("object","object",new{value=Lit("object",new Dictionary<string,object>{{"a/b",new[]{3,4}}})}),N("field","get_field",new{value=new{kind="unset"},pointer="/a~1b/1",type="number"}),N("convert","convert",new{value=new{kind="unset"},type="string"}),C("verify","if(text!=\"4\")throw new Exception();","string text"));obj["edges"]!.AsArray().Add(D("object","field","value"));obj["edges"]!.AsArray().Add(D("field","convert","value"));obj["edges"]!.AsArray().Add(D("convert","verify","text"));await Execute(obj);
            var array=G(N("array","array",new{value=Lit("array",new[]{1,2})}),N("loop","foreach",new{items=new{kind="unset"},itemType="number"}),C("body","Shared.total+=item;","double item"),C("verify","if(Shared.total!=3)throw new Exception();"));array["edges"]=new JsonArray(E("t","loop"),E("loop","body","body"),E("loop","verify"),D("array","loop","items"),D("loop","body","item","item"));await Execute(array,Shared("public static double total;"));
            array["nodes"]![1]!["config"]!["value"]=Lit("array",Array.Empty<int>());SetCode(array,"verify","if(Shared.total!=0)throw new Exception();");await Execute(array,Shared("public static double total;"));
            var fields=new JsonObject{["first"]=Lit("number",2),["second"]=Lit("string","hello")};
            var constructors=G(N("o","object_create",new{fields}),N("a","array_create",new{items=new[]{Lit("string","a"),Lit("boolean",true)}}),N("get","get_field",new{value=new{kind="unset"},pointer="/1",type="boolean"}),C("verify","if(!answer)throw new Exception();","bool answer"));constructors["edges"]!.AsArray().Add(D("a","get","value"));constructors["edges"]!.AsArray().Add(D("get","verify","answer"));await Execute(constructors);
            // A wired response output requests the response and drives runtime target/data, including a rich object constructor.
            var calls=new List<(string,string,JsonObject)>();PyInterop.CallServiceWithResponse=(domain,name,json,done,_)=>{calls.Add((domain,name,JsonNode.Parse(json!)!.AsObject()));done("{\"entity\":\"light.kitchen\",\"brightness\":42}");};PyInterop.CallService=(domain,name,json,done,_)=>{calls.Add((domain,name,JsonNode.Parse(json!)!.AsObject()));done?.Invoke();};
            var services=G(N("first","service",new{service="test.query",fields=new{},returnResponse=true}),N("entity","get_field",new{value=new{kind="unset"},pointer="/entity",type="string"}),N("level","get_field",new{value=new{kind="unset"},pointer="/brightness",type="number"}),N("object","object_create",new{fields=new JsonObject{["entity_id"]=new JsonObject{["kind"]="unset"}}}),N("target","convert",new{value=new{kind="unset"},type="target"}),N("second","service",new{service="light.turn_on",fields=new JsonObject{["brightness"]=new JsonObject{["kind"]="unset"}},target=new{kind="unset"}}));var es=services["edges"]!.AsArray();es.Add(D("first","entity","value","response"));es.Add(D("first","level","value","response"));es.Add(D("entity","object","field:entity_id"));es.Add(D("object","target","value"));es.Add(D("target","second","target"));es.Add(D("level","second","field:brightness"));await Execute(services);VisualExecutionTests.Check(calls.Count==2&&calls[1].Item3["entity_id"]!.GetValue<string>()=="light.kitchen"&&calls[1].Item3["brightness"]!.GetValue<int>()==42,"service uses typed runtime target and response data");
            var condition=G(N("condition","condition",new{entity=Lit("entity_id","sensor.test"),state="21"}),C("yes","Logger.Info(\"condition true\");"),C("no","throw new Exception(\"wrong condition\");"));condition["edges"]=new JsonArray(E("t","condition"),E("condition","yes","true"),E("condition","no","false"));await Execute(condition);
            var reads=0;PyInterop.Entity=id=>new(){EntityId=id,State=++reads>2?"30":"20"};var wait=G(N("wait","wait_until",new{entity=Lit("entity_id","sensor.test"),type="number",right=Lit("number",30),timeout=Lit("number",1),interval=Lit("number",.005)}));await Execute(wait);VisualExecutionTests.Check(reads==3,"wait polls fresh state");
            wait["nodes"]![1]!["config"]!["right"]=Lit("number",50);wait["nodes"]![1]!["config"]!["timeout"]=Lit("number",.02);wait["nodes"]!.AsArray().Add(C("timeout","if(lastError is not TimeoutException)throw new Exception();"));wait["edges"]!.AsArray().Add(E("wait","timeout","error"));await Execute(wait);wait["nodes"]![1]!["config"]!["timeout"]=Lit("number",60);await Execute(wait,cancel:true);
            // The trigger data output is the whole normalised payload of the run.
            var trigger=G(N("field","get_field",new{value=new{kind="unset"},pointer="/platform",type="string"}),C("verify","if(platform!=\"manual\")throw new Exception();","string platform"));trigger["edges"]!.AsArray().Add(D("t","field","value","data"));trigger["edges"]!.AsArray().Add(D("field","verify","platform"));
            var prior=InvocationContext.Current;try{InvocationContext.Current=new("run",InvocationCause.Manual,null,TriggerPayload.Manual());await Execute(trigger);}finally{InvocationContext.Current=prior;}
            foreach(var config in new[]{"{\"type\":\"ha\",\"trigger\":{\"trigger\":\"event\",\"event_type\":\"test_event\"}}","{\"type\":\"daily\",\"time\":\"07:30\",\"timeZone\":\"Europe/Zagreb\"}","{\"type\":\"once\",\"at\":\"2030-01-01T10:00:00Z\"}"}){var graph=G(C("c",""));graph["nodes"]![0]!["config"]=JsonNode.Parse(config);await Execute(graph);}
            Console.WriteLine("PASS v4 entity fields, JSON nodes/pointers, typed services/responses, waits, trigger data and trigger declarations");
        }finally {PyInterop.Entity=previous;PyInterop.CallService=service;PyInterop.CallServiceWithResponse=response;}
    }
}
