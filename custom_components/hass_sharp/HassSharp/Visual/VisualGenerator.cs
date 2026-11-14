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
    /// <summary>The graph as previewed when its Custom C# signatures were derived from their code first; null otherwise.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public JsonObject? Graph { get; init; }
    /// <summary>The pruned catalog JSON the generated class embeds (the run snapshot's catalog); never serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? EmbeddedCatalog { get; init; }
}

/// <summary>V4-only compiler. Preview inspects syntax and symbols without emitting or executing user code.</summary>
public sealed class VisualGenerator(DiagnosticsProvider diagnostics)
{
    static string Text(JsonNode? n,string key,string fallback="")=>VisualNodeCatalog.Text(n,key,fallback);
    static string Q(string value)=>JsonSerializer.Serialize(value);
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
    /// kind, and the types any of them, the graph or (with a trigger) the <c>triggerOutputs</c> table name, closed
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
        Strings(nodes);Strings(graph);if(kinds.Contains("trigger"))Strings(root["triggerOutputs"]);
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
    /// <summary>The generation with the graph's Custom C# nodes compiled beside its libraries; the catalog is unchanged.</summary>
    VisualCatalogSnapshot WithCustom(JsonObject graph,VisualCatalogSnapshot snapshot) {
        var sources=CustomSources(graph,snapshot);
        return sources.Length==0?snapshot:new NodeLibraryCompiler(diagnostics).Preview(snapshot.Sources,sources).Snapshot.WithServices(snapshot.Services);
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
    public JsonObject DeriveSignatures(JsonObject graph,VisualCatalogSnapshot snapshot) {
        var next=(JsonObject)graph.DeepClone();
        if(snapshot.Compilation==null)snapshot=new NodeLibraryCompiler(diagnostics).Preview(snapshot.Sources).Snapshot.WithServices(snapshot.Services);
        var compiled=WithCustom(next,snapshot);
        var edges=next["edges"] as JsonArray;
        foreach(var node in (next["nodes"] as JsonArray??[]).OfType<JsonObject>()) {
            if(!compiled.Custom.TryGetValue(Text(node,"id"),out var custom)||custom.Signature==null||node["config"] is not JsonObject cfg)continue;
            var id=Text(node,"id");var signature=(JsonObject)custom.Signature.DeepClone();cfg["signature"]=signature;
            var inputs=signature["inputs"]!.AsObject();var outputs=signature["outputs"]!.AsObject();var pure=signature["pure"]!.GetValue<bool>();
            if(cfg["arguments"] is JsonObject arguments)foreach(var name in arguments.Select(p=>p.Key).Where(k=>!inputs.ContainsKey(k)).ToArray())arguments.Remove(name);
            if(edges!=null)foreach(var edge in edges.OfType<JsonObject>().Where(e=>Text(e,"kind")=="data"
                    ? Text(e,"target")==id&&!inputs.ContainsKey(Text(e,"targetPort"))||Text(e,"source")==id&&!outputs.ContainsKey(Text(e,"sourcePort"))
                    : pure&&(Text(e,"source")==id||Text(e,"target")==id)).ToArray())edges.Remove(edge);
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
        // Custom C# nodes compile with the libraries. What their code gives is the truth: a stored signature that differs is an error.
        snapshot=WithCustom(graph,snapshot);
        var errors=new List<VisualDiagnostic>();
        foreach(var (id,custom) in snapshot.Custom.OrderBy(c=>c.Key,StringComparer.Ordinal)) {
            errors.AddRange(custom.Diagnostics.Select(d=>new VisualDiagnostic(id,d.Message,d.Severity,d.StartLine,d.StartColumn,"source")));
            var node=(graph["nodes"] as JsonArray??[]).OfType<JsonObject>().First(n=>Text(n,"id")==id);
            if(custom.Signature!=null&&node["config"]?["signature"] is JsonObject stored&&VisualNodeCatalog.WellFormedSignature(stored)&&!SameJson(stored,custom.Signature))
                errors.Add(new(id,VisualGraphValidator.Words.StaleSignature,InputId:"source",Code:"custom.signature"));
        }
        errors.AddRange(VisualGraphValidator.Validate(graph,snapshot,out var inputTypes));if(errors.Any(e=>e.Severity==3))return new(false,"",[],errors);
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
                return typed.IsAssignable(left,right)?right:left;
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
                if(kind=="entity")entities="["+diagnostics.EntityReferenceExpression(Text(cfg,"entity"))+"]";
                if(kind=="daily") {var time=TimeOnly.ParseExact(Text(cfg,"time"),"HH:mm",CultureInfo.InvariantCulture);TimeZoneInfo.FindSystemTimeZoneById(Text(cfg,"timeZone"));schedules=$"[global::HassSharp.AutomationSchedule.Daily({Q(current)},new global::System.TimeOnly({time.Hour},{time.Minute}),{Q(Text(cfg,"timeZone"))})]";}
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
            Add($"private global::System.Threading.Tasks.Task {run}(string {nodeId}) => new global::HassSharp.VisualRuntime({Q(graph.ToJsonString())},{Q(embedded.ToJsonString())},new global::System.Collections.Generic.Dictionary<string,global::System.Type> {{");
            var clrTypes=snapshot.Types.Values.Concat(snapshot.Custom.Values.SelectMany(c=>c.Types)).DistinctBy(t=>t.Id);
            foreach(var type in clrTypes.Where(t=>embeddedTypes.ContainsKey(t.Id)).OrderBy(t=>t.Id,StringComparer.Ordinal))Add($"[{Q(type.Id)}]=typeof({(type.Symbol.TypeKind==TypeKind.Dynamic ? "global::System.Object" : type.Symbol.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))}),");
            Add($"}},TriggerData,CancellationToken,{handler},new global::System.Collections.Generic.HashSet<string>{{{string.Join(",",compiledConversions.Keys.Select(Q))}}},new global::System.Collections.Generic.Dictionary<string,string>{{{string.Join(",",comparisonTypes.Select(p=>"["+Q(p.Key)+"]="+Q(p.Value)))}}}).RunAsync({nodeId});");
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
            var trees=new List<SyntaxTree>{tree,CSharpSyntaxTree.ParseText(DiagnosticsProvider.GlobalUsings)};if(diagnostics.HassEntitiesSyntaxTreeNullable() is {} entityTree)trees.Add(entityTree);
            var compilation=CSharpCompilation.Create("VisualPreview",trees,NodeLibraryCompiler.FrameworkReferences(),new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,allowUnsafe:true,nullableContextOptions:NullableContextOptions.Enable));
            foreach(var d in compilation.GetDiagnostics().Where(d=>d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)) {
                var location=d.Location.GetLineSpan();var line=location.StartLinePosition.Line+1;
                // A Custom C# node's own code reported its warnings already, when it was compiled with the libraries.
                if(customSections.FirstOrDefault(c=>line>=c.Start&&line<=c.End) is {Id:not null} section) {
                    if(d.Severity==DiagnosticSeverity.Error)errors.Add(new(section.Id,d.GetMessage(),3,Math.Max(1,line-section.Start+1-NodeLibraryCompiler.SourceLineOffset),location.StartLinePosition.Character+1,"source"));
                    continue;
                }
                errors.Add(new(maps.FirstOrDefault(m=>line>=m.StartLine&&line<=m.EndLine)?.NodeId,d.GetMessage(),(int)d.Severity,line,location.StartLinePosition.Character+1));
            }
            return new(!errors.Any(e=>e.Severity==3),source,maps,errors){EmbeddedCatalog=embedded.ToJsonString()};
        }catch(Exception e)when(e is ArgumentException or InvalidOperationException or FormatException or JsonException or TimeZoneNotFoundException){errors.Add(new(current,e.Message));return new(false,string.Join('\n',lines),maps,errors);}
    }
}
