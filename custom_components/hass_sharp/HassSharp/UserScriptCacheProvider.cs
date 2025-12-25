using System.Security.Cryptography;
using System.Text;

namespace HassSharp;

public class UserScriptCacheProvider
{
    const string CacheDllName = "hass_sharp_user_scripts.dll";
    const string CacheHashName = "hass_sharp_user_scripts.hash";

    // TODO: Move somewhere else
    readonly string CacheDllPath = Path.Combine(HassPath.UserScripts, CacheDllName);
    readonly string CacheHashPath = Path.Combine(HassPath.UserScripts, CacheHashName);

    public string ComputeHash(string[] filePaths, string[] sources)
    {
        using var sha = SHA256.Create();

        for (var i = 0; i < sources.Length; i++)
        {
            var pathBytes = Encoding.UTF8.GetBytes(filePaths[i]);
            sha.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);

            var contentBytes = Encoding.UTF8.GetBytes(sources[i]);
            sha.TransformBlock(contentBytes, 0, contentBytes.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    public async Task<byte[]?> GetScriptsCached(string hash)
    {
        if (!File.Exists(CacheDllPath) || !File.Exists(CacheHashPath)) return null;

        var existingHash = await File.ReadAllTextAsync(CacheHashPath);
        if (existingHash == hash)
        {
            var cachedBytes = await File.ReadAllBytesAsync(CacheDllPath);
            return cachedBytes;
        }

        return null;
    }

    public async Task SetScriptsCache(string hash, byte[] assemblyBytes)
    {
        await File.WriteAllBytesAsync(CacheDllPath, assemblyBytes);
        await File.WriteAllTextAsync(CacheHashPath, hash);
    }
}
