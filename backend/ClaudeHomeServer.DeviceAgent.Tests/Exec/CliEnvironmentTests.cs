using ClaudeHomeServer.DeviceAgent.Cli;
using ClaudeHomeServer.DeviceAgent.Exec;

namespace ClaudeHomeServer.DeviceAgent.Tests.Exec;

/// <summary>
/// Allow-list окружения CLI зафиксирован здесь: расширение списка — осознанная правка теста.
/// Функция чистая, поэтому обе ветки (Unix и Windows) проверяются на любой ОС.
/// </summary>
public class CliEnvironmentTests
{
    private const string Sidecar = "http://127.0.0.1:41000";
    private const string SidecarTurn = Sidecar + "/t/abc";

    private static readonly Dictionary<string, string> Agent = new()
    {
        ["PATH"] = "/usr/bin",
        ["HOME"] = "/home/u",
        ["USERPROFILE"] = @"C:\Users\u",
        ["LANG"] = "ru_RU.UTF-8",
        ["SystemRoot"] = @"C:\Windows",
        ["ComSpec"] = @"C:\Windows\system32\cmd.exe",
        ["PATHEXT"] = ".COM;.EXE",
        ["TEMP"] = @"C:\t",
        ["TMP"] = @"C:\t",
        ["APPDATA"] = @"C:\a",
        ["LOCALAPPDATA"] = @"C:\l",
        // Ничто из этого не должно дойти до CLI
        ["ANTHROPIC_API_KEY"] = "sk-SECRET",
        ["ANTHROPIC_BASE_URL"] = "https://evil",
        ["CLAUDE_CODE_OAUTH_TOKEN"] = "oat-SECRET",
        ["HTTPS_PROXY"] = "http://corp:3128",
        ["NO_PROXY"] = "*",
        ["USERNAME"] = "u",
        ["ProgramFiles"] = @"C:\Program Files",
        ["SSH_AUTH_SOCK"] = "/tmp/ssh",
    };

    [Fact]
    public void Unix_ровно_фиксированный_набор()
    {
        var env = CliEnvironment.Build(false, Agent, "/data/profile", Sidecar, SidecarTurn, null);

        env.Keys.Should().BeEquivalentTo(
            "PATH", "HOME", "USERPROFILE", "LANG", "CLAUDE_CONFIG_DIR", "ANTHROPIC_BASE_URL",
            "ANTHROPIC_AUTH_TOKEN", "NO_PROXY", "HTTPS_PROXY", "DISABLE_AUTOUPDATER", "DISABLE_UPDATES");
        env["ANTHROPIC_BASE_URL"].Should().Be(SidecarTurn + "/llm");
        env["ANTHROPIC_AUTH_TOKEN"].Should().Be(CliEnvironment.AuthPlaceholder);
        env["HTTPS_PROXY"].Should().Be(Sidecar);
        env["NO_PROXY"].Should().Be("127.0.0.1,localhost");
        env["CLAUDE_CONFIG_DIR"].Should().Be("/data/profile");
        env["LANG"].Should().Be("ru_RU.UTF-8");
    }

    [Fact]
    public void Windows_добавляет_только_минимум_системных()
    {
        var env = CliEnvironment.Build(true, Agent, @"C:\data\profile", Sidecar, SidecarTurn, null);

        env.Keys.Should().BeEquivalentTo(
            "PATH", "HOME", "USERPROFILE", "LANG", "SystemRoot", "ComSpec", "PATHEXT", "TEMP", "TMP",
            "APPDATA", "LOCALAPPDATA", "CLAUDE_CONFIG_DIR", "ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN",
            "NO_PROXY", "HTTPS_PROXY", "DISABLE_AUTOUPDATER", "DISABLE_UPDATES");
        CliEnvironment.InheritedOnWindows.Should().Equal(
            "SystemRoot", "ComSpec", "PATHEXT", "TEMP", "TMP", "APPDATA", "LOCALAPPDATA");
    }

    [Fact]
    public void Windows_имена_без_учёта_регистра_как_у_ОС()
    {
        var agent = new Dictionary<string, string> { ["Path"] = @"C:\bin", ["SYSTEMROOT"] = @"C:\Windows" };
        var env = CliEnvironment.Build(true, agent, @"C:\p", Sidecar, SidecarTurn, null);
        env["PATH"].Should().Be(@"C:\bin");
        env["SystemRoot"].Should().Be(@"C:\Windows");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Секреты_агента_и_его_прокси_не_наследуются(bool windows)
    {
        var env = CliEnvironment.Build(windows, Agent, "/p", Sidecar, SidecarTurn, null);
        var values = string.Join("\n", env.Values);
        values.Should().NotContain("SECRET").And.NotContain("evil").And.NotContain("corp:3128");
        env.Should().NotContainKey("ANTHROPIC_API_KEY").And.NotContainKey("CLAUDE_CODE_OAUTH_TOKEN")
            .And.NotContainKey("SSH_AUTH_SOCK");
    }

    [Fact]
    public void От_сервера_принимаются_только_поведенческие_ключи_и_не_перебивают_агента()
    {
        var fromServer = new Dictionary<string, string>
        {
            ["CLAUDE_CODE_DISABLE_CLAUDE_MDS"] = "1",
            ["ANTHROPIC_BASE_URL"] = "https://server-evil",
            ["ANTHROPIC_AUTH_TOKEN"] = "server-token",
            ["CLAUDE_CODE_OAUTH_TOKEN"] = "server-oauth",
            ["DISABLE_UPDATES"] = "0",
            ["NODE_OPTIONS"] = "--require /tmp/x.js",
        };
        var env = CliEnvironment.Build(false, Agent, "/p", Sidecar, SidecarTurn, fromServer);

        env["CLAUDE_CODE_DISABLE_CLAUDE_MDS"].Should().Be("1");
        env["ANTHROPIC_BASE_URL"].Should().Be(SidecarTurn + "/llm");
        env["ANTHROPIC_AUTH_TOKEN"].Should().Be(CliEnvironment.AuthPlaceholder);
        env["DISABLE_UPDATES"].Should().Be("1");
        env.Should().NotContainKey("CLAUDE_CODE_OAUTH_TOKEN").And.NotContainKey("NODE_OPTIONS");
    }

    [Fact]
    public void Переменные_обновлений_берутся_из_ManagedCliEnvironment()
    {
        var env = CliEnvironment.Build(false, Agent, "/p", Sidecar, SidecarTurn, null);
        foreach (var (key, value) in ManagedCliEnvironment.Variables) env[key].Should().Be(value);
    }

    [Fact]
    public void Без_LANG_у_агента_ставится_UTF8()
    {
        var env = CliEnvironment.Build(false, new Dictionary<string, string> { ["PATH"] = "/bin" }, "/p", Sidecar, SidecarTurn, null);
        env["LANG"].Should().Be("C.UTF-8");
    }
}
