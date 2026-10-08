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
        var trigger = new TriggerContext { NewState = newState, OldState = oldState };
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
        if (!File.Exists(scriptPath))
        {
            await File.Create(scriptPath).DisposeAsync();
        }

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

    public Task CreateEmptyScript(string name) => _userScriptManager.CreateEmptyScript(_compiler, name);

    public void DeleteScript(string scriptPath) => _userScriptManager.DeleteScript(scriptPath);
}
