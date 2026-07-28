using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

public class UserModelTierResolverTests : IDisposable
{
    private readonly string _tempDir;

    public UserModelTierResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc_user_tiers_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private (UserModelTierResolver Resolver, UserStore Users, AppSettingsService Settings) Build()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        }).Build();
        var settings = new AppSettingsService(config);
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        return (new UserModelTierResolver(users, settings), users, settings);
    }

    [Fact]
    public void OverrideUser_WinsOverGlobal()
    {
        var (resolver, users, settings) = Build();
        settings.Save(new AppSettings { ModelTierStrong = "global-opus" });
        var user = users.Add("u1", "password123", "user");
        users.SetModelTiers(user.Id, strong: "user-sonnet", null, null);

        resolver.ModelFor(ModelTier.Strong, user.Id).Should().Be("user-sonnet");
    }

    [Fact]
    public void EmptyUserOverride_FallsBackToGlobal()
    {
        var (resolver, users, settings) = Build();
        settings.Save(new AppSettings { ModelTierMedium = "global-sonnet" });
        var user = users.Add("u1", "password123", "user");
        users.SetModelTiers(user.Id, null, "user-haiku", null);
        users.SetModelTiers(user.Id, null, "", null);

        resolver.ModelFor(ModelTier.Medium, user.Id).Should().Be("global-sonnet");
    }

    [Fact]
    public void UnknownOwnerId_FallsBackToGlobal()
    {
        var (resolver, _, settings) = Build();
        settings.Save(new AppSettings { ModelTierWeak = "global-haiku" });

        resolver.ModelFor(ModelTier.Weak, "no-such-user").Should().Be("global-haiku");
    }

    [Fact]
    public void NullOwnerId_FallsBackToGlobal()
    {
        var (resolver, _, settings) = Build();
        settings.Save(new AppSettings { ModelTierStrong = "global-opus" });

        resolver.ModelFor(ModelTier.Strong, null).Should().Be("global-opus");
    }

    [Fact]
    public void UserWithoutOverrides_FallsBackToGlobal()
    {
        var (resolver, users, settings) = Build();
        settings.Save(new AppSettings { ModelTierMedium = "global-sonnet" });
        var user = users.Add("u1", "password123", "user");

        resolver.ModelFor(ModelTier.Medium, user.Id).Should().Be("global-sonnet");
    }

    [Theory]
    [InlineData("strong", ModelTier.Strong)]
    [InlineData("STRONG", ModelTier.Strong)]
    [InlineData(" medium ", ModelTier.Medium)]
    [InlineData("Weak", ModelTier.Weak)]
    public void TryParse_ИмяСлота_Принимается(string wire, ModelTier expected)
    {
        ModelTiers.TryParse(wire, out var tier).Should().BeTrue();
        tier.Should().Be(expected);
    }

    // Дыра белого списка: Enum.TryParse принимал индексы enum (в т.ч. со знаком, мимо
    // digit-guard) и списки флагов — мусор от LLM молча уезжал на самую дорогую модель
    [Theory]
    [InlineData("0")]
    [InlineData("+0")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("2")]
    [InlineData("strong,weak")]
    [InlineData("Strong, Medium")]
    [InlineData("strongest")]
    [InlineData("сильная")]
    [InlineData("tier:strong")]
    public void TryParse_НеИмяСлота_Отвергается(string wire)
    {
        ModelTiers.TryParse(wire, out _).Should().BeFalse();
        ModelTiers.IsValidWireValue(wire).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]     // поле не прислали
    [InlineData("")]       // сброс уровня
    [InlineData("   ")]
    public void IsValidWireValue_ОтсутствиеИСброс_Валидны(string? wire)
    {
        ModelTiers.IsValidWireValue(wire).Should().BeTrue();
        ModelTiers.TryParse(wire, out _).Should().BeFalse();
    }
}
