using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HassSharp;
using HassSharp.Entities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using static VisualExecutionTests;

/// <summary>
/// The entity classes generated from Home Assistant's lists: one class per domain with a typed <c>State</c> and
/// attributes, enums that carry Home Assistant's text, names that are valid and stable, and hostile text kept in literals.
/// </summary>
static class EntityGeneratorTests
{
    static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name));
    static EntityGenerator.Generated Generated(string json) => EntityGenerator.Generate(EntitySchemaCatalog.Parse(json).Items.Select(p => KeyValuePair.Create(p.Key, (JsonObject)p.Value!)));
    static string Source(string json) => Generated(json).Source;
    static readonly List<AssemblyLoadContext> Contexts = [];

    // Compiled alone against the framework (HassSharp among it) and loaded into a collectible context; HassSharp
    // resolves to the host's copy, so the handles are the host's Entity.
    static Assembly Load(string source)
    {
        var compilation = CSharpCompilation.Create("Entities_" + Guid.NewGuid().ToString("N"), [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest, documentationMode: DocumentationMode.Diagnose))],
            NodeLibraryCompiler.FrameworkReferences(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        var problems = emitted.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning && d.Id != "CS1591").ToArray();
        Check(emitted.Success && problems.Length == 0, "the generated entities do not compile cleanly: " + string.Join("\n", problems.Take(5)));
        var context = new AssemblyLoadContext("entities", isCollectible: true);
        Contexts.Add(context);
        image.Position = 0;
        return context.LoadFromStream(image);
    }

    static Type Class(Assembly assembly, string name) => assembly.GetType(EntityGenerator.Namespace + "." + name) ?? throw new Exception("no type " + name);
    static PropertyInfo Member(Assembly assembly, string type, string name) => Class(assembly, type).GetProperty(name) ?? throw new Exception($"{type} has no {name}");
    static object? Get(object owner, string name) => owner.GetType().GetProperty(name)!.GetValue(owner);
    static string? State(object entity) => Get(entity, "State")?.ToString();

    static T Throws<T>(Action action, string what) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        catch (Exception error) { throw new Exception($"{what}: threw {error.GetType().Name} instead of {typeof(T).Name}: {error.Message}"); }
        throw new Exception($"{what}: did not throw {typeof(T).Name}");
    }

    // The generated source of the fixture's domains, for reading (not run by default).
    [RuntimeSuite("--print-entities", RunByDefault = false)]
    public static void Print(string? fixture) => Console.Write(Source(Fixture(fixture ?? "ha-entity-components.json")));

    [RuntimeSuite("--entity-generator", Order = 39)]
    public static async Task Run()
    {
        var previousPath = HassPath.HassConfiguration; HassPath.HassConfiguration = Path.Combine(Path.GetTempPath(), "entity-generator-" + Guid.NewGuid());
        var (entity, log, call) = (PyInterop.Entity, PyInterop.Log, PyInterop.CallService);
        try
        {
            FixtureClasses();
            Reading();
            Names();
            Hostile();
            Spans();
            await Installed();
            ServicesLeaveEntitiesAlone();
            BrokenDomain();
            await SwappedWhileRunning();
            var root = Path.Combine(Path.GetTempPath(), "entity-generator-start-" + Guid.NewGuid());
            try
            {
                await FirstStart(root);
                await Persisted(root);
                await MalformedPush(root);
            }
            finally { await ServiceCallTests.DeleteWhenQuiet(root); }
        }
        finally
        {
            (PyInterop.Entity, PyInterop.Log, PyInterop.CallService) = (entity, log, call);
            HassPath.HassConfiguration = previousPath;
            foreach (var context in Contexts) context.Unload();
            Contexts.Clear();
        }
    }

    // The fixture's 58 domains compile, byte for byte the same each time, with the types the rules give.
    static void FixtureClasses()
    {
        var source = Source(Fixture("ha-entity-components.json"));
        var assembly = Load(source);
        Check(source == Source(Fixture("ha-entity-components.json")), "deterministic");
        Check(source.Contains("public sealed class BinarySensorEntity") && source.Contains("public enum BinarySensorState"), "binary_sensor");
        Check(source.Contains("EntityText(\"clear-night\"") && source.Contains("ClearNight"), "hyphenated state");
        // A whole word: sensor's state_class attribute is the enum SensorStateClass.
        Check(!Regex.IsMatch(source, @"\benum SensorState\b") && source.Contains("public enum SensorStateClass") && Member(assembly, "SensorEntity", "State").PropertyType == typeof(string), "sensor");
        Check(Member(assembly, "SceneEntity", "State").PropertyType == typeof(string), "no metadata");
        Check(Member(assembly, "LightEntity", "Brightness").PropertyType == typeof(JsonElement?), "json attribute");
        Check(Member(assembly, "LightEntity", "SupportedColorModes").PropertyType == typeof(JsonElement?), "same list, longer name");
        Check(Member(assembly, "LightEntity", "ColorMode").PropertyType.Name.StartsWith("Nullable"), "enum attribute");
        Check(Nullable.GetUnderlyingType(Member(assembly, "LightEntity", "ColorMode").PropertyType)!.Name == "LightColorMode", "the enum is named domain and attribute");
        Check(Member(assembly, "MediaPlayerEntity", "IsVolumeMuted").PropertyType == typeof(bool?), "flag");
        Check(Class(assembly, "LightEntity").BaseType == typeof(Entity) && Class(assembly, "LightEntity").IsSealed, "a sealed Entity");
        Check(Class(assembly, "LightEntity").GetCustomAttribute<EntityDomainAttribute>()?.Domain == "light", "the domain attribute");
        Check(assembly.GetTypes().Count(t => t.IsDefined(typeof(EntityDomainAttribute))) == 58, "a class per domain");

        // State: the listed states in ordinal order, then unknown and unavailable, then the catch-all without a text.
        var lightState = Class(assembly, "LightState");
        Check(string.Join(",", Enum.GetNames(lightState)) == "Off,On,Unknown,Unavailable,Other", "light states: " + string.Join(",", Enum.GetNames(lightState)));
        Check(string.Join(",", Enum.GetValues(lightState).Cast<Enum>().Select(v => EntityText.Of(v) ?? "-")) == "off,on,unknown,unavailable,-", "light state texts");
        Check(lightState.GetField("On")!.GetCustomAttribute<EntityTextAttribute>()!.Label == "On" && lightState.GetField("Unavailable")!.GetCustomAttribute<EntityTextAttribute>()!.Label == "Unavailable", "labels");
        var colorMode = Class(assembly, "LightColorMode");
        Check(Enum.GetNames(colorMode)[^1] == "Other" && EntityText.Of((Enum)Enum.Parse(colorMode, "Other")) == null && Enum.GetNames(colorMode).Contains("Unknown")
            && !Enum.GetNames(colorMode).Contains("Unavailable"), "an attribute enum gets only the catch-all: " + string.Join(",", Enum.GetNames(colorMode)));
        Check(colorMode.GetField("Onoff")!.GetCustomAttribute<EntityTextAttribute>()!.Label == "On/Off", "an attribute value's label");
        Check(assembly.GetType(EntityGenerator.Namespace + ".LightSupportedColorModes") == null, "no enum for the longer name of the same list");
        Console.WriteLine("PASS entity generator: the fixture's 58 domains compile cleanly and byte-stably, with typed states and attributes");
    }

    // Handles read the live state through the factory: listed and off-list states, enum and flag attributes, wrong types.
    static void Reading()
    {
        var assembly = Load(Source(Fixture("ha-entity-components.json")));
        var create = assembly.GetType("HassSharp.Entities.EntityHandles")!.GetMethod("Create")!;
        PyInterop.Entity = id => new() { EntityId = id, State = "jammed" };
        var front = (Entity)create.Invoke(null, ["lock.front"])!;
        Check(front.GetType().Name == "LockEntity" && State(front) == "Jammed", "listed state");
        PyInterop.Entity = id => new() { EntityId = id, State = "weird" }; Check(State(front) == "Other", "off-list state");
        PyInterop.Entity = id => new() { EntityId = id, State = "unavailable" }; Check(State(front) == "Unavailable", "unavailable");
        Check(create.Invoke(null, ["made_up.thing"])!.GetType() == typeof(Entity), "unknown domain");
        Throws<TargetInvocationException>(() => create.Invoke(null, ["not an id"]), "a malformed id");
        var wrong = Throws<TargetInvocationException>(() => Activator.CreateInstance(front.GetType(), "light.kitchen"), "another domain's id");
        Check(wrong.InnerException is ArgumentException && wrong.InnerException.Message.Contains("light.kitchen"), "another domain's id: " + wrong.InnerException?.Message);

        // A handle class carries the converter itself: it is written as its id and read back as its own type.
        Check(JsonSerializer.Serialize(front, front.GetType()) == "\"lock.front\"", "json: " + JsonSerializer.Serialize(front, front.GetType()));
        Check(JsonSerializer.Deserialize("\"lock.back\"", front.GetType()) is Entity { EntityId: "lock.back" } back && back.GetType() == front.GetType(), "json back");

        // Review Focus 2
        PyInterop.Entity = id => new() { EntityId = id, State = "on", Attributes = new() { ["color_mode"] = 5, ["is_volume_muted"] = "yes" } };
        Check(Get(create.Invoke(null, ["light.a"])!, "ColorMode")!.ToString() == "Other", "a number where text was expected");
        Check(Get(create.Invoke(null, ["media_player.a"])!, "IsVolumeMuted") == null, "text where a flag was expected");
        PyInterop.Entity = id => new() { EntityId = id, State = "on", Attributes = new() { ["color_mode"] = "hs", ["is_volume_muted"] = true, ["brightness"] = 128 } };
        var light = create.Invoke(null, ["light.a"])!;
        Check(Get(light, "ColorMode")!.ToString() == "Hs" && Get(create.Invoke(null, ["media_player.a"])!, "IsVolumeMuted") is true, "listed values");
        Check(Get(light, "Brightness") is JsonElement { ValueKind: JsonValueKind.Number } brightness && brightness.GetInt32() == 128, "a JSON attribute");
        PyInterop.Entity = id => new() { EntityId = id, State = "off", Attributes = new() { ["color_mode"] = "laser", ["supported_color_modes"] = new[] { "hs" } } };
        Check(Get(light, "ColorMode")!.ToString() == "Other" && Get(light, "SupportedColorModes") is JsonElement { ValueKind: JsonValueKind.Array }, "an off-list value");
        PyInterop.Entity = id => new() { EntityId = id, State = "off", Attributes = new() { ["color_mode"] = null! } };
        Check(Get(light, "ColorMode") == null && Get(light, "Brightness") == null && Get(create.Invoke(null, ["media_player.a"])!, "IsVolumeMuted") == null, "absent or JSON null is null");
        Console.WriteLine("PASS entity generator: handles read listed, off-list and missing states and attributes; a handle is written as its id");
    }

    static void Names()
    {
        // Review Focus 5
        // "input__boolean" sorts before "input_boolean", so it keeps the plain name.
        string Names(string domains) => string.Join(",", Regex.Matches(Source("""{"domains":[""" + domains + """],"translations":{}}"""), @"\[global::HassSharp\.Entities\.EntityDomain\(""(\w+)""\)\]\s+public sealed class (\w+) ").Select(m => m.Groups[1].Value + "=" + m.Groups[2].Value));
        Check(Names("\"input_boolean\",\"input__boolean\"") == "input__boolean=InputBooleanEntity,input_boolean=InputBooleanEntity2", "collision: " + Names("\"input_boolean\",\"input__boolean\""));
        Check(Names("\"a\",\"zone\",\"input_boolean\",\"input__boolean\"").Contains("input__boolean=InputBooleanEntity,input_boolean=InputBooleanEntity2"), "names do not move");

        // Type names keep clear of the hand-written types (an enum that would take one is named after its class); members
        // of the base class, State and the class's own name;
        // an enum member of its enum's name; and the catch-all of a listed "other". An enum EntityDomain may be: an attribute
        // name looks only for attribute classes, so [EntityDomain(...)] still means EntityDomainAttribute.
        static string Key(string key, string text) => $"\"component.entity.entity_component._.{key}\":\"{text}\"";
        var reserved = Source("""{"domains":["entity"],"translations":{""" + string.Join(",",
            Key("state.other", "Other"), Key("state.entity_state", "Self"), Key("state.unknown_", "Not known"),
            Key("state_attributes.domain.state.a", "A"), Key("state_attributes.text.state.b", "B"), Key("state_attributes.text_attribute.state.c", "C"),
            Key("state_attributes.handles.state.d", "D"), Key("state_attributes.json_converter.state.e", "E"), Key("state_attributes.domain_attribute.state.f", "F"),
            Key("state_attributes.raw_state.name", "Raw"), Key("state_attributes.snapshot.name", "Snapshot"), Key("state_attributes.state.name", "State"),
            Key("state_attributes.get_type.name", "Type"), Key("state_attributes.entity_entity.name", "Self"), Key("state_attributes.attribute.name", "Attribute")) + "}}");
        var assembly = Load(reserved);
        var names = assembly.GetTypes().Select(t => t.Name).Order(StringComparer.Ordinal).ToArray();
        Check(string.Join(",", names) == "EntityDomain,EntityEntity,EntityEntityDomainAttribute,EntityEntityHandles,EntityEntityJsonConverter,EntityEntityText,EntityEntityTextAttribute,EntityHandles,EntityState",
            "reserved type names: " + string.Join(",", names));
        var properties = Class(assembly, "EntityEntity").GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(p => p.Name).Order(StringComparer.Ordinal);
        Check(string.Join(",", properties) == "Attribute2,Domain,DomainAttribute,EntityEntity2,GetType2,Handles,JsonConverter,RawState2,Snapshot2,State,State2,Text,TextAttribute", "reserved property names: " + string.Join(",", properties));
        var states = Enum.GetNames(Class(assembly, "EntityState"));
        Check(string.Join(",", states) == "EntityState2,Other,Unknown,Unknown2,Unavailable,Other2", "enum members: " + string.Join(",", states));
        Check(EntityText.Of((Enum)Enum.Parse(Class(assembly, "EntityState"), "Unknown")) == "unknown_" && EntityText.Of((Enum)Enum.Parse(Class(assembly, "EntityState"), "Unknown2")) == "unknown", "an added state after the listed ones");
        Console.WriteLine("PASS entity generator: names are told apart in ordinal order and keep clear of the base class and the hand-written types");
        HassSharpNames();
    }

    // Node code imports HassSharp and HassSharp.Entities: no generated type takes the name of one of HassSharp's own, so
    // neither is ambiguous there. Automation's mode attribute is AutomationEntityMode, beside HassSharp's AutomationMode.
    static void HassSharpNames()
    {
        var own = typeof(Entity).Assembly.GetExportedTypes().Where(t => t.Namespace == "HassSharp" && !t.IsNested).Select(t => t.Name.Split('`')[0]).ToHashSet(StringComparer.Ordinal);
        Check(own.Contains("AutomationMode"), "HassSharp's own AutomationMode");
        var assembly = Load(Source(Fixture("ha-entity-components.json")));
        var generated = assembly.GetTypes().Where(t => !t.IsNested).Select(t => t.Name).ToArray();
        Check(!generated.Any(own.Contains), "generated types named like HassSharp's: " + string.Join(", ", generated.Where(own.Contains)));
        Check(Nullable.GetUnderlyingType(Member(assembly, "AutomationEntity", "Mode").PropertyType)!.Name == "AutomationEntityMode", "the automation's mode: " + string.Join(", ", generated.Where(n => n.StartsWith("Automation", StringComparison.Ordinal))));
        var preview = new NodeLibraryCompiler(NodeLibraryFixture.EntityProvider).Preview([new("modes", """
            public static class Modes
            {
                /// <summary>Restarts.</summary>
                [Node("Restarts")] public static bool Restarts(AutomationEntity automation) => automation.Mode == AutomationEntityMode.Restart;
                /// <summary>Queued.</summary>
                [Node("Queued")] public static string Queued() => AutomationMode.Queued.ToString();
            }
            """, 1)]);
        Check(preview.Valid && preview.Diagnostics.Length == 0, "both modes in node code: " + string.Join("; ", preview.Diagnostics.Select(d => d.Message)));
        Console.WriteLine("PASS entity generator: no generated type is named like one of HassSharp's, so node code can name both");
    }

    // Hostile text stays inside a literal (and a doc comment), and reads back as itself.
    static void Hostile()
    {
        const string id = "on\"); System.IO.File.Delete(\"y\"); //";
        var hostile = Source("""{"domains":["x"],"translations":{"component.x.entity_component._.state.on\"); System.IO.File.Delete(\"y\"); //":"l\n*/","component.x.entity_component._.state_attributes.a\"b.name":"</summary>\n<x>","component.x.entity_component._.state_attributes.a\"b.state.c\"d":"*/ \u2028 //""" + "\"}}");
        var assembly = Load(hostile);
        var state = Class(assembly, "XState");
        Check(string.Join(",", Enum.GetNames(state)) == "OnSystemIOFileDeleteY,Unknown,Unavailable,Other", "hostile members: " + string.Join(",", Enum.GetNames(state)));
        Check(EntityText.Of((Enum)Enum.Parse(state, "OnSystemIOFileDeleteY")) == id && state.GetField("OnSystemIOFileDeleteY")!.GetCustomAttribute<EntityTextAttribute>()!.Label == "l\n*/", "the text and label read back");
        PyInterop.Entity = entityId => new() { EntityId = entityId, State = id, Attributes = new() { ["a\"b"] = "c\"d" } };
        var x = assembly.GetType("HassSharp.Entities.EntityHandles")!.GetMethod("Create")!.Invoke(null, ["x.y"])!;
        Check(State(x) == "OnSystemIOFileDeleteY" && Get(x, "AB")!.ToString() == "CD", "hostile state and attribute read");
        Console.WriteLine("PASS entity generator: hostile text stays in literals and doc text, and reads back as itself");
    }

    // A domain's class and enums are one span, its arm in the factory a second; an empty schema gives only the factory.
    static void Spans()
    {
        var generated = Generated(Fixture("ha-entity-components.json"));
        var lines = generated.Source.Split('\n');
        int Line(string text) => Array.FindIndex(lines, l => l.Contains(text)) + 1;
        Check(generated.DomainsAt(Line("public sealed class LightEntity")).SequenceEqual(["light"]), "the class line");
        Check(generated.DomainsAt(Line("public enum LightColorMode")).SequenceEqual(["light"]), "an attribute enum line");
        Check(generated.DomainsAt(Line("new global::HassSharp.Entities.LightEntity(entityId)")).SequenceEqual(["light"]), "the factory arm");
        Check(!generated.DomainsAt(Line("public static class EntityHandles")).Any() && !generated.DomainsAt(1).Any(), "outside every domain");
        Check(generated.Spans.Count == 2 * 58 && generated.Spans.All(s => s.IsDomain), "two spans a domain");
        foreach (var group in generated.Spans.GroupBy(s => s.Id))
            Check(group.Count() == 2 && group.First().EndLine < group.Last().StartLine && group.Last().StartLine == group.Last().EndLine, "spans of " + group.Key);

        var empty = Generated("""{"domains":[],"translations":{}}""");
        var assembly = Load(empty.Source);
        Check(empty.Spans.Count == 0 && assembly.GetTypes().Single().Name == EntityGenerator.Factory, "the empty schema");
        PyInterop.Entity = entityId => new() { EntityId = entityId, State = "on" };
        Check(assembly.GetType("HassSharp.Entities.EntityHandles")!.GetMethod("Create")!.Invoke(null, ["light.a"])!.GetType() == typeof(Entity), "the empty factory");
        Console.WriteLine("PASS entity generator: each domain's code and factory arm are spans of the domain");
    }

    static KeyValuePair<string, JsonObject>[] Domains(string json) => [.. EntitySchemaCatalog.Parse(json).Items.Select(p => KeyValuePair.Create(p.Key, (JsonObject)p.Value!))];
    static ServiceSchemaCatalog Services(string json) => ServiceSchemaCatalog.Parse(json, _ => { });
    const string TurnOn = """{"light":{"turn_on":{"target":{"entity":{"domain":"light"}},"fields":{}}}}""";
    // The fixture with one more domain: other entity classes, so another assembly.
    static string More()
    {
        var more = JsonNode.Parse(Fixture("ha-entity-components.json"))!;
        more["domains"]!.AsArray().Add("made_up");
        return more.ToJsonString();
    }
    static JsonObject Custom(string id, string source) => N(id, "custom", new JsonObject { ["source"] = source, ["arguments"] = new JsonObject() });
    static string Node(string title, string signature, string body) => "public static class CustomNode\n{\n    [Node(\"" + title + "\")]\n    public static " + signature + "\n    {\n        " + body + "\n    }\n}";
    static async Task<JsonNode> Visual(HassSharpManager api, object request) => JsonNode.Parse(await api.VisualRequest(JsonSerializer.Serialize(request)))!;
    static string EntitiesOf(UserScript script) => script.CompiledScript.Assembly.GetReferencedAssemblies().Single(a => a.Name!.StartsWith(GeneratedEntities.AssemblyPrefix, StringComparison.Ordinal)).Name!;
    static List<string> Logged() { var logged = new List<string>(); PyInterop.Log = (_, message) => { lock (logged) logged.Add(message); }; return logged; }
    static int Count(List<string> logged, string message) { lock (logged) return logged.Count(m => m == message); }

    /// <summary>Runs a saved automation's manual trigger and waits for its node to log <paramref name="expected"/>.</summary>
    static async Task RunAndWait(HassSharpManager api, List<string> logged, string id, string expected)
    {
        var count = Count(logged, expected);
        var run = await Visual(api, new { operation = "run", id, triggerId = "t" });
        Check(run["ok"]!.GetValue<bool>(), $"run {id}: " + run.ToJsonString());
        for (var i = 0; i < 300 && Count(logged, expected) == count; i++) await Task.Delay(10);
        Check(Count(logged, expected) == count + 1, $"{id} did not log {expected}: " + string.Join(" | ", logged));
    }

    // Whether a library project's completion offers the type: the language services' workspace has the classes' source.
    static async Task<bool> Completes(DiagnosticsProvider provider, string type)
    {
        var typed = type[..^2];
        var source = "public class Main { void Run() { " + typed + " } }";
        var list = (CompletionListModel)await new NodeLibraryLanguageServices(provider).RequestAsync("completion", [new NodeLibrarySource("main", source, 1)], "main", source.IndexOf(typed, StringComparison.Ordinal) + typed.Length);
        return list.Items.Any(i => i.DisplayText == type);
    }

    // Whether a library naming the type compiles: libraries reference the installed entities assembly.
    static bool Compiles(DiagnosticsProvider provider, string type) =>
        new NodeLibraryCompiler(provider).Preview([new NodeLibrarySource("lib", "public static class Lib { [Node(\"X\")] public static string X() => typeof(" + type + ").Name; }", 1)]).Valid;

    // The fixture's classes installed beside the typed calls: a Custom C# node takes handles, reads them and calls a
    // service with one; the automation loads both assemblies into its own context; a library project completes them.
    static async Task Installed()
    {
        var provider = new DiagnosticsProvider();
        Check(!await Completes(provider, "LightEntity") && !Compiles(provider, "LightEntity") && Compiles(provider, "EntityHandles"), "a provider without an entity schema has only EntityHandles");
        var result = provider.UseEntitySchema(Domains(Fixture("ha-entity-components.json")));
        Check(result.Accepted && result.Changed && result.Grew && result.Excluded.Count == 0 && result.Domains.Count == 58, "the fixture's classes were not installed: " + string.Join("\n", result.Errors));
        provider.UseServiceSchema(Services(TurnOn));
        var calls = provider.ServiceCalls();
        Check(calls.Entities.AssemblyName.StartsWith(GeneratedEntities.AssemblyPrefix, StringComparison.Ordinal) && calls.Entities.AssemblyName.Length == GeneratedEntities.AssemblyPrefix.Length + 16
            && calls.References.SequenceEqual([calls.Reference, calls.Entities.Reference]), "the references every compilation gets: " + string.Join(", ", calls.References.Select(r => r.Display)));

        var made = new List<JsonNode>();
        PyInterop.CallService = (domain, name, json, done, _) => { lock (made) made.Add(new JsonObject { ["service"] = domain + "." + name, ["data"] = JsonNode.Parse(json!) }); done?.Invoke(); };
        PyInterop.Entity = id => new() { EntityId = id, State = "on" };
        var logged = Logged();
        var graph = G(Custom("make", Node("Make", "(BinarySensorEntity Sensor, LightEntity Light) Run()", "return (new(\"binary_sensor.door\"), new(\"light.kitchen\"));")),
            Custom("use", Node("Use", "async Task<bool> Run(BinarySensorEntity s, LightEntity light, NodeContext context)", "await context.Services.Light.TurnOn(light); return context.State(s).State == \"on\" && s.State == BinarySensorState.On;")),
            N("log", "log", new { value = new { kind = "unset" } }));
        foreach (var edge in new[] { D("make", "use", "s", "sensor"), D("make", "use", "light", "light"), D("use", "log", "value", "result") }) graph["edges"]!.AsArray().Add(edge);
        var snapshot = new NodeLibraryCompiler(provider).Preview([]).Snapshot;
        graph = new VisualGenerator(provider).DeriveSignatures(graph, snapshot);
        var preview = new VisualGenerator(provider).Preview(graph, snapshot);
        Check(preview.Valid, "the nodes taking entity handles do not compile: " + JsonSerializer.Serialize(preview.Diagnostics));
        var compiled = await new CodeCompiler(provider).CompileSingleFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cs"), preview.Source);
        try
        {
            var referenced = compiled.Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
            Check(referenced.Contains(calls.AssemblyName) && referenced.Contains(calls.Entities.AssemblyName), "the automation does not reference both assemblies: " + string.Join(", ", referenced));
            var instance = (Automation)Activator.CreateInstance(compiled.Assembly.GetType("VisualAutomation")!)!;
            using var cts = new CancellationTokenSource(); instance._cancellationToken.Value = cts.Token;
            await ((Task)instance.GetType().GetMethod("Run_74")!.Invoke(instance, null)!).WaitAsync(TimeSpan.FromSeconds(10));
            var context = AssemblyLoadContext.GetLoadContext(compiled.Assembly)!;
            Check(context.Assemblies.Any(a => a.GetName().Name == calls.Entities.AssemblyName) && !AssemblyLoadContext.Default.Assemblies.Any(a => a.GetName().Name == calls.Entities.AssemblyName),
                "the entities assembly was not loaded into the automation's own context");
            Check(made.Count == 1 && JsonNode.DeepEquals(made[0], JsonNode.Parse("""{"service":"light.turn_on","data":{"entity_id":"light.kitchen"}}""")), "the call with a handle: " + string.Join(", ", made.Select(m => m.ToJsonString())));
            Check(Count(logged, "True") == 1, "the node did not read the handles' states: " + string.Join(" | ", logged));
        }
        finally { compiled.Unload(); }
        Check(await Completes(provider, "LightEntity") && await Completes(provider, "BinarySensorEntity"), "completion in a library project does not offer the entity classes");
        Console.WriteLine("PASS entity generator: the classes are installed beside the typed calls; node code takes, reads and passes handles; libraries complete them");
    }

    // The two halves of the one generated-code object are swapped apart: services keep the entities and the other way round.
    static void ServicesLeaveEntitiesAlone()
    {
        var provider = new DiagnosticsProvider();
        provider.UseEntitySchema(Domains(Fixture("ha-entity-components.json")));
        provider.UseServiceSchema(Services(TurnOn));
        var carrier = provider.ServiceCalls();
        var before = carrier.Entities.AssemblyName;
        provider.UseServiceSchema(Services("""{"light":{"turn_off":{"fields":{}}}}"""));
        var after = provider.ServiceCalls();
        Check(after.Entities.AssemblyName == before && ReferenceEquals(after.Entities, carrier.Entities) && !ReferenceEquals(after, carrier) && after.AssemblyName != carrier.AssemblyName,
            "a service push replaced the entity classes, or not the typed calls");
        var locks = Domains(Fixture("ha-entity-components.json")).Where(d => d.Key == "lock").ToArray();
        var swapped = provider.UseEntitySchema(locks);
        var current = provider.ServiceCalls();
        Check(swapped.Accepted && swapped.Changed && !swapped.Grew && current.Entities.AssemblyName != before && ReferenceEquals(current.Reference, after.Reference) && ReferenceEquals(current.Tree, after.Tree),
            "an entity push that only drops domains replaced the typed calls, or grew");
        var again = provider.UseEntitySchema(locks);
        Check(again.Accepted && !again.Changed && !again.Grew && ReferenceEquals(current, provider.ServiceCalls()), "the same entity schema again replaced the classes");
        Console.WriteLine("PASS entity generator: a service push keeps the entity classes and an entity push the typed calls");
    }

    // One domain whose code does not compile is left out and the rest are kept; code broken outside every domain keeps the previous classes.
    static void BrokenDomain()
    {
        var provider = new DiagnosticsProvider();
        provider.EntitiesGenerator = domains =>
        {
            var generated = EntityGenerator.Generate(domains);
            return generated with { Source = generated.Source.Replace("class LightEntity : global::HassSharp.Entities.Entity", "class LightEntity : global::HassSharp.Entities.NoSuchEntity") };
        };
        var result = provider.UseEntitySchema(Domains(Fixture("ha-entity-components.json")));
        Check(result.Accepted && result.Excluded.SequenceEqual(["light"]) && result.Domains.Count == 57 && result.Domains.All(d => d.Key != "light") && result.Errors.Count > 0,
            $"a broken light class: accepted {result.Accepted}, excluded {string.Join(",", result.Excluded)}");
        Check(Compiles(provider, "LockEntity") && !Compiles(provider, "LightEntity"), "the other classes are not kept, or the broken one is");

        var before = provider.ServiceCalls();
        provider.EntitiesGenerator = domains =>
        {
            var generated = EntityGenerator.Generate(domains);
            return generated with { Source = generated.Source.Replace("#nullable enable", "#nullable enable\nclass Broken { int x = \"no\"; }") };
        };
        var rejected = provider.UseEntitySchema(Domains(Fixture("ha-entity-components.json")));
        Check(!rejected.Accepted && !rejected.Changed && rejected.Domains.Count == 0 && ReferenceEquals(before, provider.ServiceCalls()) && Compiles(provider, "LockEntity"), "code broken outside every domain replaced the classes");
        Console.WriteLine("PASS entity generator: a domain whose class does not compile is left out alone; a broken tree keeps the previous classes");
    }

    // An automation compiled against one set of classes keeps reading them after another set is installed; a new save uses the new one.
    static async Task SwappedWhileRunning()
    {
        var root = Path.Combine(Path.GetTempPath(), "entity-generator-swapped-" + Guid.NewGuid());
        var logged = Logged();
        PyInterop.Entity = id => new() { EntityId = id, State = "jammed" };
        var graph = G(Custom("c", Node("Read", "void Run()", "Logger.Info(\"lock \" + new LockEntity(\"lock.front\").State);")));
        var api = new HassSharpManager(root);
        try
        {
            await api.Init();
            api.SetEntityDescriptions(Fixture("ha-entity-components.json"), started: true);
            var before = ServiceCallTests.ProviderOf(api).ServiceCalls().Entities.AssemblyName;
            var saved = await Visual(api, new { operation = "save", id = "old", graph, expectedRevision = 0, deriveSignatures = true });
            Check(saved["result"]?["active"]?.GetValue<bool>() == true, "old: " + saved.ToJsonString());
            await RunAndWait(api, logged, "old", "lock Jammed");
            var loaded = VisualTests.Loaded(api, "old")!;
            api.SetEntityDescriptions(More(), started: true);
            var after = ServiceCallTests.ProviderOf(api).ServiceCalls().Entities.AssemblyName;
            Check(after != before, "the push did not replace the entities assembly");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            await RunAndWait(api, logged, "old", "lock Jammed");
            Check(ReferenceEquals(loaded, VisualTests.Loaded(api, "old")) && EntitiesOf(loaded) == before, "the running automation was replaced or rebound");
            var fresh = await Visual(api, new { operation = "save", id = "new", graph, expectedRevision = 0, deriveSignatures = true });
            Check(fresh["result"]?["active"]?.GetValue<bool>() == true && EntitiesOf(VisualTests.Loaded(api, "new")!) == after, "a newly saved automation does not use the new assembly: " + fresh.ToJsonString());
            await RunAndWait(api, logged, "new", "lock Jammed");
        }
        finally { await api.UnloadAsync(); await ServiceCallTests.DeleteWhenQuiet(root); }
        Console.WriteLine("PASS entity generator: an entity push leaves a loaded automation on the classes it was compiled against, still reading; a new save uses the new ones");
    }

    // An automation whose code names LightEntity.
    static JsonObject Lights() => G(Custom("c", Node("Read", "void Run()", "Logger.Info(\"light \" + new LightEntity(\"light.kitchen\").State);")));
    static string SchemaFile(string root) => Path.Combine(root, ".storage", "hass_sharp", "entity_schema.json");

    // Review Focus 4: with no saved schema, an automation naming LightEntity does not load at startup; the first push
    // brings the classes and loads it, without a restart.
    static async Task FirstStart(string root)
    {
        var logged = Logged();
        PyInterop.Entity = id => new() { EntityId = id, State = "on" };
        var first = new HassSharpManager(root);
        try
        {
            await first.Init();
            Check(logged.Count(l => l.Contains("No saved entity schema yet")) == 1, "a missing entity schema file is not logged once: " + string.Join(" | ", logged));
            first.SetEntityDescriptions(Fixture("ha-entity-components.json"), started: true);
            var saved = await Visual(first, new { operation = "save", id = "lit", graph = Lights(), expectedRevision = 0, deriveSignatures = true });
            Check(saved["result"]?["active"]?.GetValue<bool>() == true, "lit: " + saved.ToJsonString());
        }
        finally { await first.UnloadAsync(); }

        File.Delete(SchemaFile(root));
        var api = new HassSharpManager(root);
        try
        {
            await api.Init();
            var error = (await Visual(api, new { operation = "read", id = "lit" }))["result"]!["activationError"]?.GetValue<string>();
            Check(VisualTests.Loaded(api, "lit") == null && error?.Contains("'LightEntity' could not be found") == true, "with no saved schema, the automation naming LightEntity loaded, or failed otherwise: " + error);
            api.SetEntityDescriptions(Fixture("ha-entity-components.json"), started: true);
            Check(api.RetriesQueued == 1, "the first push did not retry: " + api.RetriesQueued);
            await api.RetriesDone();
            Check(VisualTests.Loaded(api, "lit") != null && (await Visual(api, new { operation = "read", id = "lit" }))["result"]!["activationError"] == null, "the first push did not load the automation");
            await RunAndWait(api, logged, "lit", "light On");
        }
        finally { await api.UnloadAsync(); }
        Console.WriteLine("PASS entity generator: with no saved schema an automation naming an entity class fails at startup and loads on the first push, without a restart");
    }

    // The accepted schema is saved: the next start compiles the automation before any push.
    static async Task Persisted(string root)
    {
        var persisted = JsonNode.Parse(File.ReadAllText(SchemaFile(root)))!;
        Check(persisted["version"]!.GetValue<int>() == 1 && persisted["items"]!["light"]!["states"] is JsonArray && persisted["items"]!.AsObject().Count == 58, "the entity schema was not saved: " + persisted.ToJsonString()[..Math.Min(200, persisted.ToJsonString().Length)]);
        var logged = Logged();
        var api = new HassSharpManager(root);
        try
        {
            await api.Init();
            Check(VisualTests.Loaded(api, "lit") != null && api.RetriesQueued == 0, "Init with the saved schema did not load the automation naming LightEntity: " + string.Join(" | ", logged));
            Check(!logged.Any(l => l.Contains("entity schema")), "a readable entity schema file was reported: " + string.Join(" | ", logged));
        }
        finally { await api.UnloadAsync(); }
        Console.WriteLine("PASS entity generator: the saved schema compiles node code at startup before any push");
    }

    // A push that is not Home Assistant's shape is logged and ignored; the classes stay.
    static async Task MalformedPush(string root)
    {
        var logged = Logged();
        var api = new HassSharpManager(root);
        try
        {
            await api.Init();
            var before = ServiceCallTests.ProviderOf(api).ServiceCalls();
            api.SetEntityDescriptions("[]");
            api.SetEntityDescriptions("not json", started: true);
            Check(ReferenceEquals(before, ServiceCallTests.ProviderOf(api).ServiceCalls()) && Compiles(ServiceCallTests.ProviderOf(api), "LightEntity") && VisualTests.Loaded(api, "lit") != null,
                "a malformed push replaced the classes");
            Check(logged.Count(l => l.Contains("Ignoring Home Assistant's entity components")) == 2, "a malformed push is not logged: " + string.Join(" | ", logged));
            Check(JsonNode.Parse(File.ReadAllText(SchemaFile(root)))!["items"]!.AsObject().Count == 58, "a malformed push changed the saved schema");
        }
        finally { await api.UnloadAsync(); }
        Console.WriteLine("PASS entity generator: a malformed push is logged and keeps the previous classes");
    }
}
