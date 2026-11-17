using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
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

            if (name == "SendMessage")
            {
                // mode/auto от ребёнка не доверяем: подменяем аргументы до вызова метода хаба
                var args = SanitizeSendMessageArgs(invocationContext.HubMethodArguments);
                invocationContext = new HubInvocationContext(invocationContext.Context,
                    invocationContext.ServiceProvider, invocationContext.Hub,
                    invocationContext.HubMethod, args);
            }
        }
        return next(invocationContext);
    }

    // SendMessage(sessionId, text, attachedPaths, mode, auto): mode → null, auto → false
    internal static object?[] SanitizeSendMessageArgs(IReadOnlyList<object?> arguments)
    {
        var args = arguments.ToArray();
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
