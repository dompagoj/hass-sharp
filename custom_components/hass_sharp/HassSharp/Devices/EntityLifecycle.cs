using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

public partial class HassSharpManager
{
    /// <summary>
    /// At startup: each enabled saved definition, regenerated against the saved libraries and compiled, for the loader to
    /// load with the automations, its regenerated source stored. One that does not validate or compile is recorded and
    /// warned of as any definition that cannot start (<see cref="WarnCannotStart"/>); one that fails otherwise is recorded
    /// and logged as an error; the others are compiled. An unreadable file is logged and skipped. A definition that is off is never compiled: only its
    /// source is regenerated and stored, when it is valid, so turning it on later runs the libraries as saved.
    /// </summary>
    /// <returns>Each definition compiled, with the document it was compiled from, which says why it does not start.</returns>
    async Task<List<(CompiledUserScript Compiled, EntityDefinitionDocument Document)>> CompileSavedDefinitions(VisualCatalogSnapshot snapshot, string generation)
    {
        List<(CompiledUserScript, EntityDefinitionDocument)> compiled = [];
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
                // Stored before the compile, which may fail: what is stored is what the libraries as saved generate.
                StoreRegenerated(current);
                compiled.Add((await CompileDefinition(current, null), current));
            }
            catch (Exception ex) when (ex is DefinitionValidationException or CompilationErrorException)
            {
                // Neither is a HassSharp bug: the one warning of a definition that cannot start says why, in its load error's words.
                RecordDefinitionLoadError(current, ex);
                WarnCannotStart(current, _loadErrors.GetValueOrDefault(EntityRuntimePath(document.Id)));
            }
            catch (Exception ex)
            {
                ReportLoadFailure(document.Id, () => { RecordDefinitionLoadError(current, ex); return DefinitionLoadFailure(current, ex); }, "entity definition");
                try { Logger.Debug($"Loading entity definition {document.Id} failed: {ex}"); } catch { }
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
            // A graph's problems read as its load error does, each after its node; a class's are its messages.
            var problems = document.Logic.Kind == "graph" ? GraphProblems(invalid.Preview.Diagnostics)
                : invalid.Preview.Diagnostics.Where(d => d.Severity >= 3).Select(d => d.Message).ToArray();
            return $"Cannot load entity definition {name}: {(problems.Length == 1 ? "1 problem" : $"{problems.Length} problems")}: {Listed(problems)}";
        }
        if (error is CompilationErrorException compilation)
            return $"Cannot load entity definition {name}: its code does not compile: {Listed([.. compilation.Errors.Select(e => Located(document, e))])}";
        return $"Cannot load entity definition {name}: {Capped(Reason(error))}";
    }

    /// <summary>
    /// One compiler error of a definition's code as a person reads it: "path(line,column): error CS1002: ; expected" is
    /// the message, its code, and the node library that line is in; never the generated file's path.
    /// </summary>
    static string Located(EntityDefinitionDocument document, string text)
    {
        var match = CompilerError.Match(text);
        if (!match.Success) return text;
        var line = int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var library = document.SourceMap.FirstOrDefault(m => line >= m.StartLine && line <= m.EndLine)?.NodeId?["$library:".Length..];
        return $"{match.Groups["message"].Value} ({match.Groups["code"].Value}{(library == null ? "" : ", node library " + library)})";
    }

    /// <summary>
    /// Tells Home Assistant which definitions exist, by the name of each stored file, readable or not, so it removes the
    /// entities of every other one. Waits no longer than <see cref="RemovalTimeout"/>; a failure is logged, never thrown,
    /// and when the files cannot be listed nothing is pruned. Without the definitions' directory nothing is pruned either:
    /// a fresh install has nothing to prune, and a partly restored <c>.storage</c> must not lose every entity's registry
    /// entry (an empty directory still prunes them all: its definitions were deleted). The last states remembered for the
    /// entities of every other definition are dropped alike (<see cref="EntityStateStore"/>).
    /// </summary>
    async Task PruneEntities()
    {
        var store = EntityDocuments;
        try
        {
            if (!store.Exists())
            {
                try { Logger.Info("No entity definitions directory: nothing is pruned from Home Assistant"); } catch { }
                return;
            }
            var ids = store.Ids();
            // The last states of entities no definition has are dropped too; a unique id is "<definition id>:<key>".
            if (_userScriptManager.States is { } states)
            {
                var known = ids.ToHashSet(StringComparer.Ordinal);
                states.Forget(states.Ids().Where(unique => !known.Contains(unique[..Math.Max(unique.IndexOf(':'), 0)])));
            }
            await PyInterop.PruneEntitiesAsync(JsonSerializer.Serialize(ids)).WaitAsync(RemovalTimeout);
        }
        catch (Exception ex)
        {
            var reason = ex is TimeoutException ? $"Home Assistant did not answer within {RemovalTimeout.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s" : ex.Message;
            try { Logger.Error($"Cannot prune the entities of entity definitions that no longer exist from Home Assistant: {reason}"); } catch { }
        }
    }

    /// <summary>
    /// After the typed service calls or the entity classes changed: loads again each enabled definition whose last load
    /// failed, that a push may cure, and that is not loaded, without waiting for its start. One that fails again warns as
    /// a save does, once per reason (<see cref="WarnCannotStart"/>); one that loads forgets its reason, so that the same
    /// failure later warns again. Runs under the visual gate.
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
                var error = await LoadDefinition(current);
                WarnCannotStart(current, error);
                if (error == null) Logger.Info($"Entity definition {document.Id} loaded now that the typed service calls or entity classes changed");
            }
            catch (Exception ex)
            {
                RecordDefinitionLoadError(document, ex);
                WarnCannotStart(document, _loadErrors.GetValueOrDefault(path));
            }
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
        var generator = DefinitionGeneratorWith(null);
        var all = store.List(strict: true).Select(d => (Document: d, Preview: generator.Preview(d.Body, candidate))).ToArray();
        var invalidBefore = all.Where(p => !p.Preview.Valid && !generator.Preview(p.Document.Body, saved).Valid).Select(p => p.Document.Id).ToHashSet(StringComparer.Ordinal);
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
    /// <param name="snapshot">The libraries the definitions were rebuilt with, which say which of a class's settings are secret.</param>
    /// <returns>
    /// Every rebuilt definition's answer, on or off, with <c>enabled</c>: one that is on as <see cref="ActivateDefinition"/>
    /// answers it, one that is off not active and with no error; each a secret setting without its value (<see cref="WithoutSecretValues"/>).
    /// </returns>
    async Task<List<object>> ReactivateDefinitions(DefinitionRebuild rebuild, VisualCatalogSnapshot snapshot)
    {
        var answers = new List<object>();
        foreach (var document in rebuild.Rebuilt)
        {
            if (!document.Enabled)
            {
                answers.Add(new { id = document.Id, saved = true, document = EntityJson(document, snapshot), active = false, activationError = (string?)null, enabled = false });
                continue;
            }
            var (json, active, activationError) = await Activate(document, null, snapshot);
            answers.Add(new { id = document.Id, saved = true, document = json, active, activationError, enabled = true });
        }
        return answers;
    }

    /// <summary>
    /// Answers each setting of the document that may be secret without its value: <c>value</c> null, <c>secret</c> true
    /// and <c>hasValue</c>, whether one is stored; its other fields as they are. Only a setting known not to be secret
    /// keeps its value: a graph's setting the document does not mark <c>secret</c>, a class's setting its class declares
    /// and does not mark secret (<see cref="SettingAttribute.Secret"/>). A class's setting its class does not declare, and
    /// every one when the libraries do not compile or no longer declare the class, may be secret for all that is known.
    /// The answers reach clients, the panel and an assistant over MCP, that have no business with them; a save sends such
    /// a setting back with <c>keep</c>, or as answered, to leave its value as stored (<see cref="DefinitionSetting.Keep"/>).
    /// </summary>
    /// <param name="snapshot">The libraries the definition is answered against.</param>
    static void WithoutSecretValues(JsonObject json, EntityDefinitionDocument document, VisualCatalogSnapshot snapshot)
    {
        // A graph's key is open only if no setting with it is secret: a file edited by hand may hold a key twice.
        var open = (document.Logic.Kind == "graph" ? document.Settings.GroupBy(s => s.Key, StringComparer.Ordinal).Where(g => g.All(s => !s.Secret)).Select(g => g.Key)
            : !snapshot.LibraryDiagnostics.Any(d => d.Severity == 3) && document.Logic.Type is { } type && snapshot.EntityTypes.TryGetValue(type, out var described)
                ? described.Settings.Where(s => !s.Secret).Select(s => s.Key)
            : []).ToHashSet(StringComparer.Ordinal);
        foreach (var setting in (json["settings"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (setting["key"] is JsonValue key && key.TryGetValue<string>(out var name) && open.Contains(name)) continue;
            var hasValue = setting["value"] is { } value && value.GetValueKind() != JsonValueKind.Null;
            setting["value"] = null;
            setting["secret"] = true;
            setting["hasValue"] = hasValue;
        }
    }
}
