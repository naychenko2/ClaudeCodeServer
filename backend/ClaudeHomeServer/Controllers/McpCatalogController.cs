using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp.Catalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ClaudeHomeServer.Controllers;

// Каталог MCP-серверов (план «Каталог MCP-серверов», волна 1): поиск по официальному
// реестру registry.modelcontextprotocol.io. Только чтение — записи реестра владельца
// отсюда не создаются, заведение идёт обычным POST /api/mcp/servers.
// Потолок запросов на бэке: дебаунс фронта не защита, и без лимита зажатая кнопка
// поиска молотила бы внешний сервис.
[ApiController]
[Authorize]
[Route("api/mcp/catalog")]
[EnableRateLimiting("mcp-catalog")]
public class McpCatalogController(McpCatalogClient catalog, FeatureFlagService flags,
    ILogger<McpCatalogController> log) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] string? cursor,
        CancellationToken ct)
    {
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.McpCatalog))
            return NotFound(new { error = "Каталог MCP-серверов выключен" });
        // Пустой адрес = каталог выключен (второго рубильника нет): внятный отказ,
        // а не тишина и не 500 из глубины HttpClient
        if (!catalog.IsEnabled)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Каталог MCP-серверов не настроен на этом сервере" });

        try
        {
            var page = await catalog.SearchAsync(q ?? "", cursor, ct);
            return Ok(new { items = page.Items, nextCursor = page.NextCursor });
        }
        catch (McpCatalogUnavailableException ex)
        {
            // Каталог не роняет раздел: любая его беда — плашка, ручной путь работает
            log.LogWarning("Поиск по каталогу MCP не удался: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }
}
