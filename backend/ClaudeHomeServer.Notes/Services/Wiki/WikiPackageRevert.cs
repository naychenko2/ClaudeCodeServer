using System.Text.Json;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Откат применённого пакета (ADR-035, решение 12г, мера 4): кнопка «Откатить пакет» в журнале.
// Только действие человека, через REST (решение 5): ни у агента, ни у персоны такого инструмента нет.
// Откат — по прообразам того же пакета (S4b-2): страницы возвращаются байт в байт к состоянию до
// применения, манифест — к прежним записям (RollbackTo), журнал получает строку op=revert, проекции
// перегенерируются. Никакого смыслового решения здесь не принимается: откатывается ровно то, что
// записал этот пакет.
//
// Условие отката — согласованность страниц пакета с его собственной записью состояния применения
// (keeper/applied/{pkgId}.json, её пишет шаг 6 применения; ревью S4b-3, MAJOR 1): хэш на диске
// каждой равен тому, что записал этот пакет. Манифест для этого не годится: он описывает последнее
// состояние страницы, а не состояние на момент этого применения, — откат по нему вернул бы прообраз
// этого пакета поверх записи более позднего пакета, и та запись исчезла бы без следа. Инвариант:
// откат возвращает ровно то, что записал этот пакет, и не трогает более поздние записи той же
// страницы. Это та же «нет дрейфа», которую проверяет применение перед записью: если файл меняли
// мимо Хранителя, откат стёр бы чужую правку вместе с пакетом — о такой правке решает человек, а не
// откат. Отказ честный: перечень разошедшихся страниц, на диске не тронуто ничего. Пакета без его
// записи (заведённого до этой меры) не откатывают вовсе: без состояния применения сверки нет, и
// откат по манифесту был бы молчаливым допущением (fail-closed).
//
// Что эта сверка НЕ говорит: КЕМ именно страница стала другой — более поздним пакетом или правкой
// человека. Откат цепочки пакетов — предмет отката по источнику (retract, мера 5), а не этого
// метода.
//
// Состояние пакета и его гейт — под блокировкой раздела: пакет перечитывается с диска внутри
// блока (ревью S4b-3, MAJOR 2). Прочитанный до блока, он устаревает ровно там, где решается исход:
// два отката одного пакета иначе прошли бы гейт вдвоём, и второй дописал бы в журнал вторую строку
// op=revert, а Transition(«reverted»→«reverted») дал бы ложный «статус не записан».
//
// Статус откатанного пакета меняется через Transition стора: «applied» → «reverted» (решение
// постановщика по S4b-3; статус конечный, из него переходов нет). Повторный откат «reverted»-пакета
// отказан по статусу и не дописывает в журнал вторую строку: кнопка не повторяет уже сделанный откат.
// Если Transition не удался ПОСЛЕ того, как файлы уже откатаны, состояние остаётся со следом и
// открыто для доводки: следующий вызов перечитывает статус (он ещё «applied»), видит страницы уже
// в состоянии «до пакета» и доводит переход, не дописывая журнал второй раз — строка op=revert
// одна на один фактический откат. Строка журнала несёт id пакета (поле packages — та же форма,
// что у строки op=retract): доводка и «не повторять откат» решаются по нему, а не по содержимому.
// Два разных пакета с идентичным источником и идентичным набором страниц дают для человека
// идентичную строку — и это не делает второй откат уже сделанным: у каждого пакета свой id, и на
// каждый фактический откат своя строка. Строки, записанные до этой меры, id не несут: для них
// остаётся сверка по содержимому (op, раздел, источник, автор, полный набор страниц) — но только
// там, где она законна: когда этот вызов ничего не восстанавливал сам (все страницы уже стояли в
// состоянии «до пакета» — доводка витка, не доведшего статус). Чужая строка прежнего формата не
// может быть признаком отката, который сам что-то вернул: она не различает пакеты с идентичным
// источником и идентичными страницами.
// Обратный порядок — «заявка перехода, затем откат» — необратим: «reverted» → «applied» в таблице
// нет, и неудачный откат оставил бы «reverted»-пакет с неоткатанными файлами.
//
// «Уже в состоянии «до пакета»» — названный исход, а не молчание. На диске уже стоит прообраз,
// когда откат дошёл до середины и не довел статус, когда страницу вернули вручную байт в байт или
// когда страницу, созданную пакетом, снесли вне Хранителя. Различить эти случаи по диску нельзя,
// и для состояния раздела они одинаковы: файл уже в нём, и запись манифеста обязана быть приведена
// к нему (иначе следующий Prepare отказал бы «файл из манифеста отсутствует», а откат — «нет
// записи состояния применения»). Для честности они не одинаковы: такие страницы называются в
// результате отдельно (AlreadyPages) — RestoredPages описывает эффект операции над разделом,
// AlreadyPages — то, что этот вызов не трогал. Пакет при этом закрывается: его состояние уже
// наступило, и отказываться закрыть его значило бы держать раздел в дрейфе навсегда.
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
    /// <summary>Нет записи состояния применения (пакет заведён до меры ревью S4b-3, или её файл
    /// не читается): отката нет, и по текущему манифесту он не делается.</summary>
    public const string CodeNoApplied = "wiki_revert_no_applied";
    public const string CodeChanged = "wiki_revert_changed";
    public const string CodeInternal = "wiki_revert_internal";

    // Значение поля op строки журнала (мера 6): та же форма, что op=apply у применения и op=adopt
    // у Хранителя; по нему «Журнал» отличает откат от прочих операций.
    private const string LogOpRevert = "revert";

    // Тот же путь журнала, что у WikiManifest (его константа закрытая): строки отката ищутся там
    // же, где они пишутся.
    private const string LogRelPath = "keeper/log.jsonl";

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
    /// до применения (их обновляет «Журнал»). AlreadyPages — подмножество RestoredPages: страницы,
    /// уже стоявшие в состоянии «до пакета» к началу этого вызова (их вернул прежний виток этого же
    /// отката, или их снесли/вернули мимо Хранителя); этот вызов их файлом не трогал, и назвать их
    /// возвращёнными без этого поля было бы молчанием.</summary>
    public readonly record struct RevertResult(
        bool Reverted,
        string? Code,
        string? Reason,
        IReadOnlyList<string> ChangedPages,
        IReadOnlyList<string> RestoredPages,
        IReadOnlyList<string> AlreadyPages);

    /// <summary>Откатить пакет по его идентификатору: состояние читается с диска под id, а не со слов
    /// вызывающего (урок S4b-1). Здесь, до блока, читается только его раздел: без него нечем взять
    /// замок раздела; статус и всё остальное состояние перечитываются ВНУТРИ блока — там, где
    /// решается исход (ревью S4b-3, MAJOR 2). Откатывается только пакет в «applied»: «applying»
    /// закрывает сторож старта, «apply_failed» уже откатан, rejected/stale до диска не дошли,
    /// «reverted» отказан повторным. Под блокировкой раздела (тот же замок, что у записи и у
    /// сторожа): откат не пишет параллельно с применением этого раздела и не берёт второй
    /// блокировки того же ключа.</summary>
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

        return await _keeper.WithSectionsLockedAsync(ownerId, [read.Package!.Section],
            () => Task.FromResult(RevertUnderLock(ownerId, root, packageId)));
    }

    /// <summary>Тело отката под блоком раздела, который держит вызывающий. internal — для
    /// отката по источнику (WikiRetractService, S4f-1): он держит блок всех затронутых разделов
    /// разом и зовёт откат внутри него — второй блокировки того же ключа (зависания) нет.</summary>
    internal RevertResult RevertUnderLock(string ownerId, string root, string packageId)
    {
        try
        {
            return RevertBody(ownerId, root, packageId);
        }
        catch (Exception ex)
        {
            // Любой неожиданный сбой — результат-отказ с причиной, не исключение вызывающему
            // (тот же контракт, что у применения).
            return Fail(CodeInternal, $"внутренний сбой отката: {ex.Message}");
        }
    }

    private RevertResult RevertBody(string ownerId, string root, string packageId)
    {
        // Состояние пакета — перечитанное под блоком (ревью S4b-3, MAJOR 2): прочитанное до него
        // к моменту решения уже мог изменить другой откат этого же пакета.
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

        // Запись состояния применения этого пакета (ревью S4b-3, MAJOR 1): без неё сверки нет и
        // отката нет — сверять с текущим манифестом значило бы молча допустить, что после этого
        // пакета страницу никто не трогал.
        var applied = TryReadAppliedRecord(root, package.Id);
        if (applied is null)
            return Fail(CodeNoApplied,
                "нет записи состояния применения этого пакета: откат по текущему манифесту не делается — " +
                "манифест описывает последнее состояние страницы, а не состояние на момент этого применения");

        var mres = WikiManifest.TryRead(root, package.Section);
        if (mres.State == WikiManifestState.Invalid)
            return Fail(CodeManifest, $"манифест раздела «{package.Section}» невалиден: сверку с диском провести нельзя");

        // Фаза 1 — только чтения: сверка хэшей и байты прообразов. На диске здесь не трогается
        // ничего: отказ на этой фазе — всегда отказ «на диске не тронуто ничего».
        var changed = new List<string>();
        var already = new HashSet<string>(StringComparer.Ordinal);
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
            if (!applied.TryGetValue(page, out var appliedHash))
                return Fail(CodeNoApplied, $"запись состояния применения не полна: для страницы «{page}» записи нет");

            // Тот же хэш, что у манифеста и применения (sha256 байтов), — им же записана и запись
            // состояния применения. Равен ей — страница в том состоянии, которое оставил этот пакет,
            // и она откатывается.
            var diskHash = disk is null ? null : WikiApplyService.Sha256OfBytes(disk);
            if (diskHash == appliedHash)
                continue;
            // На диске уже стоит состояние «до пакета»: его вернул этот откат в витке, который не
            // довел статус (или остановился на середине), либо его сделал кто-то мимо Хранителя —
            // правка байт в байт к прообразу или снос созданной пакетом страницы. По диску эти
            // случаи неразличимы и для раздела одинаковы: файл в нужном состоянии, и остаётся
            // довести статус и запись манифеста. Файл не перечитываем — возвращать нечего; страница
            // идёт в AlreadyPages: названная, а не записанная как возвращённая этим вызовом.
            var beforeHash = preimageBytes[page].Length == 0 ? null : WikiApplyService.Sha256OfBytes(preimageBytes[page]);
            if (diskHash == beforeHash)
            {
                already.Add(page);
                continue;
            }
            // Ни применённого состояния, ни прежнего: страницу меняли после этого пакета — более
            // поздний пакет или правка мимо Хранителя. Откат стёр бы её вместе с пакетом.
            changed.Add(page);
        }
        if (changed.Count > 0)
            return new RevertResult(false, CodeChanged,
                $"страницы изменены после применения этого пакета (диск не совпадает с его записью состояния применения): " +
                $"{string.Join(", ", changed)}. Отказ: откат стёр бы чужую правку или запись более позднего пакета; " +
                "на диске не тронуто ничего",
                changed, [], []);

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
                if (already.Contains(page))
                {
                    // Файл уже в состоянии «до пакета»: его не перечитываем (молчаливой перезаписи
                    // нет), но его запись манифеста обязана быть тем же хэшем, что файл.
                    restored.Add(page);
                    rollback[page] = bytes.Length == 0 ? null : WikiApplyService.Sha256OfBytes(bytes);
                    continue;
                }
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
        // Ровно одна строка op=revert на один фактический откат (ревью S4b-3, MAJOR 2): виток,
        // который не довел статус, журнал не дописывает — следующий вызов доводит статус и
        // оставляет строку как есть. Строка называет id этого пакета (поле packages, та же форма,
        // что у op=retract): «уже откатан» решается по id, а не по совпадению содержимого — два
        // пакета с идентичным источником и идентичными страницами откатываются каждый своим
        // откатом, и каждый получает свою строку.
        if (!RevertAlreadyLogged(root, package, pages, already.Count))
            WikiManifest.AppendLog(root, LogOpRevert, package.Section, restored, SourceOf(package), "user",
                null, [package.Id]);
        WikiProjections.RegenerateSection(root, package.Section);

        // Что этот вызов не трогал — названо по каноническому порядку страниц, а не по порядку
        // обхода: тот же порядок, что у строки журнала и у сверки.
        var alreadyPages = pages.Where(p => already.Contains(p)).ToList();

        // Статус — через Transition стора, единственную дверь в статусы: «applied» → «reverted»,
        // конечный, из него переходов нет.
        var done = WikiPackageStore.Transition(root, package.Id, WikiPackage.StatusReverted);

        // Файлы раздела изменены при любом исходе статуса — модель per-owner не должна остаться
        // о них осведомлённой (тот же инвариант, что у применения и сторожа).
        _notes.Invalidate(ownerId);

        if (!done.Ok)
            // Файлы откатаны и строка журнала записана, но статус не доведён: состояние со следом
            // и открыто для доводки — следующий вызов перечитает статус под блоком, увидит страницы
            // в состоянии «до пакета» и доведёт переход, не дописывая журнал второй раз. Обратный
            // порядок — «заявка перехода, затем откат» — необратим: «reverted» → «applied» в таблице
            // нет. Исход назван вызывающему, а не молчание (минор 4, ревью S4b-1).
            return new RevertResult(false, CodeClaim,
                $"файлы откатаны, статус «reverted» не записан: {done.Reason ?? done.Code}",
                [], restored, alreadyPages);

        return new RevertResult(true, null, null, [], restored, alreadyPages);
    }

    /// <summary>Карточка источника строки журнала — карточки источников пакета (та же форма, что у
    /// строки применения): по ним «Журнал» и откат по источнику (мера 5) связывают пакет и его чат.</summary>
    private static string SourceOf(WikiPackage package) =>
        string.Join(", ", package.Sources.Select(s => s.Ref));

    /// <summary>Прочитать запись состояния применения пакета: страница → sha256 тех байтов, которые
    /// записал этот пакет (пишет её шаг 6 применения — WikiApplyService.AppliedRecordRel). Нет
    /// файла или не читается — null: отката нет (fail-closed), а не откат по манифесту.
    /// internal: её читает и снятие сносок (WikiRetractService, S4f-2), дополняя запись своим
    /// состоянием, — форма определена здесь, в одном месте, как прообразы.</summary>
    internal static IReadOnlyDictionary<string, string>? TryReadAppliedRecord(string root, string packageId)
    {
        var full = SafePath.Join(root, WikiApplyService.AppliedRecordRel(packageId));
        if (!File.Exists(full))
            return null;
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(full));
            if (map is not null)
                return map;
        }
        catch (JsonException)
        {
            // Битая запись — тот же исход, что её отсутствие: состояние применения неизвестно.
        }
        return null;
    }

    /// <summary>Строка op=revert этого пакета в журнале уже есть (ревью S4b-3, MAJOR 2): виток,
    /// не доведший статус, не дописывает её второй раз. Признак — id пакета в поле packages строки
    /// (та же форма, что у op=retract): он один отвечает на вопрос «этот ли откат уже записан».
    /// Строка другого пакета не является им, даже когда человек видит две идентичные строки: два
    /// пакета с идентичным источником и идентичным набором страниц откатываются каждый своим
    /// откатом, и на каждый фактический откат — своя строка. Строки, записанные до этой меры, id
    /// не несут: для них остаётся прежняя сверка по содержимому (op, раздел, карточка источника,
    /// автор, полный набор страниц) — доводки прежнего круга они не теряют, но только для случая
    /// «этот вызов ничего не восстанавливал сам» (alreadyCount == pages.Count): тогда он — доводка
    /// витка, чья строка уже в журнале. Если этот вызов что-то вернул сам, чужая строка прежнего
    /// формата им быть не может: по содержимому она неотличима от строки другого пакета, а исход,
    /// который она описывает, — не этот откат. Поэтому мера — число страниц, которые этот вызов
    /// нашёл уже стоящими в состоянии «до пакета», а не их набор.</summary>
    private static bool RevertAlreadyLogged(
        string root, WikiPackage package, IReadOnlyList<string> pages, int alreadyCount)
    {
        var full = SafePath.Join(root, LogRelPath);
        if (!File.Exists(full))
            return false;
        var source = SourceOf(package);
        try
        {
            foreach (var line in File.ReadLines(full))
            {
                if (line.Length == 0)
                    continue;
                JsonElement el;
                try
                {
                    el = JsonDocument.Parse(line).RootElement;
                }
                catch (JsonException)
                {
                    continue; // битая строка журнала — не признак отката
                }
                if (Str(el, "op") != LogOpRevert || Str(el, "section") != package.Section)
                    continue;
                if (el.TryGetProperty("packages", out var ids) && ids.ValueKind == JsonValueKind.Array)
                {
                    // Строка этого круга: id решает. Чужой id — не этот откат.
                    if (ids.EnumerateArray().Any(x => x.GetString() == package.Id))
                        return true;
                    continue;
                }
                // Строка прежнего круга (без id) сверяется по содержимому только там, где эта
                // сверка законна: когда этот вызов ничего не восстанавливал сам — все страницы уже
                // стояли в состоянии «до пакета» (доводка витка, не доведшего статус). Такого
                // вызова строка уже в журнале. Если этот вызов что-то вернул сам, чужая строка
                // прежнего формата им не является: на каждый фактический откат — своя строка.
                if (alreadyCount != pages.Count)
                    continue;
                if (Str(el, "actor") != "user" || Str(el, "source") != source)
                    continue;
                if (!el.TryGetProperty("pages", out var logged) || logged.ValueKind != JsonValueKind.Array)
                    continue;
                var keys = new HashSet<string>(logged.EnumerateArray().Select(x => x.GetString() ?? ""), StringComparer.Ordinal);
                if (pages.All(p => keys.Contains(p)))
                    return true;
            }
        }
        catch (IOException)
        {
            // Журнал не читается: считаем, что строки нет; её запись тогда тоже не удастся —
            // тот же отказ с причиной, а не молчаливая потеря строки отката.
        }
        return false;
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) ? v.GetString() : null;

    /// <summary>Срок хранения прообразов и состояний «до снятия сносок»: между бэкапами откат
    /// держит они (ADR-035, решение 13); после него — мусор, который чистит сторож старта.</summary>
    public static readonly TimeSpan PreimageRetention = TimeSpan.FromDays(90);

    /// <summary>Удалить прообразы и состояния «до снятия сносок» (keeper/retract/, S4f-2) старше
    /// срока хранения (90 дней): и то и другое держит откат между бэкапами (решение 13), после
    /// срока — мусор; срок один, чтобы «снять след» и «вернуть файл» не жили разными сроками.
    /// Возраст — по времени записи файла: это тот момент, когда файл стал прообразом/состоянием.
    /// Ровно на границе (90 суток) файл остаётся: «старше» значит строго старше (интервал
    /// хранения — [now−90d, now]). За тот же срок чистятся и записи состояния применения
    /// (keeper/applied/) — но только у пакета, который больше не может быть откатан: см.
    /// PurgeOldApplied. Сбой на одном файле не останавливает остальные и наружу исключения не
    /// выходит: вызывающий — сторож старта, фоновая служба не имеет права ронять сервер. Пустой
    /// каталог пакета удаляется: держать нечего. now — то же время, которым помечены файлы (тест
    /// подставляет своё); returns — число удалённых файлов.</summary>
    public static int PurgeOldPreimages(string root, DateTimeOffset now)
    {
        var cutoff = now.ToUniversalTime() - PreimageRetention;
        return PurgeOldDir(SafePath.Join(root, "keeper/preimages"), cutoff)
            + PurgeOldDir(SafePath.Join(root, "keeper/retract"), cutoff)
            + PurgeOldApplied(root, cutoff);
    }

    /// <summary>Запись состояния применения (keeper/applied/{pkgId}.json) держит ровно то, чем
    /// меряется побайтный откат этого пакета, и после его срока хранения — мусор. Удаляется только
    /// запись пакета в «apply_failed»: его файлы уже возвращены сторожем старта по прообразам, и
    /// этой записью он не владеет ни для чего. Пакет в «applied» защищён по статусу: это тот, кто
    /// откатывается прямо сейчас или будет откатан позже, — снести его запись посреди отката
    /// значило бы вернуть откату «нет записи состояния применения» на ровном месте. Прочие статусы
    /// (в том числе нечитаемый или отсутствующий файл пакета) не чистятся: неизвестное состояние —
    /// не признак мусора (fail-closed, как везде в этой мере).</summary>
    private static int PurgeOldApplied(string root, DateTimeOffset cutoff)
    {
        var dir = SafePath.Join(root, "keeper/applied");
        if (!Directory.Exists(dir))
            return 0;
        var deleted = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                if (new DateTimeOffset(File.GetLastWriteTimeUtc(f)) >= cutoff)
                    continue;
                var read = WikiPackageStore.TryRead(root, Path.GetFileNameWithoutExtension(f));
                if (read.State != WikiPackageFileState.Valid || read.Package!.Status != WikiPackage.StatusApplyFailed)
                    continue;
                File.Delete(f);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Один недоступный файл не останавливает чистку остальных (тот же закон, что у PurgeOldDir).
            }
        }
        return deleted;
    }

    private static int PurgeOldDir(string dir, DateTimeOffset cutoff)
    {
        if (!Directory.Exists(dir))
            return 0;
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
        new(false, code, reason, [], [], []);
}
