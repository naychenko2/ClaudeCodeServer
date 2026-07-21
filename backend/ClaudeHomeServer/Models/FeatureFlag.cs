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
    // Секция destructive workspace-server: безвозвратное удаление файлов и чатов (files_delete/chats_delete).
    // Предохранитель от необратимого удаления агентом.
    public const string WorkspaceDestructive = "workspace-destructive";

    // Авто-накопление общей памяти команды проекта из ходов/групповых чатов/совещаний (Волна 1).
    public const string TeamMemoryAutolearn = "team-memory-autolearn";

    // Подсказка следующего сообщения в композере (prompt_suggestion от claude CLI).
    public const string PromptSuggestions = "prompt-suggestions";

    // Комментарии к MD-документам: выделение → заметка-комментарий с привязкой к блоку.
    public const string DocAnnotations = "doc-annotations";

    // AI-хаб: локальное (Ollama) ранжирование действий по контексту + градация FAB.
    // Выключен или Ollama не сконфигурирован → rule-based механизм подсказок.
    public const string AiLocalSuggest = "ai-local-suggest";
}

/// <summary>
/// Единственное место, где объявляются фич-флаги. Чтобы добавить новый флаг —
/// допиши строку в <see cref="All"/> (и продублируй ключ в lib/featureFlags.ts на фронте).
/// </summary>
public static class FeatureFlagCatalog
{
    public static readonly IReadOnlyList<FeatureFlagDefinition> All =
    [
        // Секция destructive workspace-server: files_delete/chats_delete. Без флага секция
        // не выдаётся никому; персоне дополнительно нужен tool-ключ destructive (Tools/привязка).
        // Единственный оставшийся флаг: все прочие фичи включены безусловно, а этот —
        // предохранитель от необратимого удаления (по умолчанию выключен). Механика флагов
        // (сервис, каталог, модалка, /api/feature-flags) оставлена рабочей для будущих флагов.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.WorkspaceDestructive,
            Title: "Разрушающие операции агента",
            Description: "Claude может БЕЗВОЗВРАТНО удалять файлы проектов и чаты через инструменты рабочего пространства (files_delete, chats_delete) — только по явной просьбе. Персоне дополнительно нужна возможность «Удаление (опасно)».",
            Default: false,
            Stage: "dev"),

        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.TeamMemoryAutolearn,
            Title: "Авто-память команды",
            Description: "Claude сам вычленяет решения, договорённости и факты о проекте из рабочих ходов, групповых чатов и совещаний и складывает их в общую память команды (её recall'ят все персоны проекта). Записи помечены источником, ручные не затрагиваются.",
            Default: false,
            Stage: "beta"),

        // Подсказки следующего сообщения: генерирует сам claude CLI (--prompt-suggestions)
        // с переиспользованием prompt cache хода. Только для родного Claude-провайдера.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.PromptSuggestions,
            Title: "Подсказки следующего сообщения",
            Description: "После ответа Claude над полем ввода появляется чип с вероятным следующим сообщением — тап, → или Tab вставляют его в композер. Работает в чатах на моделях Claude.",
            Default: false,
            Stage: "dev"),

        // Комментарии к документам: выделил текст в .md → попап → заметка-комментарий
        // с привязкой к блоку (annotates/anchor_*/status), панель при чтении, фильтры
        // status: в «Заметках». Снять флаг после недели реального использования.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.DocAnnotations,
            Title: "Комментарии к документам",
            Description: "Выделите текст в markdown-документе — и создайте комментарий, привязанный к этому месту. Пометки видны при чтении в панели справа, необработанные ищутся фильтром, комментарии сгруппированы под документами в «Заметках».",
            Default: false,
            Stage: "beta"),

        // Локальные рекомендации AI-хаба: маленькая модель Ollama (бесплатно) читает
        // содержание открытой сущности и советует уместные действия с уровнем важности,
        // кнопка AI меняет вид от бледной до анимированной. Без Ollama — работает на правилах.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.AiLocalSuggest,
            Title: "Умные подсказки действий (локально)",
            Description: "Кнопка AI подсказывает, что уместно сделать с открытой заметкой, задачей или файлом — их выбирает бесплатная локальная модель по содержанию, а не по фиксированному списку. Чем полезнее рекомендация, тем ярче кнопка (у самых важных — анимация). Без настроенной локальной модели подсказки работают по простым правилам.",
            Default: false,
            Stage: "dev"),
    ];

    private static readonly HashSet<string> Keys = All.Select(f => f.Key).ToHashSet();

    /// <summary>Существует ли флаг с таким ключом в реестре.</summary>
    public static bool Exists(string key) => Keys.Contains(key);
}
