using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Desktop;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.Desktop;

/// <summary>
/// Отладочный люк `Desktop:AllowSameHostPairing` (ADR-008): сопряжение с машиной самого
/// бэкенда по умолчанию запрещено — руки на машине сервера обходят изоляцию песочницы,
/// ради которой грань и заведена.
///
/// Люк нужен разработчику: у него продукт и десктопный клиент на одном компьютере, и без
/// него сквозной путь «сервер ↔ клиент» не проверить. Поэтому у него ДВА независимых
/// замка — выключен по умолчанию и действует только в Development, — и оба под тестом:
/// молчаливо открывшийся люк на боевом инстансе выглядел бы как работающее правило.
/// </summary>
public class DevicePairingSameHostHatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ccs-pairing-hatch-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly DeviceRegistry _registry;
    private readonly UserStore _users;
    private readonly string _ownerId;

    private const string WebSession = "веб-сессия-1";

    public DevicePairingSameHostHatchTests()
    {
        Directory.CreateDirectory(_dir);
        _registry = new DeviceRegistry(_dir);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
        }).Build();
        WriteUsersFile();
        _users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = _users.FindByUsername("owner")!.Id;
    }

    private void WriteUsersFile()
    {
        var hasher = new PasswordHasher<User>();
        var owner = new User { Username = "owner", Role = "admin" };
        owner.PasswordHash = hasher.HashPassword(owner, "пароль-владельца");
        File.WriteAllText(Path.Combine(_dir, "users.json"),
            System.Text.Json.JsonSerializer.Serialize(new { version = 1, users = new[] { owner } }));
    }

    public void Dispose()
    {
        TestFs.DeleteDirectoryResilient(_dir);
        GC.SuppressFinalize(this);
    }

    private DevicePairingService Pairing(bool? allowSameHost, string environment)
    {
        var settings = new Dictionary<string, string?>();
        if (allowSameHost is { } value) settings["Desktop:AllowSameHostPairing"] = value ? "true" : "false";

        return new DevicePairingService(_registry, _users, NullLogger<DevicePairingService>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            new FakeHostEnvironment(environment));
    }

    // Обмен кода на токен с отпечатком машины самого бэкенда — тот самый случай, ради
    // которого люк и заведён
    private DevicePairingResult RedeemAsHost(DevicePairingService pairing)
    {
        var code = pairing.Start(_ownerId, WebSession, _users.GetById(_ownerId)!.TokenVersion);
        return pairing.Redeem(DevicePairingService.PairEndpoint, code.Code, "home", MachineFingerprint.OfHost());
    }

    [Fact]
    public void ЛюкЗакрытПоУмолчанию_МашинаБэкендаОтказ()
    {
        var result = RedeemAsHost(Pairing(allowSameHost: null, Environments.Development));

        result.Status.Should().Be(DevicePairingStatus.SameHost);
        _registry.GetByOwner(_ownerId).Should().BeEmpty();
    }

    [Fact]
    public void ЛюкОткрытВDevelopment_МашинаБэкендаСопрягается()
    {
        var result = RedeemAsHost(Pairing(allowSameHost: true, Environments.Development));

        result.Status.Should().Be(DevicePairingStatus.Ok);
        result.Token.Should().NotBeNullOrWhiteSpace();
        _registry.GetByOwner(_ownerId).Should().ContainSingle().Which.Name.Should().Be("home");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void ВнеDevelopment_ЛюкНеРаботаетДажеСВключённымКлючом(string environment)
    {
        var result = RedeemAsHost(Pairing(allowSameHost: true, environment));

        // Ключ мог уехать на боевую машину вместе с чужим appsettings.Local.json —
        // среда обязана отменить его сама
        result.Status.Should().Be(DevicePairingStatus.SameHost);
        _registry.GetByOwner(_ownerId).Should().BeEmpty();
    }

    [Fact]
    public void ЛюкНеТрогаетОстальныеПроверки_ЧужаяМашинаСопрягаетсяКакРаньше()
    {
        var pairing = Pairing(allowSameHost: false, Environments.Development);
        var code = pairing.Start(_ownerId, WebSession, _users.GetById(_ownerId)!.TokenVersion);

        var result = pairing.Redeem(DevicePairingService.PairEndpoint, code.Code, "home",
            MachineFingerprint.Of("ноутбук-андрея"));

        result.Status.Should().Be(DevicePairingStatus.Ok);
    }
}
