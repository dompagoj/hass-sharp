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

/// <summary>
/// A setting of the definition: for a class, a value for one of its <c>[Setting]</c> properties (the class declares the
/// rest); for a graph, the setting itself, read by the graph's Setting nodes.
/// </summary>
/// <param name="Key">The property's name, or the graph setting's key.</param>
/// <param name="Value">The value as JSON; null leaves the class default, or gives a graph an empty value.</param>
public sealed record DefinitionSetting(string Key, JsonNode? Value)
{
    /// <summary>A graph setting's name in the panel; null for its key. A class's settings have none (the class names them).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Label { get; init; }
    /// <summary>A graph setting's type: <c>string</c>, <c>boolean</c> or <c>number</c>. A class's settings have none (the class types them).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Type { get; init; }
    /// <summary>A graph setting whose value is shown masked, such as a password. A class's settings have none (the class marks them).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Secret { get; init; }
    /// <summary>
    /// In a save or preview request only, given without a value: keep the value the stored definition has for this key,
    /// such as a secret the panel was never sent (none when it has none). The store puts that value in its place, so it
    /// is never stored or answered; a preview, which reads no stored definition, checks no value for it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Keep { get; init; }
    /// <summary>
    /// In a save or preview request only: what an answer says of a setting it masks, that a value is stored. Sent back
    /// with no value, as the answer has it, the setting keeps that value as <see cref="Keep"/> does; a value given wins.
    /// Never stored: the answer works it out from the stored value.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool HasValue { get; init; }
}

/// <summary>Where a definition's behaviour comes from.</summary>
/// <param name="Kind"><c>class</c> (a class the node libraries declare) or <c>graph</c> (a graph of nodes).</param>
/// <param name="Type">For a class, its <see cref="EntityTypeDescriptor.Id"/>, such as <c>library:shelly:NodeLib.ShellyPlug</c>.</param>
/// <param name="Graph">For a graph, the graph, never with a <c>definition</c>: that is composed from the document's
/// settings and device (<see cref="DefinitionGraph.Compose"/>). Its entities are its Entity nodes.</param>
public sealed record DefinitionLogic(string Kind, string? Type, JsonObject? Graph);

/// <summary>A definition as the user writes it: what is saved, before the store adds its id, revision and source.</summary>
/// <param name="Name">What the definition and its device are called; up to 120 characters.</param>
/// <param name="Device">What the device page shows; null for the class's own.</param>
/// <param name="Settings">Values for the class's settings, one not given keeping the class default; or a graph's settings.</param>
/// <param name="Entities">A request gives none: a graph definition's entities are its Entity nodes, and a request that
/// lists them, even as an empty list, is refused (<c>definition.entitiesNotAllowed</c>); a class definition's come from its
/// code, and an empty list is the same as none.</param>
/// <param name="Logic">The class or graph that gives it its behaviour.</param>
public sealed record DefinitionBody(string Name, DefinitionDevice? Device, List<DefinitionSetting>? Settings,
    JsonArray? Entities, DefinitionLogic Logic);

/// <summary>
/// A saved entity definition: its body, with the source generated from it when it was saved, as one JSON file of
/// <see cref="EntityDefinitionStore"/>. <see cref="Version"/> is 2; a class definition an earlier build saved is 1.
/// <see cref="Entities"/> is, for a graph, the entities its Entity nodes declare, for reading only.
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
    /// <summary>
    /// The definition as written, to preview or save again; derived, never stored. A graph's has no entities: they are its
    /// Entity nodes, so a regeneration composes from the graph alone.
    /// </summary>
    [JsonIgnore] public DefinitionBody Body => new(Name, Device, Settings, Logic?.Kind == "graph" ? null : Entities, Logic!);
    /// <summary>
    /// For a graph, the pruned catalog JSON <see cref="Source"/> embeds, for the run snapshot, as the preview that generated
    /// the source gave it; derived, never stored, so null for a document read from its file.
    /// </summary>
    [JsonIgnore] public string? EmbeddedCatalog { get; init; }
}

/// <summary>A definition that cannot be saved, with the preview that says why.</summary>
public sealed class DefinitionValidationException(DefinitionPreview preview) : Exception("Definition validation failed")
{
    /// <summary>The preview, with the definition's problems.</summary>
    public DefinitionPreview Preview { get; } = preview;
}

/// <summary>
/// A definition file that cannot be read, as a list names it: its id and why (for one an earlier development build saved,
/// <see cref="EntityDefinitionStore.EarlierBuild"/>). It is deleted at revision 0.
/// </summary>
public sealed record UnreadableDefinition(string Id, string Reason);

/// <summary>
/// The entity definitions, one <c>&lt;id&gt;.json</c> file each in a directory. Every change checks the revision the
/// caller last read and writes a whole file in place of the old one.
/// </summary>
public sealed class EntityDefinitionStore(string directory, DefinitionGenerator generator)
{
    static readonly ConcurrentDictionary<string, object> Gates = new();
    // The unreadable files List has logged, by full path, with the message logged: each is logged again only when its
    // message changes, not on every list (a retry lists them on each push of Home Assistant's services).
    static readonly ConcurrentDictionary<string, string> Skipped = new();
    object Gate => Gates.GetOrAdd(Path.GetFullPath(directory), _ => new());

    // An entity's unique id is "<definition id>:<entity key>", so an id never holds a colon.
    internal string FilePath(string id)
    {
        if (!Regex.IsMatch(id, @"^[a-z0-9][a-z0-9_-]{0,79}\z")) throw new ArgumentException("Invalid definition ID");
        return Path.Combine(directory, id + ".json");
    }

    /// <summary>The version of the document a save writes: a graph's entities are its Entity nodes.</summary>
    internal const int Version = 2;
    /// <summary>
    /// What a graph definition an earlier development build saved (version 1, whose entities may be a list) is told: it
    /// is not read, and is deleted as any unreadable file is.
    /// </summary>
    internal const string EarlierBuild = "saved by an earlier development build; delete it and build it again";

    // Version 2; or 1 for a class definition, whose document is the same in both.
    static bool Complete(EntityDefinitionDocument? document, string id) => document != null && (document.Version == Version || document.Version == 1 && document.Logic?.Kind == "class")
        && document.Id == id && document.Name != null && document.Settings != null && document.Logic != null && document.Source != null && document.SourceMap != null && document.Revision >= 1;

    /// <summary>The definition, or null when there is none.</summary>
    /// <exception cref="JsonException">Its file is not a definition, or is a graph definition an earlier build saved (<see cref="EarlierBuild"/>).</exception>
    public EntityDefinitionDocument? Read(string id)
    {
        lock (Gate)
        {
            var path = FilePath(id);
            if (!File.Exists(path)) return null;
            var document = JsonSerializer.Deserialize<EntityDefinitionDocument>(File.ReadAllText(path), VisualStore.Json);
            if (document is { Version: 1, Logic.Kind: "graph" }) throw new JsonException(EarlierBuild);
            if (!Complete(document, id)) throw new JsonException("Invalid entity definition envelope");
            return document;
        }
    }

    /// <summary>
    /// Every readable definition, by id. An unreadable file is logged with its message, once until that message changes,
    /// and skipped (and added to <paramref name="unreadable"/>, with that message, when one is given); when
    /// <paramref name="strict"/> it throws instead, an exception of the same kind naming the file.
    /// </summary>
    public EntityDefinitionDocument[] List(bool strict = false, ICollection<UnreadableDefinition>? unreadable = null)
    {
        lock (Gate)
        {
            var documents = new List<EntityDefinitionDocument>();
            foreach (var id in Ids())
            {
                var path = Path.GetFullPath(Path.Combine(directory, id + ".json"));
                try
                {
                    documents.Add(Read(id) ?? throw new JsonException("Invalid entity definition envelope"));
                    Skipped.TryRemove(path, out _);
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
                {
                    var message = $"Cannot read entity definition {path}: {ex.Message}";
                    if (strict) throw ex switch
                    {
                        IOException => new IOException(message, ex),
                        UnauthorizedAccessException => new UnauthorizedAccessException(message, ex),
                        ArgumentException => new ArgumentException(message, ex),
                        _ => new JsonException(message, ex),
                    };
                    unreadable?.Add(new(id, ex.Message));
                    if (Skipped.TryGetValue(path, out var logged) && logged == ex.Message) continue;
                    Skipped[path] = ex.Message;
                    Logger.Error($"Skipping entity definition {path}: {ex.Message}");
                }
            }
            return documents.ToArray();
        }
    }

    /// <summary>Whether the directory exists: a fresh install, or a partly restored <c>.storage</c>, has none.</summary>
    public bool Exists() { lock (Gate) return Directory.Exists(directory); }

    /// <summary>The id of every definition file, readable or not, in order.</summary>
    public string[] Ids()
    {
        lock (Gate)
        {
            if (!Directory.Exists(directory)) return [];
            return Directory.GetFiles(directory, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>
    /// Saves the definition at the next revision, with the source generated from it. A setting given
    /// <see cref="DefinitionSetting.Keep"/> is saved with the value stored before (<see cref="WithKeptValues"/>).
    /// </summary>
    /// <param name="expectedRevision">The revision last read; 0 for a new definition.</param>
    /// <param name="enabled">Only for a new definition: whether it starts on (the default) or off. An existing definition
    /// keeps its own; <see cref="SetEnabled"/> changes it.</param>
    /// <exception cref="VisualConflictException">The definition is at another revision.</exception>
    /// <exception cref="DefinitionValidationException">The definition has problems; nothing is written.</exception>
    public EntityDefinitionDocument Save(string id, DefinitionBody body, long expectedRevision, VisualCatalogSnapshot catalog,
        string? catalogGeneration, bool? enabled = null) => Save(id, body, expectedRevision, catalog, catalogGeneration, enabled, out _);

    /// <inheritdoc cref="Save(string, DefinitionBody, long, VisualCatalogSnapshot, string?, bool?)"/>
    /// <param name="preview">The preview it was saved with; its diagnostics are the graph's warnings, if any.</param>
    public EntityDefinitionDocument Save(string id, DefinitionBody body, long expectedRevision, VisualCatalogSnapshot catalog,
        string? catalogGeneration, bool? enabled, out DefinitionPreview preview)
    {
        lock (Gate)
        {
            var path = FilePath(id);
            // The panel's page for a new definition is /entities/new, so a definition saved as new could never be opened.
            if (id == "new") throw new ArgumentException("Invalid definition ID: new is reserved");
            var old = Read(id);
            if ((old?.Revision ?? 0) != expectedRevision) throw new VisualConflictException();
            if (old != null && enabled != null)
                throw new ArgumentException("enabled can only be given when saving a new definition; use set_enabled to turn a saved one on or off");
            body = WithKeptValues(body, old);
            var graphLogic = body.Logic?.Kind == "graph";
            // A graph is laid out as an automation's is, before it is previewed: the source embeds it. A definition a client
            // put in it is never trusted. A graph of the wrong shape is the preview's to report, as it is.
            if (graphLogic && body.Logic!.Graph is { } given)
            {
                var graph = DefinitionGraph.Decompose(given);
                if (graph["nodes"] is JsonArray nodes && nodes.All(n => n is JsonObject)) graph = VisualGraphContract.FillMissingLayout(graph);
                body = body with { Logic = body.Logic with { Graph = graph } };
            }
            preview = generator.Preview(body, catalog);
            if (!preview.Valid) throw new DefinitionValidationException(preview);
            // The document keeps its own copy of the JSON values, whatever the caller does with the body afterwards. A
            // class's settings are values only, as they always were; a graph's keep their label, type and secret flag.
            var settings = (body.Settings ?? []).Select(s => graphLogic ? s with { Value = s.Value?.DeepClone() } : new DefinitionSetting(s.Key, s.Value?.DeepClone())).ToList();
            // A graph is stored as previewed (a Custom C# signature the compile refreshed, as refreshed), without its definition.
            // A valid preview had logic to preview.
            var logic = graphLogic ? body.Logic! with { Graph = DefinitionGraph.Decompose(preview.Graph!) } : body.Logic! with { Graph = (JsonObject?)body.Logic.Graph?.DeepClone() };
            // A graph's entities are stored as its Entity nodes declare them, for a read answer, a list and Home Assistant.
            var entities = graphLogic ? DefinitionGraph.Entities(preview.Graph!, catalog) : (JsonArray?)body.Entities?.DeepClone();
            var document = new EntityDefinitionDocument(Version, id, checked(expectedRevision + 1), body.Name, body.Device, settings,
                entities, logic, preview.Source, preview.SourceMap)
                { CatalogGeneration = catalogGeneration, Enabled = old?.Enabled ?? enabled ?? true, SavedUtc = DateTime.UtcNow, EmbeddedCatalog = preview.EmbeddedCatalog?.ToJsonString() };
            Directory.CreateDirectory(directory);
            VisualStore.Write(path, document);
            return document;
        }
    }

    /// <summary>
    /// The body with each setting that keeps its value holding the value <paramref name="old"/> has for its key instead,
    /// none when it has none (or there is no old definition): one given <see cref="DefinitionSetting.Keep"/>, or sent back
    /// as an answer masks it, <see cref="DefinitionSetting.HasValue"/> and no value. Its other fields stay as given, but
    /// that a value kept from a secret setting stays secret: making it open needs the value given again. Neither marker
    /// is left on any setting. Under the lock of the save, so the value kept is the one the revision checked has, and is
    /// checked as any value is.
    /// </summary>
    static DefinitionBody WithKeptValues(DefinitionBody body, EntityDefinitionDocument? old)
    {
        if (body.Settings is not { } given || !given.Any(s => s is { Keep: true } or { HasValue: true })) return body;
        static bool Kept(DefinitionSetting s) => s.Keep || s.HasValue && (s.Value == null || s.Value.GetValueKind() == JsonValueKind.Null);
        DefinitionSetting? Stored(string key) => old?.Settings.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));
        return body with { Settings = [.. given.Select(s => s is not { } setting ? s
            : !Kept(setting) ? setting with { HasValue = false }
            : setting with { Value = Stored(setting.Key)?.Value?.DeepClone(), Secret = setting.Secret || Stored(setting.Key)?.Secret == true, Keep = false, HasValue = false })] };
    }

    /// <summary>
    /// The definition at the next revision with the source of a new preview, such as one against changed libraries; its
    /// flag and save time stay. Nothing is written: the caller writes it with the libraries.
    /// </summary>
    /// <exception cref="DefinitionValidationException">The preview is not valid.</exception>
    public EntityDefinitionDocument Rebuilt(EntityDefinitionDocument old, DefinitionPreview preview, string? catalogGeneration)
    {
        if (!preview.Valid) throw new DefinitionValidationException(preview);
        // A graph is stored as previewed, as a rebuilt automation's is, and without its definition.
        var logic = old.Logic.Kind == "graph" && preview.Graph != null ? old.Logic with { Graph = DefinitionGraph.Decompose(preview.Graph) } : old.Logic;
        return old with { Revision = checked(old.Revision + 1), Logic = logic, Source = preview.Source, SourceMap = preview.SourceMap, CatalogGeneration = catalogGeneration,
            EmbeddedCatalog = preview.EmbeddedCatalog?.ToJsonString() };
    }

    /// <summary>
    /// Stores the source generated from the definition again, such as against libraries changed on disk, so the revision
    /// running is recognised as current. Only when it is still at <paramref name="expectedRevision"/> and something
    /// changed; the body, revision, flag and save time stay as they are.
    /// </summary>
    /// <returns>Whether the file was written.</returns>
    internal bool RefreshDerived(string id, long expectedRevision, string source, List<VisualSourceMap> sourceMap, string? catalogGeneration)
    {
        lock (Gate)
        {
            var path = FilePath(id); var old = Read(id);
            if (old == null || old.Revision != expectedRevision) return false;
            if (old.Source == source && old.SourceMap.SequenceEqual(sourceMap) && old.CatalogGeneration == catalogGeneration) return false;
            VisualStore.Write(path, old with { Source = source, SourceMap = sourceMap, CatalogGeneration = catalogGeneration });
            return true;
        }
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
