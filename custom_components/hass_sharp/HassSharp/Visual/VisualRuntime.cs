using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>Return a detached deep copy. Implementations own copying every reachable mutable object.</summary>
public interface IVisualSnapshot { object CreateVisualSnapshot(); }

/// <summary>Copies a complete scope with one identity map, retaining aliases within that scope only.</summary>
public sealed class VisualSnapshot
{
    readonly Dictionary<object,object> copies = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<object> copyingErrors = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<object> copyingComparers = new(ReferenceEqualityComparer.Instance);
    public object? Copy(object? value)
    {
        if(value == null) return null;
        var type=value.GetType();
        if(type.IsPrimitive && type != typeof(IntPtr) && type != typeof(UIntPtr) || type.IsEnum || value is string or decimal or DateTime or DateTimeOffset or TimeSpan or Guid or DateOnly or TimeOnly or EntityStateSnapshot) return value;
        if(value is JsonElement json) return json.ValueKind==JsonValueKind.Undefined ? json : json.Clone();
        if(copyingErrors.Contains(value)||copyingComparers.Contains(value))throw Cannot(type);
        if(copies.TryGetValue(value,out var found)) return found;
        if(value is IVisualSnapshot custom) {
            var result=custom.CreateVisualSnapshot();
            if(result==null || ReferenceEquals(result,value) || result.GetType()!=type) throw new InvalidOperationException($"Cannot snapshot {type}: custom snapshot must return a detached instance of the same concrete type.");
            copies[value]=result;return result;
        }
        if(value is JsonNode node) {var result=node.DeepClone();copies[value]=result;return result;}
        if(value is Array array) {
            if(array.Rank!=1 || array.GetLowerBound(0)!=0) throw new InvalidOperationException($"Cannot snapshot {type}: only zero-based one-dimensional arrays are supported.");
            var result=Array.CreateInstance(type.GetElementType()!,array.Length);copies[value]=result;
            for(var i=0;i<array.Length;i++)result.SetValue(Copy(array.GetValue(i)),i);return result;
        }
        if(value is Exception error) {
            // Error state remains independently mutable; preserve common exception types and messages.
            // Keep a recursion marker while copying an inner error so a cycle through Data fails
            // precisely instead of overflowing. Custom exception state uses IVisualSnapshot.
            if(type.Assembly!=typeof(Exception).Assembly)throw Cannot(type);
            copyingErrors.Add(value);
            Exception result;
            try {
                var inner=(Exception?)Copy(error.InnerException);
                result=error is AggregateException aggregate ? new AggregateException(aggregate.Message,aggregate.InnerExceptions.Select(e=>(Exception)Copy(e)!)) : type.GetConstructor([typeof(string),typeof(Exception)])?.Invoke([error.Message,inner]) as Exception
                    ?? (inner==null?type.GetConstructor([typeof(string)])?.Invoke([error.Message]) as Exception:null)
                    ?? throw Cannot(type);
            } finally {copyingErrors.Remove(value);}
            copies[value]=result;
            foreach(DictionaryEntry entry in error.Data)result.Data.Add(Copy(entry.Key)!,Copy(entry.Value));
            result.Source=error.Source;result.HelpLink=error.HelpLink;
            if(error.StackTrace is {} stack)System.Runtime.ExceptionServices.ExceptionDispatchInfo.SetRemoteStackTrace(result,stack);
            return result;
        }
        if(value is Delegate or Task or Stream or System.Runtime.InteropServices.SafeHandle || type==typeof(IntPtr) || type==typeof(UIntPtr))throw Cannot(type);
        if(type.IsGenericType && type.GetGenericTypeDefinition()==typeof(List<>)) {
            var result=(IList)Activator.CreateInstance(type)!;copies[value]=result;foreach(var item in (IEnumerable)value)result.Add(Copy(item));return result;
        }
        if(type.IsGenericType && type.GetGenericTypeDefinition()==typeof(Dictionary<,>)) {
            // A stateful comparer cannot be shared across branches.
            var comparer=type.GetProperty("Comparer")!.GetValue(value);
            var comparerType=typeof(EqualityComparer<>).MakeGenericType(type.GetGenericArguments()[0]);
            var standard=comparerType.GetProperty("Default")!.GetValue(null);
            var immutable=ReferenceEquals(comparer,standard) || comparer is StringComparer && comparer.GetType().Assembly==typeof(StringComparer).Assembly;
            if(!immutable) {
                // Custom comparers are ordinary mutable values. Retain their scope aliases,
                // but reject a comparer that recursively owns this not-yet-constructed map.
                copyingComparers.Add(value);
                try {comparer=Copy(comparer);}finally{copyingComparers.Remove(value);}
            }
            var result=(IDictionary)Activator.CreateInstance(type,[comparer])!;copies[value]=result;
            foreach(DictionaryEntry item in (IDictionary)value)result.Add(Copy(item.Key)!,Copy(item.Value));return result;
        }
        if(type.Assembly==typeof(object).Assembly || type.Namespace?.StartsWith("System.",StringComparison.Ordinal)==true || type.Namespace?.StartsWith("Python.",StringComparison.Ordinal)==true) throw Cannot(type);
        var clone=typeof(object).GetMethod("MemberwiseClone",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(value,null)!;copies[value]=clone;
        for(var current=type;current!=null;current=current.BaseType)
            foreach(var field in current.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly))
                field.SetValue(clone,Copy(field.GetValue(value)));
        return clone;
    }
    static Exception Cannot(Type type)=>new InvalidOperationException($"Cannot snapshot CLR type {type.FullName} at isolated scope boundary; implement IVisualSnapshot with a detached deep copy.");
}

public sealed class VisualScope
{
    internal Dictionary<(string Node,string Port),object?> Outputs {get;}=[];
    public VisualValues Values {get;internal set;}=new();
    public Exception? LastError {get;set;}
    public CancellationToken CancellationToken {get;internal set;}
    internal bool? Condition;
    internal Dictionary<string,object?>? Updates;
    /// <summary>Trace frame (C10): "" at top level, else parallel branch and loop iteration segments. Set only while traced.</summary>
    internal string Frame="";
    /// <summary>The traced event of the node running in this scope; null when the run is not traced.</summary>
    internal VisualTrace.Event? Trace;
    internal VisualScope Clone(CancellationToken token) {
        var copy=new VisualSnapshot();var result=new VisualScope{CancellationToken=token,Values=Values.Snapshot(copy),LastError=(Exception?)copy.Copy(LastError),Frame=Frame};
        foreach(var pair in Outputs)result.Outputs.Add(pair.Key,copy.Copy(pair.Value));
        return result;
    }
}

/// <summary>
/// One engine per invocation. Data reads never schedule a step; the value nodes a step reads are worked out in its
/// scope just before it runs, every time it runs.
/// </summary>
public sealed class VisualRuntime
{
    readonly Dictionary<string,JsonObject> nodes;
    readonly Dictionary<(string,string),string> flow=[];
    readonly Dictionary<(string,string),(string,string)> data=[];
    readonly Dictionary<string,Type> types;
    readonly JsonObject catalog;
    readonly Func<string,VisualScope,VisualRuntime,Task> custom;
    readonly HashSet<string> compiledConversions;
    readonly Dictionary<string,string> comparisonTypes;
    readonly JsonElement trigger;
    readonly Dictionary<string,string> variables=[];
    readonly Dictionary<string,string[]> pureInputs=[];
    readonly Dictionary<(string,string),string?> absentTriggerOutputs=[];
    readonly CancellationToken external;
    readonly CancellationTokenSource lifetime;
    readonly CancellationTokenRegistration externalRegistration;
    Task externalCleanup=Task.CompletedTask;
    int steps;
    int stopped;
    // Null unless the invocation carries a trace sink: every trace hook is behind this or scope.Trace.
    VisualTrace? trace;
    readonly System.Collections.Concurrent.ConcurrentQueue<Exception> stopFailures = new();
    public VisualRuntime(string graphJson,string catalogJson,Dictionary<string,Type> types,JsonElement trigger,CancellationToken cancellation,Func<string,VisualScope,VisualRuntime,Task> custom,HashSet<string>? compiledConversions=null,Dictionary<string,string>? comparisonTypes=null) {
        this.comparisonTypes=comparisonTypes??[];
        this.compiledConversions=compiledConversions??[];
        var graph=JsonNode.Parse(graphJson)!.AsObject();catalog=JsonNode.Parse(catalogJson)!.AsObject();this.types=types;this.custom=custom;this.trigger=trigger;external=cancellation;lifetime=new CancellationTokenSource();externalRegistration=cancellation.Register(()=>{externalCleanup=lifetime.CancelAsync();});
        nodes=graph["nodes"]!.AsArray().OfType<JsonObject>().ToDictionary(n=>Text(n,"id"));
        foreach(var n in nodes.Values)if(Text(n["config"],"output") is var name && name!="")variables[name]=Text(n,"id");
        var sources=new Dictionary<string,List<string>>();
        foreach(var e in graph["edges"]!.AsArray().OfType<JsonObject>()) {
            if(Text(e,"kind")=="data") {
                data[(Text(e,"target"),Text(e,"targetPort"))]=(Text(e,"source"),Text(e,"sourcePort"));
                if(!sources.TryGetValue(Text(e,"target"),out var list))sources[Text(e,"target")]=list=[];list.Add(Text(e,"source"));
            }
            else {var port=Text(e,"port","next");if(Text(nodes[Text(e,"source")],"kind")=="condition" && port=="next")port="true";flow[(Text(e,"source"),port)]=Text(e,"target");}
        }
        // Each step's value nodes, through chains of value nodes, each after the ones it reads.
        foreach(var id in nodes.Keys.Where(id=>!Pure(id))) {
            var order=new List<string>();var seen=new HashSet<string>(StringComparer.Ordinal);
            void Visit(string node){foreach(var source in sources.GetValueOrDefault(node)??[])if(Pure(source)&&seen.Add(source)){Visit(source);order.Add(source);}}
            Visit(id);if(order.Count>0)pureInputs[id]=order.ToArray();
        }
    }
    bool Pure(string id)=>VisualNodeCatalog.IsPure(catalog["nodes"]?[Text(nodes[id],"kind")]);
    // The name people see on the canvas, for messages about a value node and the step that used it.
    string Title(string id)=>VisualGraphValidator.Words.StepName(nodes[id],catalog["nodes"]?[Text(nodes[id],"kind")]);
    static string Text(JsonNode? node,string key,string fallback="")=>VisualNodeCatalog.Text(node,key,fallback);
    string? Next(string node,string port="next")=>flow.GetValueOrDefault((node,port));
    public async Task RunAsync(string root) {
        trace=VisualTrace.Create(root,nodes.Keys,TraceInputType,TraceOutputType);
        try {await Walk(root,new VisualScope{CancellationToken=lifetime.Token},0);external.ThrowIfCancellationRequested();}
        catch(OperationCanceledException) when(Volatile.Read(ref stopped)!=0 && !external.IsCancellationRequested) {if(InvocationContext.Current is {} run)run.Stopped=true;}
        finally {trace?.Complete();externalRegistration.Dispose();try{await externalCleanup;}catch when(external.IsCancellationRequested){}lifetime.Dispose();}
        if(!stopFailures.IsEmpty)throw new AggregateException("Stop cleanup failed after all branches drained.",stopFailures);
    }
    // Values are traced as the node sees them: after conversion, with the wire they came from.
    public T Input<T>(VisualScope scope,string node,string port) {var value=(T)Coerce(Read(scope,node,port),typeof(T),$"{node}.{port}",false)!;if(scope.Trace!=null)Traced(scope,node,port,value);return value;}
    public object? Input(VisualScope scope,string node,string port) {var value=Read(scope,node,port);if(scope.Trace!=null)Traced(scope,node,port,value);return value;}
    void Traced(VisualScope scope,string node,string port,object? value)=>trace!.Input(scope.Trace!,node,port,value,data.TryGetValue((node,port),out var source)?source:null);
    object? Read(VisualScope scope,string node,string port) {
        if(data.TryGetValue((node,port),out var source)) {
            if(!scope.Outputs.TryGetValue(source,out var value))throw new InvalidOperationException(absentTriggerOutputs.TryGetValue(source,out var reason)
                ? $"Input {node}.{port}: trigger output {source.Item1}.{source.Item2} " + (reason==null?"is absent from this invocation's trigger payload.":$"cannot be read from this invocation's trigger payload: {reason}")
                : $"Input {node}.{port}: producer output {source.Item1}.{source.Item2} has not executed in this invocation/scope.");
            return value;
        }
        return Operand(scope,OperandOf(node,port),$"{node}.{port}");
    }
    JsonNode? OperandOf(string node,string port) {
        var n=nodes[node];var cfg=n["config"]!;JsonNode? operand;
        if(port.StartsWith("field:"))operand=cfg["fields"]?[port[6..]];
        else if(port.StartsWith("item:"))operand=cfg["items"]?[int.Parse(port[5..],CultureInfo.InvariantCulture)];
        else if(Text(n,"kind")=="loop_result")operand=cfg["values"]?[port];
        else if(Text(catalog["nodes"]?[Text(n,"kind")],"argumentStorage")=="arguments")operand=cfg["arguments"]?[port];
        else operand=cfg[port];
        if(operand==null || Text(operand,"kind")=="unset")operand=catalog["nodes"]?[Text(n,"kind")]?["inputs"]?[port]?["default"];
        return operand;
    }
    // The declared type a trace shows for a port: the producer's output type for a wire, else the operand's.
    string? TraceInputType(string node,string port) {
        if(data.TryGetValue((node,port),out var source))return TraceOutputType(source.Item1,source.Item2);
        var operand=OperandOf(node,port);
        if(Text(operand,"kind")=="literal" && Text(operand,"type") is {Length:>0} literal)return literal;
        if(Text(operand,"kind")=="variable")return variables.TryGetValue(Text(operand,"name"),out var producer)?TraceOutputType(producer,"value"):null;
        return catalog["nodes"]?[Text(nodes[node],"kind")]?["inputs"]?[port]?["types"] is JsonArray {Count:1} types && types[0] is JsonValue only && only.TryGetValue<string>(out var type)?type:null;
    }
    string? TraceOutputType(string node,string port) {
        var n=nodes[node];var kind=Text(n,"kind");var cfg=n["config"];
        if(kind=="trigger")return VisualNodeCatalog.TriggerOutputs(n,catalog)[port] is JsonObject spec?Text(spec,"type"):null;
        if(port.Split(':') is [var group and ("carry" or "export"),var name])
            return (cfg?[group=="carry"?"carry":"exports"] as JsonArray??[]).OfType<JsonObject>().FirstOrDefault(d=>Text(d,"name")==name) is {} declaration?Text(declaration,"type"):null;
        var declared=Text(catalog["nodes"]?[kind]?["outputs"]?[port],"type");
        if(declared.StartsWith('$'))declared=Text(cfg,declared[1..]);
        return declared==""?null:declared;
    }
    object? Operand(VisualScope scope,JsonNode? operand,string label) {
        if(Text(operand,"kind")=="variable") {
            var name=Text(operand,"name");
            if(!scope.Values.TryGet(name,out var value))throw new InvalidOperationException($"Input {label}: producer output {variables.GetValueOrDefault(name,name)}.value ('{name}') has not executed in this invocation/scope.");
            return value;
        }
        if(Text(operand,"kind")!="literal")throw new InvalidOperationException($"Input {label} is unset.");
        return FromJson(operand!["value"],Text(operand,"type"),label);
    }
    object? FromJson(JsonNode? value,string type,string label) {
        var root=VisualTypes.RootId(catalog,type);
        if(!types.TryGetValue(root,out var clr))throw new InvalidOperationException($"Unknown declared type {type} at {label}");
        if(value==null)return ConvertTo(null,type,label,false);
        if(clr==typeof(JsonElement)) {
            if(root=="object" && value is not JsonObject || root=="array" && value is not JsonArray)throw new InvalidOperationException($"{label} requires {type}");
            return JsonSerializer.SerializeToElement(value);
        }
        var effective=Nullable.GetUnderlyingType(clr)??clr;
        if(effective.IsEnum)return Enum.ToObject(effective,JsonSerializer.Deserialize(value.ToJsonString(),Enum.GetUnderlyingType(effective))!);
        return JsonSerializer.Deserialize(value.ToJsonString(),clr);
    }
    public void Output(VisualScope scope,string node,object? value,string port="value") {
        var declared=Text(catalog["nodes"]?[Text(nodes[node],"kind")]?["outputs"]?[port],"type");
        if(declared.StartsWith('$'))declared=Text(nodes[node]["config"],declared[1..]);
        if(declared!="")value=ConvertTo(value,declared,node+"."+port,false);
        scope.Outputs[(node,port)]=value;
        if(scope.Trace!=null)trace!.Output(scope.Trace,node,port,value);
        if(port=="value" && Text(nodes[node]["config"],"output") is var name && name!="")scope.Values.Set(name,value);
    }
    public static object? Coerce(object? value,Type type,string label,bool convert) {
        var underlying=Nullable.GetUnderlyingType(type);var target=underlying??type;
        if(value==null) {if(!type.IsValueType||underlying!=null)return null;throw new InvalidOperationException($"{label} requires {type}, got null.");}
        if(type.IsInstanceOfType(value))return value;
        if(type==typeof(JsonElement)&&value is JsonNode jsonNode)return JsonSerializer.SerializeToElement(jsonNode);
        if(convert && target==typeof(JsonElement))return JsonSerializer.SerializeToElement(value);
        if(target==typeof(JsonObject)||target==typeof(JsonArray)) {
            var parsed=value is JsonElement element?JsonNode.Parse(element.GetRawText()):convert?JsonSerializer.SerializeToNode(value):null;
            if(parsed!=null && target.IsInstanceOfType(parsed))return parsed;
        }
        if(value is JsonElement j) {
            if(j.ValueKind==JsonValueKind.Null)return Coerce(null,type,label,convert);
            if(j.ValueKind==JsonValueKind.Number && IsNumber(target)) {
                // Read the original token into its declared numeric type. A double
                // intermediate corrupts Int64/decimal precision before type checking.
                try {
                    var number=JsonSerializer.Deserialize(j.GetRawText(),type);
                    if(number is double d&&!double.IsFinite(d)||number is float f&&!float.IsFinite(f))throw new OverflowException();
                    return number;
                }catch(Exception error)when(error is JsonException or FormatException or OverflowException) {
                    throw new InvalidOperationException($"Cannot materialize {label} as {type}: JSON number is invalid or outside its range.",error);
                }
            }
            value=j.ValueKind switch{JsonValueKind.String=>j.GetString(),JsonValueKind.Number=>j.GetDouble(),JsonValueKind.True=>true,JsonValueKind.False=>false,_=>value};
            if(type.IsInstanceOfType(value))return value;
        }
        // C# char widening uses the UTF-16 code unit; IConvertible's char-to-double
        // implementation does not implement that standard language conversion.
        if(value is char character && IsNumber(target) && target!=typeof(char))value=(ushort)character;
        if(convert && target==typeof(bool))return VisualValues.ConvertValue(value,"boolean");
        if(convert && target==typeof(string))return VisualValues.ConvertValue(value,"string");
        if(value is IConvertible && (convert || IsNumber(value.GetType())&&IsNumber(target))) {
            try {return target.IsEnum?Enum.Parse(target,Convert.ToString(value,CultureInfo.InvariantCulture)!):Convert.ChangeType(value,target,CultureInfo.InvariantCulture);}catch(Exception e)when(e is FormatException or InvalidCastException or OverflowException){throw new InvalidOperationException($"Cannot convert {label} to {type}.",e);}
        }
        throw new InvalidOperationException($"{label} requires {type}, got {value?.GetType()}.");
    }
    static bool IsNumber(Type t)=>Type.GetTypeCode(t) is TypeCode.Char or >=TypeCode.SByte and <=TypeCode.Decimal;
    object? ConvertTo(object? value,string type,string label,bool convert=true) {
        var chain=VisualTypes.Chain(catalog,type);
        if(chain.Count==0||!types.TryGetValue(chain[^1],out var clr))throw new InvalidOperationException($"Unknown declared type {type} at {label}");
        var root=chain[^1];var result=Coerce(value,clr,label,convert);
        if(result==null && VisualTypes.Resolve(catalog,type)?["nullable"]?.GetValue<bool>()!=true)throw new InvalidOperationException($"{label} requires non-null {type}, got null.");
        // A refinement narrows only when the value satisfies every pattern in its chain.
        foreach(var id in chain)if(VisualTypes.Resolve(catalog,id)?["pattern"] is JsonValue pattern && result!=null && (result is not string s || !VisualTypes.Matches(s,pattern.GetValue<string>())))throw new InvalidOperationException($"Cannot convert {label} to {type}.");
        if(root=="object" && result is not JsonObject || root=="array" && result is not JsonArray)throw new InvalidOperationException($"{label} requires JSON {type}.");
        if(root=="number"&&result is double number&&!double.IsFinite(number))throw new InvalidOperationException($"{label} requires finite number.");return result;
    }
    async Task Walk(string? id,VisualScope scope,int depth) {
        if(depth>8)throw new InvalidOperationException("Structured nesting limit is 8");
        while(id!=null) {
            external.ThrowIfCancellationRequested();
            if(Volatile.Read(ref stopped)!=0)throw new OperationCanceledException(lifetime.Token);
            scope.CancellationToken.ThrowIfCancellationRequested();var n=nodes[id];var kind=Text(n,"kind");var cfg=n["config"]!;string? next=Next(id);
            VisualTrace.Event? ev=null;var port="next";
            // The step's event follows the events of its value nodes; it is started even when one of them failed.
            VisualTrace.Event? Begin()=>trace==null?null:scope.Trace=trace.Start(scope.Frame,id);
            try {
                if(Interlocked.Increment(ref steps)>100000)throw new InvalidOperationException($"Node {id}: graph execution limit 100000 steps exceeded.");
                if(pureInputs.TryGetValue(id,out var pure))await Prepare(id,pure,scope);
                ev=Begin();
                object? Get(string p)=>Input(scope,id,p);
                double Number(string p,double min,double max,bool integer=false) {var v=Input<double>(scope,id,p);if(!double.IsFinite(v)||v<min||v>max||integer&&Math.Truncate(v)!=v)throw new InvalidOperationException($"Input {id}.{p} must be {(integer?"an integer":"finite")} in [{min}, {max}].");return v;}
                switch(kind) {
                    case "trigger":Trigger(id,scope,n);break;
                    case "if":port=Input<bool>(scope,id,"value")?"true":"false";next=Next(id,port);break;
                    case "condition":port=ReadEntity(Input<string>(scope,id,"entity")).State==Text(cfg,"state")?"true":"false";next=Next(id,port);break;
                    case "read_entity":Output(scope,id,ReadEntity(Input<string>(scope,id,"entity")));break;
                    case "delay":await Waiting(scope,Task.Delay(TimeSpan.FromSeconds(Number("seconds",0,4294967.294)),scope.CancellationToken));break;
                    case "wait_until":var entity=Input<string>(scope,id,"entity");var expected=Get("right");await Waiting(scope,VisualValues.WaitUntil(()=>Compare(EntityField(ReadEntity(entity,true),cfg,id),expected,Text(cfg,"operator","eq")),Number("timeout",.001,86400),Number("interval",.001,3600),scope.CancellationToken));break;
                    case "service":await Service(id,scope,cfg);break;
                    case "log":PyInterop.Log(Text(cfg,"level","Info") switch{"Debug"=>10,"Warn"=>30,"Error"=>40,_=>20},Convert.ToString(Get("value"),CultureInfo.InvariantCulture)??"null");break;
                    case "stop":Interlocked.Exchange(ref stopped,1);try{await lifetime.CancelAsync();}catch(Exception error){stopFailures.Enqueue(error);}throw new OperationCanceledException(lifetime.Token);
                    case "parallel":await Parallel(id,scope,depth);break;
                    case "repeat":case "while":case "until":case "foreach":await Loop(id,scope,depth);break;
                    case "condition_result":scope.Condition=Input<bool>(scope,id,"value");if(ev!=null)trace!.End(ev,"ok",null,null);return;
                    case "loop_result":scope.Updates=(cfg["values"] as JsonObject??[]).ToDictionary(p=>p.Key,p=>Get(p.Key));foreach(var edge in data.Where(p=>p.Key.Item1==id))scope.Updates[edge.Key.Item2]=Get(edge.Key.Item2);if(ev!=null)trace!.End(ev,"ok",null,null);return;
                    default:await Waiting(scope,custom(id,scope,this));break;
                }
                external.ThrowIfCancellationRequested();
                if(Volatile.Read(ref stopped)!=0)throw new OperationCanceledException(lifetime.Token);
                scope.CancellationToken.ThrowIfCancellationRequested();
                if(ev!=null)trace!.End(ev,"ok",port,null);
            } catch(OperationCanceledException) when(scope.CancellationToken.IsCancellationRequested || external.IsCancellationRequested || Volatile.Read(ref stopped)!=0){ev??=Begin();if(ev!=null)trace!.End(ev,kind=="stop"?"stopped":"cancelled",null,null);throw;}
            catch(Exception error) {
                ev??=Begin();
                if(Next(id,"error") is not {} recovery){if(ev!=null)trace!.End(ev,"failed",null,error);throw;}
                scope.LastError=error;next=recovery;
                if(ev!=null)trace!.End(ev,"error","error",error);
            }
            id=next;
        }
    }
    /// <summary>
    /// Works out the value nodes <paramref name="step"/> reads, in order, in its scope. Each counts as a step and honours
    /// cancellation; a failure is the step's own, named after the value node and the step.
    /// </summary>
    async Task Prepare(string step,string[] pure,VisualScope scope) {
        foreach(var id in pure) {
            external.ThrowIfCancellationRequested();
            if(Volatile.Read(ref stopped)!=0)throw new OperationCanceledException(lifetime.Token);
            scope.CancellationToken.ThrowIfCancellationRequested();
            var ev=trace==null?null:scope.Trace=trace.Start(scope.Frame,id);
            try {
                if(Interlocked.Increment(ref steps)>100000)throw new InvalidOperationException($"Node {id}: graph execution limit 100000 steps exceeded.");
                await Evaluate(id,scope);
                if(ev!=null)trace!.End(ev,"ok",null,null);
            } catch(OperationCanceledException) when(scope.CancellationToken.IsCancellationRequested || external.IsCancellationRequested || Volatile.Read(ref stopped)!=0){if(ev!=null)trace!.End(ev,"cancelled",null,null);throw;}
            catch(Exception error) {
                if(ev!=null)trace!.End(ev,"failed",null,error);
                throw new InvalidOperationException($"{Title(id)} (used by {Title(step)}): {error.Message}",error);
            }
        }
    }
    async Task Evaluate(string id,VisualScope scope) {
        var n=nodes[id];var kind=Text(n,"kind");var cfg=n["config"]!;
        object? Get(string p)=>Input(scope,id,p);
        switch(kind) {
            case "and":case "or":case "xor":var left=Input<bool>(scope,id,"left");var right=Input<bool>(scope,id,"right");Output(scope,id,kind=="and"?left&&right:kind=="or"?left||right:left^right);break;
            case "not":Output(scope,id,!Input<bool>(scope,id,"value"));break;
            case "compare":
                if(!comparisonTypes.TryGetValue(id,out var common))throw new InvalidOperationException($"Node {id}: compiled comparison type is missing.");
                var clr=types[VisualTypes.RootId(catalog,common)];var a=Coerce(Read(scope,id,"left"),clr,id+".left",false);var b=Coerce(Read(scope,id,"right"),clr,id+".right",false);
                if(scope.Trace!=null){Traced(scope,id,"left",a);Traced(scope,id,"right",b);}
                Output(scope,id,Compare(a,b,Text(cfg,"operator","eq")));break;
            case "constant":case "convert":if(compiledConversions.Contains(id))await custom(id,scope,this);else Output(scope,id,ConvertTo(Get("value"),Text(cfg,"type"),id+".value"));break;
            case "entity_field":Output(scope,id,EntityField(Input<EntityStateSnapshot>(scope,id,"entity"),cfg,id));break;
            case "object":case "array":Output(scope,id,ConvertTo(Get("value"),kind,id,false));break;
            case "object_create":Output(scope,id,JsonSerializer.SerializeToNode((cfg["fields"] as JsonObject??[]).ToDictionary(p=>p.Key,p=>Get("field:"+p.Key))));break;
            case "array_create":Output(scope,id,JsonSerializer.SerializeToNode((cfg["items"] as JsonArray??[]).Select((_,i)=>Get("item:"+i)).ToArray()));break;
            case "get_field":var json=Input<JsonElement>(scope,id,"value");var pointer=Text(cfg,"pointer");foreach(var segment in pointer.Split('/').Skip(1)){var part=segment.Replace("~1","/").Replace("~0","~");json=json.ValueKind==JsonValueKind.Array?json[int.Parse(part,CultureInfo.InvariantCulture)]:json.GetProperty(part);}Output(scope,id,ConvertTo(json,Text(cfg,"type"),id,false));break;
            // A value node from a library (or any other kind) is compiled.
            default:await custom(id,scope,this);break;
        }
    }
    static EntityStateSnapshot ReadEntity(string entity,bool fresh=false) {
        var triggerState=UserScriptManager.CurrentTrigger.Value?.State;
        var raw=!fresh && triggerState?.EntityId==entity ? triggerState : PyInterop.Entity(entity);
        return EntityStateSnapshot.From(raw??throw new InvalidOperationException($"Entity '{entity}' is unavailable."));
    }
    // Contract C6: each typed output reads its pointer from the normalised payload. A pointer that finds
    // nothing or JSON null, or a value of another type, produces no output; only a consumer fails, naming it.
    void Trigger(string id,VisualScope scope,JsonObject node) {
        foreach(var (port,spec) in VisualNodeCatalog.TriggerOutputs(node,catalog)) {
            if(trigger.ValueKind==JsonValueKind.Undefined || !TryPointer(trigger,Text(spec,"pointer"),out var value) || value.ValueKind==JsonValueKind.Null) {absentTriggerOutputs[(id,port)]=null;continue;}
            var type=Text(spec,"type");var label=id+"."+port;
            object? output;
            try {output=scope.Outputs[(id,port)]=types.GetValueOrDefault(VisualTypes.RootId(catalog,type))==typeof(EntityStateSnapshot)?EntityStateSnapshot.FromStateJson(value):ConvertTo(value,type,label,false);}
            catch(Exception error) when(error is ArgumentException or InvalidOperationException){absentTriggerOutputs[(id,port)]=error.Message;continue;}
            if(scope.Trace!=null)trace!.Output(scope.Trace,id,port,output);
        }
    }
    // A node about to wait stores the trace first (C10), so the running event is readable during the wait.
    Task Waiting(VisualScope scope,Task task) {if(scope.Trace!=null && !task.IsCompleted)trace!.Waiting();return task;}
    Task<T> Waiting<T>(VisualScope scope,Task<T> task) {if(scope.Trace!=null && !task.IsCompleted)trace!.Waiting();return task;}
    static bool TryPointer(JsonElement json,string pointer,out JsonElement value) {
        value=json;
        foreach(var segment in pointer.Split('/').Skip(1)) {
            var part=segment.Replace("~1","/").Replace("~0","~");
            if(value.ValueKind==JsonValueKind.Object && value.TryGetProperty(part,out var child))value=child;
            else if(value.ValueKind==JsonValueKind.Array && int.TryParse(part,NumberStyles.None,CultureInfo.InvariantCulture,out var index) && index<value.GetArrayLength())value=value[index];
            else return false;
        }
        return true;
    }
    object? EntityField(EntityStateSnapshot entity,JsonNode cfg,string id) {
        object? value=Text(cfg,"field","state")=="state"?entity.State:entity.Attributes.GetProperty(Text(cfg,"attribute"));
        return ConvertTo(value,Text(cfg,"type"),id+".entity");
    }
    static bool Compare(object? a,object? b,string op) {
        if(op is "eq" or "ne") {var equal=a is JsonNode an&&b is JsonNode bn?JsonNode.DeepEquals(an,bn):a is JsonElement x&&b is JsonElement y?JsonElement.DeepEquals(x,y):Equals(a,b);return op=="eq"?equal:!equal;}
        var left=Convert.ToDouble(a,CultureInfo.InvariantCulture);var right=Convert.ToDouble(b,CultureInfo.InvariantCulture);
        return op switch{"lt"=>left<right,"le"=>left<=right,"gt"=>left>right,"ge"=>left>=right,_=>throw new InvalidOperationException("Unknown comparison")};
    }
    // An optional input contributes only when it is wired or its operand is not unset.
    bool Supplied(string id,string port,JsonNode? operand)=>data.ContainsKey((id,port)) || operand is JsonObject && Text(operand,"kind")!="unset";
    static JsonNode? Json(object? value)=>value switch{null=>null,JsonNode node=>node.DeepClone(),JsonElement element=>JsonNode.Parse(element.GetRawText()),_=>JsonSerializer.SerializeToNode(value)};
    /// <summary>
    /// Contract C5: fields by name, then the target (a string is an entity_id, an object is flattened); a response only
    /// with returnResponse. A target key that a field already set is an error, as the validator reports, never a silent overwrite.
    /// </summary>
    async Task Service(string id,VisualScope scope,JsonNode cfg) {
        var payload=new JsonObject();
        foreach(var (name,operand) in cfg["fields"] as JsonObject??[])if(Supplied(id,"field:"+name,operand))payload[name]=Json(Input(scope,id,"field:"+name));
        if(Supplied(id,"target",cfg["target"])) {
            var target=Json(Input(scope,id,"target"));
            // A target object holds only the five target keys (the validators say the same of a target value); a field
            // with one of them is a validation error, so the collision check below is only a defence.
            void Set(string name,JsonNode? value){if(payload.ContainsKey(name))throw new InvalidOperationException($"{Text(cfg,"service")} sets {name} both as a field and in the target");payload[name]=value;}
            if(target is JsonValue entity && entity.TryGetValue<string>(out var entityId))Set("entity_id",entityId);
            else if(target is JsonObject properties)foreach(var (name,value) in properties) {
                if(!VisualGraphValidator.TargetKeys.Contains(name))throw new InvalidOperationException(VisualGraphValidator.TargetKeyMessage(name));
                Set(name,value?.DeepClone());
            }
            else throw new InvalidOperationException($"Input {id}.target requires an entity id or a target object.");
        }
        var service=Text(cfg,"service").Split('.');
        if(cfg["returnResponse"] is JsonValue flag && flag.TryGetValue<bool>(out var returnResponse) && returnResponse) {
            var response=await Waiting(scope,HassServices.CallServiceAsync<JsonElement>(service[0],service[1],payload).WaitAsync(scope.CancellationToken));
            Output(scope,id,ConvertTo(response,"object",id+".response",false),"response");
        } else await Waiting(scope,HassServices.CallServiceAsync(service[0],service[1],payload).WaitAsync(scope.CancellationToken));
    }
    async Task Parallel(string id,VisualScope parent,int depth) {
        using var cts=new CancellationTokenSource();
        Task cancelCleanup=Task.CompletedTask;using var registration=parent.CancellationToken.Register(()=>{cancelCleanup=cts.CancelAsync();});
        var branches=nodes[id]["config"]!["branches"]!.AsArray().Select(b=> {
            var branch=b!.GetValue<string>();var scope=parent.Clone(cts.Token);
            if(trace!=null)scope.Frame=VisualTrace.Frame(parent.Frame,id+":branch:"+branch);
            return (Start:Next(id,"branch:"+branch),Scope:scope);
        }).ToArray();
        var failures=new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        async Task Branch(string? start,VisualScope scope) {
            try {await Walk(start,scope,depth+1);}
            catch(Exception error) {if(error is not OperationCanceledException || !cts.IsCancellationRequested)failures.Enqueue(error);try{await cts.CancelAsync();}catch(Exception cleanup){failures.Enqueue(cleanup);}throw;}
        }
        var tasks=branches.Select(b=>Branch(b.Start,b.Scope)).ToArray();
        try {await Task.WhenAll(tasks);}catch {parent.CancellationToken.ThrowIfCancellationRequested();if(failures.Count==1)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures.Single()).Throw();if(!failures.IsEmpty)throw new AggregateException("Parallel branches failed after cleanup.",failures);throw;}
        finally {registration.Dispose();try{await cancelCleanup;}catch(Exception cleanup) when(parent.CancellationToken.IsCancellationRequested){if(!external.IsCancellationRequested)stopFailures.Enqueue(cleanup);}}
    }
    async Task Loop(string id,VisualScope parent,int depth) {
        var node=nodes[id];var cfg=node["config"]!;var kind=Text(node,"kind");var max=cfg["maxIterations"]?.GetValue<int>()??1000;
        var state=new Dictionary<string,object?>();var declarations=new Dictionary<string,string>();
        foreach(var group in new[]{"carry","exports"})foreach(var d in (cfg[group] as JsonArray??[]).OfType<JsonObject>()) {
            var port=(group=="carry"?"carry:":"export:")+Text(d,"name");var type=Text(d,"type");declarations[port]=type;state[port]=ConvertTo(Operand(parent,d["initial"],id+"."+port),type,id+"."+port,false);
        }
        object?[]? items=null;var count=0;
        if(kind=="repeat") {var n=Input<double>(parent,id,"count");if(!double.IsFinite(n)||n<0||n!=Math.Truncate(n)||n>max)throw new InvalidOperationException($"Loop {id}: count {n} exceeds maximum {max} or is not a nonnegative integer.");count=(int)n;}
        if(kind=="foreach") {
            var source=Input(parent,id,"items");var copy=new VisualSnapshot();
            items=source switch{Array a when a.Rank==1=>a.Cast<object?>().Select(copy.Copy).ToArray(),JsonArray j=>j.Select(v=>v==null?null:(object?)JsonSerializer.SerializeToElement(v)).ToArray(),JsonElement j when j.ValueKind==JsonValueKind.Array=>j.EnumerateArray().Select(v=>(object?)v.Clone()).ToArray(),_=>throw new InvalidOperationException($"Loop {id}: items requires a CLR array or JSON array.")};
            if(items.Length>max)throw new InvalidOperationException($"Loop {id}: {items.Length} items exceed maximum {max}.");
            for(var i=0;i<items.Length;i++)items[i]=ConvertTo(items[i],Text(cfg,"itemType"),id+".item["+i+"]",false);count=items.Length;
        }
        VisualScope Scope(int iteration,string part) {
            var scope=parent.Clone(parent.CancellationToken);var copy=new VisualSnapshot();foreach(var pair in state.Where(p=>p.Key.StartsWith("carry:")))scope.Outputs[(id,pair.Key)]=copy.Copy(pair.Value);
            if(trace!=null)scope.Frame=VisualTrace.Frame(parent.Frame,id+"#"+iteration.ToString(CultureInfo.InvariantCulture)+":"+part);
            return scope;
        }
        async Task<bool> Condition(int iteration) {var condition=Scope(iteration,"condition");await Walk(Next(id,"condition"),condition,depth+1);return condition.Condition??throw new InvalidOperationException($"Loop {id}: condition path did not return condition_result.");}
        for(var iteration=0;;iteration++) {
            parent.CancellationToken.ThrowIfCancellationRequested();
            if(kind is "repeat" or "foreach" && iteration>=count)break;
            if(kind=="while"&&!await Condition(iteration))break;
            if(iteration>=max)throw new InvalidOperationException($"Loop {id}: maximum {max} iterations exceeded.");
            var body=Scope(iteration,"body");if(items!=null){body.Outputs[(id,"item")]=new VisualSnapshot().Copy(items[iteration]);body.Outputs[(id,"index")]=(double)iteration;}
            await Walk(Next(id,"body"),body,depth+1);
            if(declarations.Count>0&&body.Updates==null)throw new InvalidOperationException($"Loop {id}: body did not return loop_result for declared carry/exports.");
            if(body.Updates!=null){var updates=new Dictionary<string,object?>();var copy=new VisualSnapshot();foreach(var pair in body.Updates)updates[pair.Key]=copy.Copy(ConvertTo(pair.Value,declarations[pair.Key],id+"."+pair.Key,false));foreach(var pair in updates)state[pair.Key]=pair.Value;}
            if(kind=="until"&&await Condition(iteration))break;
        }
        foreach(var pair in state.Where(p=>p.Key.StartsWith("export:")))Output(parent,id,pair.Value,pair.Key);
    }
}
