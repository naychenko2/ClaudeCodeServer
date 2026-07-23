using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Modules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Список подключённых внешних модулей для оболочки (R6): вкладки и remote-загрузка.
/// Отдаются только модули, включённые фич-флагом module-{id} у текущего юзера (R8).
/// Сам трафик к модулям идёт мимо контроллера — через gateway /api/modules/{id}/** (YARP).
/// </summary>
[ApiController]
[Authorize]
[Route("api/modules")]
public class ModulesController(ModuleRegistry registry, FeatureFlagService flags) : ControllerBase
{
    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    [HttpGet]
    public IActionResult List()
    {
        if (UserId is null) return Unauthorized();
        var items = registry.All
            .Where(m => flags.IsEnabled(UserId, m.FeatureFlagKey))
            .Select(m => new
            {
                id = m.Id,
                displayName = m.Manifest.DisplayName,
                description = m.Manifest.Description,
                version = m.Manifest.Version,
                schemaVersion = m.Manifest.SchemaVersion,
                apiBase = m.Manifest.Backend!.RoutePrefix,
                tab = m.Manifest.Frontend?.Tab is { } tab
                    ? new { label = tab.Label, icon = tab.Icon, order = tab.Order }
                    : null,
                // ?v={version} — cache-busting immutable-статики remoteEntry (§7)
                remoteEntry = m.Manifest.Frontend?.RemoteEntry is { } entry
                    ? $"{entry}{(entry.Contains('?') ? '&' : '?')}v={Uri.EscapeDataString(m.Manifest.Version)}"
                    : null,
                exposedModule = m.Manifest.Frontend?.ExposedModule,
            })
            .ToList();
        return Ok(new { items });
    }
}
