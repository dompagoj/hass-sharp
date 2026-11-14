namespace HassSharp;

record TriggerContext(HasEntityState State);

class UserScriptManager
{
    public RunHistoryStore? History { get; init; }

    /// <param name="payload">The normalised trigger payload; <c>{platform: "manual"}</c> when omitted.</param>
    public string DispatchManual(string automationId, string className, string method, System.Text.Json.JsonElement? payload = null)
    {
        var script = _userScripts.Single(s => s.AutomationId == automationId);
        var klass = script.Classes.Single(c => c.ClassName == className);
        var runId = Guid.NewGuid().ToString("N");
        var task = klass.RunMethod(method, InvocationCause.Manual, runId, payload);
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

    public void UnloadUserScripts() => UnloadUserScripts(null);
    public void UnloadUserScripts(string? reason) => _ = Observe(UnloadUserScriptsAsync(reason));

    public Task UnloadUserScriptsAsync() => UnloadUserScriptsAsync(null);

    /// <summary>Cancels every loaded script's lifetime with <paramref name="reason"/>, without unloading.</summary>
    public void CancelRuns(string reason)
    {
        UserScript[] scripts;
        lock (ActivationGate) scripts = _userScripts.Concat(_preparing).Distinct().ToArray();
        List<Exception> errors = [];
        foreach (var script in scripts)
            try { script.CancelLifetime(reason); } catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    /// <param name="reason">Recorded on the runs this cancels; see <see cref="CancelReasons"/>.</param>
    public Task UnloadUserScriptsAsync(string? reason)
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
            try { script.CancelLifetime(reason); } catch (Exception error) { errors.Add(error); }
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
            Enabled = compiledScript.Enabled,
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

    /// <summary>
    /// Turns the loaded script at <paramref name="scriptPath"/> on or off (see <see cref="UserScript.Enabled"/>); no
    /// recompile or resubscription. Turning it off cancels its runs in progress with <see cref="CancelReasons.TurnedOff"/>.
    /// </summary>
    public void SetEnabled(string scriptPath, bool enabled)
    {
        UserScript[] scripts;
        lock (ActivationGate) scripts = _userScripts.Concat(_preparing).Where(s => s.CompiledScript.FilePath == scriptPath).Distinct().ToArray();
        foreach (var script in scripts) script.SetEnabled(enabled);
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
            return Task.WhenAll(entries.Where(e => e.ScriptClass.Script is { Active: true, Enabled: true }).Select(e => RunEntry(e, trigger, payload)));
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
            if (found is null)
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
                lock (ActivationGate)
                {
                    EnsureCanActivate(replacement, epoch);
                    var index = _userScripts.IndexOf(found);
                    if (index < 0 || !found.Active)
                        throw new InvalidOperationException("Script was unloaded during update");
                    found.Invalidate();
                    replacement.Active = true;
                    _preparing.Remove(replacement);
                    _userScripts[index] = replacement;
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

            found.SetCancelReason(CancelReasons.Reloaded);
            try { await found.ClearFromTrackingAsync(); } finally { found.Unload(); }
            replacement.Start();
        }
        finally { _updates.Release(); }
    }

    public async Task DeleteScriptAsync(string scriptPath)
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
                try { script.CancelLifetime(CancelReasons.Reloaded); } catch (Exception error) { errors.Add(error); }
            await _updates.WaitAsync();
            try
            {
                try { await CleanupScriptsAsync(deleting); } catch (Exception error) { errors.Add(error); }
                try { History?.Forget(scriptPath); } catch (Exception error) { errors.Add(error); }
            }
            finally { _updates.Release(); }
        }
        finally { lock (ActivationGate) _deletingPaths.Remove(scriptPath); }
        if (errors.Count > 0) throw new AggregateException(errors);
    }
}
