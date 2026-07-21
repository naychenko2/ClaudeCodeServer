using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// «Задачи из чата» (флаг chat-extract-tasks): по явному запросу собирает транскрипт
// сессии, one-shot вызовом Claude извлекает action items и ВОЗВРАЩАЕТ кандидатов
// (не создаёт молча — фронт показывает диалог подтверждения). Создание задач — уже
// на фронте через обычный tasks-API по выбору пользователя.
public sealed class ChatTaskExtractionService(
    SessionManager sessions, ProjectManager projects,
    Llm.ICheapTextRunner cheap, IConfiguration config,
    ILogger<ChatTaskExtractionService> log)
{
    private const int TranscriptBudget = 30_000;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public async Task<ExtractTasksResult> ExtractAsync(string userId, string sessionId, CancellationToken ct)
    {
        var session = sessions.GetById(sessionId)
            ?? throw new KeyNotFoundException("Сессия не найдена");
        var ownerId = session.ProjectId is not null
            ? projects.GetById(session.ProjectId)?.OwnerId
            : session.OwnerId;
        if (ownerId is null || ownerId != userId)
            throw new UnauthorizedAccessException("Сессия принадлежит другому пользователю");

        var history = await sessions.GetHistoryAsync(sessionId);
        var transcript = SessionSummaryService.BuildTranscript(history, TranscriptBudget);
        if (string.IsNullOrWhiteSpace(transcript))
            throw new InvalidOperationException("В сессии ещё нет сообщений");

        var raw = await cheap.RunAsync(
            Llm.LocalActionCatalog.ChatExtractTasks,
            BuildPrompt(transcript),
            config["Tasks:AiModel"] ?? config["Notes:AiModel"] ?? "haiku",
            ct: ct);

        var result = ParseTasks(raw);
        log.LogInformation(
            "extract-tasks: сессия {SessionId}, история {HistCount} сообщ., транскрипт {Len} симв., распознано {Count} задач",
            sessionId, history.Count, transcript.Length, result.Count);
        // Диагностика «всегда пусто»: показываем сырой ответ модели, когда ничего не распозналось,
        // чтобы отличить реальный [] от сбойного парсинга/преамбулы haiku.
        if (result.Count == 0)
            log.LogInformation("extract-tasks: 0 задач; сырой ответ модели (усечён): {Raw}",
                raw.Length > 800 ? raw[..800] + "…" : raw);

        return new ExtractTasksResult(session.ProjectId, result);
    }

    internal static string BuildPrompt(string transcript)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ниже — транскрипт беседы пользователя с ассистентом. Выпиши практические дела, " +
                      "которые пользователю имеет смысл занести в список задач по итогам разговора.");
        sb.AppendLine("Включай:");
        sb.AppendLine("- явные договорённости, TODO и обещанные доработки;");
        sb.AppendLine("- конкретные действия и следующие шаги, которые советует ассистент;");
        sb.AppendLine("- то, что пользователь сам собирается сделать.");
        sb.AppendLine("НЕ включай: чисто развлекательные/тестовые реплики (ASCII-арт, «нарисуй…»), " +
                      "общие рассуждения без действия и уже завершённое.");
        sb.AppendLine("Формулируй каждую задачу коротко, в повелительном наклонении, по-русски.");
        sb.AppendLine();
        sb.AppendLine("Пример: если обсуждали, как почистить бассейн, подходящие задачи — " +
                      "«Убрать листья сачком», «Почистить стенки щёткой», «Проверить pH и уровень хлора».");
        sb.AppendLine();
        sb.AppendLine("Для каждой задачи верни объект:");
        sb.AppendLine("  title — краткая формулировка в повелительном наклонении (обязательно);");
        sb.AppendLine("  due — дата YYYY-MM-DD, если в тексте явно назван срок, иначе null;");
        sb.AppendLine("  priority — one of low|medium|high|urgent, если очевидно, иначе null.");
        sb.AppendLine("Ответь ТОЛЬКО JSON-массивом таких объектов, без пояснений. Если подходящих дел нет — [].");
        sb.AppendLine();
        sb.AppendLine("Транскрипт:");
        sb.AppendLine(transcript);
        return sb.ToString();
    }

    // JSON-массив из ответа модели; невалидные записи отбрасываются
    internal static IReadOnlyList<ExtractedTask> ParseTasks(string raw)
    {
        var json = ExtractJsonArray(raw);
        if (json is null) return [];

        List<ExtractedTaskRaw>? parsed;
        try { parsed = JsonSerializer.Deserialize<List<ExtractedTaskRaw>>(json, JsonOpts); }
        catch (JsonException) { return []; }
        if (parsed is null) return [];

        var result = new List<ExtractedTask>();
        foreach (var t in parsed)
        {
            var title = t.Title?.Trim();
            if (string.IsNullOrWhiteSpace(title)) continue;

            var due = IsValidDate(t.Due) ? t.Due : null;
            var priority = NormalizePriority(t.Priority);
            result.Add(new ExtractedTask(title, due, priority));
        }
        return result.Take(20).ToList();
    }

    // Вырезает первый сбалансированный JSON-массив из ответа модели: устойчиво к
    // markdown-fence (```json), русской преамбуле/послесловию haiku и скобкам внутри строк
    // (в отличие от прежней жадной пары IndexOf('[')…LastIndexOf(']')).
    internal static string? ExtractJsonArray(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('[');
        if (start < 0) return null;

        var depth = 0;
        var inStr = false;
        var esc = false;
        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') inStr = true;
            else if (c == '[') depth++;
            else if (c == ']' && --depth == 0) return raw[start..(i + 1)];
        }
        return null;
    }

    private static bool IsValidDate(string? s) =>
        !string.IsNullOrWhiteSpace(s) && DateOnly.TryParseExact(s, "yyyy-MM-dd", out _);

    private static string? NormalizePriority(string? p)
    {
        var v = p?.Trim().ToLowerInvariant();
        return v is "low" or "medium" or "high" or "urgent" ? v : null;
    }

    private sealed record ExtractedTaskRaw(string? Title, string? Due, string? Priority);
}

// Кандидат в задачу, извлечённый из чата (задача ещё не создана)
public record ExtractedTask(string Title, string? Due, string? Priority);

// Результат извлечения: контекст (проект сессии) + список кандидатов
public record ExtractTasksResult(string? ProjectId, IReadOnlyList<ExtractedTask> Tasks);
