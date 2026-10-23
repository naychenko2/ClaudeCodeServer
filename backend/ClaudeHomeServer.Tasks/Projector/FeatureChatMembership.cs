using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Tasks.Projector;

// ─── Б9c, шаг 2 + W4-CHT-B: единое правило «чат → фичи» (хозяин — проектор) ───────────────────
//
// Членство чата в фичах — ЕДИНОЕ правило (решение Александра, доработка Б9c; W4-CHT-B —
// контракт §2): раньше оно жило в четырёх копиях (BuildIndexCore, ChatFeatureIds, фильтр
// ProjectForSession по SourceSessionId/ExecutorLaunchedBySessionId эпика и TaskId-правило
// BuildFeatureChats). Копии расходились: «общий чат» с точки зрения одной точки и другой —
// разные наборы, и расход общего чата оседал в одной фиче (проба F Глеба). Теперь индекс
// (BuildIndexCore), шторка (ProjectForSession) и вкладка «Чаты» (BuildFeatureChats) вызывают
// Build/BuildDetailed — вида связи (curator/orchestrator/executor) нигде больше не определяют.
//
// Правило (executor ∪ curator ∪ orchestrator):
//   executor     — Session.TaskId → задача с FeatureId == F (или сама эпик F: Role=feature);
//   curator      — epic.SourceSessionId == чат;
//   orchestrator — epic.ExecutorLaunchedBySessionId == чат ИЛИ ExecutorLaunchedBySessionId
//                  дочерней задачи фичи == чат.
// SourceSessionId дочерних задач — НЕ членство (слабая связь: показывает только плашку).
//
// Общий чат — тот, что состоит в ДВУХ И БОЛЕЕ фичах (|M[c]| ≥ 2). Удалённый/чужой чат (нет в
// снимке) не общий: его записи идут в прямой срез по taskIds, в долю ни одной фичи не попадают.
//
// Для расхода строится БЕЗ фильтра по проекту (projectId = null) — снимок сессий владельца
// целиком; индекс (BuildIndexCore) фильтрует по projectId сам, но членство в фичах от проекта
// не зависит.
//
// Изоляция per-owner (решение Александра, вопрос 3): карта строится по задачам и сессиям
// ВЛАДЕЛЬЦА — `owner` это GetByOwner(UserId), `snapshot` — снимок сессий того же владельца
// (SessionSnapshot fail-closed: чужая/удалённая сессия не в снимке). Чат ЧУЖОГО владельца
// в карту не попадёт ни как executor (сессия не в снимке), ни как curator/orchestrator
// (эпики/задачи — только владельца).
public static class FeatureChatMembership
{
    // Запись пары «чат × фича»: сильнейший вид связи и его сила. role/stepRef/lane — только
    // у executor (данные задачи по Session.TaskId); у curator/orchestrator — null.
    public record Link(string Kind, int Strength, string? Role, string? StepRef, string? Lane);

    // Сила связи вида: key(0) > curator(1) > orchestrator(2) > executor(3).
    // «key» в членстве не участвует (добавит Б2, цепочка ключевого чата): ключевой чат
    // помечает фронт по keySessionId (FeatureChatsTab), второй копии «key» на бэке нет.
    public static int KindStrength(string kind) => kind switch
    {
        "key" => 0,
        "curator" => 1,
        "orchestrator" => 2,
        _ => 3, // executor
    };

    // chatId → featureId → сильнейший Link (executor ∪ curator ∪ orchestrator). Чат, не
    // входящий ни в одну фичу, в карте не живёт. Это ЕДИНАЯ реализация правила: все
    // потребители (индекс, ProjectForSession, BuildFeatureChats) идут через неё.
    public static IReadOnlyDictionary<string, Dictionary<string, Link>> BuildDetailed(
        IReadOnlyList<TaskItem> owner,
        IReadOnlyCollection<FeatureIndexSession> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Задача по id — для executor-членства (Session.TaskId → задача).
        var byId = owner.ToDictionary(t => t.Id);
        // Эпики (role=feature) — источники curator/orchestrator-членства.
        var epics = owner.Where(t => t.Role == TaskRoles.Feature).ToList();

        // curator: кто создал эпик (SourceSessionId) → фичи.
        var curatorBySession = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var epic in epics)
            if (epic.SourceSessionId is not null)
                AddToSet(curatorBySession, epic.SourceSessionId, epic.Id);

        // orchestrator: кто запускал исполнителя эпика (ExecutorLaunchedBySessionId эпика)
        // ИЛИ дочерней задачи фичи (ExecutorLaunchedBySessionId задачи) → фичи.
        var orchestratorBySession = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var epic in epics)
            if (epic.ExecutorLaunchedBySessionId is not null)
                AddToSet(orchestratorBySession, epic.ExecutorLaunchedBySessionId, epic.Id);
        foreach (var t in owner)
            if (t.FeatureId is not null && t.ExecutorLaunchedBySessionId is not null)
                AddToSet(orchestratorBySession, t.ExecutorLaunchedBySessionId, t.FeatureId);

        // chatId → featureId → сильнейший Link: на пару «чат × фича» держим минимальную
        // силу (curator > orchestrator > executor).
        var map = new Dictionary<string, Dictionary<string, Link>>(StringComparer.Ordinal);
        foreach (var s in snapshot)
        {
            var links = new Dictionary<string, Link>(StringComparer.Ordinal);
            void AddLink(string featureId, string kind, string? role, string? stepRef, string? lane)
            {
                var strength = KindStrength(kind);
                if (!links.TryGetValue(featureId, out var cur) || strength < cur.Strength)
                    links[featureId] = new Link(kind, strength, role, stepRef, lane);
            }
            // executor: Session.TaskId → задача → её фича (эпик — сама фича).
            if (s.TaskId is not null && byId.TryGetValue(s.TaskId, out var task))
            {
                var featureId = task.FeatureId ?? (task.Role == TaskRoles.Feature ? task.Id : null);
                if (featureId is not null)
                    AddLink(featureId, "executor", task.Role, task.StepRef, task.Lane);
            }
            // curator: epic.SourceSessionId == чат.
            if (curatorBySession.TryGetValue(s.Id, out var curators))
                foreach (var featureId in curators)
                    AddLink(featureId, "curator", null, null, null);
            // orchestrator: эпик/дочерняя задача запускала исполнителя из этого чата.
            if (orchestratorBySession.TryGetValue(s.Id, out var orchestrators))
                foreach (var featureId in orchestrators)
                    AddLink(featureId, "orchestrator", null, null, null);
            if (links.Count > 0) map[s.Id] = links;
        }
        return map;
    }

    // chatId → множество featureId — тонкая обёртка над BuildDetailed для потребителей,
    // которым нужно только множество (SharedSpendSplitter, ChatFeatureIds, контроллер).
    public static IReadOnlyDictionary<string, HashSet<string>> Build(
        IReadOnlyList<TaskItem> owner,
        IReadOnlyCollection<FeatureIndexSession> snapshot)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (chatId, links) in BuildDetailed(owner, snapshot))
            result[chatId] = new HashSet<string>(links.Keys, StringComparer.Ordinal);
        return result;
    }

    // chatId → featureId: добавление в множество (вспомогательное).
    private static void AddToSet(Dictionary<string, HashSet<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var set))
            map[key] = set = new HashSet<string>(StringComparer.Ordinal);
        set.Add(value);
    }
}
