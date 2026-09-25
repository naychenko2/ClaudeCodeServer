using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Ретранслятор чтения на сервере (ADR-016 §5, задача 5.1): всё, что решается без устройства,
/// решается ДО обращения к каналу — чужой владелец, серверный проект, выключенный флаг,
/// офлайн. Офлайн — честный 409 с причиной, а не таймаут.
/// </summary>
public sealed class ProjectRelayControllerTests : IDisposable
{
    private const string Device = "dev-relay";

    private readonly FakeDeviceStatus _devices = new();
    private readonly SpyRelay _relay = new();
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    /// <summary>Канал в одном процессе: агент отвечает сценарием теста или молча закрывает поток.</summary>
    private sealed class SpyRelay : IDeviceRelayChannel
    {
        public int Opened;
        public DeviceExecRefusedException? Refuse;
        public Func<InProcessExecStream, Task> Agent = s => s.DisposeAsync().AsTask();

        public Task BeforeReturn = Task.CompletedTask;

        public async Task<IDeviceExecStream> OpenRelayAsync(string ownerId, string deviceId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Opened);
            if (Refuse is not null) throw Refuse;
            var stream = new InProcessExecStream(Guid.NewGuid().ToString("N"));
            stream.AgentTask = Task.Run(() => Agent(stream));
            await BeforeReturn;
            return stream;
        }
    }

    public ProjectRelayControllerTests()
    {
        _devices.Devices[Device] = FakeDeviceStatus.Status(Device, online: true,
            DeviceCapabilities.Exec, DeviceCapabilities.Files, DeviceCapabilities.Relay);
        _factory = new TestWebApplicationFactory
        {
            ExtraServices = s =>
            {
                s.AddSingleton<IDeviceExecChannel>(_devices);
                s.AddSingleton<IDeviceRelayChannel>(_relay);
            },
        };
        _client = _factory.CreateAuthenticatedClient();
    }

    public void Dispose() => _factory.Dispose();

    private async Task SetFlagAsync(HttpClient client, bool enabled) =>
        (await client.PutAsJsonAsync($"/api/feature-flags/{FeatureFlagKeys.LocalProjects}", new { enabled }))
            .EnsureSuccessStatusCode();

    private async Task<string> CreateAsync(string? deviceId)
    {
        await SetFlagAsync(_client, true);
        var root = deviceId is null ? MkDir() : "/home/u/relay-" + Guid.NewGuid().ToString("N")[..8];
        var r = await _client.PostAsJsonAsync("/api/projects", new { name = "R" + Guid.NewGuid().ToString("N")[..6], rootPath = root, deviceId });
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private string MkDir()
    {
        var dir = Path.Combine(_factory.TempDir, "rl_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());

    [Fact]
    public async Task ЧужойВладелец_404_ДоКанала()
    {
        var id = await CreateAsync(Device);
        var stranger = _factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        await SetFlagAsync(stranger, true);

        (await stranger.GetAsync($"/api/projects/{id}/relay/files")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        _relay.Opened.Should().Be(0);
    }

    [Fact]
    public async Task НетТакогоПроекта_404_ДоКанала()
    {
        (await _client.GetAsync("/api/projects/nope/relay/files/content?path=a.txt")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        _relay.Opened.Should().Be(0);
    }

    [Fact]
    public async Task СерверныйПроект_400_ДоКанала()
    {
        var id = await CreateAsync(deviceId: null);

        var r = await _client.GetAsync($"/api/projects/{id}/relay/files");

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(r)).GetProperty("error").GetString().Should().Be(ProjectCapabilities.NotDeviceBoundReason);
        _relay.Opened.Should().Be(0);
    }

    [Fact]
    public async Task ФлагВыключен_403_ДоКанала()
    {
        var id = await CreateAsync(Device);
        await SetFlagAsync(_client, false);

        (await _client.GetAsync($"/api/projects/{id}/relay/files")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _relay.Opened.Should().Be(0);
    }

    [Fact]
    public async Task УстройствоОфлайн_409СПричиной_ДоКанала()
    {
        var id = await CreateAsync(Device);
        _devices.Devices[Device] = FakeDeviceStatus.Status(Device, online: false,
            DeviceCapabilities.Exec, DeviceCapabilities.Files, DeviceCapabilities.Relay);

        var r = await _client.GetAsync($"/api/projects/{id}/relay/git/status");

        r.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await BodyAsync(r);
        body.GetProperty("error").GetString().Should().Be(ProjectCapabilities.DeviceOfflineReason);
        body.GetProperty("code").GetString().Should().Be(RelayProtocol.UnavailableCode);
        _relay.Opened.Should().Be(0);
    }

    [Fact]
    public async Task АгентБезРетранслятора_409_ДоКанала()
    {
        var id = await CreateAsync(Device);
        _devices.Devices[Device] = FakeDeviceStatus.Status(Device, online: true, DeviceCapabilities.Exec, DeviceCapabilities.Files);

        var r = await _client.GetAsync($"/api/projects/{id}/relay/files");

        r.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await BodyAsync(r)).GetProperty("error").GetString().Should().Be(ProjectCapabilities.NoRelayReason);
        _relay.Opened.Should().Be(0);
    }

    [Fact]
    public async Task КаналОтказал_409СПричиной()
    {
        var id = await CreateAsync(Device);
        _relay.Refuse = new DeviceExecRefusedException(DeviceExecRefusal.Offline, "Устройство «home» офлайн.");

        var r = await _client.GetAsync($"/api/projects/{id}/relay/files");

        r.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await BodyAsync(r)).GetProperty("error").GetString().Should().Be("Устройство «home» офлайн.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task СвязьОборваласьДоОтвета_409_АНеТаймаут(bool beforeRequest)
    {
        var id = await CreateAsync(Device);
        // Закрыть канал до кадра запроса или после него: обе гонки — обрыв, а не 500
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _relay.Agent = async s =>
        {
            if (!beforeRequest) await s.FromServer.ReadAsync();
            await s.DisposeAsync();
            closed.TrySetResult();
        };
        _relay.BeforeReturn = beforeRequest ? closed.Task : Task.CompletedTask;

        var r = await _client.GetAsync($"/api/projects/{id}/relay/files");

        r.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await BodyAsync(r)).GetProperty("code").GetString().Should().Be(RelayProtocol.UnavailableCode);
    }

    [Fact]
    public async Task ОтветАгента_ПересылаетсяКакЕсть_ЗапросНесётОперациюИКореньПроекта()
    {
        var id = await CreateAsync(Device);
        RelayRequest? seen = null;
        _relay.Agent = async s =>
        {
            await foreach (var frame in s.FromServer.ReadAllAsync())
            {
                seen = JsonSerializer.Deserialize<RelayRequest>(frame.Payload.Span, RelayProtocol.Json);
                break;
            }
            var body = "{\"diff\":null}"u8.ToArray();
            await s.DeviceSendAsync(DeviceExecFrameChannel.Info, JsonSerializer.SerializeToUtf8Bytes(
                new RelayResponseHead(200, "application/json; charset=utf-8", body.Length), RelayProtocol.Json));
            await s.DeviceSendAsync(DeviceExecFrameChannel.Stdout, body);
            await s.DeviceExitAsync(0);
        };

        var r = await _client.GetAsync($"/api/projects/{id}/relay/git/diff?path=sub%2Fb.txt&staged=true");

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await r.Content.ReadAsStringAsync()).Should().Be("{\"diff\":null}");
        seen!.Operation.Should().Be(RelayOperations.GitDiff);
        seen.Path.Should().Be("sub/b.txt");
        seen.Staged.Should().BeTrue();
        seen.RootPath.Should().StartWith("/home/u/relay-");
    }
}
