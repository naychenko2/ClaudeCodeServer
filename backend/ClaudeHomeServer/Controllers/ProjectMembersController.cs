using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Участники общего проекта (shared-projects, S1-2): добавить по точному username, сменить
/// роль, убрать, покинуть. Посторонний получает 404 от <c>[ProjectScope]</c>, участник
/// без права управлять — 403 от сервиса. Списка учёток и поиска по части имени нет.
/// </summary>
[ProjectCapability(ProjectCapabilityArea.Platform, ProjectKey = "id")]
[ProjectScope(Key = "id")]
[ApiController]
[Authorize]
[Route("api/projects/{id}/members")]
public class ProjectMembersController(ProjectMembershipService membership) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    public sealed record AddMemberRequest(string? Username, string? Role);
    public sealed record ChangeRoleRequest(string? Role);

    [HttpPost]
    public async Task<IActionResult> Add(string id, [FromBody] AddMemberRequest req, CancellationToken ct)
    {
        if (!TryRole(req.Role, out var role)) return Result(InvalidRole);
        return Result(await membership.AddAsync(UserId, id, req.Username, role, ct));
    }

    [HttpPut("{userId}")]
    public IActionResult ChangeRole(string id, string userId, [FromBody] ChangeRoleRequest req)
    {
        if (!TryRole(req.Role, out var role)) return Result(InvalidRole);
        return Result(membership.ChangeRole(UserId, id, userId, role));
    }

    [HttpDelete("{userId}")]
    public IActionResult Remove(string id, string userId) =>
        Result(membership.Remove(UserId, id, userId));

    // Покинуть проект: единственная операция, доступная не владельцу
    [HttpDelete("me")]
    public IActionResult Leave(string id) => Result(membership.Leave(UserId, id));

    private static readonly MembershipResult InvalidRole =
        new(400, "invalid_role", "Роль участника — editor или reader.");

    private static bool TryRole(string? value, out ProjectRole role)
    {
        role = value?.ToLowerInvariant() switch
        {
            "editor" => ProjectRole.Editor,
            "reader" => ProjectRole.Reader,
            _ => ProjectRole.Owner,
        };
        return role != ProjectRole.Owner;
    }

    private IActionResult Result(MembershipResult r) =>
        r.Ok
            ? Ok(new { members = (r.Project!.Members ?? []).Select(m => new { userId = m.UserId, role = m.Role.ToString().ToLowerInvariant() }) })
            : StatusCode(r.Status, new { error = r.Error, code = r.Code });
}
