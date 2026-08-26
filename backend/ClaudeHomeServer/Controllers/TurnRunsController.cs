using ClaudeHomeServer.Services.Llm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Диагностика ходов: чем кончился каждый, сколько попыток стоил, на какой паре
/// «модель × провайдер» встал и был ли виноват общий канал наружу, а не вендор.
///
/// Парная к <see cref="SubagentRunsController"/>: у прогонов сабагентов паспорт был, у самих
/// ходов — нет, и вопрос «что ломалось за сутки» решался раскопками по серверному логу,
/// лентам чатов и sessions.json (разбор 25.08.2026 занял час — ровно та боль, что здесь закрыта).
///
/// Только админ: данные охватывают всех владельцев (id чатов, модели, провайдеры).
/// В памяти — последние 300; полная история за сутки — в data/logs/turn-runs-*.jsonl.
/// </summary>
[ApiController]
[Authorize(Roles = "admin")]
[Route("api/turns")]
public class TurnRunsController(TurnRunLog runs) : ControllerBase
{
    [HttpGet("runs")]
    public IActionResult Get([FromQuery] int limit = 50) => Ok(new
    {
        summary = runs.Summary(),
        recent = runs.Recent(limit),
    });
}
