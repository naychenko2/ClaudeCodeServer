using System.Threading;
using System.Windows.Threading;
using AiHomeDesktop.App.Settings;
using AiHomeDesktop.App.Shell;
using AiHomeDesktop.App.Tray;
using AiHomeDesktop.Core.Protocol;
using Application = System.Windows.Application;
using ExitEventArgs = System.Windows.ExitEventArgs;
using StartupEventArgs = System.Windows.StartupEventArgs;

namespace AiHomeDesktop.App;

/// <summary>
/// Composition root клиента: настройки, хранилище токена под DPAPI, канал до сервера,
/// иконка в трее и окно с веб-мордой. Всё собирается руками — контейнера здесь нет и не
/// нужно: сущностей пять, и все они живут ровно столько же, сколько приложение.
///
/// Класс назван DesktopApp, а не App: пространство имён проекта — AiHomeDesktop.App, и
/// одноимённый класс перекрывал бы его при разборе составных имён.
///
/// Грань исполнения (кадр, запуск цели) и сеанс рук подключаются через шов
/// <see cref="DesktopAgentSeam"/>, а не отсюда: оболочка про них ничего не знает.
/// </summary>
public partial class DesktopApp : Application
{
    /// <summary>
    /// Второй экземпляр клиента на той же машине не нужен: у устройства один канал и один
    /// локальный журнал вызовов, а две копии молча делили бы их между собой.
    /// </summary>
    private const string SingleInstanceMutex = @"Local\AiHomeDesktop.SingleInstance";

    private Mutex? _instanceLock;
    private SettingsStore _settings = null!;
    private DpapiSecretStore _secrets = null!;
    private AgentHost _host = null!;
    private TrayIcon _tray = null!;
    private MainWindow _window = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceLock = new Mutex(initiallyOwned: true, SingleInstanceMutex, out var first);
        if (!first)
        {
            // Окно уже открытой копии поднять неоткуда — просто уходим молча
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _settings = new SettingsStore();
        _secrets = new DpapiSecretStore();
        _host = new AgentHost();
        _tray = new TrayIcon();
        _window = new MainWindow(_host, _settings, _secrets, _tray);

        _tray.ShowRequested += (_, _) => _window.ShowFromTray();
        _tray.HideRequested += (_, _) => _window.HideToTray();
        _tray.ExitRequested += (_, _) => Shutdown();

        _window.Show();
        _ = _window.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Выход — настоящее закрытие оболочки, и сервер обязан узнать причину: сеанс рук
        // гаснет по закрытию окна клиента, а сворачивание в трей таким закрытием не считается
        try
        {
            DesktopAgentSeam.ShellClosing?.Invoke(DesktopHandsStopReasons.ClientClosed);
        }
        catch (Exception ex)
        {
            Log("Сеанс рук не удалось погасить при выходе", ex);
        }

        if (_window is not null) _window.AllowClose = true;
        _tray?.Dispose();

        // Ждём канал недолго: гасить его дольше пары секунд незачем — сервер и так
        // увидит разрыв соединения
        try { _host?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); }
        catch (Exception ex) { Log("Канал не закрылся штатно", ex); }

        _instanceLock?.Dispose();
        base.OnExit(e);
    }

    // Необработанное исключение в UI-потоке клиент не роняет: он живёт в трее часами, и
    // падение окна означало бы молча потерянный канал
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("Необработанная ошибка интерфейса", e.Exception);
        e.Handled = true;
        _tray?.Notify("AI Home Desktop", "В интерфейсе клиента произошла ошибка. Подробности — в client.log.");
    }

    private static void Log(string message, Exception? error = null)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:O} {message}{(error is null ? "" : $": {error}")}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(ClientPaths.Local, "client.log"), line);
        }
        catch (Exception)
        {
            // Журнал — не та вещь, ради которой стоит падать
        }
    }
}
