using HassSharp;
using System.Text.Json.Nodes;
using static VisualExecutionTests;

static class VisualConversionReviewTests
{
    static void Assert(bool condition,string message)=>VisualExecutionTests.Check(condition,message);
    static JsonObject Custom(string id,string code)=>Code(id,code);
    static (VisualCatalogSnapshot Snapshot,Func<string,string> Kind) Library(string source) {
        var preview=new NodeLibraryCompiler(Diagnostics).Preview([new("conversion-review",source,1)]);
        Assert(preview.Valid,"review library must compile: "+string.Join(";",preview.Diagnostics.Select(d=>d.Message)));
        return(preview.Snapshot,name=>preview.Snapshot.Methods.Values.Single(m=>m.Method.Name==name).Kind);
    }
    [RuntimeSuite("--visual-conversion-review", Order = 9)]
    public static async Task Run(string? scenario=null) {
        var previous=HassPath.HassConfiguration;HassPath.HassConfiguration=Path.Combine(Path.GetTempPath(),"conversion-review-"+Guid.NewGuid());
        var logger=PyInterop.Log;PyInterop.Log=(_,_)=>{};
        try {
            var cases=new (string,Func<Task>)[]{("binding",Binding),("operators",Operators),("char",Char),("comparer",Comparer),("equality",Equality),("json",Json)};
            var failures=new List<Exception>();
            foreach(var (name,test) in cases.Where(c=>scenario==null||scenario==c.Item1))try{await test();Console.WriteLine("PASS visual conversion review: "+name);}catch(Exception e){Console.WriteLine("FAIL visual conversion review: "+name+": "+e.Message);failures.Add(new Exception(name,e));}
            if(failures.Count>0)throw new AggregateException(failures);
        }finally{PyInterop.Log=logger;HassPath.HassConfiguration=previous;}
    }
    static async Task Binding() {
        var (s,k)=Library("""
        public sealed class Source {public static implicit operator int(Source _) {throw new Exception("hidden user conversion executed");}}
        public static class Nodes {
          [Node("Make")]public static dynamic Make()=>new Source();
          [Node("Use")]public static void Use(dynamic value){if(value is not Source)throw new Exception("source lost");Logger.Info("selected dynamic method");}
          public static void Use(int value)=>throw new Exception("unannotated int overload executed");
          [Node("Defaults")]public static void Defaults(int value=7){if(value!=7)throw new Exception("default lost");Logger.Info("selected optional method");}
          public static void Defaults()=>throw new Exception("unannotated parameterless overload executed");
          [Node("Date default")] public static void Date(
            [System.Runtime.InteropServices.Optional,
             System.Runtime.CompilerServices.DateTimeConstant(637134336000000000L)] DateTime value) {
            if(value.Ticks!=637134336000000000L || value.Kind!=DateTimeKind.Unspecified)throw new Exception("Date metadata default lost");
            Logger.Info("selected DateTime metadata default");
          }
          public static void Date()=>throw new Exception("unannotated date overload executed");
        }
        """);
        var messages=new List<string>();var logger=PyInterop.Log;PyInterop.Log=(_,m)=>messages.Add(m);
        try {
            var g=G(N("make",k("Make"),new{arguments=new{}}),N("use",k("Use"),new{arguments=new{}}));g["edges"]!.AsArray().Add(D("make","use","value","result"));await Execute(g,s);Assert(messages.Contains("selected dynamic method"),"selected dynamic method executes without user conversion");
            await Execute(G(N("defaults",k("Defaults"),new{arguments=new{}})),s);Assert(messages.Contains("selected optional method"),"exact selected optional method retains 7");
            await Execute(G(N("date",k("Date"),new{arguments=new{}})),s);
            Assert(messages.Contains("selected DateTime metadata default"),"omitted DateTimeConstant reaches exact selected method with preserved ticks and kind");
        }finally{PyInterop.Log=logger;}
    }
    static async Task Operators() {
        var (s,k)=Library("""
        public sealed record Target(int Value);
        public sealed class Explicit {public static explicit operator Target(Explicit _) {Logger.Info("explicit operator");return new(11);}}
        public sealed class Implicit {public static implicit operator Target(Implicit _) {Logger.Info("implicit operator");return new(12);}}
        public static class Nodes {
          [Node("Explicit")]public static Explicit MakeExplicit()=>new();
          [Node("Implicit")]public static Implicit MakeImplicit()=>new();
          [Node("Consume")]public static void Consume(Target target,int expected){if(target.Value!=expected)throw new Exception("conversion result lost");Logger.Info("operator consumer");}
        }
        """);
        var messages=new List<string>();var logger=PyInterop.Log;PyInterop.Log=(_,m)=>messages.Add(m);
        try {foreach(var (factory,expected) in new[]{("MakeExplicit",11),("MakeImplicit",12)}) {
            var graph=G(N("make",k(factory),new{arguments=new{}}),N("convert","convert",new{type="global::NodeLib.Target",value=new{kind="unset"}}),N("consume",k("Consume"),new{arguments=new{expected=Lit("global::System.Int32",expected)}}));graph["edges"]!.AsArray().Add(D("make","convert","value","result"));graph["edges"]!.AsArray().Add(D("convert","consume","target"));await Execute(graph,s);
        }Assert(messages.Count(m=>m=="operator consumer")==2&&messages.Contains("explicit operator")&&messages.Contains("implicit operator"),"both declared operators execute only through explicit nodes");}finally{PyInterop.Log=logger;}
    }
    static async Task Char() {
        var (s,k)=Library("""
        public static class Nodes {
          [Node("Char")]public static char Make()=> 'A';
          [Node("Nullable char")]public static char? Maybe(bool present)=>present?'A':null;
          [Node("Double")]public static void Double(double value){if(value!=65)throw new Exception("char widening");}
          [Node("Nullable double")]public static void Nullable(double? value,bool present){if(value!=(present?65d:null))throw new Exception("nullable char widening");}
          [Node("Decimal")]public static void Decimal(decimal value){if(value!=65m)throw new Exception("char decimal widening");}
        }
        """);
        foreach(var consumer in new[]{"Double","Decimal"}){var g=G(N("make",k("Make"),new{arguments=new{}}),N("use",k(consumer),new{arguments=new{}}));g["edges"]!.AsArray().Add(D("make","use","value","result"));await Execute(g,s);}
        foreach(var present in new[]{true,false}){var g=G(N("make",k("Maybe"),new{arguments=new{present=Lit("boolean",present)}}),N("use",k("Nullable"),new{arguments=new{present=Lit("boolean",present)}}));g["edges"]!.AsArray().Add(D("make","use","value","result"));await Execute(g,s);}
        var lifted=G(N("make",k("Make"),new{arguments=new{}}),N("use",k("Nullable"),new{arguments=new{present=Lit("boolean",true)}}));lifted["edges"]!.AsArray().Add(D("make","use","value","result"));await Execute(lifted,s);
    }
    static async Task Comparer() {
        // One step returns two values (a tuple with named elements); both reach each branch as copies that keep their aliasing.
        var shared=Shared("""
        public sealed class StatefulComparer : StringComparer {
          public int Calls;
          public override int Compare(string? x,string? y)=>StringComparer.Ordinal.Compare(x,y);
          public override bool Equals(string? x,string? y)=>StringComparer.Ordinal.Equals(x,y);
          public override int GetHashCode(string value){Calls++;return StringComparer.Ordinal.GetHashCode(value);}
        }
        """);
        var g=G(Code("seed","var comparer=new Shared.StatefulComparer();return (new Dictionary<string,int>(comparer),comparer);","","(Dictionary<string,int> Dictionary, Shared.StatefulComparer Comparer)"),N("p","parallel",new{branches=new[]{"a","b"}}),
            Code("a","if(!ReferenceEquals(dictionary.Comparer,comparer))throw new Exception(\"scope comparer alias lost\");dictionary.Add(\"a\",1);await Task.Delay(20,CancellationToken);if(comparer.Calls!=1)throw new Exception(\"sibling comparer mutated\");","Dictionary<string,int> dictionary, Shared.StatefulComparer comparer"),
            Code("b","await Task.Delay(5,CancellationToken);if(comparer.Calls!=0)throw new Exception(\"other branch mutated comparer\");dictionary.Add(\"b\",2);","Shared.StatefulComparer comparer, Dictionary<string,int> dictionary"),
            Code("join","if(comparer.Calls!=0)throw new Exception(\"parent comparer mutated\");","Shared.StatefulComparer comparer"));
        g["edges"]=new JsonArray(E("t","seed"),E("seed","p"),E("p","a","branch:a"),E("p","b","branch:b"),E("p","join"),
            D("seed","a","dictionary","dictionary"),D("seed","a","comparer","comparer"),D("seed","b","dictionary","dictionary"),D("seed","b","comparer","comparer"),D("seed","join","comparer","comparer"));
        var derived=Derived(g,shared);var outputs=derived["nodes"]![1]!["config"]!["signature"]!["outputs"]!.AsObject();
        Assert(outputs.Select(p=>p.Key).SequenceEqual(["dictionary","comparer"])&&outputs["comparer"]!["label"]!.GetValue<string>()=="Comparer","a tuple with named elements gives one output each: "+outputs.ToJsonString());
        await Execute(g,shared);
    }
    static async Task Equality() {
        var (s,k)=Library("""
        public sealed class User {public static implicit operator double(User _)=>throw new Exception("hidden compare conversion");}
        public static class Nodes {
          [Node("Int")]public static int Int()=>1;
          [Node("Long")]public static long Long()=>9007199254740993L;
          [Node("Decimal")]public static decimal Decimal()=>9007199254740993m;
          [Node("User")]public static User User()=>new();
        }
        """);
        foreach(var op in new[]{"eq","ne"})foreach(var reverse in new[]{false,true}) {
            var compare=N("compare","compare",new JsonObject{["operator"]=op,[reverse?"right":"left"]=new JsonObject{["kind"]="unset"},[reverse?"left":"right"]=Lit("number",1)});
            var g=G(N("int",k("Int"),new{arguments=new{}}),compare,N("if","if",new{value=new{kind="unset"}}),Custom("correct","Logger.Info(\"mixed comparison chosen\");"),Custom("wrong","throw new Exception(\"mixed numeric branch wrong\");"));g["edges"]=new JsonArray(E("t","int"),E("int","if"),D("int","compare",reverse?"right":"left","result"),D("compare","if","value"),E("if","correct",op=="eq"?"true":"false"),E("if","wrong",op=="eq"?"false":"true"));await Execute(g,s);
        }
        var exact=G(N("long",k("Long"),new{arguments=new{}}),N("decimal",k("Decimal"),new{arguments=new{}}),N("compare","compare",new{@operator="eq",left=new{kind="unset"},right=new{kind="unset"}}),Code("check","if(!same)throw new Exception(\"long decimal equality\");","bool same"));exact["edges"]!.AsArray().Add(D("long","compare","left","result"));exact["edges"]!.AsArray().Add(D("decimal","compare","right","result"));exact["edges"]!.AsArray().Add(D("compare","check","same"));await Execute(exact,s);
        var prohibited=G(N("user",k("User"),new{arguments=new{}}),N("compare","compare",new{@operator="eq",left=new{kind="unset"},right=Lit("number",1)}));prohibited["edges"]!.AsArray().Add(D("user","compare","left","result"));Assert(!new VisualGenerator(Diagnostics).Preview(prohibited,s).Valid,"comparison never hides user conversion");
    }
    static async Task Json() {
        var (s,k)=Library("""
        public static class Nodes {
          static int n;
          [Node("Long")]public static void Long(long value){long[] expected=[9007199254740993L,long.MinValue,long.MaxValue];if(value!=expected[n++])throw new Exception("JSON Int64 precision lost: "+value);Logger.Info("exact long");}
          [Node("Decimal")]public static void Decimal(decimal value){decimal[] expected=[0.1234567890123456789012345678m,decimal.MaxValue,decimal.MinValue];if(value!=expected[n++])throw new Exception("JSON decimal precision lost: "+value);Logger.Info("exact decimal");}
        }
        """);
        JsonObject Graph(string type,string consumer,string array){var g=G(N("array","array",new{value=new JsonObject{["kind"]="literal",["type"]="array",["value"]=JsonNode.Parse(array)}}),N("loop","foreach",new{itemType=type,items=new{kind="unset"}}),N("use",k(consumer),new{arguments=new{}}));g["edges"]=new JsonArray(E("t","loop"),E("loop","use","body"),D("array","loop","items"),D("loop","use","value","item"));return g;}
        var messages=new List<string>();var logger=PyInterop.Log;PyInterop.Log=(_,m)=>messages.Add(m);
        try {
            await Execute(Graph("global::System.Int64","Long","[9007199254740993]"),s);
            messages.Clear();
            await Execute(Graph("global::System.Int64","Long","[9007199254740993,-9223372036854775808,9223372036854775807]"),s);
            await Execute(Graph("global::System.Decimal","Decimal","[0.1234567890123456789012345678,79228162514264337593543950335,-79228162514264337593543950335]"),s);
            Assert(messages.Count(m=>m=="exact long")==3&&messages.Count(m=>m=="exact decimal")==3,"exact JSON items consumed");
            foreach(var (type,consumer,array) in new[]{("global::System.Int64","Long","[9223372036854775808]"),("global::System.Int64","Long","[-9223372036854775809]"),("global::System.Decimal","Decimal","[79228162514264337593543950336]")}) {
                var g=Graph(type,consumer,array);g["nodes"]!.AsArray().Add(Custom("recover","if(!lastError.Message.Contains(\"loop.item[0]\"))throw new Exception(\"overflow lacks item location\");Logger.Info(\"overflow caught\");"));g["edges"]!.AsArray().Add(E("loop","recover","error"));await Execute(g,s);
            }Assert(messages.Count(m=>m=="overflow caught")==3,"numeric overflow errors routed with item location");
        }finally{PyInterop.Log=logger;}
    }
}
