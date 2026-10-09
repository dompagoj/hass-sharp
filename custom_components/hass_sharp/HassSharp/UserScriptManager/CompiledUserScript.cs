using System.Reflection;
using System.Runtime.Loader;

namespace HassSharp;

public class CompiledUserScript
{
    bool _unloaded;

    public required string FilePath { get; set; }
    public required string FileName { get; set; }
    public required string SourceCode { get; init; }

    public required Assembly Assembly { get; init; }

    public required bool WrittenToDisk { get; set; }

    public string Id() => FilePath;
    public string Slug() => FileName;

    public void SetDirty() => WrittenToDisk = false;

    public Task WriteToDisk() => File.WriteAllTextAsync(FilePath, SourceCode);

    public void RenameOnDisk(string newSlug)
    {
        var newPath = UserScriptManager.GetScriptFilePathFromSlug($"{newSlug}.cs");
        if (FilePath == newPath) return;

        // Move without overwriting, and keep the original identity if the move fails.
        File.Move(FilePath, newPath);
        FileName = newSlug;
        FilePath = newPath;
    }

    public async ValueTask WriteIfDirty()
    {
        if (WrittenToDisk) return;
        Logger.Debug($"Writing script to ${FilePath}");
        await WriteToDisk();
        WrittenToDisk = true;
    }

    public void DeleteFromDisk()
    {
        File.Delete(FilePath);
    }

    public void Unload()
    {
        if (_unloaded) return;
        var loadContext = AssemblyLoadContext.GetLoadContext(Assembly);
        if (loadContext?.IsCollectible == true) loadContext.Unload();
        _unloaded = true;
    }
}

public class UserScriptDTO
{
    public required string FileName { get; init; }
    public required UserScriptClassDto[] Classes { get; init; }
}

public class UserScriptClassDto
{
    public required string Name { get; init; }
    public required string[] Methods { get; init; }
}

public class UserScriptSourceDTO
{
    public required string FileName { get; init; }
    public required string Source { get; init; }
}
