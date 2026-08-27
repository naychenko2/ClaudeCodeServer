namespace ClaudeHomeServer.Services.Video;

/// <summary>
/// Что провайдер вообще умеет показывать. От этого зависит вид раздела: у эфирного
/// провайдера список каналов И ЕСТЬ содержимое (сетка плиток), у ленточного канал —
/// это подписка, а смотрят ролики из его ленты.
/// </summary>
public enum VideoProviderKind
{
    /// <summary>Прямые эфиры: канал = то, что смотрят (СМОТРИМ).</summary>
    Live,
    /// <summary>Лента: канал = подписка, смотрят ролики из неё (YouTube).</summary>
    Feed,
}

/// <summary>
/// Канал: телеканал у эфирного провайдера, подписка — у ленточного.
/// </summary>
/// <param name="Id">Идентификатор внутри провайдера (id СМОТРИМ, channelId YouTube).</param>
/// <param name="Embeddable">
/// Можно ли играть внутри продукта. У СМОТРИМ поток есть не у всех каналов из каталога
/// (17 из 20 вещаются плеером Витрины по домену-реферреру), поэтому признак вычисляется
/// из ответа API, а не хардкодится списком id: сегодня их три, завтра может стать больше.
/// </param>
/// <param name="EmbedUrl">Адрес встраиваемого плеера; null, когда <paramref name="Embeddable"/> = false.</param>
/// <param name="ExternalUrl">Куда уводить, когда играть у себя нельзя.</param>
/// <param name="NowPlaying">Что идёт сейчас (EPG). Приходит и у неиграбельных каналов.</param>
public sealed record VideoChannel(
    string Id,
    string ProviderKey,
    string Title,
    bool Embeddable,
    string? EmbedUrl,
    string? ExternalUrl,
    string? CoverUrl = null,
    string? NowPlaying = null);

/// <summary>
/// Ролик из ленты канала (ленточные провайдеры).
/// </summary>
public sealed record VideoItem(
    string Id,
    string ProviderKey,
    string Title,
    string ChannelId,
    string ChannelTitle,
    string? ThumbnailUrl,
    DateTime? PublishedAt,
    string EmbedUrl,
    string ExternalUrl);

/// <summary>
/// Карточка провайдера для фронта: по ней рисуются вкладки раздела.
/// </summary>
/// <remarks>
/// Признака «настроен» здесь нет намеренно: ненастроенный провайдер до карточки не
/// доходит вовсе — реестр отдаёт только включённые, как LlmProviders с пустым ApiKey.
/// Поле, всегда равное true, врало бы о наличии выбора.
/// </remarks>
/// <param name="Connected">
/// Подключён ли аккаунт владельца. Осмысленно только у провайдеров с OAuth; у эфирных
/// всегда true — там подключать нечего.
/// </param>
public sealed record VideoProviderInfo(
    string Key,
    string Title,
    VideoProviderKind Kind,
    bool Connected,
    bool NeedsAuth);
