namespace RuianFeedParser.Services;

/// <summary>
/// Minimal structured logger. Writes to console with timestamps and levels.
/// Output format is machine-parseable enough for log aggregators while staying
/// readable in a terminal. Replace the sink with whatever you need (file, syslog, etc.)
/// </summary>
public enum LogLevel { Debug, Info, Warn, Error }

public sealed class Logger
{
    private readonly string _component;
    private static LogLevel _minLevel = LogLevel.Info;
    private static readonly Lock _lock = new(); // System.Threading.Lock — C# 14 / net10

    public static void SetLevel(LogLevel level) => _minLevel = level;

    public Logger(string component) => _component = component;

    public void Debug(string msg) => Write(LogLevel.Debug, msg);
    public void Info(string msg)  => Write(LogLevel.Info,  msg);
    public void Warn(string msg)  => Write(LogLevel.Warn,  msg);
    public void Error(string msg, Exception? ex = null)
    {
        Write(LogLevel.Error, msg);
        if (ex != null) Write(LogLevel.Error, ex.ToString());
    }

    private void Write(LogLevel level, string msg)
    {
        if (level < _minLevel) return;
        var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z [{level,-5}] [{_component}] {msg}";
        lock (_lock)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = level switch
            {
                LogLevel.Warn  => ConsoleColor.Yellow,
                LogLevel.Error => ConsoleColor.Red,
                LogLevel.Debug => ConsoleColor.DarkGray,
                _              => prev
            };
            Console.WriteLine(line);
            Console.ForegroundColor = prev;
        }
    }

    /// <summary>Write a progress line that overwrites itself. Ends with a newline when done=true.</summary>
    public void Progress(string msg, bool done = false)
    {
        if (_minLevel > LogLevel.Info) return;
        lock (_lock)
        {
            Console.Write($"\r  {msg,-70}");
            if (done) Console.WriteLine();
        }
    }
}
