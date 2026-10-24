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
    public static List<FeatureIndexSession> Snapshot(SessionManager sessions, string ownerId) =>
        sessions.GetAllOwnedBy(ownerId)
            .Select(s => new FeatureIndexSession(s.Id, s.TaskId, s.ProjectId, s.IsArchived, s.Model, s.PersonaId))
            .ToList();

    // Фича, в которую попадает задача: её FeatureId либо она сама, если это эпик.
    public static string? TargetFeature(TaskItem task) =>
        task.FeatureId ?? (task.Role == TaskRoles.Feature ? task.Id : null);

    // Цель при СОЗДАНИИ: явный featureId, а у нового эпика (role=feature без featureId) — сама
    // новая фича, которой нет ни у одного чата, поэтому привязанный чат её не пускает.
    public static string? CreateTarget(string? featureId, string? role) =>
        !string.IsNullOrEmpty(featureId) ? featureId
        : role == TaskRoles.Feature ? "new-feature:" + Guid.NewGuid().ToString("N")
        : null;

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
