using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

/// <summary>
/// The service schema the typed service calls (<c>context.Services</c>) were last generated from, kept in
/// <c>.storage/hass_sharp/service_schema.json</c> as <c>{version: 1, items: {"domain.service": normalised}}</c>.
/// Not thread safe: the manager serialises its calls.
/// </summary>
sealed class ServiceCallSchema(string path)
{
    static readonly JsonWriterOptions Writing = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    // What the typed calls were last generated from (and the file holds), and the last schema proposed for them.
    SortedDictionary<string, JsonObject>? current;
    string? proposed, saved;

    /// <summary>
    /// The live services united with the current ones (the live definition wins), or only the live ones once Home
    /// Assistant has started: what the typed calls should be generated from. Null when that is the schema proposed
    /// last (whether it was accepted or not), so a schema is generated and checked once. Nothing is saved until
    /// <see cref="Accept"/>.
    /// </summary>
    public IReadOnlyCollection<KeyValuePair<string, JsonObject>>? Update(ServiceSchemaCatalog live, bool started)
    {
        current ??= Load();
        var next = new SortedDictionary<string, JsonObject>(started ? new Dictionary<string, JsonObject>() : current, StringComparer.Ordinal);
        foreach (var (id, service) in live.Items) if (service is JsonObject schema) next[id] = schema;
        var json = Serialize(next);
        if (json == proposed) return null;
        proposed = json;
        return next;
    }

    /// <summary>
    /// The services the typed calls were generated from: they become the current ones and are saved. A schema whose
    /// calls did not compile is never accepted, so it is never saved.
    /// </summary>
    public void Accept(IEnumerable<KeyValuePair<string, JsonObject>> services)
    {
        current = new SortedDictionary<string, JsonObject>(services.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), StringComparer.Ordinal);
        var json = Serialize(current);
        if (json != saved) Save(json);
    }

    SortedDictionary<string, JsonObject> Load()
    {
        var loaded = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path)) { Log(Logger.Info, "No saved service schema yet; the typed service calls start from the live one"); return loaded; }
            var text = File.ReadAllText(path);
            if (JsonNode.Parse(text) is not JsonObject { } root || root["version"] is not JsonValue version || !version.TryGetValue<int>(out var number) || number != 1 || root["items"] is not JsonObject items)
                throw new JsonException("it is not version 1 of the saved service schema");
            foreach (var (id, service) in items) if (service is JsonObject schema) loaded[id] = schema;
            saved = text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            loaded.Clear();
            Log(Logger.Warn, $"Ignoring the saved service schema {path}; the typed service calls start from the live one: {ex.Message}");
        }
        return loaded;
    }

    void Save(string json)
    {
        var temporary = path + "." + Guid.NewGuid() + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file)) { writer.Write(json); writer.Flush(); file.Flush(true); }
            File.Move(temporary, path, true);
            saved = json;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log(Logger.Error, $"Cannot save the service schema {path}: {ex.Message}"); }
        finally { try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    static string Serialize(SortedDictionary<string, JsonObject> services)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Writing))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteStartObject("items");
            foreach (var (id, schema) in services) { writer.WritePropertyName(id); schema.WriteTo(writer); }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    // Logging crosses into Python; a failing log line must not fail a push or Init.
    static void Log(Action<string> log, string message) { try { log(message); } catch { } }
}
