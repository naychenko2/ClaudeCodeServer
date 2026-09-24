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
/// Локальные проекты в REST (ADR-016, задача 3.1): создание только за флагом и только на
/// устройстве с exec, матрица и устройство в DTO (контракт для фронта, задача 3.4),
/// перепривязка при чатах — 409.
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

    private readonly FakeDeviceExec _devices = new();
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ProjectsControllerLocalProjectTests()
    {
        _devices.Devices["dev-exec"] = Status("dev-exec", online: false, DeviceCapabilities.Exec);
        _devices.Devices["dev-noexec"] = Status("dev-noexec", online: true, DeviceCapabilities.Files);
        _factory = new TestWebApplicationFactory
        {
            ExtraServices = s => s.AddSingleton<IDeviceExecChannel>(_devices),
        };
        _client = _factory.CreateAuthenticatedClient();
    }

    public void Dispose() => _factory.Dispose();

    private static DeviceExecStatus Status(string id, bool online, params string[] caps) =>
        new(id, "home", online, "linux-x64", "1.0.0", "2.0.0", "2.0.0", caps, true, null);

    private async Task SetFlagAsync(bool enabled) =>
        (await _client.PutAsJsonAsync($"/api/feature-flags/{FeatureFlagKeys.LocalProjects}", new { enabled }))
            .EnsureSuccessStatusCode();

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());

    private string MkDir()
    {
        var dir = Path.Combine(_factory.TempDir, "lp_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Создание_БезФлага_400()
    {
        var r = await _client.PostAsJsonAsync("/api/projects",
            new { name = "L", rootPath = "/home/u/p", deviceId = "dev-exec" });

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Создание_УстройствоБезExec_400()
    {
        await SetFlagAsync(true);

        var r = await _client.PostAsJsonAsync("/api/projects",
            new { name = "L", rootPath = "/home/u/p", deviceId = "dev-noexec" });

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(r)).GetProperty("error").GetString().Should().Be(ProjectCapabilities.NoExecReason);
    }

    [Fact]
    public async Task Создание_НеизвестноеУстройство_400()
    {
        await SetFlagAsync(true);

        var r = await _client.PostAsJsonAsync("/api/projects",
            new { name = "L", rootPath = "/home/u/p", deviceId = "nope" });

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Создание_ФлагИExec_201_ВDtoМатрицаИУстройство()
    {
        await SetFlagAsync(true);

        var r = await _client.PostAsJsonAsync("/api/projects",
            new { name = "L", rootPath = "/home/u/p", deviceId = "dev-exec", enableGit = true });

        r.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await BodyAsync(r);
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
        var created = await _client.PostAsJsonAsync("/api/projects", new { name = "S", rootPath = MkDir() });
        var id = (await BodyAsync(created)).GetProperty("id").GetString()!;
        (await _client.PostAsJsonAsync($"/api/projects/{id}/sessions", new { mode = "auto" })).EnsureSuccessStatusCode();

        var r = await _client.PutAsJsonAsync($"/api/projects/{id}/device",
            new { deviceId = "dev-exec", rootPath = "/home/u/p" });

        r.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Перепривязка_БезЧатов_200()
    {
        await SetFlagAsync(true);
        var created = await _client.PostAsJsonAsync("/api/projects", new { name = "S", rootPath = MkDir() });
        var id = (await BodyAsync(created)).GetProperty("id").GetString()!;

        var r = await _client.PutAsJsonAsync($"/api/projects/{id}/device",
            new { deviceId = "dev-exec", rootPath = "/home/u/p" });

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyAsync(r)).GetProperty("deviceId").GetString().Should().Be("dev-exec");
    }

    [Fact]
    public async Task Перепривязка_БезФлага_400()
    {
        var created = await _client.PostAsJsonAsync("/api/projects", new { name = "S", rootPath = MkDir() });
        var id = (await BodyAsync(created)).GetProperty("id").GetString()!;

        var r = await _client.PutAsJsonAsync($"/api/projects/{id}/device",
            new { deviceId = "dev-exec", rootPath = "/home/u/p" });

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
