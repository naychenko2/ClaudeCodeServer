using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>Переключение фич-флага не зависит от сбоев подписчиков OnFeatureFlagChanged.</summary>
public class UserStoreFeatureFlagTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "userstore_ff_" + Guid.NewGuid().ToString("N"));

    public UserStoreFeatureFlagTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void SetFeatureFlag_ПодписчикБросает_ПереключениеПроходитИФлагСохранён()
    {
        var sut = new UserStore(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json")
            }).Build(),
            new Helpers.FakeHostEnvironment(),
            NullLogger<UserStore>.Instance);
        var user = sut.Add("alice", "password-1", "user");
        sut.OnFeatureFlagChanged += (_, _, _) => throw new InvalidOperationException("сбой подписчика");

        var act = () => sut.SetFeatureFlag(user.Id, "spheres", true);

        act.Should().NotThrow();
        sut.SetFeatureFlag(user.Id, "spheres", true).Should().BeTrue();
        user.FeatureFlags!["spheres"].Should().BeTrue();
    }
}
