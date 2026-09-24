using System.Reflection;
using System.Runtime.InteropServices;
using ClaudeHomeServer.DeviceAgent.Cli;
using ClaudeHomeServer.DeviceAgent.Credentials;
using ClaudeHomeServer.DeviceAgent.Exec;
using ClaudeHomeServer.DeviceAgent.Hosting;
using ClaudeHomeServer.DeviceAgent.Pairing;
using ClaudeHomeServer.DeviceAgent.Processes;
using ClaudeHomeServer.DeviceAgent.Sidecar;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.DeviceAgent;

/// <summary>
/// Точка входа агента устройства (ADR-016).
///
///   ai-home-agent pair --server https://host --code ABCD2345 [--name "Ноутбук"]
///   ai-home-agent [run]
///
/// Класс назван не <c>Program</c> и без top-level statements: иначе глобальный
/// <c>Program</c> агента столкнулся бы с <c>Program</c> сервера в тестах, видящих обе сборки.
/// </summary>
public static class AgentProgram
{
    public static async Task<int> Main(string[] args)
    {
        using var loggers = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
            .SetMinimumLevel(LogLevel.Information));
        var log = loggers.CreateLogger("ai-home-agent");
        var paths = AgentPaths.ForCurrentUser();

        try
        {
            paths.Ensure();
            var store = DeviceTokenStores.ForCurrentOs(paths.ConfigDirectory);
            return (args.FirstOrDefault() ?? "run") switch
            {
                "pair" => await PairAsync(args[1..], paths, store, log),
                "run" => await RunAsync(paths, store, loggers, log),
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
        Console.Error.WriteLine("ai-home-agent pair --server https://host --code КОД [--name ИМЯ]");
        Console.Error.WriteLine("ai-home-agent [run]");
        return 64;
    }

    private static string Version =>
        typeof(AgentProgram).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    private static async Task<int> PairAsync(string[] args, AgentPaths paths, IDeviceTokenStore store, ILogger log)
    {
        string? Option(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        if (Option("--server") is not { } server || Option("--code") is not { } code) return Usage();
        var name = Option("--name") ?? Environment.MachineName;
        var serverUri = new Uri(server.EndsWith('/') ? server : server + "/");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var registration = await new PairingClient(http).PairAsync(serverUri, code, name, Version, store);
        registration.Save(paths.RegistrationFile);
        log.LogInformation("Устройство «{Name}» сопряжено с {Server}; токен — {Store}",
            registration.DeviceName, registration.ServerUrl, store.Describe);
        return 0;
    }

    private static async Task<int> RunAsync(AgentPaths paths, IDeviceTokenStore store, ILoggerFactory loggers, ILogger log)
    {
        var registration = DeviceRegistration.Load(paths.RegistrationFile);
        if (registration is null)
        {
            log.LogError("Агент не сопряжён: выпусти код в веб-интерфейсе и выполни «ai-home-agent pair --server … --code …»");
            return 2;
        }
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

        using var cliHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var managedCli = new ManagedCli(paths.CliRoot, new HttpCliDistribution(cliHttp), CliPlatform.Detect(),
            logger: loggers.CreateLogger<ManagedCli>());
        var cliLoop = managedCli.RunAsync(stop.Token);

        var grants = new TurnGrants();
        await using var sidecar = await SidecarHost.StartAsync(grants, device, loggers, ct: stop.Token);
        log.LogInformation("Сайдкар слушает {Url}", sidecar.Url);

        var executor = new TurnExecutor(
            new ExecOptions { TurnsRoot = paths.TurnsRoot, ConfigDirectory = paths.CliProfile, SidecarUrl = () => sidecar.Url },
            new ManagedCliLeaseSource(managedCli), grants, journal, loggers.CreateLogger<TurnExecutor>());
        AppDomain.CurrentDomain.ProcessExit += (_, _) => executor.KillAll();

        await using var control = new HubControlConnection(device, log);
        await using var coordinator = new AgentCoordinator(control, new ManagedCliHarness(managedCli),
            new ExecSocketConnector(device), executor.RunAsync, Version, log);

        try
        {
            await control.ConnectAsync(stop.Token);
            await coordinator.HelloAsync();
            log.LogInformation("Агент на связи с {Server} как «{Name}»", registration.ServerUrl, registration.DeviceName);
            await Task.Delay(Timeout.Infinite, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            executor.KillAll();
            try { await cliLoop; } catch (OperationCanceledException) { }
        }

        log.LogInformation("Агент остановлен");
        return 0;
    }

    private sealed record DeviceIdentity(Uri ServerUri, string DeviceToken, string Fingerprint) : IDeviceIdentity
    {
        // Токен не печатается даже случайно
        public override string ToString() => $"DeviceIdentity {{ ServerUri = {ServerUri} }}";
    }
}
