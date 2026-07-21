using ClaudeHomeServer.Services.Llm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// AI-хаб: локальное (бесплатное) ранжирование действий через Ollama. Тонкий прокси —
// фронт собирает компактный контекст открытой сущности и список доступных действий,
// бэкенд прогоняет через модель и возвращает уровни. Ollama не сконфигурирован →
// caps.ollama=false и фронт работает на rule-based механизме.
[ApiController]
[Authorize]
[Route("api/ai")]
public class AiController : ControllerBase
{
    private readonly OllamaActionRankService _rank;

    public AiController(OllamaActionRankService rank) => _rank = rank;

    // Сконфигурирована ли локальная модель — фронт заранее выбирает путь (LLM vs правила).
    [HttpGet("caps")]
    public ActionResult<AiCapsDto> Caps() => Ok(new AiCapsDto(_rank.Enabled));

    // Отранжировать доступные действия по контексту. available=false — Ollama выключен
    // (фронт → альтернативный механизм); ranked пуст при недоступности или «ничего не уместно».
    [HttpPost("suggest-actions")]
    public async Task<ActionResult<SuggestActionsResponse>> SuggestActions(
        [FromBody] SuggestActionsRequest req, CancellationToken ct)
    {
        if (!_rank.Enabled)
            return Ok(new SuggestActionsResponse(false, []));
        if (req.Actions is null || req.Actions.Count == 0)
            return Ok(new SuggestActionsResponse(true, []));

        var candidates = req.Actions
            .Where(a => !string.IsNullOrEmpty(a.Id))
            .Select(a => new RankCandidate(a.Id, a.Title ?? "", a.Hint ?? ""))
            .ToList();
        var maxK = req.MaxK is > 0 and <= 8 ? req.MaxK.Value : 3;

        var ranked = await _rank.RankAsync(req.ContextType ?? "", req.ContextText ?? "", candidates, maxK, ct);
        return Ok(new SuggestActionsResponse(true, ranked.Select(r => new RankedActionDto(r.Id, r.Level)).ToList()));
    }
}

public sealed record AiCapsDto(bool Ollama);

public sealed record SuggestActionCandidateDto(string Id, string? Title, string? Hint);
public sealed record SuggestActionsRequest(
    string? ContextType, string? ContextText, List<SuggestActionCandidateDto>? Actions, int? MaxK);

public sealed record RankedActionDto(string Id, string Level);
public sealed record SuggestActionsResponse(bool Available, List<RankedActionDto> Ranked);
