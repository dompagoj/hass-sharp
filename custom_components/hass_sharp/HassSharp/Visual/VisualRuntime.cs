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
        if(type.IsPrimitive && type != typeof(IntPtr) && type != typeof(UIntPtr) || type.IsEnum || value is string or decimal or DateTime or DateTimeOffset or TimeSpan or Guid or DateOnly or TimeOnly or EntityStateSnapshot or HassSharp.Entities.Entity) return value;
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
    static Exception Cannot(Type type)=>new InvalidOperationException($"{type.FullName} cannot be copied");
}

/// <summary>A value of an outer scope that could not be copied into an inner one; the reading step words it (see <c>VisualRuntime.Read</c>).</summary>
sealed class VisualCopyException(Exception inner):Exception(inner.Message,inner);

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
    /// <summary>How deeply the step running in this scope is nested, as <c>Walk</c> counts it; a flow it runs is one deeper.</summary>
    internal int Depth;
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
            try {value=Outputs[key]=(copies??=new VisualSnapshot()).Copy(around);}
            catch(Exception error)when(error is not VisualCopyException and not OperationCanceledException){throw new VisualCopyException(error);}
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
    // How an entity id becomes the handle a node asks for: a generated automation passes its own, so the handle classes are its own.
    readonly Func<string,HassSharp.Entities.Entity> entities;
    readonly System.Collections.Concurrent.ConcurrentQueue<Exception> stopFailures = new();
    public VisualRuntime(string graphJson,string catalogJson,Dictionary<string,Type> types,JsonElement trigger,CancellationToken cancellation,Func<string,VisualScope,VisualRuntime,Task> custom,HashSet<string>? compiledConversions=null,Dictionary<string,string>? comparisonTypes=null,JsonSerializerOptions? json=null,Func<string,HassSharp.Entities.Entity>? entities=null) {
        serializer=json??JsonOptions();this.entities=entities??(id=>new HassSharp.Entities.Entity(id));
        this.comparisonTypes=comparisonTypes??[];
        this.compiledConversions=compiledConversions??[];
        var graph=JsonNode.Parse(graphJson)!.AsObject();catalog=JsonNode.Parse(catalogJson)!.AsObject();this.types=types;this.custom=custom;this.trigger=trigger;external=cancellation;lifetime=new CancellationTokenSource();externalRegistration=cancellation.Register(()=>{externalCleanup=lifetime.CancelAsync();});
        nodes=graph["nodes"]!.AsArray().OfType<JsonObject>().ToDictionary(n=>Text(n,"id"));
        var sources=new Dictionary<string,List<string>>();
        foreach(var e in graph["edges"]!.AsArray().OfType<JsonObject>()) {
            if(Text(e,"kind")=="data") {
                // A wire into a structural input (an Entity node's Device pin) says what belongs to what and carries no value:
                // it is no value to prepare or read.
                if(nodes.ContainsKey(Text(e,"target"))&&VisualNodeCatalog.Structural((InputSpecs(Text(e,"target")) as JsonObject)?[Text(e,"targetPort")]))continue;
                data[(Text(e,"target"),Text(e,"targetPort"))]=(Text(e,"source"),Text(e,"sourcePort"));
                if(!sources.TryGetValue(Text(e,"target"),out var list))sources[Text(e,"target")]=list=[];list.Add(Text(e,"source"));
            }
            else {var port=Text(e,"port","next");if(Text(nodes[Text(e,"source")],"kind")=="condition" && port=="next")port="true";flow[(Text(e,"source"),port)]=Text(e,"target");}
        }
        // Each step's value nodes, through chains of value nodes, each after the ones it reads. A declaration (a Device node)
        // is no step: it has none, and is never walked (see Walk).
        foreach(var id in nodes.Keys.Where(id=>!Pure(id)&&!Declared(id))) {
            var order=new List<string>();var seen=new HashSet<string>(StringComparer.Ordinal);
            void Visit(string node){foreach(var source in sources.GetValueOrDefault(node)??[])if(Pure(source)&&seen.Add(source)){Visit(source);order.Add(source);}}
            Visit(id);if(order.Count>0)pureInputs[id]=order.ToArray();
        }
        FollowSettings();
    }
    /// <summary>
    /// By output, the Setting nodes whose value it carries, worked out once from the graph: a value worked out from a
    /// secret is secret. A Setting's own output carries its setting; every output of a value node (one with no flow of its
    /// own) carries what its wired inputs carry, except one of true or false or a picture (a text drawn on one is not in
    /// what a run shows of it); so do a For each's item, of its list, and a
    /// loop's kept and exported values, of what the Loop result steps of its body give them, as these hand a value on.
    /// A step's own outputs carry none (a service's response, a node method's results), except the body and headers of an
    /// HTTP request that keeps its response out of run history, which carry that request as a secret setting's value
    /// carries its Setting, and a Download file's path, which carries what its address and checksum carry (the checksum
    /// is part of it). Empty for a graph with neither, as nearly every automation's is. Whether a setting is secret
    /// is not in the graph (it must not change the source): it is asked of the run's definition (<see cref="SecretSetting"/>).
    /// </summary>
    readonly Dictionary<(string Node,string Port),HashSet<string>> settingSources=[];
    // Whether each Setting node's setting is secret, as the run's definition answered; asked once per run.
    readonly Dictionary<string,bool> secretSettings=new(StringComparer.Ordinal);
    void FollowSettings() {
        foreach(var (id,node) in nodes)
            if(Text(node,"kind")=="setting")settingSources[(id,"value")]=new(StringComparer.Ordinal){id};
            else if(HidesResponse(node))foreach(var port in new[]{"body","headers"})settingSources[(id,port)]=new(StringComparer.Ordinal){id};
        if(settingSources.Count==0)return;
        var wired=data.GroupBy(p=>p.Key.Item1,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.Select(p=>(Port:p.Key.Item2,Source:p.Value)).ToArray(),StringComparer.Ordinal);
        var steps=flow.GroupBy(p=>p.Key.Item1,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.Select(p=>p.Value).ToArray(),StringComparer.Ordinal);
        // The Loop result steps a loop's body reaches; one of a loop nested in it too, which can only hide more.
        var results=new Dictionary<string,string[]>(StringComparer.Ordinal);
        foreach(var (id,node) in nodes) {
            if(Text(node,"kind") is not ("repeat" or "while" or "until" or "foreach") || Next(id,"body") is not {} start)continue;
            var seen=new HashSet<string>(StringComparer.Ordinal);var queue=new Queue<string>([start]);var found=new List<string>();
            while(queue.TryDequeue(out var step)) {
                if(!seen.Add(step))continue;
                if(Text(nodes[step],"kind")=="loop_result")found.Add(step);
                foreach(var next in steps.GetValueOrDefault(step)??[])queue.Enqueue(next);
            }
            results[id]=[.. found];
        }
        HashSet<string> Carried((string,string) output)=>settingSources.GetValueOrDefault(output)??[];
        bool changed;
        do {
            changed=false;
            void Add((string Node,string Port) output,IEnumerable<string> from) {
                var settings=from.ToArray();var type=TraceOutputType(output.Node,output.Port);
                if(settings.Length==0 || TrueOrFalse(type) || type=="image")return;
                if(!settingSources.TryGetValue(output,out var carried))settingSources[output]=carried=new(StringComparer.Ordinal);
                foreach(var setting in settings)changed|=carried.Add(setting);
            }
            foreach(var (id,node) in nodes) {
                var kind=Text(node,"kind");var inputs=wired.GetValueOrDefault(id)??[];
                if(kind=="setting")continue;
                if(Pure(id)) {
                    var from=inputs.SelectMany(w=>Carried(w.Source)).ToArray();
                    if(from.Length>0)foreach(var (port,_) in OutputSpecs(id) as JsonObject??[])Add((id,port),from);
                }
                else if(kind=="download_file")Add((id,"path"),inputs.Where(w=>w.Port is "url" or "sha256").SelectMany(w=>Carried(w.Source)));
                else if(results.TryGetValue(id,out var loopResults) || kind=="foreach") {
                    if(kind=="foreach")Add((id,"item"),inputs.Where(w=>w.Port=="items").SelectMany(w=>Carried(w.Source)));
                    foreach(var result in loopResults??[])
                        foreach(var w in wired.GetValueOrDefault(result)??[])
                            if(w.Port.StartsWith("carry:",StringComparison.Ordinal)||w.Port.StartsWith("export:",StringComparison.Ordinal))Add((id,w.Port),Carried(w.Source));
                }
            }
        } while(changed);
    }
    /// <summary>Whether a declared type is true or false (<c>boolean</c>, or a type that is a <see cref="bool"/>).</summary>
    bool TrueOrFalse(string? type)=>type=="boolean" || type!=null && types.TryGetValue(VisualTypes.RootId(catalog,type),out var clr) && (Nullable.GetUnderlyingType(clr)??clr)==typeof(bool);
    /// <summary>
    /// Whether the setting of the Setting node <paramref name="node"/> is secret, as the run's definition answers (once
    /// per run); false without a definition, whose Setting fails before anything reads it. A definition that cannot
    /// answer is taken to have said yes: nothing is shown that might be secret.
    /// </summary>
    bool SecretSetting(string node) {
        // An HTTP request is a source only when it hides its response (see FollowSettings).
        if(Text(nodes[node],"kind")=="http_request")return true;
        lock(secretSettings)if(secretSettings.TryGetValue(node,out var known))return known;
        bool secret;
        try {secret=InvocationContext.Current?.Definition?.IsSecret(Text(nodes[node]["config"],"key"))??false;}
        catch(Exception) {return true;}
        lock(secretSettings)secretSettings[node]=secret;
        return secret;
    }
    /// <summary>
    /// Whether <paramref name="value"/>, on <paramref name="output"/>, is secret: it carries a secret setting
    /// (<see cref="settingSources"/>). True or false worked out from a secret is not (one bit does not give a password
    /// away, and a condition's trace stays readable); a Setting's own value is, whatever it is.
    /// </summary>
    bool Secret((string Node,string Port) output,object? value) {
        if(settingSources.Count==0 || !settingSources.TryGetValue(output,out var settings))return false;
        if(value is bool && Text(nodes[output.Node],"kind")!="setting")return false;
        foreach(var setting in settings)if(SecretSetting(setting))return true;
        return false;
    }
    /// <summary>Whether <paramref name="value"/>, read on the input <paramref name="port"/> of <paramref name="node"/>, came over a wire from a secret output (<see cref="Secret"/>).</summary>
    bool SecretInput(string node,string port,object? value)=>settingSources.Count>0 && data.TryGetValue((node,port),out var source) && Secret(source,value);
    /// <summary>Whether <paramref name="output"/> is the body of an HTTP request that uses HEAD, which never gives one.</summary>
    bool HeadBody((string Node,string Port) output)=>output.Port=="body"&&Text(nodes[output.Node],"kind")=="http_request"&&Text(nodes[output.Node]["config"],"method")=="HEAD";
    /// <summary>Whether an HTTP request step keeps its response out of run history: its body and headers are then secret.</summary>
    static bool HidesResponse(JsonObject node)=>Text(node,"kind")=="http_request"&&Flag(node["config"],"hideResponse",false);
    static bool Flag(JsonNode? config,string key,bool fallback)=>config?[key] is JsonValue value&&value.TryGetValue<bool>(out var on)?on:fallback;
    /// <summary>Why a secret value is hidden, as a message says it: it comes from a secret setting, else from a response kept out of run history.</summary>
    const string FromSecretSetting="it comes from a secret setting";
    /// <summary>Why the value on <paramref name="output"/> is hidden (see <see cref="FromSecretSetting"/>), worked out from the sources it carries.</summary>
    string Because((string Node,string Port) output)=>settingSources.TryGetValue(output,out var sources)&&!sources.Any(source=>Text(nodes[source],"kind")=="setting"&&SecretSetting(source))
        ?"it comes from a response kept out of run history":FromSecretSetting;
    /// <summary>Why the value read on the input <paramref name="port"/> of <paramref name="node"/> is hidden: as the output wired to it is.</summary>
    string Because(string node,string port)=>data.TryGetValue((node,port),out var source)?Because(source):FromSecretSetting;
    /// <summary>What a message says in place of the value read on the input <paramref name="port"/> of <paramref name="node"/>, when that value is secret.</summary>
    string HiddenValue(string node,string port)=>"a value that is hidden because "+Because(node,port);
    /// <summary>The failure of a step that could not use a secret value: names the pin, never the value, and keeps no inner failure that might quote it.</summary>
    InvalidOperationException Hidden(string node,string port)=>HiddenAt(Pin(node,port),Because(node,port));
    /// <summary>The failure of a step that could not use a secret value at <paramref name="where"/>, as people see it, hidden <paramref name="because"/>.</summary>
    static InvalidOperationException HiddenAt(string where,string because)=>new($"{where} cannot use the value it was given, which is hidden because {because}");
    /// <summary>
    /// The runtime's secret guard, which every step that works on a value that may be secret goes through: what
    /// <paramref name="work"/> gives, unless it fails (other than by being cancelled) while <paramref name="secret"/> says
    /// of that failure that the value it worked on is secret. Then it fails with what <paramref name="hidden"/> makes
    /// instead, which says where without quoting the value, and keeps no inner failure that might.
    /// </summary>
    static T Guarded<T>(Func<T> work,Func<Exception,bool> secret,Func<Exception> hidden) {
        try {return work();}
        catch(Exception error) when(error is not OperationCanceledException && secret(error)) {throw hidden();}
    }
    /// <summary>
    /// Serializer options for one automation to hold (a static of its generated class, so in its own load context).
    /// System.Text.Json caches the metadata of every type it meets in the options it is given, and shares that cache
    /// between options it finds equal, so <see cref="JsonSerializerOptions.Default"/> would keep the automation's own
    /// types (a library record made an object, a node's enum), and with them its load context, for the life of the
    /// process. A resolver of its own makes these options equal to no other; they behave as the default ones.
    /// </summary>
    /// <remarks>An entity enum is written as Home Assistant's text (<see cref="EnumText"/>) and read from it, wherever it is;
    /// a picture as its short text (<see cref="TraceJson.ImageText"/>), wherever it goes (Create object, Create list, a
    /// service field, Convert), and is never read back.</remarks>
    public static JsonSerializerOptions JsonOptions()=>new(){TypeInfoResolver=new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),Converters={new EntityTextJsonConverter(),new ImageTextJsonConverter()}};
    /// <summary>
    /// Writes a picture as its short text, "Image · 12 KB · 2 drawings": nothing reads its size, nothing is decoded, and
    /// none of its bytes go along. Nothing is read as a picture.
    /// </summary>
    internal sealed class ImageTextJsonConverter:System.Text.Json.Serialization.JsonConverter<SharpImage> {
        public override SharpImage Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)=>throw new JsonException("A picture is not read from JSON");
        public override void Write(Utf8JsonWriter writer,SharpImage value,JsonSerializerOptions options)=>writer.WriteStringValue(TraceJson.ImageText(value));
    }
    /// <summary>
    /// Writes a member of an entity enum as its text (<c>"on"</c>, <c>"other"</c> for the catch-all) and reads it back from the
    /// text, a text no member has (<c>"other"</c> among them) as the catch-all; any other enum is left to the serializer. It
    /// holds nothing itself: the options it is in hold what it makes per type (an automation's own, or a load context's
    /// typed service calls').
    /// </summary>
    internal sealed class EntityTextJsonConverter:System.Text.Json.Serialization.JsonConverterFactory {
        public override bool CanConvert(Type type)=>type.IsEnum&&HassSharp.Entities.EntityText.Marked(type);
        public override System.Text.Json.Serialization.JsonConverter CreateConverter(Type type,JsonSerializerOptions options)=>(System.Text.Json.Serialization.JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(type))!;
        sealed class Converter<T>:System.Text.Json.Serialization.JsonConverter<T> where T:struct,Enum {
            // The member without a text; null for an enum whose members all have one.
            readonly T? other=typeof(T).GetFields(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Static)
                .FirstOrDefault(f=>!f.IsDefined(typeof(HassSharp.Entities.EntityTextAttribute),false)) is {} field?(T)field.GetValue(null)!:null;
            public override T Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)=>reader.TokenType==JsonTokenType.String
                ?HassSharp.Entities.EntityText.Parse(typeof(T),reader.GetString()!) is T member?member:other??throw new JsonException($"{reader.GetString()} is not a value of {TraceJson.TypeName(typeof(T))}")
                :throw new JsonException($"A {TraceJson.TypeName(typeof(T))} is written as Home Assistant's text");
            public override void Write(Utf8JsonWriter writer,T value,JsonSerializerOptions options)=>writer.WriteStringValue(EnumText(value));
        }
    }
    JsonNode? Spec(string id)=>catalog["nodes"]?[Text(nodes[id],"kind")];
    bool Pure(string id)=>VisualNodeCatalog.IsPure(nodes[id],Spec(id));
    /// <summary>Whether the node is a declaration (a Device node): neither a step nor a value, it is never walked or worked out.</summary>
    bool Declared(string id)=>VisualNodeCatalog.Declaration(Spec(id) as JsonObject);
    // A Custom C# node's pins are its signature's; a Set state's or Entity node's its entity's (see EntityInputs); every other node's are its kind's.
    JsonNode? InputSpecs(string id)=>VisualNodeCatalog.CustomSignature(nodes[id],Spec(id)) is {} signature?signature["inputs"]:Text(Spec(id),"dynamicInputs") is "entity" or "declared"?EntityInputs(id):Spec(id)?["inputs"];
    readonly Dictionary<string,JsonObject> entityInputs=[];
    /// <summary>
    /// The pins of a node whose inputs are its entity's state values, as the validators resolve them: from the definition
    /// the catalog carries, or for an Entity node from the type it declares.
    /// </summary>
    JsonObject EntityInputs(string id) {
        lock(entityInputs) {
            if(!entityInputs.TryGetValue(id,out var ports))entityInputs[id]=ports=VisualNodeCatalog.DataInputs(nodes[id],null,VisualCatalogSnapshot.BuiltIn.WithRoot(catalog));
            return ports;
        }
    }
    /// <summary>Whether the step running in <paramref name="scope"/> is being cancelled: the run's own cancellation, a Stop, or its scope's.</summary>
    bool Cancelled(VisualScope scope)=>scope.CancellationToken.IsCancellationRequested || external.IsCancellationRequested || Volatile.Read(ref stopped)!=0;
    /// <summary>Ends the step running in <paramref name="scope"/> when it is being cancelled (see <see cref="Cancelled"/>), as <c>Walk</c> does between steps.</summary>
    void ThrowIfCancelled(VisualScope scope) {
        external.ThrowIfCancellationRequested();
        if(Volatile.Read(ref stopped)!=0)throw new OperationCanceledException(lifetime.Token);
        scope.CancellationToken.ThrowIfCancellationRequested();
    }
    /// <summary>
    /// Marks the entity of the Entity node <paramref name="id"/> unavailable after its state's inputs failed. Should that
    /// fail too (a definition stopping), the state's own failure stays the run's, as its record and log show; this one is logged.
    /// </summary>
    void MarkUnavailable(IDefinitionRuntime definition,string key,string id) {
        try {definition.SetAvailable(key,false);}
        catch(Exception error) {try{Logger.Warn($"Run {InvocationContext.Current?.RunId}: {Title(id)} could not mark {key} unavailable: {error.GetType().Name}: {error.Message}");}catch{}}
    }
    // An Entity node's outputs are the data values of each of its commands (see CommandOutputs).
    JsonNode? OutputSpecs(string id)=>VisualNodeCatalog.CustomSignature(nodes[id],Spec(id)) is {} signature?signature["outputs"]:Text(Spec(id),"dynamicOutputs")=="declared"?CommandOutputs(id):Spec(id)?["outputs"];
    readonly Dictionary<string,JsonObject> commandOutputs=[];
    /// <summary>
    /// The outputs of an Entity node, as the validators resolve them: <c>command:&lt;command&gt;:&lt;value&gt;</c> for each
    /// value of each command of the type it declares.
    /// </summary>
    JsonObject CommandOutputs(string id) {
        lock(commandOutputs) {
            if(!commandOutputs.TryGetValue(id,out var ports))commandOutputs[id]=ports=VisualNodeCatalog.DataOutputs(nodes[id],catalog);
            return ports;
        }
    }
    // The name people see on the canvas, for messages about a value node and the step that used it.
    string Title(string id)=>VisualGraphValidator.Words.StepName(nodes[id],Spec(id));
    static string Text(JsonNode? node,string key,string fallback="")=>VisualNodeCatalog.Text(node,key,fallback);
    /// <summary>A step's input as the canvas names it: "Delay · Seconds".</summary>
    string Pin(string id,string port)=>Title(id)+" · "+VisualGraphValidator.Words.PortLabel(InputSpecs(id)?[port],port);
    string? Next(string node,string port="next")=>flow.GetValueOrDefault((node,port));
    /// <summary>The pin an Entity node at the root of this run starts its flow at (<c>state</c> for its state, which has none); null for any other run.</summary>
    string? startPort;
    /// <param name="root">The node the run starts at.</param>
    /// <param name="port">For an Entity node, which of its flows: <c>state</c>, <c>command:&lt;command&gt;</c> or <c>frame</c>; its trace names it (<c>rootPort</c>).</param>
    public async Task RunAsync(string root,string? port=null) {
        startPort=port;
        trace=VisualTrace.Create(root,nodes.Keys,TraceInputType,TraceOutputType,port:port);
        try {await Walk(root,new VisualScope{CancellationToken=lifetime.Token},0);external.ThrowIfCancellationRequested();}
        catch(OperationCanceledException) when(Volatile.Read(ref stopped)!=0 && !external.IsCancellationRequested) {if(InvocationContext.Current is {} run)run.Stopped=true;}
        finally {trace?.Complete();externalRegistration.Dispose();try{await externalCleanup;}catch when(external.IsCancellationRequested){}lifetime.Dispose();}
        if(!stopFailures.IsEmpty)throw new AggregateException("Stop cleanup failed after all branches drained.",stopFailures);
    }
    // Values are traced as the node sees them: after conversion, with the wire they came from.
    public T Input<T>(VisualScope scope,string node,string port) {
        var given=Read(scope,node,port);
        // A secret the pin cannot take is not quoted (a conversion's messages, and the failures under them, quote it).
        var value=Guarded(()=>(T)Coerce(given,typeof(T),$"{node}.{port}",false,()=>Title(node)+" · "+VisualGraphValidator.Words.PortLabel(InputSpecs(node)?[port],port),serializer,entities)!,
            _=>SecretInput(node,port,given),()=>Hidden(node,port));
        if(scope.Trace!=null)Traced(scope,node,port,value is HassSharp.Entities.Entity handle?handle.EntityId:value);return value;
    }
    /// <summary>What a node method's <see cref="NodeContext"/> parameter gets: this run, as <paramref name="scope"/> sees it.</summary>
    public NodeContext Context(VisualScope scope,string node)=>new(this,scope,node,InvocationContext.Current?.RunId??"",trigger);
    /// <summary>What a node method's flow parameters call, for one run of the step <paramref name="node"/> in <paramref name="scope"/>.</summary>
    public VisualFlows Flows(VisualScope scope,string node)=>new(this,scope,node);
    internal string FlowLabel(string node,string port)=>Text((VisualNodeCatalog.CustomSignature(nodes[node],Spec(node))??Spec(node))?["executionLabels"],port,port);
    internal string StepTitle(string node)=>Title(node);
    /// <summary>
    /// Runs the steps on <paramref name="owner"/>'s <paramref name="port"/> in a scope of their own inside
    /// <paramref name="parent"/>, one level deeper than <paramref name="depth"/>: a loop's body or condition, or a flow of
    /// a node method. <paramref name="seed"/> gives the scope the values it starts with; <paramref name="part"/> names its
    /// trace frame. The scope is returned for what its steps left (a condition result, a loop's updates).
    /// </summary>
    internal async Task<VisualScope> RunScope(string owner,string port,VisualScope parent,int depth,string part,Action<VisualScope>? seed=null,CancellationToken? token=null) {
        var scope=parent.Clone(token??parent.CancellationToken);seed?.Invoke(scope);
        if(trace!=null)scope.Frame=VisualTrace.Frame(parent.Frame,part);
        await Walk(Next(owner,port),scope,depth+1);
        return scope;
    }
    public object? Input(VisualScope scope,string node,string port) {var value=Read(scope,node,port);if(scope.Trace!=null)Traced(scope,node,port,value);return value;}
    // A value from a secret output is written as ***, with the wire it came from (see Secret).
    void Traced(VisualScope scope,string node,string port,object? value) {
        if(!data.TryGetValue((node,port),out var source)){trace!.Input(scope.Trace!,node,port,value,null);return;}
        trace!.Input(scope.Trace!,node,port,value,source,Secret(source,value));
    }
    object? Read(VisualScope scope,string node,string port) {
        if(data.TryGetValue((node,port),out var source)) {
            object? value;
            // A value that cannot be copied into a loop or branch fails the step that reads it there, named as people see it.
            try {if(!scope.TryGetOutput(source,out value))throw new InvalidOperationException(absentTriggerOutputs.TryGetValue(source,out var reason)
                ? Text(nodes[source.Item1],"kind")=="entity" ? AbsentCommandValue(node,source.Item1,source.Item2,reason)
                : $"Input {node}.{port}: trigger output {source.Item1}.{source.Item2} " + (reason==null?"is absent from this invocation's trigger payload.":$"cannot be read from this invocation's trigger payload: {reason}")
                : HeadBody(source) ? $"{Pin(node,port)} reads the body of {Title(source.Item1)}, which uses HEAD and gets none; choose GET to read it"
                : $"Input {node}.{port}: producer output {source.Item1}.{source.Item2} has not executed in this invocation/scope.");}
            catch(VisualCopyException error) {
                throw new InvalidOperationException($"{Pin(node,port)} reads {Title(source.Item1)} · {VisualGraphValidator.Words.PortLabel(OutputSpecs(source.Item1)?[source.Item2],source.Item2)}, which cannot be copied into a loop or parallel branch; return a list or an array instead",error.InnerException);
            }
            return value;
        }
        return Operand(scope,OperandOf(node,port),$"{node}.{port}");
    }
    /// <summary>
    /// A step reading a value of its command's data that the command came without, which its entity could not give
    /// either (see <c>DefinitionHost.Filled</c>), in words for the person who gave the command, without ids: "Set state:
    /// Turn on came without a brightness, and Lamp has none". <paramref name="command"/> is the Entity node whose output
    /// <c>command:&lt;command&gt;:&lt;value&gt;</c> <paramref name="port"/> is.
    /// </summary>
    string AbsentCommandValue(string reader,string command,string port,string? reason) {
        var node=nodes[command];
        var given=port.Split(':')[1];
        var row=VisualNodeCatalog.DeclaredDomain(node,catalog);
        var commandLabel=Text(row?["commands"]?[given],"label",given);
        var value=VisualGraphValidator.Words.PortLabel(CommandOutputs(command)[port],port).ToLowerInvariant();
        // As the run names it: its name, else its key, each trimmed, else Entity.
        var name=VisualGraphValidator.Words.EntityName(node,Spec(command));
        return reason==null
            ? $"{Title(reader)}: {commandLabel} came without {VisualGraphValidator.Words.Article(value)} {value}, and {name} has none"
            : $"{Title(reader)}: {commandLabel} came with {VisualGraphValidator.Words.Article(value)} {value} that cannot be read";
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
        // So does a Setting's, which has its setting's type.
        var declared=VisualNodeCatalog.PropertyOf(OutputSpecs(node)?[port])!=""||kind=="setting"?Text(catalog["resolvedOutputs"]?[node],port):Text(OutputSpecs(node)?[port],"type");
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
        if(effective.IsEnum) {
            // An entity enum is saved as Home Assistant's text ("on"); any other enum as its number.
            if(value is JsonValue saved&&saved.TryGetValue<string>(out var text)&&EnumFromText(effective,text,label,type) is {} member)return member;
            return Enum.ToObject(effective,JsonSerializer.Deserialize(value.ToJsonString(),Enum.GetUnderlyingType(effective))!);
        }
        return JsonSerializer.Deserialize(value.ToJsonString(),clr,serializer);
    }
    /// <summary>
    /// The member of an enum whose members carry Home Assistant's texts (an entity enum, <see cref="HassSharp.Entities.EntityTextAttribute"/>)
    /// that has <paramref name="text"/>; null for an enum without texts. A text no member has fails, naming it and <paramref name="type"/>.
    /// </summary>
    internal static object? EnumFromText(Type enumType,string text,string label,string type)=>
        !HassSharp.Entities.EntityText.Marked(enumType)?null:HassSharp.Entities.EntityText.Parse(enumType,text)??throw new InvalidOperationException($"{label}: {text} is not a value of {type}");
    /// <summary>What a member of an entity enum is written as: its Home Assistant text, <c>"other"</c> for the catch-all; null for an enum without texts.</summary>
    internal static string? EnumText(Enum value)=>HassSharp.Entities.EntityText.Of(value)??(HassSharp.Entities.EntityText.Marked(value.GetType())?"other":null);
    /// <summary>A value as a Log writes it: an entity enum as its text, a picture as a run's trace shows it (never its bytes), anything else as <see cref="Convert.ToString(object, IFormatProvider)"/> gives it.</summary>
    static string LogText(object? value)=>value is Enum member&&EnumText(member) is {} text?text:value is SharpImage image?TraceJson.ImageText(image):Convert.ToString(value,CultureInfo.InvariantCulture)??"null";
    public void Output(VisualScope scope,string node,object? value,string port="value") {
        var declared=Text(OutputSpecs(node)?[port],"type");
        if(declared.StartsWith('$'))declared=Text(nodes[node]["config"],declared[1..]);
        if(declared!="") {
            var given=value;
            value=Guarded(()=>ConvertTo(given,declared,node+"."+port,false),_=>Secret((node,port),given),
                ()=>new InvalidOperationException($"{Title(node)} · {VisualGraphValidator.Words.PortLabel(OutputSpecs(node)?[port],port)} cannot give the value it worked out, which is hidden because {Because((node,port))}"));
        }
        scope.SetOutput((node,port),value);
        if(scope.Trace!=null)trace!.Output(scope.Trace,node,port,value,Secret((node,port),value));
    }
    /// <param name="pin">The step and pin, as people see them, for a number that does not fit the pin's type.</param>
    /// <param name="json">The automation's serializer options (<see cref="JsonOptions"/>), for a value of its own types.</param>
    public static object? Coerce(object? value,Type type,string label,bool convert,Func<string>? pin=null,JsonSerializerOptions? json=null,Func<string,HassSharp.Entities.Entity>? entities=null) {
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
            if(j.ValueKind==JsonValueKind.Null)return Coerce(null,type,label,convert,pin,json,entities);
            if(j.ValueKind==JsonValueKind.Number && IsNumber(target)) {
                // A JSON number (a foreach item, a field read from JSON) gets the same fit check as any other number. One no
                // number holds (1e400, a 400-digit integer from an attribute or a service response) is named as such.
                object given=j.TryGetDecimal(out var exact)?exact:j.TryGetDouble(out var real)&&double.IsFinite(real)?real
                    :throw new InvalidOperationException($"{pin?.Invoke()??label} got {j.GetRawText()}, a number too large to use");
                if(pin!=null && target!=typeof(char) && Unfit(given,target) is {} unfit)
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
        if(value is HassSharp.Entities.Entity handle&&target==typeof(string))return handle.EntityId;
        if(value is string id&&typeof(HassSharp.Entities.Entity).IsAssignableFrom(target)) {
            var made=(entities??(s=>new HassSharp.Entities.Entity(s)))(id);
            return target.IsInstanceOfType(made)?made:throw new InvalidOperationException($"{pin?.Invoke()??label} needs {target.Name}, and {id} is not one");
        }
        if(convert && target.IsEnum && value is string text && EnumFromText(target,text,label,TraceJson.TypeName(type)) is {} member)return member;
        if(convert && target==typeof(bool))return VisualValues.ConvertValue(value,"boolean");
        // Convert to Text of an entity enum gives Home Assistant's text ("on"), the catch-all "other".
        if(convert && target==typeof(string) && value is Enum entityEnum && EnumText(entityEnum) is {} enumText)return enumText;
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
        var root=chain[^1];var result=Coerce(value,clr,label,convert,pin,serializer,entities);
        if(result==null && !VisualTypes.Nullable(catalog,type))throw new InvalidOperationException($"{label} requires non-null {type}, got null.");
        // A refinement narrows only when the value satisfies every pattern in its chain.
        foreach(var id in chain)if(VisualTypes.Resolve(catalog,id)?["pattern"] is JsonValue pattern && result!=null && (result is not string s || !VisualTypes.Matches(s,pattern.GetValue<string>())))throw new InvalidOperationException($"Cannot convert {label} to {type}.");
        if(root=="object" && result is not JsonObject || root=="array" && result is not JsonArray)throw new InvalidOperationException($"{label} requires JSON {type}.");
        if(root=="number"&&result is double number&&!double.IsFinite(number))throw new InvalidOperationException($"{label} requires finite number.");return result;
    }
    async Task Walk(string? id,VisualScope scope,int depth) {
        if(depth>8)throw new InvalidOperationException("Structured nesting limit is 8");
        scope.Depth=depth;
        while(id!=null) {
            external.ThrowIfCancellationRequested();
            if(Volatile.Read(ref stopped)!=0)throw new OperationCanceledException(lifetime.Token);
            scope.CancellationToken.ThrowIfCancellationRequested();
            // No step order wire goes into or out of a declaration and none is a root, so no walk reaches one.
            if(Declared(id))throw new InvalidOperationException($"{Title(id)} is a device and is not part of the run order");
            var n=nodes[id];var kind=Text(n,"kind");var cfg=n["config"]!;string? next=Next(id);
            VisualTrace.Event? ev=null;var port="next";
            // The step's event follows the events of its value nodes; it is started even when one of them failed.
            VisualTrace.Event? Begin()=>trace==null?null:scope.Trace=trace.Start(scope.Frame,id);
            try {
                if(Interlocked.Increment(ref steps)>100000)throw new InvalidOperationException($"Node {id}: graph execution limit 100000 steps exceeded.");
                // An entity node acts on the definition of the run in progress, which only an entity definition's run has.
                var definition=EntityKinds.Contains(kind)?Definition():null;Dictionary<string,object?>? state=null;HashSet<string>? hidden=null;
                if(kind=="entity" && startPort==StateFlow) {
                    // An Entity node's state whose inputs cannot be worked out leaves its entity unavailable; a run cancelled meanwhile changes nothing.
                    try {if(pureInputs.TryGetValue(id,out var inputs))await Prepare(id,inputs,scope);ev=Begin();state=EntityValues(scope,id,out hidden);}
                    catch(Exception error) when(error is not OperationCanceledException || !Cancelled(scope)) {MarkUnavailable(definition!,EntityKey(id),id);throw;}
                }
                // An Entity node's command or frame flow does not work out its state values.
                else if(kind=="entity")ev=Begin();
                else {
                    if(pureInputs.TryGetValue(id,out var pure))await Prepare(id,pure,scope);
                    ev=Begin();
                }
                object? Get(string p)=>Input(scope,id,p);
                double Number(string p,double min,double max,bool integer=false) {var v=Input<double>(scope,id,p);if(!double.IsFinite(v)||v<min||v>max||integer&&Math.Truncate(v)!=v)throw new InvalidOperationException($"{Pin(id,p)} must be {(integer?"a whole number":"a number")} from {VisualGraphValidator.Words.Number(min)} to {VisualGraphValidator.Words.Number(max)}, not {(SecretInput(id,p,v)?HiddenValue(id,p):Convert.ToString(v,CultureInfo.InvariantCulture))}");return v;}
                switch(kind) {
                    case "trigger":Trigger(id,scope,n);break;
                    case "if":port=Input<bool>(scope,id,"value")?"true":"false";next=Next(id,port);break;
                    case "condition":port=EntityOf(id,"entity",Input<string>(scope,id,"entity")).State==Text(cfg,"state")?"true":"false";next=Next(id,port);break;
                    case "read_entity":Output(scope,id,EntityOf(id,"entity",Input<string>(scope,id,"entity")));break;
                    case "delay":await Waiting(scope,Task.Delay(TimeSpan.FromSeconds(Number("seconds",0,4294967.294)),scope.CancellationToken));break;
                    case "wait_until":var entity=Input<string>(scope,id,"entity");var expected=Get("right");await Waiting(scope,VisualValues.WaitUntil(()=>Guarded(()=>Compare(EntityField(EntityOf(id,"entity",entity,true),cfg,id),expected,Text(cfg,"operator","eq")),error=>error is not InvalidOperationException&&SecretInput(id,"right",expected),()=>Hidden(id,"right")),Number("timeout",.001,86400),Number("interval",.001,3600),scope.CancellationToken));break;
                    case "service":await Service(id,scope,cfg);break;
                    case "http_request":await HttpRequest(id,scope,cfg);break;
                    // The definition's downloads, which time spent receiving does not count against a When started flow's limit.
                    case "download_file":var address=Input<string>(scope,id,"url");var checksum=Input<string>(scope,id,"sha256");
                        // A name taken from a secret address would put part of it in the path: the file is called "file" then.
                        var fileName=Text(cfg,"name") is {Length:>0} name?name:SecretInput(id,"url",address)?"file":null;
                        Output(scope,id,await Waiting(scope,definition!.Files.Download(address,checksum,fileName,scope.CancellationToken)),"path");break;
                    // A picture of a Home Assistant camera, asked for under the definition's id, so that one of its own cameras
                    // (which would wait for itself) is refused at once; Home Assistant's failure is the step's.
                    case "camera_snapshot":var camera=Input<string>(scope,id,"entity");
                        Output(scope,id,await Waiting(scope,HassCameras.SnapshotAsync(definition!.DefinitionId,camera,scope.CancellationToken)),"image");break;
                    // A secret is not written to the log: the line is *** (true or false worked out from one is written).
                    case "log":var said=Get("value");PyInterop.Log(Text(cfg,"level","Info") switch{"Debug"=>10,"Warn"=>30,"Error"=>40,_=>20},SecretInput(id,"value",said)?TraceJson.Redacted:LogText(said));break;
                    case "stop":Interlocked.Exchange(ref stopped,1);try{await lifetime.CancelAsync();}catch(Exception error){stopFailures.Enqueue(error);}throw new OperationCanceledException(lifetime.Token);
                    case "parallel":await Parallel(id,scope,depth);break;
                    case "repeat":case "while":case "until":case "foreach":await Loop(id,scope,depth);break;
                    case "condition_result":scope.Condition=Input<bool>(scope,id,"value");if(ev!=null)trace!.End(ev,"ok",null,null);return;
                    // When started only starts its flow.
                    case "entity_setup":break;
                    // An Entity node starts the flow of its run's pin: its state, which it sets; a command's or its frame's steps.
                    case "entity":
                        if(EntityFlow(id,scope,definition!,state,hidden) is {} pin){port=pin;next=Next(id,pin);}
                        else next=null;
                        break;
                    case "set_entity_state":var values=EntityValues(scope,id,out var secret);ThrowIfCancelled(scope);definition!.SetState(Text(cfg,"entity"),values,secret);break;
                    case "set_entity_available":var available=Input<bool>(scope,id,"available");ThrowIfCancelled(scope);definition!.SetAvailable(Text(cfg,"entity"),available);break;
                    // The definition keeps a copy of the picture as its camera's current frame.
                    case "show_frame":var frame=Input<SharpImage>(scope,id,"image");ThrowIfCancelled(scope);definition!.ShowFrame(Text(cfg,"entity"),frame);break;
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
                var a=Guarded(()=>Coerce(givenLeft,clr,id+".left",false,()=>Title(id)+" · "+VisualGraphValidator.Words.PortLabel(InputSpecs(id)?["left"],"left"),serializer,entities),_=>SecretInput(id,"left",givenLeft),()=>Hidden(id,"left"));
                var b=Guarded(()=>Coerce(givenRight,clr,id+".right",false,()=>Title(id)+" · "+VisualGraphValidator.Words.PortLabel(InputSpecs(id)?["right"],"right"),serializer,entities),_=>SecretInput(id,"right",givenRight),()=>Hidden(id,"right"));
                // The trace shows the values as they came, not as they were widened.
                if(scope.Trace!=null){Traced(scope,id,"left",givenLeft);Traced(scope,id,"right",givenRight);}
                // As in C#, an ordering with a missing number (a nullable side that is null) is false.
                // An ordering of two values of different types (never in a graph the validators let through) quotes neither when one is secret.
                var op=Text(cfg,"operator","eq");
                Output(scope,id,op is not ("eq" or "ne")&&(a==null||b==null)?false:Guarded(()=>Compare(a,b,op),_=>SecretInput(id,"left",givenLeft)||SecretInput(id,"right",givenRight),()=>Hidden(id,SecretInput(id,"left",givenLeft)?"left":"right")));break;
            case "constant":case "convert":
                if(compiledConversions.Contains(id)){await custom(id,scope,this);break;}
                var convertible=Get("value");
                Output(scope,id,Guarded(()=>ConvertTo(convertible,Text(cfg,"type"),id+".value",pin:()=>Title(id)+" · "+VisualGraphValidator.Words.PortLabel(InputSpecs(id)?["value"],"value")),_=>SecretInput(id,"value",convertible),()=>Hidden(id,"value")));break;
            case "entity_field":
                var snapshot=Input<EntityStateSnapshot>(scope,id,"entity");
                Output(scope,id,Guarded(()=>EntityField(snapshot,cfg,id),_=>SecretInput(id,"entity",snapshot),()=>Hidden(id,"entity")));break;
            case "object":case "array":
                var whole=Get("value");
                Output(scope,id,Guarded(()=>ConvertTo(whole,kind,id,false),_=>SecretInput(id,"value",whole),()=>Hidden(id,"value")));break;
            case "object_create":Output(scope,id,JsonSerializer.SerializeToNode((cfg["fields"] as JsonObject??[]).ToDictionary(p=>p.Key,p=>Get("field:"+p.Key)),serializer));break;
            case "array_create":Output(scope,id,JsonSerializer.SerializeToNode((cfg["items"] as JsonArray??[]).Select((_,i)=>Get("item:"+i)).ToArray(),serializer));break;
            case "last_error":Output(scope,id,scope.LastError?.Message??"","message");Output(scope,id,scope.LastErrorStep??"","step");break;
            case "get_field":
                var read=Input<JsonElement>(scope,id,"value");
                Output(scope,id,Guarded(()=>{
                    var json=read;
                    var pointer=Text(cfg,"pointer");foreach(var segment in pointer.Split('/').Skip(1)){var part=segment.Replace("~1","/").Replace("~0","~");json=json.ValueKind==JsonValueKind.Array?json[int.Parse(part,CultureInfo.InvariantCulture)]:json.GetProperty(part);}
                    return ConvertTo(json,Text(cfg,"type"),id,false,()=>Title(id)+" · "+VisualGraphValidator.Words.PortLabel(OutputSpecs(id)?["value"],"value"));
                },_=>SecretInput(id,"value",read),()=>Hidden(id,"value")));break;
            // A failure is worded by Prepare, which names the node and the step reading it: the message says only what went wrong.
            // Wires carry entity ids: a member that is a handle goes out as its id.
            case "select_property":var property=Text(cfg,"property");var selected=SelectProperty(Get("value")??throw new InvalidOperationException($"{property} cannot be read because the value is empty"),property,id);Output(scope,id,selected is HassSharp.Entities.Entity handle?handle.EntityId:selected);break;
            // The definition's setting, of the type the graph's catalog resolved for it (resolvedOutputs).
            // A secret setting's value reaches the nodes that use it, but its trace shows *** (and so do the inputs it
            // reaches, and the values worked out from it: see Secret).
            case "setting":
                var settings=Definition();var key=Text(cfg,"key");var setting=settings.Setting(key);
                if(Text(catalog["resolvedOutputs"]?[id],"value") is {Length:>0} settingType) {
                    var stored=setting;
                    setting=Guarded(()=>ConvertTo(stored,settingType,id+".value",false),_=>SecretSetting(id),
                        ()=>new InvalidOperationException($"{Title(id)} cannot give its value as {VisualGraphValidator.Words.TypeLabel(catalog,settingType)}; the value is hidden because it comes from a secret setting"));
                }
                scope.SetOutput((id,"value"),setting);
                if(scope.Trace!=null)trace!.Output(scope.Trace,id,"value",setting,settings.IsSecret(key));
                break;
            // Exactly that state, as Home Assistant stores it: an entity it does not have has none.
            case "entity_is":var checkedEntity=Input<string>(scope,id,"entity");var wanted=Input<string>(scope,id,"state");Output(scope,id,HomeAssistantState(checkedEntity)?.State is {} has&&string.Equals(has,wanted,StringComparison.Ordinal));break;
            case "entity_current":Output(scope,id,EntityOf(id,"entity",Input<string>(scope,id,"entity")));break;
            // A drawing goes on a copy of the picture wired in, which other steps may read too (and which this node reads
            // again each time a step uses it); drawing only adds to the copy's list, so nothing is decoded or encoded here.
            // The picture's own limits (a line 1 to 50 pixels, a text of at most 200 characters, ...) fail in its words.
            case "image_draw_rect":
                var framed=Input<SharpImage>(scope,id,"image").Copy();
                Output(scope,id,framed.DrawRect(Pixels(scope,id,"x"),Pixels(scope,id,"y"),Pixels(scope,id,"width"),Pixels(scope,id,"height"),Colour(cfg,"Red"),Whole(cfg,"thickness",2)),"image");break;
            case "image_draw_text":
                var captioned=Input<SharpImage>(scope,id,"image").Copy();var caption=Input<string>(scope,id,"text");
                Output(scope,id,captioned.DrawText(caption,Pixels(scope,id,"x"),Pixels(scope,id,"y"),Colour(cfg,"White"),Whole(cfg,"size",16),Flag(cfg,"background",true)),"image");break;
            // A value node from a library (or any other kind) is compiled.
            default:await custom(id,scope,this);break;
        }
    }
    /// <summary>
    /// The member a Select property node reads, as the compiler listed it for the wired value's declared type: a public
    /// instance property or field, the most derived one of that name, found from that type down to its bases. Looked up
    /// on each read, never cached by type: a type held across runs would keep its library's load context alive. A getter
    /// that throws fails the reading step with its own exception, not one wrapping it. A wire of an entity id carries the id:
    /// its member is read from the handle this run's factory makes of it, the class of its domain, never from the catalog.
    /// </summary>
    object? SelectProperty(object value,string name,string node) {
        var wired=TraceInputType(node,"value");
        if(value is string entity&&wired!=null&&VisualTypes.Refines(catalog,wired,"entity_id"))value=entities(entity);
        var declared=wired!=null&&types.TryGetValue(VisualTypes.RootId(catalog,wired),out var clr)?Nullable.GetUnderlyingType(clr)??clr:null;
        const BindingFlags Own=BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly;
        for(var type=declared!=null&&declared.IsInstanceOfType(value)?declared:value.GetType();type!=null;type=type.BaseType) {
            if(type.GetProperty(name,Own) is {} member&&member.GetIndexParameters().Length==0&&member.GetMethod is {IsPublic:true} getter)return getter.Invoke(value,BindingFlags.DoNotWrapExceptions,null,null,null);
            if(type.GetField(name,Own) is {} field)return field.GetValue(value);
        }
        throw new InvalidOperationException($"{VisualGraphValidator.Words.TypeLabel(catalog,TraceInputType(node,"value")??"")} has no property {VisualGraphValidator.Words.Name(name)}");
    }
    /// <summary>
    /// The entity's state as the run sees it: the one the change that started the run carries, for that entity (unless
    /// <paramref name="fresh"/>), else Home Assistant's now; null when Home Assistant has no such entity.
    /// </summary>
    static HasEntityState? HomeAssistantState(string entity,bool fresh=false) {
        var triggerState=UserScriptManager.CurrentTrigger.Value?.State;
        return !fresh && triggerState?.EntityId==entity ? triggerState : PyInterop.Entity(entity);
    }
    internal static EntityStateSnapshot ReadEntity(string entity,bool fresh=false)=>
        EntityStateSnapshot.From(HomeAssistantState(entity,fresh)??throw new InvalidOperationException($"Entity '{entity}' is unavailable."));
    /// <summary><see cref="ReadEntity"/> of the entity a step read on <paramref name="port"/>; an unavailable entity whose id is secret is not named.</summary>
    EntityStateSnapshot EntityOf(string node,string port,string entity,bool fresh=false) {
        return Guarded(()=>ReadEntity(entity,fresh),error=>error is InvalidOperationException&&SecretInput(node,port,entity),
            ()=>new InvalidOperationException($"{Pin(node,port)} names an entity that is unavailable; its id is hidden because {Because(node,port)}"));
    }
    /// <summary>
    /// A drawing step's position or size in pixels, rounded to the nearest one (a half away from zero): a value that is
    /// not a number, is infinite or lies beyond a billion pixels either way fails the step, naming the input (and not
    /// quoting a secret).
    /// </summary>
    int Pixels(VisualScope scope,string node,string port) {
        var value=Input<double>(scope,node,port);
        if(!double.IsFinite(value)||Math.Abs(value)>MaxPixels)
            throw new InvalidOperationException($"{VisualGraphValidator.Words.PortLabel(InputSpecs(node)?[port],port)} must be a number of pixels from -{MaxPixels} to {MaxPixels}, not {(SecretInput(node,port,value)?HiddenValue(node,port):Convert.ToString(value,CultureInfo.InvariantCulture))}");
        return (int)Math.Round(value,MidpointRounding.AwayFromZero);
    }
    const int MaxPixels=1_000_000_000;
    /// <summary>The colour a drawing step's Colour names; <paramref name="fallback"/> when none is chosen.</summary>
    static SharpColor Colour(JsonNode cfg,string fallback)=>SharpColor.TryParse(Text(cfg,"color",fallback),out var colour)?colour
        :throw new InvalidOperationException($"{Text(cfg,"color")} is not a colour to draw in; choose Red, Green, Blue, Yellow, Orange, White or Black");
    /// <summary>A whole number a step's configuration gives; <paramref name="fallback"/> when it gives none.</summary>
    static int Whole(JsonNode cfg,string key,int fallback)=>cfg[key] is JsonValue value&&value.TryGetValue<double>(out var number)&&double.IsFinite(number)?(int)Math.Clamp(Math.Truncate(number),int.MinValue,int.MaxValue):fallback;
    /// <summary>The definition of the run in progress, which an entity node acts on; only a run of an entity definition's graph has one.</summary>
    static IDefinitionRuntime Definition()=>InvocationContext.Current?.Definition??throw new InvalidOperationException("This node only works in an entity definition");
    /// <summary>The kinds that act on the definition of the run in progress (see <see cref="Definition"/>).</summary>
    static readonly HashSet<string> EntityKinds=new(["entity","entity_setup","set_entity_state","set_entity_available","setting","download_file","camera_snapshot","show_frame"],StringComparer.Ordinal);
    /// <summary>The port of an Entity node's state flow, which has no pin.</summary>
    const string StateFlow="state";
    /// <summary>The key of the entity a node acts on: an Entity node's own, else the one its <c>entity</c> names.</summary>
    string EntityKey(string id)=>Text(nodes[id]["config"],Text(nodes[id],"kind")=="entity"?"key":"entity");
    /// <summary>
    /// Runs what the Entity node <paramref name="id"/> does at the start of its run's flow (<see cref="startPort"/>): for
    /// its state, hands <paramref name="state"/> to <paramref name="definition"/> and marks the entity available (a run
    /// already cancelled does neither), and gives null: no step follows. For a command, gives each of that command's
    /// values from its data, read from the run's trigger as a trigger's outputs are (<see cref="TriggerOutput"/>): a value
    /// the command was not given, and the host could fill neither from the entity's state nor from its row's default, is
    /// absent, and a step reading it fails in words (<see cref="AbsentCommandValue"/>). Gives the pin, whose steps run
    /// next; for its frame, the pin alone.
    /// </summary>
    string? EntityFlow(string id,VisualScope scope,IDefinitionRuntime definition,Dictionary<string,object?>? state,HashSet<string>? hidden) {
        var port=startPort??throw new InvalidOperationException($"{Title(id)} runs only as the start of one of its flows");
        if(port==StateFlow) {
            ThrowIfCancelled(scope);definition.SetState(EntityKey(id),state!,hidden!);definition.SetAvailable(EntityKey(id),true);
            return null;
        }
        if(port.StartsWith("command:",StringComparison.Ordinal)) {
            // The command's values are command:<command>:<value>, under its pin command:<command>.
            foreach(var (output,spec) in CommandOutputs(id))if(output.StartsWith(port+":",StringComparison.Ordinal))TriggerOutput(id,output,spec,scope);
        }
        return port;
    }
    /// <summary>
    /// The state values a Set state step or Entity node hands its definition: one for each of its pins (the state keys of
    /// its entity's domain) that is given a value (<see cref="Supplied"/>; an Entity node's are under its
    /// <c>arguments</c>), as <see cref="EntityStateValue"/> gives it, so the entity keeps the values it is not given;
    /// <paramref name="hidden"/> has the pins whose values are secret (<see cref="SecretInput"/>), which the definition
    /// does not quote.
    /// </summary>
    Dictionary<string,object?> EntityValues(VisualScope scope,string id,out HashSet<string> hidden) {
        var values=new Dictionary<string,object?>(StringComparer.Ordinal);hidden=new(StringComparer.Ordinal);
        var cfg=nodes[id]["config"];var stored=Text(Spec(id),"argumentStorage")=="arguments"?cfg?["arguments"] as JsonObject:cfg;
        foreach(var (port,spec) in InputSpecs(id) as JsonObject??[]) {
            // An Entity node's Device pin is no state value: the Device node wired to it says which device the entity is on and carries nothing.
            if(VisualNodeCatalog.Structural(spec)||!Supplied(id,port,stored?[port]))continue;
            var given=Input(scope,id,port);
            values[port]=Guarded(()=>EntityStateValue(given,(spec?["types"] as JsonArray??[]).OfType<JsonValue>().Select(t=>t.TryGetValue<string>(out var type)?type:"").ToArray(),Pin(id,port),!PasswordText(catalog,EntityKey(id))),
                error=>error is InvalidOperationException&&SecretInput(id,port,given),()=>Hidden(id,port));
            if(SecretInput(id,port,values[port]))hidden.Add(port);
        }
        return values;
    }
    /// <summary>
    /// A state value as a definition takes it: true or false as a <see cref="bool"/>, a number as a <see cref="double"/>,
    /// text as a <see cref="string"/>, each only where the pin's <paramref name="types"/> take it, and an empty value as null
    /// (the entity's state is then unknown). A JSON value is the value it holds. Anything else fails, naming the <paramref name="pin"/>
    /// and, unless <paramref name="quote"/> is false (a password text's), the value.
    /// </summary>
    internal static object? EntityStateValue(object? value,IReadOnlyCollection<string> types,string pin,bool quote=true) {
        if(value is JsonElement json)value=json.ValueKind switch{JsonValueKind.Null or JsonValueKind.Undefined=>null,JsonValueKind.True=>true,JsonValueKind.False=>false,JsonValueKind.String=>json.GetString(),JsonValueKind.Number when json.TryGetDouble(out var real)=>real,_=>value};
        switch(value) {
            case null:return null;
            case bool flag when types.Contains("boolean"):return flag;
            case string text when types.Contains("string"):return text;
            // An enum member is not a number, whatever it is stored as.
            case not (bool or string or char or Enum) when types.Contains("number")&&IsNumber(value.GetType()):
                if(Convert.ToDouble(value,CultureInfo.InvariantCulture) is var number&&double.IsFinite(number))return number;
                break;
        }
        var wanted=string.Join(" or ",types.Select(t=>t switch{"boolean"=>"true or false","number"=>"a number","string"=>"text",_=>t}).Distinct());
        throw new InvalidOperationException($"{pin} needs {wanted}, not {(!quote?"the hidden value given":value is bool given?(given?"true":"false"):LogText(value))}");
    }
    /// <summary>
    /// Whether the definition's entity <paramref name="key"/> is a text shown as a password, whose value a refusal does not
    /// quote, as its class words it. The validator lets only text into a text's value, so this guards a refusal no saved
    /// graph is expected to reach.
    /// </summary>
    internal static bool PasswordText(JsonObject catalog,string key)=>VisualNodeCatalog.DefinitionEntity(catalog,key) is {} entity&&Text(entity,"domain")=="text"&&Text(entity["options"],"mode")=="password";
    // Contract C6: each typed output reads its pointer from the normalised payload. A pointer that finds
    // nothing or JSON null, or a value of another type, produces no output; only a consumer fails, naming it.
    void Trigger(string id,VisualScope scope,JsonObject node) {
        foreach(var (port,spec) in VisualNodeCatalog.TriggerOutputs(node,catalog))TriggerOutput(id,port,spec,scope);
    }
    void TriggerOutput(string id,string port,JsonNode? spec,VisualScope scope) {
        if(trigger.ValueKind==JsonValueKind.Undefined || !TryPointer(trigger,Text(spec,"pointer"),out var value) || value.ValueKind==JsonValueKind.Null) {absentTriggerOutputs[(id,port)]=null;return;}
        var type=Text(spec,"type");var label=id+"."+port;
        object? output;
        try {output=types.GetValueOrDefault(VisualTypes.RootId(catalog,type))==typeof(EntityStateSnapshot)?EntityStateSnapshot.FromStateJson(value):ConvertTo(value,type,label,false);}
        catch(Exception error) when(error is ArgumentException or InvalidOperationException){absentTriggerOutputs[(id,port)]=error.Message;return;}
        scope.SetOutput((id,port),output);
        if(scope.Trace!=null)trace!.Output(scope.Trace,id,port,output);
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
        // The fields (and target keys) a secret fills, which the debug log of the call writes as ***.
        var payload=new JsonObject();var hidden=new List<string>();
        foreach(var (name,operand) in cfg["fields"] as JsonObject??[])
            if(Supplied(id,"field:"+name,operand)){var given=Input(scope,id,"field:"+name);payload[name]=Json(given);if(SecretInput(id,"field:"+name,given))hidden.Add(name);}
        object? aimed=null;
        var target=Supplied(id,"target",cfg["target"])?Json(aimed=Input(scope,id,"target"))??throw new InvalidOperationException($"Input {id}.target requires an entity id or a target object."):null;
        if(target!=null && SecretInput(id,"target",aimed))hidden.AddRange(target is JsonObject keys?keys.Select(p=>p.Key):["entity_id"]);
        var returnResponse=cfg["returnResponse"] is JsonValue flag && flag.TryGetValue<bool>(out var on) && on;
        var response=await Call(scope,Text(cfg,"service"),payload,target,returnResponse,$"Input {id}.target",hidden);
        if(returnResponse)Output(scope,id,ConvertTo(response,"object",id+".response",false),"response");
    }
    /// <summary><see cref="NodeContext.CallService"/>: the call the Call service step makes, from a node method.</summary>
    internal Task<JsonElement?> CallService(VisualScope scope,string node,string service,JsonNode? target,JsonObject? data,bool returnResponse)=>
        Call(scope,service,data==null?new JsonObject():(JsonObject)data.DeepClone(),target?.DeepClone(),returnResponse,"The target");
    /// <summary>
    /// The target into the payload (<see cref="HassServices.PrepareCall"/>), then the call; the response only with
    /// returnResponse. The <paramref name="hidden"/> keys of the payload are secret.
    /// </summary>
    async Task<JsonElement?> Call(VisualScope scope,string service,JsonObject payload,JsonNode? target,bool returnResponse,string targetLabel,IReadOnlyCollection<string>? hidden=null) {
        var (domain,name)=HassServices.PrepareCall(service,payload,target,targetLabel);
        if(returnResponse)return await Waiting(scope,HassServices.CallServiceAsync<JsonElement>(domain,name,payload,hidden).WaitAsync(scope.CancellationToken));
        await Waiting(scope,HassServices.CallServiceAsync(domain,name,payload,hidden).WaitAsync(scope.CancellationToken));
        return null;
    }
    /// <summary>The most an HTTP request step reads of an answer; a longer one fails the step.</summary>
    internal const int MaxAnswerBytes=4*1024*1024;
    /// <summary>The most redirects an HTTP request step follows.</summary>
    const int MaxRedirects=5;
    /// <summary>
    /// An HTTP request step: its method to its URL (http or https only), with its headers and body (text as it is, an
    /// object or list as JSON), over the process's pool for these steps, within its timeout and cancelled with its run;
    /// nothing of it outlives the step. It follows at most <see cref="MaxRedirects"/> redirects itself: to the same host
    /// with its headers, to another host only when it has none (.NET would hand any but Authorization on); a 303, or a
    /// 301 or 302 after a POST, as a GET without the body; a 307 or 308 as it was; from https to http not at all (the
    /// redirect is the answer, as .NET has it). A status outside 200 to 299 fails it unless Fail on an error status is
    /// off. Its outputs are the final answer's status, its body as Response type reads it (none for a HEAD) and its
    /// headers by lower-case name. Every failure is worded by its cause and names the host that failed (and a port that
    /// is not the scheme's) only, never the path, the query, a header or the body, so that a poll failing for one reason
    /// fails with one message; a .NET failure under it, which may quote any of them, is not kept. With Hide response on,
    /// where a redirect went is not said either: a failure after a redirect to another host names the host asked and
    /// says "another host", such as <c>example.com redirected to another host, which answered HTTP 404</c>.
    /// </summary>
    async Task HttpRequest(string id,VisualScope scope,JsonNode cfg) {
        var method=Text(cfg,"method","GET");var responseType=Text(cfg,"responseType","string");
        var seconds=cfg["timeout"] is JsonValue limit&&limit.TryGetValue<double>(out var given)&&double.IsFinite(given)?Math.Clamp(given,1,300):10;
        var url=Input<string>(scope,id,"url");
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme is not ("http" or "https")||uri.Host=="")throw new InvalidOperationException($"{Pin(id,"url")} is not an http:// or https:// address");
        object? body=Supplied(id,"body",cfg["body"])?Input(scope,id,"body"):null;
        if(body!=null&&method is "GET" or "HEAD")throw new InvalidOperationException($"{Title(id)} cannot send a body with {method}");
        object? givenHeaders=Supplied(id,"headers",cfg["headers"])?Input(scope,id,"headers"):null;
        var headers=givenHeaders==null?null:Json(givenHeaders);
        // One request of the chain; its headers are checked before anything is sent. The headers of a body it no longer
        // sends (a redirect that became a GET) are left out.
        HttpRequestMessage Build(Uri address,string verb,bool withBody) {
            var request=new HttpRequestMessage(new HttpMethod(verb),address){Content=withBody?Content(body):null};
            try {if(headers!=null)Guarded(()=>{AddHeaders(id,request,headers,body!=null&&!withBody);return true;},_=>SecretInput(id,"headers",givenHeaders),()=>Hidden(id,"headers"));}
            catch {request.Dispose();throw;}
            return request;
        }
        using var client=SharpHttp.CreateForRequests();client.Timeout=Timeout.InfiniteTimeSpan;
        // The step's timeout covers the whole chain.
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(scope.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        // The host asked last, and the port when it is not the scheme's own: never the user, the path or the query. Where a
        // redirect goes is part of the response, so with the response kept out of run history a failure after a redirect
        // to another host names the host asked first, and says "another host" for the one it went to (Away).
        var hidden=Flag(cfg,"hideResponse",false);var origin=uri;
        var at=uri.Authority;var verb=method;var withBody=body!=null;
        bool Away()=>hidden&&!string.Equals(uri.IdnHost,origin.IdnHost,StringComparison.OrdinalIgnoreCase);
        // A failure at the host asked last: "<what> <host>" or, away, "<origin> redirected to another host, which <away>".
        string At(string what,string away)=>Away()?$"{origin.Authority} redirected to another host, {away}":$"{what}{at}";
        int status=0;byte[]? answer=null;JsonObject headersOut=[];string? charset=null;
        try {
            for(var redirects=0;;redirects++) {
                using var request=Build(uri,verb,withBody);
                using var response=await Waiting(scope,client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token));
                status=(int)response.StatusCode;
                if(status is 301 or 302 or 303 or 307 or 308) {
                    if(response.Headers.Location is not {} location)throw new HttpRequestException($"{at} redirected without an address to go to",null,response.StatusCode);
                    if(!Uri.TryCreate(uri,location,out var next)||next.Scheme is not ("http" or "https")||next.Host=="")throw new HttpRequestException($"{at} redirected to an address that is not http or https",null,response.StatusCode);
                    if(!(uri.Scheme=="https"&&next.Scheme=="http")) {
                        if(redirects==MaxRedirects)throw new HttpRequestException($"{at} redirected too many times",null,response.StatusCode);
                        if(headers!=null&&!string.Equals(next.IdnHost,uri.IdnHost,StringComparison.OrdinalIgnoreCase))
                            throw new HttpRequestException($"{at} redirected to {(hidden?"another host":next.Authority)}, and the headers were not sent there",null,response.StatusCode);
                        if(status==303&&verb!="HEAD"||status is 301 or 302&&verb=="POST") {verb="GET";withBody=false;}
                        uri=next;if(!hidden)at=next.Authority;
                        continue;
                    }
                }
                if(Flag(cfg,"failOnStatus",true)&&status is <200 or >299)throw new HttpRequestException(Away()?At("",$"which answered HTTP {status.ToString(CultureInfo.InvariantCulture)}"):$"HTTP {status.ToString(CultureInfo.InvariantCulture)} from {at}",null,response.StatusCode);
                headersOut=AnswerHeaders(response);
                // A HEAD's answer has no body, whatever length it gives.
                if(verb!="HEAD") {charset=response.Content.Headers.ContentType?.CharSet;answer=await Waiting(scope,ReadAnswer(response.Content,Away()?$"the host {origin.Authority} redirected to":at,deadline.Token));}
                break;
            }
        }
        catch(OperationCanceledException) when(!Cancelled(scope)) {
            var within=$"within {VisualGraphValidator.Words.Number(seconds)} second{(seconds==1?"":"s")}";
            if(deadline.IsCancellationRequested)throw new TimeoutException(Away()?At("",$"which did not answer {within}"):$"{at} did not answer {within}");
            // The pool's limit on opening a connection passed (SharpHttp.ConnectTimeout).
            throw new HttpRequestException(At("Could not reach ","which could not be reached"));
        }
        catch(Exception error) when(error is HttpRequestException {StatusCode:null} or IOException) {
            throw new HttpRequestException((error as HttpRequestException)?.HttpRequestError switch {
                HttpRequestError.NameResolutionError=>At("Could not find ","which could not be found"),
                HttpRequestError.SecureConnectionError=>At("Could not make a secure connection to ","to which no secure connection could be made"),
                HttpRequestError.ResponseEnded=>Away()?At("","whose answer broke off"):$"The answer from {at} broke off",
                HttpRequestError.InvalidResponse=>Away()?At("","whose answer could not be read"):$"The answer from {at} could not be read",
                HttpRequestError.ConnectionError=>At("Could not reach ","which could not be reached"),
                _ when error is IOException=>Away()?At("","whose answer broke off"):$"The answer from {at} broke off",
                _=>At("Could not reach ","which could not be reached"),
            });
        }
        Output(scope,id,(double)status,"status");
        if(answer!=null)Output(scope,id,AnswerBody(answer,charset,responseType,Away()?$"the host {origin.Authority} redirected to":at),"body");
        Output(scope,id,headersOut,"headers");
    }
    /// <summary>What an HTTP request sends: text as it is (<c>text/plain</c>), anything else as JSON (<c>application/json</c>); nothing for no body.</summary>
    HttpContent? Content(object? body) {
        if(body==null)return null;
        var text=body as string??(body is JsonElement {ValueKind:JsonValueKind.String} element?element.GetString():null);
        if(text!=null)return new StringContent(text);
        var json=Json(body);
        if(json is JsonValue value&&value.TryGetValue<string>(out var inner))return new StringContent(inner);
        var content=new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(json?.ToJsonString()??"null"));
        content.Headers.ContentType=new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return content;
    }
    /// <summary>
    /// The headers of an HTTP request step, each name with text, a number or true or false. A header of the body (such as
    /// Content-Type) replaces the body's own; one of a body the request no longer sends (<paramref name="bodyDropped"/>)
    /// is left out. Each is checked before anything is sent, as .NET would only refuse some when sending: a name that
    /// cannot be a header's, and a value that is not plain ASCII on one line, fail the step by the header's name, never
    /// its value.
    /// </summary>
    void AddHeaders(string id,HttpRequestMessage request,JsonNode? headers,bool bodyDropped) {
        if(headers is not JsonObject named)throw new InvalidOperationException($"{Pin(id,"headers")} needs an object of names and values");
        foreach(var (name,value) in named) {
            var label=$"{Pin(id,"headers")}: {VisualGraphValidator.Words.Name(name)}";
            if(name.Length==0||!name.All(HeaderNameCharacter))throw new InvalidOperationException($"{label} is not a header name");
            var text=value is JsonValue scalar?scalar.GetValueKind() switch{JsonValueKind.String=>scalar.GetValue<string>(),JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False=>scalar.ToJsonString(),_=>null}:null;
            if(text==null)throw new InvalidOperationException($"{label} needs text, a number or true or false");
            if(!text.All(c=>c is '\t' or >=' ' and <='~'))throw new InvalidOperationException($"{label} has a value that cannot be sent in a header; use plain ASCII text on one line");
            if(request.Headers.TryAddWithoutValidation(name,text))continue;
            // Not a request header, so the body's.
            if(request.Content is {} content) {content.Headers.Remove(name);content.Headers.TryAddWithoutValidation(name,text);}
            else if(!bodyDropped)throw new InvalidOperationException($"{label} can only be sent with a body");
        }
    }
    /// <summary>A character a header name may have (an HTTP token's): a letter or digit, or one of <c>!#$%&amp;'*+-.^_`|~</c>.</summary>
    static bool HeaderNameCharacter(char c)=>char.IsAsciiLetterOrDigit(c)||"!#$%&'*+-.^_`|~".Contains(c);
    /// <summary>An answer's headers and its body's, by lower-case name; the values of a header sent more than once joined with ", ".</summary>
    static JsonObject AnswerHeaders(HttpResponseMessage response) {
        var headers=new JsonObject();
        foreach(var (name,values) in response.Headers.NonValidated.Concat(response.Content.Headers.NonValidated)) {
            var key=name.ToLowerInvariant();var joined=string.Join(", ",values);
            headers[key]=headers[key] is JsonValue earlier?earlier.GetValue<string>()+", "+joined:joined;
        }
        return headers;
    }
    /// <summary>The bytes of an answer, at most <see cref="MaxAnswerBytes"/>: one that says it is longer, or goes on longer, fails.</summary>
    static async Task<byte[]> ReadAnswer(HttpContent content,string host,CancellationToken token) {
        if(content.Headers.ContentLength>MaxAnswerBytes)throw TooLarge(host);
        await using var stream=await content.ReadAsStreamAsync(token);
        using var bytes=new MemoryStream();
        var buffer=new byte[81920];int read;
        while((read=await stream.ReadAsync(buffer,token))>0) {
            if(bytes.Length+read>MaxAnswerBytes)throw TooLarge(host);
            bytes.Write(buffer,0,read);
        }
        return bytes.ToArray();
    }
    static InvalidDataException TooLarge(string host)=>new($"The answer from {host} is larger than {MaxAnswerBytes/1024/1024} MiB");
    /// <summary>
    /// An answer as Response type reads it: text in the character set its Content-Type names (UTF-8 when none or one not
    /// known), or a JSON object or list; an empty answer, or one of another shape, fails without quoting it.
    /// </summary>
    static object AnswerBody(byte[] answer,string? charset,string responseType,string host) {
        System.Text.Encoding encoding;
        try {encoding=string.IsNullOrWhiteSpace(charset)?System.Text.Encoding.UTF8:System.Text.Encoding.GetEncoding(charset.Trim('"'));}
        catch(ArgumentException) {encoding=System.Text.Encoding.UTF8;}
        using var reader=new StreamReader(new MemoryStream(answer),encoding,detectEncodingFromByteOrderMarks:true);
        var text=reader.ReadToEnd();
        if(responseType is not ("object" or "array"))return text;
        if(answer.Length==0)throw new InvalidDataException($"The answer from {host} is empty");
        JsonNode? parsed;
        // A name given twice, at any depth, is refused here rather than when a later step reads it (whose message would quote it).
        try {parsed=JsonNode.Parse(text,documentOptions:new JsonDocumentOptions{AllowDuplicateProperties=false});}
        catch(JsonException) {parsed=null;}
        return responseType=="object"
            ? parsed as JsonObject??throw new InvalidDataException($"The answer from {host} is not a JSON object")
            : parsed as JsonArray??throw new InvalidDataException($"The answer from {host} is not a JSON list");
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
        if(kind=="repeat") {var n=Input<double>(parent,id,"count");if(!double.IsFinite(n)||n<0||n!=Math.Truncate(n)||n>max)throw new InvalidOperationException($"{Pin(id,"count")} must be a whole number from 0 to {max} (Maximum iterations), not {(SecretInput(id,"count",n)?HiddenValue(id,"count"):Convert.ToString(n,CultureInfo.InvariantCulture))}");count=(int)n;}
        if(kind=="foreach") {
            var source=Input(parent,id,"items");var copy=new VisualSnapshot();
            items=source switch{Array a when a.Rank==1=>a.Cast<object?>().Select(copy.Copy).ToArray(),JsonArray j=>j.Select(v=>v==null?null:(object?)JsonSerializer.SerializeToElement(v)).ToArray(),JsonElement j when j.ValueKind==JsonValueKind.Array=>j.EnumerateArray().Select(v=>(object?)v.Clone()).ToArray(),_=>throw new InvalidOperationException($"{Pin(id,"items")} needs a list")};
            if(items.Length>max)throw new InvalidOperationException($"{Pin(id,"items")} has {items.Length} items, more than Maximum iterations ({max})");
            for(var i=0;i<items.Length;i++){
                var item=i+1;
                var index=i;
                items[i]=Guarded(()=>ConvertTo(items[index],Text(cfg,"itemType"),id+".item["+index+"]",false,()=>Title(id)+" · "+VisualGraphValidator.Words.PortLabel(InputSpecs(id)?["items"],"items")+", item "+item.ToString(CultureInfo.InvariantCulture)),
                    _=>SecretInput(id,"items",source),()=>Hidden(id,"items"));
            }
            count=items.Length;
        }
        void Carried(VisualScope scope) {var copy=new VisualSnapshot();foreach(var pair in state.Where(p=>p.Key.StartsWith("carry:")))scope.Outputs[(id,pair.Key)]=copy.Copy(pair.Value);}
        string Part(int iteration,string part)=>id+"#"+iteration.ToString(CultureInfo.InvariantCulture)+":"+part;
        async Task<bool> Condition(int iteration) {var condition=await RunScope(id,"condition",parent,depth,Part(iteration,"condition"),Carried);return condition.Condition??throw new InvalidOperationException($"Loop {id}: condition path did not return condition_result.");}
        for(var iteration=0;;iteration++) {
            parent.CancellationToken.ThrowIfCancellationRequested();
            if(kind is "repeat" or "foreach" && iteration>=count)break;
            if(kind=="while"&&!await Condition(iteration))break;
            if(iteration>=max)throw new InvalidOperationException($"{Title(id)} stopped at Maximum iterations ({max})");
            var current=iteration;
            var body=await RunScope(id,"body",parent,depth,Part(iteration,"body"),scope=>{Carried(scope);if(items!=null){scope.Outputs[(id,"item")]=new VisualSnapshot().Copy(items[current]);scope.Outputs[(id,"index")]=(double)current;}});
            if(declarations.Count>0&&body.Updates==null)throw new InvalidOperationException($"Loop {id}: body did not return loop_result for declared carry/exports.");
            if(body.Updates!=null) {
                var updates=new Dictionary<string,object?>();var copy=new VisualSnapshot();
                foreach(var pair in body.Updates) {
                    // A kept or exported value worked out from a secret is secret (see FollowSettings), and not quoted.
                    updates[pair.Key]=Guarded(()=>copy.Copy(ConvertTo(pair.Value,declarations[pair.Key],id+"."+pair.Key,false,Declared(pair.Key))),_=>Secret((id,pair.Key),pair.Value),()=>HiddenAt(Declared(pair.Key)(),Because((id,pair.Key))));
                }
                foreach(var pair in updates)state[pair.Key]=pair.Value;
            }
            if(kind=="until"&&await Condition(iteration))break;
        }
        foreach(var pair in state.Where(p=>p.Key.StartsWith("export:")))Output(parent,id,pair.Value,pair.Key);
    }
}

/// <summary>
/// The flows of one run of a node method's step: each flow parameter calls <see cref="Run"/>, which runs the steps wired
/// to that flow in a scope of their own and ends when they do. A call after the step has finished, or more calls than a
/// loop may make, fails; a call still running when the method returns is cancelled, so no flow outlives its step.
/// </summary>
public sealed class VisualFlows
{
    readonly VisualRuntime runtime;
    readonly VisualScope scope;
    readonly string node;
    // Cancelled when the method returns with calls still running, and with the run.
    readonly CancellationTokenSource lifetime;
    readonly List<Task> running=[];
    int calls;
    bool closed;
    internal VisualFlows(VisualRuntime runtime,VisualScope scope,string node){this.runtime=runtime;this.scope=scope;this.node=node;lifetime=CancellationTokenSource.CreateLinkedTokenSource(scope.CancellationToken);}
    /// <summary>At most this many calls across a step's flows, as a loop's Maximum iterations allows.</summary>
    public const int MaxCalls=1000;
    /// <summary>
    /// Runs the steps on <paramref name="port"/>, which read a copy of <paramref name="values"/> as the step's outputs
    /// there: each call its own, as each iteration of For each has its own item.
    /// </summary>
    public Task Run(string port,params (string Port,object? Value)[] values) {
        var go=new TaskCompletionSource();Task call;
        // Registered before it starts, so Finish sees every call; its steps run outside the lock, on this thread.
        lock(running) {
            if(closed)throw new InvalidOperationException($"{runtime.StepTitle(node)}: {runtime.FlowLabel(node,port)} was called after the step finished");
            var index=calls++;
            if(index>=MaxCalls)throw new InvalidOperationException($"{runtime.StepTitle(node)} called its flows more than {MaxCalls.ToString(CultureInfo.InvariantCulture)} times");
            call=Started(go.Task,port,index,values);running.Add(call);
        }
        go.SetResult();
        return call;
    }
    async Task Started(Task go,string port,int index,(string Port,object? Value)[] values) {
        await go;
        await runtime.RunScope(node,port,scope,scope.Depth,node+"#"+index.ToString(CultureInfo.InvariantCulture)+":"+port,
            inner=>{var copy=new VisualSnapshot();foreach(var (output,value) in values)runtime.Output(inner,node,copy.Copy(value),output);},lifetime.Token);
    }
    /// <summary>
    /// Called when the step's method has returned (or thrown): later calls fail, and calls still running are cancelled
    /// and waited for. Such a call failing other than by that cancellation fails the step.
    /// </summary>
    public async Task Finish() {
        Task[] left;
        lock(running){closed=true;left=running.Where(t=>!t.IsCompleted).ToArray();}
        try {
            if(left.Length==0)return;
            await lifetime.CancelAsync();
            foreach(var call in left) {try{await call;}catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}}
        }
        finally {lifetime.Dispose();}
    }
}
