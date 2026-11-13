using System.Globalization;
using System.Text;

namespace HassSharp;

record TriggerContext(HasEntityState State);

class UserScriptManager
{
    public RunHistoryStore? History { get; init; }

    public Task RunNow(string automationId, string className, string method)
    {
        var script = _userScripts.Single(s => s.AutomationId == automationId);
        return script.Classes.Single(c => c.ClassName == className).RunMethod(method, InvocationCause.Manual);
    }

    public string DispatchManual(string automationId, string className, string method)
    {
        var script = _userScripts.Single(s => s.AutomationId == automationId);
        var klass = script.Classes.Single(c => c.ClassName == className);
        var runId = Guid.NewGuid().ToString("N");
        var task = klass.RunMethod(method, InvocationCause.Manual, runId);
        if (task.IsCompleted) task.GetAwaiter().GetResult();
        else _ = Observe(task);
        return runId;
    }

    static async Task Observe(Task task)
    {
        try { await task; }
        catch (Exception ex) { Logger.Error($"Invocation history failure: {ex.Message}"); }
    }

    public static readonly AsyncLocal<TriggerContext?> CurrentTrigger = new();

    internal object ActivationGate { get; } = new();
    readonly SemaphoreSlim _updates = new(1, 1);
    readonly List<UserScript> _userScripts = new();
    readonly HashSet<UserScript> _preparing = new();
    long _epoch;
    readonly HashSet<string> _deletingPaths = new(StringComparer.Ordinal);
    internal long ActivationEpoch { get { lock (ActivationGate) return _epoch; } }
    bool _accepting = true;
    public DependencyTracking DependencyTracking { get; } = new();
    public ScriptSyncRunner SyncRunner { get; } = new();

    public List<UserScript> GetUserScripts() { lock (ActivationGate) return _userScripts.ToList(); }

    public UserScript? GetUserScript(string scriptSlug)
    { lock (ActivationGate) return _userScripts.FirstOrDefault(s => s.CompiledScript.Slug() == scriptSlug); }

    public void UnloadUserScripts() => _ = Observe(UnloadUserScriptsAsync());

    public Task UnloadUserScriptsAsync()
    {
        UserScript[] scripts;
        lock (ActivationGate)
        {
            _epoch++;
            _accepting = false;
            scripts = _userScripts.Concat(_preparing).Distinct().ToArray();
            foreach (var script in scripts) script.Invalidate();
            _userScripts.Clear();
            _preparing.Clear();
        }
        List<Exception> errors = [];
        foreach (var script in scripts)
            try { script.CancelLifetime(); } catch (Exception error) { errors.Add(error); }
        return CleanupScriptsAsync(scripts, errors);
    }

    async Task CleanupScriptsAsync(UserScript[] scripts, List<Exception>? errors = null)
    {
        errors ??= [];
        var cleanup = Task.WhenAll(scripts.Select(script => script.ClearFromTrackingAsync()));
        try { await cleanup; }
        catch (Exception error) { errors.Add(cleanup.Exception ?? error); }
        foreach (var script in scripts)
            try { script.Unload(); } catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    internal void EnsureCanActivate(UserScript script, long epoch)
    {
        lock (ActivationGate)
        {
            if (!_accepting || epoch != _epoch || script.Invalidated ||
                (!_preparing.Contains(script) && !_userScripts.Contains(script)))
                throw new InvalidOperationException("Script generation was invalidated during preparation");
            script.LifetimeToken.ThrowIfCancellationRequested();
        }
    }

    internal void ActivatePreparedScript(UserScript script, long epoch)
    {
        lock (ActivationGate)
        {
            EnsureCanActivate(script, epoch);
            script.Active = true;
            _preparing.Remove(script);
            if (!_userScripts.Contains(script)) _userScripts.Add(script);
        }
    }

    public void RemoveScriptFromList(UserScript script)
    {
        lock (ActivationGate)
        {
            script.Invalidate();
            _userScripts.Remove(script);
            _preparing.Remove(script);
        }
    }

    public UserScript LoadUserScript(CompiledUserScript compiledScript, bool staged = false)
    {
        var baseType = typeof(Automation);

        var userScript = new UserScript
        {
            Active = !staged,
            AutomationId = History?.Identity(compiledScript.FilePath) ?? compiledScript.FilePath,
            CompiledScript = compiledScript,
            Classes = null!,
            ScriptManager = this,
        };
        var userScriptClasses = compiledScript.Assembly.GetTypes()
            .Where(t => baseType.IsAssignableFrom(t) && !t.IsAbstract &&
                (compiledScript.DesignatedRootType == null || t.FullName == compiledScript.DesignatedRootType))
            .Select(t => new UserScriptClass(t, userScript))
            .ToArray();

        userScript.Classes = userScriptClasses;
        lock (ActivationGate)
        {
            if (!_accepting || _deletingPaths.Contains(compiledScript.FilePath))
            {
                compiledScript.Unload();
                throw new InvalidOperationException("Script manager is unloaded");
            }
            if (!staged) _userScripts.Add(userScript);
            else _preparing.Add(userScript);
        }
        return userScript;
    }

    public async Task LoadUserScripts(IEnumerable<CompiledUserScript> compiledScripts, Action<string, Exception>? onFailure = null)
    {
        long epoch;
        lock (ActivationGate) { _accepting = true; epoch = _epoch; }
        foreach (var compiled in compiledScripts)
        {
            UserScript? script = null;
            try
            {
                lock (ActivationGate)
                {
                    if (epoch != _epoch) throw new InvalidOperationException("Script manager was unloaded during initial load");
                    script = LoadUserScript(compiled, staged: true);
                }
                await script.PrepareAsync();
                ActivatePreparedScript(script, epoch);
                script.Start();
            }
            catch (Exception ex)
            {
                onFailure?.Invoke(compiled.FilePath, ex);
                Logger.Error($"Cannot activate script {compiled.FilePath}: {ex}");
                try { if (script != null) await script.ClearFromTrackingAsync(); }
                catch (Exception cleanup) { Logger.Error($"Cannot clean up script {compiled.FilePath}: {cleanup}"); }
                finally { compiled.Unload(); }
            }
        }
    }

    public void TrackEntityCall(string entityId, UserScriptClass scriptClass, string method) =>
        DependencyTracking.TrackEntityCall(entityId, scriptClass, method);

    public Task RunEntries(string entityId, TriggerContext trigger)
    {
        // Built once per change and outside the gate: detaching attributes may take the GIL.
        var payload = TriggerPayload.Entity(trigger.State, trigger.State.OldState);
        lock (ActivationGate)
        {
            var entries = DependencyTracking.GetEntries(entityId);
            return Task.WhenAll(entries.Where(e => e.ScriptClass.Script.Active).Select(e => RunEntry(e, trigger, payload)));
        }
    }

    public async Task RunEntry(DependencyEntry entry, TriggerContext trigger, System.Text.Json.JsonElement payload)
    {
        var previousTrigger = CurrentTrigger.Value;
        CurrentTrigger.Value = trigger;
        try
        {
            await entry.ScriptClass.RunMethod(entry.MethodName, InvocationCause.Entity, payload: payload);
        }
        finally
        {
            CurrentTrigger.Value = previousTrigger;
        }
    }

    public async Task UpdateUserScript(CompiledUserScript compiled)
    {
        long epoch;
        lock (ActivationGate) epoch = _epoch;
        await _updates.WaitAsync();
        try
        {
            lock (ActivationGate)
                if (!_accepting || epoch != _epoch)
                {
                    compiled.Unload();
                    throw new InvalidOperationException("Script manager is unloaded");
                }
            var found = _userScripts.SingleOrDefault(s => s.CompiledScript.Id() == compiled.Id());
            if (found is null && !File.Exists(compiled.FilePath))
            {
                compiled.Unload();
                throw new InvalidOperationException("Script not found");
            }
            UserScript? replacement = null;
            try
            {
                // Stage subscriptions while the original remains the only trigger recipient.
                replacement = LoadUserScript(compiled, staged: true);
                await replacement.PrepareAsync();
                EnsureCanActivate(replacement, epoch);
                await replacement.WriteIfDirty();
                lock (ActivationGate)
                {
                    EnsureCanActivate(replacement, epoch);
                    var index = found == null ? -1 : _userScripts.IndexOf(found);
                    if (found != null && (index < 0 || !found.Active))
                        throw new InvalidOperationException("Script was unloaded during update");
                    if (found != null) found.Invalidate();
                    replacement.Active = true;
                    _preparing.Remove(replacement);
                    if (found == null) _userScripts.Add(replacement);
                    else _userScripts[index] = replacement;
                }
            }
            catch (Exception error)
            {
                try { if (replacement != null) await replacement.ClearFromTrackingAsync(); }
                finally { compiled.Unload(); }
                if (error is OperationCanceledException)
                    throw new InvalidOperationException("Script was unloaded during update", error);
                throw;
            }

            if (found != null)
            {
                try { await found.ClearFromTrackingAsync(); } finally { found.Unload(); }
            }
            replacement.Start();
        }
        finally { _updates.Release(); }
    }

    public async Task<string> CreateEmptyScript(CodeCompiler compiler, string scriptNameUserInput)
    {
        var slug = $"{NormalizeScriptName(scriptNameUserInput)}.cs";
        const string emptyScriptSource = """
                                         public class ReplaceThisNameAutomation : Automation
                                         {
                                         }
                                         """;
        var scriptPath = GetScriptFilePathFromSlug(slug);
        var slugWithoutExtension = Path.GetFileNameWithoutExtension(slug);

        if (GetUserScript(slugWithoutExtension) is not null || File.Exists(scriptPath))
            throw new InvalidOperationException($"An automation named '{slugWithoutExtension}' already exists");

        FileStream stream;
        try
        {
            stream = new FileStream(
                scriptPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true
            );
        }
        catch (IOException ex) when (File.Exists(scriptPath))
        {
            throw new InvalidOperationException($"An automation named '{slugWithoutExtension}' already exists", ex);
        }

        try
        {
            await using (stream)
            await using (var writer = new StreamWriter(stream))
                await writer.WriteAsync(emptyScriptSource);
        }
        catch
        {
            TryDeleteFailedScript(scriptPath);
            throw;
        }

        CompiledUserScript? compiled = null;
        UserScript? loadedScript = null;

        try
        {
            compiled = await compiler.CompileFromUserScriptFile(slug, emptyScriptSource, true);
            loadedScript = LoadUserScript(compiled);
            await loadedScript.ConfigureAndStart();
        }
        catch
        {
            if (loadedScript is not null)
            {
                await loadedScript.ClearFromTrackingAsync();
                loadedScript.Unload();
            }
            else
            {
                compiled?.Unload();
            }

            TryDeleteFailedScript(scriptPath);

            throw;
        }

        return slug;
    }

    static void TryDeleteFailedScript(string scriptPath)
    {
        try
        {
            File.Delete(scriptPath);
        }
        catch (Exception cleanupError)
        {
            Logger.Error($"Failed to clean up script after creation failed: {cleanupError.Message}");
        }
    }

    public async Task DeleteScriptAsync(string scriptPath, bool requireExisting = true)
    {
        UserScript[] deleting;
        lock (ActivationGate)
        {
            if (!_deletingPaths.Add(scriptPath)) throw new InvalidOperationException("Script deletion is already in progress");
            deleting = _userScripts.Concat(_preparing).Where(s => s.CompiledScript.FilePath == scriptPath).Distinct().ToArray();
            foreach (var script in deleting) script.Invalidate();
        }
        List<Exception> errors = [];
        try
        {
            foreach (var script in deleting)
                try { script.CancelLifetime(); } catch (Exception error) { errors.Add(error); }
            await _updates.WaitAsync();
            try
            {
                if (requireExisting && deleting.Length == 0 && !File.Exists(scriptPath))
                    throw new FileNotFoundException("Script not found");
                try { await CleanupScriptsAsync(deleting); } catch (Exception error) { errors.Add(error); }
                try { File.Delete(scriptPath); } catch (Exception error) { errors.Add(error); }
                try { History?.Forget(scriptPath); } catch (Exception error) { errors.Add(error); }
            }
            finally { _updates.Release(); }
        }
        finally { lock (ActivationGate) _deletingPaths.Remove(scriptPath); }
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    public static string NormalizeScriptName(string name)
    {
        var normalized = name.Trim().Normalize(NormalizationForm.FormD);
        var slug = new StringBuilder();
        var needsSeparator = false;

        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;

            var lower = char.ToLowerInvariant(character);
            var isAsciiLetter = char.IsAsciiLetter(lower);
            var isDigit = char.IsAsciiDigit(lower);

            if (isAsciiLetter || isDigit)
            {
                if (needsSeparator && slug.Length > 0)
                    slug.Append('_');

                slug.Append(lower);
                needsSeparator = false;
            }
            else
            {
                needsSeparator = true;
            }
        }

        if (slug.Length == 0)
            throw new ArgumentException("Automation name must contain at least one letter or number", nameof(name));

        return slug.ToString();
    }

    internal static string GetScriptFilePathFromSlug(string slug) => Path.Join(HassPath.UserScripts, slug);
}
