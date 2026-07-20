using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ClaudeHomeServer.Tray;

// Трей-супервизор прод-сервера. Запускает ClaudeHomeServer.exe дочерним процессом БЕЗ
// консольного окна (решает «закрыл консоль — сервер умер»), следит за ним (авто-рестарт
// при неожиданном падении) и даёт иконку в системном трее с меню: открыть в браузере,
// перезапустить, статистика, логи, выход. Stdout/stderr сервера перенаправляются в файл.
static class Program
{
    [STAThread]
    static void Main()
    {
        // Один экземпляр трея на машину: повторный запуск (например, из «Автозагрузки»
        // при уже работающем) молча выходит.
        using var mutex = new Mutex(true, "Global\\ClaudeHomeServerTray", out var isNew);
        if (!isNew) return;

        ApplicationConfiguration.Initialize();
        using var supervisor = new ServerSupervisor();
        supervisor.Start();
        Application.Run();
    }
}

// Конфиг трея (tray.json рядом с exe, опционально). Дефолты рассчитаны на прод-инсталляцию.
sealed class TrayConfig
{
    public string ServerExe { get; set; } = "ClaudeHomeServer.exe";
    public string Environment { get; set; } = "Production";
    public string Url { get; set; } = "https://naychenko.me";
    public int Port { get; set; } = 80;
}

sealed class ServerSupervisor : IDisposable
{
    private readonly string _baseDir = AppContext.BaseDirectory;
    private readonly TrayConfig _cfg;
    private readonly NotifyIcon _icon;
    private readonly StreamWriter _log;
    private readonly object _lock = new();

    private Process? _server;
    private DateTime _startedAt;
    private int _restarts;
    private bool _intentionalStop;   // true — сервер остановлен намеренно (рестарт/выход), авто-рестарт не нужен
    private bool _disposed;

    public ServerSupervisor()
    {
        _cfg = LoadConfig();

        var logDir = Path.Combine(_baseDir, "logs");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "server.log");
        // Простая защита от разрастания: перед открытием срезаем лог > 20 МБ.
        try { if (new FileInfo(logPath) is { Exists: true, Length: > 20 * 1024 * 1024 }) File.Delete(logPath); }
        catch { /* не критично */ }
        _log = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false)) { AutoFlush = true };

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "ClaudeHomeServer",
            ContextMenuStrip = BuildMenu(),
        };
        _icon.DoubleClick += (_, _) => OpenBrowser(_cfg.Url);

        SystemEvents.SessionEnding += (_, _) => StopServer();
        Application.ApplicationExit += (_, _) => Dispose();
    }

    private TrayConfig LoadConfig()
    {
        try
        {
            var path = Path.Combine(_baseDir, "tray.json");
            if (File.Exists(path))
                return JsonSerializer.Deserialize<TrayConfig>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new TrayConfig();
        }
        catch { /* дефолты */ }
        return new TrayConfig();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть в браузере", null, (_, _) => OpenBrowser(_cfg.Url));
        menu.Items.Add("Открыть локально", null, (_, _) => OpenBrowser($"http://localhost:{_cfg.Port}"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Перезапустить сервер", null, (_, _) => RestartServer());
        menu.Items.Add("Статистика…", null, (_, _) => ShowStats());
        menu.Items.Add("Открыть логи", null, (_, _) => OpenLogs());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход (остановить сервер)", null, (_, _) => ExitApp());
        return menu;
    }

    public void Start() => StartServer();

    private void StartServer()
    {
        lock (_lock)
        {
            if (_server is { HasExited: false }) return;

            var exe = Path.Combine(_baseDir, _cfg.ServerExe);
            if (!File.Exists(exe))
            {
                WriteLog($"[tray] НЕ НАЙДЕН сервер: {exe}");
                _icon.ShowBalloonTip(5000, "ClaudeHomeServer", $"Не найден {_cfg.ServerExe}", ToolTipIcon.Error);
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = _baseDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = _cfg.Environment;

            _intentionalStop = false;
            _server = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _server.OutputDataReceived += (_, e) => { if (e.Data is not null) WriteLog(e.Data); };
            _server.ErrorDataReceived += (_, e) => { if (e.Data is not null) WriteLog(e.Data); };
            _server.Exited += OnServerExited;
            _server.Start();
            _server.BeginOutputReadLine();
            _server.BeginErrorReadLine();
            _startedAt = DateTime.Now;

            WriteLog($"[tray] сервер запущен PID={_server.Id} env={_cfg.Environment}");
            _icon.Text = $"ClaudeHomeServer — работает (PID {_server.Id})";
        }
    }

    private void OnServerExited(object? sender, EventArgs e)
    {
        int code = -1;
        try { code = _server?.ExitCode ?? -1; } catch { /* ignore */ }
        WriteLog($"[tray] сервер завершился, код={code}, намеренно={_intentionalStop}");

        if (_intentionalStop || _disposed) return;

        // Неожиданное падение — авто-рестарт через 3с (супервизия).
        _restarts++;
        _icon.Text = "ClaudeHomeServer — перезапуск…";
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            if (!_disposed) StartServer();
        });
    }

    private void RestartServer()
    {
        StopServer();
        StartServer();
        _icon.ShowBalloonTip(2000, "ClaudeHomeServer", "Сервер перезапущен", ToolTipIcon.Info);
    }

    private void StopServer()
    {
        lock (_lock)
        {
            _intentionalStop = true;
            if (_server is null) return;
            try
            {
                if (!_server.HasExited)
                {
                    // Дерево процессов: сервер может держать дочерние node MCP на время хода.
                    _server.Kill(entireProcessTree: true);
                    _server.WaitForExit(5000);
                }
            }
            catch (Exception ex) { WriteLog($"[tray] ошибка остановки: {ex.Message}"); }
            finally { _server.Dispose(); _server = null; }
        }
    }

    private void ShowStats()
    {
        Process? srv;
        lock (_lock) srv = _server;

        var running = srv is { HasExited: false };
        var pid = running ? srv!.Id.ToString() : "—";
        var uptime = running ? (DateTime.Now - _startedAt) : TimeSpan.Zero;
        var listening = IsPortListening(_cfg.Port) ? "да" : "нет";

        var text =
            $"Состояние: {(running ? "работает" : "остановлен")}\n" +
            $"PID: {pid}\n" +
            $"Аптайм: {FormatUptime(uptime)}\n" +
            $"Порт {_cfg.Port} слушается: {listening}\n" +
            $"Перезапусков (авто): {_restarts}\n" +
            $"Окружение: {_cfg.Environment}\n" +
            $"URL: {_cfg.Url}";

        MessageBox.Show(text, "ClaudeHomeServer — статистика", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OpenLogs()
    {
        try
        {
            var logPath = Path.Combine(_baseDir, "logs", "server.log");
            Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
        }
        catch (Exception ex) { WriteLog($"[tray] не удалось открыть логи: {ex.Message}"); }
    }

    private void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { WriteLog($"[tray] не удалось открыть браузер: {ex.Message}"); }
    }

    private void ExitApp()
    {
        StopServer();
        _icon.Visible = false;
        Application.Exit();
    }

    private static bool IsPortListening(int port)
    {
        try
        {
            using var c = new TcpClient();
            var ok = c.ConnectAsync("127.0.0.1", port).Wait(1000);
            return ok && c.Connected;
        }
        catch { return false; }
    }

    private static string FormatUptime(TimeSpan t) =>
        t <= TimeSpan.Zero ? "—" : $"{(int)t.TotalDays}д {t.Hours}ч {t.Minutes}м {t.Seconds}с";

    private void WriteLog(string line)
    {
        lock (_log)
        {
            try { _log.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}"); }
            catch { /* лог не критичен */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopServer();
        try { _icon.Visible = false; _icon.Dispose(); } catch { /* ignore */ }
        try { _log.Dispose(); } catch { /* ignore */ }
        SystemEvents.SessionEnding -= (_, _) => StopServer();
    }
}
