using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/workflow-agents")]
public class WorkflowController : ControllerBase
{
    [HttpGet]
    public IActionResult GetAgents([FromQuery] string transcriptDir)
    {
        if (string.IsNullOrWhiteSpace(transcriptDir))
            return BadRequest(new { error = "transcriptDir обязателен" });

        var (wfPath, error) = ResolveWorkflowDir(transcriptDir);
        if (error is not null) return error;
        if (wfPath is null) return Ok(new { agents = Array.Empty<object>() });

        var agents = WorkflowAgentParser.ParseDirectory(wfPath);
        return Ok(new { agents });
    }

    // Полный поток одного агента (текст/thinking/инструменты) — лениво при раскрытии карточки
    [HttpGet("timeline")]
    public IActionResult GetTimeline([FromQuery] string transcriptDir, [FromQuery] string agentId)
    {
        if (string.IsNullOrWhiteSpace(transcriptDir) || string.IsNullOrWhiteSpace(agentId))
            return BadRequest(new { error = "transcriptDir и agentId обязательны" });
        // agentId — компонент имени файла, не даём выйти из папки
        if (agentId.Contains('/') || agentId.Contains('\\') || agentId.Contains(".."))
            return BadRequest(new { error = "Недопустимый agentId" });

        var (wfPath, error) = ResolveWorkflowDir(transcriptDir);
        if (error is not null) return error;
        if (wfPath is null) return NotFound();

        var agentFile = Path.Combine(wfPath, $"agent-{agentId}.jsonl");
        if (!System.IO.File.Exists(agentFile)) return NotFound();

        var blocks = WorkflowAgentParser.ParseAgentTimeline(agentFile);
        return Ok(new { blocks });
    }

    // Transcript dir может указывать прямо на папку wf_* или на родительскую сессию.
    // null wfPath (без error) — папка workflow ещё/уже не существует.
    private (string? WfPath, IActionResult? Error) ResolveWorkflowDir(string transcriptDir)
    {
        var fullPath = Path.GetFullPath(transcriptDir);
        if (!WorkflowAgentParser.IsPathAllowed(fullPath))
            return (null, Forbid());

        if (Directory.Exists(fullPath) && Directory.GetFiles(fullPath, "agent-*.jsonl").Length > 0)
            return (fullPath, null);

        var workflowsDir = Path.Combine(fullPath, "subagents", "workflows");
        if (!Directory.Exists(workflowsDir))
            return (null, null);

        var wfDir = Directory.GetDirectories(workflowsDir, "wf_*")
            .Select(d => new DirectoryInfo(d))
            .OrderByDescending(d => d.CreationTimeUtc)
            .FirstOrDefault();

        return (wfDir?.FullName, null);
    }
}
