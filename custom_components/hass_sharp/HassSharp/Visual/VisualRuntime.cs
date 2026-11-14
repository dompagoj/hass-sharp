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
        // A value nested very deeply (built by node code) fails its reader instead of overflowing the stack.
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
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

/// <summary>
/// The values one path of a run has made. An inner scope (a loop iteration or condition, a parallel branch) starts
/// with none of its own: the first time it reads a value of the scope around it, it takes a copy (see
/// <see cref="TryGetOutput"/>), so a value it never reads is never copied and cannot fail it.
/// </summary>
public sealed class VisualScope
{
    /// <summary>The values this scope made or copied. Write through <see cref="SetOutput"/>, read through <see cref="TryGetOutput"/>.</summary>
    internal Dictionary<(string Node,string Port),object?> Outputs {get;}=[];
    // The scope around this one, which waits while this one runs, and the copier of what this scope takes from it: one
    // identity map, so values that alias each other outside alias each other's copies inside.
    VisualScope? outer;
    VisualSnapshot? copies;
    // Parallel branches take copies from the scope they share at the same time.
    readonly object gate=new();
    /// <summary>The most recent failure on this path, kept for the error path, Error details and <see cref="NodeContext.Error"/>.</summary>
    public Exception? LastError {get;set;}
    /// <summary>The name of the step that failed with <see cref="LastError"/>, as the canvas shows it.</summary>
    internal string? LastErrorStep;
    public CancellationToken CancellationToken {get;internal set;}
    internal bool? Condition;
    internal Dictionary<string,object?>? Updates;
    /// <summary>Trace frame (C10): "" at top level, else parallel branch and loop iteration segments. Set only while traced.</summary>
    internal string Frame="";
    /// <summary>The traced event of the node running in this scope; null when the run is not traced.</summary>
    internal VisualTrace.Event? Trace;
    /// <summary>
    /// An inner scope. Isolation holds as with a copy of every value up front: a value is copied when this scope or an
    /// inner one first reads it, from the outer scope's own value, and the outer scope does not run (nor change its
    /// values) until its inner scopes have ended; each inner scope changes only its own copies.
    /// </summary>
    internal VisualScope Clone(CancellationToken token)=>new(){CancellationToken=token,LastError=(Exception?)new VisualSnapshot().Copy(LastError),LastErrorStep=LastErrorStep,Frame=Frame,outer=this};
    /// <summary>
    /// The value of a step output on this path: this scope's own, else the outer scope's (which takes its own copy
    /// first, so all of a scope's copies come from one identity map), copied into this scope.
    /// </summary>
    internal bool TryGetOutput((string Node,string Port) key,out object? value) {
        lock(gate) {
            if(Outputs.TryGetValue(key,out value))return true;
            if(outer==null || !outer.TryGetOutput(key,out var around)){value=null;return false;}
            value=Outputs[key]=(copies??=new VisualSnapshot()).Copy(around);
            return true;
        }
    }
    internal void SetOutput((string Node,string Port) key,object? value) {lock(gate)Outputs[key]=value;}
}

/// <summary>
/// One engine per invocation. Data reads never schedule a step; the value nodes a step reads are worked out in its
/// scope just before it runs, every time it runs.
/// </summary>
public sealed class VisualRuntime
{
    /// <summary>The step each unrecovered failure came from (the innermost), so Error details names it after a loop or parallel recovers.</summary>
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<Exception,string> failedSteps=new();
    readonly Dictionary<string,JsonObject> nodes;
    readonly Dictionary<(string,string),string> flow=[];
    readonly Dictionary<(string,string),(string,string)> data=[];
    readonly Dictionary<string,Type> types;
    readonly JsonObject catalog;
    readonly Func<string,VisualScope,VisualRuntime,Task> custom;
    readonly HashSet<string> compiledConversions;
    readonly Dictionary<string,string> comparisonTypes;
    readonly JsonElement trigger;
    /// <summary>What values of the automation's own types are written to and read from JSON with (see <see cref="JsonOptions"/>).</summary>
    readonly JsonSerializerOptions serializer;
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
    public VisualRuntime(string graphJson,string catalogJson,Dictionary<string,Type> types,JsonElement trigger,CancellationToken cancellation,Func<string,VisualScope,VisualRuntime,Task> custom,HashSet<string>? compiledConversions=null,Dictionary<string,string>? comparisonTypes=null,JsonSerializerOptions? json=null) {
        serializer=json??JsonOptions();
        this.comparisonTypes=comparisonTypes??[];
        this.compiledConversions=compiledConversions??[];
        var graph=JsonNode.Parse(graphJson)!.AsObject();catalog=JsonNode.Parse(catalogJson)!.AsObject();this.types=types;this.custom=custom;this.trigger=trigger;external=cancellation;lifetime=new CancellationTokenSource();externalRegistration=cancellation.Register(()=>{externalCleanup=lifetime.CancelAsync();});
        nodes=graph["nodes"]!.AsArray().OfType<JsonObject>().ToDictionary(n=>Text(n,"id"));
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
    /// <summary>
    /// Serializer options for one automation to hold (a static of its generated class, so in its own load context).
    /// System.Text.Json caches the metadata of every type it meets in the options it is given, and shares that cache
    /// between options it finds equal, so <see cref="JsonSerializerOptions.Default"/> would keep the automation's own
    /// types (a library record made an object, a node's enum), and with them its load context, for the life of the
    /// process. A resolver of its own makes these options equal to no other; they behave as the default ones.
    /// </summary>
    public static JsonSerializerOptions JsonOptions()=>new(){TypeInfoResolver=new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()};
    JsonNode? Spec(string id)=>catalog["nodes"]?[Text(nodes[id],"kind")];
    bool Pure(string id)=>VisualNodeCatalog.IsPure(nodes[id],Spec(id));
    // A Custom C# node's pins are its signature's; every other node's are its kind's.
    JsonNode? InputSpecs(string id)=>VisualNodeCatalog.CustomSignature(nodes[id],Spec(id)) is {} signature?signature["inputs"]:Spec(id)?["inputs"];
    JsonNode? OutputSpecs(string id)=>VisualNodeCatalog.CustomSignature(nodes[id],Spec(id)) is {} signature?signature["outputs"]:Spec(id)?["outputs"];
    // The name people see on the canvas, for messages about a value node and the step that used it.
    string Title(string id)=>VisualGraphValidator.Words.StepName(nodes[id],Spec(id));
    static string Text(JsonNode? node,string key,string fallback="")=>VisualNodeCatalog.Text(node,key,fallback);
    /// <summary>A step's input as the canvas names it: "Delay · Seconds".</summary>
    string Pin(string id,string port)=>Title(id)+" · "+VisualGraphValidator.Words.PortLabel(InputSpecs(id)?[port],port);
    string? Next(string node,string port="next")=>flow.GetValueOrDefault((node,port));
    public async Task RunAsync(string root) {
        trace=VisualTrace.Create(root,nodes.Keys,TraceInputType,TraceOutputType);
        try {await Walk(root,new VisualScope{CancellationToken=lifetime.Token},0);external.ThrowIfCancellationRequested();}
        catch(OperationCanceledException) when(Volatile.Read(ref stopped)!=0 && !external.IsCancellationRequested) {if(InvocationContext.Current is {} run)run.Stopped=true;}
        finally {trace?.Complete();externalRegistration.Dispose();try{await externalCleanup;}catch when(external.IsCancellationRequested){}lifetime.Dispose();}
        if(!stopFailures.IsEmpty)throw new AggregateException("Stop cleanup failed after all branches drained.",stopFailures);
    }
    // Values are traced as the node sees them: after conversion, with the wire they came from.
    public T Input<T>(VisualScope scope,string node,string port) {var value=(T)Coerce(Read(scope,node,port),typeof(T),$"{node}.{port}",false,()=>Title(node)+" · "+VisualGraphValidator.Words.PortLabel(InputSpecs(node)?[port],port),serializer)!;if(scope.Trace!=null)Traced(scope,node,port,value);return value;}
    /// <summary>What a node method's <see cref="NodeContext"/> parameter gets: this run, as <paramref name="scope"/> sees it.</summary>
    public NodeContext Context(VisualScope scope,string node)=>new(this,scope,node,InvocationContext.Current?.RunId??"",trigger);
    public object? Input(VisualScope scope,string node,string port) {var value=Read(scope,node,port);if(scope.Trace!=null)Traced(scope,node,port,value);return value;}
    void Traced(VisualScope scope,string node,string port,object? value)=>trace!.Input(scope.Trace!,node,port,value,data.TryGetValue((node,port),out var source)?source:null);
    object? Read(VisualScope scope,string node,string port) {
        if(data.TryGetValue((node,port),out var source)) {
            if(!scope.TryGetOutput(source,out var value))throw new InvalidOperationException(absentTriggerOutputs.TryGetValue(source,out var reason)
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
        if(operand==null || Text(operand,"kind")=="unset")operand=InputSpecs(node)?[port]?["default"];
        return operand;
    }
    // The declared type a trace shows for a port: the producer's output type for a wire, else the operand's.
    string? TraceInputType(string node,string port) {
        if(data.TryGetValue((node,port),out var source))return TraceOutputType(source.Item1,source.Item2);
        var operand=OperandOf(node,port);
        if(Text(operand,"kind")=="literal" && Text(operand,"type") is {Length:>0} literal)return literal;
        return InputSpecs(node)?[port]?["types"] is JsonArray {Count:1} types && types[0] is JsonValue only && only.TryGetValue<string>(out var type)?type:null;
    }
    string? TraceOutputType(string node,string port) {
        var n=nodes[node];var kind=Text(n,"kind");var cfg=n["config"];
        if(kind=="trigger")return VisualNodeCatalog.TriggerOutputs(n,catalog)[port] is JsonObject spec?Text(spec,"type"):null;
        if(port.Split(':') is [var group and ("carry" or "export"),var name])
            return (cfg?[group=="carry"?"carry":"exports"] as JsonArray??[]).OfType<JsonObject>().FirstOrDefault(d=>Text(d,"name")==name) is {} declaration?Text(declaration,"type"):null;
        // A Select property node's output has the chosen property's type, resolved for the graph under resolvedOutputs.
        var declared=VisualNodeCatalog.PropertyOf(OutputSpecs(node)?[port])!=""?Text(catalog["resolvedOutputs"]?[node],port):Text(OutputSpecs(node)?[port],"type");
        if(declared.StartsWith('$'))declared=Text(cfg,declared[1..]);
        return declared==""?null:declared;
    }
    object? Operand(VisualScope scope,JsonNode? operand,string label) {
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
        return JsonSerializer.Deserialize(value.ToJsonString(),clr,serializer);
    }
    public void Output(VisualScope scope,string node,object? value,string port="value") {
        var declared=Text(OutputSpecs(node)?[port],"type");
        if(declared.StartsWith('$'))declared=Text(nodes[node]["config"],declared[1..]);
        if(declared!="")value=ConvertTo(value,declared,node+"."+port,false);
        scope.SetOutput((node,port),value);
        if(scope.Trace!=null)trace!.Output(scope.Trace,node,port,value);
    }
    /// <param name="pin">The step and pin, as people see them, for a number that does not fit the pin's type.</param>
    /// <param name="json">The automation's serializer options (<see cref="JsonOptions"/>), for a value of its own types.</param>
    public static object? Coerce(object? value,Type type,string label,bool convert,Func<string>? pin=null,JsonSerializerOptions? json=null) {
        json??=JsonSerializerOptions.Default;
        var underlying=Nullable.GetUnderlyingType(type);var target=underlying??type;
        if(value==null) {if(!type.IsValueType||underlying!=null)return null;throw new InvalidOperationException($"{pin?.Invoke()??label} needs a value, and got none");}
        if(type.IsInstanceOfType(value))return value;
        if(type==typeof(JsonElement)&&value is JsonNode jsonNode)return JsonSerializer.SerializeToElement(jsonNode);
        if(convert && target==typeof(JsonElement))return JsonSerializer.SerializeToElement(value,json);
        if(target==typeof(JsonObject)||target==typeof(JsonArray)) {
            var parsed=value is JsonElement element?JsonNode.Parse(element.GetRawText()):convert?JsonSerializer.SerializeToNode(value,json):null;
            if(parsed!=null && target.IsInstanceOfType(parsed))return parsed;
        }
        if(value is JsonElement j) {
            if(j.ValueKind==JsonValueKind.Null)return Coerce(null,type,label,convert,json:json);
            if(j.ValueKind==JsonValueKind.Number && IsNumber(target)) {
                // A JSON number (a foreach item, a field read from JSON) gets the same fit check as any other number.
                if(pin!=null && target!=typeof(char) && Unfit(j.TryGetDecimal(out var exact)?exact:j.GetDouble(),target) is {} unfit)
                    throw new InvalidOperationException($"{pin()} needs {unfit}, not {j.GetRawText()}");
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
            value=j.ValueKind switch{JsonValueKind.String=>j.GetString(),JsonValueKind.Number=>j.TryGetDouble(out var real)&&double.IsFinite(real)?real:throw new InvalidOperationException($"{pin?.Invoke()??label} got {j.GetRawText()}, a number too large to use"),JsonValueKind.True=>true,JsonValueKind.False=>false,_=>value};
            if(type.IsInstanceOfType(value))return value;
        }
        // C# char widening uses the UTF-16 code unit; IConvertible's char-to-double
        // implementation does not implement that standard language conversion.
        if(value is char character && IsNumber(target) && target!=typeof(char))value=(ushort)character;
        if(convert && target==typeof(bool))return VisualValues.ConvertValue(value,"boolean");
        if(convert && target==typeof(string))return VisualValues.ConvertValue(value,"string");
        if(pin!=null && value is not null and not char && IsNumber(value.GetType()) && IsNumber(target) && target!=typeof(char) && Unfit(value,target) is {} range)
            throw new InvalidOperationException($"{pin()} needs {range}, not {Convert.ToString(value,CultureInfo.InvariantCulture)}");
        if(value is IConvertible && (convert || IsNumber(value.GetType())&&IsNumber(target))) {
            try {return target.IsEnum?Enum.Parse(target,Convert.ToString(value,CultureInfo.InvariantCulture)!):Convert.ChangeType(value,target,CultureInfo.InvariantCulture);}catch(Exception e)when(e is FormatException or InvalidCastException or OverflowException){throw new InvalidOperationException($"Cannot convert {label} to {type}.",e);}
        }
        throw new InvalidOperationException($"{label} requires {type}, got {value?.GetType()}.");
    }
    static bool IsNumber(Type t)=>Type.GetTypeCode(t) is TypeCode.Char or >=TypeCode.SByte and <=TypeCode.Decimal;
    /// <summary>What a number pin of type <paramref name="target"/> takes, when <paramref name="value"/> is not that (a fraction for a whole number, or out of range); else null.</summary>
    static string? Unfit(object value,Type target) {
        static string N(double d)=>VisualGraphValidator.Words.Number(d);
        var code=Type.GetTypeCode(target);
        if(code is >=TypeCode.SByte and <=TypeCode.UInt64) {
            var (min,max)=code switch{TypeCode.SByte=>((decimal)sbyte.MinValue,(decimal)sbyte.MaxValue),TypeCode.Byte=>(byte.MinValue,byte.MaxValue),TypeCode.Int16=>(short.MinValue,short.MaxValue),TypeCode.UInt16=>(ushort.MinValue,ushort.MaxValue),
                TypeCode.Int32=>(int.MinValue,int.MaxValue),TypeCode.UInt32=>(uint.MinValue,uint.MaxValue),TypeCode.Int64=>(long.MinValue,long.MaxValue),_=>(ulong.MinValue,ulong.MaxValue)};
            decimal? exact=value is double or float?(Convert.ToDouble(value,CultureInfo.InvariantCulture) is var d && double.IsFinite(d) && Math.Abs(d)<7.9e28?(decimal)d:null):Convert.ToDecimal(value,CultureInfo.InvariantCulture);
            return exact is {} x && decimal.Truncate(x)==x && x>=min && x<=max?null:$"a whole number from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}";
        }
        if(code==TypeCode.Single)return Convert.ToDouble(value,CultureInfo.InvariantCulture) is var f && (!double.IsFinite(f) || Math.Abs(f)<=float.MaxValue)?null:$"a number from {N(-float.MaxValue)} to {N(float.MaxValue)}";
        if(code==TypeCode.Decimal)return value is decimal || Convert.ToDouble(value,CultureInfo.InvariantCulture) is var m && double.IsFinite(m) && Math.Abs(m)<7.9e28?null:$"a number from {N(-7.9e28)} to {N(7.9e28)}";
        return null;
    }
    /// <param name="pin">The step and pin, as people see them, for a number that does not fit <paramref name="type"/>.</param>
    object? ConvertTo(object? value,string type,string label,bool convert=true,Func<string>? pin=null) {
        var chain=VisualTypes.Chain(catalog,type);
        if(chain.Count==0||!types.TryGetValue(chain[^1],out var clr))throw new InvalidOperationException($"Unknown declared type {type} at {label}");
        var root=chain[^1];var result=Coerce(value,clr,label,convert,pin,serializer);
        if(result==null && !VisualTypes.Nullable(catalog,type))throw new InvalidOperationException($"{label} requires non-null {type}, got null.");
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
                double Number(string p,double min,double max,bool integer=false) {var v=Input<double>(scope,id,p);if(!double.IsFinite(v)||v<min||v>max||integer&&Math.Truncate(v)!=v)throw new InvalidOperationException($"{Pin(id,p)} must be {(integer?"a whole number":"a number")} from {VisualGraphValidator.Words.Number(min)} to {VisualGraphValidator.Words.Number(max)}, not {Convert.ToString(v,CultureInfo.InvariantCulture)}");return v;}
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
                if(Next(id,"error") is not {} recovery){failedSteps.TryAdd(error,Title(id));if(ev!=null)trace!.End(ev,"failed",null,error);throw;}
                // A failure inside a loop body or a parallel branch is the inner step's, not the loop's or the parallel's.
                scope.LastError=error;scope.LastErrorStep=failedSteps.TryGetValue(error,out var inner)?inner:Title(id);next=recovery;
                // Recovered: should a later step throw the same exception again (throw context.Error;), that step failed.
                failedSteps.Remove(error);
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
                // Both sides in the type the generator chose by widening only (C#'s numeric promotion), so neither is rounded.
                var clr=ComparisonTypes.TryGetValue(common,out var widened)?widened:types[VisualTypes.RootId(catalog,common)];
                var givenLeft=Read(scope,id,"left");var givenRight=Read(scope,id,"right");
                var a=Coerce(givenLeft,clr,id+".left",false,()=>Title(id)+" · "+VisualGraphValidator.Words.PortLabel(InputSpecs(id)?["left"],"left"),serializer);
                var b=Coerce(givenRight,clr,id+".right",false,()=>Title(id)+" · "+VisualGraphValidator.Words.PortLabel(InputSpecs(id)?["right"],"right"),serializer);
                // The trace shows the values as they came, not as they were widened.
                if(scope.Trace!=null){Traced(scope,id,"left",givenLeft);Traced(scope,id,"right",givenRight);}
                // As in C#, an ordering with a missing number (a nullable side that is null) is false.
                var op=Text(cfg,"operator","eq");Output(scope,id,op is not ("eq" or "ne")&&(a==null||b==null)?false:Compare(a,b,op));break;
            case "constant":case "convert":if(compiledConversions.Contains(id))await custom(id,scope,this);else Output(scope,id,ConvertTo(Get("value"),Text(cfg,"type"),id+".value",pin:()=>Title(id)+" · "+VisualGraphValidator.Words.PortLabel(InputSpecs(id)?["value"],"value")));break;
            case "entity_field":Output(scope,id,EntityField(Input<EntityStateSnapshot>(scope,id,"entity"),cfg,id));break;
            case "object":case "array":Output(scope,id,ConvertTo(Get("value"),kind,id,false));break;
            case "object_create":Output(scope,id,JsonSerializer.SerializeToNode((cfg["fields"] as JsonObject??[]).ToDictionary(p=>p.Key,p=>Get("field:"+p.Key)),serializer));break;
            case "array_create":Output(scope,id,JsonSerializer.SerializeToNode((cfg["items"] as JsonArray??[]).Select((_,i)=>Get("item:"+i)).ToArray(),serializer));break;
            case "last_error":Output(scope,id,scope.LastError?.Message??"","message");Output(scope,id,scope.LastErrorStep??"","step");break;
            case "get_field":var json=Input<JsonElement>(scope,id,"value");var pointer=Text(cfg,"pointer");foreach(var segment in pointer.Split('/').Skip(1)){var part=segment.Replace("~1","/").Replace("~0","~");json=json.ValueKind==JsonValueKind.Array?json[int.Parse(part,CultureInfo.InvariantCulture)]:json.GetProperty(part);}Output(scope,id,ConvertTo(json,Text(cfg,"type"),id,false,()=>Title(id)+" · "+VisualGraphValidator.Words.PortLabel(OutputSpecs(id)?["value"],"value")));break;
            case "select_property":var property=Text(cfg,"property");Output(scope,id,SelectProperty(Get("value")??throw new InvalidOperationException($"{Title(id)} · {property}: the value is empty"),property,id));break;
            // A value node from a library (or any other kind) is compiled.
            default:await custom(id,scope,this);break;
        }
    }
    /// <summary>
    /// The member a Select property node reads, as the compiler listed it for the wired value's declared type: a public
    /// instance property or field, the most derived one of that name, found from that type down to its bases. Looked up
    /// on each read, never cached by type: a type held across runs would keep its library's load context alive.
    /// </summary>
    object? SelectProperty(object value,string name,string node) {
        var declared=TraceInputType(node,"value") is {} id&&types.TryGetValue(VisualTypes.RootId(catalog,id),out var clr)?Nullable.GetUnderlyingType(clr)??clr:null;
        const BindingFlags Own=BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly;
        for(var type=declared!=null&&declared.IsInstanceOfType(value)?declared:value.GetType();type!=null;type=type.BaseType) {
            if(type.GetProperty(name,Own) is {} member&&member.GetIndexParameters().Length==0&&member.GetMethod is {IsPublic:true} getter)return getter.Invoke(value,null);
            if(type.GetField(name,Own) is {} field)return field.GetValue(value);
        }
        throw new InvalidOperationException($"{Title(node)}: {VisualGraphValidator.Words.TypeLabel(catalog,TraceInputType(node,"value")??"")} has no property {VisualGraphValidator.Words.Name(name)}");
    }
    internal static EntityStateSnapshot ReadEntity(string entity,bool fresh=false) {
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
            try {output=types.GetValueOrDefault(VisualTypes.RootId(catalog,type))==typeof(EntityStateSnapshot)?EntityStateSnapshot.FromStateJson(value):ConvertTo(value,type,label,false);}
            catch(Exception error) when(error is ArgumentException or InvalidOperationException){absentTriggerOutputs[(id,port)]=error.Message;continue;}
            scope.SetOutput((id,port),output);
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
        return ConvertTo(value,Text(cfg,"type"),id+".entity",pin:()=>Title(id)+" · "+VisualGraphValidator.Words.PortLabel(OutputSpecs(id)?["value"],"value"));
    }
    /// <summary>The number types a comparison may be widened to, by the ids the generator writes (with and without <c>?</c>).</summary>
    static readonly Dictionary<string,Type> ComparisonTypes=new[]{typeof(sbyte),typeof(byte),typeof(short),typeof(ushort),typeof(int),typeof(uint),typeof(long),typeof(ulong),typeof(float),typeof(double),typeof(decimal)}
        .SelectMany(t=>new[]{KeyValuePair.Create("global::"+t.FullName,t),KeyValuePair.Create("global::"+t.FullName+"?",typeof(Nullable<>).MakeGenericType(t))}).ToDictionary(StringComparer.Ordinal);
    static bool Compare(object? a,object? b,string op) {
        // NaN compares as C# compares it: equal to nothing (itself included), so eq is false and ne true, and every
        // ordered comparison is false. (CompareTo and Equals would put NaN below every number and equal to itself.)
        static bool NaN(object? v)=>v is double d&&double.IsNaN(d)||v is float f&&float.IsNaN(f);
        if(NaN(a)||NaN(b))return op=="ne";
        if(op is "eq" or "ne") {var equal=a is JsonNode an&&b is JsonNode bn?JsonNode.DeepEquals(an,bn):a is JsonElement x&&b is JsonElement y?JsonElement.DeepEquals(x,y):Equals(a,b);return op=="eq"?equal:!equal;}
        // Two values of one number type compare exactly (a long or decimal past 2^53 keeps its digits); others as doubles.
        var order=a is IComparable same&&b!=null&&a.GetType()==b.GetType()?same.CompareTo(b)
            :Convert.ToDouble(a,CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(b,CultureInfo.InvariantCulture));
        return op switch{"lt"=>order<0,"le"=>order<=0,"gt"=>order>0,"ge"=>order>=0,_=>throw new InvalidOperationException("Unknown comparison")};
    }
    // An optional input contributes only when it is wired or its operand is not unset.
    bool Supplied(string id,string port,JsonNode? operand)=>data.ContainsKey((id,port)) || operand is JsonObject && Text(operand,"kind")!="unset";
    JsonNode? Json(object? value)=>value switch{null=>null,JsonNode node=>node.DeepClone(),JsonElement element=>JsonNode.Parse(element.GetRawText()),_=>JsonSerializer.SerializeToNode(value,serializer)};
    /// <summary>
    /// Contract C5: fields by name, then the target (a string is an entity_id, an object is flattened); a response only
    /// with returnResponse. A target key that a field already set is an error, as the validator reports, never a silent overwrite.
    /// </summary>
    async Task Service(string id,VisualScope scope,JsonNode cfg) {
        var payload=new JsonObject();
        foreach(var (name,operand) in cfg["fields"] as JsonObject??[])if(Supplied(id,"field:"+name,operand))payload[name]=Json(Input(scope,id,"field:"+name));
        var target=Supplied(id,"target",cfg["target"])?Json(Input(scope,id,"target"))??throw new InvalidOperationException($"Input {id}.target requires an entity id or a target object."):null;
        var returnResponse=cfg["returnResponse"] is JsonValue flag && flag.TryGetValue<bool>(out var on) && on;
        var response=await Call(scope,Text(cfg,"service"),payload,target,returnResponse,$"Input {id}.target");
        if(returnResponse)Output(scope,id,ConvertTo(response,"object",id+".response",false),"response");
    }
    /// <summary><see cref="NodeContext.CallService"/>: the call the Call service step makes, from a node method.</summary>
    internal Task<JsonElement?> CallService(VisualScope scope,string node,string service,JsonNode? target,JsonObject? data,bool returnResponse)=>
        Call(scope,service,data==null?new JsonObject():(JsonObject)data.DeepClone(),target?.DeepClone(),returnResponse,"The target");
    /// <summary>
    /// The target into the payload (a string is an entity_id, an object is flattened), then the call; the response only
    /// with returnResponse. A target key that a field already set is an error, as the validator reports, never a silent overwrite.
    /// </summary>
    async Task<JsonElement?> Call(VisualScope scope,string service,JsonObject payload,JsonNode? target,bool returnResponse,string targetLabel) {
        if(target!=null) {
            // A target object holds only the five target keys (the validators say the same of a target value); a field
            // with one of them is a validation error, so the collision check below is only a defence.
            void Set(string name,JsonNode? value){if(payload.ContainsKey(name))throw new InvalidOperationException($"{service} sets {name} both as a field and in the target");payload[name]=value;}
            if(target is JsonValue entity && entity.TryGetValue<string>(out var entityId))Set("entity_id",entityId);
            else if(target is JsonObject properties)foreach(var (key,value) in properties) {
                if(!VisualGraphValidator.TargetKeys.Contains(key))throw new InvalidOperationException(VisualGraphValidator.TargetKeyMessage(key));
                Set(key,value?.DeepClone());
            }
            else throw new InvalidOperationException($"{targetLabel} requires an entity id or a target object.");
        }
        if(service.Split('.') is not [{Length:>0} domain,{Length:>0} name])throw new InvalidOperationException($"Service {service} is not a service id such as light.turn_on.");
        if(returnResponse)return await Waiting(scope,HassServices.CallServiceAsync<JsonElement>(domain,name,payload).WaitAsync(scope.CancellationToken));
        await Waiting(scope,HassServices.CallServiceAsync(domain,name,payload).WaitAsync(scope.CancellationToken));
        return null;
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
        try {await Task.WhenAll(tasks);}catch {parent.CancellationToken.ThrowIfCancellationRequested();if(failures.Count==1)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures.Single()).Throw();if(!failures.IsEmpty){var all=new AggregateException("Parallel branches failed after cleanup.",failures);if(failures.TryPeek(out var firstFailure)&&failedSteps.TryGetValue(firstFailure,out var firstStep))failedSteps.AddOrUpdate(all,firstStep);throw all;}throw;}
        finally {registration.Dispose();try{await cancelCleanup;}catch(Exception cleanup) when(parent.CancellationToken.IsCancellationRequested){if(!external.IsCancellationRequested)stopFailures.Enqueue(cleanup);}}
    }
    async Task Loop(string id,VisualScope parent,int depth) {
        var node=nodes[id];var cfg=node["config"]!;var kind=Text(node,"kind");var max=cfg["maxIterations"]?.GetValue<int>()??1000;
        var state=new Dictionary<string,object?>();var declarations=new Dictionary<string,string>();
        // A kept or exported value is named by the loop and the value's name ("Repeat · Total").
        Func<string> Declared(string port)=>()=>Title(id)+" · "+VisualGraphValidator.Words.PortLabel(null,port);
        foreach(var group in new[]{"carry","exports"})foreach(var d in (cfg[group] as JsonArray??[]).OfType<JsonObject>()) {
            var port=(group=="carry"?"carry:":"export:")+Text(d,"name");var type=Text(d,"type");declarations[port]=type;state[port]=ConvertTo(Operand(parent,d["initial"],id+"."+port),type,id+"."+port,false,Declared(port));
        }
        object?[]? items=null;var count=0;
        if(kind=="repeat") {var n=Input<double>(parent,id,"count");if(!double.IsFinite(n)||n<0||n!=Math.Truncate(n)||n>max)throw new InvalidOperationException($"{Pin(id,"count")} must be a whole number from 0 to {max} (Maximum iterations), not {Convert.ToString(n,CultureInfo.InvariantCulture)}");count=(int)n;}
        if(kind=="foreach") {
            var source=Input(parent,id,"items");var copy=new VisualSnapshot();
            items=source switch{Array a when a.Rank==1=>a.Cast<object?>().Select(copy.Copy).ToArray(),JsonArray j=>j.Select(v=>v==null?null:(object?)JsonSerializer.SerializeToElement(v)).ToArray(),JsonElement j when j.ValueKind==JsonValueKind.Array=>j.EnumerateArray().Select(v=>(object?)v.Clone()).ToArray(),_=>throw new InvalidOperationException($"{Pin(id,"items")} needs a list")};
            if(items.Length>max)throw new InvalidOperationException($"{Pin(id,"items")} has {items.Length} items, more than Maximum iterations ({max})");
            for(var i=0;i<items.Length;i++){var item=i+1;items[i]=ConvertTo(items[i],Text(cfg,"itemType"),id+".item["+i+"]",false,()=>Title(id)+" · "+VisualGraphValidator.Words.PortLabel(InputSpecs(id)?["items"],"items")+", item "+item.ToString(CultureInfo.InvariantCulture));}count=items.Length;
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
            if(iteration>=max)throw new InvalidOperationException($"{Title(id)} stopped at Maximum iterations ({max})");
            var body=Scope(iteration,"body");if(items!=null){body.Outputs[(id,"item")]=new VisualSnapshot().Copy(items[iteration]);body.Outputs[(id,"index")]=(double)iteration;}
            await Walk(Next(id,"body"),body,depth+1);
            if(declarations.Count>0&&body.Updates==null)throw new InvalidOperationException($"Loop {id}: body did not return loop_result for declared carry/exports.");
            if(body.Updates!=null){var updates=new Dictionary<string,object?>();var copy=new VisualSnapshot();foreach(var pair in body.Updates)updates[pair.Key]=copy.Copy(ConvertTo(pair.Value,declarations[pair.Key],id+"."+pair.Key,false,Declared(pair.Key)));foreach(var pair in updates)state[pair.Key]=pair.Value;}
            if(kind=="until"&&await Condition(iteration))break;
        }
        foreach(var pair in state.Where(p=>p.Key.StartsWith("export:")))Output(parent,id,pair.Value,pair.Key);
    }
}
