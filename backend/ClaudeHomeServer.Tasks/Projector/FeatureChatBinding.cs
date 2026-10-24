using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Tasks.Projector;

// ─── S1 (фича chat-feature-focus): правило «чат привязан к фиче» для дверей ─────────────────
//
// Двери (TasksController, TasksToolset, шаг S2) отказывают заводить задачу в чужую фичу из
// чата, который уже ведёт фичу. Своей логики привязки у дверей нет — они зовут только
// DenyReason. FeatureChatMembership здесь НЕ меняется: на неё опираются «общий с…» и
// панель фич.
//
// Привязка чата = FeatureChatMembership (executor ∪ curator ∪ orchestrator)
// ∪ фичи задач, созданных из этого чата (SourceSessionId == chatId → их FeatureId;
// сама задача-эпик — фича). Вторая ось — легаси: чат, из которого заводили задачи
// нескольких фич, «ведёт» их все; без неё старый легаси-чат отказывал бы всё подряд.
//
// DenyReason: null — привязки нет (свободный чат) или целевая фича среди привязанных;
// иначе текст одной константы с названием первой привязанной фичы. Название — Title
// эпика из owner-среза; висячий featureId (эпик удалён) — сам id.
public static class FeatureChatBinding
{
    // Единственный текст отказа (константа; {name} — название привязанной фичы).
    private const string DenyTemplate =
        "Этот чат уже ведёт фичу «{name}». Одна фича — один чат: задачи другой фичи " +
        "заводите из отдельного чата или из чата её эпика.";

    // Фичи, к которым привязан чат: FeatureChatMembership ∪ фичи задач с SourceSessionId == чат.
    public static HashSet<string> BoundFeatures(
        IReadOnlyList<TaskItem> owner,
        IReadOnlyCollection<FeatureIndexSession> snapshot,
        string chatId)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(snapshot);

        var result = new HashSet<string>(StringComparer.Ordinal);

        var membership = FeatureChatMembership.Build(owner, snapshot);
        if (membership.TryGetValue(chatId, out var features))
            foreach (var featureId in features)
                result.Add(featureId);

        // Легаси-ось: задачи, заведённые из этого чата, тянут за собой свои фичи.
        foreach (var t in owner)
        {
            if (t.SourceSessionId != chatId)
                continue;
            var featureId = t.FeatureId ?? (t.Role == TaskRoles.Feature ? t.Id : null);
            if (featureId is not null)
                result.Add(featureId);
        }

        return result;
    }

    // Причина отказа заводить задачу в targetFeatureId из чата chatId; null — отказа нет.
    public static string? DenyReason(
        IReadOnlyList<TaskItem> owner,
        IReadOnlyCollection<FeatureIndexSession> snapshot,
        string chatId,
        string targetFeatureId)
    {
        var bound = BoundFeatures(owner, snapshot, chatId);
        if (bound.Count == 0 || bound.Contains(targetFeatureId))
            return null;

        // Название первой привязанной фичы — Title её эпика (порядок — owner-срез,
        // детерминирован); висячий id (эпика нет в owner-срезе) — сам id.
        string? name = null;
        foreach (var t in owner)
        {
            if (t.Role == TaskRoles.Feature && bound.Contains(t.Id))
            {
                name = t.Title;
                break;
            }
        }
        name ??= bound.First();

        return DenyTemplate.Replace("{name}", name);
    }
}
