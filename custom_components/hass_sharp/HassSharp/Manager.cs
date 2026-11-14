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
    void RecordLoadError(string path, Exception error)
    {
        var problems = error switch
        {
            CompilationErrorException compilation => compilation.Errors.Length,
            VisualValidationException visual => visual.Preview.Diagnostics.Count(d => d.Severity >= 3),
            _ => 0,
        };
        if (problems > 0) _loadProblems[path] = problems; else _loadProblems.TryRemove(path, out _);
        _loadErrors[path] = error is CompilationErrorException compiled
            ? string.Join("\n", compiled.Errors) : error is VisualValidationException invalid
                ? string.Join("\n", invalid.Preview.Diagnostics.Select(d => $"{d.NodeId ?? "graph"}: {d.Message}"))
                : error.GetBaseException().Message;
    }

    public HassSharpManager(string hassConfigPath)
    {
        HassPath.HassConfiguration = hassConfigPath;
        _compiler = new(_diagnosticsProvider);
        _history = new(Path.Combine(hassConfigPath, ".storage", "hass_sharp", "runs.db"));
        _serviceCallSchema = new(Path.Combine(hassConfigPath, ".storage", "hass_sharp", "service_schema.json"));
        _userScriptManager = new() { History = _history };
    }

    public async Task Init()
    {
        await _visualGate.WaitAsync();
        try
        {
            if (_languageLifetime.IsCancellationRequested) _languageLifetime = new();
            // Services appear gradually while Home Assistant starts: the persisted schema keeps node code compiling.
            RefreshServiceCalls();
            var libraries = SavedLibraries();
            _loadErrors.Clear();
            ReportLegacyUserScripts();
            List<CompiledUserScript> assemblies = [];
            var store = VisualDocuments;
            foreach (var document in store.List())
            {
                try
                {
                    var preview = new VisualGenerator(_diagnosticsProvider).Preview(document.Graph, libraries.Preview.Snapshot);
                    if (!preview.Valid) throw new VisualValidationException(preview);
                    assemblies.Add(await CompileVisualDocument(document with { Graph = preview.Graph ?? document.Graph, Source = preview.Source, SourceMap = preview.SourceMap, CatalogGeneration = libraries.Generation, EmbeddedCatalog = preview.EmbeddedCatalog }));
                    // Store the regenerated source so the running revision is recognised as current.
                    try { store.RefreshDerived(document.Id, document.Revision, preview.Source, preview.SourceMap, libraries.Generation); }
                    catch (Exception ex) { Logger.Error($"Cannot refresh stored source of visual document {document.Id}: {ex}"); }
                }
                catch (Exception ex)
                {
                    RecordLoadError(VisualRuntimePath(document.Id), ex);
                    Logger.Error($"Cannot load visual document {document.Id}: {ex}");
                    if (ex is CompilationErrorException compilation) Logger.Error(string.Join("\n", compilation.Errors));
                }
            }

            await _userScriptManager.UnloadUserScriptsAsync(CancelReasons.Reloaded); // Just in case, shouldnt be needed

            if (assemblies.Count == 0)
                Logger.Info("No automations found on initialization");

            await _userScriptManager.LoadUserScripts(assemblies, RecordLoadError);
            _userScriptManager.DependencyTracking.Debug();
        }
        finally { _visualGate.Release(); }
    }

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
            if (script?.CompiledScript.VisualRevision == null) throw new InvalidOperationException("Automation is not active");
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
        for (var current = Volatile.Read(ref _serviceDescriptions); current.Sequence < sequence;)
        {
            var seen = Interlocked.CompareExchange(ref _serviceDescriptions, next, current);
            if (ReferenceEquals(seen, current)) break;
            current = seen;
        }
        try { Logger.Debug($"Received {next.Catalog.Count} Home Assistant service descriptions"); } catch { }
        try { RefreshServiceCalls(); }
        catch (Exception ex) { try { Logger.Error($"Cannot regenerate the typed service calls: {ex}"); } catch { } }
    }

    readonly ServiceCallSchema _serviceCallSchema;
    readonly object _serviceCallsGate = new();

    /// <summary>
    /// Regenerates <c>context.Services</c> from the live schema united with the one it was last generated from (persisted,
    /// so Init compiles node code before Home Assistant has registered every service); a service present in both uses the
    /// live definition. A service that is not live is forgotten only by a push made once Home Assistant has started.
    /// Compilations made earlier, and the automations running from them, are not touched: their calls go by service id.
    /// </summary>
    void RefreshServiceCalls()
    {
        // Read inside the lock: a refresh never applies a push older than one an earlier refresh applied.
        lock (_serviceCallsGate)
        {
            var live = Volatile.Read(ref _serviceDescriptions);
            if (_serviceCallSchema.Update(live.Catalog, live.Started) is { } services) _diagnosticsProvider.UseServiceSchema(services);
        }
    }

    // An entry unload is a reload or removal unless Home Assistant is stopping; runs it cancels record which.
    static string UnloadReason(bool stopping) => stopping ? CancelReasons.Stopping : CancelReasons.Reloaded;
    public void Unload(bool stopping = false) { _languageLifetime.Cancel(); _userScriptManager.UnloadUserScripts(UnloadReason(stopping)); }
    public Task UnloadAsync(bool stopping = false) { _languageLifetime.Cancel(); return _userScriptManager.UnloadUserScriptsAsync(UnloadReason(stopping)); }

    /// <summary>
    /// Home Assistant is stopping (it does not unload config entries then): cancel every running and later
    /// run with that reason. Scripts stay loaded; a later unload still cleans them up.
    /// </summary>
    public void CancelRunsForShutdown() => _userScriptManager.CancelRuns(CancelReasons.Stopping);

    public Task OnTrackedEntityChange(string entityId, HasEntityState newState, HasEntityState? oldState)
    {
        newState.OldState = oldState;
        var trigger = new TriggerContext(newState);
        return _userScriptManager.RunEntries(entityId, trigger);
    }
}
