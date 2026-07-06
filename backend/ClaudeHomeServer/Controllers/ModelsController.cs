using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/models")]
public class ModelsController(ModelCatalogService catalog, LlmProviderRegistry providers) : ControllerBase
{
    // Актуальный список моделей (Claude — из CLI с кэшем; CLI-провайдеры — из конфига
    // LlmProviders, только при ApiKey) + возможности провайдеров, чтобы UI скрывал
    // недоступное. Ненастроенные провайдеры не попадают в ответ — UI их не предлагает.
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var caps = new Dictionary<string, LlmCapabilities>
        {
            [LlmCapabilitiesCatalog.Claude.Provider] = LlmCapabilitiesCatalog.Claude,
        };
        foreach (var p in providers.Enabled)
            caps[p.Key] = LlmProviderRegistry.CapabilitiesOf(p);

        return Ok(new { models = await catalog.GetModelsAsync(ct), providers = caps });
    }
}
