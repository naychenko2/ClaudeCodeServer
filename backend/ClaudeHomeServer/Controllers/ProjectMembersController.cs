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
public class ProjectMembersController(ProjectMembershipService membership, UserStore users) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    public sealed record AddMemberRequest(string? Username, string? Role);
    public sealed record ChangeRoleRequest(string? Role);

    // Список доступен владельцу и участникам; посторонний получает 404 от [ProjectScope]
    [HttpGet]
    public IActionResult List(string id, [FromServices] ProjectManager projects)
    {
        var project = projects.GetById(id);
        return project is null ? NotFound() : Ok(ProjectMembersView.Full(project, UserId, users));
    }

    [HttpPost]
    public async Task<IActionResult> Add(string id, [FromBody] AddMemberRequest req, CancellationToken ct)
    {
        if (!TryRole(req.Role, out var role)) return Result(InvalidRole);
        return Result(await membership.AddAsync(UserId, id, req.Username, role, ct));
    }

    [HttpPut("{userId}")]
    public async Task<IActionResult> ChangeRole(string id, string userId, [FromBody] ChangeRoleRequest req)
    {
        if (!TryRole(req.Role, out var role)) return Result(InvalidRole);
        return Result(await membership.ChangeRoleAsync(UserId, id, userId, role));
    }

    [HttpDelete("{userId}")]
    public async Task<IActionResult> Remove(string id, string userId) =>
        Result(await membership.RemoveAsync(UserId, id, userId));

    // Покинуть проект: единственная операция, доступная не владельцу
    [HttpDelete("me")]
    public async Task<IActionResult> Leave(string id) => Result(await membership.LeaveAsync(UserId, id));

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
            ? Ok(ProjectMembersView.Full(r.Project!, UserId, users))
            : StatusCode(r.Status, new { error = r.Error, code = r.Code });
}
