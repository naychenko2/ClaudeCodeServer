using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Services.Spend;

// Замер одного запуска исполнителя. Только числа и идентификаторы разрезов.
// В Core: parameter-тип ITaskPromptMetricsStore.Record.
// Инвариант приватности (под тестом-сторожем): сюда едут ТОЛЬКО размеры секций в
// символах — ни одного символа текста постановки, описания задачи или заметок.
public sealed record TaskPromptMetricsEntry(
    [property: JsonPropertyName("at")] DateTime At,
    [property: JsonPropertyName("taskId")] string TaskId,
    [property: JsonPropertyName("ownerId")] string OwnerId,
    [property: JsonPropertyName("projectId")] string? ProjectId,
    [property: JsonPropertyName("sessionId")] string? SessionId,
    [property: JsonPropertyName("personaId")] string? PersonaId,
    [property: JsonPropertyName("totalChars")] int TotalChars,
    [property: JsonPropertyName("totalTokensEst")] int TotalTokensEst,
    [property: JsonPropertyName("task")] int TaskSection,
    [property: JsonPropertyName("expected")] int ExpectedResult,
    [property: JsonPropertyName("tools")] int Tools,
    [property: JsonPropertyName("rules")] int Rules,
    [property: JsonPropertyName("restrictions")] int Restrictions,
    [property: JsonPropertyName("delegation")] int Delegation,
    [property: JsonPropertyName("omo")] int OmO,
    [property: JsonPropertyName("context")] int Context,
    [property: JsonPropertyName("notes")] int Notes);

// Узкий шов учёта размеров постановки: Main пишет Entry, конкретный
// TaskPromptMetricsStore (Spend) реализует.
public interface ITaskPromptMetricsStore
{
    void Record(TaskPromptMetricsEntry entry);
}
