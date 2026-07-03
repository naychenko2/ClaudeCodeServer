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
            Key: "session-artifacts",
            Title: "Артефакты сессии",
            Description: "Панель справа от чата с измененными файлами, планом, задачами, агентами и ссылками за текущую сессию.",
            Default: false,
            Stage: "beta"),

        // Задачи: вкладка «Календарь» в хабе и вкладка «Задачи» внутри проекта.
        new FeatureFlagDefinition(
            Key: "tasks",
            Title: "Задачи и календарь",
            Description: "Раздел «Календарь» со всеми задачами и вкладка «Задачи» внутри проекта.",
            Default: false,
            Stage: "beta"),

        // Напоминания о задачах: офсет от срока + доставка тостом и web push.
        new FeatureFlagDefinition(
            Key: "task-reminders",
            Title: "Напоминания о задачах",
            Description: "Напоминание к сроку задачи (за N минут) с уведомлением в приложении и push на устройства.",
            Default: false,
            Stage: "beta"),

        // Регулярные задачи: правило повторения, новый экземпляр при завершении.
        new FeatureFlagDefinition(
            Key: "task-recurrence",
            Title: "Регулярные задачи",
            Description: "Повторяющиеся задачи: ежедневно/еженедельно/ежемесячно/ежегодно, следующий экземпляр создаётся при завершении.",
            Default: false,
            Stage: "beta"),

        // Claude-исполнитель: запуск сессии по задаче кнопкой и автоматически по сроку.
        new FeatureFlagDefinition(
            Key: "task-claude-exec",
            Title: "Claude-исполнитель задач",
            Description: "Задачи с исполнителем Claude выполняются в отдельном чате: вручную кнопкой или автоматически при наступлении срока.",
            Default: false,
            Stage: "beta"),
    ];

    private static readonly HashSet<string> Keys = All.Select(f => f.Key).ToHashSet();

    /// <summary>Существует ли флаг с таким ключом в реестре.</summary>
    public static bool Exists(string key) => Keys.Contains(key);
}
