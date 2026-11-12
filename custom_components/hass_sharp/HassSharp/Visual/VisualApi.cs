using System.Text.Json;
using System.Text.Json.Nodes;
namespace HassSharp;

public partial class HassSharpManager
{
    readonly SemaphoreSlim _visualGate = new(1, 1);
    VisualStore VisualDocuments => new(Path.Combine(HassPath.HassConfiguration, ".storage", "hass_sharp", "visual"), new(_diagnosticsProvider));
    string VisualRuntimePath(string id) => Path.Combine(HassPath.HassConfiguration, ".storage", "hass_sharp", "visual", "visual_" + id + ".cs");

    VisualDocument WithRuntimeIdentity(VisualDocument document)
    {
        var path = VisualRuntimePath(document.Id);
        var active = _userScriptManager.GetUserScripts().Any(s => s.CompiledScript.FilePath == path && s.Active);
        // Source/maps are the last-saved artifact. Live generation belongs to preview,
        // save and startup; listing documents must not compile every graph.
        return document with { AutomationId = _history.Identity(path), Active = active,
            ActivationError = _loadErrors.TryGetValue(path, out var error) ? error : active ? null : "Not active" };
    }

    // Single JSON bridge keeps Python.NET calls and frontend versioning explicit.
    public async Task<string> VisualRequest(string requestJson)
    {
        await _visualGate.WaitAsync();
        try
        {
            var request = JsonNode.Parse(requestJson)!.AsObject();
            var operation = request["operation"]!.GetValue<string>();
            object result;
            var store = VisualDocuments;
            if (operation == "preview") result = new VisualGenerator(_diagnosticsProvider).Preview(request["graph"]!.AsObject());
            else if (operation == "list") result = store.List().Select(WithRuntimeIdentity).ToArray();
            else
            {
                var id = request["id"]!.GetValue<string>();
                if (operation == "read") result = WithRuntimeIdentity(store.Read(id) ?? throw new FileNotFoundException("Visual document not found"));
                else
                {
                    var revision = request["expectedRevision"]!.GetValue<long>();
                    if (operation == "save")
                    {
                        var document = WithRuntimeIdentity(store.Save(id, request["graph"]!.AsObject(), revision));
                        string? activationError = null;
                        try
                        {
                            var compiled = await _compiler.CompileSingleFile(VisualRuntimePath(id), document.Source, true);
                            if (!_userScriptManager.GetUserScripts().Any(s => s.CompiledScript.FilePath == VisualRuntimePath(id)))
                            {
                                var loaded = _userScriptManager.LoadUserScript(compiled);
                                try { await loaded.ConfigureAndStart(); }
                                catch
                                {
                                    await loaded.ClearFromTrackingAsync();
                                    loaded.Unload();
                                    throw;
                                }
                            }
                            else await _userScriptManager.UpdateUserScript(compiled);
                        }
                        catch (Exception ex) { activationError = ex.Message; RecordLoadError(VisualRuntimePath(id), ex); }
                        if (activationError == null) _loadErrors.TryRemove(VisualRuntimePath(id), out _);
                        document = WithRuntimeIdentity(document);
                        result = new { document, active = activationError == null, activationError };
                    }
                    else if (operation == "delete")
                    {
                        store.Delete(id, revision);
                        await _userScriptManager.DeleteScriptAsync(VisualRuntimePath(id), requireExisting: false);
                        _loadErrors.TryRemove(VisualRuntimePath(id), out _);
                        result = new { deleted = true };
                    }
                    else throw new ArgumentException("Unknown visual operation");
                }
            }
            return JsonSerializer.Serialize(new { version = 1, ok = true, result }, VisualStore.Json);
        }
        catch (VisualValidationException ex) { return Error("validation", ex.Preview); }
        catch (VisualConflictException ex) { return Error("conflict", ex.Message); }
        catch (FileNotFoundException ex) { return Error("not_found", ex.Message); }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or NullReferenceException or OverflowException)
        { return Error("invalid_request", ex.Message); }
        finally { _visualGate.Release(); }
    }
    static string Error(string code, object detail) => JsonSerializer.Serialize(new { version = 1, ok = false, error = new { code, detail } }, VisualStore.Json);
}
