using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Composition.Llm;
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
// 2) (свободно) — `GlmModelAliasMigration` уехала в спину (`Services/GlmModelAliasMigration.cs`,
//    регистрация в Program.cs рядом с PersonaProjectBindingsMigration): разовая уборка
//    данных в сторах, а не модельный слой, и единственная в вертикали мутация ядра.
// 3) `OneShotClaudeRunner` + `IOneShotRunner` (форвард) — основной раннер
//    дешёвых ходов через claude CLI. Опора на `ClaudeSubscriptionPool` и
//    `SubscriptionActivityTracker` (ротация подписок).
// 4) Локальный стек: `OllamaClient`, `LlamaServerClient`, фабрика
//    `ILocalLlmClient` по `LocalLlm:Provider`, плюс тихие HTTP-клиенты на оба
//    имени (QuietHttpLogger, см. Services/Http). Локальная модель опциональна,
//    погашенная локаль — штатная ситуация, а не авария.
// 4a) `LocalEndpointProbe` + тихий HTTP-клиент на его имя — pre-flight проба
//     локального эндпоинта (vLLM/llama.cpp); читается реестром ниже и
//     FallbackLlmSessionAdapter для ранней диагностики «локальная модель не запущена».
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
//     WorkflowAgentParser (Program.cs:~869). Принимает `ILocalEndpointProbe` и
//     подставляет живое `max_model_len` в `CLAUDE_CODE_MAX_CONTEXT_TOKENS` для
//     локального провайдера (см. ADR-007 §4 — окно контекста из живого `/v1/models`).
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
// 21) Волна 4B, шаг 2 — переселение в `Services.Llm`:
//   - `SpecialtySettingsStore` + `SpecialtySettingsLayer` (ADR-007 §2): матрицы
//     моделей по уровням, пресеты-цепочки и DefaultTier для специальностей; все
//     потребители (`ModelAssignmentResolver`, `LocalActionRouter`, `PresetStore`)
//     уже в `Llm`. `SpecialtyPromptPresets`/`SpecialtyCatalog`/`SpecialtyTemplatesService`
//     остаются в корне (не часть модели).
//   - `UsageService`: учёт расхода токенов/стоимости по аккаунтам подписок;
//     читается `ModelsController`/`UsageController` и самим `ClaudeSubscriptionPool`
//     для восстановления пометок исчерпания из снапшота.
//   - `ClaudeSubscriptionPool` + `SubscriptionActivityTracker` +
//     `SubscriptionWindowMismatchGuard` (с `ISubscriptionAlertNotifier` форвардером) +
//     `SubscriptionUsageWarmupService` (gated hosted) +
//     `SubscriptionOAuthUsageService` (gated hosted): ротация подписок пула —
//     часть механики фолбэка хода; три главных потребителя
//     (`FallbackLlmSessionAdapter`, `LlmSessionAdapterFactory`, `OneShotClaudeRunner`)
//     уже здесь.
//   - `WorkflowAgentParser`/`WorkflowWatcher`/`WorkflowMetaResolver` — статические
//     парсеры транскриптов; DI не нужны, логгер и кеш инициализируются в Program.cs
//     после Build (поэтому просто наличие файлов в `Services.Llm` достаточно).
//
// Итого 38 регистраций (в их числе два тихих HTTP-клиента Ollama/llama-server и
// два gated hosted сервиса подписок). Волна 4B, шаг 2 прибавил +8 регистраций
// (SpecialtySettingsStore/UsageService/ClaudeSubscriptionPool/SubscriptionActivityTracker/
// ISubscriptionAlertNotifier+SubscriptionAlertNotifier/SubscriptionWindowMismatchGuard/
// SubscriptionUsageWarmupService/SubscriptionOAuthUsageService+AddGatedHostedFrom от него)
// и перенесла их из Program.cs — net −8 в композиционном корне.
// Мерж master → ветка: ещё +2 (тихий HTTP-клиент LocalEndpointProbe +
// ILocalEndpointProbe → LocalEndpointProbe), LlmProviderRegistry теперь
// фабрикой через пробу. Все три добавки — из линии «локальная Qwen как
// CLI-провайдер», они закрывают pre-flight «локальная модель не запущена».
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
//   провайдеров в TranscriptRoots и установки `TranscriptRoots.ProfilesRoot`.
// - `ILocalLlmClient` резолвится в Program.cs:~912 для фонового прогрева активной
//   локальной модели.
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

        // Основной раннер дешёвых one-shot ходов через claude CLI: держит
        // ClaudeSubscriptionPool и SubscriptionActivityTracker (ротация подписок —
        // отдельный модуль, здесь только потребитель пула). `IOneShotRunner` —
        // форвард на ТОТ ЖЕ singleton (интерфейс мокируется в тестах).
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
                Consequence: "Фоновые действия уйдут облачной модели."));
        services.AddQuietHttpClient(
            LlamaServerClient.HttpClientName,
            new QuietHttpClientProfile(
                Category: "ClaudeHomeServer.Llm.LlamaServer",
                Subject: "локальной моделью llama-server",
                Consequence: "Фоновые действия уйдут облачной модели."));
        // Pre-flight проба локального эндпоинта (LocalEndpointProbe): GET /v1/models
        // с парсингом JSON по статусу модели. Шум гасится — проба стоит на горячем
        // пути каждого хода на локальной модели, без тишины лог заспамит Error-ами
        // «connection refused» за минуту.
        services.AddQuietHttpClient(
            LocalEndpointProbe.HttpClientName,
            new QuietHttpClientProfile(
                Category: "ClaudeHomeServer.Llm.LocalEndpointProbe",
                Subject: "локальным движком модели (vLLM/llama.cpp)",
                Consequence: "Ход на локальной модели завершится сразу «локальная модель не запущена»."));

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

        // Этап 5, шаг 4 (шов IPromptSectionProvider): Core-интерфейс под единственный
        // метод `EffectivePromptSections`, который PromptSectionsContributor в Turn
        // дёргает у SpecialtySettingsStore через прямую ссылку на Llm. Адаптер
        // лежит тут, в Main, рядом со сторем — DI подтянет его по конструктору.
        services.AddSingleton<IPromptSectionProvider, SpecialtyPromptSectionProvider>();

        // Пресеты автоподбора исполнителя фоновых действий.
        services.AddSingleton<LocalActionPresetService>();

        // Pre-flight проба локального эндпоинта: при остановленном llama.cpp/vLLM ход
        // завершится сразу понятной ошибкой, без шагов цепочки (см. LocalEndpointProbe,
        // FallbackLlmSessionAdapter). Используется реестром ниже для подстановки живого
        // max_model_len в CLAUDE_CODE_MAX_CONTEXT_TOKENS.
        services.AddSingleton<ILocalEndpointProbe>(sp =>
            new LocalEndpointProbe(sp.GetRequiredService<IHttpClientFactory>()));

        // Реестр провайдеров: цены, профили CLI, projects-каталоги.
        // Зависит от ILocalEndpointProbe: подставляет живое max_model_len в
        // CLAUDE_CODE_MAX_CONTEXT_TOKENS для локального провайдера. Цикла нет: проба
        // живёт от IHttpClientFactory, не от реестра. До этой правки конфиг жил руками —
        // за сутки значение 71 680 → 61 440 → 65 536, и CLI считал по объявленному
        // (57345 + 8192 < 71680), не сжимал вовремя и ход падал на одном токене.
        // Резолвится в Program.cs:~869 для регистрации корней в TranscriptRoots.
        services.AddSingleton<LlmProviderRegistry>(sp =>
            new LlmProviderRegistry(
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<ILocalEndpointProbe>()));

        // Этап 5, шаг 3 (шов IModelResolver): Core-интерфейс под единственный
        // метод `ResolveModelOrDefault`, который Spend дёргает у LlmProviderRegistry.
        // Адаптер лежит тут, в Main, рядом с LlmProviderRegistry — DI подтянет
        // его по конструктору автоматически.
        services.AddSingleton<IModelResolver, LlmModelResolverAdapter>();

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

        // === Волна 4B, шаг 2 — переселение из корня `Services` ===
        // Все три группы ниже уже лежали ВНУТРИ `Services.Llm` по потребителям (см.
        // inventory services-root, вердикт по вопросу 4): новые вертикали только
        // ради них заводить нерационально — это перенесённые папки, не модули.

        // Стор настроек специальностей и пресетов-цепочек выбора модели (ADR-007 §2).
        // SpecialtySettingsLayer — соседний класс в том же файле, отдельной регистрации
        // не требуется.
        services.AddSingleton<SpecialtySettingsStore>();

        // Учёт расхода токенов/стоимости по аккаунтам подписок. Читается
        // ModelsController/UsageController и ClaudeSubscriptionPool (восстановление
        // пометок исчерпания из снапшота после рестарта).
        services.AddSingleton<UsageService>();

        // Пул подписок с восстановлением пометок исчерпания из снапшотов usage.
        services.AddSingleton(sp => new ClaudeSubscriptionPool(
            sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<UsageService>()));

        // Время последней фактической активности аккаунта пула (живой ход / идл-пинг) —
        // делит SessionManager (RateLimitMessage живого хода) и SubscriptionUsageWarmupService.
        services.AddSingleton<SubscriptionActivityTracker>();

        // Сторож «чужого» setup-токена: расхождение сброса 5h-окна между setup-токеном
        // (probe/turn) и профильным логином (oauth) — алерт админам, без вывода из ротации.
        // Шов нотификатора — для юнит-тестов дедупа (как IKnowledgeAlertNotifier).
        services.AddSingleton<ISubscriptionAlertNotifier, SubscriptionAlertNotifier>(sp =>
            new SubscriptionAlertNotifier(
                sp.GetRequiredService<NotificationService>(),
                sp.GetRequiredService<IUserStore>()));
        services.AddSingleton<SubscriptionWindowMismatchGuard>();

        // Стартовый прогрев + идл-пинг утилизации подписок (пробный ход на простаивающий
        // аккаунт).
        services.AddGatedHostedService<SubscriptionUsageWarmupService>(config);

        // Точная утилизация обоих окон (5ч + неделя) каждого аккаунта через
        // api/oauth/usage; singleton — статусы опроса per-аккаунт (токен не подходит /
        // ошибка) читает /api/usage.
        services.AddSingleton<SubscriptionOAuthUsageService>();
        services.AddGatedHostedFrom(config, sp => sp.GetRequiredService<SubscriptionOAuthUsageService>());

        // WorkflowAgentParser / WorkflowWatcher / WorkflowMetaResolver — статические
        // парсеры транскриптов; DI не нужны (Program.cs:~788-792 ставит логгеры
        // и кеш корней после Build).
    }
}
