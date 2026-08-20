using System.Security.Claims;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Desktop;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Hubs;

/// <summary>Сервер → устройство. Строго типизированный клиент: имена методов — часть протокола.</summary>
public interface IDesktopDeviceClient
{
    /// <summary>Команда принята к исполнению не будет, пока не придёт встречный Go.</summary>
    Task Call(DesktopCallCommand command);

    /// <summary>Разрешение исполнять: с этого момента идут часы дедлайна.</summary>
    Task Go(DesktopGoCommand go);

    /// <summary>Отмена: гасит ожидание и невыполненные шаги.</summary>
    Task Cancel(DesktopCancelCommand cancel);
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
/// </summary>
[Authorize(AuthenticationSchemes = DesktopProtocol.DeviceTokenScheme)]
public sealed class DeviceHub(DesktopCallRouter router, ILogger<DeviceHub> log) : Hub<IDesktopDeviceClient>
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

        return await router.HelloAsync(Context.ConnectionId, hello, Context.ConnectionAborted);
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

    // Донесение по чужому или неизвестному callId — не «тихо ок»: устройство обязано увидеть отказ.
    private static HubException UnknownCall(string callId) =>
        new($"Вызов {callId} этому устройству не адресован");
}
