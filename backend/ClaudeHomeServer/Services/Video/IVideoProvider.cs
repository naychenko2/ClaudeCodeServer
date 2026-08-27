namespace ClaudeHomeServer.Services.Video;

/// <summary>
/// Почему провайдер не отдал данные. Классы разведены не ради красоты: каждому в разделе
/// отвечает СВОЁ пустое состояние, и «подключите аккаунт» вместо «кончилась квота» — это
/// человек, который жмёт не ту кнопку.
/// </summary>
public enum VideoFailure
{
    None = 0,
    /// <summary>Провайдер выключен в конфиге (нет ключей).</summary>
    NotConfigured,
    /// <summary>Аккаунт не подключён или refresh-токен умер — нужен повторный вход.</summary>
    NeedsAuth,
    /// <summary>Дневная квота API исчерпана.</summary>
    QuotaExceeded,
    /// <summary>Сервис не ответил: сеть, таймаут, 5xx. Для YouTube из РФ — штатное состояние.</summary>
    Unreachable,
}

/// <summary>
/// Ответ провайдера. Пустой список и отказ — разные вещи: у первого «здесь пока ничего нет»,
/// у второго есть причина, которую надо показать.
/// </summary>
public sealed record VideoResult<T>(IReadOnlyList<T> Items, VideoFailure Failure = VideoFailure.None)
{
    public static VideoResult<T> Ok(IReadOnlyList<T> items) => new(items);
    public static VideoResult<T> Fail(VideoFailure failure) => new([], failure);
    public bool Failed => Failure != VideoFailure.None;
}

/// <summary>
/// Источник видео для раздела «Видео». Реализации регистрируются в
/// <see cref="VideoProviderRegistry"/>; добавление нового источника не должно требовать
/// правок в контроллере.
/// </summary>
public interface IVideoProvider
{
    /// <summary>Ключ провайдера в API и в настройках (`smotrim`, `youtube`).</summary>
    string Key { get; }

    /// <summary>Подпись вкладки для человека.</summary>
    string Title { get; }

    VideoProviderKind Kind { get; }

    /// <summary>Есть ли всё нужное в конфиге. Ненастроенный провайдер вкладку не получает.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Подключён ли аккаунт владельца. У провайдеров без OAuth — всегда true.
    /// </summary>
    ValueTask<bool> IsConnectedAsync(string ownerId, CancellationToken ct);

    /// <summary>
    /// Каналы: телеканалы у эфирного провайдера, подписки — у ленточного.
    /// Отказ ОДНОГО канала не должен ронять весь список.
    /// </summary>
    /// <param name="refresh">
    /// Пропустить ЧТЕНИЕ кеша (кнопка «Обновить»). Запись в кеш при этом остаётся: иначе
    /// кнопка чинила бы одно нажатие, а следующий заход снова читал протухшее. Без этого
    /// флага «Обновить» визуально не делает ничего — а жмут её как раз тогда, когда пусто.
    /// </param>
    Task<VideoResult<VideoChannel>> ListChannelsAsync(string ownerId, CancellationToken ct, bool refresh = false);

    /// <summary>
    /// Лента роликов. <paramref name="channelId"/> = null — сводная лента по всем каналам.
    /// У эфирных провайдеров лент нет: возвращают пустой результат.
    /// </summary>
    Task<VideoResult<VideoItem>> ListItemsAsync(string ownerId, string? channelId, CancellationToken ct, bool refresh = false);
}
