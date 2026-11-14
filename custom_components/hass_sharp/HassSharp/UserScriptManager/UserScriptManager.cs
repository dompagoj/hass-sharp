namespace HassSharp;

record TriggerContext(HasEntityState State);

class UserScriptManager
{
    public RunHistoryStore? History { get; init; }

    /// <param name="payload">The normalised trigger payload; <c>{platform: "manual"}</c> when omitted.</param>
    /// <summary>
    /// Run now: looks the automation up and starts the run under the activation gate, as a trigger does, so it cannot
    /// race an unload or a save; refused while the automation is not active or is stopping. Answers once the run's first
    /// record is written (a storage failure is thrown), so the run id it returns is one the history has.
    /// </summary>
    public string DispatchManual(string automationId, string className, string method, System.Text.Json.JsonElement? payload = null)
    {
        var runId = Guid.NewGuid().ToString("N");
        var recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task task;
        lock (ActivationGate)
        {
            var script = _userScripts.SingleOrDefault(s => s.AutomationId == automationId);
            if (script is not { Active: true } || script.LifetimeToken.IsCancellationRequested) throw new InvalidOperationException(NotRunning);
            task = script.Classes.Single(c => c.ClassName == className).RunMethod(method, InvocationCause.Manual, runId, payload, recorded);
        }
        _ = Observe(task);
        recorded.Task.GetAwaiter().GetResult();
        return runId;
    }
    /// <summary>Why Run now is refused while an automation is being replaced, unloaded or stopped.</summary>
    internal const string NotRunning = "The automation is not active right now; try again in a moment";

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
    /// <summary>The drains of revisions a save replaced, still going: unloading waits for them as for its own scripts.</summary>
    readonly HashSet<Task> _retiring = [];
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
        UserScript[] scripts; Task[] retiring;
        lock (ActivationGate)
        {
            _epoch++;
            _accepting = false;
            scripts = _userScripts.Concat(_preparing).Distinct().ToArray();
            foreach (var script in scripts) script.Invalidate();
            _userScripts.Clear();
            _preparing.Clear();
            retiring = [.. _retiring];
        }
        List<Exception> errors = [];
        foreach (var script in scripts)
            try { script.CancelLifetime(reason); } catch (Exception error) { errors.Add(error); }
        return UnloadAsync(scripts, retiring, errors);
    }

    async Task UnloadAsync(UserScript[] scripts, Task[] retiring, List<Exception> errors)
    {
        try { await CleanupScriptsAsync(scripts, errors); }
        finally
        {
            // A save's replaced revision drains on its own; its failures are the save's, already logged there.
            try { await Task.WhenAll(retiring); } catch { }
        }
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
    /// Turns the loaded script at <paramref name="scriptPath"/> on or off (see <see cref="UserScript.Enabled"/>) with no
    /// recompile: turning it off cancels its runs in progress with <see cref="CancelReasons.TurnedOff"/> and detaches its
    /// Home Assistant triggers; turning it on attaches them again. The visual store's flag is also read when a document
    /// is compiled, which is safe because compile/activate and this call (set_enabled) all hold the visual gate.
    /// </summary>
    public async Task SetEnabledAsync(string scriptPath, bool enabled)
    {
        UserScript[] scripts;
        lock (ActivationGate) scripts = _userScripts.Concat(_preparing).Where(s => s.CompiledScript.FilePath == scriptPath).Distinct().ToArray();
        foreach (var script in scripts) await script.SetEnabledAsync(enabled);
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
            // While Home Assistant stops, runs are cancelled and the scripts stay active: a change then starts no run.
            return Task.WhenAll(entries.Where(e => e.ScriptClass.Script is { Active: true, Enabled: true } script && !script.LifetimeToken.IsCancellationRequested)
                .Select(e => RunEntry(e, trigger, payload)));
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
            UserScript? found;
            lock (ActivationGate) found = _userScripts.SingleOrDefault(s => s.CompiledScript.Id() == compiled.Id());
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
            // The replaced revision's runs drain here; unloading meanwhile waits for them too (see _retiring).
            var retiring = found.ClearFromTrackingAsync();
            lock (ActivationGate) _retiring.Add(retiring);
            try { await retiring; }
            catch (Exception error)
            {
                // The replacement is live and receiving triggers: a cleanup failure of the old revision is no failure of the save.
                Logger.Warn($"{found.CompiledScript.FileName}: cleaning up the replaced revision failed; the new revision runs: {error.GetBaseException().Message}");
            }
            finally
            {
                lock (ActivationGate) _retiring.Remove(retiring);
                try { found.Unload(); } catch (Exception error) { Logger.Warn($"{found.CompiledScript.FileName}: unloading the replaced revision failed: {error.GetBaseException().Message}"); }
            }
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
        // The document is deleted and its scripts no longer run: a cleanup failure is logged, and the delete succeeds.
        if (errors.Count > 0) Logger.Warn($"{Path.GetFileName(scriptPath)}: cleaning up the deleted automation failed: " + string.Join("; ", errors.Select(e => e.GetBaseException().Message)));
    }
}
