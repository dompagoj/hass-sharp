using System.Reflection;
using System.Runtime.InteropServices;

namespace HassSharp;

/// <summary>Python.NET does not process our deps.json native probing paths.</summary>
internal static class SqliteNative
{
    // Static initialization registers once, even when stores are opened concurrently.
    static SqliteNative() => NativeLibrary.SetDllImportResolver(
        typeof(SQLitePCL.SQLite3Provider_e_sqlite3).Assembly, Resolve);

    internal static void Initialize() { }

    static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != "e_sqlite3") return IntPtr.Zero;
        var fileName = OperatingSystem.IsWindows() ? "e_sqlite3.dll"
            : OperatingSystem.IsMacOS() ? "libe_sqlite3.dylib" : "libe_sqlite3.so";
        var directory = Path.GetDirectoryName(assembly.Location)!;
        // The runtime RID includes both process architecture and libc (linux-musl on Alpine).
        var nativePath = Path.Combine(directory, "runtimes", RuntimeInformation.RuntimeIdentifier,
            "native", fileName);
        if (File.Exists(nativePath)) return NativeLibrary.Load(nativePath);
        // RID-specific publishes flatten native assets beside the managed assemblies.
        var flatPath = Path.Combine(directory, fileName);
        if (File.Exists(flatPath)) return NativeLibrary.Load(flatPath);
        throw new DllNotFoundException(
            $"Packaged SQLite native library missing for {RuntimeInformation.RuntimeIdentifier}. Expected {nativePath} or {flatPath}. Republish HassSharp with its native runtime assets.");
    }
}
