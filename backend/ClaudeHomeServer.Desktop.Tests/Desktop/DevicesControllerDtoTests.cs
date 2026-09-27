using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Desktop;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.Desktop;

/// <summary>
/// Контракт GET /api/devices с фронтом (тип DesktopDevice во frontend/src/types): пикер
/// «Добавить проект → Локальный» пускает устройство только при <c>capabilities.exec === true</c>,
/// а подпись берёт из <c>online</c> и <c>platform</c>. Без этих полей в ответе список всегда
/// пуст — локальный проект из UI не создать.
/// </summary>
public class DevicesControllerDtoTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ccs-devices-dto-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly DeviceRegistry _registry;
    private readonly UserStore _users;
    private readonly DesktopCallRouter _router;
    private readonly string _ownerId;

    public DevicesControllerDtoTests()
    {
        Directory.CreateDirectory(_dir);
        _registry = new DeviceRegistry(_dir);

        var hasher = new PasswordHasher<User>();
        var owner = new User { Username = "owner", Role = "admin" };
        owner.PasswordHash = hasher.HashPassword(owner, "пароль-владельца");
        File.WriteAllText(Path.Combine(_dir, "users.json"),
            JsonSerializer.Serialize(new { version = 1, users = new[] { owner } }));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
        }).Build();
        _users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = _users.FindByUsername("owner")!.Id;

        _router = new DesktopCallRouter(new SilentSender(), [], NullLogger<DesktopCallRouter>.Instance);
    }

    public void Dispose()
    {
        TestFs.DeleteDirectoryResilient(_dir);
        GC.SuppressFinalize(this);
    }

    private sealed class SilentSender : IDeviceCommandSender
    {
        public Task SendCallAsync(string connectionId, DesktopCallCommand command, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task SendGoAsync(string connectionId, DesktopGoCommand go, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task SendCancelAsync(string connectionId, DesktopCancelCommand cancel, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private DevicesController Controller()
    {
        var pairing = new DevicePairingService(_registry, _users, NullLogger<DevicePairingService>.Instance,
            new ConfigurationBuilder().Build(), new FakeHostEnvironment());
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, _ownerId)], "Bearer")),
        };
        return new DevicesController(_registry, pairing, _users, _router)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private string RegisterAgent(string name, string fingerprintSeed, IReadOnlyList<string> capabilities)
    {
        var (device, _) = _registry.Register(_ownerId, name, MachineFingerprint.Of(fingerprintSeed));
        _registry.UpdateAgentInfo(_ownerId, device.Id, new DeviceHello(
            DesktopProtocol.Version, null, "1.0.0", Platform: "linux-x64", AgentVersion: "1.0.0",
            Capabilities: capabilities));
        return device.Id;
    }

    private JsonElement ListItem(string deviceId)
    {
        var body = Controller().List().Should().BeOfType<OkObjectResult>().Subject.Value!;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(body));
        return doc.RootElement.EnumerateArray().Single(e => e.GetProperty("id").GetString() == deviceId).Clone();
    }

    [Fact]
    public async Task Список_ОтдаётOnlinePlatformИCapabilitiesВФормеФронта()
    {
        var id = RegisterAgent("home", "machine-home", [DeviceCapabilities.Exec, DeviceCapabilities.Files]);
        _router.RegisterConnection("conn", _ownerId, id);
        await _router.HelloAsync("conn", new DeviceHello(DesktopProtocol.Version, null, "1.0.0"));

        var item = ListItem(id);

        item.GetProperty("online").GetBoolean().Should().BeTrue();
        item.GetProperty("platform").GetString().Should().Be("linux-x64");
        item.GetProperty("capabilities").GetProperty("exec").GetBoolean().Should().BeTrue();
        item.GetProperty("capabilities").GetProperty("files").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void УстройствоБезExecИНеВСети_ОтдаётсяСЯвнымиFalse()
    {
        var id = RegisterAgent("work", "machine-work", [DeviceCapabilities.Files]);

        var item = ListItem(id);

        item.GetProperty("online").GetBoolean().Should().BeFalse();
        item.GetProperty("capabilities").GetProperty("exec").GetBoolean().Should().BeFalse();
        item.GetProperty("capabilities").GetProperty("files").GetBoolean().Should().BeTrue();
    }
}
