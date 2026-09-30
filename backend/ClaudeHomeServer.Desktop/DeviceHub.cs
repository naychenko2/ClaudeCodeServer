using System.Security.Claims;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.Authorization;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Execution;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services.Desktop;

/// <summary>Сервер → устройство. Строго типизированный клиент: имена методов — часть протокола.</summary>
public interface IDesktopDeviceClient
{
    /// <summary>
    /// Открыть канал исполнения (ADR-016): устройство подключается WebSocket'ом к
    /// /api/devices/exec с этим execId.
    /// </summary>
    Task ExecOpen(DeviceExecOpenCommand command);
}

/// <summary>
/// Хаб устройств — канал управления агента локальных проектов (ADR-016). Маппинг — /hubs/devices.
///
/// Авторизация — ТОЛЬКО схемой токена устройства: дефолтная JwtBearer и сервисный JWT
/// владельца этой поверхности не открывают. Владелец и устройство берутся из claims токена,
/// заголовки в решении не участвуют.
///
/// Push идёт в КОНКРЕТНОЕ соединение (групп нет). Поток исполнения сюда не приезжает — он
/// идёт отдельным WebSocket <see cref="DeviceExecProtocol.Path"/>.
///
/// Живёт в самой вертикали, а не в <c>Hubs/</c> рядом с <c>SessionHub</c>/<c>TerminalHub</c>
/// (Этап 5, вынос Desktop): это ОТДЕЛЬНЫЙ канал устройств, и все его зависимости —
/// собственные (<see cref="DeviceConnectionRegistry"/>, <see cref="DeviceExecChannel"/>).
/// Оставить хаб в Main означало бы цикл Main.Hubs ⇄ вертикаль: хаб зовёт канал исполнения,
/// канал пушит в хаб через <c>IHubContext&lt;DeviceHub&gt;</c>.
/// </summary>
[Authorize(AuthenticationSchemes = DesktopProtocol.DeviceTokenScheme)]
public sealed class DeviceHub(
    DeviceConnectionRegistry connections,
    DeviceExecChannel exec,
    ILogger<DeviceHub> log,
    AgentTicketService? agentTickets = null,
    IProjectFilesChangedNotifier? filesChanged = null,
    ILocalHandsNotifier? handsNotifier = null)
    : Hub<IDesktopDeviceClient>
{
    private string? OwnerId => Context.User?.FindFirstValue(DesktopProtocol.OwnerIdClaim);
    private string? DeviceId => Context.User?.FindFirstValue(DesktopProtocol.DeviceIdClaim);

    public override async Task OnConnectedAsync()
    {
        var ownerId = OwnerId;
        var deviceId = DeviceId;
        if (string.IsNullOrEmpty(ownerId) || string.IsNullOrEmpty(deviceId))
        {
            // Токен без пары владелец+устройство каналом не пользуется.
            Context.Abort();
            return;
        }

        connections.RegisterConnection(Context.ConnectionId, ownerId, deviceId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await connections.RemoveConnectionAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Представление устройства: версия протокола объявляется явно, до Hello устройство
    /// командам недоступно. Агент локальных проектов (ADR-016) объявляет платформу, версии и
    /// возможности, а в ответ получает требуемую версию CLI и вердикт «харнес готов»;
    /// поставив нужную копию CLI, он повторяет Hello.
    /// </summary>
    public async Task<DeviceHelloAck> Hello(DeviceHello hello)
    {
        if (!DesktopProtocol.IsSupportedClientVersion(hello.ProtocolVersion))
        {
            log.LogWarning("Устройство {DeviceId} говорит на версии протокола {Version}, сервер — на {Server}",
                DeviceId, hello.ProtocolVersion, DesktopProtocol.Version);
            throw new HubException(
                $"Версия протокола {hello.ProtocolVersion} не поддерживается: сервер говорит на версии {DesktopProtocol.Version}");
        }

        return await exec.HelloAsync(
            Context.ConnectionId, OwnerId ?? "", DeviceId ?? "", hello, Context.ConnectionAborted);
    }

    /// <summary>
    /// Интроспекция билета localhost-API (ADR-016, задача 4.2): агент спрашивает, кому
    /// сервер выдал билет, пришедший от браузера. Ответ — только устройству, к которому билет
    /// привязан; во всех остальных случаях null, без различения причин.
    /// </summary>
    [HubMethodName(DeviceAgentApi.IntrospectMethod)]
    public AgentTicketIntrospection? IntrospectAgentTicket(string ticket)
    {
        if (agentTickets is null || OwnerId is not { Length: > 0 } ownerId || DeviceId is not { Length: > 0 } deviceId)
            return null;
        return agentTickets.Introspect(ownerId, deviceId, ticket);
    }

    /// <summary>
    /// Донесение ватчера агента: дерево локального проекта изменилось. Уходит в веб-морду
    /// тем же событием, что у серверного ватчера, — но только по проекту, привязанному к
    /// этому устройству: чужой проект устройство «пошевелить» не может.
    /// </summary>
    [HubMethodName(DeviceAgentApi.FilesChangedMethod)]
    public async Task ProjectFilesChanged(DeviceFilesChanged report)
    {
        if (agentTickets is null || filesChanged is null) return;
        if (OwnerId is not { Length: > 0 } ownerId || DeviceId is not { Length: > 0 } deviceId) return;
        if (agentTickets.ProjectOnDevice(ownerId, deviceId, report.ProjectId) is null)
            throw new HubException($"Проект {report.ProjectId} к этому устройству не привязан");

        var paths = report.Paths ?? [];
        var full = report.Full || paths.Count > DeviceAgentApi.MaxChangedPaths;
        await filesChanged.FilesChangedAsync(report.ProjectId, full ? [] : paths, full);
    }

    /// <summary>
    /// Донесение агента о руках хода (ADR-016 §7): «ход действует руками», «руки доступны»,
    /// «остановлено из трея». Принимается только по ходу с руками, который сервер сам отправил
    /// ЭТОМУ устройству, — чужой чат устройство «пошевелить» не может. Состояние запоминается
    /// у устройства (первая отрисовка бейджа) и уходит в чат хода.
    /// </summary>
    [HubMethodName(DeviceHandsReport.Method)]
    public async Task ReportHandsStatus(DeviceHandsReport report)
    {
        if (OwnerId is not { Length: > 0 } ownerId || DeviceId is not { Length: > 0 } deviceId) return;
        if (report is null || !HandsChatStates.FromDevice.Contains(report.State))
            throw new HubException("Незнакомое состояние рук");
        if (DeviceHandsTurns.SessionOf(ownerId, deviceId, report.TurnId ?? "") is not { } sessionId)
            throw new HubException($"Ход {report.TurnId} с руками этому устройству не отправлялся");

        // Причина — только из известного набора: текст с устройства в чат как есть не едет
        var reason = report.State == HandsChatStates.Stopped && KnownEndReasons.Contains(report.Reason ?? "")
            ? report.Reason
            : null;
        DeviceHandsTurns.SetLast(ownerId, deviceId,
            new DeviceHandsLastState(sessionId, report.TurnId!, report.State, reason, DateTimeOffset.UtcNow));
        DeviceHandsTurns.Accepted(ownerId, deviceId, report.TurnId!, report.State);
        if (handsNotifier is not null)
            await handsNotifier.HandsStatusAsync(ownerId, deviceId, sessionId, report.State, reason);
    }

    private static readonly HashSet<string> KnownEndReasons = new(StringComparer.Ordinal)
    {
        HandsEndReason.StoppedFromTray, HandsEndReason.HandsDisabled, HandsEndReason.AgentStopping,
    };
}
