using ClaudeHomeServer.Services.Tts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Синтез речи для голосового режима чата: фронт шлёт кусок текста, получает mp3.
// Коды ответа — контракт для фолбэка фронта (lib/tts.ts), менять осознанно:
//   503 { reason: "not_configured" } — ключ/folderId не заданы (ПОСТОЯННОЕ состояние:
//       фронт запоминает и уходит на speechSynthesis до конца сессии);
//   502 { reason: "upstream" } — Яндекс ответил ошибкой (403 без роли, 5xx) или не ответил
//       (таймаут). ВРЕМЕННОЕ состояние: фронт не запоминает, фолбэк только на этот раз;
//   400 — пустой текст или длиннее лимита.
// Рейт-лимита нет осознанно (Р9 плана): эндпоинт под [Authorize], ограничитель — длина текста.
[ApiController]
[Authorize]
[Route("api/tts")]
public class TtsController(YandexTtsService tts) : ControllerBase
{
    // Лимит запроса фронта; заодно верхняя граница расхода на один синтез (тарификация по символам)
    public const int MaxTextLength = 3000;

    [HttpPost]
    public async Task<IActionResult> Synthesize([FromBody] TtsRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { error = "Текст для синтеза пуст" });
        if (req.Text.Length > MaxTextLength)
            return BadRequest(new { error = $"Текст длиннее {MaxTextLength} символов" });

        if (!tts.IsConfigured)
            return StatusCode(503, new { reason = "not_configured" });

        var bytes = await tts.SynthesizeAsync(req.Text, ct);
        if (bytes is null)
            return StatusCode(502, new { reason = "upstream" });

        return File(bytes, "audio/mpeg");
    }
}

public record TtsRequest(string? Text);
