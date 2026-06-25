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
    /// </summary>
    [HttpGet("/api/proxy")]
    public async Task<IActionResult> Proxy([FromQuery] string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return BadRequest("Некорректный URL");

        if (uri.Scheme != "https")
            return BadRequest("Только HTTPS");

        if (!AllowedHosts.Any(h =>
                uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase)))
            return BadRequest("Домен не разрешён");

        var client = httpClientFactory.CreateClient("proxy");
        try
        {
            var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return StatusCode((int)response.StatusCode);

            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            var stream = await response.Content.ReadAsStreamAsync(ct);
            return File(stream, contentType);
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(502, ex.Message);
        }
    }

    // Обратная совместимость — /api/proxy/image перенаправляем на /api/proxy
    [HttpGet("/api/proxy/image")]
    public Task<IActionResult> Image([FromQuery] string url, CancellationToken ct) => Proxy(url, ct);
}
