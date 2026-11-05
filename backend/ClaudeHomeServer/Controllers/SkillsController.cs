using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Skills;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Controllers;

[ProjectCapability(ProjectCapabilityArea.FileBound)]
[ApiController]
[Authorize]
public class SkillsController(
    SkillsService skills,
    SkillsCliService cli,
    SkillSuggestService suggest,
    SkillGenerationService generation,
    SkillTranslationService translation,
    PluginSkillLocalizer pluginLocalizer,
    PersonaManager personas,
    PersonaBindingsService bindings,
    ProjectManager projects,
    IProjectAccess access,
    IProjectFiles projectFiles) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Доступ сверяем ЗДЕСЬ явным Resolve (projectId бывает и в теле — [ProjectScope] его не видит):
    // ProjectManager.GetById проверяет только существование, и без этой сверки по чужому projectId
    // читались и перезаписывались агенты чужого проекта (а текст агента — инструкции, которые
    // попадут в ход его владельца). 404, а не 403: существование чужого проекта не подтверждаем.
    private Project ScopedProject(string projectId, ProjectRight right)
    {
        var p = access.Resolve(UserId, projectId, right) is null ? null : projects.GetById(projectId);
        if (p is null)
            throw new KeyNotFoundException($"Проект не найден: {projectId}");
        return p;
    }

    private string GetRoot(string projectId) =>
        // projectId бывает и в теле запроса — атрибут группы его не видит, отказ здесь
        ProjectCapabilityGuard.ServerRoot(ScopedProject(projectId, ProjectRight.Write));

    // Список скиллов: глобальные + проектные + агенты проекта + workflow-скрипты + плагины.
    // Проектная часть — через шов файлов тем же кодом, что у агента устройства (ADR-016)
    [HttpGet("api/projects/{projectId}/skills")]
    public async Task<IActionResult> List(string projectId, CancellationToken ct)
    {
        try
        {
            var project = ScopedProject(projectId, ProjectRight.Read);
            return Ok(new
            {
                skills = skills.GetGlobalSkills(),
                projectSkills = await skills.GetProjectSkillsAsync(projectFiles, project, ct),
                agents = await skills.GetProjectAgentsAsync(projectFiles, project, ct),
                workflows = skills.GetGlobalWorkflows(),
                // Плагины профиля CLI (installed_plugins.json) и пакеты владельца — разные
                // источники (ADR-031 §2.5): в API метятся явно.
                plugins = pluginLocalizer.Localize(skills.GetPluginSkills()),
                ownerPlugins = pluginLocalizer.Localize(skills.GetOwnerPluginSkills(UserId, project)),
            });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Список глобальных скиллов (без привязки к проекту) — для чатов вне проекта.
    // Workflow-скрипты и скиллы плагинов добавляются в общий список: в композере они
    // вызываются той же командой /имя, что и скиллы (разворачивает их сам CLI).
    [HttpGet("api/skills")]
    public IActionResult ListGlobal() =>
        Ok(skills.GetGlobalSkills().Concat(skills.GetGlobalWorkflows())
            .Concat(pluginLocalizer.Localize(skills.GetPluginSkills()))
            // Пакеты владельца — отдельный источник (ADR-031 §2.5); ход без привязки
            // (project null) — все включённые пакеты, локальности нет.
            .Concat(pluginLocalizer.Localize(skills.GetOwnerPluginSkills(UserId, null))).ToList());

    // --- Реестр: поиск и установка (обёртка npx skills) ---

    // Поиск навыков по реестру skills.sh. owner — опциональное сужение по GitHub-владельцу.
    [HttpGet("api/skills/find")]
    public async Task<IActionResult> Find([FromQuery] string q, [FromQuery] string? owner, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            return BadRequest(new { error = "Запрос должен быть не короче 2 символов" });
        // Реестр skills.sh — на английском: русский запрос переводим, иначе поиск мимо
        var query = await translation.TranslateQueryAsync(q.Trim(), ct);
        var results = await cli.FindAsync(query, owner, ct);
        // translatedQuery отдаём фронту — показать, что реально искали
        return Ok(new { query, translatedQuery = query != q.Trim() ? query : null, results });
    }

    // Установка навыка из реестра. scope=project требует projectId.
    [HttpPost("api/skills/install")]
    public async Task<IActionResult> Install([FromBody] InstallSkillRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Source) || string.IsNullOrWhiteSpace(req.Skill))
            return BadRequest(new { error = "Нужны source и skill" });

        var scope = ParseScope(req.Scope);
        string? root = null;
        if (scope == SkillScope.Project)
        {
            if (string.IsNullOrWhiteSpace(req.ProjectId))
                return BadRequest(new { error = "Для установки в проект нужен projectId" });
            try { root = GetRoot(req.ProjectId); }
            catch (KeyNotFoundException) { return NotFound(new { error = "Проект не найден" }); }
        }

        var (ok, output) = await cli.InstallAsync(req.Source.Trim(), req.Skill.Trim(), scope, root, ct);
        if (!ok) return StatusCode(500, new { error = "Установка не удалась", output });
        return Ok(new { installed = req.Skill.Trim(), scope = scope.ToString().ToLowerInvariant() });
    }

    // Удаление установленного навыка из области (project требует projectId)
    [HttpDelete("api/skills/installed")]
    public async Task<IActionResult> Uninstall([FromQuery] string skill, [FromQuery] string scope,
        [FromQuery] string? projectId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(skill))
            return BadRequest(new { error = "Нужно имя навыка" });
        var s = ParseScope(scope);
        string? root = null;
        if (s == SkillScope.Project)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return BadRequest(new { error = "Для удаления из проекта нужен projectId" });
            try { root = GetRoot(projectId); }
            catch (KeyNotFoundException) { return NotFound(); }
        }
        var ok = await cli.RemoveAsync(skill.Trim(), s, root, ct);
        return ok ? Ok() : StatusCode(500, new { error = "Не удалось удалить навык" });
    }

    // --- LLM-подбор ---

    // Подбор навыков под контекст: персона / проект / свободный запрос (взаимоисключающие).
    [HttpPost("api/skills/suggest")]
    public async Task<IActionResult> Suggest([FromBody] SuggestSkillsRequest req, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<SkillSuggestion> result;
            if (!string.IsNullOrWhiteSpace(req.PersonaId))
            {
                // Навыки — библиотека владельца (разрез §3.2, B5): право Configure по персоне.
                // id в DTO — [ProjectScope] его не видит, резолв здесь; ключ подборки —
                // арендатор персоны, а не вызывающий (иначе контекст персоны читался бы
                // редактором общего проекта).
                var scope = access.ResolvePersona(UserId, req.PersonaId, ProjectRight.Configure);
                if (scope is null) return PersonaConfigureDenied(req.PersonaId);
                result = await suggest.SuggestForPersonaAsync(scope.TenantOwnerId, req.PersonaId, ct);
            }
            else if (!string.IsNullOrWhiteSpace(req.ProjectId))
                result = await suggest.SuggestForProjectAsync(UserId, req.ProjectId, ct);
            else if (!string.IsNullOrWhiteSpace(req.Query))
                result = await suggest.SuggestForQueryAsync(UserId, req.Query.Trim(), ct);
            else
                return BadRequest(new { error = "Нужен один из: personaId, projectId, query" });
            return Ok(new { candidates = result });
        }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }

    // --- LLM-генерация нового навыка по свободному промпту ---

    // Генерирует навык (SKILL.md) по описанию пользователя. НЕ сохраняет — возвращает кандидата
    // для превью/правки; сохранение — обычным POST api/skills после подтверждения.
    [HttpPost("api/skills/generate")]
    public async Task<IActionResult> Generate([FromBody] GenerateSkillRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt))
            return BadRequest(new { error = "Опишите, что должен делать навык" });

        var g = await generation.GenerateAsync(UserId, req.Prompt.Trim(), ct);
        if (g is null)
            return StatusCode(502, new { error = "Модель не вернула корректный навык — попробуйте ещё раз" });
        return Ok(new { name = g.Name, description = g.Description, body = g.Body });
    }

    // --- Установить навык персоне: глобальная установка + привязка (Skill) ---

    // Навыки — библиотека владельца (разрез §3.2, B5): право Configure по персоне. Проектной
    // персоне общего проекта навык ставит только её владелец (он же владелец проекта):
    // редактору и читателю — 403 «Только владелец проекта», постороннему — 404 (существование
    // чужой персоны не подтверждаем). Ключ записи — арендатор из RequestScope: привязка
    // валидируется и пишется по владельцу персоны (ValidateAsync/UpdateBindings), а не по
    // вызывающему.
    [ProjectScope(From = ScopeSource.Persona, Right = ProjectRight.Configure)]
    [HttpPost("api/personas/{id}/skills")]
    public async Task<IActionResult> InstallForPersona(string id, [FromBody] InstallForPersonaRequest req,
        CancellationToken ct)
    {
        var tenantId = HttpContext.GetRequestScope().TenantOwnerId;
        var persona = personas.Get(id, tenantId);
        if (persona is null) return NotFound(new { error = "Персона не найдена" });
        if (string.IsNullOrWhiteSpace(req.Source) || string.IsNullOrWhiteSpace(req.Skill))
            return BadRequest(new { error = "Нужны source и skill" });

        // Навык персоны живёт в глобальном каталоге (модель Skill-привязки смотрит туда)
        var (ok, output) = await cli.InstallAsync(req.Source.Trim(), req.Skill.Trim(), SkillScope.Global, null, ct);
        if (!ok) return StatusCode(500, new { error = "Установка не удалась", output });

        // Имя цели привязки — как навык видит каталог (frontmatter name); резолвим по установленной папке
        var target = ResolveInstalledSkillName(req.Skill.Trim());
        var binding = new PersonaBinding { Type = PersonaBindingType.Skill, Target = target };
        var err = await bindings.ValidateAsync(tenantId, binding, persona.Bindings);
        if (err is not null)
            // Навык установлен, но привязать не смогли — сообщаем честно
            return Ok(new { installed = target, bound = false, warning = err });

        var list = new List<PersonaBinding>(persona.Bindings ?? []) { binding };
        personas.UpdateBindings(id, tenantId, list);
        return Ok(new { installed = target, bound = true, binding });
    }

    // Отказ по персоне для путей, где id приходит в теле (фильтр [ProjectScope] его не видит):
    // та же грань и та же форма ответа, что у фильтра (ProjectScopeAttribute.cs:95-115) —
    // видимому без права 403 project_scope, невидимому 404; решение о праве идёт ПОСЛЕ
    // проверки видимости.
    private IActionResult PersonaConfigureDenied(string personaId) =>
        access.ResolvePersona(UserId, personaId, ProjectRight.Read) is null
            ? new NotFoundResult()
            : StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Только владелец проекта", code = ProjectScopeAttribute.ErrorCode });

    // Имя навыка в глобальном каталоге по имени папки (slug), с которым его установил CLI.
    private string ResolveInstalledSkillName(string skillSlug)
    {
        foreach (var s in skills.GetGlobalSkills())
        {
            var folder = Path.GetFileName(Path.GetDirectoryName(s.FilePath));
            if (string.Equals(folder, skillSlug, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.Name, skillSlug, StringComparison.OrdinalIgnoreCase))
                return s.Name;
        }
        return skillSlug;
    }

    private static SkillScope ParseScope(string? scope) =>
        string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase)
            ? SkillScope.Project
            : SkillScope.Global;

    // --- Содержимое и ручное создание скиллов/агентов (без изменений) ---

    [HttpGet("api/skills/{skillName}")]
    public IActionResult GetSkill(string skillName)
    {
        var content = skills.GetSkillContent(skillName);
        if (content is null) return NotFound();
        return Ok(new { content });
    }

    [HttpPut("api/skills/{skillName}")]
    public IActionResult SaveSkill(string skillName, [FromBody] SkillContentRequest req)
    {
        try { skills.SaveGlobalSkill(skillName, req.Content); return Ok(); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
    }

    [HttpPost("api/skills")]
    public IActionResult CreateSkill([FromBody] CreateSkillRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Имя скилла не может быть пустым" });
        try
        {
            skills.SaveGlobalSkill(req.Name.Trim(), req.Content);
            return Ok(new { name = req.Name.Trim() });
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
    }

    [HttpGet("api/projects/{projectId}/agents/{agentName}")]
    public async Task<IActionResult> GetAgent(string projectId, string agentName, CancellationToken ct)
    {
        try
        {
            var content = await skills.GetAgentContentAsync(projectFiles, ScopedProject(projectId, ProjectRight.Read), agentName, ct);
            if (content is null) return NotFound();
            return Ok(new { content });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPut("api/projects/{projectId}/agents/{agentName}")]
    public async Task<IActionResult> SaveAgent(string projectId, string agentName, [FromBody] SkillContentRequest req, CancellationToken ct)
    {
        try
        {
            await skills.SaveProjectAgentAsync(projectFiles, ScopedProject(projectId, ProjectRight.Write), agentName, req.Content, ct);
            return Ok();
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (LocalProjectException) { throw; }
        catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
    }

    [HttpPost("api/projects/{projectId}/agents")]
    public async Task<IActionResult> CreateAgent(string projectId, [FromBody] CreateSkillRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Имя агента не может быть пустым" });
        try
        {
            await skills.SaveProjectAgentAsync(projectFiles, ScopedProject(projectId, ProjectRight.Write), req.Name.Trim(), req.Content, ct);
            return Ok(new { name = req.Name.Trim() });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (LocalProjectException) { throw; }
        catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
    }
}

public record InstallSkillRequest(string Source, string Skill, string? Scope, string? ProjectId);
public record InstallForPersonaRequest(string Source, string Skill);
public record SuggestSkillsRequest(string? PersonaId, string? ProjectId, string? Query);
public record GenerateSkillRequest(string Prompt);
