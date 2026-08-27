using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Схемы инструментов сервера персон: копия контракта mcp/personas-server/index.js
/// (источник контракта — здесь, index.js заморожен; сторож парности — PersonasToolsetParityTests).
/// Отдельный файл части класса: логика вызовов и словарь схем читаются независимо,
/// как в index.js (TOOLS отдельно от callTool).
///
/// Группы соответствуют модулям сервера и порядку stdio-ветки: ядро → manage-голова →
/// привязки (read + manage) → knowledge_search → automation → manage-хвост → mentions.
/// internal — для сторожа парности.
/// </summary>
public sealed partial class PersonasToolset
{
    // Ссылка на справочник сервера (instructions ответа initialize): развёрнутые описания
    // слотов характера, привязок и triggerArgs живут ТАМ — в схемах их дублировать дорого
    private const string SeeInstructions = "см. инструкции сервера personas";

    private static readonly string[] Colors =
        ["yellow", "orange", "blue", "green", "purple", "red", "brown", "cyan", "pink"];

    private static readonly string[] Specialties =
    [
        "none", "analyst", "planner", "reviewer", "executor", "secretary",
        "coordinator", "mentor", "designer", "consultant", "librarian", "tester",
        "backendExecutor", "frontendExecutor", "devopsExecutor",
    ];

    private static JsonArray StrEnum(params string[] values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    private static JsonObject Str(string? description = null) =>
        description is null
            ? new JsonObject { ["type"] = "string" }
            : new JsonObject { ["type"] = "string", ["description"] = description };

    private static JsonObject StrList(string? description = null)
    {
        var schema = new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["type"] = "string" },
        };
        if (description is not null) schema["description"] = description;
        return schema;
    }

    private static McpToolSchema Tool(string name, string description, JsonObject schema) =>
        new(name, description, schema);

    private static JsonObject Obj(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject { ["type"] = "object" };
        if (required.Length > 0) schema["required"] = StrEnum(required);
        schema["properties"] = properties;
        return schema;
    }

    // Общие поля персоны для create/update (кроме name — у create он обязателен)
    private static JsonObject PersonaFields() => new()
    {
        ["role"] = Str("Роль — главное в отображении («Роль (Имя)»), например «Дизайнер»"),
        ["specialty"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = StrEnum(Specialties),
            ["description"] = "Специализация для оркестрации команды (роутинг типовых сабагентов на персону); "
                + "executor/tester при access \"full\" даёт право править файлы — выставляй осознанно",
        },
        ["description"] = Str("Короткое описание, кто это (для карточки)"),
        ["character"] = Str($"Характер на «ты» («Ты — …»), 2-5 предложений; слоты — {SeeInstructions}"),
        ["tone"] = Str(),
        ["mustDo"] = StrList(),
        ["mustNot"] = StrList(),
        ["outputFormat"] = Str(),
        ["speechExamples"] = StrList(),
        ["systemPrompt"] = Str("УСТАРЕЛО: единый текст характера — используй character"),
        ["modelTier"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = StrEnum("strong", "medium", "weak"),
            ["description"] = "strong — архитектура, ревью, запутанный баг; medium — работа по плану; "
                + "weak — рутина. Сомневаешься — не указывай",
        },
        ["effort"] = Str("Усилие рассуждения модели"),
        ["color"] = new JsonObject { ["type"] = "string", ["enum"] = StrEnum(Colors) },
        ["greeting"] = Str(),
        ["memoryEnabled"] = new JsonObject
        {
            ["type"] = "boolean",
            ["description"] = "Долгая память персоны (по умолчанию включена)",
        },
        ["handle"] = Str("@handle (латинский slug); пусто при создании — авто из имени"),
    };

    // Привязка персоны: источник знаний или правило с условием применения
    private static JsonObject BindingItemSchema() => Obj(new JsonObject
    {
        ["type"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = StrEnum("project", "projectPath", "knowledge", "notes", "tool", "skill",
                "projectPersonas", "projectTasks"),
            ["description"] = $"формат привязки — {SeeInstructions}",
        },
        ["target"] = Str(),
        ["path"] = Str(),
        ["condition"] = Str(),
        ["mode"] = new JsonObject { ["type"] = "string", ["enum"] = StrEnum("auto", "always", "off") },
    }, "type", "target");

    // Поля правила проактивности (общие для create/update)
    private static JsonObject AutomationFields(bool inProject) => new()
    {
        ["name"] = Str("Человекочитаемое имя правила («Следить за релизами») — видно в списке "
            + "и в заголовке чата правила"),
        ["enabled"] = new JsonObject
        {
            ["type"] = "boolean", ["description"] = "Включено ли правило (по умолчанию true)",
        },
        ["triggerType"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = StrEnum("timer", "file", "note", "gitCommit", "taskStatus", "mention"),
            ["description"] = "timer — по расписанию; file/note/gitCommit/taskStatus — опрос изменений; "
                + "mention — по @упоминанию handle персоны",
        },
        ["triggerArgs"] = new JsonObject
        {
            ["type"] = "object", ["description"] = "Параметры триггера, форма зависит от triggerType",
        },
        ["conditionOnlyIf"] = Str("Доп. условие для LLM-гейта («только если касается деплоя»)"),
        ["quietFrom"] = Str("Начало тихих часов \"HH:mm\" (местное время владельца)"),
        ["quietTo"] = Str("Конец тихих часов \"HH:mm\" (можно через полночь)"),
        ["minIntervalMinutes"] = new JsonObject
        {
            ["type"] = "integer", ["minimum"] = 1,
            ["description"] = "Минимальный интервал срабатываний; дефолт 5 мин (file — 1 мин)",
        },
        ["actionWeight"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = StrEnum("gate", "work"),
            ["description"] = "gate — оценить и коротко ответить; work — полноценный агентский ход",
        },
        ["actionInstruction"] = Str("Инструкция себе на реакцию при срабатывании"),
        ["rememberInHistory"] = new JsonObject
        {
            ["type"] = "boolean", ["description"] = "Писать карточку-итог в историю чата правила",
        },
        ["actionExpiresAfterMinutes"] = new JsonObject
        {
            ["type"] = new JsonArray { "integer", "null" },
            ["description"] = "TTL чата правила, мин; null — бессрочно; не указывай — дефолт 1440",
        },
    };

    // --- Ядро: доступно всегда, пока сервер персон включён ---

    internal static IReadOnlyList<McpToolSchema> CoreTools(bool inProject)
    {
        var context = inProject
            ? "Контекст — текущий проект: для проектной персоны projectId можно не указывать."
            : "Контекст — чат вне проекта: по умолчанию создаются глобальные персоны.";
        return
        [
            Tool("personas_list",
                $"Перечислить персон пользователя. {context} scope: \"context\" — доступные здесь "
                + "(глобальные + текущего проекта, по умолчанию); \"project\" — только текущего проекта; "
                + "\"global\" — только глобальные; \"all\" — все персоны владельца.",
                Obj(new JsonObject
                {
                    ["scope"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = StrEnum("context", "project", "global", "all"),
                        ["description"] = "Какие персоны показать (по умолчанию context)",
                    },
                })),
            Tool("personas_get",
                "Получить полный профиль персоны по id (роль, характер, модель, зона, приветствие).",
                Obj(new JsonObject { ["id"] = Str("ID персоны") }, "id")),
            // ВСЕГДА в составе (инвариант стабильности): ограничение — на бэкенде, вызов из
            // чата разрешён только онбординг-сессии, в остальных сервер откажет
            Tool("personas_set_default",
                "Назначить персону дефолтной: глобальную — личной дефолт-персоной пользователя, "
                + "проектную — руководителем её проекта. Для чата-онбординга: задаёт назначенную персону "
                + "(как созданную через personas_create, так и выбранную из существующих) ПОСЛЕ явного "
                + "подтверждения пользователем. Права выбранной существующей персоны НЕ меняй — вызывай "
                + "только этот инструмент. В обычных чатах бэкенд откажет — тогда предложи пользователю "
                + "сменить дефолт в настройках.",
                Obj(new JsonObject { ["personaId"] = Str("ID персоны, назначаемой дефолтной") }, "personaId")),
        ];
    }

    // --- Модуль manage: голова (создание и правка персон) ---

    internal static IReadOnlyList<McpToolSchema> ManageHeadTools(bool inProject)
    {
        var context = inProject
            ? "Контекст — текущий проект: для проектной персоны projectId можно не указывать."
            : "Контекст — чат вне проекта: по умолчанию создаются глобальные персоны.";
        var createProps = PersonaFields();
        createProps["name"] = Str("Имя персоны");
        createProps["scope"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = StrEnum("global", "project"),
            ["description"] = "Зона персоны: global (дефолт) — во всех чатах; project — в своём проекте",
        };
        createProps["projectId"] = Str("ID проекта для scope=project (дефолт — проект сессии)");
        createProps["avatarPrompt"] =
            Str("Внешность для фото-аватара; пусто — из имени и роли (фото создаётся само)");
        createProps["bindings"] = new JsonObject
        {
            ["type"] = "array",
            ["items"] = BindingItemSchema(),
            ["description"] = "Явные привязки источников знаний и правил",
        };
        createProps["autoBindings"] = new JsonObject
        {
            ["type"] = "boolean",
            ["description"] = "true — после создания AI сам подберёт привязки под роль персоны",
        };

        var updateProps = PersonaFields();
        updateProps["id"] = Str("ID персоны");
        updateProps["name"] = Str("Новое имя");
        updateProps["scope"] = new JsonObject
        {
            ["type"] = "string", ["enum"] = StrEnum("global", "project"),
            ["description"] = "Новая зона персоны",
        };
        updateProps["projectId"] = Str("ID проекта для scope=project");
        updateProps["bindings"] = new JsonObject
        {
            ["type"] = "array",
            ["items"] = BindingItemSchema(),
            ["description"] = "Полная замена набора привязок персоны",
        };

        return
        [
            Tool("personas_create",
                $"Создать персону — AI-собеседника с именем, ролью и характером. {context} "
                + $"Заполняй ВСЕ слоты характера — {SeeInstructions}.",
                Obj(createProps, "name")),
            Tool("personas_update",
                "Изменить персону: поля как при создании, передавай только изменяемые. "
                + "Пустая строка очищает role/effort/color/greeting (specialty — \"none\"). "
                + "Смена scope на \"project\" требует projectId."
                + " bindings — ПОЛНАЯ замена набора привязок (свои собственные менять нельзя).",
                Obj(updateProps, "id")),
        ];
    }

    // --- Привязки: чтение (доступно всем, у кого включён сервер персон) ---

    internal static readonly IReadOnlyList<McpToolSchema> BindingsReadTools =
    [
        Tool("personas_bindings_list",
            "Привязки персоны: источники знаний (проекты, папки, базы знаний, заметки, скиллы) "
            + "и правила инструментов с условиями «когда применять».",
            Obj(new JsonObject { ["id"] = Str("ID персоны") }, "id")),
        Tool("personas_suggest_bindings",
            "AI-подбор привязок под роль персоны (по каталогу проектов/баз/заметок/скиллов "
            + "владельца). Возвращает кандидатов, НЕ сохраняет — сохрани нужные через personas_bindings_set.",
            Obj(new JsonObject { ["id"] = Str("ID персоны") }, "id")),
        Tool("personas_mcp_list",
            "MCP-серверы личного реестра владельца: key, label, транспорт, статус, "
            + "выдан ли персоне (поле enabledForPersona при id). "
            + "Сервер по умолчанию НЕ выдан ни одной персоне — его нужно выдать явно. "
            + "Выдать/отозвать один сервер точечно (не трогая остальные привязки) — personas_mcp_grant; "
            + "полная замена набора привязок — personas_bindings_set/bindings с target \"mcp:<ключ>\".",
            Obj(new JsonObject
            {
                ["id"] = Str("ID персоны — проверить, выдан ли ей каждый сервер "
                    + "(без id — только список серверов)"),
            })),
    ];

    // --- Привязки: правка чужой персоны — только с модулем manage ---

    internal static readonly IReadOnlyList<McpToolSchema> ManageBindingsTools =
    [
        Tool("personas_bindings_set",
            "Полная замена набора привязок персоны (пустой массив — убрать все). "
            + "Свои собственные привязки персона менять не может.",
            Obj(new JsonObject
            {
                ["id"] = Str("ID персоны"),
                ["bindings"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = BindingItemSchema(),
                    ["description"] = "Новый набор привязок",
                },
            }, "id", "bindings")),
        Tool("personas_mcp_grant",
            "Выдать или отозвать персоне один MCP-сервер личного реестра — точечно, "
            + "не затрагивая остальные её привязки (в отличие от personas_bindings_set, который "
            + "заменяет весь набор). "
            + "Сервер по умолчанию НЕ выдан: выдай его (revoke=false), чтобы персона получила "
            + "инструменты сервера в своих чатах. "
            + "Свои собственные доступы персона менять не может.",
            Obj(new JsonObject
            {
                ["id"] = Str("ID персоны (не себя)"),
                ["key"] = Str("Ключ MCP-сервера (см. personas_mcp_list)"),
                ["revoke"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "true — отозвать сервер у персоны, false (дефолт) — выдать",
                },
            }, "id", "key")),
    ];

    // --- Поиск по привязанной базе знаний ---

    internal static readonly McpToolSchema KnowledgeSearchTool =
        Tool("knowledge_search",
            "Гибридный поиск (смысловой + полнотекстовый) по привязанной базе знаний (Dify) "
            + "по её datasetId. Используй, когда выполняется условие привязки-«базы знаний» из твоего "
            + "контекста. Возвращает metadataFields (по каким полям можно фильтровать) и hits — выдержки "
            + "(документ, score, текст, metadata: напр. дата встречи/источник), по ним датируй и "
            + "атрибутируй факты. Диапазоны дат не поддерживаются — для периода бери contains/start with "
            + "по «2025-09»/«2026».",
            Obj(new JsonObject
            {
                ["datasetId"] = Str("ID датасета из строки привязки"),
                ["query"] = Str("Запрос на естественном языке (по смыслу вопроса)"),
                ["topK"] = new JsonObject
                {
                    ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 20,
                    ["description"] = "Сколько выдержек (дефолт 6)",
                },
                ["filters"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Фильтры по метаданным документов — только по полям из metadataFields "
                        + "(сделай сначала поиск без фильтра, чтобы их увидеть)",
                    ["items"] = Obj(new JsonObject
                    {
                        ["name"] = Str("Имя поля метаданных (напр. meeting_date, meeting_source)"),
                        ["operator"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = StrEnum("contains", "not contains", "start with", "end with",
                                "is", "is not", "empty", "not empty"),
                            ["description"] = "Строковый оператор",
                        },
                        ["value"] = Str("Значение (не нужно для empty/not empty)"),
                    }, "name", "operator"),
                },
                ["logic"] = new JsonObject
                {
                    ["type"] = "string", ["enum"] = StrEnum("and", "or"),
                    ["description"] = "Как объединять несколько фильтров (по умолчанию and)",
                },
            }, "datasetId", "query"));

    // --- Модуль automation: правила проактивности ---

    internal static readonly IReadOnlyList<McpToolSchema> AutomationTools = BuildAutomationTools();

    private static IReadOnlyList<McpToolSchema> BuildAutomationTools()
    {
        var createProps = AutomationFields(inProject: false);
        createProps["id"] = Str("ID персоны, для которой создаётся правило");
        var updateProps = AutomationFields(inProject: false);
        updateProps["id"] = Str("ID персоны");
        updateProps["ruleId"] = Str("ID правила");
        return
        [
            Tool("personas_automation_list",
                "Список правил проактивности персоны — триггеры и условия, при которых она "
                + "сама пишет в закреплённый чат правила без запроса пользователя.",
                Obj(new JsonObject { ["id"] = Str("ID персоны") }, "id")),
            Tool("personas_automation_create",
                "Создать правило проактивности персоны. Можно для ЛЮБОЙ персоны, включая "
                + "саму себя — самоограничений нет. Троттлинг (тихие часы, минимальный интервал, потолок "
                + "срабатываний в час) применяется сервером автоматически поверх твоих настроек.",
                Obj(createProps, "id", "name", "triggerType")),
            Tool("personas_automation_update",
                "Изменить правило проактивности: передавай только изменяемые поля — "
                + "остальные (включая triggerArgs целиком) сохранятся как есть.",
                Obj(updateProps, "id", "ruleId")),
            Tool("personas_automation_delete",
                "Удалить правило проактивности персоны по id.",
                Obj(new JsonObject
                {
                    ["id"] = Str("ID персоны"),
                    ["ruleId"] = Str("ID правила"),
                }, "id", "ruleId")),
            Tool("personas_automation_test",
                "Ручной прогон правила проактивности: синтетическое событие, троттлинг "
                + "игнорируется. Запускает реакцию в фоне, результата не ждёт.",
                Obj(new JsonObject
                {
                    ["id"] = Str("ID персоны"),
                    ["ruleId"] = Str("ID правила"),
                }, "id", "ruleId")),
        ];
    }

    // --- Модуль manage: хвост (удаление, аватар, состав команды) ---

    internal static IReadOnlyList<McpToolSchema> ManageTailTools(bool inProject) =>
    [
        Tool("personas_delete",
            "Удалить персону по id. Действие необратимо: долгая память персоны тоже удаляется.",
            Obj(new JsonObject { ["id"] = Str("ID персоны") }, "id")),
        Tool("personas_generate_avatar",
            "Сгенерировать персоне фото-аватар (AI, fal.ai) и сразу применить его. "
            + "prompt — описание внешности (лучше по-английски); без prompt портрет строится по "
            + "имени/роли/описанию персоны. Занимает ~10-30 секунд.",
            Obj(new JsonObject
            {
                ["id"] = Str("ID персоны"),
                ["prompt"] = Str("Описание внешности для генерации (необязательно)"),
            }, "id")),
        Tool("personas_ai_team",
            "Сгенерировать команду персон под задачу/проект: ИИ по промпту и CLAUDE.md проекта "
            + "предлагает состав 3-6 персон. Возвращает ЧЕРНОВИКИ (members), персон НЕ создаёт — покажи "
            + "состав пользователю и заведи нужных через personas_create (поля черновика те же). "
            + "Требуется проект: вне проекта укажи projectId явно.",
            Obj(new JsonObject
            {
                ["prompt"] = Str("Какая команда нужна и подо что (задача/цели проекта)"),
                ["projectId"] = Str("ID проекта команды (дефолт — проект сессии)"),
            }, "prompt")),
    ];

    // --- @упоминания: спросить другую персону ---

    internal static readonly IReadOnlyList<McpToolSchema> MentionsTools =
    [
        Tool("persona_ask",
            "Спросить другую персону: она ответит от своего лица, в своём характере и со своей "
            + "долгой памятью. Зови, когда пользователь упоминает @handle персоны или нужна её экспертиза. "
            + "Вопрос формулируй самодостаточно — персона не видит этот разговор; контекст передай в context. "
            + "Тёзки из разных проектов: вызов по handle вернёт кандидатов — повтори с personaId.",
            Obj(new JsonObject
            {
                ["handle"] = Str("handle персоны (без @), см. personas_list; не нужен при personaId"),
                ["personaId"] = Str("ID персоны — однозначная альтернатива handle"),
                ["question"] = Str("Самодостаточный вопрос к персоне"),
                ["context"] = Str("Контекст разговора (кратко, только нужное для ответа)"),
            }, "question")),
    ];

    // Имена модульных инструментов — defense-in-depth на вызове (см. CallAsync)
    internal static readonly IReadOnlySet<string> ManageToolNames =
        ManageHeadTools(false).Concat(ManageBindingsTools).Concat(ManageTailTools(false))
            .Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

    internal static readonly IReadOnlySet<string> AutomationToolNames =
        AutomationTools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
}
