using ClaudeHomeServer.Services.Llm.Claude;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Диагностика прогонов сабагентов: чем кончился каждый (отчёт или обрыв на середине), сколько
/// прожил, сколько сделал вызовов инструментов, какое было окно контекста и на каком инструменте
/// замолчал. Отвечает на вопрос «сабагенты отваливаются — где именно граница» цифрами вместо
/// ручного разбора транскриптов agent-*.jsonl (до этого другого следа не было).
///
/// Только админ: данные охватывают всех владельцев (типы агентов, id сессий).
/// Паспорта живут в памяти процесса и обнуляются рестартом — это диагностика, не аудит.
/// </summary>
[ApiController]
[Authorize(Roles = "admin")]
[Route("api/subagents")]
public class SubagentRunsController(SubagentRunLog runs) : ControllerBase
{
    [HttpGet("runs")]
    public IActionResult Get([FromQuery] int limit = 50) => Ok(new
    {
        byType = runs.Stats(),
        recent = runs.Recent(limit),
    });
}
