using ClaudeHomeServer.Services.Desktop;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Desktop;

/// <summary>
/// Реестр устройств десктопного агента (ADR-008, «Аутентификация и транспорт»).
/// Проверяем ровно то, на чём держится канал: секрета токена на сервере нет, отпечаток
/// машины сверяется, отзыв необратим, а версия токена монотонна — иначе восстановление
/// архива воскрешало бы отозванное устройство.
/// </summary>
public class DeviceRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ccs-devices-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string _fingerprint = MachineFingerprint.Of("ноутбук-андрея");
    private const string Owner = "u1";

    public DeviceRegistryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
        GC.SuppressFinalize(this);
    }

    private DeviceRegistry NewRegistry() => new(_dir);

    private string StoreText() => File.ReadAllText(Path.Combine(_dir, DeviceRegistry.FileName));

    [Fact]
    public void Сопряжение_ВыдаётТокенИПускаетПоНему()
    {
        var registry = NewRegistry();

        var (device, token) = registry.Register(Owner, "home", _fingerprint);

        device.Name.Should().Be("home");
        device.TokenVersion.Should().Be(1);
        token.Should().StartWith(device.Id + ".1.");

        registry.Authenticate(token, _fingerprint)!.Id.Should().Be(device.Id);
    }

    [Fact]
    public void СекретТокена_НаСервереНеХранится()
    {
        var registry = NewRegistry();

        var (_, token) = registry.Register(Owner, "home", _fingerprint);
        var secret = token.Split('.')[2];

        // В сторе только SHA-256: утечка devices.json не даёт руки на машине владельца
        StoreText().Should().NotContain(secret);
        StoreText().Should().Contain("TokenHash", "в сторе лежит хеш токена, а не сам токен");
    }

    [Fact]
    public void ЧужойОтпечаток_КаналНеОткрывает()
    {
        var registry = NewRegistry();
        var (_, token) = registry.Register(Owner, "home", _fingerprint);

        // Украденный токен, приехавший с другой машины — отказ: отпечаток сверяется,
        // а не просто хранится
        registry.Authenticate(token, MachineFingerprint.Of("чужая-машина")).Should().BeNull();
        registry.Authenticate(token, fingerprint: null).Should().BeNull();
    }

    [Fact]
    public void ПодменённыйСекрет_НеПроходит()
    {
        var registry = NewRegistry();
        var (device, _) = registry.Register(Owner, "home", _fingerprint);

        registry.Authenticate($"{device.Id}.1.подделка", _fingerprint).Should().BeNull();
        registry.Authenticate("мусор", _fingerprint).Should().BeNull();
        registry.Authenticate(null, _fingerprint).Should().BeNull();
    }

    [Fact]
    public void Отзыв_УбиваетТокенИОставляетНадгробие()
    {
        var registry = NewRegistry();
        var (device, token) = registry.Register(Owner, "home", _fingerprint);

        registry.Revoke(Owner, device.Id).Should().BeTrue();

        registry.Authenticate(token, _fingerprint).Should().BeNull();
        // Запись не удаляется: удалённая вернулась бы живой из любого архива
        registry.GetByOwner(Owner).Should().ContainSingle().Which.Revoked.Should().BeTrue();
        registry.Get(Owner, device.Id)!.TokenHash.Should().BeEmpty();
    }

    [Fact]
    public void ПовторноеСопряжениеТойЖеМашины_ВращаетТокенСРостомВерсии()
    {
        var registry = NewRegistry();
        var (device, oldToken) = registry.Register(Owner, "home", _fingerprint);

        var (same, newToken) = registry.Register(Owner, "home", _fingerprint);

        same.Id.Should().Be(device.Id, "та же машина под тем же именем — не второе устройство");
        same.TokenVersion.Should().Be(2);
        registry.Authenticate(newToken, _fingerprint).Should().NotBeNull();
        registry.Authenticate(oldToken, _fingerprint).Should().BeNull("прежняя выдача умирает");
    }

    [Fact]
    public void ВерсияТокена_МонотоннаЧерезОтзывИПовторноеСопряжение()
    {
        var registry = NewRegistry();
        var (first, revokedToken) = registry.Register(Owner, "home", _fingerprint);
        registry.Revoke(Owner, first.Id);

        var (second, _) = registry.Register(Owner, "home", _fingerprint);

        // Новая запись продолжает счётчик машины, а не начинает с 1: иначе отозванный
        // токен version=1 подошёл бы к свежесопряжённому устройству
        second.Id.Should().NotBe(first.Id);
        second.TokenVersion.Should().BeGreaterThan(first.TokenVersion);
        registry.Authenticate(revokedToken, _fingerprint).Should().BeNull();
    }

    [Fact]
    public void ОтзывПереживаетПерезагрузкуСтора()
    {
        var registry = NewRegistry();
        var (device, token) = registry.Register(Owner, "home", _fingerprint);
        registry.Revoke(Owner, device.Id);

        // Тот же файл, новый экземпляр реестра — восстановление стора не должно
        // «забывать» отзыв
        var reloaded = NewRegistry();

        reloaded.Authenticate(token, _fingerprint).Should().BeNull();
        reloaded.GetByOwner(Owner).Should().ContainSingle().Which.Revoked.Should().BeTrue();
    }

    [Fact]
    public void ИмяУникальноУВладельца_НоНеМеждуВладельцами()
    {
        var registry = NewRegistry();
        registry.Register(Owner, "home", _fingerprint);

        var busy = () => registry.Register(Owner, "HOME", MachineFingerprint.Of("вторая-машина"));
        busy.Should().Throw<InvalidOperationException>().WithMessage("*занято*");

        // У другого владельца «home» своё — реестр per-owner
        registry.Register("u2", "home", MachineFingerprint.Of("машина-другого")).Device.Name.Should().Be("home");
    }

    [Fact]
    public void ИмяОтозванногоУстройства_ОсвобождаетсяДляНовогоЖелеза()
    {
        var registry = NewRegistry();
        var (device, _) = registry.Register(Owner, "home", _fingerprint);
        registry.Revoke(Owner, device.Id);

        var (fresh, _) = registry.Register(Owner, "home", MachineFingerprint.Of("новый-ноутбук"));

        fresh.Name.Should().Be("home");
        registry.FindByName(Owner, "home")!.Id.Should().Be(fresh.Id, "надгробие адресатом не бывает");
    }

    [Fact]
    public void ИмяУстройства_ЭтоЧеловеческоеИмяИзИнструментов()
    {
        var registry = NewRegistry();
        var (device, _) = registry.Register(Owner, "  home  ", _fingerprint);

        device.Name.Should().Be("home", "пробелы по краям режем — имя диктуют вслух");
        registry.FindByName(Owner, "Home").Should().NotBeNull("регистр значения не имеет");
        registry.FindByName(Owner, "work").Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("это-очень-длинное-имя-устройства-которое-никто-не-продиктует")]
    [InlineData("home/../..")]
    public void НегодноеИмя_Отказ(string name)
    {
        var registry = NewRegistry();
        var bad = () => registry.Register(Owner, name, _fingerprint);
        bad.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void НегодныйОтпечаток_Отказ()
    {
        var registry = NewRegistry();
        var bad = () => registry.Register(Owner, "home", "не-хеш");
        bad.Should().Throw<InvalidOperationException>().WithMessage("*тпечаток*");
    }

    [Fact]
    public void Переименование_НеТрогаетТокен()
    {
        var registry = NewRegistry();
        var (device, token) = registry.Register(Owner, "home", _fingerprint);

        registry.Rename(Owner, device.Id, "work")!.Name.Should().Be("work");

        registry.Authenticate(token, _fingerprint).Should().NotBeNull();
        registry.FindByName(Owner, "work").Should().NotBeNull();
    }

    [Fact]
    public void ЧужоеУстройство_НедостижимоПоId()
    {
        var registry = NewRegistry();
        var (device, _) = registry.Register(Owner, "home", _fingerprint);

        registry.Get("u2", device.Id).Should().BeNull();
        registry.Rename("u2", device.Id, "чужое").Should().BeNull();
        registry.Revoke("u2", device.Id).Should().BeFalse();
    }

    [Fact]
    public void ОтпечатокХоста_СчитаетсяОдинаковоДляОдногоИмениМашины()
    {
        // Контракт сопряжения: клиент считает ровно эту строку, поэтому регистр и пробелы
        // не должны давать разных отпечатков одной машины
        MachineFingerprint.Of("MyHost").Should().Be(MachineFingerprint.Of(" myhost "));
        MachineFingerprint.OfHost().Should().Be(MachineFingerprint.Of(Environment.MachineName));
        MachineFingerprint.IsValid(MachineFingerprint.OfHost()).Should().BeTrue();
        MachineFingerprint.IsValid("короткий").Should().BeFalse();
    }
}
