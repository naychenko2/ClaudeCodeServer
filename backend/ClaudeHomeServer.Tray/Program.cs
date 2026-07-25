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

    private readonly Icon _appIcon;
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

        _appIcon = LoadAppIcon();
        _icon = new NotifyIcon
        {
            Icon = _appIcon,
            Visible = true,
            Text = "AI Home",
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

    // Иконка приложения (appicon.ico рядом с exe) под размер трея; фолбэк — системная.
    private Icon LoadAppIcon()
    {
        try
        {
            var p = Path.Combine(_baseDir, "appicon.ico");
            if (File.Exists(p)) return new Icon(p, SystemInformation.SmallIconSize);
        }
        catch { /* фолбэк ниже */ }
        return SystemIcons.Application;
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
        menu.Items.Add("Сделать бэкап", null, (_, _) => RunBackup());
        menu.Items.Add("Восстановить из бэкапа…", null, (_, _) => RestoreFromBackup());
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
                _icon.ShowBalloonTip(5000, "AI Home",$"Не найден {_cfg.ServerExe}", ToolTipIcon.Error);
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
            _icon.Text = $"AI Home —работает (PID {_server.Id})";
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
        _icon.Text = "AI Home —перезапуск…";
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
        _icon.ShowBalloonTip(2000, "AI Home","Сервер перезапущен", ToolTipIcon.Info);
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

    // Снапшот при живом сервере безопасен: json-сторы пишутся атомарно, а лог событий
    // снимается online-backup API SQLite. Гасить сервер незачем.
    private void RunBackup()
    {
        _icon.ShowBalloonTip(2000, "AI Home", "Снимаю бэкап…", ToolTipIcon.Info);
        Task.Run(() =>
        {
            var (code, output) = RunServerExe(["--backup"], TimeSpan.FromMinutes(5));
            WriteLog($"[tray] бэкап: код={code}");
            _icon.ShowBalloonTip(4000, "AI Home",
                code == 0 ? "Бэкап готов" : $"Бэкап не удался: {LastLine(output)}",
                code == 0 ? ToolTipIcon.Info : ToolTipIcon.Error);
        });
    }

    private void RestoreFromBackup()
    {
        // Runner держит продукт своим супервизором: он поднимет сервер посреди
        // восстановления и подменяемый каталог окажется занят (та же защита, что в deploy80)
        if (Process.GetProcessesByName("ClaudeServerTray").Length > 0)
        {
            MessageBox.Show(
                "Запущен ClaudeCodeServerRunner — он поднимет сервер посреди восстановления.\n" +
                "Выйди из него и повтори.",
                "AI Home", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "Выбери архив бэкапа",
            Filter = "Архивы бэкапа (ccs-*.zip)|ccs-*.zip|Все файлы|*.*",
            InitialDirectory = ResolveBackupDir(),
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;

        var summary = DescribeArchive(dialog.FileName);
        var confirm = MessageBox.Show(
            $"Восстановить данные из архива?\n\n{summary}\n\n" +
            "Сервер будет остановлен, текущие данные сохранятся рядом в папке data.old-…\n" +
            "Секреты и настройки доступа останутся текущими.",
            "Восстановление из бэкапа", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        StopServer();
        // Kill не graceful: файловые хендлы и признак «сервер работает» отпускаются
        // не мгновенно, а восстановление на живом каталоге запрещено гейтом
        WaitForServerLockRelease(TimeSpan.FromSeconds(15));

        var (code, output) = RunServerExe(["--restore", dialog.FileName], TimeSpan.FromMinutes(10));
        WriteLog($"[tray] восстановление: код={code}");

        StartServer();

        MessageBox.Show(
            code == 0
                ? "Данные восстановлены, сервер запущен.\n\nБазы знаний пересоберутся автоматически."
                : $"Восстановление не выполнено:\n\n{LastLine(output)}\n\nСервер запущен на прежних данных.",
            "AI Home", MessageBoxButtons.OK,
            code == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Error);
    }

    private (int Code, string Output) RunServerExe(string[] args, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(_baseDir, _cfg.ServerExe))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = _baseDir,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = _cfg.Environment;

            using var process = Process.Start(psi);
            if (process is null) return (-1, "не удалось запустить процесс");

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
                return (-1, "превышено время ожидания");
            }

            var combined = (stdout + "\n" + stderr).Trim();
            WriteLog(combined);
            return (process.ExitCode, combined);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    // Мьютекс инстанса держится сервером всё время работы (см. InstanceLock в бэкенде)
    private void WaitForServerLockRelease(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsPortListening(_cfg.Port)) return;
            Thread.Sleep(500);
        }
    }

    // Папка архивов задаётся секцией Backup в appsettings.Local.json (настроек бэкапа
    // в data/app-settings.json нет — они намеренно живут только в конфиге).
    private string ResolveBackupDir()
    {
        try
        {
            var configPath = Path.Combine(_baseDir, "appsettings.Local.json");
            if (File.Exists(configPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                if (doc.RootElement.TryGetProperty("Backup", out var backup)
                    && backup.TryGetProperty("Path", out var p)
                    && p.GetString() is { Length: > 0 } configured
                    && Directory.Exists(configured))
                    return configured;
            }
        }
        catch { /* дефолт ниже */ }

        var fallback = Path.Combine(_baseDir, "data", "backups");
        return Directory.Exists(fallback) ? fallback : _baseDir;
    }

    // Состав архива берём из sidecar-манифеста рядом: вскрывать zip ради трёх чисел незачем
    private static string DescribeArchive(string archivePath)
    {
        try
        {
            var sidecar = Path.Combine(
                Path.GetDirectoryName(archivePath) ?? "",
                Path.GetFileNameWithoutExtension(archivePath) + ".manifest.json");
            if (!File.Exists(sidecar)) return Path.GetFileName(archivePath);

            using var doc = JsonDocument.Parse(File.ReadAllText(sidecar));
            var root = doc.RootElement;
            var created = root.TryGetProperty("CreatedAt", out var c) ? c.GetDateTime() : default;
            var s = root.GetProperty("Summary");

            int Get(string name) => s.TryGetProperty(name, out var v) ? v.GetInt32() : 0;

            return $"Снят: {created:dd.MM.yyyy HH:mm}\n" +
                   $"{Get("Chats")} чатов · {Get("Personas")} персон · " +
                   $"{Get("Tasks")} задач · {Get("Notes")} заметок";
        }
        catch { return Path.GetFileName(archivePath); }
    }

    private static string LastLine(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 ? lines[^1].Trim() : "неизвестная ошибка";
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

        MessageBox.Show(text, "AI Home —статистика", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        try { if (_appIcon != SystemIcons.Application) _appIcon.Dispose(); } catch { /* ignore */ }
        try { _log.Dispose(); } catch { /* ignore */ }
        SystemEvents.SessionEnding -= (_, _) => StopServer();
    }
}
