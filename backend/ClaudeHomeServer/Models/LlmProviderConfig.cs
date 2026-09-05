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
    // Модель для sonnet-слота (ANTHROPIC_DEFAULT_SONNET_MODEL); пусто — берётся
    // модель сессии. Без среднего слота алиас sonnet схлопывался в модель сессии,
    // и сильная/средняя персон у стороннего провайдера не различались
    public string MediumModel { get; set; } = "";
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
    // Cookie сессии консоли Alibaba Model Studio — для чтения квоты Coding Plan (Token Plan)
    // по шлюзу bailian: публичного API у Token Plan нет, квота отдаётся только web-сессии
    // консоли и авторизуется ЭТИМ cookie, а НЕ ApiKey. Секрет — только в appsettings.Local.json;
    // в отслеживаемом appsettings.json пусто. Источник Balance="alibaba".
    public string ConsoleCookie { get; set; } = "";
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

    // Тройки моделей провайдера для слотов сильная/средняя/слабая ("Поставщики моделей v2").
    // Заполняются из конфига; null — слот не назначен, UI прячет чипсу.
    public string? TierStrong { get; set; }
    public string? TierMedium { get; set; }
    public string? TierWeak { get; set; }

    // Локальный провайдер (vLLM/llama.cpp на своей машине) — без API-ключа.
    // Признак используется не только для Enabled: этап 2 (проба живости эндпоинта,
    // скрытие блока баланса, исключение из егресс-логики) повесит на него ещё три места.
    public bool IsLocal { get; set; }

    public bool Enabled =>
        (IsLocal || !string.IsNullOrWhiteSpace(ApiKey)) && !string.IsNullOrWhiteSpace(AnthropicBaseUrl);

    // Список усилий рассуждений, которые Anthropic-совместимый эндпойнт провайдера реально
    // принимает в поле effort. Пустой — реестр не трогает значение (fail-open для glm/kimi/
    // minimax, у которых effort не валидируется). Заданный список срабатывает по правилу
    // «ближайший поддерживаемый СНИЗУ» по шкале low < medium < high < xhigh < max: незнакомый
    // провайдеру уровень (CLI заведёт новый) уедет как есть и вернёт 400, поэтому при
    // отсутствии в списке подменяем ближайшим, который провайдер точно примет.
    public List<string> SupportedEfforts { get; set; } = [];

    // Явная карта подмены reasoning effort: EffortMap[запрошенный] → подменённое. Приоритет
    // ВЫШЕ SupportedEfforts — конфигурация доверенная, и «high → medium» декларативнее правила
    // «ближайший снизу» (тот же результат в большинстве случаев, но карта прозрачна при разборе
    // инцидентов). Пустая — работает только SupportedEfforts. vLLM/llama.cpp принимают только
    // low/medium/xhigh (high→400); у local-qwen карта сознательно жмёт high/medium → low:
    // effort у Qwen3.8 управляет длиной размышлений, и на low ход отвечает за секунды.
    public Dictionary<string, string> EffortMap { get; set; } = [];

    // Лимит токенов на блок thinking (env MAX_THINKING_TOKENS — имя по документации CLI;
    // CLAUDE_CODE_MAX_THINKING_TOKENS в бинарнике 2.1.241 отсутствует). null/0 — CLI решает
    // сам. Оговорка: на стороннем провайдере CLI по этой переменной лишь убирает параметр
    // thinking из запроса, бюджет сервером не режется — длиной размышлений у Qwen3.8 на деле
    // управляет effort (см. EffortMap), а эта переменная только страхует от лишнего параметра.
    public int? MaxThinkingTokens { get; set; }

    // Урезать состав MCP-серверов до минимума (false — обычный состав). Для провайдеров с
    // маленькимом окном (vLLM/llama.cpp, qwen3.8-27b на 65k) описания MCP-инструментов
    // едят десятки тысяч токенов служебного контекста — на local-qwen замерено 26 557
    // токенов только на MCP при общем 65k окне (см. замер 2026-09-05). false по умолчанию —
    // родной Claude и облачные провайдеры работают как раньше. Состав зависит только от
    // свойства сессии (EffectiveModel → провайдер), не от хода: McpToolsetStabilityTests
    // остаётся зелёным, сигнатура запуска стабильна в пределах одной сессии.
    public bool TrimMcpServers { get; set; }

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
