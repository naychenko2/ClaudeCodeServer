using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.DeviceAgent.Tests.Protocol;

/// <summary>Разбор и порядок версий агента по agent-distribution Р1.</summary>
public sealed class DeviceAgentVersionTests
{
    [Theory]
    [InlineData("1.4321.0", 1, 4321, 0, null, null)]
    [InlineData("1.4321.0+0a1b2c3d", 1, 4321, 0, null, "0a1b2c3d")]
    [InlineData("1.4321.0-dirty.20260926143015", 1, 4321, 0, "20260926143015", null)]
    [InlineData("1.4321.0-dirty.20260926143015+0a1b2c3d", 1, 4321, 0, "20260926143015", "0a1b2c3d")]
    [InlineData("0.0.0", 0, 0, 0, null, null)]
    public void Разбирается_версия_выкатки(string text, int major, int minor, int patch, string? dirty, string? build)
    {
        DeviceAgentVersion.TryParse(text, out var v).Should().BeTrue();
        v!.Major.Should().Be(major);
        v.Minor.Should().Be(minor);
        v.Patch.Should().Be(patch);
        v.DirtyStamp.Should().Be(dirty);
        v.Build.Should().Be(build);
        v.IsDirty.Should().Be(dirty is not null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("v1.2.3")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-beta.1")]
    [InlineData("1.2.3-dirty")]
    [InlineData("1.2.3-dirty.2026")]
    [InlineData("1.2.3-dirty.202609261430150")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3+a/b")]
    [InlineData("1.2.3/../x")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2.3\n")]
    [InlineData("1.9999999999.0")]
    public void Чужое_не_разбирается(string? text)
    {
        DeviceAgentVersion.TryParse(text, out var v).Should().BeFalse();
        v.Should().BeNull();
    }

    [Fact]
    public void Parse_на_мусоре_бросает_FormatException() =>
        FluentActions.Invoking(() => DeviceAgentVersion.Parse("мусор")).Should().Throw<FormatException>();

    [Theory]
    [InlineData("1.10.0", "1.9.0")]
    [InlineData("2.0.0", "1.9999.0")]
    [InlineData("1.9.1", "1.9.0")]
    [InlineData("1.9.0", "1.9.0-dirty.20260926143015")]
    [InlineData("1.9.0-dirty.20260926143016", "1.9.0-dirty.20260926143015")]
    [InlineData("1.9.0-dirty.20270101000000", "1.9.0-dirty.20261231235959")]
    [InlineData("1.10.0-dirty.20200101000000", "1.9.0")]
    public void Старшая_больше_младшей(string newer, string older)
    {
        var a = DeviceAgentVersion.Parse(newer);
        var b = DeviceAgentVersion.Parse(older);
        a.CompareTo(b).Should().BePositive();
        b.CompareTo(a).Should().BeNegative();
        (a > b).Should().BeTrue();
        (b < a).Should().BeTrue();
        (a >= b).Should().BeTrue();
        (b <= a).Should().BeTrue();
        (a == b).Should().BeFalse();
    }

    [Fact]
    public void Метаданные_сборки_не_влияют_на_равенство_и_строку()
    {
        var a = DeviceAgentVersion.Parse("1.9.0+0a1b2c3d");
        var b = DeviceAgentVersion.Parse("1.9.0+ffffffff");
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
        a.CompareTo(b).Should().Be(0);
        a.ToString().Should().Be("1.9.0");
        DeviceAgentVersion.Parse("1.9.0-dirty.20260926143015+0a1b2c3d").ToString()
            .Should().Be("1.9.0-dirty.20260926143015");
    }

    [Fact]
    public void Грязные_сборки_с_разным_штампом_не_равны() =>
        (DeviceAgentVersion.Parse("1.9.0-dirty.20260926143015") == DeviceAgentVersion.Parse("1.9.0-dirty.20260926143016"))
            .Should().BeFalse();

    [Fact]
    public void Минимальная_версия_разбирается_и_пропускает_агентов_до_версий_выкатки()
    {
        DeviceAgentCompatibility.Min.ToString().Should().Be(DeviceAgentCompatibility.MinVersion);
        // Сборка без -p:Version: SDK ставит 1.0.0, до AD-1 SDK ещё и приписывал ревизию
        (DeviceAgentVersion.Parse("1.0.0+0a1b2c3d") >= DeviceAgentCompatibility.Min).Should().BeTrue();
        (DeviceAgentVersion.Parse("0.0.0") >= DeviceAgentCompatibility.Min).Should().BeFalse();
    }
}
