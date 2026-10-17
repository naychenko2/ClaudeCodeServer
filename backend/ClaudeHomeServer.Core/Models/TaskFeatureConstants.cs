namespace ClaudeHomeServer.Models;

// Константы разметки фичи (режим «Исполнение фичи»,
// docs/research/turn-plan-feature-mode-cut-2026-10.md).
// Строки, а не enum: откат кода ломает чтение enum'а (урок mcp-servers.json, ушедшего в
// .corrupt), а незнакомые строки старый код при чтении просто игнорирует.

// Роль задачи в фиче.
public static class TaskRoles
{
    public const string Feature = "feature";
    public const string Cut = "cut";
    public const string Plan = "plan";
    public const string Step = "step";
    public const string Rework = "rework";
    public const string Review = "review";
    public const string Qa = "qa";
    public const string Final = "final";
    public const string Merge = "merge";
    public const string Consult = "consult";

    public static readonly IReadOnlyList<string> All =
        [Feature, Cut, Plan, Step, Rework, Review, Qa, Final, Merge, Consult];

    // Роли, которым featureId НЕ обязателен: эпик фичи (сам эпик — FeatureId=null, он и
    // есть фича) и cut/plan (семантика без привязки к эпика). Прочие роли (step/rework/
    // review/qa/final/merge/consult) — «висяк» без эпика, fail-closed требует featureId.
    public static readonly IReadOnlyList<string> FeatureIdOptional = [Feature, Cut, Plan];
}

// Исход витка (вердикт) ревью/проверки/финала.
public static class TaskVerdicts
{
    public const string Accepted = "accepted";
    public const string AcceptedWithRemarks = "accepted_with_remarks";
    public const string Returned = "returned";
    public const string Withdrawn = "withdrawn";

    public static readonly IReadOnlyList<string> All =
        [Accepted, AcceptedWithRemarks, Returned, Withdrawn];
}

// Мягкое предупреждение «ревью/проверку/финал фичи закрыли без вердикта» (разрез §5):
// карточка фичи вердикт не покажет, а блокировать закрытие ради UI нельзя. Единая строка
// и условие — для MCP (TasksToolset.CompleteResponse) и REST (TasksController.Update):
// мироринг поведения, одна точка правды. null — предупреждения нет.
public static class FeatureVerdict
{
    public const string WarningText =
        "У ревью/проверки/финала фичи нет вердикта — карточка его не покажет. "
        + "Задайте verification.verdict (accepted · accepted_with_remarks · returned · withdrawn).";

    // Условие: задача фичи (featureId != null) с ролью review/qa/final, закрытая без вердикта.
    public static string? Warning(TaskItem task) =>
        task.FeatureId is not null
            && task.Role is TaskRoles.Review or TaskRoles.Qa or TaskRoles.Final
            && string.IsNullOrEmpty(task.Verification?.Verdict)
            ? WarningText
            : null;
}
