using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Схемы инструментов сервера рабочего пространства: копия контракта
/// mcp/workspace-server/index.js (источник контракта — здесь, index.js заморожен;
/// сторож парности — WorkspaceToolsetParityTests). Отдельный файл части класса: логика
/// вызовов и словарь схем читаются независимо, как в index.js (SECTION_TOOLS отдельно
/// от callTool).
///
/// Порядок соответствует stdio-ветке: projects → files → knowledge → search → git →
/// git_write → knowledge_bases → chats → destructive → deploy.
/// Описания files_delete/chats_delete/deploy_* — часть защиты от удаления лишнего по
/// инициативе модели, формулировки менять нельзя (инвариант задачи волны 3).
/// </summary>
public sealed partial class WorkspaceToolset
{
    // Плейсхолдер контекстной заметки в описании projects_list: у stdio-ветки она собиралась
    // из env при старте процесса, здесь подставляется живьём при каждом tools/list
    private const string ContextNoteToken = "{CONTEXT_NOTE}";

    internal static IReadOnlyList<McpToolSchema> AllTools { get; } = BuildAll();

    private static IReadOnlyList<McpToolSchema> BuildAll() =>
    [
        // --- projects ---
        Tool("projects_list",
            "Список проектов пользователя (id, название, группа, путь, число чатов). " + ContextNoteToken,
            Obj(new JsonObject
            {
                ["query"] = Str("Фильтр по названию (подстрока, без учёта регистра)"),
            })),
        Tool("projects_get",
            "Карточка проекта по id: путь, системный промпт, группа, число чатов.",
            Obj(new JsonObject
            {
                ["projectId"] = Str("ID проекта"),
            }, "projectId")),
        Tool("projects_create",
            "Создать новый проект. Без rootPath на диске СОЗДАЁТСЯ папка в стандартном каталоге "
            + "проектов пользователя; с rootPath подключается существующая папка (должна существовать).",
            Obj(new JsonObject
            {
                ["name"] = Str("Название проекта"),
                ["rootPath"] = Str("Абсолютный путь к существующей папке (пусто — создать новую в каталоге по умолчанию)"),
                ["groupId"] = Str("ID группы проектов (пусто — без группы)"),
            }, "name")),
        Tool("projects_update",
            "Обновить проект: название, системный промпт, группа. Передавай только изменяемые поля; "
            + "groupId \"\" убирает проект из группы.",
            Obj(new JsonObject
            {
                ["projectId"] = Str("ID проекта"),
                ["name"] = Str("Новое название"),
                ["systemPrompt"] = Str("Системный промпт проекта (заменяется целиком)"),
                ["groupId"] = Str("ID группы (\"\" — убрать из группы)"),
            }, "projectId")),
        Tool("tags_apply",
            "Присвоить теги сущности (сессии или задаче): новые теги объединяются с уже "
            + "висящими, дубликаты без учёта регистра не плодятся. Недостающие теги автоматически "
            + "попадают в реестр тегов проекта. entityType=\"session\" — projectId обязателен; "
            + "entityType=\"task\" — projectId опционален (нужен только для автосоздания в реестре, "
            + "личные задачи работают без него).",
            Obj(new JsonObject
            {
                ["entityType"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = StrEnum("session", "task"),
                    ["description"] = "Тип сущности",
                },
                ["entityId"] = Str("ID сессии или задачи"),
                ["projectId"] = Str("ID проекта (для session обязательно; для task опционально — для автосоздания тегов в реестре)"),
                ["tags"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = "Теги для присвоения (добавляются к существующим, без удаления)",
                },
            }, "entityType", "entityId", "tags")),

        // --- files ---
        Tool("files_tree",
            "Дерево файлов проекта (рекурсивно). Большая выдача усекается — уточняй path/depth.",
            Obj(new JsonObject
            {
                ["projectId"] = Str("ID проекта"),
                ["path"] = Str("Стартовая папка (относительный путь; пусто — корень проекта)"),
                ["depth"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 1,
                    ["description"] = "Максимальная глубина вложенности от стартовой папки",
                },
            }, "projectId")),
        Tool("files_read",
            "Прочитать текстовый файл проекта. Для бинарных возвращаются только тип и размер. "
            + "Выдача обрезается до 2000 строк — читай длинный файл кусками (offset/limit), "
            + "а нужное место ищи files_search/knowledge_search: прочитанное остаётся в контексте до конца сессии.",
            Obj(new JsonObject
            {
                ["projectId"] = Str("ID проекта"),
                ["path"] = Str("Относительный путь файла"),
                ["offset"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 0,
                    ["description"] = "С какой строки читать (0 по умолчанию)",
                },
                ["limit"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 1,
                    ["description"] = "Сколько строк вернуть (по умолчанию 2000)",
                },
            }, "projectId", "path")),
        Tool("files_document_read",
            "Конвертировать бинарный документ проекта (pdf/docx/xlsx/pptx) в Markdown и вернуть текст "
            + "(markitdown, без модели). Так можно «прочитать» офисный документ, который files_read не отдаёт.",
            Obj(new JsonObject
            {
                ["projectId"] = Str("ID проекта"),
                ["path"] = Str("Путь к документу"),
            }, "projectId", "path")),
        Tool("files_document_summary",
            "Краткое содержание документа проекта (pdf/docx/xlsx/pptx): 5-8 пунктов сути. "
            + "Бесплатная локальная модель, если настроена.",
            Obj(new JsonObject
            {
                ["projectId"] = Str("ID проекта"),
                ["path"] = Str("Путь к документу"),
            }, "projectId", "path")),
        Tool("files_document_extract",
            "Структурная выжимка из документа: {decisions, dates, people, actionItems}. Локальная модель.",
            Obj(new JsonObject
            {
                ["projectId"] = Str("ID проекта"),
                ["path"] = Str("Путь к документу"),
            }, "projectId", "path")),
        Tool("files_to_markdown",
            "Трансформировать ЛЮБОЙ файл (pdf/docx/xlsx/pptx/html/csv и др.) в Markdown и СОХРАНИТЬ его "
            + "(markitdown, без модели). По умолчанию .md кладётся рядом с исходником; можно указать targetDir — "
            + "папку назначения (относительно корня проекта, создаётся при отсутствии). Возвращает { savedPath }. "
            + "Используй, когда просят «трансформировать/переделать файл в markdown» и (опционально) сохранить в папку.",
            Obj(new JsonObject
            {
                ["projectId"] = Str("ID проекта"),
                ["path"] = Str("Путь исходного файла"),
                ["targetDir"] = Str("Папка назначения (относительно корня проекта). Пусто — рядом с исходником."),
                ["enhance"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "Восстановить Markdown-разметку локальной моделью (заголовки/списки/выделения) — полезно для PDF, дающих плоский текст. По умолчанию false.",
                },
            }, "projectId", "path")),
        Tool("files_write",
            "Записать файл в ДРУГОМ проекте (создаёт при отсутствии, содержимое заменяется целиком). "
            + "Только для ДРУГИХ проектов! Для файлов текущего проекта используй встроенные Read/Edit/Write.",
            Obj(new JsonObject
            {
                ["projectId"] = Str("ID проекта"),
                ["path"] = Str("Относительный путь файла"),
                ["content"] = Str("Полное новое содержимое файла"),
            }, "projectId", "path", "content")),
        Tool("files_search",
            "Поиск файлов проекта по имени (подстрока).",
            Obj(new JsonObject
            {
                ["projectId"] = Str("ID проекта"),
                ["query"] = Str("Подстрока имени файла"),
            }, "projectId", "query")),
        Tool("files_mkdir",
            "Создать папку в проекте (родительские папки создаются автоматически).",
            Obj(new JsonObject
            {
                ["projectId"] = Str("ID проекта"),
                ["path"] = Str("Относительный путь новой папки"),
            }, "projectId", "path")),
        Tool("files_rename",
            "Переименовать или переместить файл/папку проекта.",
            Obj(new JsonObject
            {
                ["projectId"] = Str("ID проекта"),
                ["oldPath"] = Str("Текущий относительный путь"),
                ["newPath"] = Str("Новый относительный путь"),
            }, "projectId", "oldPath", "newPath")),

        // --- knowledge ---
        Tool("knowledge_search",
            "Семантический поиск по базе знаний проекта (проиндексированные документы). Возвращает чанки со score.",
            Obj(new JsonObject
            {
                ["projectId"] = Str("ID проекта"),
                ["query"] = Str("Поисковый запрос (естественный язык)"),
                ["topK"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 1,
                    ["maximum"] = 20,
                    ["description"] = "Сколько чанков вернуть (по умолчанию 8)",
                },
            }, "projectId", "query")),
        Tool("knowledge_status",
            "Статус базы знаний проекта: проиндексирована ли и список документов.",
            Obj(new JsonObject
            {
                ["projectId"] = Str("ID проекта"),
            }, "projectId")),
        Tool("knowledge_index",
            "Добавить файл проекта в базу знаний: документ загружается сразу, индексация "
            + "продолжается в фоне (статус — через knowledge_status). Поддерживаются не все форматы.",
            Obj(new JsonObject
            {
                ["projectId"] = Str("ID проекта"),
                ["path"] = Str("Относительный путь файла"),
            }, "projectId", "path")),

        // --- search ---
        Tool("search_unified",
            "Единый поиск по рабочему пространству пользователя (заметки + задачи, по смыслу и тексту). "
            + "Первый шаг, когда нужно найти «что-то где-то у пользователя».",
            Obj(new JsonObject
            {
                ["query"] = Str("Поисковый запрос"),
                ["limit"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 1,
                    ["maximum"] = 20,
                    ["description"] = "Максимум результатов (по умолчанию 8)",
                },
            }, "query")),

        // --- git (чтение) ---
        Tool("git_status",
            "Статус git-репозитория проекта: текущая ветка, upstream, staged/unstaged/untracked файлы. "
            + "Если папка проекта не git-репозиторий — возвращается ошибка.",
            Obj(new JsonObject
            {
                ["projectId"] = Str("ID проекта"),
            }, "projectId")),
        Tool("git_diff",
            "Diff файла проекта: рабочие правки (staged=false, по умолчанию) либо проиндексированные (staged=true). "
            + "path — относительный путь файла.",
            Obj(new JsonObject
            {
                ["projectId"] = Str("ID проекта"),
                ["path"] = Str("Относительный путь файла"),
                ["staged"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "true — diff проиндексированных изменений (git diff --staged); false — рабочих (по умолчанию)",
                },
            }, "projectId", "path")),
        Tool("git_log",
            "История коммитов проекта (последние limit, по умолчанию 100). branch — конкретная ветка (пусто — текущая).",
            Obj(new JsonObject
            {
                ["projectId"] = Str("ID проекта"),
                ["limit"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 1,
                    ["description"] = "Сколько коммитов вернуть (по умолчанию 100)",
                },
                ["branch"] = Str("Ветка (пусто — текущая)"),
            }, "projectId")),

        // --- git_write (запись) ---
        Tool("git_commit",
            "Зафиксировать проиндексированные изменения проекта коммитом с сообщением message. "
            + "Возвращает sha созданного коммита. Требует непустого сообщения.",
            Obj(new JsonObject
            {
                ["projectId"] = Str("ID проекта"),
                ["message"] = Str("Сообщение коммита"),
            }, "projectId", "message")),
        Tool("git_stage",
            "Проиндексировать (git add) файл проекта по относительному пути path перед коммитом.",
            Obj(new JsonObject
            {
                ["projectId"] = Str("ID проекта"),
                ["path"] = Str("Относительный путь файла"),
            }, "projectId", "path")),

        // --- knowledge_bases ---
        Tool("kb_list",
            "Список баз знаний владельца (личные + публичные): id, название, тип, видимость, число документов. "
            + "Не путать с базой знаний текущего проекта (knowledge_status/knowledge_search).",
            Obj(new JsonObject())),
        Tool("kb_get",
            "Карточка базы знаний по id: метаданные + список документов с их статусом индексации.",
            Obj(new JsonObject
            {
                ["id"] = Str("ID базы знаний"),
            }, "id")),
        Tool("kb_search",
            "Поиск по базе знаний: method=\"semantic\" (по смыслу, по умолчанию) либо \"fulltext\" (точные совпадения). "
            + "Возвращает чанки со score.",
            Obj(new JsonObject
            {
                ["id"] = Str("ID базы знаний"),
                ["query"] = Str("Поисковый запрос"),
                ["topK"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 1,
                    ["maximum"] = 20,
                    ["description"] = "Сколько чанков вернуть (по умолчанию 8)",
                },
                ["method"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = StrEnum("semantic", "fulltext"),
                    ["description"] = "Стратегия поиска (по умолчанию semantic)",
                },
            }, "id", "query")),
        Tool("kb_add_document",
            "Добавить документ в базу знаний текстом: name — имя документа, text — содержимое. "
            + "Индексация продолжается в фоне (статус — через kb_get).",
            Obj(new JsonObject
            {
                ["id"] = Str("ID базы знаний"),
                ["name"] = Str("Имя документа"),
                ["text"] = Str("Текстовое содержимое документа"),
            }, "id", "name", "text")),

        // --- chats ---
        Tool("chats_list",
            "Список чатов пользователя: без projectId — чаты вне проектов, с projectId — сессии проекта. "
            + "Компакт: id, name, status, personaId, model, updatedAt.",
            Obj(new JsonObject
            {
                ["projectId"] = Str("ID проекта (пусто — чаты вне проектов)"),
            })),
        Tool("chats_history",
            "Последние сообщения чата/сессии по id (компактно: user/assistant/tool/result, тексты усечены).",
            Obj(new JsonObject
            {
                ["sessionId"] = Str("ID сессии"),
                ["limit"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 1,
                    ["maximum"] = 200,
                    ["description"] = "Сколько последних сообщений вернуть (по умолчанию 20)",
                },
            }, "sessionId")),
        Tool("chats_create",
            "Создать новый чат: без projectId — вне проектов, с projectId — сессия в проекте; "
            + "personaId — сразу назначить собеседником персону. Возвращает id созданной сессии.",
            Obj(new JsonObject
            {
                ["name"] = Str("Название чата (пусто — авто-имя по первому сообщению)"),
                ["projectId"] = Str("ID проекта (пусто — чат вне проектов)"),
                ["personaId"] = Str("ID персоны-собеседника (пусто — обычный ассистент)"),
                ["model"] = Str("Модель (пусто — по умолчанию)"),
            })),

        Tool("chats_send",
            "Отправить сообщение в СУЩЕСТВУЮЩИЙ чат — полный ход, результат виден пользователю в ленте. "
            + "Для быстрого вопроса персоне без чата используй persona_ask. wait=\"turn\" (дефолт) ждёт ответ до timeoutSec; "
            + "wait=\"none\" — не ждать (результат позже через chats_history). Ответ queued — чат был занят, "
            + "сообщение ПРИНЯТО и уйдёт само после текущего хода: не отправляй повторно, ответ смотри через chats_history.",
            Obj(new JsonObject
            {
                ["sessionId"] = Str("ID сессии-получателя (не своей!)"),
                ["text"] = Str("Текст сообщения"),
                ["wait"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = StrEnum("turn", "none"),
                    ["description"] = "turn — ждать завершения хода (дефолт), none — вернуться сразу",
                },
                ["timeoutSec"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 5,
                    ["maximum"] = 240,
                    ["description"] = "Сколько ждать завершения хода, сек (по умолчанию 90)",
                },
            }, "sessionId", "text")),
        Tool("chats_report_up",
            "Отчитаться в ВЫШЕСТОЯЩИЙ чат — тот, из которого пришла твоя задача (или в который "
            + "тебя сгруппировали). Адресата вычисляет сервер, sessionId указывать не нужно. Отчёт ложится "
            + "карточкой в его ленту и НЕ запускает там ход — человек и агент увидят его, когда дойдут. "
            + "Для промежуточных докладов: «нашёл блокер», «нужен доступ», «сделал половину». Итоговый отчёт "
            + "по завершении задачи слать не нужно — сервер отправит его сам при tasks_complete. "
            + "blocker: true — ты застрял и работа стоит: постановщику запускается ход СРАЗУ, а человек "
            + "получает карточку с твоим текстом и кнопками. Ставь только когда реально не можешь продолжать.",
            Obj(new JsonObject
            {
                ["text"] = Str("Текст отчёта: что сделано, что мешает, что нужно"),
                ["blocker"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "Доклад о блокере: будит постановщика немедленно (по умолчанию false — "
                        + "обычный отчёт, его увидят на следующем ходу)",
                },
            }, "text")),
        Tool("chats_update",
            "Переименовать чат/сессию по id (работает и для чатов вне проектов, и для проектных сессий).",
            Obj(new JsonObject
            {
                ["sessionId"] = Str("ID сессии"),
                ["name"] = Str("Новое название чата"),
            }, "sessionId", "name")),

        // --- destructive (формулировки — часть защиты, не менять) ---
        Tool("files_delete",
            "БЕЗВОЗВРАТНО удалить файл или папку проекта — восстановить нельзя. "
            + "Используй ТОЛЬКО по явной просьбе пользователя удалить конкретный путь, никогда по своей инициативе.",
            Obj(new JsonObject
            {
                ["projectId"] = Str("ID проекта"),
                ["path"] = Str("Относительный путь файла или папки"),
            }, "projectId", "path")),
        Tool("chats_delete",
            "БЕЗВОЗВРАТНО удалить чат/сессию вместе со всей историей сообщений пользователя. "
            + "Используй ТОЛЬКО по явной просьбе пользователя удалить конкретный чат, никогда по своей инициативе.",
            Obj(new JsonObject
            {
                ["sessionId"] = Str("ID сессии"),
            }, "sessionId")),

        // --- deploy ---
        Tool("deploy_start",
            "Запустить выкатку прода: собрать фронт и бэк и переопубликовать сервер. "
            + "ВАЖНО: выкатка ПЕРЕЗАПУСКАЕТ сервер — этот чат и все идущие ходы оборвутся на несколько "
            + "секунд, а итог придёт отдельным сообщением уже от нового процесса. Зови ТОЛЬКО по явной "
            + "просьбе пользователя выкатить или опубликовать прод, никогда по своей инициативе и никогда "
            + "после правок кода «на всякий случай». Собирает и переключает внешний агент: ответ приходит "
            + "сразу и означает приём заявки, а не результат выкатки.",
            Obj(new JsonObject
            {
                ["ref"] = Str("Ветка, на которой СЕЙЧАС стоит рабочее дерево — как подтверждение того, что едет именно она (пусто — без проверки). Переключение веток не поддерживается: агент собирает рабочее дерево как есть, чужой ref = отказ"),
                ["skipFrontend"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "Не пересобирать фронт (только бэк)",
                },
                ["skipSandbox"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "Не пересобирать образ песочницы",
                },
                ["allowDirty"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "Ехать с незакоммиченными изменениями в рабочем дереве. По умолчанию такая выкатка отклоняется — ставь только после явного согласия пользователя",
                },
            })),
        Tool("deploy_status",
            "Ход текущей выкатки прода, история прошлых и список снимков релизов, на которые "
            + "можно откатиться. Ничего не запускает и сервер не трогает — безопасно звать в любой момент, "
            + "в том числе чтобы узнать, чем закончилась прошлая выкатка.",
            Obj(new JsonObject())),
        Tool("deploy_rollback",
            "Вернуть прод на прошлый снимок релиза (по умолчанию предыдущий). ВАЖНО: как и "
            + "выкатка, ПЕРЕЗАПУСКАЕТ сервер — этот чат и идущие ходы оборвутся. Данные (чаты, задачи, "
            + "заметки) при этом НЕ откатываются, возвращается только код. Зови ТОЛЬКО по явной просьбе "
            + "пользователя откатить прод. Список доступных снимков — deploy_status.",
            Obj(new JsonObject
            {
                ["releaseId"] = Str("ID снимка релиза из deploy_status (пусто — предыдущий)"),
            })),
    ];

    // --- Хелперы схем (как у PersonasToolset.Schemas) ---

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

    private static McpToolSchema Tool(string name, string description, JsonObject schema) =>
        new(name, description, schema);

    private static JsonObject Obj(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject { ["type"] = "object" };
        if (required.Length > 0) schema["required"] = StrEnum(required);
        schema["properties"] = properties;
        return schema;
    }
}
