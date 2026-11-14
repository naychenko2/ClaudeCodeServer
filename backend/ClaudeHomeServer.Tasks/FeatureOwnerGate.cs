using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Access;

namespace ClaudeHomeServer.Services.Tasks;

/// <summary>
/// Фичевые поля и жизненный цикл фичи — только владелец проекта (возврат ревью R-F2-T1:
/// разрез §2.1 оставил владельцу «всё по фичам»). Дверь правки задачи общего проекта открыта
/// редактору правом WriteContent (F2-T1), но она не должна отдавать ему ведение фичи
/// арендатора: роль, фичу, план, связь с ревью, вердикт и свидетельства решает только
/// владелец — как и решение закрыть или возобновить эпик (FeatureProjector.TerminalState
/// выводит состояние фичи из статуса эпика, закрытие снимает удержание) и как решение закрыть
/// мерж-задачу (F2-M2, п.2 a64356f9: состояние «merged» выводится из закрытого мержа —
/// closedMerges в FeatureProjector.cs:83). Исполнительские
/// поля задачи (заголовок, описание, статус обычной задачи, подзадачи, колонка, порядок,
/// исполнитель, срок) остаются редактору.
///
/// Единый предикат на все двери: REST PUT (TasksController.Update) и REST POST создания задачи
/// (ProjectTasksController.Create), MCP tasks_update / tasks_complete / tasks_create (TasksToolset)
/// зовут методы этого класса — своей копии правила в тулсете и контроллерах нет.
/// </summary>
public static class FeatureOwnerGate
{
    public const string ErrorCode = "feature_owner_only";
    public const string DeniedText =
        "Фичевые поля и жизненный цикл фичи — только владелец проекта";

    /// <summary>
    /// Трогает ли запрос фичевые поля или жизнь фичи: role / featureId / planSteps / reviewOf —
    /// прислано любое из них, включая сброс (пустая строка, пустой список): снять роль или снять
    /// связь с ревью — тоже решение по фиче. Плюс смена статуса задачи-эпика (role=feature) или
    /// мерж-задачи (role=merge) в любую сторону: закрытие и возобновление эпика решают состояние
    /// фичи, а «merged» выводится из закрытого мержа (closedMerges, FeatureProjector.cs:83) —
    /// закрыть или возобновить мерж редактором значило бы перевести чужую фичу в «merged»
    /// или обратно без решения владельца (F2-M2). Статус обычной задачи (в том числе шага
    /// фичи) — исполнительское поле, его правит редактор: оно здесь не участвует.
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
        || ((task.Role == TaskRoles.Feature || task.Role == TaskRoles.Merge)
            && req.Status is not null && req.Status != task.Status);

    /// <summary>
    /// Трогает ли запрос НА СОЗДАНИИ фичевые поля: role / featureId / planSteps / reviewOf —
    /// прислано любое из них. В отличие от правки, пустая строка здесь — «не задано», а не «снять»:
    /// у создаваемой задачи поля ещё нет, а TaskManager.Create нормализует "" в null (Trimmed,
    /// TaskManager.cs:430-431) — смысла «снять роль» из клаузы правки здесь не возникает.
    /// Оговорки про вердикт/свидетельства нет: без role/featureId создаётся обычная задача, и её
    /// вердикт — исполнительское поле (тот же смысл, что у оговорки правки c2b408f8); с role/featureId
    /// первые клаузы уже вызывают.
    /// </summary>
    public static bool Touches(CreateTaskRequest req) =>
        !string.IsNullOrWhiteSpace(req.Role) || !string.IsNullOrWhiteSpace(req.FeatureId)
        || req.PlanSteps is not null || req.ReviewOf is not null;

    /// <summary>
    /// Текст отказа или null — фичевые поля этого создания актору разрешены. Мерится тем же, что
    /// <see cref="Denied"/>: актор (REST — человек из токена, MCP — актор идущего хода) против
    /// будущего владельца записи (арендатор проекта — ключ записи, не автор: задача участника
    /// лежит под арендатором, F2-T1); право «только владелец» — ProjectRight.Write через ту же
    /// IProjectAccess. Актор = арендатор — разрешено (свой проект: вызывающий и есть владелец).
    /// Личная задача без проекта: под своим ключом — разрешено; под чужим (агентская личная
    /// задача в контексте чата арендатора) — тот же отказ, что «чужая личная задача» правки.
    /// </summary>
    public static string? DeniedForCreate(
        IProjectAccess? access, string actorUserId, string recordOwnerId, string? projectId,
        CreateTaskRequest req)
    {
        if (!Touches(req)) return null;
        if (recordOwnerId == actorUserId) return null;
        if (string.IsNullOrEmpty(projectId)) return DeniedText;
        return access?.Resolve(actorUserId, projectId, ProjectRight.Write) is null ? DeniedText : null;
    }

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
        return IsOwnerOf(access, actorUserId, task) ? null : DeniedText;
    }

    /// <summary>
    /// «Только владелец» для записи: актор — владелец записи (личная задача, свой проект) либо у него
    /// право <see cref="ProjectRight.Write"/> на проект задачи. Единый предикат двери правки
    /// (<see cref="Denied"/>) и ручек маршрута эпика (TaskWorkflowController): расхождение двух
    /// копий дало бы дверь, открытую с одной стороны.
    /// </summary>
    public static bool IsOwnerOf(IProjectAccess? access, string actorUserId, TaskItem task)
    {
        if (task.OwnerId == actorUserId) return true;
        if (string.IsNullOrEmpty(task.ProjectId)) return false;
        return access?.Resolve(actorUserId, task.ProjectId, ProjectRight.Write) is not null;
    }
}
