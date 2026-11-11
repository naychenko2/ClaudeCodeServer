using System.Globalization;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Services.Spheres;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Очередь заявок разбора (S6b-3, ADR-035 §12б этапы 5–8 и «Результат разбора»; ADR-036 §3).
// Одна заявка = один прогон двух проходов по выдержке источника (промпты S6b-1; место
// разбора задаёт заявка своим actionKey, по умолчанию wiki-ingest; ключ вне каталога
// LocalActionCatalog — заявка отказана на входе) → проверки кодом (S6b-2) → пакет (S4a) →
// применение неспорных (S4b). Модель
// ничего не выбирает: раздел даёт дом источника (код), класс и решение — валидатор и
// политика, цитаты и сноски — код.
//
// Порядок одной заявки:
// 1. Раздел назначения — WikiSourceHome по дому источника (ADR-036, решение 3). Личный
//    раздел в Ф1 отказан: Хранитель пишет в «Личное» с Ф2 (ADR-036, решение 1).
// 2. Карточка источника читается с диска: она уже захвачена (S6h) и лежит в разделе
//    назначения; её заголовок — цель сносок. Карточки нет или она не страница type: source —
//    отказ до обращения к модели (неожиданный вход — отказ с причиной, не исключение).
// 3. Проход A — one-shot без инструментов (ICheapTextRunner; место задаёт заявка,
//    по умолчанию wiki-ingest): утверждения с дословными цитатами.
// 4. Код уточняет кандидатов-страниц: страницы раздела назначения и цепочки вверх, чей
//    заголовок или алиас встречается в тексте утверждений (тот же порядок нормализации,
//    что у индекса ByName — WikiDedup.NormalizeName), не больше 5. Их текст — в промпт
//    прохода B («CURRENT CONTENT»), чтобы supersede/conflict называли цель дословно.
// 5. Проход B — план (хроника + операции). В промпт — _index только раздела назначения и
//    цепочки вверх: каталогов соседних проектов и личного в промпте не бывает (ADR-036,
//    решение 3; совпадения с соседями ищет код, не модель).
// 6. WikiPlanValidator; решение — по Rejected: это вина плана. При ней — одна попытка
//    ремонта с перечнем Rejected (третий вызов модели); после ремонта все операции пакета
//    — «inbox» (признак PackageAfterModelRepair в политике: низкое качество прохода человек
//    не пропускает). Второй отказ — заявка отказана, четвёртого прохода нет. Violations —
//    состояние раздела, а не вина плана (WikiPlanValidator.cs:60-64): ремонт из-за них не
//    вызывают (модель его не чинит), но их видит вызывающий — они называются в причине
//    исхода отдельно (ревью S6b-3, M2).
// 7. Исход «нечего добавить» — штатный (ADR-035 §12б): пакет не создаётся, исход
//    nothing_new. Пустой план (операций нет) — тот же исход: модель сказала, что писать
//    нечего, только маркер не поставила. Дыра WC-2 (WN-3): в пустом разделе (нет ни
//    одной страницы знаний) такой отказ — не решение «уже написано», а невидение цели:
//    модель не видит ни одной страницы и не знает, что для этого знания — create.
//    Тогда — ровно один повторный вызов прохода B с прямой причиной («в разделе нет
//    страниц знаний, для этого знания — create»); второй отказ МОДЕЛИ — прежний
//    nothing_new, цикла нет; упавший или пустой повтор — rejected с причиной сбоя
//    (тот же инвариант WN-4: сбой вызова не маскируется решением модели, иначе окно
//    чата уходит как «разобрано»). Ремонт по причине п.6 (отказ плана кодом) этого не
//    ловит: там план
//    есть и отвергнут, здесь плана нет и валидатор не звался.
// 8. Пакет — в WikiPackageStore (usage разбора — в операции пакета: токены того разбора,
//    который их породил). Содержание операции — подтверждённая валидатором цитата прохода A:
//    применение рендерит строку утверждения и сноску по op.Text и требует его дословности
//    в источнике (S4b) — пересказ в op.Text был бы отказан по цитате, уже подтверждённой
//    кодом (ревью S6b-3, M1). Status checking → proposed (вердикт проверок). Все операции
//    auto → proposed → accepted → WikiApplyService.ApplyAsync (состояние читается с диска).
//    Есть inbox → пакет остаётся в «proposed», исход awaiting_human: решает человек (S4a).
//
// Ответы модели — недоверенный ввод: нечитаемый JSON, утверждение без текста или цитаты,
// план без операций-объектов — отказ с причиной, не исключение и не «починим сами».
//
// Конкурентность:
// - две заявки одного источника (тот же origin+ref+version+target) дают один прогон: вторая
//   присоединяется к незавершённой задаче (in-flight) и получает её результат — двух
//   пакетов на одну версию источника и двойной оплаты модели нет; разные версии одного
//   окна — разные заявки: переписанный источник не должен получать чужой разбор (InFlightKey);
// - in-flight-ключ места разбора (actionKey, N7a) не содержит: присоединившаяся заявка
//   получает движок того, кто пришёл первым, — своей модели нет ни на уровне чата
//   (single-flight WikiChatIngestService), ни здесь;
// - заявки одного раздела стоят в очереди (semaphore по ключу раздела): вторая читает
//   состояние раздела после того, как первая завершила свой цикл, а не посреди её записи;
// - применение под теми же блокировками, что у остальной вики (внутри ApplyAsync);
// - необработанных исключений наружу нет: сбой внутри прогона — исход rejected с причиной;
// - сбой вне контракта ApplyAsync на авто-пути — тот же результат-отказ с кодом, и «applying»
//   закрывается переходом в «apply_failed», а не висит до сторожа старта (W3-1, как CrashedApply
//   пути решения человека).
public sealed class WikiIngestService
{
    /// <summary>Исходы заявки. applied — пакет применён; awaiting_human — пакет создан,
    /// в нём есть спорные операции (ждут человека, «Входящие»); nothing_new — штатный
    /// «нечего добавить»; rejected — отказ с причиной (вход, ответ модели, проверки).</summary>
    public const string OutcomeApplied = "applied";
    public const string OutcomeAwaitingHuman = "awaiting_human";
    public const string OutcomeNothingNew = "nothing_new";
    public const string OutcomeRejected = "rejected";

    /// <summary>Кандидатов-страниц, чей текст показывается модели в проходе B, не больше
    /// пяти: промпт не должен разрастись до половины раздела.</summary>
    public const int MaxCandidatePages = 5;

    /// <summary>Заявка: результат захвата источника (S6h) плюс его дом. Origin — из
    /// закрытого перечня (WikiGate.OriginValues); Ref — ссылка карточки («note:{id}»,
    /// «session:{id}#m{a}-{b}»); CardRelPath/SourceText — то, что вернул захват;
    /// Home — дом источника («personal» | «projects/{id}»); Target — null или "common"
    /// (явный выбор человека, ADR-036, решение 3).</summary>
    public sealed record Request(
        string Origin,
        string Ref,
        string CardRelPath,
        string SourceText,
        string? Home,
        string? Target);

    /// <summary>Исход заявки: Outcome + причина (для отказа и штатного nothing_new с
    /// причиной модели) + id созданного пакета (для applied и awaiting_human) + входные
    /// токены этого разбора (WC-4a): 0 — пока ни один вызов модели не ответил (отказ входа,
    /// карточки, раздела; отказ вызова, после которого ничего не отвечало); после первого
    /// ответившего вызова — сумма всех состоявшихся вызовов прогона независимо от исхода:
    /// отказанный план расход не отменяет (он понесён).</summary>
    public readonly record struct IngestResult(string Outcome, string? Reason, string? PackageId,
        long InputTokens = 0);

    private readonly ICheapTextRunner _llm;
    private readonly ISphereDirectory _spheres;
    private readonly WikiApplyService _apply;
    private readonly string _dataDir;
    private readonly TimeProvider _time;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _sectionGates = new();
    private readonly object _inFlightLock = new();
    private readonly Dictionary<string, Task<IngestResult>> _inFlight = new();

    public WikiIngestService(ICheapTextRunner llm, ISphereDirectory spheres, WikiApplyService apply,
        IConfiguration config, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _llm = llm;
        _spheres = spheres;
        _apply = apply;
        // Каталог данных — как у всех сервисов вертикали (тот же корень, что у Хранителя).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    /// <summary>Поставить заявку в очередь и дождаться её исхода. Две одновременные заявки
    /// одного источника — один прогон (вторая получает результат первой). Отказ любой
    /// фазы — IngestResult с причиной; исключения наружу нет.
    /// actionKey (N7a) — место разбора для вызова модели: null — прежний wiki-ingest,
    /// вызывающий (разбор чата ночью) подаёт своё место из LocalActionCatalog. Не-null ключ
    /// вне каталога — отказ на входе, до очереди и модели: опечатка в месте не должна
    /// молча переносить разбор в облако (LocalActionRouter у неведомого ключа берёт
    /// Tier/Medium).</summary>
    public Task<IngestResult> IngestAsync(string ownerId, Request? request, CancellationToken ct = default,
        string? actionKey = null)
    {
        // Fail-closed входа: кривая заявка не доходит ни до очереди, ни до модели.
        if (string.IsNullOrWhiteSpace(ownerId))
            return Task.FromResult(Rejected("владелец пустой"));
        if (request is null)
            return Task.FromResult(Rejected("заявка null"));
        if (request.Origin is not { Length: > 0 } origin || !WikiGate.AiOriginValues.Contains(origin))
            return Task.FromResult(Rejected($"origin «{request.Origin ?? "—"}» вне закрытого перечня — заявки нет"));
        if (request.Ref is not { Length: > 0 })
            return Task.FromResult(Rejected("заявка без ссылки источника"));
        if (request.CardRelPath is not { Length: > 0 })
            return Task.FromResult(Rejected("заявка без пути карточки — карточку обязан поставить захват (S6h)"));
        if (string.IsNullOrEmpty(request.SourceText))
            return Task.FromResult(Rejected("заявка без текста источника — цитату подтвердить не из чего"));
        if (actionKey is not null && LocalActionCatalog.Find(actionKey) is null)
            return Task.FromResult(Rejected(
                $"место разбора «{actionKey}» вне каталога LocalActionCatalog — заявки нет"));

        var key = InFlightKey(ownerId, request);
        Task<IngestResult> task;
        lock (_inFlightLock)
        {
            if (_inFlight.TryGetValue(key, out var running))
                return running;
            task = RunProtectedAsync(ownerId, request, ct, actionKey);
            _inFlight[key] = task;
        }
        // Снятие — после завершения: опоздавшая заявка, пришедшая в окно между концом
        // прогона и снятием, получает тот же (уже готовый) результат — второго прогона
        // той же версии источника это всё равно не рождает. Окно безопасно ровно потому,
        // что ключ различает версию (см. InFlightKey): без версии в него попадала заявка
        // ДРУГОЙ версии того же окна и получала чужой пакет.
        _ = task.ContinueWith(_ =>
        {
            lock (_inFlightLock)
            {
                if (_inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, task))
                    _inFlight.Remove(key);
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return task;
    }

    private const string SynthUnparsed = "синтез: ответ модели не разобран";

    /// <summary>Ключ заявки — версия источника, а не только его ссылка. Путь карточки —
    /// функция (ref, version): имя карточки строится из хэша версии (WikiSourceCapture.cs:309,
    /// CardName :454), поэтому он различает версии одного и того же окна. Без версии две
    /// заявки разных версий одного окна — одна: вторая получает чужой пакет, модель под неё не
    /// зовётся, хроника и курсор пишутся по чужому содержанию (дефект 603ae88a: под параллелью
    /// по этому пути падал C2 подмены записи N — окно между концом прогона и снятием записи
    /// пускало в завершённый прогон заявку переписанного окна).</summary>
    private static string InFlightKey(string ownerId, Request r) =>
        ownerId + "|" + r.Origin + "|" + r.Ref + "|" + r.CardRelPath + "|" + (r.Target ?? "");

    private async Task<IngestResult> RunProtectedAsync(string ownerId, Request request, CancellationToken ct,
        string? actionKey)
    {
        try
        {
            return await RunAsync(ownerId, request, ct, actionKey);
        }
        catch (OperationCanceledException)
        {
            return Rejected("разбор отменён");
        }
        catch (Exception e)
        {
            // Любой неожиданный сбой прогона — исход с причиной: ни исключения вызывающему,
            // ни «половины пакета» на диске без объяснения (урок ревью S4b-1, MAJOR 2).
            return Rejected($"внутренний сбой разбора: {e.Message}");
        }
    }

    private async Task<IngestResult> RunAsync(string ownerId, Request request, CancellationToken ct,
        string? actionKey)
    {
        // 1. Раздел назначения — дом источника (код, не модель).
        var decision = WikiSourceHome.Decide(request.Home, request.Target);
        if (decision.SectionKey is not { Length: > 0 } sectionKey)
            return Rejected(decision.Reason ?? "дом источника не задаёт раздел");
        if (sectionKey == WikiSections.Personal)
            return Rejected("личный раздел — с Ф2");

        if (!WikiPaths.IsInitialized(_dataDir, ownerId))
            return Rejected("вики этого владельца не заведена");
        var root = WikiPaths.Root(_dataDir, ownerId);

        // 2. Карточка источника — с диска: она поставлена захватом и лежит в разделе
        //    назначения; её заголовок — цель сносок принятых операций.
        // Текст карточки здесь не нужен: она войдёт в снимок раздела (ReadSectionState)
        // и через него — в проверки валидатора; от карточки берётся только заголовок.
        string cardTitle;
        DateTimeOffset? sourceWrittenAt = null;
        try
        {
            var cardFull = SafePath.JoinBelowRoot(root, "pages/" + request.CardRelPath);
            if (!File.Exists(cardFull))
                return Rejected("карточки источника нет на диске: сначала захват (S6h), потом разбор");
            var cardSection = WikiSections.Parse(request.CardRelPath);
            if (cardSection != sectionKey)
                return Rejected($"карточка источника в разделе «{cardSection ?? "—"}», а не в «{sectionKey}»");
            var parsed = WikiPage.Parse(File.ReadAllText(cardFull, Encoding.UTF8));
            if (parsed.Model is null || parsed.Model.Frontmatter.Type != WikiFrontmatter.TypeSource)
                return Rejected("карточка не читается как страница type: source");
            cardTitle = parsed.Model.Title;
            // Время написания источника — из его карточки (поле written, ставит захват): не captured,
            // момент фиксации у бэклога старых чатов — сегодня. Нет поля или не дата — неизвестно.
            if (DateTimeOffset.TryParse(parsed.Model.Frontmatter.Written, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var written))
                sourceWrittenAt = written;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
            or NotSupportedException)
        {
            return Rejected($"карточка источника не читается: {e.Message}");
        }

        var upChain = WikiSections.UpChain(sectionKey, ownerId, _spheres);

        // Очередь раздела: вторая заявка того же раздела читает состояние после того, как
        // первая завершила свой цикл (включая применение), а не посреди его записи.
        var gate = _sectionGates.GetOrAdd(ownerId + "|" + sectionKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            IReadOnlyList<WikiGate.Draft> sectionPages;
            try
            {
                sectionPages = ReadSectionState(root, sectionKey, upChain);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Rejected($"страницы раздела не читаются: {e.Message}");
            }

            var sectionIndex = BuildSectionIndex(root, sectionKey, upChain);
            var usage = new UsageAccumulator();

            // Отказ ПОСЛЕ ответившего вызова модели: понесённый расход остаётся в исходе
            // (WC-4a) — он был, даже если разбор его не окупил. Отказ до первого вызова —
            // прежний Rejected: там расхода нет (usage пуст и сам, но форма называет смысл).
            IngestResult RejectedUsed(string reason) => new(OutcomeRejected, reason, null, usage.InputTokens);

            // 3. Кандидаты-страницы: чей заголовок или алиас встречается в тексте окна
            //    (нормализация — та же, что у индекса ByName), не больше MaxCandidatePages.
            var candidates = PickCandidatePages(sectionPages, request.SourceText);
            string? existingPages = candidates.Count == 0
                ? null
                : string.Join("\n\n", candidates.Select(d => d.Content));

            // 4. Синтез одним вызовом по ПОЛНОМУ тексту окна (решение 10.11 №1): операции с
            //    утверждением и цитатами. В промпт — _index только раздела назначения и вверх.
            var promptS = WikiKeeperPrompts.Synth(
                request.SourceText, sectionIndex, WikiKeeperPrompts.NewNonce(), existingPages,
                WikiPage.MinQuoteLength, WikiPage.MaxQuoteLength);
            var answerS = await CallModelAsync(promptS, ownerId, "синтез", usage, ct, actionKey);
            if (answerS.Reason is not null)
                return RejectedUsed(answerS.Reason);
            var plan = ParseSynthPlan(answerS.Text!, out var nothingS, out var modelReasonS, out var parseErrorS);
            string? nothingReasonS = nothingS ? modelReasonS : "план без операций: писать нечего";

            // Дыра WC-2 (WN-3): отказ «нечего добавить» / пустой план в разделе без страниц
            // знаний — не решение «уже написано»: модель не видит ни одной цели и не знает,
            // что для этого знания — create. Ровно один повторный вызов с прямой причиной;
            // второй отказ модели — прежний nothing_new, цикла нет; упавший повтор —
            // rejected с причиной сбоя (см. ниже). Условия держат истинность причины:
            // «в разделе нет страниц знаний» — правда ровно когда в разделе назначения нет ни
            // одной страницы знаний (тот же предикат, что у PickCandidatePages: не карточка,
            // разбирается), а пустой раздел с кандидатами по цепочке вверх — отказ без
            // повтора: там цель у модели была. Буквальное «PickCandidatePages пуст» не взяли:
            // оно красит штатный nothing_new непустого раздела (страница есть, но заголовок
            // не встретился в тексте окна) и врёт модели про пустой раздел.
            // Folder() уже со слэшем на конце (WikiSections.cs:63-65).
            var targetFolder = WikiSections.Folder(sectionKey)!;
            var sectionHasKnowledgePages = sectionPages.Any(d =>
                d.Path.StartsWith(targetFolder, StringComparison.Ordinal) &&
                WikiPage.Parse(d.Content).Model is { } pm && pm.Frontmatter.Type != WikiFrontmatter.TypeSource);
            if ((nothingS || plan is { Operations: { Count: 0 } }) && candidates.Count == 0
                && !sectionHasKnowledgePages)
            {
                var emptySectionPrompt =
                    "THE TARGET SECTION HAS NO KNOWLEDGE PAGES (в разделе нет страниц знаний). "
                    + "For the knowledge in this window the operation is create: a new page in the "
                    + "target section. Produce a plan with a create operation, or answer the no-op "
                    + "outcome again.\n\n"
                    + promptS;
                var answerE = await CallModelAsync(emptySectionPrompt, ownerId, "пустой раздел", usage, ct, actionKey);
                if (answerE.Reason is not null)
                    // Сбой самого повторного вызова — не отказ модели: она могла не зваться ни
                    // разу (тот же инвариант WN-4, catch в CallModelAsync). Причина сбоя
                    // доезжает дословно, иначе «движок занят» читается вызывающим как решение
                    // «нечего добавить»: ночной путь трактует это как nothing_new, двигает
                    // lastOkTo и окно чата теряется при completed ночи (ревью WN-3, major).
                    return RejectedUsed(answerE.Reason);
                var planE = ParseSynthPlan(answerE.Text!, out var nothingE, out var modelReasonE, out _);
                if (nothingE)
                    // Второй отказ молчит без своей причины — остаётся первая: модель
                    // сказала это о том же окне, повтор её не опроверг.
                    return new IngestResult(OutcomeNothingNew, modelReasonE ?? nothingReasonS, null, usage.InputTokens);
                if (planE is not { Operations: { Count: > 0 } })
                    return new IngestResult(OutcomeNothingNew, nothingReasonS, null, usage.InputTokens);
                // Повтор дал план — он судится общим путём ниже (тот же валидатор и, при
                // отказе кода, обычный ремонт: это ремонт плана, а не повтор nothing_new
                // — цикл дыры WC-2 всё равно один).
                plan = planE;
            }
            else if (nothingS)
                return new IngestResult(OutcomeNothingNew, modelReasonS, null, usage.InputTokens);
            else if (plan is null)
                return RejectedUsed(parseErrorS ?? SynthUnparsed);
            else if (plan.Operations is { Count: 0 })
                return new IngestResult(OutcomeNothingNew, "план без операций: писать нечего", null, usage.InputTokens);

            // 5. Проверки кода. Решение — по Rejected: это вина плана. Violations — состояние
            // раздела (уже лежит на страницах): план от него не зависит, модель его не чинит —
            // лишний вызов за него не платят (ревью S6b-3, M2).
            var source = new WikiPlanValidator.Source(request.Origin, cardTitle, request.SourceText);
            // Настройки владельца доходят до политики в бою (мера 7, ADR-035 §12г): без этого
            // «выключить класс» и общий выключатель «Применять без ревью» в бою ничего не меняют.
            // Снимок один на заявку. Отсутствующий или битый settings.json — умолчания (их даёт
            // стор), и чтение настроек разбор не роняет: стор не бросает исключений наружу.
            var settings = WikiSettingsStore.Read(root).Settings;
            var policy = new WikiApplyPolicy.ApplyContext
            {
                AutoApplyEnabled = settings.AutoApplyEnabled,
                DisabledClasses = settings.DisabledClasses,
                // «личное в Общем» — решение о видимости принимает человек (ADR-036, решение 3).
                SourceHomeIsPersonal = request.Home == WikiSections.Personal,
                // Анти-петля (N7d): страницы, откатанные человеком за последние 7 дней.
                RecentlyRevertedPages = WikiJournalReader.ReadRecentHumanReverts(
                    root, _time.GetUtcNow().AddDays(-WikiApplyPolicy.RevertQuarantineDays)),
                // Правка человека (ADR-038) — отдельное от отката условие правила замены.
                LastHumanEditAt = WikiJournalReader.ReadLastHumanEdits(root),
                NewSourceTime = sourceWrittenAt,
            };
            // Инвариант: ни одна операция синтеза не попадает в пакет мимо ValidateSynth.
            var verdict = WikiPlanValidator.ValidateSynth(
                plan, source, sectionKey, upChain, sectionPages, policy);
            if (verdict.Rejected.Count > 0)
            {
                var issues = verdict.Rejected.Select(i => i.Reason).ToList();
                var repairPrompt =
                    "YOUR PREVIOUS PLAN WAS REJECTED BY THE CODE. Produce a corrected plan in the same "
                    + "format; fix every issue below (quotes must be verbatim, unused by another page, "
                    + "every number in a quote, at least one quote per operation), or answer the no-op "
                    + "outcome if the window leaves nothing worth writing.\nISSUES:\n"
                    + string.Join("\n", issues.Select(r => "- " + r)) + "\n\n"
                    + promptS;
                var answerR = await CallModelAsync(repairPrompt, ownerId, "ремонт плана", usage, ct, actionKey);
                if (answerR.Reason is not null)
                    return WithSectionIssues(RejectedUsed(answerR.Reason), verdict);
                var planR = ParseSynthPlan(answerR.Text!, out var nothingR, out var modelReasonR, out var parseErrorR);
                if (nothingR)
                    return WithSectionIssues(
                        new IngestResult(OutcomeNothingNew, modelReasonR, null, usage.InputTokens), verdict);
                if (planR is null)
                    return WithSectionIssues(RejectedUsed("ремонт не дал плана: " + parseErrorR), verdict);
                if (planR.Operations is { Count: 0 })
                    return WithSectionIssues(
                        new IngestResult(OutcomeNothingNew, "после ремонта план без операций", null,
                            usage.InputTokens), verdict);
                // Пакет после ремонта — весь в «Входящие»: признак низкого качества прохода
                // (WikiApplyPolicy.PackageAfterModelRepair), человек решает, код не применяет.
                verdict = WikiPlanValidator.ValidateSynth(
                    planR, source, sectionKey, upChain, sectionPages,
                    policy with { PackageAfterModelRepair = true });
                if (verdict.Rejected.Count > 0)
                    return WithSectionIssues(
                        RejectedUsed("план после одной попытки ремонта снова отклонён кодом: "
                            + FirstReason(verdict)), verdict);
            }

            if (verdict.Accepted.Count == 0)
            {
                // Все операции оказались отклонёнными сверх лимитов (Waiting) — пакета нет,
                // человек их не спрашивает (мера 3); исход — тот же штатный.
                var waiting = verdict.Waiting.FirstOrDefault();
                return WithSectionIssues(new IngestResult(OutcomeNothingNew,
                    waiting is null ? "ни одна операция не прошла проверки" : waiting.Reason, null,
                    usage.InputTokens), verdict);
            }

            // 6. Пакет: usage разбора — в каждую его операцию (токены того разбора,
            //    который их породил). Содержание операции — утверждение своими словами
            //    (Text = Claim ставит валидатор, ADR-039 реш. 2), доказательство — Quotes:
            //    подмены Text цитатой для синтеза нет. Статус: checking → proposed (вердикт
            //    проверок кода).
            var package = new WikiPackage
            {
                Id = WikiPackageStore.NewId(),
                Author = WikiPackage.AuthorAi,
                Section = sectionKey,
                Sources = [new WikiPackage.SourceRef(request.Origin, request.Ref)],
                Operations = verdict.Accepted
                    .Select(op => op with
                    {
                        InputTokens = ToIntOrMax(usage.InputTokens),
                        OutputTokens = ToIntOrMax(usage.OutputTokens),
                    })
                    .ToList(),
                Status = WikiPackage.StatusChecking,
            };
            var created = WikiPackageStore.Create(root, package);
            if (!created.Ok || created.Package is null)
                return RejectedUsed($"магазин пакетов не принял пакет: {created.Reason ?? created.Code}");

            var proposed = WikiPackageStore.Transition(root, created.Package.Id, WikiPackage.StatusProposed);
            if (!proposed.Ok)
                return RejectedUsed($"вердикт проверок не записан: {proposed.Reason ?? proposed.Code}");

            if (created.Package.Operations.Any(o => o.Decision != WikiPackage.DecisionAuto))
                return WithSectionIssues(new IngestResult(OutcomeAwaitingHuman,
                    "в пакете есть спорные операции: их решает человек («Входящие»)", created.Package.Id,
                    usage.InputTokens), verdict);

            var accepted = WikiPackageStore.Transition(root, created.Package.Id, WikiPackage.StatusAccepted);
            if (!accepted.Ok)
                return RejectedUsed($"пакет не переведён в «accepted»: {accepted.Reason ?? accepted.Code}");

            return WithSectionIssues(
                await ApplyAutoAsync(ownerId, root, created.Package,
                    new WikiApplyService.ApplyContext(cardTitle, request.SourceText), usage.InputTokens), verdict);
        }
        finally
        {
            gate.Release();
        }
    }

    // ── применение на авто-пути ───────────────────────────────────────────────────────

    /// <summary>Применить принятый пакет (все операции auto). Контракт — тот же, что у пути
    /// решения человека (AfterDecision/CrashedApply в WikiPackagesController): сбой вне
    /// контракта ApplyResult — исключение из WithSectionsLockedAsync или Root (единственные
    /// места вне собственных обхватов сервиса) — результат-отказ с причиной и кодом, а не
    /// исключение вызывающему; «applying» закрывается переходом там, где он ещё возможен
    /// (сбой до заявки пакета не трогает). internal — граница для теста: файловый сбой после
    /// заявки детерминированно в живой поток не поставить, поэтому обхват проверяется здесь
    /// формой отказа, а закрытие «applying» — в CrashedApply на диске с нужным статусом
    /// (тот же приём, что у ревью S4g, M3; W3-1). inputTokens (WC-4a) — входные токены
    /// разбора, который породил этот пакет: применение своего расхода не имеет, и в исходе
    /// лежит расход того прогона, который его создал (0 — вне прогона, напрямую).</summary>
    internal async Task<IngestResult> ApplyAutoAsync(string ownerId, string root, WikiPackage package,
        WikiApplyService.ApplyContext context, long inputTokens = 0)
    {
        WikiApplyService.ApplyResult applied;
        try
        {
            applied = await _apply.ApplyAsync(ownerId, package, context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Rejected(CrashedApply(root, package.Id, ex), inputTokens);
        }
        if (!applied.Applied)
            return Rejected($"применение пакета не удалось: {applied.Reason ?? applied.Code}", inputTokens);
        return new IngestResult(OutcomeApplied, null, package.Id, inputTokens);
    }

    /// <summary>Сбой применения вне контракта — та же форма, что у CrashedApply пути решения
    /// человека: причина называет внутренний сбой и его код («applying» закрывается
    /// «apply_failed» там, где он ещё возможен; другой статус на диске не трогать).
    /// У IngestResult поля кода нет — код в тексте причины (тот же приём, что у
    /// «applied.Reason ?? applied.Code»).</summary>
    internal static string CrashedApply(string root, string packageId, Exception ex)
    {
        var reason = $"применение пакета не удалось: внутренний сбой применения: {ex.Message} "
            + $"({WikiApplyService.CodeInternal})";
        var onDisk = WikiPackageStore.TryRead(root, packageId);
        if (onDisk.State == WikiPackageFileState.Valid && onDisk.Package!.Status == WikiPackage.StatusApplying)
        {
            var t = WikiPackageStore.Transition(root, packageId, WikiPackage.StatusApplyFailed, reason);
            if (!t.Ok)
                reason = $"{reason} (статус «apply_failed» не записан: {t.Reason ?? t.Code})";
        }
        return reason;
    }

    // ── состояние раздела и каталог ────────────────────────────────────────────────

    /// <summary>Снимок страниц раздела назначения и цепочки вверх: все .md их папок,
    /// служебные «_*» — не страницы (тот же предикат, что у Хранителя). Нечитаемый файл —
    /// отказ прогона: половины состояния раздела в проверку плана не пускаем.</summary>
    private static IReadOnlyList<WikiGate.Draft> ReadSectionState(string root, string sectionKey,
        IReadOnlyList<string> upChain)
    {
        var drafts = new List<WikiGate.Draft>();
        foreach (var key in new[] { sectionKey }.Concat(upChain))
        {
            var folder = WikiSections.Folder(key)!;
            var dir = SafePath.JoinBelowRoot(root, "pages/" + folder);
            if (!System.IO.Directory.Exists(dir))
                continue;
            foreach (var full in System.IO.Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories))
            {
                var pageKey = folder + Path.GetRelativePath(dir, full).Replace('\\', '/');
                if (WikiPaths.IsProjection(pageKey))
                    continue;
                drafts.Add(new WikiGate.Draft(pageKey, File.ReadAllText(full, Encoding.UTF8)));
            }
        }
        return drafts;
    }

    /// <summary>Каталог для промпта: текст _index только раздела назначения и цепочки
    /// вверх (ADR-036, решение 3): каталогов соседних проектов и личного в промпте нет.
    /// Каталога ещё нет, а папка раздела есть — проекции генерируются (они производные и
    /// идемпотентные); пустой папке вверх каталог не создаётся: пустого раздела на диске
    /// трогать нечем.</summary>
    private static string BuildSectionIndex(string root, string sectionKey, IReadOnlyList<string> upChain)
    {
        var sb = new StringBuilder();
        foreach (var key in new[] { sectionKey }.Concat(upChain))
        {
            var folder = WikiSections.Folder(key)!;
            var dir = SafePath.JoinBelowRoot(root, "pages/" + folder);
            if (!System.IO.Directory.Exists(dir))
                continue;
            var indexFull = SafePath.JoinBelowRoot(root, "pages/" + folder + WikiProjections.IndexFileName);
            if (!File.Exists(indexFull))
                WikiProjections.RegenerateSection(root, key);
            if (File.Exists(indexFull))
                sb.Append(File.ReadAllText(indexFull, Encoding.UTF8)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Кандидаты-страницы: существующие страницы раздела назначения и вверх, чей
    /// заголовок или алиас встречается в тексте окна (нормализация — та же, что у индекса
    /// резолва ByName: WikiDedup.NormalizeName). Карточки источников не кандидаты: они цель
    /// сносок, а не страницы знаний. Не больше MaxCandidatePages.</summary>
    private static List<WikiGate.Draft> PickCandidatePages(IReadOnlyList<WikiGate.Draft> sectionPages,
        string sourceText)
    {
        var wanted = WikiDedup.NormalizeName(sourceText);
        var picked = new List<WikiGate.Draft>();
        foreach (var draft in sectionPages)
        {
            var page = WikiPage.Parse(draft.Content).Model;
            if (page is null || page.Frontmatter.Type == WikiFrontmatter.TypeSource)
                continue;
            var names = new List<string> { WikiDedup.NormalizeName(page.Title) };
            names.AddRange(page.Frontmatter.Aliases.Select(WikiDedup.NormalizeName));
            if (names.Any(n => n.Length > 0 && wanted.Contains(n, StringComparison.Ordinal)))
            {
                picked.Add(draft);
                if (picked.Count >= MaxCandidatePages)
                    break;
            }
        }
        return picked;
    }

    // ── модель и разбор её ответов ─────────────────────────────────────────────────

    private readonly record struct ModelAnswer(string? Text, string? Reason);

    /// <summary>Один one-shot вызов места разбора (без инструментов; формат — «json»):
    /// actionKey — место заявки (N7a), null — прежний wiki-ingest; все вызовы передают его
    /// явно (default снят, чтобы вызов не мог остаться без места). Ключ проверен каталогом
    /// на входе IngestAsync. Сбой/пустой ответ — ModelAnswer с причиной: вызывающий
    /// отказывает заявку, а не ловит исключение.
    /// Токены вызова — в аккумулятор разбора (usage=null — нераспознанный
    /// формат метрик — переживаем: это разрешено контрактом шва).</summary>
    private async Task<ModelAnswer> CallModelAsync(string prompt, string ownerId, string passName,
        UsageAccumulator usage, CancellationToken ct, string? actionKey)
    {
        // Решение Андрея 02.11: ночной разбор не уходит в облако. Ночь идёт через цепочку без
        // финального claude-шага при локальном маршруте (RunLocalOrChosenDetailedAsync):
        // падение/недоступность локали посреди вызова — отказ вызова, а не уход в Tier/облако.
        // Расход этих вызовов берётся из usage того же ответа движка (WC-4c): метрик не будет
        // только если движок их не отдал — тогда плюс 0, как и раньше.
        // Кнопка «Разобрать» и агент wiki_ingest (actionKey не задан) — человеческие места,
        // облако там допустимо: прежний путь с учётом расхода.
        var noCloud = actionKey == LocalActionCatalog.WikiNightly;
        // Решение 6 ADR-039 (поправляет ADR-035 §12ж): ночь не уходит в облако ни на одном
        // шаге цепочки. Финальный claude-шаг гасит RunLocalOrChosenDetailedAsync, но шаг 1
        // цепочки зовёт выбранную админом облачную модель — поэтому облачный маршрут места
        // отказывает окно целиком, до вызова.
        if (noCloud && !_llm.UsesLocal(LocalActionCatalog.WikiNightly))
            return new ModelAnswer(null, $"{passName}: ночной разбор только локальной моделью");
        string? text;
        OneShotUsage? used = null;
        try
        {
            if (noCloud)
            {
                var viaLocal = await _llm.RunLocalOrChosenDetailedAsync(
                    LocalActionCatalog.WikiNightly, prompt, ownerId, jsonFormat: "json", ct: ct);
                text = viaLocal?.Text;
                used = viaLocal?.Usage;
            }
            else
            {
                var result = await _llm.RunDetailedAsync(
                    actionKey ?? LocalActionCatalog.WikiIngest, prompt,
                    fallbackModel: null, ownerId, jsonFormat: "json", ct: ct);
                text = result.Text;
                used = result.Usage;
            }
        }
        catch (LocalEngineSlotRefusalException e)
        {
            // Движок занят, слота не дождались: модель не звалась ни разу. Это НЕ «модель дала
            // пустой ответ» — та формулировка прятала сбой занятости за обычным исходом модели
            // (ночь 06.11: 108 отказов, ни одного вызова). Причина доезжает до журнала ночи
            // дословно — в rejected и firstRejectReason (WN-4).
            return new ModelAnswer(null, $"{passName}: {e.Message}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Ожидание слота прервано остановкой ночи / закрытием окна / остановкой службы:
            // это не отказ одного чата, это конец прохода — он уходит наружу отменой, как и
            // остальные точки прохода (тот же приём, что у цикла WikiNightlyService).
            throw;
        }
        catch (Exception e)
        {
            return new ModelAnswer(null, $"{passName}: модель не ответила: {e.Message}");
        }
        if (string.IsNullOrWhiteSpace(text))
            return new ModelAnswer(null, $"{passName}: модель дала пустой ответ");
        if (used is { } u)
        {
            usage.InputTokens += u.TotalInputTokens;
            usage.OutputTokens += u.OutputTokens;
        }
        return new ModelAnswer(text, null);
    }

    private sealed class UsageAccumulator
    {
        public long InputTokens;
        public long OutputTokens;
    }

    /// <summary>Первый сбалансированный JSON-объект ответа: модель иногда оборачивает
    /// контракт в прозу (урок контракта шва jsonFormat). Обёртки нет или объект не
    /// закрывается — false: вызывающий отказывает заявку.</summary>
    private static bool TryJsonObject(string raw, out JsonElement obj)
    {
        obj = default;
        var start = raw.IndexOf('{');
        if (start < 0)
            return false;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(raw[start..(i + 1)]);
                            obj = doc.RootElement.Clone();
                            return true;
                        }
                        catch (JsonException)
                        {
                            return false;
                        }
                    }
                    break;
            }
        }
        return false;
    }

    /// <summary>Штатный исход модели «нечего добавить» (WikiKeeperPrompts.NothingToAdd).</summary>
    private static bool TryNothingToAdd(string raw, out string? reason)
    {
        reason = null;
        if (!TryJsonObject(raw, out var obj))
            return false;
        if (!obj.TryGetProperty("outcome", out var outcome) ||
            outcome.ValueKind != JsonValueKind.String ||
            outcome.GetString() != WikiKeeperPrompts.NothingToAdd)
            return false;
        if (obj.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
            reason = r.GetString();
        return true;
    }

    /// <summary>План синтеза: operations — массив объектов op/kind/page/claim/quotes/target/
    /// as_of. Ответ модели НЕ десериализуется в операцию пакета напрямую (неизвестный ключ
    /// System.Text.Json пропускает молча — дата as_of пропала бы без ошибки): поля читаются
    /// по одному, `as_of` → SynthOp.AsOf. Отсутствующее или нераспознаваемое поле остаётся
    /// null, отсутствующие quotes — пустой список (операцию не выбрасываем молча: ValidateSynth
    /// откажет её «утверждение без цитат», причина видна в ремонте). Форму и вид судит
    /// WikiPlanValidator. nothing=true — маркер «нечего добавить».</summary>
    private static WikiPlanValidator.SynthPlan? ParseSynthPlan(string raw, out bool nothing,
        out string? nothingReason, out string? error)
    {
        nothing = false;
        nothingReason = null;
        error = null;
        if (!TryJsonObject(raw, out var obj))
        {
            error = SynthUnparsed + ": не читается как JSON (в том числе обрезан по лимиту вывода)";
            return null;
        }
        if (obj.TryGetProperty("outcome", out var outcome) &&
            outcome.ValueKind == JsonValueKind.String &&
            outcome.GetString() == WikiKeeperPrompts.NothingToAdd)
        {
            nothing = true;
            if (obj.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
                nothingReason = r.GetString();
            return null;
        }
        if (!obj.TryGetProperty("operations", out var operations))
            return new WikiPlanValidator.SynthPlan([]);
        if (operations.ValueKind != JsonValueKind.Array)
        {
            error = SynthUnparsed + ": operations не массив";
            return null;
        }
        var ops = new List<WikiPlanValidator.SynthOp>();
        foreach (var el in operations.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                error = SynthUnparsed + ": операция плана не объект";
                return null;
            }
            TryStringValue(el, "op", out var op);
            TryStringValue(el, "kind", out var kind);
            TryStringValue(el, "page", out var page);
            TryStringValue(el, "claim", out var claim);
            TryStringValue(el, "target", out var target);
            TryStringValue(el, "as_of", out var asOf);
            var quotes = new List<string>();
            if (el.TryGetProperty("quotes", out var q))
            {
                if (q.ValueKind != JsonValueKind.Array)
                {
                    error = SynthUnparsed + ": quotes операции не массив";
                    return null;
                }
                // Не строка в списке — пустая цитата: ValidateSynth откажет её с причиной.
                foreach (var item in q.EnumerateArray())
                    quotes.Add(item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "");
            }
            ops.Add(new WikiPlanValidator.SynthOp(op, kind, page, claim, quotes, target, asOf));
        }
        return new WikiPlanValidator.SynthPlan(ops);
    }

    private static bool TryStringValue(JsonElement el, string name, out string? value)
    {
        value = null;
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
            return false;
        value = v.GetString();
        return true;
    }

    // Причина отказа плана — вина плана (Rejected): нарушения состояния раздела к ней не
    // примешиваются, они называются отдельно (WithSectionIssues).
    private static string FirstReason(WikiPlanValidator.Result verdict) =>
        verdict.Rejected.FirstOrDefault() is { } rejected ? rejected.Reason
        : verdict.Violations.FirstOrDefault() ?? "без причины";

    /// <summary>Нарушения состояния раздела — не вина плана (WikiPlanValidator.cs:60-64):
    /// решение заявки от них не зависит, но их видит вызывающий — они доходят до него
    /// отдельной частью причины исхода (ревью S6b-3, M2).</summary>
    private static IngestResult WithSectionIssues(IngestResult r, WikiPlanValidator.Result verdict)
    {
        if (verdict.Violations.Count == 0)
            return r;
        var issues = "в разделе есть нарушения: " + string.Join("; ", verdict.Violations);
        return r with { Reason = r.Reason is { Length: > 0 } ? r.Reason + "; " + issues : issues };
    }

    private static int ToIntOrMax(long value) => value > int.MaxValue ? int.MaxValue : (int)value;

    private static IngestResult Rejected(string reason, long inputTokens = 0) =>
        new(OutcomeRejected, reason, null, inputTokens);
}
