using Microsoft.AspNetCore.Mvc;

namespace StubModule;

// Единственный эндпоинт заглушки. Без [Authorize] — анонимный GET, чтобы проверка
// «эндпоинт отвечает» не тянула JWT-аутентификацию. Маршрут не пересекается с роутами
// ядра и внешних (YARP) модулей, поэтому ApplicationPart модуля не создаёт конфликтов.
[ApiController]
[Route("api/stub-module")]
public class StubController : ControllerBase
{
    [HttpGet("ping")]
    public IActionResult Ping() => Ok(new { module = "__stub", ok = true });
}
