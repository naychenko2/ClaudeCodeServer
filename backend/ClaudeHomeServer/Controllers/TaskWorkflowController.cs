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
    // ответа, уже подтверждённый маршрут, пустой каталог — обычное рабочее состояние (маршрут
    // выбирает человек), поэтому 200 с proposal = null, а не отказ.
    [HttpPost("recommend")]
    public async Task<IActionResult> Recommend(string taskId, CancellationToken ct)
    {
        if (EpicOfCaller(taskId) is null) return NotFound(new { error = "Фича не найдена" });

        // RecommendAsync сам не бросает (кроме осознанной отмены вызывающим): наружу идёт только
        // отрезанное им по белым спискам предложение.
        await recommender.RecommendAsync(taskId, UserId, ct);

        // Ответ — то, что стало в эпике после вызова, а не объект, вернувший рекомендатель:
        // параллельный подбор мог записать в эпике своё предложение целым.
        var epic = tasks.GetById(taskId);
        var proposal = epic?.WorkflowProposal;
        if (proposal is not null) await TaskBoardAsync(epic!);
        return Ok(new { proposal });
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
        // req здесь точно не null: пустое тело не прошло проверку templateId выше
        var skipped = (req!.Skipped ?? []).Select(s => string.IsNullOrWhiteSpace(s) ? "" : s.Trim())
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
        return Ok(new { workflow = written.Workflow });
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
        if (epic.OwnerId == UserId) return epic;
        return epic.ProjectId is { } pid && access?.Resolve(UserId, pid, ProjectRight.Write) is not null
            ? epic
            : null;
    }

    // Изменение эпика доезжает до экрана штатным событием доски (тот же формат, что у hold/note);
    // адресат полной карточки — владелец записи, не тот, кто нажал кнопку.
    private Task TaskBoardAsync(TaskItem epic) =>
        broadcaster.ToTaskBoard(epic.OwnerId!, epic.ProjectId, new TaskChangedMessage("updated", epic), sessions, projects);
}
