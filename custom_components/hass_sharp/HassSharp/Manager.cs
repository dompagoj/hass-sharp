using Microsoft.CodeAnalysis.Completion;
using Python.Runtime;

namespace HassSharp;

public class HassSharpManager
{
    readonly DiagnosticsProvider _diagnosticsProvider = new();
    readonly CodeCompiler _compiler;
    readonly UserScriptManager _userScriptManager = new();

    public HassSharpManager(string hassConfigPath)
    {
        HassPath.HassConfiguration = hassConfigPath;
        _compiler = new(_diagnosticsProvider);
    }

    public async Task Init(string[] hassEntityIds)
    {
        _diagnosticsProvider.GenerateHassEntities(hassEntityIds);
        var assemblies = await _compiler.CompileFromUserScriptsFolder();

        _userScriptManager.UnloadUserScripts(); // Just in case, shouldnt be needed

        if (assemblies.Count == 0)
        {
            Logger.Info("No user scripts found on initialization");
            return;
        }

        await _userScriptManager.LoadUserScripts(assemblies);
        _userScriptManager.DependencyTracking.Debug();
    }

    public void Unload() => _userScriptManager.UnloadUserScripts();

    public void GenerateHassEntities(string[] entityIds) => _diagnosticsProvider.GenerateHassEntities(entityIds);

    public Task OnTrackedEntityChange(string entityId, HasEntityState newState, HasEntityState? oldState)
    {
        newState.OldState = oldState;
        var trigger = new TriggerContext(newState);
        return _userScriptManager.RunEntries(entityId, trigger);
    }

    public PyList GetUserScripts() => PyDTOConverter.UserScriptToDto(_userScriptManager.GetUserScripts());

    public UserScriptSourceDTO? GetUserScript(string fileName)
    {
        var script = _userScriptManager.GetUserScript(fileName);
        if (script == null)
            return null;

        return new() { FileName = script.CompiledScript.FileName, Source = script.CompiledScript.SourceCode };
    }

    public string? GetHoverDiagnostics(string source, int position) => _diagnosticsProvider.GetHover(source, position);

    public DiagnosticModel[] GetCompilationDiagnostics(string source) => _diagnosticsProvider.GetDiagnostics(source);

    public Task<IReadOnlyList<CompletionItem>> GetCodeCompletions(string source, int position) =>
        _diagnosticsProvider.GetCompletions(source, position);

    public Task<string> FormatCode(string source) => _diagnosticsProvider.FormatCode(source);

    public async Task<PyTuple> SaveScript(string path, string source)
    {
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

            found.CompiledScript.RenameOnDisk(normalizedName);
            return normalizedName;
        });
    }

    public async Task<PyTuple> CreateEmptyScript(string name)
    {
        return await WrapPyResult(() => _userScriptManager.CreateEmptyScript(_compiler, name));
    }


    public void DeleteScript(string scriptPath) => _userScriptManager.DeleteScript(scriptPath);
}
