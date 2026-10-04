using ClaudeHomeServer.Services.Execution;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Сторож записи песочницы (инцидент 02.10, uid образа ≠ uid владельца данных): цели проверки
// берутся из тех же опций, что и mount'ы, сообщение несёт uid и готовую команду пересборки.
// Мутация: убрать профильный каталог из WriteProbeTargets или APP_UID из BuildRebuildHint → красные.
public class SandboxManagerWriteGuardTests
{
    [Fact]
    public void WriteProbeTargets_БерутсяИзОпцийИМонтирований()
    {
        var opts = new SandboxOptions { ProjectsRoot = Path.Combine(Path.GetTempPath(), "sbx-root") };
        var profiles = Path.Combine(Path.GetTempPath(), "sbx-prof");
        var tmp = Path.Combine(Path.GetTempPath(), "sbx-tmp");

        var targets = SandboxManager.WriteProbeTargets(opts, profiles, tmp);

        targets.Should().Equal(
            (SandboxManager.ProjectsMount, opts.ProjectsRoot),
            (SandboxManager.ProfilesMount, profiles),
            (SandboxManager.TmpMount, tmp));
    }

    [Fact]
    public void BuildWriteProbeArgs_ДелаетTouchИRmВКаталогеMountЧерезDockerExec()
    {
        var args = SandboxManager.BuildWriteProbeArgs("cc-sandbox", SandboxManager.TmpMount);

        args.Take(3).Should().Equal("exec", "cc-sandbox", "sh");
        args[^1].Should().Contain("/turn-tmp/.ccs-write-probe-").And.Contain("touch").And.Contain("rm -f");
    }

    [Fact]
    public void BuildWriteGuardMessage_СодержитUidИКомандуПересборкиСUidВладельца()
    {
        var msg = SandboxManager.BuildWriteGuardMessage(
            "cc-sandbox", "/projects", "/srv/projects", "1001", "1000", "Permission denied");

        msg.Should().Contain("1001").And.Contain("1000").And.Contain("/projects")
            .And.Contain("--build-arg APP_UID=1000").And.Contain("Permission denied");
    }

    [Fact]
    public void BuildRebuildHint_БезUid_ПодставляетIdU()
    {
        SandboxManager.BuildRebuildHint(null).Should().Contain("--build-arg APP_UID=$(id -u)");
    }
}
