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

    public static readonly AsyncLocal<InvocationCause> CurrentCause = new();
    public static readonly AsyncLocal<TriggerContext?> CurrentTrigger = new();

    internal object ActivationGate { get; } = new();
    readonly SemaphoreSlim _updates = new(1, 1);
    readonly List<UserScript> _userScripts = new();
    public DependencyTracking DependencyTracking { get; } = new();
    public ScriptSyncRunner SyncRunner { get; } = new();

    public List<UserScript> GetUserScripts() => _userScripts;

    public UserScript? GetUserScript(string scriptSlug) =>
        _userScripts.FirstOrDefault(s => s.CompiledScript.Slug() == scriptSlug);

    public void UnloadUserScripts()
    {
        UserScript[] scripts;
        lock (ActivationGate)
        {
            scripts = _userScripts.ToArray();
            foreach (var script in scripts) script.Active = false;
            _userScripts.Clear();
        }
        SyncRunner.Clear(scripts);
        DependencyTracking.Clear();

        foreach (var script in scripts)
        {
            script.Unload();
        }
    }

    public void RemoveScriptFromList(UserScript script)
    {
        lock (ActivationGate)
        {
            script.Active = false;
            _userScripts.Remove(script);
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
            .Where(t => baseType.IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => new UserScriptClass(t, userScript))
            .ToArray();

        userScript.Classes = userScriptClasses;
        if (!staged) _userScripts.Add(userScript);
        return userScript;
    }

    public Task LoadUserScripts(IEnumerable<CompiledUserScript> compiledScripts)
        => Task.WhenAll(compiledScripts.Select(s => LoadUserScript(s).ConfigureAndStart()));


    public void TrackEntityCall(string entityId, UserScriptClass scriptClass, string method) =>
        DependencyTracking.TrackEntityCall(entityId, scriptClass, method);

    public Task RunEntries(string entityId, TriggerContext trigger)
    {
        lock (ActivationGate)
        {
            var entries = DependencyTracking.GetEntries(entityId);
            return Task.WhenAll(entries.Where(e => e.ScriptClass.Script.Active).Select(e => RunEntry(e, trigger)));
        }
    }

    public async Task RunEntry(DependencyEntry entry, TriggerContext trigger)
    {
        var previousTrigger = CurrentTrigger.Value;
        CurrentTrigger.Value = trigger;
        try
        {
            await entry.ScriptClass.RunMethod(entry.MethodName, InvocationCause.Entity);
        }
        finally
        {
            CurrentTrigger.Value = previousTrigger;
        }
    }

    public async Task UpdateUserScript(CompiledUserScript compiled)
    {
        await _updates.WaitAsync();
        try
        {
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
                replacement.Prepare();
                await replacement.WriteIfDirty();
                lock (ActivationGate)
                {
                    var index = _userScripts.IndexOf(found);
                    if (index < 0 || !found.Active)
                        throw new InvalidOperationException("Script was unloaded during update");
                    found.Active = false;
                    replacement.Active = true;
                    _userScripts[index] = replacement;
                }
            }
            catch
            {
                replacement?.ClearFromTracking();
                compiled.Unload();
                throw;
            }

            found.ClearFromTracking();
            found.Unload();
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
                loadedScript.ClearFromTracking();
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

    public void DeleteScript(string scriptPath)
    {
        var found = _userScripts.Find(s => s.CompiledScript.FilePath == scriptPath);

        if (found is null)
        {
            throw new Exception("Script not found");
        }

        found.FullClean();
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
