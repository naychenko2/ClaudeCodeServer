using System.Collections.Concurrent;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Ссылка на чат для бейджа «Также меняли» (панель «Изменения»): id + отображаемое имя.
// External — файл менялся этим чатом ТОЛЬКО вне заявленного хода (Bash/скрипты модели,
// человек в IDE во время хода): фронт не показывает такую запись в бейдже, но включает
// в фильтр «только файлы чата» (myChangedPaths активного чата).
public record SessionRef(string SessionId, string Name, bool External);

// Обратный индекс «файл → какие ЕЩЁ чаты проекта его меняли» (панель «Изменения» —
// бейдж в строке файла и список «Также меняли» в шапке диффа). Строится над историями
// чатов проекта (SessionChangedPaths.Extract) с кешем per-чат: лента перечитывается
// только когда история реально изменилась (LastWriteUtc файла history.json сдвинулся).
public class ProjectFileSessionsIndex(SessionManager sessions, ChatHistoryService history, ProjectManager projects)
{
    // Кеш путей одного чата на момент последнего построения — LastWriteUtc истории
    // как признак актуальности (без него перечитывали бы историю КАЖДОГО чата проекта
    // на каждый запрос эндпоинта — история живого чата может быть многомегабайтной).
    // Paths — СЫРОЕ множество из истории (путь → External), без вычитания
    // CommittedFilePaths: пометки живут в sessions.json и меняются независимо от
    // LastWriteUtc истории, поэтому вычитание применяется ПОСЛЕ кеша, на каждый запрос.
    private sealed record CacheEntry(DateTime? LastWriteUtc, Dictionary<string, bool> Paths);

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

        foreach (var (session, entry) in await LoadProjectSessionPathsAsync(projectId))
        {
            // Вычитание пометок «правки уже зафиксированы в git»: актуальная атрибуция =
            // сырое множество МИНУС CommittedFilePaths. Пометки, которых нет в сыром
            // множестве (история переписана/протухла), просто игнорируются — на чтении
            // стор НЕ переписываем, реальная чистка при следующем MarkFilesCommitted.
            var committed = session.CommittedFilePaths.Count > 0
                ? new HashSet<string>(session.CommittedFilePaths, StringComparer.OrdinalIgnoreCase)
                : null;
            foreach (var (p, external) in entry.Paths)
            {
                if (!wanted.Contains(p)) continue;
                if (committed?.Contains(p) == true) continue;
                if (!result.TryGetValue(p, out var list)) { list = []; result[p] = list; }
                list.Add(new SessionRef(session.Id, session.Name ?? "Без названия", external));
            }
        }
        return result;
    }

    // СЫРЫЕ множества путей чатов проекта (путь → External, БЕЗ вычитания пометок) — для
    // детекта коммита (CommitAttributionService): пометка ставится только по пересечению
    // путей коммита с реальным множеством чата, иначе коммит из 30 файлов положил бы
    // 30 путей каждому чату проекта и стор распух. Тот же кеш per-чат, что у GetForProjectAsync.
    public async Task<IReadOnlyList<(Session Session, IReadOnlyDictionary<string, bool> Paths)>>
        GetSessionPathsAsync(string projectId) =>
        [.. (await LoadProjectSessionPathsAsync(projectId))
            .Select(x => (x.Session, (IReadOnlyDictionary<string, bool>)x.Entry.Paths))];

    // Общее ядро: чаты проекта с их сырыми множествами путей из кеша (перечитывание
    // истории — только при сдвиге LastWriteUtc).
    private async Task<List<(Session Session, CacheEntry Entry)>> LoadProjectSessionPathsAsync(string projectId)
    {
        var result = new List<(Session, CacheEntry)>();

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
            result.Add((session, entry));
        }
        return result;
    }
}
