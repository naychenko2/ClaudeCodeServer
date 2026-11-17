using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Deploy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Лёгкий health-эндпоинт для проверки достижимости сервера (heartbeat/probe фронта).
// Анонимный и максимально дешёвый: важен сам факт ответа, а не тело. Не под rate-limit —
// фронт пингует его регулярно, пока вкладка активна.
//
// Заголовок X-Build (ADR-010) — единственная добавка: по нему агент выкатки отличает
// поднявшийся НОВЫЙ экземпляр от старого. Контракт не меняется: тело по-прежнему пустое,
// код 204, авторизации нет; идентификатора нет — нет и заголовка.
[ApiController]
[Route("api/health")]
public class HealthController(BuildIdProvider build) : ControllerBase
{
    // Пинг ребёнка (хвост дельта-ревью R-KM-F10): фронт шлёт GET /api/health со своим токеном
    // (offline.ts → probeHealth), без метки гейт детского режима отвечал 403. Тело остаётся
    // пустым: здесь нет ни версий, ни путей, ни состава подсистем — только признак живого.
    // X-Build (ADR-010) — идентификатор экземпляра выкатки (UTC-штамп), он не меняется.
    [ChildAllowed]
    [AllowAnonymous]
    [HttpGet]
    [HttpHead]
    public IActionResult Get()
    {
        if (build.BuildId is { Length: > 0 } id)
            Response.Headers[BuildIdProvider.HeaderName] = id;
        return NoContent();
    }
}
