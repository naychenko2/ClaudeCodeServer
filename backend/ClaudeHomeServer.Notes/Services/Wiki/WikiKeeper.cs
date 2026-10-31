using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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

    private readonly ConcurrentDictionary<(string Owner, string Section), SemaphoreSlim> _locks = new();
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

    public WikiKeeper(NotesService notes, IConfiguration config, IProjectEventLogService? events = null)
    {
        _notes = notes;
        _events = events;
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
                    new Dictionary<string, string> { [pageKey] = Sha256OfBytes(File.ReadAllBytes(full)) });
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
                await sem.WaitAsync().ConfigureAwait(false);
                acquired.Add(sem);
            }
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
            new Dictionary<string, string> { [pageKey] = Sha256OfBytes(File.ReadAllBytes(full)) });

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
            new Dictionary<string, string> { [pageKey] = Sha256OfBytes(File.ReadAllBytes(full)) });
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
