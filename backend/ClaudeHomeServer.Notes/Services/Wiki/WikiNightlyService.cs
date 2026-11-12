using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Ночной разбор чатов (Ф2, N7b, ADR-035 §12): в окне, которое владелец задал в настройках, служба
// проходит по его чатам и разбирает те, где накопилось новое, — тем же WikiChatIngestService, что и
// кнопка «Разобрать сейчас», но с местом wiki-nightly (N7a). Тумблер по умолчанию ВЫКЛ.
//
// Часовой пояс окна (решение шага): WindowStartHour/EndHour — часы СЕРВЕРА по локальному времени
// процесса (DateTimeOffset.Now), как окно линта (WikiLintService). Пояса владельца в модели
// пользователя нет, а заводить его ради ночи значило бы выдумать настройку, которой никто не
// просил. Окно полуоткрытое [start, end); start > end — окно через полночь (21→2). Дата прохода —
// дата НАЧАЛА окна: тик в 01:00 окна 21→2 относится к вчерашней дате, иначе ночь разобралась бы дважды.
//
// Проход владельца: флаг llm-wiki → Nightly.Enabled → окно → (вики не заведена — WikiKeeper.EnsureInitializedAsync,
// единственная запись ночи в незаведённую вики; сбой = error) → пересчёт ступени 1 → очередь
// («важные», затем по UpdatedAt убыв.) → на каждый чат: перечитать тумблер и окно, лимит открытых
// пакетов, дельта ≥ 3 реплик человека после курсора, лимиты раздела, ступень 2 для серых (лениво,
// только когда очередь дошла до чата), бюджет → AnalyzeAsync(…, WikiNightly).
//
// Инварианты и пути к ним (все здесь):
// - Чат исполнения задач не разбирается: ночь берёт только пометку «важный», зону triage и серую
//   зону с «да» ступени 2; TaskExecution отсекается в очереди, AnalyzeAsync отказывает сам,
//   ступень 2 такой чат не вердиктит.
// - skip побеждает балл: чат с пометкой «не разбирать» в очередь не попадает при любом балле.
// - Приватный чат (PrivateToUserId) не разбирается: приватный чат виден только автору, машинные
//   потребители чата его не читают вовсе (FIN3-3; образец — автообучение памяти,
//   TeamMemoryAutolearnService). Очередь отсекает его в BuildQueue (ResolveOwnerId резолвит
//   чат участника на владельца проекта — одного владения для допуска мало), AnalyzeAsync
//   отказывает его как несуществующий (not_found).
// - Отказ ступени 2 → серая зона: серый чат без «да» ночь не берёт, следующая ночь спросит снова.
//   Вердикт «да» устаревает так же: ≥3 новых реплик человека — ступень 2 переспрашивается
//   симметрично «нет», чат берётся только по новому «да».
// - Сбой применения не двигает курсор: ночь курсор НЕ пишет вообще — только AnalyzeAsync; исключение
//   или отказ разбора одного чата ничего не меняет в сторе, ночь идёт к следующему.
// - Выключенный флаг llm-wiki / Enabled=false → пропуск без записи: проверка стоит первой, до
//   единой записи в keeper/ (ни nightly-last.json, ни chat-triage.json, ни _health.md).
// - Тик не роняет хост: тело тика и каждый владелец — под своим catch, наружу из ExecuteAsync
//   исключение не выходит.
//
// Конкурентность:
// - один проход на владельца: флаг «идёт» с ключом по корню вики владельца (не по экземпляру), второй
//   тик пропускает; кнопка и ночь по одному чату — один разбор (single-flight AnalyzeAsync, движок
//   того, кто пришёл первым);
// - ручной запуск (WN-5, «Разобрать ночь сейчас»): тот же проход под тем же замком — параллельно
//   с тиком или другим ручным он не идёт; окно при ручном не проверяется, отметка «за дату уже
//   пройдено» не мешает (человек нажал кнопку — проход состоится); сам ручной проход дату не
//   закрывает: ночной тик той же даты проходит ночь заново (Manual в итоге — признак ручного);
// - PUT enabled=false посреди ночи: тумблер перечитывается перед КАЖДЫМ чатом, остановка до
//   следующего — outcome=disabled_midway;
// - дата прохода живёт в keeper/nightly-last.json, пишется атомарно (temp+Move) и ПОСЛЕ прохода:
//   рестарт ночью не повторит завершённый проход; outcome=error дату не закрывает — следующий тик
//   окна пробует снова; тот же error той же даты не повторяется: если прочитанный до прохода итог
//   этого же дня — error и итог нового прохода тоже error, уведомления нет (успех и иные исходы сообщаются).
//
// Утреннее уведомление (N7c, сводка J-J4): после записи итога владелец получает итог ночи
// человеческими словами — заголовком «Ночной разбор: применено N; судья принял K, отклонил L;
// ждут M; $X» (судья — его собственные числа, не пересказ решений ночи) и названием пути к
// «Журналу»; в теле — сколько пакетов ещё ждёт решения человека и, если судья не ответил, почему.
// Сводку собирает код: модели для сводки нет. Молчание
// ровно в двух случаях: диспетчера нет в этом составе (сборка/тест без шва — итог всё равно в
// файле) и «ничего не разобрано, и ночь обычная» — сказать нечего. Любой иной исход называется
// простым языком (причины — в OutcomeReason). Сбой доставки ничего не отменяет и не рвёт: итог
// к тому моменту уже записан, исключение отправителя не выходит из службы (LogSafe).
//
// Журнал процесса (J-O1): каждый завершённый проход владельца оставляет в журнале ОДНУ строку
// Information «Ночь вики …» — по полям итога, после записи keeper/nightly-last.json; при
// error/open_limit или при отложенных судьёй операциях (J-J4) та же строка повторяется Warning
// (плохая ночь видна по уровню, а не поиском по тексту). Пропущенный проход (дата уже пройдена,
// флаг или тумблер выключены) строки не даёт. Текста знаний в строке нет: claim, цитаты и
// названия страниц в журнал не уезжают — и сырая причина отказа в неё не печатается: в тексте
// причины путь страницы и строка утверждения (ReasonMarker), полная причина остаётся в поле
// FirstRejectReason файла итога и в построчных записях прохода (хвост ревью R-J-O1, minor 1).
//
// Проход облачного судьи (J-J4, точка «а»): в конце прохода, после разбора чатов, под тем же
// замком владельца и до записи nightly-last.json — WikiJudgeSweep.RunAsync. Его счётчики идут в
// тот же итог, ту же строку журнала и то же утреннее уведомление: сводку собирает код, модель
// для сводки не нужна. Замок прохода судьи (J-J3a) сериализует этот вызов с вызовом из очереди
// заявок разбора (точка «б», WikiIngestService): двойного вызова модели на пакет нет — судья
// трогает только нерешённые спорные операции.

// Честные пределы:
// - Токены — ОЦЕНКА EstTokens (символы дельты / 4, плюс потолок входа на каждый вызов ступени 2):
//   по ней ночь решает бюджет ЗАРАНЕЕ, до вызова. Фактический вход NightResult.InputTokens — сумма
//   ChatResult.InputTokens разобранных чатов и входа ступени 2 (WC-4a/WC-4b/WC-4c): её пишут
//   постфактум, решения она не меняет. Чатам отдано ≤ 70 % InputTokenBudget, остаток — запас на
//   ступень 2 и ответы.
// - Пакеты: хроника чата — страница, а не пакет, в ChatResult.PackageIds её нет; считаются только
//   пакеты. У исхода awaiting_human число пакетов неизвестно — он засчитывается как один (нижняя
//   граница). applied в итоге — число применённых пакетов, toInbox — число чатов с пакетами во
//   «Входящих».
// - Лимит пакетов за ночь, упёртый раньше времени, даёт outcome=budget; open_limit — только упор в
//   лимит ОТКРЫТЫХ пакетов (WikiPackage.MaxOpenPackages). Пакеты чата прибавляются пачкой: чат,
//   давший пакеты сверх остатка ночи, — последний в ночи, его пакеты остаются в Хранителе
//   (не откатывать); превышение остатка раздела закрывает только его раздел — его последующие чаты
//   пропускаются, ночь идёт по другим разделам (решение 02.11); при остатке 0 чат не начинается.
// - Облако: ночной разбор и ступень 2 в облако не уходят (решение Андрея 02.11): WikiIngestService
//   (ключ wiki-nightly) и WikiChatStage2 зовут модель через RunLocalOrChosenDetailedAsync (та же
//   цепочка, что RunLocalOrChosenAsync), без финального claude-шага. Падение или недоступность
//   локали — отказ вызова: окно разбора уходит в Rejected и курсор не двигается, ступень 2
//   оставляет чат серым; целая ночь таких отказов (каждый разобранный чат отказан и вход нулевой)
//   — итог error, не completed (WN-2, конец RunPassAsync); три отказа локального маршрута подряд
//   обрывают проход сразу (выключатель 2e13d45c, LocalDownBreakAfter) — ночь при мёртвом движке
//   не сжигает очередь на повтор одного и того же отказа. Фактический вход этих вызовов (usage
//   того же ответа движка) суммируется в NightResult.InputTokens — меры нагрузки, не денег.
//   Кнопка «Разобрать» и агент wiki_ingest не затронуты — облако там допустимо. Проход судьи
//   (J-J4) — другое место (wiki-judge, маршрут которого выбирает админ): он не зависит от того,
//   жива ли локальная модель ночи, и выключатель мёртвого локального маршрута к нему не
//   относится. Ничего в проходе ночи не предполагает единственного локального исполнителя
//   (J-W2): ни имени движка, ни его порта.
public sealed class WikiNightlyService : BackgroundService
{
    public const string OutcomeCompleted = "completed";
    public const string OutcomeBudget = "budget";
    public const string OutcomeOpenLimit = "open_limit";
    public const string OutcomeWindowClosed = "window_closed";
    public const string OutcomeDisabledMidway = "disabled_midway";
    public const string OutcomeError = "error";

    public const string LastRelPath = "keeper/nightly-last.json";

    // Дельта, с которой чат стоит ночного разбора, и порог «ступень 2 переспрашивает».
    public const int MinHumanDelta = 3;
    // Доля InputTokenBudget, отданная разбору чатов (остальное — запас).
    private const double ChatBudgetShare = 0.7;

    /// <summary>Выключатель мёртвого локального маршрута (дефект 2e13d45c, J-O1): три окна
    /// разбора подряд, отказанных потому, что локальной модели нет (причина
    /// WikiIngestService.ReasonLocalUnavailable, InputTokens 0 — модель не звалась), — это не
    /// три несчастливых чата, а один и тот же отказ трижды: дальше по очереди будет то же
    /// самое, и ночь 12.11 сожгла так 41 отказ на проход. Проход обрывается сразу, итог error
    /// (дату не закрывает, следующий тик окна пробует снова — как любой error). Считаются подряд
    /// идущие отказы маршрута места, а не конкретного движка: к ночи может добавиться второй
    /// локальный исполнитель (J-W2).</summary>
    private const int LocalDownBreakAfter = 3;

    /// <summary>Итог прохода — то, что лежит в keeper/nightly-last.json. Date — дата начала окна
    /// (yyyy-MM-dd); Selected — чатов, прошедших отбор и дельту; Analyzed — разборов, вернувших
    /// исход; Applied — применённых пакетов; ToInbox — чатов с пакетами во «Входящих»; EstTokens —
    /// оценка расхода (см. шапку). OverrodeHuman — формулировок человека, которые ИИ заменил за ночь
    /// сам; DisputedPages — страниц, где он сам оспорил утверждение человека (conflict@human, ADR-038, предохранитель 6);
    /// InputTokens (WC-4b/WC-4c) — полный вход (вместе с кэшем): сумма ChatResult.InputTokens всех
    /// разобранных чатов плюс фактический вход ступени 2. Мера нагрузки на GPU локали, а не деньги:
    /// деньги из этого поля не считать, стоимость облачного пути — OneShotUsage.CostUsd. Сумма —
    /// независимо от исхода (отказанный план и отказ ступени 2 понесённого расхода не отменяют);
    /// чат, упавший исключением, в сумму не входит. 0 до WC-4c означало «не измерено», а не
    /// «ничего не потрачено». Rejected (WN-2) — число отказанных разборов (OutcomeRejected) среди
    /// analyzed; FirstRejectReason — текст причины первого отказа (ChatResult.Reason), чтобы
    /// «completed при нулевом входе» имел, чем объясниться. Сбой разбора исключением в Rejected
    /// не идёт. Manual (WN-5) — проход запущен кнопкой вручную: он не закрывает дату, ночной тик
    /// той же даты проходит ночь заново. Dropped — операции синтеза, отброшенные поштучным приёмом,
    /// сумма по разборам; FirstDropReason — причина первой из них.
    /// Поля судьи (J-J4) — итог прохода WikiJudgeSweep, запущенного в конце этого же прохода:
    /// JudgeCalls — сколько вызовов модели состоялось, JudgeCostUsd — их стоимость (то, что взял
    /// облачный судья; единственный случай в итоге, где это деньги, а не нагрузка), JudgeApplied /
    /// JudgeRejected — операции, чей вердикт РЕАЛЬНО записался (принят/отклонён судьёй),
    /// JudgeDeferred — операции, которым засчитана попытка без вердикта (судья не ответил — они
    /// ждут следующего прохода), CodeRejected — отказы, вынесенные кодом без вызова модели. Это
    /// счёт операций, а не пакетов: Applied считает применённые пакеты чатов, JudgeApplied —
    /// операции судьи; судья применяет пакет целиком, поэтому в Applied он не переходит.
    /// Stage2Refused / FirstStage2Refusal (хвост ревью R-J-W0) — сколько раз ступень 2 отказала
    /// серому чату и причина первой из них: без неё нелокальный маршрут места молча оставлял
    /// чаты серыми весь проход. В файле итога поля аддитивны: старый итог (без поля)
    /// читается нулями, старый Manual — false.</summary>
    public sealed record NightResult(
        string Date, int Selected, int Analyzed, int Applied, int ToInbox, long EstTokens, string Outcome,
        int OverrodeHuman = 0, int DisputedPages = 0, long InputTokens = 0,
        int Rejected = 0, string? FirstRejectReason = null, bool Manual = false,
        int Dropped = 0, string? FirstDropReason = null,
        int JudgeCalls = 0, decimal JudgeCostUsd = 0m, int JudgeApplied = 0, int JudgeRejected = 0,
        int JudgeDeferred = 0, int CodeRejected = 0,
        int Stage2Refused = 0, string? FirstStage2Refusal = null);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    // Флаг «проход идёт» — по корню вики владельца, общий на процесс: два экземпляра службы на
    // одном каталоге (тест, рестарт) не пройдут одного владельца параллельно.
    private static readonly ConcurrentDictionary<string, byte> Running = new();

    // Сигнал о нечитаемом settings.json — раз в сутки на владельца, а не каждый тик окна.
    private readonly ConcurrentDictionary<string, DateOnly> _invalidSignaled = new();

    private readonly ISessionDirectory _sessions;
    private readonly WikiChatRecalc _recalc;
    private readonly WikiChatStage2 _stage2;
    private readonly ICheapTextRunner _llm;
    private readonly IConfiguration _config;
    private readonly Composition.IFeatureFlagGate? _flags;
    private readonly Composition.INotificationSender? _notifications;
    private readonly WikiKeeper? _keeper;
    private readonly ILogger<WikiNightlyService> _log;
    private readonly Func<string, string, CancellationToken, Task<WikiChatIngestService.ChatResult>> _analyze;
    // Проход судьи (J-J4, точка «а»): та же форма шва, что у _analyze — точка запуска проверяется
    // без настоящей облачной модели. null (тест без судьи) — прохода судьи нет, поля итога нулевые.
    private readonly Func<string, string, CancellationToken, Task<WikiJudgeSweep.JudgeSweepResult>>? _judge;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _dataDir;

    public WikiNightlyService(ISessionDirectory sessions, WikiChatRecalc recalc, WikiChatStage2 stage2,
        WikiChatIngestService ingest, WikiKeeper keeper, WikiJudgeSweep judge, ICheapTextRunner llm,
        IConfiguration config, ILogger<WikiNightlyService> log, Composition.IFeatureFlagGate? flags = null,
        Composition.INotificationSender? notifications = null)
        : this(sessions, recalc, stage2,
            // actionKey — только константа места: внешнего ввода у ночи нет.
            (owner, chat, ct) => ingest.AnalyzeAsync(owner, chat, ct, LocalActionCatalog.WikiNightly),
            llm, config, log, flags, clock: null, notifications: notifications, keeper: keeper,
            judge: (owner, root, ct) => judge.RunAsync(owner, root, ct))
    {
    }

    // Шов теста: разбор, судья и часы подменяются (ограничения проверяются без настоящей модели, а
    // окно — без ожидания ночи). В приложении сюда попадает только цепочка из публичного
    // конструктора. notifications и keeper — после clock: существующие вызовы шва (позиционные)
    // не сдвигаются. keeper == null (тест без шва заведения) — ночь не заводит вики и берёт только
    // заведённых.
    internal WikiNightlyService(ISessionDirectory sessions, WikiChatRecalc recalc, WikiChatStage2 stage2,
        Func<string, string, CancellationToken, Task<WikiChatIngestService.ChatResult>> analyze,
        ICheapTextRunner llm, IConfiguration config, ILogger<WikiNightlyService> log,
        Composition.IFeatureFlagGate? flags, Func<DateTimeOffset>? clock,
        Composition.INotificationSender? notifications = null, WikiKeeper? keeper = null,
        Func<string, string, CancellationToken, Task<WikiJudgeSweep.JudgeSweepResult>>? judge = null)
    {
        _keeper = keeper;
        _sessions = sessions;
        _recalc = recalc;
        _stage2 = stage2;
        _analyze = analyze;
        _judge = judge;
        _llm = llm;
        _config = config;
        _flags = flags;
        _notifications = notifications;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.Now);
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    // ─── Тик ──────────────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(CheckInterval, ct); }
            catch (OperationCanceledException) { break; }
            // Всё тело тика — в RunNightTickAsync под своим catch: отсюда исключение наружу не выходит.
            await RunNightTickAsync(_clock(), ct);
        }
    }

    private TimeSpan CheckInterval =>
        TimeSpan.FromMinutes(int.TryParse(_config["Wiki:NightlyCheckIntervalMinutes"], out var m) && m >= 1 ? m : 30);

    // Тело тика — отдельный internal-метод (образец WikiLintService.RunNightTickAsync): время
    // приходит параметром, тест проверяет тик временем в окне, не дожидаясь ночи. Каждый владелец —
    // под своей охраной: отказ одного не срывает остальных.
    internal async Task RunNightTickAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        try
        {
            foreach (var ownerId in EnumerateWikiOwners())
            {
                ct.ThrowIfCancellationRequested();
                await RunOwnerGuardedAsync(ownerId, now, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Остановка хоста: незавершённый проход файла итога не пишет, следующий запуск начнёт заново.
        }
        catch (Exception ex)
        {
            LogSafe(ex, "Тик ночного разбора вики");
        }
    }

    // Владельцы: подкаталоги data/wiki/ с pages/_schema.md (как у линта) ИЛИ с валидными настройками
    // и включённым тумблером ночи — вики такого владельца ночь заведёт сама (RunOwnerAsync). Чтение
    // настроек ничего не пишет.
    private List<string> EnumerateWikiOwners()
    {
        var wikiRoot = Path.Combine(_dataDir, WikiPaths.DirName);
        if (!Directory.Exists(wikiRoot)) return [];
        try
        {
            return Directory.EnumerateDirectories(wikiRoot)
                .Select(d => Path.GetFileName(d))
                .Where(id => !string.IsNullOrWhiteSpace(id)
                    && (WikiPaths.IsInitialized(_dataDir, id) || WantsAutoInit(id)))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // Незаведённый владелец с включённой ночью: настройки читаются и валидны, Enabled=true.
    private bool WantsAutoInit(string ownerId)
    {
        if (_keeper is null) return false;
        var (settings, invalid) = WikiSettingsStore.Read(WikiPaths.Root(_dataDir, ownerId));
        return !invalid && settings.NightlyOrDefaults().Enabled;
    }

    private async Task RunOwnerGuardedAsync(string ownerId, DateTimeOffset now, CancellationToken ct)
    {
        try { await RunOwnerAsync(ownerId, now, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { LogSafe(ex, "Ночной разбор вики {Owner}", ownerId); }
    }

    // Логирование не может стать новым источником исключения: отказ журнала вышел бы из охраны
    // и (из catch тика) погасил бы BackgroundService, а с ним по умолчанию весь хост.
    private void LogSafe(Exception ex, string template, params object?[] args)
    {
        try { _log.LogWarning(ex, template, args); }
        catch { /* журнал не пишет: сообщить об этом нечем */ }
    }

    private void LogInfoSafe(string template, params object?[] args)
    {
        try { _log.LogInformation(template, args); }
        catch { /* см. LogSafe */ }
    }

    private void LogWarnSafe(string template, params object?[] args)
    {
        try { _log.LogWarning(template, args); }
        catch { /* см. LogSafe */ }
    }

    /// <summary>Одна строка итога прохода в журнале процесса (J-O1): структурная, по полям
    /// NightResult — та же форма, что лежит в keeper/nightly-last.json, только доступная по
    /// журналу. Текста знаний в ней нет: claim, цитаты и названия страниц в журнал не уезжают, и
    /// сырая причина отказа в строку не печатается (ReasonMarker). Счётчики судьи (J-J4) — в той
    /// же строке: сводку собирает код, отдельной модели для сводки нет. При плохом исходе или при
    /// отложенных судьёй операциях строка повторяется Warning: такая ночь должна быть видна по
    /// уровню, а не поиском по тексту. Исключение журнала проход не рвёт (см. LogSafe).</summary>
    private void LogNightSummary(string ownerId, NightResult r)
    {
        // Часть о причине — только при отказах (хвост ревью R-J-O1, minor 4): «отказов 0 (первый: )»
        // читалось как недописанное поле.
        var template = new StringBuilder("Ночь вики {OwnerId} {Date}: {Outcome}; чатов {Selected}/{Analyzed}, отказов {Rejected}");
        var args = new List<object?> { ownerId, r.Date, r.Outcome, r.Selected, r.Analyzed, r.Rejected };
        if (r.Rejected > 0)
        {
            template.Append(" (первый: {RejectFirst})");
            args.Add(ReasonMarker(r.FirstRejectReason));
        }
        // Ступень 2 (хвост ревью R-J-W0): без этой части нелокальный маршрут места молча
        // оставлял чаты серыми весь проход — в журнале не было ни строки о причине.
        if (r.Stage2Refused > 0)
        {
            template.Append("; ступень 2 отказана {Stage2Refused} (первая: {Stage2First})");
            args.Add(r.Stage2Refused);
            args.Add(ReasonMarker(r.FirstStage2Refusal));
        }
        // Судья (J-J4): принятые/отклонённые/отложенные операции и их стоимость — те же числа,
        // что в файле итога и в утреннем уведомлении.
        template.Append("; пакетов {Applied}, на судье {JudgeApplied}/{JudgeRejected}/{JudgeDeferred}, ")
            .Append("отклонено кодом {CodeRejected}; вход {InputTokens} ток; судья {JudgeCalls} выз. ${JudgeCost}; ручной {Manual}");
        args.Add(r.Applied);
        args.Add(r.JudgeApplied);
        args.Add(r.JudgeRejected);
        args.Add(r.JudgeDeferred);
        args.Add(r.CodeRejected);
        args.Add(r.InputTokens);
        args.Add(r.JudgeCalls);
        args.Add(CostText(r.JudgeCostUsd));
        args.Add(r.Manual);

        var line = template.ToString();
        object?[] values = [.. args];
        LogInfoSafe(line, values);
        if (r.Outcome is OutcomeError or OutcomeOpenLimit || r.JudgeDeferred > 0) LogWarnSafe(line, values);
    }

    /// <summary>Потолок длины маркера причины в строке итога: строка — указатель, а не пересказ
    /// отказа (полная причина — в файле итога и в построчных записях прохода).</summary>
    private const int MaxReasonMarkerChars = 160;

    /// <summary>Путь страницы вики в тексте причины: токен с «.md» — его ставят WikiGate.cs:241
    /// («{путь}: утверждение в … без сноски: {строка}») и WikiPlanValidator.cs:315 («повтор:
    /// утверждение уже на странице «{путь}»»).</summary>
    private static readonly Regex PagePathTokenRx = new(@"[^\s«»""'()]*\.md",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Короткий маркер причины для строки итога (хвост ревью R-J-O1, minor 1): сырая
    /// причина отказа окна несёт путь страницы и — сразу за ним — текст утверждения, а строка
    /// журнала уезжает в SigNoz. Правило: всё, что начинается с пути страницы, вырезается целиком
    /// вместе с тем, что идёт за ним (в обеих известных формах текст знаний стоит за путём), плюс
    /// потолок длины. Префикс причины («в разделе есть нарушения», «локальная модель недоступна»,
    /// «у места нелокальный маршрут») остаётся — он и есть то, зачем строка. Если кроме пути в
    /// причине ничего не осталось — маркер отсылает к полной причине файла итога: выдумывать
    /// причину по тексту причины нельзя.</summary>
    private static string ReasonMarker(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "причина не названа";
        var path = PagePathTokenRx.Match(reason);
        var text = path.Success ? reason[..path.Index] : reason;
        text = text.TrimEnd(' ', ':', '«', '»', '"', '\'', '(', ')');
        if (text.Length > MaxReasonMarkerChars) text = text[..MaxReasonMarkerChars];
        return text.Length == 0 ? "полная причина — в файле итога" : text;
    }

    /// <summary>Стоимость судьи для строки и уведомления: инвариантный формат, разделитель —
    /// точка (та же запись, что в JSON).</summary>
    private static string CostText(decimal cost) => cost.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ─── Ручной запуск (WN-5) ────────────────────────────────────────────────

    /// <summary>Исход запроса ручного прохода (WN-5): Started — проход начат; LlmWikiOff — функция
    /// выключена; Disabled — тумблер ночи выключен; Invalid — настройки нечитаемы; AlreadyRunning —
    /// этот владелец уже проходит (тот же замок, что у ночного тика).</summary>
    public enum ManualStart { Started, LlmWikiOff, Disabled, Invalid, AlreadyRunning }

    // CTS ручных проходов: они живут вне ExecuteAsync — StopAsync службы гасит их; проход сам ловит
    // своё отключение и снимает замок в finally.
    private readonly ConcurrentDictionary<CancellationTokenSource, byte> _manualStops = new();

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var cts in _manualStops.Keys)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { /* проход уже закончился */ }
        }
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Идёт ли проход этой вики — флаг, общий на процесс (тот же ключ, что у замка
    /// службы): настройки читают его, чтобы показать состояние кнопки «Разобрать сейчас».</summary>
    public static bool IsRunning(string root) => Running.ContainsKey(root);

    /// <summary>Ручной запуск ночного разбора (кнопка «Разобрать сейчас», WN-5): тот же
    /// RunOwnerPassAsync, что и у ночного тика, но окно не проверяется и отметка «за дату уже
    /// пройдено» не мешает. Флаг и тумблер проверяются до замка: причина отказа называется, не
    /// трогая его; замок берётся синхронно, до возврата: по ответу вызывающий знает наверняка,
    /// начал он проход или владелец уже занят. Дата ручного прохода — сегодняшняя (окна нет,
    /// дата которого была бы началом прохода). Проход — Task.Run со своим CTS (гасится StopAsync),
    /// всё под catch: наружу вызывающему исключение не выходит; finally снимает замок.</summary>
    public ManualStart TryStartManual(string ownerId) => StartManual(ownerId, out _);

    // Перегрузка для теста: отдаёт Task прохода — тест ждёт его завершения и проверяет снятие
    // замка без настоящей модели.
    internal ManualStart TryStartManual(string ownerId, out Task pass) => StartManual(ownerId, out pass);

    private ManualStart StartManual(string ownerId, out Task pass)
    {
        pass = Task.CompletedTask;
        // Порядок отказов — как у тика: сначала флаг, потом тумблер, потом нечитаемые настройки —
        // и всё до замка: отказ не должен выглядеть как «уже занято».
        if (_flags?.IsEnabled(ownerId, FeatureFlagKeys.LlmWiki) != true) return ManualStart.LlmWikiOff;
        var root = WikiPaths.Root(_dataDir, ownerId);
        var (settings, invalid) = WikiSettingsStore.Read(root);
        if (invalid) return ManualStart.Invalid;
        if (!settings.NightlyOrDefaults().Enabled) return ManualStart.Disabled;
        if (!Running.TryAdd(root, 0)) return ManualStart.AlreadyRunning; // этот владелец уже проходит

        var date = DateOnly.FromDateTime(_clock().Date);
        var initialized = WikiPaths.IsInitialized(_dataDir, ownerId);
        var cts = new CancellationTokenSource();
        _manualStops[cts] = 0;
        pass = Task.Run(async () =>
        {
            try
            {
                await RunOwnerPassAsync(ownerId, root, date, initialized, manual: true, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Ручной проход — не исключение вызывающему: 202 уже дан, исход и сбой — в журнале.
                LogSafe(ex, "Ручной ночной разбор вики {Owner}", ownerId);
            }
            finally
            {
                Running.TryRemove(root, out _);
                _manualStops.TryRemove(cts, out _);
                cts.Dispose();
            }
        });
        return ManualStart.Started;
    }

    // ─── Проход владельца ─────────────────────────────────────────────────────

    private async Task RunOwnerAsync(string ownerId, DateTimeOffset now, CancellationToken ct)
    {
        // Одна проверка флага, в одной точке и до любой записи: выключенный флаг — ни одного файла.
        if (_flags?.IsEnabled(ownerId, FeatureFlagKeys.LlmWiki) != true) return;

        var root = WikiPaths.Root(_dataDir, ownerId);
        var (settings, invalid) = WikiSettingsStore.Read(root);
        var initialized = WikiPaths.IsInitialized(_dataDir, ownerId);
        if (invalid)
        {
            // У незаведённой вики сигнал создал бы pages/ мимо EnsureInitializedAsync: пропуск без записи.
            if (!initialized) return;
            // Нечитаемый файл — умолчания (ночь выключена) и строка в _health.md тем же сигналом,
            // что у здоровья классов; разбора нет.
            var today = DateOnly.FromDateTime(now.Date);
            if (_invalidSignaled.TryGetValue(ownerId, out var signaled) && signaled == today) return;
            _invalidSignaled[ownerId] = today;
            WikiClassHealth.SignalInvalidSettings(root);
            return;
        }
        var cfg = settings.NightlyOrDefaults();
        if (!cfg.Enabled) return;
        if (!InWindow(now.Hour, cfg.WindowStartHour, cfg.WindowEndHour)) return;
        if (!initialized && _keeper is null) return;

        var date = WindowDate(now, cfg.WindowStartHour, cfg.WindowEndHour);
        var key = root;
        if (!Running.TryAdd(key, 0)) return; // этого владельца уже проходит другой тик
        try
        {
            await RunOwnerPassAsync(ownerId, root, date, initialized, manual: false, ct);
        }
        finally
        {
            Running.TryRemove(key, out _);
        }
    }

    // Тело прохода под замком владельца (WN-5): общее у ночного тика и ручного запуска.
    // Ручной не встречает отметку «за дату уже пройдено» — человек нажал кнопку, проход состоится.
    // Тик пропускается, если итог той же даты не error и не ручной: ручной проход дату НЕ закрывает,
    // ночной тик той же даты проходит ночь заново.
    private async Task RunOwnerPassAsync(string ownerId, string root, DateOnly date, bool initialized, bool manual, CancellationToken ct)
    {
        var last = ReadLast(root);
        if (!manual && last is not null && last.Date == FormatDate(date) && last.Outcome != OutcomeError && !last.Manual)
            return; // эта ночь уже пройдена (в том числе до рестарта)

        // Прочитанный до прохода итог этого же дня — error: о неудаче ночи владельцу уже
        // сказано. Проход повторяется (error дату не закрывает), но то же предупреждение
        // второй раз за дату не отправляется.
        var errorAlreadyNotified = last is not null && last.Date == FormatDate(date) && last.Outcome == OutcomeError;

        NightResult result;
        if (!initialized && !await TryEnsureInitializedAsync(ownerId))
            result = new NightResult(FormatDate(date), 0, 0, 0, 0, 0, OutcomeError, Manual: manual);
        else
            result = await RunPassAsync(ownerId, root, date, manual, ct);
        // Дата прохода — ПОСЛЕ прохода. Сбой записи итога не отменяет проход (его разборы
        // уже в сторах): следующий тик найдёт курсоры на концах и не разберёт ничего лишнего.
        try { WriteLast(root, result); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSafe(ex, "Итог ночного разбора {Owner} не записан", ownerId);
        }
        // Журнал процесса (J-O1): после записи итога на диске — одна строка на проход владельца
        // (см. LogNightSummary). Пропущенный проход (дата уже пройдена, флаг/тумблер выключены)
        // строки не даёт: в журнале живут только состоявшиеся проходы.
        LogNightSummary(ownerId, result);
        // Уведомление — ПОСЛЕ записи итога: даже если доставка откажет, итог уже на диске
        // и в следующем тике читается настройками. Сбой отправителя остаёт в TryNotifyAsync.
        // Молчим только о повторном error той же даты; успех после error и любой иной исход — сообщаем.
        if (!(errorAlreadyNotified && result.Outcome == OutcomeError)) await TryNotifyAsync(ownerId, result);
    }

    // Единственная точка, где ночь пишет в незаведённую вики: идемпотентный EnsureInitializedAsync
    // (под замком владельца, с перепроверкой). Сбой — исход error: дату не закрывает, владелец уведомлён.
    private async Task<bool> TryEnsureInitializedAsync(string ownerId)
    {
        try
        {
            await _keeper!.EnsureInitializedAsync(ownerId).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSafe(ex, "Ночной разбор {Owner}: вики не заведена", ownerId);
            return false;
        }
    }

    // Окно полуоткрытое [start, end); start > end — через полночь; start == end — окна нет.
    internal static bool InWindow(int hour, int start, int end)
    {
        if (start == end) return false;
        return start < end ? hour >= start && hour < end : hour >= start || hour < end;
    }

    // Дата прохода — дата начала окна: в части окна после полуночи это вчерашняя дата.
    internal static DateOnly WindowDate(DateTimeOffset now, int start, int end) =>
        start > end && now.Hour < end
            ? DateOnly.FromDateTime(now.Date).AddDays(-1)
            : DateOnly.FromDateTime(now.Date);

    private static string FormatDate(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private sealed record Candidate(Session Session, WikiChatTriageEntry? Entry, bool Important, string Home);

    private async Task<NightResult> RunPassAsync(string ownerId, string root, DateOnly date, bool manual, CancellationToken ct)
    {
        int selected = 0, analyzed = 0, applied = 0, toInbox = 0, packagesNight = 0, overrodeHuman = 0, rejected = 0;
        string? firstRejectReason = null;
        // Ряд отказов локального маршрута подряд (выключатель 2e13d45c, см. LocalDownBreakAfter):
        // любой иной исход разбора ряд обнуляет.
        var localDownStreak = 0;
        var dropped = 0;
        string? firstDropReason = null;
        // Отказы ступени 2 (хвост ревью R-J-W0): раньше их не было ни в журнале, ни в итоге.
        var stage2Refused = 0;
        string? firstStage2Refusal = null;
        // Судья (J-J4): нули до её прохода — и для ранних выходов, где прохода судья не было.
        int judgeCalls = 0, judgeApplied = 0, judgeRejected = 0, judgeDeferred = 0, codeRejected = 0;
        decimal judgeCost = 0m;
        var disputedPages = new HashSet<string>(StringComparer.Ordinal);
        long est = 0, inputTokens = 0;
        var outcome = OutcomeCompleted;
        try
        {
            var recalc = await _recalc.RecalculateAsync(ownerId);
            if (!recalc.Ok)
            {
                LogInfoSafe("Ночной разбор {Owner}: пересчёт отбора не удался ({Code}), ночь не начата", ownerId, recalc.Code);
                return Result(OutcomeError);
            }
            var read = WikiChatTriageStore.Get(root);
            if (read.State == WikiTriageFileState.Invalid) return Result(OutcomeError);
            var entries = read.Entries.ToDictionary(e => e.SessionId, StringComparer.Ordinal);

            var queue = BuildQueue(ownerId, entries);
            var cfg = WikiSettingsStore.Read(root).Settings.NightlyOrDefaults();
            var chatCap = (long)(cfg.InputTokenBudget * ChatBudgetShare);
            var chatsBySection = new Dictionary<string, int>(StringComparer.Ordinal);
            var packagesBySection = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var c in queue)
            {
                ct.ThrowIfCancellationRequested();
                // Лимит пакетов за ночь: при остатке 0 чат не начинается.
                var (stop, cfgNow) = CheckStop(ownerId, root, date, manual);
                if (stop is not null) { outcome = stop; break; }
                if (packagesNight >= cfgNow!.MaxPackagesPerNight) { outcome = OutcomeBudget; break; }

                if (chatsBySection.GetValueOrDefault(c.Home) >= cfgNow.MaxChatsPerSection) continue;
                if (packagesBySection.GetValueOrDefault(c.Home) >= cfgNow.MaxPackagesPerSection) continue;

                var history = await _sessions.GetHistoryAsync(c.Session.Id);
                var lines = WikiChatSlicer.Lines(history, 1, history.Count);
                var cursor = c.Entry?.Cursor ?? 0;
                if (cursor > history.Count) cursor = 0; // история переписана — курсор в 0, как у разбора
                var delta = lines.Where(l => l.Index > cursor).ToList();
                if (delta.Count(l => l.IsHuman) < MinHumanDelta) continue;

                // Серый чат — только с «да» ступени 2. Спрашиваем лениво: очередь дошла до чата.
                if (!c.Important && c.Entry?.Stage1Zone == WikiChatScorer.ZoneGray)
                {
                    var verdict = c.Entry.Stage2Zone;
                    var humanTotal = lines.Count(l => l.IsHuman);
                    var stale = humanTotal - (c.Entry.Stage2HistoryCount ?? 0) >= MinHumanDelta;
                    // Свежий отказ — чат отложен до следующей ночи без переспроса. Неопрешённый
                    // вердикт и устаревший (≥3 новых реплик человека) переспрашиваются одинаково —
                    // «да» так же, как «нет»: чат берётся только по новому решению.
                    if (verdict == WikiChatScorer.ZoneSkipped && !stale) continue;
                    if (verdict != WikiChatScorer.ZoneTriage || stale)
                    {
                        var judged = await _stage2.JudgeAsync(ownerId, c.Session.Id, ct);
                        if (judged.Outcome != WikiChatStage2.Outcome.NotApplicable)
                        {
                            est += (WikiChatStage2.MaxInputChars + WikiChatStage2.PromptHeader.Length) / 4;
                            // Фактический вход ступени 2 (WC-4c): метрики того же ответа модели,
                            // по которому вынесен вердикт. Считается по факту вызова: отказ
                            // ступени 2 понесённого расхода не отменяет (тот же смысл, что у
                            // ChatResult). NotApplicable — модель не звалась, расхода нет.
                            inputTokens += judged.InputTokens;
                        }
                        // Отказ ступени 2 → серая зона: чат отложен до следующей ночи. Он назван
                        // построчно и держится в итоге (хвост ревью R-J-W0): без этого админ,
                        // поставивший место на облачную модель, видел только «чаты серые».
                        // NotApplicable (чата нет в серой зоне, пометка человека) — не отказ: его
                        // в счётчик не берём.
                        if (judged.Outcome == WikiChatStage2.Outcome.Refused)
                        {
                            stage2Refused++;
                            firstStage2Refusal ??= judged.Detail;
                            LogInfoSafe("Ночной разбор {Owner}: чат {Chat} ступень 2 отказана: {Reason}",
                                ownerId, c.Session.Id, judged.Detail ?? "причина не названа");
                        }
                        if (judged.Outcome != WikiChatStage2.Outcome.Judged
                            || judged.Zone != WikiChatScorer.ZoneTriage) continue;
                    }
                }

                selected++;
                var chatEst = (delta.Sum(l => (long)l.Text.Length) + 3) / 4;
                if (chatEst > chatCap)
                {
                    LogInfoSafe("Ночной разбор {Owner}: дельта чата {Chat} (~{Tokens} ток.) не помещается в бюджет, чат пропущен",
                        ownerId, c.Session.Id, chatEst);
                    continue;
                }
                if (est + chatEst > chatCap) { outcome = OutcomeBudget; break; }

                chatsBySection[c.Home] = chatsBySection.GetValueOrDefault(c.Home) + 1;
                est += chatEst;
                WikiChatIngestService.ChatResult r;
                try { r = await _analyze(ownerId, c.Session.Id, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // Сбой одного разбора курсор не двигает (его пишет только AnalyzeAsync) — к следующему.
                    LogSafe(ex, "Ночной разбор {Owner}: чат {Chat} не разобран", ownerId, c.Session.Id);
                    continue;
                }
                analyzed++;
                // Фактический вход (WC-4b): сумма по всем разобранным чатам независимо от исхода —
                // отказанный план понесённый расход не отменяет (тот же смысл, что у ChatResult).
                // Чат, упавший исключением выше, до analyzed++ не доходит: его токенов нет.
                inputTokens += r.InputTokens;
                dropped += r.Dropped;
                firstDropReason ??= r.FirstDropReason;
                // Отказанные разборы (WN-2): считаются и называются в журнале — без них ночь из
                // 100 отказов выглядела completed и не оставила ни строки (отчёт 79e98305, Ш2).
                if (r.Outcome == WikiChatIngestService.OutcomeRejected)
                {
                    rejected++;
                    firstRejectReason ??= r.Reason;
                    LogInfoSafe("Ночной разбор {Owner}: чат {Chat} отказан: {Reason}", ownerId, c.Session.Id, r.Reason);
                    // Выключатель мёртвого локального маршрута (2e13d45c, см. LocalDownBreakAfter):
                    // продолжать не за чем — каждый следующий чат дал бы тот же отказ с тем же
                    // нулевым входом. Итог — error с причиной первого из ряда (firstRejectReason
                    // уже назван); error дату не закрывает, следующий тик окна пробует снова.
                    localDownStreak = IsLocalDownRejection(r) ? localDownStreak + 1 : 0;
                    if (localDownStreak >= LocalDownBreakAfter)
                    {
                        LogInfoSafe("Ночной разбор {Owner}: {Reason} — {Streak} отказов подряд, проход остановлен",
                            ownerId, WikiIngestService.ReasonLocalUnavailable, localDownStreak);
                        outcome = OutcomeError;
                        break;
                    }
                }
                else
                {
                    // Любой несостоявшийся этим отказом разбор ряд обнуляет: выключатель считает
                    // подряд идущие, а не всего ночи.
                    localDownStreak = 0;
                }
                var made = r.Outcome switch
                {
                    // Частичный исход (Partial, WC-3) — тоже applied: его применённые пакеты
                    // считаются здесь же, отказ позднего окна их не перечёркивает.
                    WikiChatIngestService.OutcomeApplied => r.PackageIds.Count,
                    WikiChatIngestService.OutcomeAwaitingHuman => 1, // число неизвестно: нижняя граница
                    _ => 0,
                };
                if (r.Outcome == WikiChatIngestService.OutcomeApplied)
                {
                    applied += made;
                    overrodeHuman += r.OverrodeHuman;
                    // Страница, оспоренная в двух чатах за ночь, — одна страница: счёт по множеству путей.
                    foreach (var p in r.DisputedPages ?? []) disputedPages.Add(p);
                }
                else if (r.Outcome == WikiChatIngestService.OutcomeAwaitingHuman) toInbox++;
                packagesNight += made;
                packagesBySection[c.Home] = packagesBySection.GetValueOrDefault(c.Home) + made;
                // Пакеты чата прибавляются пачкой и уже лежат в Хранителе — их не откатывать.
                // Превышение остатка этим чатом допустимо. Лимит ночи — конец ночи: outcome=budget,
                // дальше не идти. Лимит раздела закрывает только его раздел: счётчик раздела уже
                // выше лимита, его последующие чаты отсекает предпроверка (как раздел с нулевым
                // остатком), ночь продолжает по другим разделам, итог остаётся completed.
                if (packagesNight > cfgNow.MaxPackagesPerNight) { outcome = OutcomeBudget; break; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogSafe(ex, "Ночной разбор {Owner}: проход прерван", ownerId);
            outcome = OutcomeError;
        }
        // Инвариант «нулевой вход при всех отказах ≠ completed» (WN-2): когда каждый состоявшийся
        // разбор отказан и ни один токен не дошёл до модели, ночь не «завершена» — она не удалась:
        // error дату не закрывает, следующий тик окна пробует снова (повтор и уведомление — выше,
        // их не меняем). Ветки budget/open_limit/window_closed/disabled_midway сюда не доходят:
        // их итог назван своей причиной. Сбой разбора исключением в rejected не идёт (catch выше
        // analyzed++) и инварианту не мешает: без отказа он не был бы и разобранным.
        if (outcome == OutcomeCompleted && analyzed > 0 && rejected == analyzed && inputTokens == 0)
        {
            LogInfoSafe("Ночной разбор {Owner}: локальная модель не ответила — отказаны все {Analyzed} чатов с нулевым входом",
                ownerId, analyzed);
            outcome = OutcomeError;
        }

        // Точка запуска (а) (J-J4): проход судьи после разбора чатов — под тем же замком владельца
        // (его держит RunOwnerAsync/StartManual вокруг RunOwnerPassAsync) и до записи
        // keeper/nightly-last.json: его счётчики попадают в тот же файл, ту же строку журнала и то
        // же утреннее уведомление. Ранние выходы прохода (пересчёт отбора, нечитаемый стор) идут
        // изнутри try и сюда не доходят: там вики не начала проход. Вызов сериализуется замком
        // самого прохода судьи (J-J3a) с его точкой (б) в очереди заявок разбора: двойного вызова
        // модели на пакет нет — судья берёт только нерешённые спорные операции. Мёртвый локальный
        // маршрут ночи к нему не относится: это другое место (wiki-judge), его выбирает админ.
        if (_judge is not null)
        {
            var sweep = await _judge(ownerId, root, ct).ConfigureAwait(false);
            judgeCalls = sweep.Calls;
            judgeCost = sweep.CostUsd;
            judgeApplied = sweep.Applied;
            judgeRejected = sweep.Rejected;
            judgeDeferred = sweep.Deferred;
            codeRejected = sweep.CodeRejected;
            // Сам проход судьи исключений не бросает; его FirstFailure — единственный способ
            // узнать о несделанном, и он в журнале.
            if (sweep.FirstFailure is not null)
                LogWarnSafe("Ночной разбор {Owner}: проход судьи не доведён: {Reason}", ownerId, sweep.FirstFailure);
        }
        return Result(outcome);

        NightResult Result(string o) => new(FormatDate(date), selected, analyzed, applied, toInbox, est, o,
            overrodeHuman, disputedPages.Count, inputTokens, rejected, firstRejectReason, manual, dropped, firstDropReason,
            judgeCalls, judgeCost, judgeApplied, judgeRejected, judgeDeferred, codeRejected,
            stage2Refused, firstStage2Refusal);
    }

    /// <summary>Отказ окна по мёртвому локальному маршруту (2e13d45c): причина отказа несёт
    /// формулировку WikiIngestService.ReasonLocalUnavailable, а нулевой вход подтверждает, что
    /// модель не звалась ни разу. Проверка по подстроке: до ночи причина проходит через обёртки
    /// («разбор окна m1-m8 не удался: синтез: …»). Имени движка нет: их может быть несколько
    /// (J-W2), выключатель относится к маршруту места.</summary>
    private static bool IsLocalDownRejection(WikiChatIngestService.ChatResult r) =>
        r.InputTokens == 0 && r.Reason?.Contains(WikiIngestService.ReasonLocalUnavailable, StringComparison.Ordinal) == true;

    // Что мешает продолжать ПЕРЕД чатом: тумблер/флаг сняли (disabled_midway), окно закрылось
    // (window_closed), модель недоступна (error — облако молча не подставляется), упор в лимит
    // открытых пакетов (open_limit). Настройки перечитываются каждый раз: PUT посреди ночи виден
    // следующему чату. При ручном проходе (WN-5) окно не проверяется вовсе: человек запустил
    // разбор вне окна — оно ему не преграда; флаг, тумблер, маршрут и open_limit — как у тика.
    private (string? Stop, WikiSettingsStore.NightlyConfig? Cfg) CheckStop(string ownerId, string root, DateOnly date, bool manual)
    {
        if (_flags?.IsEnabled(ownerId, FeatureFlagKeys.LlmWiki) != true) return (OutcomeDisabledMidway, null);
        var cfg = WikiSettingsStore.Read(root).Settings.NightlyOrDefaults();
        if (!cfg.Enabled) return (OutcomeDisabledMidway, null);
        if (!manual)
        {
            var now = _clock();
            if (!InWindow(now.Hour, cfg.WindowStartHour, cfg.WindowEndHour)
                || WindowDate(now, cfg.WindowStartHour, cfg.WindowEndHour) != date) return (OutcomeWindowClosed, null);
        }
        if (!_llm.HasRoute(LocalActionCatalog.WikiNightly)) return (OutcomeError, null);
        if (WikiPackageStore.CountOpen(root) >= WikiPackage.MaxOpenPackages) return (OutcomeOpenLimit, null);
        return (null, cfg);
    }

    // Очередь прохода: чаты владельца, которые ночь вообще может взять. Чат исполнения задач,
    // чат с пометкой «не разбирать», приватный чат и личный раздел отсекаются здесь
    // (skip побеждает балл); берутся «важные» (минуя ступени 1–2), зона triage и серые
    // (их судит ступень 2 на подходе очереди). Порядок: «важные», затем по UpdatedAt убыв.
    private List<Candidate> BuildQueue(string ownerId, IReadOnlyDictionary<string, WikiChatTriageEntry> entries)
    {
        var result = new List<Candidate>();
        foreach (var s in _sessions.GetAll().Where(x => _sessions.ResolveOwnerId(x) == ownerId))
        {
            // Приватный чат (FIN3-3, инвариант в шапке): PrivateToUserId != null — виден только
            // автору; чат участника общего проекта резолвится на владельца, но в его очередь
            // не входит ни под каким баллом (тот же фильтр, что у автообучения памяти).
            if (s.TaskExecution || s.WikiMark == WikiMarks.Skip || s.PrivateToUserId is not null) continue;
            var home = WikiChatIngestService.HomeOf(s);
            if (home == WikiSections.Personal) continue;
            entries.TryGetValue(s.Id, out var e);
            var important = s.WikiMark == WikiMarks.Important;
            if (!important && e?.Stage1Zone is not (WikiChatScorer.ZoneTriage or WikiChatScorer.ZoneGray)) continue;
            result.Add(new Candidate(s, e, important, home));
        }
        return [.. result.OrderByDescending(c => c.Important).ThenByDescending(c => c.Session.UpdatedAt)];
    }

    // ─── Итог ночи: keeper/nightly-last.json ──────────────────────────────────

    /// <summary>Итог последней ночи; нет файла или он нечитаем — null (дата прохода тогда не
    /// закрыта, проход повторится — безопасная сторона).</summary>
    internal static NightResult? ReadLast(string root)
    {
        try
        {
            var full = SafePath.Join(root, LastRelPath);
            if (!File.Exists(full)) return null;
            var parsed = JsonSerializer.Deserialize<NightResult>(File.ReadAllText(full, Encoding.UTF8), JsonOpts);
            return parsed is { Date: { Length: > 0 }, Outcome: { Length: > 0 } } ? parsed : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // Атомарная запись (temp → fsync → Move), как WikiSettingsStore.WriteAtomic: половина файла
    // итога не должна читаться как итог.
    private static void WriteLast(string root, NightResult result)
    {
        var full = SafePath.Join(root, LastRelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, JsonOpts);
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                fs.Write(bytes);
                fs.Flush(true);
            }
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    // ─── Утреннее уведомление (N7c) ───────────────────────────────────────────

    // Простой язык для исхода, отличного от обычного: что помешало ночи, без имён внутренних
    // констант. Формулировки — по постановке шага; обычный исход причины не имеет (null).
    private static string? OutcomeReason(string outcome) => outcome switch
    {
        OutcomeBudget => "Кончился бюджет — остальное разберём в следующую ночь.",
        OutcomeOpenLimit => "Уперлись в лимит открытых пакетов — разберите «Входящие».",
        OutcomeWindowClosed => "Окно закрылось.",
        OutcomeDisabledMidway => "Ночь прервана — отключили в настройках.",
        OutcomeError => "Ошибка — повторим в следующую ночь.",
        _ => null,
    };

    /// <summary>Текст тела утреннего уведомления — ЕДИНСТВЕННАЯ точка сборки (ADR-038, предохранитель 6):
    /// причина необычного исхода, затем по одной сводной строке за ночь («ИИ заменил N ваших
    /// формулировок», «ИИ отметил противоречие на N страницах», «Во «Входящих» N пакетов…»,
    /// «судья недоступен — ждут решения: N», «отклонено кодом: N»; нулевая строка не пишется,
    /// строки на каждую замену нет) и путь к «Журналу». Второго уведомления нет.</summary>
    internal static string NotificationBody(string? reason, int overrodeHuman, int disputedPages,
        int toInbox, int judgeDeferred, int codeRejected)
    {
        var parts = new List<string>();
        if (reason is not null) parts.Add(reason);
        if (overrodeHuman > 0)
            parts.Add($"ИИ заменил {overrodeHuman} {Plural(overrodeHuman, "вашу формулировку", "ваши формулировки", "ваших формулировок")}.");
        if (disputedPages > 0)
            parts.Add($"ИИ отметил противоречие на {disputedPages} {Plural(disputedPages, "странице", "страницах", "страницах")}.");
        // «во «Входящих» N» было частью заголовка (N7c, Ф2); заголовок J-J4 несёт сводку судьи,
        // поэтому это число — здесь: сказать владельцу, сколько ещё ждёт его решения, надо.
        if (toInbox > 0)
            parts.Add($"Во «Входящих» {toInbox} {Plural(toInbox, "пакет ждёт решения", "пакета ждут решения", "пакетов ждут решения")}.");
        // J-J4 (хвост 2, решение Софьи): форма без согласования с числом — «судья недоступен —
        // ждут решения: N»: «3 операций ждут» читается как обрывок фразы; склонять тут нечего.
        if (judgeDeferred > 0)
            parts.Add($"судья недоступен — ждут решения: {judgeDeferred}.");
        // J-J4 (хвост 3): отказы, вынесенные кодом без вызова модели, — только здесь: форма
        // заголовка задана c2 и их не несёт, без этой строки владелец не видит, что часть пакета
        // закрыта не судьёй.
        if (codeRejected > 0)
            parts.Add($"отклонено кодом: {codeRejected}.");
        parts.Add("Подробности — во вкладке «Вики» → «Журнал».");
        return string.Join(' ', parts);
    }

    // Русское число: 1 — one, 2–4 — few, остальное (5–20, 0) — many; 11–14 всегда many.
    private static string Plural(int n, string one, string few, string many)
    {
        var tail = n % 100;
        if (tail is >= 11 and <= 14) return many;
        return (n % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }

    /// <summary>Сообщение владельцу об итоге ночи — ровно одно на проход, после записи итога.
    /// Молчание: диспетчера нет в составе; или «ничего не разобрано, ночь обычная и судья ничего
    /// не отложил» (сказать нечего). Отложенные судьёй операции — не молчание (хвост 1 J-J4):
    /// ночь могла ничего не разобрать, а пакет дневной заявки всё равно застрял без вердикта —
    /// владелец обязан это увидеть. Иной исход — с причиной из OutcomeReason. Сбой доставки —
    /// предупреждение в журнале: итог уже записан, исключение наружу из службы не выходит.</summary>
    private async Task TryNotifyAsync(string ownerId, NightResult result)
    {
        if (_notifications is null) return; // диспетчера в этом составе нет (тест/сборка без шва): итог всё равно в файле
        if (result.Analyzed == 0 && result.Outcome == OutcomeCompleted && result.JudgeDeferred == 0) return;

        var reason = OutcomeReason(result.Outcome);
        try
        {
            await _notifications.SendAsync(ownerId, new CreateNotificationRequest
            {
                Kind = result.Outcome == OutcomeCompleted ? "info" : "warning",
                Type = "wiki_nightly",
                // Сводка J-J4: что решил судья и сколько это стоило. «во «Входящих»» из
                // заголовка перенесено в тело (NotificationBody): заголовок несёт одну строку
                // чисел, а их стало больше.
                Title = $"Ночной разбор: применено {result.Applied}; судья принял {result.JudgeApplied}, "
                    + $"отклонил {result.JudgeRejected}; ждут {result.JudgeDeferred}; ${CostText(result.JudgeCostUsd)}",
                Body = NotificationBody(reason, result.OverrodeHuman, result.DisputedPages,
                    result.ToInbox, result.JudgeDeferred, result.CodeRejected),
                // Url открывает вкладку «Вики» на её подкладке по умолчанию («Страницы»): deep-link на
                // подвкладку «Журнал» во фронте не предусмотрен (WikiTab держит подвкладку локально),
                // поэтому текст называет путь, а не обещает переход.
                Url = "#/wiki",
                Source = "wiki",
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Ночь уже разобрана и итог записан: сбой доставки не отменяет его и не рвёт проход.
            // Он назван в журнале — молчания нет.
            LogSafe(ex, "уведомление об итоге ночного разбора {Owner} не доставлено", ownerId);
        }
    }
}
