using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HassSharp;

/// <param name="Code">
/// A stable machine-readable kind for structural diagnostics (see <see cref="VisualGraphValidator.Codes"/>), the
/// same in both validators; null for the rest. A code starting with <c>cycle.</c> or <c>scope.</c> blocks connections.
/// </param>
public sealed record VisualDiagnostic(string? NodeId, string Message, int Severity = 3, int? StartLine = null, int? StartColumn = null, string? InputId = null, string? PortId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Code = null);
public sealed record VisualSourceMap(string NodeId, int StartLine, int EndLine);
public sealed record VisualPreview(bool Valid, string Source, List<VisualSourceMap> SourceMap, List<VisualDiagnostic> Diagnostics)
{
    /// <summary>
    /// The graph as previewed when it is not the one given: its Custom C# signatures derived from their code first, or
    /// refreshed from the compile where only their words or type entries differ; null otherwise.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public JsonObject? Graph { get; init; }
    /// <summary>The pruned catalog JSON the generated class embeds (the run snapshot's catalog); never serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? EmbeddedCatalog { get; init; }
}

/// <summary>V4-only compiler. Preview inspects syntax and symbols without emitting or executing user code.</summary>
/// <param name="serviceCalls">The typed service calls one request read once, for everything it previews; the current ones when null.</param>
public sealed class VisualGenerator(DiagnosticsProvider diagnostics,GeneratedServiceCalls? serviceCalls=null)
{
    GeneratedServiceCalls ServiceCalls()=>serviceCalls??diagnostics.ServiceCalls();
    static string Text(JsonNode? n,string key,string fallback="")=>VisualNodeCatalog.Text(n,key,fallback);
    static string Q(string value)=>JsonSerializer.Serialize(value);
    /// <summary>
    /// A long text (the graph and catalog JSON, hundreds of KB) as one expression of short literals:
    /// <c>global::System.String.Concat(new string[]{"…","…"})</c>, on one line. Roslyn keeps what it parses in two
    /// process-wide tables that are never cleared: each small syntax node (a literal is one) in a cache of 65536 entries
    /// that only another parse overwrites, and each string literal's value among its interned strings. One literal holding
    /// the graph, parsed again on every reload, preview and compile, stayed there each time: memory grew by the size of
    /// every automation on every reload of the entry. Pieces of <see cref="PieceLength"/> characters bound what those
    /// entries can hold, and the list of pieces has too many children to be cached.
    /// </summary>
    internal static string Pieces(string value) {
        if(value.Length<=PieceLength)return Q(value);
        var pieces=new List<string>();
        for(var start=0;start<value.Length;) {
            var length=Math.Min(PieceLength,value.Length-start);
            // A surrogate pair stays in one piece: one half alone would not be serialised as itself.
            if(start+length<value.Length&&char.IsHighSurrogate(value[start+length-1]))length--;
            pieces.Add(Q(value.Substring(start,length)));start+=length;
        }
        return "global::System.String.Concat(new string[]{"+string.Join(",",pieces)+"})";
    }
    internal const int PieceLength=64;
    // A dispatch expression must never acquire dynamic overload resolution.
    static string ClrExpression(ITypeSymbol symbol)
    {
        if(symbol.SpecialType==SpecialType.System_Void)return "void";
        var syntax=SyntaxFactory.ParseTypeName(NodeLibraryCompiler.TypeIdentity(symbol));
        var dynamicNames=syntax.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Where(n=>n.Identifier.Text=="dynamic" && n.Parent is not QualifiedNameSyntax and not AliasQualifiedNameSyntax);
        return syntax.ReplaceNodes(dynamicNames,(_,_)=>SyntaxFactory.ParseName("global::System.Object")).ToString();
    }
    static string DefaultExpression(NodeParameterDescriptor parameter)
    {
        var type=ClrExpression(parameter.Symbol.Type);var value=parameter.DefaultValue;
        if(value==null)return $"default({type})";
        var literal=value switch {
            string text=>Q(text), char character=>Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(character,true),
            bool flag=>flag?"true":"false",
            double number when double.IsNaN(number)=>"global::System.Double.NaN",
            double number when double.IsPositiveInfinity(number)=>"global::System.Double.PositiveInfinity",
            double number when double.IsNegativeInfinity(number)=>"global::System.Double.NegativeInfinity",
            double number=>number.ToString("R",CultureInfo.InvariantCulture)+"D",
            float number when float.IsNaN(number)=>"global::System.Single.NaN",
            float number when float.IsPositiveInfinity(number)=>"global::System.Single.PositiveInfinity",
            float number when float.IsNegativeInfinity(number)=>"global::System.Single.NegativeInfinity",
            float number=>number.ToString("R",CultureInfo.InvariantCulture)+"F",
            decimal number=>number.ToString(CultureInfo.InvariantCulture)+"M",
            DateTime date=>$"new global::System.DateTime({date.Ticks.ToString(CultureInfo.InvariantCulture)}L)",
            long number when number==long.MinValue=>"global::System.Int64.MinValue",
            long number=>number.ToString(CultureInfo.InvariantCulture)+"L",
            ulong number=>number.ToString(CultureInfo.InvariantCulture)+"UL",
            _=>Convert.ToString(value,CultureInfo.InvariantCulture)!
        };
        return $"({type})({literal})";
    }
    /// <summary>
    /// The catalog a graph's generated class embeds (D10): only the node kinds the graph uses, every library node
    /// kind, and the types any of them, the graph, <c>resolvedOutputs</c> or (with a trigger) the <c>triggerOutputs</c> table name, closed
    /// over refinement chains, <c>assignableTo</c> and element types. Every other section is kept whole. Built from
    /// the catalog root only, so service schemas never reach it (D2).
    /// </summary>
    public static JsonObject EmbeddedCatalog(JsonObject graph,VisualCatalogSnapshot snapshot)
    {
        // The graph's Custom C# types are part of its catalog, as both validators read it.
        var root=VisualNodeCatalog.WithGraphTypes(snapshot.Root,graph);var kinds=(graph["nodes"] as JsonArray??[]).Select(n=>Text(n,"kind")).ToHashSet(StringComparer.Ordinal);
        var nodes=new JsonObject();
        foreach(var (kind,spec) in root["nodes"] as JsonObject??[])if(kinds.Contains(kind)||spec?["sourceId"]!=null)nodes[kind]=spec?.DeepClone();
        var types=root["types"] as JsonObject??[];var builtIn=VisualNodeCatalog.Root["types"] as JsonObject??[];
        var pending=new Stack<string>(types.Where(t=>!builtIn.ContainsKey(t.Key)).Select(t=>t.Key));
        void Strings(JsonNode? node){switch(node){case JsonObject o:foreach(var (_,v) in o)Strings(v);break;case JsonArray a:foreach(var v in a)Strings(v);break;case JsonValue v when v.TryGetValue<string>(out var text):pending.Push(text);break;}}
        // A Select property node's output type is its property's, under resolvedOutputs: the run reads the value as that.
        Strings(nodes);Strings(graph);Strings(root["resolvedOutputs"]);if(kinds.Contains("trigger"))Strings(root["triggerOutputs"]);
        var used=new HashSet<string>(StringComparer.Ordinal);
        while(pending.TryPop(out var id)) {
            foreach(var link in VisualTypes.Chain(root,id)) {
                if(!used.Add(link)||types[link] is not JsonObject type)continue;
                foreach(var related in (type["assignableTo"] as JsonArray??[]).Append(type["elementType"]))if(related is JsonValue v&&v.TryGetValue<string>(out var text))pending.Push(text);
            }
        }
        var catalog=new JsonObject();
        foreach(var (key,value) in root) {
            if(key=="nodes")catalog[key]=nodes;
            else if(key=="types")catalog[key]=new JsonObject(types.Where(t=>used.Contains(t.Key)).Select(t=>KeyValuePair.Create(t.Key,t.Value?.DeepClone())));
            else if(key!="triggerOutputs"||kinds.Contains("trigger"))catalog[key]=value?.DeepClone();
        }
        VisualNodeCatalog.RefreshStructuralTypes(catalog);
        return catalog;
    }
    public VisualPreview Preview(JsonObject graph)=>Preview(graph,new NodeLibraryCompiler(diagnostics).Preview([]).Snapshot);
    public VisualPreview Preview(JsonObject graph,IEnumerable<NodeLibrarySource> sources) {
        var libraries=new NodeLibraryCompiler(diagnostics).Preview(sources);
        return libraries.Valid?Preview(graph,libraries.Snapshot):new(false,"",[],libraries.Diagnostics.Select(d=>new VisualDiagnostic("$library:"+d.SourceId,d.Message,d.Severity,d.StartLine,d.StartColumn)).ToList());
    }
    static bool IsCustom(JsonObject node,VisualCatalogSnapshot snapshot)=>Text(VisualNodeCatalog.Spec(node,snapshot),"dynamicInputs")=="custom";
    /// <summary>The code of each Custom C# node that has some (a statement-style node is the validator's to report).</summary>
    static CustomNodeSource[] CustomSources(JsonObject graph,VisualCatalogSnapshot snapshot)=>(graph["nodes"] as JsonArray??[]).OfType<JsonObject>()
        .Where(n=>IsCustom(n,snapshot)&&Text(n,"id")!=""&&n["config"] is JsonObject cfg&&!cfg.ContainsKey("code")&&cfg["source"] is JsonValue v&&v.TryGetValue<string>(out _))
        .Select(n=>new CustomNodeSource(Text(n,"id"),Text(n["config"],"source"))).ToArray();
    /// <summary>
    /// The type two numbers are compared in, by C#'s binary numeric promotion: <c>decimal</c> if either side is, else
    /// <c>double</c> if either is floating point (<c>float</c> only when both are), else the wider integer type; a signed
    /// and an unsigned one meet in a signed type wide enough for both (<c>long</c> for <c>int</c> and <c>uint</c>,
    /// <c>decimal</c> for <c>ulong</c> and any signed one). Nullable when either side is. Null when either side is not a number.
    /// </summary>
    internal static string? ComparisonType(JsonObject catalog,string left,string right) {
        static (string Name,bool Nullable)? Number(JsonObject catalog,string id) {
            if(!VisualTypes.Numeric(catalog,id))return null;
            var root=VisualTypes.RootId(catalog,id);var nullable=root.EndsWith('?');if(nullable)root=root[..^1];
            var name=root=="number"?"Double":root.StartsWith("global::System.",StringComparison.Ordinal)?root["global::System.".Length..]:"";
            if(name=="Char")name="UInt16";
            return name is "SByte" or "Byte" or "Int16" or "UInt16" or "Int32" or "UInt32" or "Int64" or "UInt64" or "Single" or "Double" or "Decimal"?(name,nullable):null;
        }
        if(Number(catalog,left) is not {} a||Number(catalog,right) is not {} b)return null;
        static int Size(string n)=>n switch{"SByte" or "Byte"=>1,"Int16" or "UInt16"=>2,"Int32" or "UInt32"=>4,_=>8};
        static bool Signed(string n)=>n is "SByte" or "Int16" or "Int32" or "Int64";
        static string SignedOf(int size)=>size switch{1=>"SByte",2=>"Int16",4=>"Int32",8=>"Int64",_=>"Decimal"};
        string name;
        if(a.Name=="Decimal"||b.Name=="Decimal")name="Decimal";
        else if(a.Name is "Single" or "Double"||b.Name is "Single" or "Double")name=a.Name=="Single"&&b.Name=="Single"?"Single":"Double";
        else if(Signed(a.Name)==Signed(b.Name))name=Size(a.Name)>=Size(b.Name)?a.Name:b.Name;
        else {
            var (signed,unsigned)=Signed(a.Name)?(a.Name,b.Name):(b.Name,a.Name);
            name=Size(signed)>Size(unsigned)?signed:SignedOf(Size(unsigned)*2);
        }
        return "global::System."+name+(a.Nullable||b.Nullable?"?":"");
    }
    /// <summary>The generation with the graph's Custom C# nodes compiled beside its libraries; the catalog is unchanged.</summary>
    /// <param name="services">The accepted typed service calls, read once by the caller for the whole preview.</param>
    VisualCatalogSnapshot WithCustom(JsonObject graph,VisualCatalogSnapshot snapshot,GeneratedServiceCalls services) {
        var sources=CustomSources(graph,snapshot);
        if(sources.Length==0)return snapshot;
        // The editor previews after each edit, and deriving pins compiles once more; most edits leave the code alone.
        // The last compile is kept (one, so at most one compilation stays in memory), keyed by all that goes into it:
        // the accepted service calls, the libraries and the code. Each text is length-prefixed, so no two inputs share a key.
        static string Part(string text)=>text.Length.ToString(CultureInfo.InvariantCulture)+":"+text;
        var key=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            string.Concat(snapshot.Sources.Select(s=>Part(s.Id)+Part(s.Revision.ToString(CultureInfo.InvariantCulture))+Part(s.Source)))+"|"+string.Concat(sources.Select(c=>Part(c.NodeId)+Part(c.Source))))));
        lock(CustomGate)if(LastCustom is {} last&&ReferenceEquals(last.Services,services)&&last.Key==key)return last.Compiled.WithServices(snapshot.Services);
        var compiled=new NodeLibraryCompiler(diagnostics,services).Preview(snapshot.Sources,sources).Snapshot;
        lock(CustomGate)LastCustom=(services,key,compiled);
        return compiled.WithServices(snapshot.Services);
    }
    // Keyed by the accepted typed service calls (the object, swapped whole when they change) and a hash of the sources.
    static (GeneratedServiceCalls Services,string Key,VisualCatalogSnapshot Compiled)? LastCustom;
    static readonly object CustomGate=new();
    /// <summary>
    /// Whether a stored signature has the pins the code gives: the same pure, input ids with their types, required,
    /// nullable and default, and output ids with their types. Titles, words and type entries are not compared.
    /// </summary>
    internal static bool SamePins(JsonObject stored,JsonObject compiled) {
        static bool Pins(JsonNode? a,JsonNode? b,string[] keys)=>a is JsonObject x&&b is JsonObject y&&x.Count==y.Count&&
            x.All(p=>y.TryGetPropertyValue(p.Key,out var other)&&p.Value is JsonObject mine&&other is JsonObject theirs&&keys.All(k=>SameJson(mine[k],theirs[k])));
        return SameJson(stored["pure"],compiled["pure"])&&Pins(stored["inputs"],compiled["inputs"],["types","required","nullable","hasDefaultValue","default"])&&Pins(stored["outputs"],compiled["outputs"],["type"]);
    }
    /// <summary>
    /// Whether each property a Select property node of the graph reads, of a type the stored signature lists, has the
    /// type and nullability the code gives it: the node's value has that type, so a change is a change of pins. The
    /// properties of a type no node reads, and every description, are not compared. A graph past the catalog's limits is
    /// not looked at (the validator answers it with <c>graph.limit</c>): this runs before validation.
    /// </summary>
    internal static bool SameSelected(JsonObject graph,JsonObject root,JsonObject stored,JsonObject compiled) {
        if(VisualNodeCatalog.OverLimit(root,graph))return true;
        var nodes=VisualNodeCatalog.GraphNodes(graph);
        if(!nodes.Values.Any(n=>Text(n,"kind")=="select_property")||stored["types"] is not JsonObject types)return true;
        var catalog=VisualNodeCatalog.WithGraphTypes(root,graph);
        var wires=(graph["edges"] as JsonArray??[]).OfType<JsonObject>().Where(e=>Text(e,"kind")=="data"&&Text(e,"targetPort")=="value").ToArray();
        foreach(var node in nodes.Values.Where(n=>Text(n,"kind")=="select_property")) {
            var name=Text(node["config"],"property");
            if(name==""||wires.FirstOrDefault(e=>Text(e,"target")==Text(node,"id")) is not {} wire||!nodes.TryGetValue(Text(wire,"source"),out var source))continue;
            var type=Text(VisualNodeCatalog.DataOutputs(source,catalog)[Text(wire,"sourcePort")],"type");
            if(types[type] is not JsonObject mine)continue;
            JsonObject? Member(JsonNode? entry)=>(entry?["properties"] as JsonArray??[]).OfType<JsonObject>().FirstOrDefault(p=>Text(p,"name")==name);
            var before=Member(mine);var now=Member(compiled["types"]?[type]);
            if(before==null&&now==null)continue;
            if(before==null||now==null||!SameJson(before["type"],now["type"])||!SameJson(before["nullable"],now["nullable"]))return false;
        }
        return true;
    }
    /// <summary>Numbers by value, objects by their properties in any order: a signature read back from the editor equals the compiler's.</summary>
    internal static bool SameJson(JsonNode? a,JsonNode? b)=>(a,b) switch {
        (null,null)=>true,
        (JsonObject x,JsonObject y)=>x.Count==y.Count&&x.All(p=>y.TryGetPropertyValue(p.Key,out var other)&&SameJson(p.Value,other)),
        (JsonArray x,JsonArray y)=>x.Count==y.Count&&x.Zip(y).All(p=>SameJson(p.First,p.Second)),
        (JsonValue x,JsonValue y) when x.GetValueKind()==JsonValueKind.Number&&y.GetValueKind()==JsonValueKind.Number=>
            double.Parse(x.ToJsonString(),CultureInfo.InvariantCulture)==double.Parse(y.ToJsonString(),CultureInfo.InvariantCulture),
        (JsonValue x,JsonValue y)=>x.ToJsonString()==y.ToJsonString(),
        _=>false };
    /// <summary>
    /// A copy of the graph with each Custom C# node's signature written from its code, for a caller that writes only the
    /// code. Values and wires of pins that still exist are kept; those of pins that are gone are removed, and so are the
    /// step order wires of a node that became a value node. A node whose code does not compile keeps its signature.
    /// </summary>
    public JsonObject DeriveSignatures(JsonObject graph,VisualCatalogSnapshot snapshot)=>DeriveSignatures(graph,snapshot,out _);
    /// <param name="removed">One warning per value, value wire and step order wire removed, on its node, naming the pin.</param>
    public JsonObject DeriveSignatures(JsonObject graph,VisualCatalogSnapshot snapshot,out List<VisualDiagnostic> removed) {
        var warnings=new List<VisualDiagnostic>();removed=warnings;
        var next=(JsonObject)graph.DeepClone();
        if(snapshot.Compilation==null)snapshot=new NodeLibraryCompiler(diagnostics).Preview(snapshot.Sources).Snapshot.WithServices(snapshot.Services);
        var compiled=WithCustom(next,snapshot,ServiceCalls());
        var edges=next["edges"] as JsonArray;
        foreach(var node in (next["nodes"] as JsonArray??[]).OfType<JsonObject>()) {
            if(!compiled.Custom.TryGetValue(Text(node,"id"),out var custom)||custom.Signature==null||node["config"] is not JsonObject cfg)continue;
            var id=Text(node,"id");var old=cfg["signature"] as JsonObject;var signature=(JsonObject)custom.Signature.DeepClone();cfg["signature"]=signature;
            var inputs=signature["inputs"]!.AsObject();var outputs=signature["outputs"]!.AsObject();var pure=signature["pure"]!.GetValue<bool>();
            // A pin that is gone is named as the node showed it before.
            string Label(string side,string port)=>VisualGraphValidator.Words.PortLabel(old!=null&&VisualNodeCatalog.WellFormedSignature(old)?old[side]?[port]:null,port);
            void Removed(string message,string? input=null,string? output=null)=>warnings.Add(new(id,message,2,InputId:input,PortId:output));
            if(cfg["arguments"] is JsonObject arguments)foreach(var name in arguments.Select(p=>p.Key).Where(k=>!inputs.ContainsKey(k)).ToArray()) {
                arguments.Remove(name);Removed($"Removed the value of {Label("inputs",name)}: the code no longer has that input",input:name);
            }
            if(edges!=null)foreach(var edge in edges.OfType<JsonObject>().ToArray()) {
                if(Text(edge,"kind")=="data") {
                    if(Text(edge,"target")==id&&!inputs.ContainsKey(Text(edge,"targetPort"))) {edges.Remove(edge);Removed($"Removed the connection to {Label("inputs",Text(edge,"targetPort"))}: the code no longer has that input",input:Text(edge,"targetPort"));}
                    else if(Text(edge,"source")==id&&!outputs.ContainsKey(Text(edge,"sourcePort"))) {edges.Remove(edge);Removed($"Removed the connection from {Label("outputs",Text(edge,"sourcePort"))}: the code no longer has that output",output:Text(edge,"sourcePort"));}
                }
                else if(pure&&(Text(edge,"source")==id||Text(edge,"target")==id)) {edges.Remove(edge);Removed("Removed a step order wire: this node is now a value node, which has no place in the run order");}
            }
        }
        return next;
    }
    public VisualPreview Preview(JsonObject graph,VisualCatalogSnapshot snapshot)
    {
        // A descriptor from an invalid library preview is never an executable snapshot.
        if(snapshot.Compilation==null)return Preview(graph,snapshot.Sources);
        if(snapshot.LibraryDiagnostics.Any(d=>d.Severity==3))return new(false,"",[],snapshot.LibraryDiagnostics.Select(d=>new VisualDiagnostic("$library:"+d.SourceId,d.Message,d.Severity,d.StartLine,d.StartColumn)).ToList());
        var libraryErrors=snapshot.Compilation.GetDiagnostics().Where(d=>d.Severity==DiagnosticSeverity.Error).ToArray();
        if(libraryErrors.Length>0)return new(false,"",[],libraryErrors.Select(d=>new VisualDiagnostic("$library:"+d.Location.SourceTree?.FilePath,d.GetMessage())).ToList());
        // Custom C# nodes compile with the libraries. What their code gives is the truth: a stored signature whose pins
        // differ is an error. One that differs only in what the code does not decide for wires (words, type entries, which
        // a newer HassSharp may write differently) is refreshed from the compile and previewed (and returned) as such.
        // One read of the accepted service calls for the whole preview: a schema push swapping them midway cannot give
        // the Custom C# nodes one set and the automation another.
        var services=ServiceCalls();
        snapshot=WithCustom(graph,snapshot,services);
        var errors=new List<VisualDiagnostic>();
        JsonObject? refreshed=null;
        foreach(var (id,custom) in snapshot.Custom.OrderBy(c=>c.Key,StringComparer.Ordinal)) {
            errors.AddRange(custom.Diagnostics.Select(d=>new VisualDiagnostic(id,d.Message,d.Severity,d.StartLine,d.StartColumn,"source")));
            var node=(graph["nodes"] as JsonArray??[]).OfType<JsonObject>().First(n=>Text(n,"id")==id);
            if(custom.Signature==null||node["config"]?["signature"] is not JsonObject stored||!VisualNodeCatalog.WellFormedSignature(stored))continue;
            if(!SamePins(stored,custom.Signature)||!SameSelected(graph,snapshot.Root,stored,custom.Signature))errors.Add(new(id,VisualGraphValidator.Words.StaleSignature,InputId:"source",Code:"custom.signature"));
            else if(!SameJson(stored,custom.Signature)) {
                refreshed??=(JsonObject)graph.DeepClone();
                refreshed["nodes"]!.AsArray().OfType<JsonObject>().First(n=>Text(n,"id")==id)["config"]!["signature"]=custom.Signature.DeepClone();
            }
        }
        if(refreshed!=null)graph=refreshed;
        errors.AddRange(VisualGraphValidator.Validate(graph,snapshot,out var inputTypes));if(errors.Any(e=>e.Severity==3))return new(false,"",[],errors){Graph=refreshed};
        var lines=new List<string>();var maps=new List<VisualSourceMap>();string? current=null;
        var customSections=new List<(string Id,int Start,int End)>();
        void Add(string source)=>lines.AddRange(source.Replace("\r\n","\n").Split('\n'));
        void Section(string id,string source){var start=lines.Count+1;Add(source);maps.Add(new(id,start,lines.Count));}
        try {
            var nodes=graph["nodes"]!.AsArray().OfType<JsonObject>().ToArray();
            string InputType(JsonObject node,string port)=>inputTypes[(Text(node,"id"),port)];
            // The types as the validators read them: the catalog's and each Custom C# node's own.
            var typed=snapshot.WithRoot(VisualNodeCatalog.WithGraphTypes(snapshot.Root,graph));
            var comparisonTypes=nodes.Where(n=>Text(n,"kind")=="compare").ToDictionary(n=>Text(n,"id"),n=> {
                var left=InputType(n,"left");var right=InputType(n,"right");
                // Two numbers meet in the type both widen to, never the narrower one: "any number into any number"
                // decides which wires are accepted, not which side gets rounded.
                return ComparisonType(typed.Root,left,right) ?? (typed.IsAssignable(left,right)?right:left);
            });
            var compiledConversions=new Dictionary<string,(string Source,string Target)>();
            foreach(var node in nodes.Where(n=>Text(n,"kind") is "convert" or "constant")) {
                // CLR conversions are between roots; Output still narrows to the declared (possibly refined) type.
                var sourceType=VisualTypes.RootId(typed.Root,InputType(node,"value"));var target=VisualTypes.RootId(typed.Root,Text(node["config"],"type"));
                if(snapshot.Types.TryGetValue(sourceType,out var from)&&snapshot.Types.TryGetValue(target,out var to) && snapshot.Compilation!.ClassifyConversion(from.Symbol,to.Symbol).IsUserDefined)
                    compiledConversions.Add(Text(node,"id"),(ClrExpression(from.Symbol),ClrExpression(to.Symbol)));
            }
            var roots=nodes.Where(n=>Text(n,"kind")=="trigger").OrderBy(n=>Text(n,"id"),StringComparer.Ordinal).ToArray();
            // Internal identifiers are deterministic and cannot shadow authored identifiers.
            var authored=graph.ToJsonString()+string.Join("\n",snapshot.Sources.Select(s=>s.Source));var prefix="__visual_";while(authored.Contains(prefix,StringComparison.Ordinal))prefix+="x";
            var embedded=EmbeddedCatalog(graph,snapshot);var embeddedTypes=embedded["types"]!.AsObject();
            var run=prefix+"run";var handler=prefix+"node";var scope=prefix+"scope";var engine=prefix+"engine";var nodeId=prefix+"id";
            Add("#nullable enable");Add("public class VisualAutomation : global::HassSharp.Automation\n{");
            Add("public override global::HassSharp.AutomationConfiguration Configure() => new(");
            for(var i=0;i<roots.Length;i++) {
                var root=roots[i];current=Text(root,"id");var cfg=root["config"]!;var kind=VisualNodeCatalog.TriggerType(root,snapshot);var entities="[]";var schedules="[]";var triggers="[]";
                if(kind=="entity")entities=$"[new global::HassSharp.EntityReference({Q(Text(cfg,"entity"))})]";
                if(kind=="daily") {
                    // Both validators accept only HH:MM. A time zone is this machine's to know, so it is checked here, on its input.
                    var time=TimeOnly.ParseExact(Text(cfg,"time"),"HH:mm",CultureInfo.InvariantCulture);
                    if(!TimeZoneInfo.TryFindSystemTimeZoneById(Text(cfg,"timeZone"),out _)){errors.Add(new(current,VisualGraphValidator.Words.UnknownTimeZone(Text(cfg,"timeZone")),InputId:"timeZone"));return new(false,"",[],errors){Graph=refreshed};}
                    schedules=$"[global::HassSharp.AutomationSchedule.Daily({Q(current)},new global::System.TimeOnly({time.Hour},{time.Minute}),{Q(Text(cfg,"timeZone"))})]";}
                if(kind=="once") {var at=DateTimeOffset.Parse(Text(cfg,"at"),CultureInfo.InvariantCulture);schedules=$"[global::HassSharp.AutomationSchedule.Once({Q(current)},global::System.DateTimeOffset.Parse({Q(at.ToString("O"))}))]";}
                if(kind=="ha")triggers=$"[new global::HassSharp.HomeAssistantTrigger({Q(current)},{Q(cfg["trigger"]!.ToJsonString())})]";
                var startup=cfg["startup"]?.GetValue<bool>()??kind=="startup";var manual=cfg["manual"]?.GetValue<bool>()??true;
                Section(current,$"new global::HassSharp.AutomationAction(nameof({VisualNodeCatalog.EffectiveTriggerMethod(root)}),global::HassSharp.AutomationMode.{Text(cfg,"mode","Single")},Startup:{startup.ToString().ToLowerInvariant()},Manual:{manual.ToString().ToLowerInvariant()},Entities:{entities},Schedules:{schedules},Triggers:{triggers}){(i+1<roots.Length?",":"")}");
            }
            Add(");");
            // A library node is dispatched by its kind; a Custom C# node, exactly alike, by its own method.
            NodeMethodDescriptor? MethodOf(JsonObject node)=>IsCustom(node,snapshot)?snapshot.Custom.GetValueOrDefault(Text(node,"id"))?.Method:snapshot.Methods.GetValueOrDefault(Text(node,"kind"));
            string MethodKey(JsonObject node)=>IsCustom(node,snapshot)?"custom:"+Text(node,"id"):Text(node,"kind");
            var delegates=nodes.Where(n=>MethodOf(n)!=null).Select(MethodKey).Distinct().Select((key,index)=>(key,name:prefix+"method_"+index)).ToDictionary(p=>p.key,p=>p.name);
            foreach(var node in nodes.Where(n=>MethodOf(n)!=null).DistinctBy(MethodKey)) {
                var method=MethodOf(node)!;
                Add($"private delegate {ClrExpression(method.Method.ReturnType)} {delegates[MethodKey(node)]}({string.Join(",",method.Parameters.Select(p=>ClrExpression(p.Symbol.Type)+" @"+p.Name))});");
            }
            foreach(var root in roots)Section(Text(root,"id"),$"public global::System.Threading.Tasks.Task {VisualNodeCatalog.EffectiveTriggerMethod(root)}() => {run}({Q(Text(root,"id"))});");
            // In the automation's own load context, so System.Text.Json's caches of its types go when it unloads.
            Add($"private static readonly global::System.Text.Json.JsonSerializerOptions {prefix}json = global::HassSharp.VisualRuntime.JsonOptions();");
            Add($"private global::System.Threading.Tasks.Task {run}(string {nodeId}) => new global::HassSharp.VisualRuntime({Pieces(graph.ToJsonString())},{Pieces(embedded.ToJsonString())},new global::System.Collections.Generic.Dictionary<string,global::System.Type> {{");
            var clrTypes=snapshot.Types.Values.Concat(snapshot.Custom.Values.SelectMany(c=>c.Types)).DistinctBy(t=>t.Id);
            foreach(var type in clrTypes.Where(t=>embeddedTypes.ContainsKey(t.Id)).OrderBy(t=>t.Id,StringComparer.Ordinal))Add($"[{Q(type.Id)}]=typeof({(type.Symbol.TypeKind==TypeKind.Dynamic ? "global::System.Object" : type.Symbol.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))}),");
            Add($"}},TriggerData,CancellationToken,{handler},new global::System.Collections.Generic.HashSet<string>{{{string.Join(",",compiledConversions.Keys.Select(Q))}}},new global::System.Collections.Generic.Dictionary<string,string>{{{string.Join(",",comparisonTypes.Select(p=>"["+Q(p.Key)+"]="+Q(p.Value)))}}},{prefix}json).RunAsync({nodeId});");
            Add($"private async global::System.Threading.Tasks.Task {handler}(string {nodeId},global::HassSharp.VisualScope {scope},global::HassSharp.VisualRuntime {engine}) {{");
            Add("await global::System.Threading.Tasks.Task.CompletedTask;");
            Add($"switch({nodeId}) {{");
            foreach(var node in nodes) {
                var kind=Text(node,"kind");var id=Text(node,"id");current=id;var method=MethodOf(node);if(method==null&&!compiledConversions.ContainsKey(id))continue;
                var start=lines.Count+1;Add($"case {Q(id)}: {{");
                if(compiledConversions.TryGetValue(id,out var conversion))
                    Add($"{engine}.Output({scope},{Q(id)},({conversion.Target}){engine}.Input<{conversion.Source}>({scope},{Q(id)},\"value\"));");
                else {
                    var args=new List<string>();
                    Add($"{delegates[MethodKey(node)]} {prefix}selected = {method!.InvocationType}.{method.MethodName};");
                    foreach(var parameter in method.Parameters) {
                        var connected=graph["edges"]!.AsArray().Any(e=>Text(e,"kind")=="data"&&Text(e,"target")==id&&Text(e,"targetPort")==parameter.Name);
                        var value=VisualNodeCatalog.InputValue(node,parameter.Name,snapshot);
                        var absent=!connected&&(value==null||Text(value,"kind")=="unset");
                        var expression=parameter.IsCancellationToken?$"{scope}.CancellationToken":parameter.IsContext?$"{engine}.Context({scope},{Q(id)})"
                            :absent&&parameter.HasDefaultValue?DefaultExpression(parameter):absent?$"default({ClrExpression(parameter.Symbol.Type)})":$"{engine}.Input<{ClrExpression(parameter.Symbol.Type)}>({scope},{Q(id)},{Q(parameter.Name)})";
                        args.Add("@"+parameter.Name+": "+expression);
                    }
                    var call=(method.AwaitResult?"await ":"")+prefix+"selected("+string.Join(",",args)+")";
                    if(method.Outputs.IsEmpty)Add(call+";");
                    else if(method.Outputs is [{Element:null} only])Add($"{engine}.Output({scope},{Q(id)},{call},{Q(only.Port)});");
                    else {
                        // A tuple with named elements: one output per element, read by position (the delegate type drops the names).
                        Add($"var {prefix}result = {call};");
                        foreach(var (output,index) in method.Outputs.Select((o,i)=>(o,i)))Add($"{engine}.Output({scope},{Q(id)},{prefix}result.Item{index+1},{Q(output.Port)});");
                    }
                }
                Add("break; }");maps.Add(new(id,start,lines.Count));
            }
            Add("default: throw new global::System.InvalidOperationException(\"Unknown compiled node\");\n}\n}");Add("}");
            foreach(var library in snapshot.Sources) { Add($"// NodeLibrarySource {Q(library.Id)} revision {library.Revision.ToString(CultureInfo.InvariantCulture)}"); Section("$library:"+library.Id,NodeLibraryCompiler.WrapSource(library.Source)); }
            // Each Custom C# node's code, in its own namespace; a diagnostic in it is the node's, at its own line and column.
            foreach(var node in nodes.Where(n=>IsCustom(n,snapshot)&&snapshot.Custom.ContainsKey(Text(n,"id")))) {
                var id=Text(node,"id");Add($"// Custom C# node {Q(id)}");var start=lines.Count+1;
                Add(NodeLibraryCompiler.WrapCustomSource(id,Text(node["config"],"source")));maps.Add(new(id,start,lines.Count));customSections.Add((id,start,lines.Count));
            }
            var source=string.Join('\n',lines)+"\n";
            var tree=CSharpSyntaxTree.ParseText(source,new CSharpParseOptions(LanguageVersion.Latest));
            var trees=new List<SyntaxTree>{tree,CSharpSyntaxTree.ParseText(DiagnosticsProvider.GlobalUsings)};
            var compilation=CSharpCompilation.Create("VisualPreview",trees,NodeLibraryCompiler.FrameworkReferences().Add(services.Reference),new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,allowUnsafe:true,nullableContextOptions:NullableContextOptions.Enable));
            foreach(var d in compilation.GetDiagnostics().Where(d=>d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)) {
                // Only a diagnostic in the generated automation has a line a node owns: one elsewhere (the global usings)
                // is the graph's, never the node whose section has the same line number. The typed service calls are a
                // built assembly here, so nothing in them is bound or reported again.
                if(d.Location.SourceTree is {} other&&other!=tree) {
                    if(d.Severity==DiagnosticSeverity.Error)errors.Add(new(null,d.GetMessage()));
                    continue;
                }
                var location=d.Location.GetLineSpan();var line=location.StartLinePosition.Line+1;
                // A Custom C# node's own code reported its warnings already, when it was compiled with the libraries.
                if(customSections.FirstOrDefault(c=>line>=c.Start&&line<=c.End) is {Id:not null} section) {
                    if(d.Severity==DiagnosticSeverity.Error)errors.Add(new(section.Id,d.GetMessage(),3,Math.Max(1,line-section.Start+1-NodeLibraryCompiler.SourceLineOffset),location.StartLinePosition.Character+1,"source"));
                    continue;
                }
                errors.Add(new(maps.FirstOrDefault(m=>line>=m.StartLine&&line<=m.EndLine)?.NodeId,d.GetMessage(),(int)d.Severity,line,location.StartLinePosition.Character+1));
            }
            return new(!errors.Any(e=>e.Severity==3),source,maps,errors){EmbeddedCatalog=embedded.ToJsonString(),Graph=refreshed};
        }catch(Exception e)when(e is ArgumentException or InvalidOperationException or FormatException or JsonException or TimeZoneNotFoundException){errors.Add(new(current,e.Message));return new(false,string.Join('\n',lines),maps,errors){Graph=refreshed};}
    }
}
