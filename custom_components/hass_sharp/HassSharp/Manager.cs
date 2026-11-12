using Python.Runtime;

namespace HassSharp;

public partial class HassSharpManager
{
    readonly DiagnosticsProvider _diagnosticsProvider = new();
    readonly CodeCompiler _compiler;
    readonly UserScriptManager _userScriptManager;
    readonly RunHistoryStore _history;

    public HassSharpManager(string hassConfigPath)
    {
        HassPath.HassConfiguration = hassConfigPath;
        _compiler = new(_diagnosticsProvider);
        _history = new(Path.Combine(hassConfigPath, ".storage", "hass_sharp", "runs.db"));
        _userScriptManager = new() { History = _history };
    }

    public async Task Init(string[] hassEntityIds)
    {
        _diagnosticsProvider.GenerateHassEntities(hassEntityIds);
        var assemblies = await _compiler.CompileFromUserScriptsFolder();
        foreach (var document in VisualDocuments.List())
        {
            try { assemblies.Add(await _compiler.CompileSingleFile(VisualRuntimePath(document.Id), document.Source, true)); }
            catch (Exception ex)
            {
                Logger.Error($"Cannot load visual document {document.Id}: {ex}");
                if (ex is CompilationErrorException compilation) Logger.Error(string.Join("\n", compilation.Errors));
            }
        }

        _userScriptManager.UnloadUserScripts(); // Just in case, shouldnt be needed

        if (assemblies.Count == 0)
        {
            Logger.Info("No user scripts found on initialization");
            return;
        }

        await _userScriptManager.LoadUserScripts(assemblies);
        _userScriptManager.DependencyTracking.Debug();
    }

    public string GetRunHistory(string automationId, int offset, int limit) =>
        System.Text.Json.JsonSerializer.Serialize(new { version = 1, offset, limit,
            runs = _history.Read(automationId, offset, limit) }, HistoryJson);

    static readonly System.Text.Json.JsonSerializerOptions HistoryJson = new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    public string RunNow(string automationId, string className, string method) =>
        System.Text.Json.JsonSerializer.Serialize(new { version = 1,
            runId = _userScriptManager.DispatchManual(automationId, className, method) });

    public void Unload() => _userScriptManager.UnloadUserScripts();

    public void GenerateHassEntities(string[] entityIds) => _diagnosticsProvider.GenerateHassEntities(entityIds);

    public Task OnTrackedEntityChange(string entityId, HasEntityState newState, HasEntityState? oldState)
    {
        newState.OldState = oldState;
        var trigger = new TriggerContext(newState);
        return _userScriptManager.RunEntries(entityId, trigger);
    }

    public PyList GetUserScripts() => PyDTOConverter.UserScriptToDto(_userScriptManager.GetUserScripts().Where(s => Path.GetDirectoryName(s.CompiledScript.FilePath) == HassPath.UserScripts).ToList());

    public UserScriptSourceDTO? GetUserScript(string fileName)
    {
        var script = _userScriptManager.GetUserScript(fileName);
        if (script == null)
            return null;

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
        var scriptPath = Path.Join(HassPath.UserScripts, $"{path}.cs");
        try
        {
            var compiled = await _compiler.CompileSingleFile(scriptPath, source, false);
            await _userScriptManager.UpdateUserScript(compiled);
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


    static void RequireCodeScript(string path)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) != Path.GetFullPath(HassPath.UserScripts))
            throw new ArgumentException("Use the visual API for visual documents");
    }

    public void DeleteScript(string scriptPath)
    {
        RequireCodeScript(scriptPath);
        _userScriptManager.DeleteScript(scriptPath);
    }
}
