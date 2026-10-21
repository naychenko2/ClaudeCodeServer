using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Tasks.Projector;

// ─── Б9c, шаг 2: единое правило «чат → фичи» (хозяин — проектор) ────────────────────────────
//
// Членство чата в фичах — ЕДИНОЕ правило (решение Александра, доработка Б9c): раньше оно жило
// в двух копиях (BuildIndexCore и ChatFeatureIds) плюс в третьей, ad-hoc, в контроллере (блок
// keyChatId Б9b + эпик-уровневый список общих чатов Б9c). Три копии расходились: «общий чат»
// с точки зрения Б9b (ключевой у одной фичи) и с точки зрения Б9c (эпик-уровневый список) —
// разные наборы, и расход общего чата оседал в одной фиче (проба F Глеба).
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
    // chatId → множество featureId, в которые чат входит (executor ∪ curator ∪ orchestrator).
    // Чат, не входящий ни в одну фичу, в карте не живёт.
    public static IReadOnlyDictionary<string, HashSet<string>> Build(
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

        // chatId → featureId (множество): executor ∪ curator ∪ orchestrator.
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var s in snapshot)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            // executor: Session.TaskId → задача → её фича (эпик — сама фича).
            if (s.TaskId is not null && byId.TryGetValue(s.TaskId, out var task))
            {
                var featureId = task.FeatureId ?? (task.Role == TaskRoles.Feature ? task.Id : null);
                if (featureId is not null) set.Add(featureId);
            }
            // curator: epic.SourceSessionId == чат.
            if (curatorBySession.TryGetValue(s.Id, out var curators))
                set.UnionWith(curators);
            // orchestrator: эпик/дочерняя задача запускала исполнителя из этого чата.
            if (orchestratorBySession.TryGetValue(s.Id, out var orchestrators))
                set.UnionWith(orchestrators);
            if (set.Count > 0) map[s.Id] = set;
        }
        return map;
    }

    // chatId → featureId: добавление в множество (вспомогательное, как в BuildIndexCore).
    private static void AddToSet(Dictionary<string, HashSet<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var set))
            map[key] = set = new HashSet<string>(StringComparer.Ordinal);
        set.Add(value);
    }
}
