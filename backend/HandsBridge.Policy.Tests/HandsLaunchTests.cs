using ClaudeHomeServer.HandsBridge.Policy;
using ClaudeHomeServer.Protocol;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary><c>app</c>: белый список машины, интерпретаторы, нормализация путей.</summary>
public class HandsLaunchTests
{
    [Theory]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData(@"c:\WINDOWS\Notepad.EXE")]
    [InlineData("C:/Windows/notepad.exe")]
    [InlineData(@"C:\Windows\System32\..\notepad.exe")]
    [InlineData(@"C:\Windows\.\\notepad.exe")]
    [InlineData(@"C:\Windows\notepad.exe. .")]
    public void Allowed_program_starts_by_its_normalized_path(string requested)
    {
        var decision = Machine.Policy().CheckLaunch(requested, null);

        Assert.True(decision.Allowed, decision.Reason);
        Assert.Equal(@"C:\Windows\notepad.exe", decision.ProgramPath);
    }

    [Theory]
    [InlineData("notepad.exe")]
    [InlineData("notepad")]
    [InlineData(@"Windows\notepad.exe")]
    [InlineData(@"C:notepad.exe")]
    [InlineData(@"\\server\share\notepad.exe")]
    [InlineData(@"\\?\C:\Windows\notepad.exe")]
    [InlineData(@"\\.\C:\Windows\notepad.exe")]
    [InlineData(@"C:\Windows\notepad.exe:stream")]
    [InlineData(@"C:\..\Windows\notepad.exe")]
    [InlineData("\"C:\\Windows\\notepad.exe\"")]
    [InlineData("C:\\Windows\\notepad.exe\n")]
    [InlineData("")]
    [InlineData(null)]
    public void Not_a_full_path_is_denied_without_PATH_search(string? requested)
    {
        var decision = Machine.Policy().CheckLaunch(requested, null);

        Assert.False(decision.Allowed);
        Assert.Null(decision.ProgramPath);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe")]
    [InlineData(@"C:\Windows\System32\wscript.exe")]
    [InlineData(@"C:\Windows\System32\cscript.exe")]
    [InlineData(@"C:\Windows\System32\mshta.exe")]
    [InlineData(@"C:\Windows\System32\rundll32.exe")]
    [InlineData(@"C:\Windows\System32\regsvr32.exe")]
    [InlineData(@"C:\Python312\python.exe")]
    [InlineData(@"C:\Python312\python3.12.exe")]
    [InlineData(@"C:\Python312\pythonw.exe")]
    [InlineData(@"C:\Program Files\nodejs\node.exe")]
    [InlineData(@"C:\Windows\explorer.exe")]
    [InlineData(@"C:\Windows\System32\CMD.EXE")]
    [InlineData(@"C:\Windows\System32\cmd.exe.")]
    [InlineData(@"C:\Windows\System32\cmd.com")]
    public void Interpreter_is_denied_even_when_the_user_listed_it(string interpreter)
    {
        var policy = Machine.Policy(Machine.Apps(interpreter));

        var decision = policy.CheckLaunch(interpreter, null);

        Assert.False(decision.Allowed);
        Assert.Contains("never started", decision.Reason);
    }

    [Fact]
    public void Dotdot_cannot_smuggle_an_interpreter_behind_an_allowed_folder()
    {
        var decision = Machine.Policy().CheckLaunch(@"C:\Program Files\Paint\..\..\Windows\System32\cmd.exe", null);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void Program_outside_the_list_is_denied_and_the_list_is_named()
    {
        var decision = Machine.Policy().CheckLaunch(@"C:\Program Files\Paint\helper.exe", null);

        Assert.False(decision.Allowed);
        Assert.Contains("not in the list", decision.Reason);
        Assert.Contains(Machine.Notepad, decision.Reason);
    }

    [Fact]
    public void Script_in_the_list_is_denied_because_CreateProcess_hands_it_to_cmd()
    {
        const string script = @"C:\Tools\build.bat";

        var decision = Machine.Policy(Machine.Apps(script)).CheckLaunch(script, null);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void Missing_or_unknown_version_list_allows_nothing()
    {
        var unknownVersion = new HandsAppsFile(HandsAppsFile.CurrentVersion + 1, [new HandsAppEntry(Machine.Notepad, DateTimeOffset.UnixEpoch)]);

        Assert.False(new HandsPolicy(() => null, Machine.Windows()).CheckLaunch(Machine.Notepad, null).Allowed);
        Assert.False(new HandsPolicy(() => unknownVersion, Machine.Windows()).CheckLaunch(Machine.Notepad, null).Allowed);
        Assert.False(new HandsPolicy(() => throw new IOException("locked"), Machine.Windows()).CheckLaunch(Machine.Notepad, null).Allowed);
    }

    [Theory]
    [InlineData("--renderer-cmd-prefix=\"cmd /c calc\"")]
    [InlineData("--utility-cmd-prefix=x")]
    [InlineData("--gpu-launcher=x")]
    [InlineData("--browser-subprocess-path=x")]
    [InlineData("--remote-debugging-port=9222")]
    [InlineData("--load-extension=C:\\x")]
    public void Launcher_arguments_are_denied(string arguments)
    {
        var decision = Machine.Policy().CheckLaunch(Machine.Notepad, arguments);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void Ordinary_arguments_pass()
    {
        Assert.True(Machine.Policy().CheckLaunch(Machine.Notepad, @"C:\docs\readme.txt").Allowed);
    }
}
