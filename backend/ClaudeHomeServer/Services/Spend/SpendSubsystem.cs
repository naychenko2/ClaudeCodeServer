using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Spend;

// Подсистема Spend: аналитика расхода токенов (Spend Analytics v2) —
// `SpendStore` (хранилище записей + дневные агрегаты, реализует `ISpendCollector`),
// `SpendAnalyticsService` (дашбордные запросы: разрезы по чатам/проектам/задачам/
// персонам/пользователям/провайдерам), `TaskPromptMetricsStore` (замеры размера
// постановки задач по секциям — разрез «Задача» в аналитике) и фоновый
// `SpendMaintenanceService` (backfill истории + rollup за окном).
//
// Границы (сознательные):
// - Источник истины — свой стор `data/spend/*`. Бэкап идёт общим правилом `data/`,
//   отдельно ничего не прописываем.
// - `ISpendCollector` форвардится НА `SpendStore` через явный `sp => sp.GetRequiredService<SpendStore>()`,
//   чтобы интерфейс и конкретный тип указывали на ОДИН инстанс. `AddSingleton<ISpendCollector, SpendStore>()`
//   завёл бы второй экземпляр стора и разъехался с коллектором — данные расползлись бы.
// - `SessionManager`/`ProjectManager`/`TaskManager`/`PersonaManager`/`UserStore`/`ChatHistoryService`
//   из корня Services — «вертикаль → спинка» (общая инфраструктура доменных моделей),
//   как у `Git`/`Tts`/`Images`/`Deploy`.
// - `LlmProviderRegistry` (`Services/Llm`) — шов к слою LLM-провайдеров: цены и
//   возможности нужны для расчёта стоимости токенов, как у `Git`/`Backgrounds`/`Deploy`.
public sealed class SpendSubsystem : IAppSubsystem
{
    public string Key => "spend";

    public string Title => "Аналитика расхода";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<SpendStore>();

        // `ISpendCollector` должен указывать на ТОТ ЖЕ экземпляр `SpendStore`,
        // иначе коллектор и стор разъедутся по данным.
        services.AddSingleton<ISpendCollector>(sp => sp.GetRequiredService<SpendStore>());

        services.AddSingleton<SpendAnalyticsService>();

        // Замеры размера постановки задач по секциям (разрез «Задача» в аналитике).
        services.AddSingleton<TaskPromptMetricsStore>();

        // Фоновое обслуживание: backfill истории + rollup за окном.
        services.AddGatedHostedService<SpendMaintenanceService>(config);
    }
}