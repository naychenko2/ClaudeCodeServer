namespace ClaudeHomeServer.Services.Video;

/// <summary>
/// Реестр источников видео. Контроллер работает только через него, поэтому новый провайдер
/// добавляется регистрацией в DI и больше нигде.
/// </summary>
public sealed class VideoProviderRegistry(IEnumerable<IVideoProvider> providers)
{
    private readonly IReadOnlyList<IVideoProvider> _all = [.. providers];

    /// <summary>Настроенные провайдеры — только они попадают во вкладки раздела.</summary>
    public IReadOnlyList<IVideoProvider> Enabled => [.. _all.Where(p => p.IsConfigured)];

    public IVideoProvider? Find(string key) =>
        _all.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && p.IsConfigured);

    /// <summary>Карточки для фронта: что показывать вкладками и какая из них просит вход.</summary>
    public async Task<IReadOnlyList<VideoProviderInfo>> DescribeAsync(string ownerId, CancellationToken ct)
    {
        var list = new List<VideoProviderInfo>();
        foreach (var p in Enabled)
        {
            var connected = await p.IsConnectedAsync(ownerId, ct);
            list.Add(new VideoProviderInfo(
                Key: p.Key,
                Title: p.Title,
                Kind: p.Kind,
                Connected: connected,
                NeedsAuth: !connected));
        }
        return list;
    }
}
