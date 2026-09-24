using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Desktop;
using ClaudeHomeServer.Services.Execution;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.Desktop;

// Возможности устройства и канал исполнения (ADR-016, задача 2.1): сведения агента из Hello,
// вердикт «харнес не готов» по версии управляемой копии CLI и отказ хода СРАЗУ, с причиной,
// до попытки открыть канал; кадровка канала исполнения.
public class DeviceExecChannelTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Conn = "conn-1";
    private const string RequiredCli = "2.1.281";

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ccs_devexec_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true);
    }

    private sealed class SilentSender : IDeviceCommandSender
    {
        public Task SendCallAsync(string connectionId, DesktopCallCommand command, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendGoAsync(string connectionId, DesktopGoCommand go, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendCancelAsync(string connectionId, DesktopCancelCommand cancel, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CountingOpener : IDeviceExecOpenSender
    {
        public int Calls;
        public DeviceExecOpenCommand? Last;

        public Task SendExecOpenAsync(string connectionId, DeviceExecOpenCommand command, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            Last = command;
            return Task.CompletedTask;
        }
    }

    // Таймеры срабатывают сразу: ожидание подключения устройства истекает без реальных 10 с
    private sealed class ImmediateTime : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Timeout.InfiniteTimeSpan) ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new NoopTimer();
        }

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed record Rig(DeviceRegistry Registry, DesktopCallRouter Router, DeviceExecChannel Channel, CountingOpener Opener, DesktopDevice Device);

    private Rig NewRig(string? requiredCli = RequiredCli)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DeviceHarnessPolicy.CliVersionKey] = requiredCli })
            .Build();
        var registry = new DeviceRegistry(_dataDir);
        var router = new DesktopCallRouter(new SilentSender(), [], NullLogger<DesktopCallRouter>.Instance);
        var opener = new CountingOpener();
        var channel = new DeviceExecChannel(registry, router, new DeviceHarnessPolicy(config), opener,
            NullLogger<DeviceExecChannel>.Instance, new ImmediateTime());
        var (device, _) = registry.Register(Owner, "home", MachineFingerprint.Of("device-machine"));
        return new Rig(registry, router, channel, opener, device);
    }

    private static DeviceHello AgentHello(string? cliVersion, params string[] capabilities) =>
        new(DesktopProtocol.Version, null, "agent-0.1", "linux-x64", "0.1.0", cliVersion,
            capabilities.Length == 0 ? [DeviceCapabilities.Exec] : capabilities);

    private static async Task<DeviceHelloAck> ConnectAsync(Rig rig, DeviceHello hello)
    {
        rig.Router.RegisterConnection(Conn, Owner, rig.Device.Id);
        return await rig.Channel.HelloAsync(Conn, Owner, rig.Device.Id, hello);
    }

    // ---------- «харнес не готов»: ход отказывает сразу, с причиной ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2.0.1")]
    public async Task HelloСПустойИлиЧужойВерсиейCli_ХарнесНеГотов_ХодОтказываетСразу(string? cliVersion)
    {
        var rig = NewRig();

        var ack = await ConnectAsync(rig, AgentHello(cliVersion));

        ack.RequiredCliVersion.Should().Be(RequiredCli, "в ответ на Hello сервер сообщает требуемую версию CLI");
        ack.HarnessReady.Should().BeFalse();
        ack.HarnessProblem.Should().StartWith(DeviceHarnessPolicy.NotReadyPrefix);

        var status = rig.Channel.GetStatus(Owner, rig.Device.Id)!;
        status.Online.Should().BeTrue();
        status.HarnessReady.Should().BeFalse();
        status.CanExec.Should().BeFalse();

        var open = () => rig.Channel.OpenAsync(Owner, rig.Device.Id);
        var refused = await open.Should().ThrowAsync<DeviceExecRefusedException>();
        refused.Which.Reason.Should().Be(DeviceExecRefusal.HarnessNotReady);
        refused.Which.Message.Should().Contain("Харнес не готов").And.Contain(RequiredCli);
        rig.Opener.Calls.Should().Be(0, "до устройства команда открытия не доходит — отказ случается раньше");
    }

    [Fact]
    public async Task ВерсияCliСовпала_ХарнесГотов_КаналОткрываетсяКомандойУстройству()
    {
        var rig = NewRig();

        var ack = await ConnectAsync(rig, AgentHello("2.1.281 (Claude Code)"));

        ack.HarnessReady.Should().BeTrue();
        ack.HarnessProblem.Should().BeNull();
        ack.ExecProtocolVersion.Should().Be(DeviceExecProtocol.Version);
        rig.Channel.GetStatus(Owner, rig.Device.Id)!.CanExec.Should().BeTrue();

        // Устройство не подключилось к WebSocket — отказ «не ответило», но команда ушла
        var open = () => rig.Channel.OpenAsync(Owner, rig.Device.Id);
        (await open.Should().ThrowAsync<DeviceExecRefusedException>()).Which.Reason.Should().Be(DeviceExecRefusal.NoResponse);
        rig.Opener.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ТребуемаяВерсияНаСервереНеЗадана_ХарнесНеГотов()
    {
        var rig = NewRig(requiredCli: "");

        var ack = await ConnectAsync(rig, AgentHello(RequiredCli));

        ack.RequiredCliVersion.Should().BeNull();
        ack.HarnessReady.Should().BeFalse();
        ack.HarnessProblem.Should().Contain(DeviceHarnessPolicy.CliVersionKey);
    }

    [Fact]
    public async Task ПовторныйHelloПослеУстановкиCli_ХарнесГотов()
    {
        var rig = NewRig();
        await ConnectAsync(rig, AgentHello(null));

        var ack = await rig.Channel.HelloAsync(Conn, Owner, rig.Device.Id, AgentHello(RequiredCli));

        ack.HarnessReady.Should().BeTrue();
        rig.Channel.GetStatus(Owner, rig.Device.Id)!.CanExec.Should().BeTrue();
    }

    // ---------- ретранслятор чтения (задача 5.1): тот же канал, другое назначение ----------

    [Fact]
    public async Task Ретранслятор_Офлайн_ОтказСразу()
    {
        var rig = NewRig();
        rig.Registry.UpdateAgentInfo(Owner, rig.Device.Id, AgentHello(RequiredCli, DeviceCapabilities.Relay));

        var open = () => rig.Channel.OpenRelayAsync(Owner, rig.Device.Id);
        (await open.Should().ThrowAsync<DeviceExecRefusedException>()).Which.Reason.Should().Be(DeviceExecRefusal.Offline);
        rig.Opener.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Ретранслятор_АгентБезRelay_ОтказСразу()
    {
        var rig = NewRig();
        await ConnectAsync(rig, AgentHello(RequiredCli, DeviceCapabilities.Exec, DeviceCapabilities.Files));

        var open = () => rig.Channel.OpenRelayAsync(Owner, rig.Device.Id);
        (await open.Should().ThrowAsync<DeviceExecRefusedException>()).Which.Reason.Should().Be(DeviceExecRefusal.NoRelayCapability);
        rig.Opener.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Ретранслятор_ХарнесНеНужен_КомандаОткрытияСНазначениемRelay()
    {
        var rig = NewRig();
        // Копии CLI нет — ход бы отказал, а чтению харнес не нужен
        await ConnectAsync(rig, AgentHello(null, DeviceCapabilities.Relay));

        var open = () => rig.Channel.OpenRelayAsync(Owner, rig.Device.Id);
        (await open.Should().ThrowAsync<DeviceExecRefusedException>()).Which.Reason.Should().Be(DeviceExecRefusal.NoResponse);
        rig.Opener.Calls.Should().Be(1);
        rig.Opener.Last!.Purpose.Should().Be(DeviceExecPurposes.Relay);
    }

    [Fact]
    public async Task Офлайн_ОтказСразу()
    {
        var rig = NewRig();
        rig.Registry.UpdateAgentInfo(Owner, rig.Device.Id, AgentHello(RequiredCli));

        rig.Channel.GetStatus(Owner, rig.Device.Id)!.Online.Should().BeFalse();
        var open = () => rig.Channel.OpenAsync(Owner, rig.Device.Id);
        (await open.Should().ThrowAsync<DeviceExecRefusedException>()).Which.Reason.Should().Be(DeviceExecRefusal.Offline);
        rig.Opener.Calls.Should().Be(0);
    }

    [Fact]
    public async Task БезВозможностиExec_ОтказСразу()
    {
        var rig = NewRig();
        await ConnectAsync(rig, AgentHello(RequiredCli, DeviceCapabilities.Files));

        var open = () => rig.Channel.OpenAsync(Owner, rig.Device.Id);
        (await open.Should().ThrowAsync<DeviceExecRefusedException>()).Which.Reason.Should().Be(DeviceExecRefusal.NoExecCapability);
    }

    [Fact]
    public void ЧужоеИлиОтозванноеУстройство_СтатусаНет()
    {
        var rig = NewRig();

        rig.Channel.GetStatus("чужой", rig.Device.Id).Should().BeNull();
        rig.Registry.Revoke(Owner, rig.Device.Id);
        rig.Channel.GetStatus(Owner, rig.Device.Id).Should().BeNull();
    }

    // ---------- сведения агента в devices.json ----------

    [Fact]
    public async Task СведенияАгента_ПереживаютПерезагрузкуРеестра()
    {
        var rig = NewRig();
        await ConnectAsync(rig, AgentHello(RequiredCli, "EXEC", "relay", "неизвестная", "exec"));

        var reloaded = new DeviceRegistry(_dataDir).Get(Owner, rig.Device.Id)!;

        reloaded.Platform.Should().Be("linux-x64");
        reloaded.AgentVersion.Should().Be("0.1.0");
        reloaded.CliVersion.Should().Be(RequiredCli);
        reloaded.Capabilities.Should().Equal(DeviceCapabilities.Exec, DeviceCapabilities.Relay);
    }

    [Fact]
    public async Task HelloКлиентаРук_СведенийАгентаНеЗатирает()
    {
        var rig = NewRig();
        await ConnectAsync(rig, AgentHello(RequiredCli));

        await rig.Channel.HelloAsync(Conn, Owner, rig.Device.Id,
            new DeviceHello(DesktopProtocol.Version, [DesktopCallKinds.Screen], "wpf-1.0"));

        var device = rig.Registry.Get(Owner, rig.Device.Id)!;
        device.CliVersion.Should().Be(RequiredCli);
        device.Capabilities.Should().Equal(DeviceCapabilities.Exec);
    }

    [Fact]
    public void ЗаписьДоAdr016_ЧитаетсяСПустымиПолямиАгента()
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(Path.Combine(_dataDir, DeviceRegistry.FileName), """
            [{ "Id": "d1", "OwnerId": "owner-1", "Name": "home", "TokenHash": "", "TokenVersion": 1,
               "MachineFingerprint": "", "ClientVersion": "1.0", "Revoked": false }]
            """);

        var device = new DeviceRegistry(_dataDir).Get(Owner, "d1")!;

        device.AgentVersion.Should().BeNull();
        device.CliVersion.Should().BeNull();
        device.Capabilities.Should().BeEmpty();
    }

    // ---------- версия CLI ----------

    [Theory]
    [InlineData("2.1.281 (Claude Code)", "2.1.281")]
    [InlineData(" v2.1.281 ", "2.1.281")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void НормализацияВерсииCli(string? raw, string? expected) =>
        DeviceHarnessPolicy.Normalize(raw).Should().Be(expected);

    // ---------- кадры ----------

    [Fact]
    public void Кадр_ТудаИОбратно()
    {
        var bytes = DeviceExecFrames.Encode(DeviceExecFrameChannel.Stdout, 42, "привет"u8);

        bytes.Should().HaveCount(DeviceExecProtocol.HeaderBytes + "привет"u8.Length);
        DeviceExecFrames.TryDecode(bytes, out var frame).Should().BeTrue();
        frame.Channel.Should().Be(DeviceExecFrameChannel.Stdout);
        frame.Sequence.Should().Be(42);
        Encoding.UTF8.GetString(frame.Payload.Span).Should().Be("привет");
    }

    [Fact]
    public void БитыеКадры_НеРазбираются()
    {
        var good = DeviceExecFrames.Encode(DeviceExecFrameChannel.Stdin, 1, "abc"u8);

        DeviceExecFrames.TryDecode(good.AsMemory(0, good.Length - 1), out _).Should().BeFalse("длина не совпала с данными");
        DeviceExecFrames.TryDecode(good.AsMemory(0, 5), out _).Should().BeFalse("заголовок неполный");

        var unknownChannel = (byte[])good.Clone();
        unknownChannel[0] = 77;
        DeviceExecFrames.TryDecode(unknownChannel, out _).Should().BeFalse("неизвестный канал");

        var ackWithData = (byte[])good.Clone();
        ackWithData[0] = (byte)DeviceExecFrameChannel.Ack;
        DeviceExecFrames.TryDecode(ackWithData, out _).Should().BeFalse("у подтверждения нет данных");

        DeviceExecFrames.TryDecode(DeviceExecFrames.Ack(7), out var ack).Should().BeTrue();
        ack.Sequence.Should().Be(7);
    }

    [Fact]
    public void КадрБольшеПотолка_НеКодируется()
    {
        var act = () => DeviceExecFrames.Encode(DeviceExecFrameChannel.Stdout, 1, new byte[DeviceExecProtocol.MaxPayloadBytes + 1]);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AckСервераНаHello_СовместимСКлиентомРук()
    {
        // Клиент рук ADR-008 разбирает ack своей копией контракта из четырёх полей:
        // новые поля — только хвостом, прежние имена и порядок не трогаем
        var json = JsonSerializer.Serialize(new DeviceHelloAck(1, 2, 3, 4, RequiredCli, true));
        var old = JsonSerializer.Deserialize<JsonElement>(json);

        old.GetProperty("ProtocolVersion").GetInt32().Should().Be(1);
        old.GetProperty("MaxBatchSteps").GetInt32().Should().Be(4);
        old.GetProperty("RequiredCliVersion").GetString().Should().Be(RequiredCli);
    }
}
