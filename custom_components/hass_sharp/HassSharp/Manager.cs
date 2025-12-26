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

    T WaitForAsync<T>(Func<Task<T>> cb)
    {
        return cb().GetAwaiter().GetResult();
    }

    static void WaitForAsync(Func<Task> cb) => cb().GetAwaiter().GetResult();

    // Public python interface is all blocking because it gets run using hass.async_add_executor_job,
    // we cannot await c# tasks from python unfortunately
    // All other classes should use normal async/task methods so they can be awaited here in parallel if need be
    public void Init(string[] hassEntityIds)
    {
        WaitForAsync(async () =>
        {
            _diagnosticsProvider.GenerateHassEntities(hassEntityIds);
            var assemblies = await _compiler.CompileFromUserScriptsFolder();

            _userScriptManager.ClearUserScripts();

            if (assemblies.Count == 0)
            {
                Logger.Info("No user scripts found on initialization");
            }
            else
            {
                _userScriptManager.LoadUserScripts(assemblies);
                await _userScriptManager.InitializeUserScripts();
            }
        });
    }

    public void UnLoad()
    {
        _userScriptManager.ClearUserScripts();
    }

    public void GenerateHassEntities(string[] entityIds) => _diagnosticsProvider.GenerateHassEntities(entityIds);

    public Dictionary<string, List<DependencyEntry>> GetScriptEntityDependencies() =>
        _userScriptManager.DependencyTracking;

    public void RunEntries(List<DependencyEntry> entries) => WaitForAsync(() => _userScriptManager.RunEntries(entries));

    public PyList GetUserScripts() => PyDTOConverter.UserScriptToDto(_userScriptManager.GetUserScripts());

    public UserScriptSourceDTO? GetUserScript(string fileName) =>
        WaitForAsync(() => _userScriptManager.GetUserScriptSource(fileName));

    public string? GetHoverDiagnostics(string source, int position) => _diagnosticsProvider.GetHover(source, position);
    public DiagnosticModel[] GetCompilationDiagnostics(string source) => _diagnosticsProvider.GetDiagnostics(source);

    public IReadOnlyList<CompletionItem> GetCodeCompletions(string source, int position) =>
        _diagnosticsProvider.GetCompletions(source, position);

    public void SaveScript(string path, string source)
    {
        WaitForAsync(async () =>
        {
            var compiled = await _compiler.CompileSingleFile(path, source);
            await _userScriptManager.UpdateUserScript(compiled);
        });
    }
}
