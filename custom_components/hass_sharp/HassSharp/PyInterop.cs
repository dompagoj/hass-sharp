using System.Runtime.CompilerServices;

namespace HassSharp;

using GetEntity = Func<string, HasEntityState?>;

public class HasEntityState
{
    public string EntityId { get; set; } = null!;
    public string Domain { get; set; } = null!;
    public string ObjectId { get; set; } = null!;
    public string State { get; set; } = null!;
    public Dictionary<string, object> Attributes { get; set; } = null!;
    public double LastChanged { get; set; }
    public double LastReported { get; set; }
    internal HasEntityState? OldState { get; set; }
}

public enum PyLogLevel
{
    Debug = 10,
    Info = 20,
    Warn = 30,
    Error = 40,
    Critical = 50,
}

public static class Logger
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Log(PyLogLevel level, string msg) => PyInterop.Log((int)level, msg);

    public static PyLogLevel Level => (PyLogLevel)PyInterop.LogLevel;

    public static void Info(string msg) => Log(PyLogLevel.Info, msg);
    public static void Warn(string msg) => Log(PyLogLevel.Warn, msg);
    public static void Debug(string msg) => Log(PyLogLevel.Debug, msg);
    public static void Error(string msg) => Log(PyLogLevel.Error, msg);
}

public static class PyInterop
{
    public static Action<int, string> Log { get; set; } = null!;
    public static int LogLevel { get; set; }
    public static GetEntity Entity { get; set; } = null!;
    public static Action<string, string, string?> CallService { get; set; } = null!;
    public static Action<string> UnSubscribeFromEntityTracking { get; set; } = null!;
    public static Action<string> SubscribeToEntityTracking { get; set; } = null!;
    public static Action UnSubscribeAllFromEntityTracking { get; set; } = null!;
}
