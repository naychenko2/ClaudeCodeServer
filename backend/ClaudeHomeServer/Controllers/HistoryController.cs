using ClaudeHomeServer.Services.Changelog;
using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Продуктовая история — сводка изменений по ВСЕМ проектам (не привязана к одному).
/// «Что я или брат делали и чем это полезно».
/// Ручка вертикали changelog (привязана S2b-1): до атрибута строка матрицы была «вакуумно
/// зелёной» — эти эндпоинты никем не мерились, а при выключенном changelog давали 500.
/// </summary>
[ApiController]
[Authorize]
[Route("api/history")]
[SubsystemUsage("changelog")]
public class HistoryController(
    IConfiguration config,
    // `= null` — обязательность для DI (ADR-014, S2b-1): на выключенной вертикали
    // активация контроллера не должна падать, отказ отдаёт гейт
    ChangelogService? changelog = null) : ControllerBase
{
    // Гейт выключенной вертикали (та же форма, что TtsController): проверка остаётся в
    // вызывающем (`if (changelog is null) return ChangelogDisabled();`) — дальше по коду
    // компилятор знает, что сервис живой. Отказ — до любой работы.
    private IActionResult ChangelogDisabled() => StatusCode(503, new
    {
        error = "Подсистема «Что нового» выключена (Subsystems:changelog:Enabled=false)",
    });

    // Статус настройки источника — чтобы фронт отличил «не настроено» от «пусто»
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        if (changelog is null) return ChangelogDisabled();
        return Ok(changelog.GetStatus());
    }

    // Список дней с коммитами за окно — мгновенно, без LLM
    [HttpGet("days")]
    public IActionResult GetDays([FromQuery] int sinceDays = 0)
    {
        if (changelog is null) return ChangelogDisabled();
        if (sinceDays <= 0)
            sinceDays = int.TryParse(config["Changelog:DefaultDays"], out var d) ? d : 30;
        return Ok(changelog.GetDays(Math.Min(sinceDays, 3650)));
    }

    // Продуктовая сводка одного дня (кеш либо генерация через Claude)
    [HttpGet("day/{date}")]
    public async Task<IActionResult> GetDay(string date)
    {
        if (changelog is null) return ChangelogDisabled();
        if (!System.Text.RegularExpressions.Regex.IsMatch(date, @"^\d{4}-\d{2}-\d{2}$"))
            return BadRequest(new { error = "Дата должна быть в формате yyyy-MM-dd" });
        return Ok(await changelog.GetDay(date));
    }

    // Сколько коммитов во всех проектах появилось после since (ISO-8601) — для бейджа
    [HttpGet("new-count")]
    public IActionResult GetNewCount([FromQuery] string? since)
    {
        if (changelog is null) return ChangelogDisabled();
        if (!DateTimeOffset.TryParse(since, out var sinceDate))
            return Ok(new { count = 0 });
        return Ok(new { count = changelog.GetNewCommitCount(sinceDate) });
    }

    // Сбросить кеш одного дня — следующий GetDay сгенерит сводку заново (кнопка «перегенерировать»)
    [HttpDelete("day/{date}")]
    public IActionResult InvalidateDay(string date)
    {
        if (changelog is null) return ChangelogDisabled();
        if (!System.Text.RegularExpressions.Regex.IsMatch(date, @"^\d{4}-\d{2}-\d{2}$"))
            return BadRequest(new { error = "Дата должна быть в формате yyyy-MM-dd" });
        changelog.InvalidateDay(date);
        return NoContent();
    }

    // Очистить всю продуктовую историю (весь кеш) — кнопка «очистить историю»
    [HttpDelete]
    public IActionResult ClearAll()
    {
        if (changelog is null) return ChangelogDisabled();
        changelog.ClearAll();
        return NoContent();
    }
}
