using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services.Watchdog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Снапшот активных сторожей владельца (визуализация сторожей): фронт рисует значки
// сторожа по нему при загрузке, дальше живёт на событии watchdogs_changed — без поллинга.
[ApiController, Authorize, Route("api/watchdogs")]
public class WatchdogsController(WatchdogNotifier notifier) : ControllerBase
{
    // Текущий пользователь из JWT sub claim
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    /// <summary>GET /api/watchdogs — чаты и проекты владельца с активными сторожами.
    /// Проекция в { sessions, projects }: унаследованный ServerMessage.SessionId в REST
    /// ответ не отдаём.</summary>
    [HttpGet]
    public ActionResult<object> GetSnapshot()
    {
        var snapshot = notifier.Snapshot(UserId);
        return Ok(new { sessions = snapshot.Sessions, projects = snapshot.Projects });
    }
}
