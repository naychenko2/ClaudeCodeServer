using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using RemoteStaticFiles = ClaudeHomeServer.Services.DynamicModules.RemoteStaticFiles;
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
public class ModulesController(ModuleRegistry registry, FeatureFlagService flags, RemoteStaticFiles remoteFiles) : ControllerBase
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
                // ?v= — сброс кэша remoteEntry (§7): хеш файла, если он лежит в нашей статике,
                // иначе версия манифеста (remote за gateway модуля на нашем диске не лежит)
                remoteEntry = m.Manifest.Frontend?.RemoteEntry is { } entry
                    ? RemoteStaticFiles.WithVersion(entry, remoteFiles.ContentVersion(entry) ?? m.Manifest.Version)
                    : null,
                exposedModule = m.Manifest.Frontend?.ExposedModule,
            })
            .ToList();
        return Ok(new { items });
    }
}
