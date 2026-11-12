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
// Ветки (BranchedFromSessionId) в этом шаге — обычный чат: префикс ветки уже вырезает
// слайсер (S6j), поэтому префикс родителя в разбор ветки не попадает и заново не
// разбирается (S6e-3, «родитель отвечает за префикс»). Ссылку на «прежний чат» хроника
// больше не пишет: механика «Продолжить в новом чате» убрана из продукта. Ранее
// написанные строки «Продолжение чата: [[…]]» остаются как накопленное знание; их
// снимает откат по источнику (WikiRetractService.RemoveContinuationLinks).
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
// «нечего добавить»; любой сбой — курсор на месте. Сбой посреди окон: если ничего ещё
// не применено — весь разбор отказан, хроника не начата (половины хроники на диске не
// остаётся). Если применено хотя бы одно окно — частичный исход (WC-3): applied с
// признаком Partial и причиной отказанного окна, хроника по применённым, курсор на конце
// последнего успешного окна — он не уходит назад и не прыгает за отказанное окно; отказ
// позднего окна не перечёркивает применённое. «Нечего
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
// - ключ не включает место разбора (actionKey, N7a): кнопка и ночь по одному чату дают
//   один прогон, движок — того, кто пришёл первым; присоединившаяся заявка получает его
//   результат и своей моделью не оборачивается;
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
// «разбор идёт» живут в памяти этого экземпляра (single-flight — singleton DI): последний
// исход записывается единообразно (F3) — предпрогонный отказ в момент отказа, исход прогона
// под тем же блоком, что и снятие in-flight, раньше снятия: «не идёт» обязано означать
// «last свежий». Формула пути хроники — одна здесь (ChroniclePath): список чатов
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
    /// подстрокой причины (текст причины — человеку и модели). not_found — чужой, приватный
    /// (FIN3-3) и несуществующий чат неразличимы вызывающим: per-owner и приватность чата не
    /// дают утечки существования. Остальные — ступень 0 и незаведённая вики. Кодов, не
    /// названных контрактом, service не выдумывает: прочие отказы остаются без кода
    /// (вызывающий различает их по исходу rejected).</summary>
    public const string CodeNotFound = "not_found";
    public const string CodeTaskExecution = "task_execution";
    public const string CodeMarkedSkip = "marked_skip";
    public const string CodePersonalSection = "personal_section";
    public const string CodeWikiNotInitialized = "wiki_not_initialized";

    /// <summary>Зона стора для исхода «нечего добавить» (ADR-035:269).</summary>
    public const string ZoneNothingNew = "nothing_new";

    /// <summary>Зона стора для окна, пропущенного курсором после RejectSkipAfter отказов подряд
    /// (C2R-Sh2). Имя — контракт с фронтом (Sh6c), менять нельзя.</summary>
    public const string ZoneRejectedSkipped = "rejected_skipped";

    /// <summary>Зона стора для чата, чей разобранный исход — пакет во «Входящих», ждущий
    /// решения человека (C2R-Sh2b). Имя — контракт с фронтом (Sh6d), менять нельзя.</summary>
    public const string ZoneAwaitingHuman = "awaiting_human";

    /// <summary>Сколько отказов подряд ПОСЛЕ ОТВЕТА модели переводит курсор через окно.</summary>
    public const int RejectSkipAfter = 3;

    /// <summary>Папка хроник чатов внутри раздела (ADR-035:273).</summary>
    public const string ChronicleDirName = "Хроники";

    /// <summary>Результат разбора: исход + причина (для отказа и для nothing_new —
    /// причина модели) + id применённых пакетов (для applied) + машинный код отказа
    /// (S7b0a: только для состояний, названных REST-контрактом; null для прочих). OverrodeHuman —
    /// сколько формулировок человека ИИ заменил в этом разборе сам; DisputedPages — страницы, где он
    /// сам отметил противоречие (ADR-038, предохранитель 6: идут в одну строку утреннего уведомления).
    /// Partial (WC-3): разбор применён частично — позднее окно отказано, применённое осталось;
    /// тогда Reason — причина отказанного окна, а курсор — на конце последнего успешного окна.
    /// InputTokens (WC-4a/WC-4c): полный вход (вместе с кэшем) всех ответивших вызовов модели этого
    /// разбора по всем пройденным окнам — мера нагрузки на GPU локали, а не деньги: деньги из этого
    /// поля не считать, стоимость облачного пути — OneShotUsage.CostUsd. Сумма — независимо от исхода
    /// окна (отказанный план расход не отменяет) и независимо от исхода разбора; 0 — когда метрик не
    /// было: модель ни разу не звалась (отказ ступеней до модели, «новых сообщений нет») либо движок
    /// их не отдал. Dropped/FirstDropReason — операции синтеза, отброшенные поштучным приёмом по
    /// всем разобранным окнам: число и первая причина из первого окна с отказами; отказ
    /// позднего окна не стирает то, что было отброшено раньше (минор ревью C2R-Sh1).</summary>
    public readonly record struct ChatResult(
        string Outcome, string? Reason, IReadOnlyList<string> PackageIds, string? Code = null,
        int OverrodeHuman = 0, IReadOnlyList<string>? DisputedPages = null, bool Partial = false,
        long InputTokens = 0, int Dropped = 0, string? FirstDropReason = null);

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
    /// про чат, а не про раздел разбора; чужой владелец ключа не видит. Записывается любой
    /// исход заявки единообразно (F3, хвосты ревью): предпрогонный отказ (вход, ступень 0,
    /// not_found) — в момент отказа, исход прогона — под тем же блоком, что и снятие
    /// in-flight: «анализировать» и «last» описывают одно и то же состояние. Без владельца
    /// или id ключа нет — такой отказ не записывается. Потолок 200 записей: это память,
    /// не диск; при переполнении снимается самый старый по времени окончания — статусу
    /// нужен последний, а не история.</summary>
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
    /// передаётся общему прогону (см. шапку). Исключения наружу нет.
    /// actionKey (N7a) — место разбора для вызова модели: null — прежний wiki-ingest
    /// (кнопка и путь MCP), ночь подаёт wiki-nightly. In-flight-ключ места не содержит:
    /// присоединившаяся заявка получает движок того, кто пришёл первым (см. шапку).</summary>
    public Task<ChatResult> AnalyzeAsync(string ownerId, string sessionId, CancellationToken ct = default,
        string? actionKey = null)
    {
        // F3 (хвосты ревью): отказ заявки — тоже последний исход, он записывается в тот же
        // last, что и исход прогона: «анализировать» и «last» описывают одно состояние.
        // Без владельца или id ключа нет — такой отказ не записывается (его и спросить нечем).
        Task<ChatResult> RefusedRecorded(string? code, string reason)
        {
            var res = Refused(code, reason);
            if (!string.IsNullOrEmpty(ownerId) && !string.IsNullOrEmpty(sessionId))
                RecordLast(ownerId, sessionId, res);
            return Task.FromResult(res);
        }

        // Fail-closed входа: кривая заявка не доходит ни до очереди, ни до модели.
        // Кодов у этих отказов нет: REST-контракт называет коды только для состояний,
        // которые различает фронт (not_found, ступень 0, незаведённая вики); ошибка
        // вызова — не состояние, она остаётся rejected без кода.
        if (string.IsNullOrWhiteSpace(ownerId))
            return RefusedRecorded(null, "владелец пустой");
        if (string.IsNullOrWhiteSpace(sessionId))
            return RefusedRecorded(null, "id чата пустой");
        // Id чата должен быть представим в ref источника (тот же класс, что проверяет
        // захват): id с спецсимволами не даст ни карточки, ни курсора в сторе.
        if (!IsSessionIdSafe(sessionId))
            return RefusedRecorded(null, $"id чата '{sessionId}' не представим в ссылке источника");

        // Чужой и несуществующий — одно слово на двоих (F1, хвосты ревью): формулировка
        // «чужой» выдавала бы существование чужого чата — та самая утечка, которой здесь
        // нет; различить их может только владелец (он их и не различает).
        var session = _sessions.GetById(sessionId);
        if (session is null || _sessions.ResolveOwnerId(session) != ownerId)
            return RefusedRecorded(CodeNotFound, "чат не существует");

        // Приватный чат (FIN3-3) — то же слово «не существует»: приватный чат виден только
        // автору, а этот разбор — машинный потребитель (кнопка, ночь, wiki_ingest), ему он
        // не виден вовсе (образец — автообучение памяти, TeamMemoryAutolearnService). Отказ
        // неотличим от несуществующего: приватность не выдавала бы существования чата.
        if (session.PrivateToUserId is not null)
            return RefusedRecorded(CodeNotFound, "чат не существует");

        // Ступень 0 (ADR-035 §12а) — исключения кода до обращения к модели.
        if (session.TaskExecution)
            return RefusedRecorded(CodeTaskExecution, "чаты исполнения задач не разбираются: их итоги уже источники");
        if (session.WikiMark == WikiMarks.Skip)
            return RefusedRecorded(CodeMarkedSkip, "чат помечен человеком «не разбирать»");

        // Раздел назначения — дом чата (ADR-036, решение 3). Личный раздел закрыт до Ф2 —
        // тот же отказ, что у очереди заявок (S6b-3), только до чтения истории.
        var home = HomeOf(session);
        if (home == WikiSections.Personal)
            return RefusedRecorded(CodePersonalSection, "личный раздел — с Ф2");

        // F4 (хвосты ревью): разделитель ключа не экранируется — ни одна часть не содержит
        // '|': sessionId проверяется guard'ом выше (IsSessionIdSafe), id пользователя и
        // проекта генерируются Guid (Core/Models/User.cs:8, Core/Models/Project.cs:34),
        // home построен формулой HomeOf по этим id. Коллизия ключа невозможна; префиксный
        // обход IsRunning опирается на это же.
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
            ChatResult result;
            try
            {
                result = await RunProtectedAsync(ownerId, sessionId, home, session, actionKey);
            }
            catch (Exception)
            {
                // F2 (хвосты ревью): защищённый прогон не смог отдать исход — guard не
                // справился сам (оформление причины). Исход — отказ без причины: заявка
                // не висит, исключения наружу нет, e.Message вторично не оформляется.
                result = Refused(null, "разбор чата не завершился");
            }
            try
            {
                // Исход пишется раньше снятия in-flight (S7b0a): analyze-status не должен
                // показать «не идёт» со старым last — законченный разбор фиксируется до
                // того, как чат станет «не идущим».
                lock (_inFlightLock)
                {
                    _last[ownerId + "|" + sessionId] = new LastAnalysis(result, DateTimeOffset.UtcNow);
                    TrimLastLocked();
                }
            }
            finally
            {
                // F2: сняние — безусловно, в finally, и раньше выдачи результата:
                // исключение между прогоном и снянием оставляло запись навсегда — все
                // заявки по ключу висели на незавершённой задаче (вечный 202), а порядок
                // «сняние → выдача» нужен, чтобы опоздавшая заявка начинала новый разбор,
                // а не получала старый результат через неубранную запись (урок S6b-3).
                // Проверка ReferenceEquals: чужая заявка (свой ключ) не снимается.
                lock (_inFlightLock)
                {
                    if (_inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, tcs.Task))
                        _inFlight.Remove(key);
                }
                tcs.TrySetResult(result);
            }
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

    /// <summary>Вернуть курсор разбора чата на from-1, чтобы окно from…N разбиралось заново
    /// (хвост apply_failed: окно закрылось отказанным применением, а курсор остался на его
    /// конце — финал 630b8b12, разрез плана 12554ff8). Ночь берёт чат только при дельте не
    /// меньше MinHumanDelta реплик человека (WikiNightlyService.cs:505): окно меньше этого
    /// ночь не возьёт вовсе — его переразбирает только кнопка «Разобрать». Ручное окно с тем же
    /// ref даёт лишний переразбор: повторные утверждения гасит дедup (S4d), а не курсор.
    ///
    /// Откат — поля самого разбора (cursor/cursorHash/zone/reason): поля скорера и ступени 2
    /// стор переносит из прежней записи сам (M2), streak-поля не меняются: откат — не исход
    /// разбора, прежний исход снимает следующий разбор, а не он. AdvanceCursor («курсор назад
    /// не уходит») не трогается: этот возврат — отдельная намеренная назад-операция вне
    /// разбора, она не про результат окна.
    ///
    /// Условия: живой курсор не меньше from (решающая проверка — по записи, прочитанной
    /// внутри блока стора; предчтение лишь бережёт пустую запись); чат существует, принадлежит
    /// этому владельцу, не приватный и не помечен «не разбирать»; история не короче from-1.
    /// При любом невыполнении — записи нет, причина в лог вызывающего (тот же optional ILogger,
    /// что у стора), исключения наружу нет. Формулировка отказа в логе для чужого/приватного/
    /// удалённого — одна («чат не существует»), как в AnalyzeAsync: лог не выдаёт приватность
    /// или существование чата тому, кто их не видит. Конкурентность (инвариант, MAJOR ревью
    /// a07db75e): откат — последняя запись курсора по этому чату, финальный AdvanceCursor
    /// прогона не может его затереть — поэтому проверка «чат в прогоне» и запись отката
    /// делаются под одним _inFlightLock без await между ними, и прогон, начатый после чтения
    /// истории, не успевает вклиниться между ними. Если прогон уже идёт — откат ждёт его конца
    /// и пишется ровно один раз: порядок снятия (сначала курсор прогона, потом _inFlight)
    /// гарантирует, что после конца прогона его записи уже не будет. true — курсор возвращён;
    /// false — ничего не произошло.</summary>
    public Task<bool> ReturnWindowAsync(string ownerId, string sessionId, int from, ILogger? log = null)
    {
        // Fail-closed входа, как у AnalyzeAsync: кривая заявка не доходит до стора.
        if (string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(sessionId) || from < 1)
            return Task.FromResult(false);
        if (!IsSessionIdSafe(sessionId))
            return Task.FromResult(false);

        // Проверки «чат в прогоне» здесь нет намеренно: здесь она лишь отложила бы откат,
        // но не защитила бы его от прогона, стартующего между проверкой и записью. Пара
        // «проверка + запись» — под одним замком в ReturnWindowCoreAsync (ReturnWindowAttempt).
        return ReturnWindowGuardedAsync(ownerId, sessionId, from, log);
    }

    private async Task<bool> ReturnWindowGuardedAsync(string ownerId, string sessionId, int from, ILogger? log)
    {
        try
        {
            return await ReturnWindowCoreAsync(ownerId, sessionId, from, log);
        }
        catch (Exception e)
        {
            // Тот же порядок, что у защищённого прогона: неожиданный сбой — запись в лог,
            // не исключение вызывающему.
            log?.LogWarning("Возврат окна: курсор не возвращён (внутренний сбой): {Message}", e.Message);
            return false;
        }
    }

    private async Task<bool> ReturnWindowCoreAsync(string ownerId, string sessionId, int from, ILogger? log)
    {
        bool No(string reason)
        {
            log?.LogInformation("Возврат окна: курсор не возвращён ({Reason}): {SessionId}", reason, sessionId);
            return false;
        }

        var session = _sessions.GetById(sessionId);
        if (session is null || _sessions.ResolveOwnerId(session) != ownerId)
            return No("чат не существует");
        if (session.PrivateToUserId is not null)
            return No("чат не существует");
        if (session.WikiMark == WikiMarks.Skip)
            return No("чат помечен «не разбирать»");

        var root = WikiPaths.Root(_dataDir, ownerId);
        if (!WikiPaths.IsInitialized(_dataDir, ownerId))
            return No("вики этого владельца не заведена");

        var history = await _sessions.GetHistoryAsync(sessionId);
        if (history is null || history.Count < from - 1)
            return No($"история короче целевого курсора {from - 1}");

        var back = from - 1;

        // «Проверка в прогоне + предчтение + запись отката» — под одним _inFlightLock, без
        // await между ними (MAJOR ревью a07db75e): раньше проверка стояла в начале метода, а
        // Update — после чтения истории и стора; прогон той же сессии, стартовавший в этом
        // окне, заканчивался своим AdvanceCursor уже ПОСЛЕ отката и затирал его, а метод
        // возвращал true. Предчтение — тоже под замком: при идущем прогоне запись создаёт
        // сам прогон, отказ «записи нет» права не имел бы. Порядок замков безопасен: Get/Update
        // синхронные и берут лишь корневой семафор стора (WikiChatTriageStore.cs:77,121), а
        // прогон пишет свой курсор вне _inFlightLock (финальный Update в RunProtectedAsync)
        // и снимает себя из _inFlight только в её finally — инверсии «замок стора →
        // _inFlightLock» нет, тупика нет.
        (bool Rewound, string? Refusal, Task<ChatResult>? Running) ReturnWindowAttempt()
        {
            lock (_inFlightLock)
            {
                var running = FindRunningLocked(ownerId, sessionId);
                if (running is not null)
                    return (false, null, running);

                // Предчтение: не звать Update, когда возвращать нечего (записи нет или курсор
                // уже под окном) — лишняя запись стора с новой датой была бы «изменением» там,
                // где должно быть «ничего». Решающее условие — внутри Update, по живой записи.
                var read = WikiChatTriageStore.Get(root, log);
                if (read.State == WikiTriageFileState.Invalid)
                    return (false, "стор разбора чатов нечитается", null);
                var entry = read.Entries.FirstOrDefault(e => e.SessionId == sessionId);
                if (entry is null)
                    return (false, "записи разбора этого чата нет", null);
                if (entry.Cursor < from)
                    return (false, $"курсор {entry.Cursor} уже под окном {from}", null);

                var rewound = false;
                var up = WikiChatTriageStore.Update(root, sessionId, cur =>
                {
                    // Решающая проверка — по живой записи блока: между предчтением и блоком параллельный
                    // разбор или удаление могли сдвинуть её (старое прочтение не доказательство).
                    if (cur is null || cur.Cursor < from)
                        // Запись исчезла под блоком: пустая с курсором 0 — та же законная форма, что
                        // у UpdateScoring на несуществующем чате; возвращать нечего.
                        return cur ?? new WikiChatTriageEntry(sessionId, 0, null, null, null, 0, default);
                    rewound = true;
                    return cur with
                    {
                        Cursor = back,
                        // Хэш границы — та же система, что у курсора прогона (from==1 → 0 и null).
                        CursorHash = back == 0 ? null : HashOfRecord(history[back - 1]),
                        Zone = null,
                        Reason = null,
                    };
                }, log);
                if (!up.Ok)
                    return (false, $"откат курсора не записан: {up.Reason}", null);
                if (!rewound)
                    return (false, "живая запись уже под окном (параллельный разбор или удаление)", null);
                return (true, null, null);
            }
        }

        var attempt = ReturnWindowAttempt();
        if (attempt.Running is not null)
        {
            // Чат в прогоне: ждём его задачи и пробуем ровно один раз. Порядок завершения
            // прогона (сначала его курсор и last, снятие из _inFlight — в finally следом)
            // гарантирует: после конца прогона его финального AdvanceCursor уже не будет —
            // откат станет последней записью. Повторный приезд — ответственность вызывающего
            // (T-A2), не этого метода.
            try { await attempt.Running.ConfigureAwait(false); }
            catch (Exception) { /* прогон отдал отказ — попытка отката от этого не теряется */ }
            attempt = ReturnWindowAttempt();
            if (attempt.Running is not null)
                return No("чат снова в прогоне");
        }
        if (attempt.Rewound)
            return true;
        return No(attempt.Refusal!);
    }

    /// <summary>Задача разбора этого чата под _inFlightLock (тот же префикс owner|session|,
    /// что у IsRunning). Вызывать только держа _inFlightLock. null — прогона нет.</summary>
    private Task<ChatResult>? FindRunningLocked(string ownerId, string sessionId)
    {
        var prefix = ownerId + "|" + sessionId + "|";
        foreach (var (key, task) in _inFlight)
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                return task;
        return null;
    }

    /// <summary>Записать последний исход вне прогона (предпрогонный отказ, F3): тот же
    /// ключ и тот же потолок, что у записи прогона.</summary>
    private void RecordLast(string ownerId, string sessionId, ChatResult result)
    {
        lock (_inFlightLock)
        {
            _last[ownerId + "|" + sessionId] = new LastAnalysis(result, DateTimeOffset.UtcNow);
            TrimLastLocked();
        }
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

    private async Task<ChatResult> RunProtectedAsync(string ownerId, string sessionId, string home, Session session,
        string? actionKey)
    {
        try
        {
            return await RunAsync(ownerId, sessionId, home, session, actionKey);
        }
        catch (Exception e)
        {
            // Любой неожиданный сбой — исход с причиной: ни исключения вызывающему,
            // ни «половины разбора» на диске без объяснения.
            return Refused(null, $"внутренний сбой разбора чата: {e.Message}");
        }
    }

    private async Task<ChatResult> RunAsync(string ownerId, string sessionId, string home, Session session,
        string? actionKey)
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
        var awaitingOps = 0;
        string? nothingReason = null;
        // Частичный исход (WC-3): failedReason — причина отказанного окна (null — отказа
        // не было), lastOkTo — конец последнего окна, завершившегося без отказа (1-индекс в
        // истории): курсор частичного исхода встаёт на него, не назад и не за отказанное окно.
        string? failedReason = null;
        var lastOkTo = 0;
        // Входные токены разбора (WC-4a): сумма по всем разобранным окнам, независимо от их
        // исхода — окно, отказанное после ответившего вызова, расход тоже понесло.
        long inputTokens = 0;
        // Операции синтеза, отброшенные поштучным приёмом, по всем окнам (число и первая причина).
        var dropped = 0;
        string? firstDropReason = null;

        // Отказ окна: разобранного ещё нет (ни applied, ни awaiting_human) — отказ всего
        // разбора (курсор на месте, хроника не начинается), серия отказов растёт только если
        // модель ответила (afterModel). Есть разобранное — частичный исход: запомнить причину,
        // цикл дальше не идёт — отказ позднего окна не перечёркивает разобранное (WC-3).
        // Расход и отброшенные операции разобранных до отказа окон остаются в исходе: они
        // были независимо от того, на каком окне разбор оборвался (WC-4a; минор ревью C2R-Sh1).
        ChatResult? WindowFailure(string reason, bool afterModel, int windowTo)
        {
            if (applied.Count > 0 || awaiting > 0) { failedReason = reason; return null; }
            return RecordRejection(root, sessionId, history, reason, afterModel, windowTo, inputTokens,
                dropped, firstDropReason);
        }

        foreach (var window in windows)
        {
            var from = window.Lines[0].Index;
            var to = window.Lines[^1].Index;

            // Карточка окна — захватом (S6h): владение, дом чата, Service-записи —
            // проверяет он, здесь это не дублируется.
            var cap = await _capture.CaptureSession(ownerId, home, sessionId, from, to);
            if (!cap.Ok)
            {
                var refused = WindowFailure($"захват источника m{from}-m{to} не удался: {cap.Reason}", false, to);
                if (refused.HasValue) return refused.Value;
                break;
            }

            var ing = await _ingest.IngestAsync(ownerId, new WikiIngestService.Request(
                WikiGate.OriginSession, $"session:{sessionId}#m{from}-{to}",
                cap.CardRelPath!, WindowText(window), home, null), actionKey: actionKey);
            // Расход окна прибавляется до разбора его исхода: он не зависит от того, чем
            // окно закончилось (applied, nothing_new, отказ после модели) (WC-4a).
            inputTokens += ing.InputTokens;
            dropped += ing.Dropped;
            if (ing.DropReasons is { Count: > 0 } dr) firstDropReason ??= dr[0];
            switch (ing.Outcome)
            {
                case WikiIngestService.OutcomeApplied:
                    if (ing.PackageId is not { Length: > 0 } packageId)
                    {
                        var noId = WindowFailure($"исход «applied» окна m{from}-m{to} без id пакета", ing.InputTokens > 0, to);
                        if (noId.HasValue) return noId.Value;
                        break;
                    }
                    // Заголовок карточки — имя её файла (WriteCard, S6h): цель сносок хроники.
                    applied.Add((packageId, Path.GetFileNameWithoutExtension(cap.CardRelPath!)));
                    break;
                case WikiIngestService.OutcomeAwaitingHuman:
                    awaiting++;
                    awaitingOps += ing.Operations;
                    break;
                case WikiIngestService.OutcomeNothingNew:
                    nothingReason ??= ing.Reason;
                    break;
                default:
                    // Сбой любого окна — при пустом applied отказ всего разбора: курсор не
                    // двигается, хроника не начинается («зависшей» половины хроники не
                    // остаётся); при непустом — частичный исход (WindowFailure, WC-3).
                    var failed = WindowFailure($"разбор окна m{from}-m{to} не удался: {ing.Reason}", ing.InputTokens > 0, to);
                    if (failed.HasValue) return failed.Value;
                    break;
            }
            if (failedReason is not null) break; // частичный исход: последующие окна не разбираются
            lastOkTo = to; // окно завершено без отказа: курсор частичного исхода может стоять на его конце
        }

        if (applied.Count == 0 && awaiting == 0)
        {
            // Все окна сказали «нечего добавить» — штатный исход (ADR-035:269):
            // курсор сдвинут, зона и streak записаны, хроника не создаётся. Серия считается
            // из записи, прочитанной под блокировкой корня; серия отказов обнуляется.
            var up = WikiChatTriageStore.Update(root, sessionId, cur =>
            {
                var (c, h) = AdvanceCursor(cur, history.Count, history);
                return new WikiChatTriageEntry(sessionId, c, h, ZoneNothingNew, nothingReason,
                    (cur?.NothingNewStreak ?? 0) + 1, default, RejectStreak: 0);
            });
            if (!up.Ok)
                return Refused(null, $"исход разбора не записан: {up.Reason}", inputTokens,
                    dropped, firstDropReason);
            return new ChatResult(OutcomeNothingNew, nothingReason, [], InputTokens: inputTokens,
                Dropped: dropped, FirstDropReason: firstDropReason);
        }

        if (applied.Count == 0)
        {
            // Пакеты есть, применённых нет: окно отдано человеку во «Входящие» и разобрано —
            // курсор двигается (иначе ночь разбирала бы его заново и плодила дубль-пакеты).
            // Хроника не пишется: её утверждения — из применённых пакетов. При частичном
            // исходе курсор — на конце последнего окна без отказа.
            var awaitTo = failedReason is null ? history.Count : lastOkTo;
            var upAwait = WikiChatTriageStore.Update(root, sessionId, cur =>
            {
                var (c, h) = AdvanceCursor(cur, awaitTo, history);
                return new WikiChatTriageEntry(sessionId, c, h, ZoneAwaitingHuman,
                    $"{awaitingOps} {OperationsWord(awaitingOps)} на решении", 0, default, RejectStreak: 0);
            });
            if (!upAwait.Ok)
                return Refused(null, $"курсор разбора не записан: {upAwait.Reason}", inputTokens,
                    dropped, firstDropReason);
            return new ChatResult(OutcomeAwaitingHuman,
                "в разборе есть спорные пакеты: их решает человек («Входящие»)", [],
                Partial: failedReason is not null,
                InputTokens: inputTokens, Dropped: dropped, FirstDropReason: firstDropReason);
        }

        var (chronicleOk, chronicleReason) = await AppendChronicleAsync(ownerId, home, session, sessionId, applied);
        if (!chronicleOk)
            return Refused(null, chronicleReason ?? "хроника чата не записана", inputTokens,
                dropped, firstDropReason);

        // Курсор после применения — на конец дельты, с хэшем последней записи; при
        // частичном исходе — на конце последнего успешного окна, с хэшем его записи (та же
        // система, что history.Count/HashOfRecord(history[^1])): он не уходит назад и не
        // прыгает за отказанное окно (WC-3).
        // Зона/причина исхода обнуляются: они принадлежат разбору и отражают его
        // последний исход — применённый разбор означает «новые утверждения были», и его
        // собственный устаревший nothing_new не переживает свой же исход (ревью S6f,
        // доработка 2). Streak обнулён: ряд «нечего добавить» прерван применённым
        // разбором.
        var cursorTo = failedReason is null ? history.Count : lastOkTo;
        var upApplied = WikiChatTriageStore.Update(root, sessionId, cur =>
        {
            var (c, h) = AdvanceCursor(cur, cursorTo, history);
            return new WikiChatTriageEntry(sessionId, c, h, null, null, 0, default, RejectStreak: 0);
        });
        if (!upApplied.Ok)
            return Refused(null, $"курсор разбора не записан: {upApplied.Reason}", inputTokens,
                dropped, firstDropReason);

        var overrodeHuman = 0;
        var disputed = new List<string>();
        foreach (var (packageId, _) in applied)
        {
            var stored = WikiPackageStore.TryRead(root, packageId);
            if (stored.State != WikiPackageFileState.Valid || stored.Package is not { } pkg)
                continue; // сводка — витрина: нечитаемый пакет число не искажает, а пропускается
            var (replaced, pages) = pkg.NightSummary();
            overrodeHuman += replaced;
            foreach (var page in pages)
                if (!disputed.Contains(page, StringComparer.Ordinal))
                    disputed.Add(page);
        }
        // Причина отказа позднего окна остаётся в результате (Partial): применённое не
        // перечёркнуто, но вызывающий видит, что разбор оборвался (WC-3).
        return new ChatResult(OutcomeApplied, failedReason, applied.Select(a => a.PackageId).ToList(),
            OverrodeHuman: overrodeHuman, DisputedPages: disputed, Partial: failedReason is not null,
            InputTokens: inputTokens, Dropped: dropped, FirstDropReason: firstDropReason);
    }

    // ── хроника чата ───────────────────────────────────────────────────────────────

    /// <summary>Склонение «операция» под число: 1 операция, 2–4 операции, 5+ и 11–14 — операций.</summary>
    internal static string OperationsWord(int n)
    {
        var m100 = n % 100;
        if (m100 is >= 11 and <= 14) return "операций";
        return (n % 10) switch { 1 => "операция", 2 or 3 or 4 => "операции", _ => "операций" };
    }

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
    /// Существующая нечитаемая хроника — отказ: её строки уже написанное,
    /// перезаписывать молча нельзя.</summary>
    internal async Task<(bool Ok, string? Reason)> AppendChronicleAsync(
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
                // Форма хроники — как у страницы знаний (ADR-035:276, WikiPlanValidator.RenderApply):
                // строка «Что выяснили» несёт УТВЕРЖДЕНИЕ (Claim, у старого пакета без Quotes —
                // Text), а сноска — дословную цитату (Quotes, у старого пакета — Text). Текст с
                // переводом строки складываем в одну строку: формат страницы строчный (WikiPage).
                var statement = WikiPage.OneLine(op.Claim is { Length: > 0 } ? op.Claim : op.Text);
                IEnumerable<string> candidates = op.Quotes is { Count: > 0 } ? op.Quotes : [op.Text];
                // Мера контракта гейта (8–300, одна с гейтом) — только на сноске: цитату не
                // обрезаем, она обязана остаться дословной, а пропускаем — иначе гейт отвергает
                // все последующие записи раздела. Утверждение без единой годной сноски гейт
                // тоже отвергает, поэтому такая строка не пишется вовсе.
                var quotes = candidates.Select(WikiPage.OneLine)
                    .Where(q => !QuoteMatcher.MeasureQuoteLength(q).OutOfContract)
                    .ToList();
                if (quotes.Count == 0)
                    continue;
                var refs = string.Concat(quotes.Select((_, k) => $"[^s{nextRef + k}]"));
                statements.Add($"{statement} {refs}");
                foreach (var quote in quotes)
                    footnotes.Add(new(new(true, cardTitle), quote));
                nextRef += quotes.Count;
            }
        }
        if (statements.Count == 0)
            return (true, null); // применённых утверждений нет — хронику трогать нечем.

        if (page is null)
        {
            page = new WikiPage
            {
                Title = ChronicleTitle(session, sessionId),
                Frontmatter = new WikiFrontmatter { Type = WikiFrontmatter.TypeChronicle },
                Sections =
                [
                    new() { Name = "О чём", Lines = [] },
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
                // «О чём» не трогается: уже написанные строки — накопленное знание
                // (в том числе легаси-ссылки «Продолжение чата:»); их снимает откат.
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
    /// иначе личный. Одна формула на весь продукт: её же спрашивают список чатов
    /// (WikiChatsController.List), чтобы не второго рубежа построения дома (N1).</summary>
    public static string HomeOf(Session session) =>
        session.ProjectId is { Length: > 0 } projectId
            ? WikiSections.ProjectsPrefix + projectId
            : WikiSections.Personal;

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
    // internal — виден тестовым сборкам (InternalsVisibleTo): тест возврата окна сверяет
    // записанный CursorHash с хэшем границы по той же функции, а не копией алгоритма.
    internal static string HashOfRecord(StoredMessage record)
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

    // Курсор назад не уходит (C2R-Sh2): если запись, прочитанная под блокировкой, уже стоит
    // дальше целевого конца окна и её хэш совпадает с историей (параллельный разбор той же
    // сессии), остаётся она; иначе — целевой курсор с хэшем записи на его конце.
    private static (int Cursor, string? Hash) AdvanceCursor(
        WikiChatTriageEntry? cur, int target, IReadOnlyList<StoredMessage> history)
    {
        if (cur is { Cursor: > 0 } && cur.Cursor > target && cur.Cursor <= history.Count
            && HashOfRecord(history[cur.Cursor - 1]) == cur.CursorHash)
            return (cur.Cursor, cur.CursorHash);
        return (target, HashOfRecord(history[target - 1]));
    }

    // Отказ окна при пустом разобранном: серия RejectStreak растёт, только если модель
    // ответила (afterModel) — отказ до модели (облако ночью, движок занят, нет карточки)
    // серию не наращивает и стор не трогает. На RejectSkipAfter-м отказе подряд курсор
    // переходит через окно (зона rejected_skipped с причиной последнего отказа); вручную чат
    // по-прежнему разбирается пунктом меню чата.
    private ChatResult RecordRejection(string root, string sessionId, IReadOnlyList<StoredMessage> history,
        string reason, bool afterModel, int windowTo, long inputTokens, int dropped, string? firstDropReason)
    {
        if (!afterModel)
            return Refused(null, reason, inputTokens, dropped, firstDropReason);
        var skipped = false;
        var up = WikiChatTriageStore.Update(root, sessionId, cur =>
        {
            var streak = (cur?.RejectStreak ?? 0) + 1;
            skipped = streak >= RejectSkipAfter;
            if (skipped)
            {
                var (c, h) = AdvanceCursor(cur, windowTo, history);
                return new WikiChatTriageEntry(sessionId, c, h, ZoneRejectedSkipped, reason, 0, default,
                    RejectStreak: 0);
            }
            // Поля разбора прежней записи остаются как есть, растёт только серия.
            return (cur ?? new WikiChatTriageEntry(sessionId, 0, null, null, null, 0, default))
                with { RejectStreak = streak };
        });
        if (!up.Ok)
            return Refused(null, $"{reason}; серия отказов не записана: {up.Reason}", inputTokens,
                dropped, firstDropReason);
        return Refused(null, skipped
            ? $"окно пропущено после {RejectSkipAfter} отказов подряд: {reason}"
            : reason, inputTokens, dropped, firstDropReason);
    }

    /// <summary>Отказ разбора: то, что было понесено до отказа (токены вызовов и отброшенные
    /// операции разобранных окон), остаётся в исходе — отказ его не обнуляет (WC-4a; минор
    /// ревью C2R-Sh1).</summary>
    private static ChatResult Refused(string? code, string reason, long inputTokens = 0,
        int dropped = 0, string? firstDropReason = null) =>
        new(OutcomeRejected, reason, [], code, InputTokens: inputTokens,
            Dropped: dropped, FirstDropReason: firstDropReason);
}
