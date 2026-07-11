using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
public class SkillsController(
    SkillsService skills,
    SkillsCliService cli,
    SkillSuggestService suggest,
    PersonaManager personas,
    PersonaBindingsService bindings,
    ProjectManager projects) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    private string GetRoot(string projectId) =>
        (projects.GetById(projectId) ?? throw new KeyNotFoundException($"Проект не найден: {projectId}")).RootPath;

    // Список скиллов: глобальные + проектные + агенты проекта
    [HttpGet("api/projects/{projectId}/skills")]
    public IActionResult List(string projectId)
    {
        try
        {
            var root = GetRoot(projectId);
            return Ok(new
            {
                skills = skills.GetGlobalSkills(),
                projectSkills = skills.GetProjectSkills(root),
                agents = skills.GetProjectAgents(root),
            });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Список глобальных скиллов (без привязки к проекту) — для чатов вне проекта
    [HttpGet("api/skills")]
    public IActionResult ListGlobal() => Ok(skills.GetGlobalSkills());

    // --- Реестр: поиск и установка (обёртка npx skills) ---

    // Поиск навыков по реестру skills.sh. owner — опциональное сужение по GitHub-владельцу.
    [HttpGet("api/skills/find")]
    public async Task<IActionResult> Find([FromQuery] string q, [FromQuery] string? owner, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            return BadRequest(new { error = "Запрос должен быть не короче 2 символов" });
        var results = await cli.FindAsync(q.Trim(), owner, ct);
        return Ok(results);
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
                result = await suggest.SuggestForPersonaAsync(UserId, req.PersonaId, ct);
            else if (!string.IsNullOrWhiteSpace(req.ProjectId))
                result = await suggest.SuggestForProjectAsync(req.ProjectId, ct);
            else if (!string.IsNullOrWhiteSpace(req.Query))
                result = await suggest.SuggestForQueryAsync(req.Query.Trim(), ct);
            else
                return BadRequest(new { error = "Нужен один из: personaId, projectId, query" });
            return Ok(new { candidates = result });
        }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }

    // --- Установить навык персоне: глобальная установка + привязка (Skill) ---

    [HttpPost("api/personas/{id}/skills")]
    public async Task<IActionResult> InstallForPersona(string id, [FromBody] InstallForPersonaRequest req,
        CancellationToken ct)
    {
        var persona = personas.Get(id, UserId);
        if (persona is null) return NotFound(new { error = "Персона не найдена" });
        if (string.IsNullOrWhiteSpace(req.Source) || string.IsNullOrWhiteSpace(req.Skill))
            return BadRequest(new { error = "Нужны source и skill" });

        // Навык персоны живёт в глобальном каталоге (модель Skill-привязки смотрит туда)
        var (ok, output) = await cli.InstallAsync(req.Source.Trim(), req.Skill.Trim(), SkillScope.Global, null, ct);
        if (!ok) return StatusCode(500, new { error = "Установка не удалась", output });

        // Имя цели привязки — как навык видит каталог (frontmatter name); резолвим по установленной папке
        var target = ResolveInstalledSkillName(req.Skill.Trim());
        var binding = new PersonaBinding { Type = PersonaBindingType.Skill, Target = target };
        var err = await bindings.ValidateAsync(UserId, binding, persona.Bindings);
        if (err is not null)
            // Навык установлен, но привязать не смогли — сообщаем честно
            return Ok(new { installed = target, bound = false, warning = err });

        var list = new List<PersonaBinding>(persona.Bindings ?? []) { binding };
        personas.UpdateBindings(id, UserId, list);
        return Ok(new { installed = target, bound = true, binding });
    }

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
        catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
    }

    [HttpGet("api/projects/{projectId}/agents/{agentName}")]
    public IActionResult GetAgent(string projectId, string agentName)
    {
        try
        {
            var root = GetRoot(projectId);
            var content = skills.GetAgentContent(root, agentName);
            if (content is null) return NotFound();
            return Ok(new { content });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPut("api/projects/{projectId}/agents/{agentName}")]
    public IActionResult SaveAgent(string projectId, string agentName, [FromBody] SkillContentRequest req)
    {
        try
        {
            var root = GetRoot(projectId);
            skills.SaveProjectAgent(root, agentName, req.Content);
            return Ok();
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
    }

    [HttpPost("api/projects/{projectId}/agents")]
    public IActionResult CreateAgent(string projectId, [FromBody] CreateSkillRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Имя агента не может быть пустым" });
        try
        {
            var root = GetRoot(projectId);
            skills.SaveProjectAgent(root, req.Name.Trim(), req.Content);
            return Ok(new { name = req.Name.Trim() });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
    }
}

public record SkillContentRequest(string Content);
public record CreateSkillRequest(string Name, string Content);
public record InstallSkillRequest(string Source, string Skill, string? Scope, string? ProjectId);
public record InstallForPersonaRequest(string Source, string Skill);
public record SuggestSkillsRequest(string? PersonaId, string? ProjectId, string? Query);
