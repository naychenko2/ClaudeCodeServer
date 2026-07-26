using ClaudeHomeServer.Services.Mcp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Диагностика продуктовых MCP-серверов: сколько раз звали каждый инструмент, сколько отказов
/// и какими были последние. Отвечает на вопрос «инструменты отваливаются — что именно и когда»
/// без разбора истории чатов вручную (до этого другого следа на бэкенде не было).
///
/// Только админ: данные охватывают всех владельцев (имена инструментов и id сессий).
/// Счётчики живут в памяти процесса и обнуляются рестартом — это диагностика, не аудит.
/// </summary>
[ApiController]
[Authorize(Roles = "admin")]
[Route("api/mcp")]
public class McpCallsController(McpCallLog calls) : ControllerBase
{
    [HttpGet("calls")]
    public IActionResult Get([FromQuery] int failures = 50) => Ok(new
    {
        tools = calls.Stats(),
        recentFailures = calls.RecentFailures(failures),
    });
}
