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
