using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/models")]
public class ModelsController(ModelCatalogService catalog) : ControllerBase
{
    // Актуальный список моделей Claude (из claude CLI, с кэшем на сервере)
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
        => Ok(new { models = await catalog.GetModelsAsync(ct) });
}
