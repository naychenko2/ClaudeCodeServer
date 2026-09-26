using System.Diagnostics;
using System.Runtime.Versioning;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Скрипты установки агента (H1 ревью AD-10): адрес сервера не по https и не на петле —
/// отказ ДО любой загрузки. Иначе атакующий на http-пути подменит архив, и чужой бинарь
/// выполнится раньше, чем сервер откажет в сопряжении. Вместо curl — подставной, который
/// только записывает свои аргументы: так видно, дошло ли дело до загрузки и с каким --proto.
/// </summary>
public class AgentInstallScriptChannelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "agent-install-channel-" + Guid.NewGuid().ToString("N"));

    public AgentInstallScriptChannelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string CurlArgsFile => Path.Combine(_dir, "curl-args");

    [SkippableTheory]
    [InlineData("http://192.168.1.5")]
    [InlineData("http://home.example:5000/")]
    // Хост — то, что после @, а не «localhost» перед ней
    [InlineData("http://localhost@evil.example")]
    [InlineData("http://localhost.evil.example")]
    [InlineData("ftp://localhost")]
    [InlineData("localhost:5001")]
    [UnsupportedOSPlatform("windows")]
    public async Task Sh_ОткрытыйКаналИзСети_ОтказДоЗагрузки(string server)
    {
        Skip.If(OperatingSystem.IsWindows(), "install.sh — только Linux");
        var (code, stderr) = await RunShAsync(server);

        code.Should().Be(2, stderr);
        stderr.Should().Contain("https://");
        File.Exists(CurlArgsFile).Should().BeFalse("до загрузки дело дойти не должно");
    }

    [SkippableTheory]
    [InlineData("https://home.example", "=https")]
    [InlineData("HTTPS://home.example:8443/", "=https")]
    [InlineData("http://localhost:5001", "=http,https")]
    [InlineData("http://127.0.0.1:5000/", "=http,https")]
    [InlineData("http://[::1]:5000", "=http,https")]
    [UnsupportedOSPlatform("windows")]
    public async Task Sh_HttpsИлиПетля_ИдётКЗагрузкеСЗапретомПониженияПротокола(string server, string proto)
    {
        Skip.If(OperatingSystem.IsWindows(), "install.sh — только Linux");
        var (code, stderr) = await RunShAsync(server);

        // Подставной curl отвечает ошибкой — дальше честное «манифест недоступен»
        code.Should().Be(3, stderr);
        var args = await File.ReadAllTextAsync(CurlArgsFile);
        args.Should().Contain($"--proto {proto} ").And.Contain("--proto-redir =https ");
    }

    [SkippableTheory]
    [InlineData("http://192.168.1.5", 2)]
    [InlineData("http://localhost@evil.example", 2)]
    // Дальше проверки схемы: загрузка с закрытого порта падает сетевым отказом
    [InlineData("https://127.0.0.1:9", 3)]
    [InlineData("http://localhost:9", 3)]
    public async Task Ps1_ОткрытыйКаналИзСети_ОтказДоЗагрузки(string server, int expected)
    {
        var pwsh = FindOnPath("pwsh");
        Skip.If(pwsh is null, "pwsh не установлен");

        var (code, output) = await RunAsync(pwsh!, ["-NoProfile", "-NonInteractive", "-File", Script("install.ps1"),
            "-Server", server, "-Code", "X"], path: null);

        code.Should().Be(expected, output);
    }

    [UnsupportedOSPlatform("windows")]
    private async Task<(int Code, string Stderr)> RunShAsync(string server)
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "install.sh — только Linux");
        var sh = FindOnPath("sh");
        Skip.If(sh is null, "sh не найден");
        foreach (var tool in new[] { "jq", "sha256sum", "tar" })
            Skip.If(FindOnPath(tool) is null, $"{tool} не найден");

        var fakeBin = Path.Combine(_dir, "bin");
        Directory.CreateDirectory(fakeBin);
        var curl = Path.Combine(fakeBin, "curl");
        await File.WriteAllTextAsync(curl, $"#!/bin/sh\necho \"$@ \" > '{CurlArgsFile}'\nexit 22\n");
        File.SetUnixFileMode(curl, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return await RunAsync(sh!, [Script("install.sh"), "--server", server, "--code", "X"],
            path: fakeBin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
    }

    private async Task<(int Code, string Output)> RunAsync(string exe, string[] args, string? path)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (path is not null) psi.Environment["PATH"] = path;
        // Каталог данных — во временном: даже дошедший до конца скрипт не тронет домашний
        psi.Environment["XDG_DATA_HOME"] = Path.Combine(_dir, "data");

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stderr + await stdout);
    }

    private static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(dir => Path.Combine(dir, name))
        .FirstOrDefault(File.Exists);

    private static string Script(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "deploy", "agent-install")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Корень репозитория не найден"),
            "deploy", "agent-install", name);
    }
}
