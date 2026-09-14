using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace ClaudeHomeServer.Services.Video;

/// <summary>
/// Лента подписок YouTube через Data API v3.
///
/// Смотреть под своим аккаунтом внутри продукта нельзя в принципе: встраиваемый плеер
/// всегда анонимен, персонализация в него не пробрасывается, а история просмотра закрыта
/// в API. Поэтому провайдер отвечает за КАТАЛОГ (подписки и что на них вышло), а
/// воспроизведение отдаётся штатному embed.
///
/// Сам поток видео идёт из браузера напрямую в googlevideo.com, минуя сервер, — обход
/// блокировок остаётся на стороне клиента. Через egress-прокси сервера ходят только
/// метаданные, это единицы килобайт.
/// </summary>
public sealed class YouTubeProvider(
    IHttpClientFactory httpFactory,
    YouTubeOAuthService oauth,
    VideoOptions options,
    IMemoryCache cache,
    ILogger<YouTubeProvider> log) : IVideoProvider
{
    private const string ApiBase = "https://www.googleapis.com/youtube/v3";

    public string Key => "youtube";
    public string Title => "YouTube";
    public VideoProviderKind Kind => VideoProviderKind.Feed;

    public bool IsConfigured => options.YouTube.IsConfigured;

    public ValueTask<bool> IsConnectedAsync(string ownerId, CancellationToken ct)
        => ValueTask.FromResult(oauth.HasAccount(ownerId));

    public async Task<VideoResult<VideoChannel>> ListChannelsAsync(
        string ownerId, CancellationToken ct, bool refresh = false)
    {
        if (!IsConfigured) return VideoResult<VideoChannel>.Fail(VideoFailure.NotConfigured);

        // Ключ кеша включает владельца: лента одного пользователя не должна утечь другому
        var cacheKey = $"video:youtube:subs:{ownerId}";
        if (!refresh && cache.TryGetValue(cacheKey, out IReadOnlyList<VideoChannel>? cached) && cached is not null)
            return VideoResult<VideoChannel>.Ok(cached);

        var token = await oauth.GetAccessTokenAsync(ownerId, options.YouTube, ct);
        if (token is null) return VideoResult<VideoChannel>.Fail(VideoFailure.NeedsAuth);

        var channels = new List<VideoChannel>();
        string? pageToken = null;
        var page = 0;
        do
        {
            var url = $"{ApiBase}/subscriptions?part=snippet&mine=true&maxResults=50&order=alphabetical"
                + (pageToken is null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");

            var (doc, failure) = await GetAsync(url, token, ct);
            if (failure != VideoFailure.None) return VideoResult<VideoChannel>.Fail(failure);
            using var _ = doc;

            var root = doc!.RootElement;
            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                {
                    var channel = ParseSubscription(item);
                    if (channel is not null) channels.Add(channel);
                }

            pageToken = root.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
            page++;
        }
        // Потолок именно по СТРАНИЦАМ: счётчик каналов не растёт, если страница пришла
        // без валидных items, и обход по кругу с непустым nextPageToken не остановился бы.
        // 10 страниц по 50 — 500 подписок, с запасом.
        while (pageToken is not null && page < 10);

        cache.Set(cacheKey, (IReadOnlyList<VideoChannel>)channels, TimeSpan.FromMinutes(options.YouTube.FeedTtlMinutes));
        return VideoResult<VideoChannel>.Ok(channels);
    }

    public async Task<VideoResult<VideoItem>> ListItemsAsync(
        string ownerId, string? channelId, CancellationToken ct, bool refresh = false)
    {
        if (!IsConfigured) return VideoResult<VideoItem>.Fail(VideoFailure.NotConfigured);

        var cacheKey = $"video:youtube:feed:{ownerId}:{channelId ?? "*"}";
        if (!refresh && cache.TryGetValue(cacheKey, out IReadOnlyList<VideoItem>? cached) && cached is not null)
            return VideoResult<VideoItem>.Ok(cached);

        var token = await oauth.GetAccessTokenAsync(ownerId, options.YouTube, ct);
        if (token is null) return VideoResult<VideoItem>.Fail(VideoFailure.NeedsAuth);

        IReadOnlyList<VideoChannel> channels;
        if (channelId is not null)
        {
            channels = [new VideoChannel(channelId, Key, "", true, null, null)];
        }
        else
        {
            var subs = await ListChannelsAsync(ownerId, ct, refresh);
            if (subs.Failed) return VideoResult<VideoItem>.Fail(subs.Failure);
            channels = subs.Items;

            // Потолок обхода: один канал = один запрос = единица суточной квоты.
            // Срезанное НЕ замалчиваем — иначе «показали всё» окажется неправдой.
            if (channels.Count > options.YouTube.MaxFeedChannels)
            {
                log.LogInformation(
                    "Сводная лента YouTube: опрошены первые {Taken} каналов из {Total} (потолок Video:YouTube:MaxFeedChannels)",
                    options.YouTube.MaxFeedChannels, channels.Count);
                channels = [.. channels.Take(options.YouTube.MaxFeedChannels)];
            }
        }

        // Общий дедлайн на весь обход: десятки каналов по таймауту клиента складывались бы
        // в минуты, и вкладка висела бы в пустоте. Что успели — то и показываем.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));

        var perChannel = channelId is null ? 5 : 25;   // сводная лента — «что нового», а не архив
        var gate = new SemaphoreSlim(5, 5);
        var items = new List<VideoItem>();
        var lastFailure = VideoFailure.None;
        var lockObj = new object();

        var tasks = channels.Select(async channel =>
        {
            var playlistId = UploadsPlaylistOf(channel.Id);
            if (playlistId is null) return;

            await gate.WaitAsync(deadline.Token);
            try
            {
                var url = $"{ApiBase}/playlistItems?part=snippet&maxResults={perChannel}"
                    + $"&playlistId={Uri.EscapeDataString(playlistId)}";

                var (doc, itemFailure) = await GetAsync(url, token, deadline.Token);
                if (itemFailure != VideoFailure.None)
                {
                    // Любой отказ запоминаем: молча отдать пустую ленту при лежащем сервисе —
                    // ровно то враньё, ради которого разводились классы отказов.
                    lock (lockObj) lastFailure = itemFailure;
                    return;
                }
                using var _ = doc;

                if (doc!.RootElement.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var raw in list.EnumerateArray())
                    {
                        var parsed = ParsePlaylistItem(raw);
                        if (parsed is not null) lock (lockObj) items.Add(parsed);
                    }
            }
            catch (OperationCanceledException)
            {
                // Дедлайн обхода — не ошибка приложения: отдаём собранное
                lock (lockObj) if (lastFailure == VideoFailure.None) lastFailure = VideoFailure.Unreachable;
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();   // отменил сам клиент — это другое дело

        // Пусто И был отказ — это отказ, а не «свежих роликов нет»
        if (items.Count == 0 && lastFailure != VideoFailure.None)
            return VideoResult<VideoItem>.Fail(lastFailure);

        var ordered = (IReadOnlyList<VideoItem>)[.. items.OrderByDescending(i => i.PublishedAt ?? DateTime.MinValue)];

        // Неполный сбор кешируем НЕНАДОЛГО: иначе вернувшийся сервис ещё полчаса
        // показывался бы огрызком ленты, собранным в момент обрыва.
        var ttl = lastFailure == VideoFailure.None
            ? TimeSpan.FromMinutes(options.YouTube.FeedTtlMinutes)
            : TimeSpan.FromMinutes(1);
        cache.Set(cacheKey, ordered, ttl);
        return VideoResult<VideoItem>.Ok(ordered);
    }

    /// <summary>
    /// Плейлист загрузок канала выводится из его id (UC… → UU…) — отдельный вызов
    /// channels.list ради этого не нужен, а при сотне подписок он стоил бы сотню запросов.
    /// </summary>
    internal static string? UploadsPlaylistOf(string channelId) =>
        channelId.StartsWith("UC", StringComparison.Ordinal) && channelId.Length > 2
            ? "UU" + channelId[2..]
            : null;

    private async Task<(JsonDocument? Doc, VideoFailure Failure)> GetAsync(string url, string token, CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient(YouTubeOAuthService.HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await http.SendAsync(req, ct);
            // NeedsAuth — ТОЛЬКО 401. У 403 причин много (закрытый или удалённый канал в
            // подписках даёт playlistItemsNotAccessible), и объявлять их протухшим входом
            // нельзя: человек уходит переподключать живой аккаунт и попадает в тупик,
            // потому что переподключение ничего не меняет.
            if (resp.StatusCode == HttpStatusCode.Unauthorized) return (null, VideoFailure.NeedsAuth);
            if (resp.StatusCode == HttpStatusCode.Forbidden)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return (null, body.Contains("quotaExceeded", StringComparison.OrdinalIgnoreCase)
                    ? VideoFailure.QuotaExceeded
                    : VideoFailure.Unreachable);
            }
            if (!resp.IsSuccessStatusCode) return (null, VideoFailure.Unreachable);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            return (await JsonDocument.ParseAsync(stream, cancellationToken: ct), VideoFailure.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Из России без обхода это штатное состояние, а не поломка продукта
            log.LogDebug(ex, "YouTube Data API недоступен");
            return (null, VideoFailure.Unreachable);
        }
    }

    private VideoChannel? ParseSubscription(JsonElement item)
    {
        if (!item.TryGetProperty("snippet", out var snippet)) return null;
        if (!snippet.TryGetProperty("resourceId", out var resource)
            || !resource.TryGetProperty("channelId", out var idEl)) return null;

        var id = idEl.GetString();
        if (string.IsNullOrEmpty(id)) return null;

        return new VideoChannel(
            Id: id,
            ProviderKey: Key,
            Title: snippet.TryGetProperty("title", out var t) ? t.GetString() ?? id : id,
            // Канал YouTube не «играется» сам по себе — смотрят ролики из его ленты
            Embeddable: false,
            EmbedUrl: null,
            ExternalUrl: $"https://www.youtube.com/channel/{id}",
            CoverUrl: ThumbnailOf(snippet));
    }

    private VideoItem? ParsePlaylistItem(JsonElement item)
    {
        if (!item.TryGetProperty("snippet", out var snippet)) return null;
        if (!snippet.TryGetProperty("resourceId", out var resource)
            || !resource.TryGetProperty("videoId", out var idEl)) return null;

        var id = idEl.GetString();
        if (string.IsNullOrEmpty(id)) return null;

        DateTime? published = snippet.TryGetProperty("publishedAt", out var p)
            && p.TryGetDateTime(out var dt) ? dt.ToUniversalTime() : null;

        return new VideoItem(
            Id: id,
            ProviderKey: Key,
            Title: snippet.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
            ChannelId: snippet.TryGetProperty("channelId", out var c) ? c.GetString() ?? "" : "",
            ChannelTitle: snippet.TryGetProperty("videoOwnerChannelTitle", out var oc)
                ? oc.GetString() ?? ""
                : snippet.TryGetProperty("channelTitle", out var ct2) ? ct2.GetString() ?? "" : "",
            ThumbnailUrl: ThumbnailOf(snippet),
            PublishedAt: published,
            // nocookie-домен: отложенных куки от встраивания меньше, поведение то же.
            // enablejsapi=1 — чтобы плеер слушал команды родителя: у роликов есть
            // позиция просмотра, и на время озвучки их ГЛУШАТ, а не снимают (снятый
            // iframe вернулся бы с нуля). Эфирам это не нужно — там снятие равноценно.
            EmbedUrl: $"https://www.youtube-nocookie.com/embed/{id}?enablejsapi=1",
            ExternalUrl: $"https://www.youtube.com/watch?v={id}");
    }

    private static string? ThumbnailOf(JsonElement snippet)
    {
        if (!snippet.TryGetProperty("thumbnails", out var thumbs) || thumbs.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var size in (string[])["medium", "high", "default"])
            if (thumbs.TryGetProperty(size, out var t) && t.TryGetProperty("url", out var u))
                return u.GetString();
        return null;
    }
}
