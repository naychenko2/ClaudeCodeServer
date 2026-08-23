using AiHomeDesktop.Core.Channel;
using AiHomeDesktop.Core.Protocol;

namespace AiHomeDesktop.App.Shell;

/// <summary>
/// Переходник между каналом и сеансом рук. Нужен из-за порядка сборки: канал требует
/// обработчика в конструкторе, а обработчику (координатору вызовов) нужен сам канал —
/// без переходника этот круг не разрывается.
///
/// Второе его дело — честный ответ, когда шов сеанса не зарегистрирован. Молчать нельзя:
/// сервер ждёт ack две секунды, а модель обязана получить исход, а не догадку.
/// </summary>
internal sealed class RelayCallHandler(DeviceApi api) : IDeviceCallHandler
{
    private IDeviceChannel? _channel;
    private IDeviceCallHandler? _inner;

    /// <summary>Подключить обработчик сеанса. inner == null — сборка без грани исполнения.</summary>
    public void Bind(IDeviceChannel channel, IDeviceCallHandler? inner)
    {
        _channel = channel;
        _inner = inner;
    }

    public Task OnCallAsync(DesktopCallCommand command) =>
        _inner?.OnCallAsync(command) ?? RefuseAsync(command);

    public void OnGo(DesktopGoCommand go) => _inner?.OnGo(go);

    public void OnCancel(DesktopCancelCommand cancel) => _inner?.OnCancel(cancel);

    public Task OnConnectedAsync() => _inner?.OnConnectedAsync() ?? Task.CompletedTask;

    private async Task RefuseAsync(DesktopCallCommand command)
    {
        // Ack всё равно уходит первым: сервер обязан понять, что устройство команду
        // получило, — иначе исходом станет no_ack, то есть неправда о причине.
        if (_channel is not null)
        {
            try { await _channel.AckAsync(command.CallId); }
            catch (Exception) { /* канал моргнул — результат ниже всё объяснит */ }
        }

        await api.PostResultAsync(command.CallId, DeviceCallResultBody.Refused(
            DesktopOutcomes.ProtocolError,
            "Эта сборка клиента AI Home Desktop вызовы на устройстве не исполняет: " +
            "грань исполнения в неё не входит. Ни один шаг не применён."));
    }
}
