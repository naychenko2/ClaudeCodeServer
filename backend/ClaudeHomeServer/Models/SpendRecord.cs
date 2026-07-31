using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Models;

// Источники расхода токенов (спека Spend Analytics v2): ходы чатов/задач, фоновые one-shot,
// генерации fal.ai (токенов нет — счётчик), бесплатные модели (токены есть, стоимость 0).
public static class SpendSources
{
    public const string ChatTurn = "chat-turn";
    public const string OneShot = "one-shot";
    public const string Fal = "fal";
    public const string Glif = "glif";
    public const string Free = "free";

    // Бесплатный исполнитель: локальная Ollama, прямой адаптер любого OpenAI-совместимого
    // источника (провайдер заканчивается на "-direct") или модель ":free" через CLI.
    public static bool IsFree(string provider, string? model) =>
        provider is "ollama"
        || provider.EndsWith("-direct", StringComparison.OrdinalIgnoreCase)
        || (model is not null && model.EndsWith(":free", StringComparison.OrdinalIgnoreCase));

    // Дополнительные подписки Claude (sub-*) — тот же провайдер claude, отдельной осью не нужны
    public static string NormalizeProvider(string provider) =>
        provider.StartsWith("sub-", StringComparison.Ordinal) ? "claude" : provider;
}

// Одна запись расхода: ход чата, фоновый one-shot, генерация fal.ai или вызов бесплатной
// модели. Детальные записи живут в data/spend/turns-*.jsonl последние Spend:DetailDays дней,
// старше — сворачиваются в дневные агрегаты DailySpendRow (SpendStore.RollupOlderThan).
public sealed class SpendRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    // Владелец траты; "" — системный вызов (changelog, каталог моделей и т.п.)
    public string OwnerId { get; init; } = "";
    public string? ProjectId { get; init; }
    public string? SessionId { get; init; }
    public string? TaskId { get; init; }
    public string? PersonaId { get; init; }
    public string Provider { get; init; } = "claude";
    public string? Model { get; init; }
    public string Source { get; init; } = SpendSources.ChatTurn;
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    // Стоимость собирается про запас (метрика фичи — токены, UI деньги не показывает)
    public double? CostUsd { get; init; }
    // Счётчик генераций fal.ai (токенов у него нет); у остальных источников 0
    public int Generations { get; init; }
    public long DurationMs { get; init; }
    // Подпись операции: ключ фонового действия (changelog, notes.tags…) или endpoint fal
    public string? Label { get; init; }

    [JsonIgnore]
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;

    [JsonIgnore]
    public DateOnly Date => DateOnly.FromDateTime(Timestamp);
}

// Дневной агрегат по полному составному ключу разрезов — из него считается любой pivot
// за пределами детального окна (кроме уровня «ход»).
public sealed class DailySpendRow
{
    public string Date { get; init; } = "";
    public string OwnerId { get; init; } = "";
    public string? ProjectId { get; init; }
    public string? SessionId { get; init; }
    public string? TaskId { get; init; }
    public string? PersonaId { get; init; }
    public string Provider { get; init; } = "";
    public string? Model { get; init; }
    public string Source { get; init; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheCreationTokens { get; set; }
    public double CostUsd { get; set; }
    public int Generations { get; set; }
    // Количество свёрнутых записей (ходов/вызовов)
    public int Turns { get; set; }
}
