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

// Строка фичи в индексе (контракт «Контракт индекса фич — утверждён»). Б1 создаёт
// заготовку (id/название/проект эпика); Б2 дополняет bucket/rail/chips/attention/summary/
// ключевой чат/chatCount и пересортировывает Features по OrderRuns. Значения bucket/rail/chips/
// attention/now0/summary берутся из FeatureRun (тон выводится из Attention, MAJOR-1) —
// проектор не пересчитывает. Даты — DateTime?: сериализуются в ISO-строку (camelCase),
// как и в FeatureRun; фронт читает их как string | null.
public sealed record FeatureIndexRow(
    string FeatureId,
    string Title,
    string? ProjectId,
    // Группа фичи: attention — требует внимания; running — идёт; idle — стоит; closed — закрыта.
    // Выводится из State (единая точка правда, FeatureProjector.Bucket) — bucket и state не расходятся.
    string Bucket,
    // Состояние фичи (FeatureRun.State; 9 значений, см. FeatureStates): merged · closed · stopped ·
    // waiting · merging · merge_pending · running · not_started · idle.
    string State,
    // Тревога; null — тревоги нет.
    FeatureIndexAttention? Attention,
    // Рельса: ровно 7 стадий в порядке жизненного цикла.
    List<FeatureIndexRailSegment> Rail,
    // Чипы-фильтры: open | waiting | stopped | merge | closed.
    List<string> Chips,
    // Счётчики шагов: принятые (Done) / все.
    int StepsAccepted,
    int StepsTotal,
    // Число возвратов (Loops).
    int Loops,
    // «Сейчас» (now0): одна InProgress-задача; null — фича не идёт.
    FeatureIndexNow? Now0,
    // Последняя активность (максимум по CompletedAt/ClaudeStartedAt); null — активности нет.
    DateTime? LastActivityAt,
    // Итог смерженной фичи; только у закрытой, иначе null.
    FeatureIndexSummary? Summary,
    // Ключевой чат фичи (цепочка контракта §1); null — не найден.
    string? KeySessionId,
    // Ключевой чат в архиве (остаётся ключевым, помечается).
    bool KeyArchived,
    // Проект ключевого чата; null — чат вне проектов.
    string? KeyProjectId,
    // Чатов фичи в этом ответе (архивные включены).
    int ChatCount,
    // Аддитивные (волна 2, без хранения — вычисляются из эпика/задач/«сейчас»):
    // ветка worktree эпика; null — эпик без worktree.
    string? Branch,
    // Момент создания эпика (DateTime? по конвенции дат индекса; в практике всегда есть).
    DateTime? CreatedAt,
    // Число задач фичи: эпик + дочерние.
    int TaskCount,
    // «Сейчас»: уникальные PersonaId идущих задач (Now, по убыванию старта) в порядке старта;
    // пустой список — фича не идёт.
    List<string> NowPersonaIds);

// Тревога фичи (контракт §1): kind расширен значением merge (маппинг сырой строки «мерж»
// — на бэке, на фронт уходит только kind); sessionId — чат исполнителя (без taskId, в отличие
// от feature-mode FeatureAttention).
public sealed record FeatureIndexAttention(
    string Kind,        // stopped | wait | merge
    DateTime? Since,
    string? Reason,
    string? PersonaId,
    string? SessionId);

// Сегмент рельсы: одна стадия конвейера. state — словарь FeatureStage.State один в один;
// tone — только у active, иначе null (как FeatureStage.Tone).
public sealed record FeatureIndexRailSegment(
    string Key,        // cut | plan | prod | review | qa | final | merge
    string State,      // done | active | pending | absent
    string? Tone);     // run | loop | wait | stopped | null

// «Сейчас» фичи (now0): одна InProgress-задача — кто, стадия, шаг.
public sealed record FeatureIndexNow(
    string? PersonaId,
    string Stage,
    string? StepRef);

// Итог смерженной фичи: только у закрытой, иначе null.
public sealed record FeatureIndexSummary(
    DateTime? MergedAt,
    double DurationMinutes);
