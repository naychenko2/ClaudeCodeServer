using System.Net;
using System.Net.Http;
using AiHomeDesktop.App.Settings;
using AiHomeDesktop.Core.Channel;
using AiHomeDesktop.Core.Protocol;
using Microsoft.AspNetCore.SignalR.Client;

namespace AiHomeDesktop.App.Shell;

/// <summary>
/// Composition root канала: держит HTTP-половину (<see cref="DeviceApi"/>), локальный
/// журнал вызовов и исходящее SignalR-соединение к хабу устройств.
///
/// Исходящее — потому что входящего порта на машине пользователя нет (NAT). Разрыв —
/// штатное состояние: канал переподключается сам с нарастающей паузой, а недоехавшие
/// результаты досылаются с журнала при подъёме.
///
/// Клиент — НЕ второй сервер: своей базы, своего API и копии данных у него нет. Здесь
/// живут ровно две вещи: учётка устройства и минутный журнал вызовов.
/// </summary>
public sealed class AgentHost : IAsyncDisposable
{
    /// <summary>Как часто перечитываем состояние канала для строки состояния.</summary>
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Как часто, пока связи нет, спрашиваем сервер, жив ли ещё токен устройства. Отзыв
    /// и обрыв со стороны канала выглядят одинаково, а человеку это разные новости:
    /// «переподключаемся» проходит само, «устройство отозвано» — нет.
    /// </summary>
    private static readonly TimeSpan RevokeProbeInterval = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly RelayCallHandler _relay;
    private readonly CancellationTokenSource _life = new();

    private DeviceChannel? _channel;
    private Task? _statusLoop;
    private DateTimeOffset _probedAt = DateTimeOffset.MinValue;
    private bool _revoked;

    public AgentHost()
    {
        Api = new DeviceApi(_http);
        Journal = new CallJournal(ClientPaths.CallJournalFile);
        _relay = new RelayCallHandler(Api);
    }

    /// <summary>HTTP-половина канала. Сеанс рук ходит ею же — вторых клиентов не заводим.</summary>
    public DeviceApi Api { get; }

    /// <summary>Локальный журнал вызовов: после реконнекта по нему досылается недоехавшее.</summary>
    public CallJournal Journal { get; }

    /// <summary>Учётные данные устройства либо null, пока клиент не сопряжён.</summary>
    public DeviceCredentials? Credentials { get; private set; }

    /// <summary>Текущее состояние связи — им питается индикатор в строке состояния.</summary>
    public ChannelStatus Status { get; private set; } = ChannelStatus.NotPaired;

    /// <summary>Состояние изменилось. Событие приходит из фонового потока — в UI марш через диспетчер.</summary>
    public event EventHandler<ChannelStatus>? StatusChanged;

    /// <summary>
    /// Поднять канал. Зовётся и на старте (учётка уже лежит под DPAPI), и сразу после
    /// сопряжения — второй раз прежнее соединение гасится.
    /// </summary>
    public void Start(DeviceCredentials credentials, IShellSurface shell)
    {
        StopChannel();

        Credentials = credentials;
        Api.Credentials = credentials;
        _revoked = false;
        _probedAt = DateTimeOffset.MinValue;

        var channel = new DeviceChannel(
            new DeviceChannelOptions(credentials, ClientInfo.Version), _relay);

        // Шов сеанса рук: обработчику нужен готовый канал, поэтому он собирается здесь —
        // после конструктора канала, но до его подъёма.
        var handler = DesktopAgentSeam.Compose?.Invoke(
            new DesktopAgentContext(Api, Journal, channel, credentials, shell));
        _relay.Bind(channel, handler);

        _channel = channel;
        channel.Start();

        Update(ChannelStatus.Connecting);
        _statusLoop ??= Task.Run(() => StatusLoopAsync(_life.Token));
    }

    /// <summary>Канал вниз: клиент разсопряжён или закрывается.</summary>
    public void Stop()
    {
        StopChannel();
        Credentials = null;
        Api.Credentials = null;
        Update(ChannelStatus.NotPaired);
    }

    private void StopChannel()
    {
        var channel = _channel;
        _channel = null;
        if (channel is null) return;
        // Гасим в фоне: закрытие соединения ждать незачем, а на UI-потоке — тем более.
        _ = Task.Run(async () =>
        {
            try { await channel.DisposeAsync(); }
            catch (Exception) { /* соединение и так уходит */ }
        });
    }

    private async Task StatusLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(StatusPollInterval);
        while (await SafeWaitAsync(timer, ct))
        {
            var channel = _channel;
            if (channel is null)
            {
                Update(ChannelStatus.NotPaired);
                continue;
            }

            if (channel.State == HubConnectionState.Connected)
            {
                _revoked = false;
                Update(ChannelStatus.Connected(Credentials?.DeviceName ?? "устройство"));
                continue;
            }

            await ProbeRevokedAsync(ct);
            Update(_revoked ? ChannelStatus.Revoked : ChannelStatus.Connecting);
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>
    /// Отозвано ли устройство. Спрашиваем самым дешёвым запросом канала: 401 от схемы
    /// устройства означает ровно одно — этот токен сервер больше не признаёт. Сетевую
    /// ошибку отзывом не считаем никогда: без сети «отозвано» было бы враньём.
    /// </summary>
    private async Task ProbeRevokedAsync(CancellationToken ct)
    {
        var credentials = Credentials;
        if (credentials is null) return;
        if (DateTimeOffset.UtcNow - _probedAt < RevokeProbeInterval) return;
        _probedAt = DateTimeOffset.UtcNow;

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, new Uri(new Uri(credentials.ServerUrl), "/api/devices/hands"));
            request.Headers.TryAddWithoutValidation("Authorization", $"Device {credentials.DeviceToken}");
            request.Headers.TryAddWithoutValidation("X-Device-Fingerprint", credentials.Fingerprint);

            using var response = await _http.SendAsync(request, ct);
            _revoked = response.StatusCode == HttpStatusCode.Unauthorized;
        }
        catch (Exception)
        {
            _revoked = false;
        }
    }

    private void Update(ChannelStatus status)
    {
        if (Status.State == status.State && Status.Text == status.Text) return;
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    public async ValueTask DisposeAsync()
    {
        await _life.CancelAsync();
        StopChannel();
        _life.Dispose();
        _http.Dispose();
    }
}
