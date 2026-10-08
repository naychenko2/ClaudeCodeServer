using ClaudeHomeServer.Services.Execution;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// CLI с хоста в песочнице: mount каталога версий, его учёт в ConfigHash (иначе контейнер
// не пересоздастся) и резолв команды запуска по цели симлинка хоста.
public class SandboxHostCliTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "sbx_hostcli_" + Guid.NewGuid().ToString("N"));

    public SandboxHostCliTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* уже нет */ }
    }

    private string MakeVersionsDir(params string[] versions)
    {
        var dir = Path.Combine(_tmp, ".local", "share", "claude", "versions");
        Directory.CreateDirectory(dir);
        foreach (var v in versions) File.WriteAllText(Path.Combine(dir, v), "bin");
        return dir;
    }

    private static SandboxOptions Opts(bool useHostCli = true) =>
        new() { ProjectsRoot = "/p", UseHostCli = useHostCli };

    [Fact]
    public void ВерсииЕсть_Linux_Включено_ВозвращаетКаталог()
    {
        var dir = MakeVersionsDir("2.1.287");
        SandboxManager.HostCliVersionsDir(Opts(), _tmp, isLinux: true).Should().Be(dir);
    }

    [Fact]
    public void НеLinux_ВыключеноОпцией_НетКаталога_ВозвращаетNull()
    {
        MakeVersionsDir("2.1.287");
        SandboxManager.HostCliVersionsDir(Opts(), _tmp, isLinux: false).Should().BeNull();
        SandboxManager.HostCliVersionsDir(Opts(useHostCli: false), _tmp, isLinux: true).Should().BeNull();
        SandboxManager.HostCliVersionsDir(Opts(), Path.Combine(_tmp, "нет"), isLinux: true).Should().BeNull();
    }

    [Fact]
    public void BuildRunArgs_СКаталогом_ДобавляетReadOnlyMount()
    {
        var args = SandboxManager.BuildRunArgsForHost(Path.Combine(_tmp, "sp"), Opts(), "/prof", "/tmp1", "h", "/host/versions");
        string.Join(' ', args).Should().Contain($"-v /host/versions:{SandboxManager.HostCliMount}:ro");
    }

    [Fact]
    public void BuildRunArgs_БезКаталога_MountаНет()
    {
        var args = SandboxManager.BuildRunArgsForHost(Path.Combine(_tmp, "sp"), Opts(), "/prof", "/tmp1", "h");
        string.Join(' ', args).Should().NotContain(SandboxManager.HostCliMount);
    }

    [Fact]
    public void ConfigHash_МеняетсяОтMount()
    {
        var sp = Path.Combine(_tmp, "sp");
        var without = SandboxManager.ConfigHashForHost(sp, Opts(), "/prof", "/tmp1", "img");
        var with = SandboxManager.ConfigHashForHost(sp, Opts(), "/prof", "/tmp1", "img", "/host/versions");
        with.Should().NotBe(without);
    }

    [Fact]
    public void Резолв_ВерсияЕстьВКаталоге_ВозвращаетПутьВКонтейнере()
    {
        var dir = MakeVersionsDir("2.1.287");
        DockerProcessRunner.ResolveClaudeCommand(dir, "/home/u/.local/share/claude/versions/2.1.287")
            .Should().Be($"{SandboxManager.HostCliMount}/2.1.287");
    }

    [Fact]
    public void Резолв_НетФайлаНетMountНетЦели_ВозвращаетClaude()
    {
        var dir = MakeVersionsDir("2.1.287");
        DockerProcessRunner.ResolveClaudeCommand(dir, "/x/versions/9.9.9").Should().Be("claude");
        DockerProcessRunner.ResolveClaudeCommand(null, "/x/versions/2.1.287").Should().Be("claude");
        DockerProcessRunner.ResolveClaudeCommand(dir, null).Should().Be("claude");
        DockerProcessRunner.ResolveClaudeCommand(dir, "/x/versions/..").Should().Be("claude");
    }
}
