using ClaudeHomeServer.Models;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Plugins;

// Право net:{host} (rework C3, minor): грамматика точного хоста [a-z0-9.-]+ и порт 1..65535.
// До фикса IsKnown/Parse принимали ЛЮБОЙ непустой параметр net:* («принимает что угодно»).
// Критерий: net:bad/host → invalid (слэш вне класса хоста).
public class PluginPermissionTests
{
    // --- IsHost: грамматика точного хоста ---

    [Theory]
    [InlineData("api.linear.app")]
    [InlineData("localhost")]
    [InlineData("my-host.example")]
    [InlineData("10.0.0.1")]
    [InlineData("api.linear.app:443")]
    [InlineData("localhost:8080")]
    public void IsHost_КорректныйХост_Истина(string host) =>
        PluginPermission.IsHost(host).Should().BeTrue($"хост {host} должен быть валиден");

    [Theory]
    [InlineData("bad/host")]      // слэш вне класса
    [InlineData("API.Linear.App")] // заглавные — вне [a-z0-9.-]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("host:0")]        // порт 0 вне 1..65535
    [InlineData("host:99999")]   // порт > 65535
    [InlineData("host:abc")]     // порт не число
    [InlineData("a:b:c")]        // два двоеточия
    public void IsHost_НекорректныйХост_Ложь(string? host) =>
        PluginPermission.IsHost(host).Should().BeFalse($"хост {host} должен быть невалиден");

    // --- Parse: net:bad/host → invalid (критерий) ---

    [Fact]
    public void Parse_NetBadSlashHost_Invalid()
    {
        var (isValid, _, reason) = PluginPermission.Parse("net:bad/host");

        isValid.Should().BeFalse();
        reason.Should().Contain("bad/host");
    }

    [Theory]
    [InlineData("notes:read")]
    [InlineData("tasks:read")]
    [InlineData("net:api.linear.app")]
    [InlineData("net:localhost:8080")]
    [InlineData("secret:LINEAR_TOKEN")]
    public void Parse_ИзвестноеПраво_Valid(string permission) =>
        PluginPermission.Parse(permission).IsValid.Should().BeTrue($"право {permission} должно быть валидно");

    [Theory]
    [InlineData("")]
    [InlineData("foo:bar")]
    [InlineData("net:")]
    [InlineData("secret:")]
    public void Parse_НеизвестноеИлиПустое_Invalid(string permission) =>
        PluginPermission.Parse(permission).IsValid.Should().BeFalse($"право {permission} должно быть невалидно");

    // --- IsKnown: согласован с IsHost (net:bad/host теперь неизвестно) ---

    [Fact]
    public void IsKnown_NetBadSlashHost_Ложь() =>
        PluginPermission.IsKnown("net:bad/host").Should().BeFalse();

    [Fact]
    public void IsKnown_NetValidHost_Истина() =>
        PluginPermission.IsKnown("net:api.linear.app").Should().BeTrue();
}
