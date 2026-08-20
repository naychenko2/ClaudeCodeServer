using System.Text.Json;
using AiHomeDesktop.App.Execution;

namespace AiHomeDesktop.App.Hands;

/// <summary>Чем кончился шаг ленты — по этому окно красит строку.</summary>
public enum HandsFeedKind
{
    /// <summary>Что-то ушло в модель: кадр, ответ инструмента.</summary>
    Sent,

    /// <summary>Человек подтвердил действие.</summary>
    Confirmed,

    /// <summary>Человек отклонил действие.</summary>
    Declined,

    /// <summary>Вызов отменён сервером (истекло ожидание, погас сеанс, «Стоп»).</summary>
    Cancelled,

    /// <summary>Вызов не состоялся: отказ устройства, дедлайн, сбой.</summary>
    Failed,

    /// <summary>Событие сеанса: начат, погас.</summary>
    Session
}

/// <summary>Строка ленты «что ушло в модель».</summary>
public sealed record HandsFeedEntry(DateTimeOffset At, HandsFeedKind Kind, string ChatTitle, string Text);

/// <summary>
/// Лента «что ушло в модель». Внутри сеанса кадр уходит БЕЗ отдельного нажатия — иначе
/// «посмотри, что за ошибка» неработоспособно; ценой этого каждый ушедший кадр обязан быть
/// виден человеку здесь. Лента — не журнал протокола: она про то, что увидела модель.
/// </summary>
public interface IHandsActivityFeed
{
    void Add(HandsFeedEntry entry);
}

/// <summary>Лента в памяти клиента: живёт, пока живёт окно, потолок — последние записи.</summary>
public sealed class HandsActivityFeed(int capacity = 200) : IHandsActivityFeed
{
    private readonly Lock _lock = new();
    private readonly LinkedList<HandsFeedEntry> _entries = [];

    /// <summary>Лента пополнилась — окно перерисовывает список.</summary>
    public event Action? Changed;

    /// <summary>Свежие сверху.</summary>
    public IReadOnlyList<HandsFeedEntry> Entries
    {
        get { lock (_lock) return [.. _entries]; }
    }

    public void Add(HandsFeedEntry entry)
    {
        lock (_lock)
        {
            _entries.AddFirst(entry);
            while (_entries.Count > capacity) _entries.RemoveLast();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_lock) _entries.Clear();
        Changed?.Invoke();
    }
}

/// <summary>Подписи строк ленты. Отдельно от тостов: там вопрос, здесь — уже свершившееся.</summary>
public static class HandsFeedText
{
    /// <summary>Что именно уехало в модель по завершённому вызову.</summary>
    public static string ForResult(DesktopCall call, DesktopCallOutcome outcome)
    {
        var what = call.Kind switch
        {
            DesktopCallKinds.Screen => $"кадр: {ConfirmationText.ScreenScope(call.Args)}",
            DesktopCallKinds.Open => $"открытие: {Target(call.Args)}",
            _ => $"вызов «{call.Kind}»"
        };

        if (outcome.Outcome != DesktopOutcomes.Ok)
            return $"{what} — исход {outcome.Outcome}{Tail(outcome.Message)}";

        var size = PayloadSize(outcome.Payload);
        return size is null ? $"{what} — ушло в модель" : $"{what} — ушло в модель, {size}";
    }

    private static string Tail(string? message) =>
        string.IsNullOrWhiteSpace(message) ? "" : $": {message}";

    private static string Target(JsonElement? args) =>
        args is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? "не указано"
            : "не указано";

    // Размер кадра считаем по base64-полю: человеку важно, сколько именно уехало.
    private static string? PayloadSize(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } o
            || !o.TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.Object
            || !image.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String) return null;

        var bytes = (long)(data.GetString()?.Length ?? 0) * 3 / 4;
        return bytes < 1024 ? $"{bytes} Б"
            : bytes < 1024 * 1024 ? $"{bytes / 1024} КБ"
            : $"{bytes / (1024.0 * 1024):0.0} МБ";
    }
}
