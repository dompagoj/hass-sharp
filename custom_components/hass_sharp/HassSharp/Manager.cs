using System.Text.Json.Nodes;

namespace HassSharp;

public partial class HassSharpManager
{
    readonly DiagnosticsProvider _diagnosticsProvider = new();
    readonly CodeCompiler _compiler;
    readonly UserScriptManager _userScriptManager;
    readonly RunHistoryStore _history;
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _loadErrors = new();
    // How many problems a load error names, when it names them (compile errors, graph errors); read only beside _loadErrors.
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _loadProblems = new();
    // The load errors a later service or entity push may cure, the only ones it retries: all but a graph error with no line of code.
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _curableLoadErrors = new();
    void RecordLoadError(string path, Exception error)
    {
        var problems = error switch
        {
            CompilationErrorException compilation => compilation.Errors.Length,
            VisualValidationException visual => visual.Preview.Diagnostics.Count(d => d.Severity >= 3),
            DefinitionValidationException definition => definition.Preview.Diagnostics.Count(d => d.Severity >= 3),
            _ => 0,
        };
        if (problems > 0) _loadProblems[path] = problems; else _loadProblems.TryRemove(path, out _);
        // A push that brings services comes as Home Assistant starts: code that did not compile against a service not
        // there yet, a Home Assistant trigger whose integration was not set up yet, or a storage failure may load now. A
        // graph error that names no line of code is the graph's own and stays until it is fixed and saved.
        if (error is not VisualValidationException { Preview.Diagnostics: var diagnostics } || diagnostics.Any(d => d.Severity >= 3 && d.StartLine != null))
            _curableLoadErrors[path] = 0;
        else _curableLoadErrors.TryRemove(path, out _);
        _loadErrors[path] = error is CompilationErrorException compiled
            ? string.Join("\n", compiled.Errors) : error is VisualValidationException invalid
                ? string.Join("\n", invalid.Preview.Diagnostics.Select(d => $"{d.NodeId ?? "graph"}: {d.Message}"))
                : error is DefinitionValidationException rejected ? string.Join("\n", rejected.Preview.Diagnostics.Select(d => d.Message))
                : error.GetBaseException().Message;
    }

    public HassSharpManager(string hassConfigPath)
    {
        HassPath.HassConfiguration = hassConfigPath;
        // Before any automation loads: otherwise Python.NET keeps every automation assembly, and each reload's memory.
        PythonNetAssemblies.IgnoreCollectible();
        _compiler = new(_diagnosticsProvider);
        _history = new(Path.Combine(hassConfigPath, ".storage", "hass_sharp", "runs.db"));
        _serviceCallSchema = new(Path.Combine(hassConfigPath, ".storage", "hass_sharp", "service_schema.json"));
        _entitySchema = new(Path.Combine(hassConfigPath, ".storage", "hass_sharp", "entity_schema.json"), "entity");
        // One state pump carries every entity definition's states to Home Assistant; one store keeps those they come back with.
        _userScriptManager = new() { History = _history, Pump = new(), States = new(EntityStatesPath) };
    }

    public async Task Init()
    {
        await _visualGate.WaitAsync();
        try
        {
            if (_languageLifetime.IsCancellationRequested) _languageLifetime = new();
            PythonNetAssemblies.IgnoreCollectible(Logger.Warn);
            // Services appear gradually while Home Assistant starts: the persisted schema keeps node code compiling.
            // Whatever goes wrong there leaves the previous (or empty) typed calls: setup goes on without them.
            try { RefreshServiceCalls(); }
            catch (Exception ex) { try { Logger.Error($"Cannot generate the typed service calls; node code compiles without the new ones: {ex}"); } catch { } }
            // The entity classes alike: the persisted schema keeps node code that names them compiling.
            try { RefreshEntities(); }
            catch (Exception ex) { try { Logger.Error($"Cannot generate the entity classes; node code compiles without the new ones: {ex}"); } catch { } }
            var libraries = SavedLibraries();
            _loadErrors.Clear(); _curableLoadErrors.Clear();
            ReportLegacyUserScripts();
            List<CompiledUserScript> assemblies = [];
            var store = VisualDocuments;
            foreach (var document in store.List())
            {
                VisualPreview? preview = null;
                try
                {
                    preview = new VisualGenerator(_diagnosticsProvider).Preview(document.Graph, libraries.Preview.Snapshot);
                    if (!preview.Valid) throw new VisualValidationException(preview);
                    assemblies.Add(await CompileVisualDocument(document with { Graph = preview.Graph ?? document.Graph, Source = preview.Source, SourceMap = preview.SourceMap, CatalogGeneration = libraries.Generation, EmbeddedCatalog = preview.EmbeddedCatalog }));
                    // Store the regenerated source so the running revision is recognised as current.
                    try { store.RefreshDerived(document.Id, document.Revision, preview.Source, preview.SourceMap, libraries.Generation); }
                    catch (Exception ex) { Logger.Error($"Cannot refresh stored source of visual document {document.Id}: {ex}"); }
                }
                catch (Exception ex)
                {
                    // The source map of the code that was compiled names the step of a compiler error.
                    var compiled = preview is { Source.Length: > 0 } ? document with { Graph = preview.Graph ?? document.Graph, SourceMap = preview.SourceMap } : document;
                    ReportLoadFailure(document.Id, () => { RecordLoadError(VisualRuntimePath(document.Id), ex); return LoadFailure(compiled, ex, libraries.Preview.Snapshot); });
                    // Neither a graph that does not validate nor code that does not compile is a HassSharp bug; anything else is.
                    if (ex is not (VisualValidationException or CompilationErrorException)) Logger.Debug($"Loading visual document {document.Id} failed: {ex}");
                }
            }
            var definitions = await CompileSavedDefinitions(libraries.Preview.Snapshot, libraries.Generation);
            // Read before any definition prepares, so that none waits for the disk.
            _userScriptManager.States?.Load();

            await _userScriptManager.UnloadUserScriptsAsync(CancelReasons.Reloaded); // Just in case, shouldnt be needed

            if (assemblies.Count == 0)
                Logger.Info("No automations found on initialization");

            await _userScriptManager.LoadUserScripts([.. assemblies, .. definitions], RecordLoadError);
            _userScriptManager.DependencyTracking.Debug();
            await PruneEntities();
        }
        finally { _visualGate.Release(); }
    }

    /// <summary>The .NET side of the debug memory line (<see cref="RuntimeMemory"/>); forces a full collection.</summary>
    public string MemoryReport() => RuntimeMemory.Report();

    /// <summary>How many problems the startup log names for an automation that does not load; the rest are counted.</summary>
    internal const int LoggedProblems = 3;

    /// <summary>The longest a single problem is in the startup log line; a longer one is cut with an ellipsis.</summary>
    internal const int LoggedProblemLength = 200;

    /// <summary>
    /// Logs why an automation does not load, as <paramref name="describe"/> says it. Whatever goes wrong while describing
    /// it is logged in a plain line instead: it never stops the automations after this one from loading.
    /// </summary>
    /// <param name="what">What does not load, as the plain line names it: an automation, or an entity definition.</param>
    internal static void ReportLoadFailure(string id, Func<string> describe, string what = "automation")
    {
        string line;
        try { line = describe(); }
        catch (Exception ex)
        {
            line = $"Cannot load {what} {id}; see the debug log for why";
            try { Logger.Debug($"Cannot describe why {what} {id} does not load: {ex}"); } catch { }
        }
        try { Logger.Error(line); } catch { }
    }

    /// <summary>
    /// The startup log line for a saved automation that does not load: its name and id, then why, as a person can act on
    /// it: the first problems of a graph that does not validate (each message and the step it is on), the first compiler
    /// errors, or the error's message. Never the exception's type or stack.
    /// </summary>
    internal static string LoadFailure(VisualDocument document, Exception error, VisualCatalogSnapshot snapshot)
    {
        var name = AutomationName(document) is { } named ? $"\"{named}\" ({document.Id})" : document.Id;
        var nodes = (document.Graph["nodes"] as JsonArray ?? []).OfType<JsonObject>()
            .GroupBy(n => VisualNodeCatalog.Text(n, "id"), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        string? Step(string? id) => id != null && nodes.TryGetValue(id, out var node)
            ? VisualGraphValidator.Words.StepName(node, snapshot.Root["nodes"] is JsonObject kinds ? kinds[VisualNodeCatalog.Text(node, "kind")] : null) : null;
        if (error is VisualValidationException invalid)
        {
            string Where(VisualDiagnostic diagnostic) => diagnostic.NodeId switch
            {
                { } id when id.StartsWith("$library:", StringComparison.Ordinal) => $" (node library {id["$library:".Length..]})",
                { } id when Step(id) is { } step => $" (step {step})",
                _ => "",
            };
            var problems = invalid.Preview.Diagnostics.Where(d => d.Severity >= 3).Select(d => d.Message + Where(d)).ToArray();
            return $"Cannot load automation {name}: {(problems.Length == 1 ? "1 problem" : $"{problems.Length} problems")}: {Listed(problems)}";
        }
        if (error is CompilationErrorException compilation)
        {
            // "path(line,column): error CS1002: ; expected": the message, its code, and the step whose code is on that line.
            string Located(string text)
            {
                var match = CompilerError.Match(text);
                if (!match.Success) return text;
                var line = int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
                var step = Step(document.SourceMap.FirstOrDefault(m => line >= m.StartLine && line <= m.EndLine)?.NodeId);
                return $"{match.Groups["message"].Value} ({match.Groups["code"].Value}{(step == null ? "" : ", step " + step)})";
            }
            return $"Cannot load automation {name}: its code does not compile: {Listed([.. compilation.Errors.Select(Located)])}";
        }
        return $"Cannot load automation {name}: {Capped(error.GetBaseException().Message)}";
    }

    /// <summary>One problem as the startup log names it: cut with an ellipsis past <see cref="LoggedProblemLength"/>.</summary>
    static string Capped(string problem)
    {
        if (problem.Length <= LoggedProblemLength) return problem;
        var length = LoggedProblemLength - 1;
        if (char.IsHighSurrogate(problem[length - 1])) length--;
        return problem[..length].TrimEnd() + "…";
    }

    /// <summary>The first <see cref="LoggedProblems"/> problems, each <see cref="Capped"/>, and how many more there are.</summary>
    static string Listed(IReadOnlyCollection<string> problems) =>
        string.Join("; ", problems.Take(LoggedProblems).Select(Capped)) + (problems.Count > LoggedProblems ? $"; and {problems.Count - LoggedProblems} more" : "");

    static readonly System.Text.RegularExpressions.Regex CompilerError = new(@"\((?<line>\d+),\d+\): error (?<code>[A-Z]+\d+): (?<message>.*)$",
        System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // Hand-written automations used to live here. They are no longer loaded, and the files are never touched.
    static string LegacyUserScripts => Path.Join(HassPath.HassSharpIntegration, "user_scripts");

    static void ReportLegacyUserScripts()
    {
        try
        {
            if (Directory.Exists(LegacyUserScripts) && Directory.EnumerateFiles(LegacyUserScripts, "*.cs").Any())
                Logger.Info("user_scripts is no longer used: automations are visual only. The files there were left as they are.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <param name="statuses">Only runs with one of these statuses; null or empty for every run.</param>
    public string GetRunHistory(string automationId, int offset, int limit, string[]? statuses) =>
        System.Text.Json.JsonSerializer.Serialize(new { version = 2, offset, limit,
            runs = _history.Read(automationId, offset, limit, statuses).Select(RunJson) }, HistoryJson);

    /// <summary>Contract C9 <c>run_trace</c>, or JSON <c>null</c> for an unknown run.</summary>
    public string GetRunTrace(string runId)
    {
        if (_history.ReadRun(runId) is not { } run) return "null";
        var snapshot = run.VisualId == null ? null : _history.ReadSnapshot(run.Revision);
        var trace = run.HasTrace ? _history.ReadTrace(runId) : null;
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 1, run = RunJson(run), live = run.Status is "Pending" or "Running",
            // The row is keyed by generated source, which holds neither the document id nor its revision: identity comes from the run.
            snapshot = snapshot == null ? null : new { revision = snapshot.Revision, visualId = run.VisualId, visualRevision = run.VisualRevision,
                graph = System.Text.Json.Nodes.JsonNode.Parse(snapshot.Graph), catalog = System.Text.Json.Nodes.JsonNode.Parse(snapshot.Catalog) },
            trace = trace == null ? null : System.Text.Json.Nodes.JsonNode.Parse(trace.Json),
        }, HistoryJson);
    }

    // Contract C9 run shape; "actionLabel" is the trigger node label, else the method name.
    static object RunJson(InvocationRecord run) => new
    {
        run.RunId, run.AutomationId, run.Revision, run.Action,
        ActionLabel = run.TriggerLabel ?? run.Action[(run.Action.LastIndexOf("::", StringComparison.Ordinal) is var at and >= 0 ? at + 2 : 0)..],
        run.TriggerNodeId, run.VisualId, run.VisualRevision, run.Cause,
        Trigger = run.Trigger == null ? null : System.Text.Json.Nodes.JsonNode.Parse(run.Trigger),
        run.PlannedUtc, run.ReceivedUtc, run.StartUtc, run.EndUtc, run.DurationMs, run.Status, run.Error, run.HasTrace,
    };

    static readonly System.Text.Json.JsonSerializerOptions HistoryJson = new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    public string RunNow(string automationId, string className, string method)
    {
        _visualGate.Wait();
        try
        {
            var script = _userScriptManager.GetUserScripts().FirstOrDefault(s => s.AutomationId == automationId);
            // An entity definition's script carries its revision too, but it is no automation.
            if (script?.CompiledScript.VisualRevision == null || script.CompiledScript.Definition != null) throw new InvalidOperationException("Automation is not active");
            // Identify visuals from the active compiled metadata, not a best-effort
            // document listing: missing/corrupt saved files must fail closed.
            var id = Path.GetFileNameWithoutExtension(script.CompiledScript.FilePath)["visual_".Length..];
            var document = VisualDocuments.Read(id) ?? throw new InvalidOperationException("Saved visual document not found");
            if (className != "VisualAutomation") throw new ArgumentException("Invalid visual class");
            var trigger = document.Graph["nodes"]!.AsArray().FirstOrDefault(n => n?["kind"]?.GetValue<string>() == "trigger" && VisualNodeCatalog.EffectiveTriggerMethod(n!.AsObject()) == method)
                ?? throw new ArgumentException("Trigger not found");
            return System.Text.Json.JsonSerializer.Serialize(DispatchVisual(document, trigger["id"]!.GetValue<string>()));
        }
        finally { _visualGate.Release(); }
    }

    sealed record ServiceDescriptionsState(long Sequence, ServiceSchemaCatalog Catalog, bool Started);
    ServiceDescriptionsState _serviceDescriptions = new(0, ServiceSchemaCatalog.Empty, false);
    long _serviceDescriptionsSequence;
    internal ServiceSchemaCatalog ServiceSchemas => Volatile.Read(ref _serviceDescriptions).Catalog;

    /// <summary>
    /// Receives HA's raw <c>{domain: {service: description}}</c> from a Python executor thread, at any
    /// time: before or during <see cref="Init"/>, repeatedly, or after unload. Normalises outside every
    /// lock, then swaps the immutable result in; a later push always wins over an earlier one that
    /// finishes after it. Malformed input is logged and the previous schemas stay; it never throws.
    /// Then regenerates the typed service calls (<see cref="RefreshServiceCalls"/>).
    /// </summary>
    /// <param name="started">Whether Home Assistant has finished starting: only then may a service missing from this push
    /// be dropped from the typed service calls.</param>
    public void SetServiceDescriptions(string json, bool started = false) => SetServiceDescriptions(json, Interlocked.Increment(ref _serviceDescriptionsSequence), started);

    // The sequence is taken on entry; installing compares it, so ordering is testable deterministically.
    internal void SetServiceDescriptions(string json, long sequence, bool started = false)
    {
        ServiceDescriptionsState next;
        // Logging crosses into Python; the caller must not see a failure from it either.
        try { next = new(sequence, ServiceSchemaCatalog.Parse(json), started); }
        catch (Exception ex)
        {
            try { Logger.Error($"Ignoring Home Assistant service descriptions, keeping the previous ones: {ex.Message}"); } catch { }
            return;
        }
        Publish(ref _serviceDescriptions, next, state => state.Sequence);
        try { Logger.Debug($"Received {next.Catalog.Count} Home Assistant service descriptions"); } catch { }
        try { if (RefreshServiceCalls()) RetryFailedLoads(); }
        catch (Exception ex) { try { Logger.Error($"Cannot regenerate the typed service calls: {ex}"); } catch { } }
    }

    /// <summary>Swaps <paramref name="next"/> in unless a later push (by <paramref name="sequence"/>) is there already.</summary>
    static void Publish<T>(ref T state, T next, Func<T, long> sequence) where T : class
    {
        for (var current = Volatile.Read(ref state); sequence(current) < sequence(next);)
        {
            var seen = Interlocked.CompareExchange(ref state, next, current);
            if (ReferenceEquals(seen, current)) break;
            current = seen;
        }
    }

    /// <summary>Logs, once per schema, that generated code did not compile: left out in part, or not used at all.</summary>
    static void ReportRejected(bool accepted, IReadOnlyList<string> excluded, IReadOnlyList<string> errors, Func<string, string> leftOut, string rejected)
    {
        if (accepted && excluded.Count == 0) return;
        try { Logger.Warn((accepted ? leftOut(string.Join(", ", excluded)) : rejected) + ". This is a HassSharp bug; please report it with these errors:\n" + string.Join("\n", errors)); }
        catch { }
    }

    readonly ServiceCallSchema _serviceCallSchema;
    readonly object _serviceCallsGate = new();

    /// <summary>
    /// Regenerates <c>context.Services</c> from the live schema united with the one it was last generated from (persisted,
    /// so Init compiles node code before Home Assistant has registered every service); a service present in both uses the
    /// live definition. A service that is not live is forgotten only by a push made once Home Assistant has started.
    /// A schema whose calls do not compile is not used and not persisted (<see cref="DiagnosticsProvider.UseServiceSchema(IReadOnlyCollection{KeyValuePair{string, System.Text.Json.Nodes.JsonObject}})"/>);
    /// that is logged once per schema. Compilations made earlier, and the automations running from them, are not
    /// touched: their calls go by service id.
    /// </summary>
    /// <returns>Whether the typed calls every compilation gets were replaced with ones that add or change a service: only
    /// then can an automation that failed to load for a missing service load now.</returns>
    bool RefreshServiceCalls()
    {
        // Read inside the lock: a refresh never applies a push older than one an earlier refresh applied.
        lock (_serviceCallsGate)
        {
            var live = Volatile.Read(ref _serviceDescriptions);
            if (_serviceCallSchema.Update(live.Catalog, live.Started) is not { } services) return false;
            var result = _diagnosticsProvider.UseServiceSchema(services);
            if (result.Accepted) _serviceCallSchema.Accept(result.Services);
            ReportRejected(result.Accepted, result.Excluded, result.Errors,
                excluded => $"The typed service calls of {excluded} do not compile and were left out; node code cannot call them by name",
                "The typed service calls of Home Assistant's services do not compile; node code keeps the previous ones");
            return result.Grew;
        }
    }

    sealed record EntityDescriptionsState(long Sequence, EntitySchemaCatalog Catalog, bool Started);
    EntityDescriptionsState _entityDescriptions = new(0, EntitySchemaCatalog.Empty, false);
    long _entityDescriptionsSequence;

    /// <summary>
    /// Receives Home Assistant's entity components (<c>{"domains": [...], "translations": {...}}</c>) from a Python
    /// executor thread, at any time, as <see cref="SetServiceDescriptions(string, bool)"/> receives the services: a later
    /// push always wins, malformed input is logged and the previous schema stays, and it never throws. Then regenerates
    /// the entity classes (<see cref="RefreshEntities"/>).
    /// </summary>
    /// <param name="started">Whether Home Assistant has finished starting: only then may a domain missing from this push
    /// be dropped from the entity classes.</param>
    public void SetEntityDescriptions(string json, bool started = false)
    {
        var sequence = Interlocked.Increment(ref _entityDescriptionsSequence);
        EntityDescriptionsState next;
        try { next = new(sequence, EntitySchemaCatalog.Parse(json), started); }
        catch (Exception ex)
        {
            try { Logger.Error($"Ignoring Home Assistant's entity components, keeping the previous ones: {ex.Message}"); } catch { }
            return;
        }
        Publish(ref _entityDescriptions, next, state => state.Sequence);
        try { Logger.Debug($"Received the states and attributes of {next.Catalog.Count} Home Assistant entity domains"); } catch { }
        try { if (RefreshEntities()) RetryFailedLoads(); }
        catch (Exception ex) { try { Logger.Error($"Cannot regenerate the entity classes: {ex}"); } catch { } }
    }

    readonly ServiceCallSchema _entitySchema;
    readonly object _entitiesGate = new();

    /// <summary>
    /// Regenerates the entity classes from the live schema united with the one they were last generated from, as
    /// <see cref="RefreshServiceCalls"/> regenerates the typed calls (persisted, so Init compiles node code that names a
    /// class before Home Assistant has pushed them). Compilations made earlier keep the classes they were compiled against.
    /// </summary>
    /// <returns>Whether the classes every compilation gets were replaced with ones that add or change a domain.</returns>
    bool RefreshEntities()
    {
        lock (_entitiesGate)
        {
            var live = Volatile.Read(ref _entityDescriptions);
            if (_entitySchema.Update(live.Catalog.Items, live.Started) is not { } domains) return false;
            var result = _diagnosticsProvider.UseEntitySchema(domains);
            if (result.Accepted) _entitySchema.Accept(result.Domains);
            ReportRejected(result.Accepted, result.Excluded, result.Errors,
                excluded => $"The entity classes of {excluded} do not compile and were left out; node code cannot name them",
                "The entity classes of Home Assistant's domains do not compile; node code keeps the previous ones");
            return result.Grew;
        }
    }

    readonly object _retryGate = new();
    Task _retry = Task.CompletedTask;

    /// <summary>
    /// After the typed service calls changed to add or change a service, or the entity classes to add or change a domain:
    /// tries again to activate each saved automation whose last load failed and that is not loaded (one that did not
    /// compile against a service or an entity class that was missing, or whose Home Assistant trigger
    /// could not attach before its integration was set up, say), except a graph error naming no line of code; then each
    /// enabled entity definition alike. Loaded automations and definitions are left alone. Runs after anything queued
    /// before it, under the same gate as every other activation.
    /// </summary>
    internal Task RetryFailedLoads()
    {
        lock (_retryGate) { RetriesQueued++; return _retry = _retry.ContinueWith(_ => RetryFailedLoadsAsync(), TaskScheduler.Default).Unwrap(); }
    }

    /// <summary>How many retries were queued; for tests.</summary>
    internal int RetriesQueued { get; private set; }

    /// <summary>The retries queued so far, done; for tests.</summary>
    internal Task RetriesDone() { lock (_retryGate) return _retry; }

    async Task RetryFailedLoadsAsync()
    {
        try
        {
            await _visualGate.WaitAsync();
            try
            {
                // Unloaded (or never initialised): nothing may be activated.
                if (_languageLifetime.IsCancellationRequested || _loadErrors.IsEmpty) return;
                var libraries = SavedLibraries();
                var store = VisualDocuments;
                foreach (var document in store.List())
                {
                    var path = VisualRuntimePath(document.Id);
                    if (_languageLifetime.IsCancellationRequested) return;
                    if (!_loadErrors.ContainsKey(path) || !_curableLoadErrors.ContainsKey(path) || _userScriptManager.GetUserScripts().Any(s => s.CompiledScript.FilePath == path)) continue;
                    try
                    {
                        var preview = new VisualGenerator(_diagnosticsProvider).Preview(document.Graph, libraries.Preview.Snapshot);
                        if (!preview.Valid) throw new VisualValidationException(preview);
                        var current = document with { Graph = preview.Graph ?? document.Graph, Source = preview.Source, SourceMap = preview.SourceMap, CatalogGeneration = libraries.Generation, EmbeddedCatalog = preview.EmbeddedCatalog };
                        try { store.RefreshDerived(document.Id, document.Revision, preview.Source, preview.SourceMap, libraries.Generation); }
                        catch (Exception ex) { Logger.Error($"Cannot refresh stored source of visual document {document.Id}: {ex}"); }
                        await ActivateVisualDocument(current);
                        if (!_loadErrors.ContainsKey(path)) Logger.Info($"Visual document {document.Id} loaded now that the typed service calls or entity classes changed");
                    }
                    catch (Exception ex) { RecordLoadError(path, ex); }
                }
                await RetryFailedDefinitions(libraries.Preview.Snapshot, libraries.Generation);
            }
            finally { _visualGate.Release(); }
        }
        catch (Exception ex) { try { Logger.Error($"Cannot retry loading automations after the typed service calls or entity classes changed: {ex}"); } catch { } }
    }

    // An entry unload is a reload or removal unless Home Assistant is stopping; runs it cancels record which.
    static string UnloadReason(bool stopping) => stopping ? CancelReasons.Stopping : CancelReasons.Reloaded;
    // The entity states pending or remembered as sent are dropped with the scripts: after a reload every state is sent
    // anew. The states the entities come back with are written now (a flush never throws).
    public void Unload(bool stopping = false)
    {
        _languageLifetime.Cancel();
        try { _userScriptManager.UnloadUserScripts(UnloadReason(stopping)); }
        finally
        {
            _userScriptManager.Pump.Reset();
            _userScriptManager.States?.FlushAsync().GetAwaiter().GetResult();
        }
    }
    public async Task UnloadAsync(bool stopping = false)
    {
        _languageLifetime.Cancel();
        try { await _userScriptManager.UnloadUserScriptsAsync(UnloadReason(stopping)); }
        finally
        {
            _userScriptManager.Pump.Reset();
            if (_userScriptManager.States is { } states) await states.FlushAsync();
        }
    }

    /// <summary>
    /// Home Assistant is stopping (it does not unload config entries then): cancel every running and later
    /// run with that reason, and write the states the entities come back with. Scripts stay loaded; a later unload
    /// still cleans them up.
    /// </summary>
    public void CancelRunsForShutdown()
    {
        try { _userScriptManager.CancelRuns(CancelReasons.Stopping); }
        finally { _userScriptManager.States?.FlushAsync().GetAwaiter().GetResult(); }
    }

    public Task OnTrackedEntityChange(string entityId, HasEntityState newState, HasEntityState? oldState)
    {
        newState.OldState = oldState;
        var trigger = new TriggerContext(newState);
        return _userScriptManager.RunEntries(entityId, trigger);
    }
}
