namespace ClaudeHomeServer.Services.Video;

/// <summary>
/// Каталог каналов СМОТРИМ. Зашит таблицей осознанно: эндпоинта со списком у сервиса
/// нет (`/api/v1/channels` отвечает 404), карточка канала доступна только по известному id.
///
/// Названия здесь — ФОЛБЭК на случай, когда карточка канала не ответила: список каналов
/// обязан отрисоваться целиком даже при частичном отказе, иначе один таймаут выносит
/// весь раздел. Когда карточка приходит, название берётся из неё.
///
/// Порядок — как кнопки на телевизоре (первый и второй мультиплексы РТРС), чтобы сетка
/// читалась привычно.
/// </summary>
public static class SmotrimCatalog
{
    public sealed record Entry(int Id, string Title);

    public static readonly IReadOnlyList<Entry> Channels =
    [
        new(270, "Первый канал"),
        new(1, "Россия 1"),
        new(263, "Матч ТВ"),
        new(267, "НТВ"),
        new(255, "Пятый канал"),
        new(4, "Россия К"),
        new(3, "Россия 24"),
        new(70, "Карусель"),
        new(363, "ОТР"),
        new(260, "ТВЦ"),
        new(256, "РЕН ТВ"),
        new(257, "СПАС"),
        new(258, "СТС"),
        new(250, "Домашний"),
        new(264, "ТВ-3"),
        new(265, "Пятница!"),
        new(251, "Звезда"),
        new(253, "МИР"),
        new(266, "ТНТ"),
        new(254, "Муз-ТВ"),
    ];

    /// <summary>Карточка канала в API плеера.</summary>
    public static string CardUrl(int id) => $"https://player-api.smotrim.ru/api/v1/channel/{id}";

    /// <summary>Встраиваемый плеер. Формат проверен живьём, играет и на 390 CSS-пикселях.</summary>
    public static string EmbedUrl(int id) => $"https://player.smotrim.ru/iframe/channel/id/{id}";

    /// <summary>Страница канала на сайте — туда уводим каналы без собственного потока.</summary>
    public static string ExternalUrl(int id) => $"https://smotrim.ru/channel/{id}";
}
