using ClaudeHomeServer.HandsBridge.Policy;
using ClaudeHomeServer.Protocol;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary><c>app</c>: любая программа по полному пути, кроме интерпретаторов и терминалов (решение 2б).</summary>
public class HandsLaunchTests
{
    [Theory]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData(@"c:\WINDOWS\Notepad.EXE")]
    [InlineData("C:/Windows/notepad.exe")]
    [InlineData(@"C:\Windows\System32\..\notepad.exe")]
    [InlineData(@"C:\Windows\.\\notepad.exe")]
    [InlineData(@"C:\Windows\notepad.exe. .")]
    public void Program_starts_by_its_normalized_path(string requested)
    {
        var decision = Machine.Policy().CheckLaunch(requested, null);

        Assert.True(decision.Allowed, decision.Reason);
        // Регистр Windows не различает; запускается ровно проверенная строка
        Assert.Equal(@"C:\Windows\notepad.exe", decision.ProgramPath, ignoreCase: true);
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
    [InlineData(@"C:\Program Files\Git\bin\bash.exe")]
    [InlineData(@"C:\Program Files\Git\git-bash.exe")]
    [InlineData(@"C:\Windows\System32\wsl.exe")]
    [InlineData(@"C:\Windows\System32\conhost.exe")]
    [InlineData(@"C:\Users\an\AppData\Local\Microsoft\WindowsApps\wt.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal\WindowsTerminal.exe")]
    [InlineData(@"C:\Strawberry\perl\bin\perl.exe")]
    public void Interpreter_or_terminal_is_never_started(string interpreter)
    {
        var decision = Machine.Policy().CheckLaunch(interpreter, null);

        Assert.False(decision.Allowed);
        Assert.Null(decision.ProgramPath);
        Assert.Contains("never started", decision.Reason);
    }

    [Fact]
    public void Dotdot_cannot_smuggle_an_interpreter_behind_an_allowed_folder()
    {
        var decision = Machine.Policy().CheckLaunch(@"C:\Program Files\Paint\..\..\Windows\System32\cmd.exe", null);

        Assert.False(decision.Allowed);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Paint\helper.exe")]
    [InlineData(@"D:\Tools\Some App\viewer.exe")]
    [InlineData(@"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE")]
    public void Any_program_by_full_path_is_allowed_there_is_no_allow_list(string program)
    {
        var decision = Machine.Policy().CheckLaunch(program, null);

        Assert.True(decision.Allowed, decision.Reason);
        Assert.Equal(program, decision.ProgramPath);
    }

    [Theory]
    [InlineData(@"C:\Tools\build.bat")]
    [InlineData(@"C:\Tools\build.cmd")]
    [InlineData(@"C:\Tools\run.ps1")]
    [InlineData(@"C:\Tools\link.lnk")]
    public void Non_exe_is_denied_because_CreateProcess_hands_scripts_to_cmd(string script)
    {
        Assert.False(Machine.Policy().CheckLaunch(script, null).Allowed);
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
