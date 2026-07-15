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

    // Дефолтный корень — ~/.claude/projects/ (сессии родного Claude)
    public static readonly string DefaultRoot = Path.GetFullPath(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects"));
    // Дополнительные корни — пути транскриптов профилей сторонних провайдеров
    // (GLM/DeepSeek: /data/claude-profiles/{key}/projects/). Регистрируются из Program.cs.
    private static readonly List<string> _extraRoots = new();

    public static void AddAllowedRoot(string root)
    {
        var full = Path.GetFullPath(root);
        if (Directory.Exists(full) && !_extraRoots.Contains(full, StringComparer.OrdinalIgnoreCase))
            _extraRoots.Add(full);
    }

    public static bool IsPathAllowed(string path) =>
        path.StartsWith(DefaultRoot, StringComparison.OrdinalIgnoreCase) ||
        _extraRoots.Any(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase));

    // Все корни транскриптов (родной + профили провайдеров) — для поиска папки
    // сабагентов сессии (SubagentStreamWatcher)
    public static IReadOnlyList<string> AllowedRoots => [DefaultRoot, .. _extraRoots];

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

        return new WorkflowAgentDto(agentId, prompt, summary, tools, files, isDone,
            ReadAgentTypeFromMeta(filePath));
    }

    // agent-*.meta.json лежит рядом с jsonl и несёт agentType вызова — по нему фронт
    // узнаёт персону-консультанта (handle) и рисует её карточку вместо безликой строки
    private static string? ReadAgentTypeFromMeta(string agentFilePath)
    {
        var metaPath = Path.ChangeExtension(agentFilePath, ".meta.json");
        if (!File.Exists(metaPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            if (doc.RootElement.TryGetProperty("agentType", out var t) &&
                t.ValueKind == JsonValueKind.String)
            {
                var value = t.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (Exception ex)
        {
            Log.LogDebug(ex, "Не удалось прочитать meta-файл агента: {Path}", metaPath);
        }
        return null;
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

    // Полный поток агента из транскрипта: упорядоченные блоки thinking/text/tool_use.
    // Первая строка (промпт) пропускается — карточка показывает её отдельно как «вопрос».
    // Потолок результата инструмента в таймлайне — защита payload от гигантских выводов
    private const int TimelineResultMaxChars = 30_000;

    public static IReadOnlyList<WorkflowAgentBlockDto> ParseAgentTimeline(string filePath)
    {
        var blocks = new List<WorkflowAgentBlockDto>();
        // Позиция tool-блока по id — результат из следующей user-строки дописывается в него
        var toolIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
        bool isFirst = true;
        try
        {
            foreach (var line in File.ReadLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (isFirst) { isFirst = false; continue; }
                try { ProcessTimelineLine(line, blocks, toolIndexById); }
                catch (Exception ex)
                {
                    Log.LogDebug(ex, "Битая строка таймлайна в {Path}", filePath);
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogDebug(ex, "Не удалось прочитать agent-файл для таймлайна: {Path}", filePath);
        }
        return blocks;
    }

    private static void ProcessTimelineLine(string jsonLine, List<WorkflowAgentBlockDto> blocks,
        Dictionary<string, int> toolIndexById)
    {
        using var doc = JsonDocument.Parse(jsonLine);
        var root = doc.RootElement;
        if (!root.TryGetProperty("message", out var message)) return;
        if (!message.TryGetProperty("role", out var role)) return;
        if (!message.TryGetProperty("content", out var content)) return;

        // user-строки несут результаты инструментов — дописываем в свои tool-блоки
        if (role.GetString() == "user")
        {
            if (content.ValueKind != JsonValueKind.Array) return;
            foreach (var block in content.EnumerateArray())
                AttachToolResult(block, blocks, toolIndexById);
            return;
        }
        if (role.GetString() != "assistant") return;

        if (content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                blocks.Add(new WorkflowAgentBlockDto("text", text.Trim()));
            return;
        }
        if (content.ValueKind != JsonValueKind.Array) return;

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var typeEl)) continue;
            switch (typeEl.GetString())
            {
                case "text":
                    if (block.TryGetProperty("text", out var textEl)
                        && textEl.GetString() is { } t && !string.IsNullOrWhiteSpace(t))
                        blocks.Add(new WorkflowAgentBlockDto("text", t.Trim()));
                    break;
                case "thinking":
                    if (block.TryGetProperty("thinking", out var thEl)
                        && thEl.GetString() is { } th && !string.IsNullOrWhiteSpace(th))
                        blocks.Add(new WorkflowAgentBlockDto("thinking", th.Trim()));
                    break;
                case "tool_use":
                    if (block.TryGetProperty("name", out var nameEl)
                        && nameEl.GetString() is { } name && name.Length > 0)
                    {
                        // Агент со schema отдаёт итог вызовом StructuredOutput — полезное
                        // содержимое в его input. Отдельный kind: фронт рендерит сворачиваемым
                        // блоком (по умолчанию свёрнут), а не голой строкой инструмента
                        if (name == "StructuredOutput" && block.TryGetProperty("input", out var so)
                            && so.ValueKind == JsonValueKind.Object)
                        {
                            blocks.Add(new WorkflowAgentBlockDto("structured", PrettyJson(so)));
                            break;
                        }
                        // Полный вызов: id + input — фронт рендерит тем же ToolUseView, что и чат
                        var toolId = block.TryGetProperty("id", out var tid) ? tid.GetString() : null;
                        object? input = block.TryGetProperty("input", out var inp)
                            ? JsonSerializer.Deserialize<object>(inp.GetRawText()) : null;
                        if (toolId is not null) toolIndexById[toolId] = blocks.Count;
                        blocks.Add(new WorkflowAgentBlockDto("tool_use",
                            ToolName: name, ToolId: toolId, ToolInput: input));
                    }
                    break;
            }
        }
    }

    // tool_result из user-строки → результат в свой tool-блок (матч по tool_use_id)
    private static void AttachToolResult(JsonElement block, List<WorkflowAgentBlockDto> blocks,
        Dictionary<string, int> toolIndexById)
    {
        if (!block.TryGetProperty("type", out var bt) || bt.GetString() != "tool_result") return;
        if (!block.TryGetProperty("tool_use_id", out var tuid)
            || tuid.GetString() is not { } toolUseId
            || !toolIndexById.TryGetValue(toolUseId, out var idx)) return;

        var isError = block.TryGetProperty("is_error", out var ie)
            && ie.ValueKind == JsonValueKind.True;

        var result = "";
        if (block.TryGetProperty("content", out var c))
        {
            if (c.ValueKind == JsonValueKind.String)
                result = c.GetString() ?? "";
            else if (c.ValueKind == JsonValueKind.Array)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var cb in c.EnumerateArray())
                    if (cb.TryGetProperty("text", out var tx))
                        sb.AppendLine(tx.GetString());
                result = sb.ToString().TrimEnd();
            }
        }
        if (result.Length > TimelineResultMaxChars)
            result = result[..TimelineResultMaxChars] + "\n…[вывод обрезан]";

        blocks[idx] = blocks[idx] with { ToolResult = result, IsError = isError };
    }

    // Кириллица и спецсимволы без \uXXXX-эскейпов — блок показывается человеку
    private static string PrettyJson(JsonElement el) =>
        JsonSerializer.Serialize(el, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

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
