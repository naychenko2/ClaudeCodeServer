using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Лёгкий health-эндпоинт для проверки достижимости сервера (heartbeat/probe фронта).
// Анонимный и максимально дешёвый: важен сам факт ответа, а не тело. Не под rate-limit —
// фронт пингует его регулярно, пока вкладка активна.
[ApiController]
[Route("api/health")]
public class HealthController : ControllerBase
{
    [AllowAnonymous]
    [HttpGet]
    [HttpHead]
    public IActionResult Get() => NoContent();
}
