using System.Security.Cryptography;
using System.Text;

namespace HassSharp;

public class UserScriptCacheProvider
{
    string GetCacheDir()
    {
        var dir = Path.Combine(HassPath.UserScripts, ".cache");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return dir;
    }

    string GetCachePathForFile(string filePath, string extension)
    {
        var fileName = Path.GetFileName(filePath);
        return Path.Combine(GetCacheDir(), fileName + extension);
    }

    public string ComputeHash(string filePath, string source)
    {
        using var sha = SHA256.Create();

        var pathBytes = Encoding.UTF8.GetBytes(filePath);
        sha.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);

        var contentBytes = Encoding.UTF8.GetBytes(source);
        sha.TransformBlock(contentBytes, 0, contentBytes.Length, null, 0);

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    public async Task<byte[]?> GetFileCache(string filePath, string hash)
    {
        var dllPath = GetCachePathForFile(filePath, ".dll");
        var hashPath = GetCachePathForFile(filePath, ".hash");

        if (!File.Exists(dllPath) || !File.Exists(hashPath)) return null;

        var existingHash = await File.ReadAllTextAsync(hashPath);
        if (existingHash == hash)
        {
            return await File.ReadAllBytesAsync(dllPath);
        }

        return null;
    }

    public async Task SetFileCache(string filePath, string hash, byte[] assemblyBytes)
    {
        var dllPath = GetCachePathForFile(filePath, ".dll");
        var hashPath = GetCachePathForFile(filePath, ".hash");

        await File.WriteAllBytesAsync(dllPath, assemblyBytes);
        await File.WriteAllTextAsync(hashPath, hash);
    }
}