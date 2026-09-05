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
    // едят десятки тысяч токенов служебного контекста — на local-qwen замерено 46 480
    // токенов только на MCP при общем 245 760 окне (замер 2026-09-05, окно CTX=huge; прежний
    // замер 26 557 устарел после добавления watch и личного реестра серверов). false по
    // умолчанию — родной Claude и облачные провайдеры работают как раньше. Состав зависит
    // только от свойства сессии (EffectiveModel → провайдер), не от хода:
    // McpToolsetStabilityTests остаётся зелёным, сигнатура запуска стабильна в пределах одной
    // сессии. Точечное исключение описывается полем KeepMcpServers ниже.
    public bool TrimMcpServers { get; set; }

    // Белый список ключей MCP-серверов, которые ОСТАЮТСЯ при TrimMcpServers=true. Пусто —
    // прежнее поведение «всё или ничего» (гасится всё, кроме desktop). Решает
    // «выключить почти всё, оставить только нужное локальному исполнителю»: на local-qwen
    // оставляем только tasks, без него исполнитель не закрывает задачу через tasks_complete.
    // Ключи — те же, что в сборке `servers` BuildTurnMcpConfig: задаются с тем же регистром,
    // сравнение по OrdinalIgnoreCase (см. ClaudeSession.BuildTurnMcpConfig, блок if (trimMcp)).
    // Категории user/external сбрасываются скопом (это многосерверные группы, ключи внутри
    // произвольные), consultants/modules — единые флаги из той же логики. desktop всегда
    // остаётся: десктопные чаты работают через эту грань, и стоит он дёшево.
    public string[] KeepMcpServers { get; set; } = [];

    // Запускать CLI в режиме --bare: без автозагрузки CLAUDE.md, хуков, LSP,
    // плагинов и авто-памяти. Контекст подаётся явно через SystemPromptFile.
    // Дефолт false — родной Claude и облачные провайдеры не задеты.
    public bool BareMode { get; set; }

    // Путь к краткой карте проекта для --system-prompt-file (при BareMode).
    // Пусто — флаг не ставится.
    public string? SystemPromptFile { get; set; }

    // Встроенные инструменты CLI, возвращаемые при BareMode (флаг --tools).
    // Важно: --tools только СУЖАЕТ встроенный набор — расширить его сверх того, что
    // CLI отдаёт под --bare, нельзя. Замерено 2026-09-05 на qwen3.8-27b локально:
    //   --bare без --tools    → Bash, Edit, PowerShell, Read
    //   --bare --tools default → Bash, Edit, PowerShell, Read
    //   --bare --tools "Bash Edit Read Write Glob Grep" → Bash, Edit, Read
    //   без --bare, тот же список → Bash, Edit, Glob, Grep, Read, Write
    // Т.е. Write/Glob/Grep под --bare НЕДОСТУПНЫ, их роль исполняет Bash
    // (printf > file, find, grep). Это совпадает с курсом CLI 2.1.116: Glob/Grep
    // удалены в пользу bfs/ugrep через Bash на macOS/Linux. Под --bare физический
    // потолок — Bash/Edit/Read/PowerShell. Задание BareTools=["Bash","Edit","Read"]
    // регрессирует против дефолта (отнимает PowerShell) — НЕ делать так.
    // Пусто — флаг --tools не ставится (CLI оставляет дефолтный набор --bare).
    public string[] BareTools { get; set; } = [];

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
