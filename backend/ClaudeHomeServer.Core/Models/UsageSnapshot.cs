namespace ClaudeHomeServer.Models;

// Снимок использования окна лимита подписки в момент времени (из rate_limit_event).
// Utilization — доля 0..1; LimitType — five_hour/seven_day/weekly; ResetsAt — ISO-время сброса.
// SubscriptionKey — какая подписка сгенерировала снимок ("claude" — основная, ключ из пула — дополнительная).
// Source — кто записал снимок: "turn" (живой ход чата), "probe" (идл-пинг
// SubscriptionUsageWarmupService) или "oauth" (SubscriptionOAuthUsageService); null — снимок
// записан до появления поля (обратная совместимость со старым usage.json).
public record UsageSnapshot(
    DateTime Timestamp,
    string LimitType,
    double? Utilization,
    string? Status,
    bool IsUsingOverage,
    string? ResetsAt,
    string? OverageStatus = null,
    string? OverageResetsAt = null,
    string SubscriptionKey = "claude",
    string? Source = null,
    // Почему перерасход (overage) выключен: out_of_credits — кредиты перерасхода кончились,
    // org_level_disabled — перерасход запрещён на уровне организации. Долго терялось:
    // RateLimitMessage его несёт, но UsageService.Record не принимал; доведено до снимка для
    // разбора инцидентов. ВАЖНО: это НЕ признак «модель недоступна» — поле приезжает и на
    // обычном исчерпании пятичасового окна у аккаунта с выключенным перерасходом (в логе
    // 2026-09-09 — 248 таких строк за сутки от идл-пинга, который ходит haiku). Отличить
    // отказ по модели от исчерпания окна можно только по тексту ошибки хода
    // (TurnErrorClassifier), см. ClaudeSubscriptionPool.HadRecentModelRejection.
    // null — поле отсутствовало (старые снимки) или не пришло.
    string? OverageDisabledReason = null);

// Информация о тарифе подписки (из ~/.claude/.credentials.json)
public record PlanInfo(string? SubscriptionType, string? RateLimitTier, string Label);

// Ответ /api/usage: история снимков + тариф + per-subscription utilisation.
// RotationThreshold — порог утилизации 5h-окна, выше которого аккаунт выведен из ротации
// новых чатов (заполняется только при наличии дополнительных подписок).
// Providers — снимки окон лимитов сторонних CLI-провайдеров (glm/deepseek): их
// Anthropic-совместимые эндпоинты тоже шлют rate_limit_event, снимок пишется под ключ провайдера.
// PollStatuses — статус опроса api/oauth/usage по ключам аккаунтов ("ok" | "unauthorized" |
// "error"): по "unauthorized" вкладка показывает плашку «нужен claude login», не гадая по свежести.
// RoutingTarget — ключ аккаунта, куда фактически ушёл бы новый чат сейчас (PickForDisplay):
// IsInRotation абсолютный, а Pick относительный (спилл на перегруженные при отсутствии
// свободных) — без цели роутинга бейдж «выведен из ротации» врал бы на аккаунте-спилле.
public record UsageResponse(IReadOnlyList<UsageSnapshot> Snapshots, PlanInfo? Plan,
    Dictionary<string, SubscriptionUsage>? Subscriptions = null, double? RotationThreshold = null,
    Dictionary<string, IReadOnlyList<UsageSnapshot>>? Providers = null,
    OllamaUsageInfo? Ollama = null,
    IReadOnlyDictionary<string, string>? PollStatuses = null,
    string? RoutingTarget = null,
    // Порог утилизации недельного окна: аккаунт выводится из ротации и по нему тоже,
    // иначе бейдж соврёт («5ч 35% ниже порога 80%», хотя сработало недельное).
    double? WeeklyThreshold = null);

// Блок «Локальная модель (Ollama)» для экрана использования. У Ollama нет лимитов/баланса
// (локальная, бесплатная) — показываем только модель и на какие действия она заведена.
// Enabled=false → раздел показывает «не настроена». Actions — весь каталог фоновых действий
// с флагом RoutedToOllama (идёт на локаль сейчас) — прозрачно, что бесплатно, а что на claude.
public record OllamaUsageInfo(bool Enabled, string? Model, string? BaseUrl,
    IReadOnlyList<OllamaActionInfo> Actions,
    // "ollama" | "llama-server" — какой движок сейчас активен. Имя DTO не переименовываем:
    // контракт публичный, фронт под него правят отдельно (Kira).
    string Provider = "ollama",
    // LocalEngines — локальные движки, которые UI «Исполнители ночи» может предложить (разрез
    // «ночь вики на нескольких локальных исполнителях» §2, шаг W2c): «local» (основной LocalLlm)
    // первым, затем зарегистрированные и включённые LocalEngines:* в порядке регистрации.
    // Облачных вариантов в списке нет по построению (И3): исполнителем ночи становится только
    // локальный движок. null — список не построен (лёгкий ответ scope=limits не строит блок ollama).
    IReadOnlyList<OllamaLocalEngineInfo>? LocalEngines = null);

// Одна строка списка локальных движков для выбора (W2c): name — ключ исполнителя, то же значение,
// которым админ зовёт движок в PUT списка (LocalActionExecutorsStore): «local» или имя LocalEngines:*;
// title — подпись той же логики, что Engine у OllamaActionInfo (Title, иначе модель, иначе имя);
// maxConcurrency — потолок параллельности движка: ТОТ ЖЕ число, что держит его счётчик слотов и по
// которому валидатор списка проверяет parallel (у именованного — LocalEngines:<имя>:MaxConcurrency,
// у основной локали — MaxConcurrentTurns провайдера по LocalLlm:EngineKey), иначе UI разрешал бы
// то, что физика не выдержит.
public sealed record OllamaLocalEngineInfo(string Name, string Title, int MaxConcurrency);

// Список исполнителей места с флагом каталога ParallelExecutors (OllamaActionInfo.Executors):
// maxTotal — потолок одновременных слотов одного владельца (1..4), items — движки в порядке стора:
// порядок списка = приоритет, свободный слот первого получает чат раньше. null — списка нет:
// место идёт по своему маршруту, как до фичи.
public sealed record OllamaExecutorsInfo(int MaxTotal, IReadOnlyList<OllamaExecutorInfo> Items);

// Один исполнитель: имя движка («local» или LocalEngines:*) и сколько работ на нём одновременно
// (1..maxConcurrency, см. OllamaLocalEngineInfo).
public sealed record OllamaExecutorInfo(string Engine, int Parallel);

// Route — исполнитель первого шага: "local", "tier:strong|medium|weak" (слот тира),
// легаси "claude"/"default" (≙ tier:medium) или id конкретной модели провайдера (дальше
// действие идёт по цепочке «выбранное → локаль → claude»).
// RoutedToOllama — начинается ли действие с локальной модели прямо сейчас (с учётом доступности Ollama).
// Source — откуда взято значение: "default" (каталог), "config" (Ollama:Actions) или "admin"
// (выбор в UI); по нему видно, что переопределено и что можно сбросить.
// RequiresStrong — действию нужна сильная модель (лицо продукта, генерация артефактов):
// локаль ему не годится, поэтому пресеты подбирают Claude/облачную модель, а не Ollama.
// Agentic — агентное место (группа «Чаты и персоны»): локаль и direct:-модели недоступны.
public record OllamaActionInfo(string Key, string Title, string Group, bool RoutedToOllama,
    string Source = "default", string Route = "claude", bool RequiresStrong = false,
    bool Agentic = false, object? Preset = null, string Input = "text", bool FacePose = false,
    // Engine — подпись именованного локального движка (LocalEngines:<имя>:Title, иначе модель), когда
    // Route="local" ведёт на него, а не на основной LocalLlm; null — основной движок или не локаль.
    string? Engine = null,
    // ParallelExecutors — флаг каталога места (LocalAction.ParallelExecutors): место умеет вести
    // несколько работ на локальных движках одновременно и смотрит список своих исполнителей.
    // Сейчас это одно wiki-nightly; UI рисует блок «Исполнители ночи» только при этом флаге.
    bool ParallelExecutors = false,
    // Executors — список исполнителей места из LocalActionExecutorsStore (что админ дал месту);
    // null — список не задан: место идёт по своему маршруту, как до фичи (пустой список = снятие ключа).
    OllamaExecutorsInfo? Executors = null);

// Utilisation одной подписки: снимки + опциональное имя + статус роутинга.
// InRotation — берёт ли пул этот аккаунт для новых чатов; Utilization — эффективная
// утилизация 5h-окна (истёкшее окно/нет данных = 0); Exhausted — жёсткое исчерпание
// (rejected/100%), при котором аккаунт выведен независимо от числа Utilization;
// Tier — ярлык тарифа ("Max 20×", "Pro", …), по нему пул приоритизирует аккаунты.
// LoginCommand — готовая PowerShell-команда `claude login` в профиль ЭТОГО аккаунта
// (SubscriptionOAuthUsageService.LoginCommandFor); null — у аккаунта нет файлового
// профиля, куда логин имел бы смысл. Отдаётся всегда, не только при unauthorized —
// фронт сам решает, когда показать кнопку копирования.
// SupportsOpus / Supports1M — возможности аккаунта по тарифу (из ClaudeSubscriptionConfig).
// Третья ось наблюдаемости рядом с «в ротации»: Pick их уже учитывает (SupportsModel),
// карточка — нет. false = «Без Opus» / «Без 1M» пилюля. Тип bool?, потому что в блок
// подписок попадают только аккаунты с настроенной конфигурацией пула (HasExtra), где
// оба поля невыключаемые; null остаётся для обратной совместимости со старыми бэкапами
// снимков (data/usage.json), где этих полей ещё не было.
// UnavailableModels — живые пометки «модель недоступна на ЭТОЙ подписке» (пара подписка ×
// модель). Без них человек не понимает, почему модель не выбирается: подписка «В ротации»,
// лимит не исчерпан, а ходы на неё не идут. Пустой список = пометок нет.
public record SubscriptionUsage(IReadOnlyList<UsageSnapshot> Snapshots, string? Name = null,
    bool InRotation = true, double Utilization = 0, bool Exhausted = false, string? Tier = null,
    string? LoginCommand = null, bool? SupportsOpus = null, bool? Supports1M = null,
    // Эффективная утилизация недельного окна (истёкшее окно/нет данных = 0) — вторая ось
    // вывода из ротации наравне с Utilization.
    double WeeklyUtilization = 0,
    IReadOnlyList<ModelUnavailableMark>? UnavailableModels = null);

// Пометка «модель недоступна на подписке» для выдачи наружу (ClaudeSubscriptionPool).
// Reason — wire-имя класса отказа: "model_no_access" (подписка не имеет доступа к модели —
// свойство тарифа) | "model_out_of_credits" (кончились usage credits модели — можно пополнить).
// Until — момент UTC, до которого пометка держится: по нему UI пишет «проверим снова через …».
// Снять досрочно — POST /api/usage/subscriptions/{key}/model-availability/clear.
public record ModelUnavailableMark(string Model, string Reason, DateTime Until);
