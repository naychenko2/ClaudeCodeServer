namespace ClaudeHomeServer.Services.Notes.Wiki;

// Сторож старта применения (ADR-035, решение 4, последний абзац): пакет, застрявший в
// «applying» после падения процесса, не имеет права переждать ни одного запуска: его заявка
// держит повторное применение (решение 10), а человек видит «применяется» там, где ничего не
// применяется. При старте службы такие пакеты находятся по дисковому статусу (не по памяти и
// не по журналу) и откатываются по своим прообразам в «apply_failed»: дальше — новый разбор и
// новый пакет, авто-rebase нет (решение 4).
//
// Возврат — ровно того, что успело быть записано: страница с прообразом возвращается к его
// байтам (пустой прообраз = страницы не было → файл удаляется); страниц без прообразов сторож
// не трогает — их записи не было. Манифест после возврата приводится к прежнему состоянию:
// прежний хэш возвращается записью, а запись create-страницы (её в манифеста не было) —
// убирается вместе с файлом. Падение могло случиться и ПОСЛЕ шага 6 (манифест уже новый):
// возврат файлов без этой правки оставил бы «дрейф» на ровном месте — для изменённой страницы
// как расхождение хэша, для create — как «файл из манифеста отсутствует на диске»
// (ревью S4b-2, M2), — а человек решал бы то, что уже решено.
//
// Исключение из службы не выходит ни при каком сбое: невынесенное останавливает хост, а
// фоновая служба не имеет права ронять сервер (тот же приём запуска, что у
// PersonaMemoryConsolidationService). Один плохой пакет не останавливает остальных: сбой
// одного — строка в журнал сервера, этот пакет останется в «applying» и будет повторён при
// следующем старте.

public sealed class WikiApplyStartupGuard : IHostedService
{
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly string _dataDir;
    private readonly ILogger<WikiApplyStartupGuard> _log;

    public WikiApplyStartupGuard(NotesService notes, WikiKeeper keeper, IConfiguration config, ILogger<WikiApplyStartupGuard> log)
    {
        _notes = notes;
        _keeper = keeper;
        _log = log;
        // Каталог данных — как у всех сервисов вертикали (тот же корень, что у Хранителя).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var rolled = await RollbackStuckAsync().ConfigureAwait(false);
            if (rolled.Count > 0)
                _log.LogInformation("сторож старта вики: откатано пакетов в «applying»: {Count} ({Ids})",
                    rolled.Count, string.Join(", ", rolled));
        }
        catch (Exception ex)
        {
            // Сбой обхода или одного из откатов: хост жив, несделанное видно в журнале сервера
            // и будет повторено при следующем старте (сторож идемпотентен: «applying» он
            // откатывает, «apply_failed» — уже нет).
            _log.LogError(ex, "сторож старта вики не дошёл до конца: пакеты в «applying» останутся в этом статусе до следующего старта");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Владельцы — каталоги под data/wiki/: тот, у кого есть вики, тот и кандидат. Реестра
    // владельцев сторожу не нужно: состояние, которое он чинит, лежит на диске.
    private async Task<List<string>> RollbackStuckAsync()
    {
        var rolled = new List<string>();
        var wikiRoot = Path.Combine(_dataDir, WikiPaths.DirName);
        if (!Directory.Exists(wikiRoot))
            return rolled; // ни одной вики не заведено: применять нечего

        foreach (var ownerDir in Directory.EnumerateDirectories(wikiRoot))
        {
            var ownerId = Path.GetFileName(ownerDir);
            var root = WikiPaths.Root(_dataDir, ownerId);
            var changed = false;

            foreach (var pkg in WikiPackageStore.List(root).Packages)
            {
                if (pkg.Status != WikiPackage.StatusApplying)
                    continue;
                // Блок раздела — тот же замок, что у записи: сторож не пишет параллельно с
                // работающим применением того же раздела (их и не должно быть на старте, но
                // проверка не по предположению, а по замку).
                bool done;
                try
                {
                    done = await _keeper.WithSectionsLockedAsync(ownerId, [pkg.Section],
                        () => Task.FromResult(RollbackOne(root, pkg))).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "сторож старта вики: откат пакета {Id} не удался: он останется в «applying» и будет повторён при следующем старте", pkg.Id);
                    continue;
                }
                if (done)
                {
                    rolled.Add(pkg.Id);
                    changed = true;
                }
            }

            // Прообразы старше срока хранения (90 дней) чистятся здесь: между бэкапами откат держат
            // они (решение 13), после него — мусор. После откатов «applying» — их прообразы тоже
            // подпадают под срок.
            var purged = WikiPackageRevert.PurgeOldPreimages(root, DateTimeOffset.UtcNow);
            if (purged > 0)
                _log.LogInformation("сторож старта вики: прообразов старше {Days} дней удалено: {Count}",
                    (int)WikiPackageRevert.PreimageRetention.TotalDays, purged);

            // Файлы раздела изменены — модель per-owner не должна остаться о них осведомлённой.
            // На старте кэша обычно нет; держится тот же инвариант, что у Хранителя, а не «на всякий случай».
            if (changed)
                _notes.Invalidate(ownerId);
        }
        return rolled;
    }

    // Возврат одного пакета по его прообразам и переход в «apply_failed». true — статус
    // сдвинулся; false — переход не удался (сторож сказал это журналом сервера, пакет
    // дождётся следующего старта).
    private bool RollbackOne(string root, WikiPackage package)
    {
        var dir = SafePath.Join(root, $"keeper/preimages/{package.Id}");
        if (!Directory.Exists(dir))
        {
            // Прообразов нет: ни одной страницы этого пакета записано не было (сбой был на
            // шаге 4 или раньше). Откатывать нечего — но статус всё равно обязан закрыться:
            // смысл сторожа в том, что в «applying» не остаётся ни одного пакета.
            return TransitionFailed(root, package.Id, "сторож старта: пакет остался в «applying» после падения процесса; записанных страниц нет — откатывать нечего");
        }

        var pages = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
            // Остаток temp от упавшего процесса (падение между записью temp и Move) — не
            // прообраз: вернуть его означало бы завести в pages/ файл с именем «….tmp».
            .Where(p => !p.EndsWith(".tmp", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var restored = new List<string>();
        // То же состояние манифеста, что и возвращённые файлы: хэш прежней страницы или
        // null — её записи в манифеста не было (create), её запись убирается (ревью S4b-2, M2).
        var rollback = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var page in pages)
        {
            var bytes = File.ReadAllBytes(Path.Combine(dir, page));
            var full = SafePath.Join(root, "pages/" + page);
            if (bytes.Length == 0)
            {
                // Пустой прообраз = страницы не было (create): её запись — файл, которого
                // раньше не было, и строка манифеста, добавленная шагом 6; возврат убирает
                // и то, и другое. Файла нет и сейчас: этой записи не было, в списке
                // возвращённого её быть не должно — и манифест не трогается.
                if (File.Exists(full))
                {
                    File.Delete(full);
                    restored.Add(page);
                    rollback[page] = null;
                }
            }
            else
            {
                WikiApplyService.WriteBytesAtomic(root, "pages/" + page, bytes);
                rollback[page] = WikiApplyService.Sha256OfBytes(bytes);
                restored.Add(page);
            }
        }

        if (rollback.Count > 0)
            WikiManifest.RollbackTo(root, package.Section, rollback);
        if (restored.Count > 0)
            WikiProjections.RegenerateSection(root, package.Section);

        return TransitionFailed(root, package.Id,
            $"сторож старта: пакет остался в «applying» после падения процесса, возвращено по прообразам: {string.Join(", ", restored)}");
    }

    private bool TransitionFailed(string root, string packageId, string reason)
    {
        var t = WikiPackageStore.Transition(root, packageId, WikiPackage.StatusApplyFailed, reason);
        if (t.Ok) return true;
        // Минор 4 (ревью S4b-1): исход перехода — часть результата, а не молчание.
        _log.LogWarning("сторож старта вики: статус «apply_failed» не записан для пакета {Id}: {Reason}", packageId, t.Reason ?? t.Code);
        return false;
    }
}
