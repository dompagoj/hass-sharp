using System.Runtime.CompilerServices;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    T WaitForAsync<T>(Func<Task<T>> cb)
    {
        return cb().GetAwaiter().GetResult();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

            _userScriptManager.UnloadUserScripts(); // Just in case, shouldnt be needed

            if (assemblies.Count == 0)
            {
                Logger.Info("No user scripts found on initialization");
                return;
            }

            _userScriptManager.LoadUserScripts(assemblies);
            await _userScriptManager.InitializeUserScripts();
            _userScriptManager.DependencyTracking.Debug();
        });
    }

    public void UnLoad() => _userScriptManager.UnloadUserScripts();

    public void GenerateHassEntities(string[] entityIds) => _diagnosticsProvider.GenerateHassEntities(entityIds);

    public void OnTrackedEntityChange(string entityId, HasEntityState newState, HasEntityState? oldState)
    {
        var trigger = new TriggerContext
        {
            NewState = newState,
            OldState = oldState
        };
        WaitForAsync(() => _userScriptManager.RunEntries(entityId, trigger));
    }

    public PyList GetUserScripts() => PyDTOConverter.UserScriptToDto(_userScriptManager.GetUserScripts());

    public UserScriptSourceDTO? GetUserScript(string fileName) =>
        WaitForAsync(() => _userScriptManager.GetUserScriptSource(fileName));

    public string? GetHoverDiagnostics(string source, int position) => _diagnosticsProvider.GetHover(source, position);
    public DiagnosticModel[] GetCompilationDiagnostics(string source) => _diagnosticsProvider.GetDiagnostics(source);

    public IReadOnlyList<CompletionItem> GetCodeCompletions(string source, int position) =>
        _diagnosticsProvider.GetCompletions(source, position);

    public string FormatCode(string source) => WaitForAsync(() => _diagnosticsProvider.FormatCode(source));

    public PyTuple SaveScript(string path, string source)
    {
        return WaitForAsync(async () =>
        {
            var scriptPath = Path.Join(HassPath.UserScripts, $"{path}.cs");
            if (!File.Exists(scriptPath))
            {
                Logger.Error($"File not found at {scriptPath}");
                return PyResult.Error("File not found");
            }

            Logger.Info($"Writing file to ${scriptPath}");

            try
            {
                var compiled = await _compiler.CompileSingleFile(scriptPath, source);
                await File.WriteAllTextAsync(scriptPath, source);

                await _userScriptManager.UpdateUserScript(compiled);
                _userScriptManager.DependencyTracking.Debug();
            }
            catch (CompilationErrorException ex)
            {
                return PyResult.Errors(ex.Errors);
            }
            catch (Exception ex)
            {
                return PyResult.Errors([ex.Message]);
            }

            return PyResult.Success();
        });
    }
}
