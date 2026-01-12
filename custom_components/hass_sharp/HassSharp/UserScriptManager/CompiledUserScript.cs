using System.Reflection;

namespace HassSharp;

public class CompiledUserScript
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required string SourceCode { get; init; }

    public required Assembly Assembly { get; init; }

    public required bool WrittenToDisk { get; set; }

    public string Id() => FilePath;
    public string Slug() => FileName;

    public void SetDirty() => WrittenToDisk = false;

    public Task WriteToDisk() => File.WriteAllTextAsync(FilePath, SourceCode);

    public async ValueTask WriteIfDirty()
    {
        if (WrittenToDisk) return;
        Logger.Debug($"Writing script to ${FilePath}");
        await WriteToDisk();
        WrittenToDisk = true;
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
