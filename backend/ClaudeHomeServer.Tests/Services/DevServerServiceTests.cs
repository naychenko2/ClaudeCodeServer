using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ProjectServices;
using Microsoft.Extensions.Configuration;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Тесты DevServerService без запуска процессов.
/// </summary>
public class DevServerServiceTests : IDisposable
{
    private readonly DevServerService _svc;
    private readonly DevServerPortMemory _portMemory;
    private readonly string _dir;
    private readonly Mock<IProjectManager> _projects = new();

    public void Dispose()
    {
        _svc.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    public DevServerServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "devsrv_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _projects.Setup(p => p.GetById("proj"))
            .Returns(new ClaudeHomeServer.Models.Project { Id = "proj", RootPath = _dir, OwnerId = "user1", Name = "t" });
        // Память портов пишет рядом с DataPath — во временный каталог теста
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var sandboxMgr = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        // Шов `ISandboxPortRange` (Этап 5, волна C, шаг 2): DevServerService берёт
        // только пул preview-портов, в тестах хватает адаптера вокруг настоящего
        // `SandboxManager` (пустая конфигурация → дефолтные 42000..42019).
        _sandbox = new ClaudeHomeServer.Services.Execution.SandboxPortRangeAdapter(sandboxMgr);
        _portMemory = new DevServerPortMemory(config,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DevServerPortMemory>.Instance);
        _svc = NewService(TestLauncherFactory.Instance);
    }

    private readonly ClaudeHomeServer.Services.Execution.ISandboxPortRange _sandbox;

    private DevServerService NewService(ClaudeHomeServer.Services.Execution.ILauncherFactory launchers) =>
        new(_projects.Object, new TestSessionBroadcaster(),
            new Mock<ILogger<DevServerService>>().Object, launchers, _sandbox, _portMemory);

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        _svc.Should().NotBeNull();
    }

    [Fact]
    public void GetRunning_WithNoServers_ReturnsEmpty()
    {
        _svc.GetRunning("nonexistent", "user1").Should().BeEmpty();
    }

    [Fact]
    public void GetActivePreviewPort_WithNoServers_ReturnsNull()
    {
        _svc.GetActivePreviewPort("nonexistent").Should().BeNull();
    }

    [Fact]
    public void GetActiveServiceId_WithNoServers_ReturnsNull()
    {
        _svc.GetActiveServiceId("nonexistent", "user1").Should().BeNull();
    }

    [Fact]
    public void SetActivePreview_WithoutStartedServer_StillHasNoPort()
    {
        _svc.SetActivePreview("proj", "svc");
        // Активный назначен, но процесс не запущен — порта нет.
        _svc.GetActivePreviewPort("proj").Should().BeNull();
    }

    [Theory]
    [InlineData(80)]
    [InlineData(443)]
    [InlineData(8080)]
    public async Task StartAsync_ForbiddenPort_RefusesBeforeLaunch(int port)
    {
        // Отказ по порту обязан прийти раньше любого обращения к проекту и запуска
        var result = await _svc.StartAsync("proj", "user1", "svc", "Api", "dotnet", ["run"], port: port);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain($"Порт {port} запрещён");
        _svc.GetRunning("proj", "user1").Should().BeEmpty();
        _projects.Verify(p => p.GetById(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Порт, пойманный из вывода сервиса (vite `--port 8080`, `applicationUrl` в launchSettings),
    /// проходит тот же запрет: отказ, а не «started» с превью в Dify. Порт 8080 на машине
    /// разработчика слушает живой Dify — без запрета проба соединения выдала бы «started».
    /// </summary>
    [Fact]
    public async Task StartAsync_ForbiddenPortFromOutput_Refuses()
    {
        var (command, args) = ShellPrinting("Local: http://localhost:8080/");

        var result = await _svc.StartAsync("proj", "user1", "svc", "Vite", command, args,
            readyTimeout: TimeSpan.FromSeconds(8));

        result.Success.Should().BeFalse();
        result.Status.Should().Be("error");
        result.Error.Should().Contain("Порт 8080 запрещён");
        _svc.GetRunning("proj", "user1").Should().BeEmpty("процесс погашен и убран из реестра");
        _svc.GetActivePreviewPort("proj").Should().BeNull();
        _portMemory.Get("proj", "svc").Should().BeNull();
    }

    // Команда, которая печатает строку и живёт ещё ~10 с — как дев-сервер после старта
    private static (string Command, string[] Args) ShellPrinting(string line) =>
        OperatingSystem.IsWindows()
            ? ("cmd", ["/c", $"echo {line}& ping -n 11 127.0.0.1 >nul"])
            : ("sh", ["-c", $"echo '{line}'; sleep 10"]);

    [Theory]
    [InlineData(80)]
    [InlineData(443)]
    [InlineData(8080)]
    public void SetActiveExternal_ForbiddenPort_Refused(int port)
    {
        _svc.SetActiveExternal("proj", "svc", port).Should().BeFalse();

        _svc.GetActiveExternal("proj").Should().BeNull();
        _svc.GetActivePreviewPort("proj").Should().BeNull("туннель на бой или Dify не открывается");
    }

    [Fact]
    public void SetActiveExternal_DevPort_Accepted()
    {
        _svc.SetActiveExternal("proj", "svc", 5590).Should().BeTrue();

        _svc.GetActivePreviewPort("proj").Should().Be(5590);
    }

    /// <summary>
    /// Гонка autoPort: порт резервируется в момент выбора, а не когда процесс уже стартовал.
    /// Два запуска подряд (до того, как первый записал порт в инстанс) получают разные порты.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReserveAutoPort_ParallelPicks_GetDistinctPorts(bool sandboxed)
    {
        var first = _svc.ReserveAutoPort(sandboxed);
        var second = _svc.ReserveAutoPort(sandboxed);

        first.Should().NotBeNull();
        second.Should().NotBeNull().And.NotBe(first);

        _svc.ReleasePort(first!.Value);
        _svc.ReleasePort(second!.Value);
    }

    [Fact]
    public void ReleasePort_ReturnsPortToPool()
    {
        var first = _svc.ReserveAutoPort(sandboxed: true);
        _svc.ReleasePort(first!.Value);

        _svc.ReserveAutoPort(sandboxed: true).Should().Be(first, "песочный пул идёт по порядку, снятый резерв свободен");
    }

    /// <summary>
    /// Внешнее превью берёт порт из конфигурации сервиса. Порт 8080 (Dify) в launch.json —
    /// отказ с понятной причиной, а не туннель на Dify.
    /// </summary>
    [Fact]
    public async Task SetActiveExternalAsync_ForbiddenConfigPort_Refused()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".claude"));
        File.WriteAllText(Path.Combine(_dir, ".claude", "launch.json"), """
        { "configurations": [ { "name": "Dify-like", "runtimeExecutable": "npm", "runtimeArgs": ["run", "dev"], "port": 8080 } ] }
        """);
        var launch = new LaunchConfigService(new Mock<ILogger<LaunchConfigService>>().Object);
        var discovery = new ProjectServiceDiscovery(launch, new Mock<ILogger<ProjectServiceDiscovery>>().Object);
        var api = new ProjectServicesApi(_svc, discovery, launch, _portMemory, new Mock<ILogger<ProjectServicesApi>>().Object);
        var project = _projects.Object.GetById("proj")!;
        var svcId = (await discovery.DiscoverAsync(project)).Single().Id;

        var result = await api.SetActiveExternalAsync(project, new PreviewActiveRequest(svcId));

        result.Status.Should().Be(400);
        System.Text.Json.JsonSerializer.Serialize(result.Body).Should().Contain("8080");
        _svc.GetActiveExternal("proj").Should().BeNull();
        (await api.ResolveServicePortAsync(project, svcId, "user1")).Should()
            .BeNull("внешняя ссылка и «остановить чужой» тоже не должны указывать на Dify");
    }

    /// <summary>
    /// Составной запуск, у участника которого порт 8080 (Dify): ни «поднят снаружи» (8080 на
    /// машине разработчика действительно слушается), ни запуск — отказ с причиной, превью нет.
    /// </summary>
    [Fact]
    public async Task StartGroup_MemberWithForbiddenPort_RefusedWholly()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".run"));
        File.WriteAllText(Path.Combine(_dir, ".run", "api.run.xml"), """
        <component name="ProjectRunConfigurationManager">
          <configuration default="false" name="Api" type="NodeJSConfigurationType"
                         application-parameters="--port 8080"
                         path-to-js-file="$PROJECT_DIR$/serve.mjs" />
        </component>
        """);
        File.WriteAllText(Path.Combine(_dir, ".run", "all.run.xml"), """
        <component name="ProjectRunConfigurationManager">
          <configuration default="false" name="Всё" type="com.intellij.execution.configurations.multilaunch" factoryName="MultiLaunchConfiguration">
            <rows><ExecutableRowSnapshot><option name="executable"><ExecutableSnapshot>
              <option name="id" value="runConfig:Node.js.Api" />
            </ExecutableSnapshot></option></ExecutableRowSnapshot></rows>
          </configuration>
        </component>
        """);
        var launch = new LaunchConfigService(new Mock<ILogger<LaunchConfigService>>().Object);
        var discovery = new ProjectServiceDiscovery(launch, new Mock<ILogger<ProjectServiceDiscovery>>().Object);
        var api = new ProjectServicesApi(_svc, discovery, launch, _portMemory, new Mock<ILogger<ProjectServicesApi>>().Object);
        var project = _projects.Object.GetById("proj")!;
        var group = (await discovery.DiscoverAsync(project)).Single(s => s.Members is { Length: > 0 });

        var result = await api.StartAsync(project, "user1", new PreviewStartRequest("", ServiceId: group.Id));

        var body = System.Text.Json.JsonSerializer.Serialize(result.Body);
        body.Should().Contain("\"status\":\"error\"").And.Contain("8080");
        _svc.GetRunning("proj", "user1").Should().BeEmpty();
        _svc.GetActiveExternal("proj").Should().BeNull("запрещённый порт «поднятым снаружи» не считается");
        _svc.GetActivePreviewPort("proj").Should().BeNull();
    }

    /// <summary>
    /// Лаунчер упал на старте — резерв автопорта обязан сняться, иначе каждый сбой навсегда
    /// выедал бы порт из пула песочницы (их там всего 20).
    /// </summary>
    [Fact]
    public async Task StartAsync_LauncherThrows_ReleasesAutoPortReservation()
    {
        var launcher = new Mock<ClaudeHomeServer.Services.Execution.IProcessLauncher>();
        launcher.SetupGet(l => l.IsSandboxed).Returns(true);
        launcher.Setup(l => l.Start(It.IsAny<ClaudeHomeServer.Services.Execution.ProcessSpec>()))
            .Throws(new InvalidOperationException("docker недоступен"));
        var factory = new Mock<ClaudeHomeServer.Services.Execution.ILauncherFactory>();
        factory.Setup(f => f.ForProject(It.IsAny<ClaudeHomeServer.Models.Project>())).Returns(launcher.Object);
        using var svc = NewService(factory.Object);
        var firstFree = svc.ReserveAutoPort(sandboxed: true);
        svc.ReleasePort(firstFree!.Value);

        var result = await svc.StartAsync("proj", "user1", "svc", "Api", "node", ["serve.mjs"], autoPort: true);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("docker недоступен");
        svc.ReserveAutoPort(sandboxed: true).Should().Be(firstFree, "резерв сбойного запуска снят");
    }

    /// <summary>
    /// «Стоп» хода во время подъёма стенда (start_stand): процесс гасится и уходит из реестра
    /// сразу, а не по потолку ожидания порта.
    /// </summary>
    [Fact]
    public async Task StartAsync_Cancelled_KillsStartingProcess()
    {
        // Процесс жив, но порта не объявляет — подъём висел бы до потолка
        var (command, args) = ShellPrinting("ещё собираюсь");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await _svc.StartAsync("proj", "user1", "svc", "Stand", command, args,
            readyTimeout: TimeSpan.FromSeconds(60), ct: cts.Token);

        // Заглушка сама живёт ~10 с: без отмены подъём кончился бы её выходом, а не «Стопом»
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(6), "отмена прерывает ожидание порта");
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Стоп");
        _svc.GetRunning("proj", "user1").Should().BeEmpty("стартующий процесс погашен и убран из реестра");
    }

    /// <summary>
    /// Два одновременных подъёма одного сервиса (start_stand и кнопка панели): процесс запускается
    /// один, проигравший получает «starting», а не «error» с погашенным чужим процессом.
    /// </summary>
    [Fact]
    public async Task StartAsync_ConcurrentSameService_OneLaunchNoError()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var (command, args) = ShellPrinting($"Local: http://localhost:{port}/");
        using var go = new ManualResetEventSlim();

        Task<DevServerStartResult> Raise() => Task.Run(() =>
        {
            go.Wait();
            return _svc.StartAsync("proj", "user1", "svc", "Stand", command, args,
                readyTimeout: TimeSpan.FromSeconds(20));
        });
        var tasks = Enumerable.Range(0, 4).Select(_ => Raise()).ToArray();
        go.Set();
        var results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(r => r.Success, "проигравший — «уже поднимается», а не ошибка");
        results.Count(r => r.Status == "started").Should().BeGreaterThanOrEqualTo(1);
        _svc.GetRunning("proj", "user1").Should().ContainSingle().Which.Status.Should().Be("started");
        await _svc.StopAsync("proj", "user1", "svc");
    }

    /// <summary>
    /// Стенд из чата: запуск идёт из корня рабочего дерева (worktree), а не проекта, и запись
    /// реестра несёт подпись чата-инициатора — её показывает панель «Сервисы».
    /// </summary>
    [Fact]
    public async Task StartAsync_RootAndOrigin_RunsInTreeAndCarriesOrigin()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var tree = Directory.CreateDirectory(Path.Combine(_dir, "wt")).FullName;
        File.WriteAllText(Path.Combine(tree, "marker.txt"), "дерево чата");
        // Сервис печатает адрес, только если стартовал в дереве чата (там лежит marker.txt)
        var (command, args) = OperatingSystem.IsWindows()
            ? ("cmd", new[] { "/c", $"if exist marker.txt (echo Local: http://localhost:{port}/& ping -n 11 127.0.0.1 >nul)" })
            : ("sh", new[] { "-c", $"test -f marker.txt && echo 'Local: http://localhost:{port}/' && sleep 10" });

        var result = await _svc.StartAsync("proj", "user1", "svc", "Stand", command, args,
            readyTimeout: TimeSpan.FromSeconds(20), root: tree, origin: new StandOrigin("sess-1", "чат «Е2Е»"));

        result.Success.Should().BeTrue(result.Error);
        result.Port.Should().Be(port);
        _svc.GetRunning("proj", "user1").Should().ContainSingle()
            .Which.Origin.Should().Be(new StandOrigin("sess-1", "чат «Е2Е»"));
        await _svc.StopAsync("proj", "user1", "svc");
    }

    [Theory]
    [InlineData(80, "запрещён")]
    [InlineData(8080, "запрещён")]
    [InlineData(5000, "вне диапазона")]
    [InlineData(5700, "вне диапазона")]
    public void StandPortRefusal_ForbiddenOrOutOfRange(int port, string expected) =>
        DevServerService.StandPortRefusal(port).Should().Contain(expected);

    [Theory]
    [InlineData(5500)]
    [InlineData(5699)]
    public void StandPortRefusal_StandRange_Accepted(int port) =>
        DevServerService.StandPortRefusal(port).Should().BeNull();

    [Fact]
    public void ShutdownAll_WithNoServers_DoesNotThrow()
    {
        var act = () => _svc.ShutdownAll();
        act.Should().NotThrow();
    }
}
