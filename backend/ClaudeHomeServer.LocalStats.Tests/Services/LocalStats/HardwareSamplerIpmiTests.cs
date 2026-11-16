using System.Text.Json;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

/// <summary>Слияние IPMI в снимок узла (Ш2d4): сокеты, VRM, вентиляторы, плата, свежесть 30 с.</summary>
public class HardwareSamplerIpmiTests
{
    private const string Root = "/sys/class/hwmon";
    private const long Clock = 1_800_000_000_000;

    private sealed class ManualTime : TimeProvider
    {
        private readonly DateTimeOffset _now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class Opts : IOptionsMonitor<LocalModelStatsOptions>
    {
        public LocalModelStatsOptions CurrentValue { get; } = new() { Enabled = true };
        public LocalModelStatsOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<LocalModelStatsOptions, string?> listener) => null;
    }

    private sealed class Source(Dictionary<string, string> files, string? ipmi, long? clockMs) : INodeReadingSource
    {
        public Task<NodeReading> ReadAsync(CancellationToken ct = default) => Task.FromResult(new NodeReading
        {
            Status = NodeReadingStatus.Ok,
            Files = new SliceFileSource(files),
            Ipmi = ipmi,
            ClockMs = clockMs,
        });
    }

    private static string Wrap(long at, string sdr) => JsonSerializer.Serialize(new { at, sdr });

    private const string Sdr =
        "CPU1 Temp        | 01h | ok  |  3.1 | 49 degrees C\n" +
        "CPU2 Temp        | 02h | ok  |  3.2 | 35 degrees C\n" +
        "VRMCpu1 Temp     | 03h | ok  |  3.1 | 47 degrees C\n" +
        "VRMCpu2 Temp     | 04h | ok  |  3.2 | 50 degrees C\n" +
        "System Temp      | 05h | ok  |  7.1 | 33 degrees C\n" +
        "Peripheral Temp  | 06h | ok  |  7.1 | 41 degrees C\n" +
        "FAN3             | 07h | ok  | 29.1 | 1500 RPM\n";

    // Два сокета k10temp (без ссылок device — порядок по номеру hwmon) + вентилятор nct6798 в hwmon.
    private static Dictionary<string, string> TwoSockets()
    {
        var f = new Dictionary<string, string>
        {
            ["/proc/uptime"] = "10.0 1.0\n",
            ["/proc/stat"] = "cpu  100 0 100 800 0 0 0 0 0 0\n",
            [$"{Root}/hwmon0/name"] = "k10temp\n",
            [$"{Root}/hwmon0/temp1_input"] = "60000", [$"{Root}/hwmon0/temp1_label"] = "Tctl\n",
            [$"{Root}/hwmon1/name"] = "k10temp\n",
            [$"{Root}/hwmon1/temp1_input"] = "40000", [$"{Root}/hwmon1/temp1_label"] = "Tctl\n",
            [$"{Root}/hwmon2/name"] = "nct6798\n",
            [$"{Root}/hwmon2/fan1_input"] = "900", [$"{Root}/hwmon2/fan1_label"] = "Case\n",
        };
        return f;
    }

    private static HardwareSampler Build(INodeReadingSource src, ManualTime time) =>
        new(new Opts(), [new HardwareSamplerNode("w", src)], new Gddr6HelperReader(time), time);

    private static Source Worker(string? ipmi, long? clock = Clock)
    {
        var files = TwoSockets();
        return new Source(files, ipmi, clock);
    }

    [Fact]
    public async Task FreshIpmi_MergesVrmFansBoardPeripheral()
    {
        var time = new ManualTime();
        var s = await Build(Worker(Wrap(Clock - 15_000, Sdr)), time).PollNodeAsync("w");

        s!.Vrms.Should().Equal(new int?[] { 47, 50 });
        s.BoardVrm.Should().BeNull();
        // Температуры сокетов остаются из k10temp: IPMI их не трогает (c1).
        s.Cpus!.Select(c => c.Tctl).Should().Equal(new int?[] { 60, 40 });
        // Порядок слияния (c3): сначала hwmon-вентилятор («Case»), за ним IPMI («FAN3»);
        // мутация [..ipmi.Fans, ..hw.CaseFans] краснит именно эту строку.
        s.CaseFans!.Select(f => f.Label).Should().Equal("Case", "FAN3");
        s.BoardTempC.Should().Be(33);
        s.PeripheralTempC.Should().Be(41);
        s.BoardProbe.Should().BeTrue();
    }

    [Fact]
    public async Task StaleIpmi_YieldsNulls()
    {
        var time = new ManualTime();
        var s = await Build(Worker(Wrap(Clock - 30_001, Sdr)), time).PollNodeAsync("w");

        s!.Vrms.Should().OnlyContain(v => v == null);
        s.BoardVrm.Should().BeNull();
        s.CaseFans.Should().NotContain(f => f.Label == "FAN3");
        s.BoardTempC.Should().BeNull();
        s.PeripheralTempC.Should().BeNull();
        s.Cpus.Should().HaveCount(2); // hwmon-поля на месте
        s.BoardProbe.Should().BeTrue(); // текст IPMI есть, пусть и несвежий
    }

    [Fact]
    public async Task IpmiExactlyAtThreshold_IsFresh()
    {
        var time = new ManualTime();
        var s = await Build(Worker(Wrap(Clock - 30_000, Sdr)), time).PollNodeAsync("w");
        s!.PeripheralTempC.Should().Be(41);
    }

    // Метка дальше в будущем, чем окно свежести: не свежее (хвост 53e2dc3c).
    // Мутация «проверять только age ≤ IpmiFreshAfterMs» краснит этот тест: age = −30 001 проходит.
    [Fact]
    public async Task FutureIpmiBeyondWindow_YieldsNulls()
    {
        var time = new ManualTime();
        var s = await Build(Worker(Wrap(Clock + 30_001, Sdr)), time).PollNodeAsync("w");

        s!.Vrms.Should().OnlyContain(v => v == null);
        s.CaseFans.Should().NotContain(f => f.Label == "FAN3");
        s.BoardTempC.Should().BeNull();
        s.PeripheralTempC.Should().BeNull();
        s.Cpus.Should().HaveCount(2); // hwmon-поля на месте
        s.BoardProbe.Should().BeTrue(); // текст IPMI есть, пусть и несвежий
    }

    [Fact]
    public async Task IpmiParseThrows_NodeStaysOk()
    {
        var time = new ManualTime();
        var sampler = Build(Worker(Wrap(Clock, Sdr)), time);
        sampler.IpmiParse = _ => throw new InvalidOperationException("кривой SDR");

        var s = await sampler.PollNodeAsync("w");

        s.Should().NotBeNull();
        sampler.ForNode("w")!.Status.Should().Be("ok");
        s!.PeripheralTempC.Should().BeNull();
        s.Cpus.Should().HaveCount(2);
    }

    [Fact]
    public async Task NoIpmi_BoardProbeFalse_NoValues()
    {
        var time = new ManualTime();
        var s = await Build(Worker(null), time).PollNodeAsync("w");
        s!.BoardProbe.Should().BeFalse();
        s.BoardTempC.Should().BeNull();
    }

    [Fact]
    public async Task AsusecMotherboard_WinsOverIpmiSystemTemp_AndProbeIsTrue()
    {
        var files = new Dictionary<string, string>
        {
            ["/proc/uptime"] = "10.0 1.0\n",
            [$"{Root}/hwmon0/name"] = "asusec\n",
            [$"{Root}/hwmon0/temp3_input"] = "36000", [$"{Root}/hwmon0/temp3_label"] = "Motherboard\n",
        };
        var time = new ManualTime();
        var s = await Build(new Source(files, Wrap(Clock, Sdr), Clock), time).PollNodeAsync("w");
        s!.BoardTempC.Should().Be(36);
        s.PeripheralTempC.Should().Be(41);

        // Без IPMI: плата из hwmon остаётся, источник платы есть.
        var s2 = await Build(new Source(files, null, Clock), time).PollNodeAsync("w");
        s2!.BoardTempC.Should().Be(36);
        s2.BoardProbe.Should().BeTrue();
    }

    [Fact]
    public async Task AsusecMotherboard_Unplugged_IsNull()
    {
        var files = new Dictionary<string, string>
        {
            ["/proc/uptime"] = "10.0 1.0\n",
            [$"{Root}/hwmon0/name"] = "asusec\n",
            [$"{Root}/hwmon0/temp3_input"] = "-40000", [$"{Root}/hwmon0/temp3_label"] = "Motherboard\n",
        };
        var s = await Build(new Source(files, null, Clock), new ManualTime()).PollNodeAsync("w");
        s!.BoardTempC.Should().BeNull();
    }

    private static HardwareSample TwoCpuSample() => new()
    {
        Cpus = [new HwmonCpu { Tctl = 60 }, new HwmonCpu { Tctl = 40 }],
        Vrms = new int?[2],
    };

    [Fact]
    public void Merge_SocketCountMismatch_VrmsGoToBoardVrmMax_CpusUntouched()
    {
        var hw = TwoCpuSample();
        var ipmi = new IpmiReading(new int?[] { 49 }, new int?[] { 47, 50 }, null, [], null, null);

        var r = HardwareSampler.MergeIpmi(hw, ipmi);

        r.Cpus.Should().BeSameAs(hw.Cpus);
        r.Vrms.Should().OnlyContain(v => v == null);
        r.BoardVrm.Should().Be(50);
    }

    [Fact]
    public void Merge_HwmonVrmWins_OverIpmi()
    {
        var hw = TwoCpuSample() with { Vrms = new int?[] { 70, null }, BoardVrm = 66 };
        var ipmi = new IpmiReading(new int?[] { 1, 2 }, new int?[] { 47, 50 }, 99, [], null, null);

        var r = HardwareSampler.MergeIpmi(hw, ipmi);

        r.Vrms.Should().Equal(new int?[] { 70, 50 });
        r.BoardVrm.Should().Be(66);
    }

    [Fact]
    public void Merge_NullIpmi_ReturnsSampleUnchanged()
    {
        var hw = TwoCpuSample() with { BoardTempC = 36 };
        HardwareSampler.MergeIpmi(hw, null).Should().BeSameAs(hw);
    }

    [Fact]
    public void Merge_EmptyIpmiReading_KeepsEverything()
    {
        var hw = TwoCpuSample() with { BoardTempC = 36 };
        var r = HardwareSampler.MergeIpmi(hw, new IpmiReading([], [], null, [], null, null));
        r.BoardTempC.Should().Be(36);
        r.CaseFans.Should().BeNull();
    }
}
