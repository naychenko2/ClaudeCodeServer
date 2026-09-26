using System.Net;
using ClaudeHomeServer.DeviceAgent.Credentials;
using ClaudeHomeServer.DeviceAgent.Hosting;
using ClaudeHomeServer.DeviceAgent.Install;
using ClaudeHomeServer.DeviceAgent.Pairing;
using ClaudeHomeServer.DeviceAgent.Tests.Supervision;

namespace ClaudeHomeServer.DeviceAgent.Tests.Install;

internal sealed class FakeAutostart : IAutostart
{
    public List<string> Calls { get; } = [];
    public SupervisorStartStatus StartStatus { get; set; } = SupervisorStartStatus.Started;
    public Action? OnStart { get; set; }

    public string Describe => "фейковый автозапуск";

    public AutostartResult Register(string version, bool alwaysOn)
    {
        Calls.Add($"register {version}{(alwaysOn ? " always-on" : "")}");
        return new AutostartResult(true, []);
    }

    public void Repoint(string version) => Calls.Add($"repoint {version}");

    public SupervisorStart StartNow(string version)
    {
        Calls.Add($"start {version}");
        OnStart?.Invoke();
        return new SupervisorStart(StartStatus, $"старт: {StartStatus}");
    }

    public void Unregister() => Calls.Add("unregister");
}

internal sealed class FakeSupervisorControl : ISupervisorControl
{
    public bool Running { get; set; }
    public int Stops { get; private set; }

    public bool Stop()
    {
        Stops++;
        var was = Running;
        Running = false;
        return was;
    }
}

internal sealed class MemoryTokenStore : IDeviceTokenStore
{
    public Dictionary<string, string> Saved { get; } = new();
    public string Describe => "память";
    public string? Read(string deviceId) => Saved.GetValueOrDefault(deviceId);
    public void Save(string deviceId, string token) => Saved[deviceId] = token;
    public void Delete(string deviceId) => Saved.Remove(deviceId);
}

internal sealed class FakeSelfRevoke(SelfRevokeStatus status) : ISelfRevoke
{
    public List<(Uri Server, string Token, string Fingerprint)> Calls { get; } = [];

    public Task<(SelfRevokeStatus Status, string? Error)> RevokeAsync(Uri server, string deviceToken, string fingerprint, CancellationToken ct)
    {
        Calls.Add((server, deviceToken, fingerprint));
        return Task.FromResult<(SelfRevokeStatus, string?)>((status, status == SelfRevokeStatus.Failed ? "сервер недоступен" : null));
    }
}

/// <summary>
/// <c>install</c> и <c>uninstall</c> на фейках: порядок шагов, коды выхода для скриптов,
/// ожидание первого hello, снятие автозапуска и самоотзыв (Р12).
/// </summary>
public sealed class InstallCommandsTests : IDisposable
{
    private const string Token = "device-token-SECRET-install";

    private readonly TempInstall _install = new("1.4.0");
    private readonly AgentPaths _paths;
    private readonly FakeAutostart _autostart = new();
    private readonly FakeSupervisorControl _supervisors = new() { Running = true };
    private readonly FakeClock _clock = new();
    private readonly StringWriter _output = new();
    private readonly List<InstallRequest> _pairings = [];

    public InstallCommandsTests()
    {
        var home = Directory.CreateTempSubdirectory("agent-home-").FullName;
        _paths = new AgentPaths(Path.Combine(home, "config"), Path.Combine(home, "data"));
        _paths.Ensure();
    }

    public void Dispose()
    {
        _install.Dispose();
        var home = Path.GetDirectoryName(_paths.ConfigDirectory)!;
        if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
    }

    private static readonly InstallRequest Request = new(new Uri("https://home.example/"), "ABCD2345", "Ноутбук", AlwaysOn: false);

    private AgentInstaller Installer(string version = "1.4.0") => new(_paths, _install.Layout, version, _autostart, _supervisors,
        (request, _) =>
        {
            _pairings.Add(request);
            return Task.FromResult(new DeviceRegistration(request.Server.ToString(), "dev-7", request.DeviceName, "f1ngerprint"));
        },
        _output, _clock);

    [Fact]
    public async Task Install_сопрягает_ставит_active_автозапуск_и_ждёт_первого_hello()
    {
        _autostart.OnStart = () => File.WriteAllText(_install.Layout.HealthyOf("1.4.0"), "ok");

        var code = await Installer().RunAsync(Request, CancellationToken.None);

        code.Should().Be(InstallExitCodes.Ok);
        _pairings.Should().ContainSingle().Which.Code.Should().Be("ABCD2345");
        DeviceRegistration.Load(_paths.RegistrationFile)!.DeviceId.Should().Be("dev-7");
        _supervisors.Stops.Should().Be(1, "переустановка гасит прежний супервизор");
        _install.Layout.ReadActive().Should().Be("1.4.0");
        _autostart.Calls.Should().Equal("register 1.4.0", "start 1.4.0");
        _output.ToString().Should().Contain("на связи");
    }

    [Fact]
    public async Task Always_on_на_Linux_кладёт_токен_в_файл_и_запоминает_выбор()
    {
        _autostart.OnStart = () => File.WriteAllText(_install.Layout.HealthyOf("1.4.0"), "ok");

        await Installer().RunAsync(Request with { AlwaysOn = true }, CancellationToken.None);

        _autostart.Calls.Should().Contain("register 1.4.0 always-on");
        DeviceRegistration.Load(_paths.RegistrationFile)!.TokenStore
            .Should().Be(OperatingSystem.IsWindows() ? DeviceTokenStores.DpapiKind : DeviceTokenStores.FileKind,
                "при загрузке без входа связка ключей сеанса закрыта");
    }

    [Fact]
    public async Task Старый_маркер_healthy_не_засчитывается_за_первый_hello()
    {
        File.WriteAllText(_install.Layout.HealthyOf("1.4.0"), "прошлая установка");
        var started = _clock.Now;

        var code = await Installer().RunAsync(Request, CancellationToken.None);

        code.Should().Be(InstallExitCodes.NoHelloYet);
        (_clock.Now - started).Should().BeGreaterThanOrEqualTo(AgentInstaller.HelloTimeout).And.BeLessThan(TimeSpan.FromSeconds(31));
        _output.ToString().Should().Contain("продолжит попытки");
    }

    [Fact]
    public async Task Запрещённый_breakaway_даёт_код_для_скрипта_и_не_ждёт_hello()
    {
        _autostart.StartStatus = SupervisorStartStatus.Deferred;
        var started = _clock.Now;

        var code = await Installer().RunAsync(Request, CancellationToken.None);

        code.Should().Be(InstallExitCodes.StartDeferred);
        _clock.Now.Should().Be(started);
        _install.Layout.ReadActive().Should().Be("1.4.0", "автозапуск прописан — агент поднимется при следующем входе");
    }

    [Fact]
    public async Task Без_автозапуска_установка_успешна_с_инструкцией()
    {
        _autostart.StartStatus = SupervisorStartStatus.Manual;

        (await Installer().RunAsync(Request, CancellationToken.None)).Should().Be(InstallExitCodes.Ok);
        _output.ToString().Should().Contain("старт: Manual");
    }

    [Fact]
    public async Task Install_не_из_каталога_версии_отказывает_до_сопряжения()
    {
        (await Installer("9.9.9").RunAsync(Request, CancellationToken.None)).Should().Be(InstallExitCodes.Failed);
        _pairings.Should().BeEmpty("код сопряжения одноразовый — не тратить его на заведомо провальную установку");
    }

    private (AgentUninstaller Uninstaller, MemoryTokenStore Store, FakeSelfRevoke Revoke) Uninstaller(SelfRevokeStatus status)
    {
        var store = new MemoryTokenStore();
        store.Save("dev-7", Token);
        new DeviceRegistration("https://home.example/", "dev-7", "Ноутбук", "f1ngerprint", DeviceTokenStores.FileKind)
            .Save(_paths.RegistrationFile);
        var revoke = new FakeSelfRevoke(status);
        var uninstaller = new AgentUninstaller(_paths, _install.Layout, _autostart, _supervisors, revoke,
            kind => kind == DeviceTokenStores.FileKind ? store : throw new InvalidOperationException($"не то хранилище: {kind}"), _output);
        return (uninstaller, store, revoke);
    }

    [Fact]
    public async Task Uninstall_снимает_автозапуск_гасит_супервизор_отзывает_устройство_и_стирает_токен()
    {
        var (uninstaller, store, revoke) = Uninstaller(SelfRevokeStatus.Revoked);

        (await uninstaller.RunAsync(purge: false, CancellationToken.None)).Should().Be(0);

        _autostart.Calls.Should().Equal("unregister");
        _supervisors.Stops.Should().Be(1);
        revoke.Calls.Should().ContainSingle();
        revoke.Calls[0].Server.Should().Be(new Uri("https://home.example/"));
        revoke.Calls[0].Token.Should().Be(Token);
        revoke.Calls[0].Fingerprint.Should().Be("f1ngerprint");
        store.Saved.Should().BeEmpty();
        File.Exists(_paths.RegistrationFile).Should().BeFalse();
        Directory.Exists(_paths.DataDirectory).Should().BeTrue("без --purge данные остаются");
        _output.ToString().Should().Contain("отозвано").And.NotContain(Token);
    }

    [Fact]
    public async Task Недоступный_сервер_не_мешает_удалению_но_человек_узнаёт_об_отзыве_руками()
    {
        var (uninstaller, store, _) = Uninstaller(SelfRevokeStatus.Failed);

        (await uninstaller.RunAsync(purge: false, CancellationToken.None)).Should().Be(0);

        store.Saved.Should().BeEmpty();
        _output.ToString().Should().Contain("отзови его в веб-интерфейсе");
    }

    [Fact]
    public async Task Purge_предупреждает_и_удаляет_данные()
    {
        var (uninstaller, _, _) = Uninstaller(SelfRevokeStatus.Revoked);

        await uninstaller.RunAsync(purge: true, CancellationToken.None);

        _output.ToString().Should().StartWith(AgentUninstaller.PurgeWarning);
        Directory.Exists(_paths.DataDirectory).Should().BeFalse();
        Directory.Exists(_paths.ConfigDirectory).Should().BeFalse();
        Directory.Exists(_install.Root).Should().BeFalse();
    }

    [Fact]
    public async Task Несопряжённый_агент_удаляется_без_похода_на_сервер()
    {
        var revoke = new FakeSelfRevoke(SelfRevokeStatus.Revoked);
        var uninstaller = new AgentUninstaller(_paths, _install.Layout, _autostart, _supervisors, revoke,
            _ => new MemoryTokenStore(), _output);

        (await uninstaller.RunAsync(purge: false, CancellationToken.None)).Should().Be(0);

        revoke.Calls.Should().BeEmpty();
        _autostart.Calls.Should().Equal("unregister");
    }

    [Theory]
    [InlineData("--server https://h.example --code ABC", "https://h.example/", "ABC", false)]
    [InlineData("--code ABC --always-on --server https://h.example/ --name Ноут", "https://h.example/", "ABC", true)]
    public void Аргументы_install_разбираются(string line, string server, string code, bool alwaysOn)
    {
        var request = AgentProgram.ParsePairing(line.Split(' '))!;
        request.Server.Should().Be(new Uri(server));
        request.Code.Should().Be(code);
        request.AlwaysOn.Should().Be(alwaysOn);
    }

    [Theory]
    [InlineData("--server https://h.example")]
    [InlineData("--server https://h.example --code")]
    [InlineData("--server https://h.example --code A --purge")]
    [InlineData("-Server https://h.example -Code A")]
    [InlineData("--server https://a --server https://b --code A")]
    public void Непонятные_аргументы_install_отвергаются(string line) =>
        AgentProgram.ParsePairing(line.Split(' ')).Should().BeNull();
}

/// <summary>Самоотзыв: DELETE /api/devices/self под токеном устройства и отпечатком.</summary>
public sealed class SelfRevokeClientTests
{
    private sealed class Server(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent, "Revoked")]
    [InlineData(HttpStatusCode.Unauthorized, "AlreadyGone")]
    [InlineData(HttpStatusCode.NotFound, "AlreadyGone")]
    [InlineData(HttpStatusCode.InternalServerError, "Failed")]
    public async Task Самоотзыв_идёт_под_авторизацией_устройства(HttpStatusCode response, string expected)
    {
        var server = new Server(response);

        var (status, _) = await new SelfRevokeClient(new HttpClient(server))
            .RevokeAsync(new Uri("https://home.example/"), "tok", "fp", CancellationToken.None);

        status.ToString().Should().Be(expected);
        server.Request!.Method.Should().Be(HttpMethod.Delete);
        server.Request.RequestUri.Should().Be(new Uri("https://home.example/api/devices/self"));
        server.Request.Headers.GetValues("Authorization").Should().Equal("Device tok");
        server.Request.Headers.GetValues("X-Device-Fingerprint").Should().Equal("fp");
    }

    [Fact]
    public async Task По_открытому_каналу_токен_не_уходит()
    {
        var server = new Server(HttpStatusCode.NoContent);

        var (status, _) = await new SelfRevokeClient(new HttpClient(server))
            .RevokeAsync(new Uri("http://home.example/"), "tok", "fp", CancellationToken.None);

        status.Should().Be(SelfRevokeStatus.Failed);
        server.Request.Should().BeNull();
    }
}
