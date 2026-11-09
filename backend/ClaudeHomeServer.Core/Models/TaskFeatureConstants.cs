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
    // Дизайн (W4-DES-B): задача дизайнера в фиче. Стадия «Дизайн» необязательна: её нет —
    // этап skipped, на готовность фичи не влияет. featureId обязателен (привязанная роль).
    public const string Design = "design";
    public const string Plan = "plan";
    public const string Step = "step";
    public const string Rework = "rework";
    public const string Review = "review";
    public const string Qa = "qa";
    public const string Final = "final";
    public const string Merge = "merge";
    public const string Consult = "consult";

    public static readonly IReadOnlyList<string> All =
        [Feature, Cut, Design, Plan, Step, Rework, Review, Qa, Final, Merge, Consult];

    // Роли, которым featureId НЕ обязателен: эпик фичи (сам эпик — FeatureId=null, он и
    // есть фича) и cut/plan (семантика без привязки к эпика). Прочие роли (step/rework/
    // design/review/qa/final/merge/consult) — «висяк» без эпика, fail-closed требует featureId.
    public static readonly IReadOnlyList<string> FeatureIdOptional = [Feature, Cut, Plan];
}

// Причина удержания фичи (TaskItem.Hold.Reason, волна 4 §1): draft — начальная стадия (фичу готовят,
// шаги не запускать), manual — пауза человека, queue — задел под очередь (поведение паузы, сейчас
// не ставится). Строки, а не enum — тот же урок, что у TaskRoles.
public static class FeatureHoldReasons
{
    public const string Draft = "draft";
    public const string Manual = "manual";
    public const string Queue = "queue";
}

// Кто снял удержание фичи (поле by записи hold_cleared журнала): ручное снятие поля не пишет,
// автоснятие очереди фич — Queue.
public static class FeatureHoldBy
{
    public const string Queue = "queue";
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

// Закрытый список причин возврата (вердикт returned): по нему считается метрика «доля возвратов
// по причине», поэтому свободной строкой причина быть не может. Пусто = «причина не указана»
// (старые данные и возврат без причины). Единственная константа списка — для MCP-схемы и REST.
public static class TaskReturnReasons
{
    public const string Size = "size";               // размер тач-цели / переполнение
    public const string SideEffect = "side-effect";  // скрытый побочный эффект
    public const string Contract = "contract";       // невидимый контракт соседнего слоя
    public const string Robustness = "robustness";   // необработанный вход/ошибка
    public const string Quality = "quality";         // качество кода, стиль, отсутствие тестов
    public const string Other = "other";             // только с текстом в Notes

    public static readonly IReadOnlyList<string> All =
        [Size, SideEffect, Contract, Robustness, Quality, Other];

    // Правило вердикта: причина допустима только при returned, из списка; other требует Notes.
    // Бросает InvalidOperationException (400 на REST, отказ на MCP). Пустая причина — норма.
    public static void Validate(string? verdict, string? reason, string? notes)
    {
        if (string.IsNullOrWhiteSpace(reason)) return;
        if (verdict != TaskVerdicts.Returned)
            throw new InvalidOperationException(
                $"Причина возврата (returnReason) задаётся только при вердикте «{TaskVerdicts.Returned}».");
        if (!All.Contains(reason))
            throw new InvalidOperationException(
                $"Недопустимая причина возврата «{reason}». Допустимые: {string.Join(", ", All)}.");
        if (reason == Other && string.IsNullOrWhiteSpace(notes))
            throw new InvalidOperationException(
                "Причина возврата «other» требует пояснения в verification.notes.");
    }
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
