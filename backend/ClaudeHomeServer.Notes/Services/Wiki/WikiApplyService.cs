using System.Security.Cryptography;
using ClaudeHomeServer.Services.Spheres;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// S4b-1 (ADR-035, решение 4 шаги 1–3, решение 10): проверки применения пакета до записи.
// На диск здесь ничего не пишется: файлы раздела остаются байт в байт — меняется только
// статус пакета, и только через WikiPackageStore.Transition под его блокировкой (единственная
// дверь в статусы): применить пакет дважды нельзя — первый вызов ведёт его в «applying»,
// второй отказан по состоянию на диске, а не по переданной карточке.
//
// Порядок:
// 1. Чтение пакета с диска под его id (TryRead) и заявка: accepted | partially_accepted →
//    applying через Transition (блок пакета): две параллельные заявки одного пакета дают
//    ровно один переход. Гейт и рендер судят по ДИСКОВОЙ копии (Status, Operations,
//    HumanDecision) — аргумент метода несёт только идентификатор (ревью S4b-1, MAJOR 1):
//    устаревшая карточка вызывающего не может обойти ни статус на диске («proposed» на
//    диске → отказ), ни решение человека по спорной операции.
// 2. Блокировка раздела через Хранителя (тот же замок, что у записи страницы; своего второго
//    блока того же ключа в потоке нет — иначе зависание) и под ней: модель мимо кэша
//    (BuildFresh, шаг 1 решения 4), сверка манифеста раздела с диском по sha256 БАЙТОВ
//    (дрейф — стоп) и baseHashes пакета с текущим состоянием.
// 3. Рендер операций в памяти общим рендером (WikiPlanValidator.RenderApply) и весь
//    WikiGate на итоговом состоянии; цепочка «вверх» — WikiSections.UpChain, не «видимые»
//    разделы (видимость шире: она для recall и UI, право на ссылку даёт только цепочка).
// Любой неожиданный вход — результат-отказ с причиной, не исключение наружу.
//
// baseHashes: несущий здесь смысл — не «хэш совпал», а «на что опиралось намерение»:
// страница-база исчезла или замещаемое утверждение ушло из неё → stale (авто-rebase нет,
// пакет требует нового разбора); страница менялась так, что намерение цело (чужой append) —
// проверка проходит. Дословная сверка хэшей ничего не решает одна: базовый текст в пакете
// не хранится, решает наличие цели, его проверяет рендер.
//
// Цитата в пакете не хранится (хвост ревью S4d): при применении Хранитель выводит её из
// текста источника заново — через QuoteMatcher, не из записи: утверждение может быть своей
// цитатой только если оно дословно в источнике (мера 1: без дословной цитаты не применяется
// ни при какой политике). Текст источника и заголовок карточки приносит вызывающий —
// тот, кто пакет собрал (захват источника — S6h).
public sealed class WikiApplyService
{
    // Коды отказов — контракт вызывающего (REST «Входящих», S7a), тот же стиль, что wiki_pkg_*.
    public const string CodeShape = "wiki_apply_shape";
    public const string CodeStatus = "wiki_apply_status";
    public const string CodeClaim = "wiki_apply_claim";
    public const string CodeOff = "wiki_apply_off";
    public const string CodeSource = "wiki_apply_source";
    public const string CodeManifest = "wiki_apply_manifest";
    public const string CodeDrift = "wiki_apply_drift";
    public const string CodeStale = "wiki_apply_stale";
    public const string CodeGate = "wiki_apply_gate";
    // Внутренний сбой проверки (ФС, чтение файла, неожиданный вход): пакет ведётся в
    // «apply_failed» с текстом сбоя — исключение наружу не выходит (ревью S4b-1, MAJOR 2).
    public const string CodeInternal = "wiki_apply_internal";

    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly ISphereDirectory _spheres;
    private readonly string _dataDir;

    public WikiApplyService(NotesService notes, WikiKeeper keeper, ISphereDirectory spheres, IConfiguration config)
    {
        _notes = notes;
        _keeper = keeper;
        _spheres = spheres;
        // Каталог данных — как у всех сервисов вертикали (тот же корень, что у Хранителя).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    /// <summary>Контекст применения: что вызывающий знает об источнике пакета — заголовок
    /// карточки (цель сносок рендера, она обязана быть в разделе или вверх по цепочке) и
    /// текст источника (по нему заново выводится цитата).</summary>
    public sealed record ApplyContext(string? SourceCard, string? SourceText);

    /// <summary>Итог проверок до записи. Ready — «готово к записи» (S4b-2): Rendered —
    /// изменённые рендером страницы раздела. Отказ — код и причина; для провала гейта —
    /// весь перечень нарушений. На диске изменён только статус пакета.</summary>
    public readonly record struct PrepareResult(
        bool Ready,
        string? Code,
        string? Reason,
        IReadOnlyList<string> Violations,
        IReadOnlyList<WikiGate.Draft> Rendered);

    /// <summary>Применить пакет по его идентификатору: состояние (Status, Operations,
    /// HumanDecision) читается с диска под id, карточка аргумента несёт только id
    /// (ревью S4b-1, MAJOR 1). В этом шаге (S4b-1) доходит до шага 3 решения 4: проверки
    /// и рендер в памяти; запись (прообразы, файлы, «applied») — S4b-2.</summary>
    public Task<PrepareResult> ApplyAsync(string ownerId, WikiPackage? package, ApplyContext? context = null) =>
        PrepareAsync(ownerId, package, context);

    internal async Task<PrepareResult> PrepareAsync(string ownerId, WikiPackage? package, ApplyContext? context)
    {
        // Fail-closed входа: null и пустой владелец — отказ с причиной, не NRE и не исключение
        // (уроки S4a/S6b-2). Форма пакета проверяется не по карточке аргумента: дальше
        // работает только дисковая копия, её форма — у TryRead (ревью S4b-1, MAJOR 1).
        if (package is null)
            return Fail(CodeShape, "пакет null");
        if (string.IsNullOrWhiteSpace(ownerId))
            return Fail(CodeShape, "владелец пустой");
        if (!WikiPaths.IsInitialized(_dataDir, ownerId))
            return Fail(CodeOff, "вики этого владельца не заведена");

        var root = WikiPaths.Root(_dataDir, ownerId);
        var read = WikiPackageStore.TryRead(root, package.Id);
        if (read.State == WikiPackageFileState.Missing)
            return Fail(CodeClaim, "пакета нет на диске: применять нечего");
        if (read.State == WikiPackageFileState.Invalid)
            return Fail(CodeShape, "файл пакета не читается как пакет v:1");
        var disk = read.Package!;

        // Статусный гейт — по дисковому состоянию, не по карточке вызывающего: «proposed»
        // на диске — отказ, решение человека по спорным операциям ещё не вынесено.
        var status = disk.Status;
        if (!WikiPackage.IsOpen(status))
            return Fail(CodeStatus, $"пакет в конечном статусе «{status}»: применить его дважды нельзя");
        if (status is not (WikiPackage.StatusAccepted or WikiPackage.StatusPartiallyAccepted))
            return Fail(CodeStatus, status == WikiPackage.StatusApplying
                ? "пакет уже в «applying»: его применяет другой вызов"
                : $"пакет в «{status}» не принят к применению");
        if (context?.SourceCard is not { Length: > 0 })
            return Fail(CodeSource, "карточка источника не задана: сноскам рендера нечем назвать цель");
        if (context.SourceText is not { Length: > 0 })
            return Fail(CodeSource, "текст источника не задан: цитату вывести не из чего");

        // Заявка пакета — единственная дверь в «applying»: Transition перечитывает состояние
        // под блоком id. Два параллельных вызова одного пакета: первый пишет «applying»,
        // второй под тем же блоком видит его и отказан — двойного перехода нет.
        var claim = WikiPackageStore.Transition(root, disk.Id, WikiPackage.StatusApplying);
        if (!claim.Ok)
            return Fail(CodeClaim, claim.Reason ?? "переход в «applying» не удался");

        // Блокировка раздела — та же, что у записи страницы (решение 10): применение не берёт
        // второй блокировки того же ключа и не пишет под чужой правкой посреди проверки.
        // Работаем с дисковой копией: её Operations и HumanDecision — те, что на диске.
        return await _keeper.WithSectionsLockedAsync(ownerId, [disk.Section],
            () => Task.FromResult(PrepareUnderLock(ownerId, root, disk, context!)));
    }

    // Тело проверки под блоком раздела обёрнуто: файловые чтения (манифест, байты страниц,
    // обход каталога) и сборка модели могут бросить — наружу исключение не уходит: пакет
    // ведётся в «apply_failed» с текстом сбоя, а не остаётся в «applying» навсегда
    // (ревью S4b-1, MAJOR 2).
    private PrepareResult PrepareUnderLock(string ownerId, string root, WikiPackage package, ApplyContext context)
    {
        try
        {
            return PrepareChecked(ownerId, root, package, context);
        }
        catch (Exception ex)
        {
            return FailApply(root, package.Id, CodeInternal,
                $"внутренний сбой проверки применения: {ex.Message}");
        }
    }

    private PrepareResult PrepareChecked(string ownerId, string root, WikiPackage package, ApplyContext context)
    {
        var section = package.Section;

        // Шаг 1 решения 4: модель строится мимо кэша графа — фактическое состояние диска,
        // а не кэш с TTL 2 с. Состояние проверки: страницы раздела и цепочки «вверх»
        // (UpChain — право на ссылку даёт только цепочка, не «видимые» разделы).
        var model = _notes.BuildFresh(ownerId);
        var upChain = WikiSections.UpChain(section, ownerId, _spheres);
        var allowed = new HashSet<string>(StringComparer.Ordinal) { section };
        allowed.UnionWith(upChain);

        var state = new List<WikiGate.Draft>();
        foreach (var n in model.Notes)
        {
            if (n.SourceKey != NotesService.WikiKey)
                continue;
            var sec = WikiSections.Parse(n.RelPath);
            if (sec is null || !allowed.Contains(sec))
                continue;
            state.Add(new WikiGate.Draft(n.RelPath, n.Content));
        }

        // Шаг 2: манифест раздела против диска, по sha256 байтов. Любое расхождение — дрейф:
        // стоп, ничего не применять (молчаливой перезаписи чужой правки нет, как у WritePage).
        var mres = WikiManifest.TryRead(root, section);
        if (mres.State == WikiManifestState.Invalid)
            return FailApply(root, package.Id, CodeManifest, $"манифест раздела «{section}» невалиден — применение невозможно");
        var manifest = mres.Manifest;

        var drift = CheckSectionAgainstDisk(root, section, manifest);
        if (drift is not null)
            return FailApply(root, package.Id, CodeDrift, drift);

        // baseHashes против текущего состояния: отсутствие страницы-базы — база намерения
        // исчезла (stale, авто-rebase нет). Изменённая, но целая база — не отказ: что именно
        // сломалось, решает рендер (ниже), а не сам факт смены хэша.
        foreach (var op in package.Operations)
            foreach (var (page, baseHash) in op.BaseHashes)
            {
                if (!manifest.TryGetValue(page, out var current))
                    return StaleResult(root, package.Id,
                        $"страница «{page}» — база операции «{op.Op}» — исчезла из раздела (база {baseHash} больше не несущая): пакет требует нового разбора");
                // Совпала — база цела; изменилась — продолжение проверки в рендере: намерение
                // живо, пока цела его цель (чужой append той же страницы — проверка проходит).
            }

        // Шаг 3: рендер операций в памяти и весь гейт на итоговом состоянии.
        var card = FindCard(state, context.SourceCard!);
        if (card is null)
            return FailApply(root, package.Id, CodeSource,
                $"карточка источника «{context.SourceCard}» не найдена в разделе или вверх по цепочке: сноскам рендера нечем подтвердить утверждения");
        var cardPath = card.Value.Path;

        var working = new List<WikiGate.Draft>(state);
        var renderedOrder = new List<string>(); // страницы раздела в порядке первой правки
        var unconfirmed = new List<string>();

        foreach (var op in package.Operations)
        {
            // «inbox» без решения человека или отклонённое человеком из рендера не берётся:
            // решение читается только из HumanDecision, не из тела пакета.
            var applies = op.Decision == WikiPackage.DecisionAuto ||
                (op.Decision == WikiPackage.DecisionInbox && op.HumanDecision == WikiPackage.HumanAccepted);
            if (!applies)
                continue;

            var existing = working.FirstOrDefault(d => d.Path == op.Page);
            if (op.Op != WikiPackage.OpCreate && existing.Path != op.Page)
                return StaleResult(root, package.Id,
                    $"страница «{op.Page}» не в состоянии раздела: база операции «{op.Op}» исчезла, пакет требует нового разбора");

            // Цитата заново выводится из текста источника: утверждение может быть своей
            // цитатой только если оно дословно в источнике (мера 1). Не выводится — операция
            // не применяется ни при какой политике; пакет отказан, на диске ничего.
            if (op.Text is not { Length: > 0 } || !QuoteMatcher.ContainsVerbatim(context.SourceText!, op.Text))
            {
                unconfirmed.Add($"операция «{op.Op}» на «{op.Page}»: цитата не выводится из текста источника дословно");
                continue;
            }

            WikiGate.Draft? draft;
            try
            {
                // Общий рендер плана (S6b-2): вторая копия рендера в проекте не заводится.
                draft = WikiPlanValidator.RenderApply(working, op, op.Text, context.SourceCard!);
            }
            catch (Exception)
            {
                // Внутренняя ошибка рендера — отказ с причиной, не исключение вызывающему.
                return FailApply(root, package.Id, CodeGate,
                    $"внутренняя ошибка рендера операции «{op.Op}» на «{op.Page}»");
            }
            if (draft is null)
                // supersede: замещаемого утверждения больше нет в состоянии страницы —
                // его уже заместило другое применение; авто-rebase нет (решение 4).
                return StaleResult(root, package.Id,
                    $"замещаемое утверждение «{op.Target}» больше не в странице «{op.Page}»: пакет требует нового разбора");

            working.RemoveAll(d => d.Path == op.Page);
            working.Add(draft.Value);
            if (!renderedOrder.Contains(op.Page))
                renderedOrder.Add(op.Page);
        }

        if (unconfirmed.Count > 0)
            return FailApply(root, package.Id, CodeSource, unconfirmed[0], unconfirmed);

        // Дословность — только для карточек этого пакета (GivenOnly, тот же смысл, что в
        // проверке плана S6b-2): принятые раньше чужие сноски не могут отвергнуть новую запись.
        var sourceTexts = new Dictionary<string, string> { [cardPath] = context.SourceText! };
        var violations = WikiGate.Check(working, section, upChain, sourceTexts, WikiGate.VerbatimScope.GivenOnly);
        if (violations.Count > 0)
            return FailApply(root, package.Id, CodeGate, $"итоговое состояние не проходит гейт: {violations[0]}", violations);

        var rendered = new List<WikiGate.Draft>(renderedOrder.Count);
        foreach (var path in renderedOrder)
            rendered.Add(working.First(d => d.Path == path));
        // Готово к записи (S4b-2): пакет остаётся в «applying» — его заявка держит
        // повторное применение до конца цикла.
        return new PrepareResult(true, null, null, [], rendered);
    }

    // Манифест против диска, по байтам: каждая запись манифеста — файл с этим хэшем; каждый
    // файл-страница раздела на диске — запись манифеста. Служебные «_*» — не страницы (тот же
    // контракт, что у Хранителя); нечитаемый файл — тоже дрейф: его байты не подтвердить.
    private static string? CheckSectionAgainstDisk(string root, string section, IReadOnlyDictionary<string, string> manifest)
    {
        foreach (var (page, hash) in manifest)
        {
            var full = SafePath.JoinBelowRoot(root, "pages/" + page);
            if (!File.Exists(full))
                return $"файл «{page}» из манифеста раздела отсутствует на диске";
            if (Sha256OfBytes(File.ReadAllBytes(full)) != hash)
                return $"файл «{page}» расходится с манифестом раздела (правка мимо Хранителя)";
        }

        var folder = WikiSections.Folder(section);
        if (folder is null)
            return $"«{section}» не ключ раздела вики";
        var dir = SafePath.JoinBelowRoot(root, "pages/" + folder);
        if (!Directory.Exists(dir))
            return null; // раздела на диске нет: норма до первой применённой операции (пустой манифест проверен выше).

        var known = new HashSet<string>(manifest.Keys, StringComparer.Ordinal);
        foreach (var full in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories))
        {
            var pageKey = folder + Path.GetRelativePath(dir, full).Replace('\\', '/');
            if (WikiPaths.IsProjection(pageKey))
                continue;
            if (!known.Contains(pageKey))
                return $"файл «{pageKey}» на диске, но не в манифесте раздела (записан не Хранителем)";
        }
        return null;
    }

    // Карточка источника — страница type: source с этим заголовком (та же нормализация,
    // чем мерит uniqueness гейт): цель сносок рендера обязана резолвиться в разделе или вверх.
    private static WikiGate.Draft? FindCard(List<WikiGate.Draft> state, string title)
    {
        var norm = WikiDedup.NormalizeName(title);
        if (norm.Length == 0)
            return null;
        foreach (var d in state)
        {
            var page = WikiPage.Parse(d.Content).Model;
            if (page is null || page.Frontmatter.Type != WikiFrontmatter.TypeSource)
                continue;
            if (WikiDedup.NormalizeName(page.Title) == norm)
                return d;
        }
        return null;
    }

    // Отказ применения: статус «apply_failed» — только через Transition (тот же порядок,
    // что у всех переходов); причина — текст для человека («Входящие», журнал).
    private static PrepareResult FailApply(string root, string id, string code, string reason, IReadOnlyList<string>? violations = null)
    {
        var transition = WikiPackageStore.Transition(root, id, WikiPackage.StatusApplyFailed, reason);
        // Минор 4 (ревью S4b-1): исход перехода — часть отказа. Если статус не сдвинулся,
        // вызывающий обязан видеть это в причине, а не читать молча «apply_failed».
        var resultReason = transition.Ok
            ? reason
            : $"{reason} (статус «apply_failed» не записан: {transition.Reason ?? transition.Code})";
        return new PrepareResult(false, code, resultReason, violations ?? [reason], []);
    }

    private static PrepareResult StaleResult(string root, string id, string reason)
    {
        WikiPackageStore.Transition(root, id, WikiPackage.StatusStale, reason);
        return new PrepareResult(false, CodeStale, reason, [reason], []);
    }

    private static PrepareResult Fail(string code, string reason) => new(false, code, reason, [reason], []);

    // sha256 БАЙТОВ — та же форма, что у манифеста и сверки Хранителя: сравнивать должны одно и то же.
    private static string Sha256OfBytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
