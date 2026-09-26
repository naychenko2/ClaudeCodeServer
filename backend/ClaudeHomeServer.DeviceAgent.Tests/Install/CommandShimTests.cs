using System.Diagnostics;
using System.Runtime.Versioning;
using ClaudeHomeServer.DeviceAgent.Install;
using ClaudeHomeServer.DeviceAgent.Tests.Supervision;

namespace ClaudeHomeServer.DeviceAgent.Tests.Install;

internal sealed class MemoryUserPath(string? value) : IUserPathStore
{
    public string? Value { get; private set; } = value;
    public int Writes { get; private set; }
    public string? Get() => Value;

    public void Set(string value)
    {
        Writes++;
        Value = value;
    }
}

/// <summary>
/// Команда <c>ai-home-agent</c> по имени: её копирует человек из подсказки «Папка проекта не
/// разрешена агенту», а бинарь лежит в <c>versions/{v}</c> и переезжает с каждым обновлением.
/// </summary>
public sealed class CommandShimTests : IDisposable
{
    private readonly TempInstall _install = new("1.0.0");
    private readonly string _bin = Directory.CreateTempSubdirectory("agent-bin-").FullName;

    public void Dispose()
    {
        _install.Dispose();
        try { Directory.Delete(_bin, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(null, @"C:\A\bin")]
    [InlineData("", @"C:\A\bin")]
    [InlineData(@"%USERPROFILE%\go\bin", @"%USERPROFILE%\go\bin;C:\A\bin")]
    [InlineData(@"C:\x;", @"C:\x;C:\A\bin")]
    [InlineData(@"C:\x;c:\a\BIN\;C:\y", @"C:\x;c:\a\BIN\;C:\y")]
    [InlineData(@"""C:\A\bin"";C:\y", @"""C:\A\bin"";C:\y")]
    public void Каталог_дописывается_в_PATH_один_раз_не_трогая_чужое(string? before, string after) =>
        UserPathList.With(before, @"C:\A\bin").Should().Be(after);

    [Theory]
    [InlineData(@"C:\x;C:\A\bin;C:\y", @"C:\x;C:\y")]
    [InlineData(@"C:\A\bin\", "")]
    [InlineData(@"%USERPROFILE%\go\bin;;C:\y", @"%USERPROFILE%\go\bin;;C:\y")]
    public void Снятие_убирает_только_свой_каталог(string before, string after) =>
        UserPathList.Without(before, @"C:\A\bin").Should().Be(after);

    [Fact]
    public void Windows_шим_смотрит_в_active_и_прописывает_bin_в_PATH_пользователя()
    {
        var path = new MemoryUserPath(@"C:\Windows");
        var shim = new WindowsCmdShim(_install.Layout, path);

        var notes = shim.Install();

        shim.Location.Should().Be(Path.Combine(_install.Root, "bin", "ai-home-agent.cmd"));
        File.ReadAllText(shim.Location).Should().Be(WindowsCmdShim.Script);
        WindowsCmdShim.Script.Should().Contain(@"<""%~dp0..\active""")
            .And.Contain(@"""%~dp0..\versions\%AIHOME_VERSION%\ai-home-agent.exe"" %*",
                "путь версии меняется с каждым обновлением — шим читает указатель при каждом запуске");
        WindowsCmdShim.Script.All(char.IsAscii).Should().BeTrue("cmd читает файл в OEM-кодировке");
        path.Value.Should().Be($@"C:\Windows;{shim.BinDirectory}");
        notes.Should().ContainSingle().Which.Should().Contain("новых окнах терминала");

        shim.Install();
        path.Writes.Should().Be(1, "повторная установка PATH не трогает");

        shim.Remove();
        File.Exists(shim.Location).Should().BeFalse();
        Directory.Exists(shim.BinDirectory).Should().BeFalse();
        path.Value.Should().Be(@"C:\Windows");
    }

    [SkippableFact]
    public async Task Windows_шим_запускает_активную_версию_с_аргументами()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "cmd.exe — только Windows");
        var shim = new WindowsCmdShim(_install.Layout, new MemoryUserPath(null));
        shim.Install();
        // Вместо агента — копия where.exe: настоящий exe, по выводу которого видно, что аргументы дошли
        File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"), _install.Layout.ExeOf("1.0.0"), overwrite: true);
        _install.Layout.SetActive("1.0.0");

        var (code, output) = await RunAsync("cmd.exe", "/c", shim.Location, "cmd.exe");

        code.Should().Be(0, output);
        output.Should().Contain("cmd.exe");
    }

    [SkippableFact]
    [UnsupportedOSPlatform("windows")]
    public async Task Linux_симлинк_на_current_переживает_переключение_версии()
    {
        Skip.If(OperatingSystem.IsWindows(), "симлинк current — только Linux");
        WriteFakeAgent("1.0.0");
        _install.AddVersion("2.0.0");
        WriteFakeAgent("2.0.0");
        _install.Layout.SetActive("1.0.0");
        var shim = new UnixLinkShim(_install.Layout, _bin, $"/usr/bin:{_bin}/");

        shim.Install().Should().ContainSingle().Which.Should().Contain(shim.Target);
        (await RunAsync(shim.Location, "roots", "add", "/srv/my proj")).Output.Should().Be("1.0.0 roots add /srv/my proj");

        _install.Layout.SetActive("2.0.0");
        (await RunAsync(shim.Location, "roots", "list")).Output.Should().Be("2.0.0 roots list", "шим не переписывается при обновлении");

        shim.Remove();
        File.Exists(shim.Location).Should().BeFalse();
    }

    [Fact]
    public void Linux_без_каталога_в_PATH_шим_ставится_с_подсказкой()
    {
        var shim = new UnixLinkShim(_install.Layout, Path.Combine(_bin, "nested"), "/usr/bin");

        var notes = shim.Install();

        new FileInfo(shim.Location).LinkTarget.Should().Be(shim.Target);
        notes.Should().HaveCount(2).And.Contain(n => n.Contains("нет в PATH"));
    }

    [Fact]
    public void Linux_чужой_файл_на_месте_шима_не_трогается()
    {
        var shim = new UnixLinkShim(_install.Layout, _bin, _bin);
        File.WriteAllText(shim.Location, "чужой");

        shim.Install().Should().ContainSingle().Which.Should().Contain("чужой файл");
        shim.Remove();

        File.ReadAllText(shim.Location).Should().Be("чужой");
    }

    [Fact]
    public void Linux_устаревший_симлинк_переставляется_на_current()
    {
        var shim = new UnixLinkShim(_install.Layout, _bin, _bin);
        File.CreateSymbolicLink(shim.Location, "/old/root/current/ai-home-agent");

        shim.Install();

        new FileInfo(shim.Location).LinkTarget.Should().Be(shim.Target);
    }

    [UnsupportedOSPlatform("windows")]
    private void WriteFakeAgent(string version)
    {
        var exe = _install.Layout.ExeOf(version);
        File.WriteAllText(exe, $"#!/bin/sh\necho \"{version} $*\"\n");
        File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task<(int Code, string Output)> RunAsync(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return (process.ExitCode, ((await stdout) + (await stderr)).Trim());
    }
}
