using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HassSharp;

/// <summary>The normalised Home Assistant service schema (contracts C3, C4) and its manager boundary.</summary>
public static class ServiceSchemaTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name));
    static readonly string[] AnyType = ["object", "array", "string", "number", "boolean"];

    /// <summary>The golden <c>tests/fixtures/visual-services.json</c>, written by <c>--write-services-fixture</c>.</summary>
    public static string Golden(string descriptions) => ServiceSchemaCatalog.Parse(descriptions, _ => { }).Section
        .ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";

    [RuntimeSuite("--service-schema", Order = 17)]
    public static async Task Run()
    {
        var logged = new List<string>();
        var descriptions = Fixture("ha-service-descriptions.json");
        var catalog = ServiceSchemaCatalog.Parse(descriptions, logged.Add);
        var golden = JsonNode.Parse(Fixture("visual-services.json"))!;
        Check(JsonNode.DeepEquals(catalog.Section, golden), "Services section must match tests/fixtures/visual-services.json (regenerate with --write-services-fixture)");
        Check(golden["servicesGeneration"]!.GetValue<string>() == catalog.Generation, "golden generation");
        Console.WriteLine($"PASS service schema golden: {catalog.Count} services");

        RecordedServices(catalog);
        Synthetic(catalog, logged);
        SelectorMapping();
        Generation(descriptions, catalog);
        await Manager(descriptions, catalog);
    }

    static JsonObject Item(ServiceSchemaCatalog catalog, string id) => catalog.Items[id] as JsonObject ?? throw new Exception($"service {id} missing");
    static JsonObject Field(ServiceSchemaCatalog catalog, string id, string field) => Item(catalog, id)["fields"]![field] as JsonObject ?? throw new Exception($"field {id}.{field} missing");
    static string Text(JsonNode? node) => node?.ToJsonString() ?? "null";
    static void Equal(JsonNode? actual, string expected, string message) =>
        Check(JsonNode.DeepEquals(actual, JsonNode.Parse(expected)), $"{message}: expected {expected}, got {Text(actual)}");

    static void RecordedServices(ServiceSchemaCatalog catalog)
    {
        var hvac = Item(catalog, "climate.set_hvac_mode");
        Equal(hvac["target"], """{"entity":[{"domain":["climate"],"deviceClass":[],"supportedFeatures":[]}],"device":[]}""", "climate.set_hvac_mode targets climate entities only");
        var mode = Field(catalog, "climate.set_hvac_mode", "hvac_mode");
        Equal(mode["types"], """["string"]""", "hvac_mode is a string");
        Check(mode["selector"]?["state"] is JsonObject, "hvac_mode keeps its state selector as the widget hint");
        Check(Item(catalog, "weather.get_forecasts")["response"]!.GetValue<string>() == "only", "weather.get_forecasts only returns a response");
        Equal(Item(catalog, "weather.get_forecasts")["target"]!["entity"], """[{"domain":["weather"],"deviceClass":[],"supportedFeatures":[1,2,4]}]""", "target supported features");
        Equal(Field(catalog, "weather.get_forecasts", "type")["enum"], """["daily","hourly","twice_daily"]""", "select options become enum");
        Check(Item(catalog, "light.turn_on")["response"]!.GetValue<string>() == "none", "services without a response say none");
        Check(Item(catalog, "notify.notify")["target"] == null, "services without a target have a null target");
        Equal(Field(catalog, "notify.notify", "message")["required"], "true", "required field");
        Check(Field(catalog, "notify.notify", "target") is { } notifyTarget && notifyTarget["types"]!.AsArray().Count == AnyType.Length, "notify keeps its field named target");
        Equal(Item(catalog, "homeassistant.turn_on")["target"], """{"entity":[],"device":[]}""", "an unfiltered target");
        Equal(Item(catalog, "script.turn_on")["fields"], "{}", "a service without fields");
        var brightness = Field(catalog, "light.turn_on", "brightness_pct");
        Check(brightness["min"]!.GetValue<double>() == 0 && brightness["max"]!.GetValue<double>() == 100 && brightness["step"]!.GetValue<double>() == 1
            && brightness["unit"]!.GetValue<string>() == "%" && brightness["integer"]!.GetValue<bool>(), "number constraints of brightness_pct: " + Text(brightness));
        Equal(brightness["filter"], """{"attribute":{"supported_color_modes":["brightness","color_temp","hs","xy","rgb","rgbw","rgbww"]}}""", "attribute field filter");
        Equal(Field(catalog, "light.turn_on", "transition")["filter"], """{"supportedFeatures":[32]}""", "supported-features field filter");
        var rgbw = Field(catalog, "light.turn_on", "rgbw_color");
        Check(rgbw["section"]?.GetValue<string>() == "additional_fields", "sectioned fields are flattened with their section");
        Check(Field(catalog, "light.turn_on", "transition")["section"] == null, "top-level fields have no section");
        Check(!Item(catalog, "light.turn_on")["fields"]!.AsObject().ContainsKey("additional_fields"), "a section is not a field");
        Equal(Field(catalog, "light.turn_on", "rgb_color")["types"], """["array"]""", "color_rgb");
        Console.WriteLine("PASS service schema recorded HA services: target, response, sections, filters");
    }

    static void Synthetic(ServiceSchemaCatalog catalog, List<string> logged)
    {
        var level = Field(catalog, "hass_sharp_test.sectioned", "level");
        Check(level["section"] == null && level["required"]!.GetValue<bool>() && level["default"]!.ToJsonString() == "3" && level["integer"]!.GetValue<bool>(),
            "a top-level field wins over a same-named section field: " + Text(level));
        var speed = Field(catalog, "hass_sharp_test.sectioned", "speed");
        Check(speed["section"]!.GetValue<string>() == "advanced_options" && speed["advanced"]!.GetValue<bool>() && speed["example"]!.GetValue<string>() == "fast", "section, advanced, example");
        Equal(speed["enum"], """["slow","fast"]""", "labelled select values");
        Equal(speed["enumLabels"], """["Slow","fast"]""", "labelled select labels");
        Check(Field(catalog, "hass_sharp_test.sectioned", "note")["section"]!.GetValue<string>() == "extras", "second section");
        Check(Item(catalog, "hass_sharp_test.sectioned")["name"]!.GetValue<string>() == "Sectioned", "service name");
        var filtered = Item(catalog, "hass_sharp_test.filtered");
        Equal(filtered["target"], """{"entity":[{"domain":["fan"],"deviceClass":["ceiling"],"supportedFeatures":[1]}],"device":[{"integration":"zha","manufacturer":null,"model":"Ceiling fan","modelId":null}]}""", "dict-form target");
        Equal(Field(catalog, "hass_sharp_test.filtered", "percentage")["filter"], """{"supportedFeatures":[16],"attribute":{"preset_modes":["auto","sleep"]}}""", "both field filters");
        Check(!Field(catalog, "hass_sharp_test.filtered", "percentage")["integer"]!.GetValue<bool>(), "an omitted step does not demand integers");
        Check(Item(catalog, "hass_sharp_test.respond_optionally")["response"]!.GetValue<string>() == "optional", "optional response");
        Check(Item(catalog, "hass_sharp_test.partly_malformed")["fields"]!.AsObject().Select(p => p.Key).SequenceEqual(["kept"]), "malformed fields are skipped, the rest survive");
        foreach (var skipped in new[] { "hass_sharp_test.not_an_object", "hass_sharp_test.Bad-Service", "hass_sharp_test.bad_target", "hass_sharp_test.bad_response", "hass_sharp_test.bad_fields", "broken_domain.turn_on", "Bad Domain.turn_on" })
            Check(!catalog.Items.ContainsKey(skipped), $"malformed service {skipped} is skipped");
        foreach (var name in new[] { "broken_domain", "Bad Domain", "not_an_object", "Bad-Service", "bad_target", "bad_response", "bad_fields", "two_selectors", "scalar_field", "bad_minimum", "unknown_options", "field level of hass_sharp_test.sectioned" })
            Check(logged.Count(l => l.Contains(name, StringComparison.Ordinal)) == 1, $"exactly one log line for {name}: {string.Join(" | ", logged)}");
        Check(logged.Count == 12, "one log line per skipped entry: " + string.Join(" | ", logged));
        foreach (var bad in new[] { "[]", "\"text\"", "not json", "null" })
        {
            var threw = false;
            try { ServiceSchemaCatalog.Parse(bad, _ => { }); } catch (Exception ex) when (ex is ArgumentException or JsonException) { threw = true; }
            Check(threw, $"top-level input {bad} is rejected so the caller keeps its previous schemas");
        }
        Console.WriteLine("PASS service schema synthetic sections, filters, dict target and malformed input");
    }

    // One case per contract C3 row.
    static void SelectorMapping()
    {
        var fields = new JsonObject();
        void Add(string name, string selector) => fields[name] = JsonNode.Parse(selector == "" ? "{}" : "{\"selector\":" + selector + "}");
        Add("boolean", """{"boolean":{}}""");
        Add("number_float", """{"number":{"min":1,"max":5,"step":0.5,"unit_of_measurement":"W"}}""");
        Add("number_whole", """{"number":{"step":2,"mode":"box"}}""");
        Add("number_float_mode", """{"number":{"step":1,"mode":"float"}}""");
        Add("number_any", """{"number":{"step":"any"}}""");
        Add("text", """{"text":{"multiline":true}}""");
        Add("text_multiple", """{"text":{"multiple":true}}""");
        Add("select", """{"select":{"options":["a","b"]}}""");
        Add("select_multiple", """{"select":{"options":["a"],"multiple":true}}""");
        Add("select_custom", """{"select":{"options":["a"],"custom_value":true}}""");
        Add("entity_one", """{"entity":{"domain":"light"}}""");
        Add("entity_two", """{"entity":{"domain":["light","switch"]}}""");
        Add("entity_filter", """{"entity":{"filter":[{"domain":["sensor"],"device_class":"motion"}]}}""");
        Add("entity_open_filter", """{"entity":{"filter":[{"domain":"sensor"},{"device_class":"motion"}]}}""");
        Add("entity_any", """{"entity":{}}""");
        Add("entity_multiple", """{"entity":{"domain":"light","multiple":true}}""");
        Add("state", """{"state":{}}""");
        Add("state_multiple", """{"state":{"multiple":true}}""");
        foreach (var registry in new[] { "device", "area", "floor", "label" })
        {
            Add(registry, "{\"" + registry + "\":{}}");
            Add(registry + "_multiple", "{\"" + registry + "\":{\"multiple\":true}}");
        }
        Add("target", """{"target":{}}""");
        Add("duration", """{"duration":{}}""");
        Add("time", """{"time":{}}""");
        Add("date", """{"date":{}}""");
        Add("datetime", """{"datetime":{}}""");
        Add("color_rgb", """{"color_rgb":{}}""");
        Add("color_temp", """{"color_temp":{"unit":"kelvin","min":2000,"max":6500}}""");
        Add("constant_bool", """{"constant":{"value":true,"label":"Yes"}}""");
        Add("constant_number", """{"constant":{"value":5}}""");
        Add("constant_text", """{"constant":{"value":"on"}}""");
        foreach (var other in new[] { "object", "template", "media", "action", "icon" }) Add(other, "{\"" + other + "\":{}}");
        Add("null_options", """{"boolean":null}""");
        Add("no_selector", "");
        var catalog = ServiceSchemaCatalog.Parse(new JsonObject { ["test"] = new JsonObject { ["selectors"] = new JsonObject { ["fields"] = fields } } }.ToJsonString(), message => throw new Exception("unexpected skip: " + message));
        JsonObject F(string name) => Field(catalog, "test.selectors", name);
        void Types(string name, params string[] types) => Equal(F(name)["types"], JsonSerializer.Serialize(types), $"types of {name}");
        Types("boolean", "boolean");
        Types("number_float", "number");
        Check(F("number_float")["min"]!.GetValue<double>() == 1 && F("number_float")["max"]!.GetValue<double>() == 5 && F("number_float")["step"]!.GetValue<double>() == 0.5
            && F("number_float")["unit"]!.GetValue<string>() == "W" && !F("number_float")["integer"]!.GetValue<bool>(), "fractional step: " + Text(F("number_float")));
        Check(F("number_whole")["integer"]!.GetValue<bool>() && F("number_whole")["step"]!.GetValue<double>() == 2, "whole step demands integers");
        Check(!F("number_float_mode")["integer"]!.GetValue<bool>(), "float mode does not demand integers");
        Check(F("number_any")["step"] == null && !F("number_any")["integer"]!.GetValue<bool>(), "step any");
        Types("text", "string"); Check(F("text")["selector"]!["text"]!["multiline"]!.GetValue<bool>(), "multiline hint");
        Types("text_multiple", "array");
        Types("select", "string"); Equal(F("select")["enum"], """["a","b"]""", "select enum"); Check(F("select")["enumLabels"] == null, "plain options have no labels");
        Types("select_multiple", "array");
        Check(F("select_custom")["enum"] == null, "custom values lift the enum");
        Types("entity_one", "entity_id:light");
        Types("entity_two", "entity_id");
        Types("entity_filter", "entity_id:sensor");
        Types("entity_open_filter", "entity_id");
        Types("entity_any", "entity_id");
        Types("entity_multiple", "entity_id:light", "array");
        Types("state", "string"); Types("state_multiple", "array");
        foreach (var registry in new[] { "device", "area", "floor", "label" }) { Types(registry, "string"); Types(registry + "_multiple", "string", "array"); }
        Types("target", "target");
        Types("duration", "number", "object");
        foreach (var (name, good, bad) in new[] { ("time", new[] { "07:30", "7:30", "07:30:15" }, new[] { "25:00", "07:3" }), ("date", ["2026-11-13"], ["2026-13-01", "13.11.2026"]), ("datetime", ["2026-11-13 07:30:00", "2026-11-13T07:30"], ["2026-11-13", "07:30"]) })
        {
            Types(name, "string");
            var pattern = F(name)["pattern"]!.GetValue<string>();
            Check(good.All(v => Regex.IsMatch(v, pattern)) && !bad.Any(v => Regex.IsMatch(v, pattern)), $"{name} pattern {pattern}");
        }
        Types("color_rgb", "array"); Check(F("color_rgb")["minItems"]!.ToJsonString() == "3" && F("color_rgb")["maxItems"]!.ToJsonString() == "3", "rgb triple");
        Types("color_temp", "number"); Check(F("color_temp")["selector"]!["color_temp"]!["max"]!.ToJsonString() == "6500", "color_temp slider hint");
        Types("constant_bool", "boolean"); Equal(F("constant_bool")["enum"], "[true]", "constant enum"); Equal(F("constant_bool")["enumLabels"], """["Yes"]""", "constant label");
        Types("constant_number", "number"); Equal(F("constant_number")["enum"], "[5]", "numeric constant");
        Types("constant_text", "string");
        foreach (var other in new[] { "object", "template", "media", "action", "icon", "no_selector" }) Types(other, AnyType);
        Check(F("no_selector")["selector"] == null, "a field without selector has none");
        Types("null_options", "boolean"); Equal(F("null_options")["selector"], """{"boolean":{}}""", "null selector options");
        Console.WriteLine($"PASS service schema selector mapping: {fields.Count} C3 cases");
    }

    static JsonNode? Reversed(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.Reverse().Select(p => KeyValuePair.Create(p.Key, Reversed(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Reversed).ToArray()),
        _ => node?.DeepClone(),
    };

    static void Generation(string descriptions, ServiceSchemaCatalog catalog)
    {
        var reversed = ServiceSchemaCatalog.Parse(Reversed(JsonNode.Parse(descriptions))!.ToJsonString(), _ => { });
        Check(reversed.Generation == catalog.Generation && reversed.CanonicalJson == catalog.CanonicalJson, "generation is stable across input key order");
        var respelled = ServiceSchemaCatalog.Parse(descriptions.Replace("\"step\": 1.0", "\"step\": 1"), _ => { });
        Check(respelled.Generation == catalog.Generation, "generation is stable across number spelling");
        var changed = JsonNode.Parse(descriptions)!;
        changed["light"]!["turn_on"]!["fields"]!["transition"]!["selector"]!["number"]!["max"] = 301;
        Check(ServiceSchemaCatalog.Parse(changed.ToJsonString(), _ => { }).Generation != catalog.Generation, "generation changes when a field changes");
        changed = JsonNode.Parse(descriptions)!;
        changed["light"]!["turn_on"]!["fields"]!["transition"]!["required"] = true;
        Check(ServiceSchemaCatalog.Parse(changed.ToJsonString(), _ => { }).Generation != catalog.Generation, "generation changes when a field becomes required");
        Check(System.Text.RegularExpressions.Regex.IsMatch(catalog.Generation, "^[0-9a-f]{64}$"), "generation is SHA-256 hex");
        Check(ServiceSchemaCatalog.Empty.Generation == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("{}"u8)).ToLowerInvariant(), "empty generation hashes the empty canonical object");
        var section = catalog.Section; section["items"]!.AsObject().Clear();
        Check(catalog.Count > 0 && catalog.Section["items"]!.AsObject().Count == catalog.Count, "the section is a defensive copy");
        Console.WriteLine("PASS service schema generation: key order, number spelling, field changes");
    }

    static async Task<JsonObject> Library(HassSharpManager manager, object request)
    {
        var response = JsonNode.Parse(await manager.NodeLibraryRequest(JsonSerializer.Serialize(request)))!.AsObject();
        Check(response["ok"]!.GetValue<bool>(), response.ToJsonString());
        return response["result"]!.AsObject();
    }

    static async Task Manager(string descriptions, ServiceSchemaCatalog expected)
    {
        var previous = HassPath.HassConfiguration;
        var path = Path.Combine(Path.GetTempPath(), "service-schema-" + Guid.NewGuid());
        var log = PyInterop.Log; PyInterop.Log = (_, _) => { };
        var manager = new HassSharpManager(path);
        try
        {
            var empty = await Library(manager, new { operation = "services" });
            Check(empty["servicesGeneration"]!.GetValue<string>() == ServiceSchemaCatalog.Empty.Generation && empty["items"]!.AsObject().Count == 0, "services before any push are empty");
            manager.SetServiceDescriptions(descriptions); // before Init
            await manager.Init([]);
            var services = await Library(manager, new { operation = "services" });
            Check(JsonNode.DeepEquals(services, expected.Section), "services op returns the C4 section pushed before Init");
            var catalog = await Library(manager, new { operation = "catalog" });
            Check(catalog["servicesGeneration"]!.GetValue<string>() == expected.Generation, "catalog op carries servicesGeneration");
            Check(!catalog.ContainsKey("items") && !catalog.ContainsKey("services") && !catalog["catalog"]!.AsObject().ContainsKey("services")
                && !catalog.ToJsonString().Contains("climate.set_hvac_mode"), "catalog op carries no services body");
            manager.SetServiceDescriptions("{}");
            var without = await Library(manager, new { operation = "catalog" });
            Check(without["servicesGeneration"]!.GetValue<string>() == ServiceSchemaCatalog.Empty.Generation, "servicesGeneration follows the pushed schemas");
            Check(without["generation"]!.GetValue<string>() == catalog["generation"]!.GetValue<string>() && JsonNode.DeepEquals(without["catalog"], catalog["catalog"]),
                "services enter neither the catalog root nor the library generation");

            // Never takes the authoring gate: completes while another request holds it.
            var gate = (SemaphoreSlim)typeof(HassSharpManager).GetField("_visualGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
            await gate.WaitAsync();
            try { await Task.Run(() => manager.SetServiceDescriptions("""{"light":{"turn_off":{"fields":{}}}}""")).WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { gate.Release(); }
            var swapped = await Library(manager, new { operation = "services" });
            Check(swapped["items"]!.AsObject().Select(p => p.Key).SequenceEqual(["light.turn_off"]), "a later push replaces the schemas whole");

            foreach (var malformed in new[] { "not json", "[]", "null", "" })
            {
                manager.SetServiceDescriptions(malformed);
                Check(JsonNode.DeepEquals(await Library(manager, new { operation = "services" }), swapped), $"malformed push {malformed} keeps the previous schemas");
            }
            PyInterop.Log = (_, _) => throw new Exception("logging failed");
            manager.SetServiceDescriptions("not json");
            manager.SetServiceDescriptions(descriptions);
            PyInterop.Log = (_, _) => { };
            Check(JsonNode.DeepEquals(await Library(manager, new { operation = "services" }), expected.Section), "a failing logger never reaches the caller");

            var pushes = Enumerable.Range(0, 16).Select(i => Task.Run(() => manager.SetServiceDescriptions(i % 2 == 0 ? descriptions : "{}"))).ToArray();
            await Task.WhenAll(pushes).WaitAsync(TimeSpan.FromSeconds(30));
            manager.SetServiceDescriptions(descriptions);
            Check((await Library(manager, new { operation = "services" }))["servicesGeneration"]!.GetValue<string>() == expected.Generation, "the latest push wins after concurrent pushes");

            await manager.UnloadAsync();
            manager.SetServiceDescriptions("""{"light":{"turn_off":{"fields":{}}}}""");
            manager.SetServiceDescriptions("not json");
            Check(manager.ServiceSchemas.Count == 1, "pushes after unload are harmless");
        }
        finally { await manager.UnloadAsync(); PyInterop.Log = log; HassPath.HassConfiguration = previous; try { Directory.Delete(path, true); } catch { } }

        var snapshot = VisualCatalogSnapshot.BuiltIn.WithServices(expected);
        Check(ReferenceEquals(snapshot.Services, expected) && VisualCatalogSnapshot.BuiltIn.Services == null, "WithServices attaches without changing the original");
        Check(ReferenceEquals(snapshot.Root, VisualCatalogSnapshot.BuiltIn.Root), "WithServices shares the immutable root");
        var graph = JsonNode.Parse("""{"version":5,"name":"Services","nodes":[{"id":"t","kind":"trigger","config":{"type":"manual"}}],"edges":[]}""")!.AsObject();
        var generator = new VisualGenerator(new DiagnosticsProvider());
        var plain = generator.Preview(graph, VisualCatalogSnapshot.BuiltIn); var withServices = generator.Preview(graph, snapshot);
        Check(plain.Valid && withServices.Valid && plain.Source == withServices.Source, "services never enter generated source");
        Console.WriteLine("PASS service schema manager: before Init, swap without gate, malformed and post-unload pushes, catalog and services ops");
    }
}
