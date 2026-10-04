using System.Text.Json.Serialization;
using ClaudeHomeServer.Services.VideoEditor.Contracts;

namespace ClaudeHomeServer.Services.VideoEditor.Jobs;

// Контракты исполнителя съёмки (ADR-022, как ADR-021 §2 у звука): котировка → запуск строго по quoteId →
// события в группу владельца. Отказ — результат с кодом и внятным текстом, а не исключение: ручке и
// тулсету нужно различать «котировка устарела», «слишком много задач» и «поставщик недоступен».

public sealed record VideoEditCallResult<T>(T? Value, string? ErrorCode, string? Error)
{
    // Котировка соседа при отказе поставщика: запускает её только человек
    public RetryQuote? Retry { get; init; }

    // Свежий контекст чата при отказе context_changed: ручка отдаёт его телом 409
    public Protocol.ChatContextDto? Context { get; init; }

    public static VideoEditCallResult<T> Ok(T value) => new(value, null, null);
    public static VideoEditCallResult<T> Fail(string code, string error) => new(default, code, error);
}


public enum VideoEditJobStatus { Queued, Running, Downloading, Completed, Failed, Cancelled }
