using System.Globalization;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Services.Spheres;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Очередь заявок разбора (S6b-3, ADR-035 §12б этапы 5–8 и «Результат разбора»; ADR-036 §3;
// ADR-039, решение 1: синтез одним вызовом — отдельной разметки и двух проходов нет).
// Одна заявка = один вызов синтеза по полному тексту выдержки источника (промпт S6b-1; место
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
// 3. Код уточняет кандидатов-страниц: страницы раздела назначения и цепочки вверх по
//    пересечению значимых слов выдержки с заголовком (×2), сводкой и алиасами; дословное
//    совпадение заголовка или алиаса — первой очереди, прежней нормализацией (Q1-S3,
//    WikiCandidates), не больше MaxCandidatePages. Их текст — в промпт синтеза
//    («CURRENT CONTENT»), чтобы supersede/conflict называли цель дословно.
// 4. Синтез — one-shot без инструментов (ICheapTextRunner; место задаёт заявка,
//    по умолчанию wiki-ingest): операции с утверждением и цитатами (ADR-039, решение 2).
//    В промпт — каталог только раздела назначения и цепочки вверх, построенный из того же
//    снимка страниц раздела, что и кандидаты (Q1-S2): каталогов соседних проектов и личного
//    в промпте не бывает (ADR-036, решение 3; совпадения с соседями ищет код, не модель).
//    Его символы — часть входа окна: они идут в исходе (CatalogChars) и складываются в
//    оценку расхода ночи (риск 1 разреза).
// 5. WikiPlanValidator.ValidateSynth; решение — по Rejected: это вина плана. При ней — одна
//    попытка ремонта с перечнем Rejected (следующий вызов модели); после ремонта все операции
//    пакета
//    — «inbox» (признак PackageAfterModelRepair в политике: низкое качество синтеза человек
//    не пропускает). Второй отказ — заявка отказана, второй попытки ремонта нет. Violations —
//    состояние раздела, а не вина плана (WikiPlanValidator.cs:60-64): ремонт из-за них не
//    вызывают (модель его не чинит), но их видит вызывающий — они называются в причине
//    исхода отдельно (ревью S6b-3, M2).
// 6. Исход «нечего добавить» — штатный (ADR-035 §12б): пакет не создаётся, исход
//    nothing_new. Пустой план (операций нет) — тот же исход: модель сказала, что писать
//    нечего, только маркер не поставила. Дыра WC-2 (WN-3): в пустом разделе (нет ни
//    одной страницы знаний) такой отказ — не решение «уже написано», а невидение цели:
//    модель не видит ни одной страницы и не знает, что для этого знания — create.
//    Тогда — ровно один повторный вызов синтеза с прямой причиной («в разделе нет
//    страниц знаний, для этого знания — create»); второй отказ МОДЕЛИ — прежний
//    nothing_new, цикла нет; упавший или пустой повтор — rejected с причиной сбоя
//    (тот же инвариант WN-4: сбой вызова не маскируется решением модели, иначе окно
//    чата уходит как «разобрано»). Ремонт по причине п.5 (отказ плана кодом) этого не
//    ловит: там план
//    есть и отвергнут, здесь плана нет и валидатор не звался.
// 7. Пакет — в WikiPackageStore (usage разбора — в операции пакета: токены того разбора,
//    который их породил). Содержание операции — утверждение своими словами модели,
//    доказательство — цитаты, подтверждённые кодом: Text = Claim ставит валидатор
//    (ADR-039, решение 2). Status checking → proposed (вердикт проверок). Все операции
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
// - in-flight-ключ места разбора (actionKey, N7a) и названного исполнителя (engine, W3) не
//   содержит: присоединившаяся заявка получает движок того, кто пришёл первым, — своей модели
//   нет ни на уровне чата (single-flight WikiChatIngestService), ни здесь;
// - заявки одного раздела стоят в очереди (semaphore по ключу раздела): вторая читает
//   состояние раздела после того, как первая завершила свой цикл, а не посреди её записи;
// - применение под теми же блокировками, что у остальной вики (внутри ApplyAsync);
// - после заявки с исходом awaiting_human (пакет со спорными операциями, «Входящие») в том же
//   обработчике очереди идёт проход облачного судьи (J-J4, точка «б»): спорное не обязано ждать
//   следующей ночи. Отдельного фона для этого нет — только тот же прогон заявки, уже после
//   освобождения очереди раздела (судья не держит раздел моделью). Замок прохода судьи (J-J3a)
//   сериализует этот вызов с вызовом ночного прохода (точка «а» в WikiNightlyService): двух
//   одновременных проходов одной вики и двух вызовов модели на одну операцию нет. Исход заявки
//   от этого не меняется: пакет остаётся awaiting_human (судья может решить его операции, а может
//   и не решить — решает человек); отмена вызова судьи не меняет исхода заявки и наружу
//   исключением не выходит;
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

    /// <summary>Кандидатов-страниц, чей текст показывается модели в промпте синтеза, не больше
    /// пяти: промпт не должен разрастись до половины раздела. Это внешний потолок: порядок
    /// кандидатов — top-6 в WikiCandidates, до промпта доходит не больше этого числа.</summary>
    public const int MaxCandidatePages = 5;

    /// <summary>Потолок каталога промпта, символов (Q1-S2, риск 1 разреза): полный каталог
    /// реального раздела (~130 строк со сводкой) съедает 8–9k токенов локального контекста,
    /// и окно дельты 35k токенов вместе с ним не помещается в NumCtx места. В пределах
    /// потолка к строке приложена сводка, за ним — только имя и вид; каталог человека
    /// (_index.md) потолка не знает — он полный.</summary>
    public const int PromptCatalogMaxChars = 14_000;

    /// <summary>Причина отказа окна, когда локальный маршрут места не дал ответа (дефект 2e13d45c,
    /// J-O1): «модель дала пустой ответ» лжёт, когда модель не звалась вовсе (движок не настроен,
    /// не готов или оборвался). Доезжает до журнала ночи через ChatResult.Reason и служит
    /// выключателю WikiNightlyService: три подряд таких отказа — локального маршрута нет,
    /// дальше по очереди будет то же самое. Имени движка в ней нет намеренно: к ночи может
    /// добавиться второй локальный исполнитель (J-W2).</summary>
    public const string ReasonLocalUnavailable = "локальная модель недоступна";

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
    /// отказанный план расход не отменяет (он понесён). Dropped/DropReasons — операции синтеза,
    /// отброшенные проверками кода при пакете из принятых (поштучный приём): число и первые
    /// MaxDropReasons причин; 0/null — отбрасывать было нечего. CatalogChars (Q1-S2, риск 1
    /// разреза) — символы каталога промпта, построенного этим прогоном: каталог — часть входа
    /// окна, ночь прибавляет его /4 к оценке расхода. 0 — прогон не дошёл до каталога (отказ
    /// входа, карточки) или раздел был пуст (пустой раздел каталога не даёт).</summary>
    public readonly record struct IngestResult(string Outcome, string? Reason, string? PackageId,
        long InputTokens = 0, int Dropped = 0, IReadOnlyList<string>? DropReasons = null,
        int Operations = 0, int CatalogChars = 0);

    /// <summary>Сколько причин отброшенных операций нести в исходе.</summary>
    public const int MaxDropReasons = 3;

    private readonly ICheapTextRunner _llm;
    private readonly ISphereDirectory _spheres;
    private readonly WikiApplyService _apply;
    // Точка (б) J-J4: проход судьи после заявки, вернувшей awaiting_human. Форма — та же, что у
    // точки (а) в WikiNightlyService: точка запуска проверяется без настоящей облачной модели.
    // null (тест без судьи) — судья не зовётся, поведение заявки прежнее.
    private readonly Func<string, string, CancellationToken, Task<WikiJudgeSweep.JudgeSweepResult>>? _judge;
    private readonly string _dataDir;
    private readonly TimeProvider _time;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _sectionGates = new();
    private readonly object _inFlightLock = new();
    private readonly Dictionary<string, Task<IngestResult>> _inFlight = new();

    public WikiIngestService(ICheapTextRunner llm, ISphereDirectory spheres, WikiApplyService apply,
        IConfiguration config, TimeProvider? time = null, WikiJudgeSweep? judge = null)
        : this(llm, spheres, apply, config, time, ToJudgeSweep(judge))
    {
    }

    // Шов теста: точка (б) подменяется — её вызывают без облачной модели (тот же приём, что у
    // точки (а) в WikiNightlyService). null — судья не зовётся, поведение заявки прежнее.
    internal WikiIngestService(ICheapTextRunner llm, ISphereDirectory spheres, WikiApplyService apply,
        IConfiguration config, TimeProvider? time,
        Func<string, string, CancellationToken, Task<WikiJudgeSweep.JudgeSweepResult>>? judge)
    {
        _time = time ?? TimeProvider.System;
        _llm = llm;
        _spheres = spheres;
        _apply = apply;
        _judge = judge;
        // Каталог данных — как у всех сервисов вертикали (тот же корень, что у Хранителя).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    private static Func<string, string, CancellationToken, Task<WikiJudgeSweep.JudgeSweepResult>>? ToJudgeSweep(
        WikiJudgeSweep? judge) =>
        judge is null ? null : (owner, root, ct) => judge.RunAsync(owner, root, ct);

    /// <summary>Поставить заявку в очередь и дождаться её исхода. Две одновременные заявки
    /// одного источника — один прогон (вторая получает результат первой). Отказ любой
    /// фазы — IngestResult с причиной; исключения наружу нет.
    /// actionKey (N7a) — место разбора для вызова модели: null — прежний wiki-ingest,
    /// вызывающий (разбор чата ночью) подаёт своё место из LocalActionCatalog. Не-null ключ
    /// вне каталога — отказ на входе, до очереди и модели: опечатка в месте не должна
    /// молча переносить разбор в облако (LocalActionRouter у неведомого ключа берёт
    /// Tier/Medium). engine (W3, И3 путь 2) — названный локальный исполнитель: «local» или имя
    /// из LocalEngines:*, его даёт только ночь. Не-null — модель зовётся ровно на этом движке
    /// (RunOnLocalEngineDetailedAsync: ни выбора исполнителя маршрутом, ни его страховки, ни
    /// облачного шага); его отказ — отказ окна с причиной маршрута, подмены другим исполнителем
    /// нет. Пустая или пробельная строка — тоже ветка исполнителя: пустое имя не резолвится
    /// ни в какой движок, окно отказано без вызова модели (W5b). null — прежний путь.
    /// В in-flight-ключ не входит (см. шапку).</summary>
    public Task<IngestResult> IngestAsync(string ownerId, Request? request, CancellationToken ct = default,
        string? actionKey = null, string? engine = null)
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
            task = RunProtectedAsync(ownerId, request, ct, actionKey, engine);
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
        string? actionKey, string? engine)
    {
        IngestResult res;
        try
        {
            res = await RunAsync(ownerId, request, ct, actionKey, engine);
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

        // Точка (б) J-J4: дневная заявка оставила спорные операции в «Входящих» — проход судьи
        // идёт здесь же, в этом же обработчике очереди, отдельного фона нет. Он после finally
        // RunAsync: очередь раздела уже отпущена, судья не держит раздел своей моделью и не
        // спорит с её замком; вызов сериализует замок самого прохода судьи (J-J3a) с его точкой
        // (а) в ночи. Пакет для судья — свой, только что записанный: корень тот же, что выбрал
        // RunAsync. Ночь — исключение (доработка финала J-J4, пункт 1): её окна идут через этот
        // же метод, но судья у неё один — полный проход в конце RunPassAsync (точка (а)
        // WikiNightlyService). Посреди ночи тот проход выбрасывал свой итог (в нём и цены:
        // сводка, строка журнала и уведомление ночи читали бы почти ноль), финальная точка (а)
        // видела бы уже решённое им, и шёл он без ct ночи — остановка ночи его не прерывала.
        if (_judge is not null && res.Outcome == OutcomeAwaitingHuman && res.PackageId is { Length: > 0 }
            && actionKey != LocalActionCatalog.WikiNightly)
        {
            try
            {
                await _judge(ownerId, WikiPaths.Root(_dataDir, ownerId), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Остановка вызывающего (закрытое окно, остановка службы): судья не начинался —
                // исход заявки не меняется, пакета это не касается.
            }
            // Прочего сам проход судьи не бросает: он называет свой сбой в FirstFailure (J-J3a).
        }
        return res;
    }

    private async Task<IngestResult> RunAsync(string ownerId, Request request, CancellationToken ct,
        string? actionKey, string? engine)
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

            // Каталог промпта строится из того же снимка страниц раздела, что и кандидаты:
            // второй точки правды о состоянии раздела в окне не появляется (Q1-S2).
            var sectionIndex = BuildSectionIndex(root, sectionKey, upChain, sectionPages);
            var usage = new UsageAccumulator();

            // Исход прогона после того, как каталог промпта построен: каталог — часть входа
            // окна и входит в каждый исход этого прогона, включая отказ после модели (он
            // понесён). Ночь прибавляет его /4 к оценке расхода (риск 1 разреза).
            IngestResult Res(string outcome, string? reason, string? packageId = null,
                long tokens = 0, int operations = 0) =>
                new(outcome, reason, packageId, tokens, 0, null, operations, sectionIndex.Length);

            // Отказ ПОСЛЕ ответившего вызова модели: понесённый расход остаётся в исходе
            // (WC-4a) — он был, даже если разбор его не окупил. Отказ до первого вызова —
            // прежний Rejected: там расхода нет (usage пуст и сам, но форма называет смысл).
            IngestResult RejectedUsed(string reason) => Res(OutcomeRejected, reason, null, usage.InputTokens);

            // 3. Кандидаты-страницы: пересечение значимых слов окна с заголовком, сводкой
            //    и алиасами; дословное совпадение заголовка — первой очереди, как раньше
            //    (WikiCandidates, Q1-S3). Тот же снимок sectionPages, что у каталога.
            //    Не больше MaxCandidatePages.
            var candidates = PickCandidatePages(sectionPages, request.SourceText);
            string? existingPages = candidates.Count == 0
                ? null
                : string.Join("\n\n", candidates.Select(d => d.Content));

            // 4. Синтез одним вызовом по ПОЛНОМУ тексту окна (решение 10.11 №1): операции с
            //    утверждением и цитатами. В промпт — _index только раздела назначения и вверх.
            var promptS = WikiKeeperPrompts.Synth(
                request.SourceText, sectionIndex, WikiKeeperPrompts.NewNonce(), existingPages,
                WikiPage.MinQuoteLength, WikiPage.MaxQuoteLength);
            var answerS = await CallModelAsync(promptS, ownerId, "синтез", usage, ct, actionKey, engine);
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
                var answerE = await CallModelAsync(
                    emptySectionPrompt, ownerId, "пустой раздел", usage, ct, actionKey, engine);
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
                    return Res(OutcomeNothingNew, modelReasonE ?? nothingReasonS, tokens: usage.InputTokens);
                if (planE is not { Operations: { Count: > 0 } })
                    return Res(OutcomeNothingNew, nothingReasonS, tokens: usage.InputTokens);
                // Повтор дал план — он судится общим путём ниже (тот же валидатор и, при
                // отказе кода, обычный ремонт: это ремонт плана, а не повтор nothing_new
                // — цикл дыры WC-2 всё равно один).
                plan = planE;
            }
            else if (nothingS)
                return Res(OutcomeNothingNew, modelReasonS, tokens: usage.InputTokens);
            else if (plan is null)
                return RejectedUsed(parseErrorS ?? SynthUnparsed);
            else if (plan.Operations is { Count: 0 })
                return Res(OutcomeNothingNew, "план без операций: писать нечего", tokens: usage.InputTokens);

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
                // Флаг бэклога (разрез 2026-11-12, J-P1c): после чистки старой вики разбор
                // идёт с нуля и лимит новых страниц на раздел поднят до конца флага. Сегодня —
                // по тем же часам, что уже берёт сервис (_time): политика не заводит свой clock.
                IsBacklogPass = WikiBacklogFlag.IsActive(
                    root, DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime)),
            };
            // Инвариант: ни одна операция синтеза не попадает в пакет мимо ValidateSynth.
            var verdict = WikiPlanValidator.ValidateSynth(
                WithCodeAsOf(plan, sourceWrittenAt), source, sectionKey, upChain, sectionPages, policy);
            // Поштучный приём: пакет собирается из Accepted одного вызова, Rejected отбрасываются.
            // Ремонт — только когда не принялось ничего, а отказы есть.
            if (verdict.Accepted.Count == 0 && verdict.Rejected.Count > 0)
            {
                var issues = verdict.Rejected.Select(i => i.Reason).ToList();
                var repairPrompt =
                    "YOUR PREVIOUS PLAN WAS REJECTED BY THE CODE. Produce a corrected plan in the same "
                    + "format; fix every issue below (quotes must be verbatim, unused by another page, "
                    + "every number in a quote, at least one quote per operation), or answer the no-op "
                    + "outcome if the window leaves nothing worth writing.\nISSUES:\n"
                    + string.Join("\n", issues.Select(r => "- " + r)) + "\n\n"
                    + promptS;
                var answerR = await CallModelAsync(repairPrompt, ownerId, "ремонт плана", usage, ct, actionKey, engine);
                if (answerR.Reason is not null)
                    return WithSectionIssues(RejectedUsed(answerR.Reason), verdict);
                var planR = ParseSynthPlan(answerR.Text!, out var nothingR, out var modelReasonR, out var parseErrorR);
                if (nothingR)
                    return WithSectionIssues(Res(OutcomeNothingNew, modelReasonR, tokens: usage.InputTokens), verdict);
                if (planR is null)
                    return WithSectionIssues(RejectedUsed("ремонт не дал плана: " + parseErrorR), verdict);
                if (planR.Operations is { Count: 0 })
                    return WithSectionIssues(
                        Res(OutcomeNothingNew, "после ремонта план без операций", tokens: usage.InputTokens), verdict);
                // Пакет после ремонта — весь в «Входящие»: признак низкого качества прохода
                // (WikiApplyPolicy.PackageAfterModelRepair), человек решает, код не применяет.
                verdict = WikiPlanValidator.ValidateSynth(
                    WithCodeAsOf(planR, sourceWrittenAt), source, sectionKey, upChain, sectionPages,
                    policy with { PackageAfterModelRepair = true });
                if (verdict.Accepted.Count == 0 && verdict.Rejected.Count > 0)
                    return WithSectionIssues(
                        RejectedUsed("план после одной попытки ремонта снова отклонён кодом: "
                            + FirstReason(verdict)), verdict);
            }

            if (verdict.Accepted.Count == 0)
            {
                // Все операции оказались отклонёнными сверх лимитов (Waiting) — пакета нет,
                // человек их не спрашивает (мера 3); исход — тот же штатный.
                var waiting = verdict.Waiting.FirstOrDefault();
                return WithSectionIssues(Res(OutcomeNothingNew,
                    waiting is null ? "ни одна операция не прошла проверки" : waiting.Reason,
                    tokens: usage.InputTokens), verdict);
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
            // Сбой хранилища пакета — не отмена разбора: операции, отброшенные поштучным
            // приёмом, были отброшены независимо от того, дожил ли пакет. Эти отказы идут
            // через WithSectionIssues, как остальные исходы после вердикта (минор ревью C2R-Sh1).
            var created = WikiPackageStore.Create(root, package);
            if (!created.Ok || created.Package is null)
                return WithSectionIssues(
                    RejectedUsed($"магазин пакетов не принял пакет: {created.Reason ?? created.Code}"), verdict);

            var proposed = WikiPackageStore.Transition(root, created.Package.Id, WikiPackage.StatusProposed);
            if (!proposed.Ok)
                return WithSectionIssues(
                    RejectedUsed($"вердикт проверок не записан: {proposed.Reason ?? proposed.Code}"), verdict);

            if (created.Package.Operations.Any(o => o.Decision != WikiPackage.DecisionAuto))
                return WithSectionIssues(Res(OutcomeAwaitingHuman,
                    "в пакете есть спорные операции: их решает человек («Входящие»)", created.Package.Id,
                    usage.InputTokens,
                    operations: created.Package.Operations.Count(o => o.Decision == WikiPackage.DecisionInbox)), verdict);

            var accepted = WikiPackageStore.Transition(root, created.Package.Id, WikiPackage.StatusAccepted);
            if (!accepted.Ok)
                return WithSectionIssues(
                    RejectedUsed($"пакет не переведён в «accepted»: {accepted.Reason ?? accepted.Code}"), verdict);

            // Исход пути применения строит ApplyAutoAsync без контекста прогона: символы
            // каталога дописывает здесь тот, кто прогон ведёт — этот прогон их построил.
            var appliedRes = await ApplyAutoAsync(ownerId, root, created.Package,
                new WikiApplyService.ApplyContext(cardTitle, request.SourceText), usage.InputTokens);
            return WithSectionIssues(appliedRes with { CatalogChars = sectionIndex.Length }, verdict);
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
    /// отказ прогона: половины состояния раздела в проверку плана не пускаем.
    /// internal — тот же снимок нужен записи хроники (WikiChatIngestService.
    /// AppendChronicleAsync, Q1-S4b): второй читатель состояния раздела не заводим.</summary>
    internal static IReadOnlyList<WikiGate.Draft> ReadSectionState(string root, string sectionKey,
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

    /// <summary>Каталог промпта (Q1-S2): строится из того же снимка страниц раздела, что и
    /// кандидаты, — перечитыванием _index.md или самих файлов состояния больше не занимается:
    /// второй точки правды о состоянии раздела в окне нет (критерий c3). Только раздел
    /// назначения и цепочка вверх (ADR-036, решение 3): каталогов соседних проектов и личного
    /// в промпте не бывает. Карточек source в каталоге промпта нет: они цель сносок, а не
    /// страницы знаний; неразобранные страницы тоже — модель не назовёт то, что код не читает.
    /// Порядок внутри раздела — по времени создания файла, новые в конце блока раздела:
    /// позиция страницы не меняется от правки её содержания, а новая страница дописывается
    /// в конец своего блока и префикс промпта с уже известными страницами не сдвигает.
    /// _index.md человека при этом
    /// остаётся полным и по пути (WikiProjections.WriteIndex). Потолок — PromptCatalogMaxChars:
    /// сначала строится голый скелет (имя и вид) — он гарантирует присутствие новых страниц;
    /// освободившийся бюджет тратится на сводки старых строк порядка, хвост остаётся
    /// «[[Заголовок]] — вид». Скелет, который не вмещается сам, теряет старые страницы
    /// первыми: текущие для разбора должны остаться (полный каталог видит человек).
    /// Пустой раздел каталога не даёт вовсе: ни заголовка, ни строк — пустой раздел был
    /// в промпте без описания и раньше.</summary>
    internal static string BuildSectionIndex(string root, string sectionKey, IReadOnlyList<string> upChain,
        IReadOnlyList<WikiGate.Draft> sectionPages)
    {
        // Потолок держится двумя проходами ради одного: новые страницы остаются в конце и
        // в каталоге всегда, а весь каталог не перерастает потолок. Сначала — голый скелет
        // (имя и вид) всех непустых разделов; на него смотрит потолок. Освободившийся
        // бюджет тратится на сводки старых строк порядка; дальше первой невместившейся
        // сводки строки остаются голыми (риск 1 разреза: полный каталог реального раздела
        // съедал 8–9k токенов, и окно не помещалось в контекст места).
        var blocks = new List<(string Header, List<string> Lines)>();
        var used = 0;
        foreach (var key in new[] { sectionKey }.Concat(upChain))
        {
            var folder = WikiSections.Folder(key)!;
            var entries = new List<WikiGate.Draft>();
            foreach (var d in sectionPages)
            {
                if (!d.Path.StartsWith(folder, StringComparison.Ordinal))
                    continue;
                var fm = WikiFrontmatter.Parse(d.Content);
                if (fm.Model is null || fm.Model.Type == WikiFrontmatter.TypeSource)
                    continue;
                entries.Add(d);
            }
            if (entries.Count == 0)
                continue;
            entries.Sort((a, b) =>
            {
                var ca = CreatedUtc(root, a.Path);
                var cb = CreatedUtc(root, b.Path);
                return ca != cb ? ca.CompareTo(cb) : string.CompareOrdinal(a.Path, b.Path);
            });

            var header = $"# Каталог раздела «{key}»\n";
            if (used + header.Length > PromptCatalogMaxChars)
                break; // даже заголовок блока не вмещается — дальше не соберётся
            var fulls = entries
                .Select(d => WikiProjections.CatalogLine(d.Path, d.Content))
                .ToList();
            var bares = entries
                .Select(d => WikiProjections.CatalogLine(d.Path, d.Content, withSummary: false))
                .ToList();
            var bareTotal = header.Length + bares.Sum(b => b.Length + 1);

            // Даже голый скелет этого раздела не вмещается: старые страницы выпадают
            // первыми — новые, текущие для разбора, должны остаться.
            while (bares.Count > 0 && used + bareTotal > PromptCatalogMaxChars)
            {
                bareTotal -= bares[0].Length + 1;
                fulls.RemoveAt(0);
                bares.RemoveAt(0);
            }
            if (bares.Count == 0)
                break; // и с одним голым рядом раздела не хватает — дальше не соберётся

            var budget = PromptCatalogMaxChars - used - bareTotal;
            var spent = 0;
            var lines = new List<string>(bares.Count);
            var swodok = true;
            for (var i = 0; i < bares.Count; i++)
            {
                if (swodok && spent + fulls[i].Length - bares[i].Length <= budget)
                {
                    spent += fulls[i].Length - bares[i].Length;
                    lines.Add(fulls[i]);
                }
                else
                {
                    // Сводки дальше по порядку уже не в бюджет: хвост блока — голые строки,
                    // новая страница дописывается в его конец и префикс промпта не сдвигает.
                    swodok = false;
                    lines.Add(bares[i]);
                }
            }
            used += bareTotal + spent; // приписанные сводки — тоже длина каталога: следующий
            // раздел не должен получить бюджет, который уже занят сводками этого
            blocks.Add((header, lines));
        }

        var sb = new StringBuilder();
        foreach (var (header, lines) in blocks)
        {
            sb.Append(header);
            foreach (var line in lines)
                sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    // «Created» страницы для порядка каталога — время рождения файла: оно не меняется при
    // перечисании страницы, поэтому позиция страницы в каталоге постоянна. Если файловая
    // система birth time не отдаёт (GetCreationTime ставит UnixEpoch) — берётся время
    // последней записи: для только что созданной страницы они совпадают. Время снимается
    // только с файлов из снимка: каталог не смотрит на остальной диск и не перечитывает
    // его содержимое.
    private static DateTimeOffset CreatedUtc(string root, string pageRel)
    {
        try
        {
            var full = SafePath.JoinBelowRoot(root, "pages/" + pageRel);
            var created = File.GetCreationTimeUtc(full);
            return created > DateTimeOffset.UnixEpoch ? created : File.GetLastWriteTimeUtc(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Файла из снимка уже нет (его удалили под этим же шлюзом раздела): порядок по пути,
            // содержание страницы уже лежит в снимке — каталог о ней не молчит.
            return DateTimeOffset.UnixEpoch;
        }
    }

    /// <summary>Кандидаты-страницы: существующие страницы раздела назначения и вверх по
    /// пересечению значимых слов окна с заголовком (вес ×2), сводкой (та же проекция, что
    /// у строки каталога) и алиасами; дословное совпадение заголовка или алиаса — первой
    /// очереди, прежней нормализацией (WikiDedup.NormalizeName). Порядок и потолок TopN —
    /// в WikiCandidates; MaxCandidatePages остаётся внешним потолком промпта. Карточки
    /// источников не кандидаты: они цель сносок, а не страницы знаний (предикат тот же).
    /// Вход — тот же снимок sectionPages, что у каталога промпта: второй точки правды о
    /// состоянии раздела в окне нет.</summary>
    private static List<WikiGate.Draft> PickCandidatePages(IReadOnlyList<WikiGate.Draft> sectionPages,
        string sourceText)
    {
        var picked = WikiCandidates.Pick(sectionPages, sourceText, WikiCandidates.TopN);
        // TopN упорядочивает кандидатов; потолок промпта — внешний и остаётся за этим
        // сервисом: «не больше пяти», промпт не должен разрастись до половины раздела.
        return picked.Count > MaxCandidatePages ? picked[..MaxCandidatePages] : picked;
    }

    // ── модель и разбор её ответов ─────────────────────────────────────────────────

    private readonly record struct ModelAnswer(string? Text, string? Reason);

    /// <summary>Один one-shot вызов места разбора (без инструментов; формат — «json»):
    /// actionKey — место заявки (N7a), null — прежний wiki-ingest; все вызовы передают его
    /// явно (default снят, чтобы вызов не мог остаться без места). Ключ проверен каталогом
    /// на входе IngestAsync. engine (W3) — названный локальный исполнитель: при не-null engine
    /// (W5b) вызов идёт только через RunOnLocalEngineDetailedAsync (путь без выбора
    /// исполнителя и без облака); пустое/пробельное имя — отказ окна без вызова модели;
    /// при null — прежние две ветки. Сбой/пустой ответ — ModelAnswer с причиной:
    /// вызывающий отказывает заявку, а не ловит исключение.
    /// Токены вызова — в аккумулятор разбора (usage=null — нераспознанный
    /// формат метрик — переживаем: это разрешено контрактом шва).</summary>
    private async Task<ModelAnswer> CallModelAsync(string prompt, string ownerId, string passName,
        UsageAccumulator usage, CancellationToken ct, string? actionKey, string? engine)
    {
        // Решение Андрея 02.11: ночной разбор не уходит в облако. Ночь идёт через цепочку без
        // финального claude-шага при локальном маршруте (RunLocalOrChosenDetailedAsync):
        // падение/недоступность локали посреди вызова — отказ вызова, а не уход в Tier/облако.
        // Расход этих вызовов берётся из usage того же ответа движка (WC-4c): метрик не будет
        // только если движок их не отдал — тогда плюс 0, как и раньше.
        // Кнопка «Разобрать» и агент wiki_ingest (actionKey не задан) — человеческие места,
        // облако там допустимо: прежний путь с учётом расхода.
        var noCloud = actionKey == LocalActionCatalog.WikiNightly;
        // Исполнитель назван аргументом заявки (W3, И3 путь 2): страж места его не касается —
        // RunOnLocalEngineDetailedAsync не знает ни шага «выбранная модель», ни страховки
        // FallbackRoute, ни облачного шага, то есть до облака не доходит ни при каком маршруте
        // места. Исполнителя даёт только ночь (её обёртка в WikiNightlyService:217 зовёт
        // AnalyzeAsync с местом ночи), поэтому без места заявки берётся она.
        // W5b: назван — значит не-null. Прежнее ветвление (Length > 0) отдавало пустую строку
        // маршруту места молча: вызывающий просил этот движок, а модель звалась на другом.
        // Пустое или пробельное имя ни в какой движок не резолвится — отказ окна ниже.
        var viaEngine = engine is not null;
        // Решение 6 ADR-039 (поправляет ADR-035 §12ж): ночь не уходит в облако ни на одном
        // шаге цепочки. Финальный claude-шаг гасит RunLocalOrChosenDetailedAsync, но шаг 1
        // цепочки зовёт выбранную админом облачную модель — поэтому облачный маршрут места
        // отказывает окно целиком, до вызова.
        if (!viaEngine && noCloud && !_llm.UsesLocal(LocalActionCatalog.WikiNightly))
            return new ModelAnswer(null, $"{passName}: ночной разбор только локальной моделью");
        string? text;
        OneShotUsage? used = null;
        try
        {
            if (viaEngine)
            {
                if (string.IsNullOrWhiteSpace(engine))
                    // Пустой/пробельный engine (W5b): по такому имени движок не резолвится ни в
                    // какой — звать модель не на чём, и решать это должен не шов. Отказ окна той
                    // же формулировкой и с InputTokens 0, что мёртвый движок: по ним ночь отличает
                    // отказ движка от отказа плана (IsLocalDownRejection, WikiNightlyService:1158).
                    // Ни одного вызова: по пустой строке прежний путь звал модель маршрута места.
                    return new ModelAnswer(null, $"{passName}: {ReasonLocalUnavailable}");
                var onEngine = await _llm.RunOnLocalEngineDetailedAsync(
                    engine!, actionKey ?? LocalActionCatalog.WikiNightly, prompt, ownerId,
                    jsonFormat: "json", ct: ct);
                if (onEngine is null)
                    // Движок неизвестен, выключен, не готов или не ответил — и всё это null без
                    // исключения (CheapTextRunner.RunOnLocalEngineDetailedAsync): подмены другим
                    // исполнителем нет, решение за вызывающим. Причина — та же формулировка
                    // маршрута, что у мёртвой локали ниже: без имени движка (их несколько, J-W2),
                    // по ней и InputTokens == 0 ночь отличает отказ движка от отказа плана
                    // (IsLocalDownRejection) и выключает исполнителя (W5).
                    return new ModelAnswer(null, $"{passName}: {ReasonLocalUnavailable}");
                text = onEngine.Text;
                used = onEngine.Usage;
            }
            else if (noCloud)
            {
                var viaLocal = await _llm.RunLocalOrChosenDetailedAsync(
                    LocalActionCatalog.WikiNightly, prompt, ownerId, jsonFormat: "json", ct: ct);
                if (viaLocal is null)
                    // Мёртвый локальный движок даёт null БЕЗ исключения (CheapTextRunner: движок
                    // не готов или не ответил → (null, null), облачного шага нет) — ниже это
                    // читалось как «модель дала пустой ответ», и ночь 12.11 сожгла 41 отказа с
                    // ложной причиной (дефект 2e13d45c). Здесь — честная причина маршрута.
                    // Текст последней ошибки движка шов не отдаёт: RunLocalOrChosenDetailedAsync
                    // возвращает null без деталей (ICheapTextRunner.cs:90), поэтому формулировка
                    // общая, без имени движка и порта.
                    return new ModelAnswer(null, $"{passName}: {ReasonLocalUnavailable}");
                text = viaLocal.Text;
                used = viaLocal.Usage;
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

    /// <summary>План синтеза: operations — массив объектов op/kind/page/claim/quotes/target.
    /// Ответ модели НЕ десериализуется в операцию пакета напрямую: поля читаются по одному.
    /// `as_of` модели игнорируется — дату state ставит код (WithCodeAsOf, решение Андрея 11.11).
    /// Отсутствующее или нераспознаваемое поле остаётся null, отсутствующие quotes — пустой список (операцию не выбрасываем молча: ValidateSynth
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
            ops.Add(new WikiPlanValidator.SynthOp(op, kind, page, claim, quotes, target, null));
        }
        return new WikiPlanValidator.SynthPlan(ops);
    }

    /// <summary>`as_of` операций ставит код: дата источника (у чата — последняя реплика окна,
    /// у заметки и файла — запись источника, всё это уже лежит в поле written карточки).
    /// Значение модели в плане не доезжает (парсер его не читает). Нет даты — null: state без
    /// as_of валидатор отклонит с причиной.</summary>
    internal static WikiPlanValidator.SynthPlan? WithCodeAsOf(WikiPlanValidator.SynthPlan? plan, DateTimeOffset? at)
    {
        if (plan?.Operations is null) return plan;
        var asOf = at?.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new WikiPlanValidator.SynthPlan(
            plan.Operations.Select(o => o with { AsOf = asOf }).ToList());
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
        // Отброшенные при пакете из принятых: Rejected при Accepted > 0 — только на этом пути
        // (при Accepted == 0 отказы ушли в ремонт или в исход rejected, это не «отбрасывание»).
        if (verdict.Accepted.Count > 0 && verdict.Rejected.Count > 0)
            r = r with
            {
                Dropped = verdict.Rejected.Count,
                DropReasons = verdict.Rejected.Take(MaxDropReasons).Select(i => i.Reason).ToList(),
            };
        if (verdict.Violations.Count == 0)
            return r;
        var issues = "в разделе есть нарушения: " + string.Join("; ", verdict.Violations);
        return r with { Reason = r.Reason is { Length: > 0 } ? r.Reason + "; " + issues : issues };
    }

    private static int ToIntOrMax(long value) => value > int.MaxValue ? int.MaxValue : (int)value;

    private static IngestResult Rejected(string reason, long inputTokens = 0) =>
        new(OutcomeRejected, reason, null, inputTokens);
}
