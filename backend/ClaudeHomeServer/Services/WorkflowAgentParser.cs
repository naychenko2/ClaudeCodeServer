using System.Text.Json;
using ClaudeHomeServer.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Services;

// Общий парсер agent-*.jsonl файлов workflow-транскриптов.
// Используется и WorkflowWatcher (реалтайм), и WorkflowController (REST).
public static class WorkflowAgentParser
{
    // Класс статический, DI-логгера нет — ставится один раз из Program.cs.
    // Битые строки — норма (файл дописывается на лету), поэтому уровень Debug.
    public static ILogger Log { get; set; } = NullLogger.Instance;

    public static readonly string AllowedRoot = Path.GetFullPath(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects"));

    public static bool IsPathAllowed(string path) =>
        path.StartsWith(AllowedRoot, StringComparison.OrdinalIgnoreCase);

    // Читает все agent-*.jsonl из wfPath и возвращает список агентов.
    public static IReadOnlyList<WorkflowAgentDto> ParseDirectory(string wfPath)
    {
        if (!Directory.Exists(wfPath)) return [];
        // journal.jsonl — источник истины: агент done если есть {"type":"result","agentId":"..."}
        var doneAgents = ReadDoneAgentsFromJournal(wfPath);
        var agents = new List<WorkflowAgentDto>();
        foreach (var file in Directory.GetFiles(wfPath, "agent-*.jsonl").OrderBy(f => f))
        {
            var parsed = ParseAgentFile(file, doneAgents);
            if (parsed is not null) agents.Add(parsed);
        }
        return agents;
    }

    // Читает journal.jsonl и возвращает множество agentId с типом "result"
    private static HashSet<string> ReadDoneAgentsFromJournal(string wfPath)
    {
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var journalPath = Path.Combine(wfPath, "journal.jsonl");
        if (!File.Exists(journalPath)) return done;
        try
        {
            foreach (var line in File.ReadLines(journalPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("type", out var t) && t.GetString() == "result" &&
                        root.TryGetProperty("agentId", out var aid))
                        done.Add(aid.GetString() ?? "");
                }
                catch (Exception ex)
                {
                    Log.LogDebug(ex, "Битая строка в journal.jsonl: {Path}", journalPath);
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogDebug(ex, "Не удалось прочитать journal.jsonl: {Path}", journalPath);
        }
        return done;
    }

    public static WorkflowAgentDto? ParseAgentFile(string filePath, HashSet<string>? doneAgents = null)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var agentId = fileName.Length > 6 ? fileName[6..] : fileName;

        string? prompt = null;
        string? summary = null;
        // journal.jsonl — приоритетный источник истины о завершении
        bool isDone = doneAgents?.Contains(agentId) ?? false;
        var toolCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var filesSet = new LinkedList<string>();
        var filesDedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool isFirst = true;
        try
        {
            foreach (var line in File.ReadLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (isFirst)
                {
                    isFirst = false;
                    try { prompt = ExtractText(line); }
                    catch (Exception ex)
                    {
                        Log.LogDebug(ex, "Битая первая строка (промпт) в {Path}", filePath);
                        return null;
                    }
                    continue;
                }
                try { ProcessLine(line, ref summary, ref isDone, toolCounts, filesSet, filesDedup); }
                catch (Exception ex)
                {
                    Log.LogDebug(ex, "Битая строка в {Path}", filePath);
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogDebug(ex, "Не удалось прочитать agent-файл: {Path}", filePath);
            return null;
        }

        if (prompt is null) return null;

        IReadOnlyList<WorkflowToolDto>? tools = null;
        if (toolCounts.Count > 0)
            tools = toolCounts.OrderByDescending(kv => kv.Value)
                .Select(kv => new WorkflowToolDto(kv.Key, kv.Value)).ToArray();

        IReadOnlyList<string>? files = filesSet.Count > 0 ? filesSet.Take(10).ToArray() : null;

        return new WorkflowAgentDto(agentId, prompt, summary, tools, files, isDone);
    }

    private static void ProcessLine(string jsonLine, ref string? summary, ref bool isDone,
        Dictionary<string, int> toolCounts, LinkedList<string> filesSet, HashSet<string> filesDedup)
    {
        using var doc = JsonDocument.Parse(jsonLine);
        var root = doc.RootElement;

        // Старый формат: явное событие result
        if (root.TryGetProperty("type", out var rootTypeEl) && rootTypeEl.GetString() == "result")
        {
            isDone = true;
            return;
        }

        if (!root.TryGetProperty("message", out var message)) return;

        // Новый формат (agent-*.jsonl): assistant с stop_reason == "end_turn"
        if (message.TryGetProperty("stop_reason", out var stopReasonEl) &&
            stopReasonEl.GetString() == "end_turn")
        {
            isDone = true;
        }

        if (!message.TryGetProperty("content", out var content)) return;

        var isAssistant = message.TryGetProperty("role", out var role) && role.GetString() == "assistant";

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

    private static string ExtractText(string jsonLine)
    {
        using var doc = JsonDocument.Parse(jsonLine);
        var root = doc.RootElement;
        if (!root.TryGetProperty("message", out var message)) return string.Empty;
        if (!message.TryGetProperty("content", out var content)) return string.Empty;
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
                if (item.TryGetProperty("type", out var type) && type.GetString() == "text"
                    && item.TryGetProperty("text", out var text))
                    return text.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen];
}
