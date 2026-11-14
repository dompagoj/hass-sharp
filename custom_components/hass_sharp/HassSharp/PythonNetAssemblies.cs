using System.Reflection;

namespace HassSharp;

/// <summary>
/// Keeps Python.NET from holding on to the automations' collectible assemblies.
/// <para>
/// Python.NET, which hosts this runtime in Home Assistant, records every assembly loaded into the process, in any load
/// context, so that Python can import from it: its <c>AssemblyManager</c> handles <see cref="AppDomain.AssemblyLoad"/>,
/// keeps each assembly in a queue and its namespaces in a table, and never lets go of them. An assembly that is referenced
/// cannot be collected, so its load context never unloads even after nothing else uses it: every reload of the entry, and
/// every save, kept each automation's assembly, its compiled code and its types (about 10 MiB per reload with six
/// automations). Python never imports from an automation's assembly, so a collectible assembly is not passed on to
/// Python.NET; every other assembly still is.
/// </para>
/// <para>
/// Python.NET has no setting for this. Its handler is a private field; the field is given the filtering handler, so
/// Python.NET's own shutdown removes it as it would its own.
/// </para>
/// <para>
/// On a Python.NET upgrade (pinned in <c>manifest.json</c> and <c>HassSharp.csproj</c>): check that
/// <c>Python.Runtime.AssemblyManager</c> still has the static <c>lhandler</c> field of type
/// <see cref="AssemblyLoadEventHandler"/> that it subscribes to <see cref="AppDomain.AssemblyLoad"/> and unsubscribes in
/// <c>Shutdown</c>, and that neither it nor <c>ClassManager</c> gained another place that keeps assemblies or types. The
/// reload memory test (<c>tests/Runtime/ReloadMemoryTests.cs</c>) fails when the field is gone or the handler keeps them.
/// </para>
/// </summary>
static class PythonNetAssemblies
{
    static readonly object Gate = new();
    // Per handler field: the filter installed in it, or null after an attempt that failed (then it is not tried again).
    static readonly Dictionary<FieldInfo, AssemblyLoadEventHandler?> Installed = [];
    static bool _warned;

    static FieldInfo? HandlerField
    {
        get
        {
            try
            {
                return typeof(Python.Runtime.PyObject).Assembly.GetType("Python.Runtime.AssemblyManager")
                    ?.GetField("lhandler", BindingFlags.Static | BindingFlags.NonPublic);
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>
    /// Passes Python.NET's assembly handler only assemblies that are not collectible. Never throws, and safe to call
    /// again: it installs once per handler Python.NET registered.
    /// </summary>
    /// <returns>Whether collectible assemblies are kept from Python.NET: false when it has not registered its handler (it
    /// does not host this runtime, as in tests) or when its internals are not what this expects.</returns>
    internal static bool IgnoreCollectible() => HandlerField is { } field && Filter(field);

    /// <summary>
    /// Wraps the handler in <paramref name="field"/> (a static <see cref="AssemblyLoadEventHandler"/> subscribed to
    /// <see cref="AppDomain.AssemblyLoad"/>) so that it is not called for collectible assemblies. On any failure
    /// everything is left as it was: the original handler stays subscribed and in the field.
    /// </summary>
    internal static bool Filter(FieldInfo field)
    {
        lock (Gate)
        {
            try
            {
                if (Installed.TryGetValue(field, out var installed) && installed == null) return false;
                if (field.GetValue(null) is not AssemblyLoadEventHandler handler) return false;
                if (ReferenceEquals(handler, installed)) return true;
                AssemblyLoadEventHandler filter = (sender, args) => { if (!args.LoadedAssembly.IsCollectible) handler(sender, args); };
                // The field first: if it cannot be written, nothing has changed yet.
                field.SetValue(null, filter);
                var added = false;
                try
                {
                    AppDomain.CurrentDomain.AssemblyLoad += filter; added = true;
                    AppDomain.CurrentDomain.AssemblyLoad -= handler;
                }
                catch
                {
                    if (added) AppDomain.CurrentDomain.AssemblyLoad -= filter;
                    field.SetValue(null, handler);
                    throw;
                }
                Installed[field] = filter;
                return true;
            }
            catch (Exception)
            {
                Installed[field] = null;
                return false;
            }
        }
    }

    /// <summary>
    /// <see cref="IgnoreCollectible"/>, warning once (through <paramref name="warn"/>) when it could not be done while
    /// Python.NET hosts this runtime.
    /// </summary>
    internal static void IgnoreCollectible(Action<string> warn)
    {
        if (IgnoreCollectible() || !PythonHosted) return;
        lock (Gate) { if (_warned) return; _warned = true; }
        try { warn("Python.NET keeps every automation assembly it sees: each reload of HassSharp and each save will keep some memory until Home Assistant restarts"); }
        catch (Exception) { }
    }

    /// <summary>Whether Python.NET hosts this runtime: then <see cref="IgnoreCollectible()"/> must have worked.</summary>
    internal static bool PythonHosted
    {
        get { try { return Python.Runtime.PythonEngine.IsInitialized; } catch (Exception) { return false; } }
    }
}
