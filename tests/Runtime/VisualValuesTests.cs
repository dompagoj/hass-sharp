using HassSharp;
using System.Text.Json.Nodes;

static class VisualValuesTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static JsonObject Node(string id, string kind, string config = "{}") => new() { ["id"] = id, ["kind"] = kind, ["config"] = JsonNode.Parse(config) };
    static JsonObject Edge(string from, string to, string port = "next") => new() { ["source"] = from, ["target"] = to, ["port"] = port };
    static string Lit(string type, object value) => System.Text.Json.JsonSerializer.Serialize(new { kind = "literal", type, value });
    static string Ref(string name) => System.Text.Json.JsonSerializer.Serialize(new { kind = "variable", name });
    static JsonObject Graph(params JsonObject[] nodes) => new() { ["version"] = 3, ["name"] = "Values", ["nodes"] = new JsonArray(new[] { Node("t", "trigger", "{\"type\":\"manual\"}") }.Concat(nodes).Cast<JsonNode>().ToArray()), ["edges"] = new JsonArray(new[] { "t" }.Concat(nodes.Select(n => n["id"]!.GetValue<string>())).Zip(nodes.Select(n => n["id"]!.GetValue<string>()), (a,b) => (JsonNode)Edge(a,b)).ToArray()) };
    public static async Task Run()
    {
        var diagnostics = new DiagnosticsProvider(); diagnostics.GenerateHassEntities(["sensor.test"]);
        var generator = new VisualGenerator(diagnostics); var compiler = new CodeCompiler(diagnostics);
        var root = Path.Combine(Path.GetTempPath(), "values-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        var previous = HassPath.HassConfiguration; HassPath.HassConfiguration = root;
        async Task Execute(JsonObject graph, bool cancel = false, bool parallel = false)
        {
            var preview = generator.Preview(graph);
            Check(preview.Valid, "v3 fixture compiles: " + System.Text.Json.JsonSerializer.Serialize(preview.Diagnostics));
            var compiled = await compiler.CompileSingleFile(Path.Combine(root, Guid.NewGuid() + ".cs"), preview.Source, true);
            try
            {
                var instance = (Automation)Activator.CreateInstance(compiled.Assembly.GetType("VisualAutomation")!)!;
                using var cts = new CancellationTokenSource(); instance._cancellationToken.Value = cts.Token;
                Task Invoke() => (Task)instance.GetType().GetMethod("Run_74")!.Invoke(instance, null)!;
                var task = parallel ? Task.WhenAll(Invoke(), Invoke()) : Invoke();
                if (cancel) { cts.CancelAfter(30); try { await task.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("cancellation ignored"); } catch (OperationCanceledException) {} }
                else await task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally { compiled.Unload(); }
        }
        try
        {
            await WaitUntilDeadline();
            foreach (var kind in new[] { "and", "or", "xor" }) foreach (var left in new[] { false, true }) foreach (var right in new[] { false, true })
            {
                var expected = kind == "and" ? left && right : kind == "or" ? left || right : left ^ right;
                await Execute(Graph(Node("logic", kind, $"{{\"left\":{Lit("boolean", left)},\"right\":{Lit("boolean", right)},\"output\":\"result\"}}"), Node("check", "custom", $$"""{"code":"if (values.Get<bool>(\"result\", \"check\") != {{expected.ToString().ToLowerInvariant()}}) throw new Exception(\"truth table\");"}""")));
            }
            foreach (var value in new[] { true, false })
            {
                var g = Graph(Node("branch", "if", $"{{\"value\":{Lit("boolean", value)}}}"), Node("yes", "custom", "{\"code\":\"if (!true) throw new Exception();\"}"), Node("no", "custom", "{\"code\":\"throw new Exception(\\\"wrong branch\\\");\"}"));
                g["edges"] = new JsonArray(Edge("t", "branch"), Edge("branch", "yes", value ? "true" : "false"), Edge("branch", "no", value ? "false" : "true")); await Execute(g);
            }
            PyInterop.Entity = id => new() { EntityId = id, State = "21", Attributes = new() { ["temperature"] = 23L } };
            var pipeline = Graph(Node("read", "read_entity", "{\"entity\":\"sensor.test\",\"field\":\"attribute\",\"attribute\":\"temperature\",\"type\":\"number\",\"output\":\"temp\"}"), Node("compare", "compare", $"{{\"left\":{Ref("temp")},\"right\":{Lit("number", 20)},\"operator\":\"gt\",\"output\":\"warm\"}}"), Node("check", "custom", "{\"code\":\"if (!values.Get<bool>(\\\"warm\\\", \\\"check\\\")) throw new Exception();\"}")); await Execute(pipeline);
            var repeat = Graph(Node("r", "repeat", "{\"count\":\"3\"}"), Node("body", "custom", "{\"code\":\"count++;\"}"), Node("after", "custom", "{\"code\":\"if (count != 3) throw new Exception();\"}")); repeat["members"] = "private int count;";
            repeat["edges"] = new JsonArray(Edge("t","r"), Edge("r","body","body"), Edge("r","after")); await Execute(repeat);
            var invalid = (JsonObject)repeat.DeepClone(); invalid["edges"]!.AsArray().Add(Edge("body","after")); Check(!generator.Preview(invalid).Valid, "body cannot escape to continuation");
            repeat["nodes"]![2]!["kind"] = "delay"; repeat["nodes"]![2]!["config"] = JsonNode.Parse("{\"seconds\":\"60\"}"); await Execute(repeat, cancel:true);
            var wait = Graph(Node("wait", "wait_until", $"{{\"entity\":\"sensor.test\",\"field\":\"state\",\"type\":\"number\",\"operator\":\"gt\",\"right\":{Lit("number", 30)},\"timeout\":\"0.05\",\"interval\":\"0.01\"}}"), Node("recover","custom","{\"code\":\"if (lastError is not TimeoutException) throw new Exception();\"}")); wait["edges"]![1]!["port"] = "error"; await Execute(wait);
            wait["nodes"]![1]!["config"]!["timeout"] = "60"; await Execute(wait, cancel:true);
            var reads = 0; PyInterop.Entity = id => new() { EntityId=id, State=++reads > 2 ? "40" : "21" }; wait["nodes"]![1]!["config"]!["timeout"] = "1"; await Execute(wait); Check(reads > 2,"wait rereads live entity");
            var isolation = Graph(Node("set","set_value",$"{{\"output\":\"local\",\"type\":\"string\",\"value\":{Lit("string","initial")}}}"), Node("check","custom","{\"code\":\"var unique = Guid.NewGuid().ToString(); values.Set(\\\"local\\\", unique); await Task.Delay(10); if (values.Get<string>(\\\"local\\\", \\\"check\\\") != unique) throw new Exception();\"}")); await Execute(isolation,parallel:true);
            invalid = Graph(Node("bad","if",$"{{\"value\":{Ref("missing")}}}")); Check(!generator.Preview(invalid).Valid,"missing declared variable rejected");
            invalid = Graph(Node("bad","if",$"{{\"value\":{Lit("number",3)}}}")); Check(!generator.Preview(invalid).Valid,"boolean operand required");
            foreach (var boolean in new[] { true, false })
                await Execute(Graph(Node("not", "not", $"{{\"value\":{Lit("boolean", boolean)},\"output\":\"inverse\"}}"), Node("verify", "custom", $$"""{"code":"if (values.Get<bool>(\"inverse\", \"verify\") != {{(!boolean).ToString().ToLowerInvariant()}}) throw new Exception();"}""")));
            var conversions = Graph(Node("number", "set_value", $"{{\"type\":\"number\",\"output\":\"n\",\"value\":{Lit("string", "12.5")}}}"), Node("boolean", "set_value", $"{{\"type\":\"boolean\",\"output\":\"b\",\"value\":{Lit("string", "on")}}}"), Node("verify", "custom", """{"code":"if (values.Get<double>(\"n\", \"verify\") != 12.5 || !values.Get<bool>(\"b\", \"verify\")) throw new Exception();"}"""));
            await Execute(conversions);
            conversions["nodes"]![1]!["config"]!["value"] = JsonNode.Parse(Lit("string", "not numeric"));
            conversions["nodes"]![2]!["kind"] = "custom"; conversions["nodes"]![2]!["config"] = JsonNode.Parse("""{"code":"if (!lastError.Message.Contains(\"Cannot convert\")) throw new Exception();"}""");
            conversions["nodes"]!.AsArray().RemoveAt(3); conversions["edges"]!.AsArray().RemoveAt(2); conversions["edges"]![1]!["port"] = "error"; await Execute(conversions);
            // Declared does not imply assigned: a producer on the other branch must fail usefully.
            var missingPath = Graph(Node("branch","if",$"{{\"value\":{Lit("boolean",false)}}}"), Node("set","set_value",$"{{\"type\":\"boolean\",\"output\":\"conditional\",\"value\":{Lit("boolean",true)}}}"), Node("use","if",$"{{\"value\":{Ref("conditional")}}}"), Node("recover","custom","""{"code":"if (!lastError.Message.Contains(\"conditional\") || !lastError.Message.Contains(\"use\")) throw new Exception();"}"""));
            missingPath["edges"] = new JsonArray(Edge("t","branch"),Edge("branch","set","true"),Edge("branch","use","false"),Edge("set","use"),Edge("use","recover","error")); await Execute(missingPath);
            var nested = Graph(Node("outer","repeat","{\"count\":\"2\"}"),Node("inner","repeat","{\"count\":\"3\"}"),Node("body","custom","{\"code\":\"count++;\"}"),Node("after","custom","{\"code\":\"if (count != 6) throw new Exception();\"}"));
            nested["members"]="private int count;"; nested["edges"]=new JsonArray(Edge("t","outer"),Edge("outer","inner","body"),Edge("inner","body","body"),Edge("outer","after")); await Execute(nested);
            var nestedCancel = (JsonObject)nested.DeepClone(); nestedCancel["nodes"]![3]!["kind"]="delay";nestedCancel["nodes"]![3]!["config"]=JsonNode.Parse("{\"seconds\":\"60\"}"); await Execute(nestedCancel,cancel:true);
            nested["nodes"]![3]!["kind"]="stop"; nested["nodes"]![3]!["config"]=new JsonObject(); nested["nodes"]![4]!["config"]=JsonNode.Parse("{\"code\":\"throw new Exception(\\\"Stop must skip outer continuation\\\");\"}"); await Execute(nested);
            var bodyRecovery = Graph(Node("repeat","repeat","{\"count\":\"2\"}"),Node("body","custom","{\"code\":\"throw new InvalidOperationException();\"}"),Node("recover","custom","{\"code\":\"count++;\"}"),Node("after","custom","{\"code\":\"if (count != 2) throw new Exception();\"}"));
            bodyRecovery["members"]="private int count;"; bodyRecovery["edges"]=new JsonArray(Edge("t","repeat"),Edge("repeat","body","body"),Edge("body","recover","error"),Edge("repeat","after")); await Execute(bodyRecovery);
            var savedRoot=Path.Combine(root,"saved");var store=new VisualStore(savedRoot,generator);var saved=store.Save("v3",pipeline,0);Check(JsonNode.DeepEquals(new VisualStore(savedRoot,generator).Read("v3")!.Graph,pipeline),"v3 graph roundtrip preserves typed operands");
            foreach (var (kind,config) in new[] {
                ("repeat","{\"count\":\"1001\"}"), ("repeat","{\"count\":\"1.5\"}"),
                ("wait_until","{\"timeout\":\"0\",\"interval\":\"0\"}"),
                ("read_entity","{\"entity\":\"sensor.test\",\"field\":\"attribute\",\"type\":\"number\",\"output\":\"value\"}"),
                ("log","{\"level\":\"Invalid\"}"),
                ("compare",$"{{\"left\":{Lit("string","a")},\"right\":{Lit("string","b")},\"operator\":\"gt\",\"output\":\"result\"}}") })
                Check(!generator.Preview(Graph(Node("bad",kind,config))).Valid,"invalid config rejected: "+kind+config);
            var incomplete=Graph(Node("bad","compare",$"{{\"left\":{Lit("number", "-")},\"right\":{Lit("number",0)},\"operator\":\"eq\",\"output\":\"result\"}}"));Check(generator.Preview(incomplete).Diagnostics.Any(d=>d.Message.Contains("incomplete or invalid")),"unfinished numeric input blocks saving with useful diagnostic");
            var conflict=Graph(Node("one","set_value",$"{{\"type\":\"string\",\"output\":\"same\",\"value\":{Lit("string","a")}}}"),Node("two","not",$"{{\"output\":\"same\",\"value\":{Lit("boolean",true)}}}"));Check(!generator.Preview(conflict).Valid,"conflicting declarations rejected");
            var scopeShare=(JsonObject)bodyRecovery.DeepClone();scopeShare["edges"]!.AsArray().Add(Edge("after","body"));Check(!generator.Preview(scopeShare).Valid,"outer incoming body edge rejected");
            var deep=Graph(Enumerable.Range(0,9).Select(i=>Node("r"+i,"repeat","{\"count\":\"1\"}")).Append(Node("end","stop")).ToArray());foreach(var edge in deep["edges"]!.AsArray().Skip(1)) edge!["port"]="body";Check(!generator.Preview(deep).Valid,"repeat nesting bounded");
            foreach(var (op,left,right,expected) in new[] { ("eq",2d,2d,true),("ne",2d,3d,true),("lt",2d,3d,true),("le",2d,2d,true),("gt",3d,2d,true),("ge",2d,2d,true),("lt",2d,2d,false) })
                await Execute(Graph(Node("cmp","compare",$"{{\"left\":{Lit("number",left)},\"right\":{Lit("number",right)},\"operator\":\"{op}\",\"output\":\"answer\"}}"),Node("verify","custom",$$"""{"code":"if (values.Get<bool>(\"answer\", \"verify\") != {{expected.ToString().ToLowerInvariant()}}) throw new Exception();"}""")));
            PyInterop.Entity = id => new() { EntityId=id, State="25", Attributes=new() };
            var stateGraph=(JsonObject)pipeline.DeepClone();stateGraph["nodes"]![1]!["config"]!["field"]="state";await Execute(stateGraph);
            var missingAttribute=Graph(Node("read","read_entity","{\"entity\":\"sensor.test\",\"field\":\"attribute\",\"attribute\":\"missing\",\"type\":\"number\",\"output\":\"n\"}"),Node("verify","custom","""{"code":"if (!lastError.Message.Contains(\"missing\")) throw new Exception();"}"""));missingAttribute["edges"]![1]!["port"]="error";await Execute(missingAttribute);
            var previousTrigger=UserScriptManager.CurrentTrigger.Value;
            try {
                UserScriptManager.CurrentTrigger.Value=new(new() {EntityId="sensor.test",State="0",LastReported=double.MaxValue});
                var fresh=(JsonObject)wait.DeepClone();fresh["nodes"]![1]!["config"]!["right"]=JsonNode.Parse(Lit("number",20));
                fresh["nodes"]![2]!["config"]=JsonNode.Parse("{\"code\":\"throw new Exception(\\\"fresh poll used old snapshot\\\");\"}");await Execute(fresh);
                Check(generator.Preview(fresh).Source.Contains("ReadFresh(Entities.Sensor.Test.EntityId"),"fresh reads retain typed generated reference");
            } finally { UserScriptManager.CurrentTrigger.Value=previousTrigger; }
            var previousLog=PyInterop.Log;var logged=new List<string>();
            try { PyInterop.Log=(level,message)=>logged.Add(level+":"+message);await Execute(Graph(Node("log","log",$"{{\"level\":\"Info\",\"value\":{Lit("string","visual message")}}}")));Check(logged.Contains("20:visual message"),"Log forwards configured level and value"); }
            finally { PyInterop.Log=previousLog; }
            var slashScopes=Graph(Node("branch","if",$"{{\"value\":{Lit("boolean",true)}}}"),Node("a","repeat","{\"count\":\"1\"}"),Node("b","repeat","{\"count\":\"1\"}"),Node("a/b","repeat","{\"count\":\"1\"}"),Node("shared","stop"));
            slashScopes["edges"]=new JsonArray(Edge("t","branch"),Edge("branch","a","true"),Edge("branch","a/b","false"),Edge("a","b","body"),Edge("b","shared","body"),Edge("a/b","shared","body"));Check(!generator.Preview(slashScopes).Valid,"slash-containing IDs cannot collide with nested scopes");
            Console.WriteLine("PASS visual values: generated logic, branches, extraction, isolation, repeat and wait");
        }
        finally { HassPath.HassConfiguration = previous; Directory.Delete(root,true); }
    }
    static async Task WaitUntilDeadline()
    {
        var initialPolls = 0;
        await VisualValues.WaitUntil(() => { initialPolls++; return true; }, 1, 0.01, CancellationToken.None);
        Check(initialPolls == 1, "wait checks an already-true predicate immediately");

        // A synchronous read cannot be interrupted, but a result observed after the
        // deadline must be discarded even when it is the first immediate check.
        try
        {
            await VisualValues.WaitUntil(() => { Thread.Sleep(40); return true; }, 0.01, 0.01, CancellationToken.None);
            throw new Exception("wait accepted a late initial true result");
        }
        catch (TimeoutException) { }

        var laterPolls = 0;
        try
        {
            await VisualValues.WaitUntil(() =>
            {
                if (++laterPolls == 1) return false;
                Thread.Sleep(150);
                return true;
            }, 0.1, 0.01, CancellationToken.None);
            throw new Exception("wait accepted a true result after the deadline on a later poll");
        }
        catch (TimeoutException) { }
        Check(laterPolls is 1 or 2, "wait must not poll again after timeout");

        // Resume the delay after the deadline to prove no extra entity read starts.
        var expiredPolls = 0;
        var previousContext = SynchronizationContext.Current;
        Task expiredWait;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new LatePollContext());
            expiredWait = VisualValues.WaitUntil(() => ++expiredPolls > 1, 0.01, 0.01, CancellationToken.None);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        try
        {
            await expiredWait;
            throw new Exception("wait polled after its delay resumed past the deadline");
        }
        catch (TimeoutException) { }
        Check(expiredPolls == 1, "expired wait must not start another predicate read");

        var successfulPolls = 0;
        await VisualValues.WaitUntil(() => ++successfulPolls == 2, 1, 0.01, CancellationToken.None);
        Check(successfulPolls == 2, "wait accepts a later true observation within its timeout");
    }

    sealed class LatePollContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) =>
            ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(40); callback(state); });
    }

}
