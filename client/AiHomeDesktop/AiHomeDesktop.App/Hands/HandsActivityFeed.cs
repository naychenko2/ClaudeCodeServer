using AiHomeDesktop.Core.Abstractions;
using AiHomeDesktop.Core.Protocol;

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

/// <summary>
/// Лента в памяти клиента: живёт, пока живёт окно, потолок — последние записи.
///
/// Она же принимает события склейки вызова из ядра (<see cref="ICallFeed"/>): у ленты один
/// список на всё — и на сеанс, и на вызовы, иначе человек читал бы историю в двух местах.
/// </summary>
public sealed class HandsActivityFeed(int capacity = 200) : IHandsActivityFeed, ICallFeed
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

    /// <summary>Событие вызова из ядра. Без исхода — вызов только пришёл, с исходом — кончился.</summary>
    public void Add(DesktopFeedEntry entry) => Add(new HandsFeedEntry(
        entry.At,
        HandsFeedText.KindOf(entry.Outcome),
        string.IsNullOrWhiteSpace(entry.ChatName) ? "без названия" : entry.ChatName!,
        HandsFeedText.ForCall(entry)));

    public void Clear()
    {
        lock (_lock) _entries.Clear();
        Changed?.Invoke();
    }
}

/// <summary>Подписи строк ленты. Отдельно от тостов: там вопрос, здесь — уже свершившееся.</summary>
public static class HandsFeedText
{
    /// <summary>Строка ленты по событию вызова: первая строка тоста плюс исход и размер.</summary>
    public static string ForCall(DesktopFeedEntry entry)
    {
        // Заголовок вызова — первая строка того же текста, что видел человек в тосте:
        // пересказывать вызов своими словами здесь так же нельзя, как и там.
        var what = entry.Title.Split('\n')[0];

        if (entry.Outcome is null) return $"{what} — вызов принят";
        if (entry.Outcome != DesktopOutcomes.Ok) return $"{what} — исход {entry.Outcome}";

        return entry.Details is null
            ? $"{what} — ушло в модель"
            : $"{what} — ушло в модель, {entry.Details}";
    }

    /// <summary>Как красить строку: отказ человека, отмена и сбой — разные новости.</summary>
    public static HandsFeedKind KindOf(string? outcome) => outcome switch
    {
        null => HandsFeedKind.Sent,
        DesktopOutcomes.Ok => HandsFeedKind.Sent,
        DesktopOutcomes.Denied => HandsFeedKind.Declined,
        DesktopOutcomes.Cancelled => HandsFeedKind.Cancelled,
        DesktopOutcomes.AwaitingConfirmation => HandsFeedKind.Cancelled,
        _ => HandsFeedKind.Failed
    };
}
