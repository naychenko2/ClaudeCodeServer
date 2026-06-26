using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
public class SkillsController(SkillsService skills, ProjectManager projects) : ControllerBase
{
    private string GetRoot(string projectId) =>
        (projects.GetById(projectId) ?? throw new KeyNotFoundException($"Проект не найден: {projectId}")).RootPath;

    // Список всех скиллов (глобальные) + агентов проекта
    [HttpGet("api/projects/{projectId}/skills")]
    public IActionResult List(string projectId)
    {
        try
        {
            var root = GetRoot(projectId);
            return Ok(new
            {
                skills = skills.GetGlobalSkills(),
                agents = skills.GetProjectAgents(root),
            });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Содержимое глобального скилла
    [HttpGet("api/skills/{skillName}")]
    public IActionResult GetSkill(string skillName)
    {
        var content = skills.GetSkillContent(skillName);
        if (content is null) return NotFound();
        return Ok(new { content });
    }

    // Сохранить глобальный скилл (создать или обновить)
    [HttpPut("api/skills/{skillName}")]
    public IActionResult SaveSkill(string skillName, [FromBody] SkillContentRequest req)
    {
        try { skills.SaveGlobalSkill(skillName, req.Content); return Ok(); }
        catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
    }

    // Создать новый глобальный скилл
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

    // Содержимое агента проекта
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

    // Сохранить агента проекта (создать или обновить)
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

    // Создать нового агента проекта
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
