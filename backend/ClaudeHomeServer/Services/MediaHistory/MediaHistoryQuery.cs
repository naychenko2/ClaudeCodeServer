using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.MediaHistory;

// Запрос к истории: фильтры (вид, модель, исход, логин, владелец, период) + курсорная пагинация.
// Пустое значение фильтра — «не задан». Лимит страницы — 50 (ADR-030 §7).
public sealed class MediaHistoryQuery
{
    public const int PageLimit = 50;

    // Вид (image|video|audio|train|other)
    public string? Kind { get; init; }
    // Модель (имя)
    public string? Model { get; init; }
    // Исход (completed|failed|cancelled|interrupted)
    public string? Outcome { get; init; }
    // Логин владельца
    public string? Login { get; init; }
    // Владелец (id): фильтр на уровне индекса — только записи этого владельца.
    // Для не-админа обязателен (OwnerId == sub); админ — null (все).
    // fail-closed (ADR-030, 2026-10-18): null/пустой/пробельный при scope "self" — ни одной
    // записи (пустой владелец не расширяет выдачу).
    public string? OwnerId { get; init; }
    // Охват (ADR-030, 2026-10-18): "all" — админ (все, логин владельца раскрывается),
    // "self" — не-админ (только свои, логин не раскрывается).
    public string Scope { get; init; } = "all";
    // Период: от/до (UTC)
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    // Курсор предыдущей страницы (null — первая)
    public string? Cursor { get; init; }
    // Лимит (по умолчанию 50)
    public int Limit { get; init; } = PageLimit;
}

// Строка списка: одна запись. JSON-имена стабильны (фронт, Кира).
public sealed record MediaHistoryRecordDto
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("op")] public string? Op { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("prompt")] public string? Prompt { get; init; }
    [JsonPropertyName("params")] public JsonObject? Params { get; init; }
    [JsonPropertyName("owner")] public string? Owner { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("outcome")] public string? Outcome { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("runSeconds")] public int? RunSeconds { get; init; }
    [JsonPropertyName("enqueuedAt")] public DateTimeOffset? EnqueuedAt { get; init; }
    [JsonPropertyName("startedAt")] public DateTimeOffset? StartedAt { get; init; }
    [JsonPropertyName("finishedAt")] public DateTimeOffset? FinishedAt { get; init; }
    [JsonPropertyName("source")] public string? Source { get; init; }
}

// Счётчики для меню фильтров: сколько записей по каждому виду и исходу (по всему окну,
// с учётом остальных активных фильтров). Пусто — фильтр не даёт срез.
public sealed record MediaHistoryCounters
{
    [JsonPropertyName("kinds")] public Dictionary<string, int> Kinds { get; init; } = [];
    [JsonPropertyName("outcomes")] public Dictionary<string, int> Outcomes { get; init; } = [];
}

// Страница истории: записи (по newest-first), курсор следующей страницы и счётчики фильтров.
public sealed record MediaHistoryPage
{
    [JsonPropertyName("records")] public IReadOnlyList<MediaHistoryRecordDto> Records { get; init; } = [];
    [JsonPropertyName("nextCursor")] public string? NextCursor { get; init; }
    [JsonPropertyName("counters")] public MediaHistoryCounters Counters { get; init; } = new();
    // Охват (ADR-030, 2026-10-18): "all" — админ (все), "self" — не-админ (только свои).
    [JsonPropertyName("scope")] public string Scope { get; init; } = "all";
}
