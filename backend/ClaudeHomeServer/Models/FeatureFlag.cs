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
        // [[wikilinks]], backlinks и графом; Claude ведёт их через MCP notes-server.
        new FeatureFlagDefinition(
            Key: FeatureFlagKeys.Notes,
            Title: "Заметки",
            Description: "База знаний: markdown-заметки со связями [[…]], обратными ссылками и графом. Claude ведёт заметки, единый граф поверх личного vault и проектов.",
            Default: false,
            Stage: "beta"),
    ];

    private static readonly HashSet<string> Keys = All.Select(f => f.Key).ToHashSet();

    /// <summary>Существует ли флаг с таким ключом в реестре.</summary>
    public static bool Exists(string key) => Keys.Contains(key);
}
