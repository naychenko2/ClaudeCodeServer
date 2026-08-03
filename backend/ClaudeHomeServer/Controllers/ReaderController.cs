using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Reader;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Режим чтения ссылок (ADR-005): сервер сам идёт по внешнему URL и возвращает markdown статьи.
/// За флагом <see cref="FeatureFlagKeys.LinkReader"/> — гейтит эндпоинт, а не только кнопку.
/// </summary>
[ApiController]
[Authorize]
[Route("api/reader")]
public class ReaderController(ReaderService reader, ReaderQuotaService quota, FeatureFlagService flags) : ControllerBase
{
    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    [HttpPost("read")]
    public async Task<IActionResult> Read([FromBody] ReadRequest req, CancellationToken ct)
    {
        var userId = UserId;
        if (userId is null) return Unauthorized();
        if (!flags.IsEnabled(userId, FeatureFlagKeys.LinkReader)) return Forbid();

        if (string.IsNullOrWhiteSpace(req.Url))
            return Ok(new { error = new { code = "invalid-url", httpStatus = (int?)null } });

        if (!quota.TryAcquireRate(userId))
            return StatusCode(StatusCodes.Status429TooManyRequests);

        using var slot = quota.TryAcquireConcurrency(userId);
        if (slot is null)
            return StatusCode(StatusCodes.Status429TooManyRequests);

        var outcome = await reader.ReadAsync(req.Url, ct);

        if (!outcome.Success)
            return Ok(new { error = new { code = outcome.Error!.Value.ToWireName(), httpStatus = outcome.HttpStatus } });

        return Ok(new { title = outcome.Title, siteName = outcome.SiteName, byline = outcome.Byline, markdown = outcome.Markdown });
    }

    // Прокси картинок статьи через тот же SsrfGuard (продуктовое решение поверх ADR-005 —
    // без него браузер человека шёл бы на CDN сайта напрямую своим IP). Без квоты /read:
    // одна статья несёт много картинок, и общий счётчик чтений тут же исчерпался бы.
    [HttpGet("image")]
    public async Task<IActionResult> Image([FromQuery] string url, CancellationToken ct)
    {
        var userId = UserId;
        if (userId is null) return Unauthorized();
        if (!flags.IsEnabled(userId, FeatureFlagKeys.LinkReader)) return Forbid();
        if (string.IsNullOrWhiteSpace(url)) return BadRequest();

        var result = await reader.ReadImageAsync(url, ct);
        if (result is null) return StatusCode(StatusCodes.Status502BadGateway);

        Response.Headers.CacheControl = "no-store";
        return File(result.Value.Bytes, result.Value.ContentType);
    }
}

public record ReadRequest(string Url);
