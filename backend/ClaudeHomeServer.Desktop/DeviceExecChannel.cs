using System.Collections.Concurrent;
using System.Security.Claims;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services.Desktop;

/// <summary>
/// Отправка команды открытия канала исполнения в КОНКРЕТНОЕ соединение хаба устройства.
/// Отдельный интерфейс, чтобы канал жил без SignalR в тестах.
/// </summary>
public interface IDeviceExecOpenSender
{
    Task SendExecOpenAsync(string connectionId, DeviceExecOpenCommand command, CancellationToken ct = default);
}

/// <summary>Боевой отправитель — push через хаб устройств.</summary>
public sealed class DeviceHubExecOpenSender(IHubContext<DeviceHub, IDesktopDeviceClient> hub) : IDeviceExecOpenSender
{
    public Task SendExecOpenAsync(string connectionId, DeviceExecOpenCommand command, CancellationToken ct = default)
        => hub.Clients.Client(connectionId).ExecOpen(command);
}

/// <summary>
/// Канал исполнения на устройстве (ADR-016, план §2 пп. 4–6) — реализация шва
/// <see cref="IDeviceExecChannel"/>. Хаб /hubs/devices остаётся каналом управления (hello,
/// возможности, онлайн, команда открытия); поток stdio идёт отдельным WebSocket
/// <see cref="DeviceExecProtocol.Path"/> кадрами с номером и подтверждением.
///
/// Готовность считается при каждом вопросе: онлайн (живое соединение хаба после Hello),
/// объявленная возможность <c>exec</c> и копия CLI ровно той версии, что требует сервер.
/// Не готово — отказ открыть канал сразу, с причиной, до старта процесса.
///
/// Авторизация WebSocket — только токен устройства плюс отпечаток: дефолтная JwtBearer и
/// сервисный JWT владельца канал не открывают (та же граница, что у /api/devices/*).
/// </summary>
public sealed class DeviceExecChannel : IDeviceExecChannel
{
    private readonly DeviceRegistry _registry;
    private readonly DesktopCallRouter _router;
    private readonly DeviceHarnessPolicy _harness;
    private readonly IDeviceExecOpenSender _opener;
    private readonly ILogger<DeviceExecChannel> _log;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, DeviceExecStream> _streams = new();

    public DeviceExecChannel(
        DeviceRegistry registry,
        DesktopCallRouter router,
        DeviceHarnessPolicy harness,
        IDeviceExecOpenSender opener,
        ILogger<DeviceExecChannel> log,
        TimeProvider? timeProvider = null)
    {
        _registry = registry;
        _router = router;
        _harness = harness;
        _opener = opener;
        _log = log;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Hello устройства: сведения агента сохраняются ДО того, как маршрутизатор объявит
    /// устройство онлайн, — наблюдатели онлайна видят уже свежие возможности. В ответ уходит
    /// требуемая версия CLI и вердикт по объявленной копии.
    /// </summary>
    public async Task<DeviceHelloAck> HelloAsync(
        string connectionId, string ownerId, string deviceId, DeviceHello hello, CancellationToken ct = default)
    {
        var device = _registry.UpdateAgentInfo(ownerId, deviceId, hello);
        var ack = await _router.HelloAsync(connectionId, hello, ct);

        var (ready, problem) = _harness.Evaluate(device?.CliVersion);
        if (!ready && !string.IsNullOrWhiteSpace(hello.AgentVersion))
            _log.LogInformation("Устройство {DeviceId}: {Problem}", deviceId, problem);

        return ack with
        {
            RequiredCliVersion = _harness.RequiredCliVersion,
            HarnessReady = ready,
            HarnessProblem = problem,
            ExecProtocolVersion = DeviceExecProtocol.Version,
        };
    }

    public DeviceExecStatus? GetStatus(string ownerId, string deviceId)
    {
        var device = _registry.Get(ownerId, deviceId);
        if (device is null || device.Revoked) return null;

        var (ready, problem) = _harness.Evaluate(device.CliVersion);
        return new DeviceExecStatus(
            device.Id,
            device.Name,
            _router.IsOnline(ownerId, deviceId),
            device.Platform,
            device.AgentVersion,
            device.CliVersion,
            _harness.RequiredCliVersion,
            device.Capabilities.ToList(),
            ready,
            problem);
    }

    public async Task<IDeviceExecStream> OpenAsync(string ownerId, string deviceId, CancellationToken ct = default)
    {
        var status = GetStatus(ownerId, deviceId)
            ?? throw new DeviceExecRefusedException(DeviceExecRefusal.UnknownDevice, "Устройство не найдено или отозвано.");

        var name = $"Устройство «{status.DeviceName}»";
        if (!status.Online)
            throw new DeviceExecRefusedException(DeviceExecRefusal.Offline, $"{name} офлайн — ход не запущен.");
        if (!status.HasCapability(DeviceCapabilities.Exec))
            throw new DeviceExecRefusedException(DeviceExecRefusal.NoExecCapability,
                $"{name} не исполняет ходы: агент локальных проектов не подключён или не объявил возможность exec.");
        if (!status.HarnessReady)
            throw new DeviceExecRefusedException(DeviceExecRefusal.HarnessNotReady, $"{name}: {status.HarnessProblem}.");

        var connection = _router.Find(ownerId, deviceId)
            ?? throw new DeviceExecRefusedException(DeviceExecRefusal.Offline, $"{name} офлайн — ход не запущен.");

        var stream = new DeviceExecStream(DesktopProtocol.NewCallId(), ownerId, deviceId, s => _streams.TryRemove(s.ExecId, out _),
            time: _time);
        _streams[stream.ExecId] = stream;

        try
        {
            await _opener.SendExecOpenAsync(
                connection.ConnectionId, new DeviceExecOpenCommand(stream.ExecId, DeviceExecProtocol.Version), ct);
            await stream.Attached.WaitAsync(DeviceExecProtocol.OpenTimeout, _time, ct);
            return stream;
        }
        catch (Exception ex) when (ex is TimeoutException or HubException or IOException or InvalidOperationException)
        {
            await stream.DisposeAsync();
            _log.LogWarning(ex, "Устройство {DeviceId} не открыло канал исполнения {ExecId}", deviceId, stream.ExecId);
            throw new DeviceExecRefusedException(DeviceExecRefusal.NoResponse,
                $"{name} не открыло канал исполнения за {(int)DeviceExecProtocol.OpenTimeout.TotalSeconds} с — ход не запущен.");
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    /// <summary>Обработчик WebSocket /api/devices/exec. Авторизация уже пройдена схемой устройства.</summary>
    internal async Task HandleAsync(HttpContext context)
    {
        var user = context.User;
        var ownerId = user.FindFirstValue(DesktopProtocol.OwnerIdClaim);
        var deviceId = user.FindFirstValue(DesktopProtocol.DeviceIdClaim);
        // Вторая линия: принципал обязан быть построен схемой устройства, а не чем-то ещё
        var byDeviceScheme = user.Identities.Any(i =>
            i.IsAuthenticated && i.AuthenticationType == DesktopProtocol.DeviceTokenScheme);
        if (!byDeviceScheme || string.IsNullOrEmpty(ownerId) || string.IsNullOrEmpty(deviceId))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var versionHeader = context.Request.Headers[DeviceExecProtocol.VersionHeader].ToString();
        if (!int.TryParse(versionHeader, out var version) || !DeviceExecProtocol.IsSupportedClientVersion(version))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                error = DeviceExecProtocol.UnsupportedVersionError,
                message = string.IsNullOrEmpty(versionHeader)
                    ? $"Устройство не назвало версию протокола канала исполнения (заголовок {DeviceExecProtocol.VersionHeader})"
                    : $"Версия протокола канала исполнения {versionHeader} не поддерживается: сервер говорит на версии {DeviceExecProtocol.Version}",
                serverVersion = DeviceExecProtocol.Version,
                minVersion = DeviceExecProtocol.MinClientVersion,
            });
            return;
        }

        var execId = context.Request.Query["execId"].ToString();
        if (string.IsNullOrEmpty(execId) || !_streams.TryGetValue(execId, out var stream))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (stream.OwnerId != ownerId || stream.DeviceId != deviceId)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "websocket_required" });
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await stream.RunAsync(socket, context.RequestAborted);
    }

    /// <summary>Живые потоки — для тестов и диагностики.</summary>
    internal bool HasStream(string execId) => _streams.ContainsKey(execId);
}

public static class DeviceExecChannelEndpoints
{
    /// <summary>
    /// Маппинг WebSocket канала исполнения. WebSocket-мидлвара ставится на ветку эндпоинта,
    /// а не на весь конвейер (как у SignalR MapConnections): глобальная мидлвара влезла бы в
    /// апгрейды, которые проксирует YARP.
    /// </summary>
    public static IEndpointConventionBuilder MapDeviceExecChannel(this IEndpointRouteBuilder endpoints)
    {
        var branch = endpoints.CreateApplicationBuilder();
        branch.UseWebSockets();
        branch.Run(context => context.RequestServices.GetRequiredService<DeviceExecChannel>().HandleAsync(context));

        return endpoints.Map(DeviceExecProtocol.Path, branch.Build())
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DesktopProtocol.DeviceTokenScheme });
    }
}
