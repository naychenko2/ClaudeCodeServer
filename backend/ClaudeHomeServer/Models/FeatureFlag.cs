namespace ClaudeHomeServer.Models;

/// <summary>
/// Определение фич-флага — декларируется в коде (source of truth).
/// </summary>
/// <param name="Key">Стабильный машинный ключ (kebab-case), по нему хранится override юзера.</param>
/// <param name="Title">Человекочитаемое название для тумблера.</param>
/// <param name="Description">Что включает фича.</param>
/// <param name="Default">Значение по умолчанию, когда у юзера нет override.</param>
/// <param name="Stage">Зрелость: "dev" | "beta" | "stable" — только для метки в UI.</param>
public record FeatureFlagDefinition(
    string Key,
    string Title,
    string Description,
    bool Default,
    string Stage);

/// <summary>
/// Константы ключей флагов — использовать вместо строковых литералов,
/// чтобы опечатка не отключала фичу молча.
/// </summary>
public static class FeatureFlagKeys
{
    public const string SessionArtifacts = "session-artifacts";
    public const string Notes = "notes";
    public const string TaskBoard = "task-board";
    // Зонтичный флаг расширенного AI: хаб-палитра + подсказки + вся кросс-раздельная
    // AI-синергия (бриф, единый поиск, задачи из чата, итог сессии, auto-recall, контекст исполнителя).
    public const string AiAssist = "ai-assist";
    // Единый офлайн-режим для заметок и задач.
    public const string Offline = "offline";
    public const string Personas = "personas";
    public const string PersonaMemoryAutolearn = "persona-memory-autolearn";
    // @упоминания персон в чатах: MCP persona_ask, автокомплит @ в композере
    public const string PersonaMentions = "persona-mentions";
    // Привязки персон: источники знаний и правила с условиями применения (индекс в промпте)
    public const string PersonaBindings = "persona-bindings";
    // MCP workspace-server: доступ сессии к проектам/файлам/знаниям/поиску владельца
    public const string WorkspaceTools = "workspace-tools";
    // Секция chats workspace-server: Claude и персоны могут писать в другие чаты (chats_send)
    public const string WorkspaceChatSend = "workspace-chat-send";
    // Секция destructive workspace-server: безвозвратное удаление файлов и чатов (files_delete/chats_delete)
    public const string WorkspaceDestructive = "workspace-destructive";
}

/// <summary>
/// Единственное место, где объявляются фич-флаги. Чтобы добавить новый флаг —
/// допиши строку в <see cref="All"/> (и продублируй ключ в lib/featureFlags.ts на фронте).
/// </summary>
public static class FeatureFlagCatalog
{
    public static readonly IReadOnlyList<FeatureFlagDefinition> All =
    [
        // Панель «Артефакты сессии» — сводка по активной сессии справа от чата:
        // измененные файлы (с дельтами строк), план (из ExitPlanMode), задачи (TodoWrite) и ссылки.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.SessionArtifacts,
            Title: "Артефакты сессии",
            Description: "Панель справа от чата с измененными файлами, планом, задачами, агентами и ссылками за текущую сессию.",
            Default: false,
            Stage: "beta"),

        // Раздел «Заметки» — Obsidian-совместимая база знаний: markdown-заметки с
        // [[wikilinks]], backlinks и графом. Включает связь чекбоксов с задачами.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.Notes,
            Title: "Заметки",
            Description: "База знаний: markdown-заметки со связями [[…]], обратными ссылками и графом; единый граф поверх личного vault и проектов. Плюс чекбоксы `- [ ]` можно превращать в задачи с синхронизацией статуса.",
            Default: false,
            Stage: "beta"),

        // Вид «Доска» (Kanban) в разделе «Календарь»: колонки по статусу,
        // drag & drop, дорожки (swimlanes), фильтры и WIP-лимиты.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.TaskBoard,
            Title: "Доска задач (Kanban)",
            Description: "Вид «Доска» в разделе «Календарь»: колонки по статусу, drag & drop карточек, группировка по дорожкам, фильтры и WIP-лимиты.",
            Default: false,
            Stage: "beta"),

        // Расширенный AI — зонтичный флаг: AI-хаб (палитра ⌘/Ctrl+K + проактивные
        // подсказки) и вся кросс-раздельная AI-синергия одним тумблером.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.AiAssist,
            Title: "Расширенный AI",
            Description: "Единый AI-хаб (палитра ⌘/Ctrl+K + проактивные подсказки) и AI-синергия: утренний бриф, единый поиск по смыслу, задачи из чата, итог сессии в заметку, заметки как память Claude в чате и в контексте исполнителя.",
            Default: false,
            Stage: "dev"),

        // Единый офлайн-режим для заметок и задач: правки без соединения сохраняются
        // локально (IndexedDB) и синхронизируются при возврате связи.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.Offline,
            Title: "Офлайн-режим",
            Description: "Заметки и задачи работают без соединения — просмотр, правка и создание сохраняются на устройстве и синхронизируются с сервером, как только связь вернётся. Конфликты сохраняются копией.",
            Default: false,
            Stage: "beta"),

        // Раздел «Персоны»: имя, аватар, характер, отдельный чат, долгая память и доступ
        // к контексту (глобально или в рамках проекта). Не путать с .md-агентами Claude Code.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.Personas,
            Title: "Персоны",
            Description: "Персоны с именем, аватаром и характером: у каждой свой чат, долгая память и доступ ко всей информации в своей зоне контекста (глобально или по проекту).",
            Default: false,
            Stage: "beta"),

        // Персона сама извлекает факты из диалога в долгую память (авто-обучение) после сессии.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.PersonaMemoryAutolearn,
            Title: "Авто-память персон",
            Description: "После разговора персона сама вычленяет из диалога факты и выводы и сохраняет их в свою долгую память — без явной команды «запомни».",
            Default: false,
            Stage: "dev"),

        // @упоминания персон: в любом чате можно позвать другую персону (@handle) —
        // она ответит от своего лица через persona_ask, со своим характером и памятью.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.PersonaMentions,
            Title: "@упоминания персон",
            Description: "Упомяни персону через @ в любом чате — ассистент спросит её, и она ответит в своём характере, со своей моделью и долгой памятью. Плюс кнопка «Обсудить с командой» в чате персоны.",
            Default: false,
            Stage: "dev"),

        // Привязки персон к источникам знаний и правилам: индекс «когда → откуда» в
        // системном промпте, выжимки режима «всегда», сужение workspace до привязанных проектов.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.PersonaBindings,
            Title: "Знания и правила персон",
            Description: "Персоне можно привязать источники знаний (проекты, папки, базы знаний, заметки, скиллы) и правила инструментов с условиями «когда применять». Персона видит индекс привязок и сама подгружает нужный источник; режим «всегда» подмешивает выжимку в каждый ход.",
            Default: false,
            Stage: "dev"),

        // MCP workspace-server: Claude в любом чате видит все проекты владельца —
        // список, файлы, базы знаний и единый поиск по рабочему пространству.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.WorkspaceTools,
            Title: "Инструменты рабочего пространства",
            Description: "Claude в любом чате получает доступ ко всем твоим проектам: список и карточки, чтение и правка файлов других проектов, поиск по базам знаний и единый поиск по заметкам и задачам.",
            Default: false,
            Stage: "dev"),

        // Секция chats workspace-server: список/история/создание чужих чатов и chats_send —
        // полноценный ход в другом чате от имени пользователя (результат виден в его ленте).
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.WorkspaceChatSend,
            Title: "Отправка сообщений в чаты",
            Description: "Claude и персоны могут писать в другие твои чаты: просматривать список и историю, создавать чаты и отправлять сообщения (полноценный ход, ответ появляется в ленте чата). Требует включённых «Инструментов рабочего пространства».",
            Default: false,
            Stage: "dev"),

        // Секция destructive workspace-server: files_delete/chats_delete. Без флага секция
        // не выдаётся никому; персоне дополнительно нужен tool-ключ destructive (Tools/привязка).
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.WorkspaceDestructive,
            Title: "Разрушающие операции агента",
            Description: "Claude может БЕЗВОЗВРАТНО удалять файлы проектов и чаты через инструменты рабочего пространства (files_delete, chats_delete) — только по явной просьбе. Требует включённых «Инструментов рабочего пространства»; персоне дополнительно нужна возможность «Удаление (опасно)».",
            Default: false,
            Stage: "dev"),
    ];

    private static readonly HashSet<string> Keys = All.Select(f => f.Key).ToHashSet();

    /// <summary>Существует ли флаг с таким ключом в реестре.</summary>
    public static bool Exists(string key) => Keys.Contains(key);
}
