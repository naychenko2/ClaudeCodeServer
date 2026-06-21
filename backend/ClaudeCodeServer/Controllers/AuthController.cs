using Microsoft.AspNetCore.Mvc;
namespace ClaudeCodeServer.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    [HttpPost("ping")]
    public IActionResult Ping([FromBody] PingRequest req)
    {
        // Простая проверка — сохраняем серверный URL и API-ключ в сессии
        // В будущем здесь будет реальная валидация
        if (string.IsNullOrWhiteSpace(req.ServerUrl) || string.IsNullOrWhiteSpace(req.ApiKey))
            return BadRequest(new { error = "serverUrl и apiKey обязательны" });
        return Ok(new { ok = true });
    }
}
public record PingRequest(string ServerUrl, string ApiKey);
