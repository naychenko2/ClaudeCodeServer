using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.DeviceAgent.Composition;
using ClaudeHomeServer.DeviceAgent.Processes;
using ClaudeHomeServer.DeviceAgent.Supervision;
using ClaudeHomeServer.DeviceAgent.Tests.Supervision;
using ClaudeHomeServer.DeviceAgent.Update;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.DeviceAgent.Tests.Update;

/// <summary>
/// Самообновление агента (AD-5): версия из ack — цель, в том числе ниже текущей; архив
/// сверяется с хешем и размером из ack; переключение ждёт пустого реестра активности.
/// </summary>
public sealed class AgentUpdaterTests : IDisposable
{
    private const string Rid = "linux-x64";
    private const string Own = "1.5.0";

    private readonly TempInstall _install = new(Own);
    private readonly FakeArchives _archives = new();
    private readonly ActivityRegistry _activity = new();
    private readonly List<string> _repointed = [];

    public AgentUpdaterTests() => _install.Layout.SetActive(Own);

    public void Dispose() => _install.Dispose();

    private AgentLayout Layout => _install.Layout;

    private AgentUpdater Create() =>
        new(Layout, Own + "+abcdef12", Rid, _archives, _activity, _repointed.Add, () => Own);

    private DeviceHelloAck AckFor(string version, string? sha = null)
    {
        var (file, bytes) = _archives.Publish(version);
        return new DeviceHelloAck(1, 30, 1024, 10,
            AgentLatestVersion: version,
            AgentArchiveSha256: sha ?? Convert.ToHexStringLower(SHA256.HashData(bytes)),
            AgentArchiveSize: bytes.Length,
            AgentArchivePath: $"{version}/{Rid}/{file}");
    }

    [Fact]
    public async Task Новая_версия_скачивается_проверяется_и_становится_активной()
    {
        var updater = Create();
        updater.OnAck(AckFor("2.0.0"));

        (await updater.StepAsync(CancellationToken.None)).Should().BeTrue("работы нет — переключаемся сразу");

        Layout.ReadActive().Should().Be("2.0.0");
        Layout.ReadPrevious().Should().Be(Own, "прошлая версия — цель отката супервизора");
        File.ReadAllText(Layout.ExeOf("2.0.0")).Should().Be("agent 2.0.0");
        _repointed.Should().Equal("2.0.0");
        Directory.EnumerateFileSystemEntries(Path.Combine(_install.Root, "staging")).Should().BeEmpty();
        if (!OperatingSystem.IsWindows())
            new DirectoryInfo(Layout.CurrentLink).LinkTarget.Should().Be(Path.Combine("versions", "2.0.0"));
    }

    [Fact]
    public async Task Активный_ход_держит_переключение_после_его_конца_оно_происходит()
    {
        var updater = Create();
        updater.OnAck(AckFor("2.0.0"));
        var turn = _activity.TryAcquire(WorkKind.Turn)!;

        (await updater.StepAsync(CancellationToken.None)).Should().BeFalse();

        updater.Status.State.Should().Be(DeviceAgentUpdateStates.WaitingIdle);
        updater.Status.TargetVersion.Should().Be("2.0.0");
        updater.Status.Reason.Should().Be("идёт ход", "префикс «Обновление ждёт» добавляет интерфейс");
        Layout.ReadActive().Should().Be(Own, "пока идёт ход, active не трогаем");
        _repointed.Should().BeEmpty();
        Layout.IsInstalled("2.0.0").Should().BeTrue("скачать можно и под ходом");

        turn.Dispose();

        (await updater.StepAsync(CancellationToken.None)).Should().BeTrue();
        Layout.ReadActive().Should().Be("2.0.0");
    }

    [SkippableFact]
    public async Task Открытый_терминал_держит_переключение_после_закрытия_оно_происходит()
    {
        Skip.If(!OperatingSystem.IsWindows() && UnixGroupProcess.FindSetsid() is null, "нет setsid");
        var launchers = new AgentLauncherFactory(activity: _activity);
        var terminal = launchers.Local.Start(OperatingSystem.IsWindows()
            ? new ProcessSpec { FileName = "cmd.exe", Args = ["/c", "ping -n 600 127.0.0.1 >nul"], EnableRaisingEvents = true }
            : new ProcessSpec { FileName = "/bin/sh", Args = ["-c", "sleep 600"], EnableRaisingEvents = true });
        try
        {
            var updater = Create();
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            updater.Changed += s => { if (s.State == DeviceAgentUpdateStates.WaitingIdle) waiting.TrySetResult(); };
            updater.OnAck(AckFor("2.0.0"));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var run = updater.RunAsync(stop.Token);

            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            updater.Status.Reason.Should().Contain("терминал");
            run.IsCompleted.Should().BeFalse();
            Layout.ReadActive().Should().Be(Own);

            launchers.KillAll();

            (await run.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue("терминал закрыт — переключение происходит само");
            Layout.ReadActive().Should().Be("2.0.0");
        }
        finally
        {
            launchers.KillAll();
            terminal.Dispose();
        }
    }

    [Fact]
    public async Task Битый_хеш_это_failed_и_active_не_тронут()
    {
        var updater = Create();
        updater.OnAck(AckFor("2.0.0", sha: new string('0', 64)));

        (await updater.StepAsync(CancellationToken.None)).Should().BeFalse();

        updater.Status.State.Should().Be(DeviceAgentUpdateStates.Failed);
        updater.Status.Reason.Should().Contain("SHA-256");
        Layout.ReadActive().Should().Be(Own);
        Layout.IsInstalled("2.0.0").Should().BeFalse("непроверенный архив в versions/ не попадает");
        _repointed.Should().BeEmpty();
    }

    [Fact]
    public async Task Архив_длиннее_заявленного_не_принимается()
    {
        var updater = Create();
        var ack = AckFor("2.0.0");
        updater.OnAck(ack with { AgentArchiveSize = ack.AgentArchiveSize - 1 });

        (await updater.StepAsync(CancellationToken.None)).Should().BeFalse();

        updater.Status.State.Should().Be(DeviceAgentUpdateStates.Failed);
        Layout.IsInstalled("2.0.0").Should().BeFalse();
    }

    [Fact]
    public async Task Сервер_назвал_версию_ниже_текущей_агент_идёт_вниз()
    {
        var updater = Create();
        updater.OnAck(AckFor("1.2.0"));

        (await updater.StepAsync(CancellationToken.None)).Should().BeTrue();

        Layout.ReadActive().Should().Be("1.2.0");
        _repointed.Should().Equal("1.2.0");
    }

    [Fact]
    public async Task Своя_версия_или_сервер_без_раздачи_это_idle_без_скачивания()
    {
        var updater = Create();
        updater.OnAck(AckFor(Own));
        (await updater.StepAsync(CancellationToken.None)).Should().BeFalse();
        updater.OnAck(new DeviceHelloAck(1, 30, 1024, 10));
        (await updater.StepAsync(CancellationToken.None)).Should().BeFalse();

        updater.Status.State.Should().Be(DeviceAgentUpdateStates.Idle);
        _archives.Opened.Should().BeEmpty();
        Layout.ReadActive().Should().Be(Own);
    }

    [Fact]
    public async Task Версия_помеченная_плохой_не_пробуется()
    {
        _install.AddVersion("2.0.0");
        Layout.MarkBad("2.0.0", DateTimeOffset.UtcNow);
        var updater = Create();
        updater.OnAck(AckFor("2.0.0"));

        (await updater.StepAsync(CancellationToken.None)).Should().BeFalse();

        updater.Status.State.Should().Be(DeviceAgentUpdateStates.Failed);
        Layout.ReadActive().Should().Be(Own);
    }

    [Theory]
    [InlineData("2.0.0/win-x64/agent.zip")]
    [InlineData("2.0.0/linux-x64/../../x.tar.gz")]
    [InlineData("1.0.0/linux-x64/agent.tar.gz")]
    [InlineData("2.0.0/linux-x64/agent.exe")]
    public async Task Путь_архива_не_своего_RID_или_версии_отвергается(string path)
    {
        var updater = Create();
        updater.OnAck(AckFor("2.0.0") with { AgentArchivePath = path });

        (await updater.StepAsync(CancellationToken.None)).Should().BeFalse();

        updater.Status.State.Should().Be(DeviceAgentUpdateStates.Failed);
        _archives.Opened.Should().BeEmpty();
    }

    [Fact]
    public void Запечатанный_реестр_отказывает_новым_арендам_до_распечатывания()
    {
        _activity.TrySeal().Should().BeTrue();
        _activity.TryAcquire(WorkKind.Turn).Should().BeNull();
        _activity.Unseal();

        using var lease = _activity.TryAcquire(WorkKind.Turn);
        lease.Should().NotBeNull();
        _activity.TrySeal().Should().BeFalse("под арендой не запечатывается");
    }

    /// <summary>Раздача архивов: tar.gz (Linux) или zip (Windows) с бинарём агента в корне.</summary>
    private sealed class FakeArchives : IAgentArchiveSource
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        public List<string> Opened { get; } = [];

        public (string File, byte[] Bytes) Publish(string version)
        {
            var exe = Encoding.UTF8.GetBytes($"agent {version}");
            var file = OperatingSystem.IsWindows() ? "ai-home-agent.zip" : "ai-home-agent.tar.gz";
            var bytes = OperatingSystem.IsWindows() ? Zip(exe) : TarGz(exe);
            _files[$"{version}/{Rid}/{file}"] = bytes;
            return (file, bytes);
        }

        public Task<Stream> OpenAsync(string relativePath, CancellationToken ct)
        {
            Opened.Add(relativePath);
            return _files.TryGetValue(relativePath, out var bytes)
                ? Task.FromResult<Stream>(new MemoryStream(bytes))
                : throw new HttpRequestException("нет такого архива", null, System.Net.HttpStatusCode.NotFound);
        }

        private static byte[] TarGz(byte[] exe)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            using (var tar = new TarWriter(gzip))
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, SupervisorContract.ExecutableName)
                {
                    DataStream = new MemoryStream(exe),
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                });
            }
            return output.ToArray();
        }

        private static byte[] Zip(byte[] exe)
        {
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            using (var entry = zip.CreateEntry(SupervisorContract.ExecutableName).Open())
                entry.Write(exe);
            return output.ToArray();
        }
    }
}
