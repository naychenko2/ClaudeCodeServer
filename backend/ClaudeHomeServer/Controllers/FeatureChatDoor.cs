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

    // Приватный чат (Session.PrivateToUserId) не привязывается к фиче (S2d-7b-2): чат фичи
    // виден участникам проекта через страницу фичи, а приватный — только автору. Код общий
    // для REST (409 { error, code }), MCP-отказа и обратного направления (смена видимости).
    public const string PrivateChatCode = "private_chat_mode";
    public const string PrivateChatReason =
        "Приватный чат нельзя привязать к фиче: сначала сделайте чат общим либо работайте с фичей из общего чата.";

    // Дверь «приватный чат»: чат-вызыватель приватный, а действие привязывает его к фиче
    // (цель непустая) → причина отказа. Признак берётся с серверной сессии, не из тела запроса.
    public static string? DenyPrivate(Session? caller, string? targetFeatureId) =>
        !string.IsNullOrEmpty(targetFeatureId) && caller?.PrivateToUserId is not null ? PrivateChatReason : null;

    // Режимы чата с общим состоянием (S2d-7b-3): штаб «Командной реализации» и групповой чат
    // видят общую команду персон и пишут в общий контур, поэтому приватному чату недоступны.
    // Включить режим на приватном чате нельзя; обратное направление (сделать приватным чат,
    // уже являющийся штабом или группой) закрывает SessionManager.SetChatVisibility. Код тот же.
    public const string PrivateModeReason =
        "Приватный чат нельзя сделать штабом или групповым: сначала сделайте чат общим.";
    public const string ModeChatPrivateReason =
        "Штаб или групповой чат нельзя сделать приватным: у него уже есть команда персон.";

    // Дверь «приватный чат — штаб или группа»: признак берётся с серверной сессии.
    public static string? DenyPrivateMode(Session? chat) =>
        chat?.PrivateToUserId is not null ? PrivateModeReason : null;

    // REST-вход той же двери: чат-вызыватель — по заголовку X-Caller-Session-Id (его ставит конфиг
    // хода). Без владельца в сверке: чужой приватный чат в заголовке — тоже отказ (fail-closed).
    public static string? DenyPrivateForHeader(SessionManager sessions, string? callerSessionHeader, string? targetFeatureId) =>
        string.IsNullOrEmpty(callerSessionHeader) ? null : DenyPrivate(sessions.GetById(callerSessionHeader), targetFeatureId);

    // Узкий снимок живых сессий владельца — единственная сборка для индекса фич и для гарда.
    // Busy — та же занятость чата, что в ClassifyStall (TaskExecutionService.cs:1806): ход идёт или
    // ждёт ответа человека; остальные статусы (Active/Finished/Error/Orphaned) — свободен.
    public static List<FeatureIndexSession> Snapshot(SessionManager sessions, string ownerId) =>
        sessions.GetAllOwnedBy(ownerId)
            .Select(s => new FeatureIndexSession(s.Id, s.TaskId, s.ProjectId, s.IsArchived, s.Model, s.PersonaId,
                s.Status is SessionStatus.Starting or SessionStatus.Working or SessionStatus.Waiting,
                s.ContinuedInSessionId))
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
