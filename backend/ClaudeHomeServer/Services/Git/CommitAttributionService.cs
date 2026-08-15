using System.Collections.Concurrent;

namespace ClaudeHomeServer.Services.Git;

// Детект коммита по сдвигу HEAD — помечает чатам пути «правки уже зафиксированы в git»
// (Session.CommittedFilePaths), чтобы атрибуция «какой чат менял файл» (панель
// «Изменения»: бейдж «Также меняли» и фильтр «только файлы чата») переставала врать
// после коммита. Сидит на горячем пути получения git-статуса (GitController.Status),
// а НЕ на событии GitService.CommitAsync: коммиты часто идут мимо продукта — git commit
// из Bash в чате, руками в терминале, revert (он тоже мимо CommitAsync) — событие их
// не увидело бы, и фича не работала бы в основном сценарии. Отдельный таймер не нужен:
// фронт дёргает статус постоянно (realtime, focus, дебаунс file_changed).
//
// Известные ограничения (принято): reset/откат коммита атрибуцию не возвращает —
// пометки не снимаются (дифф диапазона симметричен, и обратный сдвиг HEAD может даже
// добавить пометки по путям отменённых коммитов); _lastHead живёт только в памяти —
// коммит при погашенном сервере не засчитается (первый статус после рестарта лишь
// запоминает HEAD).
public class CommitAttributionService(
    GitService git, SessionManager sessions, ProjectManager projects, ProjectFileSessionsIndex index)
{
    // Последний известный HEAD per-root (ключ — нормализованный путь корня). In-memory
    // достаточно: после рестарта первый статус только запоминает HEAD, ничего не помечая.
    private readonly ConcurrentDictionary<string, string> _lastHead = new(StringComparer.Ordinal);

    // Дедуп in-flight per-root: обработка диапазона может быть долгой (git diff большого
    // репозитория, в container-среде — docker exec с 30с таймаутом), а статусы летят
    // пачками (realtime + focus + дебаунс file_changed) — без дедупа параллельные поллы
    // плодили бы параллельные git-процессы и вставали в очередь на 30с каждый.
    // Пропущенный сдвиг не теряется: маркер не двинут, следующий статус повторит.
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);

    // Вызывается после каждого ответа git-статуса. root — дерево запроса: корень git
    // worktree чата не резолвится ни в один Project.RootPath и базы чатов основного
    // дерева не трогает. headSha — branch.oid из УЖЕ полученного статуса (лишний
    // git rev-parse на горячем пути не нужен; в container-среде каждый запуск git —
    // docker exec); null — не репозиторий либо пустой без HEAD.
    // Пользовательский CancellationToken сюда не передаётся намеренно: это был бы
    // HttpContext.RequestAborted, и ушедший со страницы клиент отменял бы git-вызов
    // посреди обработки — детект обязан переживать такое. Ошибки глушатся: детект —
    // побочный наблюдатель, статус из-за него падать не должен.
    public async Task OnStatusRequestAsync(string? ownerId, string root, string? headSha)
    {
        try
        {
            if (string.IsNullOrEmpty(headSha)) return;
            var key = NormalizeRoot(root);
            // Первое наблюдение — только запомнить, ничего не помечать
            if (_lastHead.TryAdd(key, headSha)) return;
            if (!_lastHead.TryGetValue(key, out var prev) || prev == headSha) return;

            // Диапазон этого корня уже обрабатывается — тихо выходим (см. _inFlight)
            if (!_inFlight.TryAdd(key, 0)) return;
            try
            {
                // _lastHead двигается ТОЛЬКО после успешной обработки диапазона: упавший
                // MarkRangeAsync (таймаут git, сбой запуска) оставляет prev на месте, и
                // следующий статус повторяет попытку — иначе сбой терял бы коммит навсегда.
                // Пометка идемпотентна — повтор безопасен.
                await MarkRangeAsync(ownerId, root, prev, headSha);
                _lastHead.TryUpdate(key, headSha, prev);
            }
            finally { _inFlight.TryRemove(key, out _); }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CommitAttribution] Детект коммита ({root}) не отработал: {ex.Message}");
        }
    }

    // Сдвиг old→new: пути диапазона пересекаются с сырыми множествами чатов проекта —
    // только совпавшие пути помечаются (иначе коммит из 30 файлов положил бы 30 путей
    // каждому чату, и стор распух). Штатное завершение = диапазон обработан, вызывающий
    // двигает маркер; транзиентные сбои (таймаут/запуск git) приходят исключением и
    // маркер не двигают. Нерабочий дифф (old не существует: история переписана и добита
    // gc) — тоже штатное завершение без пометок: ретрай на каждом статусе молотил бы git
    // впустую. virtual — для подмены в тестах (сценарий «сбой → повтор»).
    protected virtual async Task MarkRangeAsync(string? ownerId, string root, string oldHead, string newHead)
    {
        var rootKey = NormalizeRoot(root);
        // Одна папка допустима у РАЗНЫХ владельцев (EnsureRootFree запрещает повтор только
        // внутри одного) — помечаем чаты ВСЕХ проектов с этим корнем, а не первого попавшегося
        var matching = projects.GetAll().Where(p => NormalizeRoot(p.RootPath) == rootKey).ToList();
        if (matching.Count == 0) return; // git worktree чата / не корень проекта

        var files = await git.ChangedFilePathsBetweenAsync(ownerId, root, oldHead, newHead);
        if (files is null || files.Count == 0) return; // несуществующий old либо пустой дифф
        var committed = new HashSet<string>(
            files.Select(SessionChangedPaths.Normalize), StringComparer.OrdinalIgnoreCase);

        var batch = new List<(string SessionId, IReadOnlyCollection<string> Paths, IReadOnlyCollection<string> RawPaths)>();
        foreach (var project in matching)
        {
            foreach (var (session, raw) in await index.GetSessionPathsAsync(project.Id))
            {
                // Пустое сырое множество = history.json пуст или не прочитался (битый файл
                // даёт пустую ленту): чат не трогаем — IntersectWith(∅) в MarkFilesCommitted
                // стёр бы все его пометки, и атрибуция откатилась бы к исходному вранью
                if (raw.Count == 0) continue;
                var hit = raw.Keys.Where(committed.Contains).ToList();
                if (hit.Count == 0 && session.CommittedFilePaths.Count == 0) continue;
                batch.Add((session.Id, hit, [.. raw.Keys]));
            }
        }
        if (batch.Count > 0) sessions.MarkFilesCommitted(batch);
    }

    // Нормализация корня для сравнения с Project.RootPath: слэши, хвостовой разделитель,
    // регистр (пути Windows регистронезависимы)
    private static string NormalizeRoot(string root) =>
        root.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
}
