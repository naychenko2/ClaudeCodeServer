using System.Security.Claims;
using ClaudeHomeServer.Services.Mcp.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
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
///
/// Ход админа — но не любой (docs/architecture/mcp-toolsets.md, п. 10): сервисный токен на делегированном ходу (ход
/// процесса, chats_send из другого чата, реакция на доклад исполнителя) admin-ручку не
/// получает — у такого хода нет человека, который её заказал. Решает гейт делегирования по
/// сессии-вызывателю fail-closed: сервисный токен без заголовка вызывателя — тоже отказ.
/// Интерактивный токен человека гейт не трогает.
/// </summary>
public sealed class AdminByStoreRequirement : IAuthorizationRequirement
{
    /// <summary>Имя политики для [Authorize(Policy = ...)].</summary>
    public const string PolicyName = "admin-by-store";
}

public sealed class AdminByStoreHandler(
    IUserStore users,
    IDelegatedTurnGate? delegatedTurnGate = null,
    ILogger<AdminByStoreHandler>? logger = null)
    : AuthorizationHandler<AdminByStoreRequirement>
{
    private const string Action = "Админ-действие";

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, AdminByStoreRequirement requirement)
    {
        if (!IsAdmin(users, context.User)) return Task.CompletedTask;
        if (DelegatedDenial(context) is { } denial)
        {
            logger?.LogWarning("Гейт делегированного хода: admin-ручка {Path} отклонена (владелец {Owner}, чат {Caller}): {Reason}",
                denial.Path, denial.Owner, denial.Caller ?? "-", denial.Reason);
            return Task.CompletedTask;
        }
        context.Succeed(requirement);
        return Task.CompletedTask;
    }

    private sealed record Denial(string Path, string Owner, string? Caller, string Reason);

    // Отказ гейта делегирования для сервисного токена; null — гейт не против
    private Denial? DelegatedDenial(AuthorizationHandlerContext context)
    {
        if (context.User.FindFirstValue(JwtService.TokenKindClaim) != JwtService.ServiceTokenKind) return null;
        var http = context.Resource switch
        {
            HttpContext h => h,
            AuthorizationFilterContext f => f.HttpContext,
            _ => null,
        };
        var owner = context.User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;
        var caller = http?.Request.Headers[McpEndpoints.CallerSessionHeader].FirstOrDefault();
        var path = http?.Request.Path.Value ?? "?";
        // Гейта нет (тестовый хост без Main) — сервисный токен admin-ручку не получает:
        // проверить ход нечем, а пропуск вслепую и есть дыра
        var reason = delegatedTurnGate is null
            ? "гейт делегирования не подключён"
            : delegatedTurnGate.Deny(owner, caller ?? "", Action);
        return reason is null ? null : new(path, owner, caller, reason);
    }

    /// <summary>Роль владельца токена по стору. Вынесено ради теста и повторного использования.</summary>
    internal static bool IsAdmin(IUserStore users, ClaimsPrincipal? principal)
    {
        if (principal?.FindFirstValue(JwtRegisteredClaimNames.Sub) is not { Length: > 0 } userId)
            return false;
        return users.GetById(userId) is { Role: { } role }
            && string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase);
    }
}
