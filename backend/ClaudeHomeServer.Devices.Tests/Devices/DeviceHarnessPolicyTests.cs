using ClaudeHomeServer.Services.Devices;
using ClaudeHomeServer.Services.Execution;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services.Devices;

// Устройства идут за хостом: требуемая версия CLI — версия CLI сервера, конфиг — только
// аварийный пин поверх неё.
public class DeviceHarnessPolicyTests
{
    private sealed class FakeHostCli(string? version) : IHostCliVersion
    {
        public string? Current => version;
    }

    private static IConfiguration Config(string? pin) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [DeviceHarnessPolicy.CliVersionKey] = pin })
        .Build();

    [Fact]
    public void БезПина_ТребуетсяВерсияХоста()
    {
        var policy = new DeviceHarnessPolicy(Config(""), new FakeHostCli("2.1.283 (Claude Code)"));

        policy.RequiredCliVersion.Should().Be("2.1.283");
        policy.Evaluate("2.1.283").Ready.Should().BeTrue();
        policy.Evaluate("2.1.281").Ready.Should().BeFalse();
    }

    [Fact]
    public void Пин_ПеребиваетВерсиюХоста()
    {
        new DeviceHarnessPolicy(Config("2.1.280"), new FakeHostCli("2.1.283"))
            .RequiredCliVersion.Should().Be("2.1.280");
    }

    [Fact]
    public void НиПина_НиВерсииХоста_ХарнесНеГотов()
    {
        var policy = new DeviceHarnessPolicy(Config(null), new FakeHostCli(null));

        policy.RequiredCliVersion.Should().BeNull();
        policy.Evaluate("2.1.283").Problem.Should().Contain(DeviceHarnessPolicy.CliVersionKey);
    }

    [Theory]
    [InlineData("2.1.283 (Claude Code)", "2.1.283")]
    [InlineData("claude 2.1.283\n", "2.1.283")]
    [InlineData("нет версии", null)]
    [InlineData(null, null)]
    public void ВерсияCliХоста_РазборВывода(string? output, string? expected)
    {
        ClaudeCliVersion.Parse(output).Should().Be(expected);
    }
}
