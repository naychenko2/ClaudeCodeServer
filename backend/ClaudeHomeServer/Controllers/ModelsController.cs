using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/models")]
public class ModelsController(ModelCatalogService catalog, ILlmSessionAdapterFactory adapters) : ControllerBase
{
    // Актуальный список моделей (Claude — из CLI с кэшем; DeepSeek — из конфига, только при ApiKey)
    // + возможности провайдеров, чтобы UI скрывал недоступное (план, effort, compact).
    // DeepSeek в providers — только когда провайдер настроен (иначе UI его не предлагает).
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var providers = new Dictionary<string, LlmCapabilities>
        {
            [LlmCapabilitiesCatalog.Claude.Provider] = LlmCapabilitiesCatalog.Claude,
        };
        if (adapters.IsProviderAvailable(LlmProvider.DeepSeek))
            providers[LlmCapabilitiesCatalog.DeepSeek.Provider] = LlmCapabilitiesCatalog.DeepSeek;

        return Ok(new { models = await catalog.GetModelsAsync(ct), providers });
    }
}
