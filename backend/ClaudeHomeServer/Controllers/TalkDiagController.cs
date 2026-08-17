using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Приём диагностических дампов режима разговора с устройств (телефон/планшет,
// где консоль недоступна). Фронт (lib/talkDiag.ts) при остановке петли шлёт дамп
// сюда; запись попадает в серверный лог с тегом [talk-diag] и id юзера — дальше
// её видно в обычных логах инстанса (docker logs / файловый лог).
//
// Эндпоинт сознательно "только записать": читать чужие дампы никуда не отдаём,
// UI не строим — это временная диагностика для расследования глухоты
// распознавания на части Android-устройств.
[ApiController]
[Authorize]
[Route("api/tts/diag")]
public class TalkDiagController(ILogger<TalkDiagController> logger) : ControllerBase
{
    // Верхняя граница дампа: фронтовый кольцевой буфер — 400 записей, но на всякий
    // случай режем и на сервере, чтобы лог не раздувался злонамеренным запросом
    public const int MaxDumpLength = 64_000;

    [HttpPost]
    public IActionResult Upload([FromBody] TalkDiagRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Dump))
            return BadRequest(new { error = "Пустой дамп" });

        var dump = req.Dump.Length > MaxDumpLength ? req.Dump[..MaxDumpLength] : req.Dump;
        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "?";

        // Одна запись в лог: префикс [talk-diag] ищется grep'ом. Дамп многострочный —
        // ILogger сам склеит переносы, читаемость в docker logs приемлемая
        logger.LogInformation("[talk-diag] user={UserId} ver={Version}\n{Dump}", userId, req.Version ?? "-", dump);
        return Ok(new { ok = true });
    }
}

public record TalkDiagRequest(string? Dump, string? Version);
