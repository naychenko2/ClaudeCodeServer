using System.Reflection;
using System.Runtime.InteropServices;
using ClaudeHomeServer.DeviceAgent.Cli;
using ClaudeHomeServer.DeviceAgent.Composition;
using ClaudeHomeServer.DeviceAgent.Credentials;
using ClaudeHomeServer.DeviceAgent.Exec;
using ClaudeHomeServer.DeviceAgent.Hosting;
using ClaudeHomeServer.DeviceAgent.Install;
using ClaudeHomeServer.DeviceAgent.Pairing;
using ClaudeHomeServer.DeviceAgent.Processes;
using ClaudeHomeServer.DeviceAgent.Relay;
using ClaudeHomeServer.DeviceAgent.Sidecar;
using ClaudeHomeServer.DeviceAgent.Supervision;
using ClaudeHomeServer.DeviceAgent.Update;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Git;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.DeviceAgent;

/// <summary>
/// Точка входа агента устройства (ADR-016).
///
///   ai-home-agent install --server https://host --code ABCD2345 [--name "Ноутбук"] [--always-on]
///   ai-home-agent uninstall [--purge]
///   ai-home-agent pair --server https://host --code ABCD2345 [--name "Ноутбук"] [--always-on]
///   ai-home-agent roots add ПУТЬ [--force] | roots remove ПУТЬ | roots list
///   ai-home-agent supervise
///   ai-home-agent [run]
///   ai-home-agent --version
///
/// Класс назван не <c>Program</c> и без top-level statements: иначе глобальный
/// <c>Program</c> агента столкнулся бы с <c>Program</c> сервера в тестах, видящих обе сборки.
/// </summary>
public static class AgentProgram
{
    public static async Task<int> Main(string[] args)
    {
        // До каталогов и хранилища токена: версию спрашивают установщик и скрипты, побочных
        // эффектов у вопроса быть не должно
        if (args is ["--version"])
        {
            Console.WriteLine(Version);
            return 0;
        }

        // Дочерний супервизора гибнет вместе с ним: взводится до всего остального
        var command = args.FirstOrDefault() ?? "run";
        if (command == "run" && !SupervisedRun.ArmParentDeath()) return 0;

        var paths = AgentPaths.ForCurrentUser();
        if (command == "supervise") return await SuperviseAsync(paths);

        using var loggers = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
            .SetMinimumLevel(LogLevel.Information));
        var log = loggers.CreateLogger("ai-home-agent");

        try
        {
            paths.Ensure();
            return command switch
            {
                "install" => await InstallAsync(args[1..], paths),
                "uninstall" when args[1..] is [] or ["--purge"] => await UninstallAsync(args.Contains("--purge"), paths),
                "pair" => await PairAsync(args[1..], paths, log),
                "roots" => Roots(args[1..], paths),
                "run" => await RunAsync(paths, loggers, log),
                _ => Usage(),
            };
        }
        catch (Exception e) when (e is TokenStoreException or PairingException)
        {
            log.LogError("{Message}", e.Message);
            return 2;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("ai-home-agent install --server https://host --code КОД [--name ИМЯ] [--always-on]");
        Console.Error.WriteLine("ai-home-agent uninstall [--purge]");
        Console.Error.WriteLine("ai-home-agent pair --server https://host --code КОД [--name ИМЯ] [--always-on]");
        Console.Error.WriteLine("ai-home-agent roots add ПУТЬ [--force]");
        Console.Error.WriteLine("ai-home-agent roots remove ПУТЬ");
        Console.Error.WriteLine("ai-home-agent roots list");
        Console.Error.WriteLine("ai-home-agent supervise");
        Console.Error.WriteLine("ai-home-agent [run]");
        Console.Error.WriteLine("ai-home-agent --version");
        return 64;
    }

    /// <summary>Разбор <c>--server --code [--name] [--always-on]</c>; null — аргументы не те.</summary>
    internal static InstallRequest? ParsePairing(string[] args)
    {
        var values = new Dictionary<string, string>();
        var alwaysOn = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--always-on":
                    alwaysOn = true;
                    break;
                case "--server" or "--code" or "--name" when i + 1 < args.Length && !values.ContainsKey(args[i]):
                    values[args[i]] = args[++i];
                    break;
                default:
                    return null;
            }
        }
        if (!values.TryGetValue("--server", out var server) || !values.TryGetValue("--code", out var code)) return null;
        if (!Uri.TryCreate(server.EndsWith('/') ? server : server + "/", UriKind.Absolute, out var serverUri)) return null;
        return new InstallRequest(serverUri, code, values.GetValueOrDefault("--name") ?? Environment.MachineName, alwaysOn);
    }

    private static async Task<DeviceRegistration> PairWithServerAsync(InstallRequest request, IDeviceTokenStore store)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        return await new PairingClient(http).PairAsync(request.Server, request.Code, request.DeviceName, Version, store);
    }

    private static async Task<int> InstallAsync(string[] args, AgentPaths paths)
    {
        if (ParsePairing(args) is not { } request) return Usage();
        if (AgentLayout.OwnVersion() is not { } own)
        {
            Console.Error.WriteLine("install запускается из каталога versions/{версия} — его готовит скрипт установки (/agent/install.ps1, /agent/install.sh)");
            return InstallExitCodes.Failed;
        }

        var layout = AgentLayout.Resolve(paths);
        var installer = new AgentInstaller(paths, layout, own, Autostarts.ForCurrentOs(layout),
            new PidFileSupervisorControl(layout), PairWithServerAsync, Console.Out);
        try
        {
            return await installer.RunAsync(request, CancellationToken.None);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Установка не завершена: {e.Message}");
            return InstallExitCodes.Failed;
        }
    }

    private static async Task<int> UninstallAsync(bool purge, AgentPaths paths)
    {
        var layout = AgentLayout.Resolve(paths);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var uninstaller = new AgentUninstaller(paths, layout, Autostarts.ForCurrentOs(layout), new PidFileSupervisorControl(layout),
            new SelfRevokeClient(http), kind => DeviceTokenStores.Open(kind, paths.ConfigDirectory), Console.Out);
        return await uninstaller.RunAsync(purge, CancellationToken.None);
    }

    /// <summary>
    /// Супервизор (Р7): один на установку, журнал — в {root}/logs, дочерний — <c>run</c>
    /// активной версии. SIGTERM и Ctrl+C гасят дочерний вежливо.
    /// </summary>
    private static async Task<int> SuperviseAsync(AgentPaths paths)
    {
        var detached = OperatingSystem.IsWindows() && ConsoleDetach.IfSoleOwner();
        var layout = AgentLayout.Resolve(paths);
        var supervisorLog = new RotatingFileLog(Path.Combine(layout.LogDirectory, "supervisor.log"));
        var agentLog = new RotatingFileLog(Path.Combine(layout.LogDirectory, "agent.log"));

        using var loggers = LoggerFactory.Create(b =>
        {
            // Отцепившемуся от консоли писать в неё нечем — только файл
            if (!detached) b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
            b.AddProvider(new FileLoggerProvider(supervisorLog)).SetMinimumLevel(LogLevel.Information);
        });
        var log = loggers.CreateLogger("ai-home-agent supervise");

        using var single = SupervisorLock.TryAcquire(layout);
        if (single is null)
        {
            log.LogInformation("Супервизор этой установки уже работает ({Root}) — выхожу", layout.Root);
            return 0;
        }
        log.LogInformation("Супервизор {Version} стартовал из {Dir}, корень {Root}", Version, AppContext.BaseDirectory, layout.Root);

        using var stop = new CancellationTokenSource();
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

        var autostart = Autostarts.ForCurrentOs(layout);
        var supervisor = new AgentSupervisor(layout, new ProcessChildLauncher(agentLog.Append), autostart.Repoint, log);
        return await supervisor.RunAsync(stop.Token);
    }

    // Корни, под которыми агент открывает файлы проектов: правит только человек на машине
    private static int Roots(string[] args, AgentPaths paths)
    {
        var roots = new AgentRootsStore(paths.RootsFile);
        switch (args)
        {
            case ["list"] or []:
                foreach (var r in roots.Roots) Console.WriteLine(r);
                return 0;
            case ["add", ..] when args[1..].Where(a => a != "--force").ToArray() is [var path]:
                Console.Error.WriteLine("Внимание: " + AgentRootsStore.SharedWriteWarning);
                try { roots.Add(path, force: args.Contains("--force")); }
                catch (SharedRootException e)
                {
                    Console.Error.WriteLine(e.Message);
                    return 1;
                }
                Console.WriteLine($"Разрешён корень {Path.GetFullPath(path)}");
                return 0;
            case ["remove", var path]:
                Console.WriteLine(roots.Remove(path) ? "Корень убран" : "Такого корня нет");
                return 0;
            default:
                return Usage();
        }
    }

    private static string Version =>
        typeof(AgentProgram).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    private static async Task<int> PairAsync(string[] args, AgentPaths paths, ILogger log)
    {
        if (ParsePairing(args) is not { } request) return Usage();
        var (store, kind) = DeviceTokenStores.Choose(paths.ConfigDirectory, request.AlwaysOn);
        var registration = await PairWithServerAsync(request, store) with { TokenStore = kind };
        registration.Save(paths.RegistrationFile);
        log.LogInformation("Устройство «{Name}» сопряжено с {Server}; токен — {Store}",
            registration.DeviceName, registration.ServerUrl, store.Describe);
        return 0;
    }

    private static async Task<int> RunAsync(AgentPaths paths, ILoggerFactory loggers, ILogger log)
    {
        var registration = DeviceRegistration.Load(paths.RegistrationFile);
        if (registration is null)
        {
            log.LogError("Агент не сопряжён: выпусти код в веб-интерфейсе и выполни «ai-home-agent pair --server … --code …»");
            return 2;
        }
        var store = DeviceTokenStores.Open(registration.TokenStore, paths.ConfigDirectory);
        var token = store.Read(registration.DeviceId);
        if (token is null)
        {
            log.LogError("Токена устройства нет ({Store}): выполни сопряжение заново", store.Describe);
            return 2;
        }
        var device = new DeviceIdentity(new Uri(registration.ServerUrl), token, registration.Fingerprint);

        // Хвосты прошлой жизни агента: ходы, пережившие его, добиваются до первого нового хода
        var journal = new TurnJournal(paths.JournalDirectory, loggers.CreateLogger<TurnJournal>());
        var swept = journal.SweepLeftovers(new SessionFileJanitor(paths.CliProfile, log));
        if (swept > 0) log.LogWarning("Добито ходов, переживших прошлый запуск агента: {Count}", swept);

        using var stop = new CancellationTokenSource();
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

        // Всё, что держит переключение на новую версию: ходы, ретранслятор, терминалы, превью
        var activity = new ActivityRegistry();

        using var cliHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var managedCli = new ManagedCli(paths.CliRoot, new HttpCliDistribution(cliHttp), CliPlatform.Detect(),
            logger: loggers.CreateLogger<ManagedCli>());
        var cliLoop = managedCli.RunAsync(stop.Token);

        var grants = new TurnGrants();
        await using var sidecar = await SidecarHost.StartAsync(grants, device, loggers, ct: stop.Token);
        log.LogInformation("Сайдкар слушает {Url}", sidecar.Url);

        // Одна политика корней на исполнение ходов и на файлы проектов (ADR-016 §5)
        var policy = new AgentPathPolicy(new AgentRootsStore(paths.RootsFile));
        var executor = new TurnExecutor(
            new ExecOptions
            {
                TurnsRoot = paths.TurnsRoot, ConfigDirectory = paths.CliProfile, SidecarUrl = () => sidecar.Url,
                PathPolicy = policy,
            },
            new ManagedCliLeaseSource(managedCli), grants, journal, loggers.CreateLogger<TurnExecutor>());
        AppDomain.CurrentDomain.ProcessExit += (_, _) => executor.KillAll();

        await using var control = new HubControlConnection(device, log);

        // Вторая композиция файловых вертикалей (задача 4.2): та же Files и Git, что на
        // сервере, за политикой корней машины; localhost-API — только для веб-морды сервера.
        // Терминалы и дев-серверы (задача 4.3) живут группой/Job Object в журнале ходов:
        // переживших агента добьёт зачистка при следующем старте (SweepLeftovers выше)
        var launchers = new AgentLauncherFactory(journal, activity);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => launchers.KillAll();
        var git = new GitService(launchers, loggers.CreateLogger<GitService>());
        var projectFiles = new AgentProjectFiles(new FileService(git, logger: loggers.CreateLogger<FileService>()), policy);
        using var watchers = new AgentFileWatchers(control, loggers.CreateLogger<AgentFileWatchers>());
        var tickets = new AgentTicketCache(control);
        var port = int.TryParse(Environment.GetEnvironmentVariable("AI_HOME_AGENT_PORT"), out var p) ? p : DeviceAgentApi.DefaultPort;
        var previewPort = int.TryParse(Environment.GetEnvironmentVariable("AI_HOME_AGENT_PREVIEW_PORT"), out var pp)
            ? pp
            : DeviceAgentApi.DefaultPreviewPort;
        await using var localApi = LocalApi.Build(
            new LocalApiOptions(port, LocalApiOptions.OriginOf(registration.ServerUrl), Version),
            projectFiles, git, tickets, watchers, loggers,
            workbench: new AgentWorkbench(paths.DataDirectory, paths.CliProfile, previewPort, launchers));
        try
        {
            await localApi.StartAsync(stop.Token);
            log.LogInformation("Проекты: http://127.0.0.1:{Port}, превью — :{PreviewPort}, разрешённых корней — {Count}",
                port, previewPort, policy.RootCount);
        }
        // Порт занят — ходы работают и без файлов: агент не падает, веб-морда покажет «агент не найден»
        catch (IOException e)
        {
            log.LogError("localhost-API не поднялся на порту {Port}: {Error}", port, e.Message);
        }
        // Ретранслятор чтения для других устройств (задача 5.1) — поверх тех же файлов и git
        var relay = new RelayHandler(projectFiles, git, loggers.CreateLogger<RelayHandler>());
        var updater = CreateUpdater(paths, device, activity, cliHttp, loggers, log);
        var updateLoop = updater is null ? new TaskCompletionSource<bool>().Task : RunUpdaterAsync(updater, log, stop.Token);
        await using var coordinator = new AgentCoordinator(control, new ManagedCliHarness(managedCli),
            new ExecSocketConnector(device), executor.RunAsync, Version, log, runRelay: relay.RunAsync,
            updates: updater, activity: activity);

        var exitCode = 0;
        try
        {
            await control.ConnectAsync(stop.Token);
            await coordinator.HelloAsync();
            // Успешный ack — версия здорова: супервизор её не откатит, install дождался
            SupervisedRun.MarkHealthy();
            log.LogInformation("Агент на связи с {Server} как «{Name}»", registration.ServerUrl, registration.DeviceName);
            // Цикл обновления завершается true, только переключив active: выходим с 75, супервизор поднимет новую версию
            if (await updateLoop.WaitAsync(stop.Token)) exitCode = SupervisorContract.SwitchExitCode;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            await stop.CancelAsync();
            executor.KillAll();
            launchers.KillAll();
            try { await cliLoop; } catch (OperationCanceledException) { }
        }

        log.LogInformation(exitCode == 0 ? "Агент остановлен" : "Агент остановлен для перехода на новую версию");
        return exitCode;
    }

    /// <summary>
    /// Самообновление — только у установленного агента под супервизором: собранный из
    /// исходников или запущенный руками выйти с 75 некому, его обновляет человек.
    /// </summary>
    private static AgentUpdater? CreateUpdater(AgentPaths paths, IDeviceIdentity device, ActivityRegistry activity,
        HttpClient http, ILoggerFactory loggers, ILogger log)
    {
        if (AgentLayout.OwnVersion() is null || !SupervisedRun.IsSupervised)
        {
            log.LogInformation("Самообновление выключено: агент запущен не супервизором из каталога versions/");
            return null;
        }
        var layout = AgentLayout.Resolve(paths);
        var autostart = Autostarts.ForCurrentOs(layout);
        return new AgentUpdater(layout, Version, AgentCoordinator.RidName, new HttpAgentArchiveSource(http, device.ServerUri),
            activity, autostart.Repoint, () => SupervisedRun.SupervisorVersion(layout),
            log: loggers.CreateLogger<AgentUpdater>());
    }

    // Сбой самого цикла обновления агента не валит: работаем на текущей версии дальше
    private static async Task<bool> RunUpdaterAsync(AgentUpdater updater, ILogger log, CancellationToken ct)
    {
        try
        {
            return await updater.RunAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Цикл самообновления агента упал — работаю на текущей версии");
            await Task.Delay(Timeout.Infinite, ct);
            return false;
        }
    }

    private sealed record DeviceIdentity(Uri ServerUri, string DeviceToken, string Fingerprint) : IDeviceIdentity
    {
        // Токен не печатается даже случайно
        public override string ToString() => $"DeviceIdentity {{ ServerUri = {ServerUri} }}";
    }
}
