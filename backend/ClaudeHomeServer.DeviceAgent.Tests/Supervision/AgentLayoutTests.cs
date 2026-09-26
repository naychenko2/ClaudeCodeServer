using ClaudeHomeServer.DeviceAgent.Supervision;

namespace ClaudeHomeServer.DeviceAgent.Tests.Supervision;

/// <summary>Раскладка версий: указатели, маркеры, симлинк current, мусор в указателе.</summary>
public sealed class AgentLayoutTests : IDisposable
{
    private readonly TempInstall _install = new("1.0.0", "2.0.0");

    public void Dispose() => _install.Dispose();

    private AgentLayout Layout => _install.Layout;

    [Fact]
    public void SetActive_уводит_прошлую_в_previous()
    {
        Layout.SetActive("1.0.0");
        Layout.SetActive("2.0.0");
        Layout.SetActive("2.0.0");

        Layout.ReadActive().Should().Be("2.0.0");
        Layout.ReadPrevious().Should().Be("1.0.0", "повторная установка той же версии previous не затирает");
    }

    [SkippableFact]
    public void На_Linux_current_смотрит_на_активную_версию()
    {
        Skip.If(OperatingSystem.IsWindows(), "симлинк current — для unit systemd --user");
        Layout.SetActive("1.0.0");
        Layout.SetActive("2.0.0");
        new DirectoryInfo(Layout.CurrentLink).LinkTarget.Should().Be(Path.Combine("versions", "2.0.0"));

        Layout.RollbackTo("1.0.0");
        new DirectoryInfo(Layout.CurrentLink).LinkTarget.Should().Be(Path.Combine("versions", "1.0.0"));
        File.Exists(Path.Combine(Layout.CurrentLink, "ai-home-agent")).Should().BeTrue();
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    [InlineData(".hidden")]
    public void Мусор_в_указателе_равен_отсутствию_версии(string pointer)
    {
        File.WriteAllText(Layout.ActiveFile, pointer);
        Layout.ReadActive().Should().BeNull();
        var act = () => Layout.VersionDir(pointer);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Плохая_версия_помнится_сутки()
    {
        var now = DateTimeOffset.UtcNow;
        Layout.MarkBad("2.0.0", now);

        Layout.IsBad("2.0.0", now.AddHours(23)).Should().BeTrue();
        Layout.IsBad("2.0.0", now.AddHours(25)).Should().BeFalse();
        Layout.IsBad("1.0.0", now).Should().BeFalse();
    }
}
