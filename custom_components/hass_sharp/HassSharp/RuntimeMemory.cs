using System.Runtime.Loader;

namespace HassSharp;

/// <summary>
/// What the .NET side of the process holds, for the debug line Python logs after each setup of the entry
/// (<c>memory_report.py</c>): it tells a leak of automations (collectible assemblies, load contexts) from growth of the
/// GC heap and from native memory. Only read when debug logging is on: it forces a full collection.
/// </summary>
static class RuntimeMemory
{
    public static string Report()
    {
        // Before the forced collection: what the GC has committed and holds as it runs.
        var running = GC.GetGCMemoryInfo();
        var live = GC.GetTotalMemory(forceFullCollection: true);
        var collected = GC.GetGCMemoryInfo();
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        var contexts = AssemblyLoadContext.All.ToArray();
        static string MiB(long bytes) => (bytes / 1048576.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        return $"dotnet live {MiB(live)} MiB, committed {MiB(running.TotalCommittedBytes)} then {MiB(collected.TotalCommittedBytes)} MiB, "
            + $"heap {MiB(running.HeapSizeBytes)} then {MiB(collected.HeapSizeBytes)} MiB, assemblies {assemblies.Length} "
            + $"(collectible {assemblies.Count(a => a.IsCollectible)}), load contexts {contexts.Length} "
            + $"(collectible {contexts.Count(c => c.IsCollectible)}), python.net hosted {PythonNetAssemblies.PythonHosted}, "
            + $"python.net filter installed {PythonNetAssemblies.IgnoreCollectible()}";
    }
}
