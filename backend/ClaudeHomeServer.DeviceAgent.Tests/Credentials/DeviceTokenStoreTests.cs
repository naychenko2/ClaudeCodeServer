using System.Runtime.Versioning;
using ClaudeHomeServer.DeviceAgent.Credentials;

namespace ClaudeHomeServer.DeviceAgent.Tests.Credentials;

/// <summary>Хранение токена устройства по ОС. Ветки помечены условием ОС: каждый CI гоняет свою.</summary>
public class DeviceTokenStoreTests : IDisposable
{
    private const string Token = "device-token-SECRET-123";
    private readonly string _root = Directory.CreateTempSubdirectory("token-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [SkippableFact]
    [UnsupportedOSPlatform("windows")]
    public void Linux_файл_0600_в_каталоге_0700()
    {
        Skip.If(OperatingSystem.IsWindows());
        var dir = Path.Combine(_root, "ai-home-agent");
        var store = new FileTokenStore(dir);

        store.Save("dev-1", Token);

        File.GetUnixFileMode(dir).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.GetUnixFileMode(store.FilePath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        store.Read("dev-1").Should().Be(Token);
        store.Read("dev-2").Should().BeNull("токен привязан к устройству сопряжения");
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    [UnsupportedOSPlatform("windows")]
    public void Linux_права_шире_допустимого_агент_отказывается_и_говорит_как_починить(bool fileTooWide)
    {
        Skip.If(OperatingSystem.IsWindows());
        var dir = Path.Combine(_root, "ai-home-agent");
        var store = new FileTokenStore(dir);
        store.Save("dev-1", Token);

        if (fileTooWide)
            File.SetUnixFileMode(store.FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        else
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                      | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var act = () => store.Read("dev-1");
        var error = act.Should().Throw<TokenStoreException>().Which.Message;
        error.Should().Contain(fileTooWide ? "chmod 600" : "chmod 700").And.Contain("не стартует");
        error.Should().NotContain(Token);
    }

    [Fact]
    public void Secret_Service_токен_уходит_через_stdin_а_не_argv()
    {
        var tool = new FakeSecretTool();
        var store = new SecretServiceTokenStore(tool);

        store.Save("dev-1", Token);
        store.Read("dev-1").Should().Be(Token);

        tool.Calls.Should().OnlyContain(c => !c.Args.Any(a => a.Contains(Token)), "argv видит любой пользователь машины");
        tool.Calls.First().Stdin.Should().Be(Token);
    }

    [Fact]
    public void Secret_Service_без_демона_недоступен_и_агент_уходит_на_файл()
    {
        SecretServiceTokenStore.IsAvailable(new FakeSecretTool { Exists = false }).Should().BeFalse();
        SecretServiceTokenStore.IsAvailable(new FakeSecretTool { ProbeStderr = "Cannot autolaunch D-Bus without X11" })
            .Should().BeFalse();
        SecretServiceTokenStore.IsAvailable(new FakeSecretTool()).Should().BeTrue();
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void Windows_DPAPI_CurrentUser_расшифровывает_и_не_хранит_открытым_текстом()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "ветка Windows: DPAPI");
        var file = Path.Combine(_root, "device-token.bin");
        var store = new DpapiTokenStore(file);

        store.Save("dev-1", Token);

        File.ReadAllText(file).Should().NotContain(Token);
        store.Read("dev-1").Should().Be(Token);
        store.Read("dev-2").Should().BeNull();
    }

    [SkippableFact]
    public void MacOS_Keychain_помечен_как_не_реализованный()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "ветка macOS");
        var store = DeviceTokenStores.ForCurrentOs(_root);
        var act = () => store.Read("dev-1");
        act.Should().Throw<TokenStoreException>().WithMessage("*не реализовано*");
    }

    [SkippableFact]
    public void Выбор_хранилища_по_текущей_ОС()
    {
        var store = DeviceTokenStores.ForCurrentOs(_root);
        if (OperatingSystem.IsWindows()) store.Should().BeOfType<DpapiTokenStore>();
        else if (OperatingSystem.IsMacOS()) store.Should().BeOfType<UnsupportedTokenStore>();
        else store.Should().Match(s => s is FileTokenStore || s is SecretServiceTokenStore);
    }

    private sealed class FakeSecretTool : ISecretTool
    {
        private readonly Dictionary<string, string> _secrets = new();

        public bool Exists { get; init; } = true;
        public string ProbeStderr { get; init; } = "";
        public List<(IReadOnlyList<string> Args, string? Stdin)> Calls { get; } = [];

        public (int Code, string Stdout, string Stderr) Run(IReadOnlyList<string> args, string? stdin = null)
        {
            Calls.Add((args, stdin));
            var key = string.Join('|', args.Skip(1).Where(a => !a.StartsWith("--label", StringComparison.Ordinal)));
            switch (args[0])
            {
                case "store":
                    _secrets[key] = stdin ?? "";
                    return (0, "", "");
                case "lookup" when args.Contains("probe"):
                    return (1, "", ProbeStderr);
                case "lookup":
                    return _secrets.TryGetValue(key, out var v) ? (0, v, "") : (1, "", "");
                default:
                    _secrets.Remove(key);
                    return (0, "", "");
            }
        }
    }
}
