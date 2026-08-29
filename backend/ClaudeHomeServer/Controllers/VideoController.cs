using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Video;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Раздел «Видео»: эфиры телеканалов и лента подписок YouTube.
/// Источники подключаются через <see cref="VideoProviderRegistry"/> — здесь про них
/// не знают ничего, кроме ключа.
/// </summary>
[ApiController]
[Authorize]
[Route("api/video")]
public class VideoController(
    VideoProviderRegistry registry,
    YouTubeOAuthService youtubeOauth,
    VideoOptions options,
    UserStore users,
    ILogger<VideoController> log) : ControllerBase
{
    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    /// <summary>Какие источники доступны и какой из них просит подключить аккаунт.</summary>
    [HttpGet("providers")]
    public async Task<IActionResult> Providers(CancellationToken ct)
    {
        var userId = UserId;
        if (userId is null) return Unauthorized();

        var list = await registry.DescribeAsync(userId, ct);
        return Ok(list.Select(p => new
        {
            key = p.Key,
            title = p.Title,
            kind = p.Kind == VideoProviderKind.Live ? "live" : "feed",
            connected = p.Connected,
            needsAuth = p.NeedsAuth,
        }));
    }

    /// <summary>Каналы источника: телеканалы или подписки.</summary>
    [HttpGet("channels")]
    public async Task<IActionResult> Channels(
        [FromQuery] string provider, [FromQuery] bool refresh, CancellationToken ct)
    {
        var userId = UserId;
        if (userId is null) return Unauthorized();

        var target = registry.Find(provider);
        if (target is null) return NotFound(new { error = "unknown-provider" });

        var result = await target.ListChannelsAsync(userId, ct, refresh);
        return Ok(new
        {
            error = WireError(result.Failure),
            channels = result.Items.Select(Wire),
        });
    }

    /// <summary>Лента роликов: сводная или по одному каналу.</summary>
    [HttpGet("feed")]
    public async Task<IActionResult> Feed(
        [FromQuery] string provider, [FromQuery] string? channelId, [FromQuery] bool refresh,
        CancellationToken ct)
    {
        var userId = UserId;
        if (userId is null) return Unauthorized();

        var target = registry.Find(provider);
        if (target is null) return NotFound(new { error = "unknown-provider" });

        var result = await target.ListItemsAsync(userId, channelId, ct, refresh);
        return Ok(new
        {
            error = WireError(result.Failure),
            items = result.Items.Select(i => new
            {
                id = i.Id,
                provider = i.ProviderKey,
                title = i.Title,
                channelId = i.ChannelId,
                channelTitle = i.ChannelTitle,
                thumbnailUrl = i.ThumbnailUrl,
                publishedAt = i.PublishedAt,
                embedUrl = i.EmbedUrl,
                externalUrl = i.ExternalUrl,
            }),
        });
    }

    /// <summary>
    /// Избранные каналы владельца — то, что показывает полоса в шапке панели, центра и
    /// плавающего окна. Живёт здесь, а не в /api/me/*, потому что смысл имеет только внутри
    /// раздела: ключи каналов знает каталог, и фронт ходит сюда тем же модулем api.video.
    ///
    /// Поле <c>configured</c> отвечает на вопрос «человек уже настраивал?»: пустой список
    /// при <c>configured: false</c> означает дефолтный набор, при <c>true</c> — осознанно
    /// пустую полосу. Свести их в одно поле нельзя — это разные экраны.
    /// </summary>
    [HttpGet("favorites")]
    public IActionResult Favorites()
    {
        var userId = UserId;
        if (userId is null) return Unauthorized();

        var saved = users.GetFavoriteVideoChannels(userId);
        return Ok(new
        {
            configured = saved is not null,
            keys = saved ?? VideoFavorites.Defaults,
        });
    }

    /// <summary>
    /// Полная замена набора. Валидация молчаливая, как у «Стены»: мусорные ключи и дубли
    /// отбрасываются, длинный список режется по потолку — сохранение не должно падать
    /// из-за канала, пропавшего из каталога, пока полоса была открыта.
    /// </summary>
    [HttpPut("favorites")]
    public IActionResult PutFavorites([FromBody] PutFavoritesRequest req)
    {
        var userId = UserId;
        if (userId is null) return Unauthorized();
        if (req?.Keys is null) return BadRequest(new { error = "keys обязателен" });

        var keys = VideoFavorites.Normalize(req.Keys.Take(200));
        if (!users.SetFavoriteVideoChannels(userId, keys)) return Unauthorized();

        return Ok(new { configured = true, keys });
    }

    /// <summary>Адрес согласия Google — фронт уводит на него окно.</summary>
    [HttpGet("youtube/auth-url")]
    public IActionResult YouTubeAuthUrl()
    {
        var userId = UserId;
        if (userId is null) return Unauthorized();
        if (!options.YouTube.IsConfigured) return BadRequest(new { error = "not-configured" });

        var url = youtubeOauth.BuildAuthUrl(userId, RedirectUri(), options.YouTube);
        return Ok(new { url });
    }

    /// <summary>
    /// Возврат от Google. Анонимный осознанно: это переход браузера, нашего токена в нём нет —
    /// владельца опознаём по одноразовому state, выданному при старте входа.
    /// Возвращаем в «Чаты»: отдельного раздела «Видео» нет, каталог живёт панелью рельсы.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("youtube/callback")]
    public async Task<IActionResult> YouTubeCallback(
        [FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            return Redirect("/#/chats?connect=failed");

        var ownerId = await youtubeOauth.CompleteAsync(state, code, options.YouTube, ct);
        if (ownerId is null)
        {
            log.LogInformation("Возврат YouTube OAuth с неизвестным state — вход не завершён");
            return Redirect("/#/chats?connect=failed");
        }

        return Redirect("/#/chats?connect=ok");
    }

    [HttpPost("youtube/disconnect")]
    public async Task<IActionResult> YouTubeDisconnect(CancellationToken ct)
    {
        var userId = UserId;
        if (userId is null) return Unauthorized();
        await youtubeOauth.DisconnectAsync(userId, ct);
        return Ok(new { ok = true });
    }

    /// <summary>
    /// Адрес возврата. Из конфига, иначе собран из текущего запроса — за реверс-прокси
    /// второй вариант может разойтись с зарегистрированным в Google, поэтому на бою
    /// значение задают явно.
    /// </summary>
    private string RedirectUri()
    {
        if (!string.IsNullOrWhiteSpace(options.YouTube.RedirectUri))
            return options.YouTube.RedirectUri;

        // Host приходит из заголовка запроса: за реверс-прокси он легко расходится с тем,
        // что зарегистрировано в консоли Google, и человек получает redirect_uri_mismatch
        // без единой подсказки, откуда взялся адрес. Пишем его в лог явно.
        var guessed = $"{Request.Scheme}://{Request.Host}/api/video/youtube/callback";
        log.LogInformation(
            "Video:YouTube:RedirectUri не задан — адрес возврата собран из запроса: {Uri}. "
            + "Он обязан совпадать с зарегистрированным в Google Cloud Console.", guessed);
        return guessed;
    }

    private static object Wire(VideoChannel c) => new
    {
        id = c.Id,
        provider = c.ProviderKey,
        title = c.Title,
        embeddable = c.Embeddable,
        embedUrl = c.EmbedUrl,
        externalUrl = c.ExternalUrl,
        coverUrl = c.CoverUrl,
        nowPlaying = c.NowPlaying,
    };

    private static string? WireError(VideoFailure failure) => failure switch
    {
        VideoFailure.None => null,
        VideoFailure.NotConfigured => "not-configured",
        VideoFailure.NeedsAuth => "needs-auth",
        VideoFailure.QuotaExceeded => "quota-exceeded",
        VideoFailure.Unreachable => "unreachable",
        _ => "unreachable",
    };
}

/// <summary>Новый состав избранного целиком: PATCH-семантики нет, полоса шлёт весь список.</summary>
public record PutFavoritesRequest(List<string>? Keys);
