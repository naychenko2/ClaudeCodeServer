using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Access;

namespace ClaudeHomeServer.Services.Tasks;

/// <summary>
/// Фичевые поля и жизненный цикл фичи — только владелец проекта (возврат ревью R-F2-T1:
/// разрез §2.1 оставил владельцу «всё по фичам»). Дверь правки задачи общего проекта открыта
/// редактору правом WriteContent (F2-T1), но она не должна отдавать ему ведение фичи
/// арендатора: роль, фичу, план, связь с ревью, вердикт и свидетельства решает только
/// владелец — как и решение закрыть или возобновить эпик (FeatureProjector.TerminalState
/// выводит состояние фичи из статуса эпика, закрытие снимает удержание). Исполнительские
/// поля задачи (заголовок, описание, статус обычной задачи, подзадачи, колонка, порядок,
/// исполнитель, срок) остаются редактору.
///
/// Единый предикат на обе двери: REST PUT (TasksController.Update) и MCP tasks_update /
/// tasks_complete (TasksToolset) зовут этот метод — своей копии правила в тулсете нет.
/// </summary>
public static class FeatureOwnerGate
{
    public const string ErrorCode = "feature_owner_only";
    public const string DeniedText =
        "Фичевые поля и жизненный цикл фичи — только владелец проекта";

    /// <summary>
    /// Трогает ли запрос фичевые поля или жизнь фичи: role / featureId / planSteps / reviewOf —
    /// прислано любое из них, включая сброс (пустая строка, пустой список): снять роль или снять
    /// связь с ревью — тоже решение по фиче. Плюс смена статуса задачи-эпика (role=feature) в
    /// любую сторону: закрытие и возобновление эпика решают состояние фичи. Статус обычной задачи
    /// (в том числе шага фичи) — исполнительское поле, его правит редактор: оно здесь не участвует.
    ///
    /// verification / evidence — фичевые поля ТОЛЬКО у задачи фичи (её Role или FeatureId заданы):
    /// вердикт и свидетельства шага/ревью ведёт постановщик фичи. У обычной задачи и дефекта вне
    /// фичи это исполнительские поля: фронт шлёт verification при каждом сохранении карточки
    /// (TaskEditForm), и без этой оговорки редактор общего проекта не мог закрыть обычный дефект
    /// с заметкой проверки (возврат дельта-ревью c2b408f8).
    /// </summary>
    public static bool Touches(UpdateTaskRequest req, TaskItem task) =>
        req.Role is not null || req.FeatureId is not null || req.PlanSteps is not null
        || req.ReviewOf is not null
        || (task.Role is not null || task.FeatureId is not null)
            && (req.Verification is not null || req.Evidence is not null)
        || (task.Role == TaskRoles.Feature && req.Status is not null && req.Status != task.Status);

    /// <summary>
    /// Текст отказа или null — фичевые поля этого запроса актору разрешены. Актор — ВЫЗЫВАЮЩИЙ
    /// (REST — человек из токена, MCP — актор идущего хода), а не владелец записи: запись
    /// участника лежит под арендатором (ключ хранилищ), поэтому сверка с <c>task.OwnerId</c>
    /// всегда говорила бы «арендатор» и пропускала бы редактора — на этом стояла находка
    /// R-F2-T1 (фичевые гейты TaskManager мерили запись, а не вызывающего).
    /// Своя запись (личная задача, свой проект) — разрешено: там вызывающий и есть владелец.
    /// Чужая личная задача — отказ: её фичу ведёт её владелец.
    /// Право «только владелец» — <see cref="ProjectRight.Write"/>: оно закрыто участнику
    /// замком ProjectAccessService (тот же выбор, что у переноса задачи между проектами).
    /// </summary>
    public static string? Denied(IProjectAccess? access, string actorUserId, TaskItem task, UpdateTaskRequest req)
    {
        if (!Touches(req, task)) return null;
        if (task.OwnerId == actorUserId) return null;
        if (string.IsNullOrEmpty(task.ProjectId)) return DeniedText;
        return access?.Resolve(actorUserId, task.ProjectId, ProjectRight.Write) is null ? DeniedText : null;
    }
}
