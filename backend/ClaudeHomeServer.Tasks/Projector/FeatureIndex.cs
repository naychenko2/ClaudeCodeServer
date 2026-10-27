namespace ClaudeHomeServer.Services.Tasks.Projector;

// Узкий снимок сессии владельца для индекса «чат → фичи» (контракт С0, шаг Б1).
// Вертикаль Tasks SessionManager не видит (шов решён в С0): контроллер (Main) сам берёт
// сессии владельца (SessionManager.GetAllOwnedBy) и маппит в эту запись. Id — id чата;
// TaskId — Session.TaskId (задача-владелец чата-исполнителя); ProjectId — проект чата
// (null — чат вне проектов); Archived — чат в архиве (Session.IsArchived); Model — модель
// чата как сохранена в Session.Model (null/пусто — «Авто», слот; Б4). PersonaId — персона
// чата (Session.PersonaId, W4-1B): оркестратор фичи берётся только от сессий этого снимка
// (чужой/удалённый ключевой чат в него не попадает → запасная цепочка по полям эпика).
// Busy — чат занят: ход исполнителя идёт (Starting/Working) или ждёт ответа человека (Waiting) —
// то же условие, что у сторожа зависания ClassifyStall (TR-B1b). null — у составившего снимок
// признака нет: reported_open при отсутствии признака не выдаётся (fail-closed).
public sealed record FeatureIndexSession(
    string Id, string? TaskId, string? ProjectId, bool Archived, string? Model = null, string? PersonaId = null,
    bool? Busy = null);

// Чат фичи (GET /api/tasks/features/{id}, chats[]; Б4 + W4-CHT-B): один чат из снимка
// владельца, состоящий в фиче по ЕДИНОМУ правилу FeatureChatMembership. kind: curator |
// orchestrator | executor — тот же вид, что в FeatureIndexChatEntry; «key» бэк не emits:
// ключевой чат помечает фронт по keySessionId (FeatureChatsTab).
// modelLabel/modelFull — как у исполнителя (FeatureStageExecutor): «Авто»/короткая метка и
// полное имя (для «Авто» — «Авто → {модель} (слот «{тир}»)»).
// projectId — проект задачи этого чата (хвосты 5634609f п.26: переход «в задачу» берёт проект
// эпика как приближение); null — у чата нет задачи фичи (curator/orchestrator) или задача личная.
public sealed record FeatureChat(
    string Id,
    string Kind,
    string? TaskId,
    bool Archived,
    string? PersonaId,
    string? TaskRole,
    string? StepRef,
    string? Lane,
    string? ModelLabel,
    string? ModelFull,
    string? ProjectId);

// Индекс «чат → фичи» (GET /api/tasks/features/index, контракт С0 §1).
// Б1 строит карту членства (Chats) и строки-заготовки фич (Features); Б2 дополнит строки
// статусами (bucket/rail/chips/attention/summary/ключевой чат/chatCount) и пересортирует
// Features по OrderRuns. Чистая функция: сессии приходят узким снимком, задачи — из TaskManager
// (per-owner, fail-closed: наружу уходит только id, который есть во входном снимке владельца).
public sealed class FeatureIndex
{
    // Чат → его фичи. Порядок (W4-CHT-B, контракт §2): сначала открытые фичи, потом закрытые;
    // внутри класса — по силе связи (curator > orchestrator > executor), при равенстве — по
    // порядку фичи в Features. «Своя» фича чата (по TaskId) — первая внутри своего класса.
    // Id чата — только из входного снимка.
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
    // Группа фичи: attention — требует внимания; running — идёт; merged — смержена, эпик открыт
    // (ждёт закрытия); idle — стоит; closed — закрыта (эпик Done).
    // bucket = f(state, эпик Done) (единая точка правды, FeatureProjector.Bucket): merged при эпике Done → closed.
    string Bucket,
    // Состояние фичи (FeatureRun.State; 11 значений, см. FeatureStates): merged · closed · paused ·
    // stopped · waiting · draft · merging · merge_pending · running · not_started · idle.
    string State,
    // Тревога; null — тревоги нет.
    FeatureIndexAttention? Attention,
    // Рельса: 8 стадий в явном порядке жизненного цикла (cut → design → plan → …; «design»
    // необязательна: без задач — skipped, W4-DES-B).
    List<FeatureIndexRailSegment> Rail,
    // Чипы-фильтры: open | waiting | stopped | merge | merged | closed (у открытой смерженной — open + merged).
    // Фронт фильтром по состоянию их не читает: отбор идёт по Kind (Б-1.2); уборка чипов — отдельная (разрез п.2).
    List<string> Chips,
    // Счётчики шагов: принятые ревью / знаменатель (честный прогресс, см. FeatureStepProgress).
    int StepsAccepted,
    int StepsTotal,
    // Число возвратов (Loops).
    int Loops,
    // «Сейчас» (now0): одна InProgress-задача; null — фича не идёт.
    FeatureIndexNow? Now0,
    // Последняя активность (максимум по CompletedAt/ClaudeStartedAt); null — активности нет.
    DateTime? LastActivityAt,
    // Итог смерженной фичи (у закрытой и у смерженной с открытым эпиком); иначе null.
    FeatureIndexSummary? Summary,
    // Ключевой чат фичи (цепочка контракта §1); null — не найден.
    string? KeySessionId,
    // Ключевой чат в архиве (остаётся ключевым, помечается).
    bool KeyArchived,
    // Проект ключевого чата; null — чат вне проектов.
    string? KeyProjectId,
    // Оркестратор фичи (W4-1B): персона ключевого чата из снимка владельца; иначе —
    // персона, запустившая исполнителя эпика; иначе — создавшая эпик; null — никого нет.
    // Только в строке индекса: в features/{id} (FeatureRun) не дублируется.
    string? OrchestratorPersonaId,
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
    List<string> NowPersonaIds,
    // Честный прогресс (Б-pr2): шаги на ревью / в работе / не заведены (план есть, задачи нет)
    // и состояние плана: pending | partial | cut | done. StepsAccepted/StepsTotal — тоже оттуда.
    int StepsReview,
    int StepsWork,
    int StepsNotCreated,
    string PlanState,
    // Вне знаменателя (как в FeatureStepProgress): шаги, снятые ревью с выкладки, и доработки без
    // привязки к шагу — индекс отдаёт те же числа, что страница фичи.
    int StepsWithdrawn,
    int UnlinkedReworks,
    // Причина удержания эпика (W4-HOLD-B1): draft | manual | queue; null — удержания нет.
    // Только сигнал для фронта (тон «на паузе»/«черновик»); сам факт состояния — в State.
    string? HoldReason,
    // Момент постановки удержания (FeatureHold.Since, R-HOLD-B1): длительность «на паузе · {dur}»
    // в макете (блокер фронта HOLE-F2). Аддитивное поле: null — удержания нет.
    DateTime? HoldSince,
    // W4-ANB (разрез §4): «тихо» — фича стоит без движения ≥ 24 часов (решение Андрея, 24.10).
    // Берётся из FeatureRun.Quiet (единая точка правды), не пересчитывается здесь.
    bool Quiet,
    // Очередь фич (волна 6): место в очереди проекта; null — фича не в очереди. Из FeatureRun.Queue.
    FeatureQueueInfo? Queue = null,
    // Категория состояния фичи (Б-1.2, «Фильтр фич по состоянию»): та же величина, что brief.kind, —
    // FeatureBrief.KindOf (та же таблица KindByState и те же расщепления: paused→queued по причине
    // удержания, merged→closed по закрытости эпика). Null — состояние вне таблицы KindByState:
    // BuildRow guard'ит по ContainsKey — индекс не падает (в отличие от brief, который бросает).
    string? Kind = null,
    // TR-B1c: число расхождений трекера (FeatureRun.Tracker.Count), проектор считает, индекс только несёт.
    int TrackerIssues = 0);

// Тревога фичи (контракт §1): kind: stopped | wait | merge; merge — только когда merge-задачи
// нет (финал принят), reason у merge — null (на бэке, AttentionOf); sessionId — чат исполнителя
// (без taskId, в отличие от feature-mode FeatureAttention).
public sealed record FeatureIndexAttention(
    string Kind,        // stopped | wait | merge
    DateTime? Since,
    string? Reason,
    string? PersonaId,
    string? SessionId,
    string? TaskId);    // id задачи тревоги (Б7: FeatureAttention.TaskId)

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

// Итог смерженной фичи (у закрытой и у смерженной с открытым эпиком): иначе null.
public sealed record FeatureIndexSummary(
    DateTime? MergedAt,
    double DurationMinutes,
    // Шагов не принято (Total − Accepted); у merged и closed — «закрыта вручную с непринятыми шагами».
    int StepsOpen);
