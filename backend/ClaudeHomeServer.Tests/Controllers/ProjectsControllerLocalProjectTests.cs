using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.DeviceAgent.Composition;
using ClaudeHomeServer.DeviceAgent.Exec;
using ClaudeHomeServer.DeviceAgent.Relay;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Локальные проекты в REST (ADR-016, задача 3.1): создание только за флагом и только на
/// устройстве с exec, матрица и устройство в DTO (контракт для фронта, задача 3.4),
/// перепривязка при чатах — 409. Папку на устройстве создание и перепривязка проверяют ДО
/// сохранения настоящим обработчиком агента (RelayHandler + AgentPathPolicy) через канал
/// ретранслятора: нет папки, файл, вне корней, офлайн — 400; старый агент — пропуск.
/// </summary>
public sealed class ProjectsControllerLocalProjectTests : IDisposable
{
    private sealed class FakeDeviceExec : IDeviceExecChannel
    {
        public Dictionary<string, DeviceExecStatus> Devices { get; } = [];
        public DeviceExecStatus? GetStatus(string ownerId, string deviceId) => Devices.GetValueOrDefault(deviceId);
        public Task<IDeviceExecStream> OpenAsync(string ownerId, string deviceId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Канал ретранслятора по устройствам: у каждого свой агент.</summary>
    private sealed class RelayByDevice : IDeviceRelayChannel
    {
        public Dictionary<string, LoopbackRelayChannel> Agents { get; } = [];
        public Task<IDeviceExecStream> OpenRelayAsync(string ownerId, string deviceId, CancellationToken ct = default) =>
            Agents[deviceId].OpenRelayAsync(ownerId, deviceId, ct);
    }

    private sealed class AllowedRoots(params string[] roots) : IAgentRoots
    {
        public IReadOnlyList<string> Roots { get; } = roots;
    }

    private readonly FakeDeviceExec _devices = new();
    private readonly RelayByDevice _relay = new();
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    // Машина устройства: разрешённый корень агента и «чужой» каталог рядом
    private readonly string _machine = Path.Combine(Path.GetTempPath(), "lp-agent-" + Guid.NewGuid().ToString("N"));
    private string AllowedRoot => Path.Combine(_machine, "allowed");
    private string Outside => Path.Combine(_machine, "outside");

    public ProjectsControllerLocalProjectTests()
    {
        Directory.CreateDirectory(AllowedRoot);
        Directory.CreateDirectory(Outside);

        _devices.Devices["dev-exec"] = Status("dev-exec", online: true, DeviceCapabilities.Exec, DeviceCapabilities.Relay);
        _devices.Devices["dev-offline"] = Status("dev-offline", online: false, DeviceCapabilities.Exec, DeviceCapabilities.Relay);
        _devices.Devices["dev-old"] = Status("dev-old", online: true, DeviceCapabilities.Exec);
        _devices.Devices["dev-old-relay"] = Status("dev-old-relay", online: true, DeviceCapabilities.Exec, DeviceCapabilities.Relay);
        _devices.Devices["dev-noexec"] = Status("dev-noexec", online: true, DeviceCapabilities.Files);

        _factory = new TestWebApplicationFactory
        {
            ExtraServices = s =>
            {
                s.AddSingleton<IDeviceExecChannel>(_devices);
                s.AddSingleton<IDeviceRelayChannel>(_relay);
            },
        };
        _client = _factory.CreateAuthenticatedClient();

        // Папки сервера тоже разрешены агенту: «один путь на сервере и устройстве»
        var git = new GitService(AgentLauncherFactory.Instance);
        var policy = new AgentPathPolicy(new AllowedRoots(AllowedRoot, _factory.TempDir));
        var agent = new RelayHandler(new AgentProjectFiles(new FileService(git), policy), git);
        _relay.Agents["dev-exec"] = new LoopbackRelayChannel { Agent = agent.RunAsync };
        _relay.Agents["dev-old-relay"] = new LoopbackRelayChannel { Agent = OldAgentAsync };
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_machine, recursive: true); } catch { /* временная папка */ }
    }

    // Агент до операции check-path: незнакомая операция — 400, как у RelayHandler того времени
    private static async Task OldAgentAsync(ExecLink link, CancellationToken ct)
    {
        await using var owned = link;
        await foreach (var _ in link.ReadAllAsync(ct)) break;
        var body = JsonSerializer.SerializeToUtf8Bytes(new { error = "Операция не поддерживается ретранслятором" }, RelayProtocol.Json);
        await link.SendAsync(DeviceExecFrameChannel.Info,
            JsonSerializer.SerializeToUtf8Bytes(new RelayResponseHead(400, "application/json", body.LongLength), RelayProtocol.Json), ct);
        await link.SendAsync(DeviceExecFrameChannel.Stdout, body, ct);
        await link.SendAsync(DeviceExecFrameChannel.Exit, DeviceExecJson.Serialize(new DeviceExecExit(0)), ct);
        await link.DrainAsync(TimeSpan.FromSeconds(10));
    }

    private static DeviceExecStatus Status(string id, bool online, params string[] caps) =>
        new(id, "home", online, "linux-x64", "1.0.0", "2.0.0", "2.0.0", caps, true, null);

    private async Task SetFlagAsync(bool enabled) =>
        (await _client.PutAsJsonAsync($"/api/feature-flags/{FeatureFlagKeys.LocalProjects}", new { enabled }))
            .EnsureSuccessStatusCode();

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());

    private static async Task<string> ErrorAsync(HttpResponseMessage r) =>
        (await BodyAsync(r)).GetProperty("error").GetString()!;

    private string MkDir()
    {
        var dir = Path.Combine(_factory.TempDir, "lp_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Папка на «устройстве» под разрешённым корнем агента
    private string DeviceDir()
    {
        var dir = Path.Combine(AllowedRoot, "p_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private Task<HttpResponseMessage> CreateLocalAsync(string deviceId, string rootPath) =>
        _client.PostAsJsonAsync("/api/projects", new { name = "L" + Guid.NewGuid().ToString("N")[..6], rootPath, deviceId });

    private async Task<string> CreateServerAsync()
    {
        var created = await _client.PostAsJsonAsync("/api/projects", new { name = "S" + Guid.NewGuid().ToString("N")[..6], rootPath = MkDir() });
        return (await BodyAsync(created)).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Создание_БезФлага_400()
    {
        var r = await CreateLocalAsync("dev-exec", DeviceDir());

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Создание_УстройствоБезExec_400()
    {
        await SetFlagAsync(true);

        var r = await CreateLocalAsync("dev-noexec", DeviceDir());

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(r)).Should().Be(ProjectCapabilities.NoExecReason);
    }

    [Fact]
    public async Task Создание_НеизвестноеУстройство_400()
    {
        await SetFlagAsync(true);

        var r = await CreateLocalAsync("nope", DeviceDir());

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Создание_ФлагИExec_201_ВDtoМатрицаИУстройство()
    {
        await SetFlagAsync(true);

        var r = await _client.PostAsJsonAsync("/api/projects",
            new { name = "L", rootPath = DeviceDir(), deviceId = "dev-exec", enableGit = true });

        r.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await BodyAsync(r)).GetProperty("id").GetString()!;
        // Устройство ушло в офлайн после создания: матрица в DTO это показывает
        _devices.Devices["dev-exec"] = Status("dev-exec", online: false, DeviceCapabilities.Exec, DeviceCapabilities.Relay);
        var body = await BodyAsync(await _client.GetAsync($"/api/projects/{id}"));
        body.GetProperty("deviceId").GetString().Should().Be("dev-exec");
        body.GetProperty("device").GetProperty("online").GetBoolean().Should().BeFalse();
        body.GetProperty("device").GetProperty("name").GetString().Should().Be("home");
        var caps = body.GetProperty("capabilities");
        caps.GetProperty("host").GetString().Should().Be(CapabilityHost.Device);
        caps.GetProperty("files").GetProperty("host").GetString().Should().Be(CapabilityHost.Device);
        caps.GetProperty("platform").GetProperty("available").GetBoolean().Should().BeTrue();
        caps.GetProperty("serverContent").GetProperty("host").GetString().Should().Be(CapabilityHost.Off);
        caps.GetProperty("exec").GetProperty("available").GetBoolean().Should().BeFalse();
        caps.GetProperty("exec").GetProperty("reason").GetString().Should().Be(ProjectCapabilities.DeviceOfflineReason);
    }

    // ---------- проверка папки на устройстве ----------

    [Fact]
    public async Task Создание_НетПапкиНаУстройстве_400_ПроектНеСоздан()
    {
        await SetFlagAsync(true);
        var missing = Path.Combine(AllowedRoot, "nope");

        var r = await CreateLocalAsync("dev-exec", missing);

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(r)).Should().Be($"Папки «{missing}» нет на устройстве «home». Создайте её или укажите другую");
        (await _client.GetFromJsonAsync<JsonElement>("/api/projects")).EnumerateArray()
            .Should().NotContain(p => p.GetProperty("rootPath").GetString() == missing);
    }

    [Fact]
    public async Task Создание_ПапкаВнеКорней_400СПодсказкойКоманды()
    {
        await SetFlagAsync(true);

        var r = await CreateLocalAsync("dev-exec", Outside);

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(r)).Should().StartWith("Папка вне разрешённых на устройстве")
            .And.Contain($"ai-home-agent roots add \"{Outside}\"");
    }

    [Fact]
    public async Task Создание_ПутьКФайлу_400()
    {
        await SetFlagAsync(true);
        var file = Path.Combine(DeviceDir(), "a.txt");
        File.WriteAllText(file, "x");

        var r = await CreateLocalAsync("dev-exec", file);

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(r)).Should().Contain("файл, а не папка");
    }

    [Fact]
    public async Task Создание_УстройствоОфлайн_400_ПроверитьНельзя()
    {
        await SetFlagAsync(true);

        var r = await CreateLocalAsync("dev-offline", DeviceDir());

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(r)).Should().Be("Устройство не в сети — проверить папку нельзя. Включите компьютер с агентом");
    }

    [Theory]
    [InlineData("dev-old")]       // агент без ретранслятора вовсе
    [InlineData("dev-old-relay")] // ретранслятор есть, операции check-path нет
    public async Task Создание_СтарыйАгент_ПроверкаПропущена_201(string deviceId)
    {
        await SetFlagAsync(true);

        var r = await CreateLocalAsync(deviceId, Path.Combine(AllowedRoot, "nope"));

        r.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Перепривязка_НетПапкиНаУстройстве_400_ПривязкаНеМеняется()
    {
        await SetFlagAsync(true);
        var id = await CreateServerAsync();

        var r = await _client.PutAsJsonAsync($"/api/projects/{id}/device",
            new { deviceId = "dev-exec", rootPath = Path.Combine(AllowedRoot, "nope") });

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(r)).Should().Contain("нет на устройстве «home»");
        (await BodyAsync(await _client.GetAsync($"/api/projects/{id}"))).GetProperty("deviceId").ValueKind
            .Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Перепривязка_ВнеКорней_400()
    {
        await SetFlagAsync(true);
        var id = await CreateServerAsync();

        var r = await _client.PutAsJsonAsync($"/api/projects/{id}/device", new { deviceId = "dev-exec", rootPath = Outside });

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(r)).Should().StartWith("Папка вне разрешённых на устройстве");
    }

    [Fact]
    public async Task Перепривязка_УстройствоОфлайн_400()
    {
        await SetFlagAsync(true);
        var id = await CreateServerAsync();

        var r = await _client.PutAsJsonAsync($"/api/projects/{id}/device", new { deviceId = "dev-offline", rootPath = DeviceDir() });

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(r)).Should().Contain("Устройство не в сети");
    }

    [Fact]
    public async Task Перепривязка_ТаЖеПривязка_УстройствоНеСпрашивается()
    {
        await SetFlagAsync(true);
        var dir = DeviceDir();
        var id = (await BodyAsync(await CreateLocalAsync("dev-exec", dir))).GetProperty("id").GetString()!;
        _devices.Devices["dev-exec"] = Status("dev-exec", online: false, DeviceCapabilities.Exec, DeviceCapabilities.Relay);

        var r = await _client.PutAsJsonAsync($"/api/projects/{id}/device", new { deviceId = "dev-exec", rootPath = dir });

        r.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---------- прежнее поведение ----------

    [Fact]
    public async Task СерверныйПроект_МатрицаВсёНаСервере()
    {
        var r = await _client.PostAsJsonAsync("/api/projects", new { name = "S", rootPath = MkDir() });

        r.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await BodyAsync(r);
        body.GetProperty("deviceId").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("device").ValueKind.Should().Be(JsonValueKind.Null);
        var caps = body.GetProperty("capabilities");
        caps.GetProperty("host").GetString().Should().Be(CapabilityHost.Server);
        caps.GetProperty("serverContent").GetProperty("available").GetBoolean().Should().BeTrue();
        caps.GetProperty("exec").GetProperty("available").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ОдинПутьНаСервереИУстройстве_ДваПроекта()
    {
        await SetFlagAsync(true);
        var dir = MkDir();

        (await _client.PostAsJsonAsync("/api/projects", new { name = "S", rootPath = dir }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await _client.PostAsJsonAsync("/api/projects", new { name = "L", rootPath = dir, deviceId = "dev-exec" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Перепривязка_ПриЧатах_409()
    {
        await SetFlagAsync(true);
        var id = await CreateServerAsync();
        (await _client.PostAsJsonAsync($"/api/projects/{id}/sessions", new { mode = "auto" })).EnsureSuccessStatusCode();

        var r = await _client.PutAsJsonAsync($"/api/projects/{id}/device",
            new { deviceId = "dev-exec", rootPath = DeviceDir() });

        r.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Перепривязка_БезЧатов_200()
    {
        await SetFlagAsync(true);
        var id = await CreateServerAsync();

        var r = await _client.PutAsJsonAsync($"/api/projects/{id}/device",
            new { deviceId = "dev-exec", rootPath = DeviceDir() });

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyAsync(r)).GetProperty("deviceId").GetString().Should().Be("dev-exec");
    }

    [Fact]
    public async Task Перепривязка_БезФлага_400()
    {
        var id = await CreateServerAsync();

        var r = await _client.PutAsJsonAsync($"/api/projects/{id}/device",
            new { deviceId = "dev-exec", rootPath = DeviceDir() });

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
