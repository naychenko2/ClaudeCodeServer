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
    // Консолидация памяти персон: периодический LLM-merge дублей + вытеснение при переполнении
    public const string PersonaMemoryConsolidation = "persona-memory-consolidation";
    // @упоминания персон в чатах: MCP persona_ask, автокомплит @ в композере
    public const string PersonaMentions = "persona-mentions";
    // Проактивность персон: «пишет первой» по расписанию (утренний бриф и т.п.)
    public const string PersonaProactive = "persona-proactive";
    // Групповые чаты персон (2-4 участника, роутинг спикера по @) + совещания cross-attack
    public const string PersonaGroupChats = "persona-group-chats";
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

        // Консолидация памяти персон: фоновый сервис периодически схлопывает дубли
        // (LLM-merge с детерминированными гейтами) и вытесняет наименее ценные записи
        // при переполнении памяти.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.PersonaMemoryConsolidation,
            Title: "Консолидация памяти персон",
            Description: "Фоновая уборка долгой памяти: похожие записи схлопываются в одну, а при переполнении наименее ценные забываются — память не разрастается бесконечно.",
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

        // Проактивность персон: персона сама пишет первой по расписанию —
        // выполняет свою инструкцию (напр. утренний бриф) и присылает уведомление.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.PersonaProactive,
            Title: "Проактивные персоны",
            Description: "Персона может писать первой по расписанию: в заданное время выполняет свою инструкцию (например, собирает утренний бриф) и присылает уведомление со ссылкой на чат.",
            Default: false,
            Stage: "dev"),

        // Групповые чаты персон: 2-4 участника в одном чате, отвечает тот, к кому
        // обращаются через @handle. Плюс режим «Совещание»: независимые позиции,
        // перекрёстная критика и синтез итога от ведущей.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.PersonaGroupChats,
            Title: "Групповые чаты персон",
            Description: "Чат сразу с несколькими персонами: отвечает та, к кому обращаешься через @, остальных она может спросить сама. Плюс «Совещание»: участники независимо высказываются, критикуют позиции друг друга, ведущая сводит итог.",
            Default: false,
            Stage: "dev"),
    ];

    private static readonly HashSet<string> Keys = All.Select(f => f.Key).ToHashSet();

    /// <summary>Существует ли флаг с таким ключом в реестре.</summary>
    public static bool Exists(string key) => Keys.Contains(key);
}
