using System.Security.Claims;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.Authorization;
using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services.Desktop;

/// <summary>Сервер → устройство. Строго типизированный клиент: имена методов — часть протокола.</summary>
public interface IDesktopDeviceClient
{
    /// <summary>Команда принята к исполнению не будет, пока не придёт встречный Go.</summary>
    Task Call(DesktopCallCommand command);

    /// <summary>Разрешение исполнять: с этого момента идут часы дедлайна.</summary>
    Task Go(DesktopGoCommand go);

    /// <summary>Отмена: гасит ожидание и невыполненные шаги.</summary>
    Task Cancel(DesktopCancelCommand cancel);

    /// <summary>
    /// Открыть канал исполнения (ADR-016): устройство подключается WebSocket'ом к
    /// /api/devices/exec с этим execId.
    /// </summary>
    Task ExecOpen(DeviceExecOpenCommand command);
}

/// <summary>
/// Канал устройств десктопного агента (ADR-008, «Протокол канала»). Маппинг — /hubs/devices.
///
/// Авторизация — ТОЛЬКО схемой токена устройства: дефолтная JwtBearer и сервисный JWT
/// владельца этой поверхности не открывают. Владелец и устройство берутся из claims токена,
/// заголовки в решении не участвуют.
///
/// Push идёт в КОНКРЕТНОЕ соединение (групп нет): адресат вызова определён сеансом рук.
/// Результат сюда не приезжает — он уходит HTTP-POST'ом мимо 32-КБ лимита сообщения хаба.
///
/// Живёт в самой вертикали, а не в <c>Hubs/</c> рядом с <c>SessionHub</c>/<c>TerminalHub</c>
/// (Этап 5, вынос Desktop): это ОТДЕЛЬНЫЙ канал устройств, и все его зависимости —
/// собственные (<see cref="DesktopCallRouter"/>, <see cref="DesktopProtocol"/>). Шов, как у
/// <c>ITerminalHubNotifier</c>, здесь был бы лишним: он понадобился терминалу потому, что
/// <c>TerminalHub</c> держит корневой <c>ProjectManager</c> и физически не мог уехать из Main.
/// Оставить хаб в Main означало бы цикл Main.Hubs ⇄ вертикаль: хаб зовёт маршрутизатор,
/// маршрутизатор пушит в хаб через <c>IHubContext&lt;DeviceHub&gt;</c>.
/// </summary>
[Authorize(AuthenticationSchemes = DesktopProtocol.DeviceTokenScheme)]
public sealed class DeviceHub(
    DesktopCallRouter router,
    DeviceExecChannel exec,
    ILogger<DeviceHub> log,
    AgentTicketService? agentTickets = null,
    IProjectFilesChangedNotifier? filesChanged = null)
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

        router.RegisterConnection(Context.ConnectionId, ownerId, deviceId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await router.RemoveConnectionAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Представление устройства: версия протокола объявляется явно, поддерживаемые типы шагов
    /// сервер не додумывает. До Hello устройство командам недоступно.
    /// Агент локальных проектов (ADR-016) дополнительно объявляет платформу, версии и
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

    /// <summary>Подтверждение приёма команды. Не пришло за 2 с — вызов кончается честной ошибкой.</summary>
    public Task Ack(string callId)
    {
        if (!router.Ack(callId, Context.ConnectionId)) throw UnknownCall(callId);
        return Task.CompletedTask;
    }

    /// <summary>Устройство разговаривает с человеком и просит времени (минуты).</summary>
    public Task Awaiting(string callId, int minutes)
    {
        if (!router.Awaiting(callId, Context.ConnectionId, minutes)) throw UnknownCall(callId);
        return Task.CompletedTask;
    }

    /// <summary>Человек подтвердил действие — сервер отвечает встречным Go.</summary>
    public Task Confirm(string callId)
    {
        if (!router.Confirm(callId, Context.ConnectionId)) throw UnknownCall(callId);
        return Task.CompletedTask;
    }

    /// <summary>Человек отклонил действие — отказ уходит модели текстом.</summary>
    public Task Decline(string callId)
    {
        if (!router.Decline(callId, Context.ConnectionId)) throw UnknownCall(callId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Индекс последнего применённого шага по ходу батча: без него при обрыве и дедлайне
    /// вернуть этот индекс (инвариант ADR) было бы нечем.
    /// </summary>
    public Task Progress(string callId, int lastAppliedStep)
    {
        if (!router.Progress(callId, Context.ConnectionId, lastAppliedStep)) throw UnknownCall(callId);
        return Task.CompletedTask;
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

    // Донесение по чужому или неизвестному callId — не «тихо ок»: устройство обязано увидеть отказ.
    private static HubException UnknownCall(string callId) =>
        new($"Вызов {callId} этому устройству не адресован");
}
