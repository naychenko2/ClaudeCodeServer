using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Hubs;

/// <summary>
/// Гейт И1 на хабах (ADR-041): глобальный <see cref="IHubFilter"/> рядом с ProjectCapabilityHubFilter.
/// Ребёнку (по <see cref="IChildModePolicy"/>, а не по claim) разрешены только SendMessage и
/// подписка/присоединение к своему чату. Хабы terminal и devices закрыты на negotiate гейтом
/// REST-слоя: у них нет [ChildAllowed], поэтому ребёнок не доходит до подключения.
/// Профиль с DenyAll не пускает никуда.
/// У SendMessage ребёнка <c>mode</c> и <c>auto</c> игнорируются: режим подставляется из профиля (KM-B3a).
/// </summary>
public sealed class ChildHubFilter : IHubFilter
{
    // Allow-list методов SessionHub для ребёнка. Join*/Leave* — подписки на группы своего чата и
    // личную комнату; JoinUser сам сверяет userId со своим.
    internal static readonly string[] AllowedMethods =
        ["SendMessage", "JoinSession", "LeaveSession", "JoinUser", "LeaveUser"];

    private const int SessionIdArg = 0;
    private const int AttachedPathsArg = 2;
    private const int ModeArg = 3;
    private const int AutoArg = 4;

    public ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        if (ChildOf(invocationContext.Context, invocationContext.ServiceProvider, invocationContext.Hub) is { } snapshot)
        {
            var name = invocationContext.HubMethodName;
            if (snapshot.DenyAll || invocationContext.Hub is not SessionHub
                || !AllowedMethods.Contains(name, StringComparer.Ordinal))
                throw new HubException("child_mode");

            // Ребёнку — только свои детские чаты (владелец + ChildSpace); остальные — child_mode
            if (name is "SendMessage" or "JoinSession"
                && !IsOwnChildChat(invocationContext, snapshot.OwnerId))
                throw new HubException("child_mode");

            if (name == "SendMessage")
            {
                // mode/auto/attachedPaths от ребёнка не доверяем: подменяем аргументы до вызова метода хаба
                var args = SanitizeSendMessageArgs(invocationContext.HubMethodArguments);
                invocationContext = new HubInvocationContext(invocationContext.Context,
                    invocationContext.ServiceProvider, invocationContext.Hub,
                    invocationContext.HubMethod, args);
            }
        }
        return next(invocationContext);
    }

    // Fail-closed: нет id, чат не найден, чужой, без ChildSpace или сбой чтения — отказ.
    private static bool IsOwnChildChat(HubInvocationContext ctx, string ownerId)
    {
        try
        {
            if (ctx.HubMethodArguments.Count <= SessionIdArg
                || ctx.HubMethodArguments[SessionIdArg] is not string sessionId) return false;
            var session = ctx.ServiceProvider.GetService<SessionManager>()?.GetById(sessionId);
            return session is { ChildSpace: not null }
                && ownerId.Length > 0
                && string.Equals(session.OwnerId, ownerId, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // SendMessage(sessionId, text, attachedPaths, mode, auto): attachedPaths → null, mode → null, auto → false
    internal static object?[] SanitizeSendMessageArgs(IReadOnlyList<object?> arguments)
    {
        var args = arguments.ToArray();
        if (args.Length > AttachedPathsArg) args[AttachedPathsArg] = null;
        if (args.Length > ModeArg) args[ModeArg] = null;
        if (args.Length > AutoArg) args[AutoArg] = false;
        return args;
    }

    // Пустой владелец при JWT-хабе — fail-closed (For("") дал бы null, «не ребёнок»).
    // Хаб устройств авторизуется токеном устройства без sub — его это не касается.
    private static ChildPolicySnapshot? ChildOf(HubCallerContext caller, IServiceProvider services, object? hub)
    {
        if (caller.User?.FindFirstValue(JwtRegisteredClaimNames.Sub) is { Length: > 0 } ownerId)
            return services.GetRequiredService<IChildModePolicy>().For(ownerId);
        return caller.User?.Identity?.IsAuthenticated == true && hub is SessionHub or TerminalHub
            ? new ChildPolicySnapshot("", null, DenyAll: true)
            : null;
    }
}
