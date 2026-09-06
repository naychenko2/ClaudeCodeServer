using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace ClaudeHomeServer.Services.Video;

/// <summary>
/// Прямые эфиры телеканалов через СМОТРИМ (ВГТРК).
///
/// Почему именно он: Rutube транслирует те же каналы, но встраивание их эфира запрещает
/// правообладатель, а плеер «Витрины ТВ» раздаётся только по договору. У СМОТРИМ плеер
/// отдаётся без X-Frame-Options и frame-ancestors — это единственный проверенный легальный
/// путь смотреть федеральные каналы внутри своего интерфейса.
///
/// Важное ограничение: собственный поток есть не у всех каналов каталога. Остальные на
/// самом сайте играются плеером «Витрины» по домену-реферреру — подделывать реферер мы
/// не будем, поэтому такие каналы показываем карточкой со ссылкой наружу.
/// </summary>
public sealed class SmotrimProvider(
    IHttpClientFactory httpFactory,
    IMemoryCache cache,
    ILogger<SmotrimProvider> log) : IVideoProvider
{
    public const string HttpClientName = "video-smotrim";

    private const string CacheKey = "video:smotrim:channels";
    // Карточка канала несёт ТЕКУЩУЮ передачу — кеш живёт минуту, иначе в сетке будет
    // висеть то, что шло полчаса назад.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    // Одновременных запросов к чужому API: каталог почти на четыре десятка каналов, лупить
    // их разом невежливо и легко получить троттлинг. Шесть — компромисс: холодный сбор
    // укладывается в пару секунд, а очередь из тридцати семи запросов по четыре растянула бы
    // первое открытие панели.
    private static readonly SemaphoreSlim Throttle = new(6, 6);
    // Первое открытие раздела у нескольких вкладок сразу не должно превращаться в
    // несколько полных обходов каталога: собираем один раз, остальные ждут результат.
    private static readonly SemaphoreSlim BuildGate = new(1, 1);

    public string Key => "smotrim";
    public string Title => "ТВ";
    public VideoProviderKind Kind => VideoProviderKind.Live;

    /// <summary>Ключей и аккаунтов не требует — работает всегда.</summary>
    public bool IsConfigured => true;

    public ValueTask<bool> IsConnectedAsync(string ownerId, CancellationToken ct) => ValueTask.FromResult(true);

    public async Task<VideoResult<VideoChannel>> ListChannelsAsync(
        string ownerId, CancellationToken ct, bool refresh = false)
    {
        // Кеш общий на всех пользователей: эфир и программа у всех одинаковые, персональных
        // данных в ответе нет.
        if (!refresh && cache.TryGetValue(CacheKey, out IReadOnlyList<VideoChannel>? cached) && cached is not null)
            return VideoResult<VideoChannel>.Ok(cached);

        await BuildGate.WaitAsync(ct);
        try
        {
            if (!refresh && cache.TryGetValue(CacheKey, out cached) && cached is not null)
                return VideoResult<VideoChannel>.Ok(cached);

            var channels = await BuildAsync(ct);
            cache.Set(CacheKey, channels, CacheTtl);
            return VideoResult<VideoChannel>.Ok(channels);
        }
        finally
        {
            BuildGate.Release();
        }
    }

    /// <summary>Эфирный провайдер: лент нет, смотрят сам канал.</summary>
    public Task<VideoResult<VideoItem>> ListItemsAsync(
        string ownerId, string? channelId, CancellationToken ct, bool refresh = false)
        => Task.FromResult(VideoResult<VideoItem>.Ok([]));

    private async Task<IReadOnlyList<VideoChannel>> BuildAsync(CancellationToken ct)
    {
        var tasks = SmotrimCatalog.Channels.Select(e => LoadOneAsync(e, ct)).ToArray();
        var loaded = await Task.WhenAll(tasks);

        // Играбельные вперёд, внутри групп — порядок каталога (кнопки телевизора).
        // Иначе каналы, которые реально можно смотреть, теряются среди ссылок наружу.
        return [.. loaded.OrderByDescending(c => c.Embeddable)];
    }

    /// <summary>
    /// Карточка одного канала. Любой отказ вырождается в запись из каталога без программы
    /// и без плеера — список каналов не должен зависеть от доступности каждого из них.
    /// </summary>
    private async Task<VideoChannel> LoadOneAsync(SmotrimCatalog.Entry entry, CancellationToken ct)
    {
        var fallback = new VideoChannel(
            Id: entry.Id.ToString(),
            ProviderKey: Key,
            Title: entry.Title,
            Embeddable: false,
            EmbedUrl: null,
            ExternalUrl: SmotrimCatalog.ExternalUrl(entry.Id));

        await Throttle.WaitAsync(ct);
        try
        {
            var http = httpFactory.CreateClient(HttpClientName);
            using var resp = await http.GetAsync(SmotrimCatalog.CardUrl(entry.Id), ct);
            if (!resp.IsSuccessStatusCode) return fallback;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return fallback;

            var title = data.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? entry.Title
                : entry.Title;

            // Признак «можно играть у себя» — наличие собственного потока, а не список id.
            var embeddable = data.TryGetProperty("streams", out var streams)
                && streams.ValueKind == JsonValueKind.Object
                && streams.TryGetProperty("m3u8", out var m3u8)
                && m3u8.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(m3u8.GetString());

            string? cover = null;
            if (data.TryGetProperty("splash", out var splash) && splash.ValueKind == JsonValueKind.Object
                && splash.TryGetProperty("medium", out var medium) && medium.ValueKind == JsonValueKind.String)
                cover = medium.GetString();

            string? now = null;
            if (data.TryGetProperty("epg", out var epg) && epg.ValueKind == JsonValueKind.Object
                && epg.TryGetProperty("programName", out var prog) && prog.ValueKind == JsonValueKind.String)
                now = Normalize(prog.GetString());

            return fallback with
            {
                Title = title,
                Embeddable = embeddable,
                EmbedUrl = embeddable ? SmotrimCatalog.EmbedUrl(entry.Id) : null,
                CoverUrl = cover,
                NowPlaying = now,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Тихо: чужой сервис лежит штатно, а раздел от этого не ломается
            log.LogDebug(ex, "Карточка канала СМОТРИМ {Id} не получена", entry.Id);
            return fallback;
        }
        finally
        {
            Throttle.Release();
        }
    }

    /// <summary>
    /// Название передачи приходит с техническим хвостом вида `TUCKER. "Россия 24"` —
    /// имя канала в подписи «сейчас в эфире» лишнее, оно уже написано на карточке.
    /// </summary>
    private static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        var quote = text.IndexOf('"');
        if (quote > 0) text = text[..quote].TrimEnd(' ', '.', ',', '-', '—');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
