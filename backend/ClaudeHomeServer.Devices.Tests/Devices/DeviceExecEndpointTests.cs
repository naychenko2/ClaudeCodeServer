using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Devices;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeHomeServer.Tests.Services.Devices;

// WebSocket /api/devices/exec на настоящем конвейере (ADR-016, задача 2.1): канал открывает
// ТОЛЬКО токен устройства с отпечатком — пользовательский JWT и сервисный JWT владельца
// отвергаются; несовместимая версия протокола — явный отказ до апгрейда; кадры
// подтверждаются, неподтверждённое досылается после реконнекта, дубли не доходят дважды.
[Trait("Category", "Host")]
public class DeviceExecEndpointTests : IClassFixture<DeviceExecEndpointTests.Hosts>
{

    public sealed class CapturingOpener : IDeviceExecOpenSender
    {
        public readonly Channel<DeviceExecOpenCommand> Opened = Channel.CreateUnbounded<DeviceExecOpenCommand>();

        public Task SendExecOpenAsync(string connectionId, DeviceExecOpenCommand command, CancellationToken ct = default)
        {
            Opened.Writer.TryWrite(command);
            return Task.CompletedTask;
        }
    }

    // Хост на класс. Устройство, отпечаток и идентификатор соединения каждый тест заводит свои,
    // поэтому состояние реестров между тестами не пересекается; очередь открытий опенера
    // сбрасывается в начале теста
    public sealed class Hosts : IDisposable
    {
        public CapturingOpener Opener { get; } = new();
        public TestWebApplicationFactory Factory { get; }

        public Hosts()
        {
            Factory = new TestWebApplicationFactory
            {
                ExtraServices = services =>
                {
                    services.RemoveAll<IDeviceExecOpenSender>();
                    services.AddSingleton<IDeviceExecOpenSender>(Opener);
                }
            };
            Factory.ExtraConfig[DeviceHarnessPolicy.CliVersionKey] = "2.1.281";
        }

        public void Dispose() => Factory.Dispose();
    }

    private readonly TestWebApplicationFactory _factory;
    private readonly CapturingOpener _opener;
    private readonly string _ownerId;
    private readonly string _deviceId;
    private readonly string _deviceToken;
    private readonly string _conn = "exec-conn-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _fingerprint;

    public DeviceExecEndpointTests(Hosts hosts)
    {
        _factory = hosts.Factory;
        _opener = hosts.Opener;
        while (_opener.Opened.Reader.TryRead(out _)) { }

        var machine = "exec-test-machine-" + Guid.NewGuid().ToString("N")[..8];
        _fingerprint = MachineFingerprint.Of(machine);
        var sp = _factory.Services;
        _ownerId = sp.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        var (device, token) = sp.GetRequiredService<DeviceRegistry>().Register(_ownerId, machine, _fingerprint);
        _deviceId = device.Id;
        _deviceToken = token;
    }

    private HttpRequestMessage ExecRequest(string? version = "1", string execId = "none")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{DeviceExecProtocol.Path}?execId={execId}");
        if (version is not null) request.Headers.Add(DeviceExecProtocol.VersionHeader, version);
        return request;
    }

    private HttpRequestMessage DeviceRequest(string? version = "1", string execId = "none", string? fingerprint = null)
    {
        var request = ExecRequest(version, execId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Device", _deviceToken);
        request.Headers.Add(DeviceAuthHandler.FingerprintHeader, fingerprint ?? _fingerprint);
        return request;
    }

    // ---------- канал: только HTTPS или петля, как у сопряжения ----------

    // Запрос пришёл через прокси без https: петля соединения ничего не доказывает
    private static HttpRequestMessage ViaPlainProxy(HttpRequestMessage request)
    {
        request.Headers.Add("X-Forwarded-For", "192.168.1.5");
        return request;
    }

    private HttpRequestMessage HubNegotiate()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/hubs/devices/negotiate?negotiateVersion=1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Device", _deviceToken);
        request.Headers.Add(DeviceAuthHandler.FingerprintHeader, _fingerprint);
        return request;
    }

    [Fact]
    public async Task КаналИсполненияПоОткрытомуКаналу_403()
    {
        using var client = _factory.CreateClient();
        var response = await client.SendAsync(ViaPlainProxy(DeviceRequest()));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("HTTPS");
    }

    [Fact]
    public async Task ХабУстройствПоОткрытомуКаналу_403()
    {
        using var client = _factory.CreateClient();
        var response = await client.SendAsync(ViaPlainProxy(HubNegotiate()));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("HTTPS");
    }

    [Fact]
    public async Task ХабУстройствНаПетле_Пускает()
    {
        using var client = _factory.CreateClient();
        (await client.SendAsync(HubNegotiate())).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---------- авторизация ----------

    [Fact]
    public async Task БезАвторизации_401()
    {
        using var client = _factory.CreateClient();
        (await client.SendAsync(ExecRequest())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ПользовательскийJwt_401()
    {
        using var client = _factory.CreateAuthenticatedClient();
        (await client.SendAsync(ExecRequest())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task СервисныйJwtВладельца_401()
    {
        var serviceJwt = _factory.Services.GetRequiredService<JwtService>().IssueServiceToken(_ownerId);
        using var client = _factory.CreateClient();
        var request = ExecRequest();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceJwt);

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ТокенУстройстваСЧужимОтпечатком_401()
    {
        using var client = _factory.CreateClient();
        var request = DeviceRequest(fingerprint: MachineFingerprint.Of("other-machine"));

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ТокенУстройстваСОтпечатком_ПроходитАвторизацию()
    {
        using var client = _factory.CreateClient();
        // Авторизация пройдена — дальше честное «такого исполнения нет»
        (await client.SendAsync(DeviceRequest())).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------- версия протокола ----------

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("abc")]
    public async Task НесовместимаяВерсияПротокола_ЯвныйОтказ(string? version)
    {
        using var client = _factory.CreateClient();

        var response = await client.SendAsync(DeviceRequest(version));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(DeviceExecProtocol.UnsupportedVersionError);
    }

    // ---------- поток ----------

    private async Task<(IDeviceExecStream Stream, string ExecId)> OpenReadyStreamAsync(Func<string, Task<WebSocket>> connect)
    {
        var channel = _factory.Services.GetRequiredService<DeviceExecChannel>();
        var router = _factory.Services.GetRequiredService<DeviceConnectionRegistry>();
        router.RegisterConnection(_conn, _ownerId, _deviceId);
        await channel.HelloAsync(_conn, _ownerId, _deviceId,
            new DeviceHello(DesktopProtocol.Version, null, null, "linux-x64", DeviceAgentCompatibility.MinVersion, "2.1.281", [DeviceCapabilities.Exec]));

        var opening = channel.OpenAsync(_ownerId, _deviceId);
        var command = await _opener.Opened.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await connect(command.ExecId);
        return (await opening.WaitAsync(TimeSpan.FromSeconds(10)), command.ExecId);
    }

    private async Task<WebSocket> ConnectDeviceAsync(string execId)
    {
        var ws = _factory.Server.CreateWebSocketClient();
        ws.ConfigureRequest = r =>
        {
            r.Headers.Authorization = $"Device {_deviceToken}";
            r.Headers[DeviceAuthHandler.FingerprintHeader] = _fingerprint;
            r.Headers[DeviceExecProtocol.VersionHeader] = DeviceExecProtocol.Version.ToString();
        };
        return await ws.ConnectAsync(new Uri($"ws://localhost{DeviceExecProtocol.Path}?execId={execId}"), CancellationToken.None);
    }

    private static async Task<DeviceExecFrame> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        result.EndOfMessage.Should().BeTrue();
        DeviceExecFrames.TryDecode(buffer.AsMemory(0, result.Count).ToArray(), out var frame).Should().BeTrue();
        return frame;
    }

    private static Task SendAsync(WebSocket socket, byte[] frame) =>
        socket.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);

    private static string Text(DeviceExecFrame frame) => Encoding.UTF8.GetString(frame.Payload.Span);

    [Fact]
    public async Task Кадры_ПодтверждаютсяДосылаютсяПослеРеконнектаИБезДублей()
    {
        WebSocket device = null!;
        var (stream, execId) = await OpenReadyStreamAsync(async id => device = await ConnectDeviceAsync(id));
        await using var _ = stream;

        // сервер → устройство: кадр с номером 1
        await stream.SendAsync(DeviceExecFrameChannel.Stdin, "первый"u8.ToArray());
        var first = await ReceiveAsync(device);
        first.Sequence.Should().Be(1);
        Text(first).Should().Be("первый");

        // устройство → сервер: кадр 1 и его дубль; потребитель видит его один раз, ack приходит оба раза
        var stdout = DeviceExecFrames.Encode(DeviceExecFrameChannel.Stdout, 1, "вывод"u8);
        await SendAsync(device, stdout);
        (await ReceiveAsync(device)).Should().Match<DeviceExecFrame>(f => f.Channel == DeviceExecFrameChannel.Ack && f.Sequence == 1);
        await SendAsync(device, stdout);
        (await ReceiveAsync(device)).Should().Match<DeviceExecFrame>(f => f.Channel == DeviceExecFrameChannel.Ack && f.Sequence == 1);

        // обрыв без подтверждения кадра 1 сервера; кадр 2 уходит в пустоту
        device.Abort();
        await stream.SendAsync(DeviceExecFrameChannel.Stdin, "второй"u8.ToArray());

        // реконнект: сначала «докуда дошло», затем досылка 1 и 2 по порядку
        var again = await ConnectDeviceAsync(execId);
        (await ReceiveAsync(again)).Should().Match<DeviceExecFrame>(f => f.Channel == DeviceExecFrameChannel.Ack && f.Sequence == 1);
        Text(await ReceiveAsync(again)).Should().Be("первый");
        var second = await ReceiveAsync(again);
        second.Sequence.Should().Be(2);
        Text(second).Should().Be("второй");

        // устройство подтверждает оба и присылает дубль старого плюс новый кадр 2
        await SendAsync(again, DeviceExecFrames.Ack(2));
        await SendAsync(again, stdout);
        (await ReceiveAsync(again)).Sequence.Should().Be(1);
        await SendAsync(again, DeviceExecFrames.Encode(DeviceExecFrameChannel.Exit, 2, "{\"code\":0}"u8));
        (await ReceiveAsync(again)).Sequence.Should().Be(2);

        var received = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var frame in stream.ReadAllAsync(timeout.Token))
        {
            received.Add($"{frame.Channel}:{frame.Sequence}:{Text(frame)}");
            if (frame.Channel == DeviceExecFrameChannel.Exit) break;
        }

        received.Should().Equal("Stdout:1:вывод", "Exit:2:{\"code\":0}");
    }

    [Fact]
    public async Task ПропускНомераКадра_СоединениеЗакрываетсяКакСбойПротокола()
    {
        WebSocket device = null!;
        var (stream, _) = await OpenReadyStreamAsync(async id => device = await ConnectDeviceAsync(id));
        await using var _s = stream;

        await SendAsync(device, DeviceExecFrames.Encode(DeviceExecFrameChannel.Stdout, 5, "x"u8));

        var buffer = new byte[256];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await device.ReceiveAsync(buffer, timeout.Token);
        result.MessageType.Should().Be(WebSocketMessageType.Close);
        result.CloseStatus.Should().Be(WebSocketCloseStatus.ProtocolError);
    }

    [Fact]
    public void ШовКаналаИсполнения_ЭтоТотЖеКанал()
    {
        var sp = _factory.Services;
        sp.GetRequiredService<IDeviceExecChannel>().Should().BeSameAs(sp.GetRequiredService<DeviceExecChannel>());
        sp.GetRequiredService<IDeviceExecChannel>().GetStatus(_ownerId, _deviceId)!.Online.Should().BeFalse();
    }
}
