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
    Llm.OneShotClaudeRunner runner, FeatureFlagService flags, IConfiguration config,
    ILogger<ChatTaskExtractionService> log)
{
    private const int TranscriptBudget = 30_000;
    private static readonly TimeSpan LlmTimeout = TimeSpan.FromSeconds(120);
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
        if (!flags.IsEnabled(userId, FeatureFlagKeys.ChatExtractTasks))
            throw new InvalidOperationException("Функция «Задачи из чата» выключена");

        var history = await sessions.GetHistoryAsync(sessionId);
        var transcript = SessionSummaryService.BuildTranscript(history, TranscriptBudget);
        if (string.IsNullOrWhiteSpace(transcript))
            throw new InvalidOperationException("В сессии ещё нет сообщений");

        var raw = await runner.RunAsync(
            BuildPrompt(transcript),
            runner.NormalizeModel(config["Tasks:AiModel"] ?? config["Notes:AiModel"] ?? "haiku"),
            LlmTimeout, ct);

        var result = ParseTasks(raw);
        log.LogInformation(
            "extract-tasks: сессия {SessionId}, история {HistCount} сообщ., транскрипт {Len} симв., распознано {Count} задач",
            sessionId, history.Count, transcript.Length, result.Count);

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
    private static IReadOnlyList<ExtractedTask> ParseTasks(string raw)
    {
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start < 0 || end <= start) return [];

        List<ExtractedTaskRaw>? parsed;
        try { parsed = JsonSerializer.Deserialize<List<ExtractedTaskRaw>>(raw[start..(end + 1)], JsonOpts); }
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
