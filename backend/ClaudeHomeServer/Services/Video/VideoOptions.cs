namespace ClaudeHomeServer.Services.Video;

/// <summary>
/// Настройки раздела «Видео» (секция конфига <c>Video</c>).
/// </summary>
public sealed class VideoOptions
{
    public YouTubeOptions YouTube { get; init; } = new();

    public static VideoOptions FromConfig(IConfiguration config)
    {
        var section = config.GetSection("Video");
        return new VideoOptions
        {
            YouTube = new YouTubeOptions
            {
                ClientId = section["YouTube:ClientId"] ?? "",
                ClientSecret = section["YouTube:ClientSecret"] ?? "",
                RedirectUri = section["YouTube:RedirectUri"] ?? "",
                FeedTtlMinutes = int.TryParse(section["YouTube:FeedTtlMinutes"], out var ttl) && ttl > 0 ? ttl : 30,
                MaxFeedChannels = int.TryParse(section["YouTube:MaxFeedChannels"], out var max) && max > 0 ? max : 40,
            },
        };
    }
}

public sealed class YouTubeOptions
{
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";

    /// <summary>
    /// Адрес, который зарегистрирован в Google Cloud Console. Пусто — собираем из текущего
    /// запроса; за реверс-прокси это может разойтись с зарегистрированным, поэтому на бою
    /// значение лучше задать явно.
    /// </summary>
    public string RedirectUri { get; init; } = "";

    /// <summary>Сколько живёт собранная лента. Квота API дневная, дёргать её на каждый показ нельзя.</summary>
    public int FeedTtlMinutes { get; init; } = 30;

    /// <summary>
    /// Сколько каналов опрашиваем для сводной ленты. Каждый канал — один запрос
    /// (1 единица квоты), при сотне подписок и коротком TTL суточные 10 000 кончаются.
    /// </summary>
    public int MaxFeedChannels { get; init; } = 40;

    /// <summary>Пустые ключи = провайдер выключен (та же логика, что у LlmProviders).</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
