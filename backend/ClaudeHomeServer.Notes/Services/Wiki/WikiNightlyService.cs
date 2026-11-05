using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
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
// - PUT enabled=false посреди ночи: тумблер перечитывается перед КАЖДЫМ чатом, остановка до
//   следующего — outcome=disabled_midway;
// - дата прохода живёт в keeper/nightly-last.json, пишется атомарно (temp+Move) и ПОСЛЕ прохода:
//   рестарт ночью не повторит завершённый проход; outcome=error дату не закрывает — следующий тик
//   окна пробует снова; тот же error той же даты не повторяется: если прочитанный до прохода итог
//   этого же дня — error и итог нового прохода тоже error, уведомления нет (успех и иные исходы сообщаются).
//
// Утреннее уведомление (N7c): после записи итога владелец получает итог ночи человеческими
// словами — «Ночной разбор: применено N, во „Входящих” M» и названием пути к «Журналу». Молчание
// ровно в двух случаях: диспетчера нет в этом составе (сборка/тест без шва — итог всё равно в
// файле) и «ничего не разобрано, и ночь обычная» — сказать нечего. Любой иной исход называется
// простым языком (причины — в OutcomeReason). Сбой доставки ничего не отменяет и не рвёт: итог
// к тому моменту уже записан, исключение отправителя не выходит из службы (LogSafe).

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
//   оставляет чат серым. Фактический вход этих вызовов (usage того же ответа движка) суммируется
//   в NightResult.InputTokens — меры нагрузки, не денег.
//   Кнопка «Разобрать» и агент wiki_ingest не затронуты — облако там допустимо.
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
    /// «ничего не потрачено». В файле итога поля аддитивны: старый итог (без поля) читается
    /// нулями.</summary>
    public sealed record NightResult(
        string Date, int Selected, int Analyzed, int Applied, int ToInbox, long EstTokens, string Outcome,
        int OverrodeHuman = 0, int DisputedPages = 0, long InputTokens = 0);

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
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _dataDir;

    public WikiNightlyService(ISessionDirectory sessions, WikiChatRecalc recalc, WikiChatStage2 stage2,
        WikiChatIngestService ingest, WikiKeeper keeper, ICheapTextRunner llm, IConfiguration config,
        ILogger<WikiNightlyService> log, Composition.IFeatureFlagGate? flags = null,
        Composition.INotificationSender? notifications = null)
        : this(sessions, recalc, stage2,
            // actionKey — только константа места: внешнего ввода у ночи нет.
            (owner, chat, ct) => ingest.AnalyzeAsync(owner, chat, ct, LocalActionCatalog.WikiNightly),
            llm, config, log, flags, clock: null, notifications: notifications, keeper: keeper)
    {
    }

    // Шов теста: разбор и часы подменяются (ограничения проверяются без настоящей модели, а окно —
    // без ожидания ночи). В приложении сюда попадает только цепочка из публичного конструктора.
    // notifications и keeper — после clock: существующие вызовы шва (позиционные) не сдвигаются.
    // keeper == null (тест без шва заведения) — ночь не заводит вики и берёт только заведённых.
    internal WikiNightlyService(ISessionDirectory sessions, WikiChatRecalc recalc, WikiChatStage2 stage2,
        Func<string, string, CancellationToken, Task<WikiChatIngestService.ChatResult>> analyze,
        ICheapTextRunner llm, IConfiguration config, ILogger<WikiNightlyService> log,
        Composition.IFeatureFlagGate? flags, Func<DateTimeOffset>? clock,
        Composition.INotificationSender? notifications = null, WikiKeeper? keeper = null)
    {
        _keeper = keeper;
        _sessions = sessions;
        _recalc = recalc;
        _stage2 = stage2;
        _analyze = analyze;
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
            var last = ReadLast(root);
            if (last is not null && last.Date == FormatDate(date) && last.Outcome != OutcomeError)
                return; // эта ночь уже пройдена (в том числе до рестарта)

            // Прочитанный до прохода итог этого же дня — error: о неудаче ночи владельцу уже
            // сказано. Проход повторяется (error дату не закрывает), но то же предупреждение
            // второй раз за дату не отправляется.
            var errorAlreadyNotified = last is not null && last.Date == FormatDate(date) && last.Outcome == OutcomeError;

            NightResult result;
            if (!initialized && !await TryEnsureInitializedAsync(ownerId))
                result = new NightResult(FormatDate(date), 0, 0, 0, 0, 0, OutcomeError);
            else
                result = await RunPassAsync(ownerId, root, date, ct);
            // Дата прохода — ПОСЛЕ прохода. Сбой записи итога не отменяет проход (его разборы
            // уже в сторах): следующий тик найдёт курсоры на концах и не разберёт ничего лишнего.
            try { WriteLast(root, result); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogSafe(ex, "Итог ночного разбора {Owner} не записан", ownerId);
            }
            // Уведомление — ПОСЛЕ записи итога: даже если доставка откажет, итог уже на диске
            // и в следующем тике читается настройками. Сбой отправителя остаёт в TryNotifyAsync.
            // Молчим только о повторном error той же даты; успех после error и любой иной исход — сообщаем.
            if (!(errorAlreadyNotified && result.Outcome == OutcomeError)) await TryNotifyAsync(ownerId, result);
        }
        finally
        {
            Running.TryRemove(key, out _);
        }
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

    private async Task<NightResult> RunPassAsync(string ownerId, string root, DateOnly date, CancellationToken ct)
    {
        int selected = 0, analyzed = 0, applied = 0, toInbox = 0, packagesNight = 0, overrodeHuman = 0;
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
                var (stop, cfgNow) = CheckStop(ownerId, root, date);
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
                        // Отказ ступени 2 → серая зона: чат отложен до следующей ночи.
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
        return Result(outcome);

        NightResult Result(string o) => new(FormatDate(date), selected, analyzed, applied, toInbox, est, o,
            overrodeHuman, disputedPages.Count, inputTokens);
    }

    // Что мешает продолжать ПЕРЕД чатом: тумблер/флаг сняли (disabled_midway), окно закрылось
    // (window_closed), модель недоступна (error — облако молча не подставляется), упор в лимит
    // открытых пакетов (open_limit). Настройки перечитываются каждый раз: PUT посреди ночи виден
    // следующему чату.
    private (string? Stop, WikiSettingsStore.NightlyConfig? Cfg) CheckStop(string ownerId, string root, DateOnly date)
    {
        if (_flags?.IsEnabled(ownerId, FeatureFlagKeys.LlmWiki) != true) return (OutcomeDisabledMidway, null);
        var cfg = WikiSettingsStore.Read(root).Settings.NightlyOrDefaults();
        if (!cfg.Enabled) return (OutcomeDisabledMidway, null);
        var now = _clock();
        if (!InWindow(now.Hour, cfg.WindowStartHour, cfg.WindowEndHour)
            || WindowDate(now, cfg.WindowStartHour, cfg.WindowEndHour) != date) return (OutcomeWindowClosed, null);
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
    /// формулировок», «ИИ отметил противоречие на N страницах»; нулевая строка не пишется, строки на
    /// каждую замену нет) и путь к «Журналу». Второго уведомления нет.</summary>
    internal static string NotificationBody(string? reason, int overrodeHuman, int disputedPages)
    {
        var parts = new List<string>();
        if (reason is not null) parts.Add(reason);
        if (overrodeHuman > 0)
            parts.Add($"ИИ заменил {overrodeHuman} {Plural(overrodeHuman, "вашу формулировку", "ваши формулировки", "ваших формулировок")}.");
        if (disputedPages > 0)
            parts.Add($"ИИ отметил противоречие на {disputedPages} {Plural(disputedPages, "странице", "страницах", "страницах")}.");
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
    /// Молчание: диспетчера нет в составе; или «ничего не разобрано, и ночь обычная» (сказать
    /// нечего). Иной исход — с причиной из OutcomeReason. Сбой доставки — предупреждение в
    /// журнале: итог уже записан, исключение наружу из службы не выходит.</summary>
    private async Task TryNotifyAsync(string ownerId, NightResult result)
    {
        if (_notifications is null) return; // диспетчера в этом составе нет (тест/сборка без шва): итог всё равно в файле
        if (result.Analyzed == 0 && result.Outcome == OutcomeCompleted) return;

        var reason = OutcomeReason(result.Outcome);
        try
        {
            await _notifications.SendAsync(ownerId, new CreateNotificationRequest
            {
                Kind = result.Outcome == OutcomeCompleted ? "info" : "warning",
                Type = "wiki_nightly",
                Title = $"Ночной разбор: применено {result.Applied}, во «Входящих» {result.ToInbox}",
                Body = NotificationBody(reason, result.OverrodeHuman, result.DisputedPages),
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
