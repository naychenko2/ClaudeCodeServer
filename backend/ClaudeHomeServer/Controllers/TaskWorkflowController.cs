using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// B4b (разрезы docs/research/ai-fabrika-workflow-cut-2026-11.md §5 «Запуск»/«Подтверждение» и
// docs/research/ai-fabrika-workflow-2-cut-2026-11.md §1.2, §3 Г1): ручки подбора и подтверждения
// маршрута фичи.
//
// И7: epic.Workflow пишет только confirm — вызов TaskManager.ConfirmWorkflow вне вертикали Tasks
// здесь единственный; рекомендатель пишет только WorkflowProposal; TasksController PUT и MCP
// tasks_update поля не принимают (закрыто в B2).
// Г1: граф подтверждённого маршрута строит только WorkflowGraph.Resolve в этой ручке — от клиента
// граф не принимается: в теле есть только шаблон, область и id пропускаемых этапов.
//
// Ключ — эпик по id: чужой/несуществующий/не-эпик дают один 404 (существование чужой фичи не
// раскрываем, как у соседних ручек hold/note). Запись — через TaskManager, на экран — штатный
// task_changed по эпику. MCP-инструмента подтверждения нет: подтверждает только человек.
[ProjectCapability(ProjectCapabilityArea.Platform)]
[ApiController]
[Authorize]
[Route("api/tasks/{taskId}/workflow")]
[SubsystemUsage("tasks")]
public class TaskWorkflowController(
    TaskManager tasks, WorkflowRecommender recommender, WorkflowTemplateStore templates,
    ISessionBroadcaster broadcaster, SessionManager sessions, ProjectManager projects,
    // Явный резолв доступа к проекту эпика — та же точка, что у FeatureOwnerGate на двери правки;
    // null — fail-closed (проект недоступен)
    IProjectAccess? access = null) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Тело confirm: { templateId, scope, skipped }. Полей графа у клиента нет: Stages сервер строит
    // Resolve'ом по шаблону и пропускам (Г1), любые другие поля тела игнорируются. scope
    // принимается (его присылает экран), но пишется та область, где шаблон реально лежит в сторе
    // (И8: проектный шаблон — его проект), а не та, что прислал клиент.
    public record ConfirmRequest(string? TemplateId, string? Scope, List<string>? Skipped);

    // POST /api/tasks/{id}/workflow/recommend — запуск подбора (место feature-workflow-recommend,
    // B4a) и ответ текущим предложением эпика. Предложения может не быть: сбой модели, мусор
    // ответа, уже подтверждённый маршрут (его устаревшее предложение в ответ не доезжает), пустой
    // каталог — обычное рабочее состояние (маршрут выбирает человек), поэтому 200 с proposal = null,
    // а не отказ.
    [HttpPost("recommend")]
    public async Task<IActionResult> Recommend(string taskId, CancellationToken ct)
    {
        if (EpicOfCaller(taskId) is not { } epic) return NotFound(new { error = "Фича не найдена" });

        // RecommendWithReasonAsync сам не бросает (кроме осознанной отмены вызывающим): наружу идёт только
        // отрезанное им по белым спискам предложение. Ключ — владелец записи, а не тот, кто нажал
        // кнопку: рекомендатель сверяет ownerId с OwnerId эпика, и вызов по UserId молча дал бы
        // «нет предложения», если право Write когда-нибудь получит участник.
        var outcome = await recommender.RecommendWithReasonAsync(taskId, epic.OwnerId!, ct);

        // Ответ — то, что стало в эпике после вызова, а не объект, вернувший рекомендатель:
        // параллельный подбор мог записать в эпике своё предложение целым.
        var current = tasks.GetById(taskId);
        var body = WorkflowState(current, outcome.Reason);
        // И7: подтверждённый маршрут никогда не сопровождается «живым» предложением в ответе
        // (ConfirmWorkflow предложения не очищает) и о нём не вещаем.
        if (current?.Workflow is null && current?.WorkflowProposal is not null) await TaskBoardAsync(current);
        return Ok(body);
    }

    // GET /api/tasks/{id}/workflow — текущее состояние без запуска подбора (B03): предложение
    // (state none|proposed), подтверждённый маршрут с rationale предложения и предупреждение
    // «шаблон изменился» (templateUpdate).
    [HttpGet("")]
    public IActionResult Get(string taskId)
    {
        if (EpicOfCaller(taskId) is not { } epic) return NotFound(new { error = "Фича не найдена" });
        return Ok(WorkflowState(epic, null));
    }

    // POST /api/tasks/{id}/workflow/confirm — подтверждение маршрута человеком. Решает тело
    // запроса: WorkflowProposal не читается, подбор, пришедший во время подтверждения, маршрут
    // не меняет. Человек может прислать шаблон и пропуски, отличные от предложения.
    [HttpPost("confirm")]
    public async Task<IActionResult> Confirm(string taskId, [FromBody] ConfirmRequest? req)
    {
        if (EpicOfCaller(taskId) is not { } epic) return NotFound(new { error = "Фича не найдена" });

        var templateId = req?.TemplateId?.Trim() ?? "";
        if (templateId.Length == 0)
            return BadRequest(new { error = "Нужен templateId подтверждаемого шаблона" });

        // Каталог эпика: глобальные шаблоны плюс его проекта — тот же список, что отдаётся
        // recommendателю. Область пишется та, где шаблон лежит в сторе.
        var entry = templates.List(epic.ProjectId).FirstOrDefault(e => e.Template.Id == templateId);
        if (entry is null)
            return BadRequest(new { error = $"Шаблон «{templateId}» среди доступных этому проекту не найден." });

        // Белый список пропуска: снять можно только этап Presence=optional выбранного шаблона,
        // ключ — id этапа (у линейных шаблонов он равен роли). id несъёмного или чужого этапа —
        // 400: молча снять несъёмное или проигнорировать опечатку нельзя (тот же список, что у B4a).
        var optional = entry.Template.Stages
            .Where(s => s.Presence == WorkflowStagePresence.Optional)
            .Select(s => string.IsNullOrWhiteSpace(s.Id) ? s.Role : s.Id!)
            .ToHashSet(StringComparer.Ordinal);
        // B03: клиент skipped не прислал (null) и маршрут уже подтверждён — обновление версии берёт
        // прежние пропуски. Из них остаются те, что и в новой версии шаблона съёмные: этап, ставший
        // обязательным или исчезнувший, молча не снимаем и 400 на человеке не вешаем. Явный [] —
        // решение человека «ничего не снимать» и наследованием не подменяется.
        // req здесь точно не null: пустое тело не прошло проверку templateId выше
        var requested = req!.Skipped ?? (epic.Workflow is { } prev && prev.TemplateId == templateId
            ? prev.Skipped.Where(optional.Contains).ToList() : []);
        var skipped = requested.Select(s => string.IsNullOrWhiteSpace(s) ? "" : s.Trim())
            .Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (skipped.FirstOrDefault(s => !optional.Contains(s)) is { } notSkippable)
            return BadRequest(new { error = $"Снять можно только этап с Presence=optional: «{notSkippable}» не снимается." });

        var workflow = new FeatureWorkflow
        {
            TemplateId = entry.Template.Id,
            Scope = entry.Scope == WorkflowTemplateScope.Project ? "project" : "global",
            // Г1: единственная точка построения графа подтверждённого маршрута
            Stages = WorkflowGraph.Resolve(entry.Template, skipped),
            Skipped = skipped,
            ConfirmedVersion = entry.Template.Version,
            ConfirmedAt = DateTime.UtcNow,
            ConfirmedByUser = true,
        };

        // И7: единственный путь записи epic.Workflow
        var written = tasks.ConfirmWorkflow(taskId, workflow);
        if (written is null) return NotFound(new { error = "Фича не найдена" });
        await TaskBoardAsync(written);
        return Ok(new { workflow = written.Workflow, confirmed = ConfirmedDto(written), templateUpdate = (object?)null, updateAvailable = false });
    }

    public record KeepVersionRequest(int TemplateVersion);

    // POST /api/tasks/{id}/workflow/keep-version { templateVersion } — «Оставить v1» (B03): человек
    // отказался от обновления маршрута до версии templateVersion. Пишется confirmed.dismissedUpdateVersion;
    // предупреждение «шаблон изменился» не отдаётся, пока версия шаблона не вырастет выше. Маршрут
    // (граф, пропуски, ConfirmedVersion) не меняется. Версия должна быть новее подтверждённой и не новее
    // текущей версии шаблона в каталоге — иначе 400.
    [HttpPost("keep-version")]
    public async Task<IActionResult> KeepVersion(string taskId, [FromBody] KeepVersionRequest? req)
    {
        if (EpicOfCaller(taskId) is not { } epic) return NotFound(new { error = "Фича не найдена" });
        if (epic.Workflow is not { } wf) return Conflict(new { error = "Маршрут фичи ещё не подтверждён" });

        var version = req?.TemplateVersion ?? 0;
        var current = CurrentTemplateEntry(epic, wf)?.Template.Version;
        if (version <= wf.ConfirmedVersion || current is null || version > current)
            return BadRequest(new { error = "templateVersion должна быть новее подтверждённой и не новее текущей версии шаблона." });

        var written = tasks.KeepWorkflowVersion(taskId, version);
        if (written is null) return Conflict(new { error = "Маршрут фичи ещё не подтверждён" });
        await TaskBoardAsync(written);
        return Ok(new { workflow = written.Workflow, confirmed = ConfirmedDto(written), templateUpdate = TemplateUpdate(written), updateAvailable = TemplateUpdate(written) is not null });
    }

    // Тело ответа подбора и GET: предложение и подтверждённый маршрут. proposal — только у
    // неподтверждённого эпика (state proposed | none c reason), у подтверждённого — null.
    private object WorkflowState(TaskItem? epic, string? reason)
    {
        if (epic?.Workflow is not null)
            return new { proposal = (object?)null, confirmed = ConfirmedDto(epic), templateUpdate = TemplateUpdate(epic), updateAvailable = TemplateUpdate(epic) is not null };
        if (epic?.WorkflowProposal is { } p)
            return new
            {
                proposal = new
                {
                    state = "proposed", templateId = p.TemplateId, scope = p.Scope, skipped = p.Skipped,
                    rationale = p.Rationale, proposedAt = p.ProposedAt, model = p.Model,
                    needsHuman = p.NeedsHuman, doubtReason = p.DoubtReason,
                },
                confirmed = (object?)null, templateUpdate = (object?)null, updateAvailable = false,
            };
        return new
        {
            proposal = new { state = "none", reason = reason ?? WorkflowRecommender.ReasonNoAnswer },
            confirmed = (object?)null, templateUpdate = (object?)null, updateAvailable = false,
        };
    }

    // Подтверждённый маршрут для экрана. rationale/needsHuman/doubtReason/proposedAt — только чтение
    // из предложения, которое сервер не стирает; proposedTemplateId показывает, совпал ли выбор
    // человека с предложением (rationale относится к шаблону предложения, не подтверждённого).
    private static object ConfirmedDto(TaskItem epic)
    {
        var wf = epic.Workflow!;
        var p = epic.WorkflowProposal;
        return new
        {
            templateId = wf.TemplateId, scope = wf.Scope, skipped = wf.Skipped,
            confirmedVersion = wf.ConfirmedVersion, confirmedAt = wf.ConfirmedAt,
            confirmedByUser = wf.ConfirmedByUser, autoConfirmed = wf.AutoConfirmed,
            dismissedUpdateVersion = wf.DismissedUpdateVersion,
            proposedTemplateId = p?.TemplateId, rationale = p?.Rationale, needsHuman = p?.NeedsHuman,
            doubtReason = p?.DoubtReason, proposedAt = p?.ProposedAt,
        };
    }

    // Текущая запись шаблона подтверждённого маршрута в каталоге эпика (та же область, что при
    // подтверждении); null — шаблон удалён.
    private WorkflowTemplateEntry? CurrentTemplateEntry(TaskItem epic, FeatureWorkflow wf)
    {
        var scope = wf.Scope == "project" ? WorkflowTemplateScope.Project : WorkflowTemplateScope.Global;
        return templates.List(epic.ProjectId)
            .FirstOrDefault(e => e.Template.Id == wf.TemplateId && e.Scope == scope);
    }

    // «Шаблон изменился» (Ф-2.14/Ф-2.16): версия в каталоге выше подтверждённой И выше отклонённой
    // («Оставить v1») И состав маршрута правда другой — наборы id из Resolve(текущий шаблон,
    // прежние пропуски) и замороженного графа не совпадают. Переименование этапа, правка его
    // промпта, модели или прав состава не касаются: такие правки и так действуют со следующего
    // шага (новые задачи берут текущую версию шаблона), и предупреждения быть не должно.
    // Та же семантика, что у templateChanged на фронте: сравнение наборов id, не порядка.
    private object? TemplateUpdate(TaskItem epic)
    {
        var wf = epic.Workflow!;
        if (CurrentTemplateEntry(epic, wf) is not { } entry) return null;
        var now = entry.Template.Version;
        if (now <= wf.ConfirmedVersion || now <= (wf.DismissedUpdateVersion ?? 0)) return null;
        var currentIds = new HashSet<string>(WorkflowGraph.Resolve(entry.Template, wf.Skipped).Select(s => s.Id), StringComparer.Ordinal);
        if (currentIds.SetEquals(wf.Stages.Select(s => s.Id))) return null;
        return new { fromVersion = wf.ConfirmedVersion, toVersion = now };
    }

    // Эпик вызывающего: своя запись или эпик проекта, где у вызывающего право Write — ровно тот
    // же предикат владения, что FeatureOwnerGate.Denied на двери правки (FeatureOwnerGate.cs:95-101:
    // «только владелец» — ProjectRight.Write через IProjectAccess; участнику Write закрыт замком
    // ProjectAccessService.cs:29). Отказ — 404, не 403: существование чужой фичи не раскрываем
    // (тот же смысл, что поиск по GetByOwner в note/hold, TasksController.cs:1319 и :1227).
    private TaskItem? EpicOfCaller(string taskId)
    {
        var epic = tasks.GetById(taskId);
        if (epic is null || epic.Role != TaskRoles.Feature) return null;
        return FeatureOwnerGate.IsOwnerOf(access, UserId, epic) ? epic : null;
    }

    // Изменение эпика доезжает до экрана штатным событием доски (тот же формат, что у hold/note);
    // адресат полной карточки — владелец записи, не тот, кто нажал кнопку.
    private Task TaskBoardAsync(TaskItem epic) =>
        broadcaster.ToTaskBoard(epic.OwnerId!, epic.ProjectId, new TaskChangedMessage("updated", epic), sessions, projects);
}
