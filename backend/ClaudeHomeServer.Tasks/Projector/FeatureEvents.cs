namespace ClaudeHomeServer.Services.Tasks.Projector;

// Событие ленты фичи (волна 2 «Обзор фич», разрез §2): один факт из жизни фичи,
// выведенный из полей TaskItem/витков БЕЗ хранилища. Чистая проекция FeatureProjector.BuildEvents
// собирает список, сортирует по `At desc` (при равенстве — по TaskId) и отдаёт; фронт рисует
// ленту. record — как остальные DTO индекса (FeatureIndexSession/FeatureIndexRow): value-семантика
// для сравнения в золотых тестах.
public sealed record FeatureEvent(
    // Вид события: created | started | stopped | waiting | accepted | accepted_with_remarks |
    // returned | withdrawn | completed | dropped | merged | closed.
    string Kind,
    // Момент события (UTC): CreatedAt/ClaudeStartedAt/ExecutorStoppedAt/AwaitingHumanSince/
    // DroppedByHumanAt/CompletedAt — по смыслу вида.
    DateTime At,
    // Id задачи, к которой относится событие.
    string TaskId,
    // Чат исполнителя (task.LinkedSessionId); наружу только если есть в снимке сессий владельца,
    // иначе null (fail-closed, как KeySession/AttentionOf).
    string? SessionId,
    // Причина (≤200 символов): ExecutorStopReason (stopped) / AwaitingHumanReason (waiting) /
    // Verification.Notes (вердикты); у остальных видов null.
    string? Reason);
