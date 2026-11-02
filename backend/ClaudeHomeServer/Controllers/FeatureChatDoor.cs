using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Projector;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Controllers;

// Общий вход дверей REST и MCP в правило «чат привязан к фиче» (FeatureChatBinding, Tasks).
// Своей логики привязки здесь нет: только сборка входов правила и единый код отказа.
public static class FeatureChatDoor
{
    // Код отказа REST (409 { error, code }); текст отказа — DenyReason, один и тот же в REST и MCP.
    public const string DeniedCode = "chat_feature_bound";

    // Узкий снимок живых сессий владельца — единственная сборка для индекса фич и для гарда.
    // Busy — та же занятость чата, что в ClassifyStall (TaskExecutionService.cs:1806): ход идёт или
    // ждёт ответа человека; остальные статусы (Active/Finished/Error/Orphaned) — свободен.
    public static List<FeatureIndexSession> Snapshot(SessionManager sessions, string ownerId) =>
        sessions.GetAllOwnedBy(ownerId)
            .Select(s => new FeatureIndexSession(s.Id, s.TaskId, s.ProjectId, s.IsArchived, s.Model, s.PersonaId,
                s.Status is SessionStatus.Starting or SessionStatus.Working or SessionStatus.Waiting))
            .ToList();

    // Тот же снимок для ЧЕЛОВЕЧЕСКИХ проекций (страница фичи, индекс, список фич: ChatIds, чаты,
    // исполнители, шаги, счётчики «чаты фичи» считаются из него): чужой приватный чат не попадает
    // ни id, ни полем (SessionAccess.CanSee через GetAllOwnedVisibleBy, S2d-4j). Гард привязки
    // и MCP остаются на Snapshot: правилу нужны все чаты владельца.
    public static List<FeatureIndexSession> SnapshotVisible(SessionManager sessions, string viewerId) =>
        sessions.GetAllOwnedVisibleBy(viewerId)
            .Select(s => new FeatureIndexSession(s.Id, s.TaskId, s.ProjectId, s.IsArchived, s.Model, s.PersonaId,
                s.Status is SessionStatus.Starting or SessionStatus.Working or SessionStatus.Waiting))
            .ToList();

    // Маска id чатов в самих задачах облегчённого run (Epic/Steps/Reviews/…: SourceSessionId,
    // ExecutorLaunchedBySessionId, LinkedSessionId, ReportToSessionId) — та же TaskView.MaskForeignChats,
    // что у /api/tasks. Правит КОПИИ из SlimRun/Build (живой store не трогает); для полного run нельзя.
    public static FeatureRun MaskRunChats(FeatureRun run, SessionManager sessions, ProjectManager projects, string viewerId)
    {
        var audience = ChatAudience.Viewer(viewerId);
        void Mask(TaskItem? t) { if (t is not null) TaskView.MaskForeignChats(t, sessions, projects, audience); }
        void MaskAll(IEnumerable<TaskItem> ts) { foreach (var t in ts) Mask(t); }
        Mask(run.Epic); Mask(run.Final); Mask(run.Merge);
        MaskAll(run.Steps); MaskAll(run.Reviews); MaskAll(run.Qa);
        MaskAll(run.Cut); MaskAll(run.Design); MaskAll(run.Plan); MaskAll(run.Unroled);
        return run;
    }

    // Фича, в которую попадает задача: её FeatureId либо она сама, если это эпик.
    public static string? TargetFeature(TaskItem task) =>
        task.FeatureId ?? (task.Role == TaskRoles.Feature ? task.Id : null);

    // Цель при СОЗДАНИИ: явный featureId, а у нового эпика (role=feature без featureId) — сама
    // новая фича, которой нет ни у одного чата, поэтому привязанный чат её не пускает.
    public static string? CreateTarget(string? featureId, string? role) =>
        !string.IsNullOrEmpty(featureId) ? featureId
        : role?.Trim() == TaskRoles.Feature ? "new-feature:" + Guid.NewGuid().ToString("N")
        : null;

    // Цель при ОБНОВЛЕНИИ: фича задачи ПОСЛЕ правки (значение из запроса, если прислано, иначе
    // текущее; пустая строка — очистка) либо сама задача, если она станет эпиком. Возвращает
    // цель только когда она отличается от текущей фичи задачи — иначе правка привязку не меняет.
    public static string? UpdateTarget(TaskItem existing, string? reqFeatureId, string? reqRole)
    {
        var effFeatureId = reqFeatureId ?? existing.FeatureId;
        var effRole = (reqRole ?? existing.Role)?.Trim();
        var target = !string.IsNullOrEmpty(effFeatureId) ? effFeatureId
            : effRole == TaskRoles.Feature ? existing.Id
            : null;
        return target is not null && target != TargetFeature(existing) ? target : null;
    }

    // REST-вход: чат-вызыватель — по заголовку X-Caller-Session-Id. Нет заголовка (кнопка в UI),
    // чужая или несуществующая сессия — привязки нет, гард пропускает.
    public static string? DenyForHeader(
        SessionManager sessions, TaskManager tasks, string ownerId, string? callerSessionHeader,
        string? targetFeatureId)
    {
        if (string.IsNullOrEmpty(targetFeatureId) || string.IsNullOrEmpty(callerSessionHeader)
            || sessions.GetById(callerSessionHeader) is not { } caller
            || sessions.ResolveOwnerId(caller) != ownerId)
            return null;
        return DenyReason(tasks, Snapshot(sessions, ownerId), ownerId, caller.Id, targetFeatureId);
    }

    // Причина отказа; null — пускать. Пустая цель (задача вне фичи) правило не затрагивает.
    public static string? DenyReason(
        TaskManager tasks, IReadOnlyCollection<FeatureIndexSession> snapshot,
        string ownerId, string chatId, string? targetFeatureId)
    {
        if (string.IsNullOrEmpty(targetFeatureId))
            return null;
        return FeatureChatBinding.DenyReason(
            tasks.GetByOwner(ownerId).ToList(), snapshot, chatId, targetFeatureId);
    }
}
