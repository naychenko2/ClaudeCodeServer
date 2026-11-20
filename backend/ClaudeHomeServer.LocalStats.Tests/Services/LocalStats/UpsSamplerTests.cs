using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

/// <summary>
/// ИБП на вкладке «Стенды»: разбор ответа upsd (снят с Raskat HIL-3000VA+ 20.11.2026),
/// статусы сэмплера и блок ups в сводке фермы.
/// </summary>
public class UpsSamplerTests
{
    private const string LiveList = """
        BEGIN LIST VAR raskat
        VAR raskat battery.charge "100"
        VAR raskat battery.voltage "54.1"
        VAR raskat battery.voltage.nominal "48.0"
        VAR raskat device.type "ups"
        VAR raskat driver.version.usb "libusb-1.0.29 (API: 0x0100010B)"
        VAR raskat input.frequency "49.9"
        VAR raskat input.voltage "222.0"
        VAR raskat output.voltage "222.0"
        VAR raskat ups.load "22"
        VAR raskat ups.status "OL"
        VAR raskat ups.temperature "28.0"
        END LIST VAR raskat
        """;

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);

    private static (UpsSampler Sampler, ManualTime Time) NewSampler(Func<string> reply, int? ratedW = 2400)
    {
        var time = new ManualTime(T0);
        var opts = Options.Create(new UpsOptions { Host = "127.0.0.1", Name = "raskat", Title = "Raskat", RatedW = ratedW });
        var s = new UpsSampler(opts, time) { ReadFunc = _ => Task.FromResult(reply()) };
        return (s, time);
    }

    [Fact]
    public void Parse_LiveReply_MapsValues()
    {
        var vars = NutVarParser.Parse(LiveList, "raskat");
        vars.Should().NotBeNull();
        var s = NutVarParser.ToSample(vars!, T0);
        s.Status.Should().Be("OL");
        s.LoadPerc.Should().Be(22);
        s.ChargePerc.Should().Be(100);
        s.BatteryV.Should().Be(54.1);
        s.BatteryNominalV.Should().Be(48.0);
        s.InputV.Should().Be(222.0);
        s.InputHz.Should().Be(49.9);
        s.OutputV.Should().Be(222.0);
        s.TempC.Should().Be(28);
    }

    [Theory]
    [InlineData("ERR UNKNOWN-UPS\n")]
    [InlineData("BEGIN LIST VAR raskat\nVAR raskat ups.load \"22\"\n")]
    public void Parse_ErrOrCutReply_IsNull(string text) =>
        NutVarParser.Parse(text, "raskat").Should().BeNull();

    [Fact]
    public void Parse_OtherUpsVarsIgnored() =>
        NutVarParser.Parse("VAR other ups.load \"99\"\nEND LIST VAR raskat\n", "raskat").Should().BeEmpty();

    [Fact]
    public async Task Status_DownBeforeFirstReply_OkAfter_StaleAfter30s_DownOnError()
    {
        var reply = LiveList;
        var (s, time) = NewSampler(() => reply);
        s.Status.Should().Be("down");

        await s.PollOnceAsync();
        s.Status.Should().Be("ok");
        s.Since.Should().BeNull();

        time.Now = T0.AddSeconds(31);
        s.Status.Should().Be("stale");
        s.Since.Should().Be(T0.ToUnixTimeMilliseconds());

        reply = "ERR DATA-STALE\n";
        await s.PollOnceAsync();
        s.Status.Should().Be("down");
        s.Since.Should().Be(T0.AddSeconds(31).ToUnixTimeMilliseconds());
        // Последний удачный снимок остаётся для приглушённого показа.
        s.Current!.LoadPerc.Should().Be(22);
    }

    [Fact]
    public async Task Poll_ReaderThrows_DownHostLives()
    {
        var (s, _) = NewSampler(() => throw new IOException("connection refused"));
        await s.PollOnceAsync();
        s.Status.Should().Be("down");
        s.Since.Should().Be(T0.ToUnixTimeMilliseconds());
    }

    // ─── Блок ups в сводке фермы ─────────────────────────────────────────────────

    private sealed class EmptySampler : IHardwareSampler
    {
        public IReadOnlyList<string> NodeKeys => [];
        public INodeHardwareView? ForNode(string key) => null;
    }

    private static IConfiguration NoNodes() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{HardwareNodesOptions.Section}:0:Key"] = "worker",
            [$"{HardwareNodesOptions.Section}:0:Source"] = "http://10.0.0.10:9105",
        }).Build();

    private static async Task<HardwareFarmReport> FarmWith(string status, int load)
    {
        var (s, time) = NewSampler(() => LiveList.Replace("\"OL\"", $"\"{status}\"").Replace("\"22\"", $"\"{load}\""));
        await s.PollOnceAsync();
        time.Now = T0.AddSeconds(1);
        return new HardwareReportService(new EmptySampler(), NoNodes(), s).Farm();
    }

    [Theory]
    [InlineData("OL", 22, "line", "ok")]
    [InlineData("OL CHRG", 85, "line", "warn")]
    [InlineData("OL", 96, "line", "danger")]
    [InlineData("OB DISCHRG", 40, "battery", "warn")]
    [InlineData("OB LB", 40, "lowBattery", "danger")]
    public async Task Farm_UpsPowerAndLoad_GiveLevel_AndPaintFarm(string flags, int load, string power, string level)
    {
        var farm = await FarmWith(flags, load);
        farm.Ups.Should().NotBeNull();
        farm.Ups!.Power.Should().Be(power);
        farm.Ups.Level.Should().Be(level);
        farm.Level.Should().Be(level);
    }

    [Fact]
    public async Task Farm_Ups_LoadWattsAndSparks()
    {
        var farm = await FarmWith("OL", 50);
        var u = farm.Ups!;
        u.Title.Should().Be("Raskat");
        u.Status.Should().Be("ok");
        u.LoadW.Should().Be(1200);
        u.LoadSpark15m.Should().HaveCount(90);
        u.LoadSpark15m[^1].Should().Be(50);
        u.LoadSpark15m.Should().Contain(50);
        u.InputVSpark15m.Should().Contain(222);
        u.BatteryDeciVSpark15m[^1].Should().Be(541);
    }

    [Fact]
    public async Task Farm_UpsDown_NotPainted()
    {
        var (s, _) = NewSampler(() => "ERR ACCESS-DENIED\n");
        await s.PollOnceAsync();
        var farm = new HardwareReportService(new EmptySampler(), NoNodes(), s).Farm();
        farm.Ups!.Status.Should().Be("down");
        farm.Ups.Level.Should().Be("ok");
        farm.Level.Should().Be("ok");
    }

    [Fact]
    public void Farm_UpsNotConfigured_IsNull()
    {
        var s = new UpsSampler(Options.Create(new UpsOptions()), new ManualTime(T0));
        new HardwareReportService(new EmptySampler(), NoNodes(), s).Farm().Ups.Should().BeNull();
        new HardwareReportService(new EmptySampler(), NoNodes()).Farm().Ups.Should().BeNull();
    }
}
