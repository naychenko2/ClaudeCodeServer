using System.Net;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Desktop;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.Desktop;

/// <summary>
/// Сопряжение устройства одноразовым кодом (ADR-008): 8 символов, 5 минут, ≤5 попыток
/// с счётчиком ПО ВЛАДЕЛЬЦУ И ЭНДПОИНТУ. Счётчик по коду был бы фикцией: перебор
/// обходится перевыпуском кода.
/// </summary>
public class DevicePairingTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ccs-pairing-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string _fingerprint = MachineFingerprint.Of("ноутбук-андрея");
    private readonly DeviceRegistry _registry;
    private readonly UserStore _users;
    private readonly DevicePairingService _pairing;
    private readonly string _ownerId;

    private const string WebSession = "веб-сессия-1";

    public DevicePairingTests()
    {
        Directory.CreateDirectory(_dir);
        _registry = new DeviceRegistry(_dir);

        // Настоящий UserStore на временном каталоге: код привязан к веб-сессии, а её
        // актуальность проверяется по версии токенов пользователя
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
        }).Build();
        WriteUsersFile();
        _users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = _users.FindByUsername("owner")!.Id;
        _pairing = new DevicePairingService(_registry, _users);
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

    private DevicePairingCode Start(DateTime? now = null) =>
        _pairing.Start(_ownerId, WebSession, TokenVersion(), now);

    private int TokenVersion() => _users.GetById(_ownerId)!.TokenVersion;

    [Fact]
    public void Код_ВосемьСимволовИзНазванногоАлфавита()
    {
        var code = Start();

        code.Code.Should().HaveLength(DevicePairingService.CodeLength);
        code.Code.Should().MatchRegex($"^[{DevicePairingService.Alphabet}]+$");
        // Двусмысленных символов в алфавите нет: код диктуют голосом
        DevicePairingService.Alphabet.Should().NotContain("I").And.NotContain("O")
            .And.NotContain("0").And.NotContain("1");
    }

    [Fact]
    public void ВерныйКод_МеняетсяНаТокенРовноОдинРаз()
    {
        var code = Start();

        var result = _pairing.Redeem(DevicePairingService.PairEndpoint, code.Code, "home", _fingerprint);

        result.Status.Should().Be(DevicePairingStatus.Ok);
        result.Token.Should().NotBeNullOrWhiteSpace();
        _registry.Authenticate(result.Token, _fingerprint).Should().NotBeNull();

        // Повторный обмен тем же кодом — уже нет: код одноразовый
        _pairing.Redeem(DevicePairingService.PairEndpoint, code.Code, "work", _fingerprint)
            .Status.Should().Be(DevicePairingStatus.BadCode);
    }

    [Fact]
    public void КодЖивётПятьМинут()
    {
        var issued = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc);
        var code = Start(issued);

        code.ExpiresAt.Should().Be(issued.Add(DevicePairingService.CodeLifetime));

        // Время подаём явным аргументом: ждать пять минут в тесте нельзя, а спать —
        // тем более (CI слабее и таймингам не верит)
        _pairing.Redeem(DevicePairingService.PairEndpoint, code.Code, "home", _fingerprint,
                now: issued.AddMinutes(5).AddSeconds(1))
            .Status.Should().Be(DevicePairingStatus.BadCode);
    }

    [Fact]
    public void ПятьПромахов_ГасятЗаявку()
    {
        var code = Start();

        for (var i = 0; i < DevicePairingService.MaxAttempts; i++)
            _pairing.Redeem(DevicePairingService.PairEndpoint, "ABCDEFGH", "home", _fingerprint)
                .Status.Should().Be(DevicePairingStatus.BadCode);

        // Даже верный код после исчерпания попыток не работает: заявка погашена
        _pairing.Redeem(DevicePairingService.PairEndpoint, code.Code, "home", _fingerprint)
            .Status.Should().Be(DevicePairingStatus.BadCode);
        _pairing.AttemptsLeft(_ownerId, DevicePairingService.PairEndpoint).Should().Be(0);
    }

    [Fact]
    public void СчётчикПопыток_НеОбнуляетсяВыпускомНовогоКода()
    {
        Start();
        for (var i = 0; i < 3; i++)
            _pairing.Redeem(DevicePairingService.PairEndpoint, "ABCDEFGH", "home", _fingerprint);

        var second = Start();

        // Ровно этот случай счётчик по владельцу и закрывает: по коду он бы обнулился
        second.AttemptsLeft.Should().Be(DevicePairingService.MaxAttempts - 3);

        _pairing.Redeem(DevicePairingService.PairEndpoint, "JKLMNPQR", "home", _fingerprint);
        _pairing.Redeem(DevicePairingService.PairEndpoint, "JKLMNPQR", "home", _fingerprint);
        _pairing.Redeem(DevicePairingService.PairEndpoint, second.Code, "home", _fingerprint)
            .Status.Should().Be(DevicePairingStatus.BadCode, "попытки владельца уже исчерпаны");
    }

    [Fact]
    public void ОкноПопыток_ЗакрываетсяЧерезПятьМинут()
    {
        var start = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc);
        Start(start);
        for (var i = 0; i < DevicePairingService.MaxAttempts; i++)
            _pairing.Redeem(DevicePairingService.PairEndpoint, "ABCDEFGH", "home", _fingerprint, now: start);

        var later = start.AddMinutes(6);
        var fresh = _pairing.Start(_ownerId, WebSession, TokenVersion(), later);

        fresh.AttemptsLeft.Should().Be(DevicePairingService.MaxAttempts, "окно перебора закрылось");
        _pairing.Redeem(DevicePairingService.PairEndpoint, fresh.Code, "home", _fingerprint, now: later)
            .Status.Should().Be(DevicePairingStatus.Ok);
    }

    [Fact]
    public void Заявка_ВиднаТолькоВыпустившейВебСессии()
    {
        var code = Start();

        _pairing.GetPending(_ownerId, WebSession)!.Code.Should().Be(code.Code);
        _pairing.GetPending(_ownerId, "другая-вкладка-другой-сессии").Should().BeNull();
        _pairing.Cancel(_ownerId, "другая-вкладка-другой-сессии").Should().BeFalse();

        _pairing.Cancel(_ownerId, WebSession).Should().BeTrue();
        _pairing.GetPending(_ownerId, WebSession).Should().BeNull();
    }

    [Fact]
    public void СменаПароля_УбиваетВыпущенныйКод()
    {
        var code = Start();

        // Смена пароля бампает версию токенов: веб-сессия, выпустившая код, умерла
        _users.SetPassword(_users.GetById(_ownerId)!, "новый-пароль");

        _pairing.Redeem(DevicePairingService.PairEndpoint, code.Code, "home", _fingerprint)
            .Status.Should().Be(DevicePairingStatus.SessionGone);
    }

    [Fact]
    public void МашинаБэкенда_СопряжениеНеПолучает()
    {
        var code = Start();

        var result = _pairing.Redeem(
            DevicePairingService.PairEndpoint, code.Code, "home", MachineFingerprint.OfHost());

        // Руки на машине сервера — это не грань, а обход изоляции
        result.Status.Should().Be(DevicePairingStatus.SameHost);
        _registry.GetByOwner(_ownerId).Should().BeEmpty();
    }

    [Fact]
    public void НегодноеИмя_ЗаявкуНеГасит()
    {
        var code = Start();

        _pairing.Redeem(DevicePairingService.PairEndpoint, code.Code, "", _fingerprint)
            .Status.Should().Be(DevicePairingStatus.Rejected);

        // Человек ошибся именем, а не подбирал код — заявка жива
        _pairing.Redeem(DevicePairingService.PairEndpoint, code.Code, "home", _fingerprint)
            .Status.Should().Be(DevicePairingStatus.Ok);
    }

    [Fact]
    public void ЧужаяЗаявка_НеПлатитЗаПромахПоСвоейИСохраняетСвойКод()
    {
        // Две живые заявки: промах бьёт по обеим (какую подбирают — не видно), но
        // владельцы независимы по счётчику
        var mine = Start();
        _pairing.Redeem(DevicePairingService.PairEndpoint, "ABCDEFGH", "home", _fingerprint);

        _pairing.AttemptsLeft(_ownerId, DevicePairingService.PairEndpoint)
            .Should().Be(DevicePairingService.MaxAttempts - 1);
        _pairing.AttemptsLeft("другой-владелец", DevicePairingService.PairEndpoint)
            .Should().Be(DevicePairingService.MaxAttempts);
        _pairing.GetPending(_ownerId, WebSession)!.Code.Should().Be(mine.Code);
    }

    [Theory]
    // HTTPS — годится всегда
    [InlineData(true, "203.0.113.7", false, true)]
    [InlineData(true, "127.0.0.1", true, true)]
    // Открытый канал из сети — нет: подменный сервер сам сочинит текст подтверждения
    [InlineData(false, "192.168.1.20", false, false)]
    // Петля без прокси — подслушивать нечего
    [InlineData(false, "127.0.0.1", false, true)]
    // Петля, но запрос пришёл через прокси: адрес соединения больше ничего не доказывает
    [InlineData(false, "127.0.0.1", true, false)]
    public void КодИТокен_НеЕдутПоНешифрованномуКаналу(
        bool https, string ip, bool viaProxy, bool expected) =>
        DeviceChannelGuard.IsSecure(https, IPAddress.Parse(ip), viaProxy).Should().Be(expected);
}
