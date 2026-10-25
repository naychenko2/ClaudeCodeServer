namespace ClaudeHomeServer.Services.Tasks.Projector;

// Событие ленты фичи (волна 2 «Обзор фич», разрез §2): один факт из жизни фичи,
// выведенный из полей TaskItem/витков БЕЗ хранилища. Чистая проекция FeatureProjector.BuildEvents
// собирает список, сортирует по `At desc` (при равенстве — по TaskId) и отдаёт; фронт рисует
// ленту. record — как остальные DTO индекса (FeatureIndexSession/FeatureIndexRow): value-семантика
// для сравнения в золотых тестах.
//
// Контракт типа (разрез §2): { at, kind, taskId, role, stepRef?, personaId?, sessionId?, round?,
// reason? }. role/stepRef/personaId — атрибуты задачи (у всех видов); round — номер витка, только
// у вердиктов (из FeatureRound); sessionId — чат, наружу только если в снимке владельца.
public sealed record FeatureEvent(
    // Момент события (UTC): CreatedAt/ClaudeStartedAt/ExecutorStoppedAt/AwaitingHumanSince/
    // DroppedByHumanAt/CompletedAt — по смыслу вида.
    DateTime At,
    // Вид события: created | started | stopped | waiting | accepted | accepted_with_remarks |
    // returned | withdrawn | completed | dropped | merged | closed | started_from_queue.
    string Kind,
    // Id задачи, к которой относится событие.
    string TaskId,
    // Роль задачи (TaskItem.Role): feature · cut · plan · step · rework · review · qa · final ·
    // merge · consult. У всех видов события.
    string? Role,
    // Код шага (TaskItem.StepRef, «B1»/«F2»); у step/rework, иначе null.
    string? StepRef,
    // Исполнитель (TaskItem.PersonaId); null — без персоны.
    string? PersonaId,
    // Чат исполнителя (task.LinkedSessionId); наружу только если есть в снимке сессий владельца,
    // иначе null (fail-closed, как KeySession/AttentionOf).
    string? SessionId,
    // Номер витка (1-based) из FeatureRound; только у вердиктов (accepted/awr/returned/withdrawn),
    // у остальных видов null.
    int? Round,
    // Причина (≤200 символов): ExecutorStopReason (stopped) / AwaitingHumanReason (waiting) /
    // Verification.Notes (вердикты); у остальных видов null.
    string? Reason);
