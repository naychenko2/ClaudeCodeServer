using ClaudeHomeServer.Services.Composition;
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
///
/// Подсистема video отключаемая (ADR-014, S2a-2): при Subsystems:video:Enabled=false её
/// сервисы в контейнер не попадают, поэтому зависимости опциональные (= null), а весь поле
/// <c>/api/video</c> отвечает честный 503 с причиной вместо 500 активации.
/// </summary>
[ApiController]
[Authorize]
[Route("api/video")]
[SubsystemUsage("video")]
public class VideoController(
    UserStore users,
    ILogger<VideoController> log,
    // `= null` — обязательность для DI: без значения по умолчанию активация контроллера на
    // выключенной вертикали падала (500), а не отвечала гейтом (та же форма, что PluginsController)
    VideoProviderRegistry? registry = null,
    YouTubeOAuthService? youtubeOauth = null,
    VideoOptions? options = null) : ControllerBase
{
    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    /// <summary>
    /// Гейт выключенной вертикали (ADR-014, S2a-2): null-зависимость = подсистема выключена
    /// (её сервисы регистрируются только при открытом гейте). Отказываем всем маршрутам поля,
    /// включая избранное: ключи каналов знает каталог вертикали, без неё их хранить не для чего.
    /// Проверку оставляют в вызывающем (<c>if (registry is null || …) return VideoDisabled();</c>)
    /// — дальше по коду компилятор знает, что сервисы живые.
    /// </summary>
    private ActionResult VideoDisabled() => StatusCode(503, new
    {
        reason = "video_disabled",
        error = "Подсистема видео выключена (Subsystems:video:Enabled=false)",
    });

    /// <summary>Какие источники доступны и какой из них просит подключить аккаунт.</summary>
    [HttpGet("providers")]
    public async Task<IActionResult> Providers(CancellationToken ct)
    {
        if (registry is null || youtubeOauth is null || options is null) return VideoDisabled();

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
        if (registry is null || youtubeOauth is null || options is null) return VideoDisabled();

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
        if (registry is null || youtubeOauth is null || options is null) return VideoDisabled();

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
        if (registry is null || youtubeOauth is null || options is null) return VideoDisabled();

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
        if (registry is null || youtubeOauth is null || options is null) return VideoDisabled();

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
        if (registry is null || youtubeOauth is null || options is null) return VideoDisabled();

        var userId = UserId;
        if (userId is null) return Unauthorized();
        if (!options.YouTube.IsConfigured) return BadRequest(new { error = "not-configured" });

        var url = youtubeOauth.BuildAuthUrl(userId, RedirectUri(options), options.YouTube);
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
        if (registry is null || youtubeOauth is null || options is null) return VideoDisabled();

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
        if (registry is null || youtubeOauth is null || options is null) return VideoDisabled();

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
    // options приходит параметром: вызывающий уже прошёл гейт вертикали и знает, что он жив;
    // читать поле из nullable-поля класса компилятор права не имеет (CS8602)
    private string RedirectUri(VideoOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.YouTube.RedirectUri))
            return opts.YouTube.RedirectUri;

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
