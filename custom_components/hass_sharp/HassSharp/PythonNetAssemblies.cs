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
/// </summary>
static class PythonNetAssemblies
{
    static readonly object Gate = new();
    static AssemblyLoadEventHandler? _filter;

    static FieldInfo? HandlerField => typeof(Python.Runtime.PyObject).Assembly.GetType("Python.Runtime.AssemblyManager")
        ?.GetField("lhandler", BindingFlags.Static | BindingFlags.NonPublic);

    /// <summary>
    /// Passes Python.NET's assembly handler only assemblies that are not collectible. Safe to call again: it installs once
    /// per handler Python.NET registered.
    /// </summary>
    /// <returns>Whether collectible assemblies are kept from Python.NET: false when it has not registered its handler (it
    /// does not host this runtime, as in tests) or when its internals are not what this expects.</returns>
    internal static bool IgnoreCollectible()
    {
        lock (Gate)
        {
            var field = HandlerField;
            if (field?.GetValue(null) is not AssemblyLoadEventHandler handler) return false;
            if (ReferenceEquals(handler, _filter)) return true;
            AssemblyLoadEventHandler filter = (sender, args) => { if (!args.LoadedAssembly.IsCollectible) handler(sender, args); };
            AppDomain.CurrentDomain.AssemblyLoad += filter;
            AppDomain.CurrentDomain.AssemblyLoad -= handler;
            field.SetValue(null, filter);
            _filter = filter;
            return true;
        }
    }

    /// <summary>Whether Python.NET hosts this runtime: then <see cref="IgnoreCollectible"/> must have worked.</summary>
    internal static bool PythonHosted
    {
        get { try { return Python.Runtime.PythonEngine.IsInitialized; } catch (Exception) { return false; } }
    }
}
