using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// «Стена» (фича wall): per-user набор чатов из разных проектов, открываемых колонками
// рядом. Конвенция per-user настроек — как /api/me/model-tiers (MyModelTiersController).
// Хранение — User.WallChatIds через UserStore (единственная точка записи users.json).
[ApiController]
[Authorize]
[Route("api/me/wall")]
public class MyWallController(UserStore users, SessionManager sessions) : ControllerBase
{
    // Потолок набора: монет на рельсе больше пары десятков не разглядеть, а неограниченный
    // список — дорога к разбуханию users.json от забытых стен.
    private const int MaxChats = 24;

    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    // Состав стены — ПОЛНЫЕ Session в порядке набора (фронту не нужен ни резолв по id,
    // ни N+1). Ленивая фильтрация мёртвых id: чат удалён или протух по ExpiresAfterMinutes —
    // молча выпадает из ответа; users.json при чтении не мутируем (чистка при PUT).
    [HttpGet]
    public IActionResult Get()
    {
        if (UserId is null) return Unauthorized();
        return Ok(new WallDto(ResolveChats(users.GetWallChatIds(UserId), UserId)));
    }

    // Полная замена состава. Валидация молчаливая, без кодов ошибок: дедуп → отброс
    // чужих/несуществующих id (гонка «чат удалили, пока стена открыта» не должна ронять
    // сохранение) → обрезка до потолка. 400 — только на отсутствующее тело.
    [HttpPut]
    public IActionResult Put([FromBody] PutWallRequest req)
    {
        if (UserId is null) return Unauthorized();
        if (req?.ChatIds is null) return BadRequest(new { error = "chatIds обязателен" });

        // Take ДО резолва — гигантский массив id не должен гонять GetOwned сотни тысяч
        // раз; запас над потолком, чтобы мёртвые/чужие id в голове списка не съели живых
        var live = ResolveChats(req.ChatIds.Take(500), UserId).Take(MaxChats).ToList();
        if (!users.SetWallChatIds(UserId, live.Select(s => s.Id).ToList()))
            return Unauthorized();

        return Ok(new WallDto(live));
    }

    // Кандидаты для пикера: чаты владельца (проектные и вне проектов), свежие сверху.
    // Потолок 200 — защита от стен текста у старых аккаунтов (у пикера есть поиск,
    // но фильтрует он то, что приехало; чат старше двух сотен на стену не позовут).
    [HttpGet("candidates")]
    public IActionResult Candidates()
    {
        if (UserId is null) return Unauthorized();
        return Ok(sessions.GetAllOwnedBy(UserId).OrderByDescending(s => s.UpdatedAt).Take(200).ToList());
    }

    // id → живые Session владельца, с дедупликацией и сохранением порядка
    private List<Session> ResolveChats(IEnumerable<string> chatIds, string ownerId)
    {
        var result = new List<Session>();
        var seen = new HashSet<string>();
        foreach (var id in chatIds)
        {
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
            var s = sessions.GetOwned(id, ownerId);
            if (s is not null) result.Add(s);
        }
        return result;
    }
}

public record WallDto(List<Session> Chats);
public record PutWallRequest(List<string>? ChatIds);
