using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HassSharp;

public sealed record VisualDiagnostic(string? NodeId, string Message, int Severity = 3, int? StartLine = null, int? StartColumn = null, string? InputId = null, string? PortId = null);
public sealed record VisualSourceMap(string NodeId, int StartLine, int EndLine);
public sealed record VisualPreview(bool Valid, string Source, List<VisualSourceMap> SourceMap, List<VisualDiagnostic> Diagnostics);

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
            long number when number==long.MinValue=>"global::System.Int64.MinValue",
            long number=>number.ToString(CultureInfo.InvariantCulture)+"L",
            ulong number=>number.ToString(CultureInfo.InvariantCulture)+"UL",
            _=>Convert.ToString(value,CultureInfo.InvariantCulture)!
        };
        return $"({type})({literal})";
    }
    public VisualPreview Preview(JsonObject graph)=>Preview(graph,new NodeLibraryCompiler(diagnostics).Preview([]).Snapshot);
    public VisualPreview Preview(JsonObject graph,IEnumerable<NodeLibrarySource> sources) {
        var libraries=new NodeLibraryCompiler(diagnostics).Preview(sources);
        return libraries.Valid?Preview(graph,libraries.Snapshot):new(false,"",[],libraries.Diagnostics.Select(d=>new VisualDiagnostic("$library:"+d.SourceId,d.Message,d.Severity,d.StartLine,d.StartColumn)).ToList());
    }
    public VisualPreview Preview(JsonObject graph,VisualCatalogSnapshot snapshot)
    {
        // A descriptor from an invalid library preview is never an executable snapshot.
        if(snapshot.Compilation==null)return Preview(graph,snapshot.Sources);
        if(snapshot.LibraryDiagnostics.Any(d=>d.Severity==3))return new(false,"",[],snapshot.LibraryDiagnostics.Select(d=>new VisualDiagnostic("$library:"+d.SourceId,d.Message,d.Severity,d.StartLine,d.StartColumn)).ToList());
        var libraryErrors=snapshot.Compilation.GetDiagnostics().Where(d=>d.Severity==DiagnosticSeverity.Error).ToArray();
        if(libraryErrors.Length>0)return new(false,"",[],libraryErrors.Select(d=>new VisualDiagnostic("$library:"+d.Location.SourceTree?.FilePath,d.GetMessage())).ToList());
        var errors=VisualGraphValidator.Validate(graph,snapshot,out var inputTypes);if(errors.Any(e=>e.Severity==3))return new(false,"",[],errors);
        var lines=new List<string>();var maps=new List<VisualSourceMap>();string? current=null;
        void Add(string source)=>lines.AddRange(source.Replace("\r\n","\n").Split('\n'));
        void Section(string id,string source){var start=lines.Count+1;Add(source);maps.Add(new(id,start,lines.Count));}
        try {
            var nodes=graph["nodes"]!.AsArray().OfType<JsonObject>().ToArray();
            string InputType(JsonObject node,string port)=>inputTypes[(Text(node,"id"),port)];
            var comparisonTypes=nodes.Where(n=>Text(n,"kind")=="compare").ToDictionary(n=>Text(n,"id"),n=> {
                var left=InputType(n,"left");var right=InputType(n,"right");
                return snapshot.IsAssignable(left,right)?right:left;
            });
            var compiledConversions=new Dictionary<string,(string Source,string Target)>();
            foreach(var node in nodes.Where(n=>Text(n,"kind") is "convert" or "set_value")) {
                var sourceType=InputType(node,"value");var target=Text(node["config"],"type");
                if(snapshot.Types.TryGetValue(sourceType,out var from)&&snapshot.Types.TryGetValue(target,out var to) && snapshot.Compilation.ClassifyConversion(from.Symbol,to.Symbol).IsUserDefined)
                    compiledConversions.Add(Text(node,"id"),(ClrExpression(from.Symbol),ClrExpression(to.Symbol)));
            }
            var roots=nodes.Where(n=>Text(n,"kind")=="trigger").OrderBy(n=>Text(n,"id"),StringComparer.Ordinal).ToArray();
            // Internal identifiers are deterministic and cannot shadow authored identifiers.
            var authored=graph.ToJsonString()+string.Join("\n",snapshot.Sources.Select(s=>s.Source));var prefix="__visual_";while(authored.Contains(prefix,StringComparison.Ordinal))prefix+="x";
            var run=prefix+"run";var handler=prefix+"node";var scope=prefix+"scope";var engine=prefix+"engine";var nodeId=prefix+"id";
            Add("#nullable enable");Section("$imports",Text(graph,"imports"));Add("public class VisualAutomation : global::HassSharp.Automation\n{");Section("$members",Text(graph,"members"));
            Add("public override global::HassSharp.AutomationConfiguration Configure() => new(");
            for(var i=0;i<roots.Length;i++) {
                var root=roots[i];current=Text(root,"id");var cfg=root["config"]!;var kind=Text(cfg,"type");var entities="[]";var schedules="[]";var triggers="[]";
                if(kind=="entity")entities="["+diagnostics.EntityReferenceExpression(Text(cfg,"entity"))+"]";
                if(kind=="daily") {var time=TimeOnly.ParseExact(Text(cfg,"time"),"HH:mm",CultureInfo.InvariantCulture);TimeZoneInfo.FindSystemTimeZoneById(Text(cfg,"timeZone"));schedules=$"[global::HassSharp.AutomationSchedule.Daily({Q(current)},new global::System.TimeOnly({time.Hour},{time.Minute}),{Q(Text(cfg,"timeZone"))})]";}
                if(kind=="once") {var at=DateTimeOffset.Parse(Text(cfg,"at"),CultureInfo.InvariantCulture);schedules=$"[global::HassSharp.AutomationSchedule.Once({Q(current)},global::System.DateTimeOffset.Parse({Q(at.ToString("O"))}))]";}
                if(kind=="ha")triggers=$"[new global::HassSharp.HomeAssistantTrigger({Q(current)},{Q(cfg["trigger"]!.ToJsonString())})]";
                entities=Text(cfg,"entitiesExpression",entities);schedules=Text(cfg,"schedulesExpression",schedules);
                var startup=cfg["startup"]?.GetValue<bool>()??kind=="startup";var manual=cfg["manual"]?.GetValue<bool>()??true;
                Section(current,$"new global::HassSharp.AutomationAction(nameof({VisualNodeCatalog.EffectiveTriggerMethod(root)}),global::HassSharp.AutomationMode.{Text(cfg,"mode","Single")},Startup:{startup.ToString().ToLowerInvariant()},Manual:{manual.ToString().ToLowerInvariant()},Entities:{entities},Schedules:{schedules},Triggers:{triggers}){(i+1<roots.Length?",":"")}");
            }
            Add(");");
            var delegates=nodes.Select(n=>Text(n,"kind")).Distinct().Where(snapshot.Methods.ContainsKey).Select((kind,index)=>(kind,name:prefix+"method_"+index)).ToDictionary(p=>p.kind,p=>p.name);
            foreach(var (kind,name) in delegates) {
                var method=snapshot.Methods[kind];
                Add($"private delegate {ClrExpression(method.Method.ReturnType)} {name}({string.Join(",",method.Parameters.Select(p=>ClrExpression(p.Symbol.Type)+" @"+p.Name))});");
            }
            foreach(var root in roots)Section(Text(root,"id"),$"public global::System.Threading.Tasks.Task {VisualNodeCatalog.EffectiveTriggerMethod(root)}() => {run}({Q(Text(root,"id"))});");
            Add($"private global::System.Threading.Tasks.Task {run}(string {nodeId}) => new global::HassSharp.VisualRuntime({Q(graph.ToJsonString())},{Q(snapshot.Catalog.ToJsonString())},new global::System.Collections.Generic.Dictionary<string,global::System.Type> {{");
            foreach(var type in snapshot.Types.Values.OrderBy(t=>t.Id,StringComparer.Ordinal))Add($"[{Q(type.Id)}]=typeof({(type.Symbol.TypeKind==TypeKind.Dynamic ? "global::System.Object" : type.Symbol.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))}),");
            Add($"}},TriggerData,CancellationToken,{handler},new global::System.Collections.Generic.HashSet<string>{{{string.Join(",",compiledConversions.Keys.Select(Q))}}},new global::System.Collections.Generic.Dictionary<string,string>{{{string.Join(",",comparisonTypes.Select(p=>"["+Q(p.Key)+"]="+Q(p.Value)))}}}).RunAsync({nodeId});");
            Add($"private async global::System.Threading.Tasks.Task {handler}(string {nodeId},global::HassSharp.VisualScope {scope},global::HassSharp.VisualRuntime {engine}) {{");
            var authoredNames = CSharpSyntaxTree.ParseText("class Authored {"+Text(graph,"members")+"}").GetRoot().DescendantTokens().Where(t=>t.IsKind(SyntaxKind.IdentifierToken)).Select(t=>t.ValueText).ToHashSet(StringComparer.Ordinal);
            if(!authoredNames.Contains("values"))Add($"var values={scope}.Values;");
            if(!authoredNames.Contains("responses"))Add($"var responses={scope}.Responses;");
            if(!authoredNames.Contains("lastError"))Add($"var lastError={scope}.LastError;");
            Add($"var CancellationToken={scope}.CancellationToken;");
            Add("await global::System.Threading.Tasks.Task.CompletedTask;");
            Add($"try {{ switch({nodeId}) {{");
            foreach(var node in nodes) {
                var kind=Text(node,"kind");var id=Text(node,"id");current=id;if(kind!="custom"&&!snapshot.Methods.ContainsKey(kind)&&!compiledConversions.ContainsKey(id))continue;
                var start=lines.Count+1;Add($"case {Q(id)}: {{");
                if(kind=="custom")Add(Text(node["config"],"code"));
                else if(compiledConversions.TryGetValue(id,out var conversion))
                    Add($"{engine}.Output({scope},{Q(id)},({conversion.Target}){engine}.Input<{conversion.Source}>({scope},{Q(id)},\"value\"));");
                else {
                    var method=snapshot.Methods[kind];var args=new List<string>();
                    Add($"{delegates[kind]} {prefix}selected = {method.InvocationType}.{method.MethodName};");
                    foreach(var parameter in method.Parameters) {
                        var connected=graph["edges"]!.AsArray().Any(e=>Text(e,"kind")=="data"&&Text(e,"target")==id&&Text(e,"targetPort")==parameter.Name);
                        var value=VisualNodeCatalog.InputValue(node,parameter.Name,snapshot);
                        var absent=!connected&&(value==null||Text(value,"kind")=="unset");
                        var expression=parameter.IsCancellationToken?"CancellationToken":absent&&parameter.HasDefaultValue?DefaultExpression(parameter):absent?$"default({ClrExpression(parameter.Symbol.Type)})":$"{engine}.Input<{ClrExpression(parameter.Symbol.Type)}>({scope},{Q(id)},{Q(parameter.Name)})";
                        args.Add("@"+parameter.Name+": "+expression);
                    }
                    var call=(method.AwaitResult?"await ":"")+prefix+"selected("+string.Join(",",args)+")";
                    Add(method.ResultType==null?call+";":$"{engine}.Output({scope},{Q(id)},{call},\"result\");");
                }
                Add("break; }");maps.Add(new(id,start,lines.Count));
            }
            Add("default: throw new global::System.InvalidOperationException(\"Unknown compiled node\");\n} } finally {");Add(authoredNames.Contains("lastError")?"}\n}":$"{scope}.LastError=lastError; }}\n}}");Add("}");
            foreach(var library in snapshot.Sources) { Add($"// NodeLibrarySource {Q(library.Id)} revision {library.Revision.ToString(CultureInfo.InvariantCulture)}"); Section("$library:"+library.Id,NodeLibraryCompiler.WrapSource(library.Source)); }
            var source=string.Join('\n',lines)+"\n";
            var tree=CSharpSyntaxTree.ParseText(source,new CSharpParseOptions(LanguageVersion.Latest));
            var trees=new List<SyntaxTree>{tree,CSharpSyntaxTree.ParseText(DiagnosticsProvider.GlobalUsings)};if(diagnostics.HassEntitiesSyntaxTreeNullable() is {} entityTree)trees.Add(entityTree);
            var compilation=CSharpCompilation.Create("VisualPreview",trees,NodeLibraryCompiler.FrameworkReferences(),new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,allowUnsafe:true,nullableContextOptions:NullableContextOptions.Enable));
            foreach(var d in compilation.GetDiagnostics().Where(d=>d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)) {
                var location=d.Location.GetLineSpan();var line=location.StartLinePosition.Line+1;errors.Add(new(maps.FirstOrDefault(m=>line>=m.StartLine&&line<=m.EndLine)?.NodeId,d.GetMessage(),(int)d.Severity,line,location.StartLinePosition.Character+1));
            }
            return new(!errors.Any(e=>e.Severity==3),source,maps,errors);
        }catch(Exception e)when(e is ArgumentException or InvalidOperationException or FormatException or JsonException or TimeZoneNotFoundException){errors.Add(new(current,e.Message));return new(false,string.Join('\n',lines),maps,errors);}
    }
}
