using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Шаблоны процесса фичи (разрез docs/research/ai-fabrika-workflow-cut-2026-11.md §6 И8) поверх
// WorkflowTemplateStore (B1c). Область определяется наличием projectId: параметра нет —
// глобальная (читает любой авторизованный, пишет только admin), параметр есть — проектная
// (читает имеющий доступ к проекту, пишет только владелец — временно, по решению вопроса 3:
// участники общих проектов не пишут). Ключ шаблона — (scope, projectId, id); id для PUT/DELETE —
// из маршрута, id тела игнорируется.
//
// Версия для 409 — отдельный аргумент expectedVersion, НЕ поле шаблона (план 2efc74a5):
// Version из тела стор игнорирует всегда. PUT требует expectedVersion в теле (версию редактор
// получил из GET {id}); без неё — 400 и стор не вызывается: клиент без версии не должен
// затирать чужую правку. POST передаёт expectedVersion: 0 — существующий id даёт 409, а не
// тихую перезапись. Отказы стора раскладываются по Kind: Validation→400 (со списком Errors),
// Conflict→409, NotFound→404, Forbidden→403.
// Группа матрицы — Platform (сторож G1): содержимое живёт в data/workflow-templates.json
// (WorkflowTemplateStore), к файлам проекта на сервере не обращаемся — вход работает у любого
// проекта, включая локальный. Права это не отменяет: чтение проектной области — ProjectRight.Read,
// запись — Configure (И8), их по-прежнему проверяет IProjectAccess в теле (DenyProjectRead /
// DenyWrite); глобальная область — только admin, группа к ней не применяется (без projectId).
[ProjectCapability(ProjectCapabilityArea.Platform)]
[ApiController]
[Authorize]
[Route("api/workflow-templates")]
public class WorkflowTemplatesController(
    WorkflowTemplateStore templates,
    // Для usageCount; null — счётчик нулевой
    TaskManager? tasks = null,
    // Явный резолв доступа к проекту — та же точка, что у ProjectTasksController (сверяем не
    // OwnerId инлайном); null — fail-closed (проект недоступен)
    IProjectAccess? access = null) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;
    private bool IsAdmin => User.IsInRole("admin");

    // Тело POST/PUT: поля шаблона плюс expectedVersion (обязателен для PUT). Version из тела
    // не несёт смысла — её ставит стор (контракт B1b).
    public class WorkflowTemplateUpsertRequest : WorkflowTemplate
    {
        public int? ExpectedVersion { get; set; }
    }

    // Список: глобальные; с ?projectId= — глобальные плюс проектные этого проекта.
    [HttpGet]
    public IActionResult List(string? projectId = null)
    {
        var (scope, pid) = ScopeOf(projectId);
        if (DenyProjectRead(pid) is { } denied) return denied;
        var entries = templates.List(pid);

        // Эпики, доступные вызывающему: свои плюс (при доступном проекте) проектные. Считаем
        // фичи с подтверждённым маршрутом по ключу (область, шаблон).
        var epics = new Dictionary<string, TaskItem>(StringComparer.Ordinal);
        if (tasks is not null)
        {
            foreach (var t in tasks.GetByOwner(UserId)) epics[t.Id] = t;
            if (pid is not null) foreach (var t in tasks.GetByProject(pid)) epics[t.Id] = t;
        }
        var used = epics.Values
            .Where(t => t.Role == TaskRoles.Feature && t.Workflow is not null)
            .GroupBy(t => (t.Workflow!.TemplateId, Project: string.Equals(t.Workflow.Scope, "project", StringComparison.Ordinal)))
            .ToDictionary(g => g.Key, g => g.Count());

        var items = entries.Select(e =>
        {
            var t = e.Template;
            var original = t.BasedOn is { } b ? templates.Get(b.TemplateId, WorkflowTemplateScope.Global) : null;
            return new WorkflowTemplateListItem(
                e.Scope, e.ProjectId, t,
                WorkflowTemplateMeta.StageCount(t),
                WorkflowTemplateMeta.ParallelColumns(t),
                used.GetValueOrDefault((t.Id, e.Scope == WorkflowTemplateScope.Project)),
                t.BasedOn is { } r ? new WorkflowTemplateBasedOn(r.TemplateId, original?.Name ?? r.TemplateId, r.Version) : null,
                original?.Version,
                WorkflowTemplateMeta.DiffFromPreset(t));
        }).ToList();
        return Ok(items);
    }

    // Элемент списка: прежние поля (scope/projectId/template) плюс мета для списка и тулбара.
    public sealed record WorkflowTemplateBasedOn(string TemplateId, string Name, int Version);

    public sealed record WorkflowTemplateListItem(
        WorkflowTemplateScope Scope, string? ProjectId, WorkflowTemplate Template,
        int StageCount, int ParallelColumns, int UsageCount,
        WorkflowTemplateBasedOn? BasedOn, int? BasedOnLatestVersion, int? DiffFromPreset);

    // Карточка одного шаблона: без projectId — глобальный, с ним — проектный.
    [HttpGet("{id}")]
    public IActionResult Get(string id, string? projectId = null)
    {
        var (scope, pid) = ScopeOf(projectId);
        if (DenyProjectRead(pid) is { } denied) return denied;
        return templates.Get(id, scope, pid) is { } template
            ? Ok(template)
            : NotFound(new { error = "Шаблон не найден." });
    }

    // Создание: без projectId — глобальный (admin), с ним — проектный (владелец).
    [HttpPost]
    public IActionResult Create([FromBody] WorkflowTemplateUpsertRequest? body, string? projectId = null)
    {
        var (scope, pid) = ScopeOf(projectId);
        if (DenyWrite(scope, pid) is { } denied) return denied;
        if (body is null) return BadRequest(new { error = "Шаблон не задан." });
        // Пустой id создающий ключ не даёт — генерируем здесь, стор ключует по нему.
        if (string.IsNullOrWhiteSpace(body.Id)) body.Id = Guid.NewGuid().ToString();
        try
        {
            // expectedVersion: 0 — «шаблона с таким id быть не должно»: существующий id → 409.
            return Ok(templates.Save(body, scope, pid, expectedVersion: 0));
        }
        catch (WorkflowTemplateException ex) { return FromStore(ex); }
    }

    // Правка по id из маршрута; expectedVersion обязателен (версия из последнего GET).
    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] WorkflowTemplateUpsertRequest? body, string? projectId = null)
    {
        var (scope, pid) = ScopeOf(projectId);
        if (DenyWrite(scope, pid) is { } denied) return denied;
        if (body is null) return BadRequest(new { error = "Шаблон не задан." });
        if (body.ExpectedVersion is null) return BadRequest(new { error = "expectedVersion обязателен." });
        body.Id = id; // ключ — маршрут, id тела игнорируется
        try
        {
            return Ok(templates.Save(body, scope, pid, expectedVersion: body.ExpectedVersion));
        }
        catch (WorkflowTemplateException ex) { return FromStore(ex); }
    }

    // Удаление по id из маршрута. Встроенный стор не отдаёт (Forbidden → 403): его можно
    // только сбросить к коду.
    [HttpDelete("{id}")]
    public IActionResult Delete(string id, string? projectId = null)
    {
        var (scope, pid) = ScopeOf(projectId);
        if (DenyWrite(scope, pid) is { } denied) return denied;
        try
        {
            templates.Delete(id, scope, pid);
            return NoContent();
        }
        catch (WorkflowTemplateException ex) { return FromStore(ex); }
    }

    // Сброс встроенного пресета к коду (содержимое из кода, Version+1) — только admin:
    // встроенные живут в глобальной области. id — id шаблона (builtin-*), ключ пресета
    // берётся из его BuiltInKey, а не из соглашения об имени id.
    [HttpPost("{id}/reset")]
    public IActionResult Reset(string id)
    {
        if (!IsAdmin)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Сбрасывать встроенные шаблоны может только админ." });
        if (templates.Get(id, WorkflowTemplateScope.Global) is not { } template)
            return NotFound(new { error = "Шаблон не найден." });
        if (template.BuiltInKey is null)
            return BadRequest(new { error = "Сбросить к коду можно только встроенный шаблон." });
        try
        {
            return Ok(templates.ResetBuiltIn(template.BuiltInKey));
        }
        catch (WorkflowTemplateException ex) { return FromStore(ex); }
    }

    // Область по наличию projectId: нет — глобальная, есть — проектная.
    private static (WorkflowTemplateScope Scope, string? ProjectId) ScopeOf(string? projectId) =>
        string.IsNullOrWhiteSpace(projectId)
            ? (WorkflowTemplateScope.Global, null)
            : (WorkflowTemplateScope.Project, projectId.Trim());

    // Чтение проектных шаблонов — имеющему доступ к проекту (Read); посторонний — 404,
    // существование не раскрываем. Глобальные читает любой авторизованный.
    private IActionResult? DenyProjectRead(string? projectId)
    {
        if (projectId is null) return null;
        if (access?.Resolve(UserId, projectId, ProjectRight.Read) is not { } scope) return NotFound();
        HttpContext.SetRequestScope(scope);
        return null;
    }

    // Запись (И8): глобальная — только admin; проектная — только владелец (право Configure,
    // тот же источник, что у [ProjectScope(Right = Configure)]). Участник — 403 «только
    // владелец» (виден по Read — отказ различается правом, а не существом, та же грань, что у
    // ProjectScopeAttribute), посторонний — 404. Отказ — до любого вызова стора.
    private IActionResult? DenyWrite(WorkflowTemplateScope scope, string? projectId)
    {
        if (scope == WorkflowTemplateScope.Global)
            return IsAdmin
                ? null
                : StatusCode(StatusCodes.Status403Forbidden,
                    new { error = "Глобальные шаблоны пишет только админ." });
        if (access?.Resolve(UserId, projectId, ProjectRight.Configure) is { } ownerScope)
        {
            HttpContext.SetRequestScope(ownerScope);
            return null;
        }
        return access?.Resolve(UserId, projectId, ProjectRight.Read) is not null
            ? StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Проектные шаблоны процесса пишет только владелец проекта.", code = ProjectScopeAttribute.ErrorCode })
            : NotFound();
    }

    // Отказ стора → HTTP по Kind (контракт B1c): Validation — 400 со списком ошибок
    // валидатора, дальше по списку контракта.
    private IActionResult FromStore(WorkflowTemplateException ex) => ex.Kind switch
    {
        WorkflowTemplateErrorKind.Validation => BadRequest(new { error = ex.Message, errors = ex.Errors }),
        WorkflowTemplateErrorKind.Conflict => Conflict(new { error = ex.Message }),
        WorkflowTemplateErrorKind.NotFound => NotFound(new { error = ex.Message }),
        _ => StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message }),
    };
}
