using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Deploy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ClaudeHomeServer.Controllers;

// Пересборка и переопубликация прода из чата (ADR-010).
//
// Только для админов, и это не формальность: ручка исполняет код на хосте под учёткой
// владельца — граница привилегий. Поэтому сервер сам ничего не собирает и не гасит, а
// проверяет guard'ы, пишет заявку в журнал и будит задачу планировщика; работу делает
// внешний агент, не состоящий с сервером в родстве (иначе трей убил бы его вместе
// с деревом процессов при остановке).
[ApiController]
[Route("api/deploy")]
[Authorize(Roles = "admin")]
public class DeployController(DeployService deploy, SessionManager sessions) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    public record StartRequest(string? Ref, bool? SkipFrontend, bool? SkipSandbox, bool? AllowDirty);

    public record RollbackRequest(string? ReleaseId);

    // Чат, из которого пришла заявка: в него новый инстанс доложит итог. Заголовок ставит
    // ход CLI; у запроса из UI его нет — тогда доклад придёт одним уведомлением.
    // Чужой чат в журнал не пишем: доклад ушёл бы в разговор другого владельца.
    private string? CallerSessionId
    {
        get
        {
            if (!Request.Headers.TryGetValue(Filters.DenyOnDelegatedTurnAttribute.CallerHeader, out var v)
                || v.FirstOrDefault() is not { Length: > 0 } id) return null;
            var session = sessions.GetById(id);
            return session is not null && sessions.ResolveOwnerId(session) == UserId ? id : null;
        }
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] StartRequest? req, CancellationToken ct)
    {
        var result = await deploy.StartAsync(
            new DeployStartRequest(
                req?.Ref,
                req?.SkipFrontend ?? false,
                req?.SkipSandbox ?? false,
                req?.AllowDirty ?? false),
            UserId, CallerSessionId, ct);

        return Respond(result);
    }

    [HttpPost("rollback")]
    public async Task<IActionResult> Rollback([FromBody] RollbackRequest? req, CancellationToken ct)
    {
        var result = await deploy.RollbackAsync(req?.ReleaseId, UserId, CallerSessionId, ct);
        return Respond(result);
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        var options = deploy.Options;
        var state = deploy.Load();
        return Ok(new
        {
            enabled = options.Enabled,
            state.Current,
            state.History,
            state.Releases,
        });
    }

    private IActionResult Respond(DeployStartResult result) => result.Status switch
    {
        DeployStartStatus.Accepted => Accepted(new
        {
            deployId = result.DeployId,
            // Переключение режет идущие ходы (ADR-010, «Что сознательно не делаем»):
            // предупреждаем заказчика, сколько их сейчас
            activeTurns = ActiveTurns(),
        }),
        DeployStartStatus.AlreadyRunning =>
            Conflict(new { error = result.Error, deployId = result.DeployId }),
        DeployStartStatus.DirtyTree =>
            BadRequest(new { error = result.Error, files = result.DirtyFiles ?? [] }),
        DeployStartStatus.InvalidRef => BadRequest(new { error = result.Error }),
        DeployStartStatus.NoRelease => BadRequest(new { error = result.Error }),
        DeployStartStatus.Disabled =>
            StatusCode(503, new { error = result.Error, reason = "not_configured" }),
        DeployStartStatus.Misconfigured =>
            StatusCode(503, new { error = result.Error, reason = "misconfigured" }),
        _ => StatusCode(500, new { error = result.Error, deployId = result.DeployId }),
    };

    private int ActiveTurns() =>
        sessions.GetAll().Count(s => s.Status is SessionStatus.Working or SessionStatus.Starting);
}
