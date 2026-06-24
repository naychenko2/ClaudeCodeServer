using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace ClaudeCodeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/workflow-agents")]
public class WorkflowController : ControllerBase
{
    // Разрешённый корень: ~/.claude/projects/
    private static readonly string AllowedRoot = Path.GetFullPath(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects"));

    [HttpGet]
    public IActionResult GetAgents([FromQuery] string transcriptDir)
    {
        if (string.IsNullOrWhiteSpace(transcriptDir))
            return BadRequest(new { error = "transcriptDir обязателен" });

        // Нормализуем и проверяем путь — защита от path traversal
        var fullPath = Path.GetFullPath(transcriptDir);
        if (!fullPath.StartsWith(AllowedRoot, StringComparison.OrdinalIgnoreCase))
            return Forbid();

        // Transcript dir может указывать прямо на папку wf_* или на родительскую сессию.
        // Пробуем найти agent-*.jsonl в самом fullPath, иначе ищем subagents/workflows/wf_*/
        string wfPath;
        if (Directory.GetFiles(fullPath, "agent-*.jsonl").Length > 0)
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

        var agents = new List<object>();

        foreach (var jsonlFile in Directory.GetFiles(wfPath, "agent-*.jsonl")
                                           .OrderBy(f => f))
        {
            // Имя файла: agent-<uuid>.jsonl → id = uuid
            var fileName = Path.GetFileNameWithoutExtension(jsonlFile); // agent-<uuid>
            var agentId = fileName.Length > 6 ? fileName[6..] : fileName; // убираем "agent-"

            var firstLine = System.IO.File.ReadLines(jsonlFile).FirstOrDefault();
            if (firstLine is null) continue;

            string prompt;
            try
            {
                prompt = ExtractPrompt(firstLine);
            }
            catch
            {
                continue; // пропускаем битые строки
            }

            agents.Add(new { id = agentId, prompt });
        }

        return Ok(new { agents });
    }

    // Извлекает текст из message.content (строка или [{type:"text", text:"..."}])
    private static string ExtractPrompt(string jsonLine)
    {
        using var doc = JsonDocument.Parse(jsonLine);
        var root = doc.RootElement;

        if (!root.TryGetProperty("message", out var message))
            return string.Empty;

        if (!message.TryGetProperty("content", out var content))
            return string.Empty;

        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;

        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var type) &&
                    type.GetString() == "text" &&
                    item.TryGetProperty("text", out var text))
                {
                    return text.GetString() ?? string.Empty;
                }
            }
        }

        return string.Empty;
    }
}
