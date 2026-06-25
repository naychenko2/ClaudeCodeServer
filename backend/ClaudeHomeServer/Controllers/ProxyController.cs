using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
public class ProxyController(IHttpClientFactory httpClientFactory) : ControllerBase
{
    // Белый список доменов — только доверенные внешние сервисы
    private static readonly string[] AllowedHosts =
    [
        "fal.media", "fal.run", "queue.fal.run", "cdn.fal.ai",
        "storage.googleapis.com", "replicate.delivery", "pbxt.replicate.delivery",
    ];

    /// <summary>
    /// Универсальный прокси: загружает любой контент (изображение, видео и т.п.)
    /// по URL и отдаёт клиенту с оригинальным Content-Type.
    /// Пробрасывает Range-заголовок, чтобы браузер мог перематывать видео.
    /// </summary>
    [HttpGet("/api/proxy")]
    public async Task Proxy([FromQuery] string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            Response.StatusCode = 400;
            await Response.WriteAsync("Некорректный URL", ct);
            return;
        }

        if (uri.Scheme != "https")
        {
            Response.StatusCode = 400;
            await Response.WriteAsync("Только HTTPS", ct);
            return;
        }

        if (!AllowedHosts.Any(h =>
                uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase)))
        {
            Response.StatusCode = 400;
            await Response.WriteAsync("Домен не разрешён", ct);
            return;
        }

        var client = httpClientFactory.CreateClient("proxy");
        var upstream = new HttpRequestMessage(HttpMethod.Get, url);

        // Пробрасываем Range для поддержки seek в видео
        if (Request.Headers.TryGetValue("Range", out var rangeHeader))
            upstream.Headers.TryAddWithoutValidation("Range", rangeHeader.ToString());

        HttpResponseMessage upstreamResponse;
        try
        {
            upstreamResponse = await client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            Response.StatusCode = 502;
            await Response.WriteAsync(ex.Message, ct);
            return;
        }

        Response.StatusCode = (int)upstreamResponse.StatusCode;
        Response.ContentType = upstreamResponse.Content.Headers.ContentType?.ToString()
                               ?? "application/octet-stream";

        if (upstreamResponse.Content.Headers.ContentLength is long len)
            Response.ContentLength = len;

        // Заголовки для корректной поддержки Range-запросов
        if (upstreamResponse.Headers.Contains("Accept-Ranges"))
            Response.Headers.Append("Accept-Ranges",
                string.Join(", ", upstreamResponse.Headers.GetValues("Accept-Ranges")));
        if (upstreamResponse.Content.Headers.Contains("Content-Range"))
            Response.Headers.Append("Content-Range",
                string.Join(", ", upstreamResponse.Content.Headers.GetValues("Content-Range")));

        await upstreamResponse.Content.CopyToAsync(Response.Body, ct);
    }

    // Обратная совместимость — /api/proxy/image перенаправляем на /api/proxy
    [HttpGet("/api/proxy/image")]
    public Task Image([FromQuery] string url, CancellationToken ct) => Proxy(url, ct);
}
