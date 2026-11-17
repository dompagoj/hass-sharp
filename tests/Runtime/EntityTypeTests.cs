using HassSharp;

/// <summary>
/// The custom device and entity classes a node library declares, as the library snapshot lists them: which classes are
/// listed and with what id, kind and domain, their settings with their form types, the problems a setting can have,
/// and that the catalog automations embed never carries them.
/// </summary>
static class EntityTypeTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static string Messages(IEnumerable<NodeLibraryDiagnostic> diagnostics) => string.Join(" | ", diagnostics.Select(d => $"{d.SourceId} {d.StartLine}:{d.StartColumn} {d.Message}"));

    const string Shelly = """
        public enum Mode { Eco, Boost }
        /// <summary>A Shelly smart plug.</summary>
        public class ShellyPlug : SharpDevice
        {
            [Setting("Host")] public string Host { get; set; } = "";
            [Setting(Secret = true)] public string? Token { get; set; }
            [Setting] public int Channel { get; set; }
            [Setting] public Mode Mode { get; set; }
            public string NotASetting { get; set; } = "";
        }
        public class Relay : SharpSwitch { }
        public abstract class Base : SharpDevice { }
        public class NoCtor : SharpDevice { public NoCtor(string host) { } }
        """;

    const string Broken = """
        public class Odd : SharpDevice
        {
            [Setting] public DateTime When { get; set; }
            [Setting] public string X { get; } = "";
            [Setting] public bool On { get; set; }
        }
        """;

    [RuntimeSuite("--entity-types", Order = 12)]
    public static Task Run()
    {
        var compiler = new NodeLibraryCompiler(new DiagnosticsProvider());
        Listed(compiler);
        Problems(compiler);
        Check(VisualCatalogSnapshot.BuiltIn.EntityTypes.IsEmpty, "the built-in snapshot has no entity types");
        Console.WriteLine("PASS the built-in snapshot has no entity types");
        return Task.CompletedTask;
    }

    static void Listed(NodeLibraryCompiler compiler)
    {
        var preview = compiler.Preview([new NodeLibrarySource("shelly", Shelly, 1)]);
        Check(preview.Valid, "the library compiles: " + Messages(preview.Diagnostics));
        var types = preview.Snapshot.EntityTypes;
        Check(types.Keys.Order(StringComparer.Ordinal).SequenceEqual(["library:shelly:NodeLib.Relay", "library:shelly:NodeLib.ShellyPlug"]),
            "only the public, concrete classes with a parameterless constructor are listed: " + string.Join(", ", types.Keys));
        Console.WriteLine("PASS entity types are listed by library id, without abstract classes or classes without a parameterless constructor");

        var plug = types["library:shelly:NodeLib.ShellyPlug"];
        Check(plug is { Kind: "device", Domain: null, SourceId: "shelly", Title: "ShellyPlug", Description: "A Shelly smart plug.", TypeExpression: "global::NodeLib.ShellyPlug" },
            "the device's kind, domain, source, title, description and type: " + plug);
        Console.WriteLine("PASS a device is described by its class and its summary");

        Check(plug.Settings.Select(s => s.Key).SequenceEqual(["Host", "Token", "Channel", "Mode"]), "the device's settings: " + string.Join(", ", plug.Settings.Select(s => s.Key)));
        Check(plug.Settings.Select(s => s.Type).SequenceEqual(["string", "string", "integer", "enum"]), "the settings' types: " + string.Join(", ", plug.Settings.Select(s => s.Type)));
        Console.WriteLine("PASS the settings are the [Setting] properties, with their form types");

        Check(plug.Settings[0].Label == "Host" && plug.Settings[2].Label == "Channel", "a setting's label is the attribute's, else the property name");
        Check(plug.Settings[1].Secret && !plug.Settings[0].Secret, "Token is secret and Host is not");
        Check(plug.Settings[3].Options.SequenceEqual(["Eco", "Boost"]) && plug.Settings[0].Options.IsEmpty, "an enum setting's options are its member names: " + string.Join(", ", plug.Settings[3].Options));
        Console.WriteLine("PASS a setting carries its label, whether it is secret, and an enum's options");

        var relay = types["library:shelly:NodeLib.Relay"];
        Check(relay is { Kind: "entity", Domain: "switch", Description: null } && relay.Settings.IsEmpty, "a switch is an entity of the switch domain: " + relay);
        Console.WriteLine("PASS an entity has the domain of its typed base");

        var catalog = preview.Snapshot.Catalog.ToJsonString();
        Check(!catalog.Contains("ShellyPlug") && !catalog.Contains("library:shelly:NodeLib.Relay"), "the catalog does not carry the entity types");
        Console.WriteLine("PASS the catalog automations embed does not carry the entity types");
        Check(preview.Snapshot.WithServices(null).EntityTypes.Count == 2 && preview.Snapshot.WithRoot(preview.Snapshot.Catalog).EntityTypes.Count == 2, "a derived snapshot keeps the entity types");
        Console.WriteLine("PASS a snapshot derived from another keeps its entity types");
    }

    static void Problems(NodeLibraryCompiler compiler)
    {
        var preview = compiler.Preview([new NodeLibrarySource("broken", Broken, 1)]);
        var errors = preview.Diagnostics.Where(d => d.Severity == 3).ToArray();
        Check(!preview.Valid, "a library with an invalid setting is not valid");
        var type = errors.SingleOrDefault(d => d.Message == "A setting is a string, bool, int, long, double or enum; DateTime cannot be one");
        Check(type is { SourceId: "broken", StartLine: 3, StartColumn: 31 }, "a setting of another type is reported at the property: " + Messages(errors));
        Console.WriteLine("PASS a setting of another type is reported at the property");
        var setter = errors.SingleOrDefault(d => d.Message == "A setting needs a public setter");
        Check(setter is { SourceId: "broken", StartLine: 4, StartColumn: 29 }, "a setting without a public setter is reported at the property: " + Messages(errors));
        Console.WriteLine("PASS a setting without a public setter is reported at the property");
        Check(errors.Length == 2, "no other errors: " + Messages(errors));
        var odd = preview.Snapshot.EntityTypes["library:broken:NodeLib.Odd"];
        Check(odd.Settings.Select(s => s.Key).SequenceEqual(["On"]) && odd.Settings[0].Type == "boolean", "a class with an invalid setting is listed without it");
        Console.WriteLine("PASS a class with an invalid setting is listed without that setting");
    }
}
