using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Продуктовая история — сводка изменений по ВСЕМ проектам (не привязана к одному).
/// «Что я или брат делали и чем это полезно».
/// </summary>
[ApiController]
[Authorize]
[Route("api/history")]
public class HistoryController(ChangelogService changelog, IConfiguration config) : ControllerBase
{
    // Статус настройки источника — чтобы фронт отличил «не настроено» от «пусто»
    [HttpGet("status")]
    public IActionResult GetStatus() => Ok(changelog.GetStatus());

    // Список дней с коммитами за окно — мгновенно, без LLM
    [HttpGet("days")]
    public IActionResult GetDays([FromQuery] int sinceDays = 0)
    {
        if (sinceDays <= 0)
            sinceDays = int.TryParse(config["Changelog:DefaultDays"], out var d) ? d : 30;
        return Ok(changelog.GetDays(Math.Min(sinceDays, 3650)));
    }

    // Продуктовая сводка одного дня (кеш либо генерация через Claude)
    [HttpGet("day/{date}")]
    public async Task<IActionResult> GetDay(string date)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(date, @"^\d{4}-\d{2}-\d{2}$"))
            return BadRequest(new { error = "Дата должна быть в формате yyyy-MM-dd" });
        return Ok(await changelog.GetDay(date));
    }

    // Сколько коммитов во всех проектах появилось после since (ISO-8601) — для бейджа
    [HttpGet("new-count")]
    public IActionResult GetNewCount([FromQuery] string? since)
    {
        if (!DateTimeOffset.TryParse(since, out var sinceDate))
            return Ok(new { count = 0 });
        return Ok(new { count = changelog.GetNewCommitCount(sinceDate) });
    }

    // Сбросить кеш одного дня — следующий GetDay сгенерит сводку заново (кнопка «перегенерировать»)
    [HttpDelete("day/{date}")]
    public IActionResult InvalidateDay(string date)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(date, @"^\d{4}-\d{2}-\d{2}$"))
            return BadRequest(new { error = "Дата должна быть в формате yyyy-MM-dd" });
        changelog.InvalidateDay(date);
        return NoContent();
    }

    // Очистить всю продуктовую историю (весь кеш) — кнопка «очистить историю»
    [HttpDelete]
    public IActionResult ClearAll()
    {
        changelog.ClearAll();
        return NoContent();
    }
}
