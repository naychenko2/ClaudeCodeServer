using System.ComponentModel;

namespace ClaudeHomeServer.HandsBridge;

/// <summary>
/// Лог моста рядом с логами агента (<c>AiHomeAgent\logs\hands.log</c>): запуск, отказы гейта,
/// коды ошибок Windows при запуске программ. stdout занят MCP, stderr читает CLI и никуда не
/// сохраняет — без файла разбор сбоя на машине пользователя превращается в гадание.
/// Сбой записи лога никогда не ломает инструмент.
/// </summary>
internal static class HandsLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly Lock Sync = new();
    private static string? s_path;

    /// <summary>Путь по умолчанию: мост живёт в <c>AiHomeAgent\hands</c>, логи агента — в соседнем <c>logs</c>.</summary>
    public static string DefaultPath =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "logs", "hands.log"));

    /// <summary>Пока не вызван — лог не пишется (рантайм-тесты грузят мост в свой процесс).</summary>
    public static void Configure(string? path) => s_path = string.IsNullOrWhiteSpace(path) ? null : path;

    public static void Write(string message)
    {
        var path = s_path;
        if (path is null)
            return;
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Лог недоступен (второй мост держит файл, нет прав) — инструмент работает дальше
        }
    }

    /// <summary>Код ошибки Windows с текстом ОС: «5 (Отказано в доступе.)».</summary>
    public static string Win32(int error) => $"{error} ({new Win32Exception(error).Message})";
}
