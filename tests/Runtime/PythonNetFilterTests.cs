using HassSharp;
using System.Reflection;
using System.Reflection.Emit;
using static VisualExecutionTests;

/// <summary>
/// <see cref="PythonNetAssemblies.Filter"/> on handler fields that stand in for Python.NET's <c>AssemblyManager.lhandler</c>:
/// a collectible assembly is no longer passed on, every other one still is once, installing again changes nothing, and a
/// field that cannot be written leaves the original handler subscribed and in place.
/// </summary>
static class PythonNetFilterTests
{
    static AssemblyLoadEventHandler? Writable;
    static readonly AssemblyLoadEventHandler ReadOnly = (_, _) => Interlocked.Increment(ref _readOnlyCalls);
    static int _writableCalls, _readOnlyCalls;
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    [RuntimeSuite("--python-net-filter", Order = 32)]
    public static void Run()
    {
        static void Load(AssemblyBuilderAccess access) => AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Filter_" + Guid.NewGuid().ToString("N")), access);

        Writable = (_, _) => Interlocked.Increment(ref _writableCalls);
        var original = Writable;
        AppDomain.CurrentDomain.AssemblyLoad += original;
        var field = typeof(PythonNetFilterTests).GetField(nameof(Writable), Static)!;
        try
        {
            Check(PythonNetAssemblies.Filter(field) && PythonNetAssemblies.Filter(field) && PythonNetAssemblies.Filter(field), "the filter installs, and installing again reports it installed");
            Check(!ReferenceEquals(Writable, original), "the field holds the filter");
            _writableCalls = 0;
            Load(AssemblyBuilderAccess.Run);
            Check(_writableCalls == 1, $"a non-collectible assembly reached the handler {_writableCalls} times, not once");
            Load(AssemblyBuilderAccess.RunAndCollect);
            Check(_writableCalls == 1, "a collectible assembly reached the handler");
        }
        finally
        {
            // As Python.NET's Shutdown does: remove whatever the field holds.
            AppDomain.CurrentDomain.AssemblyLoad -= Writable;
            AppDomain.CurrentDomain.AssemblyLoad -= original;
        }
        _writableCalls = 0;
        Load(AssemblyBuilderAccess.Run);
        Check(_writableCalls == 0, "removing the field's handler did not remove the filter");

        // static readonly: the field cannot be written after the type is initialised, as if Python.NET made it readonly.
        AppDomain.CurrentDomain.AssemblyLoad += ReadOnly;
        var readOnly = typeof(PythonNetFilterTests).GetField(nameof(ReadOnly), Static)!;
        try
        {
            Check(!PythonNetAssemblies.Filter(readOnly) && !PythonNetAssemblies.Filter(readOnly), "a field that cannot be written is reported, again on a later call, and does not throw");
            _readOnlyCalls = 0;
            Load(AssemblyBuilderAccess.RunAndCollect);
            Load(AssemblyBuilderAccess.Run);
            Check(_readOnlyCalls == 2, $"after a failed attempt the original handler must still see every assembly once: {_readOnlyCalls} calls for 2 loads");
        }
        finally { AppDomain.CurrentDomain.AssemblyLoad -= ReadOnly; }
        Console.WriteLine("PASS python.net filter: collectible assemblies are kept from the handler, others reach it once; installing twice changes nothing; a field that cannot be written leaves the original in place");
    }
}
