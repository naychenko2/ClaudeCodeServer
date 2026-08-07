using System.Collections.Concurrent;

namespace ClaudeHomeServer.Services.Llm;

// Реестр заявок на правку файла: сессия регистрирует нормализованный полный путь при
// tool_use Edit/Write/MultiEdit/NotebookEdit (ClaudeSession.HandleAssistantToolsAsync),
// а TurnFileWatcher перед показом карточки file_changed сверяется — не заявлен ли путь
// ДРУГОЙ сессией только что. Нужен как singleton на процесс сервера: TurnFileWatcher
// per-сессионный, но слушает весь rootPath — при параллельных ходах двух чатов одного
// проекта правка из чата A иначе попадает в ленту чата B (FS-событие не несёт автора).
public sealed class FileChangeAttributor
{
    // Окно, в течение которого заявка считается «живой». Эвристика: параллельные ходы
    // debounce'ат FS-событие на 400мс (см. TurnFileWatcher), запас — на медленные диски/CI.
    public static readonly TimeSpan AttributionWindow = TimeSpan.FromSeconds(15);

    // OrdinalIgnoreCase — как в FileService.SafeJoin: единообразное сравнение путей
    // независимо от регистрочувствительности ФС хоста (Windows-разработка / Linux CI)
    private readonly ConcurrentDictionary<string, (string SessionId, DateTimeOffset At)> _claims =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromSeconds(30);
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    // Сессия только что записала/отредактировала путь — заявка живёт AttributionWindow.
    // at — только для тестов (детерминированное время без Thread.Sleep); прод не передаёт.
    public void Claim(string sessionId, string fullPath, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        _claims[NormalizePath(fullPath)] = (sessionId, now);
        Prune(now);
    }

    // Путь только что заявлен ДРУГОЙ (не sessionId) сессией в пределах окна — карточку
    // в этом чате нужно подавить, её покажет чат-источник. Свои и незаявленные пути — false.
    public bool IsClaimedByOther(string sessionId, string fullPath, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        if (!_claims.TryGetValue(NormalizePath(fullPath), out var claim)) return false;
        if (string.Equals(claim.SessionId, sessionId, StringComparison.Ordinal)) return false;
        return now - claim.At <= AttributionWindow;
    }

    // Путь заявлен ИМЕННО sessionId в пределах окна — правку сделала модель этого чата
    // (Edit/Write и т.п.). Нет живой заявки вообще ни от кого (в т.ч. от sessionId) —
    // правка пришла извне процесса Claude (человек в IDE, форматтер) — false.
    public bool IsClaimedBySelf(string sessionId, string fullPath, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        if (!_claims.TryGetValue(NormalizePath(fullPath), out var claim)) return false;
        if (!string.Equals(claim.SessionId, sessionId, StringComparison.Ordinal)) return false;
        return now - claim.At <= AttributionWindow;
    }

    // Регистр не в git-репо (нет .git) сравнивать не с чем — нормализация просто схлопывает
    // "." и ".." сегменты; сравнение по OrdinalIgnoreCase (как в FileService.SafeJoin) —
    // единообразно для Windows-разработки и Linux CI, а не по фактической ФС хоста.
    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    // Протухшие заявки вычёркиваем заодно с записью новой — реестр живёт весь процесс
    // сервера, отдельный таймер под редкие правки избыточен.
    private void Prune(DateTimeOffset now)
    {
        if (now - _lastPrune < PruneInterval) return;
        _lastPrune = now;
        foreach (var (path, claim) in _claims)
            if (now - claim.At > AttributionWindow)
                _claims.TryRemove(path, out _);
    }
}
