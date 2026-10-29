namespace ClaudeHomeServer.Services.Notes.Wiki;

// Откат применённого пакета (ADR-035, решение 12г, мера 4): кнопка «Откатить пакет» в журнале.
// Только действие человека, через REST (решение 5): ни у агента, ни у персоны такого инструмента нет.
// Откат — по прообразам того же пакета (S4b-2): страницы возвращаются байт в байт к состоянию до
// применения, манифест — к прежним записям (RollbackTo), журнал получает строку op=revert, проекции
// перегенерируются. Никакого смыслового решения здесь не принимается: откатывается ровно то, что
// записал этот пакет.
//
// Условие отката — согласованность страниц пакета с манифестом: хэш на диске каждой равен её записи.
// Это та же «нет дрейфа», которую проверяет применение перед записью: если файл меняли мимо Хранителя,
// откат стёр бы чужую правку вместе с пакетом — о такой правке решает человек, а не откат. Отказ
// честный: перечень изменённых страниц, на диске не тронуто ничего.
//
// Что эта сверка НЕ говорит: менял ли страницу другой пакет после этого — манифест описывает
// последнее состояние, а не порядок. Откат цепочки пакетов — предмет откатa по источнику (retract,
// мера 5), а не этого метода.
//
// Статус откатанного пакета меняется через Transition стора: «applied» → «reverted» (решение
// постановщика по S4b-3; статус конечный, из него переходов нет). Повторный откат «reverted»-пакета
// отказан по статусу и не дописывает в журнал вторую строку: кнопка не повторяет уже сделанный откат.
// Если Transition не удался ПОСЛЕ того, как файлы уже откатаны, состояние остаётся со следом:
// файлы откатаны, строка журнала есть, исход назван вызывающему — следующий вызов отката проверку
// пройдёт (файлы согласованы с манифестом) и доведёт статус. Обратный порядок — «заявка перехода,
// затем откат» — необратим: «reverted» → «applied» в таблице нет, и неудачный откат оставил бы
// «reverted»-пакет с неоткатанными файлами.
//
// Порядок успешной фазы — порядок шага 6 применения: файлы, манифест, журнал, проекции (_log.md
// раздела — производная от log.jsonl и без этой строки остался бы на полшага позади отката). Сбой
// любого шага — результат-отказ с причиной, а не исключение вызывающему (тот же контракт, что у
// применения).
public sealed class WikiPackageRevert
{
    // Коды отказов — контракт вызывающего (REST «Журнала», S7a), тот же стиль, что wiki_*.
    public const string CodeShape = "wiki_revert_shape";
    public const string CodeOff = "wiki_revert_off";
    public const string CodeClaim = "wiki_revert_claim";
    public const string CodeStatus = "wiki_revert_status";
    public const string CodeManifest = "wiki_revert_manifest";
    public const string CodeNoPreimages = "wiki_revert_no_preimages";
    public const string CodeChanged = "wiki_revert_changed";
    public const string CodeInternal = "wiki_revert_internal";

    // Значение поля op строки журнала (мера 6): та же форма, что op=apply у применения и op=adopt
    // у Хранителя; по нему «Журнал» отличает откат от прочих операций.
    private const string LogOpRevert = "revert";

    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly string _dataDir;

    public WikiPackageRevert(NotesService notes, WikiKeeper keeper, IConfiguration config)
    {
        _notes = notes;
        _keeper = keeper;
        // Каталог данных — как у всех сервисов вертикали (тот же корень, что у Хранителя).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    /// <summary>Итог отката. ChangedPages — только при отказе по хэшах: что человек видит в диалоге
    /// перед повторной попыткой. RestoredPages — только при успехе: страницы, возвращённые к состоянию
    /// до применения (их обновляет «Журнал»).</summary>
    public readonly record struct RevertResult(
        bool Reverted,
        string? Code,
        string? Reason,
        IReadOnlyList<string> ChangedPages,
        IReadOnlyList<string> RestoredPages);

    /// <summary>Откатить пакет по его идентификатору: состояние читается с диска под id, а не со слов
    /// вызывающего (урок S4b-1). Откатывается только пакет в «applied»: «applying» закрывает сторож
    /// старта, «apply_failed» уже откатан, rejected/stale до диска не дошли, «reverted» отказан
    /// повторным. Под блокировкой раздела (тот же замок, что у записи и у сторожа): откат не пишет
    /// параллельно с применением этого раздела и не берёт второй блокировки того же ключа.</summary>
    public async Task<RevertResult> RevertAsync(string ownerId, string? packageId)
    {
        // Fail-closed входа (уроки S4a/S4b): отказ с причиной, не NRE.
        if (string.IsNullOrWhiteSpace(ownerId))
            return Fail(CodeShape, "владелец пустой");
        if (string.IsNullOrWhiteSpace(packageId))
            return Fail(CodeShape, "id пакета пустой");
        if (!WikiPaths.IsInitialized(_dataDir, ownerId))
            return Fail(CodeOff, "вики этого владельца не заведена");

        var root = WikiPaths.Root(_dataDir, ownerId);
        var read = WikiPackageStore.TryRead(root, packageId);
        if (read.State == WikiPackageFileState.Missing)
            return Fail(CodeClaim, "пакета нет на диске: откатывать нечего");
        if (read.State == WikiPackageFileState.Invalid)
            return Fail(CodeShape, "файл пакета не читается как пакет v:1");
        var package = read.Package!;

        // Откатывают только применённый пакет: «applying» закрывает сторож старта, «apply_failed»
        // уже откатан, rejected/stale до диска не дошли, «reverted» уже откатан — второй раз его
        // не откатывают и вторую строку в журнал не пишут.
        if (package.Status == WikiPackage.StatusReverted)
            return Fail(CodeStatus, "пакет уже откатан: второй раз он не откатывается");
        if (package.Status != WikiPackage.StatusApplied)
            return Fail(CodeStatus, $"пакет в «{package.Status}»: откатывают только применённый пакет");

        return await _keeper.WithSectionsLockedAsync(ownerId, [package.Section],
            () => Task.FromResult(RevertUnderLock(ownerId, root, package)));
    }

    private RevertResult RevertUnderLock(string ownerId, string root, WikiPackage package)
    {
        try
        {
            return RevertBody(ownerId, root, package);
        }
        catch (Exception ex)
        {
            // Любой неожиданный сбой — результат-отказ с причиной, не исключение вызывающему
            // (тот же контракт, что у применения).
            return Fail(CodeInternal, $"внутренний сбой отката: {ex.Message}");
        }
    }

    private RevertResult RevertBody(string ownerId, string root, WikiPackage package)
    {
        var preimagesDir = SafePath.Join(root, WikiApplyService.PreimagesRel(package.Id));
        if (!Directory.Exists(preimagesDir))
            return Fail(CodeNoPreimages, "прообразов пакета на диске нет: откатывать нечего");

        // Комплект страниц — по прообразам, а не по операциям: прообразы и есть то, что этот пакет
        // записал (операция «inbox» без решения человека на диск не выходила — для неё откатывать
        // нечего). Остаток temp упавшего процесса — не прообраз (тот же фильтр, что у сторожа).
        var pages = Directory.EnumerateFiles(preimagesDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(preimagesDir, f).Replace('\\', '/'))
            .Where(p => !p.EndsWith(".tmp", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        if (pages.Count == 0)
            return Fail(CodeNoPreimages, "прообразы пакета пусты: откатывать нечего");

        var mres = WikiManifest.TryRead(root, package.Section);
        if (mres.State == WikiManifestState.Invalid)
            return Fail(CodeManifest, $"манифест раздела «{package.Section}» невалиден: сверку с диском провести нельзя");
        var manifest = mres.Manifest;

        // Фаза 1 — только чтения: сверка хэшей и байты прообразов. На диске здесь не трогается
        // ничего: отказ на этой фазе — всегда отказ «на диске не тронуто ничего».
        var changed = new List<string>();
        var preimageBytes = new Dictionary<string, byte[]>(pages.Count, StringComparer.Ordinal);
        foreach (var page in pages)
        {
            byte[]? disk = null;
            try
            {
                preimageBytes[page] = File.ReadAllBytes(SafePath.Join(root, WikiApplyService.PreimageRel(package.Id, page)));
                var full = SafePath.Join(root, "pages/" + page);
                disk = File.Exists(full) ? File.ReadAllBytes(full) : null;
            }
            catch (Exception ex)
            {
                return Fail(CodeInternal, $"не читается прообраз или страница «{page}»: {ex.Message}");
            }
            // Тот же хэш, что у манифеста и применения (sha256 байтов). Страница, которой нет ни на
            // диске, ни в манифесте, — согласована (её убрали вместе с записью: возвращать нечего);
            // на диске без записи — изменённая: её состояние манифест не описывает.
            var diskHash = disk is null ? null : WikiApplyService.Sha256OfBytes(disk);
            var recorded = manifest.TryGetValue(page, out var h) ? h : null;
            if (diskHash != recorded)
                changed.Add(page);
        }
        if (changed.Count > 0)
            return new RevertResult(false, CodeChanged,
                $"страницы изменены после применения (диск не совпадает с манифестом): {string.Join(", ", changed)}. " +
                "Отказ: откат стёр бы чужую правку; на диске не тронуто ничего",
                changed, []);

        // Фаза 2 — возврат. Байты — прообразов; пустой прообраз = страницы не было (create): её файл
        // удаляется, её запись манифеста — тоже. Сбой на одной странице не откатывает остальные и не
        // оставляет раздел рассогласованным: возвращённое возвращается, и для него манифест правится
        // (тот же закон, что у сбоя применения); нетронутые страницы остаются в своём применённом
        // состоянии и в манифесте — раздел снова согласован с диском, и повторный вызов продолжит
        // откат с того места, где он остановился.
        var restored = new List<string>();
        var rollback = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? writeError = null;
        foreach (var page in pages)
        {
            try
            {
                var bytes = preimageBytes[page];
                var full = SafePath.Join(root, "pages/" + page);
                if (bytes.Length == 0)
                {
                    // Файла нет и сейчас: этой записи до диска не дошло — возвращать нечего,
                    // и записью в манифесте она не является.
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
                    restored.Add(page);
                    // Запись манифеста о возвращённой странице — хэш того, что вернули: согласованность
                    // с диском, а не память о применении.
                    rollback[page] = WikiApplyService.Sha256OfBytes(bytes);
                }
            }
            catch (Exception ex)
            {
                writeError = $"{page}: {ex.Message}";
                break;
            }
        }

        string? manifestError = null;
        if (rollback.Count > 0)
        {
            try
            {
                WikiManifest.RollbackTo(root, package.Section, rollback);
            }
            catch (Exception ex)
            {
                // Манифест следует за файлами: что возвращено, то и записано обратно. Его сбой —
                // часть отказа, а не исключение вызывающему: хэш применённого состояния мог остаться
                // в манифесте при возвращённых файлах (тот же текст, что у сбоя применения).
                manifestError = ex.Message;
            }
        }

        if (writeError is not null)
        {
            var text = $"откат остановился на странице «{writeError}»: возвращено по прообразам: {string.Join(", ", restored)}; " +
                "остальные страницы остались в применённом состоянии";
            if (manifestError is not null)
                text += $" (откат записей манифеста не удался: {manifestError} — хэш применённого состояния мог остаться в манифесте)";
            else if (restored.Count > 0)
                // Проекции — за файлами и манифестом: они производные от них (и при частичном откате).
                WikiProjections.RegenerateSection(root, package.Section);
            return Fail(CodeInternal, text);
        }

        // Журнал — перед проекциями (порядок шага 6): _log.md — производная от log.jsonl.
        // Actor — «user»: откат — кнопка человека (тот же автор, что у ручного adopt); источник —
        // карточки источника пакета: по ним «Журнал» связывает пакет и источник (мера 5).
        WikiManifest.AppendLog(root, LogOpRevert, package.Section, restored, SourceOf(package), "user");
        WikiProjections.RegenerateSection(root, package.Section);

        // Статус — через Transition стора, единственную дверь в статусы: «applied» → «reverted»,
        // конечный, из него переходов нет.
        var done = WikiPackageStore.Transition(root, package.Id, WikiPackage.StatusReverted);

        // Файлы раздела изменены при любом исходе статуса — модель per-owner не должна остаться
        // о них осведомлённой (тот же инвариант, что у применения и сторожа).
        _notes.Invalidate(ownerId);

        if (!done.Ok)
            // Файлы откатаны и строка журнала записана, но статус не доведён: состояние со следом
            // и открыто для доводки — следующий вызов отката пройдёт сверку (файлы согласованы с
            // манифестом) и доведёт переход. Обратный порядок — «заявка перехода, затем откат» —
            // необратим: «reverted» → «applied» в таблице нет. Исход назван вызывающему, а не
            // молчание (минор 4, ревью S4b-1).
            return new RevertResult(false, CodeClaim,
                $"файлы откатаны, статус «reverted» не записан: {done.Reason ?? done.Code}",
                [], restored);

        return new RevertResult(true, null, null, [], restored);
    }

    /// <summary>Карточка источника строки журнала — карточки источников пакета (та же форма, что у
    /// строки применения): по ним «Журнал» и откат по источнику (мера 5) связывают пакет и его чат.</summary>
    private static string SourceOf(WikiPackage package) =>
        string.Join(", ", package.Sources.Select(s => s.Ref));

    /// <summary>Срок хранения прообразов: между бэкапами откат держат они (ADR-035, решение 13);
    /// после него — мусор, который чистит сторож старта.</summary>
    public static readonly TimeSpan PreimageRetention = TimeSpan.FromDays(90);

    /// <summary>Удалить прообразы старше срока хранения (90 дней). Возраст — по времени записи файла:
    /// это тот момент, когда прообраз стал прообразом. Ровно на границе (90 суток) файл остаётся:
    /// «старше» значит строго старше (интервал хранения — [now−90d, now]). Сбой на одном файле не
    /// останавливает остальные и наружу исключения не выходит: вызывающий — сторож старта, фоновая
    /// служба не имеет права ронять сервер. Пустой каталог пакета удаляется: держать нечего.
    /// now — то же время, которым помечены файлы (тест подставляет своё); returns — число удалённых
    /// файлов.</summary>
    public static int PurgeOldPreimages(string root, DateTimeOffset now)
    {
        var dir = SafePath.Join(root, "keeper/preimages");
        if (!Directory.Exists(dir))
            return 0;
        var cutoff = now.ToUniversalTime() - PreimageRetention;
        var deleted = 0;
        foreach (var pkgDir in Directory.EnumerateDirectories(dir))
        {
            var empty = true;
            foreach (var f in Directory.EnumerateFiles(pkgDir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (new DateTimeOffset(File.GetLastWriteTimeUtc(f)) >= cutoff)
                    {
                        empty = false;
                        continue;
                    }
                    File.Delete(f);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Один недоступный файл не останавливает чистку остальных; пустым каталог
                    // с ним объявлять нельзя.
                    empty = false;
                }
            }
            try
            {
                if (empty && Directory.Exists(pkgDir))
                    Directory.Delete(pkgDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return deleted;
    }

    private static RevertResult Fail(string code, string reason) =>
        new(false, code, reason, [], []);
}
