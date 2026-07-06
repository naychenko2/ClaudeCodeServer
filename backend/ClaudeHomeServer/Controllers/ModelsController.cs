using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/models")]
public class ModelsController(ModelCatalogService catalog) : ControllerBase
{
    // Актуальный список моделей (Claude — из CLI с кэшем; DeepSeek — из конфига)
    // + возможности провайдеров, чтобы UI скрывал недоступное (план, effort, compact)
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
        => Ok(new
        {
            models = await catalog.GetModelsAsync(ct),
            providers = new Dictionary<string, LlmCapabilities>
            {
                [LlmCapabilitiesCatalog.Claude.Provider] = LlmCapabilitiesCatalog.Claude,
                [LlmCapabilitiesCatalog.DeepSeek.Provider] = LlmCapabilitiesCatalog.DeepSeek,
            },
        });
}
