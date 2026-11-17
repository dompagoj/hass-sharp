using System.Text.Json;

namespace HassSharp;

public partial class HassSharpManager
{
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
    /// and when the files cannot be listed nothing is pruned. Without the definitions' directory nothing is pruned either:
    /// a fresh install has nothing to prune, and a partly restored <c>.storage</c> must not lose every entity's registry
    /// entry (an empty directory still prunes them all: its definitions were deleted).
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
            await PyInterop.PruneEntitiesAsync(JsonSerializer.Serialize(store.Ids())).WaitAsync(RemovalTimeout);
        }
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
    /// <returns>Each one's activation answer, as <see cref="ActivateDefinition"/> gives it.</returns>
    async Task<List<object>> ReactivateDefinitions(DefinitionRebuild rebuild)
    {
        var answers = new List<object>();
        foreach (var document in rebuild.Rebuilt.Where(d => d.Enabled)) answers.Add(await ActivateDefinition(document));
        return answers;
    }
}
