using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HassSharp;

/// <summary>The device a definition's device is connected through, such as a hub: another definition's, or a Home Assistant device.</summary>
/// <param name="Definition">The id of the definition whose device it is; null for a Home Assistant device.</param>
/// <param name="DeviceId">The Home Assistant device id; null for a definition's device.</param>
public sealed record DefinitionVia(string? Definition, string? DeviceId);

/// <summary>What the device page shows about a definition's device, each null for none (or the class's own).</summary>
/// <param name="Manufacturer">Who made the device.</param>
/// <param name="Model">The device's model.</param>
/// <param name="SwVersion">The version of the device's software.</param>
/// <param name="HwVersion">The version of the device's hardware.</param>
/// <param name="ConfigurationUrl">The address of the device's own configuration page.</param>
/// <param name="SuggestedArea">The area Home Assistant suggests when it first registers the device.</param>
/// <param name="Via">The device it is connected through.</param>
public sealed record DefinitionDevice(string? Manufacturer, string? Model, string? SwVersion, string? HwVersion,
    string? ConfigurationUrl, string? SuggestedArea, DefinitionVia? Via);

/// <summary>A value for one <c>[Setting]</c> property of the definition's class.</summary>
/// <param name="Key">The property's name.</param>
/// <param name="Value">The value as JSON; null leaves the class default.</param>
public sealed record DefinitionSetting(string Key, JsonNode? Value);

/// <summary>Where a definition's behaviour comes from.</summary>
/// <param name="Kind"><c>class</c> (a class the node libraries declare) or <c>graph</c> (not available yet).</param>
/// <param name="Type">For a class, its <see cref="EntityTypeDescriptor.Id"/>, such as <c>library:shelly:NodeLib.ShellyPlug</c>.</param>
/// <param name="Graph">For a graph, the graph.</param>
public sealed record DefinitionLogic(string Kind, string? Type, JsonObject? Graph);

/// <summary>A definition as the user writes it: what is saved, before the store adds its id, revision and source.</summary>
/// <param name="Name">What the definition and its device are called; up to 120 characters.</param>
/// <param name="Device">What the device page shows; null for the class's own.</param>
/// <param name="Settings">Values for the class's settings; one not given keeps the class default.</param>
/// <param name="Entities">The entities of a graph definition; a class definition's come from its code.</param>
/// <param name="Logic">The class or graph that gives it its behaviour.</param>
public sealed record DefinitionBody(string Name, DefinitionDevice? Device, List<DefinitionSetting>? Settings,
    JsonArray? Entities, DefinitionLogic Logic);

/// <summary>
/// A saved entity definition: its body, with the source generated from it when it was saved, as one JSON file of
/// <see cref="EntityDefinitionStore"/>.
/// </summary>
public sealed record EntityDefinitionDocument(int Version, string Id, long Revision, string Name, DefinitionDevice? Device,
    List<DefinitionSetting> Settings, JsonArray? Entities, DefinitionLogic Logic, string Source, List<VisualSourceMap> SourceMap)
{
    /// <summary>Off: the definition is not loaded, and its entities are unavailable. Saving keeps it.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>When the definition was last saved; turning it on or off and rebuilding it never change it.</summary>
    public DateTime? SavedUtc { get; init; }
    /// <summary>The library generation <see cref="Source"/> was generated from.</summary>
    public string? CatalogGeneration { get; init; }
    /// <summary>The definition as written, to preview or save again; derived, never stored.</summary>
    [JsonIgnore] public DefinitionBody Body => new(Name, Device, Settings, Entities, Logic);
}

/// <summary>A definition that cannot be saved, with the preview that says why.</summary>
public sealed class DefinitionValidationException(DefinitionPreview preview) : Exception("Definition validation failed")
{
    /// <summary>The preview, with the definition's problems.</summary>
    public DefinitionPreview Preview { get; } = preview;
}

/// <summary>
/// The entity definitions, one <c>&lt;id&gt;.json</c> file each in a directory. Every change checks the revision the
/// caller last read and writes a whole file in place of the old one.
/// </summary>
public sealed class EntityDefinitionStore(string directory, DefinitionGenerator generator)
{
    static readonly ConcurrentDictionary<string, object> Gates = new();
    object Gate => Gates.GetOrAdd(Path.GetFullPath(directory), _ => new());

    // An entity's unique id is "<definition id>:<entity key>", so an id never holds a colon.
    internal string FilePath(string id)
    {
        if (!Regex.IsMatch(id, @"^[a-z0-9][a-z0-9_-]{0,79}\z")) throw new ArgumentException("Invalid definition ID");
        return Path.Combine(directory, id + ".json");
    }

    static bool Complete(EntityDefinitionDocument? document, string id) => document != null && document.Version == 1 && document.Id == id &&
        document.Name != null && document.Settings != null && document.Logic != null && document.Source != null && document.SourceMap != null && document.Revision >= 1;

    /// <summary>The definition, or null when there is none.</summary>
    /// <exception cref="JsonException">Its file is not a definition.</exception>
    public EntityDefinitionDocument? Read(string id)
    {
        lock (Gate)
        {
            var path = FilePath(id);
            if (!File.Exists(path)) return null;
            var document = JsonSerializer.Deserialize<EntityDefinitionDocument>(File.ReadAllText(path), VisualStore.Json);
            if (!Complete(document, id)) throw new JsonException("Invalid entity definition envelope");
            return document;
        }
    }

    /// <summary>Every readable definition, by id; an unreadable file is logged and skipped, or throws when <paramref name="strict"/>.</summary>
    public EntityDefinitionDocument[] List(bool strict = false)
    {
        lock (Gate)
        {
            var documents = new List<EntityDefinitionDocument>();
            foreach (var id in Ids())
            {
                try { documents.Add(Read(id) ?? throw new JsonException("Invalid entity definition envelope")); }
                catch (Exception ex) when (ex is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
                { if (strict) throw; Logger.Error($"Skipping entity definition {Path.Combine(directory, id + ".json")}: {ex}"); }
            }
            return documents.ToArray();
        }
    }

    /// <summary>The id of every definition file, readable or not, in order.</summary>
    public string[] Ids()
    {
        lock (Gate)
        {
            if (!Directory.Exists(directory)) return [];
            return Directory.GetFiles(directory, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>Saves the definition at the next revision, with the source generated from it.</summary>
    /// <param name="expectedRevision">The revision last read; 0 for a new definition.</param>
    /// <param name="enabled">Only for a new definition: whether it starts on (the default) or off. An existing definition
    /// keeps its own; <see cref="SetEnabled"/> changes it.</param>
    /// <exception cref="VisualConflictException">The definition is at another revision.</exception>
    /// <exception cref="DefinitionValidationException">The definition has problems; nothing is written.</exception>
    public EntityDefinitionDocument Save(string id, DefinitionBody body, long expectedRevision, VisualCatalogSnapshot catalog,
        string? catalogGeneration, bool? enabled = null)
    {
        lock (Gate)
        {
            var path = FilePath(id); var old = Read(id);
            if ((old?.Revision ?? 0) != expectedRevision) throw new VisualConflictException();
            if (old != null && enabled != null)
                throw new ArgumentException("enabled can only be given when saving a new definition; use set_enabled to turn a saved one on or off");
            var preview = generator.Preview(body, catalog);
            if (!preview.Valid) throw new DefinitionValidationException(preview);
            // The document keeps its own copy of the JSON values, whatever the caller does with the body afterwards.
            var settings = (body.Settings ?? []).Select(s => new DefinitionSetting(s.Key, s.Value?.DeepClone())).ToList();
            var logic = body.Logic with { Graph = (JsonObject?)body.Logic.Graph?.DeepClone() };
            var document = new EntityDefinitionDocument(1, id, checked(expectedRevision + 1), body.Name, body.Device, settings,
                (JsonArray?)body.Entities?.DeepClone(), logic, preview.Source, preview.SourceMap)
                { CatalogGeneration = catalogGeneration, Enabled = old?.Enabled ?? enabled ?? true, SavedUtc = DateTime.UtcNow };
            Directory.CreateDirectory(directory);
            VisualStore.Write(path, document);
            return document;
        }
    }

    /// <summary>
    /// The definition at the next revision with the source of a new preview, such as one against changed libraries; its
    /// flag and save time stay. Nothing is written: the caller writes it with the libraries.
    /// </summary>
    /// <exception cref="DefinitionValidationException">The preview is not valid.</exception>
    public EntityDefinitionDocument Rebuilt(EntityDefinitionDocument old, DefinitionPreview preview, string? catalogGeneration)
    {
        if (!preview.Valid) throw new DefinitionValidationException(preview);
        return old with { Revision = checked(old.Revision + 1), Source = preview.Source, SourceMap = preview.SourceMap, CatalogGeneration = catalogGeneration };
    }

    /// <summary>Turns the definition on or off at its current revision; the body, revision and save time stay as they are.</summary>
    /// <exception cref="FileNotFoundException">There is no such definition.</exception>
    public EntityDefinitionDocument SetEnabled(string id, bool enabled)
    {
        lock (Gate)
        {
            var path = FilePath(id);
            var old = Read(id) ?? throw new FileNotFoundException("Entity definition not found");
            if (old.Enabled == enabled) return old;
            var document = old with { Enabled = enabled };
            VisualStore.Write(path, document);
            return document;
        }
    }

    /// <summary>Deletes the definition at the revision last read; revision 0 deletes a file that is not a definition.</summary>
    /// <exception cref="VisualConflictException">The definition is at another revision.</exception>
    /// <exception cref="FileNotFoundException">There is no such definition.</exception>
    public void Delete(string id, long expectedRevision)
    {
        lock (Gate)
        {
            EntityDefinitionDocument old;
            try { old = Read(id) ?? throw new FileNotFoundException("Entity definition not found"); }
            catch (JsonException)
            {
                // Revision zero is an explicit recovery request, never a wildcard for valid documents.
                if (expectedRevision != 0) throw new VisualConflictException();
                File.Delete(FilePath(id));
                return;
            }
            if (old.Revision != expectedRevision) throw new VisualConflictException();
            File.Delete(FilePath(id));
        }
    }
}
