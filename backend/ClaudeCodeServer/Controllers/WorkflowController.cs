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

            var parsed = ParseAgentFile(jsonlFile);
            if (parsed is null) continue;

            agents.Add(parsed);
        }

        return Ok(new { agents });
    }

    // Читает весь jsonl-файл агента и возвращает расширенный объект
    private static object? ParseAgentFile(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var agentId = fileName.Length > 6 ? fileName[6..] : fileName;

        string? prompt = null;
        string? summary = null;
        var toolCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var filesSet = new LinkedList<string>(); // сохраняем порядок появления
        var filesDedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool isFirst = true;
        foreach (var line in System.IO.File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (isFirst)
            {
                isFirst = false;
                try { prompt = ExtractText(line); }
                catch { /* первая строка битая — пропускаем файл */ return null; }
                continue;
            }

            try
            {
                ProcessLine(line, ref summary, toolCounts, filesSet, filesDedup);
            }
            catch
            {
                // Битые строки пропускаем, не роняем весь файл
            }
        }

        if (prompt is null) return null;

        // Строим инструменты: list отсортированный по count desc, null если пусто
        object? tools = null;
        if (toolCounts.Count > 0)
        {
            tools = toolCounts
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new { name = kv.Key, count = kv.Value })
                .ToArray();
        }

        // Файлы: максимум 10, null если пусто
        object? files = null;
        if (filesSet.Count > 0)
        {
            files = filesSet.Take(10).ToArray();
        }

        return new { id = agentId, prompt, summary, tools, files };
    }

    // Обрабатывает одну строку (не первую): обновляет summary, tools, files
    private static void ProcessLine(
        string jsonLine,
        ref string? summary,
        Dictionary<string, int> toolCounts,
        LinkedList<string> filesSet,
        HashSet<string> filesDedup)
    {
        using var doc = JsonDocument.Parse(jsonLine);
        var root = doc.RootElement;

        if (!root.TryGetProperty("message", out var message)) return;
        if (!message.TryGetProperty("content", out var content)) return;

        // role нужен для определения: assistant → summary-кандидат
        var isAssistant = message.TryGetProperty("role", out var role) &&
                          role.GetString() == "assistant";

        if (content.ValueKind == JsonValueKind.String)
        {
            if (isAssistant)
            {
                var text = content.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    summary = Truncate(text.Trim(), 400);
            }
            return;
        }

        if (content.ValueKind != JsonValueKind.Array) return;

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var typeEl)) continue;
            var blockType = typeEl.GetString();

            if (blockType == "text" && isAssistant)
            {
                if (block.TryGetProperty("text", out var textEl))
                {
                    var text = textEl.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        summary = Truncate(text.Trim(), 400);
                }
            }
            else if (blockType == "tool_use")
            {
                if (!block.TryGetProperty("name", out var nameEl)) continue;
                var toolName = nameEl.GetString();
                if (string.IsNullOrEmpty(toolName)) continue;

                toolCounts[toolName] = toolCounts.GetValueOrDefault(toolName, 0) + 1;

                // Извлекаем файлы из Read и Glob
                if ((toolName == "Read" || toolName == "Glob") &&
                    block.TryGetProperty("input", out var input))
                {
                    string? rawPath = null;

                    if (toolName == "Read" && input.TryGetProperty("file_path", out var fp))
                        rawPath = fp.GetString();
                    else if (toolName == "Glob" && input.TryGetProperty("pattern", out var pt))
                        rawPath = pt.GetString();

                    if (!string.IsNullOrEmpty(rawPath))
                    {
                        var name = Path.GetFileName(rawPath);
                        if (!string.IsNullOrEmpty(name) && filesDedup.Add(name))
                            filesSet.AddLast(name);
                    }
                }
            }
        }
    }

    // Извлекает текст из первой строки (prompt)
    private static string ExtractText(string jsonLine)
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

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen];
}
