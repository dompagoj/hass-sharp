using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

public partial class HassSharpManager
{
    /// <summary>The saved entity definitions, one file each in <c>.storage/hass_sharp/entities</c>.</summary>
    EntityDefinitionStore EntityDocuments => new(Path.Combine(AuthoringRoot, "entities"), new DefinitionGenerator());

    /// <summary>
    /// The path a definition's compiled script is known by in the script manager, its load errors and run history. It
    /// is never written; its folder and prefix keep it apart from an automation's of the same id.
    /// </summary>
    string EntityRuntimePath(string id) => Path.Combine(AuthoringRoot, "entities", "entity_" + id + ".cs");

    /// <summary>How long a save waits for a definition's <c>Start()</c>, so its answer says whether it runs; then it answers <c>setup</c>.</summary>
    static readonly TimeSpan StartReportWait = TimeSpan.FromSeconds(5);

    /// <summary>How long a delete waits for Home Assistant to remove the definition's entities; settable for tests.</summary>
    internal static TimeSpan RemovalTimeout { get; set; } = TimeSpan.FromSeconds(30);

    // The started host of the definition; null when none is, or when it is stopping.
    DefinitionHost? RunningHost(string id) => _userScriptManager.Entities.Host(id) is { Status: not "stopped" } host ? host : null;

    /// <summary>
    /// The document as the API answers it: its stored fields, and how it runs now: <c>automationId</c>, <c>active</c>,
    /// <c>currentRevisionActive</c>, <c>activationError</c> (the load error, else why its <c>Start()</c> failed),
    /// <c>status</c> (<c>running</c>, <c>setup</c>, <c>failed</c>, or
    /// <c>off</c> when not loaded) and its <c>liveEntities</c> (none when not loaded).
    /// </summary>
    JsonObject EntityJson(EntityDefinitionDocument document)
    {
        var path = EntityRuntimePath(document.Id);
        var running = _userScriptManager.GetUserScripts().FirstOrDefault(s => s.CompiledScript.FilePath == path && s.Active);
        var host = RunningHost(document.Id);
        var status = host?.Status ?? "off";
        var json = JsonSerializer.SerializeToNode(document, VisualStore.Json)!.AsObject();
        json["automationId"] = _history.Identity(path);
        json["active"] = running != null;
        json["currentRevisionActive"] = running != null && running.CompiledScript.VisualRevision == document.Revision && running.CompiledScript.SourceCode == document.Source && !_loadErrors.ContainsKey(path);
        json["activationError"] = _loadErrors.TryGetValue(path, out var error) ? error : status == "failed" ? host!.Error : running != null ? null : "Not active";
        json["status"] = status;
        json["liveEntities"] = JsonSerializer.SerializeToNode(host?.Entities ?? [], VisualStore.Json);
        return json;
    }

    async Task<CompiledUserScript> CompileDefinition(EntityDefinitionDocument document, GeneratedServiceCalls? services)
    {
        var compiled = await _compiler.CompileSingleFile(EntityRuntimePath(document.Id), document.Source, services);
        compiled.DesignatedRootType = "SharpDefinition";
        compiled.VisualRevision = document.Revision;
        compiled.CatalogGeneration = document.CatalogGeneration;
        compiled.Definition = new(document.Id, document.Name, document.Device, document.Settings);
        return compiled;
    }

    /// <summary>
    /// Runs the saved definition: compiles it and loads it, or replaces the revision running. A failure is recorded as
    /// its load error and answered as <c>activationError</c>; when no revision is left running then, the entities it
    /// last ran are shown unavailable. A definition that loaded but whose <c>Start()</c> threw answers that error as
    /// <c>activationError</c>, and stays active. A definition that is off is never started: whatever of it runs is unloaded.
    /// </summary>
    /// <param name="services">The typed service calls the request read once, so the compile references them.</param>
    /// <returns><c>{id, saved, document, active, activationError}</c>.</returns>
    internal async Task<object> ActivateDefinition(EntityDefinitionDocument document, GeneratedServiceCalls? services = null)
    {
        string? activationError = null;
        if (!document.Enabled) await UnloadDefinition(document.Id);
        else
        {
            activationError = await LoadDefinition(document, services);
            // The answer says how the start went, unless it takes long; a publish that failed in it is the host's to log.
            if (RunningHost(document.Id) is { } host)
                try { await host.Started.WaitAsync(StartReportWait); } catch (Exception) { }
            // Loaded, so active, but its Start() threw: the answer says why.
            if (activationError == null && RunningHost(document.Id) is { Status: "failed" } failed) activationError = failed.Error;
        }
        var json = EntityJson(document);
        return new { id = document.Id, saved = true, document = json, active = json["currentRevisionActive"]!.GetValue<bool>(), activationError };
    }

    /// <summary>
    /// Compiles the definition and loads it, or replaces the revision running, without waiting for its start. A failure
    /// is recorded as its load error; when no revision is left running then, the entities it last ran are shown unavailable.
    /// </summary>
    /// <returns>The load error; null when it loaded.</returns>
    async Task<string?> LoadDefinition(EntityDefinitionDocument document, GeneratedServiceCalls? services = null)
    {
        var path = EntityRuntimePath(document.Id);
        var known = RunningHost(document.Id)?.Entities ?? [];
        try { await LoadOrReplace(await CompileDefinition(document, services)); }
        catch (Exception ex)
        {
            RecordLoadError(path, ex);
            if (RunningHost(document.Id) == null) PostUnavailable(known);
            return _loadErrors[path];
        }
        ClearLoadError(path);
        return null;
    }

    /// <summary>The definition with the source generated from it against these libraries.</summary>
    /// <exception cref="DefinitionValidationException">It is not valid against them.</exception>
    static EntityDefinitionDocument Regenerated(EntityDefinitionDocument document, VisualCatalogSnapshot snapshot, string generation)
    {
        var preview = new DefinitionGenerator().Preview(document.Body, snapshot);
        if (!preview.Valid) throw new DefinitionValidationException(preview);
        return document with { Source = preview.Source, SourceMap = preview.SourceMap, CatalogGeneration = generation };
    }

    /// <summary>
    /// Stores the definition's regenerated source, so the revision that runs it is recognised as current and a later
    /// activation compiles it, not the code its file embedded before (see <see cref="EntityDefinitionStore.RefreshDerived"/>).
    /// A failure is logged, never thrown.
    /// </summary>
    void StoreRegenerated(EntityDefinitionDocument current)
    {
        try { EntityDocuments.RefreshDerived(current.Id, current.Revision, current.Source, current.SourceMap, current.CatalogGeneration); }
        catch (Exception ex) { try { Logger.Error($"Cannot refresh stored source of entity definition {current.Id}: {ex}"); } catch { } }
    }

    /// <summary>
    /// At startup: each enabled saved definition, regenerated against the saved libraries and compiled, for the loader to
    /// load with the automations, its regenerated source stored. One that does not is recorded and logged, and the others
    /// are compiled; an unreadable file is logged and skipped. A definition that is off is never compiled: only its
    /// source is regenerated and stored, when it is valid, so turning it on later runs the libraries as saved.
    /// </summary>
    async Task<List<CompiledUserScript>> CompileSavedDefinitions(VisualCatalogSnapshot snapshot, string generation)
    {
        List<CompiledUserScript> compiled = [];
        EntityDefinitionDocument[] documents;
        try { documents = EntityDocuments.List(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { Logger.Error($"Cannot read the entity definitions; none is loaded: {ex.Message}"); } catch { }
            return compiled;
        }
        foreach (var document in documents)
        {
            if (!document.Enabled)
            {
                // One that is not valid keeps its stored source; turning it on says why.
                try { StoreRegenerated(Regenerated(document, snapshot, generation)); }
                catch (DefinitionValidationException) { }
                catch (Exception ex) { try { Logger.Debug($"Regenerating entity definition {document.Id} failed: {ex}"); } catch { } }
                continue;
            }
            // The source map of the code that was compiled names the library of a compiler error.
            var current = document;
            try
            {
                current = Regenerated(document, snapshot, generation);
                compiled.Add(await CompileDefinition(current, null));
                StoreRegenerated(current);
            }
            catch (Exception ex)
            {
                ReportLoadFailure(document.Id, () => { RecordLoadError(EntityRuntimePath(document.Id), ex); return DefinitionLoadFailure(current, ex); }, "entity definition");
                // Neither a definition that does not validate nor code that does not compile is a HassSharp bug; anything else is.
                if (ex is not (DefinitionValidationException or CompilationErrorException)) try { Logger.Debug($"Loading entity definition {document.Id} failed: {ex}"); } catch { }
            }
        }
        return compiled;
    }

    /// <summary>
    /// The startup log line for a saved definition that does not load: its name and id, then why, in the words
    /// <see cref="LoadFailure"/> uses: its first problems, its first compiler errors, or the error's message.
    /// </summary>
    static string DefinitionLoadFailure(EntityDefinitionDocument document, Exception error)
    {
        var name = $"\"{document.Name}\" ({document.Id})";
        if (error is DefinitionValidationException invalid)
        {
            var problems = invalid.Preview.Diagnostics.Where(d => d.Severity >= 3).Select(d => d.Message).ToArray();
            return $"Cannot load entity definition {name}: {(problems.Length == 1 ? "1 problem" : $"{problems.Length} problems")}: {Listed(problems)}";
        }
        if (error is CompilationErrorException compilation)
        {
            // "path(line,column): error CS1002: ; expected": the message, its code, and the node library that line is in.
            string Located(string text)
            {
                var match = CompilerError.Match(text);
                if (!match.Success) return text;
                var line = int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
                var library = document.SourceMap.FirstOrDefault(m => line >= m.StartLine && line <= m.EndLine)?.NodeId?["$library:".Length..];
                return $"{match.Groups["message"].Value} ({match.Groups["code"].Value}{(library == null ? "" : ", node library " + library)})";
            }
            return $"Cannot load entity definition {name}: its code does not compile: {Listed([.. compilation.Errors.Select(Located)])}";
        }
        return $"Cannot load entity definition {name}: {Capped(error.GetBaseException().Message)}";
    }

    /// <summary>
    /// Tells Home Assistant which definitions exist, by the name of each stored file, readable or not, so it removes the
    /// entities of every other one. Waits no longer than <see cref="RemovalTimeout"/>; a failure is logged, never thrown,
    /// and when the files cannot be listed nothing is pruned.
    /// </summary>
    async Task PruneEntities()
    {
        try { await PyInterop.PruneEntitiesAsync(JsonSerializer.Serialize(EntityDocuments.Ids())).WaitAsync(RemovalTimeout); }
        catch (Exception ex)
        {
            var reason = ex is TimeoutException ? $"Home Assistant did not answer within {RemovalTimeout.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s" : ex.Message;
            try { Logger.Error($"Cannot prune the entities of entity definitions that no longer exist from Home Assistant: {reason}"); } catch { }
        }
    }

    /// <summary>
    /// After the typed service calls or the entity classes changed: loads again each enabled definition whose last load
    /// failed, that a push may cure, and that is not loaded, without waiting for its start. Runs under the visual gate.
    /// </summary>
    async Task RetryFailedDefinitions(VisualCatalogSnapshot snapshot, string generation)
    {
        foreach (var document in EntityDocuments.List().Where(d => d.Enabled))
        {
            var path = EntityRuntimePath(document.Id);
            if (_languageLifetime.IsCancellationRequested) return;
            if (!_loadErrors.ContainsKey(path) || !_curableLoadErrors.ContainsKey(path) || _userScriptManager.GetUserScripts().Any(s => s.CompiledScript.FilePath == path)) continue;
            try
            {
                var current = Regenerated(document, snapshot, generation);
                StoreRegenerated(current);
                if (await LoadDefinition(current) == null)
                    Logger.Info($"Entity definition {document.Id} loaded now that the typed service calls or entity classes changed");
            }
            catch (Exception ex) { RecordLoadError(path, ex); }
        }
    }

    /// <summary>
    /// A library save's check of every definition against the candidate libraries. <see cref="Breaking"/>: those valid
    /// with the saved libraries and not with the candidate, which refuse the save. <see cref="Invalid"/>: those already
    /// invalid with the saved libraries, left as they are. <see cref="Rebuilt"/>: the others at their next revision, to
    /// be written with the libraries.
    /// </summary>
    sealed record DefinitionRebuild(object[] Breaking, object[] Invalid, EntityDefinitionDocument[] Rebuilt);

    /// <summary>
    /// Previews every definition, on or off, against the candidate libraries, as a library save previews the graphs: one
    /// that is invalid with the saved libraries too does not block the save. Nothing is written.
    /// </summary>
    /// <exception cref="JsonException">A definition file cannot be read: a library save cannot check it.</exception>
    DefinitionRebuild CheckDefinitions(VisualCatalogSnapshot candidate, VisualCatalogSnapshot saved, string generation)
    {
        var store = EntityDocuments;
        var all = store.List(strict: true).Select(d => (Document: d, Preview: new DefinitionGenerator().Preview(d.Body, candidate))).ToArray();
        var invalidBefore = all.Where(p => !p.Preview.Valid && !new DefinitionGenerator().Preview(p.Document.Body, saved).Valid).Select(p => p.Document.Id).ToHashSet(StringComparer.Ordinal);
        var previews = all.Where(p => !invalidBefore.Contains(p.Document.Id)).ToArray();
        var breaking = previews.Where(p => !p.Preview.Valid).Select(p => (object)new { id = p.Document.Id, name = p.Document.Name, diagnostics = p.Preview.Diagnostics }).ToArray();
        // Such a definition is not rebuilt: a copy of it that is running keeps the previous version, with the previous library code.
        var invalid = all.Where(p => invalidBefore.Contains(p.Document.Id)).Select(p =>
        {
            var running = _userScriptManager.GetUserScripts().Any(s => s.CompiledScript.FilePath == EntityRuntimePath(p.Document.Id) && s.Active);
            return (object)new { id = p.Document.Id, name = p.Document.Name, diagnostics = p.Preview.Diagnostics, running,
                message = running ? "Not rebuilt, because the definition has errors: it is still running the previous version, with the previous library code"
                    : "Not rebuilt, because the definition has errors; it is not running" };
        }).ToArray();
        EntityDefinitionDocument[] rebuilt = breaking.Length > 0 ? [] : previews.Select(p => store.Rebuilt(p.Document, p.Preview, generation)).ToArray();
        return new(breaking, invalid, rebuilt);
    }

    /// <summary>The rebuilt definitions' files, to write in the same commit as the libraries.</summary>
    void AddDefinitionChanges(Dictionary<string, string?> changes, DefinitionRebuild rebuild)
    {
        var store = EntityDocuments;
        foreach (var document in rebuild.Rebuilt) changes[store.FilePath(document.Id)] = JsonSerializer.Serialize(document, VisualStore.Json);
    }

    /// <summary>Runs each rebuilt definition that is on, in place of the revision running; one that is off stays off.</summary>
    /// <returns>Each one's activation answer, as <see cref="ActivateDefinition"/> gives it.</returns>
    async Task<List<object>> ReactivateDefinitions(DefinitionRebuild rebuild)
    {
        var answers = new List<object>();
        foreach (var document in rebuild.Rebuilt.Where(d => d.Enabled)) answers.Add(await ActivateDefinition(document));
        return answers;
    }

    /// <summary>Unloads the definition, forgets its load error and shows the entities it ran unavailable.</summary>
    async Task UnloadDefinition(string id)
    {
        var known = RunningHost(id)?.Entities ?? [];
        await _userScriptManager.UnloadScriptAsync(EntityRuntimePath(id));
        ClearLoadError(EntityRuntimePath(id));
        PostUnavailable(known);
    }

    // Through the pump, after the unload: a state the stopped instance posted is replaced, never sent after this one.
    void PostUnavailable(IEnumerable<LiveEntity> entities)
    {
        foreach (var entity in entities)
            _userScriptManager.Pump.Post(entity.UniqueId, new JsonObject
                { ["uniqueId"] = entity.UniqueId, ["available"] = false, ["values"] = new JsonObject(), ["attributes"] = new JsonObject() });
    }

    static DefinitionBody DefinitionBody(JsonObject request) =>
        ApiRequest.Object(request, "definition").Deserialize<DefinitionBody>(VisualStore.Json) ?? throw new ArgumentException("definition is required");

    static object PreviewJson(DefinitionPreview preview) => new { valid = preview.Valid, diagnostics = preview.Diagnostics, source = preview.Source };

    /// <summary>
    /// The entity definitions' JSON API, answered in the envelope <see cref="VisualRequest"/> uses: <c>types</c>,
    /// <c>list</c>, <c>read</c>, <c>preview</c>, <c>save</c>, <c>set_enabled</c> and <c>delete</c>. Saving or turning on a
    /// definition runs it; turning it off or deleting it unloads it.
    /// </summary>
    /// <param name="requestJson">The request: <c>operation</c> and that operation's fields.</param>
    /// <returns><c>{version, ok: true, result}</c>, or <c>{version, ok: false, error: {code, detail}}</c>.</returns>
    public async Task<string> EntityRequest(string requestJson)
    {
        await _visualGate.WaitAsync();
        try
        {
            var request = ApiRequest.Parse(requestJson);
            var operation = ApiRequest.String(request, "operation");
            object result;
            // One read of the typed service calls for the whole request, as VisualRequest does.
            var services = _diagnosticsProvider.ServiceCalls();
            var libraries = SavedLibraries(services);
            var snapshot = libraries.Preview.Snapshot;
            var store = EntityDocuments;
            if (operation == "types")
                result = new { types = snapshot.EntityTypes.Values.OrderBy(t => t.Id, StringComparer.Ordinal).ToArray(), generation = libraries.Generation };
            else if (operation == "preview") result = PreviewJson(new DefinitionGenerator().Preview(DefinitionBody(request), snapshot));
            // Listing omits the generated source and its map; "read" returns them.
            else if (operation == "list") result = store.List().Select(document =>
            {
                var listed = EntityJson(document);
                listed.Remove("source"); listed.Remove("sourceMap");
                return listed;
            }).ToArray();
            else
            {
                var id = ApiRequest.String(request, "id");
                if (operation == "read") result = EntityJson(store.Read(id) ?? throw new FileNotFoundException("Entity definition not found"));
                else if (operation == "set_enabled")
                {
                    var enabled = ApiRequest.Bool(request, "enabled");
                    var document = store.SetEnabled(id, enabled);
                    // Turned on, it runs the libraries as saved now, never the code its file embeds from before; one not
                    // valid against them records why and does not load. One whose saved revision already runs is left as it is.
                    var valid = true;
                    if (enabled)
                        try { document = Regenerated(document, snapshot, libraries.Generation); StoreRegenerated(document); }
                        catch (DefinitionValidationException ex) { RecordLoadError(EntityRuntimePath(id), ex); valid = false; }
                    if (!enabled || valid && !EntityJson(document)["currentRevisionActive"]!.GetValue<bool>()) await ActivateDefinition(document, services);
                    result = new { id, enabled = document.Enabled };
                }
                else
                {
                    var revision = ApiRequest.Long(request, "expectedRevision", "the revision this replaces, 0 for a new definition");
                    if (operation == "save")
                    {
                        var document = store.Save(id, DefinitionBody(request), revision, snapshot, libraries.Generation, ApiRequest.OptionalBool(request, "enabled"));
                        result = await ActivateDefinition(document, services);
                    }
                    else if (operation == "delete")
                    {
                        store.Delete(id, revision);
                        await _userScriptManager.UnloadScriptAsync(EntityRuntimePath(id));
                        ClearLoadError(EntityRuntimePath(id));
                        // Whether it ran or not: Home Assistant may still have its entities from an earlier run. Its last
                        // states go out first, so none arrives after the entities are gone.
                        await _userScriptManager.Pump.FlushAsync();
                        // Bounded: every request waits behind this one, and Python may never answer.
                        try { await PyInterop.RemoveEntitiesAsync(id).WaitAsync(RemovalTimeout); }
                        catch (Exception ex)
                        {
                            var reason = ex is TimeoutException ? $"Home Assistant did not answer within {RemovalTimeout.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s" : ex.Message;
                            try { Logger.Error($"Cannot remove the entities of deleted entity definition {id} from Home Assistant: {reason}"); } catch { }
                        }
                        result = new { deleted = true };
                    }
                    else throw new ArgumentException("Unknown entity operation");
                }
            }
            return JsonSerializer.Serialize(new { version = 1, ok = true, result }, VisualStore.Json);
        }
        catch (IOException ex) when (ex is not FileNotFoundException) { return Error("storage", ex.Message); }
        catch (UnauthorizedAccessException ex) { return Error("storage", ex.Message); }
        catch (DefinitionValidationException ex) { return Error("validation", PreviewJson(ex.Preview)); }
        catch (VisualConflictException ex) { return Error("conflict", ex.Message); }
        catch (FileNotFoundException ex) { return Error("not_found", ex.Message); }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or OverflowException)
        { return Error("invalid_request", ex.Message); }
        // Every field is read through ApiRequest, so a null reference here is HassSharp's own defect, not the request's.
        catch (NullReferenceException ex) { return InternalError("Entity", ex); }
        finally { _visualGate.Release(); }
    }

    /// <summary>
    /// Runs a command Home Assistant sent to an entity of a running definition, such as <c>turn_on</c>, with the user's
    /// handler. Never waits for the API: a handler may take long or use the API itself.
    /// </summary>
    /// <param name="uniqueId">The entity's unique id, <c>&lt;definition id&gt;:&lt;key&gt;</c>.</param>
    /// <param name="command">The command, such as <c>turn_on</c>.</param>
    /// <param name="dataJson">The service data as a JSON object; null or empty for none.</param>
    /// <exception cref="InvalidOperationException">The entity is not running.</exception>
    /// <exception cref="NotSupportedException">The entity has no such command.</exception>
    public async Task EntityCommand(string uniqueId, string command, string? dataJson)
    {
        if (!_userScriptManager.Entities.TryGet(uniqueId, out var host, out var entity)) throw new InvalidOperationException("The entity is not running");
        using var data = JsonDocument.Parse(string.IsNullOrEmpty(dataJson) ? "{}" : dataJson);
        // A copy, so a handler may keep it after this returns.
        await host.Command(entity, command, data.RootElement.Clone());
    }
}
