namespace ClaudeHomeServer.Services.Tasks.Projector;

// Узкий снимок сессии владельца для индекса «чат → фичи» (контракт С0, шаг Б1).
// Вертикаль Tasks SessionManager не видит (шов решён в С0): контроллер (Main) сам берёт
// сессии владельца (SessionManager.GetAllOwnedBy) и маппит в эту запись. Id — id чата;
// TaskId — Session.TaskId (задача-владелец чата-исполнителя); ProjectId — проект чата
// (null — чат вне проектов); Archived — чат в архиве (Session.IsArchived).
public sealed record FeatureIndexSession(string Id, string? TaskId, string? ProjectId, bool Archived);

// Индекс «чат → фичи» (GET /api/tasks/features/index, контракт С0 §1).
// Б1 строит карту членства (Chats) и строки-заготовки фич (Features); Б2 дополнит строки
// статусами (bucket/rail/chips/attention/summary/ключевой чат/chatCount) и пересортирует
// Features по OrderRuns. Чистая функция: сессии приходят узким снимком, задачи — из TaskManager
// (per-owner, fail-closed: наружу уходит только id, который есть во входном снимке владельца).
public sealed class FeatureIndex
{
    // Чат → его фичи, упорядоченные по силе связи: key > curator > orchestrator > executor.
    // При равенстве силы — по порядку фичи в Features. Id чата — только из входного снимка.
    public Dictionary<string, List<FeatureIndexChatEntry>> Chats { get; init; } = new();
    // Строки фич (контракт С0 §1) — Б2.
    public List<FeatureIndexRow> Features { get; init; } = [];
}

// Одна пара «чат × фича» с сильнейшим видом связи: на пару одна запись, key перекрывает
// curator/orchestrator, curator перекрывает orchestrator/executor, orchestrator — executor.
public sealed record FeatureIndexChatEntry(
    string FeatureId,
    string Kind,          // key | curator | orchestrator | executor
    string? TaskRole,     // только у executor: Role задачи Session.TaskId
    string? StepRef,      // только у executor: StepRef задачи Session.TaskId
    string? Lane);        // только у executor: Lane задачи Session.TaskId

// Строка фичи в индексе (контракт С0 §1). Б1 создаёт заготовку (id/название/проект эпика);
// Б2 дополнит bucket/rail/chips/attention/summary/ключевой чат/chatCount.
public sealed record FeatureIndexRow(
    string FeatureId,
    string Title,
    string? ProjectId);
