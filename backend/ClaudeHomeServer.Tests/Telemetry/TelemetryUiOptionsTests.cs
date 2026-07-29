using ClaudeHomeServer.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Telemetry;

/// <summary>
/// Разбор секции <c>Telemetry:Ui</c> — проброса SigNoz UI в раздел «Телеметрия».
/// Дефолты значимы: отсутствие секции = раздел выключен + локальный адрес SigNoz,
/// а пустой <c>InternalUrl</c> не должен превратиться в форвард в никуда.
/// </summary>
public class TelemetryUiOptionsTests
{
    private static TelemetryUiOptions Parse(Dictionary<string, string?> values) =>
        TelemetryUiOptions.FromConfig(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public void NotConfigured_Disabled_WithLocalDefault()
    {
        var o = Parse(new());
        o.Enabled.Should().BeFalse();
        o.InternalUrl.Should().Be("http://127.0.0.1:3301");
    }

    [Fact]
    public void Enabled_WithCustomUrl_IsParsed()
    {
        var o = Parse(new()
        {
            ["Telemetry:Ui:Enabled"] = "true",
            ["Telemetry:Ui:InternalUrl"] = "http://localhost:3301",
        });
        o.Enabled.Should().BeTrue();
        o.InternalUrl.Should().Be("http://localhost:3301");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyInternalUrl_FallsBackToDefault(string url)
    {
        // Пустой адрес в конфиге = дефолт, а не форвард в никуда
        var o = Parse(new()
        {
            ["Telemetry:Ui:Enabled"] = "true",
            ["Telemetry:Ui:InternalUrl"] = url,
        });
        o.InternalUrl.Should().Be("http://127.0.0.1:3301");
    }
}
