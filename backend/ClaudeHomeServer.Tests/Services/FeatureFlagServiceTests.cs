using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

public class FeatureFlagServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly UserStore _users;
    private readonly FeatureFlagService _sut;
    private readonly string _userId;

    public FeatureFlagServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ff_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _users = CreateUserStore();
        // UserStore при пустом хранилище создаёт дефолтного admin — используем его
        _userId = _users.GetFirst()!.Id;
        _sut = new FeatureFlagService(_users);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private UserStore CreateUserStore() => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json")
            }).Build(),
        new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(),
        NullLogger<UserStore>.Instance);

    // --- GetEffective: дефолты ---

    [Fact]
    public void GetEffective_NoOverrides_ReturnsCatalogDefaults()
    {
        var effective = _sut.GetEffective(_userId);

        foreach (var def in FeatureFlagCatalog.All)
            effective[def.Key].Should().Be(def.Default, $"у флага '{def.Key}' нет override — ожидается дефолт каталога");
    }

    [Fact]
    public void GetEffective_UnknownUser_ReturnsCatalogDefaults()
    {
        var effective = _sut.GetEffective("no-such-user");

        foreach (var def in FeatureFlagCatalog.All)
            effective[def.Key].Should().Be(def.Default);
    }

    [Fact]
    public void GetEffective_ContainsAllCatalogKeys()
    {
        var effective = _sut.GetEffective(_userId);

        effective.Keys.Should().BeEquivalentTo(FeatureFlagCatalog.All.Select(f => f.Key));
    }

    // --- Override ---

    [Fact]
    public void SetFeatureFlag_Override_TakesPrecedenceOverDefault()
    {
        var def = FeatureFlagCatalog.All[0];

        _users.SetFeatureFlag(_userId, def.Key, !def.Default).Should().BeTrue();

        var effective = _sut.GetEffective(_userId);
        effective[def.Key].Should().Be(!def.Default, "override юзера важнее дефолта каталога");
        // Остальные флаги не затронуты
        foreach (var other in FeatureFlagCatalog.All.Skip(1))
            effective[other.Key].Should().Be(other.Default);
    }

    [Fact]
    public void SetFeatureFlag_Override_PersistsAcrossStoreInstances()
    {
        var def = FeatureFlagCatalog.All[0];
        _users.SetFeatureFlag(_userId, def.Key, !def.Default);

        // «Рестарт»: новый UserStore читает users.json с тем же DataPath
        var restartedService = new FeatureFlagService(CreateUserStore());

        restartedService.GetEffective(_userId)[def.Key].Should().Be(!def.Default);
    }

    [Fact]
    public void SetFeatureFlag_UnknownUser_ReturnsFalse()
    {
        _users.SetFeatureFlag("no-such-user", FeatureFlagKeys.WorkspaceDestructive, true).Should().BeFalse();
    }

    // --- Несуществующий ключ ---

    [Fact]
    public void CatalogExists_UnknownKey_ReturnsFalse()
    {
        // Контроллер отвергает PUT по несуществующему ключу именно через Exists
        FeatureFlagCatalog.Exists("no-such-flag").Should().BeFalse();
    }

    [Fact]
    public void CatalogExists_AllCatalogKeys_ReturnTrue()
    {
        foreach (var def in FeatureFlagCatalog.All)
            FeatureFlagCatalog.Exists(def.Key).Should().BeTrue();
    }

    [Fact]
    public void GetEffective_IgnoresOverrideForKeyMissingFromCatalog()
    {
        // UserStore хранит любой ключ, но GetEffective отдаёт только ключи каталога
        _users.SetFeatureFlag(_userId, "no-such-flag", true);

        var effective = _sut.GetEffective(_userId);

        effective.Should().NotContainKey("no-such-flag");
        effective.Keys.Should().BeEquivalentTo(FeatureFlagCatalog.All.Select(f => f.Key));
    }

    // --- GetDefinitions ---

    [Fact]
    public void GetDefinitions_ReturnsCatalog()
    {
        // Без реестра модулей definitions = статический каталог (модульные флаги добавляются динамически)
        _sut.GetDefinitions().Should().BeEquivalentTo(FeatureFlagCatalog.All);
    }
}
