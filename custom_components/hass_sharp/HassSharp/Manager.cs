using Python.Runtime;

namespace HassSharp;

public partial class HassSharpManager
{
    readonly DiagnosticsProvider _diagnosticsProvider = new();
    readonly CodeCompiler _compiler;
    readonly UserScriptManager _userScriptManager;
    readonly RunHistoryStore _history;
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _loadErrors = new();
    void RecordLoadError(string path, Exception error) => _loadErrors[path] = error is CompilationErrorException compilation
        ? string.Join("\n", compilation.Errors) : error is VisualValidationException visual
            ? string.Join("\n", visual.Preview.Diagnostics.Select(d => $"{d.NodeId ?? "graph"}: {d.Message}"))
            : error.GetBaseException().Message;

    public HassSharpManager(string hassConfigPath)
    {
        HassPath.HassConfiguration = hassConfigPath;
        _compiler = new(_diagnosticsProvider);
        _history = new(Path.Combine(hassConfigPath, ".storage", "hass_sharp", "runs.db"));
        _userScriptManager = new() { History = _history };
    }

    public async Task Init(string[] hassEntityIds)
    {
        await _visualGate.WaitAsync();
        try
        {
            if (_languageLifetime.IsCancellationRequested) _languageLifetime = new();
            _diagnosticsProvider.GenerateHassEntities(hassEntityIds);
            var libraries = SavedLibraries();
            _loadErrors.Clear();
            var assemblies = await _compiler.CompileFromUserScriptsFolder(RecordLoadError);
            var store = VisualDocuments;
            foreach (var document in store.List())
            {
                try
                {
                    var preview = new VisualGenerator(_diagnosticsProvider).Preview(document.Graph, libraries.Preview.Snapshot);
                    if (!preview.Valid) throw new VisualValidationException(preview);
                    assemblies.Add(await CompileVisualDocument(document with { Source = preview.Source, SourceMap = preview.SourceMap, CatalogGeneration = libraries.Generation }));
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

            await _userScriptManager.UnloadUserScriptsAsync(); // Just in case, shouldnt be needed

            if (assemblies.Count == 0)
                Logger.Info("No user scripts found on initialization");

            await _userScriptManager.LoadUserScripts(assemblies, RecordLoadError);
            _userScriptManager.DependencyTracking.Debug();
        }
        finally { _visualGate.Release(); }
    }

    public string GetRunHistory(string automationId, int offset, int limit) =>
        System.Text.Json.JsonSerializer.Serialize(new { version = 1, offset, limit,
            runs = _history.Read(automationId, offset, limit) }, HistoryJson);

    static readonly System.Text.Json.JsonSerializerOptions HistoryJson = new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    public string RunNow(string automationId, string className, string method)
    {
        _visualGate.Wait();
        try
        {
            var script = _userScriptManager.GetUserScripts().FirstOrDefault(s => s.AutomationId == automationId);
            if (script?.CompiledScript.VisualRevision != null)
            {
                // Identify visuals from the active compiled metadata, not a best-effort
                // document listing: missing/corrupt saved files must fail closed.
                var id = Path.GetFileNameWithoutExtension(script.CompiledScript.FilePath)["visual_".Length..];
                var document = VisualDocuments.Read(id) ?? throw new InvalidOperationException("Saved visual document not found");
                if (className != "VisualAutomation") throw new ArgumentException("Invalid visual class");
                var trigger = document.Graph["nodes"]!.AsArray().FirstOrDefault(n => n?["kind"]?.GetValue<string>() == "trigger" && VisualNodeCatalog.EffectiveTriggerMethod(n!.AsObject()) == method)
                    ?? throw new ArgumentException("Trigger not found");
                return System.Text.Json.JsonSerializer.Serialize(DispatchVisual(document, trigger["id"]!.GetValue<string>()));
            }
            return System.Text.Json.JsonSerializer.Serialize(new { version = 1,
                runId = _userScriptManager.DispatchManual(automationId, className, method) });
        }
        finally { _visualGate.Release(); }
    }

    sealed record ServiceDescriptionsState(long Sequence, ServiceSchemaCatalog Catalog);
    ServiceDescriptionsState _serviceDescriptions = new(0, ServiceSchemaCatalog.Empty);
    long _serviceDescriptionsSequence;
    internal ServiceSchemaCatalog ServiceSchemas => Volatile.Read(ref _serviceDescriptions).Catalog;

    /// <summary>
    /// Receives HA's raw <c>{domain: {service: description}}</c> from a Python executor thread, at any
    /// time: before or during <see cref="Init"/>, repeatedly, or after unload. Normalises outside every
    /// lock, then swaps the immutable result in; a later push always wins over an earlier one that
    /// finishes after it. Malformed input is logged and the previous schemas stay; it never throws.
    /// </summary>
    public void SetServiceDescriptions(string json) => SetServiceDescriptions(json, Interlocked.Increment(ref _serviceDescriptionsSequence));

    // The sequence is taken on entry; installing compares it, so ordering is testable deterministically.
    internal void SetServiceDescriptions(string json, long sequence)
    {
        ServiceDescriptionsState next;
        // Logging crosses into Python; the caller must not see a failure from it either.
        try { next = new(sequence, ServiceSchemaCatalog.Parse(json)); }
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
    }

    public void Unload() { _languageLifetime.Cancel(); _userScriptManager.UnloadUserScripts(); }
    public Task UnloadAsync() { _languageLifetime.Cancel(); return _userScriptManager.UnloadUserScriptsAsync(); }

    public void GenerateHassEntities(string[] entityIds)
    {
        _visualGate.Wait();
        try { _diagnosticsProvider.GenerateHassEntities(entityIds); }
        finally { _visualGate.Release(); }
    }

    public Task OnTrackedEntityChange(string entityId, HasEntityState newState, HasEntityState? oldState)
    {
        newState.OldState = oldState;
        var trigger = new TriggerContext(newState);
        return _userScriptManager.RunEntries(entityId, trigger);
    }

    public PyList GetUserScripts() => PyDTOConverter.UserScriptToDto(GetScriptSummaries());

    internal List<UserScriptDTO> GetScriptSummaries()
    {
        var scripts = _userScriptManager.GetUserScripts()
        .Where(s => Path.GetDirectoryName(s.CompiledScript.FilePath) == HassPath.UserScripts)
        .Select(s => new UserScriptDTO { AutomationId = s.AutomationId, Revision = s.Revision,
            FileName = s.CompiledScript.FileName, Active = s.Active,
            Classes = s.Classes.Select(c => new UserScriptClassDto { Name = c.ClassName, Methods = c.Methods.Select(m => m.Name).ToArray() }).ToArray() })
        .ToList();
        if (Directory.Exists(HassPath.UserScripts))
        foreach (var path in Directory.GetFiles(HassPath.UserScripts, "*.cs").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (scripts.Any(s => s.FileName == name)) continue;
            scripts.Add(new UserScriptDTO { FileName = name, Active = false, Classes = [],
                ActivationError = _loadErrors.TryGetValue(path, out var error) ? error : "Not active; save to activate" });
        }
        return scripts;
    }

    public UserScriptSourceDTO? GetUserScript(string fileName)
    {
        var script = _userScriptManager.GetUserScript(fileName);
        if (script == null)
        {
            var path = CodePath(fileName);
            return File.Exists(path) ? new() { FileName = fileName, Source = File.ReadAllText(path) } : null;
        }

        return new() { FileName = script.CompiledScript.FileName, Source = script.CompiledScript.SourceCode };
    }

    public string? GetHoverDiagnostics(string source, int position) => _diagnosticsProvider.GetHover(source, position);

    public DiagnosticModel[] GetCompilationDiagnostics(string source) => _diagnosticsProvider.GetDiagnostics(source);

    public Task<IReadOnlyList<CompletionModel>> GetCodeCompletions(string source, int position) =>
        _diagnosticsProvider.GetCompletions(source, position);

    public Task<string> FormatCode(string source) => _diagnosticsProvider.FormatCode(source);

    public async Task<PyTuple> SaveScript(string path, string source)
    {
        if (_userScriptManager.GetUserScripts().Any(s => s.CompiledScript.FileName == path && Path.GetDirectoryName(s.CompiledScript.FilePath) != HassPath.UserScripts)
            && !File.Exists(Path.Join(HassPath.UserScripts, $"{path}.cs")))
            return PyResult.Errors(["Use the visual API for visual documents"]);
        var scriptPath = CodePath(path);
        try
        {
            var compiled = await _compiler.CompileSingleFile(scriptPath, source, false);
            await _userScriptManager.UpdateUserScript(compiled);
            _loadErrors.TryRemove(scriptPath, out _);
            _userScriptManager.DependencyTracking.Debug();
        }
        catch (CompilationErrorException ex)
        {
            return PyResult.Errors(ex.Errors);
        }
        catch (Exception ex)
        {
            if (ex.InnerException != null)
                return PyResult.Errors([ex.InnerException.Message]);
            return PyResult.Errors([ex.Message]);
        }

        return PyResult.Success();
    }

    public PyTuple RenameScript(string path, string name)
    {
        return WrapPyResult(() =>
        {
            var normalizedName = UserScriptManager.NormalizeScriptName(name);
            var found = _userScriptManager.GetUserScripts().Find(s => s.CompiledScript.Id() == path);
            if (found is null) throw new ArgumentException($"Cant find script at {path}");

            var alreadyExists = _userScriptManager.GetUserScripts().Exists(s =>
                s.CompiledScript.Id() != path && s.CompiledScript.Slug() == normalizedName);

            if (alreadyExists) throw new ArgumentException($"Script with name {name} already exists");

            RequireCodeScript(found.CompiledScript.FilePath);
            var oldPath = found.CompiledScript.FilePath;
            var oldName = found.CompiledScript.FileName;
            found.CompiledScript.RenameOnDisk(normalizedName);
            try { _history.Rename(oldPath, found.CompiledScript.FilePath); }
            catch { found.CompiledScript.RenameOnDisk(oldName); throw; }
            return normalizedName;
        });
    }

    public async Task<PyTuple> CreateEmptyScript(string name)
    {
        return await WrapPyResult(() => _userScriptManager.CreateEmptyScript(_compiler, name));
    }


    static string CodePath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new ArgumentException("Invalid script name");
        return Path.Combine(HassPath.UserScripts, name + ".cs");
    }

    static void RequireCodeScript(string path)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) != Path.GetFullPath(HassPath.UserScripts))
            throw new ArgumentException("Use the visual API for visual documents");
    }

    public Task DeleteScript(string scriptPath)
    {
        RequireCodeScript(scriptPath);
        return DeleteScriptAsync(scriptPath);
    }

    public async Task DeleteScriptAsync(string scriptPath)
    {
        RequireCodeScript(scriptPath);
        await _userScriptManager.DeleteScriptAsync(scriptPath);
        _loadErrors.TryRemove(scriptPath, out _);
    }
}
