using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Spend;
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
//   400 — пустой текст, длиннее лимита ИЛИ явно переданный голос/амплуа не из белого
//       списка (прослушивание в форме: там ошибку надо показать, а не подменить дефолтом).
// Рейт-лимита нет осознанно (Р9 плана): эндпоинт под [Authorize], ограничитель — длина текста.
[ApiController]
[Authorize]
[Route("api/tts")]
public class TtsController(YandexTtsService tts, VoiceResolver voices,
    SessionManager sessions, ISpendCollector spend) : ControllerBase
{
    // Лимит запроса фронта; заодно верхняя граница расхода на один синтез (тарификация по запросам)
    public const int MaxTextLength = 3000;

    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Голоса для формы выбора: канонические имена (алиасы наружу не выходят — один голос
    // не должен давать в списке две строки), подпись, пол и доступные амплуа.
    // configured=false — синтеза на сервере нет: форме нечего предлагать слушать, и
    // честная плашка лучше живых кнопок, которые молча не работают.
    [HttpGet("voices")]
    public ActionResult<TtsVoicesResponse> Voices() => Ok(new TtsVoicesResponse(
        tts.IsConfigured,
        TtsVoiceCatalog.All.Select(v => new TtsVoiceDto(
            v.Voice, v.Label, v.Gender.ToString().ToLowerInvariant(), v.Roles)).ToList()));

    [HttpPost]
    public async Task<IActionResult> Synthesize([FromBody] TtsRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { error = "Текст для синтеза пуст" });
        if (req.Text.Length > MaxTextLength)
            return BadRequest(new { error = $"Текст длиннее {MaxTextLength} символов" });

        // Явный голос проверяем ДО настроенности синтеза: иначе опечатка в форме вернула бы
        // 503 «синтез не настроен» и увела диагностику не туда
        var picked = new PersonaVoice { Voice = req.Voice, Role = req.Role, Speed = req.Speed };
        if (!picked.IsEmpty)
        {
            if (string.IsNullOrWhiteSpace(req.Voice))
                return BadRequest(new { error = "Амплуа и темп задаются вместе с голосом" });
            if (!TtsVoiceCatalog.IsKnown(req.Voice))
                return BadRequest(new { error = $"Неизвестный голос синтеза: {req.Voice}" });
            if (!string.IsNullOrWhiteSpace(req.Role) && !TtsVoiceCatalog.SupportsRole(req.Voice, req.Role))
                return BadRequest(new { error = $"Голос {req.Voice} не умеет «{req.Role}»" });
        }

        if (!tts.IsConfigured)
            return StatusCode(503, new { reason = "not_configured" });

        // Чужой или протухший personaId — не ошибка, а дефолтный голос: резолвер проверяет
        // владельца сам. Отвечать 400 нельзя, фронт уводит на голос браузера ОСТАТОК фразы,
        // то есть устаревший id в открытой вкладке стоил бы человеку куска озвучки
        var voice = voices.Resolve(req.PersonaId, UserId, picked);

        var res = await tts.SynthesizeAsync(req.Text, voice, ct);
        // Расход пишем ДО проверки успеха: принятые запросы оплачены и при провалившемся
        // синтезе тоже, а молчаливо потерянные деньги — худший вид неучтённых
        RecordSpend(req, voice, res);
        if (res.Audio is null)
            return StatusCode(502, new { reason = "upstream" });

        return File(res.Audio, "audio/mpeg");
    }

    // Трата на озвучку: SpeechKit тарифицируется ЗА ЗАПРОС, и сервис вернул ровно число
    // запросов, которые Яндекс принял. Ноль отбрасывает сам SpendStore — пустых записей он
    // не копит, так что отдельной проверки «а было ли что тратить» здесь не нужно.
    private void RecordSpend(TtsRequest req, VoiceChoice voice, TtsResult res)
    {
        if (res.BilledRequests <= 0) return;

        // Чат нужен только ради разрезов (проект, задача, персона). Чужой или протухший id —
        // не ошибка: озвучка уже состоялась, трата всё равно ложится на своего владельца,
        // просто без разрезов. Прослушивание голоса в карточке персоны идёт сюда же, но
        // чата у него нет вовсе — оно и будет строкой без чата.
        var session = req.SessionId is { Length: > 0 } sid ? sessions.GetById(sid) : null;
        if (session is not null && session.OwnerId != UserId) session = null;

        spend.Record(new SpendRecord
        {
            OwnerId = UserId,
            ProjectId = session?.ProjectId,
            SessionId = session?.Id,
            TaskId = session?.TaskId,
            // Только из чата: id из запроса не проверен на владельца, а чужая персона в
            // отчёте о МОИХ деньгах — ложь дороже пропущенного разреза
            PersonaId = session?.PersonaId,
            Provider = "yandex",
            Model = voice.Voice,
            Source = SpendSources.Tts,
            Generations = res.BilledRequests,
            CostRub = res.Rub,
            Label = voice.Voice,
        });
    }
}

// SessionId — чат, которому засчитать расход на озвучку (разрезы проект/задача/персона в
// аналитике трат). Не влияет ни на голос, ни на ответ: чужой, протухший или отсутствующий
// id просто оставляет трату без разрезов — старый фронт его не шлёт и работает как прежде.
// PersonaId — чьим голосом читать; null/неизвестный — голосом инстанса из конфига.
// Voice/Role/Speed — примеряемый голос для прослушивания в форме персоны: он сильнее
// PersonaId, потому что человек слушает то, что выбирает сейчас, а не сохранённое.
// Старый фронт полей не шлёт и получает прежнее поведение
public record TtsRequest(string? Text, string? PersonaId = null,
    string? Voice = null, string? Role = null, double? Speed = null, string? SessionId = null);

public record TtsVoiceDto(string Voice, string Label, string Gender, IReadOnlyList<string> Roles);

public record TtsVoicesResponse(bool Configured, IReadOnlyList<TtsVoiceDto> Voices);
