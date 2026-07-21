namespace ClaudeHomeServer.Models;

// Сторонний LLM-провайдер с Anthropic-совместимым эндпоинтом (секция "LlmProviders":
// словарь key → конфиг). Чат работает через тот же claude CLI: на процесс хода
// выставляются env ANTHROPIC_BASE_URL/ANTHROPIC_AUTH_TOKEN и маппинг моделей.
// API-ключ — в appsettings.Local.json; пустой ключ = провайдер выключен
// (модели не попадают в каталог, создание сессии недоступно).
public class LlmProviderConfig
{
    // Ключ провайдера из словаря конфига — wire-токен для фронта ("deepseek", "glm").
    // Заполняется реестром при загрузке.
    public string Key { get; set; } = "";

    public string DisplayName { get; set; } = "";
    // Anthropic-совместимый эндпоинт для claude CLI (ANTHROPIC_BASE_URL)
    public string AnthropicBaseUrl { get; set; } = "";
    // Нативный API провайдера (баланс, GET /models); пусто — эти возможности недоступны
    public string ApiBaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    // Модель для haiku-слота и субагентов (ANTHROPIC_DEFAULT_HAIKU_MODEL,
    // CLAUDE_CODE_SUBAGENT_MODEL); пусто — берётся модель сессии
    public string SmallModel { get; set; } = "";
    // Источник состояния аккаунта: "deepseek" (GET /user/balance), "moonshot"
    // (GET /users/me/balance) или "openrouter" (GET /credits) — деньги; "glm"
    // (GET BalanceUrl) — квота подписки Coding Plan в процентах; пусто — нет
    public string Balance { get; set; } = "";
    // Явный URL эндпоинта баланса/квоты — когда он не выводится из ApiBaseUrl
    // (у GLM монитор живёт вне /paas/v4). Пусто — URL строит сам обработчик источника
    public string BalanceUrl { get; set; } = "";
    // Префикс id моделей провайдера — по нему резолвится провайдер для моделей
    // не из конфига (напр. пришедших из GET /models); пусто — используется Key
    public string ModelPrefix { get; set; } = "";
    // Несколько префиксов — для агрегаторов, где id несут имя первоисточника
    // ("anthropic/…", "openai/…", "z-ai/…" у OpenRouter) и общего префикса нет.
    // Задан — полностью заменяет ModelPrefix/Key при резолве по префиксу
    public List<string> ModelPrefixes { get; set; } = [];
    // Опрашивать ли GET {ApiBaseUrl}/models для пополнения каталога
    public bool QueryModelsApi { get; set; }
    public bool SupportsImages { get; set; } = true;
    // Дополнительные env процесса CLI (напр. API_TIMEOUT_MS у Z.ai)
    public Dictionary<string, string> ExtraEnv { get; set; } = [];
    public List<LlmModelConfig> Models { get; set; } = [];

    public bool Enabled =>
        !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(AnthropicBaseUrl);

    public string EffectiveModelPrefix => string.IsNullOrWhiteSpace(ModelPrefix) ? Key : ModelPrefix;

    // Все префиксы для резолва по id модели (см. ModelPrefixes). Пустые строки
    // отбрасываем: такой «префикс» подошёл бы любой модели и увёл бы чужие ходы сюда
    public IReadOnlyList<string> EffectiveModelPrefixes =>
        ModelPrefixes.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() is { Count: > 0 } list
            ? list : [EffectiveModelPrefix];

    public LlmModelConfig? FindModel(string? id) =>
        id is null ? null : Models.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}

// Запись каталога моделей провайдера. Модели меняются (алиасы deepseek-chat/reasoner
// выведены 24.07.2026) — список строго из конфига, без хардкода в коде.
public class LlmModelConfig
{
    // Значение в каталоге моделей (хранится в Session.Model)
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    // Короткое задаче-ориентированное описание для UI (для каких задач модель).
    // У Claude приходит из CLI; у сторонних API его нет — задаём здесь.
    public string? Description { get; set; }
    public int ContextWindow { get; set; } = 1_000_000;
    // Цены $/1M токенов — для расчёта стоимости из usage; 0 → стоимость не считаем
    public double PriceInMissPer1M { get; set; }
    public double PriceInHitPer1M { get; set; }
    public double PriceOutPer1M { get; set; }
}
