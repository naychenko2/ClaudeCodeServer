using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// «Разобрать этот чат сейчас» (S6e-2, ADR-035 §12а «Ступень 0» и §12б этапы 1–2, 9):
// курсор и дельта из стора разбора чатов (S6e-1), окна S6j → заявка в WikiIngestService
// на каждое окно → одна хроника на чат плюс пакеты страниц знаний, и только после этого
// сдвиг курсора. Сам разбор (проходы A/B, проверки, пакет, применение) — не здесь: его
// делает очередь заявок (S6b-3); здесь — отбор ступени 0, дельта, хроника и курсор.
//
// Ступень 0 (ADR-035 §12а): не разбираются чаты исполнения задач, чаты без единой
// реплики человека и чаты с пометкой «не разбирать» (WikiMark=skip) — отказ с причиной
// до единого обращения к модели. «Важный» (WikiMark=important) здесь не special-case:
// минуя ступени 1–2, он проходит ступень 0 так же (это правило отбора ночи, S6f).
// Ветки и продолжения (BranchedFromSessionId/ContinuedFromSessionId) в этом шаге —
// обычный чат: префикс ветки и записку-передачи уже вырезает слайсер (S6j), поэтому
// префикс родителя в разбор ветки не попадает и заново не разбирается (S6e-3,
// «родитель отвечает за префикс»). Хроника продолжения дополнительно несёт ссылку на
// хронику прежнего чата (§12б этап 3): цель — её заголовок на диске, путь к прежней
// хронике — только ChroniclePath (ContinuationLinkLine).
//
// Курсор (§12б этап 1): из записи стора «разобрано до m{N}» + хэш записи N. Хэш не
// сошёлся (история переписана) — курсор в 0, повторные утверждения гасит дедup (S4d),
// а не курсор. Дельта N+1…конец режется окнами S6j; каждое окно — своя карточка
// (захват S6h: владение, дом чата, TaskExecution — уже проверяет CaptureSession,
// здесь это не дублируется) и своя заявка разбора. Текст модели — всё окно: выдержка
// карточки ≤8 КБ (контракт S6j) — данные карточки, а не мера промпта (§12б этап 4:
// дельта режется на окна по 35k токенов, и окно — единица разбора).
//
// Хроника (§12б «Результат разбора»): одна страница type: chronicle на чат, папка
// «Хроники/» раздела дома; имя файла — id чата (одна хроника не должна зависеть от
// переименования чата; имя для человека — в заголовке страницы). Создаётся первым
// успешным разбором, следующий разбор дописывает её. Утверждения — подтверждённые
// кодом цитаты применённых пакетов (S6b-3, M1) с сносками на карточку своего окна.
// Классификацию плана («Что решили», «Открытые вопросы») mechanical-хроника этого
// шага не несёт: WikiPlanValidator.cs:32 — хроника есть механическая операция, а
// chronicle-объект плана код пока не разбирает; mechanical-доступная секция —
// «Что выяснили» (утверждения, установленные этим разбором).
//
// Курсор (§12б этап 9): сдвигается на конец дельты только после применения или исхода
// «нечего добавить»; любой сбой — курсор на месте. Сбой посреди окон: весь разбор
// отказан, хроника не начата (половины хроники на диске не остаётся). «Нечего
// добавить» — штатный исход (ADR-035:269): курсор сдвинут, zone=nothing_new с
// причиной модели, nothingNewStreak+1 (счётчик понижения балла — его читает S6f),
// хроника не создаётся. При частично применённом разборе (есть applied) исход —
// applied; пакет в «Входящих» без единого применённого — awaiting_human, курсор на
// месте: «применения» ещё не было. Зона/причина исхода принадлежат разбору и отражают
// его последний исход: при applied они обнуляются — применённый разбор означает «новые
// утверждения были», и его собственный устаревший nothing_new не переживает свой же
// исход (ревью S6f, доработка 2); nothingNewStreak при applied обнуляется: ряд «нечего
// добавить» прерван.
//
// Конкурентность (хвосты ревью S6b-3):
// - две заявки одного чата — один разбор: ключ in-flight включает дом (owner|chat|home),
//   вторая присоединяется к незавершённой задаче и получает её результат;
// - ct заявителя не передаётся общему прогону: разбор — задача очереди, отмена одной
//   заявки не гасит чужую;
// - сбой внутри прогона — исход с причиной, исключения наружу нет;
// - стор читается и пишется через WikiChatTriageStore (его блок на корень вики — атомарный
//   цикл «прочитал→изменил→записал» уже там); хроника пишется через WikiKeeper.WritePageAsync
//   (манифест и проекции раздела — его транзакция; запись в drifting-файл будет отказана,
//   и это тоже сбой разбора с причиной).
//
// REST-вход (S7b0a): контроллер зовёт только AnalyzeAsync/IsRunning/GetLast. Отличительная
// часть контракта — машинный код отказа (ChatResult.Code), текст причины — человеку и модели.
// Последний исход на чат (ключ owner|session, потолок 200, с временем окончания) и признак
// «разбор идёт» живут в памяти этого экземпляра (single-flight — singleton DI): записываются
// под тем же блоком, что и снятие in-flight, исход — раньше снятия: «не идёт» обязано
// означать «last свежий». Формула пути хроники — одна здесь (ChroniclePath): список чатов
// берёт её отсюда, а не копированием построения.
public sealed class WikiChatIngestService
{
    /// <summary>Исходы разбора чата — те же четыре, что у заявки разбора (S6b-3):
    /// applied — есть применённый пакет; awaiting_human — пакеты есть, ни один не
    /// применён (ждут человека); nothing_new — штатное «нечего добавить»;
    /// rejected — отказ с причиной (ступень 0, вход, сбой окна, сбой применения).
    /// Плюс up_to_date (S7b0a): курсор на конце истории, разбирать нечего — состояние,
    /// а не отказ: REST-контракт различает их, фронт показывает «нечего разобрать».</summary>
    public const string OutcomeApplied = "applied";
    public const string OutcomeAwaitingHuman = "awaiting_human";
    public const string OutcomeNothingNew = "nothing_new";
    public const string OutcomeRejected = "rejected";
    public const string OutcomeUpToDate = "up_to_date";

    /// <summary>Машинные коды отказа (S7b0a): вызывающий различает отказы по коду, а не
    /// подстрокой причины (текст причины — человеку и модели). not_found — чужой и
    /// несуществующий чат неразличимы вызывающим: per-owner, без утечки существования
    /// чужого. Остальные — ступень 0 и незаведённая вики. Кодов, не названных контрактом,
    /// service не выдумывает: прочие отказы остаются без кода (вызывающий различает их
    /// по исходу rejected).</summary>
    public const string CodeNotFound = "not_found";
    public const string CodeTaskExecution = "task_execution";
    public const string CodeMarkedSkip = "marked_skip";
    public const string CodePersonalSection = "personal_section";
    public const string CodeWikiNotInitialized = "wiki_not_initialized";

    /// <summary>Зона стора для исхода «нечего добавить» (ADR-035:269).</summary>
    public const string ZoneNothingNew = "nothing_new";

    /// <summary>Папка хроник чатов внутри раздела (ADR-035:273).</summary>
    public const string ChronicleDirName = "Хроники";

    /// <summary>Результат разбора: исход + причина (для отказа и для nothing_new —
    /// причина модели) + id применённых пакетов (для applied) + машинный код отказа
    /// (S7b0a: только для состояний, названных REST-контрактом; null для прочих).</summary>
    public readonly record struct ChatResult(
        string Outcome, string? Reason, IReadOnlyList<string> PackageIds, string? Code = null);

    /// <summary>Последний исход разбора чата с временем окончания (S7b0a, чтение
    /// analyze-status и списка).</summary>
    public sealed record LastAnalysis(ChatResult Result, DateTimeOffset FinishedAt);

    private readonly WikiSourceCapture _capture;
    private readonly WikiIngestService _ingest;
    private readonly ISessionDirectory _sessions;
    private readonly WikiKeeper _keeper;
    private readonly string _dataDir;

    private readonly object _inFlightLock = new();
    private readonly Dictionary<string, Task<ChatResult>> _inFlight = new();

    /// <summary>Последний исход на чат: ключ owner|session — без дома: статус спрашивают
    /// про чат, а не про раздел разбора; чужой владелец ключа не видит. Потолок 200 записей:
    /// это память, не диск; при переполнении снимается самый старый по времени окончания —
    /// статусу нужен последний, а не история.</summary>
    private readonly Dictionary<string, LastAnalysis> _last = new();
    private const int LastCap = 200;

    public WikiChatIngestService(WikiSourceCapture capture, WikiIngestService ingest,
        ISessionDirectory sessions, WikiKeeper keeper, IConfiguration config)
    {
        _capture = capture;
        _ingest = ingest;
        _sessions = sessions;
        _keeper = keeper;
        // Каталог данных — как у всех сервисов вертикали (тот же корень, что у Хранителя).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    /// <summary>Поставить разбор чата в очередь и дождаться его исхода. Две одновременные
    /// заявки одного чата — один разбор. ct участвует в сигнатуре заявителя, но не
    /// передаётся общему прогону (см. шапку). Исключения наружу нет.</summary>
    public Task<ChatResult> AnalyzeAsync(string ownerId, string sessionId, CancellationToken ct = default)
    {
        // Fail-closed входа: кривая заявка не доходит ни до очереди, ни до модели.
        // Кодов у этих отказов нет: REST-контракт называет коды только для состояний,
        // которые различает фронт (not_found, ступень 0, незаведённая вики); ошибка
        // вызова — не состояние, она остаётся rejected без кода.
        if (string.IsNullOrWhiteSpace(ownerId))
            return Task.FromResult(Refused(null, "владелец пустой"));
        if (string.IsNullOrWhiteSpace(sessionId))
            return Task.FromResult(Refused(null, "id чата пустой"));
        // Id чата должен быть представим в ref источника (тот же класс, что проверяет
        // захват): id с спецсимволами не даст ни карточки, ни курсора в сторе.
        if (!IsSessionIdSafe(sessionId))
            return Task.FromResult(Refused(null, $"id чата '{sessionId}' не представим в ссылке источника"));

        var session = _sessions.GetById(sessionId);
        if (session is null)
            return Task.FromResult(Refused(CodeNotFound, "чат не существует"));
        if (_sessions.ResolveOwnerId(session) != ownerId)
            return Task.FromResult(Refused(CodeNotFound, "чат принадлежит другому владельцу: разбор — per-owner"));

        // Ступень 0 (ADR-035 §12а) — исключения кода до обращения к модели.
        if (session.TaskExecution)
            return Task.FromResult(Refused(CodeTaskExecution, "чаты исполнения задач не разбираются: их итоги уже источники"));
        if (session.WikiMark == WikiMarks.Skip)
            return Task.FromResult(Refused(CodeMarkedSkip, "чат помечен человеком «не разбирать»"));

        // Раздел назначения — дом чата (ADR-036, решение 3). Личный раздел закрыт до Ф2 —
        // тот же отказ, что у очереди заявок (S6b-3), только до чтения истории.
        var home = HomeOf(session);
        if (home == WikiSections.Personal)
            return Task.FromResult(Refused(CodePersonalSection, "личный раздел — с Ф2"));

        var key = ownerId + "|" + sessionId + "|" + home;
        // Заявка регистрируется ДО начала прогона и сам прогон идёт вне вызывающего
        // (Task.Run): иначе опоздавшая заяка пришла бы в очередь до регистрации и
        // начала бы второй разбор — та самая вакуумность гонки, отмеченная в ревью S6b-3.
        var tcs = new TaskCompletionSource<ChatResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_inFlightLock)
        {
            if (_inFlight.TryGetValue(key, out var running))
                return running;
            _inFlight[key] = tcs.Task;
        }
        _ = Task.Run(async () =>
        {
            var result = await RunProtectedAsync(ownerId, sessionId, home, session);
            // Снятие записи ПРЕДШЕСТВУЕТ выдаче результата: заявка, пришедшая сразу
            // после завершения разбора, не должна получить его старый результат через
            // неубранную запись — она начинает новый разбор (или слышит «новых сообщений
            // нет»). Порядок «TrySetResult → сняние» давал это в полном прогоне тестов:
            // опоздавшая заявка присоединялась к уже завершённой задаче.
            lock (_inFlightLock)
            {
                // Исход пишется раньше снятия in-flight (S7b0a): analyze-status не должен
                // показать «не идёт» со старым last — законченный разбор фиксируется до
                // того, как чат станет «не идущим».
                _last[ownerId + "|" + sessionId] = new LastAnalysis(result, DateTimeOffset.UtcNow);
                TrimLastLocked();
                if (_inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, tcs.Task))
                    _inFlight.Remove(key);
            }
            tcs.TrySetResult(result);
        });
        return tcs.Task;
    }

    /// <summary>Идёт ли сейчас разбор этого чата (S7b0a: поле running списка и analyze-status).
    /// Ключ — owner|session: чужой владелец не видит чужой чат, даже по признаку «его
    /// разбирают». In-flight-ключ содержит дом, поэтому проверка — по префиксу owner|session|.</summary>
    public bool IsRunning(string ownerId, string sessionId)
    {
        if (string.IsNullOrEmpty(ownerId) || string.IsNullOrEmpty(sessionId))
            return false;
        var prefix = ownerId + "|" + sessionId + "|";
        lock (_inFlightLock)
        {
            foreach (var key in _inFlight.Keys)
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            return false;
        }
    }

    /// <summary>Последний исход разбора этого чата с временем окончания (S7b0a,
    /// analyze-status). null — разбора не было; чужой владелец — тоже null (ключ с владельцем).</summary>
    public LastAnalysis? GetLast(string ownerId, string sessionId)
    {
        if (string.IsNullOrEmpty(ownerId) || string.IsNullOrEmpty(sessionId))
            return null;
        lock (_inFlightLock)
            return _last.TryGetValue(ownerId + "|" + sessionId, out var last) ? last : null;
    }

    private void TrimLastLocked()
    {
        while (_last.Count > LastCap)
        {
            string oldestKey = "";
            DateTimeOffset oldest = DateTimeOffset.MaxValue;
            foreach (var (k, v) in _last)
                if (v.FinishedAt < oldest)
                {
                    oldest = v.FinishedAt;
                    oldestKey = k;
                }
            _last.Remove(oldestKey);
        }
    }

    private async Task<ChatResult> RunProtectedAsync(string ownerId, string sessionId, string home, Session session)
    {
        try
        {
            return await RunAsync(ownerId, sessionId, home, session);
        }
        catch (Exception e)
        {
            // Любой неожиданный сбой — исход с причиной: ни исключения вызывающему,
            // ни «половины разбора» на диске без объяснения.
            return Refused(null, $"внутренний сбой разбора чата: {e.Message}");
        }
    }

    private async Task<ChatResult> RunAsync(string ownerId, string sessionId, string home, Session session)
    {
        var root = WikiPaths.Root(_dataDir, ownerId);
        if (!WikiPaths.IsInitialized(_dataDir, ownerId))
            return Refused(CodeWikiNotInitialized, "вики этого владельца не заведена");

        var history = await _sessions.GetHistoryAsync(sessionId);
        if (history is null || history.Count == 0)
            return Refused(null, "у чата нет истории");

        // Ступень 0: чат без единой реплики человека. Мера «реплики человека» — та же,
        // что у отбора и у цитируемого текста (фильтр слайсера): auto, директивы,
        // через агента, плашки штаба, записка-передача — не реплики.
        if (!WikiChatSlicer.Lines(history, 1, history.Count).Any(l => l.IsHuman))
            return Refused(null, "в чате нет ни одной реплики человека");

        // Курсор из стора (S6e-1). Нечитаемый стор — не «нулевой курсор»: начать с нуля
        // значило бы молча переразобрать всё, что уже разобрано.
        var read = WikiChatTriageStore.Get(root);
        if (read.State == WikiTriageFileState.Invalid)
            return Refused(null, "стор разбора чатов нечитается: разбор не начинается с неизвестного курсора");
        var entry = read.Entries.FirstOrDefault(e => e.SessionId == sessionId);

        var cursor = entry?.Cursor ?? 0;
        if (cursor > 0 &&
            (cursor > history.Count || HashOfRecord(history[cursor - 1]) != entry!.CursorHash))
        {
            // Запись N подменена или исчезла — история переписана (§12б этап 1).
            // Курсор в 0; повторные утверждения гасит дедup (S4d), а не курсор.
            cursor = 0;
        }
        if (cursor >= history.Count)
            // Курсор на конце истории — не отказ, а состояние (S7b0a): REST показывает его
            // исходом up_to_date («нечего разобрать»), MCP-путь — отказом с той же причиной
            // (ветка `_` в IngestChatAsync).
            return new ChatResult(OutcomeUpToDate, "чат разобран до конца: новых сообщений нет", []);

        var windows = WikiChatSlicer.Windows(history, cursor + 1, history.Count);
        if (windows.Count == 0)
            return Refused(null, "в дельте нечего цитировать: всё её содержимое — служебные записи");

        var applied = new List<(string PackageId, string CardTitle)>();
        var awaiting = 0;
        string? nothingReason = null;

        foreach (var window in windows)
        {
            var from = window.Lines[0].Index;
            var to = window.Lines[^1].Index;

            // Карточка окна — захватом (S6h): владение, дом чата, Service-записи —
            // проверяет он, здесь это не дублируется.
            var cap = await _capture.CaptureSession(ownerId, home, sessionId, from, to);
            if (!cap.Ok)
                return Refused(null, $"захват источника m{from}-m{to} не удался: {cap.Reason}");

            var ing = await _ingest.IngestAsync(ownerId, new WikiIngestService.Request(
                WikiGate.OriginSession, $"session:{sessionId}#m{from}-{to}",
                cap.CardRelPath!, WindowText(window), home, null));
            switch (ing.Outcome)
            {
                case WikiIngestService.OutcomeApplied:
                    if (ing.PackageId is not { Length: > 0 } packageId)
                        return Refused(null, $"исход «applied» окна m{from}-m{to} без id пакета");
                    // Заголовок карточки — имя её файла (WriteCard, S6h): цель сносок хроники.
                    applied.Add((packageId, Path.GetFileNameWithoutExtension(cap.CardRelPath!)));
                    break;
                case WikiIngestService.OutcomeAwaitingHuman:
                    awaiting++;
                    break;
                case WikiIngestService.OutcomeNothingNew:
                    nothingReason ??= ing.Reason;
                    break;
                default:
                    // Сбой любого окна — отказ всего разбора: курсор не двигается,
                    // хроника не начинается («зависшей» половины хроники не остаётся).
                    return Refused(null, $"разбор окна m{from}-m{to} не удался: {ing.Reason}");
            }
        }

        if (applied.Count == 0 && awaiting == 0)
        {
            // Все окна сказали «нечего добавить» — штатный исход (ADR-035:269):
            // курсор сдвинут, зона и streak записаны, хроника не создаётся.
            var up = WikiChatTriageStore.Upsert(root, new WikiChatTriageEntry(
                sessionId, history.Count, HashOfRecord(history[^1]),
                ZoneNothingNew, nothingReason, (entry?.NothingNewStreak ?? 0) + 1, default));
            if (!up.Ok)
                return Refused(null, $"исход разбора не записан: {up.Reason}");
            return new ChatResult(OutcomeNothingNew, nothingReason, []);
        }

        if (applied.Count == 0)
        {
            // Пакеты есть, применённых нет: применения не было — курсор на месте
            // (сдвиг только после применения или «нечего добавить»). Хроника тоже
            // не пишется: её утверждения — из применённых пакетов.
            return new ChatResult(OutcomeAwaitingHuman,
                "в разборе есть спорные пакеты: их решает человек («Входящие»)", []);
        }

        var (chronicleOk, chronicleReason) = await AppendChronicleAsync(ownerId, home, session, sessionId, applied);
        if (!chronicleOk)
            return Refused(null, chronicleReason ?? "хроника чата не записана");

        // Курсор после применения — на конец дельты, с хэшем последней записи.
        // Зона/причина исхода обнуляются: они принадлежат разбору и отражают его
        // последний исход — применённый разбор означает «новые утверждения были», и его
        // собственный устаревший nothing_new не переживает свой же исход (ревью S6f,
        // доработка 2). Streak обнулён: ряд «нечего добавить» прерван применённым
        // разбором.
        var upApplied = WikiChatTriageStore.Upsert(root, new WikiChatTriageEntry(
            sessionId, history.Count, HashOfRecord(history[^1]),
            null, null, 0, default));
        if (!upApplied.Ok)
            return Refused(null, $"курсор разбора не записан: {upApplied.Reason}");

        return new ChatResult(OutcomeApplied, null, applied.Select(a => a.PackageId).ToList());
    }

    // ── хроника чата ───────────────────────────────────────────────────────────────

    /// <summary>Путь хроники чата под pages/ — та же формула, которой разбор её пишет
    /// (AppendChronicleAsync ниже): один источник пути на весь продукт — список чатов и
    /// фронт берут её отсюда, а не копированием построения (S7b0a). null — хроники здесь
    /// быть не может: не-проектный дом (личный — с Ф2, сфера — следующая фича) или id,
    /// не представимый в пути (тот же guard, что у AnalyzeAsync).</summary>
    public static string? ChroniclePath(string home, string sessionId) =>
        WikiSections.TryProjectId(home, out _) && IsSessionIdSafe(sessionId)
            ? WikiSections.Folder(home) + ChronicleDirName + "/" + sessionId + ".md"
            : null;

    /// <summary>Дописать хронику чата утверждениями применённых пакетов. Первой
    /// успешный разбор создаёт страницу, следующий — дописывает «Что выяснили».
    /// Хроника продолжения несёт строку-ссылку на хронику прежнего чата в «О чём»
    /// (ContinuationLinkLine). Существующая нечитаемая хроника — отказ: её строки уже
    /// написанное, перезаписывать молча нельзя.</summary>
    private async Task<(bool Ok, string? Reason)> AppendChronicleAsync(
        string ownerId, string home, Session session, string sessionId,
        IReadOnlyList<(string PackageId, string CardTitle)> applied)
    {
        var root = WikiPaths.Root(_dataDir, ownerId);
        // Дом и id проверены самим путём разбора (AnalyzeAsync: ступень 0 и guard id) —
        // для проектного дома формула полна.
        var pageKey = ChroniclePath(home, sessionId)!;
        var full = SafePath.JoinBelowRoot(root, "pages/" + pageKey);

        WikiPage? page = null;
        if (File.Exists(full))
        {
            var parsed = WikiPage.Parse(File.ReadAllText(full, Encoding.UTF8));
            if (parsed.Model is null || parsed.Model.Frontmatter.Type != WikiFrontmatter.TypeChronicle)
                return (false, $"страница хроники чата '{pageKey}' не читается как хроника: дописать её нельзя");
            page = parsed.Model;
        }

        var statements = new List<string>();
        var footnotes = new List<WikiPage.WikiFootnote>(page?.Footnotes ?? []);
        // Render() нумерует сноски s1…sN по порядку списка: ссылки новых строк нумеруются
        // от конца существующих (канонический порядок сохраняется).
        var nextRef = footnotes.Count + 1;
        foreach (var (packageId, cardTitle) in applied)
        {
            var read = WikiPackageStore.TryRead(root, packageId);
            if (read.State != WikiPackageFileState.Valid || read.Package is null)
                return (false, $"применённый пакет '{packageId}' не читается: хронику дописать нечем");
            foreach (var op in read.Package.Operations)
            {
                if (op.Op == WikiPackage.OpLink || op.Text is not { Length: > 0 })
                    continue; // «link» — связь, не утверждение; утверждения без цитаты нет.
                // Утверждение хроники — цитата, подтверждённая кодом (S6b-3, M1). Цитату
                // с переводом строки складываем в одну строку: формат страницы строчный
                // (WikiPage), целая цитата остаётся на странице знаний.
                var quote = op.Text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
                statements.Add($"{quote} [^s{nextRef}]");
                footnotes.Add(new(new(true, cardTitle), quote));
                nextRef++;
            }
        }
        if (statements.Count == 0)
            return (true, null); // применённых утверждений нет — хронику трогать нечем.

        var linkLine = ContinuationLinkLine(ownerId, home, session);

        if (page is null)
        {
            page = new WikiPage
            {
                Title = ChronicleTitle(session, sessionId),
                Frontmatter = new WikiFrontmatter { Type = WikiFrontmatter.TypeChronicle },
                Sections =
                [
                    new() { Name = "О чём", Lines = linkLine is not null ? [linkLine] : [] },
                    new() { Name = "Что решили", Lines = [] },
                    new() { Name = "Что выяснили", Lines = statements },
                    new() { Name = "Открытые вопросы", Lines = [] },
                ],
                Footnotes = footnotes,
            };
        }
        else
        {
            var sections = new List<WikiPage.Section>();
            foreach (var s in page.Sections)
            {
                if (s.Name == "Что выяснили")
                {
                    sections.Add(new() { Name = s.Name, Lines = [.. s.Lines, .. statements] });
                    continue;
                }
                // Ссылка прежней хроники дописывается идемпотентно: она уже в «О чём» —
                // строка та же, дубль не плодится.
                if (s.Name == "О чём" && linkLine is not null && !s.Lines.Contains(linkLine))
                {
                    sections.Add(new() { Name = s.Name, Lines = [.. s.Lines, linkLine] });
                    continue;
                }
                sections.Add(s);
            }
            if (sections.TrueForAll(s => s.Name != "Что выяснили"))
                sections.Add(new() { Name = "Что выяснили", Lines = statements });
            page = new WikiPage
            {
                Title = page.Title,
                Frontmatter = page.Frontmatter,
                Sections = sections,
                Footnotes = footnotes,
            };
        }

        var write = await _keeper.WritePageAsync(ownerId, home, pageKey, page.Render());
        return write.Status == WikiOpStatus.Applied
            ? (true, null)
            : (false, $"хроника чата не записана: {write.Reason ?? write.Status.ToString()}");
    }

    /// <summary>Дом чата — раздел назначения (ADR-036, решение 3): проект → его раздел,
    /// иначе личный. Одна формула на весь сервис: её же спрашивает ссылка хроники
    /// продолжения (ContinuationLinkLine), чтобы не второй рубеж разбора дома.</summary>
    private static string HomeOf(Session session) =>
        session.ProjectId is { Length: > 0 } projectId
            ? WikiSections.ProjectsPrefix + projectId
            : WikiSections.Personal;

    /// <summary>Префикс строки-ссылки хроники продолжения на хронику прежнего чата. Одна
    /// копия формата на весь продукт: её пишет ContinuationLinkLine, по ней же откат по
    /// источнику убирает строку из хроник-продолжений (H2-1, WikiRetractService).</summary>
    public const string ContinuationLinkPrefix = "Продолжение чата: ";

    /// <summary>Строка-ссылка хроники продолжения на хронику прежнего чата (§12б этап 3:
    /// «хроника нового чата ссылается на хронику прежнего»). Путь к прежней хронике —
    /// только ChroniclePath (единственная точка правды); цель ссылки — её заголовок на
    /// диске (ссылка резолвится по заголовку, WikiGate.cs:320). Только свой раздел:
    /// ссылка вбок — нарушение ADR-036, решение 4. Прежнего чата нет, он чужого
    /// владельца, его хроника не написана или не читается — ссылки нет: битая ссылка
    /// хуже отсутствующей, и её отсутствие — не отказ разбора.</summary>
    private string? ContinuationLinkLine(string ownerId, string home, Session session)
    {
        if (session.ContinuedFromSessionId is not { Length: > 0 } prevId)
            return null;
        var prev = _sessions.GetById(prevId);
        if (prev is null || _sessions.ResolveOwnerId(prev) != ownerId)
            return null;
        var prevHome = HomeOf(prev);
        if (prevHome != home)
            return null;
        if (ChroniclePath(prevHome, prevId) is not { Length: > 0 } prevKey)
            return null;
        var prevFull = SafePath.JoinBelowRoot(WikiPaths.Root(_dataDir, ownerId), "pages/" + prevKey);
        if (!File.Exists(prevFull))
            return null;
        var parsed = WikiPage.Parse(File.ReadAllText(prevFull, Encoding.UTF8));
        if (parsed.Model is null || parsed.Model.Frontmatter.Type != WikiFrontmatter.TypeChronicle)
            return null;
        return $"{ContinuationLinkPrefix}[[{parsed.Model.Title}]]";
    }

    /// <summary>Заголовок хроники — имя чата плюс дата его начала (ADR-035:273). Имя
    /// пустое — id чата. Управляющие символы имени в заголовок не идут (страница
    /// строчная).</summary>
    private static string ChronicleTitle(Session session, string sessionId)
    {
        var name = new string((session.Name ?? "").Where(c => c >= ' ').ToArray()).Trim();
        if (name.Length == 0)
            name = sessionId;
        return $"{name} {session.CreatedAt:yyyy-MM-dd}";
    }

    // ── окно → текст источника ─────────────────────────────────────────────────────

    /// <summary>Текст окна для модели: целые реплики окна с подписями «Пользователь:/
    /// Ассистент:» — тот же формат ленты, что у выдержки (WikiChatSlicer.Excerpt), без
    /// её 8-КБ предела: выдержка — данные карточки, окно — единица разбора (§12б этап 4).</summary>
    private static string WindowText(WikiChatSlicer.Window window)
    {
        var sb = new StringBuilder();
        foreach (var line in window.Lines)
            sb.Append(line.IsHuman ? "Пользователь:" : "Ассистент:").Append('\n')
                .Append(line.Text).Append("\n\n");
        return sb.ToString().Trim();
    }

    // ── курсор ─────────────────────────────────────────────────────────────────────

    /// <summary>Хэш записи N — sha256 её JSON без служебных полей (с дискриминатором типа —
    /// базовый StoredMessage): «история переписана» значит изменилось содержимое записи, а не
    /// только байты файла под ней. Добавление поля в модель истории сдвинет все хэши —
    /// следующий разбор начнётся с нуля и упрётся в дедup, а не в потерю данных
    /// (fail-closed дороже экономии).
    ///
    /// Инвариант (хвост ревью S6e-2, п.2): хэш зависит только от неизменяемого содержимого
    /// реплики — роли, текста и признаков, которые не дописываются потом. Поля, которые
    /// дописываются ПОСЛЕ создания записи (см. Protocol/StoredMessage.cs: PromptSnapshotId —
    /// при запуске хода, TurnAccumulator.cs:97; Model у текста — backfill'ом по result;
    /// Input у tool_use — финальным OnToolUse по дедупу ранней карточки из стрима,
    /// TurnAccumulator.cs:138; Result/IsError/… у tool_use — при tool_result;
    /// Resolved/Answers — при ответе человека), меняют служебное состояние записи, а не то,
    /// что в ней сказано: живой чат,
    /// разобранный посреди хода, держал бы курсор на реплике, хэш которой изменился бы без
    /// всякой правки истории, — и следующий разбор видел бы «историю переписали» и звал бы
    /// модель заново на весь чат (дедup гасит повторы, но модель-то зовётся). Удаляются по
    /// дискриминатору kind: имена, которые у других типов несут неизменяемое содержимое
    /// (Model у session_started, PersonaId у текста), выпадают только у своего kind.
    ///
    /// Обратная совместимость: курсоры, записанные прежним полным хэшем, при смене формата
    /// дают разовый сброс в 0 — допустимо: переразбор гасит дедup, потеря данных не.</summary>
    private static string HashOfRecord(StoredMessage record)
    {
        var node = JsonSerializer.SerializeToNode<StoredMessage>(record)!.AsObject();
        if (node["kind"] is JsonValue kind && ServiceFieldsByKind.TryGetValue(kind.GetValue<string>(), out var fields))
            foreach (var field in fields)
                node.Remove(field);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(node.ToJsonString()))).ToLowerInvariant();
    }

    // Служебные поля по типу записи — то, что дописывается после её создания (строки
    // Protocol/StoredMessage.cs: 45, 117, 273-302, 311-313, 321-322, 325-327, 342-352, 361).
    private static readonly Dictionary<string, string[]> ServiceFieldsByKind = new(StringComparer.Ordinal)
    {
        ["user_message"] = ["PromptSnapshotId"],
        ["text"] = ["Model"],
        ["tool_use"] = ["Input", "Result", "IsError", "BgDone", "StartedAt", "FinishedAt", "Started", "Stages", "Totals"],
        ["workflow_progress"] = ["IsDone", "Aborted", "Agents"],
        ["ask_question"] = ["Resolved", "Answers"],
        ["plan_review"] = ["Resolved", "Approved", "Feedback"],
        ["team_plan"] = ["Plan", "Resolved", "Approved", "Feedback", "SupersededBy", "PersonaId"],
        ["team_escalation"] = ["Escalation"],
    };

    private static bool IsSessionIdSafe(string sessionId) =>
        sessionId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static ChatResult Refused(string? code, string reason) => new(OutcomeRejected, reason, [], code);
}
