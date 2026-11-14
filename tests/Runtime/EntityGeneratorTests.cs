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
    public static void Run()
    {
        var entity = PyInterop.Entity;
        try
        {
            FixtureClasses();
            Reading();
            Names();
            Hostile();
            Spans();
        }
        finally
        {
            PyInterop.Entity = entity;
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

        // Type names keep clear of the hand-written types; members of the base class, State and the class's own name;
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
        Check(string.Join(",", names) == "EntityDomain,EntityDomainAttribute2,EntityEntity,EntityHandles,EntityHandles2,EntityJsonConverter2,EntityState,EntityText2,EntityTextAttribute2",
            "reserved type names: " + string.Join(",", names));
        var properties = Class(assembly, "EntityEntity").GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(p => p.Name).Order(StringComparer.Ordinal);
        Check(string.Join(",", properties) == "Attribute2,Domain,DomainAttribute,EntityEntity2,GetType2,Handles,JsonConverter,RawState2,Snapshot2,State,State2,Text,TextAttribute", "reserved property names: " + string.Join(",", properties));
        var states = Enum.GetNames(Class(assembly, "EntityState"));
        Check(string.Join(",", states) == "EntityState2,Other,Unknown,Unknown2,Unavailable,Other2", "enum members: " + string.Join(",", states));
        Check(EntityText.Of((Enum)Enum.Parse(Class(assembly, "EntityState"), "Unknown")) == "unknown_" && EntityText.Of((Enum)Enum.Parse(Class(assembly, "EntityState"), "Unknown2")) == "unknown", "an added state after the listed ones");
        Console.WriteLine("PASS entity generator: names are told apart in ordinal order and keep clear of the base class and the hand-written types");
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
}
