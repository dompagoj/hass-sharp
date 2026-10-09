using System.Globalization;
using System.Text;

namespace HassSharp;

record TriggerContext(HasEntityState State);

class UserScriptManager
{
    public static readonly AsyncLocal<TriggerContext?> CurrentTrigger = new();

    readonly List<UserScript> _userScripts = new();
    public DependencyTracking DependencyTracking { get; } = new();
    public ScriptSyncRunner SyncRunner { get; } = new();

    public List<UserScript> GetUserScripts() => _userScripts;

    public UserScript? GetUserScript(string scriptSlug) =>
        _userScripts.FirstOrDefault(s => s.CompiledScript.Slug() == scriptSlug);

    public void UnloadUserScripts()
    {
        var scripts = _userScripts.ToArray();
        PyInterop.UnSubscribeAllFromEntityTracking();
        DependencyTracking.Clear();
        _userScripts.Clear();
        SyncRunner.Clear(scripts);

        foreach (var script in scripts)
        {
            script.Unload();
        }
    }

    public void RemoveScriptFromList(UserScript script)
    {
        _userScripts.Remove(script);
    }

    public UserScript LoadUserScript(CompiledUserScript compiledScript)
    {
        var baseType = typeof(Automation);

        var userScript = new UserScript
        {
            CompiledScript = compiledScript,
            Classes = null!,
            ScriptManager = this,
        };
        var userScriptClasses = compiledScript.Assembly.GetTypes()
            .Where(t => baseType.IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => new UserScriptClass(t, userScript))
            .ToArray();

        userScript.Classes = userScriptClasses;
        _userScripts.Add(userScript);
        return userScript;
    }

    public Task LoadUserScripts(IEnumerable<CompiledUserScript> compiledScripts)
        => Task.WhenAll(compiledScripts.Select(s => LoadUserScript(s).Initialize()));


    public void TrackEntityCall(string entityId, UserScriptClass scriptClass, string method) =>
        DependencyTracking.TrackEntityCall(entityId, scriptClass, method);

    public Task RunEntries(string entityId, TriggerContext trigger)
    {
        var entries = DependencyTracking.GetEntries(entityId);
        return Task.WhenAll(entries.Select(async e => await RunEntry(e, trigger)));
    }

    public async Task RunEntry(DependencyEntry entry, TriggerContext trigger)
    {
        var previousTrigger = CurrentTrigger.Value;
        CurrentTrigger.Value = trigger;
        try
        {
            await entry.ScriptClass.RunMethod(entry.MethodName);
        }
        finally
        {
            CurrentTrigger.Value = previousTrigger;
        }
    }

    public async Task UpdateUserScript(CompiledUserScript compiled)
    {
        var foundIdx = _userScripts.FindIndex(s => s.CompiledScript.Id() == compiled.Id());
        if (foundIdx == -1)
        {
            compiled.Unload();
            throw new("Script not found");
        }

        var found = _userScripts[foundIdx];

        UserScript? loadedScript = null;

        try
        {
            // Keep the old assembly loaded until the replacement has initialized,
            // so it can still be restored if the update fails.
            found.ClearFromTracking();
            loadedScript = LoadUserScript(compiled);

            Logger.Info("Initializing newly loaded scripts");
            await loadedScript.Initialize();
            await loadedScript
                .WriteIfDirty(); // TODO maybe make this a single call? its easy to forget not to write to disk
            found.Unload();
        }
        catch (Exception ex)
        {
            // Restore the original script
            Logger.Error($"Failed to update user script {ex.Message} {ex.InnerException?.Message}");
            if (loadedScript != null)
            {
                loadedScript.ClearFromTracking();
            }

            compiled.Unload();
            found.RenewLifetime();
            if (!_userScripts.Contains(found))
                _userScripts.Add(found);
            await found.Initialize();
            await found.WriteToDisk();
            throw;
        }

        DependencyTracking.Debug();
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
            await loadedScript.Initialize();
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
