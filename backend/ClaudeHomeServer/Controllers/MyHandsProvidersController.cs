using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Провайдеры, которым владелец доверил руки локальных проектов (ADR-016 §7). Список
// per-owner, а не админский «Поставщики моделей»: провайдер видит снимки и текст окон, и
// решать, кому это отдать, может только владелец данных. Пустой список — руки выключены
// во всех проектах: разрешение явное, по умолчанию его нет.
[ApiController]
[Authorize]
[Route("api/me/hands-providers")]
public class MyHandsProvidersController(UserStore users, LlmProviderRegistry providers,
    FeatureFlagService flags, SessionManager sessions) : ControllerBase
{
    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    // Кого можно отметить: родной Claude и включённые сторонние провайдеры. Зрение — пометка
    // для человека («видит снимки окон» / «видит только текст окон»).
    private List<HandsProviderOption> Available() =>
    [
        new(HandsProviders.Claude, "Claude", SupportsImages: true),
        .. providers.Enabled.Select(p => new HandsProviderOption(p.Key, p.DisplayName, p.SupportsImages)),
    ];

    [HttpGet]
    public IActionResult Get()
    {
        if (UserId is null) return Unauthorized();
        return Ok(new HandsProvidersDto([.. users.GetHandsProviders(UserId)], Available()));
    }

    // Полная замена списка. Незнакомые ключи отбрасываются молча, порядок — как в Available.
    // Правка меняет состав хода — адаптеры живых чатов владельца пересоздаются лениво.
    [HttpPut]
    public IActionResult Put([FromBody] PutHandsProvidersRequest req)
    {
        if (UserId is null) return Unauthorized();
        if (req?.Providers is null) return BadRequest(new { error = "providers обязателен" });
        var available = Available();
        var requested = req.Providers.Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Без флага список можно только очистить: разрешать руки выключенной фиче незачем
        if (requested.Count > 0 && !flags.IsEnabled(UserId, FeatureFlagKeys.LocalHands))
            return BadRequest(new { error = ProjectCapabilities.HandsFlagOffReason });

        List<string> clean = [.. available.Select(o => o.Key).Where(requested.Contains)];
        if (!users.SetHandsProviders(UserId, clean)) return Unauthorized();
        sessions.InvalidateHandsSessions(UserId, projectId: null);
        return Ok(new HandsProvidersDto(clean, available));
    }
}

public record HandsProviderOption(string Key, string DisplayName, bool SupportsImages);
public record HandsProvidersDto(List<string> Providers, List<HandsProviderOption> Available);
public record PutHandsProvidersRequest(List<string>? Providers);
