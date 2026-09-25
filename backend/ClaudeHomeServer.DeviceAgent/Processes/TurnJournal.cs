using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.DeviceAgent.Processes;

/// <summary>
/// Журнал живых ходов на диске: pid, время старта и каталог хода. Нужен одному сценарию —
/// агент умер, не успев убить ходы (kill -9, падение, выключение питания). При следующем
/// старте агент добивает оставшиеся группы, убирает их <c>sessions/&lt;pid&gt;</c> и
/// временные каталоги. Pid сверяется со временем старта: чужой процесс, получивший тот же
/// pid, не трогается.
/// </summary>
internal sealed class TurnJournal
{
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    private readonly string _dir;
    private readonly ILogger _log;

    public TurnJournal(string directory, ILogger? log = null)
    {
        _dir = directory;
        _log = log ?? NullLogger.Instance;
        Directory.CreateDirectory(_dir);
    }

    internal sealed record Entry(string TurnId, int Pid, DateTime StartTimeUtc, string? TurnDirectory);

    public void Add(Entry entry) =>
        File.WriteAllText(PathOf(entry.TurnId), JsonSerializer.Serialize(entry));

    public void Remove(string turnId)
    {
        try { File.Delete(PathOf(turnId)); }
        catch (IOException e) { _log.LogDebug(e, "Запись журнала хода {TurnId} не удалена", turnId); }
    }

    public IReadOnlyList<Entry> ReadAll()
    {
        var entries = new List<Entry>();
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<Entry>(File.ReadAllText(file)) is { } entry) entries.Add(entry);
            }
            catch (Exception e) when (e is IOException or JsonException)
            {
                _log.LogWarning(e, "Битая запись журнала ходов {File}", file);
            }
        }
        return entries;
    }

    /// <summary>
    /// Зачистка после прошлой жизни агента: убить уцелевшие ходы, убрать их следы.
    /// Возвращает число убитых.
    /// </summary>
    public int SweepLeftovers(SessionFileJanitor janitor)
    {
        var killed = 0;
        foreach (var entry in ReadAll())
        {
            if (IsSameProcess(entry.Pid, entry.StartTimeUtc))
            {
                _log.LogWarning("Ход {TurnId} пережил агента (pid {Pid}) — добиваю", entry.TurnId, entry.Pid);
                KillLeftover(entry.Pid);
                killed++;
            }

            janitor.CleanUp(entry.Pid);
            if (entry.TurnDirectory is { } dir) TurnWorkspaceCleanup.TryDelete(dir, _log);
            Remove(entry.TurnId);
        }
        return killed;
    }

    private string PathOf(string turnId) => Path.Combine(_dir, turnId + ".json");

    private static bool IsSameProcess(int pid, DateTime startTimeUtc)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            return (process.StartTime.ToUniversalTime() - startTimeUtc).Duration() <= StartTimeTolerance;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static void KillLeftover(int pid)
    {
        if (!OperatingSystem.IsWindows())
        {
            UnixGroupProcess.KillGroup(pid, leaderAlive: () => true);
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { }
    }
}

/// <summary>
/// Уборка файлов сессии CLI в профиле устройства после убийства: <c>sessions/{pid}.json</c>
/// и <c>sessions/{pid}.{хеш}.key</c>. Штатно их убирает сам CLI на выходе, после SIGKILL /
/// TerminateJobObject — некому. Удаляются только файлы с точным pid, никогда по маске шире.
/// </summary>
internal sealed class SessionFileJanitor(string configDirectory, ILogger? log = null)
{
    private readonly ILogger _log = log ?? NullLogger.Instance;

    public string SessionsDirectory => Path.Combine(configDirectory, "sessions");

    public int CleanUp(int pid)
    {
        var dir = SessionsDirectory;
        if (!Directory.Exists(dir)) return 0;

        var removed = 0;
        var prefix = pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".";
        foreach (var file in Directory.EnumerateFiles(dir, prefix + "*"))
        {
            var name = Path.GetFileName(file);
            if (!IsSessionFileOf(name, prefix)) continue;
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(e, "Файл сессии CLI {File} не удалён", name);
            }
        }
        return removed;
    }

    // {pid}.json или {pid}.{один сегмент}.key — и ничего больше (1234.json не задевает 12345.json)
    internal static bool IsSessionFileOf(string name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var rest = name[prefix.Length..];
        if (rest == "json") return true;
        if (!rest.EndsWith(".key", StringComparison.Ordinal)) return false;
        var middle = rest[..^".key".Length];
        return middle.Length > 0 && middle.All(char.IsAsciiLetterOrDigit);
    }
}

internal static class TurnWorkspaceCleanup
{
    public static void TryDelete(string directory, ILogger log)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(e, "Каталог хода {Dir} не удалён", directory);
        }
    }
}
