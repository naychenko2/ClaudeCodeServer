using System.Net.Http;
using System.Windows.Threading;
using AiHomeDesktop.App.Execution;

namespace AiHomeDesktop.App.Hands;

/// <summary>
/// Сборка руками: сеанс, лента, тосты и склейка вызова в одном месте. Оболочке остаётся
/// создать это и раздать события канала — composition root не должен знать, из чего руки
/// состоят внутри.
///
/// Ожидаемые строки composition root (App.xaml.cs / AppHost):
/// <code>
/// var hands = new DesktopHandsRuntime(deviceHttp, channel, callsApi, executor, journal, tray, Dispatcher, log: Log);
/// channel.CallReceived   += c  => hands.Calls.OnCallAsync(c);
/// channel.GoReceived     += g  => hands.Calls.OnGo(g.CallId, g.DeadlineSeconds);
/// channel.CancelReceived += c  => hands.Calls.OnCancelAsync(c.CallId, c.Reason);
/// channel.Connected      += () => hands.OnConnectedAsync();
/// channel.Disconnected   += () => hands.OnDisconnectedAsync();
/// // окно оболочки: Closing → await hands.OnWindowClosedAsync(); сворачивание в трей — НЕ закрытие
/// </code>
/// </summary>
public sealed class DesktopHandsRuntime : IAsyncDisposable
{
    /// <param name="deviceHttp">HttpClient с базовым адресом сервера и заголовками токена устройства.</param>
    /// <param name="channel">Донесения устройства серверу по каналу хаба.</param>
    /// <param name="callsApi">Отдача результата вызова HTTP-POST'ом.</param>
    /// <param name="executor">Исполнитель грани на самой машине.</param>
    /// <param name="journal">Локальный журнал вызовов по callId.</param>
    /// <param name="indicator">Иконка трея: отдельный вид на время активного сеанса.</param>
    /// <param name="dispatcher">Поток UI: тосты показываются только на нём.</param>
    public DesktopHandsRuntime(
        HttpClient deviceHttp,
        IDeviceChannelClient channel,
        IDeviceCallsApi callsApi,
        IDesktopExecutor executor,
        ICallJournal journal,
        IHandsIndicator indicator,
        Dispatcher dispatcher,
        TimeProvider? timeProvider = null,
        Action<string, Exception?>? log = null)
    {
        Feed = new HandsActivityFeed();
        Confirmation = new ConfirmationToasts(dispatcher);
        Session = new HandsSessionManager(new HandsApiClient(deviceHttp), indicator, Feed, timeProvider, log);
        Calls = new CallPipeline(channel, callsApi, executor, journal, Confirmation, Feed, timeProvider, log);
    }

    /// <summary>Сеанс рук: очередь заявок, старт, стоп, отсчёт до предела.</summary>
    public HandsSessionManager Session { get; }

    /// <summary>Лента «что ушло в модель» — окно клиента показывает её как есть.</summary>
    public HandsActivityFeed Feed { get; }

    /// <summary>Склейка вызова: её события раздаёт канал.</summary>
    public CallPipeline Calls { get; }

    /// <summary>Тосты подтверждения — отдельно на случай, если оболочке нужно их погасить.</summary>
    public ICallConfirmation Confirmation { get; }

    /// <summary>
    /// Канал поднялся: досылаем недоехавшие результаты и перечитываем сеанс. Сеанс после
    /// разрыва НЕ воскресает — сервер гасит его, и человек начинает заново.
    /// </summary>
    public async Task OnConnectedAsync(CancellationToken ct = default)
    {
        await Calls.FlushJournalAsync(ct);
        await Session.RefreshAsync(ct);
        Session.StartPolling();
    }

    /// <summary>
    /// Канал оборвался. Это штатное состояние, а не авария: закрываем висящие тосты, чтобы
    /// человек не подтверждал вызов, которого на сервере уже нет.
    /// </summary>
    public Task OnDisconnectedAsync(CancellationToken ct = default) =>
        Calls.CancelAllAsync("связь с сервером потеряна", ct);

    /// <summary>
    /// Окно оболочки закрывают — сеанс гаснет с поводом <c>client_closed</c>. Сворачивание в
    /// трей сюда не относится: оно закрытием не считается, и руки остаются у чата.
    /// </summary>
    public async Task OnWindowClosedAsync(CancellationToken ct = default)
    {
        await Calls.CancelAllAsync("окно клиента закрыто", ct);
        await Session.StopOnWindowClosedAsync(ct);
    }

    public ValueTask DisposeAsync() => Session.DisposeAsync();
}
