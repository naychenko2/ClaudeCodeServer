using AiHomeDesktop.Core.Protocol;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiHomeDesktop.Core.Channel;

/// <summary>
/// Донесения устройства серверу. Имена методов — часть протокола: они совпадают с методами
/// хаба (Ack/Awaiting/Confirm/Decline/Progress).
/// </summary>
public interface IDeviceChannel
{
    /// <summary>Приём команды. Сервер ждёт его 2 секунды — дальше честная ошибка вызова.</summary>
    Task AckAsync(string callId, CancellationToken ct = default);

    /// <summary>Разговариваем с человеком и просим времени (минуты).</summary>
    Task AwaitingAsync(string callId, int minutes, CancellationToken ct = default);

    /// <summary>Человек подтвердил — сервер ответит встречным Go.</summary>
    Task ConfirmAsync(string callId, CancellationToken ct = default);

    /// <summary>Человек отклонил — отказ уйдёт модели текстом.</summary>
    Task DeclineAsync(string callId, CancellationToken ct = default);

    /// <summary>Индекс последнего применённого шага по ходу батча.</summary>
    Task ProgressAsync(string callId, int lastAppliedStep, CancellationToken ct = default);
}

/// <summary>Верхний слой канала: кто разбирает команды сервера.</summary>
public interface IDeviceCallHandler
{
    /// <summary>
    /// Пришла команда. Обработчик обязан вернуть управление быстро (Ack за 2 с и не
    /// задерживать очередь сообщений) — длинную работу уносить в фон.
    /// </summary>
    Task OnCallAsync(DesktopCallCommand command);

    /// <summary>Встречный go: с этого момента идут часы дедлайна исполнения.</summary>
    void OnGo(DesktopGoCommand go);

    /// <summary>Отмена: гасим ожидание и невыполненные шаги.</summary>
    void OnCancel(DesktopCancelCommand cancel);

    /// <summary>Канал поднялся (в том числе после обрыва) — самое время дослать недоехавшее.</summary>
    Task OnConnectedAsync();
}

/// <summary>Настройки подключения к хабу устройств.</summary>
/// <param name="Credentials">Учётные данные устройства: адрес сервера и его токен.</param>
/// <param name="ClientVersion">Версия клиента — уезжает в Hello, диагностика на сервере.</param>
public sealed record DeviceChannelOptions(DeviceCredentials Credentials, string? ClientVersion = null);

/// <summary>
/// Канал устройства: исходящее SignalR-соединение к <c>{сервер}/hubs/devices</c>.
///
/// Исходящее — потому что входящего порта на машине пользователя нет (NAT), и открывать
/// его продукт не будет никогда. Разрыв связи — ШТАТНОЕ состояние, а не авария: клиент
/// переподключается с нарастающей паузой и после подъёма досылает недоехавшие результаты.
///
/// Авторизация — заголовками на каждом запросе, включая рукопожатие: схема
/// <c>Authorization: Device {токен}</c> плюс <c>X-Device-Fingerprint</c>. Штатный
/// AccessTokenProvider SignalR здесь не годится: он ставит Bearer, а на Bearer серверный
/// обработчик устройства отвечает NoResult.
/// </summary>
public sealed class DeviceChannel : IDeviceChannel, IAsyncDisposable
{
    private readonly DeviceChannelOptions _options;
    private readonly IDeviceCallHandler _handler;
    private readonly ILogger _log;
    private readonly HubConnection _connection;
    private readonly CancellationTokenSource _life = new();
    private Task? _loop;

    public DeviceChannel(DeviceChannelOptions options, IDeviceCallHandler handler, ILogger<DeviceChannel>? log = null)
    {
        _options = options;
        _handler = handler;
        _log = log ?? NullLogger<DeviceChannel>.Instance;

        var hubUrl = new Uri(new Uri(options.Credentials.ServerUrl), "/hubs/devices");

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, o =>
            {
                o.Headers["Authorization"] = $"Device {options.Credentials.DeviceToken}";
                o.Headers["X-Device-Fingerprint"] = options.Credentials.Fingerprint;
                // WebSockets, но без запрета на фолбэк: сервер за прокси может не отдать
                // апгрейд, и тогда лучше long polling, чем отсутствующий канал.
                o.Transports = HttpTransportType.WebSockets | HttpTransportType.LongPolling;
            })
            .WithAutomaticReconnect(new BackoffRetryPolicy())
            .Build();

        _connection.On<DesktopCallCommand>("Call", command => _handler.OnCallAsync(command));
        _connection.On<DesktopGoCommand>("Go", go => _handler.OnGo(go));
        _connection.On<DesktopCancelCommand>("Cancel", cancel => _handler.OnCancel(cancel));

        _connection.Reconnected += async _ =>
        {
            _log.LogInformation("Канал устройства поднялся заново");
            await HelloAsync(_life.Token);
            await _handler.OnConnectedAsync();
        };

        _connection.Closed += async error =>
        {
            // Автопереподключение SignalR однажды сдаётся — дальше держим соединение сами.
            _log.LogInformation(error, "Канал устройства закрыт; переподключаемся");
            if (_life.IsCancellationRequested) return;
            await Task.Delay(TimeSpan.FromSeconds(5), _life.Token);
            StartLoop();
        };
    }

    /// <summary>Состояние канала — им же питается индикатор в окне клиента.</summary>
    public HubConnectionState State => _connection.State;

    /// <summary>Потолки, которые объявил сервер в ответ на Hello.</summary>
    public DeviceHelloAck? ServerAck { get; private set; }

    /// <summary>Поднять канал и держать его: попытки идут с нарастающей паузой, без предела.</summary>
    public void Start() => StartLoop();

    private void StartLoop()
    {
        if (_loop is { IsCompleted: false }) return;
        _loop = Task.Run(() => ConnectLoopAsync(_life.Token));
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested && _connection.State == HubConnectionState.Disconnected)
        {
            try
            {
                await _connection.StartAsync(ct);
                await HelloAsync(ct);
                await _handler.OnConnectedAsync();
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                var delay = BackoffRetryPolicy.DelayFor(attempt++);
                // Обрыв — штатное состояние: пишем как информацию, а не как аварию.
                _log.LogInformation(ex, "Канал устройства не поднялся; следующая попытка через {Delay}", delay);
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>
    /// Представиться серверу: версия протокола и что клиент умеет. Состав объявляется здесь,
    /// а НЕ подменой tools/list — он входит в сигнатуру запуска CLI, и его изменение по ходу
    /// перезапустило бы процесс со всеми MCP-серверами.
    /// </summary>
    private async Task HelloAsync(CancellationToken ct)
    {
        try
        {
            ServerAck = await _connection.InvokeAsync<DeviceHelloAck>(
                "Hello",
                new DeviceHello(DesktopProtocol.Version, DesktopCallKinds.Supported, _options.ClientVersion),
                ct);

            if (ServerAck is not null && !DesktopProtocol.IsSupportedServerVersion(ServerAck.ProtocolVersion))
                _log.LogWarning("Сервер говорит на версии протокола {Server}, клиент — на {Client}",
                    ServerAck.ProtocolVersion, DesktopProtocol.Version);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Hello не прошёл: сервер команд этому устройству не пришлёт");
        }
    }

    public Task AckAsync(string callId, CancellationToken ct = default) =>
        _connection.SendAsync("Ack", callId, ct);

    public Task AwaitingAsync(string callId, int minutes, CancellationToken ct = default) =>
        _connection.SendAsync("Awaiting", callId, minutes, ct);

    public Task ConfirmAsync(string callId, CancellationToken ct = default) =>
        _connection.SendAsync("Confirm", callId, ct);

    public Task DeclineAsync(string callId, CancellationToken ct = default) =>
        _connection.SendAsync("Decline", callId, ct);

    public Task ProgressAsync(string callId, int lastAppliedStep, CancellationToken ct = default) =>
        _connection.SendAsync("Progress", callId, lastAppliedStep, ct);

    public async ValueTask DisposeAsync()
    {
        await _life.CancelAsync();
        await _connection.DisposeAsync();
        _life.Dispose();
    }

    /// <summary>
    /// Пауза между попытками: 0, 2, 5, 10, 20, 30 с и дальше 30 — сдаваться нельзя, машина
    /// пользователя может ночевать без сети, а утром канал обязан подняться сам.
    /// </summary>
    private sealed class BackoffRetryPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] Delays =
        [
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(30)
        ];

        public static TimeSpan DelayFor(int attempt) =>
            Delays[Math.Min(Math.Max(attempt, 0), Delays.Length - 1)];

        public TimeSpan? NextRetryDelay(RetryContext context) => DelayFor((int)context.PreviousRetryCount);
    }
}
