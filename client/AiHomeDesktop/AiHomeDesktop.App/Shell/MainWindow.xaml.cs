using System.ComponentModel;
using System.Diagnostics;
using AiHomeDesktop.App.Pairing;
using AiHomeDesktop.App.Settings;
using AiHomeDesktop.App.Tray;
using AiHomeDesktop.Core.Abstractions;
using AiHomeDesktop.Core.Protocol;
using Microsoft.Web.WebView2.Core;
using Brush = System.Windows.Media.Brush;
using ContentControl = System.Windows.Controls.ContentControl;
using RoutedEventArgs = System.Windows.RoutedEventArgs;
using Visibility = System.Windows.Visibility;
using Window = System.Windows.Window;
using WindowState = System.Windows.WindowState;

namespace AiHomeDesktop.App.Shell;

/// <summary>
/// Окно клиента: веб-морда сервера в WebView2 плюс строка состояния связи.
///
/// Клиент — НЕ второй сервер и не второй интерфейс: своей базы, своего API и своей копии
/// данных у него нет, в WebView2 открыта та же веб-морда, что в браузере.
///
/// Правила WebView2 здесь жёсткие (ADR-008, «Аутентификация и транспорт»): хост-объектов
/// страница не получает, <c>WebMessage</c> выключен явно, скриптов в страницу клиент не
/// вставляет. Мост «страница → нативная часть» не сделан не по недосмотру: он превратил бы
/// содержимое веб-страницы в команды на этой машине.
///
/// Профиль WebView2 постоянный (%LOCALAPPDATA%\AiHomeDesktop\WebView2) — иначе вход в
/// веб-морду приходилось бы повторять на каждом запуске. Плата названа честно: вход
/// владельца лежит на диске этой машины ровно так же, как у обычного браузера.
/// </summary>
public partial class MainWindow : Window, IShellSurface
{
    private readonly AgentHost _host;
    private readonly SettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly TrayIcon _tray;

    private bool _trayHintShown;
    private bool _webReady;
    private string? _serverUrl;

    public MainWindow(AgentHost host, SettingsStore settings, ISecretStore secrets, TrayIcon tray)
    {
        _host = host;
        _settings = settings;
        _secrets = secrets;
        _tray = tray;

        InitializeComponent();

        _host.StatusChanged += OnHostStatusChanged;
    }

    /// <summary>Настоящее закрытие (выход из трея): без него окно уходит в трей, а не гаснет.</summary>
    public bool AllowClose { get; set; }

    Window IShellSurface.Window => this;

    ContentControl IShellSurface.HandsHost => HandsHost;

    void IShellSurface.SetHandsActive(bool active) => Dispatcher.Invoke(() => _tray.SetHandsActive(active));

    void IShellSurface.ShowWindow() => Dispatcher.Invoke(ShowFromTray);

    void IShellSurface.Notify(string title, string text) => Dispatcher.Invoke(() => _tray.Notify(title, text));

    /// <summary>
    /// Старт оболочки: сопряжённый клиент сам поднимает канал и открывает веб-морду,
    /// несопряжённый показывает экран сопряжения.
    /// </summary>
    public async Task InitializeAsync()
    {
        ApplyStatus(_host.Status);

        var credentials = _secrets.Read();
        if (credentials is null)
        {
            ShowPairing();
            return;
        }

        await StartPairedAsync(credentials);
    }

    /// <summary>Поднять окно из трея — в том числе по заявке на сеанс рук.</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    /// <summary>
    /// Спрятать окно в трей. Это НЕ закрытие окна оболочки: сеанс рук на сервере от
    /// сворачивания не гаснет, и человеку об этом говорится прямо.
    /// </summary>
    public void HideToTray()
    {
        Hide();
        if (_trayHintShown) return;
        _trayHintShown = true;
        _tray.Notify(
            "AI Home Desktop работает дальше",
            "Клиент живёт в трее и остаётся на связи. Сеанс рук от сворачивания окна не гаснет — " +
            "его гасят «Стоп» и выход из клиента.");
    }

    private async Task StartPairedAsync(DeviceCredentials credentials)
    {
        PairingHost.Content = null;
        PairingHost.Visibility = Visibility.Collapsed;
        RepairButton.Visibility = Visibility.Visible;
        _serverUrl = credentials.ServerUrl;

        // Канал поднимаем ДО веб-морды: устройство обязано появиться на связи даже тогда,
        // когда страница не открылась
        _host.Start(credentials, this);

        await LoadWebAsync();
    }

    private void ShowPairing()
    {
        var view = new PairingView(_host.Api, _settings.Current.ServerUrl, _settings.Current.DeviceName);
        view.Paired += OnPaired;

        PairingHost.Content = view;
        PairingHost.Visibility = Visibility.Visible;
        LoadingPane.Visibility = Visibility.Collapsed;
        LoadErrorPane.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Collapsed;
        RepairButton.Visibility = Visibility.Collapsed;

        ApplyStatus(ChannelStatus.NotPaired);
        view.FocusFirstField();
    }

    private async void OnPaired(object? sender, DeviceCredentials credentials)
    {
        // Секрет — под DPAPI, всё остальное — в обычные настройки: адрес и имя человек
        // должен видеть и править руками
        _secrets.Save(credentials);
        _settings.Save(new ClientSettings
        {
            ServerUrl = credentials.ServerUrl,
            DeviceName = credentials.DeviceName
        });

        await StartPairedAsync(credentials);
    }

    private async Task LoadWebAsync()
    {
        if (string.IsNullOrEmpty(_serverUrl)) return;

        LoadingText.Text = "Открываю веб-морду…";
        LoadingPane.Visibility = Visibility.Visible;
        LoadErrorPane.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Collapsed;

        if (!await EnsureWebViewAsync()) return;

        try
        {
            var target = new Uri(_serverUrl);
            if (Web.Source == target) Web.Reload();
            else Web.Source = target;
        }
        catch (Exception ex)
        {
            ShowLoadError($"Адрес сервера не открывается: {ex.Message}");
        }
    }

    private async Task<bool> EnsureWebViewAsync()
    {
        if (_webReady) return true;

        try
        {
            // Профиль в %LOCALAPPDATA%: логин переживает перезапуск клиента
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: ClientPaths.WebViewProfile);
            await Web.EnsureCoreWebView2Async(environment);

            var core = Web.CoreWebView2;
            // Мост со страницей закрыт явно: содержимое веб-страницы не должно иметь
            // способа позвать нативную часть клиента
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsStatusBarEnabled = false;

            core.NewWindowRequested += OnNewWindowRequested;
            Web.NavigationCompleted += OnNavigationCompleted;

            _webReady = true;
            return true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowLoadError(
                "На этой машине нет среды Microsoft Edge WebView2. Установите её — без неё " +
                "клиент не покажет веб-морду. Канал устройства при этом работает.");
            return false;
        }
        catch (Exception ex)
        {
            ShowLoadError($"WebView2 не поднялся: {ex.Message}");
            return false;
        }
    }

    // Ссылка «в новом окне» уходит во внешний браузер: окно клиента — не место для
    // отдельных вкладок, а безрамочное окно WebView2 человеку неуправляемо
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Внешний браузер не открылся — ссылка просто не откроется, роняться незачем
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            LoadingPane.Visibility = Visibility.Collapsed;
            LoadErrorPane.Visibility = Visibility.Collapsed;
            Web.Visibility = Visibility.Visible;
            return;
        }

        ShowLoadError(
            $"Веб-морда по адресу {_serverUrl} не открылась ({e.WebErrorStatus}). " +
            "Канал устройства продолжает попытки сам.");
    }

    private void ShowLoadError(string text)
    {
        LoadErrorText.Text = text;
        LoadErrorPane.Visibility = Visibility.Visible;
        LoadingPane.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Collapsed;
    }

    private void OnRetryClick(object sender, RoutedEventArgs e) => _ = LoadWebAsync();

    private void OnHideClick(object sender, RoutedEventArgs e) => HideToTray();

    private void OnRepairClick(object sender, RoutedEventArgs e)
    {
        // Сопряжение заново: канал вниз, токен с диска долой. Прежняя запись устройства
        // на сервере живёт до отзыва — повторное сопряжение той же машины под тем же
        // именем вращает её токен, а не плодит вторую
        _host.Stop();
        _secrets.Clear();
        _serverUrl = null;
        ShowPairing();
    }

    private void OnHostStatusChanged(object? sender, ChannelStatus status) =>
        Dispatcher.Invoke(() => ApplyStatus(status));

    private void ApplyStatus(ChannelStatus status)
    {
        StatusText.Text = status.Text;
        StatusDetail.Text = status.Detail ?? "";
        StatusDetail.ToolTip = status.Detail;
        StatusDot.Fill = StatusBrush(status.State);
    }

    private Brush StatusBrush(ChannelState state) => (Brush)FindResource(state switch
    {
        ChannelState.Connected => "Success",
        ChannelState.Connecting => "Warning",
        ChannelState.Revoked => "Danger",
        _ => "TextMuted"
    });

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            // Крестик — не выход: клиент живёт в трее, и сеанс рук на сервере от этого
            // не гаснет (ADR-008: «жизнь в трее не считается закрытием окна»)
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnClosing(e);
    }
}
