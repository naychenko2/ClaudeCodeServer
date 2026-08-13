using System.Collections.Concurrent;

namespace ClaudeHomeServer.Services;

// Ссылка на чат для бейджа «Также меняли» (панель «Изменения»): id + отображаемое имя.
public record SessionRef(string SessionId, string Name);

// Обратный индекс «файл → какие ЕЩЁ чаты проекта его меняли» (панель «Изменения» —
// бейдж в строке файла и список «Также меняли» в шапке диффа). Строится над историями
// чатов проекта (SessionChangedPaths.Extract) с кешем per-чат: лента перечитывается
// только когда история реально изменилась (LastWriteUtc файла history.json сдвинулся).
public class ProjectFileSessionsIndex(SessionManager sessions, ChatHistoryService history, ProjectManager projects)
{
    // Кеш путей одного чата на момент последнего построения — LastWriteUtc истории
    // как признак актуальности (без него перечитывали бы историю КАЖДОГО чата проекта
    // на каждый запрос эндпоинта — история живого чата может быть многомегабайтной)
    private sealed record CacheEntry(DateTime? LastWriteUtc, HashSet<string> Paths);

    // Ключ — ClaudeSessionId чата (истории лежат по нему, не по Session.Id)
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    // paths — фильтр ответа: возвращаются только записи для этих путей (сравнение по
    // lowercase, ключи внутреннего словаря — уже нормализованные пути SessionChangedPaths).
    // Имена чатов НЕ кешируются — подставляются из живого Session.Name на каждый вызов,
    // чтобы переименование чата отражалось сразу без инвалидации кеша путей.
    public async Task<Dictionary<string, List<SessionRef>>> GetForProjectAsync(
        string projectId, IReadOnlyCollection<string> paths)
    {
        var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, List<SessionRef>>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return result;

        // Worktree-чаты пропускаем: их правки живут в отдельном дереве, а не в rootPath
        // проекта (см. SessionChangedPaths — worktree-ХОД тоже исключён, здесь то же
        // на уровне целого чата). Без ClaudeSessionId истории просто нет.
        var projectSessions = sessions.GetByProject(projectId)
            .Where(s => s.WorktreePath == null && s.ClaudeSessionId != null)
            .ToList();

        // Чистка кеша от чатов, которых больше нет НИ В ОДНОМ проекте (удалены) — резолв
        // ГЛОБАЛЬНЫЙ, по всем сессиям процесса, а НЕ по чатам ТЕКУЩЕГО projectId: словарь
        // _cache общий на все проекты (ключ — ClaudeSessionId), и чистка по чатам одного
        // проекта на каждый вызов выбивала бы кеш соседних — с двумя открытыми проектами
        // (и любым фокус-рефрешем каждого) лента любого чата перечитывалась бы с диска
        // на КАЖДЫЙ запрос вместо одного раза при реальном изменении истории
        var liveIds = new HashSet<string>(
            sessions.GetAll().Where(s => s.ClaudeSessionId != null).Select(s => s.ClaudeSessionId!),
            StringComparer.Ordinal);
        foreach (var key in _cache.Keys)
            if (!liveIds.Contains(key)) _cache.TryRemove(key, out _);

        var rootPath = projects.GetById(projectId)?.RootPath;
        if (rootPath is null) return result; // проект не резолвится — ownership уже проверил контроллер

        foreach (var session in projectSessions)
        {
            var claudeId = session.ClaudeSessionId!;
            var lastWrite = history.LastWriteUtc(claudeId);
            if (!_cache.TryGetValue(claudeId, out var entry) || entry.LastWriteUtc != lastWrite)
            {
                var messages = await history.LoadAsync(claudeId);
                entry = new CacheEntry(lastWrite, SessionChangedPaths.Extract(messages, rootPath));
                _cache[claudeId] = entry;
            }

            foreach (var p in entry.Paths)
            {
                if (!wanted.Contains(p)) continue;
                if (!result.TryGetValue(p, out var list)) { list = []; result[p] = list; }
                list.Add(new SessionRef(session.Id, session.Name ?? "Без названия"));
            }
        }
        return result;
    }
}
