using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using AiHomeDesktop.App.Settings;
using AiHomeDesktop.App.Shell;
using AiHomeDesktop.Core.Abstractions;
using AiHomeDesktop.Core.Channel;
using AiHomeDesktop.Core.Policies;
using AiHomeDesktop.Core.Protocol;
using AiHomeDesktop.Windows.Execution;

namespace AiHomeDesktop.App.Hands;

/// <summary>
/// Руки на этом устройстве целиком: сеанс, лента, тосты, грань исполнения и склейка вызова.
/// Подключается к оболочке через <see cref="DesktopAgentSeam"/> — сама оболочка про руки
/// ничего не знает и файлов её мы не трогаем.
///
/// Регистрация — <see cref="Register"/> в <c>[ModuleInitializer]</c>: шов заполняется до
/// первого подъёма канала. Не зарегистрируйся он — канал всё равно поднимется, а на команду
/// уйдёт честный отказ (см. <c>RelayCallHandler</c>); молчания в ответ на команду не бывает.
/// </summary>
public sealed class DesktopHandsRuntime : IDeviceCallHandler, IAsyncDisposable
{
    private static DesktopHandsRuntime? _current;

    [ModuleInitializer]
    internal static void Register()
    {
        DesktopAgentSeam.Compose = context =>
        {
            // Сопряжение заново поднимает канал второй раз: прежние руки гасим, иначе
            // два опроса сеанса ходили бы на сервер наперегонки. Гасим НЕ дожидаясь: шов
            // зовут с UI-потока, а ожидание тут упиралось бы в него же.
            var previous = _current;
            if (previous is not null) _ = Task.Run(() => previous.DisposeAsync().AsTask());

            _current = new DesktopHandsRuntime(context);
            return _current;
        };

        // Выход из клиента — настоящее закрытие: сеанс гаснет с поводом client_closed.
        // Сворачивание в трей сюда не приходит и сеанс не трогает.
        //
        // Через Task.Run: зовут с UI-потока при выходе, и продолжения внутри StopAsync
        // иначе встали бы в очередь диспетчера, которую этот же поток и держит.
        DesktopAgentSeam.ShellClosing = reason =>
        {
            var runtime = _current;
            if (runtime is null) return;
            Task.Run(() => runtime.Session.StopAsync(reason)).Wait(TimeSpan.FromSeconds(2));
        };
    }

    private readonly DesktopCallCoordinator _calls;
    private readonly HttpClient _http;
    private readonly HandsPanel _panel;

    private DesktopHandsRuntime(DesktopAgentContext context)
    {
        var settings = new SettingsStore().Current;

        // Отдельный HttpClient сеанса: у него базовый адрес и заголовки токена устройства,
        // а токен ставит оболочка — половине «рук» его знать незачем.
        _http = new HttpClient { BaseAddress = new Uri(context.Credentials.ServerUrl.TrimEnd('/') + "/") };
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", $"Device {context.Credentials.DeviceToken}");
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-Device-Fingerprint", context.Credentials.Fingerprint);

        Feed = new HandsActivityFeed();
        Confirmation = new ConfirmationToasts(context.Shell.Window.Dispatcher);
        Session = new HandsSessionManager(
            new HandsApiClient(_http),
            new ShellIndicator(context.Shell, context.Shell.Window.Dispatcher),
            Feed);

        _calls = new DesktopCallCoordinator(
            context.Channel,
            context.Api,
            context.Journal,
            new DesktopExecutor(() => new OpenAllowList(settings.OpenAllowList)),
            Confirmation,
            Feed);

        _panel = new HandsPanel(Session, Feed);
        context.Shell.Window.Dispatcher.Invoke(() => context.Shell.HandsHost.Content = _panel);
    }

    /// <summary>Сеанс рук: очередь заявок, старт с устройства, «Стоп», отсчёт до предела.</summary>
    public HandsSessionManager Session { get; }

    /// <summary>Лента «что ушло в модель»: и события сеанса, и каждый вызов.</summary>
    public HandsActivityFeed Feed { get; }

    /// <summary>Тосты подтверждения — их же гасит отмена вызова сервером.</summary>
    public ConfirmationToasts Confirmation { get; }

    // ---------- события канала ----------

    public Task OnCallAsync(DesktopCallCommand command) => _calls.OnCallAsync(command);

    public void OnGo(DesktopGoCommand go) => _calls.OnGo(go);

    public void OnCancel(DesktopCancelCommand cancel) => _calls.OnCancel(cancel);

    /// <summary>
    /// Канал поднялся: досылаем недоехавшие результаты и перечитываем сеанс. Сеанс после
    /// разрыва НЕ воскресает — сервер гасит его, и человек начинает заново.
    /// </summary>
    public async Task OnConnectedAsync()
    {
        await _calls.OnConnectedAsync();
        await Session.RefreshAsync();
        Session.StartPolling();
    }

    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync();
        _http.Dispose();
    }

    /// <summary>
    /// Индикатор сеанса в трее плюс подсказка, когда пришла заявка.
    ///
    /// В диспетчер уходим НЕ дожидаясь ответа: зовут отсюда из потока опроса, а оболочка
    /// внутри себя переходит на UI-поток блокирующим Invoke — при закрытии клиента эти два
    /// ожидания встретились бы лбами.
    /// </summary>
    private sealed class ShellIndicator(IShellSurface shell, Dispatcher dispatcher) : IHandsIndicator
    {
        private HandsIndicatorState _last = HandsIndicatorState.Idle;

        public void Update(HandsIndicatorState state, string? chatTitle)
        {
            var was = _last;
            _last = state;

            dispatcher.BeginInvoke(() =>
            {
                shell.SetHandsActive(state == HandsIndicatorState.Active);

                // Заявку человек обязан увидеть: она не уходит сама и без него не исполнится.
                if (state == HandsIndicatorState.Requested && was != HandsIndicatorState.Requested)
                    shell.Notify("AI Home — заявка на сеанс",
                        $"Чат «{chatTitle ?? "без названия"}» просит руки на этом компьютере.");
            });
        }
    }
}
