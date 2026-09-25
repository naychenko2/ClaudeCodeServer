using ClaudeHomeServer.DeviceAgent.Cli;

namespace ClaudeHomeServer.DeviceAgent.Tests.Cli;

public sealed class ManagedCliTests : IDisposable
{
    private static readonly CliPlatform Linux = new("linux-x64", IsWindows: false);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "managed-cli-" + Guid.NewGuid().ToString("N"));
    private readonly FakeCliDistribution _dist = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private static readonly CliManifestVerifier TrustedVerifier = TestPgpKey.Trusted.Verifier();

    private IExecutableSignatureCheck _executableCheck = ExecutableSignatureCheck.None;

    private ManagedCli Create(CliPlatform? platform = null) =>
        new(_root, _dist, platform ?? Linux, _time, manifestVerifier: TrustedVerifier, executableCheck: _executableCheck);

    private string VersionDir(string v) => Path.Combine(_root, "versions", v);
    private string[] Staging() => Directory.GetFileSystemEntries(Path.Combine(_root, "staging"));

    [Fact]
    public async Task Установка_кладёт_проверенную_копию_в_каталог_агента_и_делает_харнес_готовым()
    {
        _dist.Publish("2.1.281");
        var cli = Create();

        cli.SetRequiredVersion("2.1.281");
        var status = await cli.EnsureAsync();

        status.State.Should().Be(HarnessState.Ready);
        status.Problem.Should().BeNull();
        cli.ActiveVersion.Should().Be("2.1.281");

        using var lease = cli.TryAcquire(out var problem);
        problem.Should().BeNull();
        lease!.Version.Should().Be("2.1.281");
        lease.ExecutablePath.Should().Be(Path.Combine(VersionDir("2.1.281"), "claude"));
        File.ReadAllBytes(lease.ExecutablePath).Should().Equal(FakeCliDistribution.Payload("2.1.281"));
        Staging().Should().BeEmpty();
        File.ReadAllText(Path.Combine(_root, "active")).Should().Be("2.1.281");
    }

    [Fact]
    public async Task До_ответа_сервера_харнес_не_готов_и_ход_получает_причину()
    {
        var cli = Create();

        cli.TryAcquire(out var problem).Should().BeNull();
        problem.Should().StartWith(ManagedCli.NotReadyPrefix).And.Contain("от сервера ещё не получена");

        cli.SetRequiredVersion(null);
        (await cli.EnsureAsync()).Problem.Should().Contain("не задана версия CLI");
        _dist.ManifestCalls.Should().Be(0);
    }

    [Fact]
    public async Task Битая_контрольная_сумма_не_даёт_копии_и_держит_причину()
    {
        _dist.Publish("2.1.281", sha256: new string('0', 64));
        var cli = Create();

        cli.SetRequiredVersion("2.1.281");
        var status = await cli.EnsureAsync();

        status.State.Should().Be(HarnessState.NotReady);
        status.Problem.Should().Contain("проверка целостности").And.Contain("SHA256");
        Directory.Exists(VersionDir("2.1.281")).Should().BeFalse();
        Staging().Should().BeEmpty();
        cli.ActiveVersion.Should().BeNull();
        cli.TryAcquire(out var problem).Should().BeNull();
        problem.Should().Contain("проверка целостности");
    }

    [Fact]
    public async Task Подменённый_бинарь_той_же_длины_отвергается()
    {
        _dist.Publish("2.1.281");
        var tampered = FakeCliDistribution.Payload("2.1.281");
        tampered[^1] ^= 0xFF;
        _dist.Tamper = tampered;
        var cli = Create();

        cli.SetRequiredVersion("2.1.281");
        (await cli.EnsureAsync()).Problem.Should().Contain("SHA256 не совпал");
        Directory.Exists(VersionDir("2.1.281")).Should().BeFalse();
    }

    [Fact]
    public async Task Без_подписи_манифеста_копия_не_ставится_и_бинарь_не_качается()
    {
        _dist.Publish("2.1.281");
        _dist.OmitSignature = true;
        var cli = Create();

        cli.SetRequiredVersion("2.1.281");
        var status = await cli.EnsureAsync();

        status.State.Should().Be(HarnessState.NotReady);
        status.Problem.Should().StartWith(ManagedCli.NotReadyPrefix).And.Contain("нет подписи manifest.json.sig");
        _dist.BinaryCalls.Should().Be(0);
        Directory.Exists(VersionDir("2.1.281")).Should().BeFalse();
        cli.TryAcquire(out var problem).Should().BeNull();
        problem.Should().Contain("нет подписи");
    }

    [Fact]
    public async Task Манифест_подписанный_чужим_ключом_отвергается()
    {
        _dist.Publish("2.1.281");
        _dist.Signer = TestPgpKey.Stranger;
        var cli = Create();

        cli.SetRequiredVersion("2.1.281");
        var status = await cli.EnsureAsync();

        status.State.Should().Be(HarnessState.NotReady);
        status.Problem.Should().Contain("чужим ключом");
        _dist.BinaryCalls.Should().Be(0);
        Directory.Exists(VersionDir("2.1.281")).Should().BeFalse();
    }

    [Fact]
    public async Task Подменённый_манифест_при_настоящей_подписи_отвергается()
    {
        // Злоумышленник на канале подставил свою SHA256 и свой бинарь, а .sig оставил настоящий.
        var evil = FakeCliDistribution.Payload("evil");
        _dist.Publish("2.1.281");
        _dist.TamperManifest = m => System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(m)
            .Replace(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(FakeCliDistribution.Payload("2.1.281"))),
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(evil))));
        var cli = Create();

        cli.SetRequiredVersion("2.1.281");
        var status = await cli.EnsureAsync();

        status.State.Should().Be(HarnessState.NotReady);
        status.Problem.Should().Contain("проверка целостности").And.Contain("подпись манифеста не сходится");
        _dist.BinaryCalls.Should().Be(0);
        Directory.Exists(VersionDir("2.1.281")).Should().BeFalse();
    }

    [Fact]
    public async Task Отказ_платформенной_подписи_бинаря_не_даёт_копии()
    {
        _dist.Publish("2.1.281");
        _executableCheck = new RejectingCheck();
        var cli = Create();

        cli.SetRequiredVersion("2.1.281");
        var status = await cli.EnsureAsync();

        status.State.Should().Be(HarnessState.NotReady);
        status.Problem.Should().Contain("подписан «Mallory»");
        Directory.Exists(VersionDir("2.1.281")).Should().BeFalse();
        Staging().Should().BeEmpty();
    }

    private sealed class RejectingCheck : IExecutableSignatureCheck
    {
        public void Verify(string executablePath) =>
            throw new CliIntegrityException($"{Path.GetFileName(executablePath)} подписан «Mallory», ожидался «Anthropic, PBC»");
    }

    [Fact]
    public async Task Бинарь_длиннее_манифеста_обрывается_без_докачки()
    {
        _dist.Publish("2.1.281", size: 1000);
        var cli = Create();

        cli.SetRequiredVersion("2.1.281");
        (await cli.EnsureAsync()).Problem.Should().Contain("больше заявленного");
        Staging().Should().BeEmpty();
    }

    [Fact]
    public async Task Обрыв_посреди_скачивания_не_оставляет_полурабочей_копии_и_ждёт_бэкофф()
    {
        _dist.Publish("2.1.281");
        _dist.FailAfterBytes = 50_000;
        var cli = Create();

        cli.SetRequiredVersion("2.1.281");
        var failed = await cli.EnsureAsync();

        failed.State.Should().Be(HarnessState.NotReady);
        failed.Problem.Should().Contain("прервана");
        failed.NextAttemptAt.Should().Be(_time.Now + ManagedCli.BackoffBase);
        Directory.Exists(VersionDir("2.1.281")).Should().BeFalse();
        Staging().Should().BeEmpty();

        // До срока бэкоффа сеть не трогается.
        await cli.EnsureAsync();
        _dist.BinaryCalls.Should().Be(1);

        _dist.FailAfterBytes = null;
        _time.Advance(ManagedCli.BackoffBase);
        (await cli.EnsureAsync()).State.Should().Be(HarnessState.Ready);
        _dist.BinaryCalls.Should().Be(2);
    }

    [Fact]
    public async Task Поток_кончился_раньше_манифеста_это_обрыв_а_не_копия()
    {
        _dist.Publish("2.1.281");
        _dist.TruncateAfterBytes = 10_000;
        var cli = Create();

        cli.SetRequiredVersion("2.1.281");
        (await cli.EnsureAsync()).Problem.Should().Contain("оборвалось");
        Directory.Exists(VersionDir("2.1.281")).Should().BeFalse();
    }

    [Fact]
    public async Task Офлайн_даёт_понятную_причину_и_растущий_бэкофф_а_не_вечный_ретрай()
    {
        _dist.Publish("2.1.281");
        _dist.Offline = true;
        var cli = Create();
        cli.SetRequiredVersion("2.1.281");

        var first = await cli.EnsureAsync();
        first.Problem.Should().Contain("нет доступа к fake-dist").And.Contain("повтор не раньше");
        first.NextAttemptAt.Should().Be(_time.Now + TimeSpan.FromSeconds(30));

        _time.Advance(TimeSpan.FromSeconds(30));
        var second = await cli.EnsureAsync();
        second.NextAttemptAt.Should().Be(_time.Now + TimeSpan.FromSeconds(60));
        _dist.ManifestCalls.Should().Be(2);

        ManagedCli.BackoffFor(50).Should().Be(ManagedCli.BackoffMax);
    }

    [Fact]
    public async Task Смена_версии_сбрасывает_бэкофф()
    {
        _dist.Offline = true;
        var cli = Create();
        cli.SetRequiredVersion("2.1.281");
        await cli.EnsureAsync();

        _dist.Offline = false;
        _dist.Publish("2.1.290");
        cli.SetRequiredVersion("2.1.290");
        (await cli.EnsureAsync()).State.Should().Be(HarnessState.Ready);
    }

    [Fact]
    public async Task Смена_версии_при_живом_ходе_не_трогает_его_копию_а_старую_удаляет_после()
    {
        _dist.Publish("2.1.281");
        _dist.Publish("2.1.290");
        var cli = Create();
        var hellos = new List<string?>();
        cli.Changed += s => hellos.Add(s.ActiveVersion);

        cli.SetRequiredVersion("2.1.281");
        await cli.EnsureAsync();
        var liveTurn = cli.TryAcquire(out _)!;

        cli.SetRequiredVersion("2.1.290");
        var status = await cli.EnsureAsync();

        status.State.Should().Be(HarnessState.Ready);
        cli.ActiveVersion.Should().Be("2.1.290");
        hellos.Should().Contain("2.1.290");
        // Живой ход дорабатывает на своей копии.
        File.Exists(liveTurn.ExecutablePath).Should().BeTrue();
        using (var next = cli.TryAcquire(out _))
            next!.Version.Should().Be("2.1.290");

        liveTurn.Dispose();
        Directory.Exists(VersionDir("2.1.281")).Should().BeFalse();
        Directory.Exists(VersionDir("2.1.290")).Should().BeTrue();
    }

    [Fact]
    public async Task Пока_новая_версия_не_встала_ход_отказывает_с_причиной()
    {
        _dist.Publish("2.1.281");
        var cli = Create();
        cli.SetRequiredVersion("2.1.281");
        await cli.EnsureAsync();

        cli.SetRequiredVersion("2.1.290");

        cli.TryAcquire(out var problem).Should().BeNull();
        problem.Should().Contain("нет CLI 2.1.290");
        cli.ActiveVersion.Should().Be("2.1.281");
    }

    [Fact]
    public async Task Перезапуск_агента_подхватывает_копию_и_вычищает_прерванную_установку()
    {
        _dist.Publish("2.1.281");
        var cli = Create();
        cli.SetRequiredVersion("2.1.281");
        await cli.EnsureAsync();

        // Агента убили посреди следующей установки.
        var leftover = Path.Combine(_root, "staging", "2.1.290-dead");
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, "claude"), "half");

        var restarted = Create();
        restarted.ActiveVersion.Should().Be("2.1.281");
        Staging().Should().BeEmpty();

        restarted.SetRequiredVersion("2.1.281");
        (await restarted.EnsureAsync()).State.Should().Be(HarnessState.Ready);
        _dist.ManifestCalls.Should().Be(1);
    }

    [Fact]
    public async Task Каталог_версии_без_записи_установки_не_считается_копией()
    {
        Directory.CreateDirectory(VersionDir("2.1.281"));
        File.WriteAllText(Path.Combine(VersionDir("2.1.281"), "claude"), "чужое");
        File.WriteAllText(Path.Combine(_root, "active"), "2.1.281");
        _dist.Publish("2.1.281");

        var cli = Create();
        cli.ActiveVersion.Should().BeNull();

        cli.SetRequiredVersion("2.1.281");
        (await cli.EnsureAsync()).State.Should().Be(HarnessState.Ready);
        File.ReadAllBytes(Path.Combine(VersionDir("2.1.281"), "claude"))
            .Should().Equal(FakeCliDistribution.Payload("2.1.281"));
    }

    [Theory]
    [InlineData("../../etc")]
    [InlineData("2.1.281/../../x")]
    [InlineData("latest")]
    [InlineData("2.1")]
    public async Task Недопустимая_версия_от_сервера_не_идёт_ни_в_сеть_ни_в_путь(string version)
    {
        var cli = Create();
        cli.SetRequiredVersion(version);

        var status = await cli.EnsureAsync();

        status.State.Should().Be(HarnessState.NotReady);
        status.Problem.Should().Contain("недопустимую версию");
        _dist.ManifestCalls.Should().Be(0);
    }

    [Fact]
    public async Task Манифест_чужой_версии_отвергается()
    {
        _dist.Publish("2.1.281", manifestVersion: "2.1.200");
        var cli = Create();
        cli.SetRequiredVersion("2.1.281");

        (await cli.EnsureAsync()).Problem.Should().Contain("манифест выдан для версии 2.1.200");
        _dist.BinaryCalls.Should().Be(0);
    }

    [Fact]
    public async Task Нет_сборки_под_платформу_понятная_причина()
    {
        _dist.Publish("2.1.281", platform: "darwin-arm64");
        var cli = Create();
        cli.SetRequiredVersion("2.1.281");

        (await cli.EnsureAsync()).Problem.Should().Contain("нет сборки для платформы linux-x64");
    }

    [Fact]
    public async Task Нет_выпуска_в_раздаче_понятная_причина()
    {
        var cli = Create();
        cli.SetRequiredVersion("9.9.9");

        (await cli.EnsureAsync()).Problem.Should().Contain("выпуска CLI 9.9.9 нет в fake-dist");
    }

    [Fact]
    public async Task На_Windows_берётся_сам_exe()
    {
        var windows = new CliPlatform("win32-x64", IsWindows: true);
        _dist.Publish("2.1.281", platform: "win32-x64", binary: "claude.exe");
        var cli = Create(windows);
        cli.SetRequiredVersion("2.1.281");

        (await cli.EnsureAsync()).State.Should().Be(HarnessState.Ready);
        using var lease = cli.TryAcquire(out _)!;
        Path.GetFileName(lease.ExecutablePath).Should().Be("claude.exe");
    }

    [Theory]
    [InlineData("claude.cmd")]
    [InlineData("claude")]
    public async Task На_Windows_обёртка_вместо_exe_отвергается(string binary)
    {
        var windows = new CliPlatform("win32-x64", IsWindows: true);
        _dist.Publish("2.1.281", platform: "win32-x64", binary: binary);
        var cli = Create(windows);
        cli.SetRequiredVersion("2.1.281");

        (await cli.EnsureAsync()).Problem.Should().Contain("ожидается .exe");
        _dist.BinaryCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("../claude")]
    [InlineData("bin/claude")]
    public async Task Имя_бинаря_с_путём_отвергается(string binary)
    {
        _dist.Publish("2.1.281", binary: binary);
        var cli = Create();
        cli.SetRequiredVersion("2.1.281");

        (await cli.EnsureAsync()).Problem.Should().Contain("недопустимое имя бинаря");
    }

    [SkippableFact]
    public async Task На_Unix_копия_исполняемая()
    {
        Skip.If(OperatingSystem.IsWindows(), "права Unix");
        _dist.Publish("2.1.281");
        var cli = Create();
        cli.SetRequiredVersion("2.1.281");
        await cli.EnsureAsync();

        using var lease = cli.TryAcquire(out _)!;
        if (OperatingSystem.IsWindows()) return;
        File.GetUnixFileMode(lease.ExecutablePath).Should().HaveFlag(UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task Фоновый_цикл_ставит_версию_по_сигналу_сервера()
    {
        _dist.Publish("2.1.281");
        var cli = Create();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cli.Changed += s => { if (s.State == HarnessState.Ready) ready.TrySetResult(); };
        using var cts = new CancellationTokenSource();

        var loop = cli.RunAsync(cts.Token);
        cli.SetRequiredVersion("2.1.281");

        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cli.ActiveVersion.Should().Be("2.1.281");
        cts.Cancel();
        await FluentActions.Awaiting(() => loop).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Автообновление_CLI_выключено_в_env_запуска()
    {
        var env = new Dictionary<string, string?>();
        ManagedCliEnvironment.ApplyTo(env);

        env.Should().Contain("DISABLE_AUTOUPDATER", "1").And.Contain("DISABLE_UPDATES", "1");
    }
}
