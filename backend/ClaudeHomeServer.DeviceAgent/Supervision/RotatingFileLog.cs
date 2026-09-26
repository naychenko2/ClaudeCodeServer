using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.DeviceAgent.Supervision;

/// <summary>
/// Журнал супервизора и его дочернего в файле: у агента из автозапуска нет консоли, а
/// разбирать «почему откатился» (Р7, AD-9) без журнала нечем. Один файл плюс одна
/// ротированная копия; провал записи журнала агента не роняет.
/// </summary>
internal sealed class RotatingFileLog(string file, long maxBytes = 5 * 1024 * 1024)
{
    private readonly object _gate = new();

    public string File { get; } = file;

    public void Append(string line)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File)!);
                var info = new FileInfo(File);
                if (info.Exists && info.Length > maxBytes) System.IO.File.Move(File, File + ".1", overwrite: true);
                System.IO.File.AppendAllText(File, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}

internal sealed class FileLoggerProvider(RotatingFileLog log) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(log);

    public void Dispose() { }

    private sealed class FileLogger(RotatingFileLog log) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var level = logLevel switch { LogLevel.Warning => "warn", LogLevel.Error or LogLevel.Critical => "fail", _ => "info" };
            log.Append($"[supervisor pid={Environment.ProcessId}] {level}: {formatter(state, exception)}"
                + (exception is null ? "" : " — " + exception.Message));
        }
    }
}
