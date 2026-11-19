using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Хранитель (ADR-035, решение 2) — единственный писатель файлов под data/wiki/{ownerId}/pages/.
// Запрет «пишет только Хранитель» держат отказ на чужих путях (WikiPaths.IsWikiPath) и здесь:
// перед каждой записью байты файла на диске сверяются с манифестом раздела, расхождение —
// статус Drift и отказ писать. Молчаливой перезаписи нет никогда: внешняя правка (человек,
// Obsidian) остаётся на диске, пока человек не скажет «Принять как есть» (adopt) или не
// вернёт файл из прообразов.
//
// Блокировка и манифест ключуются парой (ownerId, section) (решение 10): разделы независимы —
// ночной разбор одного проекта не держит ручное применение в другом. Операция над двумя
// разделами берёт обе блокировки в каноническом порядке ключей (WithSectionsLockedAsync):
// встречные операции на одной паре иначе встают в взаимной блокировке.
//
// Здесь — контракт записи и её последствия (манифест, проекции раздела, инвалидация кэша
// графа, событие журнала). Пакеты, прообразы и _health.md — следующий шаг (S2c):
// он же приведёт сюда op/actor применения и строки дрейфа в health.
public sealed class WikiKeeper : IWikiRelink
{
    private readonly string _dataDir;
    private readonly NotesService _notes;
    private readonly IProjectEventLogService? _events;
    // Время сервера — то же, что у остальных дат вертикали (флаг бэклога чистки считает
    // «сегодня» по нему, а не по DateTime.Now — ревью J-P1c).
    private readonly TimeProvider _time;

    private readonly ConcurrentDictionary<(string Owner, string Section), SemaphoreSlim> _locks = new();
    /// <summary>Тестовый сигнал: операция подошла взять замок раздела — ДО ожидания. Нужен
    /// там, где тест обязан знать, что встречная операция стоит на чужом замке, а не мерит
    /// это по времени: без сигнала окно проверки строится паузой и флакает под параллелью
    /// (Ф7). Вызов — вне блокировок, только когда тест навесил хук; в проде он null.</summary>
    internal Action<string, string>? SectionLockRequestedHook;
    // Блокировка инициализации — на владельца, вне раздела: «Завести» трогает только
    // pages/_schema.md и служебные каталоги, ни один раздел не держит.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _initLocks = new();

    // Шаблон pages/_schema.md — признака заведённости (решение 1): правила страниц по
    // решению 3. Правит файл только человек через Хранителя (операция schema, решение 6),
    // поэтому «Завести» пишет шаблон ровно один раз и больше к нему не прикасается.
    private const string SchemaTemplate = """
        # Схема вики

        Правила страниц этой вики. Хранитель читает этот файл при разборе пакета;
        правит его только человек через Хранителя (системная операция `schema`).

        ## Страница
        - Шапка (frontmatter): `type` (закрытый перечень — ADR-035, решение 3), `kind`,
          `summary`, `aliases`, `owns`. Поля `sources`, `updated`, `status` считает код.
        - Тела разделы фиксированы: «Сводка» → «Хронология» → «Противоречия» → «История».
        - Удаления нет: замещённое утверждение переносится в «Историю» дословно, со
          сноской и датами «с — по».

        ## Утверждение и цитата
        Утверждение — абзац или пункт «Сводки»/«Противоречий». Каждое заканчивается
        сноской `[^sN]: [[Карточка источника]] — «цитата»`; цитата 8–300 символов,
        дословно из источника.

        ## Ссылки
        Внутри раздела — `[[Страница]]`; между разделами — с префиксом раздела,
        например `[[Сферы/{id}/Страница]]`.

        ## Разделы
        `Проекты/{id}/`, `Сферы/{id}/`, `Общее/`, `Личное/` — папки ключуются по id,
        а не по имени. Раздел заводится первой применённой операцией в нём.
        """;

    public WikiKeeper(NotesService notes, IConfiguration config, IProjectEventLogService? events = null, TimeProvider? time = null)
    {
        _notes = notes;
        _events = events;
        _time = time ?? TimeProvider.System;
        // Каталог данных — как у всех сервисов (DataPath указывает на projects.json в dataDir).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    /// <summary>«Завести вики»: pages/_schema.md из шаблона и каталоги keeper/.
    /// Повторный вызов ничего не меняет: существующий _schema.md не перезаписывается
    /// (его мог править человек), каталоги уже есть. Разделы при этом НЕ заводятся:
    /// раздел появляется первой применённой операцией в нём (решение 1).
    /// Инициализация — под блокировкой владельца (ревью S2b-2, MAJOR): две одновременные
    /// «Завести» иначе проверяют существование схемы до записи друг друга и вторая
    /// перезаписывает шаблон (или правку человека, если она успела между проверкой и
    /// записью). Блокировка и повторная проверка под ней дают: ровно одна запись шаблона.</summary>
    public async Task EnsureInitializedAsync(string ownerId)
    {
        var initLock = _initLocks.GetOrAdd(ownerId, _ => new SemaphoreSlim(1));
        await initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var root = WikiPaths.Root(_dataDir, ownerId);
            // Служебные каталоги — те, что по ADR являются каталогами (manifest/, packages/,
            // preimages/); файлы журнала и метрик появятся при первой записи в них.
            foreach (var dir in new[] { "keeper/manifest", "keeper/packages", "keeper/preimages" })
                Directory.CreateDirectory(SafePath.JoinBelowRoot(root, dir));

            const string schemaRel = "pages/" + WikiPaths.SchemaFileName;
            var schema = SafePath.JoinBelowRoot(root, schemaRel);
            if (!File.Exists(schema))
                WikiFileWriter.Write(root, schemaRel, SchemaTemplate);
        }
        finally
        {
            initLock.Release();
        }
    }

    /// <summary>Записать страницу (pageKey — путь относительно pages/, тот же ключ, что в
    /// манифесте и журнале). Непосредственно перед записью — сверка байтов файла на диске
    /// с манифестом раздела: расхождение (чужая правка, чужое удаление, файл вне манифеста)
    /// — Drift, запись не выполняется; манифест Invalid — Refused с причиной, ничего не
    /// применяется и ничего не трогается. После записи в манифест кладётся sha256 байтов
    /// файла, прочитанных с диска.</summary>
    public Task<WikiOpResult> WritePageAsync(string ownerId, string sectionKey, string pageKey, string content)
    {
        var refusal = CheckPage(sectionKey, pageKey);
        if (refusal is not null)
            return Task.FromResult(new WikiOpResult(WikiOpStatus.Refused, refusal));
        return WithSectionsLockedAsync(ownerId, [sectionKey], () => WriteUnderLockAsync(ownerId, sectionKey, pageKey, content));
    }

    /// <summary>«Принять как есть» — системная операция adopt (решение 6): текущий файл на
    /// диске принимается в манифест раздела, дрейф снимается, файл не трогается.
    /// Вызывается только REST по кнопке человека (S2f); в журнал идёт строка op=adopt,
    /// в лог проекта — событие с честным автором «user».</summary>
    public Task<WikiOpResult> AdoptAsync(string ownerId, string sectionKey, string pageKey)
    {
        var refusal = CheckPage(sectionKey, pageKey);
        if (refusal is not null)
            return Task.FromResult(new WikiOpResult(WikiOpStatus.Refused, refusal));
        return WithSectionsLockedAsync(ownerId, [sectionKey], () => AdoptUnderLockAsync(ownerId, sectionKey, pageKey));
    }

    /// <summary>Удаление раздела-сироты (ADR-036, решение 1): только по кнопке человека,
    /// сиротство проверяет вызывающий (у Хранителя нет справочника проектов/сфер). Сносит
    /// папку раздела под pages/ (проекции — в ней) и манифест раздела; журнал и прообразы
    /// остаются: история вики не удаляется. Кэш графа инвалидируется: удалённая страница
    /// не должна остаться в графе. Автоочистки сирот нет — это решение человека.</summary>
    public async Task<WikiOpResult> DeleteSectionAsync(string ownerId, string sectionKey)
    {
        var folder = WikiSections.Folder(sectionKey);
        if (folder is null)
            return Refused($"«{sectionKey}» — не ключ раздела");
        return await WithSectionsLockedAsync(ownerId, [sectionKey], async () =>
        {
            var root = WikiPaths.Root(_dataDir, ownerId);
            var dir = SafePath.JoinBelowRoot(root, "pages/" + folder);
            if (!Directory.Exists(dir))
                return Refused($"папки раздела «{sectionKey}» на диске нет — удалять нечего");
            Directory.Delete(dir, recursive: true);
            var manifest = Path.Combine(WikiPaths.Keeper(_dataDir, ownerId), "manifest",
                WikiManifest.SafeFileName(sectionKey) + ".json");
            if (File.Exists(manifest)) File.Delete(manifest);
            _notes.Invalidate(ownerId);
            return new WikiOpResult(WikiOpStatus.Applied, null);
        });
    }

    // ——— Чистка ИИ-страниц (J-P1a, ADR-039 решение 10, разрез 2026-11-12 §4) ———

    /// <summary>Коды отказа чистки — контракт вызывающего (J-P2), устойчивые строки, как коды
    /// стора пакетов.</summary>
    public const string PurgeCodeArchive = "wiki_purge_archive";
    public const string PurgeCodeIo = "wiki_purge_io";
    public const string PurgeCodeManifest = "wiki_purge_manifest";

    /// <summary>Срок флага бэклога после чистки (J-P1c): разбор всего архива — бэклог, и
    /// поднятый лимит новых страниц раздела действует ровно отмеренный срок.</summary>
    public const int PurgeBacklogDays = 14;

    // Ссылка на карточку — это ссылка (WikiPage.LinkRx), где бы она ни стояла: в определении
    // сноски или в теле.

    /// <summary>Чистка ИИ-страниц (ADR-039, решение 10): вики начинается заново — страницы
    /// модели уходят, разбор перезапускается. Разрушающая: сначала предпросмотр (dryRun —
    /// только результат, на диск ничего не пишется, архива нет), затем zip всего дерева
    /// владельца, и только потом удаление. Инициатор — человек (кнопка J-P2): автор строки
    /// журнала «user», тот же, что у revert/adopt.
    ///
    /// Предикат удаления (решение Андрея 12.11, №2): авторство по PageAuthorship — «ai» или
    /// «together»; chronicle (по построению ИИ); карточка source, на которую не ссылается ни
    /// одна оставшаяся страница (проверка — после помеченных страниц: сноска удалённой страницы
    /// не должна держать карточку). Не трогать: «human», принятые как есть (строка op=adopt в
    /// журнале — решение человека), проекции «_*», карточки, на которые ссылаются оставшиеся,
    /// неразбираемые файлы (авторства по ним сказать нельзя — fail-closed).
    ///
    /// Все разделы владельца — под замками в каноническом порядке: параллельное применение в тот
    /// же раздел ждёт; после обоих манифест согласен с диском. Невалидный манифест затронутого
    /// раздела — отказ до первой записи: разрушающая операция должна быть доводима (при dryRun
    /// не проверяется: предпросмотр описывает состав, а не исход). Архив не создан — ничего не
    /// удаляем, исход — код; исключения наружу нет. После удаления — записи манифестов
    /// разделов, строка журнала op=purge (по одной на раздел: формат журнала sectional),
    /// перегенерация проекций, инвалидация графа, «stale» открытых ИИ-пакетов, сброс курсоров
    /// разбора и флаг бэклога на PurgeBacklogDays дней (при dryRun и при любом отказе — не
    /// звать). Применённые пакеты не трогаются.</summary>
    public async Task<WikiPurgeResult> PurgeAiPagesAsync(string ownerId, bool dryRun)
    {
        // Корень — той же строкой, что у остальных вызовов стора (ревью a2db48b9): ключ замка
        // ResetCursors — сам путь, root со слешем на конце дал бы другой семафор, и гонка вернулась.
        var root = WikiPaths.Root(_dataDir, ownerId);
        var pagesDir = Path.Combine(root, "pages");
        if (!Directory.Exists(pagesDir))
            return PurgeOk(dryRun, [], null, 0, 0); // вики нет или пустой pages/: удалять нечего

        List<(string Rel, string Section)> files;
        try
        {
            files = [];
            foreach (var full in Directory.EnumerateFiles(pagesDir, "*.md", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(pagesDir, full).Replace('\\', '/');
                if (WikiPaths.IsProjection(rel)) continue; // служебный файл раздела — не страница
                var section = WikiSections.Parse(rel);
                if (section is null) continue; // путь вне разделов — не страница вики
                files.Add((rel, section));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return PurgeFail(dryRun, PurgeCodeIo, "каталог pages/ не читается — предпросмотр невозможен");
        }

        var sections = files.Select(f => f.Section).Distinct(StringComparer.Ordinal).ToList();
        return await WithSectionsLockedAsync(ownerId, sections, () => PurgeUnderLock(ownerId, root, files, dryRun));
    }

    // Тело чистки под всеми замками (тот же порядок, что у RevertUnderLock): сам метод
    // синхронный — весь его I/O последовательный, await здесь не для чего.
    private Task<WikiPurgeResult> PurgeUnderLock(string ownerId, string root, List<(string Rel, string Section)> files, bool dryRun)
    {
        // Одно чтение всего состояния: авторство, ссылки и сиротский тест — по одному чтению
        // одних и тех же файлов. Нечитаемый файл — «неразбираемый»: он остаётся нетронутым,
        // но его текст участвует в поиске ссылок (ссылка не требует разобранной шапки).
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var models = new Dictionary<string, WikiPage?>(StringComparer.Ordinal);
        foreach (var (rel, _) in files)
        {
            string text;
            try { text = File.ReadAllText(SafePath.JoinBelowRoot(root, "pages/" + rel), Encoding.UTF8); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { text = ""; }
            texts[rel] = text;
            models[rel] = text.Length == 0 ? null : WikiPage.Parse(text).Model;
        }

        // Карточки — тот же ключ, что у авторства (WikiProjections.Cards): нормализованный заголовок.
        var cards = new Dictionary<string, (string Rel, string Section, string? Origin)>(StringComparer.Ordinal);
        foreach (var (rel, section) in files)
            if (models[rel] is { } card && card.Frontmatter.Type == WikiFrontmatter.TypeSource)
                cards.TryAdd(WikiGate.Norm(card.Title), (rel, section, card.Frontmatter.Origin));

        var adopted = ReadAdoptedPageKeys(root);
        if (adopted is null)
            // Без журнала нельзя сказать, какие страницы человек уже принял: составить состав
            // чистки нечем — честный отказ, а не чистка вслепую.
            return Task.FromResult(PurgeFail(dryRun, PurgeCodeIo, "журнал не читается — состав чистки определить нельзя"));
        var doomed = new List<WikiPurgeItem>();
        foreach (var (rel, section) in files)
        {
            var page = models[rel];
            if (page is null) continue; // неразбираемый: авторства нет — не трогать (fail-closed)
            // Приёмыш — решение человека (И6), и оно раньше деления по типам: карточка,
            // принятая как есть, защищена им наравне с принятой страницей (ревью R-P1a, M1).
            if (adopted.Contains(rel)) continue; // принятый как есть — не трогать, любой тип
            if (page.Frontmatter.Type == WikiFrontmatter.TypeSource) continue; // карточки — ниже, сиротским тестом
            var authorship = WikiProjections.PageAuthorship(page,
                t => cards.TryGetValue(WikiGate.Norm(t), out var c) ? c.Origin : null);
            if (authorship == WikiProjections.AuthorshipHuman) continue; // человеческое — не трогать (И6)
            // Решение Андрея 12.11 (№2): «together» уходит вместе с «ai» — содержимое в архиве,
            // разбор соберёт страницу заново. Хроника — по построению ИИ, без проверки авторства.
            doomed.Add(new WikiPurgeItem(rel, section,
                page.Frontmatter.Type == WikiFrontmatter.TypeChronicle ? WikiFrontmatter.TypeChronicle : authorship));
        }

        // Ссылки оставшихся страниц на карточки: цель сноски и любая [[…]]-ссылка в тексте.
        // Удалённые страницы в счёт не идут: их сноска не должна держать карточку (иначе ни
        // одна карточка, названная в «Сводке» ИИ-страницы, не была бы сиротой никогда).
        var doomedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in doomed) doomedKeys.Add(item.PageKey);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var referencedQualified = new HashSet<(string Section, string Title)>();
        foreach (var (rel, _) in files)
        {
            if (doomedKeys.Contains(rel)) continue;
            if (models[rel] is { } own && own.Frontmatter.Type == WikiFrontmatter.TypeSource) continue; // карточка — не читатель
            foreach (Match m in WikiPage.LinkRx.Matches(texts[rel]))
            {
                var target = m.Groups[1].Value;
                referenced.Add(WikiGate.Norm(target)); // внутри раздела — голым заголовком
                var slash = target.LastIndexOf('/');
                if (slash > 0 && WikiSections.Parse(target) is { } qualified)
                    referencedQualified.Add((qualified, WikiGate.Norm(target[(slash + 1)..]))); // с префиксом своего раздела
            }
        }

        // Сиротский тест не трогает принятые карточки: «приём как есть» — тот же И6, и он
        // сильнее сиротства (ревью R-P1a, M1).
        foreach (var (norm, card) in cards)
            if (!adopted.Contains(card.Rel) && !referenced.Contains(norm) && !referencedQualified.Contains((card.Section, norm)))
                doomed.Add(new WikiPurgeItem(card.Rel, card.Section, WikiFrontmatter.TypeSource));

        var items = doomed.OrderBy(d => d.PageKey, StringComparer.Ordinal).ToList();
        if (dryRun)
            return Task.FromResult(PurgeOk(dryRun, items, null, 0, 0)); // предпросмотр: на диск ничего не пишется, архива нет
        if (items.Count == 0)
            return Task.FromResult(PurgeOk(dryRun, items, null, 0, 0)); // удалять нечего: ни архива, ни журнала, ни перезапуска

        // Невалидный манифест затронутого раздела — отказ до первой записи: чистка обязана
        // оставить манифест согласованным с диском, а с нечитаемым манифестом это невозможно.
        foreach (var section in items.Select(i => i.Section).Distinct(StringComparer.Ordinal))
            if (WikiManifest.TryRead(root, section).State == WikiManifestState.Invalid)
                return Task.FromResult(PurgeFail(dryRun, PurgeCodeManifest, $"манифест раздела «{section}» невалиден — чистка невозможна"));

        var archiveRel = $"keeper/archive/purge-{_time.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}.zip";
        var archiveFull = SafePath.Join(root, archiveRel);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(archiveFull)!);
            using (var fs = new FileStream(archiveFull, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                AddTreeToZip(zip, root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Половина архива не должна остаться в каталоге архивов: она притворяется архивом.
            try { File.Delete(archiveFull); } catch (Exception) { /* уже нет — не наша уборка */ }
            return Task.FromResult(PurgeFail(dryRun, PurgeCodeArchive, "архив не создан — ничего не удалено: разрушающая операция без архива не делается"));
        }

        var deleted = new List<WikiPurgeItem>();
        var counts = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        try
        {
            foreach (var item in items)
            {
                File.Delete(SafePath.JoinBelowRoot(root, "pages/" + item.PageKey));
                deleted.Add(item);
            }

            foreach (var group in items.GroupBy(i => i.Section, StringComparer.Ordinal))
            {
                var byClass = group.GroupBy(i => i.Class, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
                counts[group.Key] = byClass;

                // Манифест следует за диском: записей удалённых страниц больше нет (тот же
                // RollbackTo, что у отката create-страницы). Empty-манифеста нет — чистить нечего.
                var mres = WikiManifest.TryRead(root, group.Key);
                if (mres.State == WikiManifestState.Valid)
                    WikiManifest.RollbackTo(root, group.Key,
                        group.ToDictionary(i => i.PageKey, _ => (string?)null));
                // Строка журнала системной операции человека: одна на раздел (формат журнала
                // sectional, тот же порядок, что у relink), policy null — меры политики нет.
                WikiManifest.AppendLog(root, "purge", group.Key,
                    group.Select(i => i.PageKey).ToList(), null, "user");
                // _log.md раздела — производная журнала: без перегенерации он остался бы на
                // полшага позади операции (тот же порядок, что у adopt/revert).
                WikiProjections.RegenerateSection(root, group.Key);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Архив на диске, часть страниц уже удалена. Манифест чинится здесь же, по
            // фактически удалённым: удалённых файлов больше нет на диске, и повторная чистка
            // до их записей не дойдёт — оставленные, они разошлись бы навсегда (ревью R-P1a, M2).
            // Строки журнала нет: наполовину сделанная операция — не операция; фактический
            // состав — в отказе. Сбой самой починки наружу не бросается (контракт «без
            // исключений» держится на любом исходе чистки) — он назван в причине.
            string repairNote;
            try
            {
                foreach (var group in deleted.GroupBy(i => i.Section, StringComparer.Ordinal))
                    if (WikiManifest.TryRead(root, group.Key).State == WikiManifestState.Valid)
                        WikiManifest.RollbackTo(root, group.Key,
                            group.ToDictionary(i => i.PageKey, _ => (string?)null));
                repairNote = "манифест починен по фактически удалённым";
            }
            catch (Exception)
            {
                repairNote = "манифест не починен";
            }

            return Task.FromResult(new WikiPurgeResult(false, PurgeCodeIo,
                $"архив создан, удаление прервано: удалено {deleted.Count} из {items.Count}; {repairNote}",
                dryRun, deleted,
                deleted.GroupBy(i => i.Section, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, int>)g.GroupBy(i => i.Class, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal), StringComparer.Ordinal),
                archiveRel, 0, 0));
        }

        // Граф обязан не видеть удалённые страницы сразу (тот же шаг, что у записи); синк Dify
        // подчистит их сам при следующем проходе, отдельного каскада не пишем (разрез §4.6).
        _notes.Invalidate(ownerId);

        // Перезапуск разбора — только после состоявшегося удаления (ревью J-P1c): открытые
        // ИИ-пакеты гасятся, курсоры обнуляются, бэклог поднимается на отмеренный срок.
        // Сбой здесь — не отказ чистки (она состоялась): исход с честной пометкой, исключения
        // наружу нет ни на одном шаге операции.
        try
        {
            var staled = WikiPackageStore.StaleOpenAiPackages(root, $"чистка вики {DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime):yyyy-MM-dd}");
            var triage = WikiChatTriageStore.ResetCursors(root);
            var until = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime).AddDays(PurgeBacklogDays);
            WikiBacklogFlag.Set(root, until);
            return Task.FromResult(PurgeOk(dryRun, items, archiveRel, staled, triage.Ok ? triage.ResetCount : 0, counts));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new WikiPurgeResult(true, null,
                $"чистка выполнена, перезапуск разбора не доведён: {e.Message}", dryRun, items, counts, archiveRel, 0, 0));
        }
    }

    /// <summary>Страницы, принятые как есть (приёмыши): pageKey из строк журнала op=adopt.
    /// Строки читаются терпимо, как в проекциях: оборванная — не строка. Отказ чтения — пустое
    /// множество: «приёмышей нет» хуже молчания о решении человека, поэтому вызывающий на сбое
    /// журнала получает отказ (состав чистки без этой проверки сказать нельзя).</summary>
    private static HashSet<string>? ReadAdoptedPageKeys(string root)
    {
        var adopted = new HashSet<string>(StringComparer.Ordinal);
        var full = SafePath.Join(root, "keeper/log.jsonl");
        string[] lines;
        try { lines = File.Exists(full) ? File.ReadAllLines(full, Encoding.UTF8) : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var obj = doc.RootElement;
                if (obj.ValueKind != JsonValueKind.Object) continue;
                if (!obj.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String || op.GetString() != "adopt") continue;
                if (!obj.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array) continue;
                foreach (var p in pages.EnumerateArray())
                    if (p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } pageKey)
                        adopted.Add(pageKey);
            }
            catch (JsonException) { /* обрыв посреди записи — не строка (тот же приём, что у _log.md) */ }
        }
        return adopted;
    }

    /// <summary>Весь снимок вики владельца в архив: pages/ и keeper/, кроме самого каталога
    /// archive/ (его снимок в самом архиве — бесконечен) и имени temp атомарной записи.
    /// Имена записей — относительные пути с «/»: архив читается на любой системе.</summary>
    private static void AddTreeToZip(ZipArchive zip, string root)
    {
        var archiveDir = Path.Combine(root, "keeper", "archive");
        foreach (var dirRel in new[] { "pages", "keeper" })
        {
            var dir = Path.Combine(root, dirRel);
            if (!Directory.Exists(dir)) continue;
            foreach (var full in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (full.StartsWith(archiveDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                if (full.EndsWith(".tmp", StringComparison.Ordinal)) continue; // остаток упавшей атомарной записи
                var entry = zip.CreateEntry(Path.GetRelativePath(root, full).Replace('\\', '/'), CompressionLevel.SmallestSize);
                using var source = File.Open(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var target = entry.Open();
                source.CopyTo(target);
            }
        }
    }

    private static WikiPurgeResult PurgeOk(bool dryRun, IReadOnlyList<WikiPurgeItem> items, string? archivePath,
        int staledPackages, int triageReset,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>? counts = null) =>
        new(true, null, null, dryRun, items,
            counts ?? items.GroupBy(i => i.Section, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, int>)g.GroupBy(i => i.Class, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal), StringComparer.Ordinal),
            archivePath, staledPackages, triageReset);

    private static WikiPurgeResult PurgeFail(bool dryRun, string code, string reason) =>
        new(false, code, reason, dryRun, [], new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal), null, 0, 0);

    /// <summary>relink — системная операция (ADR-035, решение 6): переименована (newTitle)
    /// или истекла/удалена (newTitle null) заметка, на которую ссылаются страницы вики.
    /// Смысловых правок нет: переименование меняет только цель [[…]] с сохранением её
    /// квалификатора (префикса раздела/источника), подписи и якоря (тот же код
    /// NotesService.RewriteLinks), истечение разворачивает ссылку в текст в любой форме
    /// записи (тот же UnlinkTo) — второго варианта «замены ссылки» в вертикали нет.
    /// Ссылка, квалифицированная чужим разделом, называет страницу того раздела, а не
    /// заметку: relink её не трогает (тот же смысл, что у резолвера — Resolve).
    /// Идёт под блокировкой затронутых разделов (решение 10) и с записью в журнал
    /// (op=relink, одна строка на раздел). Кандидаты отбираются чтением вне блокировки;
    /// файл под блокировкой читается заново — запись пакета, вклинившаяся между отбором
    /// и блокировкой, не потеряется и не будет затёрта. Страница в дрейфе не трогается:
    /// системная операция чужую правку не затирает — решает человек (adopt/возврат), как у
    /// любой записи. Возвращает число исправленных страниц.</summary>
    public async Task<int> RelinkAsync(string ownerId, string oldTitle, string? newTitle)
    {
        var root = WikiPaths.Root(_dataDir, ownerId);
        var pagesDir = Path.Combine(root, "pages");
        if (!Directory.Exists(pagesDir)) return 0;

        var candidates = new List<(string Section, string PageKey)>();
        try
        {
            foreach (var full in Directory.EnumerateFiles(pagesDir, "*.md", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(pagesDir, full).Replace('\\', '/');
                if (WikiPaths.IsProjection(rel)) continue; // каталог/журнал — не страница
                var section = WikiSections.Parse(rel);
                if (section is null) continue; // путь вне разделов — не страница вики
                string text;
                try { text = File.ReadAllText(full, Encoding.UTF8); } catch { continue; }
                // Отбор — с своим разделом страницы: qualified чужим разделом ссылка
                // называет страницу того раздела, а не переименовываемую заметку (M1).
                if (NotesService.HasWikiLinkTo(text, oldTitle, section)) candidates.Add((section, rel));
            }
        }
        catch (Exception) { return 0; } // каталог недоступен — нечего менять
        if (candidates.Count == 0) return 0;

        var sections = candidates.Select(c => c.Section).Distinct(StringComparer.Ordinal).ToList();
        return await WithSectionsLockedAsync(ownerId, sections, async () =>
        {
            var changed = 0;
            var touched = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var (section, pageKey) in candidates)
            {
                var full = SafePath.JoinBelowRoot(root, "pages/" + pageKey);
                string current;
                try { current = File.ReadAllText(full, Encoding.UTF8); } catch { continue; }
                var updated = newTitle is null
                    ? NotesService.UnlinkTo(current, oldTitle, section)
                    : NotesService.RewriteLinks(current, oldTitle, newTitle, section);
                if (updated == current) continue; // под блокировкой совпадений нет — нечего менять

                var manifest = WikiManifest.TryRead(root, section);
                if (manifest.State == WikiManifestState.Invalid) continue;
                var diskHash = Sha256OfBytes(File.ReadAllBytes(full));
                var recorded = manifest.Manifest.TryGetValue(pageKey, out var known) ? known : null;
                if (diskHash != recorded) continue; // дрейф: чужая правка на диске остаётся нетронутой

                WikiFileWriter.Write(root, "pages/" + pageKey, updated);
                // Хэш — байтов с диска, как у записи и adopt: следующая сверка сравнивает то,
                // что реально лежит в файле.
                WikiManifest.RecordApplied(root, section,
                    new Dictionary<string, string> { [pageKey] = WikiBaselines.Save(root, File.ReadAllBytes(full)) });
                if (!touched.TryGetValue(section, out var pages)) touched[section] = pages = [];
                pages.Add(pageKey);
                changed++;
            }
            if (changed == 0) return 0;

            foreach (var (section, pages) in touched)
            {
                // Строка журнала системной операции: policy null (меры политики нет — это не
                // знание), actor «keeper» — исполнитель операции (тот же, что у apply).
                WikiManifest.AppendLog(root, "relink", section, pages, null, "keeper");
                // Журнал изменился → _log.md раздела перегенерируется (тот же блок, что у adopt).
                WikiProjections.RegenerateSection(root, section);
            }
            // Граф обязан видеть исправленные ссылки сразу: после relink страница резолвится
            // по новому заголовку, старый остаётся призраком только если на него больше никто не ссылается.
            _notes.Invalidate(ownerId);
            return changed;
        });
    }

    /// <summary>Блокировки разделов владельца в каноническом порядке ключей (решение 10):
    /// порядок — по строке ключа (Ordinal), НЕ по порядку аргументов вызова: две встречные
    /// операции на одной паре разделов иначе держат A→B и B→A и встают в взаимной
    /// блокировке. Ключи повторяющиеся — одна блокировка. internal:Operations, задевающие
    /// два раздела (relink ссылки в чужой раздел, повышение — ADR-036), живут в этой же
    /// сборке и берут блокировки только отсюда.</summary>
    internal async Task<T> WithSectionsLockedAsync<T>(string ownerId, IReadOnlyList<string> sectionKeys, Func<Task<T>> body)
    {
        var ordered = sectionKeys.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var acquired = new List<SemaphoreSlim>(ordered.Count);
        try
        {
            foreach (var key in ordered)
            {
                var sem = _locks.GetOrAdd((ownerId, key), _ => new SemaphoreSlim(1));
                // Сигнал — до ожидания: «подошла к замку», а не «получила его». Порядок сигнала
                // и есть то, что встречный тест сверяет с каноническим порядком (решение 10).
                SectionLockRequestedHook?.Invoke(ownerId, key);
                await sem.WaitAsync().ConfigureAwait(false);
                acquired.Add(sem);
            }
            // Ленивый backfill основ (ADR-038 §6): под замком раздела, один раз на раздел.
            if (WikiPaths.IsInitialized(_dataDir, ownerId))
                foreach (var key in ordered)
                    WikiBaselines.BackfillOnce(WikiPaths.Root(_dataDir, ownerId), key);
            return await body().ConfigureAwait(false);
        }
        finally
        {
            // Отпускание в обратном порядке: порядок взятия от него не зависит, но симметрия
            // читается в коде и не даёт «почему именно так» вопросом на ревью.
            for (var i = acquired.Count - 1; i >= 0; i--) acquired[i].Release();
        }
    }

    /// <summary>Контракт пары (раздел, страница): ключ раздела обязан совпасть с разбором
    /// пути страницы. Иначе запись шла бы под блокировкой одного раздела, а файл относился
    /// к другому — два писателя одних файлов (тот риск, который решение 10 и снимает).
    /// Служебные файлы раздела (последний сегмент на «_»: _schema/_index/_log/_health) —
    /// не страницы: их заводит признак заведённости и проекции, запись по пути страницы
    /// молча перезаписала бы их (хвост ревью S2b-3, Н2).</summary>
    private static string? CheckPage(string sectionKey, string pageKey)
    {
        if (WikiPaths.IsProjection(pageKey))
            return $"«{pageKey}» — служебный файл раздела, не страница";
        if (WikiSections.Parse(pageKey) != sectionKey)
            return $"страница «{pageKey}» не принадлежит разделу «{sectionKey}»";
        return null;
    }

    private async Task<WikiOpResult> WriteUnderLockAsync(string ownerId, string sectionKey, string pageKey, string content)
    {
        var root = WikiPaths.Root(_dataDir, ownerId);
        var manifest = WikiManifest.TryRead(root, sectionKey);
        if (manifest.State == WikiManifestState.Invalid)
            // Invalid — НЕ пустая карта: «не применять» отличается от «не применено ничего»
            // (ревью S2b-1, M1). Причина — в результате: вызывающий пишет её в _health.md.
            return Refused($"манифест раздела «{sectionKey}» невалиден — запись невозможна");

        var full = SafePath.JoinBelowRoot(root, "pages/" + pageKey);
        var diskHash = File.Exists(full) ? Sha256OfBytes(File.ReadAllBytes(full)) : null;
        var recorded = manifest.Manifest.TryGetValue(pageKey, out var known) ? known : null;
        if (diskHash != recorded)
            // Дрейф (решение 2): файл на диске не равен последней применённой версии —
            // чужая правка, чужое удаление или файл, которого Хранитель не записывал.
            // Ни откатить молча, ни перезаписать: решает человек (adopt / возврат).
            return new WikiOpResult(WikiOpStatus.Drift, $"файл «{pageKey}» расходится с манифестом раздела");

        WikiFileWriter.Write(root, "pages/" + pageKey, content);
        // Хэш в манифест — с диска, а не из строки аргумента: следующая сверка сравнивает
        // то, что реально лежит в файле (BOM или чужая кодировка — уже не «то же самое»).
        WikiManifest.RecordApplied(root, sectionKey,
            new Dictionary<string, string> { [pageKey] = WikiBaselines.Save(root, File.ReadAllBytes(full)) });

        // Проекции раздела — шаг 6 той же транзакции (решение 4): после записи страницы, под
        // этой же блокировкой раздела (вторая блокировка того же ключа — зависание). В манифест
        // они не кладутся и дрейфом не считаются: сверяется страница, а не её каталог.
        WikiProjections.RegenerateSection(root, sectionKey);

        // Граф обязан видеть запись сразу: его кэш — модель per-owner с TTL 2 с (шаг 6
        // транзакции применения). Событие — только раздела проекта: у «Общего» и «Личного»
        // нет проекта-адресата (тот же порядок, что у личных заметок в NotesService).
        _notes.Invalidate(ownerId);
        AppendEvent(ownerId, sectionKey, pageKey, "keeper", $"Хранитель записал страницу вики «{pageKey}»");
        return new WikiOpResult(WikiOpStatus.Applied, null);
    }

    private async Task<WikiOpResult> AdoptUnderLockAsync(string ownerId, string sectionKey, string pageKey)
    {
        var root = WikiPaths.Root(_dataDir, ownerId);
        var full = SafePath.JoinBelowRoot(root, "pages/" + pageKey);
        if (!File.Exists(full))
            return Refused($"файла «{pageKey}» на диске нет — принимать нечего");
        var manifest = WikiManifest.TryRead(root, sectionKey);
        if (manifest.State == WikiManifestState.Invalid)
            return Refused($"манифест раздела «{sectionKey}» невалиден — запись невозможна");

        // Тот же хэш байтов диска, что сверяет запись: adopt ровно закрывает дрейф,
        // ничего не переписывая.
        WikiManifest.RecordApplied(root, sectionKey,
            new Dictionary<string, string> { [pageKey] = WikiBaselines.Save(root, File.ReadAllBytes(full)) });
        // policy null: у системной операции нет решения политики — есть только автор (человек).
        WikiManifest.AppendLog(root, "adopt", sectionKey, [pageKey], null, "user", null);
        // Журнал изменился → _log.md раздела перегенерируется (тот же блок, что у adopt).
        WikiProjections.RegenerateSection(root, sectionKey);
        // Кэш графа не трогается: содержимое файлов не изменилось, модель та же.
        AppendEvent(ownerId, sectionKey, pageKey, "user", $"Принята как есть страница вики «{pageKey}»");
        return new WikiOpResult(WikiOpStatus.Applied, null);
    }

    /// <summary>Запись в манифест раздела страницы, которую Хранитель написал своим
    /// внутренним инструментом (карточка источника — захват S6h): файл уже на диске, и его
    /// запись в манифесте обязательна — страница раздела вне манифеста считается дрейфом
    /// (сверка применения), и карточка без записи отбивала первое же применение после захвата
    /// (дефект d144bb0f). Хэш — байтов, прочитанных с диска (та же форма, что у записи и
    /// adopt). Вызывается изнутри WithSectionsLockedAsync: вызывающий держит замок раздела,
    /// вторая блокировка того же ключа дала бы зависание. Строки журнала нет: журнал —
    /// изменения знаний (словарь op — apply/adopt), карточка — данные захвата, не знание;
    /// автора «user», как у adopt, здесь и быть не может — это авто-операция Хранителя.</summary>
    internal void RegisterCapturedUnderLock(string ownerId, string sectionKey, string pageKey)
    {
        var root = WikiPaths.Root(_dataDir, ownerId);
        var full = SafePath.JoinBelowRoot(root, "pages/" + pageKey);
        WikiManifest.RecordApplied(root, sectionKey,
            new Dictionary<string, string> { [pageKey] = Sha256OfBytes(File.ReadAllBytes(full)) });
    }

    /// <summary>Служебный файл здоровья раздела (S3-3): пишется только Хранителем — как
    /// любой файл под pages/. Это не проекция журнала: его содержимое — отчёт линта,
    /// готовит его вызывающий; Хранитель даёт блокировку раздела и саму запись.</summary>
    public const string HealthFileName = "_health.md";

    /// <summary>Здоровье раздела и строка метрик (S3-3): под одной блокировкой раздела —
    /// health прогона и его метрики не разъезжаются, а дописывание metrics.jsonl атомарно
    /// и не рвёт строку (append, не перезапись файла — тот же приём, что у log.jsonl).
    /// Вызывается только внутренней службой линта вертикали. metricsLine null — пишется
    /// только health: у незавершённого прогона раздела метрик нет.</summary>
    internal async Task WriteSectionHealthAndMetricsAsync(string ownerId, string sectionKey, string healthContent, string? metricsLine)
    {
        await WithSectionsLockedAsync(ownerId, [sectionKey], async () =>
        {
            var root = WikiPaths.Root(_dataDir, ownerId);
            var folder = WikiSections.Folder(sectionKey)
                ?? throw new ArgumentException($"health пишется в раздел, а «{sectionKey}» — не ключ раздела", nameof(sectionKey));
            WikiFileWriter.Write(root, "pages/" + folder + HealthFileName, healthContent);
            if (metricsLine is not null)
            {
                var full = SafePath.Join(root, "keeper/metrics.jsonl");
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.AppendAllText(full, metricsLine + "\n", new UTF8Encoding(false));
            }
            return 0;
        });
    }

    private void AppendEvent(string ownerId, string sectionKey, string pageKey, string actor, string summary)
    {
        if (WikiSections.TryProjectId(sectionKey, out var projectId))
            _events?.Append(projectId, ownerId, ProjectEventTypes.WikiChanged, actor, summary);
    }

    private static WikiOpResult Refused(string reason) => new(WikiOpStatus.Refused, reason);

    // sha256 БАЙТОВ файла (не строки): форма та же, что у WikiManifest.Sha256Hex —
    // манифест, baseHashes пакета и сверка диска должны сравнивать одно и то же.
    private static string Sha256OfBytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

/// <summary>Исход операции Хранителя над страницей. Молчаливых исходов нет: любой исход
/// возвращается вызывающему (UI, _health.md, журнал).</summary>
public enum WikiOpStatus
{
    /// <summary>Файл записан, манифест обновлён.</summary>
    Applied,
    /// <summary>Диск расходится с манифестом: запись НЕ выполнена, файл на диске цел.
    /// Человек решает: adopt («Принять как есть») или возврат из прообразов.</summary>
    Drift,
    /// <summary>Операция невозможна (манифест невалиден, страница не своего раздела,
    /// файла нет). Причина — в Reason; ничего не применено и не тронуто.</summary>
    Refused,
}

/// <summary>Результат операции Хранителя: статус + причина для вызывающего.</summary>
public readonly record struct WikiOpResult(WikiOpStatus Status, string? Reason);

/// <summary>Одна страница, помеченная чисткой: путь относительно pages/, раздел и класс —
/// строка таблицы решения («ai» | «together» | «chronicle» | «source»): по классам предпросмотр
/// aggregate-ится для человека, по путям он решает.</summary>
public sealed record WikiPurgeItem(string PageKey, string Section, string Class);

/// <summary>Исход чистки ИИ-страниц: Ok + код отказа для вызывающего (J-P2) + состав
/// (пути и числа по разделам и классам) + путь архива (относительный корня вики — тот же
/// формат, что у путей страниц) + число гасимых пакетов и обнулённых курсоров. При dryRun
/// архива и эффектов нет: результат описывает только состав. Reasons — только там, где
/// что-то не доведено: молчаливых исходов нет.</summary>
public sealed record WikiPurgeResult(
    bool Ok,
    string? Code,
    string? Reason,
    bool DryRun,
    IReadOnlyList<WikiPurgeItem> Items,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> CountsBySection,
    string? ArchivePath,
    int StaledPackages,
    int TriageResetCount);

/// <summary>Внутренний шов побочных писателей NotesService к Хранителю (ADR-035, решение 2):
/// переименование или истечение заметки, на которую ссылаются страницы вики, — не правка
/// файла NotesService, а системная операция relink Хранителя (решение 6). internal: шов
/// вертикальный, наружу (REST/MCP) не выходит; реализация — WikiKeeper.</summary>
internal interface IWikiRelink
{
    /// <summary>newTitle — новый заголовок (переименование); null — цель истекла/удалена,
    /// ссылка разворачивается в текст. Возвращает число исправленных страниц.</summary>
    Task<int> RelinkAsync(string ownerId, string oldTitle, string? newTitle);
}
