using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Единый поиск по рабочему пространству (флаг unified-search): заметки + задачи.
[ApiController]
[Authorize]
[Route("api/search")]
public class SearchController(UnifiedSearchService search, FeatureFlagService flags) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SearchHit>>> Search(
        [FromQuery] string q, [FromQuery] int topK = 8)
    {
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.UnifiedSearch)) return Forbid();
        if (string.IsNullOrWhiteSpace(q)) return Ok(Array.Empty<SearchHit>());
        var hits = await search.SearchAsync(UserId, q.Trim(), Math.Clamp(topK, 1, 20));
        return Ok(hits);
    }
}
