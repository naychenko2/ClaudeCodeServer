using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Быстрые фразы композера: готовые сообщения, уходящие в чат одним нажатием.
// Набор один на все чаты владельца (не на проект) — конвенция per-user настроек
// повторяет /api/me/wall (MyWallController) и /api/me/model-tiers.
[ApiController]
[Authorize]
[Route("api/me/quick-phrases")]
public class MyQuickPhrasesController(UserStore users) : ControllerBase
{
    // Потолок набора: попап со списком длиннее двух десятков строк перестаёт быть
    // «быстрым», а безлимитный список — дорога к разбуханию users.json.
    private const int MaxPhrases = 24;
    // Фраза — сообщение в чат, а не роман: длинный текст пишут руками в поле.
    private const int MaxLength = 500;

    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    [HttpGet]
    public IActionResult Get()
    {
        if (UserId is null) return Unauthorized();
        return Ok(new QuickPhrasesDto([.. users.GetQuickPhrases(UserId)]));
    }

    // Полная замена набора. Валидация молчаливая (как у стены): обрезка пробелов →
    // отброс пустых → дедуп без учёта регистра → обрезка длины → потолок.
    // 400 — только на отсутствующее тело.
    [HttpPut]
    public IActionResult Put([FromBody] PutQuickPhrasesRequest req)
    {
        if (UserId is null) return Unauthorized();
        if (req?.Phrases is null) return BadRequest(new { error = "phrases обязателен" });

        var clean = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in req.Phrases.Take(500))
        {
            var text = (raw ?? "").Trim();
            if (text.Length == 0) continue;
            if (text.Length > MaxLength) text = text[..MaxLength];
            if (!seen.Add(text)) continue;
            clean.Add(text);
            if (clean.Count >= MaxPhrases) break;
        }

        if (!users.SetQuickPhrases(UserId, clean)) return Unauthorized();
        return Ok(new QuickPhrasesDto(clean));
    }
}

public record QuickPhrasesDto(List<string> Phrases);
public record PutQuickPhrasesRequest(List<string?>? Phrases);
