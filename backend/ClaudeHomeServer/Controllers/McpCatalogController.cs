using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp;
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
public class McpCatalogController(McpCatalogClient catalog, McpRegistry registry,
    McpCatalogOptions catalogOptions, FeatureFlagService flags,
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

    // Ревизия импортированных записей (волна 2): сверка CatalogRef с живым реестром —
    // «отозван ли сервер, есть ли версия новее». Эндпоинт ОТДЕЛЬНЫЙ от списка записей:
    // лежащий реестр не должен задерживать или ронять открытие раздела, поэтому список
    // его не ждёт, а фронт зовёт ревизию после отрисовки. Беды проверки — по элементу
    // ответа (checkFailed), не статусом запроса: один битый ответ не роняет соседние.
    [HttpPost("revision")]
    public async Task<IActionResult> Revision([FromBody] McpCatalogRevisionRequest req,
        CancellationToken ct)
    {
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.McpCatalog))
            return NotFound(new { error = "Каталог MCP-серверов выключен" });
        if (!catalog.IsEnabled)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Каталог MCP-серверов не настроен на этом сервере" });

        var wanted = (req.Names ?? [])
            .Select(n => n?.Trim() ?? "")
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (wanted.Count == 0)
            return BadRequest(new { error = "Не заданы имена записей каталога" });
        if (wanted.Count > catalogOptions.RevisionBatchLimit)
            return BadRequest(new
            {
                error = $"Слишком много записей для одной проверки (не больше {catalogOptions.RevisionBatchLimit})",
            });

        // Ревизия идёт только по записям ЭТОГО владельца с CatalogRef: чужие имена
        // молча выпадают из ответа (изоляция), ручные записи не участвуют вовсе.
        // Импортированная версия для сверки — старшая среди записей одного имени:
        // «есть новее» значит «новее всего, что подключено»
        var wantedSet = wanted.ToHashSet(StringComparer.Ordinal);
        var imported = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var record in registry.GetByOwner(UserId))
        {
            var name = record.CatalogRef?.Name?.Trim();
            if (name is null || name.Length == 0 || !wantedSet.Contains(name)) continue;
            imported[name] = McpCatalogSemVer.MaxBySemVer(imported.GetValueOrDefault(name),
                record.CatalogRef!.Version);
        }

        var queries = wanted.Where(imported.ContainsKey)
            .Select(n => new McpCatalogRevisionQuery(n, imported[n]))
            .ToList();
        var items = await catalog.ReviseAsync(queries, ct);
        return Ok(new { items });
    }
}

/// <summary>
/// Запрос ревизии: имена записей каталога (как в McpCatalogRef.Name). Сервер проверяет
/// только те из них, что есть у владельца с CatalogRef, — остальное молча игнорируется.
/// </summary>
public record McpCatalogRevisionRequest(List<string?>? Names);
