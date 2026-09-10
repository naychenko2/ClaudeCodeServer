namespace ClaudeHomeServer.Services;

// Запись каталога моделей для фонового автоподбора (LocalActionPresetService).
// Чистый DTO — примитивы и одна nullable строка, никаких зависимостей на Main.
//
// Раньше жил nested-типом `ModelCatalogService.ModelInfo` в Main. На этапе 5,
// волна 1 выноса Llm, едет в Core с переименованием: ModelInfo как имя слишком
// общее (в проекте уже есть ImageModelInfo — риск коллизии при рефакторинге).
// Имя ModelCatalogEntry явно говорит, откуда запись и зачем.
public sealed record ModelCatalogEntry(
    string Value,
    string DisplayName,
    string? Description,
    string Provider = "claude",
    int? ContextWindow = null,
    bool IsCurated = true);

// Шов каталога моделей для выноса Llm. Llm (LocalActionPresetService) использует
// только список моделей — без знаний об опросе CLI, провайдеров и кэша. Полный
// ModelCatalogService остаётся в Main, Llm видит узкую обёртку.
//
// Прецедент: IKnowledgeNotificationDispatcher (Core) + KnowledgeNotificationDispatcher
// (Main Composition/Notifications) — та же форма «один метод — узкий шов».
public interface IModelCatalog
{
    Task<IReadOnlyList<ModelCatalogEntry>> GetModelsAsync(CancellationToken ct = default);
}
