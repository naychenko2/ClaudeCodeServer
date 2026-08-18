using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ClaudeHomeServer.Services.Auth;

/// <summary>
/// «Владелец токена — админ ПО СТОРУ пользователей», а не по claim'у роли.
///
/// Два повода не проверять роль claim'ом:
///  1. Сервисные токены MCP (JwtService.IssueServiceToken) выдаются с ролью "user" всегда —
///     иначе пришлось бы поднять их до admin, а это открыло бы ВЕСЬ admin-периметр каждому
///     MCP-инструменту разом. С этой политикой admin-ручка доступна из хода админа и только
///     из него, а остальные [Authorize(Roles = "admin")] остаются недоступны сервисному токену.
///  2. Роль в токене — слепок на срок его жизни (у сервисного — семь дней): снятие админства
///     не подействовало бы до истечения. Источник истины о роли — UserStore.
/// </summary>
public sealed class AdminByStoreRequirement : IAuthorizationRequirement
{
    /// <summary>Имя политики для [Authorize(Policy = ...)].</summary>
    public const string PolicyName = "admin-by-store";
}

public sealed class AdminByStoreHandler(UserStore users)
    : AuthorizationHandler<AdminByStoreRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, AdminByStoreRequirement requirement)
    {
        if (IsAdmin(users, context.User)) context.Succeed(requirement);
        return Task.CompletedTask;
    }

    /// <summary>Роль владельца токена по стору. Вынесено ради теста и повторного использования.</summary>
    internal static bool IsAdmin(UserStore users, ClaimsPrincipal? principal)
    {
        if (principal?.FindFirstValue(JwtRegisteredClaimNames.Sub) is not { Length: > 0 } userId)
            return false;
        return users.GetById(userId) is { Role: { } role }
            && string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase);
    }
}
