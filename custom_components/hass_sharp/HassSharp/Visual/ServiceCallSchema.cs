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
    SortedDictionary<string, JsonObject>? current;
    string? currentJson, saved;
    bool generated;

    /// <summary>
    /// The live services united with the current ones (the live definition wins), or only the live ones once Home
    /// Assistant has started. Returns the result when the typed calls must be generated from it (it changed, or nothing
    /// was generated yet), else null; saves it when it differs from the file.
    /// </summary>
    public IReadOnlyCollection<KeyValuePair<string, JsonObject>>? Update(ServiceSchemaCatalog live, bool started)
    {
        current ??= Load();
        currentJson ??= Serialize(current);
        var next = new SortedDictionary<string, JsonObject>(started ? new Dictionary<string, JsonObject>() : current, StringComparer.Ordinal);
        foreach (var (id, service) in live.Items) if (service is JsonObject schema) next[id] = schema;
        var json = Serialize(next);
        var changed = json != currentJson;
        (current, currentJson) = (next, json);
        if (json != saved) Save(json);
        if (generated && !changed) return null;
        generated = true;
        return next;
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
