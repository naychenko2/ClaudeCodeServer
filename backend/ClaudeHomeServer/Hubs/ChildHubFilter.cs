using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Hubs;

/// <summary>
/// Гейт И1 на хабах (ADR-041): глобальный <see cref="IHubFilter"/> рядом с ProjectCapabilityHubFilter.
/// Ребёнку (по <see cref="IChildModePolicy"/>, а не по claim) разрешены только SendMessage, ответ на
/// карточку вопроса (AnswerQuestion) и подписка/присоединение к своему чату. Хабы terminal и devices
/// закрыты на negotiate гейтом REST-слоя: у них нет [ChildAllowed], поэтому ребёнок не доходит до
/// подключения. Профиль с DenyAll доходит до подключения к session-хабу (Г14: HTTP-гейт отвечает
/// только за allow-list), но каждый метод-вызов этого фильтра — child_mode.
/// У SendMessage ребёнка <c>mode</c> и <c>auto</c> игнорируются: режим подставляется из профиля (KM-B3a);
/// <c>attachedPaths</c> — пути папки предмета этого чата (И8, решение 57): их судит
/// <see cref="ChildPathPolicy"/> (одна точка правды), Deny/Hidden — путь отбрасывается, вызов не падает.
/// </summary>
public sealed class ChildHubFilter : IHubFilter
{
    // Allow-list методов SessionHub для ребёнка. Join*/Leave* — подписки на группы своего чата и
    // личную комнату; JoinUser сам сверяет userId со своим. AnswerQuestion — ответ на карточку
    // AskUserQuestion этого же хода (решение 10: инструмент в списке ребёнка есть, Б19): только
    // для своего чата, как SendMessage и Interrupt.
    internal static readonly string[] AllowedMethods =
        ["SendMessage", "Interrupt", "JoinSession", "LeaveSession", "JoinUser", "LeaveUser", "AnswerQuestion"];

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
            if (name is "SendMessage" or "Interrupt" or "JoinSession" or "AnswerQuestion"
                && !IsOwnChildChat(invocationContext, snapshot.OwnerId))
                throw new HubException("child_mode");

            if (name == "SendMessage")
            {
                // mode/auto/attachedPaths от ребёнка не доверяем: подменяем аргументы до вызова метода хаба
                var args = SanitizeSendMessageArgs(invocationContext.HubMethodArguments,
                    invocationContext.ServiceProvider.GetService<SessionManager>(), snapshot.OwnerId);
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
            // «Свой чат» — каноническим GetOwnedVisible (арендатор по ResolveOwnerId + видимость
            // чата), без инлайн-сравнения владельца: человеческий путь хаба (HumanSessionAccessGuard).
            // Для своего projectless-чата CanSee = владение (SessionAccess.cs:24) — семантика та же.
            var session = ownerId.Length > 0
                ? ctx.ServiceProvider.GetService<SessionManager>()?.GetOwnedVisible(sessionId, ownerId)
                : null;
            return session is { ChildSpace: not null };
        }
        catch (Exception)
        {
            return false;
        }
    }

    // SendMessage(sessionId, text, attachedPaths, mode, auto): mode → null, auto → false;
    // attachedPaths — пути папки предмета этого чата (решение 57), см. FilterAttachedPaths.
    internal static object?[] SanitizeSendMessageArgs(IReadOnlyList<object?> arguments,
        SessionManager? sessions, string ownerId)
    {
        var args = arguments.ToArray();
        if (args.Length > AttachedPathsArg)
            args[AttachedPathsArg] = args[AttachedPathsArg] is IEnumerable<string> paths
                && args[SessionIdArg] is string sessionId
                ? FilterAttachedPaths(paths, sessions, sessionId, ownerId)
                : null;
        if (args.Length > ModeArg) args[ModeArg] = null;
        if (args.Length > AutoArg) args[AutoArg] = false;
        return args;
    }

    // И8 / решение 57: «вложения в урок — путём проекта, как у взрослого». Каждый путь судит
    // ChildPathPolicy — та же функция, что решение разрешения хода и рельса «Файлы»: граница
    // папки предмета этого чата (с канонизацией SafePath.Canonical: «..» и симлинки), «Материалы/»
    // на чтение, «Для репетитора/» и любое скрытое — вне. Deny и Hidden — путь отбрасывается,
    // вызов не падает: лишний путь не повод отказывать сообщение. Офис и PDF подменяются своей
    // текстовой копией (TextCopyIfExists), если она есть.
    // Путь судится как присланный: относительный резолвится от папки предмета, абсолютный остаётся
    // абсолютным и отказывается как чужой — TrimStart('/') его молча превратил бы в путь внутри
    // папки (шапка SafePath: на Linux «/etc/passwd» стал бы «etc/passwd»).
    private static List<string>? FilterAttachedPaths(IEnumerable<string> paths, SessionManager? sessions,
        string sessionId, string ownerId)
    {
        if (sessions is null || sessions.GetChatRoot(sessionId, ownerId) is not { } root) return null;
        var result = new List<string>();
        foreach (var raw in paths)
        {
            // Разделители только выравниваются в «/» (тот же приём, что у TextCopyPath в ходе);
            // пустое — не путь.
            var rel = (raw ?? "").Replace('\\', '/');
            if (rel.Length == 0) continue;
            if (!ChildPathPolicy.Decide(ChildActor.Child, ChildPathOp.Read, rel, root).Allowed) continue;
            result.Add(TextCopyIfExists(root, rel) ?? rel);
        }
        return result.Count == 0 ? null : result;
    }

    // Офис и PDF в урок встраиваются текстом (решение 57): копия — та же форма, что у всех
    // остальных: ChildPathPolicy.TextCopyOf (решение 90) — «.ccs-text/» с сохранённым
    // расширением оригинала, та же функция, что подсказка модели для картинки
    // (ClaudeSession.TextCopyPath, решение 67) и та же папка, которую читает синк предмета
    // (ProjectKnowledgeSyncService). Копии нет — остаётся исходный путь: AttachmentInliner
    // отдаст о нём пометку «бинарный/документ», отказа нет.
    private static readonly HashSet<string> TextCopyExts =
        new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx" };

    private static string? TextCopyIfExists(string root, string rel)
    {
        // Путь уже лежит под «.ccs-text/» — копия сама: второй копии не ищем, путь остаётся
        // как есть (OriginalOf — обратная к TextCopyOf; проверка по первому сегменту шире:
        // ловит и кривые формы, не являющиеся копией).
        if (ChildPathPolicy.OriginalOf(rel) is not null ||
            rel.StartsWith(ChildPathPolicy.TextDir + "/", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!TextCopyExts.Contains(Path.GetExtension(rel))) return null;
        var copy = ChildPathPolicy.TextCopyOf(rel);
        try { return File.Exists(SafePath.Join(root, copy)) ? copy : null; }
        catch (Exception) { return null; }
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
