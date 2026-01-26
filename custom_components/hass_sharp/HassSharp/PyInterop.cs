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

    static PyLogLevel? _level;

    public static PyLogLevel Level
    {
        get
        {
            _level ??= (PyLogLevel)PyInterop.LogLevel;

            return _level.Value;
        }
    }

    public static bool IsLevel(PyLogLevel level) => level >= Level;


    public static void Debug(ref DebugLogHandler handler)
    {
        if (handler.IsEnabled) Log(PyLogLevel.Debug, handler.ToStringAndClear());
    }

    public static void Debug(string msg)
    {
        if (IsLevel(PyLogLevel.Debug)) Log(PyLogLevel.Debug, msg);
    }

    public static void Info(ref InfoLogHandler handler)
    {
        if (handler.IsEnabled) Log(PyLogLevel.Info, handler.ToStringAndClear());
    }

    public static void Info(string msg)
    {
        if (IsLevel(PyLogLevel.Info)) Log(PyLogLevel.Info, msg);
    }

    public static void Warn(ref WarnLogHandler handler)
    {
        if (handler.IsEnabled) Log(PyLogLevel.Warn, handler.ToStringAndClear());
    }

    public static void Warn(string msg)
    {
        if (IsLevel(PyLogLevel.Warn)) Log(PyLogLevel.Warn, msg);
    }


    public static void Error(ref ErrorLogHandler handler)
    {
        if (handler.IsEnabled) Log(PyLogLevel.Error, handler.ToStringAndClear());
    }

    public static void Error(string msg)
    {
        if (IsLevel(PyLogLevel.Error)) Log(PyLogLevel.Error, msg);
    }
}

[InterpolatedStringHandler]
public ref struct InfoLogHandler
{
    private DefaultInterpolatedStringHandler _innerHandler;
    public bool IsEnabled { get; }

    public InfoLogHandler(int literalLength, int formattedCount, out bool isEnabled)
    {
        isEnabled = Logger.IsLevel(PyLogLevel.Info);
        IsEnabled = isEnabled;
        _innerHandler = isEnabled
            ? new DefaultInterpolatedStringHandler(literalLength, formattedCount)
            : default;
    }

    public void AppendLiteral(string value) => _innerHandler.AppendLiteral(value);
    public void AppendFormatted<T>(T value) => _innerHandler.AppendFormatted(value);
    public string ToStringAndClear() => _innerHandler.ToStringAndClear();
}

[InterpolatedStringHandler]
public ref struct DebugLogHandler
{
    private DefaultInterpolatedStringHandler _innerHandler;
    public bool IsEnabled { get; }

    public DebugLogHandler(int literalLength, int formattedCount, out bool isEnabled)
    {
        isEnabled = Logger.IsLevel(PyLogLevel.Debug);
        IsEnabled = isEnabled;
        _innerHandler = isEnabled
            ? new DefaultInterpolatedStringHandler(literalLength, formattedCount)
            : default;
    }

    public void AppendLiteral(string value) => _innerHandler.AppendLiteral(value);
    public void AppendFormatted<T>(T value) => _innerHandler.AppendFormatted(value);
    public string ToStringAndClear() => _innerHandler.ToStringAndClear();
}

[InterpolatedStringHandler]
public ref struct WarnLogHandler
{
    private DefaultInterpolatedStringHandler _innerHandler;
    public bool IsEnabled { get; }

    public WarnLogHandler(int literalLength, int formattedCount, out bool isEnabled)
    {
        isEnabled = Logger.IsLevel(PyLogLevel.Warn);
        IsEnabled = isEnabled;
        _innerHandler = isEnabled
            ? new DefaultInterpolatedStringHandler(literalLength, formattedCount)
            : default;
    }

    public void AppendLiteral(string value) => _innerHandler.AppendLiteral(value);
    public void AppendFormatted<T>(T value) => _innerHandler.AppendFormatted(value);
    public string ToStringAndClear() => _innerHandler.ToStringAndClear();
}

[InterpolatedStringHandler]
public ref struct ErrorLogHandler
{
    private DefaultInterpolatedStringHandler _innerHandler;
    public bool IsEnabled { get; }

    public ErrorLogHandler(int literalLength, int formattedCount, out bool isEnabled)
    {
        isEnabled = Logger.IsLevel(PyLogLevel.Error);
        IsEnabled = isEnabled;
        _innerHandler = isEnabled
            ? new DefaultInterpolatedStringHandler(literalLength, formattedCount)
            : default;
    }

    public void AppendLiteral(string value) => _innerHandler.AppendLiteral(value);
    public void AppendFormatted<T>(T value) => _innerHandler.AppendFormatted(value);
    public string ToStringAndClear() => _innerHandler.ToStringAndClear();
}

public static class PyInterop
{
    public static Action<int, string> Log { get; set; } = null!;
    public static int LogLevel { get; set; }
    public static GetEntity Entity { get; set; } = null!;
    public static Action<string, string, string?, Action?> CallService { get; set; } = null!;
    public static Action<string> UnSubscribeFromEntityTracking { get; set; } = null!;
    public static Action<string> SubscribeToEntityTracking { get; set; } = null!;
    public static Action UnSubscribeAllFromEntityTracking { get; set; } = null!;
}