using System.Collections.Concurrent;

namespace ClaudeHomeServer.Services;

// Диагностика жизненного цикла SignalR-соединений.
// Пишет connect/disconnect с длительностью соединения, транспортом и причиной
// разрыва в data/logs/connections.log. Нужна для отлова регулярных разрывов,
// из-за которых UI мигает онлайн/офлайн (onreconnecting → notifyOffline).
//
// Как читать лог:
//   - длительность DISCONNECT кластеризуется около ~30с → таймаут keep-alive/ClientTimeout
//   - разброс длительностей + reason с сетевой ошибкой → обрыв канала (прокси/VPN/Wi-Fi)
//   - transport != WebSockets → фолбэк-транспорт «хлопает», смотреть почему не поднялся WS
public class ConnectionDiagnostics
{
    private readonly string _logFile;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<string, DateTime> _connectedAt = new();
    private int _active;

    public ConnectionDiagnostics(IConfiguration config)
    {
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        var logDir = Path.Combine(dataDir, "logs");
        Directory.CreateDirectory(logDir);
        _logFile = Path.Combine(logDir, "connections.log");
    }

    public void RecordConnect(string connectionId, string transport, string? user)
    {
        _connectedAt[connectionId] = DateTime.UtcNow;
        var active = Interlocked.Increment(ref _active);
        Write($"CONNECT    {Short(connectionId)} transport={transport,-14} user={user ?? "-"} active={active}");
    }

    public void RecordDisconnect(string connectionId, Exception? exception)
    {
        var active = Interlocked.Decrement(ref _active);
        var dur = "?";
        if (_connectedAt.TryRemove(connectionId, out var start))
            dur = $"{(DateTime.UtcNow - start).TotalSeconds:F1}s";
        var reason = exception is null ? "clean-close" : $"{exception.GetType().Name}: {exception.Message}";
        Write($"DISCONNECT {Short(connectionId)} after={dur,-7} active={active} reason={reason}");
    }

    // Короткий префикс ConnectionId — для читаемости (полный id 32+ символов)
    private static string Short(string connId) => connId.Length > 8 ? connId[..8] : connId;

    private void Write(string line)
    {
        var stamped = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z  {line}{Environment.NewLine}";
        try { lock (_writeLock) File.AppendAllText(_logFile, stamped); }
        catch { /* лог не критичен — не валим соединение из-за ошибки записи */ }
    }
}
