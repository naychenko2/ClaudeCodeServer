using ClaudeHomeServer.DeviceAgent.Cli;

namespace ClaudeHomeServer.DeviceAgent.Tests.Cli;

/// <summary>
/// Authenticode — ветка Windows, гоняет Windows-CI (2.5). Настоящего claude.exe без сети не
/// достать, поэтому положительный путь проверяется на подписанном Microsoft хосте тестов:
/// WinVerifyTrust и сверка подписанта — те же самые, меняется только ожидаемое имя.
/// </summary>
public sealed class AuthenticodeCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "authenticode-" + Guid.NewGuid().ToString("N"));

    public AuthenticodeCheckTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    [SkippableFact]
    public void Неподписанный_exe_отвергается()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Authenticode — только Windows");
        var path = Path.Combine(_dir, "claude.exe");
        File.WriteAllBytes(path, FakeCliDistribution.Payload("2.1.281"));

        FluentActions.Invoking(() => Check(ExecutableSignatureCheck.AnthropicWindowsSigner).Verify(path))
            .Should().Throw<CliIntegrityException>().WithMessage("*Authenticode*");
    }

    [SkippableFact]
    public void Подписанный_не_Anthropic_отвергается()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Authenticode — только Windows");

        FluentActions.Invoking(() => Check(ExecutableSignatureCheck.AnthropicWindowsSigner).Verify(SignedHost()))
            .Should().Throw<CliIntegrityException>().WithMessage("*подписан «Microsoft Corporation»*");
    }

    [SkippableFact]
    public void Подпись_ожидаемого_подписанта_проходит()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Authenticode — только Windows");

        FluentActions.Invoking(() => Check("Microsoft Corporation").Verify(SignedHost())).Should().NotThrow();
    }

    [SkippableFact]
    public void Испорченный_подписанный_exe_отвергается()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Authenticode — только Windows");
        var bytes = File.ReadAllBytes(SignedHost());
        bytes[bytes.Length / 2] ^= 0xFF;
        var path = Path.Combine(_dir, "claude.exe");
        File.WriteAllBytes(path, bytes);

        FluentActions.Invoking(() => Check("Microsoft Corporation").Verify(path))
            .Should().Throw<CliIntegrityException>().WithMessage("*Authenticode*");
    }

    [SkippableFact]
    public void На_Windows_боевая_проверка_это_Authenticode_Anthropic()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Authenticode — только Windows");

        ExecutableSignatureCheck.ForCurrentOs().GetType().Name.Should().Be(nameof(AuthenticodeCheck));
    }

    [SkippableFact]
    public void Вне_Windows_платформенной_подписи_нет()
    {
        Skip.If(OperatingSystem.IsWindows(), "ветка не-Windows");

        ExecutableSignatureCheck.ForCurrentOs().Should().BeSameAs(ExecutableSignatureCheck.None);
    }

    private static IExecutableSignatureCheck Check(string signer)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return new AuthenticodeCheck(signer);
    }

    // dotnet.exe / testhost.exe — собственная (не каталожная) подпись Microsoft.
    private static string SignedHost() =>
        Environment.ProcessPath ?? throw new InvalidOperationException("нет пути процесса");
}
