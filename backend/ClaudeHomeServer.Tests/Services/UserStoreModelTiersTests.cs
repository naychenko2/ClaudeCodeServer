using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

public class UserStoreModelTiersTests : IDisposable
{
    private readonly string _tempDir;

    public UserStoreModelTiersTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc_user_tiers_store_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private UserStore BuildStore()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        }).Build();
        return new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
    }

    [Fact]
    public void GetModelTiers_UnknownUser_ReturnsNulls()
    {
        var store = BuildStore();
        var tiers = store.GetModelTiers("no-such-user");
        tiers.Should().Be((null, null, null));
    }

    [Fact]
    public void SetModelTiers_PersistsAndSurvivesReload()
    {
        var store = BuildStore();
        var user = store.Add("u1", "password123", "user");

        store.SetModelTiers(user.Id, "opus", "sonnet", "haiku").Should().BeTrue();
        store.GetModelTiers(user.Id).Should().Be(("opus", "sonnet", "haiku"));

        var reloaded = BuildStore();
        reloaded.GetModelTiers(user.Id).Should().Be(("opus", "sonnet", "haiku"));
    }

    [Fact]
    public void SetModelTiers_Null_DoesNotTouchOthers()
    {
        var store = BuildStore();
        var user = store.Add("u1", "password123", "user");
        store.SetModelTiers(user.Id, "opus", "sonnet", "haiku");

        store.SetModelTiers(user.Id, null, "glm-5.2", null);
        store.GetModelTiers(user.Id).Should().Be(("opus", "glm-5.2", "haiku"));
    }

    [Fact]
    public void SetModelTiers_EmptyString_ClearsToInherit()
    {
        var store = BuildStore();
        var user = store.Add("u1", "password123", "user");
        store.SetModelTiers(user.Id, "opus", "sonnet", "haiku");

        store.SetModelTiers(user.Id, "", "", "");
        store.GetModelTiers(user.Id).Should().Be((null, null, null));
    }

    [Fact]
    public void SetModelTiers_TrimsWhitespace()
    {
        var store = BuildStore();
        var user = store.Add("u1", "password123", "user");
        store.SetModelTiers(user.Id, "  opus  ", " sonnet", "haiku ");
        store.GetModelTiers(user.Id).Should().Be(("opus", "sonnet", "haiku"));
    }

    [Fact]
    public void SetModelTiers_WhitespaceBecomesNull()
    {
        var store = BuildStore();
        var user = store.Add("u1", "password123", "user");
        store.SetModelTiers(user.Id, "   ", null, null);
        store.GetModelTiers(user.Id).Should().Be((null, null, null));
    }
}
