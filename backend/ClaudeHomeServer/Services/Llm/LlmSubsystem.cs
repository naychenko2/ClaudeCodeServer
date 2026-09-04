using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Http;

namespace ClaudeHomeServer.Services.Llm;

// Подсистема Llm — модельный слой продукта: вся работа с LLM-провайдерами
// (Claude через cli, GLM/OpenRouter напрямую, локальные Ollama/llama-server),
// one-shot раннеры и дешёвые текстовые операции, реестры провайдеров/балансов/
// контекстных ёмкостей, файлы наблюдаемости (логи ходов и сабагентов, egress-probe),
// а также пара «не-LLM» карточек, живущих в `Services.Llm`:
// `ChatDigestService` (сводка архива чата — место `chat-digest`, отдельный ход
// по кнопке) и `PlanMapService` (визуальная развертка плана — место `plan-map`).
//
// Зарегистрировано здесь (ранее жило блоком в Program.cs:~156-355, 454, 457):
// 1) `UserModelTierResolver` — слоты моделей (strong/medium/weak) per-user,
//    глобальная таблица назначений LocalActionCatalog. Шов к слою персон.
// 2) `GlmModelAliasMigration` (gated hosted) — разовая переадресация закреплённых
//    GLM-моделей на актуальный каталог (z.ai алиасы). Опора на SessionManager
//    и SpecialtySettingsStore.
// 3) `OneShotClaudeRunner` + `IOneShotRunner` (форвард) — основной раннер
//    дешёвых ходов через claude CLI. Опора на `ClaudeSubscriptionPool` и
//    `SubscriptionActivityTracker` (ротация подписок).
// 4) Локальный стек: `OllamaClient`, `LlamaServerClient`, фабрика
//    `ILocalLlmClient` по `LocalLlm:Provider`, плюс тихие HTTP-клиенты на оба
//    имени (QuietHttpLogger, см. Services/Http). Локальная модель опциональна,
//    погашенная локаль — штатная ситуация, а не авария.
// 5) `OllamaActionRankService` — бесплатное ранжирование действий локальной
//    моделью Ollama для роутинга через LocalActionRouter.
// 6) `CloudCheapClient` — прямой HTTP-адаптер бесплатных моделей OpenRouter
//    для one-shot задач (второй транспорт рядом с провайдером через claude CLI).
// 7) `LocalActionOverridesStore` (админские тумблеры маршрута из UI,
//    слой поверх Ollama:Actions) + `LocalActionRouter` (роутинг фоновых действий
//    локаль(Ollama)/claude).
// 8) `ModelAssignmentResolver` — резолвер моделей агентных мест (явная модель
//    → назначение админа → слот тира).
// 9) `FallbackSettingsStore` — стор настроек фолбэк-оркестрации (ADR-007 §4).
// 10) `LocalActionPresetService` — пресеты автоподбора исполнителя фоновых действий.
// 11) `ICheapTextRunner` → `CheapTextRunner` — единый «дешёвый» текстовый раннер
//     с фолбэком на Ollama/Claude.
// 12) `LlmProviderRegistry` — реестр провайдеров и их прайсов; читается
//     ModuleRegistry (динамический слой LocalActionCatalog), LlmProviderRegistry
//     отдаёт `GetProviderProjectsDirs()`/`ProfilesDir` для пост-билд инициализации
//     WorkflowAgentParser (Program.cs:~869).
// 13) `ProviderBalanceService` + `IProviderBalanceService` (форвард на тот же
//     singleton) — остаток баланса у внешнего провайдера, обязательный контракт
//     «интерфейс и конкретный тип указывают на ОДИН инстанс» (форвардер, не
//     `AddSingleton<IProviderBalanceService, ProviderBalanceService>` — был бы
//     второй экземпляр и расхождение данных).
// 14) `ProviderHealthRegistry` — кулдаун недоступности провайдера (in-memory).
// 15) `ContextCapacityRegistry` — наблюдаемая ёмкость окна модели (ContextOverflow).
// 16) `FileChangeAttributor` — атрибуция file_changed чату-источнику при параллельных
//     ходах одного проекта.
// 17) `ILlmSessionAdapterFactory` → `LlmSessionAdapterFactory` — фабрика адаптеров
//     хода (fallback-логика ADR-007).
// 18) `Claude.SubagentRunLog.Create` + `Llm.TurnRunLog.Create` — паспорта
//     прогонов сабагентов и ходов. Рестарт инстанса уносит с собой память API —
//     чтобы серия прогонов и «что ломалось за сутки» пережили рестарт, оба стора
//     пишут зеркало в `data/logs/subagent-runs-*.jsonl` и `data/logs/turn-runs-*.jsonl`.
// 19) `IEgressProbe` → `EgressProbe` — проба исходящего HTTP(S)_PROXY для
//     разведения отказа провайдера и отказа канала наружу.
// 20) `ChatDigestService` (место `chat-digest`, сводка архива) и
//     `PlanMapService` (место `plan-map`, визуальный разворот плана).
// 21) `GlmModelAliasMigration` (gated hosted) — см. п.2.
//
// Шов `services.Memory` (`MemoryWriteResolver`) лежит ВНЕ `Services.Llm`,
// поэтому НЕ переносится сюда — отдельная вертикаль Memory, и реестр уже
// зарегистрирован в Program.cs:~304 (без `MemorySubsystem` потому что это
// сервис общего слоя, не фасад памяти).
//
// Шов `Services.Hubs` не нужен: Llm-слой пишет события через SessionManager
// (который держит IHubContext<SessionHub>), не напрямую.
//
// ⚠ Биллинг-сервисы `FalCostService`/`FalAccountService`/`GlifAccountService`
// живут В КОРНЕ `ClaudeHomeServer.Services` и НЕ переезжают в LlmSubsystem —
// они часть слоя биллинга, не модели. Их регистрации остаются в Program.cs.
//
// ⚠ `PersonaAskService` (one-shot ответы персон от их лица через IOneShotRunner)
// живёт в корне Services и НЕ переезжает: это персональный фасад, не модельный
// сервис; его перенос — отдельная задача волны 4.
//
// Шов к модели через формат `provider:x/model:y` (или просто алиас провайдера) —
// текст выбора идёт из `LocalActionCatalog`/`TierMatrix`/`PresetStore`. Эти
// компоненты лежат ВНУТРИ `Services.Llm` (префикс открыт через собственный
// `NamespaceRoot`), поэтому при `Vertical_НеСсылаетсяНаДругиеВертикали(Llm)`
// страж границ их не флагает.
//
// Прогрев после Build:
// - `LlmProviderRegistry` резолвится в Program.cs:~869 для регистрации корней
//   провайдеров в WorkflowAgentParser и установки `WorkflowAgentParser.ProfilesRoot`.
// - `ILocalLlmClient` резолвится в Program.cs:~912 для фонового прогрева активной
//   локальной модели.
//
// Удалено из Program.cs (всё, что выше): 24 строки регистраций,
// 2 тихих HTTP-клиента, 5 прокомментированных пояснений.
public sealed class LlmSubsystem : IAppSubsystem
{
    public string Key => "llm";

    public string Title => "Модельный слой LLM";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        // Слоты моделей (strong/medium/weak) per-user + глобальная таблица
        // назначений LocalActionCatalog. Шов к слою персон (UserStore),
        // AppSettingsService и доменному ModelTier (из Models).
        services.AddSingleton<UserModelTierResolver>();

        // One-shot ответы персон через провайдерский ClaudeSubscriptionPool
        // и подписки на утилизацию (SubscriptionActivityTracker). Ротация подписок
        // — отдельный модуль, здесь только потребитель пула.
        services.AddSingleton<OneShotClaudeRunner>();
        services.AddSingleton<IOneShotRunner>(sp => sp.GetRequiredService<OneShotClaudeRunner>());

        // AI-хаб: локальная LLM (Ollama или llama-server, выбор по LocalLlm:Provider).
        // Обе реализации регистрируются как конкретные синглтоны (тестам и прямому
        // прогреву они нужны под своим типом), а ILocalLlmClient — тот, кого держат
        // потребители (CheapTextRunner, LocalActionRouter, SessionManager и т.д.).
        services.AddSingleton<OllamaClient>();
        services.AddSingleton<LlamaServerClient>();
        services.AddSingleton<ILocalLlmClient>(sp =>
        {
            var options = LocalLlmOptions.Read(sp.GetRequiredService<IConfiguration>());
            return options.Provider == LocalLlmOptions.LlamaServer
                ? sp.GetRequiredService<LlamaServerClient>()
                : sp.GetRequiredService<OllamaClient>();
        });
        // Тихие HTTP-логгеры на оба имени: каждая реализация пишет в свою категорию,
        // и одна мёртвая зависимость не глушит жалобы другой. Без них каждый вызов
        // даёт Error со стектрейсом на весь экран.
        services.AddQuietHttpClient(
            OllamaClient.HttpClientName,
            new QuietHttpClientProfile(
                Category: "ClaudeHomeServer.Llm.Ollama",
                Subject: "локальной моделью Ollama",
                Consequence: "Фоновые действия уйдут облачной модели."))
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddQuietHttpClient(
            LlamaServerClient.HttpClientName,
            new QuietHttpClientProfile(
                Category: "ClaudeHomeServer.Llm.LlamaServer",
                Subject: "локальной моделью llama-server",
                Consequence: "Фоновые действия уйдут облачной модели."))
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));

        // Бесплатное ранжирование действий локальной Ollama для роутинга
        // через LocalActionRouter.
        services.AddSingleton<OllamaActionRankService>();

        // Прямой HTTP-адаптер бесплатных моделей OpenRouter для one-shot задач
        // (второй транспорт рядом с провайдером через claude CLI; модели —
        // курируемый список OpenRouter:DirectModels).
        services.AddSingleton<CloudCheapClient>();

        // Роутинг фоновых действий локаль(Ollama)/claude + единый «дешёвый»
        // текстовый раннер с фолбэком.
        services.AddSingleton<LocalActionOverridesStore>();
        services.AddSingleton<LocalActionRouter>();
        services.AddSingleton<ICheapTextRunner, CheapTextRunner>();

        // Резолвер моделей агентных мест (явная модель → назначение админа →
        // слот тира).
        services.AddSingleton<ModelAssignmentResolver>();

        // Стор настроек фолбэк-оркестрации (ADR-007 §4).
        services.AddSingleton<FallbackSettingsStore>();

        // Пресеты автоподбора исполнителя фоновых действий.
        services.AddSingleton<LocalActionPresetService>();

        // Разовая переадресация закреплённых моделей GLM на актуальный каталог
        // (z.ai алиасы) — gated hosted: в Testing не стартует, прогон детерминирован
        // маркером в data.
        services.AddGatedHostedService<GlmModelAliasMigration>(config);

        // Реестр провайдеров: цены, профили CLI, projects-каталоги.
        // Резолвится в Program.cs:~869 для регистрации корней в WorkflowAgentParser.
        services.AddSingleton<LlmProviderRegistry>();

        // Кулдаун недоступности провайдера (волна 2 ADR-007): in-memory, без персиста.
        services.AddSingleton<ProviderHealthRegistry>();
        services.AddSingleton<IProviderBalanceService>(sp => sp.GetRequiredService<ProviderBalanceService>());
        services.AddSingleton<ProviderBalanceService>();

        // Наблюдаемая ёмкость окна модели (ContextOverflow): модель, не принявшая
        // контекст, не получает следующие ходы с контекстом ≥ N. In-memory.
        services.AddSingleton<ContextCapacityRegistry>();

        // Атрибуция file_changed чату-источнику при параллельных ходах одного проекта.
        services.AddSingleton<FileChangeAttributor>();

        // Фабрика адаптеров хода (fallback-логика ADR-007): IOneShotRunner и
        // ILlmSessionAdapterFactory отдают разные интерфейсы разным потребителям,
        // обе реализации живут в OneShotClaudeRunner + FallbackLlmSessionAdapter.
        services.AddSingleton<ILlmSessionAdapterFactory, LlmSessionAdapterFactory>();

        // Паспорта прогонов сабагентов и ходов — память для API + зеркало в
        // data/logs/{subagent,turn}-runs-*.jsonl (рестарт инстанса не уносит серию).
        services.AddSingleton(sp => Claude.SubagentRunLog.Create(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp => TurnRunLog.Create(sp.GetRequiredService<IConfiguration>()));

        // Жив ли исходящий прокси (HTTP(S)_PROXY): отличает отказ канала наружу
        // от отказа эндпоинта вендора — при первом смена модели не лечит ничего.
        services.AddSingleton<IEgressProbe>(sp => new EgressProbe(sp.GetRequiredService<IConfiguration>()));

        // Сводка карточки архива (место chat-digest) и визуальная развертка плана
        // (место plan-map). Эти карточки живут в Services.Llm, хоть и не про модель —
        // перенос сюда согласован с архитектурой шага.
        services.AddSingleton<ChatDigestService>();
        services.AddSingleton<PlanMapService>();
    }
}
