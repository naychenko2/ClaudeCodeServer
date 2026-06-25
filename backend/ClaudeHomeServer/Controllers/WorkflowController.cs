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

        var fullPath = Path.GetFullPath(transcriptDir);
        if (!WorkflowAgentParser.IsPathAllowed(fullPath))
            return Forbid();

        // Transcript dir может указывать прямо на папку wf_* или на родительскую сессию.
        string wfPath;
        if (Directory.Exists(fullPath) && Directory.GetFiles(fullPath, "agent-*.jsonl").Length > 0)
        {
            wfPath = fullPath;
        }
        else
        {
            var workflowsDir = Path.Combine(fullPath, "subagents", "workflows");
            if (!Directory.Exists(workflowsDir))
                return Ok(new { agents = Array.Empty<object>() });

            var wfDir = Directory.GetDirectories(workflowsDir, "wf_*")
                .Select(d => new DirectoryInfo(d))
                .OrderByDescending(d => d.CreationTimeUtc)
                .FirstOrDefault();

            if (wfDir is null)
                return Ok(new { agents = Array.Empty<object>() });

            wfPath = wfDir.FullName;
        }

        var agents = WorkflowAgentParser.ParseDirectory(wfPath);
        return Ok(new { agents });
    }
}
